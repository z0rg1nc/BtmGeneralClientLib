using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.ComputableTaskInterfaces.Client;
using BtmI2p.GeneralClientInterfaces;
using BtmI2p.GeneralClientInterfaces.CdnProxyServer;
using BtmI2p.GeneralClientInterfaces.WalletServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.JsonRpcSamI2pClient;
using BtmI2p.MiscUtils;
using BtmI2p.MyBinDiff.Lib;
using BtmI2p.MyFileManager;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.ObjectStateLib;
using BtmI2p.SamHelper;
using NLog;

namespace BtmI2p.BitMoneyClient.Lib
{
    public interface IProxyServerSession : IGetRelativeTime
    {
        Task<LookupServerSession> GetLookupServerSession(
            CancellationToken cancellationToken
        );
        IFromClientToCdnProxy ProxyInterface { get; }
        void ForcePing();
        Task<BitmoneyInvoiceData> IssueInvoiceToFillup(
            long transferAmount
        );

        Task<Tuple<Version, byte[]>> GetNewVersionArchive(
            CancellationToken token,
            bool diffsOnly,
            Version oldVersion,
            byte[] oldVersionArchiveData,
            Subject<DownloadFileProgressInfo> progressSubject
        );

        Task<Version> GetClientVersionFromServer(
            CancellationToken token
        );

        void UpdateBalance();
    }

    public interface IProxyServerSessionModel : IMyNotifyPropertyChanged
    {
        BitmoneyInvoiceData IssueInvoiceToFillup(long transferAmount);
        string I2PDestination { get; set; }
        decimal Balance { get; set; }
        bool TaskComputing { get; set; }
        bool ProxyServerStatus { get; set; }
        ProxyServerInfo ProxyServerInfo { get; set; }
        TimeSpan ClientServerTimeDifference { get; set; }
        bool NewVersionAvailable { get; set; }
    }

    public class ProxyServerSessionModel : IProxyServerSessionModel
    {
        public ProxyServerSessionModel()
        {
            Balance = 0.0m;
            ClientServerTimeDifference = TimeSpan.Zero;
            I2PDestination = string.Empty;
            NewVersionAvailable = false;
            ProxyServerInfo = null;
            ProxyServerStatus = false;
            TaskComputing = false;
        }

        public BitmoneyInvoiceData IssueInvoiceToFillup(long transferAmount)
        {
            return new BitmoneyInvoiceData()
            {
                ForceAnonymousTransfer = false,
                TransferAmount = transferAmount,
                CommentBytes = Encoding.UTF8.GetBytes(
                    I2PDestination
                ),
                WalletTo = ProxyServerInfo.WalletGuid
            };
        }

        public string I2PDestination { get; set; }
        public decimal Balance { get; set; }
        public bool TaskComputing { get; set; }
        public bool ProxyServerStatus { get; set; }
        public ProxyServerInfo ProxyServerInfo { get; set; }
        public TimeSpan ClientServerTimeDifference { get; set; }
        public bool NewVersionAvailable { get; set; }
        /**/
        public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
        {
            get
            {
                throw new Exception(MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString);
            }
        }
    }
    
    public interface IProxyServerSessionSettings : ICheckable
    {
        string SamServerAddress { get; set; }
        int SamServerPort { get; set; }
        string ClientI2PPrivateKeys { get; set; }
        TimeSpan PingProxyInterval { get; set; }
        List<ProxyServerClientInfo> ProxyServerClientInfos { get; set; }
        bool AutoFillup { get; set; }
        decimal AutoFillupMinBalance { get; set; }
    }

