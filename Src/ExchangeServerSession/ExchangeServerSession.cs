using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.AuthenticatedTransport;
using BtmI2p.CdnProxyEndReceiverClientTransport;
using BtmI2p.GeneralClientInterfaces;
using BtmI2p.GeneralClientInterfaces.ExchangeServer;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.MyNotifyPropertyChanged.MyObservableCollections;
using BtmI2p.ObjectStateLib;
using BtmI2p.OneSideSignedJsonRpc;
using NLog;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.ExchangeServerSession
{
    public enum EExchangeSessionEventType
    {
        AccountChanged, // Guid
    }

    public interface IExchangeServerSessionModelData 
        : IMyNotifyPropertyChanged
    {
        bool AccountBalanceListUpdated { get; set; }
        MyObservableCollectionSafeAsyncImpl<ExchangeAccountTotalBalanceInfo> AccountBalanceCollection { get; } 
        /**/
        bool AccountListUpdated { get; set; }
        MyObservableCollectionSafeAsyncImpl<ExchangeAccountClientInfo> AccountCollection { get; }
        /**/
        bool CurrencyListUpdated { get; set; }
        MyObservableCollectionSafeAsyncImpl<ExchangeCurrencyClientInfo> 
            CurrencyCollection { get; }
        /**/
        bool SecurityListUpdated { get; set; }
        MyObservableCollectionSafeAsyncImpl<ExchangeSecurityClientInfo> SecurityCollection { get; }
		/**/
		bool CurrencyPairSecurityInfoListUpdated { get; set; }
		List<ExchangeCurrencyPairSecurityClientInfo> CurrencyPairSecurityInfoList { get; }
		/**/
        Dictionary<Guid, MyObservableCollectionSafeAsyncImpl<ExchangeAccountTranferClientInfo>> 
            AccountTransferCollectionChangedDict { get; } 
        /**/
        Dictionary<Guid, MyObservableCollectionSafeAsyncImpl<ExchangeAccountLockedFundsClientInfo>> 
            AccountLockedFundsCollectionChangedDict { get; }
        /**/
        MyObservableCollectionSafeAsyncImpl<ExchangeSecurityTradeClientInfo>
            TradesCollection { get; }
        /**/
        MyObservableCollectionSafeAsyncImpl<ExchangeSecurityOrderClientInfo> OrderList { get; }
		/**/
		LinkedList<string> SecCodesToUpdateDomEntries { get; }
		Dictionary<string,List<DomEntry>> DomEntries { get; }
		/**/
		bool FeesDictUpdated { get; set; }
		Dictionary<EExchangeFeeTypes, decimal> FeesDict { get; }
        /**/
        MyObservableCollectionSafeAsyncImpl<ExchangeWithdrawClientInfo> WithrawList { get; }
        /**/
        MyObservableCollectionSafeAsyncImpl<ExchangeDepositClientInfo> DepositList { get; }
        /**/
        List<Tuple<string, EExchangeChartTimeframe>> CandlesToUpdate { get; }
        MyObservableCollectionSafeAsyncImpl<ExchangeChartCandleClientInfo>
            GetChartCandlesCollection(
                string secCode,
                EExchangeChartTimeframe timeframe
            );
    }

    public class ExchangeServerSessionModelData 
        : IExchangeServerSessionModelData
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public static async Task Reset(IExchangeServerSessionModelData data)
        {
            /**/
            data.AccountListUpdated = false;
            /**/
            await data.AccountCollection.ClearAsync().ConfigureAwait(false);
            /**/
            data.CurrencyListUpdated = false;
            /**/
            await data.CurrencyCollection.ClearAsync().ConfigureAwait(false);
            /**/
            data.SecurityListUpdated = false;
            /**/
            await data.SecurityCollection.ClearAsync().ConfigureAwait(false);
            /**/
            data.AccountBalanceListUpdated = false;
            /**/
            await data.AccountBalanceCollection.ClearAsync().ConfigureAwait(false);
            /**/
            using (await data.AccountTransferCollectionChangedDict.GetLockSem().GetDisposable().ConfigureAwait(false))
            {
                foreach (var collectionChanged in data.AccountTransferCollectionChangedDict.Values)
                {
                    Assert.NotNull(collectionChanged);
                    await collectionChanged.ClearAsync().ConfigureAwait(false);
                }
                data.AccountTransferCollectionChangedDict.Clear();
            }
            /**/
	        using (await data.AccountLockedFundsCollectionChangedDict.GetLockSem().GetDisposable().ConfigureAwait(false))
	        {
                foreach (var collectionChanged in data.AccountLockedFundsCollectionChangedDict.Values)
                {
                    Assert.NotNull(collectionChanged);
                    await collectionChanged.ClearAsync().ConfigureAwait(false);
                }
                data.AccountLockedFundsCollectionChangedDict.Clear();
	        }
            /**/
            await data.TradesCollection.ClearAsync().ConfigureAwait(false);
            /**/
            await data.OrderList.ClearAsync().ConfigureAwait(false);
			/**/
			using(await data.SecCodesToUpdateDomEntries.GetLockSem().GetDisposable().ConfigureAwait(false))
				data.SecCodesToUpdateDomEntries.Clear();
			MyNotifyPropertyChangedArgs.RaiseProperyChanged(
				data,
				_ => _.SecCodesToUpdateDomEntries
			);
			/**/
			using(await data.DomEntries.GetLockSem().GetDisposable().ConfigureAwait(false))
				data.DomEntries.Clear();
			MyNotifyPropertyChangedArgs.RaiseProperyChanged(
				data,
				_ => _.DomEntries
			);
			/**/
			data.CurrencyPairSecurityInfoListUpdated = false;
			await data.CurrencyPairSecurityInfoList.WithAsyncLockSem(
				_ => _.Clear()
			).ConfigureAwait(false);
			MyNotifyPropertyChangedArgs.RaiseProperyChanged(
				data,
				_ => _.CurrencyPairSecurityInfoList
			);
			/**/
			data.FeesDictUpdated = false;
	        await data.FeesDict.WithAsyncLockSem(
		        _ => _.Clear()
		    ).ConfigureAwait(false);
			MyNotifyPropertyChangedArgs.RaiseProperyChanged(
				data,
				_ => _.FeesDict
			);
			/**/
            await data.WithrawList.ClearAsync().ConfigureAwait(false);
			/**/
            await data.DepositList.ClearAsync().ConfigureAwait(false);
            /**/
            await data.CandlesToUpdate.WithAsyncLockSem(
                _ => _.Clear()
            ).ConfigureAwait(false);
        }

	    public bool AccountBalanceListUpdated { get; set; } = false;
        public MyObservableCollectionSafeAsyncImpl<ExchangeAccountTotalBalanceInfo> AccountBalanceCollection { get; } = new MyObservableCollectionSafeAsyncImpl<ExchangeAccountTotalBalanceInfo>();
	    public bool AccountListUpdated { get; set; } = false;
        public MyObservableCollectionSafeAsyncImpl<ExchangeAccountClientInfo> AccountCollection { get; } = new MyObservableCollectionSafeAsyncImpl<ExchangeAccountClientInfo>();
	    public bool CurrencyListUpdated { get; set; } = false;
        public MyObservableCollectionSafeAsyncImpl<ExchangeCurrencyClientInfo> CurrencyCollection { get; } = new MyObservableCollectionSafeAsyncImpl<ExchangeCurrencyClientInfo>();
	    public bool SecurityListUpdated { get; set; } = false;
        public MyObservableCollectionSafeAsyncImpl<ExchangeSecurityClientInfo> SecurityCollection { get; } = new MyObservableCollectionSafeAsyncImpl<ExchangeSecurityClientInfo>();
		/**/
	    public bool CurrencyPairSecurityInfoListUpdated { get; set; } = false;
		public List<ExchangeCurrencyPairSecurityClientInfo> 
			CurrencyPairSecurityInfoList { get; } = new List<ExchangeCurrencyPairSecurityClientInfo>();
		/**/
	    public Dictionary<Guid, MyObservableCollectionSafeAsyncImpl<ExchangeAccountTranferClientInfo>> 
            AccountTransferCollectionChangedDict { get; } = new Dictionary<Guid, MyObservableCollectionSafeAsyncImpl<ExchangeAccountTranferClientInfo>>();
        public Dictionary<Guid, MyObservableCollectionSafeAsyncImpl<ExchangeAccountLockedFundsClientInfo>>
            AccountLockedFundsCollectionChangedDict { get; } = new Dictionary<Guid, MyObservableCollectionSafeAsyncImpl<ExchangeAccountLockedFundsClientInfo>>();
        public MyObservableCollectionSafeAsyncImpl<ExchangeSecurityTradeClientInfo> 
            TradesCollection { get; } = new MyObservableCollectionSafeAsyncImpl<ExchangeSecurityTradeClientInfo>();
        public MyObservableCollectionSafeAsyncImpl<ExchangeSecurityOrderClientInfo> 
            OrderList { get; } = new MyObservableCollectionSafeAsyncImpl<ExchangeSecurityOrderClientInfo>();

	    public LinkedList<string>
		    SecCodesToUpdateDomEntries { get; } = new LinkedList<string>();
		public Dictionary<string, List<DomEntry>> 
			DomEntries { get; } = new Dictionary<string, List<DomEntry>>();

	    public bool FeesDictUpdated { get; set; } = false;
		public Dictionary<EExchangeFeeTypes, decimal>
			FeesDict { get; } = new Dictionary<EExchangeFeeTypes, decimal>();

	    public MyObservableCollectionSafeAsyncImpl<ExchangeWithdrawClientInfo> WithrawList { get; } 
            = new MyObservableCollectionSafeAsyncImpl<ExchangeWithdrawClientInfo>();
	    public MyObservableCollectionSafeAsyncImpl<ExchangeDepositClientInfo> DepositList { get; } 
            = new MyObservableCollectionSafeAsyncImpl<ExchangeDepositClientInfo>();

        public List<Tuple<string, EExchangeChartTimeframe>> CandlesToUpdate { get; } 
            = new List<Tuple<string, EExchangeChartTimeframe>>();

        public MyObservableCollectionSafeAsyncImpl<ExchangeChartCandleClientInfo> 
            GetChartCandlesCollection(string secCode, EExchangeChartTimeframe timeframe)
        {
            return _chartCandlesDict.GetOrAdd(
                Tuple.Create(secCode, timeframe),
                x => new MyObservableCollectionSafeAsyncImpl<ExchangeChartCandleClientInfo>()
            );
        }
        
        private readonly ConcurrentDictionary<
            Tuple<string, EExchangeChartTimeframe>, 
            MyObservableCollectionSafeAsyncImpl<ExchangeChartCandleClientInfo>
        > _chartCandlesDict = new ConcurrentDictionary<
            Tuple<string, EExchangeChartTimeframe>, 
            MyObservableCollectionSafeAsyncImpl<ExchangeChartCandleClientInfo>
        >();

        public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
        {
            get
            {
                throw new Exception(
                    MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString
                );
            }
        }
    }

    public class ExchangeSessionEvent
    {
        public EExchangeSessionEventType EventType;
        public object EventArg;
        /**/
        public readonly Guid EventGuid = MiscFuncs.GenGuidWithFirstBytes(0);
        public readonly DateTime RaisedDateTime = DateTime.UtcNow;
        /**/
        public static Type GetSessionEventArgsType(EExchangeSessionEventType eventType)
        {
            switch (eventType)
            {
                case EExchangeSessionEventType.AccountChanged:
                    return typeof (Guid);
                default:
                    throw new ArgumentOutOfRangeException(
                        MyNameof.GetLocalVarName(() => eventType));
            }
        }
    }
    public class ExchangeServerSessionModel
    {
        private readonly Subject<ExchangeSessionEvent> _sessionEventsSubject
            = new Subject<ExchangeSessionEvent>();
        public IObservable<ExchangeSessionEvent> SessionEvents => _sessionEventsSubject;

        public void RaiseSessionEvent<T1>(
            EExchangeSessionEventType eventType,
            T1 arg
        )
        {
            if (
                typeof(T1) != ExchangeSessionEvent.GetSessionEventArgsType(eventType)
            )
                throw new ArgumentException(
                    "Wrong arg type",
                    MyNameof.GetLocalVarName(() => arg)
                );
            _sessionEventsSubject.OnNext(
                new ExchangeSessionEvent()
                {
                    EventType = eventType,
                    EventArg = arg
                }
            );
        }

        public IExchangeServerSessionModelData Data
            = MyNotifyPropertyChangedImpl.GetProxy(
                (IExchangeServerSessionModelData) 
                    new ExchangeServerSessionModelData()
            );
    }

    public class ExchangeServerSessionSettings
    {
        public LightCertificate ExchangeClientCert = null;
    }
    public partial class ExchangeServerSession : IMyAsyncDisposable
    {
        private ExchangeServerSession()
        {
        }

        private static class ExchangeServerSessionInternalConstants
        {
            public static readonly TimeSpan DefaultLastTs = TimeSpan.FromDays(7.0);
        }

        private ExchangeServerSessionSettings _settings;
        private ExchangeServerSessionModel _exchangeSessionModel;
        private AesProtectedByteArray _exchangeClientCertPass;
        private DateTime _initTime;
        public static async Task<ExchangeServerSession> CreateInstance(
            ExchangeServerSessionSettings settings,
            ExchangeServerSessionModel exchangeSessionModel,
            IProxyServerSession proxySession,
            AesProtectedByteArray exchangeCertPass,
            CancellationToken cancellationToken
        )
        {
            var result = new ExchangeServerSession();
            result._initTime = await proxySession.GetNowTime().ConfigureAwait(false);
            result._settings = settings;
            result._exchangeClientCertPass = exchangeCertPass;
            result._exchangeSessionModel = exchangeSessionModel;
            ServerAddressForClientInfo exchangeServerInfo = null;
            var lookupSession =
                await proxySession.GetLookupServerSession(
                    cancellationToken
                ).ConfigureAwait(false);
            exchangeServerInfo = await lookupSession.GetExchangeServerAddress(
                settings.ExchangeClientCert.Id, 
                cancellationToken
            ).ConfigureAwait(false);
            Assert.NotNull(exchangeServerInfo);
            Assert.False(exchangeServerInfo.ServerGuid == Guid.Empty);
            /**/
            result._exchangeTransport = await EndReceiverServiceClientInterceptor
                .GetClientProxy<IFromClientToExchange>(
                    proxySession.ProxyInterface,
                    exchangeServerInfo.ServerGuid,
                    cancellationToken,
                    exchangeServerInfo.EndReceiverMethodInfos
                ).ConfigureAwait(false);
            var checkIsExchangeClientRegisteredTask = await Task.Factory.StartNew(
                async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            if (
                                !await result._exchangeTransport.IsExchangeClientCertRegistered(
                                    settings.ExchangeClientCert.Id
                                ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false)
                            )
                                throw new Exception("Exchange client not registered");
                            break;
                        }
                        catch (TimeoutException)
                        {
                        }
                    }
                }
            ).ConfigureAwait(false);
            var getSignedMethodInfosTask = await Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            return
                                (
                                    await result._exchangeTransport
                                        .GetSignedMethodInfos()
                                        .ThrowIfCancelled(cancellationToken).ConfigureAwait(false)
                                ).Select(x => x.JsonRpcMethodInfo)
                                .ToList();
                        }
                        catch (TimeoutException)
                        {
                        }
                    }
                }
            ).ConfigureAwait(false);
            var getAuthenticatedMethodInfosTask = await Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            return
                                (
                                    await result._exchangeTransport
                                        .GetAuthenticatedMethodInfos()
                                        .ThrowIfCancelled(cancellationToken).ConfigureAwait(false)
                                ).Select(x => x.JsonRpcMethodInfo)
                                .ToList();
                        }
                        catch (TimeoutException)
                        {
                        }
                    }
                }
            ).ConfigureAwait(false);
            await checkIsExchangeClientRegisteredTask.ConfigureAwait(false);
            /**/
            result._signedExchangeTransport =
                await ClientOneSideSignedTransport<
                    ISignedFromClientToExchange
                >
                    .CreateInstance(
                        new ServerOneSideSignedFuncsForClient()
                        {
                            GetMethodInfos =
                                async () =>
                                    (
                                        await result._exchangeTransport
                                            .GetSignedMethodInfos().ConfigureAwait(false)
                                    ).Select(x => x.JsonRpcMethodInfo)
                                    .ToList(),
                            ProcessSignedRequestPacket =
                                result
                                    ._exchangeTransport
                                    .ProcessSignedRequestPacket,
                            GetNowTimeFunc = proxySession.GetNowTime
                        },
                        settings.ExchangeClientCert,
                        exchangeCertPass,
                        cancellationToken,
                        await getSignedMethodInfosTask.ConfigureAwait(false)
                    ).ConfigureAwait(false);
            try
            {
                result._signedExchangeInterface
                    = await result._signedExchangeTransport.GetClientProxy().ConfigureAwait(false);
                /**/
                result._authenticatedExchangeTransport
                    = await ClientAuthenticatedTransport<
                        IAuthenticatedFromClientToExchange
                        >
                        .CreateInstance(
                            new ClientAuthenticatedTransportSettings(),
                            new ServerAuthenticatedFuncsForClient()
                            {
                                AuthMe =
                                    result
                                        ._exchangeTransport
                                        .AuthMe,
                                GetAuthData =
                                    result
                                        ._exchangeTransport
                                        .GetAuthData,
                                GetMethodInfos = async () =>
                                    (
                                        await result
                                            ._exchangeTransport
                                            .GetAuthenticatedMethodInfos().ConfigureAwait(false)
                                        )
                                        .Select(x => x.JsonRpcMethodInfo)
                                        .ToList(),
                                ProcessRequest =
                                    result
                                        ._exchangeTransport
                                        .ProcessAuthenticatedPacket,
                                DecryptAuthDataFunc = async dataToDecrypt =>
                                {
                                    using (var tempPass = exchangeCertPass.TempData)
                                    {
                                        return await Task.FromResult(
											settings.ExchangeClientCert.DecryptData(
												dataToDecrypt, 
												tempPass.Data
                                            )
										);
                                    }
                                }
                            },
                            settings.ExchangeClientCert.Id,
                            cancellationToken,
                            await getAuthenticatedMethodInfosTask.ConfigureAwait(false)
                        ).ConfigureAwait(false);
                try
                {
                    result._authenticatedExchangeInterface
                        = await
                            result
                                ._authenticatedExchangeTransport
                                .GetClientProxy().ConfigureAwait(false);
                    /**/
                    result._stateHelper.SetInitializedState();
                }
                catch(Exception)
                {
                    await result._authenticatedExchangeTransport.MyDisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
			catch(Exception)
            {
                await result._signedExchangeTransport.MyDisposeAsync().ConfigureAwait(false);
                throw;
            }
            /**/
            result.InitSubscriptions();
            /**/
            result.FeedServerEventsAction();
            // async voids
            result.UpdateAccountList();
            result.UpdateCurrencyList();
            result.UpdateSecurityList();
            result.UpdateAccountTransferDict(Guid.Empty);
            result.UpdateAccountLockedFundsDict(Guid.Empty);
            result.UpdateTradesList();
			result.UpdateOrderList();
	        result.UpdateDomEntries();
	        result.UpdateSecurityCurrencyPair();
			result.UpdateFeesDict();
			result.ReadNewDepositList();
			result.ReadNewWithdrawList();
            /**/
            return result;
        }
        /**/
        private void InitSubscriptions()
        {
            InitMiscSubscriptions();
            InitAccountSubscriptions();
            InitOrderBookSubscriptions();
			InitDwSubscriptions();
        }

        /**/
        private void HandleUnexpectedError(
            Exception exc,
            [CallerMemberName] string mthdName = "",
            [CallerLineNumber] int lineNumber = 0)
        {
            var logMessage = string.Format(
                "Unexpected error {0}:{1} {2}",
                mthdName,
                lineNumber,
                exc.ToString());
            _logger.Error(
                logMessage
            );
        }
        /**/
        public static async Task<TValue> GetUpdatableValueTemplate<TValue>(
            Func<TValue> valueGetter,
            Action updateAction,
            Func<bool> updatedBoolGetter,
            CancellationToken token
        )
            where TValue : class
        {
            Assert.NotNull(valueGetter);
            Assert.NotNull(updateAction);
            if (updatedBoolGetter())
                return valueGetter();
            updateAction();
            while (true)
            {
                if (updatedBoolGetter())
                    return valueGetter();
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }

        /**/
        private static readonly Logger _logger
            = LogManager.GetCurrentClassLogger();
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("ExchangeServerSession");
        private readonly CancellationTokenSource _cts
            = new CancellationTokenSource();
        private readonly List<IDisposable> _subscriptions
            = new List<IDisposable>();
        /**/
        private IFromClientToExchange _exchangeTransport;
        /**/
        private ClientOneSideSignedTransport<ISignedFromClientToExchange>
            _signedExchangeTransport;
        private ISignedFromClientToExchange _signedExchangeInterface;
        /**/
        private ClientAuthenticatedTransport<IAuthenticatedFromClientToExchange>
            _authenticatedExchangeTransport;
        private IAuthenticatedFromClientToExchange
            _authenticatedExchangeInterface;
        /**/
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            await _authenticatedExchangeTransport.MyDisposeAsync().ConfigureAwait(false);
            await _signedExchangeTransport.MyDisposeAsync().ConfigureAwait(false);
            _exchangeClientCertPass.Dispose();
            await ExchangeServerSessionModelData.Reset(
                _exchangeSessionModel.Data
            ).ConfigureAwait(false);
            _serverEvents.Dispose();
            _cts.Dispose();
        }
    }
}
