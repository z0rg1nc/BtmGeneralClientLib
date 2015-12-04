using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.ComputableTaskInterfaces.Client;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.ObjectStateLib;
using BtmI2p.TaskSolvers.Scrypt;
using NLog;

namespace BtmI2p.BitMoneyClient.Lib
{
    public class MiningTaskManagerModel
    {
        public Subject<IMiningTaskInfo> OnTaskStatusChanged
            = new Subject<IMiningTaskInfo>();
        public Subject<IMiningTaskInfo> OnTaskAdded
            = new Subject<IMiningTaskInfo>();
        public Subject<Guid> OnTaskRemoved 
            = new Subject<Guid>();
    }

    public enum EMiningNativeSupport
    {
        None,
        WinDll,
        LinuxSo
    }

    
    public interface IMiningTaskManagerSettings : IMyNotifyPropertyChanged
    {
        int RunningTaskPoolSize { get; set; }
        int ThreadsPerTask { get; set; }
        EMiningNativeSupport NativeSupport { get; set; }
        bool PassProblemsToExternalSolver { get; set; }
        string ExternalSolverExeFullPath { get; set; }
        string ExternalSolverCommandArguments { get; set; }
        string SolutionsFolder { get; set; }
    }

    public class MiningTaskManagerSettings
        : IMiningTaskManagerSettings
    {
        public MiningTaskManagerSettings()
        {
            RunningTaskPoolSize = 1;
            ThreadsPerTask = 2;
            NativeSupport = EMiningNativeSupport.None;
            PassProblemsToExternalSolver = false;
            ExternalSolverExeFullPath = string.Empty;
            ExternalSolverCommandArguments = "--td=__TASK_DESCRIPTION__ --outdir=\"__SOLUTION_DIR__\"";
            SolutionsFolder = ".";
        }

        public int RunningTaskPoolSize { get; set; }
        public int ThreadsPerTask { get; set; }
        public EMiningNativeSupport NativeSupport { get; set; }
        public bool PassProblemsToExternalSolver { get; set; }
        public string ExternalSolverExeFullPath { get; set; }
        public string ExternalSolverCommandArguments { get; set; }
        public string SolutionsFolder { get; set; }
        public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
        {
            get
            {
                throw new Exception(MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString);
            }
        }
    }

    public enum EMiningTaskStatus
    {
        Complete,
        Expired,
        Running,
        Paused,
        Waiting,
        Fault
    }
    
    public interface IMiningTaskInfo : IMyNotifyPropertyChanged
    {
        ComputableTaskCommonInfo CommonInfo { get; set; }
        int Priority { get; set; }
        EMiningTaskStatus Status { get; set; }
        string JsonSerializedTaskDescription { get; set; }
        string JsonSerializedTaskSolution { get; set; }
    }

    public class MiningTaskInfo : IMiningTaskInfo
    {
        public MiningTaskInfo()
        {
            Status = EMiningTaskStatus.Waiting;
            JsonSerializedTaskSolution = null;
        }

        public ComputableTaskCommonInfo CommonInfo { get; set; }
        public int Priority { get; set; }
        public EMiningTaskStatus Status { get; set; }
        public string JsonSerializedTaskDescription { get; set; }
        public string JsonSerializedTaskSolution { get; set; }
        public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
        {
            get
            {
                throw new Exception(MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString);
            }
        }
    }

    public class MiningTaskManager : IMyAsyncDisposable
    {
        public enum ECallbackMiningTaskCodes
        {
            None,
            Pause,
            ToWaiting
        }
        
