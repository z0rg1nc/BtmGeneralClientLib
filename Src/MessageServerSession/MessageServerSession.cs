using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.AuthenticatedTransport;
using BtmI2p.CdnProxyEndReceiverClientTransport;
using BtmI2p.GeneralClientInterfaces;
using BtmI2p.GeneralClientInterfaces.MessageServer;
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

namespace BtmI2p.BitMoneyClient.Lib.MessageServerSession
{
    public class PreparedToSendMessage
    {
        public Guid RequestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
        public string MessageText;
        public Guid UserTo;
        public bool EncryptMessage = true;
        public TimeSpan KeepForTs = TimeSpan.FromDays(1.0d);
        public decimal MaxMessageFee = 0.0m;
    }
    public class MessageServerSessionSettings
    {
        public LightCertificate UserCert = null;
        public TimeSpan MessageKeyLifetime = TimeSpan.FromDays(7.0f);
        /* Receiving message settings */
        public TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(
            ClientLifecycleEnvironment.LifeCycle == ELifeCycle.Dev
                ? 5
                : 180
        );
        public TimeSpan BufferTime = TimeSpan.FromSeconds(3.0f);
        public int BufferCount = 100;
        public TimeSpan InitLoadTimeFrame = TimeSpan.FromDays(30.0);
        /**/
        public Guid LastKnownSentMessageGuid = Guid.Empty;
        public Guid LastKnownReceivedMessageGuid = Guid.Empty;
        public int MessagesInitCounter = 0;
        /**/
        public bool SendIAmOnlineMessages = true;
        public TimeSpan PingContactInterval
            = TimeSpan.FromSeconds(
                ClientLifecycleEnvironment.LifeCycle == ELifeCycle.Dev
                    ? 10
                    : 90
            );
    }

    public class ClientTextMessage
    {
        public virtual Guid MessageGuid { get; set; }
        public virtual int MessageNum { get; set; }
        public virtual string MessageText { get; set; }
        public virtual DateTime SaveUntil { get; set; }
        public virtual DateTime SentTime { get; set; }
        public virtual Guid UserFrom { get; set; }
        public virtual Guid UserTo { get; set; }
        public virtual bool OutcomeMessage { get; set; } = false;
        public virtual bool OtherUserCertAuthenticated { get; set; } = false;
        public virtual bool MessageKeyAuthenticated { get; set; } = false;
        public virtual bool MessageAuthenticated { get; set; } = false;
    }

    public interface IMessageServerSessionModel : IMyNotifyPropertyChanged
    {
        Subject<List<ClientTextMessage>> IncomeMessageReceived { get; }
        Subject<List<ClientTextMessage>> OutcomeMessageSent { get; }
        Subject<Tuple<PreparedToSendMessage, string>> SendingMessageError { get; }
        Subject<object> NeedToUpdateContactInfos { get; }
        Queue<PreparedToSendMessage> OfflineMessages { get; set; }
        List<Guid> OnlineContactGuids { get; set; }
        decimal Balance { get; set; }
    }

    public class MessageServerSessionModel : IMessageServerSessionModel
    {
        public MessageServerSessionModel()
        {
            IncomeMessageReceived = new Subject<List<ClientTextMessage>>();
            OutcomeMessageSent = new Subject<List<ClientTextMessage>>();
            SendingMessageError = new Subject<Tuple<PreparedToSendMessage, string>>();
            NeedToUpdateContactInfos = new Subject<object>();
            OfflineMessages = new Queue<PreparedToSendMessage>();
            OnlineContactGuids = new List<Guid>();
            Balance = 0.0m;
        }
        public Subject<List<ClientTextMessage>> IncomeMessageReceived { get; private set; }
        public Subject<List<ClientTextMessage>> OutcomeMessageSent { get; private set; }
        public Subject<Tuple<PreparedToSendMessage, string>> SendingMessageError { get; private set; }
        public Subject<object> NeedToUpdateContactInfos { get; private set; }
        public Queue<PreparedToSendMessage> OfflineMessages { get; set; }
        public List<Guid> OnlineContactGuids { get; set; }
        public decimal Balance { get; set; }
        /**/
        public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
        {
            get
            {
                throw new Exception(MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString);
            }
        }
    }
    public partial class MessageServerSession : IMyAsyncDisposable
    {
        public const decimal MaxFeeSurplus = 0.01m;
        private AesProtectedByteArray _userCertPass;
        private MessageServerSessionSettings _settings;
        private IMessageServerSessionModel _sessionModel;
        private readonly List<IDisposable> _subscriptions 
            = new List<IDisposable>();
        private IFromClientToMessage _messageTransport;
        private IGetRelativeTime _getTimeInterface;
        // . Right guid hash
        private readonly ConcurrentDictionary<Guid, MutableTuple<LightCertificate,bool>> _otherUserCertificateDict
            = new ConcurrentDictionary<Guid, MutableTuple<LightCertificate,bool>>();

