using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class BackupTests
    {
        [Fact]
        public async Task StartsOnlyTheDocumentedBackupTaskAndWaitsForANewResult()
        {
            var backup = Worker("Emby Server Backup");
            backup.LastResult = new TaskResult { Status = TaskCompletionStatus.Completed, EndTimeUtc = DateTime.UtcNow.AddDays(-1) };
            var manager = Manager(Worker("Backup another plugin"), backup);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Run = async () =>
            {
                await gate.Task;
                backup.LastResult = new TaskResult { Status = TaskCompletionStatus.Failed, EndTimeUtc = DateTime.UtcNow };
            };
            var job = new TuneInBackup((ITaskManager)(object)manager, null);
            Assert.Equal("Running", job.Start().State);
            Assert.Equal("Running", job.Start().State);
            Assert.Equal("Running", job.Status().State);
            gate.SetResult(true);
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => job.Status().State != "Running", TimeSpan.FromSeconds(5))));
            Assert.Equal("Failed", job.Status().State);
            Assert.Same(backup, Assert.Single(manager.Started));
        }

        [Fact]
        public void RejectsMissingAmbiguousAndRunningTasks()
        {
            var exact = Worker("Emby Server Backup");
            var busy = Worker("Emby Server Backup");
            busy.State = TaskState.Running;
            foreach (var test in new[]
            {
                (Expected: "Unavailable", Workers: new[] { Worker("Emby Server Backup Test") }),
                (Expected: "Unavailable", Workers: new[] { exact, Worker("Emby Server Backup") }),
                (Expected: "Busy", Workers: new[] { busy })
            })
            {
                var manager = Manager(test.Workers);
                var job = new TuneInBackup((ITaskManager)(object)manager, null);
                Assert.Equal(test.Expected, job.Start().State);
                Assert.Empty(manager.Started);
            }
            Assert.Equal("Admin", typeof(GetTuneInBackupStatus).GetCustomAttribute<AuthenticatedAttribute>().Roles);
            Assert.Equal("Admin", typeof(StartTuneInBackup).GetCustomAttribute<AuthenticatedAttribute>().Roles);
        }

        [Fact]
        public void ThirtyDayScheduleIsAcceptedButNearbyUnsupportedIntervalsAreNot()
        {
            var config = new PluginConfiguration();
            var schedule = new CategoryRefreshSchedule { Category = "talk", IntervalHours = 720 };
            config.RefreshSchedules.Add(schedule);
            ConfigurationSnapshot.Validate(config);
            schedule.IntervalHours = 721;
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
        }

        private static TaskWorker Worker(string name)
        {
            var identity = (BackupIdentity)(object)DispatchProxy.Create<IScheduledTask, BackupIdentity>();
            identity.Name = name;
            var worker = (TaskWorker)(object)DispatchProxy.Create<IScheduledTaskWorker, TaskWorker>();
            worker.Identity = (IScheduledTask)(object)identity;
            return worker;
        }

        private static TaskManager Manager(params TaskWorker[] workers)
        {
            var manager = (TaskManager)(object)DispatchProxy.Create<ITaskManager, TaskManager>();
            manager.Workers = workers.Select(worker => (IScheduledTaskWorker)(object)worker).ToArray();
            return manager;
        }

        public class BackupIdentity : DispatchProxy
        {
            internal string Name;
            protected override object Invoke(MethodInfo method, object[] args) =>
                method.Name == "get_Name" ? Name : throw new NotSupportedException(method.Name);
        }

        public class TaskWorker : DispatchProxy
        {
            internal IScheduledTask Identity;
            internal TaskState State;
            internal TaskResult LastResult;
            protected override object Invoke(MethodInfo method, object[] args) => method.Name switch
            {
                "get_ScheduledTask" => Identity,
                "get_LastExecutionResult" => LastResult,
                "get_State" => State,
                _ => throw new NotSupportedException(method.Name)
            };
        }

        public class TaskManager : DispatchProxy
        {
            internal IScheduledTaskWorker[] Workers;
            internal readonly System.Collections.Generic.List<object> Started = new System.Collections.Generic.List<object>();
            internal Func<Task> Run;
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_ScheduledTasks") return Workers;
                if (method.Name != "Execute") throw new NotSupportedException(method.Name);
                Started.Add(args[0]);
                return Run();
            }
        }
    }
}