        public interface IMiningTaskInfoInternal : IMyNotifyPropertyChanged, ILockSemaphoreSlim
        {
            IMiningTaskInfo PublicTaskInfo { get; set; }
            ECallbackMiningTaskCodes CallbackRequest { get; set; }
        }
        private class MiningTaskInfoInternal : IMiningTaskInfoInternal
        {
            public MiningTaskInfoInternal()
            {
                LockSem = new SemaphoreSlim(1);
                CallbackRequest = ECallbackMiningTaskCodes.None;
            }
            public SemaphoreSlim LockSem { get; private set; }
            public IMiningTaskInfo PublicTaskInfo { get; set; }
            public ECallbackMiningTaskCodes CallbackRequest { get; set; }
            public Subject<MyNotifyPropertyChangedArgs> PropertyChangedSubject
            {
                get
                {
                    throw new Exception(MyNotifyPropertyChangedArgs.DefaultNotProxyExceptionString);
                }
            }
        }
        private readonly SemaphoreSlim _computingTaskDbLockSem 
            = new SemaphoreSlim(1);
        private readonly Dictionary<Guid, IMiningTaskInfoInternal> _computingTaskDb 
            = new Dictionary<Guid,IMiningTaskInfoInternal>();
        private IMiningTaskManagerSettings _settings;

        public MiningTaskManagerModel Model
        {
            get; 
            private set;
        }

        private MiningTaskManager()
        {
            
        }

        private ComputableTaskSerializedSolution ReadSolutionFromDirectory(Guid taskGuid)
        {
            var solutionPath = Path.Combine(
                _settings.SolutionsFolder,
                string.Format("{0}.json", taskGuid)
            );
            if (!File.Exists(solutionPath))
                return null;
            try
            {
                return File.ReadAllText(
                    solutionPath,
                    Encoding.UTF8
                ).ParseJsonToType<ComputableTaskSerializedSolution>();
            }
            catch (Exception exc)
            {
                _logger.Error("Parse solution error '{0}'",exc.ToString());
                File.Delete(solutionPath);
                return null;
            }
        }

