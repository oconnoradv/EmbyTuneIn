using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Plugins.TuneIn.Configuration;
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
    public class LibraryCleanupTests
    {
        [Fact]
        public async Task FailedSiblingKeepsItsParentEvenWhenOtherSiblingIsRemoved()
        {
            var fixture = new Fixture();
            var id = fixture.SignedId();
            fixture.Add(2, 1, id);
            fixture.Add(3, 2, id);
            fixture.Add(4, 2, id);
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot());
            fixture.Library.BeforeDeleteItem = item =>
            {
                if (item.InternalId == 3) throw new InvalidOperationException("Synthetic failure");
            };
            var counts = (0, 0);

            await fixture.Cleanup.RunAsync(plan, (removed, failed) => counts = (removed, failed), CancellationToken.None);

            Assert.Equal((1, 2), counts);
            Assert.Equal(new long[] { 4 }, fixture.Library.Removed);
            Assert.True(fixture.Library.Items.ContainsKey(2));
            Assert.True(fixture.Library.Items.ContainsKey(3));
        }

        [Fact]
        public async Task SiblingRemovalsOverlapAndParentWaitsForChildren()
        {
            var fixture = new Fixture();
            var id = fixture.SignedId();
            fixture.Add(2, 1, id);
            for (var index = 3; index < 7; index++) fixture.Add(index, 2, id);
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot());
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            var parentStarted = 0;
            fixture.Library.BeforeDeleteItem = item =>
            {
                if (item.ParentId != 2)
                {
                    Interlocked.Exchange(ref parentStarted, 1);
                    return;
                }
                if (Interlocked.Increment(ref started) == 4) entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            var cleanup = fixture.Cleanup.RunAsync(plan, (_, _) => { }, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, Volatile.Read(ref parentStarted));
            }
            finally { release.Set(); }
            await cleanup;
            Assert.Equal(1, parentStarted);
            Assert.Equal(5, fixture.Library.Removed.Count);
            Assert.Equal(2, fixture.Library.Removed.Last());
        }

        [Fact]
        public async Task ReportsAfterFiveHundredAttemptsAndTheRemainingGroup()
        {
            var fixture = new Fixture();
            var id = fixture.SignedId();
            for (var index = 0; index < 1001; index++)
                fixture.Add(index + 2, 1, id);
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot());
            var reports = new List<(int Removed, int Failed)>();
            fixture.Library.BeforeDeleteItem = item =>
            {
                if (item.InternalId == 1002) throw new InvalidOperationException("Synthetic failure");
            };

            await fixture.Cleanup.RunAsync(plan, (removed, failed) => reports.Add((removed, failed)), CancellationToken.None);

            Assert.Equal(new[] { (499, 1), (999, 1), (1000, 1) }, reports);
            Assert.Equal(1000, fixture.Library.Removed.Count);
        }

        [Fact]
        public async Task ChangedRootStopsBeforeTheNextRemovalWithinAGroup()
        {
            var fixture = new Fixture();
            var id = fixture.SignedId();
            fixture.Add(2, 1, id);
            fixture.Add(3, 2, id);
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot());
            fixture.Library.BeforeDelete = () => fixture.Library.Items[1].InternalId = 99;
            var reports = new List<(int Removed, int Failed)>();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Cleanup.RunAsync(plan, (removed, failed) => reports.Add((removed, failed)), CancellationToken.None));

            Assert.Equal(new[] { (1, 0) }, reports);
            Assert.Single(fixture.Library.Removed);
        }

        [Fact]
        public async Task DeletesOnlyPreviewedGeneratedDescendantsAndProtectsFoldersWithUnverifiedChildren()
        {
            var fixture = new Fixture();
            var oldId = fixture.SignedId();
            fixture.Config.Username = "new settings";
            fixture.Add(2, 1, oldId);
            fixture.Add(3, 2, oldId);
            fixture.Add(4, 2, "foreign");
            fixture.Add(5, 90, oldId);
            var snapshot = fixture.Directory.Snapshot();
            var plan = fixture.Cleanup.Preview(snapshot);
            Assert.Equal(new long[] { 3, 2 }, plan.Targets.Select(item => item.Id));
            Assert.Empty(fixture.Library.Removed);
            fixture.Add(6, 1, oldId);
            var counts = (0, 0);
            await fixture.Cleanup.RunAsync(plan, (removed, failed) => counts = (removed, failed), CancellationToken.None);
            Assert.Equal((1, 1), counts);
            Assert.Equal(new long[] { 3 }, fixture.Library.Removed);
            Assert.Equal(new long[] { 1, 2, 4, 5, 6 }, fixture.Library.Items.Keys.OrderBy(id => id));
        }

        [Fact]
        public async Task RefusesItemsMovedAfterPreview()
        {
            var fixture = new Fixture();
            fixture.Add(2, 1, fixture.SignedId());
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot());
            fixture.Library.Items[2].ParentId = 90;
            await fixture.Cleanup.RunAsync(plan, (removed, failed) => Assert.Equal((0, 1), (removed, failed)),
                CancellationToken.None);
            Assert.Empty(fixture.Library.Removed);
        }

        [Fact]
        public async Task CategoryPreviewDeletesOnlyMatchingVerifiedTreeAndAcceptsOldScopes()
        {
            var fixture = new Fixture();
            fixture.Config.EnableSports = true;
            var talk = fixture.SignedId();
            var sports = fixture.SignedId("sports");
            fixture.Config.Username = "changed";
            fixture.Add(2, 1, talk);
            fixture.Add(3, 2, talk);
            fixture.Add(4, 1, sports);
            fixture.Add(5, 4, sports);
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot(), "talk");
            Assert.Equal(new long[] { 3, 2 }, plan.Targets.Select(item => item.Id));
            Assert.Equal(0, plan.Unverified);
            await fixture.Cleanup.RunAsync(plan, (_, _) => { }, CancellationToken.None);
            Assert.Equal(new long[] { 3, 2 }, fixture.Library.Removed);
            Assert.Equal(new long[] { 1, 4, 5 }, fixture.Library.Items.Keys.OrderBy(id => id));
        }

        [Fact]
        public async Task CategoryCleanupProtectsForeignAndOtherCategoryChildren()
        {
            var fixture = new Fixture();
            fixture.Config.EnableSports = true;
            fixture.Add(2, 1, fixture.SignedId());
            fixture.Add(3, 2, fixture.SignedId("sports"));
            fixture.Add(4, 2, "foreign");
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot(), "talk");
            Assert.Single(plan.Targets);
            Assert.Equal(2, plan.Unverified);
            await fixture.Cleanup.RunAsync(plan, (removed, failed) => Assert.Equal((0, 1), (removed, failed)),
                CancellationToken.None);
            Assert.Empty(fixture.Library.Removed);
            Assert.Throws<ArgumentException>(() => fixture.Cleanup.Preview(fixture.Directory.Snapshot(), "unknown"));
        }

        [Fact]
        public void CategoryPreviewDoesNotTreatSignedNestedEntryAsCategoryRoot()
        {
            var fixture = new Fixture();
            var request = new DirectoryRequest();
            request.Parameters["c"] = "talk";
            var nested = fixture.Directory.Register(fixture.Directory.Snapshot(),
                new NavigationEntry { Category = "talk", Request = request });
            fixture.Add(2, 1, nested);
            Assert.Empty(fixture.Cleanup.Preview(fixture.Directory.Snapshot(), "talk").Targets);
        }

        [Fact]
        public async Task NewScopedPreviewInvalidatesThePreviousToken()
        {
            var fixture = new Fixture();
            fixture.Config.EnableSports = true;
            fixture.Add(2, 1, fixture.SignedId());
            fixture.Add(3, 1, fixture.SignedId("sports"));
            var path = Path.Combine(Path.GetTempPath(), "tunein-cleanup-" + Guid.NewGuid().ToString("N"));
            var store = new TuneInRefreshStore(path);
            using var refresh = new TuneInRefresh(fixture.Directory, store,
                () => throw new InvalidOperationException(), (_, _, _, _, _) => throw new InvalidOperationException(),
                (_, _, _, _) => throw new InvalidOperationException(), null, libraryCleanup: fixture.Cleanup);
            try
            {
                var talk = refresh.PreviewCleanup("talk");
                var sports = refresh.PreviewCleanup("sports");
                Assert.Equal("Talk", talk.Scope);
                Assert.Equal("Sports", sports.Scope);
                Assert.Throws<InvalidOperationException>(() => refresh.StartCleanup(talk.Token));
                refresh.StartCleanup(sports.Token);
                Assert.True(await Task.Run(() => SpinWait.SpinUntil(
                    () => refresh.Status().Cleanup.State != "Running", TimeSpan.FromSeconds(5))));
                Assert.Equal("Completed", refresh.Status().Cleanup.State);
                Assert.Contains("No compatible baseline", refresh.Status().Cleanup.Message);
                Assert.Equal(new long[] { 3 }, fixture.Library.Removed);
            }
            finally
            {
                store.Clear();
                store.ClearDiscovery();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task ConfirmedCleanupBlocksRefreshAndWritesAndClearsDurableReplayBeforeDeletion()
        {
            var fixture = new Fixture();
            fixture.Add(2, 1, fixture.SignedId());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var path = Path.Combine(Path.GetTempPath(), "tunein-cleanup-" + Guid.NewGuid().ToString("N"));
            var store = new TuneInRefreshStore(path);
            var snapshot = fixture.Directory.Snapshot();
            var now = DateTime.UtcNow;
            store.Write(new TuneInRefreshCheckpoint { ConfigurationKey = snapshot.Key, SavedUtc = now, StartedUtc = now });
            store.WriteRecovery(new TuneInRefreshRecovery { ConfigurationKey = snapshot.Key });
            store.WriteDiscovery(new TuneInDiscoveryCheckpoint { ConfigurationKey = snapshot.DiscoveryKey, StartedUtc = now, SavedUtc = now });
            fixture.Library.BeforeDelete = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            using var refresh = new TuneInRefresh(fixture.Directory, store,
                () => throw new InvalidOperationException("Refresh must not start"),
                (_, _, _, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
                null, clock: () => now, libraryCleanup: fixture.Cleanup);
            try
            {
                var service = new TuneInRefreshService(refresh);
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new PreviewTuneInCleanup()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new StartTuneInCleanup()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new CancelTuneInCleanup()));
                Assert.Equal("Admin", typeof(PreviewTuneInCleanup).GetCustomAttribute<AuthenticatedAttribute>().Roles);
                Assert.Equal("Admin", typeof(StartTuneInCleanup).GetCustomAttribute<AuthenticatedAttribute>().Roles);
                Assert.Equal("Admin", typeof(CancelTuneInCleanup).GetCustomAttribute<AuthenticatedAttribute>().Roles);
                var expiry = refresh.PreviewCleanup();
                now = now.AddMinutes(6);
                Assert.Throws<InvalidOperationException>(() => refresh.StartCleanup(expiry.Token));
                var preview = refresh.PreviewCleanup();
                Assert.Throws<InvalidOperationException>(() => refresh.StartCleanup("wrong"));
                Assert.Equal("Running", refresh.StartCleanup(preview.Token).Cleanup.State);
                Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
                Assert.Null(store.Read());
                Assert.Null(store.ReadRecovery());
                Assert.False(File.Exists(path + ".discovery"));
                refresh.CheckSchedules(); // A pending restart recovery may not start.
                Assert.Throws<InvalidOperationException>(() => refresh.Start());
                Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => { }));
                Assert.Throws<InvalidOperationException>(() => refresh.SetSettingsLock(() => true));
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => refresh.Status().Cleanup.State != "Running", TimeSpan.FromSeconds(5)));
                Assert.Equal("Completed", refresh.Status().Cleanup.State);
                Assert.Throws<InvalidOperationException>(() => refresh.StartCleanup(preview.Token));
            }
            finally
            {
                release.Set();
                store.Clear();
                store.ClearDiscovery();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task StopCleanupDrainsCurrentRemovalRetainsProgressAndKeepsScopedRecoveryMarker()
        {
            var fixture = new Fixture();
            fixture.Config.EnableSports = true;
            for (var index = 2; index < 8; index++)
                fixture.Add(index, 1, fixture.SignedId());
            fixture.Add(8, 1, fixture.SignedId("sports"));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var path = Path.Combine(Path.GetTempPath(), "tunein-cleanup-" + Guid.NewGuid().ToString("N"));
            var store = new TuneInRefreshStore(path);
            var snapshot = fixture.Directory.Snapshot();
            var now = DateTime.UtcNow;
            store.Write(new TuneInRefreshCheckpoint { ConfigurationKey = snapshot.Key, SavedUtc = now, StartedUtc = now });
            fixture.Library.BeforeDelete = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            using var refresh = new TuneInRefresh(fixture.Directory, store,
                () => throw new InvalidOperationException(), (_, _, _, _, _) => throw new InvalidOperationException(),
                (_, _, _, _) => throw new InvalidOperationException(), null, libraryCleanup: fixture.Cleanup);
            try
            {
                Assert.Equal("Idle", refresh.CancelCleanup().Cleanup.State);
                var preview = refresh.PreviewCleanup("talk");
                Assert.Equal(6, preview.Folders);
                refresh.StartCleanup(preview.Token);
                Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
                Assert.Contains("talk", store.Read().CleanedCategories);
                var stopping = refresh.CancelCleanup().Cleanup;
                Assert.Equal("Running", stopping.State);
                Assert.True(stopping.Cancelling);
                Assert.True(refresh.CancelCleanup().Cleanup.Cancelling);
                Assert.Throws<InvalidOperationException>(() => refresh.Start());
                Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => { }));
                Assert.Throws<InvalidOperationException>(() => refresh.PreviewCleanup());
                release.Set();
                Assert.True(await Task.Run(() => SpinWait.SpinUntil(
                    () => refresh.Status().Cleanup.State != "Running", TimeSpan.FromSeconds(5))));
                var result = refresh.Status().Cleanup;
                Assert.Equal("Stopped", result.State);
                Assert.False(result.Cancelling);
                Assert.InRange(result.Removed, 1, TuneInLibraryCleanup.ParallelRemovals);
                Assert.Equal(0, result.Failed);
                Assert.Equal(6, result.Total);
                Assert.Equal(result.Removed, fixture.Library.Removed.Count);
                Assert.True(fixture.Library.Items.ContainsKey(8));
                Assert.Contains("talk", store.Read().CleanedCategories);
                var remaining = refresh.PreviewCleanup("talk");
                Assert.NotNull(remaining.Token);
                Assert.Equal(6 - result.Removed, remaining.Folders);
            }
            finally
            {
                release.Set();
                store.Clear();
                store.ClearDiscovery();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task CancellationPreservesFailuresAndDoesNotDeleteRemainingEntries()
        {
            var fixture = new Fixture();
            fixture.Add(2, 1, fixture.SignedId());
            fixture.Add(3, 2, fixture.SignedId());
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot(), "talk");
            using var cancellation = new CancellationTokenSource();
            fixture.Library.BeforeDelete = () =>
            {
                cancellation.Cancel();
                throw new InvalidOperationException("Removal failed");
            };
            var counts = (0, 0);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Cleanup.RunAsync(plan, (removed, failed) => counts = (removed, failed), cancellation.Token));
            Assert.Equal((0, 1), counts);
            Assert.Empty(fixture.Library.Removed);
        }

        [Fact]
        public async Task CleanupCancelledBeforeFirstRemovalDoesNotDeleteEntries()
        {
            var fixture = new Fixture();
            fixture.Add(2, 1, fixture.SignedId());
            var plan = fixture.Cleanup.Preview(fixture.Directory.Snapshot(), "talk");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var counts = (-1, -1);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Cleanup.RunAsync(plan, (removed, failed) => counts = (removed, failed), cancellation.Token));
            Assert.Equal((0, 0), counts);
            Assert.Empty(fixture.Library.Removed);
        }

        private sealed class Fixture
        {
            internal readonly PluginConfiguration Config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true
            };
            internal readonly TuneInDirectory Directory;
            internal readonly LibraryProxy Library;
            internal readonly TuneInLibraryCleanup Cleanup;

            internal Fixture()
            {
                Directory = new TuneInDirectory(() => Config, (_, _) => throw new InvalidOperationException());
                var api = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
                Library = (LibraryProxy)api;
                var channel = new Channel { InternalId = 1, Name = "TuneIn" };
                Library.Items.Add(1, channel);
                Cleanup = new TuneInLibraryCleanup(api, () => channel, null);
            }

            internal string SignedId(string category = "talk") =>
                Directory.Register(Directory.Snapshot(), new NavigationEntry { Category = category });
            internal void Add(long id, long parent, string externalId) =>
                Library.Items.Add(id, new Folder { InternalId = id, ParentId = parent, ExternalId = externalId });
        }

        public class LibraryProxy : DispatchProxy
        {
            private readonly object gate = new object();
            internal readonly Dictionary<long, BaseItem> Items = new Dictionary<long, BaseItem>();
            internal readonly List<long> Removed = new List<long>();
            internal Action BeforeDelete;
            internal Action<BaseItem> BeforeDeleteItem;

            protected override object Invoke(MethodInfo method, object[] args)
            {
                switch (method.Name)
                {
                    case nameof(ILibraryManager.GetItemById):
                        lock (gate) return Items.TryGetValue((long)args[0], out var item) ? item : null;
                    case nameof(ILibraryManager.GetItemList):
                        var query = (InternalItemsQuery)args[0];
                        lock (gate)
                            return Items.Values.Where(entry => query.Recursive || query.ParentIds.Contains(entry.ParentId))
                                .Take(query.Limit.GetValueOrDefault(int.MaxValue)).ToArray();
                    case nameof(ILibraryManager.DeleteItem):
                        var options = Assert.IsType<DeleteOptions>(args[1]);
                        Assert.False(options.DeleteFileLocation);
                        Assert.False(options.DeleteFromExternalProvider);
                        BeforeDeleteItem?.Invoke((BaseItem)args[0]);
                        BeforeDelete?.Invoke();
                        var id = ((BaseItem)args[0]).InternalId;
                        lock (gate)
                        {
                            Removed.Add(id);
                            Items.Remove(id);
                        }
                        return null;
                    default: throw new NotSupportedException(method.Name);
                }
            }
        }
    }
}
