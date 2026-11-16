using MediaBrowser.Model.Tasks;
using System;
using System.Globalization;
using System.Linq;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class ChannelRefreshStatus
    {
        public string State { get; set; }
        public double? Progress { get; set; }
        public string LastResult { get; set; }
        public DateTimeOffset? StartedUtc { get; set; }
        public DateTimeOffset? FinishedUtc { get; set; }
        public string FailedAttempt { get; set; }
        public ConfiguredRefreshStatus Current { get; set; }
    }

    public sealed class ChannelRefreshFailure
    {
        public string Attempt { get; set; }
        public string Text { get; set; }
        public bool Truncated { get; set; }
    }

    internal sealed class TuneInChannelRefresh
    {
        private const int ReportLimit = 64 * 1024;
        private readonly ITaskManager manager;
        private readonly TuneInRefresh refresh;
        internal TuneInChannelRefresh(ITaskManager manager, TuneInRefresh refresh = null)
        {
            this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
            this.refresh = refresh ?? TuneInRefresh.Instance;
        }

        private IScheduledTaskWorker Worker()
        {
            var matches = manager.ScheduledTasks.Where(worker => worker.ScheduledTask.Key == RefreshTuneInTask.TaskKey).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("The Refresh TuneIn scheduled task is unavailable or ambiguous.");
            return matches[0];
        }

        public ChannelRefreshStatus Status()
        {
            var worker = Worker();
            var result = worker.LastExecutionResult;
            var state = worker.State.ToString();
            return new ChannelRefreshStatus
            {
                State = state,
                Progress = worker.CurrentProgress,
                LastResult = result?.Status.ToString() ?? "NeverRun",
                StartedUtc = result?.StartTimeUtc,
                FinishedUtc = result?.EndTimeUtc,
                FailedAttempt = result?.Status == TaskCompletionStatus.Failed ? Attempt(result) : null,
                Current = state == "Running" || state == "Cancelling" ? refresh.CurrentConfiguredStatus() : null
            };
        }

        private static string Attempt(TaskResult result) =>
            result.StartTimeUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) + "-"
                + result.EndTimeUtc.UtcTicks.ToString(CultureInfo.InvariantCulture);

        public ChannelRefreshFailure Failure(string attempt)
        {
            var result = Worker().LastExecutionResult;
            if (result == null || result.Status != TaskCompletionStatus.Failed)
                throw new InvalidOperationException("No failed Refresh TuneIn task report is available. Update the status first.");
            if (attempt != Attempt(result))
                throw new InvalidOperationException("The Refresh TuneIn task result changed. Update the status before retrieving its report.");
            var text = "Refresh TuneIn - Failed\nStarted (UTC): " + result.StartTimeUtc.UtcDateTime.ToString("u")
                + "\nFinished (UTC): " + result.EndTimeUtc.UtcDateTime.ToString("u") + "\n\n";
            var shortError = result.ErrorMessage;
            var longError = result.LongErrorMessage;
            var truncated = false;
            foreach (var error in new[] { shortError, longError })
            {
                if (string.IsNullOrEmpty(error)) continue;
                var available = ReportLimit - text.Length;
                text += error.Substring(0, Math.Min(available, error.Length));
                if (error.Length > available) truncated = true;
                if (text.Length < ReportLimit) text += "\n";
            }
            if (string.IsNullOrEmpty(shortError) && string.IsNullOrEmpty(longError))
                text += "Emby recorded a Refresh TuneIn failure without error details. Consult the server log for this time range.";
            return new ChannelRefreshFailure { Attempt = attempt, Text = text, Truncated = truncated };
        }
    }
}
