using System;
using System.Collections.Generic;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInImportProgress
    {
        public int StationEntriesTotal { get; set; }
        public int StationEntriesServed { get; set; }
        public int SubfolderEntriesTotal { get; set; }
        public int SubfolderEntriesServed { get; set; }
        public int? LastFolderStationEntries { get; set; }
        public int? LastFolderSubfolderEntries { get; set; }
        public TuneInImportFolderDetails[] PendingFolders { get; set; }
        public int PendingFoldersOmitted { get; set; }
    }

    public sealed class TuneInImportFolderDetails
    {
        public string Reference { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public int StationEntries { get; set; }
        public int SubfolderEntries { get; set; }
    }

    internal sealed class TuneInImportStallDiagnostics
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        internal const int MaxReports = 3;
        internal const int MaxFolderDetails = 50;
        private DateTime? lastReportedUtc;
        private int reports;
        internal bool LimitReached => reports >= MaxReports;

        internal string TryCreate(DateTime now, DateTime? lastProgressUtc, int foldersTotal,
            int foldersServed, bool cancellationDraining)
        {
            if (LimitReached || !lastProgressUtc.HasValue
                || now - lastProgressUtc.Value < Interval
                || (lastReportedUtc.HasValue && now - lastReportedUtc.Value < Interval))
                return null;

            lastReportedUtc = now;
            reports++;
            var elapsedSeconds = Math.Max(0, (int)(now - lastProgressUtc.Value).TotalSeconds);
            var unread = Math.Max(0, foldersTotal - foldersServed);
            var stage = cancellationDraining ? "cancellation draining" : "Emby import";
            return $"TuneIn staged import stalled: {elapsedSeconds}s since progress; {unread} folders unread; stage={stage}.";
        }

        internal static IEnumerable<string> DescribePending(TuneInImportProgress progress)
        {
            if (progress?.PendingFolders == null) yield break;
            yield return $"TuneIn staged listing contents: {progress.StationEntriesServed}/{progress.StationEntriesTotal} station entries served; "
                + $"{progress.SubfolderEntriesServed}/{progress.SubfolderEntriesTotal} subfolder entries served. "
                + "Listing delivery is not confirmation of Emby database writes; station entries can repeat across folders.";
            foreach (var folder in progress.PendingFolders)
                yield return $"TuneIn unserved folder [{folder.Reference}]: {folder.Path}; "
                    + $"{folder.StationEntries} station entries; {folder.SubfolderEntries} subfolder entries.";
            if (progress.PendingFoldersOmitted > 0)
                yield return $"TuneIn additional unserved folder details omitted: {progress.PendingFoldersOmitted}.";
        }
    }
}
