using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using BtmI2p.GeneralClientInterfaces.ExchangeServer;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.BitMoneyClient.Lib.ExchangeServerSession
{
    public partial class ExchangeServerSession
    {
        private void InitMiscSubscriptions()
        {
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.CurrencyListChanged)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => UpdateCurrencyList())
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.SecurityListChanged)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => UpdateSecurityList())
            );
			_subscriptions.Add(
				ServerEvents
					.Where(_ => _.EventType == EExchangeClientEventTypes.SecurityCurrencyPairListChanged)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
					.ObserveOn(TaskPoolScheduler.Default)
					.Subscribe(_ => UpdateSecurityCurrencyPair())
			);
        }

        /**/
        private readonly SemaphoreSlim _updateSecurityListLockSem
            = new SemaphoreSlim(1);
        public async void UpdateSecurityList()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateSecurityListLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateSecurityListLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
						while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var newSecurityList = await MiscFuncs.RepeatWhileTimeout(
                                _exchangeTransport.GetSecurityList,
                                _cts.Token
                            ).ConfigureAwait(false);
                            await _exchangeSessionModel.Data.SecurityCollection.ClearAsync().ConfigureAwait(false);
                            await _exchangeSessionModel.Data.SecurityCollection.AddRangeAsync(
                                newSecurityList
                            ).ConfigureAwait(false);
                            _exchangeSessionModel.Data.SecurityListUpdated = true;
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
        private readonly SemaphoreSlim _updateCurrencyListLockSem
            = new SemaphoreSlim(1);
        public async void UpdateCurrencyList()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateCurrencyListLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateCurrencyListLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
						while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var newCurrencyList = await MiscFuncs.RepeatWhileTimeout(
                                _exchangeTransport.GetCurrencyList,
                                _cts.Token
                            ).ConfigureAwait(false);
                            await _exchangeSessionModel.Data.CurrencyCollection
                                .ClearAsync().ConfigureAwait(false);
                            await _exchangeSessionModel.Data.CurrencyCollection
                                .AddRangeAsync(newCurrencyList).ConfigureAwait(false);
                            _exchangeSessionModel.Data.CurrencyListUpdated = true;
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
		private readonly SemaphoreSlim _updateSecurityCurrencyPairLockSem 
			= new SemaphoreSlim(1);
	    public async void UpdateSecurityCurrencyPair()
	    {
		    try
		    {
			    var lockSemCalledWrapper = 
					_updateSecurityCurrencyPairLockSem.GetCalledWrapper();
			    lockSemCalledWrapper.Called = true;
			    using (await _updateSecurityCurrencyPairLockSem.GetDisposable(true).ConfigureAwait(false))
			    {
				    while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
				    {
						lockSemCalledWrapper.Called = false;
					    var newSecurityCurrencyPairInfos = await MiscFuncs.RepeatWhileTimeout(
						    _exchangeTransport.GetCurrencyPairSecurityList,
						    _cts.Token
						).ConfigureAwait(false);
					    await _exchangeSessionModel.Data.CurrencyPairSecurityInfoList.WithAsyncLockSem(
						    _ =>
						    {
							    _.Clear();
							    _.AddRange(newSecurityCurrencyPairInfos);
						    }
						).ConfigureAwait(false);
					    _exchangeSessionModel.Data.CurrencyPairSecurityInfoListUpdated = true;
						MyNotifyPropertyChangedArgs.RaiseProperyChanged(
							_exchangeSessionModel.Data,
							_ => _.CurrencyPairSecurityInfoList
						);
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
		private readonly SemaphoreSlim _updateFeesDictLockSem = new SemaphoreSlim(1);
		public async void UpdateFeesDict()
	    {
			try
			{
				var lockSemCalledWrapper =
					_updateFeesDictLockSem.GetCalledWrapper();
				lockSemCalledWrapper.Called = true;
				using (await _updateFeesDictLockSem.GetDisposable(true).ConfigureAwait(false))
				{
					while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
					{
						lockSemCalledWrapper.Called = false;
						var newFees = await MiscFuncs.RepeatWhileTimeout(
							_exchangeTransport.GetFees,
							_cts.Token
						).ConfigureAwait(false);
						await _exchangeSessionModel.Data.FeesDict.WithAsyncLockSem(
							_ =>
							{
								_.Clear();
								foreach (var fee in newFees)
								{
									_.Add(fee.Item1,fee.Item2);
								}
							}
						).ConfigureAwait(false);
						_exchangeSessionModel.Data.FeesDictUpdated = true;
						MyNotifyPropertyChangedArgs.RaiseProperyChanged(
							_exchangeSessionModel.Data,
							_ => _.FeesDict
						);
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
    }
}
