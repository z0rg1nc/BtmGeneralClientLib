using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.GeneralClientInterfaces.ExchangeServer;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using MoreLinq;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.ExchangeServerSession
{
	public partial class ExchangeServerSession
	{
		private void InitDwSubscriptions()
		{
			_subscriptions.Add(
				ServerEvents
					.Where(_ => _.EventType == EExchangeClientEventTypes.NewDeposit)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
					.ObserveOn(TaskPoolScheduler.Default)
					.Subscribe(_ => ReadNewDepositList()));
			_subscriptions.Add(
				ServerEvents
					.Where(_ => _.EventType == EExchangeClientEventTypes.DepositChanged)
					.Select(_ => (MutableTuple<Guid,Guid>)_.EventArgs)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
					.ObserveOn(TaskPoolScheduler.Default)
					.Subscribe(UpdateDepositStatus));
			_subscriptions.Add(
				ServerEvents
					.Where(_ => _.EventType == EExchangeClientEventTypes.NewWithdraw)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
					.ObserveOn(TaskPoolScheduler.Default)
					.Subscribe(_ => ReadNewWithdrawList()));
			_subscriptions.Add(
				ServerEvents
					.Where(_ => _.EventType == EExchangeClientEventTypes.WithdrawChanged)
					.Select(_ => (MutableTuple<Guid, Guid>)_.EventArgs)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
					.ObserveOn(TaskPoolScheduler.Default)
					.Subscribe(UpdateWithdrawStatus));
		}
		/**/
		private readonly SemaphoreSlim _updateDepositStatusLockSem = new SemaphoreSlim(1);
		/// <summary>
		/// 
		/// </summary>
		/// <param name="depositPairList">(MutableTuple) accountGuid, depositGuid</param>
		private async void UpdateDepositStatus(
			IList<MutableTuple<Guid, Guid>> depositPairList
		)
		{
			try
			{
				using (_stateHelper.GetFuncWrapper())
				{
					using (await _updateDepositStatusLockSem.GetDisposable().ConfigureAwait(false))
					{
						foreach (var depositEnumerable in depositPairList.Select(_ => _.Item2).Batch(100))
						{
							var currentDepositList = depositEnumerable.ToList();
							var newDepositStatusList = await MiscFuncs.RepeatWhileTimeout(
								async () => await _authenticatedExchangeInterface.GetDepositInfos(
									new GetDepositInfosRequest()
									{
										DepositGuidList = currentDepositList.Distinct().ToList()
									}
								).ConfigureAwait(false),
								_cts.Token
							).ConfigureAwait(false);
							var newDepositStatusDict = newDepositStatusList.ToDictionary(_ => _.DepositGuid);
						    var existIndexes = await _exchangeSessionModel.Data.DepositList.IndexesOfAsync(
						        _ => newDepositStatusDict.ContainsKey(_.DepositGuid)
						    );
						    foreach (var j in existIndexes)
						    {
						        var depositGuid = (await _exchangeSessionModel.Data.DepositList.GetItemDeepCopyAtAsync(
						            j
						        ).ConfigureAwait(false)).DepositGuid;
                                if (newDepositStatusDict.ContainsKey(depositGuid))
                                {
                                    await _exchangeSessionModel.Data.DepositList.ReplaceItemAtAsync(
                                        j,
                                        newDepositStatusDict[depositGuid]
                                    ).ConfigureAwait(false);
                                }
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
		private readonly SemaphoreSlim _updateDepositListLockSem
			= new SemaphoreSlim(1);
		public async void ReadNewDepositList()
		{
			try
			{
				using (_stateHelper.GetFuncWrapper())
				{
					var lockSemCalledWrapper = _updateDepositListLockSem.GetCalledWrapper();
					lockSemCalledWrapper.Called = true;
					using (await _updateDepositListLockSem.GetDisposable(true).ConfigureAwait(false))
					{
						var lastKnownDepositGuid = (await _exchangeSessionModel.Data.DepositList.LastOrDefaultDeepCopyAsync().ConfigureAwait(false))?.DepositGuid ?? Guid.Empty;
						while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
						{
							lockSemCalledWrapper.Called = false;
							var currentLastKnownDepositGuid = lastKnownDepositGuid;
							var newDeposits = await MiscFuncs.RepeatWhileTimeout(
								async () => await _authenticatedExchangeInterface.GetNewDeposits(
									new GetAllActiveNewOtherDepositsRequest()
									{
										LastKnownDepositGuid = currentLastKnownDepositGuid,
										MaxBufferCount = 100,
										StartTime = _initTime - ExchangeServerSessionInternalConstants.DefaultLastTs
                                    }
									),
								_cts.Token
								).ConfigureAwait(false);
							if (newDeposits.Any())
							{
								await _exchangeSessionModel.Data.DepositList.AddRangeAsync(newDeposits).ConfigureAwait(false);
								lastKnownDepositGuid = newDeposits.Last().DepositGuid;
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
		private readonly SemaphoreSlim _updateWithdrawListLockSem = new SemaphoreSlim(1);
		public async void ReadNewWithdrawList()
		{
			try
			{
				using (_stateHelper.GetFuncWrapper())
				{
					var lockSemCalledWrapper = _updateWithdrawListLockSem.GetCalledWrapper();
					lockSemCalledWrapper.Called = true;
					using (await _updateWithdrawListLockSem.GetDisposable(true).ConfigureAwait(false))
					{
					    var lastKnownWithdrawGuid =
					        (await _exchangeSessionModel.Data.WithrawList.LastOrDefaultDeepCopyAsync().ConfigureAwait(false))?
					            .WithdrawGuid ?? Guid.Empty;
						while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
						{
							lockSemCalledWrapper.Called = false;
							var currentLastKnownWithdrawGuid = lastKnownWithdrawGuid;
							var newWithdraws = await MiscFuncs.RepeatWhileTimeout(
								async () => await _authenticatedExchangeInterface.GetNewWithdraws(
									new GetAllActiveNewOtherWithdrawRequest()
									{
										LastKnownWithdrawGuid = currentLastKnownWithdrawGuid,
										MaxBufferCount = 100,
										StartTime = _initTime - ExchangeServerSessionInternalConstants.DefaultLastTs
                                    }
								),
								_cts.Token
								).ConfigureAwait(false);
							if (newWithdraws.Any())
							{
							    await _exchangeSessionModel.Data.WithrawList.AddRangeAsync(
							        newWithdraws
							    ).ConfigureAwait(false);
								lastKnownWithdrawGuid = newWithdraws.Last().WithdrawGuid;
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
		
		public async Task<Guid> AddNewWithdraw(
			Guid accountGuid,
			ExchangePaymentDetails paymentDetails,
			decimal withdrawValuePos,
			CancellationToken token
			)
		{
			Assert.NotEqual(accountGuid, Guid.Empty);
            Assert.NotNull(paymentDetails);
            paymentDetails.CheckMe();
			Assert.True(withdrawValuePos > 0 && withdrawValuePos <= ExchangeServerConstants.MaxAbsCurrencyValue);
			using (_stateHelper.GetFuncWrapper())
			{
				var requestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
				return await MiscFuncs.RepeatWhileTimeout(
					async () => await _authenticatedExchangeInterface.CreateNewWithdraw(
						new CreateNewWithdrawRequest()
						{
							AccountGuid = accountGuid,
							RequestGuid = requestGuid,
							ValueNeg = -withdrawValuePos,
							PaymentDetails = paymentDetails
						}
					).ConfigureAwait(false)
				).ConfigureAwait(false);
			}
		}

		public async Task<Guid> AddNewDeposit(
			Guid accountGuid,
			decimal valuePos,
			CancellationToken token
		)
		{
			Assert.NotEqual(accountGuid,Guid.Empty);
			Assert.True(valuePos > 0 && valuePos <= ExchangeServerConstants.MaxAbsCurrencyValue);
			using (_stateHelper.GetFuncWrapper())
			{
				var requestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
				return await MiscFuncs.RepeatWhileTimeout(
					async () => await _authenticatedExchangeInterface.CreateNewDeposit(
						new CreateNewDepositRequest()
						{
							AccountGuid = accountGuid,
							RequestGuid = requestGuid,
							ValuePos = valuePos
						}
						),
					_cts.Token,
					token
				).ConfigureAwait(false);
			}
		}
		/**/
		private readonly SemaphoreSlim _updateWithdrawStatusLockSem = new SemaphoreSlim(1);
		/// <summary>
		/// 
		/// </summary>
		/// <param name="withdrawPairList">(MutableTuple) accountGuid, withdrawGuid</param>
		private async void UpdateWithdrawStatus(
			IList<MutableTuple<Guid, Guid>> withdrawPairList
			)
		{
			try
			{
				using (_stateHelper.GetFuncWrapper())
				{
					using (await _updateWithdrawStatusLockSem.GetDisposable().ConfigureAwait(false))
					{
						foreach (var withdrawEnumerable in withdrawPairList.Select(_ => _.Item2).Batch(100))
						{
							var currentWithdrawList = withdrawEnumerable.ToList();
							var newWithdrawStatusList = await MiscFuncs.RepeatWhileTimeout(
								async () => await _authenticatedExchangeInterface.GetWithdrawInfos(
									new GetWithdrawInfosRequest()
									{
										WithdrawGuidList = currentWithdrawList.Distinct().ToList()
									}
									).ConfigureAwait(false),
								_cts.Token
								).ConfigureAwait(false);
							var newWithdrawStatusDict = newWithdrawStatusList.ToDictionary(_ => _.WithdrawGuid);
						    var indices = await _exchangeSessionModel.Data.WithrawList.IndexesOfAsync(
						        w => newWithdrawStatusDict.ContainsKey(w.WithdrawGuid)
                            ).ConfigureAwait(false);
						    foreach (var i in indices)
						    {
						        var withdrawGuid = (
                                    await _exchangeSessionModel.Data.WithrawList.GetItemDeepCopyAtAsync(i)
                                ).WithdrawGuid;
						        await _exchangeSessionModel.Data.WithrawList.ReplaceItemAtAsync(
						            i,
						            newWithdrawStatusDict[withdrawGuid]
						        ).ConfigureAwait(false);
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
        private readonly ConcurrentDictionary<string,ExchangePaymentDetails> _emptyWithdrawPaymentDetailsDict
            = new ConcurrentDictionary<string, ExchangePaymentDetails>();
        public async Task<ExchangePaymentDetails> GetEmptyWithdrawPaymentDetailsDeepCopy(
	        string currencyCode,
            CancellationToken token
	    )
	    {
            Assert.NotNull(currencyCode);
            Assert.InRange(
                currencyCode.Length,
                1,
                ExchangeServerConstants.MaxCurrencyCodeLength
            );
	        using (_stateHelper.GetFuncWrapper())
	        {
	            ExchangePaymentDetails result;
	            if (_emptyWithdrawPaymentDetailsDict.TryGetValue(currencyCode, out result))
	            {
	                return MiscFuncs.GetDeepCopy(result);
	            }
	            else
	            {
	                result = await MiscFuncs.RepeatWhileTimeout(
	                    async () => await _exchangeTransport.GetEmptyWithdrawPaymentDetails(currencyCode).ConfigureAwait(false),
	                    _cts.Token,
	                    token
	                ).ConfigureAwait(false);
	                _emptyWithdrawPaymentDetailsDict.TryAdd(
	                    currencyCode,
	                    result
	                );
	                return MiscFuncs.GetDeepCopy(result);
	            }
	        }
	    }

	    public async Task ProlongDeposit(
            Guid depositGuid, 
            Guid accountGuid, 
            CancellationToken token
        )
	    {
	        using (_stateHelper.GetFuncWrapper())
	        {
                Assert.NotEqual(depositGuid,Guid.Empty);
                Assert.NotEqual(accountGuid,Guid.Empty);
	            var requestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
	            await MiscFuncs.RepeatWhileTimeout(
	                async () => await _authenticatedExchangeInterface.ProlongDepositOnePeriod(
	                    new ProlongDepositOnePeriodRequest()
	                    {
	                        DepositGuid = depositGuid,
	                        RequestGuid = requestGuid,
	                        AccountGuid = accountGuid
	                    }
	                    ),
	                token,
	                _cts.Token
	            ).ConfigureAwait(false);
	        }
	    }
	}
}