        public static async Task<MiningTaskManager> CreateInstance(
            IMiningTaskManagerSettings settings,
            MiningTaskManagerModel model
        )
        {
            if(settings == null)
                throw new ArgumentNullException(
                    "settings"
                );
            if(model == null)
                throw new ArgumentNullException(
                    "model"
                );
            var result = new MiningTaskManager();
            result._settings = settings;
            result.Model = model;
            result._stateHelper.SetInitializedState();
            result._subscriptions.AddRange(
                new []{
                    settings.PropertyChangedSubject.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        i => result.ProcessTasks()
                    ),
                    result.Model.OnTaskAdded.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        async i =>
                        {
                            await result.CancelTaskLessPriority(
                                i.Priority
                            ).ConfigureAwait(false);
                            result.ProcessTasks();
                        }
                    ),
                    result.Model.OnTaskRemoved.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        i => result.ProcessTasks()
                    ),
                    result.Model.OnTaskStatusChanged.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        i => result.ProcessTasks()
                    ),
                    settings.PropertyChangedSubject.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        i => result.ProcessTasks()
                    )
                }
            );
            result.ProcessTasks();
            return await Task.FromResult(result).ConfigureAwait(false);
        }


        private readonly List<IDisposable> _subscriptions
            = new List<IDisposable>();
        private async void ProcessTaskInternal(
            IMiningTaskInfoInternal taskInfo
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    Task<MyNotifyPropertyChangedArgs> callbackRequestTask;
                    using (await taskInfo.LockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        if (
                            taskInfo.PublicTaskInfo.Status
                            != EMiningTaskStatus.Running
                        )
                            return;
                        callbackRequestTask
                            = taskInfo.PropertyChangedSubject.Where(
                                x =>
                                    x.PropertyName
                                        == taskInfo.MyNameOfProperty(
                                            e => e.CallbackRequest
                                        )
                                    && (ECallbackMiningTaskCodes) x.CastedNewProperty
                                        != ECallbackMiningTaskCodes.None
                                ).FirstAsync()
                                .ToTask(_cts.Token);
                        if (
                            taskInfo.CallbackRequest
                            == ECallbackMiningTaskCodes.Pause
                            )
                        {
                            taskInfo.PublicTaskInfo.Status
                                = EMiningTaskStatus.Paused;
                            taskInfo.CallbackRequest
                                = ECallbackMiningTaskCodes.None;
                            Model.OnTaskStatusChanged.OnNext(
                                taskInfo.PublicTaskInfo
                            );
                            return;
                        }
                        if (
                            taskInfo.CallbackRequest
                            == ECallbackMiningTaskCodes.ToWaiting
                            )
                        {
                            taskInfo.PublicTaskInfo.Status
                                = EMiningTaskStatus.Waiting;
                            taskInfo.CallbackRequest
                                = ECallbackMiningTaskCodes.None;
                            Model.OnTaskStatusChanged.OnNext(
                                taskInfo.PublicTaskInfo
                            );
                            return;
                        }

                    }
                    using (var cts = new CancellationTokenSource())
                    {
                        try
                        {
                            //Serialized ScryptTaskSolution
                            Task<string> taskSolutionTask = null;
                            bool passProblemsToExternalSolver = _settings.PassProblemsToExternalSolver;
                            if (passProblemsToExternalSolver)
                            {
                                var waitTillSolutionFileAdded
                                    = Task.Run(async () =>
                                    {
                                        while (true)
                                        {
                                            if (
                                                cts.Token.IsCancellationRequested
                                                || taskInfo.PublicTaskInfo.CommonInfo.ValidUntil < DateTime.UtcNow)
                                            {
                                                throw new OperationCanceledException();
                                            }
                                            var fileName = Path.Combine(
                                                _settings.SolutionsFolder,
                                                string.Format(
                                                    "{0}.json",
                                                    taskInfo.PublicTaskInfo.CommonInfo.TaskGuid
                                                    )
                                                );
                                            if (
                                                File.Exists(
                                                    fileName
                                                    )
                                                )
                                            {
                                                return
                                                    File.ReadAllText(fileName, Encoding.UTF8)
                                                        .ParseJsonToType<ComputableTaskSerializedSolution>()
                                                        .TaskSolutionSerialized;
                                            }
                                            await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                                        }
                                    }, cts.Token);
                                var taskSolutionAlreadyExisted =
                                    ReadSolutionFromDirectory(
                                        taskInfo.PublicTaskInfo.CommonInfo.TaskGuid
                                        );
                                if (taskSolutionAlreadyExisted != null)
                                {
                                    taskSolutionTask = Task.FromResult(
                                        taskSolutionAlreadyExisted.TaskSolutionSerialized
                                        );
                                }
                                else
                                {
                                    var curArguments = _settings.ExternalSolverCommandArguments
                                        .Replace(
                                            "__SOLUTION_DIR__",
                                            Path.GetFullPath(_settings.SolutionsFolder)
                                        ).Replace(
                                            "__TASK_DESCRIPTION__",
                                            Convert.ToBase64String(
                                                Encoding.UTF8.GetBytes(
                                                    new ComputableTaskSerializedDescription()
                                                    {
                                                        CommonInfo = taskInfo.PublicTaskInfo.CommonInfo,
                                                        TaskDescriptionSerialized = taskInfo.PublicTaskInfo
                                                            .JsonSerializedTaskDescription
                                                    }.WriteObjectToJson()
                                                    )
                                                )
                                        );
                                    var externalSolverProcessStartInfo = new ProcessStartInfo();
                                    var workingDirectory = Path.GetDirectoryName(
                                        _settings.ExternalSolverExeFullPath
                                        );
                                    if (string.IsNullOrWhiteSpace(workingDirectory))
                                        throw new ArgumentNullException(
                                            MyNameof.GetLocalVarName(() => workingDirectory));
                                    externalSolverProcessStartInfo.WorkingDirectory = workingDirectory;
                                    externalSolverProcessStartInfo.FileName = _settings.ExternalSolverExeFullPath;
                                    externalSolverProcessStartInfo.Arguments = curArguments;
                                    externalSolverProcessStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                                    externalSolverProcessStartInfo.CreateNoWindow = true;
                                    externalSolverProcessStartInfo.UseShellExecute = false;
                                    externalSolverProcessStartInfo.RedirectStandardInput = true;
                                    var process = Process.Start(externalSolverProcessStartInfo);
                                    await Task.Delay(500).ConfigureAwait(false); //Initial load of external solver
                                    if (process == null)
                                        throw new ArgumentNullException(
                                            MyNameof.GetLocalVarName(() => process)
                                            );
                                    var reg = new CancellationTokenRegistration();
                                    var processCancelFunc = (Func<Task>) (
                                        async () =>
                                        {
                                            try
                                            {
                                                if (!process.HasExited)
                                                {
                                                    process.StandardInput.WriteLine("cancel");
                                                    if (
                                                        !await MiscFuncs.WaitForProcessExitAsync(
                                                            process,
                                                            CancellationToken.None,
                                                            5000
                                                            ).ConfigureAwait(false)
                                                        && !process.HasExited
                                                        )
                                                    {
                                                        _logger.Trace(
                                                            "Force external process kill"
                                                            );
                                                        process.Kill();
                                                    }
                                                }
                                            }
                                            catch (Exception e)
                                            {
                                                _logger.Error(
                                                    "processCancelFunc error '{0}'",
                                                    e.ToString()
                                                    );
                                            }
                                        }
                                        );
                                    if (!cts.IsCancellationRequested)
                                    {
                                        reg = cts.Token.Register(
                                            async () => { await processCancelFunc().ConfigureAwait(false); }
                                            );
                                    }
                                    if (cts.IsCancellationRequested)
                                    {
                                        reg.Dispose();
                                        await processCancelFunc().ConfigureAwait(false);
                                    }
                                    taskSolutionTask = waitTillSolutionFileAdded;
                                }
                            }
                            else
                            {
                                if (
                                    taskInfo.PublicTaskInfo.CommonInfo.TaskType ==
                                    (int) ETaskTypes.Scrypt
                                    )
                                {

                                    var taskDesc =
                                        taskInfo.PublicTaskInfo.JsonSerializedTaskDescription
                                            .ParseJsonToType<ScryptTaskDescription>();
                                    taskSolutionTask = await Task.Factory.StartNew(
                                        async () =>
                                            (
                                                await ScryptTaskSolver.SolveScryptTask(
                                                    new ComputableTaskDescription<
                                                        ScryptTaskDescription
                                                        >
                                                    {
                                                        TaskDescription = taskDesc,
                                                        CommonInfo =
                                                            taskInfo.PublicTaskInfo.CommonInfo
                                                    },
                                                    cts.Token,
                                                    _settings.ThreadsPerTask,
                                                    _settings.NativeSupport == EMiningNativeSupport.None
                                                        ? ScryptTaskSolver.EUseNativeScrypt.None
                                                        : _settings.NativeSupport == EMiningNativeSupport.WinDll
                                                            ? ScryptTaskSolver.EUseNativeScrypt.WinDll
                                                            : ScryptTaskSolver.EUseNativeScrypt.LinuxSo
                                                    ).ConfigureAwait(false)
                                                ).TaskSolution.WriteObjectToJson()
                                        ).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw new ArgumentException(
                                        "Wrong task type"
                                        );
                                }
                            }
                            if (taskSolutionTask == null)
                                throw new ArgumentNullException(
                                    MyNameof.GetLocalVarName(
                                        () => taskSolutionTask
                                        )
                                    );
                            Task waitedTask;
                            try
                            {
                                waitedTask = await Task.WhenAny(
                                    taskSolutionTask,
                                    callbackRequestTask
                                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                waitedTask = null;
                            }
                            /**/
                            if (
                                waitedTask == taskSolutionTask
                                )
                            {
                                string taskSolution = null;
                                using (await taskInfo.LockSem.GetDisposable().ConfigureAwait(false))
                                {
                                    try
                                    {
                                        taskSolution = await taskSolutionTask.ConfigureAwait(false);
                                        if (taskSolution == null)
                                            throw new ArgumentNullException(
                                                MyNameof.GetLocalVarName(() => taskSolution)
                                                );
                                        taskInfo.PublicTaskInfo.JsonSerializedTaskSolution
                                            = taskSolution;
                                        taskInfo.PublicTaskInfo.Status =
                                            EMiningTaskStatus.Complete;
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        if (taskInfo.PublicTaskInfo.CommonInfo.ValidUntil < DateTime.UtcNow)
                                        {
                                            taskInfo.PublicTaskInfo.Status
                                                = EMiningTaskStatus.Fault;
                                        }
                                        else
                                        {
                                            taskInfo.PublicTaskInfo.Status
                                                = EMiningTaskStatus.Waiting;
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        taskInfo.PublicTaskInfo.Status
                                            = EMiningTaskStatus.Fault;
                                        cts.Cancel();
                                    }
                                    Model.OnTaskStatusChanged.OnNext(
                                        taskInfo.PublicTaskInfo
                                        );
                                }
                            }
                            else
                            {
                                cts.Cancel();
                                if (waitedTask == null)
                                {
                                    using (await taskInfo.LockSem.GetDisposable().ConfigureAwait(false))
                                    {
                                        taskInfo.PublicTaskInfo.Status
                                            = EMiningTaskStatus.Waiting;
                                        Model.OnTaskStatusChanged.OnNext(
                                            taskInfo.PublicTaskInfo
                                            );
                                    }
                                }
                                else
                                {
                                    // == callbackRequestTask
                                    var callbackRequest
                                        = (ECallbackMiningTaskCodes)
                                            (await callbackRequestTask.ConfigureAwait(false)).CastedNewProperty;
                                    if (
                                        callbackRequest
                                        == ECallbackMiningTaskCodes.Pause
                                        )
                                    {
                                        using (await taskInfo.LockSem.GetDisposable().ConfigureAwait(false))
                                        {
                                            taskInfo.PublicTaskInfo.Status
                                                = EMiningTaskStatus.Paused;
                                            taskInfo.CallbackRequest
                                                = ECallbackMiningTaskCodes.None;
                                            Model.OnTaskStatusChanged.OnNext(
                                                taskInfo.PublicTaskInfo
                                                );
                                        }
                                        return;
                                    }
                                    if (
                                        callbackRequest
                                        == ECallbackMiningTaskCodes.ToWaiting
                                        )
                                    {
                                        using (await taskInfo.LockSem.GetDisposable().ConfigureAwait(false))
                                        {
                                            taskInfo.PublicTaskInfo.Status
                                                = EMiningTaskStatus.Waiting;
                                            taskInfo.CallbackRequest
                                                = ECallbackMiningTaskCodes.None;
                                            Model.OnTaskStatusChanged.OnNext(
                                                taskInfo.PublicTaskInfo
                                                );
                                        }
                                        return;
                                    }
                                    throw new NotImplementedException();
                                }
                            }
                        }
                        finally
                        {
                            cts.Cancel();
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
                _logger.Error(
                    "ProcessTaskInternal unexpected error '{0}'",
                    exc.ToString()
                );
            }
            finally
            {
                Interlocked.Decrement(ref _runningTaskCount);
                ProcessTasks();
            }
        }

        private void RemoveTaskSolutionsFromSolutionDirectory(Guid taskGuid)
        {
            var filePath = Path.Combine(
                _settings.SolutionsFolder,
                string.Format("{0}.json", taskGuid)
            );
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public async Task RemoveTask(Guid taskGuid)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _computingTaskDbLockSem.GetDisposable().ConfigureAwait(false))
                {
                    if (!_computingTaskDb.ContainsKey(taskGuid))
                        throw new Exception("No task with such guid");
                    var taskToRemove = _computingTaskDb[taskGuid];
                    if (
                        taskToRemove.PublicTaskInfo.Status
                        == EMiningTaskStatus.Running
                    )
                    {
                        taskToRemove.CallbackRequest
                            = ECallbackMiningTaskCodes.Pause;
                    }
                    _computingTaskDb.Remove(taskGuid);
                    Model.OnTaskRemoved.OnNext(taskGuid);
                    RemoveTaskSolutionsFromSolutionDirectory(taskGuid);
                }
            }
        }

        public async Task AddTask(IMiningTaskInfo taskInfo)
        {
            using (_stateHelper.GetFuncWrapper())
            {
                if(
                    taskInfo.Status == EMiningTaskStatus.Running 
                    || taskInfo.Status == EMiningTaskStatus.Fault
                    || taskInfo.Status == EMiningTaskStatus.Complete
                )
                    throw new ArgumentOutOfRangeException(
                        "Wrong adding task status"
                    );
                using (await _computingTaskDbLockSem.GetDisposable().ConfigureAwait(false))
                {
                    if (
                        _computingTaskDb.ContainsKey(
                            taskInfo.CommonInfo.TaskGuid
                        )
                    )
                        throw new ArgumentException(
                            "Task with such guid already added"
                        );
                    var miningTaskInfoInternal
                        = MyNotifyPropertyChangedImpl.GetProxy(
                            (IMiningTaskInfoInternal)new MiningTaskInfoInternal()
                            {
                                PublicTaskInfo = taskInfo
                            }
                        );
                    _computingTaskDb.Add(
                        taskInfo.CommonInfo.TaskGuid, 
                        miningTaskInfoInternal
                    );
                }
                Model.OnTaskAdded.OnNext(
                    taskInfo
                );
            }
        }

        private async Task CancelTaskLessPriority(int priority)
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await _computingTaskDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        foreach (var internalTaskInfo in _computingTaskDb.Values)
                        {
                            using (await internalTaskInfo.LockSem.GetDisposable().ConfigureAwait(false))
                            {
                                if (
                                    internalTaskInfo.PublicTaskInfo.Priority 
                                        > priority
                                    && internalTaskInfo.PublicTaskInfo.Status
                                        == EMiningTaskStatus.Running
                                    )
                                {
                                    internalTaskInfo.CallbackRequest
                                        = ECallbackMiningTaskCodes.ToWaiting;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _logger.Error(
                    "CancelTaskLessPriority err '{0}'", 
                    exc.ToString()
                );
            }
        }
        private readonly SemaphoreSlim _processTasksLockSem
            = new SemaphoreSlim(1);
        private int _runningTaskCount = 0;
        private async void ProcessTasks()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await _processTasksLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (true)
                        {
                            if (_runningTaskCount >= _settings.RunningTaskPoolSize)
                            {
                                break;
                            }
                            IMiningTaskInfoInternal taskToCompute = null;
                            int taskToComputePriority = int.MaxValue;
                            using (await _computingTaskDbLockSem.GetDisposable().ConfigureAwait(false))
                            {
                                foreach (var taskInfo in _computingTaskDb.Values)
                                {
                                    using (await taskInfo.LockSem.GetDisposable().ConfigureAwait(false))
                                    {
                                        if (
                                            taskInfo.PublicTaskInfo.Status
                                                == EMiningTaskStatus.Waiting
                                            && taskInfo.PublicTaskInfo.Priority
                                                < taskToComputePriority
                                        )
                                        {
                                            taskToCompute = taskInfo;
                                            taskToComputePriority =
                                                taskInfo.PublicTaskInfo.Priority;
                                        }
                                    }
                                }
                            }
                            if (taskToCompute != null)
                            {
                                Interlocked.Increment(ref _runningTaskCount);
                                using (await taskToCompute.LockSem.GetDisposable().ConfigureAwait(false))
                                {
                                    taskToCompute.PublicTaskInfo.Status 
                                        = EMiningTaskStatus.Running;
                                    Model.OnTaskStatusChanged.OnNext(
                                        taskToCompute.PublicTaskInfo
                                    );
                                }
                                ProcessTaskInternal(taskToCompute);
                            }
                            else
                            {
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
                _logger.Error(
                    "ProcessTasks err: '{0}'", 
                    exc.ToString()
                );
            }
        }

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly CancellationTokenSource _cts 
            = new CancellationTokenSource();
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("MiningTaskManager");

        public async Task MyDisposeAsync()
        {
            var curMethodName = this.MyNameOfMethod(e => e.MyDisposeAsync());
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("{0} _stateHelper", curMethodName);
            foreach (
                IDisposable subscription 
                in _subscriptions
            )
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            _logger.Trace("{0} _subscriptions", curMethodName);
            _cts.Dispose();
        }
    }
}
