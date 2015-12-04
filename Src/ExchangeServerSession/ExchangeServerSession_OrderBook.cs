using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.GeneralClientInterfaces.ExchangeServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.MyNotifyPropertyChanged.MyObservableCollections;
using BtmI2p.ObjectStateLib;
using MoreLinq;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.ExchangeServerSession
{
    partial class ExchangeServerSession
    {
        private void InitOrderBookSubscriptions()
        {
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.NewTrade)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => UpdateTradesList())
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.NewOrder)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => UpdateOrderList())
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.OrderChanged)
                    .SelectMany(async _ =>
                    {
                        await _delayedOrdersToUpdate.WithAsyncLockSem(
							__ => __.Add((Guid) _.EventArgs)
						).ConfigureAwait(false);
                        return 0;
                    })
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
					.Subscribe(_ => UpdateOrderStatus()));
			_subscriptions.Add(
				_exchangeSessionModel.Data.OrderList.CollectionChangedObservable.Where(
                    _ => _.ChangedAction == EMyCollectionChangedAction.NewItemsRangeInserted
                ).BufferNotEmpty(TimeSpan.FromSeconds(2.0))
				.ObserveOn(TaskPoolScheduler.Default)
				.Subscribe(_ => UpdateOrderStatus())
			);
			_subscriptions.Add(
				_exchangeSessionModel.Data.PropertyChangedSubject
				.Where(
					_ => _.PropertyName == _exchangeSessionModel.Data.MyNameOfProperty(
						__ => __.SecCodesToUpdateDomEntries
					)
				).BufferNotEmpty(TimeSpan.FromSeconds(2.0))
				.ObserveOn(TaskPoolScheduler.Default)
				.Subscribe(_ => UpdateDomEntries())
			);
			_subscriptions.Add(
				ServerEvents.Where(
					_ => _.EventType == EExchangeClientEventTypes.NewOrder
						|| _.EventType == EExchangeClientEventTypes.NewTrade
						|| _.EventType == EExchangeClientEventTypes.OrderChanged
				).BufferNotEmpty(TimeSpan.FromSeconds(2.0))
				.ObserveOn(TaskPoolScheduler.Default)
				.Subscribe(_ => UpdateDomEntries())
			);
			_subscriptions.Add(
				Observable.Interval(TimeSpan.FromSeconds(30.0))
				.ObserveOn(TaskPoolScheduler.Default)
				.Subscribe(_ => UpdateDomEntries())
			);
            _subscriptions.Add(
                Observable.Interval(TimeSpan.FromSeconds(30.0))
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(_ => UpdateChartCandles())
            );
            _subscriptions.Add(
                ServerEvents.Where(
                    _ =>  _.EventType == EExchangeClientEventTypes.NewTrade
                ).BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(_ => UpdateChartCandles())
            );
            _subscriptions.Add(
                _exchangeSessionModel.Data.PropertyChangedSubject.Where(
                    _ => _.PropertyName == _exchangeSessionModel.Data.MyNameOfProperty(
                        __ => __.CandlesToUpdate
                    )
                )
                .BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(_ => UpdateChartCandles())
            );
        }
        private readonly SemaphoreSlim _updateTradesListLockSem = new SemaphoreSlim(1);
        private async void UpdateTradesList()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateTradesListLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateTradesListLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        var lastKnownTradeGuid =
                            (await _exchangeSessionModel.Data.TradesCollection.LastOrDefaultDeepCopyAsync()
                                    .ConfigureAwait(false))?.TradeGuid ?? Guid.Empty;
						while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var currentLastKnownTradeGuid = lastKnownTradeGuid;
                            var newTrades = await MiscFuncs.RepeatWhileTimeout(
                                async () => await _authenticatedExchangeInterface.GetNewTrades(
                                    new GetNewTradesRequest()
                                    {
                                        LastKnownTradeGuid = currentLastKnownTradeGuid,
                                        MaxBufferCount = 100,
                                        StartTime = _initTime - ExchangeServerSessionInternalConstants.DefaultLastTs
                                    }
                                    ).ConfigureAwait(false),
                                _cts.Token
                                ).ConfigureAwait(false);
                            if (newTrades.Any())
                            {
                                await
                                    _exchangeSessionModel.Data.TradesCollection.AddRangeAsync(newTrades)
                                        .ConfigureAwait(false);
                                lastKnownTradeGuid = newTrades.Last().TradeGuid;
	                            lockSemCalledWrapper.Called = true;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                HandleUnexpectedError(exc);
            }
        }
        /**/
        private readonly SemaphoreSlim _updateOrderListLockSem = new SemaphoreSlim(1);
        private async void UpdateOrderList()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateOrderListLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateOrderListLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        var lastKnownOrderGuid =
                            (await _exchangeSessionModel.Data.OrderList.LastOrDefaultDeepCopyAsync().ConfigureAwait(false))
                            ?.OrderGuid ?? Guid.Empty;
						while (!_cts.IsCancellationRequested)
                        {
                            lockSemCalledWrapper.Called = false;
							if (_cts.IsCancellationRequested)
								break;
                            var currentLastKnownOrderGuid = lastKnownOrderGuid;
                            var newOrders = await MiscFuncs.RepeatWhileTimeout(
                                async () => await _authenticatedExchangeInterface.GetAllActiveNewOtherOrders(
                                    new GetAllActiveNewOtherOrdersRequest()
                                    {
                                        LastKnownOrderGuid = currentLastKnownOrderGuid,
                                        StartTime = _initTime - ExchangeServerSessionInternalConstants.DefaultLastTs,
                                        MaxBufferCount = 100
                                    }
                                ).ConfigureAwait(false),
                                _cts.Token
                            ).ConfigureAwait(false);
                            if (newOrders.Any())
                            {
                                await _exchangeSessionModel.Data.OrderList.AddRangeAsync(
                                    newOrders
                                ).ConfigureAwait(false);
                                lastKnownOrderGuid = newOrders.Last().OrderGuid;
                            }
                            else
                            {
                                if(!lockSemCalledWrapper.Called)
                                    break;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                HandleUnexpectedError(exc);
            }
        }

        private readonly HashSet<Guid> _delayedOrdersToUpdate
            = new HashSet<Guid>();
		private readonly SemaphoreSlim _updateOrderStatusLockSem = new SemaphoreSlim(1);
        private async void UpdateOrderStatus()
        {
            try
            {
	            using (_stateHelper.GetFuncWrapper())
	            {
		            var lockSemCalledWrapper = _updateOrderStatusLockSem.GetCalledWrapper();
		            lockSemCalledWrapper.Called = true;
		            using (await _updateOrderStatusLockSem.GetDisposable(true).ConfigureAwait(false))
		            {
			            while (true)
			            {
							lockSemCalledWrapper.Called = false;
							if(_cts.IsCancellationRequested)
								break;
				            var orderGuidToUpdate = (await _delayedOrdersToUpdate.WithAsyncLockSem(
					            _ =>
					            {
						            var result = _.Take(100).ToList();
						            foreach (var orderGuid in result)
						            {
							            _.Remove(orderGuid);
						            }
						            return result.Distinct().ToList();
					            }
					        ).ConfigureAwait(false));
				            if (orderGuidToUpdate.Any())
				            {
					            var newOrdersInfoDict = (await MiscFuncs.RepeatWhileTimeout(
						            async () => await _authenticatedExchangeInterface.GetOrderInfos(
							            new GetOrderInfosRequest()
							            {
								            OrderGuidList = orderGuidToUpdate
							            }
							        ),
						            _cts.Token
						        ).ConfigureAwait(false)).ToDictionary(_ => _.OrderGuid);
				                var indicesToUpdate = await _exchangeSessionModel.Data.OrderList.IndexesOfAsync(
				                    o => newOrdersInfoDict.ContainsKey(o.OrderGuid)
				                    ).ConfigureAwait(false);
				                foreach (var i in indicesToUpdate)
				                {
				                    var currentOrder = await _exchangeSessionModel.Data.OrderList.GetItemDeepCopyAtAsync(
				                        i
				                        ).ConfigureAwait(false);
				                    await _exchangeSessionModel.Data.OrderList.ReplaceItemAtAsync(
				                        i,
				                        newOrdersInfoDict[currentOrder.OrderGuid]
				                    ).ConfigureAwait(false);
				                }
				            }
				            else
				            {
					            if (!lockSemCalledWrapper.Called)
						            break;
				            }
			            }
		            }
	            }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                HandleUnexpectedError(exc);
            }
        }
		/**/
		private readonly SemaphoreSlim _updateDomEntriesLockSem = new SemaphoreSlim(1);
	    private async void UpdateDomEntries()
	    {
		    try
		    {
			    using (_stateHelper.GetFuncWrapper())
			    {
				    var lockSemCalledWrapper = _updateDomEntriesLockSem.GetCalledWrapper();
				    lockSemCalledWrapper.Called = true;
				    using (await _updateDomEntriesLockSem.GetDisposable(true).ConfigureAwait(false))
				    {
						while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
					    {
							lockSemCalledWrapper.Called = false;
						    var domSecCodes = await _exchangeSessionModel.Data.SecCodesToUpdateDomEntries.WithAsyncLockSem(
							    _ => _.Distinct().ToList()
							).ConfigureAwait(false);
						    if (domSecCodes.Any())
						    {
							    foreach (var batchedDocSecCodes in domSecCodes.Batch(100))
							    {
								    var currentBatchedDocSecCodes = batchedDocSecCodes.ToList();
									Assert.NotEmpty(currentBatchedDocSecCodes);
									var newDomEntries = await MiscFuncs.RepeatWhileTimeout(
										async () => await _exchangeTransport.GetDomList(
											new GetDomListRequest()
											{
												SecCodeList = currentBatchedDocSecCodes
											}
										).ConfigureAwait(false),
										_cts.Token
									).ConfigureAwait(false);
								    await _exchangeSessionModel.Data.DomEntries.WithAsyncLockSem(
									    domEntries =>
									    {
											foreach (MutableTuple<string, List<DomEntry>> entry in newDomEntries)
											{
												domEntries[entry.Item1] = entry.Item2;
											}
									    }
									).ConfigureAwait(false);
							    }
								MyNotifyPropertyChangedArgs.RaiseProperyChanged(
									_exchangeSessionModel.Data,
									_ => _.DomEntries
								);
						    }
					    }
				    }
			    }
		    }
		    catch (WrongDisposableObjectStateException)
		    {
		    }
		    catch (OperationCanceledException)
		    {
		    }
		    catch (Exception exc)
		    {
				HandleUnexpectedError(exc);
		    }
	    }

	    public async Task CancelOrder(Guid orderGuid, CancellationToken token)
	    {
		    using (_stateHelper.GetFuncWrapper())
		    {
			    try
			    {
				    await MiscFuncs.RepeatWhileTimeout(
					    async () => await _signedExchangeInterface.CancelOrder(orderGuid),
					    token, 
						_cts.Token
					).ConfigureAwait(false);
			    }
			    catch (RpcRethrowableException rpcExc)
			    {
				    if (
						!rpcExc.ErrorData.ErrorCode.In(
							(int) ECancelOrderErrCodes.WrongStatusCancelled
						)
					)
				    {
					    throw;
				    }
			    }
		    }
	    }

	    public async Task<Guid> AddNewOrder(
			string secCode,
			EExchangeOrderSide side,
			decimal price,
			long qty, 
			Guid baseAccountGuid,
			Guid secondAccountGuid,
			CancellationToken token
		)
	    {
		    using (_stateHelper.GetFuncWrapper())
		    {
				var requestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
				return await MiscFuncs.RepeatWhileTimeout(
					async () => await _signedExchangeInterface.AddNewOrder(
						new NewExchangeSecurityOrderRequest()
						{
							RequestGuid = requestGuid,
							Side = side,
							Qty = qty,
							SecurityCode = secCode,
							Price = price,
							BaseAccountGuid = baseAccountGuid,
							SecondAccountGuid = secondAccountGuid
						}
					).ConfigureAwait(false),
					token,
					_cts.Token
				).ConfigureAwait(false);
		    }
	    }
        /**/
        private readonly SemaphoreSlim _updateChartCandlesLockSem = new SemaphoreSlim(1);
        private readonly List<Tuple<string,EExchangeChartTimeframe>> _initChartDataLoaded
            = new List<Tuple<string, EExchangeChartTimeframe>>(); 
        private async void UpdateChartCandles()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateChartCandlesLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateChartCandlesLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var currentChartsToUpdate = await _exchangeSessionModel.Data.CandlesToUpdate
                                .WithAsyncLockSem(_ => _.Distinct().Take(100).ToList()).ConfigureAwait(false);
                            if(!currentChartsToUpdate.Any())
                                return;
                            foreach (
                                var chartsToLoadInitData 
                                in currentChartsToUpdate.Except(_initChartDataLoaded).ToList()
                            )
                            {
                                var changedCollection = _exchangeSessionModel.Data.GetChartCandlesCollection(
                                    chartsToLoadInitData.Item1,
                                    chartsToLoadInitData.Item2
                                );
                                if (await changedCollection.CountAsync().ConfigureAwait(false) == 0)
                                {
                                    var currentChartsToLoadInitData = chartsToLoadInitData;
                                    var lastNCandles = await MiscFuncs.RepeatWhileTimeout(
                                        async () => await _exchangeTransport.GetLastNChartCandles(
                                            new GetLastNCandlesRequest()
                                            {
                                                N = 200,
                                                Timeframe = currentChartsToLoadInitData.Item2,
                                                SecCode = currentChartsToLoadInitData.Item1
                                            }
                                            ).ConfigureAwait(false),
                                        _cts.Token
                                        ).ConfigureAwait(false);
                                    await changedCollection.ClearAsync().ConfigureAwait(false);
                                    await changedCollection.AddRangeAsync(lastNCandles).ConfigureAwait(false);
                                }
                                _initChartDataLoaded.Add(chartsToLoadInitData);
                            }
                            var request = new GetChangedChartCandlesRequest();
                            foreach (Tuple<string, EExchangeChartTimeframe> tuple in currentChartsToUpdate)
                            {
                                var chartCollection = _exchangeSessionModel.Data.GetChartCandlesCollection(
                                    tuple.Item1,
                                    tuple.Item2
                                );
                                var lastKnownCandle =
                                    await chartCollection.LastOrDefaultDeepCopyAsync().ConfigureAwait(false);
                                var lastKnownChangedNum = lastKnownCandle?.LastChangedNum ?? -1;
                                request.KnownCandleInfos.Add(
                                    MutableTuple.Create(tuple.Item1,tuple.Item2,lastKnownChangedNum)
                                );
                            }
                            var updateData = await MiscFuncs.RepeatWhileTimeout(
                                async () => await _exchangeTransport.GetChangedChartCandles(
                                    request
                                    ).ConfigureAwait(false),
                                _cts.Token
                            ).ConfigureAwait(false);
                            if (updateData.Count == request.MaxCount)
                                lockSemCalledWrapper.Called = true;
                            foreach (var updatedCandleData in updateData)
                            {
                                var chartCollection = _exchangeSessionModel.Data.GetChartCandlesCollection(
                                    updatedCandleData.SecurityCode,
                                    updatedCandleData.Timeframe
                                );
                                var currentCandleIndexes = await chartCollection.IndexesOfAsync(
                                    _ => _.StartTime == updatedCandleData.StartTime
                                    ).ConfigureAwait(false);
                                if (currentCandleIndexes.Any())
                                {
                                    await chartCollection.ChangeAtAsync(
                                        currentCandleIndexes[0],
                                        currentCandle =>
                                        {
                                            currentCandle.OpenValue = updatedCandleData.OpenValue;
                                            currentCandle.CloseValue = updatedCandleData.CloseValue;
                                            currentCandle.HighValue = updatedCandleData.HighValue;
                                            currentCandle.LowValue = updatedCandleData.LowValue;
                                            currentCandle.TotalVolumeLots = updatedCandleData.TotalVolumeLots;
                                            currentCandle.LastChangedNum = updatedCandleData.LastChangedNum;
                                        }
                                    ).ConfigureAwait(false);
                                }
                                else
                                {
                                    await chartCollection.AddRangeAsync(new[]
                                    {
                                        updatedCandleData
                                    }).ConfigureAwait(false);
                                }
                                var currentCollectionCount = await chartCollection.CountAsync().ConfigureAwait(false);
                                if (currentCollectionCount > 200)
                                    await chartCollection.RemoveRangeAsync(
                                        0, 
                                        currentCollectionCount - 200
                                    ).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exc)
            {
                HandleUnexpectedError(exc);
            }
        }
    }
}
