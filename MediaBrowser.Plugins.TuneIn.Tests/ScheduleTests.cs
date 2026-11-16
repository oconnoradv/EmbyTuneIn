using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ScheduleTests
    {
        private static (ITaskManager Manager, WorkerProxy[] Workers) Manager()
        {
            var workers = TuneInRefresh.Categories.Select(category => Worker(new FakeTask("TuneInForkRefresh_" + category)))
                .Concat(new[] { Worker(new RefreshChannelsScheduledTask()) }).ToArray();
            var manager = DispatchProxy.Create<ITaskManager, ManagerProxy>();
            ((ManagerProxy)manager).Workers = workers.Select(item => item.Worker).ToArray();
            return (manager, workers.Select(item => item.Proxy).ToArray());
        }

        private static (IScheduledTaskWorker Worker, WorkerProxy Proxy) Worker(IScheduledTask task)
        {
            var worker = DispatchProxy.Create<IScheduledTaskWorker, WorkerProxy>();
            var proxy = (WorkerProxy)worker;
            proxy.Task = task;
            proxy.Value = new[] { new TaskTriggerInfo { Type = "DailyTrigger", TimeOfDayTicks = TimeSpan.FromHours(2).Ticks } };
            return (worker, proxy);
        }

        [Fact]
        public void SharedIntervalsOverridesAndGlobalDisableRestoreOriginalTriggers()
        {
            var fixture = Manager();
            var store = new TuneInRefreshStateStore();
            var schedules = new TuneInSchedules(fixture.Manager, store);
            Assert.False(schedules.Read().ManageIntervals);
            Assert.All(fixture.Workers, worker => Assert.Equal(0, worker.Writes));
            var settings = new SetTuneInSchedule
            {
                ManageIntervals = true,
                GlobalIntervalMinutes = 120,
                DisableGlobalRefreshTrigger = true,
                Overrides = new List<RadioGroupInterval>
                {
                    new RadioGroupInterval { Category = "local", Mode = "custom", Minutes = 30 },
                    new RadioGroupInterval { Category = "talk", Mode = "manual" }
                }
            };
            var applied = schedules.Apply(settings);
            Assert.NotNull(applied.AppliedUtc);
            Assert.Equal(TimeSpan.FromMinutes(30).Ticks, Assert.Single(fixture.Workers[0].Value).IntervalTicks);
            Assert.Equal(TimeSpan.FromMinutes(120).Ticks, Assert.Single(fixture.Workers[1].Value).IntervalTicks);
            Assert.Empty(fixture.Workers[4].Value);
            Assert.Empty(fixture.Workers[8].Value);
            schedules.Apply(settings);
            Assert.Equal(9, store.Value.TriggerBackups.Count);
            schedules.Apply(new TuneInScheduleSettings());
            Assert.All(fixture.Workers, worker => Assert.Equal("DailyTrigger", Assert.Single(worker.Value).Type));
            Assert.Empty(store.Value.TriggerBackups);
            var writes = fixture.Workers.Sum(worker => worker.Writes);
            schedules.Apply(new TuneInScheduleSettings());
            Assert.Equal(writes, fixture.Workers.Sum(worker => worker.Writes));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(525601)]
        public void InvalidIntervalsCannotTouchTriggers(int minutes)
        {
            var fixture = Manager();
            var schedules = new TuneInSchedules(fixture.Manager, new TuneInRefreshStateStore());
            Assert.Throws<ArgumentException>(() => schedules.Apply(new TuneInScheduleSettings
            { ManageIntervals = true, GlobalIntervalMinutes = minutes }));
            Assert.All(fixture.Workers, worker => Assert.Equal(0, worker.Writes));
        }

        [Fact]
        public void InvalidOverridesAndMissingGlobalTaskFailBeforeChangingAnyTriggers()
        {
            var fixture = Manager();
            var schedules = new TuneInSchedules(fixture.Manager, new TuneInRefreshStateStore());
            Assert.Throws<ArgumentException>(() => schedules.Apply(new TuneInScheduleSettings
            { Overrides = new List<RadioGroupInterval> { new RadioGroupInterval { Category = "talk", Mode = "custom", Minutes = 0 } } }));
            Assert.Throws<ArgumentException>(() => schedules.Apply(new TuneInScheduleSettings
            { Overrides = new List<RadioGroupInterval> { new RadioGroupInterval { Category = "unknown" } } }));
            ((ManagerProxy)fixture.Manager).Workers = ((ManagerProxy)fixture.Manager).Workers.Take(8).ToArray();
            Assert.Throws<InvalidOperationException>(() => schedules.Apply(new TuneInScheduleSettings
            { ManageIntervals = true, GlobalIntervalMinutes = 30, DisableGlobalRefreshTrigger = true }));
            Assert.All(fixture.Workers, worker => Assert.Equal(0, worker.Writes));
        }

        [Fact]
        public void TriggerFailureRollsBackAlreadyChangedWorkersAndDoesNotSaveSuccess()
        {
            var fixture = Manager();
            fixture.Workers[2].FailNextWrite = true;
            var schedules = new TuneInSchedules(fixture.Manager, new TuneInRefreshStateStore());
            Assert.Throws<AggregateException>(() => schedules.Apply(new TuneInScheduleSettings
            { ManageIntervals = true, GlobalIntervalMinutes = 30 }));
            Assert.Null(schedules.Read().AppliedUtc);
            Assert.All(fixture.Workers, worker => Assert.Equal("DailyTrigger", Assert.Single(worker.Value).Type));
        }

        [Fact]
        public void BackupPersistenceFailureCannotChangeHostTriggers()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tunein-backup-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var fixture = Manager();
                var schedules = new TuneInSchedules(fixture.Manager, new TuneInRefreshStateStore(directory));
                Assert.Throws<IOException>(() => schedules.Apply(new TuneInScheduleSettings
                { ManageIntervals = true, GlobalIntervalMinutes = 60, DisableGlobalRefreshTrigger = true }));
                Assert.All(fixture.Workers, worker => Assert.Equal(0, worker.Writes));
                Assert.Null(schedules.Read().AppliedUtc);
                Assert.False(File.Exists(directory + ".tmp"));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void ScheduleBackupsAndRefreshHistorySurviveRestartOutsideConfiguration()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tunein-schedules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "state.xml");
                var fixture = Manager();
                var store = new TuneInRefreshStateStore(path);
                var schedules = new TuneInSchedules(fixture.Manager, store);
                schedules.Apply(new SetTuneInSchedule { DisableGlobalRefreshTrigger = true });
                var success = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                store.Record("talk", "Completed", success);
                store.Record("talk", "Failed", success.AddMinutes(1));
                var restarted = new TuneInRefreshStateStore(path);
                var history = restarted.History("talk");
                Assert.Equal(success, history.LastSuccessfulUtc);
                Assert.Equal(success.AddMinutes(1), history.LastAttemptUtc);
                Assert.Equal("Failed", history.LastAttemptState);
                Assert.True(new TuneInSchedules(fixture.Manager, restarted).Read().DisableGlobalRefreshTrigger);
                new TuneInSchedules(fixture.Manager, restarted).Apply(new TuneInScheduleSettings());
                Assert.Equal("DailyTrigger", Assert.Single(fixture.Workers[8].Value).Type);
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally { Directory.Delete(directory, true); }
        }

        public class ManagerProxy : DispatchProxy
        {
            public IScheduledTaskWorker[] Workers { get; set; }
            protected override object Invoke(MethodInfo method, object[] args) =>
                method.Name == "get_ScheduledTasks" ? Workers : throw new InvalidOperationException(method.Name);
        }

        public class WorkerProxy : DispatchProxy
        {
            public IScheduledTask Task { get; set; }
            public TaskTriggerInfo[] Value { get; set; }
            public int Writes { get; set; }
            public bool FailNextWrite { get; set; }
            public TaskResult LastResult { get; set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_ScheduledTask") return Task;
                if (method.Name == "get_LastExecutionResult") return LastResult;
                if (method.Name == "get_Triggers") return Value;
                if (method.Name == "set_Triggers")
                {
                    Writes++;
                    if (FailNextWrite) { FailNextWrite = false; throw new IOException("Synthetic trigger write failure"); }
                    Value = (TaskTriggerInfo[])args[0];
                    return null;
                }
                throw new InvalidOperationException(method.Name);
            }
        }

        private class FakeTask : IScheduledTask
        {
            internal FakeTask(string key) { Key = key; }
            public string Key { get; }
            public string Name => Key;
            public string Description => Key;
            public string Category => "Internet Channels";
            public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
            public Task Execute(CancellationToken token, IProgress<double> progress) => Task.CompletedTask;
        }

        private sealed class RefreshChannelsScheduledTask : FakeTask
        {
            internal RefreshChannelsScheduledTask() : base("RefreshInternetChannels") { }
        }
    }
}
