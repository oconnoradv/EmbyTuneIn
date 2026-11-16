using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class RadioGroupRefreshStatus
    {
        public string Category { get; set; }
        public string Id { get; set; }
        public bool Enabled { get; set; }
        public string State { get; set; } = "Idle";
        public string Phase { get; set; }
        public int TotalRequests { get; set; }
        public int CompletedRequests { get; set; }
        public int ApiPercent => TotalRequests == 0 ? 0 : 100 * CompletedRequests / TotalRequests;
        public int StationRows { get; set; }
        public int FolderRows { get; set; }
        public int? LastHttpStatus { get; set; }
        public string LastApiResult { get; set; }
        public string Error { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        internal RadioGroupRefreshStatus Copy() => (RadioGroupRefreshStatus)MemberwiseClone();
    }

    internal sealed class TuneInRefresh
    {
        internal static readonly string[] Categories = { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" };
        public static TuneInRefresh Instance { get; } = new TuneInRefresh(TuneInDirectory.Instance);
        private readonly object gate = new object();
        private readonly TuneInDirectory directory;
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();

        internal TuneInRefresh(TuneInDirectory directory) { this.directory = directory; }

        public RadioGroupRefreshStatus[] Status()
        {
            var snapshot = directory.Snapshot();
            lock (gate)
                return Categories.Select(category =>
                {
                    var status = jobs.TryGetValue(category, out var job) ? job.Status.Copy()
                        : new RadioGroupRefreshStatus { Category = category };
                    status.Enabled = snapshot.Enabled(category);
                    return status;
                }).ToArray();
        }

        public RadioGroupRefreshStatus Start(string category,
            Func<string, int, CancellationToken, Task> synchronize, ILogger logger = null)
        {
            if (!Categories.Contains(category)) throw new ArgumentException("Unknown radio group.");
            var snapshot = directory.Snapshot();
            var entries = TuneInChannel.StartingEntries(snapshot, category);
            lock (gate)
            {
                if (jobs.TryGetValue(category, out var previous) && Active(previous.Status.State))
                    return previous.Status.Copy();
                var job = new Job
                {
                    Status = new RadioGroupRefreshStatus
                    {
                        Category = category,
                        Id = Guid.NewGuid().ToString("N"),
                        Enabled = true,
                        State = "Queued",
                        Phase = "Waiting to refresh saved starting pages",
                        TotalRequests = entries.Count + (category == "language" ? 1 : 0),
                        StartedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    }
                };
                jobs[category] = job;
                job.Completion = Task.Run(() => RunAsync(job, snapshot, entries, synchronize, logger));
                return job.Status.Copy();
            }
        }

        public RadioGroupRefreshStatus Cancel(string category, string id)
        {
            lock (gate)
            {
                if (category == null || !jobs.TryGetValue(category, out var job) || job.Status.Id != id)
                    throw new ArgumentException("Refresh is no longer current.");
                if (Active(job.Status.State)) { job.CancelledByAdmin = true; job.Cancellation.Cancel(); }
                return job.Status.Copy();
            }
        }

        internal Task Completion(string category)
        {
            lock (gate) return jobs[category].Completion;
        }

        private static bool Active(string state) => state == "Queued" || state == "Running" || state == "Synchronizing";

        private void Update(Job job, Action<RadioGroupRefreshStatus> update)
        {
            lock (gate) { update(job.Status); job.Status.UpdatedUtc = DateTime.UtcNow; }
        }

        private async Task RunAsync(Job job, ConfigurationSnapshot snapshot, List<NavigationEntry> entries,
            Func<string, int, CancellationToken, Task> synchronize, ILogger logger)
        {
            try
            {
                job.Cancellation.CancelAfter(TimeSpan.FromMinutes(20));
                using (var operation = directory.Operation(snapshot, job.Cancellation.Token))
                {
                    var category = job.Status.Category;
                    directory.InvalidateCategory(category);
                    Action<int> received = status => Update(job, value => value.LastHttpStatus = status);
                    if (category == "language")
                    {
                        BeginRequest(job, "Validating selected languages");
                        var languages = await directory.SelectAsync("language", null, null, true, operation.Token, received).ConfigureAwait(false);
                        Update(job, status => { status.CompletedRequests++; status.LastApiResult = "Validated language catalogue"; });
                        if (entries.Any(entry => !languages.Items.Any(item => item.Id == entry.SelectionId)))
                            throw new InvalidOperationException("A selected language is no longer available.");
                    }
                    foreach (var entry in entries)
                    {
                        BeginRequest(job, "Fetching saved starting page");
                        var request = TuneInChannel.CatalogueRequest(snapshot, entry);
                        var document = await directory.FetchCategoryAsync(snapshot, category, request,
                            operation.Token, true, received).ConfigureAwait(false);
                        Update(job, status =>
                        {
                            status.CompletedRequests++;
                            status.LastApiResult = "Validated catalogue";
                            status.StationRows += document.Descendants("outline").Count(row => TuneInDirectory.Attribute(row, "type") == "audio");
                            status.FolderRows += document.Descendants("outline").Count(row => TuneInDirectory.Attribute(row, "type") == "link");
                        });
                    }
                    operation.Token.ThrowIfCancellationRequested();
                    Update(job, status => { status.State = "Synchronizing"; status.Phase = "API complete; synchronizing Emby (no host progress percentage available)"; });
                    var folderId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category));
                    var depth = category == "music" || category == "language" || category == "location" ? 3 : 2;
                    await directory.SynchronizeCategoryAsync(category, entries.Count,
                        () => synchronize(folderId, depth, operation.Token)).ConfigureAwait(false);
                    operation.Token.ThrowIfCancellationRequested();
                    Update(job, status => { status.State = "Completed"; status.Phase = "Saved starting pages refreshed; Emby synchronization completed"; });
                }
            }
            catch (OperationCanceledException)
            {
                Update(job, status =>
                {
                    status.State = "Cancelled";
                    status.Error = job.CancelledByAdmin ? "Cancelled by administrator."
                        : job.Cancellation.IsCancellationRequested ? "Refresh exceeded its 20-minute time limit."
                        : "Configuration changed, a request was cancelled, or the API request timed out.";
                });
            }
            catch (Exception error)
            {
                Update(job, status =>
                {
                    status.Error = status.State == "Synchronizing"
                        ? "API pages were refreshed, but Emby did not complete bounded synchronization. Check server logs and reopen the channel root."
                        : error is InvalidDataException || error is System.Xml.XmlException
                            ? "Provider response was rejected: HTTP failure, API fault, or malformed/unsafe catalogue."
                            : "Refresh failed. Check saved selections, API connectivity, cache capacity and server logs.";
                    status.State = "Failed";
                    status.LastApiResult = status.LastApiResult == "Waiting for API response" ? "Request failed" : status.LastApiResult;
                });
                logger?.Error("TuneIn group refresh failed in {0} ({1}).", job.Status.Phase, error.GetType().Name);
            }
            finally
            {
                lock (gate)
                {
                    job.Status.FinishedUtc = DateTime.UtcNow;
                    job.Status.UpdatedUtc = job.Status.FinishedUtc;
                    job.Cancellation.Dispose();
                }
            }
        }

        private void BeginRequest(Job job, string phase) => Update(job, status =>
        {
            status.State = "Running";
            status.Phase = phase + " " + (status.CompletedRequests + 1) + "/" + status.TotalRequests;
            status.LastHttpStatus = null;
            status.LastApiResult = "Waiting for API response";
        });

        private sealed class Job
        {
            public RadioGroupRefreshStatus Status { get; set; }
            public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
            public bool CancelledByAdmin { get; set; }
            public Task Completion { get; set; }
        }
    }
}
