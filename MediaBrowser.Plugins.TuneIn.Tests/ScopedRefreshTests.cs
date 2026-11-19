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

        [Theory]
        [InlineData("no baseline")]
        [InlineData("saved parent")]
        [InlineData("new parent")]
        public async Task MalformedMusicLinkAppliesValidUpdatesOnlyWhenSavedBranchesCanBeRetained(string baseline)
        {
            var withBaseline = baseline != "no baseline";
            var canApply = baseline == "saved parent";
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, EnableTalk = true
            };
            config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            const string privateMarker = "not-for-diagnostics";
            var skip = false;
            var healed = false;
            var requests = new List<string>();
            string Link(string id, string filter = "") =>
                $"<outline type='link' text='{privateMarker}' URL='https://opml.radiotime.com/Browse.ashx?id={id}{filter}'/>";
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var scope = request.Parameters.TryGetValue("id", out var id) ? id : request.Parameters["c"];
                requests.Add(scope);
                var invalid = string.Concat(Enumerable.Repeat(Link("c123", "&amp;filter=private%2F" + privateMarker), 7))
                    + Link("c123", "#" + privateMarker);
                var rows = scope == "g2698"
                    ? (healed ? "" : !skip ? Link("c123") : baseline == "new parent" ? Link("c125") : invalid)
                        + Link("c124", "&amp;filter=s:~A")
                    : scope == "c125" ? invalid
                    : scope == "c123" ? Link("c126")
                    : $"<outline type='audio' guide_id='s1' text='{(scope == "c126" ? "Retained" : skip ? "Changed" : "Original")}'/>";
                return Task.FromResult(XDocument.Parse($"<opml><body>{rows}</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-partial-{Guid.NewGuid():N}.xml");
            var imports = 0;
            var artworkUpdates = 0;
            Dictionary<string, string[]> imported = null;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports++;
                        imported = ReadStagedTree(staged, token);
                        return Task.CompletedTask;
                    }, null, updateArtwork: (_, __, ___) => artworkUpdates++);
                if (withBaseline)
                {
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Contains("c123", requests);
                }
                var originalCheckpoint = withBaseline ? File.ReadAllText(path) : null;
                var originalArtworkUpdates = artworkUpdates;
                var savedMusic = withBaseline ? imported["Music"] : null;
                var savedTalk = withBaseline ? imported["Talk"] : null;
                requests.Clear();
                skip = true;
                refresh.Start(withBaseline ? "music" : null);
                await WaitUntilIdle(refresh);

                var status = refresh.Status();
                Assert.Equal("Partial", status.State);
                Assert.Contains(canApply ? "valid updates applied" : "existing library left unchanged", status.Phase);
                Assert.Equal(withBaseline ? 0 : 1, status.CategoriesCompleted);
                Assert.False(status.ImportMayHavePartiallyApplied);
                Assert.Null(status.DirtyCategory);
                Assert.Equal(8, status.SkippedBrowseLinks);
                Assert.Equal("7 x Music: Invalid catalog link parameter: filter; 1 x Music: Invalid catalog fragment",
                    status.SkippedBrowseLinkDetails);
                Assert.DoesNotContain(privateMarker, status.SkippedBrowseLinkDetails);
                Assert.Contains(status.Categories, category => category.Category == "music"
                    && category.State == (canApply ? "Partial (applied)" : "Incomplete (catalog links skipped)"));
                Assert.Contains(status.Categories, category => category.Category == "talk"
                    && category.State == (withBaseline ? "Imported" : "Checked (not applied)"));
                Assert.Contains("c124", requests);
                Assert.Equal(!withBaseline, requests.Contains("talk"));
                Assert.DoesNotContain("c123", requests);
                Assert.DoesNotContain("c126", requests);
                Assert.Equal(canApply ? 2 : withBaseline ? 1 : 0, imports);
                if (!withBaseline)
                {
                    Assert.False(File.Exists(path));
                    return;
                }

                if (!canApply)
                {
                    Assert.Equal(originalArtworkUpdates, artworkUpdates);
                    Assert.Equal(originalCheckpoint, File.ReadAllText(path));
                    return;
                }

                var before = savedMusic.Select(id => directory.Resolve(directory.Snapshot(), id).Name).ToArray();
                var after = imported["Music"].Select(id => directory.Resolve(directory.Snapshot(), id).Name).ToArray();
                Assert.Equal(new[] { "Original", "Retained" }, before.OrderBy(name => name));
                Assert.Equal(new[] { "Changed", "Retained" }, after.OrderBy(name => name));
                Assert.Equal(savedTalk, imported["Talk"]);
                var savedPartialMusic = imported["Music"];
                var checkpoint = new TuneInRefreshStore(path).Read();
                Assert.True(checkpoint.Categories.Single(category => category.Category == "music").DiscoveryPartial);
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.Equal("Partial", refresh.Status().State);
                Assert.Contains("merged library unchanged", refresh.Status().Phase);
                Assert.Equal(2, imports);

                requests.Clear();
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(0, refresh.Status().SkippedBrowseLinks);
                Assert.Equal(new[] { "talk" }, requests);
                Assert.Equal(3, imports);
                Assert.Equal(savedPartialMusic, imported["Music"]);
                Assert.NotEqual(Assert.Single(savedTalk), Assert.Single(imported["Talk"]));

                healed = true;
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal("Changed", directory.Resolve(directory.Snapshot(), Assert.Single(imported["Music"])).Name);
                Assert.False(new TuneInRefreshStore(path).Read().Categories.Single(category => category.Category == "music").DiscoveryPartial);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ScopedAndMultiCategoryRefreshesCanFetchBeyondThePreviousPageLimit(bool musicOnly)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, EnableTalk = !musicOnly
            };
            config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            var folderCount = TuneInCatalogueSession.MaxPages;
            var fetches = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                fetches++;
                var rows = request.Parameters.TryGetValue("id", out var id) && id == "g2698"
                    ? string.Concat(Enumerable.Range(1, folderCount).Select(n =>
                        $"<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c{n}'/>"))
                    : "<outline type='audio' guide_id='s1' text='Radio'/>";
                return Task.FromResult(XDocument.Parse($"<opml><body>{rows}</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-page-limit-{Guid.NewGuid():N}.xml");
            var imports = 0;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        Assert.Equal(folderCount, ReadStagedTree(staged, token)["Music"].Length);
                        imports++;
                        return Task.CompletedTask;
                    }, null);
                refresh.Start(musicOnly ? "music" : null);
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(folderCount + (musicOnly ? 1 : 2), fetches);
                Assert.Equal(1, imports);
                Assert.True(File.Exists(path));
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("music")]
        [InlineData("talk")]
        public async Task LongDiscoveryReusesPagesFetchedBeforeThePreviousTimeout(string category)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = category == "music", EnableTalk = category == "talk"
            };
            config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            var now = DateTime.UtcNow;
            var childFetches = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                request.Parameters.TryGetValue("id", out var id);
                var rows = id != "c1"
                    ? "<outline type='link' text='One' URL='https://opml.radiotime.com/Browse.ashx?id=c1'/>"
                        + "<outline type='link' text='Two' URL='https://opml.radiotime.com/Browse.ashx?id=c1'/>"
                    : "<outline type='audio' guide_id='s1' text='Radio'/>";
                if (id == "c1") childFetches++;
                return Task.FromResult(XDocument.Parse($"<opml><body>{rows}</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-long-music-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) =>
                    {
                        if (childFetches == 1) now = now.AddMinutes(21);
                        return c.GetRefreshChannelItems(folder, session, strict, token);
                    },
                    (_, staged, __, token) =>
                    {
                        Assert.Equal(2, ReadStagedTree(staged, token)[TuneInBrowsing.CategoryName(category)].Length);
                        return Task.CompletedTask;
                    }, null, clock: () => now);
                refresh.Start(category);
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(1, childFetches);
                Assert.Equal(1, refresh.Status().CacheHits);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task DiscoveryStopsAfterTenAutomaticRetriesAndLeavesExistingLibraryUntouched()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var block = false;
            var attempts = 0;
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                if (block)
                {
                    attempts++;
                    await Task.Delay(Timeout.Infinite, token);
                }
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-discovery-timeout-{Guid.NewGuid():N}.xml");
            var imports = 0;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        ReadStagedTree(staged, token);
                        imports++;
                        return Task.CompletedTask;
                    }, null, timeoutOverride: TimeSpan.FromSeconds(1));
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                var checkpoint = File.ReadAllText(path);

                block = true;
                refresh.Start("talk");
                await WaitUntilIdle(refresh, TimeSpan.FromSeconds(30));
                Assert.Equal("TimedOut", refresh.Status().State);
                Assert.Equal(11, refresh.Status().DiscoveryTimeouts);
                Assert.Equal(11, attempts);
                Assert.Null(refresh.Status().DiscoveryCheckpointUtc);
                Assert.Equal(1, imports);
                Assert.Equal(checkpoint, File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DiscoveryReusesTwentyMinuteCheckpointOnRetryOrRestart(bool restart)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var now = DateTime.UtcNow;
            var initial = now;
            var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new List<string>();
            var childAttempts = 0;
            var imports = 0;
            var path = Path.Combine(Path.GetTempPath(), $"tunein-discovery-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                var id = request.Parameters.GetValueOrDefault("id");
                requests.Add(id ?? "talk");
                if (id == null)
                {
                    now = initial.AddMinutes(19);
                    return XDocument.Parse("<opml><body><outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1'/></body></opml>");
                }
                if (id == "c1")
                {
                    Assert.False(File.Exists(path + ".discovery"));
                    now = initial.AddMinutes(20);
                    return XDocument.Parse("<opml><body><outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c2'/></body></opml>");
                }
                if (++childAttempts == 1)
                {
                    Assert.True(File.Exists(path + ".discovery"));
                    Assert.False(File.Exists(path));
                    blocked.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            TuneInRefresh NewRefresh() => new TuneInRefresh(directory, store, () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (_, staged, __, token) =>
                {
                    Assert.Single(ReadStagedTree(staged, token)["Talk"]);
                    imports++;
                    return Task.CompletedTask;
                }, null, clock: () => now, timeoutOverride: TimeSpan.FromSeconds(2));
            try
            {
                using (var refresh = NewRefresh())
                {
                    refresh.Start("talk");
                    await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(initial.AddMinutes(20), refresh.Status().DiscoveryCheckpointUtc);
                    var key = directory.Snapshot().Key;
                    Assert.NotNull(store.ReadDiscovery(key, "talk", now, TimeSpan.FromHours(48)));
                    Assert.Null(store.ReadDiscovery(key, null, now, TimeSpan.FromHours(48)));
                    Assert.Null(store.ReadDiscovery(new string('0', 64), "talk", now, TimeSpan.FromHours(48)));
                    Assert.Null(store.ReadDiscovery(key, "talk", initial.AddHours(48), TimeSpan.FromHours(48)));
                    if (restart) refresh.Cancel();
                    await WaitUntilIdle(refresh);
                    if (restart)
                    {
                        Assert.Equal("Cancelled", refresh.Status().State);
                        Assert.Equal(0, imports);
                    }
                    else
                    {
                        Assert.Equal("Completed", refresh.Status().State);
                        Assert.Equal(1, refresh.Status().DiscoveryTimeouts);
                        Assert.Contains("1 discovery timeout", refresh.Status().Phase);
                        Assert.True(refresh.Status().Resumed);
                    }
                }
                if (restart)
                {
                    now = initial.AddHours(47);
                    using var refresh = NewRefresh();
                    refresh.Start("talk");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.True(refresh.Status().Resumed);
                    Assert.Equal(0, refresh.Status().DiscoveryTimeouts);
                }
                Assert.Equal(new[] { "talk", "c1", "c2", "c2" }, requests);
                Assert.Equal(1, imports);
                Assert.True(File.Exists(path));
                Assert.False(File.Exists(path + ".discovery"));
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public async Task ForwardPagesDoNotConsumeTheFolderDepthBudget(bool music, bool paginate)
        {
            var category = music ? "music" : "talk";
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = music, EnableTalk = !music
            };
            const string genre = "g71";
            const string canonical = "c100002515";
            config.Music.Add(new CatalogSelection { Id = genre, Enabled = true });
            var fetches = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var index = ++fetches;
                var id = request.Parameters.GetValueOrDefault("id");
                var nextId = paginate ? (id == genre ? canonical : id) : "c" + index;
                var scope = nextId == null ? "c=talk" : "id=" + nextId;
                var page = paginate ? $"&amp;offset={index}&amp;filter=s" : "";
                var key = paginate ? "nextStations" : "child";
                var row = index < 12
                    ? $"<outline type='link' key='{key}' text='More' URL='https://opml.radiotime.com/Browse.ashx?{scope}{page}'/>"
                    : "<outline type='audio' guide_id='s1' text='Radio'/>";
                return Task.FromResult(XDocument.Parse($"<opml><body>{row}</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-depth-{Guid.NewGuid():N}.xml");
            var imports = 0;
            try
            {
                var store = new TuneInRefreshStore(path);
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        Assert.Single(ReadStagedTree(staged, token)[TuneInBrowsing.CategoryName(category)]);
                        imports++;
                        return Task.CompletedTask;
                    }, null);
                refresh.Start(category);
                await WaitUntilIdle(refresh);
                Assert.Equal(paginate ? "Completed" : "Error", refresh.Status().State);
                Assert.Equal(paginate ? 1 : 0, imports);
                if (paginate) Assert.Equal(12, fetches);
                else
                {
                    Assert.Contains("folder depth limit", refresh.GetFailureLog().Text);
                    Assert.False(File.Exists(path));
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task MusicRefreshRetainsSavedStationWhenFallbackUrlHasAnInvalidFragment()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true };
            config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            var mode = 0;
            var imports = new List<string[]>();
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                var host = mode == 2 ? "example.invalid" : "opml.radiotime.com";
                var fragment = mode == 0 ? "" : "#not-for-diagnostics";
                var rows = $"<outline type='audio' text='Saved' URL='https://{host}/Tune.ashx?id=s1{fragment}'/>"
                    + $"<outline type='audio' guide_id='s2' text='{(mode == 0 ? "Original" : "Updated")}'/>"
                    + (mode == 0 ? "" : "<outline type='audio' guide_id='s3' text='New'/>");
                return Task.FromResult(XDocument.Parse($"<opml><body>{rows}</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-audio-fragment-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports.Add(ReadStagedTree(staged, token)["Music"]
                            .Select(id => directory.Resolve(directory.Snapshot(), id).Name)
                            .OrderBy(name => name, StringComparer.Ordinal).ToArray());
                        return Task.CompletedTask;
                    }, null);
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(new[] { "Original", "Saved" }, Assert.Single(imports));

                mode = 1;
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal("Partial", status.State);
                Assert.Contains("valid updates applied", status.Phase);
                Assert.Equal(1, status.SkippedBrowseLinks);
                Assert.Contains("station URL: Invalid catalog fragment", status.SkippedBrowseLinkDetails);
                Assert.DoesNotContain("not-for-diagnostics", status.SkippedBrowseLinkDetails);
                Assert.Equal(2, imports.Count);
                Assert.Equal(new[] { "New", "Saved", "Updated" }, imports[1]);

                mode = 2;
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.Equal("Error", refresh.Status().State);
                Assert.Contains("Untrusted TuneIn catalog link", refresh.GetFailureLog().Text);
                Assert.Equal(2, imports.Count);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("untrusted", "Error", "InvalidDataException")]
        [InlineData("pagination", "Error", "InvalidDataException")]
        [InlineData("transport", "Error", "IOException")]
        [InlineData("cancelled", "Cancelled", "Cancelled")]
        public async Task UnrecoverableDiscoveryDoesNotBecomePartialAfterARejectedLink(string failure, string state, string errorCode)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                if (request.Parameters.ContainsKey("id"))
                {
                    if (failure == "transport") throw new IOException("Provider connection failed.");
                    if (failure == "cancelled") throw new OperationCanceledException(token);
                    throw new InvalidOperationException("The refused destination must not be fetched.");
                }
                var next = failure == "untrusted"
                    ? "<outline type='link' URL='https://example.invalid/Browse.ashx?id=c124'/>"
                    : failure == "pagination"
                        ? "<outline type='link' key='next' URL='https://opml.radiotime.com/Browse.ashx?id=c124&amp;offset=1'/>"
                        : "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c124'/>";
                return Task.FromResult(XDocument.Parse("<opml><body>"
                    + "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c123&amp;filter=invalid%2Ffilter'/>"
                    + next + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-fatal-{Guid.NewGuid():N}.xml");
            var imports = 0;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, __, ___, ____) => { imports++; return Task.CompletedTask; }, null);
                refresh.Start();
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal(state, status.State);
                Assert.Equal(errorCode, status.ErrorCode);
                Assert.Equal(1, status.SkippedBrowseLinks);
                Assert.Equal(0, imports);
                Assert.False(File.Exists(path));
                if (state == "Error")
                    Assert.Contains("Skipped malformed catalog links: 1. By reason: 1 x Talk: Invalid catalog link parameter: filter",
                        refresh.GetFailureLog().Text);
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

        [Theory]
        [InlineData("0", 3, 30, 12)]
        [InlineData("240", 3, 240, 12)]
        [InlineData("invalid", 30, 30, 30)]
        public async Task RateLimitsAdaptCataloguePacingWithoutChangingPlaybackInterval(
            string retryAfter, int interval, int firstCooldown, int learnedInterval)
        {
            var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
            var start = now;
            var pacer = new TuneInRequestPacer(() => now, (duration, token) =>
            {
                token.ThrowIfCancellationRequested();
                now += duration;
                return Task.CompletedTask;
            });
            Task<HttpResponseInfo> Limited() => Task.FromResult(new HttpResponseInfo
            {
                StatusCode = (HttpStatusCode)429,
                Headers = new Dictionary<string, string> { ["Retry-After"] = retryAfter }
            });
            Task<HttpResponseInfo> Success() => Task.FromResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            using var first = await pacer.SendAsync(Limited, interval, CancellationToken.None, catalogueRequest: true);
            using var second = await pacer.SendAsync(Limited, interval, CancellationToken.None, catalogueRequest: true);
            Assert.Equal(start.AddSeconds(firstCooldown), now);
            using var recovered = await pacer.SendAsync(Success, interval, CancellationToken.None, catalogueRequest: true);
            Assert.Equal(start.AddSeconds(firstCooldown + Math.Max(60, firstCooldown)), now);
            var recoveredAt = now;
            using var later = await pacer.SendAsync(Success, interval, CancellationToken.None, catalogueRequest: true);
            Assert.Equal(recoveredAt.AddSeconds(learnedInterval), now);
            var laterAt = now;
            using var playback = await pacer.SendAsync(Success, 3, CancellationToken.None);
            Assert.Equal(laterAt.AddSeconds(learnedInterval), now);
            var playbackAt = now;
            using var nextPlayback = await pacer.SendAsync(Success, 3, CancellationToken.None);
            Assert.Equal(playbackAt.AddSeconds(3), now);
            using var limitedAgain = await pacer.SendAsync(Limited, interval, CancellationToken.None, catalogueRequest: true);
            var limitedAt = now;
            using var resetCooldown = await pacer.SendAsync(Success, interval, CancellationToken.None, catalogueRequest: true);
            Assert.Equal(limitedAt.AddSeconds(firstCooldown), now);
        }

        [Fact]
        public async Task CancellingRateLimitCooldownDoesNotSendAnotherRequest()
        {
            var now = DateTime.UtcNow;
            using var cancelled = new CancellationTokenSource();
            var pacer = new TuneInRequestPacer(() => now, (_, token) =>
            {
                cancelled.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
            using var first = await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
            { StatusCode = (HttpStatusCode)429 }), 3, CancellationToken.None, catalogueRequest: true);
            var sent = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacer.SendAsync(() =>
            {
                sent = true;
                return Task.FromResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            }, 3, cancelled.Token, catalogueRequest: true));
            Assert.False(sent);
        }

        [Fact]
        public async Task RateLimitRetryBudgetIsSharedAcrossPagesAndDoesNotRetryOtherErrors()
        {
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => DateTime.UtcNow,
                retryRateLimit: true);
            for (var page = 0; page < 3; page++)
            {
                var request = new DirectoryRequest();
                request.Parameters["id"] = "page" + page;
                var attempts = 0;
                await session.FetchAsync(request, () =>
                {
                    if (++attempts == 1) throw new TuneInProviderResponseException(429);
                    return Task.FromResult(XDocument.Parse("<opml><body/></opml>"));
                }, CancellationToken.None);
                Assert.Equal(2, attempts);
            }
            var finalAttempts = 0;
            var error = await Assert.ThrowsAsync<TuneInProviderResponseException>(() =>
                session.FetchAsync(new DirectoryRequest(), () =>
                {
                    finalAttempts++;
                    throw new TuneInProviderResponseException(429);
                }, CancellationToken.None));
            Assert.Equal(429, error.StatusCode);
            Assert.Equal(1, finalAttempts);
            var otherSession = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => DateTime.UtcNow,
                retryRateLimit: true);
            var otherAttempts = 0;
            var other = await Assert.ThrowsAsync<TuneInProviderResponseException>(() =>
                otherSession.FetchAsync(new DirectoryRequest(), () =>
                {
                    otherAttempts++;
                    throw new TuneInProviderResponseException(503);
                }, CancellationToken.None));
            Assert.Equal(503, other.StatusCode);
            Assert.Equal(1, otherAttempts);
        }

        [Theory]
        [InlineData("45", 45, false, 1)]
        [InlineData("invalid", 30, true, 4)]
        [InlineData(null, 30, false, 2)]
        public async Task RefreshRetriesRateLimitedPageWithinRunBudget(
            string retryAfter, int seconds, bool persistent, int limitedAttempts)
        {
            var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
            var pacer = new TuneInRequestPacer(() => now, (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                now += delay;
                return Task.CompletedTask;
            });
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
            };
            var sportAttempts = new List<DateTime>();
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                if (request.Parameters["c"] == "sports")
                {
                    using var response = await pacer.SendAsync(() =>
                    {
                        sportAttempts.Add(now);
                        return Task.FromResult(new HttpResponseInfo
                        {
                            StatusCode = sportAttempts.Count <= limitedAttempts ? (HttpStatusCode)429 : HttpStatusCode.OK,
                            Headers = retryAfter == null ? null : new Dictionary<string, string> { ["Retry-After"] = retryAfter }
                        });
                    }, 3, token, catalogueRequest: true);
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TuneInProviderResponseException((int)response.StatusCode);
                }
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-rate-limit-{Guid.NewGuid():N}.xml");
            try
            {
                var store = new TuneInRefreshStore(path);
                var imports = 0;
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        Assert.Equal(2, ReadStagedTree(staged, token).Count);
                        imports++;
                        return Task.CompletedTask;
                    }, null, clock: () => now);
                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal(persistent ? 4 : limitedAttempts + 1, sportAttempts.Count);
                Assert.Equal(TimeSpan.FromSeconds(seconds), sportAttempts[1] - sportAttempts[0]);
                if (limitedAttempts > 1)
                    Assert.Equal(TimeSpan.FromSeconds(60), sportAttempts[2] - sportAttempts[1]);
                var status = refresh.Status();
                Assert.Equal(sportAttempts.Count + 1, status.RequestsStarted);
                Assert.Equal(429, status.LastHttpStatus);
                if (persistent)
                {
                    Assert.Equal(TimeSpan.FromSeconds(120), sportAttempts[3] - sportAttempts[2]);
                    Assert.Equal("Error", status.State);
                    Assert.Contains("rate limit reached", status.Phase);
                    Assert.Null(store.Read());
                    Assert.Equal(0, imports);
                }
                else
                {
                    Assert.Equal("Completed", status.State);
                    Assert.Equal(1, imports);
                    Assert.Null(store.Read().DirtyCategory);
                }
            }
            finally { File.Delete(path); }
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

        [Fact]
        public async Task EnablingCategoriesPreservesCompletedListingsAndAllowsIncrementalRefreshAfterRestart()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
            };
            var fetched = new List<string>();
            var failFavorites = false;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var category = request.Parameters["c"];
                fetched.Add(category);
                if (failFavorites && category == "presets") throw new InvalidDataException("Discovery failed");
                return Task.FromResult(XDocument.Parse(
                    "<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-add-categories-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            var imported = new List<Dictionary<string, string[]>>();
            TuneInRefresh Make() => new TuneInRefresh(directory, store, () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (_, staged, __, token) =>
                {
                    imported.Add(ReadStagedTree(staged, token));
                    return Task.CompletedTask;
                }, null);
            try
            {
                using (var refresh = Make())
                {
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    fetched.Clear();
                    refresh.UpdateConfiguration(() =>
                    {
                        config.Username = "tester";
                        config.EnableFavorites = true;
                        config.EnablePodcasts = true;
                    });
                    var state = refresh.Status();
                    Assert.All(state.Categories.Where(category => category.Category == "talk" || category.Category == "sports"),
                        category => Assert.Equal("Imported", category.State));
                    Assert.All(state.Categories.Where(category => category.Category == "presets" || category.Category == "podcast"),
                        category => Assert.Equal("No recorded refresh", category.State));
                    Assert.Empty(fetched);
                    Assert.Single(imported);
                }
                using (var refresh = Make())
                {
                    Assert.Equal(2, refresh.Status().Categories.Count(category => category.State == "Imported"));
                    failFavorites = true;
                    refresh.Start("presets");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Error", refresh.Status().State);
                    Assert.Single(imported);
                    Assert.Equal(2, store.Read().Categories.Count);
                    Assert.Equal(new[] { "podcast", "presets" }, store.Read().PendingCategories);

                    failFavorites = false;
                    fetched.Clear();
                    refresh.Start("presets");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Equal(new[] { "presets" }, fetched);
                    Assert.Equal(new[] { "Talk", "Sports", "My Favorites" }, imported[1].Keys);
                    var snapshot = directory.Snapshot();
                    Assert.All(imported[1]["Talk"], id => Assert.Equal("talk", directory.Resolve(snapshot, id).Category));
                    Assert.All(imported[1]["Sports"], id => Assert.Equal("sports", directory.Resolve(snapshot, id).Category));
                    Assert.Equal(new[] { "podcast" }, store.Read().PendingCategories);

                    fetched.Clear();
                    refresh.Start("podcast");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Single(fetched);
                    Assert.Equal(4, imported[2].Count);
                    Assert.All(refresh.Status().Categories, category => Assert.Equal("Imported", category.State));
                    Assert.Empty(store.Read().PendingCategories);

                    refresh.UpdateConfiguration(() => config.EnableSports = false);
                    Assert.DoesNotContain(refresh.Status().Categories, category => category.Category == "sports");
                    fetched.Clear();
                    refresh.Start("presets");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Equal(new[] { "presets" }, fetched);
                    Assert.DoesNotContain("Sports", imported[3].Keys);

                    refresh.UpdateConfiguration(() => config.Username = "different-user");
                    Assert.Equal("Imported", refresh.Status().Categories.Single(category => category.Category == "talk").State);
                    Assert.DoesNotContain(store.Read().Categories, category => category.Category == "presets");
                    Assert.DoesNotContain("presets", store.Read().PendingCategories);
                    fetched.Clear();
                    refresh.Start("talk");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("BaselineRequired", refresh.Status().State);
                    Assert.Empty(fetched);
                    refresh.Start("presets");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Equal(new[] { "presets" }, fetched);
                    Assert.Equal(3, imported[4].Count);
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task InterruptedFirstImportOfANewCategoryCannotBeOmittedByTheNextRefresh()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true
            };
            var fetched = new List<string>();
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                fetched.Add(request.Parameters["c"]);
                return Task.FromResult(XDocument.Parse(
                    "<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-pending-import-{Guid.NewGuid():N}.xml");
            try
            {
                var store = new TuneInRefreshStore(path);
                var failImport = false;
                var imports = 0;
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports++;
                        ReadStagedTree(staged, token);
                        if (failImport) throw new InvalidOperationException("Import partially applied");
                        return Task.CompletedTask;
                    }, null);
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                refresh.UpdateConfiguration(() => config.EnableSports = true);
                failImport = true;
                refresh.Start("sports");
                await WaitUntilIdle(refresh);
                Assert.Equal("sports", store.Read().DirtyCategory);
                Assert.Contains("sports", store.Read().PendingCategories);

                fetched.Clear();
                var choice = refresh.Start("talk");
                refresh.Start("talk", choice.RecoveryId, false);
                await WaitUntilIdle(refresh);
                Assert.Equal("BaselineRequired", refresh.Status().State);
                Assert.Empty(fetched);
                Assert.Equal(2, imports);
                Assert.Equal("sports", store.Read().DirtyCategory);

                failImport = false;
                refresh.Start("sports");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(new[] { "sports" }, fetched);
                Assert.Null(store.Read().DirtyCategory);
                Assert.Empty(store.Read().PendingCategories);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SavedItemRebindingRequiresAnUnchangedAuthorizedCategoryAndValidSignature()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableFavorites = true, Username = "first"
            };
            var directory = new TuneInDirectory(() => config, (_, __) =>
                throw new InvalidOperationException("Rebinding must not fetch the provider."));
            var previous = directory.Snapshot();
            var id = directory.Register(previous, new NavigationEntry { Category = "presets", Name = "My Favorites" });
            config.EnableTalk = true;
            var current = directory.Snapshot();
            var rebind = directory.CategoryRebinder(previous, current, "presets");
            Assert.Equal("presets", directory.Resolve(current, rebind(id)).Category);
            Assert.Throws<InvalidOperationException>(() => rebind(id.Substring(0, id.Length - 5) + "AAAAA"));

            config.Username = "second";
            var changedUser = directory.Snapshot();
            Assert.Throws<InvalidOperationException>(() => directory.CategoryRebinder(current, changedUser, "presets"));
            config.Username = "first";
            config.EnableFavorites = false;
            var disabled = directory.Snapshot();
            Assert.Throws<InvalidOperationException>(() => directory.CategoryRebinder(current, disabled, "presets"));
            Assert.Throws<InvalidOperationException>(() => directory.Resolve(disabled, id));
        }

        private static async Task WaitUntilIdle(TuneInRefresh refresh, TimeSpan? wait = null)
        {
            using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(15));
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
