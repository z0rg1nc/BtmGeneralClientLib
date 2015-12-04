using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.AuthenticatedTransport;
using BtmI2p.CdnProxyEndReceiverClientTransport;
using BtmI2p.GeneralClientInterfaces;
using BtmI2p.GeneralClientInterfaces.WalletServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.ObjectStateLib;
using BtmI2p.OneSideSignedJsonRpc;
using MoreLinq;
using NLog;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.WalletServerSession
{
    public class ClientTransferBase
    {
        public virtual int TransferNum { get; set; }
        public virtual Guid TransferGuid { get; set; }
        public virtual Guid RequestGuid { get; set; } = Guid.Empty;
        public virtual long Amount { get; set; }
        public virtual long Fee { get; set; } = 0;
        public virtual DateTime SentTime { get; set; }
        public virtual byte[] CommentBytes { get; set; } = new byte[0];
        public virtual bool AnonymousTransfer { get; set; }
        public virtual Guid WalletFrom { get; set; }
        public virtual Guid WalletTo { get; set; }
        public virtual bool OutcomeTransfer { get; set; } = false;
        public virtual bool AuthenticatedOtherWalletCert { get; set; } = false;
        public virtual bool AuthenticatedCommentKey { get; set; } = false;
        public virtual bool AuthenticatedTransferDetails { get; set; } = false;
    }
    
    public class PreparedToSendTransfer
    {
        public byte[] CommentBytes = new byte[0];
        public bool EncryptComment = true;
        public Guid WalletTo;
        public long Amount;
        public bool AnonymousTransfer = false;
        public Guid RequestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
        public long MaxFee = 0;
    }
    public enum EPreparedToSendTransferFaultErrCodes
    {
        None,
        WalletToNotExist,
        /* Additional error code in string value 
         * (EProcessSimpleTransferErrCodes.ToString())
         */
        ServerException,
        //Exception message in string value
        UnknownError
    }
    public class OnPreparedToSendTransferFaultArgs
	{
		public Guid RequestGuid { get; set; }
		public PreparedToSendTransfer PreparedTransfer { get; set; }
		public EPreparedToSendTransferFaultErrCodes FaultCode { get; set; }
		public string FaultMessage { get; set; }
		public EProcessSimpleTransferErrCodes ServerFaultCode { get; set; } 
            = EProcessSimpleTransferErrCodes.NoErrors;
        public EWalletGeneralErrCodes ServerGeneralFaultCode { get; set; }
            = EWalletGeneralErrCodes.NoErrors;
	}
    public interface IWalletServerSessionModel : IMyNotifyPropertyChanged
    {
        Subject<PreparedToSendTransfer> OnPreparedToSendTransferAdded { get; }
        Subject<Guid> OnPreparedToSendTransferComplete { get; } //Request guid
        Subject<OnPreparedToSendTransferFaultArgs> 
            OnPreparedToSendTransferFault { get; } //Request guid
        /**/
        Subject<List<ClientTransferBase>> OnTransferSent { get; }
        Subject<List<ClientTransferBase>> OnTransferReceived { get; }
        /**/
        long Balance { get; set; }
        WalletClientSettingsOnServer ClientSettingsOnServer { get; set; }
    }

    public class WalletServerSessionModel : IWalletServerSessionModel
    {
        public WalletServerSessionModel()
        {
            OnPreparedToSendTransferAdded = new Subject<PreparedToSendTransfer>();
            OnPreparedToSendTransferComplete = new Subject<Guid>();
            OnPreparedToSendTransferFault =
                new Subject<OnPreparedToSendTransferFaultArgs>();
            OnTransferSent = new Subject<List<ClientTransferBase>>();
            OnTransferReceived = new Subject<List<ClientTransferBase>>();
            Balance = 0;
            ClientSettingsOnServer = null;
        }
        public Subject<PreparedToSendTransfer> OnPreparedToSendTransferAdded { get; private set; }
        public Subject<Guid> OnPreparedToSendTransferComplete { get; private set; }
        public Subject<OnPreparedToSendTransferFaultArgs> 
            OnPreparedToSendTransferFault { get; private set; }
        public Subject<List<ClientTransferBase>> OnTransferSent { get; private set; }
        public Subject<List<ClientTransferBase>> OnTransferReceived { get; private set; }
        /**/
        public long Balance { get; set; }
        public WalletClientSettingsOnServer ClientSettingsOnServer { get; set; }
        /**/
        public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
        {
            get
            {
                throw new Exception(MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString);
            }
        }
    }

    public class WalletServerSessionSettings : ICheckable
    {
        public LightCertificate MasterWalletCert = null;
        public LightCertificate WalletCert = null;
        public TimeSpan CommentKeyLifetime = TimeSpan.FromDays(7.0f);
        /* Receiving transfer settings */
        public TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(
            ClientLifecycleEnvironment.LifeCycle == ELifeCycle.Dev
                ? 5
                : 180
        );
        public TimeSpan BufferTime = TimeSpan.FromSeconds(3.0f);
        public int BufferCount = 100;
        /**/
        public Guid LastKnownSentTransferGuid = Guid.Empty;
        public DateTime LastKnownSentTransferSentTime 
            = DateTime.UtcNow - TimeSpan.FromDays(30.0d);
        public Guid LastKnownReceivedTransferGuid = Guid.Empty;
        public DateTime LastKnownReceivedTransferSentTime 
            = DateTime.UtcNow - TimeSpan.FromDays(30.0d);
        public int TransferInitCounter = 0;
        /**/
        public List<PreparedToSendTransfer> PreparedToSendTransfers
            = new List<PreparedToSendTransfer>();
        /**/
        public void CheckMe()
        {
            if(
                WalletCert == null
                || PreparedToSendTransfers == null
            )
                throw new ArgumentNullException();
            WalletCert.CheckMe();
            if (
                !LightCertificateRestrictions.IsValid(
                    WalletCert
                )
            )
                throw new ArgumentOutOfRangeException(
                    this.MyNameOfProperty(e => e.WalletCert)
                );
            MasterWalletCert.CheckMe();
            if (
                !LightCertificateRestrictions.IsValid(
                    MasterWalletCert
                )
            )
                throw new ArgumentOutOfRangeException(
                    this.MyNameOfProperty(e => e.MasterWalletCert)
                );
        }
    }
    public partial class WalletServerSession : IMyAsyncDisposable
    {
        private readonly SemaphoreSlim _preparedToSendTransfersLockSem
            = new SemaphoreSlim(1);
        private readonly List<PreparedToSendTransfer> _preparedToSendTransfers
            = new List<PreparedToSendTransfer>();
        /**/
        private WalletServerSessionSettings _settings;
        private IWalletServerSessionModel _walletSessionModel;
        public IWalletServerSessionModel Model => _walletSessionModel;
        private AesProtectedByteArray _walletCertPass;
        /**/
        private IFromClientToWallet _walletTransport;
        /**/
        private ClientOneSideSignedTransport<ISignedFromClientToWallet>
            _signedWalletTransport;
        private ISignedFromClientToWallet 
            _signedWalletTransportInterface;
        /**/
        private ClientAuthenticatedTransport<IAuthenticatedFromClientToWallet>
            _authenticatedWalletTransport;
        private IAuthenticatedFromClientToWallet
            _authedWalletInterface;
        /**/
        private WalletServerSession()
        {
        }

        public async Task<List<PreparedToSendTransfer>> GetPreparedToSendTransfers()
        {
            using (await _preparedToSendTransfersLockSem.GetDisposable().ConfigureAwait(false))
            {
                return _preparedToSendTransfers;
            }
        }

        private WalletServerInfoForClient _serverInfo;
        private IGetRelativeTime _getRelativeTimeInterface;
        private DateTime _initTime;
        public static async Task<WalletServerSession> CreateInstance(
            IFromClientToWallet walletTransportInterface,
            WalletServerSessionSettings settings,
            IWalletServerSessionModel walletSessionModel,
            AesProtectedByteArray walletCertPass,
            CancellationToken cancellationToken,
            IGetRelativeTime getRelativeTimeInterface
        )
        {
            Assert.NotNull(settings);
            settings.CheckMe();
            using (var tempPass = walletCertPass.TempData)
            {
                settings.WalletCert.CheckMe(
                    true,
                    tempPass.Data
                );
            }
            if (
                !LightCertificateRestrictions.IsValid(
                    settings.WalletCert
                )
            )
                throw new ArgumentOutOfRangeException(
                    MyNameof.GetLocalVarName(() => settings.WalletCert)
                );
            var result = new WalletServerSession();
            result._initTime = await getRelativeTimeInterface.GetNowTime().ConfigureAwait(false);
            result._getRelativeTimeInterface = getRelativeTimeInterface;
            result._settings = settings;
            result._walletSessionModel = walletSessionModel;
            result._walletCertPass = walletCertPass;
            result._walletTransport = walletTransportInterface;
            var getSignedMethodInfosTask = await Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            return
                                (
                                    await result._walletTransport
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
                                    await result._walletTransport
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
            Assert.True(
                await MiscFuncs.RepeatWhileTimeout(
                    async () => await result._walletTransport.IsWalletRegistered(
                        settings.WalletCert.Id
                    ).ConfigureAwait(false),
                    cancellationToken
                ).ConfigureAwait(false)
            );
            /**/
            result._signedWalletTransport =
                await ClientOneSideSignedTransport<ISignedFromClientToWallet>
                    .CreateInstance(
                        new ServerOneSideSignedFuncsForClient()
                        {
                            GetMethodInfos = async () =>
                                (await result._walletTransport.GetSignedMethodInfos().ConfigureAwait(false))
                                    .Select(x => x.JsonRpcMethodInfo)
                                    .ToList(),
                            ProcessSignedRequestPacket =
                                result._walletTransport.ProcessSignedRequestPacket,
                            GetNowTimeFunc = getRelativeTimeInterface.GetNowTime
                        },
                        settings.WalletCert,
                        walletCertPass,
                        cancellationToken,
                        await getSignedMethodInfosTask.ConfigureAwait(false)
                    ).ConfigureAwait(false);
            try
                {
                    result._signedWalletTransportInterface =
                        await result._signedWalletTransport.GetClientProxy().ConfigureAwait(false);
                    /**/
                    result._authenticatedWalletTransport =
                        await ClientAuthenticatedTransport<IAuthenticatedFromClientToWallet>
                            .CreateInstance(
                                new ClientAuthenticatedTransportSettings(),
                                new ServerAuthenticatedFuncsForClient()
                                {
                                    AuthMe = result._walletTransport.AuthMe,
                                    GetAuthData = result._walletTransport.GetAuthData,
                                    GetMethodInfos =
                                        async () =>
                                            (await result
                                                ._walletTransport
                                                .GetAuthenticatedMethodInfos().ConfigureAwait(false))
                                                .Select(x => x.JsonRpcMethodInfo)
                                                .ToList(),
                                    ProcessRequest =
                                        result
                                            ._walletTransport
                                            .ProcessAuthenticatedPacket,
                                    DecryptAuthDataFunc = async dataToDecrypt =>
                                    {
                                        using (var tempPass = walletCertPass.TempData)
                                        {
                                            return await Task.FromResult(
												settings.WalletCert.DecryptData(
													dataToDecrypt, tempPass.Data
                                                )
											);
                                        }
                                    }
                                },
                                settings.WalletCert.Id,
                                cancellationToken,
                                await getAuthenticatedMethodInfosTask.ConfigureAwait(false)
                            ).ConfigureAwait(false);
				try
                {
                    result._authedWalletInterface =
                        await result._authenticatedWalletTransport.GetClientProxy().ConfigureAwait(false);
                    var getMySettingsTask = MiscFuncs.RepeatWhileTimeout(
                        async () => await result._authedWalletInterface
                            .GetMySettings().ConfigureAwait(false),
                        cancellationToken
                    );
                    var getServerInfoTask = MiscFuncs.RepeatWhileTimeout(
                        async () => await result._authedWalletInterface
                            .GetServerInfo().ConfigureAwait(false),
                        cancellationToken
                    );
                    walletSessionModel.ClientSettingsOnServer = await getMySettingsTask.ConfigureAwait(false);
                    result._serverInfo = await getServerInfoTask.ConfigureAwait(false);
                    /**/
                    result._stateHelper.SetInitializedState();
                    
                }
				catch (Exception)
                {
                    await result._authenticatedWalletTransport
                        .MyDisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
			catch (Exception)
            {
                await result._signedWalletTransport.MyDisposeAsync().ConfigureAwait(false);
                throw;
            }
            /**/
            result._sessionSubscriptions.Add(
                walletSessionModel.PropertyChangedSubject
                    .Where(
                        x =>
                            x.PropertyName
                            == walletSessionModel.MyNameOfProperty(
                                e => e.ClientSettingsOnServer
                                )
                    )
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(x => result.SaveClientSettings())
            );
            result._sessionSubscriptions.Add(
                walletSessionModel.OnPreparedToSendTransferAdded.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                    result.OnPreparedSendTransferAddedAction
                    )
                );
            /*
            result._sessionSubscriptions.Add(
                walletSessionModel.OnPreparedToSendTransferComplete
                    .ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        x => result.ProcessSentTranfers()
                    )
                );
            */
            result._sessionSubscriptions.Add(
                walletSessionModel.OnTransferReceived
                    .Throttle(TimeSpan.FromSeconds(3.0f))
                    .ObserveOn(TaskPoolScheduler.Default).Subscribe(i => result.UpdateBalance())
                );
            result._sessionSubscriptions.Add(
                walletSessionModel.OnTransferSent
                    .Throttle(TimeSpan.FromSeconds(3.0f))
                    .ObserveOn(TaskPoolScheduler.Default).Subscribe(i => result.UpdateBalance())
                );
            result._sessionSubscriptions.Add(
                result.ServerEvents
                    .Where(_ => _.EventType == EWalletClientEventTypes.NewSentTransfer)
                    .BufferNotEmpty(TimeSpan.FromSeconds(0.3))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => result.ProcessSentTranfers())
            );
            result._sessionSubscriptions.Add(
                result.ServerEvents
                    .Where(_ => _.EventType == EWalletClientEventTypes.NewReceivedTransfer)
                    .BufferNotEmpty(TimeSpan.FromSeconds(0.3))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => result.ProcessIncomeTransfers())
            );
            /**/
            result.FeedServerEventsAction();
            /**/
            result.ProcessIncomeTransfers();
            result.ProcessSentTranfers();
            foreach (
                PreparedToSendTransfer preparedToSendTransfer
                    in settings.PreparedToSendTransfers
                )
            {
                using (
                    await
                        result._preparedToSendTransfersLockSem.GetDisposable().ConfigureAwait(false)
                    )
                {
                    if (
                        result._preparedToSendTransfers
                            .Any(
                                x =>
                                    x.RequestGuid
                                    == preparedToSendTransfer.RequestGuid
                            )
                        )
                        continue;
                    result._preparedToSendTransfers
                        .Add(preparedToSendTransfer);
                }
                walletSessionModel
                    .OnPreparedToSendTransferAdded
                    .OnNext(preparedToSendTransfer);
            }
            return result;
        }

        public static async Task<WalletServerSession> CreateInstance(
            WalletServerSessionSettings settings,
            IWalletServerSessionModel walletSessionModel,
            IProxyServerSession proxySession,
            AesProtectedByteArray walletCertPass,
            CancellationToken cancellationToken
        )
        {
            var lookupSession = await proxySession.GetLookupServerSession(
                cancellationToken
                ).ConfigureAwait(false);
            ServerAddressForClientInfo walletServerInfo =
                await lookupSession.GetWalletServerAddress(
                    settings.WalletCert.Id,
                    cancellationToken
                ).ConfigureAwait(false);
            if (walletServerInfo == null)
                throw new ArgumentNullException();
            if (walletServerInfo.ServerGuid == Guid.Empty)
            {
                throw new Exception(
                    "walletServerInfo.ServerGuid == Guid.Empty"
                );
            }
            var walletTransportInterface =
                await EndReceiverServiceClientInterceptor
                    .GetClientProxy<IFromClientToWallet>(
                        proxySession.ProxyInterface,
                        walletServerInfo.ServerGuid,
                        cancellationToken,
                        walletServerInfo.EndReceiverMethodInfos
                    ).ConfigureAwait(false);
            return await CreateInstance(
                walletTransportInterface,
                settings,
                walletSessionModel,
                walletCertPass,
                cancellationToken,
                proxySession
            ).ConfigureAwait(false);
        }
        private readonly List<IDisposable> _sessionSubscriptions 
            = new List<IDisposable>();
        private readonly DisposableObjectStateHelper _stateHelper 
            = new DisposableObjectStateHelper("WalletServerSession");
        private readonly CancellationTokenSource _cts 
            = new CancellationTokenSource();
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();
        public class RegisterWalletResult
        {
            public Guid WalletGuid;
            public LightCertificate WalletCert;
            public LightCertificate MasterWalletCert;
        }
        /**/

        public static async Task<RegisterWalletResult> RegisterWallet(
            IFromClientToWallet walletServerInterface,
            LightCertificate newWalletCert,
            LightCertificate newMasterWalletCert,
            AesProtectedByteArray newWalletCertPass,
            WalletClientSettingsOnServer walletSettingsOnServer,
            CancellationToken cancellationToken,
            IGetRelativeTime getRelativeTimeInterface 
        )
        {
            Assert.NotNull(walletServerInterface);
            Assert.NotNull(newWalletCert);
            Assert.NotNull(newWalletCertPass);
            using (var tempData = newWalletCertPass.TempData)
            {
                newWalletCert.CheckMe(true, tempData.Data);
            }
            Assert.NotNull(newMasterWalletCert);
            newMasterWalletCert.CheckMe();
            Assert.NotNull(walletSettingsOnServer);
            Assert.NotNull(getRelativeTimeInterface);
            Assert.True(
                await MiscFuncs.RepeatWhileTimeout(
                    async () => await walletServerInterface.IsWalletGuidValidForRegistration(
                        newWalletCert.Id
                    ).ConfigureAwait(false),
                    cancellationToken
                ).ConfigureAwait(false)
            );
            Assert.False(
                await MiscFuncs.RepeatWhileTimeout(
                    async () => await walletServerInterface.IsWalletRegistered(
                        newWalletCert.Id
                    ).ConfigureAwait(false),
                    cancellationToken
                ).ConfigureAwait(false)
            );
            var registerRequestGuid = Guid.NewGuid();
            while (true)
            {
                try
                {
                    var request = new RegisterWalletOrderRequest()
                    {
                        PublicWalletCert = newWalletCert.GetOnlyPublic(),
                        PublicMasterWalletCert = newMasterWalletCert.GetOnlyPublic(),
                        WalletSettingsOnServer = walletSettingsOnServer,
                        SentTime = await getRelativeTimeInterface.GetNowTime().ConfigureAwait(false),
                        RequestGuid = registerRequestGuid
                    };
                    SignedData<RegisterWalletOrderRequest> signedRequest;
                    using (var tempPass = newWalletCertPass.TempData)
                    {
                        signedRequest = new SignedData<RegisterWalletOrderRequest>(
                            request,
                            newWalletCert,
                            tempPass.Data
                        );
                    }
                    await walletServerInterface.RegisterWallet(signedRequest)
                        .ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (RpcRethrowableException rpcExc)
                {
                    if (
                        rpcExc.ErrorData.ErrorCode
                        == (int)ERegisterWalletErrCodes.AlreadyRegistered
                    )
                        break;
                    throw;
                }
                catch (TimeoutException)
                {
                }
            }
            return new RegisterWalletResult()
            {
                MasterWalletCert = newMasterWalletCert,
                WalletCert = newWalletCert,
                WalletGuid = newWalletCert.Id
            };
        }

        /**/
        public static async Task<RegisterWalletResult> RegisterWallet(
            IFromClientToWallet walletServerInterface,
            AesProtectedByteArray newWalletCertPass,
            AesProtectedByteArray newMasterWalletCertPass,
            WalletClientSettingsOnServer walletSettingsOnServer,
            CancellationToken cancellationToken,
            IGetRelativeTime getRelativeTimeInterface,
            EWalletIdsTypeClient clientType = EWalletIdsTypeClient.Clients
        )
        {
            Assert.True(
                clientType.In(
                    EWalletIdsTypeClient.Clients,
                    EWalletIdsTypeClient.SystemUsed,
                    EWalletIdsTypeClient.Emission
                )
            );
            Guid newMasterWalletCertId = Guid.NewGuid();
            LightCertificate newMasterWalletCert;
            using (var tempData = newMasterWalletCertPass.TempData)
            {
                var masterWalletCertPasswordBytes = tempData.Data;
                newMasterWalletCert =
                    LightCertificatesHelper.GenerateSelfSignedCertificate(
                        ELightCertificateSignType.Rsa,
                        2048,
                        ELightCertificateEncryptType.Rsa,
                        2048,
                        EPrivateKeysKeyDerivationFunction.ScryptDefault,
                        newMasterWalletCertId,
                        $"{newMasterWalletCertId}",
                        masterWalletCertPasswordBytes
                    );
            }
            LightCertificate newWalletCert;
            using (var tempData = newWalletCertPass.TempData)
            {
                var walletCertPasswordBytes = tempData.Data;
                var firstCertBytes =
                    new byte[WalletServerClientConstants.WalletClientGuidFirstZeroBytesCount[clientType]];
                newWalletCert =
                    LightCertificatesHelper.GenerateSelfSignedCertificate(
                        ELightCertificateSignType.Rsa,
                        2048,
                        ELightCertificateEncryptType.Rsa,
                        2048,
                        EPrivateKeysKeyDerivationFunction.ScryptDefault,
                        firstCertBytes,
                        ECertGuidHashTypes.Scrypt8Mb, 
                        null,
                        walletCertPasswordBytes
                    );
                LightCertificatesHelper.SignCertificate(
                    newMasterWalletCert,
                    newWalletCert,
                    walletCertPasswordBytes
                );
            }
            return await RegisterWallet(
                walletServerInterface,
                newWalletCert,
                newMasterWalletCert,
                newWalletCertPass,
                walletSettingsOnServer,
                cancellationToken,
                getRelativeTimeInterface
            ).ConfigureAwait(false);
        }
        // Only for usual clients
        public static async Task<RegisterWalletResult> RegisterWallet(
            IProxyServerSession proxySession,
            LookupServerSession lookupSession,
            AesProtectedByteArray newWalletCertPass,
            AesProtectedByteArray newMasterWalletCertPass,
            WalletClientSettingsOnServer walletSettingsOnServer,
            CancellationToken cancellationToken
        )
        {
            var randomClientGuid = MiscFuncs.GenGuidWithFirstBytes(
                (byte)ECertGuidHashTypes.Scrypt8Mb
            );
            if(
                WalletServerClientConstants.GetWalletIdType(randomClientGuid) 
                != EWalletIdsTypeClient.Clients
            )
                throw new ArgumentOutOfRangeException(
                    MyNameof.GetLocalVarName(() => randomClientGuid));
            var walletServerInfo =
                await lookupSession.GetWalletServerAddress(
                    randomClientGuid,
                    cancellationToken
                ).ConfigureAwait(false);
            Assert.NotEqual(Guid.Empty, walletServerInfo.ServerGuid);
            var walletServerInterface =
                await EndReceiverServiceClientInterceptor
                    .GetClientProxy<IFromClientToWallet>(
                        proxySession.ProxyInterface,
                        walletServerInfo.ServerGuid,
                        cancellationToken,
                        walletServerInfo.EndReceiverMethodInfos
                    ).ConfigureAwait(false);
            return await RegisterWallet(
                walletServerInterface,
                newWalletCertPass,
                newMasterWalletCertPass,
                walletSettingsOnServer,
                cancellationToken,
                proxySession
            ).ConfigureAwait(false);
        }
        private readonly SemaphoreSlim _saveClientSettingsLockSem
            = new SemaphoreSlim(1);
        private async void SaveClientSettings()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await _saveClientSettingsLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        var settingsToSave = _walletSessionModel.ClientSettingsOnServer;
                        while (true)
                        {
                            try
                            {
                                await _authedWalletInterface.UpdateMySettings(
                                    settingsToSave
                                ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                break;
                            }
                            catch (TimeoutException)
                            {
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
                MiscFuncs.HandleUnexpectedError(exc,_log);
            }
        }

        public async Task<bool> IsWalletRegistered(Guid walletGuid, CancellationToken token)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                return await MiscFuncs.RepeatWhileTimeout(
                    async () => await _walletTransport
                        .IsWalletRegistered(walletGuid).ConfigureAwait(false),
                    token,
                    _cts.Token
                ).ConfigureAwait(false);
            }
        }

        public async Task<CheckSimpleTransferWasProcessedResponse> WasRequestProcessed(
            Guid requestGuid,
            CancellationToken token
            )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                return await MiscFuncs.RepeatWhileTimeout(
                    async () => await _authedWalletInterface.WasSimpleTransferRequestProcessed(
                        new CheckSimpleTransferWasProcessedRequest()
                        {
                            OrderRequestId = requestGuid
                        }
                    ).ConfigureAwait(false),
                    token,
                    _cts.Token
                ).ConfigureAwait(false);
            }
        }

        private readonly SemaphoreSlim _updateBalanceLockSem 
            = new SemaphoreSlim(1);
        public async void UpdateBalance()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await _updateBalanceLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (true)
                        {
                            try
                            {
                                var balance
                                    = await _authedWalletInterface.GetWalletBalance()
                                        .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                _walletSessionModel.Balance = balance;
                                break;
                            }
                            catch (TimeoutException)
                            {
                            }
                        }
                    }
                }
            }
            catch(OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "UpdateBalance unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }
        // . Authenticated
        private readonly ConcurrentDictionary<Guid,MutableTuple<LightCertificate,bool>> _otherWalletCertDict
            = new ConcurrentDictionary<Guid, MutableTuple<LightCertificate, bool>>(); 
        private async Task UpdateOtherWalletCertInfoPrivate(IList<Guid> certGuidList)
        {
            Assert.NotNull(certGuidList);
            var certToUpdate = certGuidList.Except(_otherWalletCertDict.Keys).Distinct().ToList();
            foreach (var certGuidChunk in certToUpdate.Batch(20).Select(_ => _.ToList()))
            {
                var newCerts = await MiscFuncs.RepeatWhileTimeout(
                    async () => await _authedWalletInterface.GetOtherWalletCert(
                        new GetWalletCertRequest()
                        {
                            WalletCertGuidList = certGuidChunk
                        }
                        ).ConfigureAwait(false),
                    _cts.Token
                    ).ConfigureAwait(false);
                foreach (var newCert in newCerts)
                {
                    var newCertWalletType = WalletServerClientConstants.GetWalletIdType(
                        newCert.Id
                    );
                    bool authenticatedOtherWalletCert = false;
                    var firstByteCount =
                        WalletServerClientConstants.WalletClientGuidFirstZeroBytesCount[newCertWalletType];
                    var newCertGuidBytes = newCert.Id.ToByteArray();
                    var expectedGuidHashTypeByte = newCertGuidBytes.Skip(firstByteCount).Take(1).Single();
                    if (expectedGuidHashTypeByte == (byte) ECertGuidHashTypes.Scrypt8Mb)
                    {
                        authenticatedOtherWalletCert = LightCertificatesHelper.CheckGuidHash(
                            newCert,
                            ECertGuidHashTypes.Scrypt8Mb,
                            16 - 1 - firstByteCount
                        );
                    }
                    _otherWalletCertDict.TryAdd(
                        newCert.Id,
                        MutableTuple.Create(
                            newCert,
                            authenticatedOtherWalletCert
                        )
                    );
                }
            }
        }
        /**/
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            _sessionSubscriptions.ForEach(x => x.Dispose());
            _sessionSubscriptions.Clear();
            await _authenticatedWalletTransport.MyDisposeAsync().ConfigureAwait(false);
            await _signedWalletTransport.MyDisposeAsync().ConfigureAwait(false);
            _walletCertPass.Dispose();
            _serverEvents.Dispose();
            _cts.Dispose();
        }
    }
}
