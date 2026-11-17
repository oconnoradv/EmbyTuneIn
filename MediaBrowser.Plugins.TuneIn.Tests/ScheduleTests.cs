using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ScheduleTests
    {
        private static (ITaskManager Manager, WorkerProxy Worker, TuneInRefreshStateStore Store) Fixture(
            bool disabled = true, string taskKey = "RefreshInternetChannels")
        {
            var worker = DispatchProxy.Create<IScheduledTaskWorker, WorkerProxy>();
            var proxy = (WorkerProxy)worker;
            proxy.Task = new NativeRefreshTask(taskKey);
            proxy.Value = Array.Empty<TaskTriggerInfo>();
            var manager = DispatchProxy.Create<ITaskManager, ManagerProxy>();
            ((ManagerProxy)manager).Workers = new[] { worker };
            var store = new TuneInRefreshStateStore();
            store.Value.Schedule.DisableGlobalRefreshTrigger = disabled;
            return (manager, proxy, store);
        }

        [Fact]
        public void MigrationRestoresSavedWeeklyTriggerOnceWithoutExecutingTheTask()
        {
            var fixture = Fixture();
            var saved = new[] { new TaskTriggerInfo { Type = "WeeklyTrigger", DayOfWeek = DayOfWeek.Friday,
                TimeOfDayTicks = TimeSpan.FromHours(5).Ticks } };
            fixture.Store.Value.TriggerBackups.Add(new TuneInTriggerBackup { Key = "EmbyGlobalChannels", Triggers = saved });
            var migration = new TuneInScheduleMigration(fixture.Manager, fixture.Store);
            migration.Run();
            Assert.Same(saved, fixture.Worker.Value);
            Assert.False(fixture.Store.Value.Schedule.DisableGlobalRefreshTrigger);
            Assert.Empty(fixture.Store.Value.TriggerBackups);
            migration.Run();
            Assert.Equal(1, fixture.Worker.Writes);
        }

        [Fact]
        public void MigrationUsesNativeDefaultsOnlyWhenLegacySuppressionIsRecorded()
        {
            var fixture = Fixture(false);
            var migration = new TuneInScheduleMigration(fixture.Manager, fixture.Store);
            migration.Run();
            Assert.Equal(0, fixture.Worker.Writes);
            fixture.Store.Value.Schedule.DisableGlobalRefreshTrigger = true;
            migration.Run();
            Assert.Equal("WeeklyTrigger", Assert.Single(fixture.Worker.Value).Type);
        }

        [Fact]
        public void AdministratorScheduleEditsAreNotOverwritten()
        {
            var fixture = Fixture();
            var edited = new[] { new TaskTriggerInfo { Type = "DailyTrigger" } };
            fixture.Worker.Value = edited;
            fixture.Store.Value.TriggerBackups.Add(new TuneInTriggerBackup
            { Key = "EmbyGlobalChannels", Triggers = new[] { new TaskTriggerInfo { Type = "WeeklyTrigger" } } });
            new TuneInScheduleMigration(fixture.Manager, fixture.Store).Run();
            Assert.Same(edited, fixture.Worker.Value);
            Assert.Equal(0, fixture.Worker.Writes);
            Assert.False(fixture.Store.Value.Schedule.DisableGlobalRefreshTrigger);
        }

        [Fact]
        public void SavedBackupIsSufficientProvenanceAndAnOriginallyEmptyScheduleStaysEmpty()
        {
            var fixture = Fixture(false);
            fixture.Store.Value.TriggerBackups.Add(new TuneInTriggerBackup
            { Key = "EmbyGlobalChannels", Triggers = Array.Empty<TaskTriggerInfo>() });
            new TuneInScheduleMigration(fixture.Manager, fixture.Store).Run();
            Assert.Empty(fixture.Worker.Value);
            Assert.Equal(1, fixture.Worker.Writes);
            Assert.Empty(fixture.Store.Value.TriggerBackups);
        }

        [Fact]
        public void IncompleteBackupAndAmbiguousWorkerDoNotFallBackToInventedSchedules()
        {
            var fixture = Fixture();
            fixture.Store.Value.TriggerBackups.Add(new TuneInTriggerBackup { Key = "EmbyGlobalChannels" });
            Assert.Throws<InvalidOperationException>(() => new TuneInScheduleMigration(fixture.Manager, fixture.Store).Run());
            Assert.Equal(0, fixture.Worker.Writes);
            Assert.True(fixture.Store.Value.Schedule.DisableGlobalRefreshTrigger);
            Assert.Single(fixture.Store.Value.TriggerBackups);
            var workers = ((ManagerProxy)fixture.Manager).Workers;
            ((ManagerProxy)fixture.Manager).Workers = new[] { workers[0], workers[0] };
            Assert.Throws<InvalidOperationException>(() => new TuneInScheduleMigration(fixture.Manager, fixture.Store).Run());
            Assert.Throws<InvalidOperationException>(() => new TuneInChannelRefresh(fixture.Manager).Status());
            Assert.Equal(0, fixture.Worker.Writes);
        }

        [Fact]
        public void TriggerAndRollbackErrorsAreBothRetained()
        {
            var fixture = Fixture();
            fixture.Worker.FailAllWrites = true;
            var error = Assert.Throws<AggregateException>(() => new TuneInScheduleMigration(fixture.Manager, fixture.Store).Run());
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.All(error.InnerExceptions, failure => Assert.IsType<IOException>(failure));
            Assert.True(fixture.Store.Value.Schedule.DisableGlobalRefreshTrigger);
        }

        [Fact]
        public void MissingTaskAndTriggerWriteFailuresRetainLegacyBackupForRetry()
        {
            var fixture = Fixture();
            fixture.Worker.FailNextWrite = true;
            Assert.Throws<IOException>(() => new TuneInScheduleMigration(fixture.Manager, fixture.Store).Run());
            Assert.True(fixture.Store.Value.Schedule.DisableGlobalRefreshTrigger);
            Assert.Empty(fixture.Worker.Value);
            ((ManagerProxy)fixture.Manager).Workers = Array.Empty<IScheduledTaskWorker>();
            Assert.Throws<InvalidOperationException>(() => new TuneInScheduleMigration(fixture.Manager, fixture.Store).Run());
            Assert.True(fixture.Store.Value.Schedule.DisableGlobalRefreshTrigger);
        }

        [Fact]
        public void StatePersistenceFailureRollsBackHostScheduleAndAllowsRetry()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tunein-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var fixture = Fixture();
                var store = new TuneInRefreshStateStore(directory);
                store.Value.Schedule.DisableGlobalRefreshTrigger = true;
                Assert.Throws<IOException>(() => new TuneInScheduleMigration(fixture.Manager, store).Run());
                Assert.True(store.Value.Schedule.DisableGlobalRefreshTrigger);
                Assert.Empty(fixture.Worker.Value);
                Assert.False(File.Exists(directory + ".tmp"));
            }
            finally { Directory.Delete(directory); }
        }

        [Fact]
        public void MigrationPersistsCompletionAndRetainsHistoricalGroupData()
        {
            var directory = Path.Combine(Path.GetTempPath(), "tunein-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var fixture = Fixture();
                var path = Path.Combine(directory, "state.xml");
                var store = new TuneInRefreshStateStore(path);
                store.Value.Schedule.DisableGlobalRefreshTrigger = true;
                store.Record("music", "Failed", DateTime.UtcNow);
                new TuneInScheduleMigration(fixture.Manager, store).Run();
                var restarted = new TuneInRefreshStateStore(path);
                Assert.False(restarted.Value.Schedule.DisableGlobalRefreshTrigger);
                Assert.Equal("Failed", restarted.History("music").LastAttemptState);
                new TuneInScheduleMigration(fixture.Manager, restarted).Run();
                Assert.Equal(1, fixture.Worker.Writes);
            }
            finally { File.Delete(Path.Combine(directory, "state.xml")); Directory.Delete(directory); }
        }

        [Fact]
        public void MaximumValidTimeoutCheckpointCanBePersistedAndReloaded()
        {
            var path = Path.Combine(Environment.CurrentDirectory, "tunein-state-bound-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var pages = new List<TuneInRefreshPageCheckpoint>();
                using (var hash = System.Security.Cryptography.SHA256.Create())
                {
                    for (var index = 0; index < 4; index++)
                    {
                        var key = ":" + Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(index.ToString())));
                        pages.Add(new TuneInRefreshPageCheckpoint { Key = key, Bytes = new byte[2 * 1024 * 1024] });
                    }
                }
                var completedFolders = new List<string>();
                for (var index = 0; index < 1000; index++)
                    completedFolders.Add(index.ToString("D4") + new string('x', 5996));

                var store = new TuneInRefreshStateStore(path);
                store.RecordTimeoutCheckpoint("talk", "attempt", new string('a', 64), 60, false,
                    DateTime.UtcNow, pages: pages, completedImportFolders: completedFolders);

                var length = new FileInfo(path).Length;
                Assert.InRange(length, 12L * 1024 * 1024 + 1, 24L * 1024 * 1024);
                var reloaded = new TuneInRefreshStateStore(path).TimeoutCheckpoint;
                Assert.Equal(4, reloaded.Pages.Count);
                Assert.Equal(1000, reloaded.CompletedImportFolders.Count);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
        }

        [Fact]
        public void NativeStatusAndFailedReportAreReadOnlyBoundedAndAttemptSpecific()
        {
            var fixture = Fixture(taskKey: RefreshTuneInTask.TaskKey);
            var diagnostics = new TuneInChannelRefresh(fixture.Manager);
            Assert.Equal("NeverRun", diagnostics.Status().LastResult);
            Assert.Null(diagnostics.Status().FailedAttempt);
            Assert.Throws<InvalidOperationException>(() => diagnostics.Failure("old"));
            fixture.Worker.State = TaskState.Running;
            fixture.Worker.Progress = 35;
            fixture.Worker.LastResult = new TaskResult
            {
                Status = TaskCompletionStatus.Failed,
                StartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                EndTimeUtc = DateTimeOffset.UtcNow,
                ErrorMessage = "Provider failed",
                LongErrorMessage = "<script>Not HTML</script>\n" + new string('x', 100000)
            };
            var status = diagnostics.Status();
            Assert.Equal("Running", status.State);
            Assert.Equal(35d, status.Progress);
            Assert.Equal("Failed", status.LastResult);
            var report = diagnostics.Failure(status.FailedAttempt);
            Assert.Contains("Provider failed", report.Text);
            Assert.Contains("<script>Not HTML</script>", report.Text);
            Assert.True(report.Truncated);
            Assert.True(report.Text.Length <= 64 * 1024);
            fixture.Worker.LastResult.EndTimeUtc = fixture.Worker.LastResult.EndTimeUtc.AddSeconds(1);
            Assert.Throws<InvalidOperationException>(() => diagnostics.Failure(status.FailedAttempt));
            fixture.Worker.LastResult.Status = TaskCompletionStatus.Completed;
            Assert.Throws<InvalidOperationException>(() => diagnostics.Failure(diagnostics.Status().FailedAttempt));
            Assert.Equal(0, fixture.Worker.Writes);
        }

        [Fact]
        public void FailureWithoutDetailsIsExplicitAndMissingTaskIsNotReportedAsSuccess()
        {
            var fixture = Fixture(taskKey: RefreshTuneInTask.TaskKey);
            fixture.Worker.LastResult = new TaskResult { Status = TaskCompletionStatus.Failed };
            var diagnostics = new TuneInChannelRefresh(fixture.Manager);
            Assert.Contains("without error details", diagnostics.Failure(diagnostics.Status().FailedAttempt).Text);
            ((ManagerProxy)fixture.Manager).Workers = Array.Empty<IScheduledTaskWorker>();
            Assert.Throws<InvalidOperationException>(() => diagnostics.Status());
        }

        [Fact]
        public void OversizedShortAndLongErrorsRemainBoundedWithoutDroppingTruncationNotice()
        {
            var fixture = Fixture(taskKey: RefreshTuneInTask.TaskKey);
            fixture.Worker.LastResult = new TaskResult
            {
                Status = TaskCompletionStatus.Failed,
                ErrorMessage = new string('s', 100000),
                LongErrorMessage = new string('l', 100000)
            };
            var diagnostics = new TuneInChannelRefresh(fixture.Manager);
            var report = diagnostics.Failure(diagnostics.Status().FailedAttempt);
            Assert.Equal(64 * 1024, report.Text.Length);
            Assert.True(report.Truncated);
            Assert.Throws<InvalidOperationException>(() => diagnostics.Failure(null));
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
            public bool FailAllWrites { get; set; }
            public TaskResult LastResult { get; set; }
            public TaskState State { get; set; }
            public double? Progress { get; set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_ScheduledTask") return Task;
                if (method.Name == "get_LastExecutionResult") return LastResult;
                if (method.Name == "get_State") return State;
                if (method.Name == "get_CurrentProgress") return Progress;
                if (method.Name == "get_Triggers") return Value;
                if (method.Name == "set_Triggers")
                {
                    Writes++;
                    if (FailNextWrite || FailAllWrites) { FailNextWrite = false; throw new IOException("Synthetic trigger write failure"); }
                    Value = (TaskTriggerInfo[])args[0];
                    return null;
                }
                throw new InvalidOperationException(method.Name);
            }
        }

        private sealed class NativeRefreshTask : IScheduledTask
        {
            private readonly string key;
            public NativeRefreshTask(string key) { this.key = key; }
            public string Key => key;
            public string Name => key == RefreshTuneInTask.TaskKey ? "Refresh TuneIn" : "Refresh Internet Channels";
            public string Description => Name;
            public string Category => "Internet Channels";
            public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[] { new TaskTriggerInfo { Type = "WeeklyTrigger" } };
            public Task Execute(CancellationToken token, IProgress<double> progress) =>
                throw new InvalidOperationException("Status and migration must never start a refresh.");
        }
    }
}
