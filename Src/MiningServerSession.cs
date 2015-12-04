using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.AuthenticatedTransport;
using BtmI2p.CdnProxyEndReceiverClientTransport;
using BtmI2p.ComputableTaskInterfaces.Client;
using BtmI2p.GeneralClientInterfaces;
using BtmI2p.GeneralClientInterfaces.MiningServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.ObjectStateLib;
using BtmI2p.OneSideSignedJsonRpc;
using NLog;

namespace BtmI2p.BitMoneyClient.Lib
{
    public class MiningServerSessionModel
    {
        public readonly Subject<IMiningJobInfo> MiningJobAdded
            = new Subject<IMiningJobInfo>();
        public readonly Subject<Guid> MiningJobRemoved
            = new Subject<Guid>();
        public readonly Subject<IMiningJobInfo> MiningJobChanged
            = new Subject<IMiningJobInfo>();
        /**/
        public readonly Subject<ComputableTaskSerializedSolution> TaskSolutionAdded
            = new Subject<ComputableTaskSerializedSolution>();
        public readonly Subject<Guid> TaskSolutionRemoved
            = new Subject<Guid>();
        /**/
        public readonly Subject<MiningTransferToWalletInfo> TransferToWalletAdded
            = new Subject<MiningTransferToWalletInfo>();
        public readonly Subject<Guid> TransferToWalletRemoved
            = new Subject<Guid>();
    }

    public class MiningServerSessionSettings
    {
        public LightCertificate MiningClientCert = null;
    }
    
    public interface IMiningJobInfo : IMyNotifyPropertyChanged, ILockSemaphoreSlim
    {
        ComputableTaskSerializedDescription CurrentRunningTask { get; set; }
        EMiningTaskStatus Status { get; set; }
        ETaskTypes TaskType { get; set; }
        Guid JobGuid { get; set; }
        string JobName { get; set; }
        long WishfulTotalGain { get; set; }
        long MinedGain { get; set; }
    }

    public class MiningJobInfo : IMiningJobInfo
    {
        public MiningJobInfo()
        {
            LockSem = new SemaphoreSlim(1);
        }

        public MiningJobInfo(IMiningJobInfo orig) : this()
        {
            Status = orig.Status;
            TaskType = orig.TaskType;
            JobGuid = orig.JobGuid;
            JobName = orig.JobName;
            WishfulTotalGain = orig.WishfulTotalGain;
            MinedGain = orig.MinedGain;
            CurrentRunningTask = orig.CurrentRunningTask;
        }

        public SemaphoreSlim LockSem { get; private set; }

        public ComputableTaskSerializedDescription CurrentRunningTask { get; set; }
        public EMiningTaskStatus Status { get; set; }
        public ETaskTypes TaskType { get; set; }
        public Guid JobGuid { get; set; }
        public string JobName { get; set; }
        public long WishfulTotalGain { get; set; }
        public long MinedGain { get; set; }
        /**/
        public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
        {
            get
            {
                throw new Exception(MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString);
            }
        }
    }

    public class MiningTransferToWalletInfo
    {
        public Guid TransferGuid ;
        public Guid WaletTo;
        public long Amount;
    }
    public class MiningServerSession : IMyAsyncDisposable
    {
        private MiningServerSession()
        {
        }
        private MiningServerSessionSettings _settings;
        private MiningServerSessionModel _miningSessionModel;
        private AesProtectedByteArray _miningClientCertPass;
        private MiningServerInfoForClient _miningServerInfo;
        private MiningTaskManager _miningTaskManager;
        /**/
        public MiningServerInfoForClient MiningServerInfo
        {
            get
            {
                if (_cts.IsCancellationRequested)
                    throw new Exception("Cancelled");
                using (_stateHelper.GetFuncWrapper())
                {
                    return _miningServerInfo;
                }
            }
        }
        
        /**/
        //private byte[] _miningCertPass;
        private readonly SemaphoreSlim _miningJobInfosLockSem
            = new SemaphoreSlim(1);
        private readonly Dictionary<Guid,IMiningJobInfo> _miningJobInfos
            = new Dictionary<Guid,IMiningJobInfo>();