        private async Task UpdateOtherUserCertInfo(IList<Guid> userGuidList)
        {
            Assert.NotNull(userGuidList);
            var usersToUpdateCertInfo = userGuidList.Distinct().Except(_otherUserCertificateDict.Keys).ToList();
            if (!usersToUpdateCertInfo.Any())
                return;
            foreach (var chunk in usersToUpdateCertInfo.Batch(20).Select(_ => _.ToList()))
            {
                if(!chunk.Any())
                    break;
                var newCerts = await MiscFuncs.RepeatWhileTimeout(
                    async () => await _authedMessageInterface.GetOtherUserPubCertList(
                        new GetOtherUserPubCertCommandRequest()
                        {
                            OtherUserGuidList = chunk.ToList()
                        }
                        ).ConfigureAwait(false),
                    _cts.Token
                ).ConfigureAwait(false);
                foreach (var newCert in newCerts)
                {
                    Assert.NotNull(newCert);
                    newCert.CheckMe();
                    Assert.Equal(
                        MessageServerClientConstants.GetMessageClientGuidType(
                            newCert.Id
                        ),
                        MessageClientGuidType.Clients
                    );
                    var userTo = newCert.Id;
                    bool userToCertAuthenticated;
                    var userToGuidBytes = userTo.ToByteArray();
                    if (userToGuidBytes[0] == (sbyte)(ECertGuidHashTypes.None))
                    {
                        userToCertAuthenticated = false;
                    }
                    else if (userToGuidBytes[0] == (sbyte)ECertGuidHashTypes.Scrypt8Mb)
                    {
                        userToCertAuthenticated = LightCertificatesHelper.CheckGuidHash(
                            newCert,
                            ECertGuidHashTypes.Scrypt8Mb,
                            15
                        );
                    }
                    else
                    {
                        userToCertAuthenticated = false;
                    }
                    _otherUserCertificateDict.TryAdd(
                        userTo,
                        MutableTuple.Create(
                            newCert,
                            userToCertAuthenticated
                        )
                    );
                }
            }
        }

        /**/
        private ClientOneSideSignedTransport<ISignedFromClientToMessage> 
            _signedUserTransport;
        private ISignedFromClientToMessage 
            _signedMessageTransportInterface;
        /**/
        private ClientAuthenticatedTransport<IAuthenticatedFromClientToMessage> 
            _authenticatedUserTransport;
        private IAuthenticatedFromClientToMessage 
            _authedMessageInterface;
        /**/
        public async Task GetPermissionWriteToUser(
            Guid userGuid,
            Guid permissionGuid,
            CancellationToken token
            )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        await _authedMessageInterface.GrantMeWriteToUser(userGuid,permissionGuid)
                            .ThrowIfCancelled(token)
                            .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        public async Task RevokeUserPermissionWriteToMe(
            Guid userGuid,
            CancellationToken token
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        await _authedMessageInterface.RevokeUserPermissionWriteToMe(userGuid)
                            .ThrowIfCancelled(token)
                            .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }
        public async Task GrantUserPermissionWriteToMe(
            Guid userGuid,
            CancellationToken token
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        await _authedMessageInterface.GrantUserWriteToMe(userGuid)
                            .ThrowIfCancelled(token)
                            .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        /**/
        public async Task SetMySettingsOnServer(
            MessageClientSettingsOnServerClientInfo settingsOnServer,
            CancellationToken token
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        await _authedMessageInterface.SaveMySettings(settingsOnServer)
                            .ThrowIfCancelled(token)
                            .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        public async Task<MessageClientSettingsOnServerClientInfo> GetMySettingsOnServer(
            CancellationToken token
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        return await _authedMessageInterface.GetMySettings()
                            .ThrowIfCancelled(token)
                            .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        /**/
        private List<Guid> _contactsList = new List<Guid>(); 
        private readonly SemaphoreSlimSet _lockSemSet = new SemaphoreSlimSet();
        public async Task SetContactsList(
            List<Guid> contactGuids
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                if(contactGuids == null)
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => contactGuids)
                    );
                using (
                    await _lockSemSet.GetDisposable(
                        this.MyNameOfProperty(e => e._contactsList)
                    ).ConfigureAwait(false)
                )
                {
                    _contactsList = contactGuids;
                }
            }
        }

