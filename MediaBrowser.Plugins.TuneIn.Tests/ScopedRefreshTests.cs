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
            var artworkUpdates = 0;
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
                        ReadStagedTree(staged, token);
                        return Task.CompletedTask;
                    }, null, timeoutOverride: TimeSpan.FromSeconds(5),
                    updateArtwork: (_, __, ___) => Interlocked.Increment(ref artworkUpdates));
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
                Assert.Equal(2, artworkUpdates);
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
        public async Task RefreshFailureReportIsAdminOnlyBoundedAndAvailableOnlyForLatestFailedRun()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var fail = true;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                if (fail) throw new InvalidOperationException(new string('x', 70 * 1024));
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-report-{Guid.NewGuid():N}.xml");
            using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (c, staged, folder, token) =>
                {
                    ReadStagedTree(staged, token);
                    return Task.CompletedTask;
                }, null);
            try
            {
                Assert.Throws<InvalidOperationException>(() => refresh.GetFailureLog());
                refresh.Start();
                await WaitUntilIdle(refresh);
                var report = refresh.GetFailureLog();
                Assert.True(report.Truncated);
                Assert.Equal(64 * 1024, report.Text.Length);
                Assert.Contains("Category: talk", report.Text);
                Assert.Equal(refresh.Status().FinishedUtc, report.FinishedUtc);

                var service = new TuneInRefreshService(refresh);
                Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInRefreshFailureLog()));
                fail = false;
                refresh.Start();
                Assert.Throws<InvalidOperationException>(() => refresh.GetFailureLog());
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Throws<InvalidOperationException>(() => refresh.GetFailureLog());
            }
            finally { File.Delete(path); }
        }

            [Fact]
            public async Task SingleCategoryRefreshPreservesSiblingListingsWithoutFetchingThemAgain()
            {
                var config = new PluginConfiguration
                {
                    SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
                };
                var fetched = new List<string>();
                var imported = new List<Dictionary<string, string[]>>();
                var sportsId = "s2";
                var directory = new TuneInDirectory(() => config, (request, _) =>
                {
                    fetched.Add(request.Parameters["c"]);
                    var id = request.Parameters["c"] == "sports" ? sportsId : "s1";
                    return Task.FromResult(XDocument.Parse($"<opml><body><outline type='audio' guide_id='{id}' text='Station'/></body></opml>"));
                });
                var channel = new TuneInChannel(directory);
                var path = Path.Combine(Path.GetTempPath(), $"tunein-refresh-{Guid.NewGuid():N}.xml");
                var store = new TuneInRefreshStore(path);
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (c, staged, folder, token) => c.ImportAsync(staged, async () =>
                    {
                        Assert.Null(folder);
                        Task<Dictionary<string, string[]>> callbacks;
                        using (ExecutionContext.SuppressFlow())
                            callbacks = Task.Run(async () =>
                            {
                                var roots = await c.GetChannelItems(new InternalChannelItemQuery(), token);
                                var listings = new Dictionary<string, string[]>();
                                foreach (var root in roots.Items)
                                {
                                    var result = await c.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Id }, token);
                                    listings.Add(root.Name, result.Items.Select(item => item.Id).ToArray());
                                }
                                return listings;
                            });
                        imported.Add(await callbacks);
                    }), null);
                try
                {
                    Assert.Throws<ArgumentException>(() => refresh.Start("music"));
                    Assert.Throws<ArgumentException>(() => refresh.Start("https://example.test/"));
                    Assert.Empty(fetched);

                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    Assert.Equal(new[] { "talk", "sports" }, fetched);
                    Assert.Equal(new[] { "Talk", "Sports" }, Assert.Single(imported).Keys);
                    var talkCheckpoint = store.Read().Categories.Single(category => category.Category == "talk");
                    var originalTalkItems = imported[0]["Talk"];

                    sportsId = "s3";
                    refresh.Start("sports");
                    await WaitUntilIdle(refresh);
                    Assert.Equal(new[] { "talk", "sports", "sports" }, fetched);
                    Assert.Equal(2, imported.Count);
                    Assert.Equal(originalTalkItems, imported[1]["Talk"]);
                    Assert.NotEqual(imported[0]["Sports"].Single(), imported[1]["Sports"].Single());
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

                    // A checkpoint from the previous RC must not let an unchanged category skip repair.
                    var olderCheckpoint = store.Read();
                    olderCheckpoint.ChannelImportVersion = 0;
                    store.Write(olderCheckpoint);
                    fetched.Clear();
                    refresh.Start("sports");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("BaselineRequired", refresh.Status().State);
                    Assert.Empty(fetched);
                    Assert.Equal(2, imported.Count);
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Equal(new[] { "Talk", "Sports" }, imported[2].Keys);
                    Assert.Equal(new[] { "talk", "sports" }, fetched);
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
                var fetched = new List<string>();
                var directory = new TuneInDirectory(() => config, (request, _) =>
                {
                    fetched.Add(request.Parameters["c"]);
                    return Task.FromResult(XDocument.Parse("<opml><body>" + listing + "</body></opml>"));
                });
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
                        var roots = await c.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
                        Assert.Equal(new[] { "Talk", "Sports" }, roots.Items.Select(item => item.Name));
                        foreach (var categoryRoot in roots.Items)
                        {
                            var result = await c.GetChannelItems(new InternalChannelItemQuery { FolderId = categoryRoot.Id }, token);
                            if (categoryRoot.Name == "Talk") imported.Add(result.Items.Select(item => item.Id).ToArray());
                        }
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
                        refresh.Start();
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
                        Assert.Equal(new[] { "all" }, refresh.Start().QueuedRequests);
                        refresh.Cancel();
                        await WaitUntilIdle(refresh);
                        Assert.Equal("Cancelled", refresh.Status().State);
                        Assert.Empty(refresh.Status().QueuedRequests);
                        Assert.Equal("talk", store.Read().DirtyCategory);
                        Assert.Equal(2, imported.Last().Length);
                    }
                    interrupt = false;
                    listing = first; // Same as the last successful import, but a dirty category cannot skip.
                    fetched.Clear();
                    using (var restarted = Make())
                    {
                        Assert.Equal("talk", restarted.Status().DirtyCategory);
                        var choice = restarted.Start("sports");
                        Assert.True(choice.RecoveryChoiceRequired);
                        Assert.False(choice.Busy);
                        Assert.Throws<InvalidOperationException>(() => restarted.Start("sports", "stale", false));
                        restarted.Start("sports", choice.RecoveryId, false);
                        await WaitUntilIdle(restarted);
                        Assert.Equal("Completed", restarted.Status().State);
                        Assert.Equal(0, restarted.Status().CategoriesSkipped);
                        Assert.Null(store.Read().DirtyCategory);
                        Assert.Equal(3, imported.Count);
                        Assert.Equal(new[] { "sports" }, fetched);
                    }
                }
                finally { File.Delete(path); }
            }

            [Fact]
            public void RefreshRoutesRequireAdministratorAndRejectMissingRequestContext()
            {
                foreach (var route in new[] { typeof(GetTuneInRefreshStatus), typeof(StartTuneInRefresh),
                    typeof(GetTuneInRefreshFailureLog), typeof(CancelTuneInRefresh), typeof(SetTuneInSettingsLock) })
                    Assert.Equal("Admin", Assert.Single(route.GetCustomAttributes(typeof(AuthenticatedAttribute), false).Cast<AuthenticatedAttribute>()).Roles);

                var config = new PluginConfiguration();
                var directory = new TuneInDirectory(() => config, (_, __) => throw new InvalidOperationException());
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(null), () => null,
                    (_, __, ___, ____, _____) => throw new InvalidOperationException(),
                    (_, __, ___, ____) => throw new InvalidOperationException(), null);
                var service = new TuneInRefreshService(refresh);
                Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInRefreshStatus()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInRefreshFailureLog()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new StartTuneInRefresh()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new StartTuneInRefresh { Category = "talk" }));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new CancelTuneInRefresh()));
                Assert.Throws<UnauthorizedAccessException>(() => service.Post(new SetTuneInSettingsLock { Locked = false }));
            }

            [Fact]
            public void SettingsLockPersistsAndRejectsConfigurationWritesUntilUnlocked()
            {
                var path = Path.Combine(Path.GetTempPath(), $"tunein-lock-{Guid.NewGuid():N}.xml");
                try
                {
                    var store = new TuneInSettingsLockStore(path);
                    var saves = 0;
                    store.SaveConfiguration(() => saves++);
                    store.Set(true);
                    var reloaded = new TuneInSettingsLockStore(path);
                    Assert.Throws<InvalidOperationException>(() => reloaded.SaveConfiguration(() => saves++));
                    Assert.Equal(1, saves);
                    reloaded.Set(false);
                    new TuneInSettingsLockStore(path).SaveConfiguration(() => saves++);
                    Assert.Equal(2, saves);
                }
                finally { File.Delete(path); }
            }

            [Theory]
            [InlineData(1, null, 1)]
            [InlineData(30, null, 30)]
            [InlineData(60, null, 30)]
            [InlineData(1, "45", 45)]
            public async Task CatalogRequestsHonorTheConfiguredIntervalAndProviderRetryAfter(
                int interval, string retryAfter, int expectedDelay)
            {
                var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
                var start = now;
                var pacer = new TuneInRequestPacer(() => now, (delay, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    now += delay;
                    return Task.CompletedTask;
                });
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (retryAfter != null) headers["Retry-After"] = retryAfter;
                using var first = await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                { StatusCode = retryAfter == null ? HttpStatusCode.OK : (HttpStatusCode)429, Headers = headers }),
                    interval, CancellationToken.None);
                using var second = await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                { StatusCode = HttpStatusCode.OK }), interval, CancellationToken.None);
                Assert.Equal(start.AddSeconds(expectedDelay), now);
            }

        [Fact]
        public async Task SavedScheduleStartsOnlyAnEnabledDueCategoryAndDoesNotOverlap()
        {
            var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var scopeKey = new ConfigurationSnapshot(config).Key;
            config.RefreshSchedules.Add(new CategoryRefreshSchedule { Category = "talk", IntervalHours = 6 });
            config.RefreshSchedules.Add(new CategoryRefreshSchedule { Category = "sports", IntervalHours = 6 });
            Assert.Equal(scopeKey, new ConfigurationSnapshot(config).Key);
            ConfigurationSnapshot.Validate(config);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetches = 0;
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                Assert.Equal("talk", request.Parameters["c"]);
                Interlocked.Increment(ref fetches);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-scheduled-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        ReadStagedTree(staged, token);
                        return Task.CompletedTask;
                    }, null, clock: () => now, timeoutOverride: TimeSpan.FromSeconds(10));
                refresh.CheckSchedules();
                now = now.AddHours(6).AddSeconds(-1);
                refresh.CheckSchedules();
                Assert.Equal("NeverRun", refresh.Status().State);
                now = now.AddSeconds(1);
                refresh.CheckSchedules();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    refresh.CheckSchedules();
                    Assert.Throws<InvalidOperationException>(() => refresh.Start("talk"));
                    Assert.Equal(1, fetches);
                }
                finally { release.TrySetResult(true); }
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                refresh.CheckSchedules();
                Assert.False(refresh.Status().Busy);
                Assert.Equal(1, fetches);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(true, "talk")]
        [InlineData(false, "sports")]
        public async Task DiscoveryFailureRequiresAChoiceAndWaitingRequestsRunInOrder(bool retry, string first)
        {
        var config = new PluginConfiguration
        {
            SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true,
            EnableSports = true, EnablePodcasts = true
        };
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = false;
        var pause = false;
        var fetched = new List<string>();
        var directory = new TuneInDirectory(() => config, async (request, token) =>
        {
            if (fail) throw new InvalidDataException("Discovery failed");
            var name = request.Parameters.TryGetValue("c", out var category) ? category : "(no category)";
            fetched.Add(name);
            if (pause)
            {
                entered.TrySetResult(name);
                await release.Task.WaitAsync(token);
            }
            return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
        });
        var channel = new TuneInChannel(directory);
        var path = Path.Combine(Path.GetTempPath(), $"tunein-queued-{Guid.NewGuid():N}.xml");
        try
        {
            using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (_, staged, __, token) =>
                {
                    ReadStagedTree(staged, token);
                    return Task.CompletedTask;
                }, null);
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            var baselinePodcastRequest = fetched[2];
            fetched.Clear();
            fail = true;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            var choice = refresh.Start("sports");
            Assert.True(choice.RecoveryChoiceRequired);
            Assert.False(choice.Busy);
            Assert.Equal("talk", choice.RecoveryCategory);
            Assert.Null(choice.DirtyCategory);
            fail = false;
            pause = true;
            var started = refresh.Start("sports", choice.RecoveryId, retry);
            Assert.Equal(first, started.ActiveRequest);
            Assert.Equal(first, await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            if (!retry) refresh.Start("talk");
            var queued = refresh.Start("podcast");
            Assert.Equal(new[] { retry ? "sports" : "talk", "podcast" }, queued.QueuedRequests);
            Assert.Equal(0, queued.QueueSlotsAvailable);
            Assert.Throws<InvalidOperationException>(() => refresh.Start());
            Assert.Throws<InvalidOperationException>(() => refresh.Start("sports"));
            release.TrySetResult(true);
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Empty(refresh.Status().QueuedRequests);
            Assert.Equal(new[] { first, retry ? "sports" : "talk", baselinePodcastRequest }, fetched);
        }
        finally { File.Delete(path); }
        }

        [Fact]
        public async Task FavoritesRequiresABaselineAndThenImportsWithoutFetchingMusic()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, Username = "tester",
                EnableFavorites = true, EnableMusic = true
            };
            config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            var requests = new List<string>();
            var imports = new List<Dictionary<string, string[]>>();
            var favoriteStation = "s2";
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var scope = request.Parameters.TryGetValue("id", out var id) ? id : request.Parameters["c"];
                requests.Add(scope);
                var station = scope == "presets" ? favoriteStation : "s1";
                return Task.FromResult(XDocument.Parse(
                    $"<opml><body><outline type='audio' guide_id='{station}' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-favorites-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports.Add(ReadStagedTree(staged, token));
                        return Task.CompletedTask;
                    }, null);
                refresh.Start("presets");
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal("presets", status.Category);
                Assert.Equal("BaselineRequired", status.State);
                Assert.Contains("Refresh all enabled categories", status.Phase);
                Assert.Empty(requests);
                Assert.Empty(imports);
                Assert.Null(status.DirtyCategory);

                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(new[] { "g2698", "presets" }, requests);
                var baseline = Assert.Single(imports);
                requests.Clear();
                favoriteStation = "s3";

                refresh.Start("presets");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(new[] { "presets" }, requests);
                Assert.Equal(2, imports.Count);
                Assert.Equal(Assert.Single(baseline["Music"]), Assert.Single(imports[1]["Music"]));
                Assert.NotEqual(Assert.Single(baseline["My Favorites"]), Assert.Single(imports[1]["My Favorites"]));
            }
            finally { File.Delete(path); }
        }

        private static async Task WaitUntilIdle(TuneInRefresh refresh)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (refresh.Status().Busy) await Task.Delay(10, timeout.Token);
        }

        private static Dictionary<string, string[]> ReadStagedTree(TuneInStagedImport staged, CancellationToken token)
        {
            string[] ReadDescendants(string id) => staged.Read(id, token).Items.SelectMany(item =>
                item.Type == ChannelItemType.Folder ? ReadDescendants(item.Id) : new[] { item.Id }).ToArray();
            return staged.Read(null, token).Items.ToDictionary(root => root.Name, root => ReadDescendants(root.Id));
        }
    }
}