        public async Task PassTaskSolution(
            ComputableTaskSerializedSolution computableTaskSolution
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _solutionsListLockSem.GetDisposable().ConfigureAwait(false))
                {
                    _solutionsList.Add(
                        computableTaskSolution.CommonInfo.TaskGuid,
                        computableTaskSolution
                    );
                }
                _miningSessionModel.TaskSolutionAdded.OnNext(
                    computableTaskSolution
                );
            }
        }

        public async Task AddJob(
            Guid jobGuid,
            string jobName,
            long totalGain,
            ETaskTypes tasktype,
            long minedGain = 0
            )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _miningJobInfosLockSem.GetDisposable().ConfigureAwait(false))
                {
                    if(_miningJobInfos.ContainsKey(jobGuid))
                        throw new ArgumentException("jobGuid already exists");
                    var newMiningJobInfo
                        = MyNotifyPropertyChangedImpl.GetProxy(
                            (IMiningJobInfo)new MiningJobInfo()
                            {
                                JobGuid = jobGuid,
                                JobName = jobName,
                                WishfulTotalGain = totalGain,
                                MinedGain = minedGain,
                                TaskType = tasktype,
                                Status = EMiningTaskStatus.Waiting
                            }
                        );
                    _miningJobInfos.Add(
                        jobGuid,
                        newMiningJobInfo
                    );
                    newMiningJobInfo.PropertyChangedSubject.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        i => _miningSessionModel.MiningJobChanged.OnNext(
                            newMiningJobInfo
                        )
                    );
                    _miningSessionModel.MiningJobAdded.OnNext(
                        newMiningJobInfo
                    );
                }
            }
        }
        
        public static async Task<MiningServerSession> CreateInstance(
            MiningServerSessionSettings settings,
            MiningServerSessionModel miningSessionModel,
            IProxyServerSession proxySession,
            AesProtectedByteArray miningCertPass,
            MiningTaskManager miningTaskManager,
            CancellationToken cancellationToken
            )
        {
            var result = new MiningServerSession();
            result._miningTaskManager = miningTaskManager;
            result._settings = settings;
            result._miningClientCertPass = miningCertPass;
            result._miningSessionModel = miningSessionModel;
            ServerAddressForClientInfo miningServerInfo = null;
            var lookupSession =
                await proxySession.GetLookupServerSession(
                    cancellationToken
                ).ConfigureAwait(false);
            miningServerInfo =
                await lookupSession
                    .GetMiningServerAddress(
                        settings.MiningClientCert.Id,
                        cancellationToken
                    ).ConfigureAwait(false);
            if(miningServerInfo == null)
                throw new ArgumentNullException("miningServerInfo");
            if (miningServerInfo.ServerGuid == Guid.Empty)
            {
                throw new Exception(
                    "miningServerInfo.ServerGuid == Guid.Empty"
                );
            }
            /**/
            result._miningTransport
                = await EndReceiverServiceClientInterceptor
                    .GetClientProxy<IFromClientToMining>(
                        proxySession.ProxyInterface,
                        miningServerInfo.ServerGuid,
                        cancellationToken,
                        miningServerInfo.EndReceiverMethodInfos
                    ).ConfigureAwait(false);
            var checkIsMiningClientRegisteredTask = await Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            if (
                                !await result._miningTransport.IsMiningClientCertRegistered(
                                    settings.MiningClientCert.Id
                                ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false)
                            )
                                throw new Exception("Mining client not registered");
                            break;
                        }
                        catch (TimeoutException)
                        {
                        }
                    }
                }
            ).ConfigureAwait(false);
            var getMiningServerInfoTask = await Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            result._miningServerInfo =
                                await result._miningTransport.GetMiningServerInfo()
                                    .ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
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
                                    await result._miningTransport
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
                                    await result._miningTransport
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
            await checkIsMiningClientRegisteredTask.ConfigureAwait(false);
            await getMiningServerInfoTask.ConfigureAwait(false);
            /**/
            result._signedMiningTransport =
                await ClientOneSideSignedTransport<
                    ISignedFromClientToMining
                >
                    .CreateInstance(
                        new ServerOneSideSignedFuncsForClient()
                        {
                            GetMethodInfos =
                                async () =>
                                    (
                                        await result._miningTransport
                                            .GetSignedMethodInfos().ConfigureAwait(false)
                                    ).Select(x => x.JsonRpcMethodInfo)
                                    .ToList(),
                            ProcessSignedRequestPacket =
                                result
                                    ._miningTransport
                                    .ProcessSignedRequestPacket,
                            GetNowTimeFunc = proxySession.GetNowTime
                        },
                        settings.MiningClientCert,
                        miningCertPass,
                        cancellationToken,
                        await getSignedMethodInfosTask.ConfigureAwait(false)
                    ).ConfigureAwait(false);
            try
            {
                result._signedMiningTransportInterface
                    = await result._signedMiningTransport.GetClientProxy().ConfigureAwait(false);
                /**/
                result._authenticatedMiningTransport
                    = await ClientAuthenticatedTransport<
                        IAuthenticatedFromClientToMining
                        >
                        .CreateInstance(
                            new ClientAuthenticatedTransportSettings(),
                            new ServerAuthenticatedFuncsForClient()
                            {
                                AuthMe =
                                    result
                                        ._miningTransport
                                        .AuthMe,
                                GetAuthData =
                                    result
                                        ._miningTransport
                                        .GetAuthData,
                                GetMethodInfos = async () =>
                                    (
                                        await result
                                            ._miningTransport
                                            .GetAuthenticatedMethodInfos().ConfigureAwait(false)
                                        )
                                        .Select(x => x.JsonRpcMethodInfo)
                                        .ToList(),
                                ProcessRequest =
                                    result
                                        ._miningTransport
                                        .ProcessAuthenticatedPacket,
                                DecryptAuthDataFunc = async dataToDecrypt =>
                                {
                                    using (var tempPass = miningCertPass.TempData)
                                    {
                                        return await Task.FromResult(
											settings.MiningClientCert.DecryptData(
												dataToDecrypt, tempPass.Data
                                            )
										);
                                    }
                                }
                            },
                            settings.MiningClientCert.Id,
                            cancellationToken,
                            await getAuthenticatedMethodInfosTask.ConfigureAwait(false)
                        ).ConfigureAwait(false);
                try
                {
                    result._authenticatedMiningInterface
                        = await
                            result
                                ._authenticatedMiningTransport
                                .GetClientProxy().ConfigureAwait(false);
                    /**/
                    result._subscriptions.Add(
                        miningSessionModel.MiningJobAdded.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                            result.ProcessJob
                            )
                        );
                    result._subscriptions.Add(
                        miningSessionModel.MiningJobChanged.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                            result.ProcessJob
                            )
                        );
                    result._subscriptions.Add(
                        miningSessionModel.TaskSolutionAdded.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                            result.OnSolutionAdded
                            ));
                    result._subscriptions.Add(
                        miningSessionModel.TransferToWalletAdded.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                            result.OnTransferToAdded
                            ));
                    result._stateHelper.SetInitializedState();
                }
				catch (Exception)
                {
                    await result._authenticatedMiningTransport.MyDisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }catch(Exception)
            {
                await result._signedMiningTransport.MyDisposeAsync().ConfigureAwait(false);
                throw;
            }
            return result;
        }

        private async void OnSolutionAdded(
            ComputableTaskSerializedSolution solution
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    while (true)
                    {
                        try
                        {
                            await _authenticatedMiningInterface.PassTaskSolution(
                                solution.CommonInfo.TaskType,
                                solution
                            ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                        catch (RpcRethrowableException rpcExc)
                        {
                            var excCode =
                                (EPassTaskSolutionErrCodes)
                                    rpcExc.ErrorData.ErrorCode;
                            if (excCode == EPassTaskSolutionErrCodes.TaskNotExist
                                || excCode == EPassTaskSolutionErrCodes.TaskExpired
                                || excCode == EPassTaskSolutionErrCodes.WrongSolution
                                )
                            {
                                _logger.Error(
                                    "Pass solution error {0} {1}",
                                    solution.WriteObjectToJson(),
                                    excCode
                                );
                            }
                        }
                        using (await _solutionsListLockSem.GetDisposable().ConfigureAwait(false))
                        {
                            _solutionsList.Remove(
                                solution.CommonInfo.TaskGuid
                            );
                        }
                        _miningSessionModel.TaskSolutionRemoved.OnNext(
                            solution.CommonInfo.TaskGuid
                        );
                        break;
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
                _logger.Error(
                    "OnSolutionAdded unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }

        private readonly SemaphoreSlim _solutionsListLockSem 
            = new SemaphoreSlim(1);
        private readonly Dictionary<Guid,ComputableTaskSerializedSolution> 
            _solutionsList
                = new Dictionary<Guid, ComputableTaskSerializedSolution>();

        public async Task PauseJob(Guid jobGuid)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _miningJobInfosLockSem.GetDisposable().ConfigureAwait(false))
                {
                    if(!_miningJobInfos.ContainsKey(jobGuid))
                        throw new Exception("Job doesn't exist");
                    var jobInfo = _miningJobInfos[jobGuid];
                    jobInfo.Status = EMiningTaskStatus.Paused;
                }
            }
        }

        public async Task ResumeJob(Guid jobGuid)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _miningJobInfosLockSem.GetDisposable().ConfigureAwait(false))
                {
                    if (!_miningJobInfos.ContainsKey(jobGuid))
                        throw new Exception("Job doesn't exist");
                    var jobInfo = _miningJobInfos[jobGuid];
                    if (jobInfo.Status != EMiningTaskStatus.Paused)
                    {
                        throw new Exception("Wrong job status");
                    }
                    jobInfo.Status = EMiningTaskStatus.Waiting;
                    ProcessJob(jobInfo);
                }
            }
        }

        public async Task RemoveJob(Guid jobGuid)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _miningJobInfosLockSem.GetDisposable().ConfigureAwait(false))
                {
                    if (!_miningJobInfos.ContainsKey(jobGuid))
                        throw new Exception("Job doesn't exist");
                    var jobInfo = _miningJobInfos[jobGuid];
                    if (jobInfo.Status == EMiningTaskStatus.Running)
                    {
                        jobInfo.Status = EMiningTaskStatus.Paused;
                    }
                    _miningJobInfos.Remove(jobGuid);
                    _miningSessionModel.MiningJobRemoved.OnNext(
                        jobInfo.JobGuid
                    );
                }
            }
        }

        private async void ProcessJob(IMiningJobInfo jobInfo)
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    while (true)
                    {
                        using (await jobInfo.LockSem.GetDisposable(true).ConfigureAwait(false))
                        {
                            if (jobInfo.Status == EMiningTaskStatus.Waiting)
                            {
                                jobInfo.Status = EMiningTaskStatus.Running;
                            }
                            var jobInfoStatusChangedTask
                                = jobInfo.PropertyChangedSubject
                                    .Where(
                                        x => 
                                            x.PropertyName 
                                                == jobInfo.MyNameOfProperty(e => e.Status)
                                            &&
                                            (EMiningTaskStatus) x.CastedNewProperty
                                                == EMiningTaskStatus.Paused
                                    ).FirstAsync()
                                    .ToTask();
                            if(jobInfo.Status != EMiningTaskStatus.Running)
                                return;
                            if (
                                jobInfo.MinedGain
                                >= jobInfo.WishfulTotalGain
                            )
                            {
                                jobInfo.Status = EMiningTaskStatus.Complete;
                                using (await _miningJobInfosLockSem.GetDisposable().ConfigureAwait(false))
                                {
                                    _miningJobInfos.Remove(jobInfo.JobGuid);
                                }
                                _miningSessionModel.MiningJobRemoved.OnNext(
                                    jobInfo.JobGuid
                                );
                                return;
                            }
                            ComputableTaskSerializedDescription taskDesk;
                            if (
                                jobInfo.CurrentRunningTask != null
                                && jobInfo.CurrentRunningTask.CommonInfo.ValidUntil > DateTime.UtcNow
                            )
                            {
                                taskDesk = jobInfo.CurrentRunningTask;
                            }
                            else
                            {
                                jobInfo.CurrentRunningTask = null;
                                var wishfulTaskGain =
                                    jobInfo.WishfulTotalGain
                                    - jobInfo.MinedGain;
                                if (
                                    wishfulTaskGain
                                    < _miningServerInfo.MinMiningTaskBalanceGain
                                    )
                                    wishfulTaskGain =
                                        _miningServerInfo.MinMiningTaskBalanceGain;
                                if (wishfulTaskGain
                                    > _miningServerInfo.MaxMiningTaskBalanceGain)
                                    wishfulTaskGain =
                                        _miningServerInfo.MaxMiningTaskBalanceGain;
                                taskDesk = await MiscFuncs.RepeatWhileTimeout(
                                    async () => await _authenticatedMiningInterface
                                        .GenNewTaskDescription(
                                            (int)jobInfo.TaskType,
                                            wishfulTaskGain
                                        ).ConfigureAwait(false),
                                    _cts.Token
                                ).ConfigureAwait(false);
                                jobInfo.CurrentRunningTask = taskDesk;
                            }
                            var miningTaskStatusTask =
                                _miningTaskManager.Model.OnTaskStatusChanged
                                    .Where(
                                        x =>
                                            x.CommonInfo.TaskGuid
                                            == taskDesk.CommonInfo.TaskGuid
                                            && (
                                                x.Status == EMiningTaskStatus.Complete
                                                || x.Status == EMiningTaskStatus.Expired
                                                || x.Status == EMiningTaskStatus.Fault
                                            )
                                    )
                                    .FirstAsync()
                                    .ToTask();
                            await _miningTaskManager.AddTask(
                                MyNotifyPropertyChangedImpl.GetProxy(
                                    (IMiningTaskInfo)new MiningTaskInfo()
                                    {
                                        CommonInfo = taskDesk.CommonInfo,
                                        JsonSerializedTaskDescription =
                                            taskDesk.TaskDescriptionSerialized,
                                        Priority = 1
                                    }
                                )
                            ).ConfigureAwait(false);
                            Task waitedTask = null;
                            try
                            {
                                waitedTask = await Task.WhenAny(
                                    miningTaskStatusTask,
                                    jobInfoStatusChangedTask
                                )
                                .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            }
							finally 
                            {
                                await _miningTaskManager.RemoveTask(
                                    taskDesk.CommonInfo.TaskGuid
                                ).ConfigureAwait(false);
                            }
                            if(waitedTask == jobInfoStatusChangedTask)
                                return;
                            var taskSolution = await miningTaskStatusTask.ConfigureAwait(false);
                            var computableTaskSolution =
                                new ComputableTaskSerializedSolution()
                                {
                                    CommonInfo =
                                        taskSolution.CommonInfo,
                                    TaskSolutionSerialized =
                                        taskSolution.JsonSerializedTaskSolution
                                };
                            using (await _solutionsListLockSem.GetDisposable().ConfigureAwait(false))
                            {
                                _solutionsList.Add(
                                    computableTaskSolution.CommonInfo.TaskGuid,
                                    computableTaskSolution
                                );
                            }
                            _miningSessionModel.TaskSolutionAdded.OnNext(
                                computableTaskSolution
                            );
                            jobInfo.CurrentRunningTask = null;
                            jobInfo.MinedGain += taskDesk.CommonInfo.BalanceGain;
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
                _logger.Error(
                    "ProcessJob unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }
        
        public async Task<long> GetBalance()
        {
            if(_cts.IsCancellationRequested)
                throw new Exception("Cancelled");
            using (_stateHelper.GetFuncWrapper())
            {
                return await _authenticatedMiningInterface.GetCurrentBalance().ConfigureAwait(false);
            }
        }

        private async void OnTransferToAdded(
            MiningTransferToWalletInfo transferToWalletInfo
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    while (true)
                    {
                        try
                        {
                            await _signedMiningTransportInterface.TransferFundsToWallet(
                                transferToWalletInfo.WaletTo,
                                transferToWalletInfo.Amount,
                                transferToWalletInfo.TransferGuid
                            ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                        catch (RpcRethrowableException rpcExc)
                        {
                            var errCode = (ETransferFundsToWalletErrCodes) 
                                rpcExc.ErrorData.ErrorCode;
                            if (
                                errCode 
                                != 
                                ETransferFundsToWalletErrCodes
                                    .AlreadyProcessed
                            )
                            {
                                _logger.Error(
                                    "TransferFundsToWallet rpc exception '{0}'",
                                    errCode
                                );
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exc)
                        {
                            _logger.Error(
                                "TransferFundsToWallet err '{0}'",
                                exc.ToString()
                            );
                        }
                        using (
                            await _miningTransferToWalletInfosLockSem.GetDisposable().ConfigureAwait(false)
                        )
                        {
                            _miningTransferToWalletInfos.Remove(
                                transferToWalletInfo.TransferGuid
                            );
                        }
                        _miningSessionModel.TransferToWalletRemoved.OnNext(
                            transferToWalletInfo.TransferGuid
                        );
                        break;
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
                _logger.Error(
                    "OnTransferToAdded unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }

        private readonly SemaphoreSlim _miningTransferToWalletInfosLockSem
            = new SemaphoreSlim(1);
        private readonly Dictionary<Guid,MiningTransferToWalletInfo> 
            _miningTransferToWalletInfos
                = new Dictionary<Guid, MiningTransferToWalletInfo>();
        public async Task TransferFundsToWallet(
            Guid miningTranferGuid,
            Guid walletTo,
            long amount,
            AesProtectedByteArray miningCertPass
            )
        {
            _cts.Token.ThrowIfCancellationRequested();
            using (_stateHelper.GetFuncWrapper())
            {
                using (var curTempPass = _miningClientCertPass.TempData)
                {
                    using (var testTempPass = miningCertPass.TempData)
                    {
                        if (
                            !curTempPass.Data.SequenceEqual(
                                testTempPass.Data
                            )
                        )
                            throw new Exception("Wrong pass");
                    }
                }
                
                var transferToWalletInfo = 
                    new MiningTransferToWalletInfo()
                    {
                        Amount = amount,
                        TransferGuid = miningTranferGuid,
                        WaletTo = walletTo
                    };
                using (await _miningTransferToWalletInfosLockSem.GetDisposable().ConfigureAwait(false))
                {
                    _miningTransferToWalletInfos.Add(
                        miningTranferGuid,
                        transferToWalletInfo
                    );
                }
                _miningSessionModel.TransferToWalletAdded.OnNext(
                    transferToWalletInfo
                );
            }
        }

        public static async Task<LightCertificate> RegisterMiningClient(
            IProxyServerSession proxySession,
            LookupServerSession lookupSession,
            AesProtectedByteArray newMiningClientCertPass,
            CancellationToken cancellationToken
        )
        {
            var miningServerInfo
                = await lookupSession.GetMiningServerAddress(
                    Guid.NewGuid(),
                    cancellationToken
                ).ConfigureAwait(false);
            if(miningServerInfo.ServerGuid == Guid.Empty)
                throw new Exception("Mining server info not found");
            var miningServerInterface
                = await EndReceiverServiceClientInterceptor
                    .GetClientProxy<IFromClientToMining>(
                        proxySession.ProxyInterface,
                        miningServerInfo.ServerGuid,
                        cancellationToken,
                        miningServerInfo.EndReceiverMethodInfos
                    ).ConfigureAwait(false);
            Guid newMiningClientGuid;
            while (true)
            {
                try
                {
                    newMiningClientGuid =
                        await
                            miningServerInterface.GenMiningClientGuidForRegistration()
                                .ThrowIfCancelled(cancellationToken)
                                .ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                }
            }
            LightCertificate newMiningClientCert;
            using (var tempData = newMiningClientCertPass.TempData)
            {
                var miningClientCertPassBytes = tempData.Data;
                newMiningClientCert =
                    LightCertificatesHelper.GenerateSelfSignedCertificate(
                        ELightCertificateSignType.Rsa,
                        2048,
                        ELightCertificateEncryptType.Rsa,
                        2048,
                        EPrivateKeysKeyDerivationFunction.ScryptDefault,
                        newMiningClientGuid,
                        newMiningClientGuid.ToString(),
                        miningClientCertPassBytes
                    );
            }
            var requestGuid = Guid.NewGuid();
            while (true)
            {
                try
                {
                    var request = new RegisterMiningClientCertRequest()
                    {
                        PublicMiningClientCert =
                            newMiningClientCert.GetOnlyPublic(),
                        SentTime = await proxySession.GetNowTime().ConfigureAwait(false),
                        RequestGuid = requestGuid
                    };
                    SignedData<RegisterMiningClientCertRequest> signedRequest;
                    using (var tempPass = newMiningClientCertPass.TempData)
                    {
                        signedRequest =
                            new SignedData<RegisterMiningClientCertRequest>(
                                request,
                                newMiningClientCert,
                                tempPass.Data
                            );
                    }
                    await miningServerInterface.RegisterMiningClientCert(
                        signedRequest
                        ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (RpcRethrowableException rpcExc)
                {
                    if(
                        rpcExc.ErrorData.ErrorCode 
                        == (int)ERegisterMiningClientCertErrCodes.AlreadyRegistered
                    )
                        break;
                    throw;
                }
                catch (TimeoutException)
                {
                }
            }
            return newMiningClientCert;
        }

        private static readonly Logger _logger
            = LogManager.GetCurrentClassLogger();
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("MiningServerSession");
        private readonly CancellationTokenSource _cts
            = new CancellationTokenSource();
        private readonly List<IDisposable> _subscriptions 
            = new List<IDisposable>();
        /**/
        private IFromClientToMining _miningTransport;
        /**/
        private ClientOneSideSignedTransport<ISignedFromClientToMining>
            _signedMiningTransport;
        private ISignedFromClientToMining
            _signedMiningTransportInterface;
        /**/
        private ClientAuthenticatedTransport<IAuthenticatedFromClientToMining>
            _authenticatedMiningTransport;
        private IAuthenticatedFromClientToMining
            _authenticatedMiningInterface;
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
            await _authenticatedMiningTransport.MyDisposeAsync().ConfigureAwait(false);
            await _signedMiningTransport.MyDisposeAsync().ConfigureAwait(false);
            _miningClientCertPass.Dispose();
            _cts.Dispose();
        }
    }
}
