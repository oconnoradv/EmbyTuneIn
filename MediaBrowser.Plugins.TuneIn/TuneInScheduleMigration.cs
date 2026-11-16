using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Linq;

namespace MediaBrowser.Plugins.TuneIn
{
    // Read legacy state only to undo the plugin's former global-trigger suppression.
    public sealed class TuneInScheduleMigration : IServerEntryPoint
    {
        private readonly ITaskManager manager;
        private readonly TuneInRefreshStateStore store;
        private readonly ILogger logger;

        public TuneInScheduleMigration(ITaskManager manager, ILogManager logs)
            : this(manager, Plugin.Instance.RefreshState, logs.GetLogger(nameof(TuneInScheduleMigration))) { }

        internal TuneInScheduleMigration(ITaskManager manager, TuneInRefreshStateStore store, ILogger logger = null)
        { this.manager = manager; this.store = store; this.logger = logger; }

        public void Run()
        {
            try { Restore(); }
            catch (Exception error)
            {
                logger?.ErrorException("TuneIn could not restore the formerly disabled Internet Channels schedule. Legacy state was retained; retry after resolving the error.", error);
                throw;
            }
        }

        internal void Restore()
        {
            lock (store.Gate)
            {
                var previous = store.Value.Schedule;
                var backups = store.Value.TriggerBackups;
                var backup = backups.SingleOrDefault(item => item.Key == "EmbyGlobalChannels");
                if (!previous.DisableGlobalRefreshTrigger && backup == null) return;
                var workers = manager.ScheduledTasks.Where(item => item.ScheduledTask.Key == "RefreshInternetChannels").ToArray();
                if (workers.Length != 1)
                    throw new InvalidOperationException("The built-in Refresh Internet Channels task is unavailable or ambiguous.");
                var worker = workers[0];
                var original = worker.Triggers
                    ?? throw new InvalidOperationException("The current Internet Channels schedule is unavailable.");
                // A nonempty current schedule may be an administrator's later edit. Never replace it.
                var restore = original.Length == 0;
                var next = restore
                    ? (backup != null ? backup.Triggers : worker.ScheduledTask.GetDefaultTriggers()?.ToArray())
                    : original;
                if (next == null || next.Any(trigger => trigger == null))
                    throw new InvalidOperationException("The saved or default Internet Channels schedule is incomplete.");
                try
                {
                    if (restore) worker.Triggers = next;
                    store.Value.Schedule = new TuneInScheduleSettings();
                    store.Value.TriggerBackups = backups.Where(item => item.Key != "EmbyGlobalChannels").ToList();
                    store.Save();
                }
                catch (Exception error)
                {
                    store.Value.Schedule = previous;
                    store.Value.TriggerBackups = backups;
                    if (restore)
                    {
                        try { worker.Triggers = original; }
                        catch (Exception rollbackError)
                        {
                            throw new AggregateException("Restoring the Internet Channels schedule failed, and rollback also failed. Legacy state was retained.", error, rollbackError);
                        }
                    }
                    throw;
                }
            }
        }

        public void Dispose() { }
    }
}
