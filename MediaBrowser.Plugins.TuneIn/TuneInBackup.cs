using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInBackupStatus
    {
        public string State { get; set; }
    }

    [Route("/TuneIn/Backup/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInBackupStatus : IReturn<TuneInBackupStatus> { }

    [Route("/TuneIn/Backup/Start", "POST")]
    [Authenticated(Roles = "Admin")]
    public sealed class StartTuneInBackup : IReturn<TuneInBackupStatus> { }

    public sealed class TuneInBackupService : IService, IRequiresRequest
    {
        private static readonly ConditionalWeakTable<ITaskManager, TuneInBackup> Jobs =
            new ConditionalWeakTable<ITaskManager, TuneInBackup>();
        private readonly TuneInBackup backup;
        public IRequest Request { get; set; }

        public TuneInBackupService(ITaskManager tasks, ILogManager logs)
        {
            backup = Jobs.GetValue(tasks, manager => new TuneInBackup(manager, logs.GetLogger(nameof(TuneInBackup))));
        }

        public TuneInBackupStatus Get(GetTuneInBackupStatus request)
        {
            CheckRequest();
            return backup.Status();
        }

        public TuneInBackupStatus Post(StartTuneInBackup request)
        {
            CheckRequest();
            return backup.Start();
        }

        private void CheckRequest()
        {
            if (Request == null) throw new UnauthorizedAccessException("Emby backup requires administrator access.");
        }
    }

    internal sealed class TuneInBackup
    {
        private const string BackupTaskName = "Emby Server Backup";
        private readonly object gate = new object();
        private readonly ITaskManager manager;
        private readonly ILogger logger;
        private Task dispatch;
        private bool startFailed;
        private DateTimeOffset? previousEnd;
        private bool requested;

        internal TuneInBackup(ITaskManager manager, ILogger logger)
        {
            this.manager = manager;
            this.logger = logger;
        }

        internal TuneInBackupStatus Status()
        {
            lock (gate) return GetStatus(FindTask());
        }

        internal TuneInBackupStatus Start()
        {
            lock (gate)
            {
                var worker = FindTask();
                var current = GetStatus(worker);
                if (current.State == "Unavailable" || current.State == "Busy" || current.State == "Running")
                    return current;

                previousEnd = worker.LastExecutionResult?.EndTimeUtc;
                requested = true;
                startFailed = false;
                dispatch = Task.Run(async () =>
                {
                    try
                    {
                        if (worker.State == TaskState.Idle)
                            await manager.Execute(worker, new TaskOptions()).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        lock (gate) startFailed = true;
                        logger?.ErrorException("TuneIn could not run the Emby Server Backup task.", error);
                    }
                });
                return new TuneInBackupStatus { State = "Running" };
            }
        }

        private IScheduledTaskWorker FindTask()
        {
            // Backup & Restore ships separately; the SDK exposes task keys but does not publish this task's key.
            var matches = manager.ScheduledTasks.Where(task => task.ScheduledTask != null &&
                string.Equals(task.ScheduledTask.Name, BackupTaskName, StringComparison.Ordinal)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private TuneInBackupStatus GetStatus(IScheduledTaskWorker worker)
        {
            var result = worker?.LastExecutionResult;
            var state = dispatch != null && !dispatch.IsCompleted ? "Running"
                : worker == null ? "Unavailable"
                : worker.State != TaskState.Idle ? "Busy"
                : requested && startFailed ? "Failed"
                : requested && (result == null || previousEnd.HasValue && result.EndTimeUtc <= previousEnd.Value) ? "Unconfirmed"
                : result == null ? "Ready"
                : result.Status == TaskCompletionStatus.Completed ? "Completed" : "Failed";
            return new TuneInBackupStatus { State = state };
        }
    }
}
