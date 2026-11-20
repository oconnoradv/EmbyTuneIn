using System;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInImportStallDiagnostics
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        internal const int MaxReports = 3;
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
    }
}