    public class ProxyServerSessionSettings : IProxyServerSessionSettings
    {
        public ProxyServerSessionSettings()
        {
            PingProxyInterval = TimeSpan.FromSeconds(
                ClientLifecycleEnvironment.LifeCycle == ELifeCycle.Dev
                    ? 5
                    : 30
            );
        }
        public string SamServerAddress { get; set; }
        public int SamServerPort { get; set; }
        public string ClientI2PPrivateKeys { get; set; }
        public TimeSpan PingProxyInterval { get; set; }
        public List<ProxyServerClientInfo> ProxyServerClientInfos { get; set; }
        public bool AutoFillup { get; set; }
        public decimal AutoFillupMinBalance { get; set; }
        public void CheckMe()
        {
            
        }
    }
    /**/
    public class ProxyServerSessionOverI2P
        : IProxyServerSession, IMyAsyncDisposable
    {
        private ProxyServerSessionOverI2P(
            MiningTaskManager miningManager
        )
        {
            _miningManager = miningManager;
        }
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("ProxyServerSessionOverI2P");

        private TimeSpan _serverClientDateTimeDifference 
            = TimeSpan.Zero;
        private readonly SemaphoreSlim _serverClientDateTimeDifferenceLockSem
            = new SemaphoreSlim(1);

        public async Task<DateTime> GetRelativeTime(DateTime time)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _serverClientDateTimeDifferenceLockSem.GetDisposable().ConfigureAwait(false))
                {
                    return time + _serverClientDateTimeDifference;
                }
            }
        }
        public async Task<DateTime> GetNowTime()
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _serverClientDateTimeDifferenceLockSem.GetDisposable().ConfigureAwait(false))
                {
                    return DateTime.UtcNow + _serverClientDateTimeDifference;
                }
            }
        }

        private readonly MiningTaskManager _miningManager;
        private IProxyServerSessionModel _proxySessionModel;
        private IProxyServerSessionSettings _settings;
        private ProxyServerClientInfo _commonInfoAboutServer;
        public static async Task<ProxyServerSessionOverI2P> CreateInstance(
            MiningTaskManager miningManager,
            IProxyServerSessionSettings settings,
            IProxyServerSessionModel proxySessionModel,
            CancellationToken cancellationToken
        )
        {
            var result = new ProxyServerSessionOverI2P(
                miningManager
            );
            result._settings = settings;
            result._proxySessionModel = proxySessionModel;
            /***************/
            if (
                string.IsNullOrWhiteSpace(settings.ClientI2PPrivateKeys)
            )
            {
                var samHelper = await SamHelper.SamHelper.CreateInstance(
                    new SamHelperSettings()
                    {
                        SamServerAddress = settings.SamServerAddress,
                        SamServerPort = settings.SamServerPort,
                        SessionPrivateKeys = null
                    }, 
                    cancellationToken
                ).ConfigureAwait(false);
                settings.ClientI2PPrivateKeys
                    = samHelper.Session.PrivateKey;
                await samHelper.MyDisposeAsync().ConfigureAwait(false);
            }
            var clientI2PDestination = 
                new I2PPrivateKey(settings.ClientI2PPrivateKeys).Destination.ToI2PBase64();
            proxySessionModel.I2PDestination = clientI2PDestination;
            var myProxyList = settings.ProxyServerClientInfos
                .Where(x => x.DestinationRestictions.Any(
                    y => CheckProxyRestriction(clientI2PDestination, y)
                    )).ToList();
            if (myProxyList.Count == 0)
                throw new Exception("No proxy for client i2p destination");
            var myProxy = myProxyList.OrderBy(x => _rng.Next()).First();
            if(myProxy.I2PDestinations.Count == 0)
                throw new ArgumentOutOfRangeException(
                    MyNameof.GetLocalVarName(() => myProxy.I2PDestinations)
                );
            result._commonInfoAboutServer = myProxy;
            try
            {
                const bool useReconnectingSamHelper = true;
                result._proxyClientSamInfo =
                    await SamClientInfo<IFromClientToCdnProxy>.CreateInstance(
                        settings.SamServerAddress,
                        settings.SamServerPort,
                        myProxy.I2PDestinations.OrderBy(x => Guid.NewGuid()).First(),
                        cancellationToken,
                        new JsonSamClientSettings() { CompressData = true },
                        settings.ClientI2PPrivateKeys,
                        ProxyNotEnoughFundsException
                            .ProcessRpcRethrowableException,
                        useReconnectingSamHelper,
                        "outbound.priority=20 inbound.backupQuantity=2 outbound.backupQuantity=2"
                    ).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                _log.Error(
                    "CreateInstance SamClientInfo<IFromClientToCdnProxy>" +
                    ".CreateInstance '{0}'",
                    exc.ToString()
                );
                throw;
            }
            _log.Trace(
                "CreateInstance result._proxyClientSamInfo" +
                " created"
            );
            try
            {
                while (true)
                {
                    try
                    {
                        result._proxyServerInfoFromServer =
                            await result._proxyClientSamInfo.JsonClient
                                .GetProxyServerInfo()
                                .ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
			catch(Exception)
            {
                await result._proxyClientSamInfo.MyDisposeAsync().ConfigureAwait(false);
                throw;
            }
            _log.Trace(
                "CreateInstance result._proxyServerInfoFromServer" +
                " get"
            );
            proxySessionModel.ProxyServerInfo = 
                result._proxyServerInfoFromServer;
            result._subscriptions.Add(
                result._proxyClientSamInfo.SamInterceptor.ServiceStatus.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                    x =>
                    {
                        proxySessionModel.ProxyServerStatus = x;
                    }
                )
            );
            result._subscriptions.Add( 
                proxySessionModel.PropertyChangedSubject.Where(
                    x => 
                        x.PropertyName 
                            == proxySessionModel.MyNameOfProperty(
                                e => e.Balance
                            )
                        && settings.AutoFillup
                        && proxySessionModel.Balance
                            < settings.AutoFillupMinBalance
                ).Throttle(TimeSpan.FromSeconds(1.0d)).ObserveOn(TaskPoolScheduler.Default).Subscribe(
                    x => result.AutoProxyAccountFillUpAction()
                )
            );
            result._subscriptions.Add(
                Observable
                    .Interval(settings.PingProxyInterval)
                    .ObserveOn(TaskPoolScheduler.Default).Subscribe(result.ProxyServerPingAction)
            );
            /***************/
            result._stateHelper.SetInitializedState();
            result.ProxyServerPingAction(0);
            _log.Trace("CreateInstance success");
            return await Task.FromResult(result).ConfigureAwait(false);
        }
        private readonly SemaphoreSlim _getLookupServerSessionLockSem
            = new SemaphoreSlim(1);
        public async Task<LookupServerSession> GetLookupServerSession(
            CancellationToken cancellationToken
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _getLookupServerSessionLockSem.GetDisposable().ConfigureAwait(false))
                {
                    return
                        _lookupServerSession
                        ??
                        (
                            _lookupServerSession =
                                await LookupServerSession.CreateInstance(
                                    this,
                                    cancellationToken
                                ).ConfigureAwait(false)
                        );
                }
            }
        }

        private LookupServerSession _lookupServerSession = null;
        private ProxyServerInfo _proxyServerInfoFromServer;
        private readonly List<IDisposable> _subscriptions 
            = new List<IDisposable>();
        private SamClientInfo<IFromClientToCdnProxy> _proxyClientSamInfo = null;
        private static readonly Random _rng = new Random();

        public IFromClientToCdnProxy ProxyInterface
        {
            get
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    return _proxyClientSamInfo.JsonClient;
                }
            }
        }

        private static bool CheckProxyRestriction(
            string i2PDestination,
            I2PHostClientDestinations restriction
            )
        {
            byte[] hash;
            using (var sha256 = new SHA256Managed())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(i2PDestination));
            }
            for (int i = 0; i < restriction.ClientDestinationMask.Length; i++)
            {
                if (
                    (hash[i] & restriction.ClientDestinationMask[i])
                    !=
                    (
                        restriction.ClientDestinationMaskEqual[i]
                        & restriction.ClientDestinationMask[i]
                        )
                    )
                    return false;
            }
            return true;
        }
        private readonly SemaphoreSlim _updateServerClientDateTimeDifferenceLockSem
            = new SemaphoreSlim(1);

        

        private readonly SemaphoreSlim _proxyServerPingActionLockSem 
            = new SemaphoreSlim(1);

        public void ForcePing()
        {
            using (_stateHelper.GetFuncWrapper())
            {
                ProxyServerPingAction(0);
            }
        }
        private int _setServerClientTimeDifferenceCount = 0;
        private async void ProxyServerPingAction(long i)
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (
                        await _proxyServerPingActionLockSem.GetDisposable(true).ConfigureAwait(false)
                    )
                    {
                        try
                        {
                            var sw = new Stopwatch();
                            sw.Start();
                            PingResponse pingResponse;
                            try
                            {
                                pingResponse = await _proxyClientSamInfo.JsonClient.Ping(
                                    new PingRequest()
                                    {
                                        ClientVersion
                                            = CommonClientConstants.CurrentClientVersionString
                                    }
                                ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                sw.Stop();
                            }
                            if (pingResponse.NewVersionAvailable)
                            {
                                _proxySessionModel.NewVersionAvailable = true;
                            }
                            _proxySessionModel.Balance = pingResponse.Balance;
                            if (sw.ElapsedMilliseconds < 10000)
                            {
                                DateTime serverTime = pingResponse.NowTimeUtc; 
                                var nowTime = DateTime.UtcNow;
                                var serverNowTime
                                    = serverTime
                                        + TimeSpan.FromMilliseconds(
                                            sw.ElapsedMilliseconds / 2.0
                                        );
                                var timeDiff = serverNowTime - nowTime;
                                using (
                                    await _serverClientDateTimeDifferenceLockSem
                                        .GetDisposable().ConfigureAwait(false)
                                )
                                {
                                    var oldTimeDiffMs
                                        = _serverClientDateTimeDifference.TotalMilliseconds;
                                    var newTimeDiffMs = timeDiff.TotalMilliseconds;
                                    oldTimeDiffMs =
                                        (
                                            oldTimeDiffMs * _setServerClientTimeDifferenceCount
                                            + newTimeDiffMs
                                        ) / (_setServerClientTimeDifferenceCount + 1);
                                    _serverClientDateTimeDifference = TimeSpan.FromMilliseconds(
                                        oldTimeDiffMs
                                    );
                                    _proxySessionModel.ClientServerTimeDifference
                                        = _serverClientDateTimeDifference;
                                }
                                if (_setServerClientTimeDifferenceCount < 10)
                                    _setServerClientTimeDifferenceCount++;
                            }
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
                _log.Error(
                    string.Format(
                        "ProxyServerPingAction unexpected error '{0}'",
                        exc.ToString()
                        )
                    );
            }
        }

        public async Task<BitmoneyInvoiceData> IssueInvoiceToFillup(
            long transferAmount
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                return await Task.FromResult(
                    _proxySessionModel.IssueInvoiceToFillup(
                        transferAmount
                    )
                );
            }
        }
        
        public async Task<Tuple<Version, byte[]>> GetNewVersionArchive(
            CancellationToken token,
            bool diffsOnly,
            Version oldVersion,
            byte[] oldVersionArchiveData,
            Subject<DownloadFileProgressInfo> progressSubject
        )
        {
            var curMethodName = nameof(GetNewVersionArchive);
            using (_stateHelper.GetFuncWrapper())
            {
                if(progressSubject == null)
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => progressSubject)
                    );
                if(diffsOnly && oldVersionArchiveData == null)
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => oldVersionArchiveData));
                var oldDataHash = new byte[32];
                if(diffsOnly)
                    using (var hashAlg = new SHA256Managed())
                    {
                        oldDataHash = hashAlg.ComputeHash(oldVersionArchiveData);
                    }
                while (true)
                {
                    try
                    {
                        var archiveData = await _proxyClientSamInfo.JsonClient
                            .GetUpdateClientPackageInfo(
                                new GetUpdateClientPackageInfoRequest(){
                                    DiffsOnly = diffsOnly,
                                    OldVersionString = CommonClientConstants.CurrentClientVersionString,
                                    OldVersionArchiveDataHash = oldDataHash
                                }
                            ).ThrowIfCancelled(token).ConfigureAwait(false);
                        if (ClientLifecycleEnvironment.LifeCycle != ELifeCycle.Dev)
                        {
                            if (
                                !CommonClientConstants.UpdatesSignatureCertificate.VerifyData(
                                    archiveData.SignedIdentity
                                    )
                                )
                            {
                                throw new Exception(
                                    "Wrong new version archive signature"
                                    );
                            }
                        }
                        var identity =
                            ClientLifecycleEnvironment.LifeCycle != ELifeCycle.Dev
                                ? archiveData.SignedIdentity.GetValue(
                                    CommonClientConstants.UpdatesSignatureCertificate
                                )
                                : archiveData.SignedIdentity.GetValue();
                        if (
                            new Version(identity.NewVersionString) 
                            < CommonClientConstants.CurrentClientVersion
                        )
                            throw new Exception("New version less than current");
                        /*_log.Trace(
                            "{0} GetUpdateClientPackageInfo {1} {2} {3}",
                            curMethodName,
                            CommonClientConstants.CurrentClientVersionString,
                            diffsOnly,
                            archiveData.WriteObjectToJson()
                        );*/
                        progressSubject.OnNext(
                            new DownloadFileProgressInfo
                            {
                                ChunkDownloadedCount = 0,
                                DownloadedSize = 0,
                                TotalChunkCount = archiveData.PackageDataInfo.Chunks.Count,
                                TotalSize = archiveData.PackageDataInfo
                                    .Chunks.Select(x => x.DataLength).Sum()
                            }
                        );
                        var updateData = await MyFileManagerClientFunctions.Download(
                            archiveData.PackageDataInfo,
                            async i =>
                                await _proxyClientSamInfo.JsonClient.GetUpdateClientPackageChunk(
                                    archiveData.PackageDataInfo.FileGuid, i
                                ).ConfigureAwait(false),
                            token,
                            4,
                            progressSubject
                        ).ConfigureAwait(false);
                        
                        //_log.Trace("Get update archive data");
                        byte[] resultData;
                        if (archiveData.DiffsOnly)
                        {
                            try
                            {
                                resultData = await Task.Factory.StartNew(
                                    () => MyBinDiffHelper.ApplyPatch(oldVersionArchiveData, updateData),
                                    TaskCreationOptions.LongRunning
                                ).ConfigureAwait(false);
                            }
                            catch (Exception exc)
                            {
                                _log.Error(
                                    "{0} applying patch error '{1}'",
                                    curMethodName,
                                    exc.ToString()
                                );
                                if (diffsOnly)
                                {
                                    diffsOnly = false;
                                    throw new TimeoutException(); //retry with full archive download
                                }
                                throw new Exception("Wrong new version archive data");
                            }
                        }
                        else
                        {
                            resultData = updateData;
                        }
                        
                        using (var hashAlg = new SHA256Managed())
                        {
                            if (
                                !(hashAlg.ComputeHash(resultData)
                                    .SequenceEqual(identity.NewVersionArchiveSha256Hash)
                                )
                            )
                            {
                                _log.Error(
                                    "Wrong new version archive data hash actual={0}, right={1}",
                                    hashAlg.ComputeHash(resultData),
                                    identity.NewVersionArchiveSha256Hash
                                );
                                if (diffsOnly)
                                {
                                    diffsOnly = false;
                                    throw new TimeoutException(); //retry with full archive download
                                }
                                throw new Exception("Wrong new version archive data");
                            }
                        }
                        return new Tuple<Version, byte[]>(
                            new Version(identity.NewVersionString),
                            resultData
                        );
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        public async Task<Version> GetClientVersionFromServer(
            CancellationToken token
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        var proxyServerInfo
                            = await _proxyClientSamInfo.JsonClient
                                .GetProxyServerInfo().ThrowIfCancelled(token).ConfigureAwait(false);
                        return new Version(
                            proxyServerInfo.ClientVersionString
                        );
                    }
                    catch (TimeoutException)
                    {
                    }
                }
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
                        try
                        {
                            _proxySessionModel.Balance
                                = await _proxyClientSamInfo.JsonClient.GetCurrentBalance()
                                    .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
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
                _log.Error(
                    "UpdateBalance unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }

        private readonly SemaphoreSlim _calcTaskLockSem = new SemaphoreSlim(1);
        private bool _taskCalculated = false;
        private async void AutoProxyAccountFillUpAction()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await _calcTaskLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        if (_taskCalculated)
                            return;
                        if (
                            _proxySessionModel.Balance 
                            >= _settings.AutoFillupMinBalance
                        )
                        {
                            return;
                        }
                        decimal curBalance = 0.0m;
                        while (true)
                        {
                            try
                            {
                                curBalance 
                                    = await _proxyClientSamInfo.JsonClient
                                        .GetCurrentBalance()
                                        .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                break;
                            }
                            catch (TimeoutException)
                            {
                            }
                        }
                        _proxySessionModel.Balance = curBalance;
                        if (curBalance < _settings.AutoFillupMinBalance)
                        {
                            ComputableTaskSerializedDescription taskDesc = null;
                            var wishfulTaskIncome = (long)(curBalance*0.25m);
                            if (wishfulTaskIncome < _proxyServerInfoFromServer.MinMiningTaskBalanceGain)
                                wishfulTaskIncome = _proxyServerInfoFromServer.MinMiningTaskBalanceGain;
                            if (wishfulTaskIncome > _proxyServerInfoFromServer.MaxMiningTaskBalanceGain)
                                wishfulTaskIncome = _proxyServerInfoFromServer.MaxMiningTaskBalanceGain;
                            while (true)
                            {
                                try
                                {
                                    
                                    taskDesc = await _proxyClientSamInfo.JsonClient
                                        .GenNewTaskDescryption(
                                            (int) ETaskTypes.Scrypt,
                                            wishfulTaskIncome
                                        ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                    break;
                                }
                                catch (TimeoutException)
                                {
                                }
                            }
                            if (taskDesc == null)
                                throw new ArgumentNullException();
                            var miningManagerTaskStatusTask =
                                _miningManager.Model.OnTaskStatusChanged
                                    .Where(x =>
                                        x.CommonInfo.TaskGuid 
                                            == taskDesc.CommonInfo.TaskGuid
                                        && x.Status == EMiningTaskStatus.Complete
                                        || x.Status == EMiningTaskStatus.Fault
                                    )
                                    .FirstAsync()
                                    .ToTask();
                            await _miningManager.AddTask(
                                MyNotifyPropertyChangedImpl.GetProxy(
                                    (IMiningTaskInfo)new MiningTaskInfo()
                                    {
                                        CommonInfo = taskDesc.CommonInfo,
                                        JsonSerializedTaskDescription =
                                            taskDesc.TaskDescriptionSerialized,
                                        JsonSerializedTaskSolution = null,
                                        Priority = 0,
                                        Status = EMiningTaskStatus.Waiting
                                    }
                                )
                            ).ConfigureAwait(false);
                            _taskCalculated = true;
                            _proxySessionModel.TaskComputing = true;
                            try
                            {
                                var miningManagerTaskInfo
                                    = await miningManagerTaskStatusTask
                                        .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                await _miningManager.RemoveTask(
                                    miningManagerTaskInfo.CommonInfo.TaskGuid
                                ).ConfigureAwait(false);
                                if (
                                    miningManagerTaskInfo.Status
                                    == EMiningTaskStatus.Complete
                                )
                                {
                                    try
                                    {
                                        await MiscFuncs.RepeatWhileTimeout(
                                            async () => await _proxyClientSamInfo.JsonClient
                                                .PassTaskSolution(
                                                    (int) ETaskTypes.Scrypt,
                                                    new ComputableTaskSerializedSolution()
                                                    {
                                                        CommonInfo =
                                                            miningManagerTaskInfo.CommonInfo,
                                                        TaskSolutionSerialized =
                                                            miningManagerTaskInfo
                                                                .JsonSerializedTaskSolution
                                                    }
                                                ).ConfigureAwait(false),
                                            _cts.Token
                                        ).ConfigureAwait(false);
                                    }
                                    catch (RpcRethrowableException rpcExc)
                                    {
                                        if (
                                            rpcExc.ErrorData.ErrorCode
                                            == (int)EPassTaskSolutionProxyErrCodes
                                                .TaskAlreadySolved
                                            )
                                        {
                                            _log.Trace(
                                                "AutoProxyAccountFillUpAction task already solved"
                                                );
                                        }
                                        else
                                        {
                                            _log.Trace(
                                                "AutoProxyAccountFillUpAction pass task error '{0}'",
                                                (EPassTaskSolutionProxyErrCodes)
                                                    rpcExc.ErrorData.ErrorCode
                                                );
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                _taskCalculated = false;
                                _proxySessionModel.TaskComputing = false;
                                UpdateBalance();
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
                _log.Error(
                    "AutoProxyAccountFillUpAction err {0}", 
                    exc.ToString()
                );
            }
        }
        private readonly CancellationTokenSource _cts 
            = new CancellationTokenSource();
        private static readonly Logger _log 
            = LogManager.GetCurrentClassLogger();
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            _log.Trace("MyDisposeAsync _cts");
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            _log.Trace("MyDisposeAsync _stateHelper");
            if (_lookupServerSession != null)
            {
                await _lookupServerSession.MyDisposeAsync().ConfigureAwait(false);
                _lookupServerSession = null;
            }
            _log.Trace("MyDisposeAsync _lookupServerSession");
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            _log.Trace("MyDisposeAsync _subscriptions");
            await _proxyClientSamInfo.MyDisposeAsync().ConfigureAwait(false);
            _log.Trace("MyDisposeAsync _proxyClientSamInfo");
            _cts.Dispose();
        }
    }
}