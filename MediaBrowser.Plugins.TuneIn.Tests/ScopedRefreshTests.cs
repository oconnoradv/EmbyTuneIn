using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public sealed class ScopedRefreshTests
    {
        [Fact]
        public async Task CommittedListingIsAvailableOnlyDuringRefreshAndFreshDiscoverySkipsUnchangedScope()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var calls = 0;
            var imports = 0;
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(token);
                }
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(AppContext.BaseDirectory, $"refresh-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, folder, token) =>
                    {
                        Interlocked.Increment(ref imports);
                        staged.Read(folder, token);
                        staged.Verify();
                        return Task.CompletedTask;
                    }, null, timeoutOverride: TimeSpan.FromSeconds(5));
                var snapshot = directory.Snapshot();
                var folderId = directory.Register(snapshot, new NavigationEntry { Category = "talk", Name = "Talk" });

                Assert.True(refresh.Start().Busy);
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.False(refresh.TryGetCommitted(snapshot, folderId, out _));
                refresh.Start();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(refresh.TryGetCommitted(snapshot, folderId, out var saved));
                    Assert.Equal("Radio", Assert.Single(saved.Items).Name);
                }
                finally { release.TrySetResult(true); }
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(1, refresh.Status().CategoriesSkipped);
                Assert.Equal(1, imports);
                Assert.False(refresh.TryGetCommitted(snapshot, folderId, out _));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task DoesNotImportOrWriteDirtyStateWhenDiscoveryFetchesNothing()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new InvalidOperationException());
            var channel = new TuneInChannel(directory);
            var imports = 0;
            var path = Path.Combine(AppContext.BaseDirectory, $"refresh-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (_, __, ___, ____, _____) => Task.FromResult(new ChannelItemResult
                    { Items = new System.Collections.Generic.List<ChannelItemInfo>() }),
                    (_, __, ___, ____) =>
                    {
                        Interlocked.Increment(ref imports);
                        return Task.CompletedTask;
                    }, null);
                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal("Error", refresh.Status().State);
                Assert.False(refresh.Status().ImportMayHavePartiallyApplied);
                Assert.Equal(0, imports);
                Assert.False(File.Exists(path));
            }
            finally { File.Delete(path); }
        }

            [Fact]
            public async Task SingleCategoryRefreshValidatesSavedScopeAndLeavesOtherCategoriesUntouched()
            {
                var config = new PluginConfiguration
                {
                    SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
                };
                var fetched = new List<string>();
                var imported = new List<string>();
                var directory = new TuneInDirectory(() => config, (request, _) =>
                {
                    fetched.Add(request.Parameters["c"]);
                    return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Station'/></body></opml>"));
                });
                var channel = new TuneInChannel(directory);
                var path = Path.Combine(Path.GetTempPath(), $"tunein-refresh-{Guid.NewGuid():N}.xml");
                var store = new TuneInRefreshStore(path);
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, __, folder, ___) =>
                    {
                        imported.Add(directory.Resolve(directory.Snapshot(), folder).Category);
                        return Task.CompletedTask;
                    }, null);
                try
                {
                    Assert.Throws<ArgumentException>(() => refresh.Start("music"));
                    Assert.Throws<ArgumentException>(() => refresh.Start("https://example.test/"));
                    Assert.Empty(fetched);

                    refresh.Start("talk");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("talk", Assert.Single(fetched));
                    Assert.Equal("talk", Assert.Single(imported));
                    var talkCheckpoint = Assert.Single(store.Read().Categories);
                    Assert.Equal("talk", talkCheckpoint.Category);

                    refresh.Start("sports");
                    await WaitUntilIdle(refresh);
                    Assert.Equal(new[] { "talk", "sports" }, fetched);
                    Assert.Equal(new[] { "talk", "sports" }, imported);
                    Assert.Equal(talkCheckpoint.Fingerprint, store.Read().Categories.Single(category => category.Category == "talk").Fingerprint);

                    refresh.Start("sports");
                    await WaitUntilIdle(refresh);
                    var status = refresh.Status();
                    Assert.Equal("Completed", status.State);
                    Assert.Equal(1, status.CategoriesSkipped);
                    Assert.Collection(status.Categories,
                        talk => { Assert.Equal("talk", talk.Category); Assert.Equal("Imported", talk.State); },
                        sports => { Assert.Equal("sports", sports.Category); Assert.Equal("Unchanged", sports.State); });
                    Assert.Equal(2, imported.Count);
                }
                finally { File.Delete(path); }
            }

            [Fact]
            public async Task ChangedCategoryImportsCompleteListingAndReconcilesAfterCancellationAndRestart()
            {
                var config = new PluginConfiguration
                {
                    SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
                };
                const string first = "<outline type='audio' guide_id='s1' text='Existing'/>";
                var listing = first;
                var directory = new TuneInDirectory(() => config, (_, __) =>
                    Task.FromResult(XDocument.Parse("<opml><body>" + listing + "</body></opml>")));
                var channel = new TuneInChannel(directory);
                var path = Path.Combine(Path.GetTempPath(), $"tunein-refresh-{Guid.NewGuid():N}.xml");
                var store = new TuneInRefreshStore(path);
                var snapshot = directory.Snapshot();
                var root = directory.Register(snapshot, new NavigationEntry { Category = "talk", Name = "Talk" });
                var native = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root }, CancellationToken.None);
                var originalId = Assert.Single(native.Items).Id;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var imported = new List<string[]>();
                var interrupt = false;
                TuneInRefresh Make() => new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (c, staged, folder, token) => c.ImportAsync(staged, async () =>
                    {
                        Assert.Equal("talk", store.Read().DirtyCategory);
                        var result = await c.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
                        imported.Add(result.Items.Select(item => item.Id).ToArray());
                        Assert.Contains(originalId, imported.Last());
                        if (interrupt)
                        {
                            entered.TrySetResult(true);
                            await Task.Delay(Timeout.Infinite, token);
                        }
                    }), null, timeoutOverride: TimeSpan.FromSeconds(15));
                try
                {
                    using (var refresh = Make())
                    {
                        Assert.Equal("NeverRun", refresh.Status().State);
                        refresh.Start("talk");
                        await WaitUntilIdle(refresh);
                        Assert.Equal("Completed", refresh.Status().State);
                        Assert.Single(Assert.Single(imported));

                        listing = first + "<outline type='link' text='Invalid' url='https://invalid.example/Browse.ashx?id=s2'/>";
                        refresh.Start("talk");
                        await WaitUntilIdle(refresh);
                        Assert.Equal("Error", refresh.Status().State);
                        Assert.Null(store.Read().DirtyCategory);
                        Assert.Single(imported);

                        listing = first + "<outline type='audio' guide_id='s2' text='New'/>";
                        interrupt = true;
                        refresh.Start("talk");
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => config.EnableTalk = false));
                        Assert.Throws<InvalidOperationException>(() => refresh.Start());
                        refresh.Cancel();
                        await WaitUntilIdle(refresh);
                        Assert.Equal("Cancelled", refresh.Status().State);
                        Assert.Equal("talk", store.Read().DirtyCategory);
                        Assert.Equal(2, imported.Last().Length);
                    }
                    interrupt = false;
                    listing = first; // Same as the last successful import, but a dirty category cannot skip.
                    using (var restarted = Make())
                    {
                        Assert.Equal("talk", restarted.Status().DirtyCategory);
                        Assert.Throws<InvalidOperationException>(() => restarted.Start("sports"));
                        restarted.Start("talk");
                        await WaitUntilIdle(restarted);
                        Assert.Equal("Completed", restarted.Status().State);
                        Assert.Equal(0, restarted.Status().CategoriesSkipped);
                        Assert.Null(store.Read().DirtyCategory);
                        Assert.Equal(3, imported.Count);
                    }
                }
                finally { File.Delete(path); }
            }

            [Fact]
            public void RefreshRoutesRequireAdministratorAndRejectMissingRequestContext()
            {
                foreach (var route in new[] { typeof(GetTuneInRefreshStatus), typeof(StartTuneInRefresh), typeof(CancelTuneInRefresh) })
                    Assert.Equal("Admin", Assert.Single(route.GetCustomAttributes(typeof(AuthenticatedAttribute), false).Cast<AuthenticatedAttribute>()).Roles);

                var config = new PluginConfiguration();
                var directory = new TuneInDirectory(() => config, (_, __) => throw new InvalidOperationException());
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(null), () => null,
                    (_, __, ___, ____, _____) => throw new InvalidOperationException(),
                    (_, __, ___, ____) => throw new InvalidOperationException(), null);
                var service = new TuneInRefreshService(refresh);
                Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInRefreshStatus()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new StartTuneInRefresh()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new StartTuneInRefresh { Category = "talk" }));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new CancelTuneInRefresh()));
            }

            [Fact]
            public async Task ProviderRetryAfterDelaysTheNextCatalogueRequest()
            {
                var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
                var start = now;
                var pacer = new TuneInRequestPacer(() => now, (delay, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    now += delay;
                    return Task.CompletedTask;
                });
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "30" };
                using var first = await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                { StatusCode = (HttpStatusCode)429, Headers = headers }), 3, CancellationToken.None);
                using var second = await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                { StatusCode = HttpStatusCode.OK }), 3, CancellationToken.None);
                Assert.Equal(start.AddSeconds(30), now);
            }

        private static async Task WaitUntilIdle(TuneInRefresh refresh)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (refresh.Status().Busy) await Task.Delay(10, timeout.Token);
        }
    }
}
