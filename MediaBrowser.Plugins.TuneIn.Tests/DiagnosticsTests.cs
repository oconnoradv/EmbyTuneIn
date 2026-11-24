using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Moq;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public sealed class DiagnosticsTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tunein-diagnostics-" + Guid.NewGuid().ToString("N"));
        private DateTime now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        private TuneInDiagnosticStore Store => new TuneInDiagnosticStore(root, () => now);
        private static PluginConfiguration Configuration() => new PluginConfiguration
        {
            SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true,
            Username = "private-user-name", LatLon = "12.345,67.890", NavigationSecret = "private-signing-secret",
            LocalCities = { new CatalogSelection { Id = "r999", Name = "private-city-name" } }
        };
        private static TuneInDiagnosticCollector Collector()
        {
            var collector = new TuneInDiagnosticCollector();
            collector.Begin(Configuration());
            return collector;
        }
        private TuneInDiagnosticPack Pack(TuneInDiagnosticCollector collector = null, bool navigation = false)
        {
            collector = collector ?? Collector();
            var status = new TuneInRefreshStatus
            {
                State = "Partial", Category = "location", StartedUtc = now.AddMinutes(-1), FinishedUtc = now,
                Phase = "private-folder-path", ActiveRequest = "private-request", SkippedBrowseLinkDetails = "private-log",
                ErrorCode = "private-exception-type", RequestsStarted = 2, SkippedBrowseLinks = 1,
                ImportProgress = new TuneInImportProgress
                {
                    StationEntriesTotal = 10, StationEntriesServed = 8,
                    PendingFolders = new[] { new TuneInImportFolderDetails { Name = "private-folder-name", Path = "private-server-path", Reference = "private-reference" } }
                }
            };
            return collector.Capture(collector.Latest(status, false).RunId, status, false, navigation, now, "4.10.2.0");
        }

        [Theory]
        [InlineData("api_token=private-token")]
        [InlineData("filter=private-token%3Dcredential")]
        [InlineData("filter=private-token%253Dcredential")]
        [InlineData("filter=private-token%25253Dcredential")]
        [InlineData("filter=private-token%00%0A")]
        [InlineData("filter=private-token&filter=s%3A~A")]
        [InlineData("filter=")]
        public void RedactionPrecedesPersistenceAndNeverCopiesUnknownValues(string tail)
        {
            var collector = Collector();
            collector.Record("location", "private-reason", "https://opml.radiotime.com/Browse.ashx?id=r123",
                "https://opml.radiotime.com/Browse.ashx?id=r124&" + tail, false);
            collector.Record("presets", "private-favorite", "https://opml.radiotime.com/Browse.ashx?id=r999",
                "https://opml.radiotime.com/Browse.ashx?c=presets&username=private-user-name", false);
            collector.Record("music", "private-error", "https://opml.radiotime.com/Browse.ashx?id=r123",
                "https://opml.radiotime.com/Browse.ashx?id=r125&sid=private-station", true);
            var preview = Store.Save(Pack(collector, true));
            Assert.DoesNotContain("private-", preview.PreviewText);
            Assert.Contains("r123", preview.PreviewText);
            Assert.DoesNotContain("r999", preview.PreviewText);
            Assert.DoesNotContain("12.345", preview.PreviewText);
            Assert.DoesNotContain("username", preview.PreviewText, StringComparison.OrdinalIgnoreCase);
            Assert.All(Directory.GetFiles(root), path => Assert.DoesNotContain("private-", File.ReadAllText(path)));
            var restored = Store.Read(preview.Id);
            Assert.Equal(preview.PreviewText, restored.PreviewText);
            Assert.Equal(preview.Revision, restored.Revision);
        }

        [Theory]
        [InlineData("https://127.0.0.1/Browse.ashx?id=r1")]
        [InlineData("https://private-host/Browse.ashx?id=r1")]
        [InlineData("https://private-user:private-password@opml.radiotime.com/Browse.ashx?id=r1")]
        [InlineData("https://opml.radiotime.com:8443/Browse.ashx?id=r1")]
        [InlineData("https://opml.radiotime.com/Tune.ashx?id=s123")]
        [InlineData("file:///private-server-path")]
        public void UntrustedAndPlaybackLinksAreNotExported(string link)
        {
            Assert.Null(TuneInDiagnosticFormat.Navigation(link, out var omitted, out var shape));
            Assert.Equal(1, omitted);
            Assert.Null(shape);
        }

        [Fact]
        public void PublicNavigationIsOptInAndUnknownFiltersExportOnlyShape()
        {
            var collector = Collector();
            collector.Record("location", "Invalid catalog link parameter: filter",
                "https://opml.radiotime.com/Browse.ashx?id=r123&filter=s%3A~A%3Acountry",
                "https://opml.radiotime.com/Browse.ashx?id=r124&filter=secret%2Fvalue&password=hidden", false);
            var safe = Pack(collector);
            Assert.Empty(safe.Examples);
            Assert.Equal(1, safe.OmittedExamples);
            Assert.Equal("unsupported_filter", Assert.Single(safe.Reasons).Code);
            var detailed = Pack(collector, true);
            var example = Assert.Single(detailed.Examples);
            Assert.Contains("s%3A~A%3Acountry", example.Source);
            Assert.Equal("L6?L5", example.FilterShape);
            Assert.DoesNotContain("secret", example.Target);
            Assert.True(detailed.OmittedNavigationFields >= 2);
            Assert.Equal("operation_failed", detailed.Refresh.ErrorCode);
            Assert.Equal("finished", detailed.Refresh.Phase);
        }

        [Fact]
        public void CaptureIsBoundedDeduplicatedAndDoesNotMutateTheRunningCollector()
        {
            var collector = Collector();
            for (var i = 1; i <= 150; i++)
                collector.Record("location", "TuneIn pagination escaped its selected node (private-name)",
                    "https://opml.radiotime.com/Browse.ashx?id=r1",
                    "https://opml.radiotime.com/Browse.ashx?id=r" + i, false);
            var captured = Pack(collector, true);
            Assert.Equal(100, captured.Examples.Length);
            Assert.Equal(50, captured.OmittedExamples);
            Assert.Equal(150, Assert.Single(captured.Reasons).Count);
            Assert.True(captured.Truncated);
            collector.Record("location", "anything", null, null, false);
            Assert.Equal(150, Assert.Single(captured.Reasons).Count);
            var run = collector.Latest(new TuneInRefreshStatus(), true);
            collector.Begin(Configuration());
            Assert.Throws<InvalidOperationException>(() => collector.Capture(run.RunId, new TuneInRefreshStatus(), true, false, now, null));
        }

        [Fact]
        public void SavedPacksSurviveRestartAndExpireOnTheExactRetentionBoundary()
        {
            var saved = Store.Save(Pack());
            now = now.AddDays(7).AddTicks(-1);
            Assert.Equal(saved.PreviewText, Store.Read(saved.Id).PreviewText);
            now = now.AddTicks(1);
            Assert.Empty(Store.List());
            Assert.Empty(Directory.GetFiles(root));
            Assert.Throws<InvalidOperationException>(() => Store.Read(saved.Id));
        }

        [Fact]
        public async Task ConcurrentCapturesKeepOnlyFiveBoundedImmutablePacks()
        {
            var packs = Enumerable.Range(0, 12).Select(_ => Pack()).ToArray();
            await Task.WhenAll(packs.Select(pack => Task.Run(() => Store.Save(pack))));
            var list = Store.List();
            Assert.Equal(5, list.Length);
            Assert.Equal(5, list.Select(p => p.Id).Distinct().Count());
            Assert.True(list.Sum(p => p.Bytes) <= TuneInDiagnosticStore.MaximumTotalBytes);
            foreach (var item in list)
            {
                var preview = Store.Read(item.Id);
                Assert.Equal(item.Revision, TuneInDiagnosticFormat.Revision(Encoding.UTF8.GetBytes(preview.PreviewText)));
                Assert.InRange(item.Bytes, 1, TuneInDiagnosticStore.MaximumPackBytes);
            }
        }

        [Fact]
        public void CancelledCaptureDoesNotWriteAndDeletionRequiresTheReviewedRevision()
        {
            var saved = Store.Save(Pack());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => Store.Save(Pack(), cancellation.Token));
            Assert.Single(Store.List());
            Assert.Throws<InvalidOperationException>(() => Store.Delete(saved.Id, "stale", false));
            Assert.Single(Store.List());
            Store.Delete(saved.Id, saved.Revision, false);
            Assert.Empty(Store.List());
            Assert.Throws<ArgumentException>(() => Store.Read("..\\private-server-path"));
        }

        [Fact]
        public void UnknownOnDiskFieldsAreNeverReturnedAndCorruptStorageHasExplicitRecovery()
        {
            var saved = Store.Save(Pack());
            var path = Path.Combine(root, saved.Id + ".json");
            File.WriteAllText(path, saved.PreviewText.Insert(saved.PreviewText.LastIndexOf('}'), ",\"Unexpected\":\"private-secret\""));
            using var refresh = EmptyRefresh();
            var service = Service(refresh);
            var status = Assert.IsType<TuneInDiagnosticsStatus>(service.Get(new GetTuneInDiagnostics()));
            Assert.NotNull(status.StorageError);
            Assert.True(status.HasStoredPacks);
            Assert.Empty(status.Packs);
            Assert.DoesNotContain("private-secret", status.StorageError);
            Assert.Throws<InvalidOperationException>(() => service.Get(new GetTuneInDiagnosticPack { Id = saved.Id }));
            var cleared = Assert.IsType<TuneInDiagnosticsStatus>(service.Post(new DeleteTuneInDiagnosticPacks { All = true }));
            Assert.False(cleared.HasStoredPacks);
            Assert.Null(cleared.StorageError);
        }

        [Fact]
        public void OversizedFilesAreRejectedAndInterruptedTemporaryFilesAreRemoved()
        {
            Directory.CreateDirectory(root);
            var id = Guid.NewGuid().ToString("N");
            var temporary = Path.Combine(root, id + ".tmp");
            File.WriteAllText(temporary, "redacted interrupted snapshot");
            Assert.Empty(Store.List());
            Assert.False(File.Exists(temporary));
            File.WriteAllBytes(Path.Combine(root, id + ".json"), new byte[TuneInDiagnosticStore.MaximumPackBytes + 1]);
            Assert.Throws<InvalidDataException>(() => Store.List());
            Store.Delete(null, null, true);
            Assert.Empty(Store.List());
        }

        [Fact]
        public void FailedStorageDoesNotEvictPreviouslySavedPacks()
        {
            var saved = Store.Save(Pack());
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            var unavailable = new TuneInDiagnosticStore(blocked, () => now);
            Assert.Throws<IOException>(() => unavailable.Save(Pack()));
            Assert.Throws<IOException>(() => unavailable.List());
            Assert.Equal(saved.Revision, Assert.Single(Store.List()).Revision);
        }

        [Theory]
        [InlineData("https://github.com/owner/repo")]
        [InlineData("owner/repo?token=private")]
        [InlineData("owner/repo/extra")]
        [InlineData("owner/..")]
        [InlineData("owner/repo.git")]
        [InlineData(" owner/repo ")]
        [InlineData("owner\n/repo")]
        public void HiddenDestinationAcceptsOnlyAnExplicitGitHubRepositoryName(string value)
        {
            Assert.Throws<ArgumentException>(() => TuneInDiagnosticFormat.Repository(value));
        }

        [Fact]
        public void DestinationChangesInvalidateReviewWithoutChangingThePackOrCatalogIdentity()
        {
            var saved = Store.Save(Pack());
            using var refresh = EmptyRefresh();
            string repository = null;
            var service = Service(refresh, () => repository);
            var original = Assert.IsType<TuneInDiagnosticPreview>(service.Get(new GetTuneInDiagnosticPack { Id = saved.Id }));
            Assert.Equal("oconnoradv/EmbyTuneIn", original.Repository);
            Assert.StartsWith("https://github.com/oconnoradv/EmbyTuneIn/issues/new?", original.DraftUrl);
            Assert.InRange(original.DraftUrl.Length, 1, 1800);
            var draft = Assert.IsType<TuneInDiagnosticDraft>(service.Post(new CreateTuneInDiagnosticDraft
                { Id = saved.Id, Revision = saved.Revision, Repository = original.Repository }));
            Assert.Equal(original.DraftUrl, draft.Url);
            repository = "MediaBrowser/TuneIn";
            Assert.Throws<InvalidOperationException>(() => service.Post(new CreateTuneInDiagnosticDraft
                { Id = saved.Id, Revision = saved.Revision, Repository = original.Repository }));
            var updated = Assert.IsType<TuneInDiagnosticPreview>(service.Get(new GetTuneInDiagnosticPack { Id = saved.Id }));
            Assert.Equal(saved.PreviewText, updated.PreviewText);
            Assert.Equal(saved.Revision, updated.Revision);
            Assert.Contains("/MediaBrowser/TuneIn/issues/new?", updated.DraftUrl);
            repository = "https://private-host/report";
            var invalid = Assert.IsType<TuneInDiagnosticPreview>(service.Get(new GetTuneInDiagnosticPack { Id = saved.Id }));
            Assert.Null(invalid.DraftUrl);
            Assert.NotNull(invalid.ReportingError);
            Assert.Equal(saved.PreviewText, invalid.PreviewText);
            var configuration = Configuration();
            var before = new ConfigurationSnapshot(configuration);
            configuration.DiagnosticsIssueRepository = "MediaBrowser/TuneIn";
            var after = new ConfigurationSnapshot(configuration);
            Assert.Equal(before.Key, after.Key);
            Assert.Equal(before.CategoryScopeKey("music"), after.CategoryScopeKey("music"));
            Assert.Equal("MediaBrowser/TuneIn", after.Value.DiagnosticsIssueRepository);
        }

        [Fact]
        public void AllDiagnosticRoutesRequireAdministratorAndMissingContextCannotMutateStorage()
        {
            var types = new[] { typeof(GetTuneInDiagnostics), typeof(CaptureTuneInDiagnostics), typeof(GetTuneInDiagnosticPack),
                typeof(DeleteTuneInDiagnosticPacks), typeof(CreateTuneInDiagnosticDraft) };
            foreach (var type in types) Assert.Equal("Admin", type.GetCustomAttribute<AuthenticatedAttribute>().Roles);
            using var refresh = EmptyRefresh();
            var service = new TuneInDiagnosticsService(refresh, Store, () => null, "4.10.2.0");
            Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInDiagnostics()));
            Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInDiagnosticPack()));
            Assert.Throws<UnauthorizedAccessException>(() => service.Post(new CaptureTuneInDiagnostics()));
            Assert.Throws<UnauthorizedAccessException>(() => service.Post(new DeleteTuneInDiagnosticPacks { All = true }));
            Assert.Throws<UnauthorizedAccessException>(() => service.Post(new CreateTuneInDiagnosticDraft()));
            Assert.False(Directory.Exists(root));
        }

        [Fact]
        public async Task CaptureDuringDiscoveryDoesNotStopTheRefreshAndRecordsVerifiedPartialImport()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var configuration = Configuration();
            configuration.NavigationSecret = new PluginConfiguration().NavigationSecret;
            var directory = new TuneInDirectory(() => configuration, async (_, token) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='private-station-name'/>"
                    + "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=r124&amp;filter=private-invalid/value'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var statePath = Path.Combine(root, "refresh.xml");
            Directory.CreateDirectory(root);
            using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(statePath), () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (_, staged, __, token) =>
                {
                    void Read(string id)
                    {
                        foreach (var item in staged.Read(id, token).Items.Where(i => i.Type == ChannelItemType.Folder)) Read(item.Id);
                    }
                    Read(null);
                    return Task.CompletedTask;
                }, null);
            try
            {
                refresh.Start();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var service = Service(refresh);
                var available = Assert.IsType<TuneInDiagnosticsStatus>(service.Get(new GetTuneInDiagnostics()));
                Assert.True(available.LatestRefresh.Busy);
                var active = Assert.IsType<TuneInDiagnosticPreview>(service.Post(new CaptureTuneInDiagnostics { RunId = available.LatestRefresh.RunId }));
                Assert.True(refresh.Status().Busy);
                release.TrySetResult(true);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (refresh.Status().Busy && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.False(refresh.Status().Busy);
                Assert.Equal("Partial", refresh.Status().State);
                var completed = Assert.IsType<TuneInDiagnosticPreview>(service.Post(new CaptureTuneInDiagnostics
                    { RunId = available.LatestRefresh.RunId, IncludePublicNavigation = true }));
                var payload = TuneInDiagnosticFormat.Deserialize<TuneInDiagnosticPack>(Encoding.UTF8.GetBytes(completed.PreviewText));
                Assert.Equal("unsupported_filter", Assert.Single(payload.Reasons).Code);
                Assert.True(payload.Counts.LastImportConsumptionVerified);
                Assert.True(payload.Counts.LastImportRefreshLevel > 0);
                Assert.DoesNotContain("private-", completed.PreviewText);
                Assert.Equal(active.PreviewText, Store.Read(active.Id).PreviewText);
            }
            finally { release.TrySetResult(true); refresh.Cancel(); }
        }

        private TuneInDiagnosticsService Service(TuneInRefresh refresh, Func<string> repository = null) =>
            new TuneInDiagnosticsService(refresh, Store, repository ?? (() => null), "4.10.2.0", clock: () => now)
                { Request = Mock.Of<IRequest>() };

        private static TuneInRefresh EmptyRefresh()
        {
            var configuration = Configuration();
            var directory = new TuneInDirectory(() => configuration, (_, __) => throw new InvalidOperationException());
            return new TuneInRefresh(directory, new TuneInRefreshStore(null), () => null,
                (_, __, ___, ____, _____) => throw new InvalidOperationException(),
                (_, __, ___, ____) => throw new InvalidOperationException(), null);
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
