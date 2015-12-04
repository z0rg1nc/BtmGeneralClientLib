using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.GeneralClientInterfaces.ExchangeServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged.MyObservableCollections;
using BtmI2p.ObjectStateLib;
using MoreLinq;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.ExchangeServerSession
{
    public partial class ExchangeServerSession
    {
        private void InitAccountSubscriptions()
        {
            var data = _exchangeSessionModel.Data;
            _subscriptions.Add(
                data.AccountCollection.CollectionChangedObservable
                    .BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(i =>
                        {
                            UpdateAccountBalanceInfos();
                            UpdateAccountTransferDict(Guid.Empty);
							UpdateAccountLockedFundsDict(Guid.Empty);
                        }
                    )
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.AccountListChanged)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => UpdateAccountList())
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(
                        _ => _.EventType == EExchangeClientEventTypes.AccountNewTransfer
                        || _.EventType == EExchangeClientEventTypes.AccountLockedFundsAdded
                        || _.EventType == EExchangeClientEventTypes.AccountLockedFundsModified
                        || _.EventType == EExchangeClientEventTypes.AccountLockedFundsReleased
					).BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => UpdateAccountBalanceInfos())
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.AccountNewTransfer)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ =>
                    {
                        if(!_.Any())
                            return;
                        var accountGuids = _.Select(__ => (Guid) __.EventArgs).Distinct();
                        foreach (var accountGuid in accountGuids)
                        {
                            UpdateAccountTransferDict(accountGuid);
                        }
                    })
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => _.EventType == EExchangeClientEventTypes.AccountLockedFundsAdded)
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(
                        buffer =>
                        {
                            var accountGuids = buffer
                                .Select(_ => ((MutableTuple<Guid, Guid>) _.EventArgs).Item1)
                                .Distinct();
                            foreach (var accountGuid in accountGuids)
                            {
                                UpdateAccountLockedFundsDict(accountGuid);
                            }
                        }
                    )
            );
            _subscriptions.Add(
                ServerEvents
                    .Where(_ => 
                        _.EventType == EExchangeClientEventTypes.AccountLockedFundsModified
                        || _.EventType == EExchangeClientEventTypes.AccountLockedFundsReleased
                    ).Select(_ =>
                    {
                        var args = (MutableTuple<Guid, Guid>) _.EventArgs;
                        _delayedLockedFundsToUpdate.TryAdd(args.Item2, args.Item1);
                        return 0;
                    })
					.BufferNotEmpty(TimeSpan.FromSeconds(2.0))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => UpdateLockedFundsStatus())
            );
        }

        public async Task<Guid> RegisterNewAccount(
            string currencyCode,
            CancellationToken token
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                var newAccountGuid = await MiscFuncs.RepeatWhileTimeout(
                    _authenticatedExchangeInterface.GetNewAccountGuidForRegistration,
                    token
                ).ConfigureAwait(false);
                try
                {
                    await MiscFuncs.RepeatWhileTimeout(
                        async () =>
                        {
                            await _authenticatedExchangeInterface
                                .RegisterNewAccount(
                                    new ExchangeAccountClientInfo()
                                    {
                                        AccountGuid = newAccountGuid,
                                        CurrencyCode = currencyCode,
                                        IsDefaultForTheCurrency = false
                                    }
                                ).ConfigureAwait(false);
                        },
                        token
                    ).ConfigureAwait(false);
                }
                catch (RpcRethrowableException rpcExc)
                {
                    if (
                        rpcExc.ErrorData.ErrorCode
                        != (int)ERegisterNewAccountErrCodes.AlreadyRegistered
                    )
                        throw;
                }
                return newAccountGuid;
            }
        }
        /**/
        private readonly SemaphoreSlim _updateAccountListLockSem
            = new SemaphoreSlim(1);
        public async void UpdateAccountList()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateAccountListLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateAccountListLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            lockSemCalledWrapper.Called = false;
                            var newAccountList = await MiscFuncs.RepeatWhileTimeout(
                                _authenticatedExchangeInterface.GetAllAccounts,
                                _cts.Token
                            ).ConfigureAwait(false);
                            await _exchangeSessionModel.Data.AccountCollection.ClearAsync().ConfigureAwait(false);
                            await
                                _exchangeSessionModel.Data.AccountCollection.AddRangeAsync(newAccountList)
                                    .ConfigureAwait(false);
                            _exchangeSessionModel.Data.AccountListUpdated = true;
                            if (!lockSemCalledWrapper.Called)
                                break;
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
        private readonly SemaphoreSlim _updateAccountBalancesListLockSem
            = new SemaphoreSlim(1);
        public async void UpdateAccountBalanceInfos()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateAccountBalancesListLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateAccountBalancesListLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (true)
                        {
                            lockSemCalledWrapper.Called = false;
                            var newAccountBalanceList = await MiscFuncs.RepeatWhileTimeout(
                                _authenticatedExchangeInterface.GetAllAccountBalances,
                                _cts.Token
                            ).ConfigureAwait(false);
                            await _exchangeSessionModel.Data.AccountBalanceCollection
                                .ClearAsync().ConfigureAwait(false);
                            await _exchangeSessionModel.Data.AccountBalanceCollection
                                .AddRangeAsync(newAccountBalanceList).ConfigureAwait(false);
                            _exchangeSessionModel.Data.AccountBalanceListUpdated = true;
                            if (!lockSemCalledWrapper.Called)
                                break;
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
        public async Task MakeAccountDefault(Guid accountGuid, CancellationToken token)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                await MiscFuncs.RepeatWhileTimeout(
                    async () =>
                    {
                        await _authenticatedExchangeInterface.MakeAccountDefault(
                            accountGuid
                        ).ConfigureAwait(false);
                    }
                ).ConfigureAwait(false);
            }
        }
        /**/
        private readonly ConcurrentDictionary<Guid,SemaphoreSlim> _updateAccountTransferListLockSemDict
            = new ConcurrentDictionary<Guid, SemaphoreSlim>();
        // if accountGuid == Guid.Empty update all
        private async void UpdateAccountTransferDict(Guid accountGuidToUpdate)
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    IList<Guid> accountList = 
                        accountGuidToUpdate == Guid.Empty
                        ? (await (
                            await GetUpdatableValueTemplate(
                                () => _exchangeSessionModel.Data.AccountCollection,
                                UpdateAccountList,
                                () => _exchangeSessionModel.Data.AccountListUpdated,
                                _cts.Token
                            ).ConfigureAwait(false)
                        ).GetDeepCopyAsync().ConfigureAwait(false)).NewItems.Select(_ => _.AccountGuid).ToList()
                        : new List<Guid> { accountGuidToUpdate };
                    foreach (var accountGuid in accountList)
                    {
                        if(_cts.IsCancellationRequested)
                            return;
                        var currentAccountGuid = accountGuid;
                        // add key if not exist
                        await _exchangeSessionModel.Data.AccountLockedFundsCollectionChangedDict.WithAsyncLockSem(
                            _ =>
                            {
                                if(!_.ContainsKey(currentAccountGuid))
                                    _.Add(
                                        currentAccountGuid,
                                        new MyObservableCollectionSafeAsyncImpl<ExchangeAccountLockedFundsClientInfo>()
                                    );
                            }
                        ).ConfigureAwait(false);
                        var lockSem = _updateAccountTransferListLockSemDict.GetOrAdd(
                           accountGuid,
                           _ => new SemaphoreSlim(1)
                        );
                        var lockSemCalledWrapper = lockSem.GetCalledWrapper();
                        lockSemCalledWrapper.Called = true;
                        using (await lockSem.GetDisposable(true).ConfigureAwait(false))
                        {
                            var lastKnownTransferGuid = await _exchangeSessionModel.Data.AccountTransferCollectionChangedDict.WithAsyncLockSem(
                                async transferDict =>
                                {
                                    if(!transferDict.ContainsKey(currentAccountGuid))
                                        transferDict.Add(currentAccountGuid,
                                            new MyObservableCollectionSafeAsyncImpl<ExchangeAccountTranferClientInfo>());
                                    var transferList = transferDict[currentAccountGuid];
                                    return
                                        (await transferList.LastOrDefaultDeepCopyAsync().ConfigureAwait(false))?
                                            .TransferGuid ?? Guid.Empty;
                                }
                            ).ConfigureAwait(false);
							while (!_cts.IsCancellationRequested)
                            {
                                lockSemCalledWrapper.Called = false;
                                var currentLastKnownTransferGuid = lastKnownTransferGuid;
                                var newTransfers = await MiscFuncs.RepeatWhileTimeout(
                                    async () => await _authenticatedExchangeInterface.GetAccountTransfers(
                                        new GetAccountTransfersRequest
                                        {
                                            AccountGuid = currentAccountGuid,
                                            StartTime = _initTime - ExchangeServerSessionInternalConstants.DefaultLastTs,
                                            LastKnownTransferGuid = currentLastKnownTransferGuid,
                                            MaxBufferCount = 100
                                        }
                                    ).ConfigureAwait(false),
                                    _cts.Token
                                ).ConfigureAwait(false);
                                if (newTransfers.Any())
                                {
                                    lastKnownTransferGuid = newTransfers.Last().TransferGuid;

                                    await _exchangeSessionModel.Data.AccountTransferCollectionChangedDict.WithAsyncLockSem(
                                        async transferDict =>
                                        {
                                            var transferList = transferDict[currentAccountGuid];
                                            await transferList.AddRangeAsync(newTransfers).ConfigureAwait(false);
                                        }
                                    ).ConfigureAwait(false);
                                }
                                if(!newTransfers.Any() && !lockSemCalledWrapper.Called)
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
        private readonly ConcurrentDictionary<Guid,SemaphoreSlim> _updateAccountLockedFundsListLockSemDict
            = new ConcurrentDictionary<Guid, SemaphoreSlim>();
        // if accountGuid == Guid.Empty update all
        private async void UpdateAccountLockedFundsDict(Guid accountGuidToUpdate)
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var accountList =
                        accountGuidToUpdate == Guid.Empty
                        ? (await (
                            await GetUpdatableValueTemplate(
                                () => _exchangeSessionModel.Data.AccountCollection,
                                UpdateAccountList,
                                () => _exchangeSessionModel.Data.AccountListUpdated,
                                _cts.Token
                            ).ConfigureAwait(false)
                        ).GetDeepCopyAsync().ConfigureAwait(false)).NewItems.Select(_ => _.AccountGuid).ToList()
                        : new List<Guid> { accountGuidToUpdate };
                    foreach (var accountGuid in accountList)
                    {
                        if(_cts.IsCancellationRequested)
                            return;
                        var currentAccountGuid = accountGuid;
                        // add key if not exist
                        await _exchangeSessionModel.Data.AccountLockedFundsCollectionChangedDict.WithAsyncLockSem(
                            _ =>
                            {
                                if(!_.ContainsKey(currentAccountGuid))
									_.Add(
                                        currentAccountGuid,
                                        new MyObservableCollectionSafeAsyncImpl<ExchangeAccountLockedFundsClientInfo>()
                                    );
                            }
                        ).ConfigureAwait(false);
                        var lockSem = _updateAccountLockedFundsListLockSemDict.GetOrAdd(
                            accountGuid,
                            _ => new SemaphoreSlim(1)
                        );
                        lockSem.GetCalledWrapper().Called = true;
                        using (await lockSem.GetDisposable(true).ConfigureAwait(false))
                        {
                            var lastKnownLockedFundsGuid =
                                await _exchangeSessionModel.Data.AccountLockedFundsCollectionChangedDict.WithAsyncLockSem(
                                    async lockedFundsDict =>
                                    {
                                        if (!lockedFundsDict.ContainsKey(currentAccountGuid))
                                            lockedFundsDict.Add(
                                                currentAccountGuid,
                                                new MyObservableCollectionSafeAsyncImpl<ExchangeAccountLockedFundsClientInfo>()
                                            );
                                        var lockedFundsList = lockedFundsDict[currentAccountGuid];
                                        return (await lockedFundsList.WhereAsync(_ => _.IsActive).ConfigureAwait(false))
                                            .With(_ => _.Any() ? _.Last().LockedFundsGuid : Guid.Empty);
                                    }
                                ).ConfigureAwait(false);
							while (!_cts.IsCancellationRequested && lockSem.GetCalledWrapper().Called)
                            {
                                lockSem.GetCalledWrapper().Called = false;
                                var currentLastKnownLockedFundsGuid = lastKnownLockedFundsGuid;
                                var newLockedFunds = await MiscFuncs.RepeatWhileTimeout(
                                    async () => await _authenticatedExchangeInterface.GetNewActiveAccountLockedFunds(
                                        new GetActiveAccountLockedFundsRequest()
                                        {
                                            AccountGuid = currentAccountGuid,
                                            LastKnownLockedFundsGuid = currentLastKnownLockedFundsGuid,
                                            MaxBufferCount = 100
                                        }
                                        ).ConfigureAwait(false),
                                    _cts.Token
                                ).ConfigureAwait(false);
                                if (newLockedFunds.Any())
                                {
                                    lastKnownLockedFundsGuid = newLockedFunds.Last().LockedFundsGuid;
                                    await _exchangeSessionModel.Data.AccountLockedFundsCollectionChangedDict.WithAsyncLockSem(
                                        async lockedFundsDict =>
                                        {
                                            var lockedFundsList = lockedFundsDict[currentAccountGuid];
                                            await lockedFundsList.AddRangeAsync(newLockedFunds).ConfigureAwait(false);
                                        }
                                    ).ConfigureAwait(false);
                                    UpdateLockedFundsStatus();
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
        // LockedFundsGuid -> AccountGuid
        private readonly ConcurrentDictionary<Guid, Guid> _delayedLockedFundsToUpdate
            = new ConcurrentDictionary<Guid, Guid>();

        private class LockedFundsIdPair
        {
            public Guid LockedFundsGuid;
            public Guid AccountGuid;
        }
        private readonly SemaphoreSlim _updateLockedFundsStatusLockSem 
            = new SemaphoreSlim(1);
        private async void UpdateLockedFundsStatus()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _updateLockedFundsStatusLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _updateLockedFundsStatusLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
						while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            // LockedFundsGuid, AccountGuid
                            var lockedFundsToUpdate = await _exchangeSessionModel.Data
                                .AccountLockedFundsCollectionChangedDict.WithAsyncLockSem(
                                    async downloadedLockedFunds =>
                                    {
                                        var tData = _delayedLockedFundsToUpdate.Select(
                                            _ => new LockedFundsIdPair
                                            {
                                                LockedFundsGuid = _.Key,
                                                AccountGuid = _.Value
                                            }
                                        ).ToList();
                                        // LockedFundsGuid, AccountGuid
                                        var result = new List<LockedFundsIdPair>(); 
                                        foreach (var accountGuid in downloadedLockedFunds.Keys)
                                        {
                                            var currentAccountGuid = accountGuid;
                                            // todo: replace with proper where condition
                                            var downloadedLockedFundsAccountGuid =
                                                (await
                                                    downloadedLockedFunds[currentAccountGuid].GetDeepCopyAsync()
                                                        .ConfigureAwait(false)).NewItems;
                                            result.AddRange(
                                                from newPair in tData.Where(_ => _.AccountGuid == currentAccountGuid)
												join oldPair in downloadedLockedFundsAccountGuid
                                                    on newPair.LockedFundsGuid equals oldPair.LockedFundsGuid
                                                select newPair
                                            );
                                        }
                                        return result;
                                    }
                                ).ConfigureAwait(false);
                            if (lockedFundsToUpdate.Any())
                            {
                                foreach (var lockedFundsId in lockedFundsToUpdate)
                                {
                                    Guid t;
                                    _delayedLockedFundsToUpdate.TryRemove(lockedFundsId.LockedFundsGuid, out t);
                                }
                                foreach (
                                    var lockedFundsGroupedByAccount 
                                    in lockedFundsToUpdate.GroupBy(_ => _.AccountGuid)
                                )
                                {
                                    foreach (var fundsIdEnumerable in lockedFundsGroupedByAccount.Batch(100))
                                    {
                                        var fundsIdList = fundsIdEnumerable.ToList();
                                        Assert.NotEmpty(fundsIdList);
                                        var accountGuid = fundsIdList.First().AccountGuid;
                                        var lockedFundsGuidList =
                                            fundsIdList.Select(_ => _.LockedFundsGuid).ToList();
                                        var newLockedFundsInfos = await MiscFuncs.RepeatWhileTimeout(
                                            async () =>
                                                await _authenticatedExchangeInterface.GetAccountLockedFundsInfos(
                                                    new GetAccountLockedFundsStatusRequest()
                                                    {
                                                        AccountGuid = accountGuid,
                                                        LockedFundsGuidList = lockedFundsGuidList
                                                    }
                                                ).ConfigureAwait(false),
                                            _cts.Token
                                        ).ConfigureAwait(false);
                                        await _exchangeSessionModel.Data.AccountLockedFundsCollectionChangedDict.WithAsyncLockSem(
                                            async _ =>
                                            {
                                                if (!_.ContainsKey(accountGuid))
                                                    return;
                                                var bulkUpdate = new List<Tuple<int, ExchangeAccountLockedFundsClientInfo>>();
                                                foreach (var newStatus in newLockedFundsInfos)
                                                {
                                                    var indexes = await _[accountGuid].IndexesOfAsync(
                                                        __ => __.LockedFundsGuid == newStatus.LockedFundsGuid
                                                    ).ConfigureAwait(false);
                                                    if (indexes.Count > 0)
                                                    {
                                                        bulkUpdate.Add(Tuple.Create(indexes[0], newStatus));
                                                    }
                                                }
                                                if (bulkUpdate.Any())
                                                {
                                                    await _[accountGuid].ReplaceBulkAsync(
                                                        bulkUpdate
                                                    ).ConfigureAwait(false);
                                                }
                                            }
                                        ).ConfigureAwait(false);
                                    }
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
    }
}