        private async Task<List<Guid>> GetContactsList(
            )
        {
            using (
                    await _lockSemSet.GetDisposable(
                        this.MyNameOfProperty(e => e._contactsList)
                    ).ConfigureAwait(false)
                )
            {
                return _contactsList.ToList();
            }
        }

        private MessageServerInfoForClient _serverInfoForClient;
        private DateTime _initTime;
        public static async Task<MessageServerSession> CreateInstance(
            MessageServerSessionSettings settings,
            IMessageServerSessionModel sessionModel,
            IProxyServerSession proxySession,
            AesProtectedByteArray userCertPass,
            CancellationToken cancellationToken
        )
        {
            var result = new MessageServerSession();
            result._initTime = await proxySession.GetNowTime().ConfigureAwait(false);
            result._getTimeInterface = proxySession;
            result._userCertPass = userCertPass;
            result._settings = settings;
            result._sessionModel = sessionModel;
            /**/
            var lookupSession = 
                await proxySession.GetLookupServerSession(
                    cancellationToken
                ).ConfigureAwait(false);
            ServerAddressForClientInfo userServerInfo  =
                await lookupSession.GetUserServerAddress(
                    settings.UserCert.Id,
                    cancellationToken
                ).ConfigureAwait(false);
            /**/
            if(
                userServerInfo == null 
                || userServerInfo.ServerGuid == Guid.Empty
            )
                throw new ArgumentNullException();
            /**/
            result._messageTransport =
                await EndReceiverServiceClientInterceptor
                    .GetClientProxy<IFromClientToMessage>(
                        proxySession.ProxyInterface,
                        userServerInfo.ServerGuid,
                        cancellationToken,
                        userServerInfo.EndReceiverMethodInfos
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
                                    await result._messageTransport
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
                                    await result._messageTransport
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
            
            while (true)
            {
                try
                {
                    if (
                        !await result._messageTransport.IsUserCertRegistered(
                            settings.UserCert.Id
                        ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false)
                    )
                        throw new Exception("User not registered");
                    break;
                }
                catch (TimeoutException)
                {
                }
            }
            /**/
            result._signedUserTransport =
                await ClientOneSideSignedTransport<ISignedFromClientToMessage>
                    .CreateInstance(
                        new ServerOneSideSignedFuncsForClient()
                        {
                            GetMethodInfos = async () =>
                                (await result._messageTransport.GetSignedMethodInfos().ConfigureAwait(false))
                                    .Select(x => x.JsonRpcMethodInfo)
                                    .ToList(),
                            ProcessSignedRequestPacket =
                                result._messageTransport.ProcessSignedRequestPacket,
                            GetNowTimeFunc = proxySession.GetNowTime
                        },
                        settings.UserCert,
                        userCertPass,
                        cancellationToken,
                        await getSignedMethodInfosTask.ConfigureAwait(false)
                    ).ConfigureAwait(false);
            try
            {
                result._signedMessageTransportInterface =
                    await result._signedUserTransport.GetClientProxy().ConfigureAwait(false);
                /**/
                result._authenticatedUserTransport =
                    await ClientAuthenticatedTransport<IAuthenticatedFromClientToMessage>
                        .CreateInstance(
                            new ClientAuthenticatedTransportSettings(),
                            new ServerAuthenticatedFuncsForClient()
                            {
                                AuthMe = result._messageTransport.AuthMe,
                                GetAuthData = result._messageTransport.GetAuthData,
                                GetMethodInfos =
                                    async () =>
                                        (
                                            await result._messageTransport
                                                .GetAuthenticatedMethodInfos().ConfigureAwait(false)
                                            )
                                            .Select(x => x.JsonRpcMethodInfo)
                                            .ToList(),
                                ProcessRequest =
                                    result
                                        ._messageTransport
                                        .ProcessAuthenticatedPacket,
                                DecryptAuthDataFunc = async dataToDecrypt =>
                                {
                                    using (var tempPass = userCertPass.TempData)
                                    {
                                        return await Task.FromResult(
											settings.UserCert.DecryptData(
												dataToDecrypt, tempPass.Data
                                            )
										).ConfigureAwait(false);
                                    }
                                }
                            },
                            settings.UserCert.Id,
                            cancellationToken,
                            await getAuthenticatedMethodInfosTask.ConfigureAwait(false)
                        ).ConfigureAwait(false);
                try
                {
                    result._authedMessageInterface =
                        await result._authenticatedUserTransport.GetClientProxy().ConfigureAwait(false);
                    var messageServerInfoForClientTask = await Task.Factory.StartNew(
                        async () =>
                        {
                            while (true)
                            {
                                try
                                {
                                    return await result._authedMessageInterface
                                        .GetServerInfo()
                                        .ThrowIfCancelled(cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                catch (TimeoutException)
                                {
                                }
                            }
                        }
                        ).ConfigureAwait(false);
                    result._serverInfoForClient = await messageServerInfoForClientTask.ConfigureAwait(false);
                    result._stateHelper.SetInitializedState();
                }
				catch(Exception)
                {
                    await result._authenticatedUserTransport.MyDisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
			catch(Exception)
            {
                await result._signedUserTransport.MyDisposeAsync().ConfigureAwait(false);
                throw;
            }
            /* Init subscriptions */
            result._subscriptions.Add(
                        Observable.Timer(
                            TimeSpan.Zero,
                            settings.PingContactInterval
                            ).ObserveOn(TaskPoolScheduler.Default).Subscribe(
                                async i => await result.PingContactIntervalAction().ConfigureAwait(false)
                            )
                        );
            result._subscriptions.Add(
                result.ServerEvents
                    .Where(_ => _.EventType == EMessageClientEventTypes.NewSentMessage)
                    .BufferNotEmpty(TimeSpan.FromSeconds(0.3))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => result.ProcessSentMessages())
            );
            result._subscriptions.Add(
                result.ServerEvents
                    .Where(_ => _.EventType == EMessageClientEventTypes.NewReceivedMessage)
                    .BufferNotEmpty(TimeSpan.FromSeconds(0.3))
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(_ => result.ProcessIncomeMessages())
            );
            /**/
            result.FeedServerEventsAction();
            /**/
            result._sessionModel.OnlineContactGuids =
                new List<Guid>();
            /**/
            result.UpdateBalance();
            result.ProcessSentMessages();
            result.ProcessOfflineMessagesQueue();
            result.ProcessIncomeMessages();
            return result;
        }

        public async Task<BitmoneyInvoiceData> IssueInvoiceTo
            (long transferAmount)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                return await Task.FromResult(new BitmoneyInvoiceData()
                {
                    ForceAnonymousTransfer = false,
                    TransferAmount = transferAmount,
                    CommentBytes = Encoding.UTF8.GetBytes(
                        $"{_settings.UserCert.Id}"
                        ),
                    WalletTo = _serverInfoForClient.WalletGuid
                }).ConfigureAwait(false);
            }
        }
        // (settings on server, can i write to, can he writes to me)
        public async Task<Dictionary<Guid, Tuple<MessageClientSettingsOnServerClientInfo, bool, bool>>> 
            GetContactUserInfosOnServer(
                List<Guid> contactUserGuids,
                CancellationToken token
            )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                if(contactUserGuids == null)
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => contactUserGuids)
                    );
                contactUserGuids = contactUserGuids.Distinct().ToList();
                var getUserSettingsOnServerTask = await Task.Factory.StartNew(
                    async () =>
                    {
                        while (true)
                        {
                            try
                            {
                                return await _authedMessageInterface
                                    .GetOtherUserSettings(
                                        contactUserGuids
                                    ).ThrowIfCancelled(token)
                                    .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            }
                            catch (TimeoutException)
                            {
                            }
                        }
                    }
                ).ConfigureAwait(false);
                var canIwriteToUserListTask = await Task.Factory.StartNew(
                    async () =>
                    {
                        while (true)
                        {
                            try
                            {
                                return await _authedMessageInterface
                                    .GetIGrantedWriteToUserList(
                                        contactUserGuids
                                    ).ThrowIfCancelled(token)
                                    .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            }
                            catch (TimeoutException)
                            {
                            }
                        }
                    }
                ).ConfigureAwait(false);
                var canUsersWriteToMeListTask = await Task.Factory.StartNew(
                    async () =>
                    {
                        while (true)
                        {
                            try
                            {
                                return await _authedMessageInterface
                                    .GetGrantedWriteToMeUserList(
                                        contactUserGuids
                                    ).ThrowIfCancelled(token)
                                    .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            }
                            catch (TimeoutException)
                            {
                            }
                        }
                    }
                ).ConfigureAwait(false);
                var settingsOnServer = await getUserSettingsOnServerTask.ConfigureAwait(false);
                var canIwriteToUserList = await canIwriteToUserListTask.ConfigureAwait(false);
                var canUsersWriteToMeList = await canUsersWriteToMeListTask.ConfigureAwait(false);
                var result = new Dictionary<Guid, Tuple<MessageClientSettingsOnServerClientInfo, bool, bool>>();
                for (int i = 0; i < contactUserGuids.Count; i++)
                {
                    result.Add(contactUserGuids[i],
                        new Tuple<MessageClientSettingsOnServerClientInfo, bool, bool>(
                            settingsOnServer[i],
                            canIwriteToUserList.Contains(contactUserGuids[i]),
                            canUsersWriteToMeList.Contains(contactUserGuids[i])
                        )
                    );
                }
                return result;
            }
        }
        private readonly SemaphoreSlim _updateBalanceLockSem = new SemaphoreSlim(1);
        public async void UpdateBalance()
        {
            var curMethodName = this.MyNameOfMethod(e => e.UpdateBalance());
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
                                _sessionModel.Balance
                                    = await _authedMessageInterface.GetBalance()
                                        .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                break;
                            }
                            catch (TimeoutException)
                            {
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
                MiscFuncs.HandleUnexpectedError(exc, _log);
            }
        }

        private readonly SemaphoreSlim _pingContactActionLockSem 
            = new SemaphoreSlim(1);
        private async Task PingContactIntervalAction()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (
                        await _pingContactActionLockSem.GetDisposable(true).ConfigureAwait(false)
                        )
                    {
                        if (_settings.SendIAmOnlineMessages)
                        {
                            try
                            {
                                await _authedMessageInterface
                                    .AmOnline().ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            }
                            catch (TimeoutException)
                            {
                            }
                        }
                        var contactList = await GetContactsList().ConfigureAwait(false);
                        try
                        {
                            List<Guid> onlineContactList 
                                = await _authedMessageInterface.GetOnlineUsers(
                                    contactList
                                ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            _sessionModel.OnlineContactGuids =
                                onlineContactList;
                        }
                        catch (TimeoutException)
                        {
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
                MiscFuncs.HandleUnexpectedError(exc, _log);
            }
        }

        private static readonly Logger _log 
            = LogManager.GetCurrentClassLogger();

        public class RegisterUserResult
        {
            public Guid UserGuid;
            public LightCertificate UserCert;
            public LightCertificate MasterUserCert;
        }
        public static async Task<RegisterUserResult> RegisterUser(
            IProxyServerSession proxySession,
            LookupServerSession lookupSession,
            AesProtectedByteArray newUserCertPass,
            AesProtectedByteArray newMasterUserCertPass,
            CancellationToken cancellationToken
        )
        {
            var randomNewUserGuid = MiscFuncs.GenGuidWithFirstBytes((byte)ECertGuidHashTypes.Scrypt8Mb);
            if(
                MessageServerClientConstants.GetMessageClientGuidType(randomNewUserGuid) 
                != MessageClientGuidType.Clients
            )
                throw new ArgumentOutOfRangeException(
                    MyNameof.GetLocalVarName(() => randomNewUserGuid));
            var userServerInfo =
                await lookupSession.GetUserServerAddress(
                    randomNewUserGuid,
                    cancellationToken
                ).ConfigureAwait(false);
            var userServerInterface =
                await EndReceiverServiceClientInterceptor.GetClientProxy<IFromClientToMessage>(
                    proxySession.ProxyInterface,
                    userServerInfo.ServerGuid,
                    cancellationToken,
                    userServerInfo.EndReceiverMethodInfos
                ).ConfigureAwait(false);
            var newMasterUserCertId = MiscFuncs.GenGuidWithFirstBytes(0);
            LightCertificate newMasterUserCert;
            using (var tempData = newMasterUserCertPass.TempData)
            {
                var masterUserCertPasswordBytes = tempData.Data;
                newMasterUserCert =
                    LightCertificatesHelper.GenerateSelfSignedCertificate(
                        ELightCertificateSignType.Rsa,
                        2048,
                        ELightCertificateEncryptType.Rsa,
                        2048,
                        EPrivateKeysKeyDerivationFunction.ScryptDefault,
                        newMasterUserCertId,
                        $"{newMasterUserCertId}",
                        masterUserCertPasswordBytes
                    );
            }
            LightCertificate newUserCert;
            using (var tempData = newUserCertPass.TempData)
            {
                var userCertPasswordBytes = tempData.Data;
                newUserCert =
                    LightCertificatesHelper.GenerateSelfSignedCertificate(
                        ELightCertificateSignType.Rsa,
                        2048,
                        ELightCertificateEncryptType.Rsa,
                        2048,
                        EPrivateKeysKeyDerivationFunction.ScryptDefault,
                        new byte[0],
                        ECertGuidHashTypes.Scrypt8Mb, 
                        null,
                        userCertPasswordBytes
                    );
                LightCertificatesHelper.SignCertificate(
                    newMasterUserCert,
                    newUserCert,
                    userCertPasswordBytes
                );
            }
            Assert.True(await MiscFuncs.RepeatWhileTimeout(
                async () => await userServerInterface.IsUserGuidValidForRegistration(
                    newUserCert.Id).ConfigureAwait(false),
                cancellationToken
            ));
            var requestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
            while (true)
            {
                var request = new RegisterUserRequest()
                {
                    PublicUserCert = newUserCert.GetOnlyPublic(),
                    PublicMasterUserCert = newMasterUserCert.GetOnlyPublic(),
                    ClientSettingsOnServer = new MessageClientSettingsOnServerClientInfo(),
                    SentTime = await proxySession.GetNowTime().ConfigureAwait(false),
                    RequestGuid = requestGuid
                };
                SignedData<RegisterUserRequest> signedRequest;
                using (var tempPass = newUserCertPass.TempData)
                {
                    signedRequest = new SignedData<RegisterUserRequest>(
                        request,
                        newUserCert,
                        tempPass.Data
                    );
                }
                try
                {
                    await userServerInterface.RegisterMessageClient(
                        signedRequest
                    ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (RpcRethrowableException rpcRethrowableExc)
                {
                    if (
                        rpcRethrowableExc.ErrorData.ErrorCode
                        == (int) ERegisterMessageClientErrCodes.RegisteredAlready
                    )
                        break;
                    throw;
                }
                catch (TimeoutException)
                {
                }
            }
            return new RegisterUserResult()
            {
                UserGuid = newUserCert.Id,
                UserCert = newUserCert,
                MasterUserCert = newMasterUserCert
            };
        }

        private MessageServerSession()
        {
        }

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("MessageServerSession");
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            foreach (IDisposable subscription in _subscriptions)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            await _authenticatedUserTransport.MyDisposeAsync().ConfigureAwait(false);
            await _signedUserTransport.MyDisposeAsync().ConfigureAwait(false);
            _userCertPass.Dispose();
            /**/
            _sessionModel.Balance = 0.0m;
            _serverEvents.Dispose();
            _cts.Dispose();
        }
    }
}
