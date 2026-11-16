using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInSchedules
    {
        private readonly ITaskManager manager;
        private readonly TuneInRefreshStateStore store;

        internal TuneInSchedules(ITaskManager manager, TuneInRefreshStateStore store)
        { this.manager = manager; this.store = store; }

        internal TuneInScheduleSettings Read()
        {
            lock (store.Gate)
            {
                var current = store.Value.Schedule;
                return new TuneInScheduleSettings
                {
                    ManageIntervals = current.ManageIntervals,
                    GlobalIntervalMinutes = current.GlobalIntervalMinutes,
                    DisableGlobalRefreshTrigger = current.DisableGlobalRefreshTrigger,
                    AppliedUtc = current.AppliedUtc,
                    Overrides = current.Overrides.Select(item => new RadioGroupInterval
                    { Category = item.Category, Mode = item.Mode, Minutes = item.Minutes }).ToList()
                };
            }
        }

        internal TuneInScheduleSettings Apply(TuneInScheduleSettings settings)
        {
            Validate(settings);
            settings = new TuneInScheduleSettings
            {
                ManageIntervals = settings.ManageIntervals,
                GlobalIntervalMinutes = settings.GlobalIntervalMinutes,
                DisableGlobalRefreshTrigger = settings.DisableGlobalRefreshTrigger,
                Overrides = settings.Overrides.Select(item => new RadioGroupInterval
                { Category = item.Category, Mode = item.Mode, Minutes = item.Minutes }).ToList()
            };
            lock (store.Gate)
            {
                var changes = new List<TriggerChange>();
                foreach (var category in TuneInRefresh.Categories)
                {
                    var key = "TuneInForkRefresh_" + category;
                    var backup = store.Value.TriggerBackups.SingleOrDefault(item => item.Key == key);
                    if (!settings.ManageIntervals && backup == null) continue;
                    var worker = Worker(task => task.Key == key);
                    var entry = settings.Overrides.SingleOrDefault(item => item.Category == category);
                    var minutes = entry?.Mode == "manual" ? 0
                        : entry?.Mode == "custom" ? entry.Minutes : settings.GlobalIntervalMinutes;
                    changes.Add(Change(worker, settings.ManageIntervals,
                        minutes == 0 ? Array.Empty<TaskTriggerInfo>() : new[] { new TaskTriggerInfo
                        { Type = "IntervalTrigger", IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks } }));
                }
                var globalBackup = store.Value.TriggerBackups.SingleOrDefault(item => item.Key == "EmbyGlobalChannels");
                if (settings.DisableGlobalRefreshTrigger || globalBackup != null)
                {
                    var worker = Worker(task => task.Key == "RefreshInternetChannels");
                    changes.Add(Change(worker, settings.DisableGlobalRefreshTrigger,
                        Array.Empty<TaskTriggerInfo>(), "EmbyGlobalChannels"));
                }
                // Persist original triggers before touching host-wide scheduling.
                foreach (var change in changes.Where(item => item.Backup != null))
                    store.Value.TriggerBackups.Add(change.Backup);
                store.Save();
                var applied = new List<TriggerChange>();
                try
                {
                    foreach (var change in changes)
                    {
                        applied.Add(change);
                        change.Worker.Triggers = change.Next;
                    }
                    var previous = store.Value.Schedule;
                    var previousBackups = store.Value.TriggerBackups;
                    store.Value.TriggerBackups = previousBackups.Where(backup => !changes.Any(change =>
                        change.RestoredKey == backup.Key)).ToList();
                    settings.AppliedUtc = DateTime.UtcNow;
                    store.Value.Schedule = settings;
                    try { store.Save(); }
                    catch
                    {
                        store.Value.Schedule = previous;
                        store.Value.TriggerBackups = previousBackups;
                        throw;
                    }
                }
                catch (Exception error)
                {
                    var errors = new List<Exception> { error };
                    foreach (var change in applied.AsEnumerable().Reverse())
                    {
                        try { change.Worker.Triggers = change.Previous; }
                        catch (Exception rollback) { errors.Add(rollback); }
                    }
                    throw new AggregateException("TuneIn schedule changes failed; original triggers were restored where possible.", errors);
                }
                return Read();
            }
        }

        private IScheduledTaskWorker Worker(Func<IScheduledTask, bool> predicate)
        {
            var matches = manager.ScheduledTasks.Where(item => predicate(item.ScheduledTask)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("The required Emby scheduled task is unavailable or ambiguous. No schedule was applied.");
            return matches[0];
        }

        private TriggerChange Change(IScheduledTaskWorker worker, bool manage, TaskTriggerInfo[] next, string key = null)
        {
            key = key ?? worker.ScheduledTask.Key;
            var backup = store.Value.TriggerBackups.SingleOrDefault(item => item.Key == key);
            return new TriggerChange
            {
                Worker = worker,
                Previous = worker.Triggers,
                Next = manage ? next : backup.Triggers,
                RestoredKey = manage ? null : key,
                Backup = manage && backup == null ? new TuneInTriggerBackup { Key = key, Triggers = worker.Triggers } : null
            };
        }

        private static void Validate(TuneInScheduleSettings settings)
        {
            if (settings == null || settings.GlobalIntervalMinutes < 0 || settings.GlobalIntervalMinutes > 525600
                || settings.Overrides == null || settings.Overrides.Count > 8
                || settings.Overrides.Any(item => item == null || !TuneInRefresh.Categories.Contains(item.Category)
                    || !new[] { "inherit", "manual", "custom" }.Contains(item.Mode)
                    || item.Minutes < 0 || item.Minutes > 525600 || item.Mode == "custom" && item.Minutes == 0)
                || settings.Overrides.Select(item => item.Category).Distinct().Count() != settings.Overrides.Count)
                throw new ArgumentException("Refresh intervals must be whole minutes from 1 to 525600, or 0 for manual; category overrides must be unique.");
        }

        private sealed class TriggerChange
        {
            internal IScheduledTaskWorker Worker { get; set; }
            internal TaskTriggerInfo[] Previous { get; set; }
            internal TaskTriggerInfo[] Next { get; set; }
            internal TuneInTriggerBackup Backup { get; set; }
            internal string RestoredKey { get; set; }
        }
    }
}
