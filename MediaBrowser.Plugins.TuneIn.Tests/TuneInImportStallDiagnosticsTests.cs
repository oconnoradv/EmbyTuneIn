using System;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public sealed class TuneInImportStallDiagnosticsTests
    {
        [Fact]
        public void ReportsElapsedProgressUnreadCountAndImportStage()
        {
            var diagnostics = new TuneInImportStallDiagnostics();
            var lastProgress = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.Null(diagnostics.TryCreate(lastProgress.AddSeconds(59), lastProgress, 8, 3, false));
            Assert.Equal(
                "TuneIn staged import stalled: 60s since progress; 5 folders unread; stage=Emby import.",
                diagnostics.TryCreate(lastProgress.AddSeconds(60), lastProgress, 8, 3, false));
        }

        [Fact]
        public void IdentifiesCancellationDrainAndBoundsReports()
        {
            var diagnostics = new TuneInImportStallDiagnostics();
            var lastProgress = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.Equal(
                "TuneIn staged import stalled: 60s since progress; 0 folders unread; stage=cancellation draining.",
                diagnostics.TryCreate(lastProgress.AddMinutes(1), lastProgress, 2, 4, true));
            Assert.NotNull(diagnostics.TryCreate(lastProgress.AddMinutes(2), lastProgress, 2, 2, true));
            Assert.NotNull(diagnostics.TryCreate(lastProgress.AddMinutes(3), lastProgress, 2, 2, false));
            Assert.Null(diagnostics.TryCreate(lastProgress.AddMinutes(4), lastProgress, 2, 2, false));
        }

        [Fact]
        public void IncludesNoFolderIdentityOrPathData()
        {
            var diagnostics = new TuneInImportStallDiagnostics();
            var lastProgress = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var message = diagnostics.TryCreate(lastProgress.AddMinutes(1), lastProgress, 1, 0, false);

            Assert.DoesNotContain("http", message.ToLowerInvariant());
            Assert.DoesNotContain("\\", message);
            Assert.DoesNotContain("/", message);
        }
    }
}
