using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Logging;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Moq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public sealed class ScopedRefreshTests
    {
        [Fact]
        public void RefreshStatusReportsRunningPluginVersionAndCommit()
        {
            var status = new TuneInRefreshStatus();
            Assert.Equal(PluginBuild.Display, status.PluginVersion);
            Assert.Equal(PluginBuild.Display, status.Copy().PluginVersion);
            Assert.Matches(@"^v1\.2\.8\.0( \([0-9a-f]{1,7}\))?$", status.PluginVersion);
        }

        [Fact]
        public async Task ChangingSelectedGenresRetainsOnlyAllowedEntriesAndKeepsMissingStations()
        {
            var config = new PluginConfiguration
            { SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, MusicMode = "selected" };
            config.Music.Add(new CatalogSelection { Id = "g71", Enabled = true });
            config.Music.Add(new CatalogSelection { Id = "g79", Enabled = true });
            var secondRun = false;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var genre = request.Parameters["id"];
                var rows = secondRun && genre == "g79" ? ""
                    : "<outline type='audio' guide_id='" + (genre == "g71" ? "s1" : genre == "g79" ? "s2" : "s3") + "' text='Radio'/>";
                return Task.FromResult(XDocument.Parse("<opml><body>" + rows + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), "tunein-retained-filters-" + Guid.NewGuid().ToString("N"));
            var store = new TuneInRefreshStore(path);
            using var refresh = new TuneInRefresh(directory, store, () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (_, staged, _, token) => { ReadStagedTree(staged, token); return Task.CompletedTask; }, null);
            try
            {
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                refresh.UpdateConfiguration(() =>
                {
                    config.Music.RemoveAll(selection => selection.Id == "g71");
                    config.Music.Add(new CatalogSelection { Id = "g133", Enabled = true });
                });
                var snapshot = directory.Snapshot();
                var remaining = store.Read().Categories.Single().Folders.SelectMany(folder => folder.Items)
                    .Where(item => item.Type == "Media").Select(item => directory.Resolve(snapshot, item.Id).StationId);
                Assert.Equal(new[] { "s2" }, remaining);
                secondRun = true;
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(new[] { "s2", "s3" }, store.Read().Categories.Single().Folders.SelectMany(folder => folder.Items)
                    .Where(item => item.Type == "Media").Select(item => directory.Resolve(snapshot, item.Id).StationId).OrderBy(id => id));
            }
            finally { store.Clear(); store.ClearDiscovery(); store.ClearRecovery(); }
        }

        [Fact]
        public void LibraryInventoryRetainsUncachedStationsButRejectsUnverifiedDescendants()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, _) => throw new InvalidOperationException());
            var snapshot = directory.Snapshot();
            var library = new Mock<ILibraryManager>();
            var channel = new TuneInChannel(directory, library: library.Object);
            var root = channel.GetCategoryRoot(snapshot, "talk");
            var stationId = directory.Register(snapshot, new NavigationEntry { Category = "talk", StationId = "s1", Name = "Uncached" });
            var station = new MediaBrowser.Controller.Entities.Audio.Audio { InternalId = 3, ParentId = 2, ExternalId = stationId, Name = "Uncached" };
            library.Setup(api => api.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<CancellationToken>()))
                .Returns((InternalItemsQuery query, CancellationToken _) => query.IncludeItemTypes?.Contains(nameof(Channel)) == true
                    ? new BaseItem[] { new Channel { InternalId = 1, Name = "TuneIn" } }
                    : query.Recursive ? new BaseItem[] { station }
                    : new BaseItem[] { new Folder { InternalId = 2, ParentId = 1, ExternalId = root.Id } });
            var retained = channel.ReadCategorySnapshot(snapshot, "talk", CancellationToken.None);
            Assert.Equal(stationId, Assert.Single(Assert.Single(retained.Folders).Items).Id);
            station.ExternalId = "unverified";
            Assert.Throws<InvalidOperationException>(() => channel.ReadCategorySnapshot(snapshot, "talk", CancellationToken.None));
        }

        [Theory]
        [InlineData("local")]
        [InlineData("music")]
        [InlineData("language")]
        [InlineData("location")]
        [InlineData("talk")]
        [InlineData("sports")]
        [InlineData("podcast")]
        [InlineData("presets")]
        public void LibraryInventoryIgnoresRepeatedItemsInEveryCategory(string category)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, Username = "listener", EnableLocalRadio = true, EnableMusic = true,
                EnableLanguage = true, EnableLocation = true, EnableTalk = true, EnableSports = true, EnablePodcasts = true,
                EnableFavorites = true, LocalRadioSource = "coordinates", UseCoordinates = true, LatLon = "40.7,-74.0"
            };
            var directory = new TuneInDirectory(() => config, (_, _) => throw new InvalidOperationException());
            var snapshot = directory.Snapshot();
            var library = new Mock<ILibraryManager>();
            var channel = new TuneInChannel(directory, library: library.Object);
            var root = channel.GetCategoryRoot(snapshot, category);
            var stationId = directory.Register(snapshot, new NavigationEntry
            {
                Category = category, StationId = "s1", Name = "Repeated",
                SelectionId = category == "local" ? "coordinates" : null, ContentCategory = category == "language" ? "talk" : null
            });
            var rootFolder = new Folder { InternalId = 2, ParentId = 1, ExternalId = root.Id };
            var station = new MediaBrowser.Controller.Entities.Audio.Audio { InternalId = 3, ParentId = 2, ExternalId = stationId, Name = "Repeated" };
            library.Setup(api => api.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<CancellationToken>()))
                .Returns((InternalItemsQuery query, CancellationToken _) => query.IncludeItemTypes?.Contains(nameof(Channel)) == true
                    ? new BaseItem[] { new Channel { InternalId = 1, Name = "TuneIn" } }
                    : query.Recursive ? new BaseItem[] { station, rootFolder, station }
                    : new BaseItem[] { rootFolder });
            var retained = channel.ReadCategorySnapshot(snapshot, category, CancellationToken.None);
            Assert.Equal(stationId, Assert.Single(Assert.Single(retained.Folders).Items).Id);
        }

        [Theory]
        [InlineData(2, 2, 3)]
        [InlineData(null, 3, 4)]
        public async Task DiscoveryCountsDistinctStationsAcrossMusicGenres(int? cap, int unique, int placements)
        {
            var directory = new TuneInDirectory(() => new PluginConfiguration(), (_, _) => throw new InvalidOperationException());
            var channel = new TuneInChannel(directory);
            var root = new ChannelItemInfo { Id = "root", Name = "Music", Type = ChannelItemType.Folder };
            ChannelItemInfo Item(string id, ChannelItemType type) => new ChannelItemInfo { Id = id, Name = id, Type = type };
            var graph = new Dictionary<string, ChannelItemResult>
            {
                ["root"] = new ChannelItemResult { Items = new List<ChannelItemInfo>
                    { Item("genre-a", ChannelItemType.Folder), Item("genre-b", ChannelItemType.Folder) } },
                ["genre-a"] = new ChannelItemResult { Items = new List<ChannelItemInfo>
                    { Item("s1:a", ChannelItemType.Media), Item("s2:a", ChannelItemType.Media) } },
                ["genre-b"] = new ChannelItemResult { Items = new List<ChannelItemInfo>
                    { Item("s1:b", ChannelItemType.Media), Item("s3:b", ChannelItemType.Media) } }
            };
            var staged = await TuneInStagedImport.DiscoverAsync(channel, root,
                new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow), CancellationToken.None,
                (id, _, _, _) => Task.FromResult(graph[id]), null, null, (_, _) => false,
                maxStationCount: cap, stationId: id => id.Split(':')[0]);
            Assert.Equal(unique, staged.CountStations(id => id.Split(':')[0]));
            var snapshot = staged.CreateSnapshot("music");
            Assert.Equal(placements, snapshot.Folders.Sum(folder => folder.Items.Count(item => item.Type == "Media")));
            Assert.Equal(cap.HasValue, snapshot.DiscoveryLimitReason != null);
            var import = TuneInStagedImport.ForChannel(new[] { root }, new[] { snapshot }, CancellationToken.None, null, null);
            ReadStagedTree(import, CancellationToken.None);
            import.Verify();
        }

        [Fact]
        public async Task MoreStationsPagesAreListedInTheirGenreInsteadOfAsFolders()
        {
            var directory = new TuneInDirectory(() => new PluginConfiguration(), (_, _) => throw new InvalidOperationException());
            var channel = new TuneInChannel(directory);
            var root = new ChannelItemInfo { Id = "root", Name = "Music", Type = ChannelItemType.Folder };
            ChannelItemInfo Item(string id, ChannelItemType type) => new ChannelItemInfo { Id = id, Name = id, Type = type };
            var graph = new Dictionary<string, ChannelItemResult>
            {
                ["root"] = new ChannelItemResult { Items = new List<ChannelItemInfo> { Item("genre", ChannelItemType.Folder) } },
                ["genre"] = new ChannelItemResult { Items = new List<ChannelItemInfo>
                    { Item("s1", ChannelItemType.Media), Item("sub", ChannelItemType.Folder), Item("page-1", ChannelItemType.Folder) } },
                ["sub"] = new ChannelItemResult { Items = new List<ChannelItemInfo> { Item("s4", ChannelItemType.Media) } },
                ["page-1"] = new ChannelItemResult { Items = new List<ChannelItemInfo>
                    { Item("s2", ChannelItemType.Media), Item("page-2", ChannelItemType.Folder) } },
                ["page-2"] = new ChannelItemResult { Items = new List<ChannelItemInfo>
                    { Item("s1", ChannelItemType.Media), Item("s3", ChannelItemType.Media) } }
            };
            var staged = await TuneInStagedImport.DiscoverAsync(channel, root,
                new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow), CancellationToken.None,
                (id, _, _, _) => Task.FromResult(graph[id]), null, null,
                (parent, child) => child.StartsWith("page", StringComparison.Ordinal) || child == "old-page");
            var snapshot = staged.CreateSnapshot("music");
            Assert.Equal(new[] { "genre", "root", "sub" }, snapshot.Folders.Select(folder => folder.Id));
            Assert.Equal(new[] { "s1", "sub", "s2", "s3" },
                snapshot.Folders.Single(folder => folder.Id == "genre").Items.Select(item => item.Id));

            var old = new TuneInCategorySnapshot { Category = "music", RootId = "root" };
            old.Folders.Add(new TuneInFolderSnapshot { Id = "root", Items = new List<TuneInItemSnapshot>
                { new TuneInItemSnapshot { Id = "genre", Name = "genre", Type = "Folder", ContentType = "Song", MediaType = "Audio" } } });
            old.Folders.Add(new TuneInFolderSnapshot { Id = "genre", Items = new List<TuneInItemSnapshot>
                { new TuneInItemSnapshot { Id = "old-page", Name = "More Stations", Type = "Folder", ContentType = "Song", MediaType = "Audio" } } });
            old.Folders.Add(new TuneInFolderSnapshot { Id = "old-page", Items = new List<TuneInItemSnapshot>
                { new TuneInItemSnapshot { Id = "s9", Name = "s9", Type = "Media", ContentType = "Song", MediaType = "Audio" } } });
            var merged = staged.MergeRetainedCategory("music", old, id => id, false);
            Assert.DoesNotContain(merged.Folders, folder => folder.Id.Contains("page"));
            Assert.Contains(merged.Folders.Single(folder => folder.Id == "genre").Items, item => item.Id == "s9");
            var import = TuneInStagedImport.ForChannel(new[] { root }, new[] { merged }, CancellationToken.None, null, null);
            ReadStagedTree(import, CancellationToken.None);
            import.Verify();
        }

        [Fact]
        public async Task RetentionKeepsMissingStationsAndDeepFoldersAndUpdatesMetadataWithoutMovingIds()
        {
            var directory = new TuneInDirectory(() => new PluginConfiguration(), (_, _) => throw new InvalidOperationException());
            var channel = new TuneInChannel(directory);
            var root = new ChannelItemInfo { Id = "root", Name = "Music", Type = ChannelItemType.Folder };
            var old = new TuneInCategorySnapshot { Category = "music", RootId = "root" };
            old.Folders.Add(new TuneInFolderSnapshot { Id = "root", Items = new List<TuneInItemSnapshot>
            {
                new TuneInItemSnapshot { Id = "s1:old", Name = "Old", Type = "Media", ContentType = "Song", MediaType = "Audio", ImageUrl = "https://old.test/image.png" },
                new TuneInItemSnapshot { Id = "deep-1", Name = "More Stations", Type = "Folder", ContentType = "Song", MediaType = "Audio" }
            } });
            for (var depth = 1; depth <= 7; depth++)
                old.Folders.Add(new TuneInFolderSnapshot { Id = "deep-" + depth, Items = new List<TuneInItemSnapshot>
                { new TuneInItemSnapshot { Id = depth == 7 ? "s2:old" : "deep-" + (depth + 1), Name = "Retained",
                    Type = depth == 7 ? "Media" : "Folder", ContentType = "Song", MediaType = "Audio" } } });

            var staged = await TuneInStagedImport.DiscoverAsync(channel, root,
                new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow), CancellationToken.None,
                (id, _, _, _) => Task.FromResult(new ChannelItemResult { Items = id == "root"
                    ? new List<ChannelItemInfo> { new ChannelItemInfo { Id = "page-new", Name = "More Stations", Type = ChannelItemType.Folder } }
                    : new List<ChannelItemInfo>
                    { new ChannelItemInfo { Id = "s1:new", Name = "Updated", Type = ChannelItemType.Media,
                        ImageUrl = "https://new.test/image.png" },
                      new ChannelItemInfo { Id = "s3:new", Name = "New", Type = ChannelItemType.Media } } }),
                null, null, (_, _) => false, maxStationCount: 1, traversalDepthLimit: 3,
                stationId: id => id.Split(':')[0]);
            var merged = staged.MergeRetainedCategory("music", old, id => id.Split(':')[0], false);
            var updated = merged.Folders.Single(folder => folder.Id == "root").Items.Single(item => item.Type == "Media");
            Assert.Equal("s1:old", updated.Id);
            Assert.Equal("Updated", updated.Name);
            Assert.True(updated.ForceUpdate);
            Assert.Equal("https://new.test/image.png", updated.ImageUrl);
            Assert.Contains(merged.Folders.SelectMany(folder => folder.Items), item => item.Id == "s2:old");
            Assert.DoesNotContain(merged.Folders.SelectMany(folder => folder.Items), item => item.Id == "s3:new");
            Assert.DoesNotContain(merged.Folders, folder => folder.Id == "page-new");
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(TuneInCategorySnapshot));
            using var xml = new StringWriter();
            serializer.Serialize(xml, merged);
            var recovered = (TuneInCategorySnapshot)serializer.Deserialize(new StringReader(xml.ToString()));
            var import = TuneInStagedImport.ForChannel(new[] { root }, new[] { recovered }, CancellationToken.None, null, null);
            Assert.True(import.ImportRefreshLevel >= 9);
            Assert.Equal(2, ReadStagedTree(import, CancellationToken.None)["Music"].Length);
            Assert.True(import.Read("root", CancellationToken.None).Items.Single(item => item.Type == ChannelItemType.Media).ForceUpdate);
            import.Verify();
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ImportContentsIdentifyRemainingFoldersWithoutCountingRepeatedReads(bool verbose)
        {
            var root = new ChannelItemInfo { Id = "private-root", Name = "Music", Type = ChannelItemType.Folder };
            TuneInItemSnapshot Item(string id, string name, string type) => new TuneInItemSnapshot
            {
                Id = id, Name = name, Type = type, ContentType = "Song", MediaType = "Audio"
            };
            var category = new TuneInCategorySnapshot
            {
                RootId = root.Id,
                Folders = new List<TuneInFolderSnapshot>
                {
                    new TuneInFolderSnapshot { Id = root.Id, Items = new List<TuneInItemSnapshot>
                    {
                        Item("private-station", "Radio", "Media"),
                        Item("private-page-1", "More Stations", "Folder"),
                        Item("private-page-2", "More Stations\nhttps://private.invalid/secret token=secret", "Folder")
                    } },
                    new TuneInFolderSnapshot { Id = "private-page-1", Items = new List<TuneInItemSnapshot>
                    {
                        Item("private-station", "Radio", "Media"), Item("private-other-station", "Other", "Media")
                    } },
                    new TuneInFolderSnapshot { Id = "private-page-2" }
                }
            };
            var updates = new List<TuneInImportProgress>();
            using var cancellation = new CancellationTokenSource();
            var staged = TuneInStagedImport.ForChannel(new[] { root }, new[] { category }, cancellation.Token,
                null, null, reportProgress: updates.Add, verboseDiagnostics: verbose);
            Assert.Equal(3, updates.Last().StationEntriesTotal);
            Assert.Equal(3, updates.Last().SubfolderEntriesTotal);
            Assert.Null(updates.Last().LastFolderStationEntries);
            staged.Read(null, cancellation.Token);
            staged.Read(root.Id, cancellation.Token);
            var progress = updates.Last();
            Assert.Equal(1, progress.StationEntriesServed);
            Assert.Equal(3, progress.SubfolderEntriesServed);
            Assert.Equal(1, progress.LastFolderStationEntries);
            Assert.Equal(2, progress.LastFolderSubfolderEntries);
            if (verbose)
            {
                Assert.Equal(2, progress.PendingFolders.Length);
                Assert.Equal(new[] { 2, 0 }, progress.PendingFolders.Select(folder => folder.StationEntries));
                Assert.Equal(2, progress.PendingFolders.Select(folder => folder.Reference).Distinct().Count());
                Assert.Contains("TuneIn > Music > More Stations", progress.PendingFolders[0].Path);
                var description = string.Join("\n", TuneInImportStallDiagnostics.DescribePending(progress));
                Assert.DoesNotContain("secret", description);
                Assert.DoesNotContain("private-", description);
                Assert.DoesNotContain("https", description);
                Assert.DoesNotContain("\nhttps", progress.PendingFolders[1].Name);
            }
            else
            {
                Assert.Null(progress.PendingFolders);
                Assert.Empty(TuneInImportStallDiagnostics.DescribePending(progress));
            }
            var count = updates.Count;
            staged.Read(root.Id, cancellation.Token);
            Assert.Equal(count, updates.Count);
            Assert.Same(progress, updates.Last());
            staged.Read("private-page-1", cancellation.Token);
            Assert.Equal(3, updates.Last().StationEntriesServed);
            if (verbose) Assert.Single(updates.Last().PendingFolders);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => staged.Read("private-page-2", CancellationToken.None));
            Assert.Equal(0, progress.PendingFoldersOmitted);
            Assert.Equal(3, updates.Last().StationEntriesServed);
        }

        [Fact]
        public void ScopedImportBoundsPendingDetailsAndDoesNotCountSyntheticChannelRoot()
        {
            var root = new ChannelItemInfo { Id = "root", Name = "Talk", Type = ChannelItemType.Folder };
            var pages = Enumerable.Range(0, 60).Select(index => new TuneInItemSnapshot
            {
                Id = "page-" + index, Name = "Page " + index, Type = "Folder", ContentType = "Song", MediaType = "Audio"
            }).ToList();
            var category = new TuneInCategorySnapshot
            {
                RootId = root.Id,
                Folders = new[] { new TuneInFolderSnapshot { Id = root.Id, Items = pages } }
                    .Concat(pages.Select(page => new TuneInFolderSnapshot { Id = page.Id })).ToList()
            };
            var updates = new List<TuneInImportProgress>();
            var staged = TuneInStagedImport.ForChannel(new[] { root }, new[] { category }, CancellationToken.None,
                null, null, root.Id, updates.Add, true);
            Assert.Equal(50, updates.Last().PendingFolders.Length);
            Assert.Equal(11, updates.Last().PendingFoldersOmitted);
            Assert.Equal("Talk", updates.Last().PendingFolders[0].Path);
            staged.Read(null, CancellationToken.None);
            Assert.Single(updates);
            Assert.Equal(0, updates.Last().SubfolderEntriesServed);
            staged.Read(root.Id, CancellationToken.None);
            Assert.Equal(60, updates.Last().SubfolderEntriesServed);
            foreach (var page in pages) staged.Read(page.Id, CancellationToken.None);
            Assert.Empty(updates.Last().PendingFolders);
            Assert.Equal(0, updates.Last().PendingFoldersOmitted);
            staged.Verify();
        }

        [Fact]
        public void DeepPaginationDiagnosticsBoundPathsAndKeepTheLeafIdentifiable()
        {
            var root = new ChannelItemInfo { Id = "root", Name = "Music", Type = ChannelItemType.Folder };
            var folders = Enumerable.Range(0, 8).Select(index => new TuneInFolderSnapshot
            {
                Id = index == 0 ? root.Id : "page-" + index,
                Items = index == 7 ? new List<TuneInItemSnapshot>() : new List<TuneInItemSnapshot>
                {
                    new TuneInItemSnapshot
                    {
                        Id = "page-" + (index + 1),
                        Name = "More Stations " + new string('x', 70) + " " + new string('y', 70),
                        Type = "Folder", ContentType = "Song", MediaType = "Audio"
                    }
                }
            }).ToList();
            TuneInImportProgress progress = null;
            var staged = TuneInStagedImport.ForChannel(new[] { root },
                new[] { new TuneInCategorySnapshot { RootId = root.Id, Folders = folders } },
                CancellationToken.None, null, null, reportProgress: value => progress = value, verboseDiagnostics: true);
            staged.Read(null, CancellationToken.None);
            foreach (var folder in folders.Take(7)) staged.Read(folder.Id, CancellationToken.None);
            var leaf = Assert.Single(progress.PendingFolders);
            Assert.StartsWith("... ", leaf.Path);
            Assert.True(leaf.Path.Length <= 512);
            Assert.True(leaf.Name.Length <= 128);
            Assert.EndsWith(leaf.Name, leaf.Path);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ImportTimeoutRetainsPendingContentsAndGatesVerboseReports(bool verbose)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true,
                EnableCatalogIdentityDiagnostics = verbose
            };
            var directory = new TuneInDirectory(() => config, (_, __) =>
                Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>")));
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-pending-diagnostics-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            var logger = new Mock<ILogger>();
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    async (_, staged, __, token) =>
                    {
                        staged.Read(null, token);
                        await Task.Delay(Timeout.Infinite, token);
                    }, logger.Object, timeoutOverride: TimeSpan.FromSeconds(1));
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                var stopped = refresh.Status();
                Assert.Equal("TimedOut", stopped.State);
                Assert.Equal(1, stopped.ImportFoldersServed);
                Assert.Equal(2, stopped.ImportFoldersTotal);
                Assert.Equal(1, stopped.ImportProgress.StationEntriesTotal);
                Assert.Equal(0, stopped.ImportProgress.StationEntriesServed);
                Assert.Equal(1, stopped.ImportProgress.SubfolderEntriesServed);
                var report = refresh.GetFailureLog().Text;
                var verboseCalls = logger.Invocations.Where(invocation =>
                    invocation.Method.Name == nameof(ILogger.Warn)
                    && ((string)invocation.Arguments[0]) == "{0}"
                    && ((string)((object[])invocation.Arguments[1])[0]).StartsWith("TuneIn unserved folder", StringComparison.Ordinal));
                if (verbose)
                {
                    var pending = Assert.Single(stopped.ImportProgress.PendingFolders);
                    Assert.Contains("Talk", pending.Path);
                    Assert.Equal(1, pending.StationEntries);
                    Assert.Contains("TuneIn unserved folder", report);
                    Assert.NotEmpty(verboseCalls);
                    Assert.DoesNotContain(channel.GetCategoryRoot(directory.Snapshot(), "talk").Id, report);
                }
                else
                {
                    Assert.Null(stopped.ImportProgress.PendingFolders);
                    Assert.DoesNotContain("TuneIn unserved folder", report);
                    Assert.Empty(verboseCalls);
                }
            }
            finally
            {
                store.Clear();
                store.ClearDiscovery();
                store.ClearRecovery();
            }
        }

        [Fact]
        public void IdentityDiagnosticsSeparateStationChangesFromFolderMembershipChanges()
        {
            var ids = new Dictionary<string, string>
            {
                ["old-name-id"] = "s1", ["new-name-id"] = "s1",
                ["stable-id"] = "s2", ["removed-id"] = "s3", ["added-id"] = "s4"
            };
            TuneInItemSnapshot Item(string id, string name, string type = "Song") =>
                new TuneInItemSnapshot
                {
                    Id = id, Name = name, Type = "Media", ContentType = type, MediaType = "Audio"
                };
            TuneInCategorySnapshot Catalog(params TuneInFolderSnapshot[] folders) =>
                new TuneInCategorySnapshot { Folders = folders.ToList() };
            var saved = Catalog(
                new TuneInFolderSnapshot
                {
                    Id = "parent-a",
                    Items = new List<TuneInItemSnapshot>
                    {
                        Item("old-name-id", "private old label"),
                        Item("stable-id", "unchanged"), Item("removed-id", "removed")
                    }
                },
                new TuneInFolderSnapshot
                {
                    Id = "parent-b", Items = new List<TuneInItemSnapshot> { Item("stable-id", "unchanged") }
                });
            var fresh = Catalog(
                new TuneInFolderSnapshot
                {
                    Id = "parent-a",
                    Items = new List<TuneInItemSnapshot>
                    {
                        Item("new-name-id", "private new label", "Podcast"),
                        Item("added-id", "https://private.example/secret")
                    }
                },
                new TuneInFolderSnapshot
                {
                    Id = "parent-b",
                    Items = new List<TuneInItemSnapshot> { Item("stable-id", "unchanged") }
                });
            var result = TuneInIdentityDiagnostics.Compare(saved, fresh, id => ids[id], CancellationToken.None);
            Assert.True(result.HasSavedBaseline);
            Assert.Equal(3, result.SavedStations);
            Assert.Equal(3, result.FreshStations);
            Assert.Equal(2, result.RetainedStations);
            Assert.Equal(1, result.AddedStations);
            Assert.Equal(1, result.RemovedStations);
            Assert.Equal(1, result.ChangedIdentities);
            Assert.Equal(1, result.ChangedNames);
            Assert.Equal(1, result.ChangedTypes);
            Assert.Equal(1, result.ChangedParents);
            Assert.Equal(2, result.AddedMemberships);
            Assert.Equal(3, result.RemovedMemberships);
            Assert.DoesNotContain("private", result.ToString());
            Assert.DoesNotContain("old-name-id", result.ToString());
            Assert.DoesNotContain("parent-a", result.ToString());
        }

        [Fact]
        public void IdentityDiagnosticsTreatUnchangedListingsAsRetainedAndLabelMissingBaseline()
        {
            var catalog = new TuneInCategorySnapshot
            {
                Folders = new List<TuneInFolderSnapshot>
                {
                    new TuneInFolderSnapshot
                    {
                        Id = "folder",
                        Items = new List<TuneInItemSnapshot>
                        {
                            new TuneInItemSnapshot { Id = "child", Type = "Folder" },
                            new TuneInItemSnapshot { Id = "signed-id", Type = "Media", Name = "Radio" }
                        }
                    }
                }
            };
            var retained = TuneInIdentityDiagnostics.Compare(catalog, catalog, _ => "s1", CancellationToken.None);
            Assert.Equal(1, retained.RetainedStations);
            Assert.Equal(0, retained.ChangedIdentities);
            Assert.Equal(0, retained.ChangedParents);
            Assert.Equal(0, retained.AddedMemberships);
            Assert.Equal(0, retained.RemovedMemberships);
            var initial = TuneInIdentityDiagnostics.Compare(null, catalog, _ => "s1", CancellationToken.None);
            Assert.False(initial.HasSavedBaseline);
            Assert.Equal(1, initial.AddedStations);
            Assert.Equal(0, initial.RemovedStations);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                TuneInIdentityDiagnostics.Compare(null, catalog, _ => "s1", cancelled.Token));
        }

        [Fact]
        public async Task CatalogIdentityLoggingRunsOncePerCategoryWithoutForcingUnchangedImports()
        {
            using var fixture = new ExperimentalImportFixture();
            fixture.Config.EnableCatalogIdentityDiagnostics = true;
            var logger = new Mock<ILogger>();
            using var refresh = fixture.Create(allowScoped: true, logger: logger.Object);
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            var imports = fixture.Manager.Restrictions.Count;
            logger.Invocations.Clear();
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Equal(imports, fixture.Manager.Restrictions.Count);
            var call = Assert.Single(logger.Invocations, invocation =>
                invocation.Method.Name == nameof(ILogger.Info)
                && ((string)invocation.Arguments[0]).StartsWith("TuneIn catalog identity comparison", StringComparison.Ordinal));
            var arguments = (object[])call.Arguments[1];
            Assert.Equal("talk", arguments[0]);
            var comparison = Assert.IsType<TuneInIdentityDiagnostics>(arguments[1]);
            Assert.True(comparison.HasSavedBaseline);
            Assert.Equal(1, comparison.RetainedStations);
            Assert.Equal(0, comparison.ChangedIdentities);
            Assert.Equal(0, comparison.RemovedMemberships);
            fixture.Config.EnableCatalogIdentityDiagnostics = false;
            logger.Invocations.Clear();
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Equal(imports, fixture.Manager.Restrictions.Count);
            Assert.DoesNotContain(logger.Invocations, invocation =>
                invocation.Method.Name == nameof(ILogger.Info)
                && ((string)invocation.Arguments[0]).StartsWith("TuneIn catalog identity comparison", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RestrictedImportUsesTheEmbyArgumentAndOnlyRequiresItsCategory(bool enumerateChannelRoot)
        {
            using var fixture = new ExperimentalImportFixture();
            using var refresh = fixture.Create(allowScoped: true);
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Null(Assert.Single(fixture.Manager.Restrictions));
            var oldSports = fixture.Category("sports").Fingerprint;
            fixture.Artwork.Clear();
            fixture.Inventory.Clear();
            fixture.Manager.EnumerateChannelRoot = enumerateChannelRoot;
            fixture.Version++;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Equal(fixture.Category("talk").RootId, fixture.Manager.Restrictions.Last());
            Assert.Equal(oldSports, fixture.Category("sports").Fingerprint);
            Assert.Equal(new[] { "talk" }, fixture.Artwork);
            Assert.Equal(new[] { "talk", "talk" }, fixture.Inventory);
            Assert.Equal(2, refresh.Status().ImportFoldersServed);
            Assert.Equal(2, refresh.Status().ImportFoldersTotal);
        }

        [Fact]
        public async Task CategoryDiscoveryRejectsSettingsAndLockChangesUntilItStops()
        {
            using var fixture = new ExperimentalImportFixture();
            using var refresh = fixture.Create(allowScoped: true);
            refresh.Start();
            await WaitUntilIdle(refresh);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.BeforeSports = async token =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            };
            refresh.Start("sports");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => fixture.Config.EnableTalk = false));
                Assert.Throws<InvalidOperationException>(() => refresh.SetSettingsLock(() => false));
                Assert.True(fixture.Config.EnableTalk);
            }
            finally { release.TrySetResult(true); }
            await WaitUntilIdle(refresh);
            Assert.False(refresh.SetSettingsLock(() => false));
        }

        [Fact]
        public async Task ScopedCleanupRetainsSiblingBaselineAcrossRestartAndRebuildsOnlyTheRemovedCategory()
        {
            using var fixture = new ExperimentalImportFixture();
            var cleanup = fixture.Cleanup("talk");
            string sportsFingerprint;
            using (var refresh = fixture.Create(allowScoped: true, cleanup: cleanup))
            {
                refresh.Start();
                await WaitUntilIdle(refresh);
                sportsFingerprint = fixture.Category("sports").Fingerprint;
                var preview = refresh.PreviewCleanup("talk");
                refresh.StartCleanup(preview.Token);
                await WaitUntilCleanupIdle(refresh);
                Assert.Equal("Completed", refresh.Status().Cleanup.State);
                Assert.Equal(new[] { "talk" }, fixture.Store.Read().CleanedCategories);
                Assert.Equal(sportsFingerprint, fixture.Category("sports").Fingerprint);
                Assert.Equal("Incomplete (cleanup needs refresh)", refresh.Status().Categories.Single(c => c.Category == "talk").State);
            }
            using (var refresh = fixture.Create(allowScoped: true, cleanup: cleanup))
            {
                Assert.Equal("Incomplete (cleanup needs refresh)", refresh.Status().Categories.Single(c => c.Category == "talk").State);
                fixture.Version++;
                refresh.Start("sports");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(fixture.Category("sports").RootId, fixture.Manager.Restrictions.Last());
                Assert.Equal(new[] { "talk" }, fixture.Store.Read().CleanedCategories);
                var refreshedSports = fixture.Category("sports").Fingerprint;
                var fetches = fixture.Requests.Count;
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Null(fixture.Manager.Restrictions.Last());
                Assert.Equal(new[] { "talk", "c1" }, fixture.Requests.Skip(fetches));
                Assert.Equal(refreshedSports, fixture.Category("sports").Fingerprint);
                Assert.Empty(fixture.Store.Read().CleanedCategories);
            }
        }

        [Fact]
        public async Task MultipleCleanedCategoriesCannotBeReplayedFromStaleSnapshots()
        {
            using var fixture = new ExperimentalImportFixture();
            var cleanup = fixture.Cleanup("talk", "sports");
            using var refresh = fixture.Create(allowScoped: true, cleanup: cleanup);
            refresh.Start();
            await WaitUntilIdle(refresh);
            foreach (var category in new[] { "talk", "sports" })
            {
                refresh.StartCleanup(refresh.PreviewCleanup(category).Token);
                await WaitUntilCleanupIdle(refresh);
            }
            var imports = fixture.Manager.Restrictions.Count;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Error", refresh.Status().State);
            Assert.Equal(imports, fixture.Manager.Restrictions.Count);
            Assert.Equal(2, fixture.Store.Read().CleanedCategories.Count);
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Empty(fixture.Store.Read().CleanedCategories);
        }

        [Fact]
        public async Task IncompleteDiscoveryCannotRestoreACleanedCategoryFromItsOldSnapshot()
        {
            using var fixture = new ExperimentalImportFixture();
            var cleanup = fixture.Cleanup("talk");
            using var refresh = fixture.Create(allowScoped: true, cleanup: cleanup);
            refresh.Start();
            await WaitUntilIdle(refresh);
            refresh.StartCleanup(refresh.PreviewCleanup("talk").Token);
            await WaitUntilCleanupIdle(refresh);
            var imports = fixture.Manager.Restrictions.Count;
            fixture.BrokenTalk = true;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Partial", refresh.Status().State);
            Assert.Equal(imports, fixture.Manager.Restrictions.Count);
            Assert.Equal(new[] { "talk" }, fixture.Store.Read().CleanedCategories);
            fixture.BrokenTalk = false;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Empty(fixture.Store.Read().CleanedCategories);
        }

        [Fact]
        public async Task ScopedCleanupPersistsTheMarkerBeforeTheFirstRemoval()
        {
            using var fixture = new ExperimentalImportFixture();
            var cleanup = fixture.Cleanup("talk");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            fixture.CleanupLibrary.BeforeDelete = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            using var refresh = fixture.Create(allowScoped: true, cleanup: cleanup);
            refresh.Start();
            await WaitUntilIdle(refresh);
            try
            {
                refresh.StartCleanup(refresh.PreviewCleanup("talk").Token);
                Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
                Assert.Equal(new[] { "talk" }, fixture.Store.Read().CleanedCategories);
                Assert.Empty(fixture.CleanupLibrary.Removed);
            }
            finally { release.Set(); }
            await WaitUntilCleanupIdle(refresh);
            Assert.Equal("Completed", refresh.Status().Cleanup.State);
        }

        [Fact]
        public async Task InterruptedRebuildCanRetryItsFreshStagedListing()
        {
            using var fixture = new ExperimentalImportFixture();
            var cleanup = fixture.Cleanup("talk");
            using var refresh = fixture.Create(allowScoped: true, cleanup: cleanup);
            refresh.Start();
            await WaitUntilIdle(refresh);
            refresh.StartCleanup(refresh.PreviewCleanup("talk").Token);
            await WaitUntilCleanupIdle(refresh);
            var attempts = 0;
            fixture.Manager.BeforeImport = (_, __) =>
            {
                if (++attempts == 1) throw new IOException("Interrupted import.");
                return Task.CompletedTask;
            };
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal(2, attempts);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Empty(fixture.Store.Read().CleanedCategories);
        }

        [Fact]
        public async Task DisablingACleanedCategoryDropsItsMarkerAndRetainsOtherSnapshots()
        {
            using var fixture = new ExperimentalImportFixture();
            var cleanup = fixture.Cleanup("talk");
            using var refresh = fixture.Create(allowScoped: true, cleanup: cleanup);
            refresh.Start();
            await WaitUntilIdle(refresh);
            refresh.StartCleanup(refresh.PreviewCleanup("talk").Token);
            await WaitUntilCleanupIdle(refresh);
            refresh.UpdateConfiguration(() => fixture.Config.EnableTalk = false);
            var saved = fixture.Store.Read();
            Assert.Empty(saved.CleanedCategories);
            Assert.Equal("sports", Assert.Single(saved.Categories).Category);
            Assert.DoesNotContain(refresh.Status().Categories, category => category.Category == "talk");
        }

        private static async Task WaitUntilCleanupIdle(TuneInRefresh refresh)
        {
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(
                () => refresh.Status().Cleanup.State != "Running", TimeSpan.FromSeconds(5))));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task IncompleteOrOutOfScopeReadLeavesDirtyStateAndRetriesWithTheSameRestriction(bool readSibling)
        {
            using var fixture = new ExperimentalImportFixture();
            using var refresh = fixture.Create(allowScoped: true);
            refresh.Start();
            await WaitUntilIdle(refresh);
            var originalTalk = fixture.Category("talk").Fingerprint;
            var talkId = fixture.Category("talk").RootId;
            fixture.Manager.SkipDescendants = !readSibling;
            fixture.Manager.ReadSibling = readSibling;
            fixture.Version++;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Error", refresh.Status().State);
            Assert.Equal("talk", fixture.Store.Read().DirtyCategory);
            Assert.True(fixture.Store.Read().DirtyImportWasScoped);
            Assert.Equal(originalTalk, fixture.Category("talk").Fingerprint);
            Assert.Equal(new[] { null, talkId }, fixture.Manager.Restrictions);
            fixture.Manager.SkipDescendants = fixture.Manager.ReadSibling = false;
            var reads = fixture.Fetches;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Equal(reads, fixture.Fetches);
            Assert.Equal(new[] { null, talkId, talkId }, fixture.Manager.Restrictions);
            Assert.Null(fixture.Store.Read().DirtyCategory);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConcurrentEmbyChannelTraversalCannotFailOrCompleteAScopedImport(bool importSkipsDescendants)
        {
            using var fixture = new ExperimentalImportFixture();
            using var refresh = fixture.Create(allowScoped: true);
            refresh.Start();
            await WaitUntilIdle(refresh);
            var talkId = fixture.Category("talk").RootId;
            var sportsId = fixture.Category("sports").RootId;
            var talkFolders = fixture.Category("talk").Folders.Select(folder => folder.Id).ToArray();
            var foreignReads = new List<string>();
            fixture.Manager.SkipDescendants = importSkipsDescendants;
            fixture.Manager.BeforeImport = async (restriction, token) =>
            {
                // Emby's Refresh Internet Channels task and client browsing pass their own tokens.
                using var foreign = new CancellationTokenSource();
                foreach (var folder in new string[] { null, sportsId }.Concat(talkFolders))
                {
                    var listing = await fixture.Channel.GetChannelItems(
                        new InternalChannelItemQuery { FolderId = folder }, foreign.Token);
                    Assert.NotNull(listing.Items);
                    foreignReads.Add(folder);
                }
            };
            fixture.Version++;
            refresh.Start("talk");
            await WaitUntilIdle(refresh);
            Assert.Equal(2 + talkFolders.Length, foreignReads.Count);
            Assert.Equal(new[] { null, talkId }, fixture.Manager.Restrictions);
            if (importSkipsDescendants)
            {
                Assert.Equal("Error", refresh.Status().State);
                Assert.Equal("talk", fixture.Store.Read().DirtyCategory);
            }
            else
            {
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Null(fixture.Store.Read().DirtyCategory);
            }
        }

        [Theory]
        [InlineData("default")]
        [InlineData("topology")]
        [InlineData("unrelated dirty")]
        public async Task DoesNotRestrictWhenTheBaselineOrOptInDoesNotPermitIt(string reason)
        {
            using var fixture = new ExperimentalImportFixture();
            using var refresh = fixture.Create();
            refresh.Start();
            await WaitUntilIdle(refresh);
            var checkpoint = fixture.Store.Read();
            checkpoint.NeedsImport = reason == "topology";
            if (reason == "unrelated dirty") checkpoint.DirtyCategory = "sports";
            fixture.Store.Write(checkpoint);
            fixture.Version++;
            var recovery = refresh.Start("talk");
            if (recovery.RecoveryChoiceRequired) refresh.Start("talk", recovery.RecoveryId, false);
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Equal(new string[] { null, null }, fixture.Manager.Restrictions);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefreshAllKeepsTheEarlierCommitWhenLaterDiscoveryOrImportFails(bool failDuringImport)
        {
            using var fixture = new ExperimentalImportFixture();
            using var refresh = fixture.Create(allowScoped: true);
            refresh.Start();
            await WaitUntilIdle(refresh);
            var oldTalk = fixture.Category("talk").Fingerprint;
            var oldSports = fixture.Category("sports").Fingerprint;
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Fail(CancellationToken token)
            {
                entered.SetResult(true);
                await release.Task.WaitAsync(token);
                throw new InvalidDataException("A later provider page failed.");
            }
            if (failDuringImport)
            {
                var sportsId = fixture.Category("sports").RootId;
                fixture.Manager.BeforeImport = (restriction, token) => restriction == sportsId
                    ? Fail(token) : Task.CompletedTask;
            }
            else fixture.BeforeSports = Fail;
            fixture.Version++;
            refresh.Start();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotEqual(oldTalk, fixture.Category("talk").Fingerprint);
                Assert.Equal(oldSports, fixture.Category("sports").Fingerprint);
                Assert.Equal(failDuringImport ? "sports" : null, fixture.Store.Read().DirtyCategory);
                Assert.Equal(failDuringImport, fixture.Store.Read().DirtyImportWasScoped);
                Assert.Equal(fixture.Category(failDuringImport ? "sports" : "talk").RootId,
                    fixture.Manager.Restrictions.Last());
            }
            finally { release.TrySetResult(true); }
            await WaitUntilIdle(refresh);
            Assert.Equal("Error", refresh.Status().State);
            Assert.NotEqual(oldTalk, fixture.Category("talk").Fingerprint);
            Assert.Equal(oldSports, fixture.Category("sports").Fingerprint);
            Assert.Equal("Imported", refresh.Status().Categories.Single(category => category.Category == "talk").State);
            Assert.Equal(failDuringImport ? "sports" : null, fixture.Store.Read().DirtyCategory);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ProgressiveRefreshImportsMusicLastUnlessItNeedsRepair(bool repairMusic)
        {
            using var fixture = new ExperimentalImportFixture(includeMusic: true);
            using var refresh = fixture.Create(allowScoped: true);
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Null(Assert.Single(fixture.Manager.Restrictions));
            if (repairMusic)
            {
                var checkpoint = fixture.Store.Read();
                checkpoint.DirtyCategory = "music";
                checkpoint.DirtyImportWasScoped = true;
                fixture.Store.Write(checkpoint);
            }
            fixture.Version++;
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            var expected = repairMusic ? new[] { "music", "talk", "sports" } : new[] { "talk", "sports", "music" };
            Assert.Equal(expected.Select(category => fixture.Category(category).RootId),
                fixture.Manager.Restrictions.Skip(1));
            Assert.Equal(new[] { "music", "talk", "sports" }, refresh.Status().Categories.Select(category => category.Category));
        }

        [Fact]
        public async Task ProgressiveImportTimeoutStopsBeforeProceedingToSibling()
        {
            using var fixture = new ExperimentalImportFixture();
            using var refresh = fixture.Create(allowScoped: true, timeout: TimeSpan.FromSeconds(1));
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("Completed", refresh.Status().State);
            var talk = fixture.Category("talk").RootId;
            fixture.Manager.BeforeImport = async (restriction, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
            };
            fixture.Version++;
            refresh.Start();
            await WaitUntilIdle(refresh);
            Assert.Equal("TimedOut", refresh.Status().State);
            Assert.Equal(1, refresh.Status().ImportTimeouts);
            Assert.Equal(new[] { null, talk }, fixture.Manager.Restrictions);
            Assert.Equal("talk", fixture.Store.Read().DirtyCategory);
            Assert.Null(refresh.Status().AutomaticRetry);
        }

        private sealed class ExperimentalImportFixture : IDisposable
        {
            private readonly string path = Path.Combine(Path.GetTempPath(), $"tunein-experimental-{Guid.NewGuid():N}.xml");
            private readonly TuneInDirectory directory;
            private readonly TuneInChannel channel;
            private readonly IChannelManager manager;
            internal readonly ExperimentalChannelManager Manager;
            internal readonly TuneInRefreshStore Store;
            internal readonly List<string> Artwork = new List<string>();
            internal readonly List<string> Inventory = new List<string>();
            internal int Version = 1;
            internal int Fetches;
            internal readonly ConcurrentQueue<string> Requests = new ConcurrentQueue<string>();
            internal Func<CancellationToken, Task> BeforeSports;
            internal bool BrokenTalk;
            internal LibraryCleanupTests.LibraryProxy CleanupLibrary;
            internal readonly PluginConfiguration Config;
            internal TuneInChannel Channel => channel;

            internal ExperimentalImportFixture(bool includeMusic = false)
            {
                var config = new PluginConfiguration
                {
                    SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true,
                    EnableMusic = includeMusic
                };
                Config = config;
                if (includeMusic) config.Music.Add(new CatalogSelection { Id = "g1", Name = "Genre" });
                directory = new TuneInDirectory(() => config, async (request, token) =>
                {
                    Interlocked.Increment(ref Fetches);
                    Requests.Enqueue(request.Parameters.GetValueOrDefault("c") ?? request.Parameters.GetValueOrDefault("id"));
                    if (BeforeSports != null && request.Parameters.GetValueOrDefault("c") == "sports")
                        await BeforeSports(token);
                    if (BrokenTalk && request.Parameters.GetValueOrDefault("c") == "talk")
                        return XDocument.Parse("<opml><body><outline type='link' text='Bad' "
                            + "URL='https://opml.radiotime.com/Browse.ashx?id=c1&amp;filter=bad%20value'/></body></opml>");
                    if (!request.Parameters.ContainsKey("id"))
                        return XDocument.Parse("<opml><body><outline type='link' text='Subfolder' "
                            + "URL='https://opml.radiotime.com/Browse.ashx?id=c1'/></body></opml>");
                    return XDocument.Parse($"<opml><body><outline type='audio' guide_id='s{Version}' text='Radio'/></body></opml>");
                });
                channel = new TuneInChannel(directory);
                manager = DispatchProxy.Create<IChannelManager, ExperimentalChannelManager>();
                Manager = (ExperimentalChannelManager)manager;
                if (includeMusic) Manager.ExpectedRoots = new[] { "Music", "Talk", "Sports" };
                Store = new TuneInRefreshStore(path);
            }

            internal TuneInCategorySnapshot Category(string category) =>
                Store.Read().Categories.Single(saved => saved.Category == category);

            internal TuneInLibraryCleanup Cleanup(params string[] categories)
            {
                var api = DispatchProxy.Create<ILibraryManager, LibraryCleanupTests.LibraryProxy>();
                var library = (LibraryCleanupTests.LibraryProxy)api;
                CleanupLibrary = library;
                var root = new Channel { InternalId = 1, Name = "TuneIn" };
                library.Items.Add(1, root);
                var snapshot = directory.Snapshot();
                for (var i = 0; i < categories.Length; i++)
                    library.Items.Add(i + 2, new Folder { InternalId = i + 2, ParentId = 1,
                        ExternalId = channel.GetCategoryRoot(snapshot, categories[i]).Id });
                return new TuneInLibraryCleanup(api, () => root, null);
            }

            internal TuneInRefresh Create(bool allowScoped = false, TimeSpan? timeout = null,
                TuneInLibraryCleanup cleanup = null, ILogger logger = null) => new TuneInRefresh(directory, Store, () => channel,
                (source, folder, session, strict, token) => source.GetRefreshChannelItems(folder, session, strict, token),
                (source, staged, restriction, token) => TuneInRefreshService.ImportChannel(manager, source, staged, restriction, token),
                logger, timeoutOverride: timeout ?? TimeSpan.FromSeconds(10), allowScopedImport: allowScoped, libraryCleanup: cleanup,
                updateArtwork: (_, folder, token) => Artwork.Add(directory.Resolve(directory.Snapshot(), folder).Category),
                countStations: (_, folder, token) =>
                {
                    Inventory.Add(directory.Resolve(directory.Snapshot(), folder).Category);
                    return 1;
                });

            public void Dispose()
            {
                Store.Clear();
                Store.ClearDiscovery();
                Store.ClearRecovery();
            }
        }

        public class ExperimentalChannelManager : DispatchProxy
        {
            internal readonly List<string> Restrictions = new List<string>();
            internal bool EnumerateChannelRoot = true;
            internal bool SkipDescendants;
            internal bool ReadSibling;
            internal string[] ExpectedRoots = new[] { "Talk", "Sports" };
            internal Func<string, CancellationToken, Task> BeforeImport;

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name != "RefreshChannelContent") throw new NotSupportedException(method.Name);
                return Refresh((TuneInChannel)args[0], (int)args[1], (string)args[2], (CancellationToken)args[3]);
            }

            private async Task Refresh(TuneInChannel channel, int depth, string restriction, CancellationToken token)
            {
                Restrictions.Add(restriction);
                if (BeforeImport != null) await BeforeImport(restriction, token);
                async Task<ChannelItemResult> Read(string id) =>
                    await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = id }, token);
                async Task Walk(string id, int level)
                {
                    if (level > depth) throw new InvalidOperationException("The delegated depth did not cover the staged tree.");
                    var listing = await Read(id);
                    if (restriction != null && SkipDescendants) return;
                    foreach (var child in listing.Items.Where(item => item.Type == ChannelItemType.Folder))
                        await Walk(child.Id, level + 1);
                }
                if (restriction == null || EnumerateChannelRoot || ReadSibling)
                {
                    var roots = await Read(null);
                    Assert.Equal(ExpectedRoots, roots.Items.Select(item => item.Name));
                    if (ReadSibling)
                        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                            await Read(roots.Items.Single(root => root.Id != restriction).Id));
                }
                await Walk(restriction, restriction == null ? 1 : 2);
            }
        }

        [Fact]
        public async Task ManualRestartAfterServerShutdownRecoversOriginalCompleteRequest()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
            };
            var requests = 0;
            var imports = 0;
            var importing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                requests++;
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-restart-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            TuneInRefresh Create() => new TuneInRefresh(directory, store, () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (c, staged, _, token) => c.ImportAsync(staged, token, async () =>
                {
                    imports++;
                    Assert.Equal(new[] { "Talk", "Sports" }, ReadStagedTree(staged, token).Keys);
                    if (imports == 1)
                    {
                        importing.SetResult(true);
                        await Task.Delay(Timeout.Infinite, token);
                    }
                }), null);
            try
            {
                using (var refresh = Create())
                {
                    refresh.Start();
                    await importing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    refresh.Dispose();
                }
                var interrupted = store.ReadRecovery();
                Assert.NotNull(interrupted);
                Assert.Null(interrupted.RequestedCategory);
                Assert.Equal("import", interrupted.Phase);
                Assert.NotNull(store.Read().DirtyCategory);
                using var restarted = Create();
                restarted.CheckSchedules();
                Assert.Equal(1, imports);
                Assert.NotNull(store.ReadRecovery());
                restarted.Start();
                await WaitUntilIdle(restarted);
                Assert.Equal("Completed", restarted.Status().State);
                Assert.Equal(2, imports);
                Assert.Equal(4, requests);
                Assert.Null(store.ReadRecovery());
                Assert.Null(store.Read().DirtyCategory);
                Assert.Equal(2, store.Read().Categories.Count);
            }
            finally
            {
                store.Clear();
                store.ClearRecovery();
            }
        }

        [Theory]
        [InlineData("discovery", 0, true)]
        [InlineData("import", 1, true)]
        [InlineData("discovery", 3, true)]
        [InlineData("import", 0, false)]
        public async Task RequestedRecoveryUsesCompatibleMarkerAndPreservesRetryBudget(string phase, int retries, bool compatible)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var imports = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
                Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>")));
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-startup-recovery-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            store.WriteRecovery(new TuneInRefreshRecovery
            {
                ConfigurationKey = compatible ? directory.Snapshot().Key : new string('0', 64),
                RequestedCategory = "talk", Phase = phase, Retries = retries
            });
            TuneInRefresh runningRefresh = null;
            var recovers = compatible && retries < 3;
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports++;
                        Assert.Equal(recovers ? retries + 1 : 0, store.ReadRecovery().Retries);
                        Assert.Equal(recovers ? retries + 1 : (int?)null, runningRefresh.Status().AutomaticRetry);
                        Assert.Equal(recovers ? phase : null, runningRefresh.Status().AutomaticRetryPhase);
                        ReadStagedTree(staged, token);
                        return Task.CompletedTask;
                    }, null);
                runningRefresh = refresh;
                refresh.CheckSchedules();
                Assert.Equal(0, imports);
                Assert.NotNull(store.ReadRecovery());
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal(1, imports);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Null(store.ReadRecovery());
                refresh.CheckSchedules();
                await WaitUntilIdle(refresh);
                Assert.Equal(1, imports);
            }
            finally
            {
                store.Clear();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task RepeatedImportFolderRequestsDoNotAdvanceUniqueProgress()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var now = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
            var directory = new TuneInDirectory(() => config, (_, __) =>
                Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>")));
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-progress-{Guid.NewGuid():N}.xml");
            TuneInRefresh runningRefresh = null;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        now = now.AddMinutes(1);
                        var root = Assert.Single(staged.Read(null, token).Items);
                        var firstProgress = runningRefresh.Status().LastProgressUtc;
                        Assert.Equal(now, firstProgress);
                        Assert.Equal(1, runningRefresh.Status().ImportFoldersServed);

                        now = now.AddMinutes(1);
                        Assert.Equal(root.Id, Assert.Single(staged.Read(string.Empty, token).Items).Id);
                        var repeated = runningRefresh.Status();
                        Assert.Equal(firstProgress, repeated.LastProgressUtc);
                        Assert.Equal(1, repeated.ImportFoldersServed);
                        Assert.NotNull(new TuneInImportStallDiagnostics().TryCreate(now, repeated.LastProgressUtc,
                            repeated.ImportFoldersTotal, repeated.ImportFoldersServed, false));

                        now = now.AddMinutes(1);
                        var station = Assert.Single(staged.Read(root.Id, token).Items);
                        var nextProgress = runningRefresh.Status().LastProgressUtc;
                        Assert.Equal(now, nextProgress);
                        Assert.Equal(2, runningRefresh.Status().ImportFoldersServed);
                        now = now.AddMinutes(1);
                        Assert.Equal(station.Id, Assert.Single(staged.Read(root.Id, token).Items).Id);
                        Assert.Equal(nextProgress, runningRefresh.Status().LastProgressUtc);
                        Assert.Equal(2, runningRefresh.Status().ImportFoldersServed);
                        return Task.CompletedTask;
                    }, null, clock: () => now);
                runningRefresh = refresh;
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ImportInterruptionDrainsAndReplaysCompleteTreeWithPartialStatus(bool partial)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, EnableTalk = true,
                AllowPartialCatalogImport = true
            };
            config.Music.Add(new CatalogSelection { Id = "g71", Enabled = true });
            var changed = false;
            var requests = new List<string>();
            var imports = 0;
            var retryImports = 0;
            var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDrain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TuneInStagedImport timedOutTree = null;
            TuneInRefresh runningRefresh = null;
            string[] savedTalk = null;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var scope = request.Parameters.GetValueOrDefault("id") ?? request.Parameters["c"];
                requests.Add(scope);
                var malformed = changed && partial && scope == "g71"
                    ? "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1&amp;filter=bad%20value'/>" : "";
                return Task.FromResult(XDocument.Parse("<opml><body>"
                    + $"<outline type='audio' guide_id='s1' text='{(changed ? "Changed" : "Original")}'/>"
                    + malformed + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-replay-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (c, staged, _, token) => c.ImportAsync(staged, token, async () =>
                    {
                        imports++;
                        if (changed)
                        {
                            retryImports++;
                            Assert.Equal(0, runningRefresh.Status().ImportFoldersServed);
                            if (retryImports == 2)
                            {
                                Assert.NotSame(timedOutTree, staged);
                                Assert.Equal(1, runningRefresh.Status().AutomaticRetry);
                                Assert.Equal(3, runningRefresh.Status().AutomaticRetryLimit);
                                Assert.Equal("import", runningRefresh.Status().AutomaticRetryPhase);
                            }
                        }
                        var tree = ReadStagedTree(staged, token);
                        Assert.Equal(new[] { "Music", "Talk" }, tree.Keys);
                        if (!changed) savedTalk = tree["Talk"];
                        else Assert.Equal(savedTalk, tree["Talk"]);
                        if (changed && retryImports == 1)
                        {
                            timedOutTree = staged;
                            try { throw new OperationCanceledException("Emby interrupted the import without cancelling the refresh."); }
                            catch (OperationCanceledException)
                            {
                                drained.TrySetResult(true);
                                await releaseDrain.Task;
                                throw;
                            }
                        }
                    }), null, timeoutOverride: TimeSpan.FromSeconds(5),
                    countStations: (_, __, ___) => 0);
                runningRefresh = refresh;
                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                var savedMusic = store.Read().Categories.Single(category => category.Category == "music").Fingerprint;
                requests.Clear();
                changed = true;
                refresh.Start("music");
                try
                {
                    await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(refresh.Status().Busy);
                    Assert.Equal(1, retryImports);
                    Assert.Equal("Importing", refresh.Status().Phase);
                    Assert.Null(refresh.Status().AutomaticRetry);
                    var pending = store.Read();
                    Assert.Equal("music", pending.DirtyCategory);
                    Assert.Equal(savedMusic, pending.Categories.Single(category => category.Category == "music").Fingerprint);
                }
                finally { releaseDrain.TrySetResult(true); }
                await WaitUntilIdle(refresh);
                Assert.Equal(partial ? "Partial" : "Completed", refresh.Status().State);
                Assert.Equal(0, refresh.Status().DiscoveryTimeouts);
                Assert.Equal(0, refresh.Status().ImportTimeouts);
                Assert.Equal(3, imports);
                Assert.Equal(new[] { "g71" }, requests);
                Assert.False(refresh.Status().ImportMayHavePartiallyApplied);
                Assert.True(refresh.Status().ReportAvailable);
                Assert.Equal(partial ? 1 : 0, refresh.Status().SkippedBrowseLinks);
                Assert.Contains("Automatic interruption retries: 1", refresh.GetFailureLog().Text);
                var saved = store.Read();
                Assert.Null(saved.DirtyCategory);
                Assert.Equal(partial, saved.Categories.Single(category => category.Category == "music").DiscoveryPartial);
                Assert.False(saved.Categories.Single(category => category.Category == "talk").DiscoveryPartial);
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
        }

        [Fact]
        public async Task DiscoveryRetriesInterruptionsOnlyThreeTimesAndKeepsExistingLibrary()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var block = false;
            var attempts = 0;
            var activeRetries = new List<(int? Number, int? Limit)>();
            TuneInRefresh runningRefresh = null;
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                if (block)
                {
                    attempts++;
                    var active = runningRefresh.Status();
                    activeRetries.Add((active.AutomaticRetry, active.AutomaticRetryLimit));
                    await Task.Yield();
                    throw new IOException("Provider connection interrupted.");
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
                runningRefresh = refresh;
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                var checkpoint = File.ReadAllText(path);

                block = true;
                refresh.Start("talk");
                await WaitUntilIdle(refresh, TimeSpan.FromSeconds(30));
                Assert.Equal("Error", refresh.Status().State);
                Assert.Equal(0, refresh.Status().DiscoveryTimeouts);
                Assert.Equal(4, attempts);
                Assert.Equal((null, null), activeRetries[0]);
                Assert.Equal(Enumerable.Range(1, 3).Select(n => ((int?)n, (int?)3)), activeRetries.Skip(1));
                Assert.Null(refresh.Status().AutomaticRetry);
                Assert.Contains("interruption retry limit reached", refresh.Status().Phase);
                Assert.Null(refresh.Status().DiscoveryCheckpointUtc);
                Assert.Equal(1, imports);
                Assert.Equal(checkpoint, File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

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
                Assert.True(refresh.Status().HasRefreshBeenStarted);
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
        public async Task MalformedMusicLinkImportsValidResultsAndRetainsSavedBranchesWhenAvailable(string baseline)
        {
            var withBaseline = baseline != "no baseline";
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, EnableTalk = true,
                AllowPartialCatalogImport = true
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
                var invalid = string.Concat(Enumerable.Repeat(Link("c123", "#" + privateMarker), 4))
                    + Link("c123", "&amp;filter=private%2F" + privateMarker)
                    + "<outline type='link' URL='https://opml.radiotime.com/Tune.ashx?id=s1#invalid'/>";
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
            Dictionary<string, string[]> importedNames = null;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports++;
                        imported = ReadStagedTree(staged, token);
                        importedNames = ReadStagedTree(staged, token, names: true);
                        return Task.CompletedTask;
                    }, null, updateArtwork: (_, __, ___) => artworkUpdates++);
                if (withBaseline)
                {
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Contains("c123", requests);
                }
                var savedMusic = withBaseline ? imported["Music"] : null;
                var savedTalk = withBaseline ? imported["Talk"] : null;
                requests.Clear();
                skip = true;
                refresh.Start(withBaseline ? "music" : null);
                await WaitUntilIdle(refresh);

                var status = refresh.Status();
                Assert.Equal("Partial", status.State);
                Assert.Contains("valid updates applied, rejected entries excluded", status.Phase);
                Assert.Equal(withBaseline ? 0 : 1, status.CategoriesCompleted);
                Assert.False(status.ImportMayHavePartiallyApplied);
                Assert.Null(status.DirtyCategory);
                Assert.Equal(5, status.SkippedBrowseLinks);
                Assert.Equal("4 x Music: Invalid catalog fragment; 1 x Music: Invalid catalog link parameter: filter",
                    status.SkippedBrowseLinkDetails);
                Assert.DoesNotContain(privateMarker, status.SkippedBrowseLinkDetails);
                Assert.Contains(status.Categories, category => category.Category == "music"
                    && category.State == "Partial (applied)");
                Assert.Contains(status.Categories, category => category.Category == "talk"
                    && category.State == "Imported");
                Assert.Contains("c124", requests);
                Assert.Equal(!withBaseline, requests.Contains("talk"));
                Assert.DoesNotContain("c123", requests);
                Assert.DoesNotContain("c126", requests);
                Assert.Equal(withBaseline ? 2 : 1, imports);
                Assert.True(artworkUpdates > 0);

                var after = importedNames["Music"];
                Assert.Equal(withBaseline ? new[] { "Changed", "Changed" } : new[] { "Changed" },
                    after.OrderBy(name => name));
                if (withBaseline)
                {
                    var before = savedMusic.Select(id => directory.Resolve(directory.Snapshot(), id).Name).ToArray();
                    Assert.Equal(new[] { "Original", "Retained" }, before.OrderBy(name => name));
                    Assert.Equal(savedTalk, imported["Talk"]);
                }
                var savedPartialMusic = imported["Music"];
                var checkpoint = new TuneInRefreshStore(path).Read();
                Assert.True(checkpoint.Categories.Single(category => category.Category == "music").DiscoveryPartial);
                if (!withBaseline)
                {
                    Assert.False(refresh.Status().HasCompleteBaseline);
                    using (var restarted = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                        (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                        (_, __, ___, ____) => Task.CompletedTask, null))
                        Assert.False(restarted.Status().HasCompleteBaseline);
                    requests.Clear();
                    refresh.Start("music");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("BaselineRequired", refresh.Status().State);
                    Assert.Empty(requests);
                    healed = true;
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.True(refresh.Status().HasCompleteBaseline);
                    Assert.True(new TuneInRefreshStore(path).Read().CompleteBaseline);
                    return;
                }
                var previousImports = imports;
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.Equal("Partial", refresh.Status().State);
                Assert.Contains("merged library unchanged", refresh.Status().Phase);
                Assert.Equal(previousImports, imports);

                requests.Clear();
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(0, refresh.Status().SkippedBrowseLinks);
                Assert.Equal(new[] { "talk" }, requests);
                Assert.Equal(previousImports + (withBaseline ? 1 : 0), imports);
                Assert.Equal(savedPartialMusic, imported["Music"]);
                if (withBaseline) Assert.Equal(Assert.Single(savedTalk), Assert.Single(imported["Talk"]));

                healed = true;
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.All(importedNames["Music"], name => Assert.Equal("Changed", name));
                Assert.False(new TuneInRefreshStore(path).Read().Categories.Single(category => category.Category == "music").DiscoveryPartial);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("music", false, false)]
        [InlineData("music", false, true)]
        [InlineData("music", true, false)]
        [InlineData("music", true, true)]
        [InlineData("talk", false, false)]
        [InlineData("talk", false, true)]
        [InlineData("talk", true, false)]
        [InlineData("talk", true, true)]
        [InlineData("sports", false, false)]
        [InlineData("sports", false, true)]
        [InlineData("sports", true, false)]
        [InlineData("sports", true, true)]
        [InlineData("location", false, false)]
        [InlineData("location", false, true)]
        [InlineData("location", true, false)]
        [InlineData("location", true, true)]
        public async Task AdminPartialImportPolicyControlsExclusionsAcrossCategories(string category, bool allow, bool withBaseline)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, AllowPartialCatalogImport = allow,
                EnableMusic = category == "music", EnableTalk = category == "talk",
                EnableSports = category == "sports", EnableLocation = category == "location"
            };
            if (category == "music") config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            if (category == "location") config.Locations.Add(new CatalogSelection { Id = "r10", Enabled = true });
            var childId = category == "location" ? "r11" : "c123";
            var skipped = false;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var child = request.Parameters.TryGetValue("id", out var id) && id == childId;
                var rows = child ? "<outline type='audio' guide_id='s1' text='Retained'/>"
                    : "<outline type='link' text='Branch' URL='https://opml.radiotime.com/Browse.ashx?id="
                        + childId + (skipped ? "&amp;filter=invalid%2Fvalue" : "") + "'/>"
                        + "<outline type='audio' guide_id='s2' text='" + (skipped ? "Updated" : "Original") + "'/>";
                return Task.FromResult(XDocument.Parse("<opml><body>" + rows + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-admin-partial-{Guid.NewGuid():N}.xml");
            var imports = new List<string[]>();
            try
            {
                var store = new TuneInRefreshStore(path);
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports.Add(ReadStagedTree(staged, token, names: true)[TuneInBrowsing.CategoryName(category)]
                            .OrderBy(name => name, StringComparer.Ordinal).ToArray());
                        return Task.CompletedTask;
                    }, null);
                if (withBaseline)
                {
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                }
                var before = withBaseline ? File.ReadAllText(path) : null;
                skipped = true;
                refresh.Start(category);
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal("Partial", status.State);
                Assert.Equal(category, status.Category);
                Assert.Equal(allow, status.PartialImportAllowed);
                Assert.Equal(1, status.SkippedBrowseLinks);
                var applied = allow || category == "music" && withBaseline;
                Assert.Equal((withBaseline ? 1 : 0) + (applied ? 1 : 0), imports.Count);
                if (applied)
                {
                    Assert.Contains("valid updates applied", status.Phase);
                    Assert.Equal(withBaseline ? new[] { "Retained", "Updated" } : new[] { "Updated" }, imports.Last());
                    Assert.True(Assert.Single(store.Read().Categories).DiscoveryPartial);
                }
                else
                {
                    Assert.Contains("existing library left unchanged", status.Phase);
                    if (withBaseline) Assert.Equal(before, File.ReadAllText(path));
                    else Assert.False(File.Exists(path));
                }

                if (!allow && !applied)
                {
                    config.AllowPartialCatalogImport = true;
                    refresh.Start(category);
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Partial", refresh.Status().State);
                    Assert.Contains("valid updates applied", refresh.Status().Phase);
                    Assert.Equal((withBaseline ? 1 : 0) + 1, imports.Count);
                }
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public async Task RefreshSupportsMoreThanFiveThousandPages(bool musicOnly, bool aboveOldLimit)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, EnableTalk = !musicOnly
            };
            config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            var folderCount = 5000 - (musicOnly ? 1 : 2) + (aboveOldLimit ? 1 : 0);
            var now = DateTime.UtcNow;
            var fetches = 0;
            var leaf = "<outline type='audio' guide_id='s1' text='Radio'/><!--" + new string('x', 1800) + "-->";
            var path = Path.Combine(Path.GetTempPath(), $"tunein-page-limit-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                fetches++;
                var rows = request.Parameters.TryGetValue("id", out var id) && id == "g2698"
                    ? string.Concat(Enumerable.Range(1, folderCount).Select(n =>
                        $"<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c{n}'/>"))
                    : leaf;
                return Task.FromResult(XDocument.Parse($"<opml><body>{rows}</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var imports = 0;
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        Assert.Equal(folderCount, ReadStagedTree(staged, token)["Music"].Length);
                        imports++;
                        return Task.CompletedTask;
                    }, null, clock: () => now);
                refresh.Start(musicOnly ? "music" : null);
                await WaitUntilIdle(refresh, TimeSpan.FromSeconds(60));
                Assert.True(fetches == 5000 + (aboveOldLimit ? 1 : 0), $"Unexpected fetch count {fetches}. " +
                    (refresh.Status().State == "Error" ? refresh.GetFailureLog().Text : refresh.Status().State));
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(1, imports);
                Assert.Equal(folderCount + 2, store.Read().Categories.Single(category => category.Category == "music").Folders.Count);
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
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
        public async Task CatalogDeadlineCancelsAStalledResponseButNotPacerWaits()
        {
            var now = DateTime.UtcNow;
            var pacer = new TuneInRequestPacer(() => now, async (wait, token) =>
            {
                await Task.Delay(150, token);
                now += wait;
            });
            using var initial = await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                { StatusCode = (HttpStatusCode)429 }), 3, CancellationToken.None, catalogueRequest: true);
            var timings = new TuneInRefreshTimings();
            var sentAt = DateTime.MinValue;
            await Assert.ThrowsAsync<TuneInCatalogTimeoutException>(() => TuneInRequests.ReadCatalogAsync(pacer,
                async token =>
                {
                    sentAt = now;
                    Assert.Equal(1, Volatile.Read(ref timings.ActiveResponses));
                    Assert.False(token.IsCancellationRequested);
                    await Task.Delay(Timeout.Infinite, token);
                    return null;
                }, 3, CancellationToken.None, TimeSpan.FromMilliseconds(100), timings));
            Assert.True(sentAt != DateTime.MinValue);
            Assert.True(timings.Backoff > TimeSpan.Zero);
            Assert.Equal(0, Volatile.Read(ref timings.ActiveResponses));
            Assert.Null(timings.DescribeWait());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ParallelDiscoveryTimeoutStopsAndManualRefreshReusesCompletedSibling(bool responseDeadline)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var firstSlowRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowReads = 0;
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                var id = request.Parameters.GetValueOrDefault("id") ?? "root";
                requests.Enqueue(id);
                if (id == "root")
                    return XDocument.Parse("<opml><body>"
                        + "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1'/>"
                        + "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c2'/></body></opml>");
                if (id == "c1")
                {
                    if (++slowReads == 1)
                    {
                        firstSlowRead.TrySetResult(true);
                        if (responseDeadline)
                        {
                            await Task.Delay(100, token);
                            throw new TuneInCatalogTimeoutException();
                        }
                        await Task.Delay(Timeout.Infinite, token);
                    }
                }
                else Assert.True(firstSlowRead.Task.IsCompleted);
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-parallel-retry-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        Assert.Equal(2, ReadStagedTree(staged, token)["Talk"].Length);
                        return Task.CompletedTask;
                    }, null, timeoutOverride: TimeSpan.FromSeconds(2));
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("TimedOut", refresh.Status().State);
                Assert.Equal(1, refresh.Status().DiscoveryTimeouts);
                Assert.Equal(new[] { "root", "c1", "c2" }, requests);
                Assert.Null(refresh.Status().AutomaticRetry);
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Null(store.ReadRecovery());
                Assert.Equal(2, refresh.Status().CacheHits);
                Assert.Equal(new[] { "root", "c1", "c2", "c1" }, requests);
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DiscoveryDeadlineBeforeImportStopsWithoutRetry(bool cancel)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, AllowPartialCatalogImport = true
            };
            config.Music.Add(new CatalogSelection { Id = "g71", Enabled = true });
            TuneInRefresh runningRefresh = null;
            CancellationToken attemptToken = default;
            var delayed = false;
            var requests = 0;
            var imports = 0;
            var directory = new TuneInDirectory(() =>
            {
                if (!delayed && runningRefresh?.Status().Categories.Single().StationCounts?.AvailableStations == 1)
                {
                    delayed = true;
                    if (cancel) runningRefresh.Cancel();
                    else Assert.True(attemptToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
                }
                return config;
            }, (_, __) =>
            {
                requests++;
                return Task.FromResult(XDocument.Parse("<opml><body>"
                    + "<outline type='audio' guide_id='s1' text='Radio'/>"
                    + "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1&amp;filter=bad%20value'/>"
                    + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-preimport-timeout-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports++;
                        Assert.Single(ReadStagedTree(staged, token)["Music"]);
                        return Task.CompletedTask;
                    }, null, timeoutOverride: TimeSpan.FromSeconds(1),
                    countStations: (_, __, token) => { attemptToken = token; return 0; });
                runningRefresh = refresh;
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                Assert.True(delayed);
                Assert.Equal(1, requests);
                if (cancel)
                {
                    Assert.Equal("Cancelled", refresh.Status().State);
                    Assert.Equal(0, refresh.Status().DiscoveryTimeouts);
                    Assert.Equal(0, imports);
                    Assert.Null(store.Read());
                    return;
                }
                Assert.Equal("TimedOut", refresh.Status().State);
                Assert.Equal(1, refresh.Status().DiscoveryTimeouts);
                Assert.Null(refresh.Status().AutomaticRetry);
                Assert.Equal(0, refresh.Status().CacheHits);
                Assert.Equal(1, refresh.Status().SkippedBrowseLinks);
                Assert.Equal(0, imports);
                Assert.Null(store.Read());
                Assert.NotNull(store.ReadDiscovery(directory.Snapshot().DiscoveryKey, "music", DateTime.UtcNow, TimeSpan.FromHours(48)));
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
        }

        [Fact]
        public async Task ImportDeadlineStopsAfterOneAttemptAndReportsTheStoppedPhase()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var requests = 0;
            var imports = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                requests++;
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-timeout-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    async (_, staged, __, token) =>
                    {
                        imports++;
                        ReadStagedTree(staged, token);
                        await Task.Delay(Timeout.Infinite, token);
                    }, null, timeoutOverride: TimeSpan.FromSeconds(1));
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("TimedOut", refresh.Status().State);
                Assert.Equal(0, refresh.Status().DiscoveryTimeouts);
                Assert.Equal(1, refresh.Status().ImportTimeouts);
                Assert.Equal(1, requests);
                Assert.Equal(1, imports);
                Assert.Null(store.ReadRecovery());
                Assert.Null(refresh.Status().AutomaticRetry);
                Assert.True(refresh.Status().ImportMayHavePartiallyApplied);
                Assert.Equal("talk", store.Read().DirtyCategory);
                var report = refresh.GetFailureLog().Text;
                Assert.Contains("Stopped during: Importing", report);
                Assert.Contains("Import timeouts: 1 (no automatic timeout retries)", report);
                Assert.Contains("Import may have partially applied: True", report);
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
        }

        [Theory]
        [InlineData("complete")]
        [InlineData("cancel")]
        [InlineData("deadline")]
        [InlineData("timeout")]
        [InlineData("invalid data")]
        public async Task TerminalImportDoesNotRecoverAutomaticallyAfterRestart(string stop)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var requests = 0;
            var imports = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                requests++;
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-terminal-restart-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            TuneInRefresh runningRefresh = null;
            TuneInRefresh Create() => new TuneInRefresh(directory, store, () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                async (_, staged, __, token) =>
                {
                    imports++;
                    Assert.NotNull(store.ReadRecovery());
                    ReadStagedTree(staged, token);
                    if (stop == "cancel")
                    {
                        runningRefresh.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                    if (stop == "deadline") await Task.Delay(Timeout.Infinite, token);
                    if (stop == "timeout") throw new TimeoutException("Emby import timed out.");
                    if (stop == "invalid data") throw new InvalidDataException("Invalid import state.");
                }, null, timeoutOverride: TimeSpan.FromSeconds(1));
            try
            {
                using (var refresh = Create())
                {
                    runningRefresh = refresh;
                    refresh.Start("talk");
                    await WaitUntilIdle(refresh);
                    Assert.Equal(stop == "complete" ? "Completed" : stop == "cancel" ? "Cancelled"
                        : stop == "invalid data" ? "Error" : "TimedOut", refresh.Status().State);
                    Assert.Null(store.ReadRecovery());
                    Assert.Equal(1, imports);
                }
                using var restarted = Create();
                restarted.CheckSchedules();
                await WaitUntilIdle(restarted);
                Assert.Equal(1, requests);
                Assert.Equal(1, imports);
                Assert.Null(store.ReadRecovery());
            }
            finally
            {
                store.Clear();
                store.ClearDiscovery();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task ServerShutdownDuringImportRecoversOriginalCompleteRequest()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
            };
            var requests = 0;
            var imports = 0;
            var importing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                requests++;
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-restart-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            TuneInRefresh Create() => new TuneInRefresh(directory, store, () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (c, staged, _, token) => c.ImportAsync(staged, token, async () =>
                {
                    imports++;
                    Assert.Equal(new[] { "Talk", "Sports" }, ReadStagedTree(staged, token).Keys);
                    if (imports == 1)
                    {
                        importing.SetResult(true);
                        await Task.Delay(Timeout.Infinite, token);
                    }
                }), null);
            try
            {
                using (var refresh = Create())
                {
                    refresh.Start();
                    await importing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    refresh.Dispose();
                }
                var interrupted = store.ReadRecovery();
                Assert.NotNull(interrupted);
                Assert.Null(interrupted.RequestedCategory);
                Assert.Equal("import", interrupted.Phase);
                Assert.NotNull(store.Read().DirtyCategory);
                using var restarted = Create();
                restarted.CheckSchedules();
                await WaitUntilIdle(restarted);
                Assert.Equal(1, imports);
                Assert.Equal(2, requests);
                Assert.NotNull(store.Read().DirtyCategory);
                restarted.Start();
                await WaitUntilIdle(restarted);
                Assert.Equal("Completed", restarted.Status().State);
                Assert.Equal(2, imports);
                Assert.Equal(4, requests);
                Assert.Null(store.ReadRecovery());
                Assert.Null(store.Read().DirtyCategory);
                Assert.Equal(2, store.Read().Categories.Count);
            }
            finally
            {
                store.Clear();
                store.ClearRecovery();
            }
        }

        [Theory]
        [InlineData("discovery", 0, false)]
        [InlineData("import", 1, true)]
        [InlineData("discovery", 3, true)]
        public async Task InterruptedFirstRefreshRequiresManualRequestEvenWhenScheduleIsDue(string phase, int retries, bool scheduled)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true,
                RefreshSchedules = new List<CategoryRefreshSchedule>
                {
                    new CategoryRefreshSchedule { Category = "talk", IntervalHours = 6 }
                }
            };
            var now = DateTime.UtcNow;
            var imports = 0;
            var requests = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                requests++;
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-startup-recovery-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            store.WriteRecovery(new TuneInRefreshRecovery
            {
                ConfigurationKey = directory.Snapshot().Key,
                RequestedCategory = "talk", Phase = phase, Retries = retries
            });
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports++;
                        Assert.Equal(retries < 3 ? retries + 1 : 0, store.ReadRecovery().Retries);
                        ReadStagedTree(staged, token);
                        return Task.CompletedTask;
                    }, null, clock: () => now);
                refresh.CheckSchedules();
                await WaitUntilIdle(refresh);
                now = now.AddHours(6).AddSeconds(-1);
                refresh.CheckSchedules();
                Assert.Equal(0, imports);
                Assert.Equal(0, requests);
                Assert.Equal("NeverRun", refresh.Status().State);
                Assert.Equal(retries, store.ReadRecovery().Retries);
                if (scheduled)
                {
                    now = now.AddSeconds(1);
                    refresh.CheckSchedules();
                    Assert.Equal(0, requests);
                    Assert.Equal(0, imports);
                    Assert.Equal("NeverRun", refresh.Status().State);
                }
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(1, imports);
                Assert.Equal(1, requests);
                Assert.Null(store.ReadRecovery());
            }
            finally
            {
                store.Clear();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task RepeatedImportFolderRequestsDoNotAdvanceProgressOrHideStalls()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var now = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
            var directory = new TuneInDirectory(() => config, (_, __) =>
                Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>")));
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-progress-{Guid.NewGuid():N}.xml");
            TuneInRefresh runningRefresh = null;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        now = now.AddMinutes(1);
                        var root = Assert.Single(staged.Read(null, token).Items);
                        var firstProgress = runningRefresh.Status().LastProgressUtc;
                        Assert.Equal(now, firstProgress);
                        Assert.Equal(1, runningRefresh.Status().ImportFoldersServed);

                        now = now.AddMinutes(1);
                        Assert.Equal(root.Id, Assert.Single(staged.Read(string.Empty, token).Items).Id);
                        var repeated = runningRefresh.Status();
                        Assert.Equal(firstProgress, repeated.LastProgressUtc);
                        Assert.Equal(1, repeated.ImportFoldersServed);
                        Assert.NotNull(new TuneInImportStallDiagnostics().TryCreate(now, repeated.LastProgressUtc,
                            repeated.ImportFoldersTotal, repeated.ImportFoldersServed, false));

                        now = now.AddMinutes(1);
                        var station = Assert.Single(staged.Read(root.Id, token).Items);
                        var nextProgress = runningRefresh.Status().LastProgressUtc;
                        Assert.Equal(now, nextProgress);
                        Assert.Equal(2, runningRefresh.Status().ImportFoldersServed);
                        now = now.AddMinutes(1);
                        Assert.Equal(station.Id, Assert.Single(staged.Read(root.Id, token).Items).Id);
                        Assert.Equal(nextProgress, runningRefresh.Status().LastProgressUtc);
                        Assert.Equal(2, runningRefresh.Status().ImportFoldersServed);
                        return Task.CompletedTask;
                    }, null, clock: () => now);
                runningRefresh = refresh;
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task ImportGetsAFreshDeadlineInsteadOfDiscoveryTimeRemaining()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var requests = 0;
            var imports = 0;
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                requests++;
                await Task.Delay(1300, token);
                return XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-separate-deadlines-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    async (_, staged, __, token) =>
                    {
                        imports++;
                        await Task.Delay(1300, token);
                        ReadStagedTree(staged, token);
                    }, null, timeoutOverride: TimeSpan.FromSeconds(2));
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(0, refresh.Status().ImportTimeouts);
                Assert.Equal(1, requests);
                Assert.Equal(1, imports);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ImportInterruptionDrainsThenReplaysCompleteTreeAndPreservesPartialStatus(bool partial)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true, EnableTalk = true,
                AllowPartialCatalogImport = true
            };
            config.Music.Add(new CatalogSelection { Id = "g71", Enabled = true });
            var changed = false;
            var requests = new List<string>();
            var imports = 0;
            var retryImports = 0;
            var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDrain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TuneInStagedImport timedOutTree = null;
            TuneInRefresh runningRefresh = null;
            string[] savedTalk = null;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var scope = request.Parameters.GetValueOrDefault("id") ?? request.Parameters["c"];
                requests.Add(scope);
                var malformed = changed && partial && scope == "g71"
                    ? "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1&amp;filter=bad%20value'/>" : "";
                return Task.FromResult(XDocument.Parse("<opml><body>"
                    + $"<outline type='audio' guide_id='s1' text='{(changed ? "Changed" : "Original")}'/>"
                    + malformed + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-replay-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (c, staged, _, token) => c.ImportAsync(staged, token, async () =>
                    {
                        imports++;
                        if (changed)
                        {
                            retryImports++;
                            Assert.Equal(0, runningRefresh.Status().ImportFoldersServed);
                            if (retryImports == 2)
                            {
                                Assert.NotSame(timedOutTree, staged);
                                Assert.Equal(1, runningRefresh.Status().AutomaticRetry);
                                Assert.Equal(3, runningRefresh.Status().AutomaticRetryLimit);
                                Assert.Equal("import", runningRefresh.Status().AutomaticRetryPhase);
                            }
                        }
                        var tree = ReadStagedTree(staged, token);
                        Assert.Equal(new[] { "Music", "Talk" }, tree.Keys);
                        if (!changed) savedTalk = tree["Talk"];
                        else Assert.Equal(savedTalk, tree["Talk"]);
                        if (changed && retryImports == 1)
                        {
                            timedOutTree = staged;
                            try { throw new OperationCanceledException("Emby interrupted the import without cancelling the refresh."); }
                            catch (OperationCanceledException)
                            {
                                drained.TrySetResult(true);
                                await releaseDrain.Task;
                                throw;
                            }
                        }
                    }), null, timeoutOverride: TimeSpan.FromSeconds(5),
                    countStations: (_, __, ___) => 0);
                runningRefresh = refresh;
                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                var savedMusic = store.Read().Categories.Single(category => category.Category == "music").Fingerprint;
                requests.Clear();
                changed = true;
                refresh.Start("music");
                try
                {
                    await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(refresh.Status().Busy);
                    Assert.Equal(1, retryImports);
                    Assert.Equal("Importing", refresh.Status().Phase);
                    Assert.Null(refresh.Status().AutomaticRetry);
                    var pending = store.Read();
                    Assert.Equal("music", pending.DirtyCategory);
                    Assert.Equal(savedMusic, pending.Categories.Single(category => category.Category == "music").Fingerprint);
                }
                finally { releaseDrain.TrySetResult(true); }
                await WaitUntilIdle(refresh);
                Assert.Equal(partial ? "Partial" : "Completed", refresh.Status().State);
                Assert.Equal(0, refresh.Status().DiscoveryTimeouts);
                Assert.Equal(0, refresh.Status().ImportTimeouts);
                Assert.Equal(3, imports);
                Assert.Equal(new[] { "g71" }, requests);
                Assert.False(refresh.Status().ImportMayHavePartiallyApplied);
                Assert.True(refresh.Status().ReportAvailable);
                Assert.Equal(partial ? 1 : 0, refresh.Status().SkippedBrowseLinks);
                Assert.Contains("Automatic interruption retries: 1", refresh.GetFailureLog().Text);
                var saved = store.Read();
                Assert.Null(saved.DirtyCategory);
                Assert.Equal(partial, saved.Categories.Single(category => category.Category == "music").DiscoveryPartial);
                Assert.False(saved.Categories.Single(category => category.Category == "talk").DiscoveryPartial);
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("scope changed")]
        [InlineData("fault after deadline")]
        public async Task ImportTimeoutRecoveryDoesNotOverrideCancellationScopeChangesOrFaults(string stop)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var imports = 0;
            var requests = 0;
            TuneInRefresh runningRefresh = null;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                requests++;
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-import-stop-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (c, staged, _, token) => c.ImportAsync(staged, token, async () =>
                    {
                        imports++;
                        ReadStagedTree(staged, token);
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException)
                        {
                            if (stop == "cancel")
                            {
                                runningRefresh.Start();
                                runningRefresh.Cancel();
                            }
                            else if (stop == "scope changed") config.EnableTalk = false;
                            else throw new IOException("Import failed independently of cancellation.");
                            throw;
                        }
                    }), null, timeoutOverride: TimeSpan.FromSeconds(1));
                runningRefresh = refresh;
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal(stop == "fault after deadline" ? "Error" : "Cancelled", refresh.Status().State);
                Assert.Equal(1, imports);
                Assert.Equal(1, requests);
                Assert.Empty(refresh.Status().QueuedRequests);
                Assert.Equal("talk", store.Read().DirtyCategory);
                Assert.True(refresh.Status().ImportMayHavePartiallyApplied);
            }
            finally
            {
                File.Delete(path);
                store.ClearDiscovery();
            }
        }

        [Fact]
        public async Task DiscoveryCheckpointsAfterThreeThousandStationRowsWithoutWaitingFiveMinutes()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, MaxStationCount = null };
            var now = DateTime.UtcNow;
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                if (request.Parameters.ContainsKey("id"))
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(token);
                    return XDocument.Parse("<opml><body/></opml>");
                }
                var stations = string.Concat(Enumerable.Range(1, 3000)
                    .Select(n => $"<outline type='audio' guide_id='s{n}' text='Radio {n}'/>"));
                return XDocument.Parse("<opml><body>" + stations
                    + "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1'/></body></opml>");
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-station-checkpoint-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) => { ReadStagedTree(staged, token); return Task.CompletedTask; },
                    null, clock: () => now, countStations: (_, __, ___) => 0);
                refresh.Start("talk");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(now, refresh.Status().DiscoveryCheckpointUtc);
                Assert.NotNull(store.ReadDiscovery(directory.Snapshot().DiscoveryKey, "talk", now, TimeSpan.FromHours(48)));
                release.TrySetResult(true);
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
            }
            finally
            {
                release.TrySetResult(true);
                store.Clear();
                store.ClearDiscovery();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task DiscoveryStartsFourIndependentSiblingFolders()
        {
            var directory = new TuneInDirectory(() => new PluginConfiguration(), (_, __) => throw new InvalidOperationException());
            var channel = new TuneInChannel(directory);
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow);
            var started = 0;
            var allStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var root = new ChannelItemInfo { Id = "root" };
            var discovery = TuneInStagedImport.DiscoverAsync(channel, root, session, CancellationToken.None,
                async (id, _, __, token) =>
                {
                    if (id == "root")
                        return new ChannelItemResult
                        {
                            Items = Enumerable.Range(1, 4).Select(n => new ChannelItemInfo
                            { Id = "child" + n, Type = ChannelItemType.Folder }).ToList()
                        };
                    if (Interlocked.Increment(ref started) == 4) allStarted.TrySetResult(true);
                    await release.Task.WaitAsync(token);
                    return new ChannelItemResult { Items = new List<ChannelItemInfo>() };
                }, null, null, (_, __) => false);
            try { await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { release.TrySetResult(true); }
            var result = await discovery.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(5, result.Count);
        }

        [Fact]
        public async Task RecentSuccessfulCategoryPagesAreReusedOnlyAcrossCategoriesAndExpire()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
            };
            var now = DateTime.UtcNow;
            var fetches = 0;
            var incomplete = false;
            var request = new DirectoryRequest();
            request.Parameters["id"] = "c1";
            var directory = new TuneInDirectory(() => config, (_, __) => throw new InvalidOperationException());
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-recent-pages-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    async (_, __, session, ___, token) =>
                    {
                        await session.FetchAsync(request, () =>
                        {
                            fetches++;
                            return Task.FromResult(XDocument.Parse("<opml><body/></opml>"));
                        }, token);
                        if (incomplete)
                            Assert.True(session.TrySkipCatalogLink("talk", "invalid catalog link", request,
                                "https://opml.radiotime.com/Browse.ashx?filter=invalid"));
                        return new ChannelItemResult { Items = new List<ChannelItemInfo>() };
                    },
                    (_, staged, __, token) => { ReadStagedTree(staged, token); return Task.CompletedTask; },
                    null, clock: () => now, countStations: (_, __, ___) => 0);
                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(1, fetches);
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal(2, fetches);
                refresh.Start("sports");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(2, fetches);
                Assert.Equal(1, refresh.Status().CacheHits);
                refresh.Start("sports");
                await WaitUntilIdle(refresh);
                Assert.Equal(3, fetches);
                now = now.AddMinutes(10);
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal(4, fetches);
                incomplete = true;
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Partial", refresh.Status().State);
                Assert.Equal(5, fetches);
                incomplete = false;
                refresh.Start("sports");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.Equal(6, fetches);
            }
            finally
            {
                store.Clear();
                store.ClearDiscovery();
                store.ClearRecovery();
            }
        }

        [Fact]
        public async Task ParallelRequestsKeepGlobalSpacingAndDrainAfterRateLimit()
        {
            var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
            var start = now;
            var pacer = new TuneInRequestPacer(() => now, (duration, _) =>
            {
                now += duration;
                return Task.CompletedTask;
            });
            var timings = new TuneInRefreshTimings(TimeSpan.FromSeconds(75));
            var first = new TaskCompletionSource<HttpResponseInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<HttpResponseInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var third = new TaskCompletionSource<HttpResponseInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fourth = new TaskCompletionSource<HttpResponseInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fifthStarted = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
            var one = pacer.SendAsync(() => first.Task, 3, CancellationToken.None, true, timings);
            var two = pacer.SendAsync(() => second.Task, 3, CancellationToken.None, true, timings);
            Assert.Equal(start.AddSeconds(3), now);
            var three = pacer.SendAsync(() => third.Task, 3, CancellationToken.None, true, timings);
            var four = pacer.SendAsync(() => fourth.Task, 3, CancellationToken.None, true, timings);
            Assert.Equal(start.AddSeconds(9), now);
            var five = pacer.SendAsync(() =>
            {
                fifthStarted.SetResult(now);
                return Task.FromResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            }, 3, CancellationToken.None, true, timings);
            var six = pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                { StatusCode = HttpStatusCode.OK }), 3, CancellationToken.None, true, timings);
            Assert.False(fifthStarted.Task.IsCompleted);
            second.SetResult(new HttpResponseInfo
            {
                StatusCode = (HttpStatusCode)429,
                Headers = new Dictionary<string, string> { ["Retry-After"] = "75" }
            });
            using var limited = await two;
            Assert.False(fifthStarted.Task.IsCompleted);
            first.SetResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            using var initial = await one;
            third.SetResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            fourth.SetResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            using var thirdResponse = await three;
            using var fourthResponse = await four;
            Assert.Equal(start.AddSeconds(84), await fifthStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            using var recovered = await five;
            using var alsoRecovered = await six;
            Assert.Equal(TimeSpan.FromSeconds(75), timings.RateLimitWait);
        }

        [Fact]
        public async Task DiscoveryStopsAfterThreeInterruptionRetriesAndLeavesExistingLibraryUntouched()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var block = false;
            var attempts = 0;
            var activeRetries = new List<(int? Number, int? Limit)>();
            TuneInRefresh runningRefresh = null;
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                if (block)
                {
                    attempts++;
                    var active = runningRefresh.Status();
                    activeRetries.Add((active.AutomaticRetry, active.AutomaticRetryLimit));
                    await Task.Yield();
                    throw new IOException("Provider connection interrupted.");
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
                runningRefresh = refresh;
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                var checkpoint = File.ReadAllText(path);

                block = true;
                refresh.Start("talk");
                await WaitUntilIdle(refresh, TimeSpan.FromSeconds(30));
                Assert.Equal("Error", refresh.Status().State);
                Assert.Equal(0, refresh.Status().DiscoveryTimeouts);
                Assert.Equal(4, attempts);
                Assert.Equal((null, null), activeRetries[0]);
                Assert.Equal(Enumerable.Range(1, 3).Select(n => ((int?)n, (int?)3)), activeRetries.Skip(1));
                Assert.Null(refresh.Status().AutomaticRetry);
                Assert.Contains("interruption retry limit reached", refresh.Status().Phase);
                Assert.Null(refresh.Status().DiscoveryCheckpointUtc);
                Assert.Equal(1, imports);
                Assert.Equal(checkpoint, File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public async Task DiscoveryReusesCheckpointOnTimeoutOrRestart(bool restart, bool periodic)
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
                    now = initial.AddMinutes(4);
                    return XDocument.Parse("<opml><body><outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1'/></body></opml>");
                }
                if (id == "c1")
                {
                    Assert.False(File.Exists(path + ".discovery"));
                    now = initial.AddMinutes(periodic ? 5 : 4);
                    return XDocument.Parse("<opml><body><outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c2'/></body></opml>");
                }
                if (++childAttempts == 1)
                {
                    Assert.Equal(periodic, File.Exists(path + ".discovery"));
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
                    Assert.Equal(periodic ? initial.AddMinutes(5) : (DateTime?)null, refresh.Status().DiscoveryCheckpointUtc);
                    var key = directory.Snapshot().DiscoveryKey;
                    Assert.Equal(periodic, store.ReadDiscovery(key, "talk", now, TimeSpan.FromHours(48)) != null);
                    Assert.Null(store.ReadDiscovery(key, null, now, TimeSpan.FromHours(48)));
                    Assert.Null(store.ReadDiscovery(new string('0', 64), "talk", now, TimeSpan.FromHours(48)));
                    Assert.Null(store.ReadDiscovery(key, "talk", initial.AddHours(48), TimeSpan.FromHours(48)));
                    if (restart) refresh.Dispose();
                    await WaitUntilIdle(refresh);
                    if (restart)
                    {
                        Assert.Equal("Cancelled", refresh.Status().State);
                        Assert.Equal(0, imports);
                    }
                    else
                    {
                        Assert.Equal("TimedOut", refresh.Status().State);
                        Assert.Equal(1, refresh.Status().DiscoveryTimeouts);
                        Assert.Contains("1 discovery timeout", refresh.Status().Phase);
                        Assert.Equal(0, imports);
                        Assert.Null(refresh.Status().AutomaticRetry);
                    }
                }
                now = initial.AddHours(47);
                using (var refresh = NewRefresh())
                {
                    refresh.CheckSchedules();
                    Assert.False(refresh.Status().Busy);
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
        [InlineData(true, true, 5)]
        [InlineData(false, true, 5)]
        [InlineData(true, false, 5)]
        [InlineData(true, true, 50)]
        [InlineData(false, true, 50)]
        public async Task FolderAndPaginationTransitionsConsumeConfiguredDepthAndLimitedResultsImport(bool music, bool paginate, int depth)
        {
            var category = music ? "music" : "talk";
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = music, EnableTalk = !music,
                TraversalDepthLimit = depth
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
                        var stations = ReadStagedTree(staged, token, staged.ImportRefreshLevel)[TuneInBrowsing.CategoryName(category)];
                        if (depth == 5) Assert.Empty(stations);
                        else Assert.Single(stations);
                        staged.Verify();
                        imports++;
                        return Task.CompletedTask;
                    }, null);
                refresh.Start(category);
                await WaitUntilIdle(refresh);
                Assert.Equal(depth == 5 ? "Limited" : "Completed", refresh.Status().State);
                Assert.Equal(1, imports);
                Assert.Equal(depth == 5 ? music ? 5 : 6 : 12, fetches);
                if (depth == 5) Assert.Contains("depth limit 5", new TuneInRefreshStore(path).Read().Categories.Single().DiscoveryLimitReason);
                var limitReason = refresh.Status().Categories.Single(item => item.Category == category).LimitReason;
                if (depth == 5) Assert.Contains("depth limit 5", limitReason);
                else Assert.Null(limitReason);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task UnexpectedPaginationJumpIsSkippedAndImportsPartialResults()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true };
            config.Music.Add(new CatalogSelection { Id = "g71", Enabled = true });
            var fetches = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                fetches++;
                return Task.FromResult(XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/>"
                    + "<outline type='link' key='nextStations' text='More' URL='https://opml.radiotime.com/Browse.ashx?offset=26&amp;id=c999&amp;filter=s'/>"
                    + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-jump-{Guid.NewGuid():N}.xml");
            var imported = 0;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imported = ReadStagedTree(staged, token, staged.ImportRefreshLevel)[TuneInBrowsing.CategoryName("music")].Count();
                        staged.Verify();
                        return Task.CompletedTask;
                    }, null);
                refresh.Start("music");
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal("Partial", status.State);
                Assert.Equal(1, status.SkippedBrowseLinks);
                Assert.Contains("g71 to c999", status.SkippedBrowseLinkDetails);
                Assert.Equal(1, imported);
                Assert.Equal(1, fetches);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("music", "none")]
        [InlineData("music", "saved")]
        [InlineData("music", "dirty")]
        [InlineData("music", "rebuild")]
        [InlineData("talk", "none")]
        [InlineData("talk", "saved")]
        public async Task PartialImportExcludesInvalidStationUrlsAndRetainsAvailableSavedStations(string category, string baseline)
        {
            var withBaseline = baseline != "none";
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = category == "music",
                EnableTalk = category == "talk", AllowPartialCatalogImport = true
            };
            if (category == "music") config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
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
                var store = new TuneInRefreshStore(path);
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports.Add(ReadStagedTree(staged, token, names: true)[TuneInBrowsing.CategoryName(category)]
                            .OrderBy(name => name, StringComparer.Ordinal).ToArray());
                        return Task.CompletedTask;
                    }, null);
                if (withBaseline)
                {
                    refresh.Start(category);
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Completed", refresh.Status().State);
                    Assert.Equal(new[] { "Original", "Saved" }, Assert.Single(imports));
                    var checkpoint = store.Read();
                    if (baseline == "dirty") checkpoint.DirtyCategory = category;
                    if (baseline == "rebuild") checkpoint.ChannelImportVersion = 0;
                    store.Write(checkpoint);
                }

                mode = 1;
                refresh.Start(category);
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal("Partial", status.State);
                Assert.Contains("valid updates applied", status.Phase);
                Assert.Equal(1, status.SkippedBrowseLinks);
                Assert.Contains("station URL: Invalid catalog fragment", status.SkippedBrowseLinkDetails);
                Assert.DoesNotContain("not-for-diagnostics", status.SkippedBrowseLinkDetails);
                Assert.Equal(withBaseline ? 2 : 1, imports.Count);
                Assert.Equal(withBaseline ? new[] { "New", "Saved", "Updated" } : new[] { "New", "Updated" },
                    imports.Last());
                var partialCheckpoint = store.Read();
                Assert.True(Assert.Single(partialCheckpoint.Categories).DiscoveryPartial);
                Assert.All(partialCheckpoint.Categories.SelectMany(saved => saved.Folders).SelectMany(folder => folder.Items),
                    item => Assert.False(item.ForceUpdate));
                Assert.Null(partialCheckpoint.DirtyCategory);
                var checkpointBeforeFailure = File.ReadAllText(path);

                mode = 2;
                refresh.Start(category);
                await WaitUntilIdle(refresh);
                Assert.Equal("Error", refresh.Status().State);
                Assert.Contains("Untrusted TuneIn catalog link", refresh.GetFailureLog().Text);
                Assert.Equal(withBaseline ? 2 : 1, imports.Count);
                Assert.Equal(checkpointBeforeFailure, File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("talk", "untrusted", "Error", "InvalidDataException")]
        [InlineData("talk", "transport", "Error", "IOException")]
        [InlineData("talk", "cancelled", "Cancelled", "Cancelled")]
        [InlineData("music", "untrusted", "Error", "InvalidDataException")]
        [InlineData("music", "transport", "Error", "IOException")]
        [InlineData("music", "cancelled", "Cancelled", "Cancelled")]
        public async Task UnrecoverableDiscoveryDoesNotBecomePartialAfterARejectedLink(
            string category, string failure, string state, string errorCode)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true,
                EnableTalk = category == "talk", EnableMusic = category == "music", AllowPartialCatalogImport = true
            };
            if (category == "music") config.Music.Add(new CatalogSelection { Id = "g2698", Enabled = true });
            TuneInRefresh runningRefresh = null;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                if (request.Parameters.TryGetValue("id", out var id) && id != "g2698")
                {
                    if (failure == "transport") throw new IOException("Provider connection failed.");
                    if (failure == "cancelled")
                    {
                        runningRefresh.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
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
                runningRefresh = refresh;
                refresh.Start();
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal(state, status.State);
                Assert.Equal(errorCode, status.ErrorCode);
                Assert.Equal(1, status.SkippedBrowseLinks);
                Assert.Equal(0, imports);
                Assert.False(File.Exists(path));
                if (state == "Error")
                    Assert.Contains("Skipped malformed catalog links: 1. By reason: 1 x "
                        + TuneInBrowsing.CategoryName(category) + ": Invalid catalog link parameter: filter",
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
        public async Task MissingDiscoveryCountsUseDistinctInventoryAndAreUnavailableForPartialResults()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true,
                AllowPartialCatalogImport = true
            };
            var partial = false;
            var includeMissing = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(XDocument.Parse(
                "<opml><body><outline type='audio' guide_id='s1' text='Radio'/>"
                + (partial ? "<outline type='link' text='Skipped' URL='https://opml.radiotime.com/Browse.ashx?id=c123&amp;filter=bad%2Ffilter'/>" : "")
                + "</body></opml>")));
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-missing-{Guid.NewGuid():N}.xml");
            try
            {
                var store = new TuneInRefreshStore(path);
                using (var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) => { ReadStagedTree(staged, token); return Task.CompletedTask; }, null,
                    stationIds: (_, __, ___) => new HashSet<string>(
                        includeMissing ? new[] { "s1", "s2", "s2" } : new[] { "s1" }, StringComparer.Ordinal)))
                {
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    var counts = Assert.Single(refresh.Status().Categories).StationCounts;
                    Assert.Equal(2, counts.EmbyStations);
                    Assert.Equal(1, counts.NotInLatestDiscovery);
                    includeMissing = false;
                    refresh.Start("talk");
                    await WaitUntilIdle(refresh);
                    Assert.Equal(0, Assert.Single(refresh.Status().Categories).StationCounts.NotInLatestDiscovery);
                }
                using (var restored = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) => { ReadStagedTree(staged, token); return Task.CompletedTask; }, null,
                    stationIds: (_, __, ___) => new HashSet<string>(new[] { "s1", "s2" }, StringComparer.Ordinal)))
                {
                    Assert.Equal(0, Assert.Single(restored.Status().Categories).StationCounts.NotInLatestDiscovery);
                    partial = true;
                    restored.Start("talk");
                    await WaitUntilIdle(restored);
                    var counts = Assert.Single(restored.Status().Categories).StationCounts;
                    Assert.True(counts.AvailablePartial);
                    Assert.Null(counts.NotInLatestDiscovery);
                }
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StationCountsDistinguishDiscoveryFromEmbyAndPersistOnlyAppliedSnapshots(bool allowPartial)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true,
                AllowPartialCatalogImport = allowPartial
            };
            var skip = false;
            var inventoryReads = 0;
            var inventoryCount = 0;
            var imports = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var rows = request.Parameters.ContainsKey("id")
                    ? "<outline type='audio' guide_id='s2' text='Saved station'/>"
                    : "<outline type='audio' guide_id='s1' text='First listing'/>"
                        + "<outline type='audio' guide_id='s1' text='Duplicate station, different listing'/>"
                        + "<outline type='link' text='Saved branch' URL='https://opml.radiotime.com/Browse.ashx?id=c123"
                        + (skip ? "&amp;filter=bad%2Ffilter" : "") + "'/>";
                return Task.FromResult(XDocument.Parse("<opml><body>" + rows + "</body></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-counts-{Guid.NewGuid():N}.xml");
            try
            {
                using (var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        ReadStagedTree(staged, token);
                        imports++;
                        inventoryCount = 3;
                        return Task.CompletedTask;
                    }, null, countStations: (_, __, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        inventoryReads++;
                        return inventoryCount;
                    }))
                {
                    Assert.Null(Assert.Single(refresh.Status().Categories).StationCounts);
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    var counts = Assert.Single(refresh.Status().Categories).StationCounts;
                    Assert.Equal(2, counts.AvailableStations);
                    Assert.Equal(3, counts.EmbyStations);
                    Assert.False(counts.AvailablePartial);
                    Assert.NotNull(counts.AvailableCheckedUtc);
                    Assert.NotNull(counts.EmbyCheckedUtc);
                    var readsAfterImport = inventoryReads;
                    counts.AvailableStations = 99;
                    Assert.Equal(2, Assert.Single(refresh.Status().Categories).StationCounts.AvailableStations);
                    Assert.Equal(readsAfterImport, inventoryReads);
                    skip = true;
                    refresh.Start("talk");
                    await WaitUntilIdle(refresh);
                    Assert.Equal("Partial", refresh.Status().State);
                    Assert.Equal(allowPartial ? 2 : 1, imports);
                    counts = Assert.Single(refresh.Status().Categories).StationCounts;
                    Assert.Equal(1, counts.AvailableStations);
                    Assert.True(counts.AvailablePartial);
                    Assert.Equal(3, counts.EmbyStations);
                    Assert.True(refresh.Status().ReportAvailable);
                    Assert.Contains("Invalid catalog link parameter: filter", refresh.GetFailureLog().Text);
                }
                var readsBeforeRestart = inventoryReads;
                using var restored = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (_, __, ___, ____, _____) => throw new Exception("No discovery expected."),
                    (_, __, ___, ____) => throw new Exception("No import expected."), null);
                var savedCounts = Assert.Single(restored.Status().Categories).StationCounts;
                Assert.Equal(allowPartial ? 1 : 2, savedCounts.AvailableStations);
                Assert.Equal(allowPartial, savedCounts.AvailablePartial);
                Assert.Equal(3, savedCounts.EmbyStations);
                Assert.Equal(readsBeforeRestart, inventoryReads);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        public async Task LastKnownEmbyCountSurvivesFailedReadsRestartAndReconciliation(int inventory)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(
                XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>")));
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-last-count-{Guid.NewGuid():N}.xml");
            var store = new TuneInRefreshStore(path);
            var failRead = false;
            var currentInventory = inventory;
            var now = DateTime.UtcNow;
            TuneInRefresh Create() => new TuneInRefresh(directory, store, () => channel,
                (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                (_, staged, __, token) =>
                {
                    ReadStagedTree(staged, token);
                    return Task.CompletedTask;
                }, null, clock: () => now, countStations: (_, __, ___) => failRead
                    ? throw new IOException("Synthetic inventory failure.") : currentInventory);
            try
            {
                DateTime? checkedAt;
                using (var refresh = Create())
                {
                    refresh.Start();
                    await WaitUntilIdle(refresh);
                    var counts = Assert.Single(refresh.Status().Categories).StationCounts;
                    Assert.Equal(inventory, counts.EmbyStations);
                    checkedAt = counts.EmbyCheckedUtc;
                    Assert.NotNull(checkedAt);
                }
                failRead = true;
                now = now.AddHours(1);
                using (var reopened = Create())
                {
                    Assert.Equal(inventory, Assert.Single(reopened.Status().Categories).StationCounts.EmbyStations);
                    reopened.Start("talk");
                    await WaitUntilIdle(reopened);
                    Assert.Equal("Completed", reopened.Status().State);
                    var counts = Assert.Single(reopened.Status().Categories).StationCounts;
                    Assert.Equal(inventory, counts.EmbyStations);
                    Assert.Equal(checkedAt, counts.EmbyCheckedUtc);
                    Assert.Equal("Emby inventory read failed; check server log", counts.EmbyCountError);
                }
                var checkpoint = store.Read();
                var persisted = Assert.Single(checkpoint.Categories).StationCounts;
                Assert.Equal(inventory, persisted.EmbyStations);
                Assert.Equal(checkedAt, persisted.EmbyCheckedUtc);
                checkpoint.DirtyCategory = "talk";
                store.Write(checkpoint);
                using var restored = Create();
                var saved = Assert.Single(restored.Status().Categories).StationCounts;
                Assert.Equal(inventory, saved.EmbyStations);
                Assert.Equal(checkedAt, saved.EmbyCheckedUtc);
                Assert.Equal("Import needs reconciliation", saved.EmbyCountError);
                failRead = false;
                currentInventory = 0;
                now = now.AddHours(1);
                restored.Start();
                await WaitUntilIdle(restored);
                Assert.Equal("Completed", restored.Status().State);
                var recovered = Assert.Single(restored.Status().Categories).StationCounts;
                Assert.Equal(0, recovered.EmbyStations);
                Assert.Equal(now, recovered.EmbyCheckedUtc);
                Assert.Null(recovered.EmbyCountError);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InventoryReadFailureIsExplicitAndCancellationIsNotSwallowed(bool cancel)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(
                XDocument.Parse("<opml><body><outline type='audio' guide_id='s1' text='Radio'/></body></opml>")));
            var channel = new TuneInChannel(directory);
            var imports = 0;
            var path = Path.Combine(Path.GetTempPath(), $"tunein-inventory-failure-{Guid.NewGuid():N}.xml");
            TuneInRefresh runningRefresh = null;
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        imports++;
                        ReadStagedTree(staged, token);
                        return Task.CompletedTask;
                    }, null, countStations: (_, __, token) =>
                    {
                        if (cancel)
                        {
                            runningRefresh.Cancel();
                            token.ThrowIfCancellationRequested();
                        }
                        throw new IOException("Synthetic inventory failure.");
                    });
                runningRefresh = refresh;
                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal(cancel ? "Cancelled" : "Completed", refresh.Status().State);
                Assert.Equal(cancel ? 0 : 1, imports);
                if (!cancel)
                {
                    var counts = Assert.Single(refresh.Status().Categories).StationCounts;
                    Assert.Equal(1, counts.AvailableStations);
                    Assert.Null(counts.EmbyStations);
                    Assert.Null(counts.NotInLatestDiscovery);
                    Assert.Equal("Emby inventory read failed; check server log", counts.EmbyCountError);
                }
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(5)]
        [InlineData(150)]
        public async Task SkippedEntryReportCapturesReproducibleDetailsWithRedactionAndBounds(int skipped)
        {
            const string privateValue = "private-credential-marker";
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, AllowPartialCatalogImport = true
            };
            var reject = true;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                request.Parameters["username"] = privateValue;
                request.Parameters["serial"] = privateValue;
                request.Parameters["latlon"] = privateValue;
                var body = new XElement("body", new XElement("outline",
                    new XAttribute("type", "audio"), new XAttribute("guide_id", "s1"),
                    new XAttribute("text", "private-entry-label")));
                if (reject)
                    for (var index = 0; index < skipped; index++)
                        body.Add(new XElement("outline", new XAttribute("type", "link"),
                            new XAttribute("text", "private-entry-label"),
                            new XAttribute("URL", "https://opml.radiotime.com/Browse.ashx?id=c123&filter=bad%2Ffilter"
                                + "&serial=" + privateValue + "&token=" + privateValue
                                + (index == skipped - 1 ? "" : "#unsupported&token=" + privateValue))));
                return Task.FromResult(new XDocument(new XElement("opml", body)));
            });
            var channel = new TuneInChannel(directory);
            var path = Path.Combine(Path.GetTempPath(), $"tunein-skips-report-{Guid.NewGuid():N}.xml");
            try
            {
                using var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) => { ReadStagedTree(staged, token); return Task.CompletedTask; }, null);
                refresh.Start();
                await WaitUntilIdle(refresh);
                var status = refresh.Status();
                Assert.Equal("Partial", status.State);
                Assert.True(status.ReportAvailable);
                var report = refresh.GetFailureLog();
                Assert.Equal(status.FinishedUtc, report.FinishedUtc);
                Assert.Contains("State: Partial", report.Text);
                Assert.Contains("Source: /Browse.ashx; c[1]=\"talk\"", report.Text);
                Assert.Contains("Rejected link: /Browse.ashx; id[1]=\"c123\"; filter[1]=\"bad/filter\"", report.Text);
                Assert.Contains("fragment=\"#unsupported&token=[redacted]\"", report.Text);
                Assert.DoesNotContain(privateValue, report.Text);
                Assert.DoesNotContain("private-entry-label", report.Text);
                Assert.DoesNotContain("bad/filter", System.Text.Json.JsonSerializer.Serialize(status));
                Assert.True(report.Text.Length <= 64 * 1024);
                Assert.Equal(skipped > 100, report.Truncated);
                if (skipped > 100) Assert.Contains("Additional skipped-entry details omitted:", report.Text);
                else Assert.Contains("Invalid catalog link parameter: filter", report.Text);
                var service = new TuneInRefreshService(refresh);
                Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInRefreshFailureLog()));

                reject = false;
                refresh.Start();
                Assert.Throws<InvalidOperationException>(() => refresh.GetFailureLog());
                await WaitUntilIdle(refresh);
                Assert.False(refresh.Status().ReportAvailable);
                Assert.Throws<InvalidOperationException>(() => refresh.GetFailureLog());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void CatalogDiagnosticsPreserveDuplicateFiltersAndEscapeUntrustedFields()
        {
            var report = TuneInCatalogDiagnostics.DescribeLink("https://opml.radiotime.com/Browse.ashx?"
                + "id=c123&filter=invalid%2Ffilter&filter=s%3A~A&pivot=bad%0Aline&sid=secret-sid&partnerid=secret-partner"
                + "#bad%26token%3Dsecret-token");
            Assert.Contains("filter[2]=\"invalid/filter\", \"s:~A\"", report);
            Assert.Contains("pivot[1]=\"bad\\u000aline\"", report);
            Assert.DoesNotContain("\n", report);
            Assert.DoesNotContain("secret-", report);
            Assert.Contains("decoded for redaction", report);
            foreach (var fragment in new[] { "token=space secret-value", "filter=token=secret-value",
                "token%25253Dsecret-value" })
                Assert.DoesNotContain("secret-value", TuneInCatalogDiagnostics.DescribeLink(
                    "https://opml.radiotime.com/Browse.ashx?filter=invalid#" + fragment));
            Assert.Equal("(trusted catalog link unavailable)",
                TuneInCatalogDiagnostics.DescribeLink("https://example.invalid/?token=secret"));
            Assert.Contains("[truncated; 701 characters]", TuneInCatalogDiagnostics.DescribeLink(
                "https://opml.radiotime.com/Browse.ashx?filter=" + new string('a', 701)));
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
                    (c, staged, folder, token) => c.ImportAsync(staged, token, async () =>
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
                    Assert.Contains(imported[0]["Sports"].Single(), imported[1]["Sports"]);
                    Assert.Equal(2, imported[1]["Sports"].Length);
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
                    (c, staged, folder, token) => c.ImportAsync(staged, token, async () =>
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
                        Assert.Throws<InvalidOperationException>(() => refresh.SetSettingsLock(() => false));
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
        public async Task CataloguePacingRecoversGraduallyWithoutExceedingConfiguredRate()
        {
            var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
            var pacer = new TuneInRequestPacer(() => now, (duration, _) =>
            {
                now += duration;
                return Task.CompletedTask;
            });
            var timings = new TuneInRefreshTimings();
            async Task<TimeSpan> Send(int code)
            {
                var before = now;
                using var response = await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                {
                    StatusCode = (HttpStatusCode)code,
                    Headers = new Dictionary<string, string> { ["Retry-After"] = "45" }
                }), 3, CancellationToken.None, catalogueRequest: true, timings: timings);
                return now - before;
            }

            Assert.Equal(TimeSpan.Zero, await Send(429));
            Assert.Equal(TimeSpan.FromSeconds(45), await Send(200));
            for (var i = 0; i < 18; i++) Assert.Equal(TimeSpan.FromSeconds(6), await Send(200));
            Assert.Equal(TimeSpan.FromSeconds(6), await Send(500));
            for (var i = 0; i < 20; i++) Assert.Equal(TimeSpan.FromSeconds(6), await Send(200));
            for (var interval = 5; interval >= 3; interval--)
                for (var i = 0; i < 21 - (interval > 3 ? 1 : 0); i++)
                    Assert.Equal(TimeSpan.FromSeconds(interval), await Send(200));
            Assert.Equal(3, timings.MinIntervalSeconds);
            Assert.Equal(6, timings.MaxIntervalSeconds);
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
        public async Task RateLimitRetryStreakResetsOnSuccessfulPagesAndDoesNotRetryOtherErrors()
        {
            var rateLimitRetries = 0;
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => DateTime.UtcNow,
                retryRateLimit: true, rateLimitRetry: () => rateLimitRetries++);
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
            Assert.Equal(7, finalAttempts);
            Assert.Equal(9, rateLimitRetries);
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

        [Fact]
        public async Task InFlightSuccessDoesNotResetNewerRateLimitStreak()
        {
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => DateTime.UtcNow,
                retryRateLimit: true, timings: new TuneInRefreshTimings());
            var firstResponse = new TaskCompletionSource<XDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var continueSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRequest = new DirectoryRequest();
            firstRequest.Parameters["id"] = "first";
            var secondRequest = new DirectoryRequest();
            secondRequest.Parameters["id"] = "second";
            var first = session.FetchAsync(firstRequest, () => firstResponse.Task, CancellationToken.None);
            var attempts = 0;
            async Task<XDocument> Limited()
            {
                var attempt = ++attempts;
                if (attempt == 2)
                {
                    secondStarted.SetResult(true);
                    await continueSecond.Task;
                }
                if (attempt <= 7) throw new TuneInProviderResponseException(429);
                return XDocument.Parse("<opml><body/></opml>");
            }
            var second = session.FetchAsync(secondRequest, Limited, CancellationToken.None);
            await secondStarted.Task;
            firstResponse.SetResult(XDocument.Parse("<opml><body/></opml>"));
            await first;
            continueSecond.SetResult(true);
            await Assert.ThrowsAsync<TuneInProviderResponseException>(() => second);
            Assert.Equal(7, attempts);
        }

        [Fact]
        public async Task SuccessQueuedBeforeRateLimitResetsStreakWhenActuallySentAfterIt()
        {
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => DateTime.UtcNow,
                retryRateLimit: true, timings: new TuneInRefreshTimings());
            var limited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startSuccess = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRequest = new DirectoryRequest();
            firstRequest.Parameters["id"] = "limited";
            var secondRequest = new DirectoryRequest();
            secondRequest.Parameters["id"] = "queued";
            var attempts = 0;
            async Task<XDocument> FetchLimited()
            {
                var attempt = ++attempts;
                if (attempt == 1)
                {
                    TuneInCatalogueSession.MarkRequestStarted();
                    await limited.Task;
                }
                else if (attempt == 2) await resume.Task;
                if (attempt <= 7)
                {
                    TuneInCatalogueSession.MarkResponseReceived(429);
                    if (attempt == 1) observed.SetResult(true);
                    throw new TuneInProviderResponseException(429);
                }
                return XDocument.Parse("<opml><body/></opml>");
            }
            async Task<XDocument> FetchQueued()
            {
                await startSuccess.Task;
                TuneInCatalogueSession.MarkRequestStarted();
                return XDocument.Parse("<opml><body/></opml>");
            }
            var first = session.FetchAsync(firstRequest, FetchLimited, CancellationToken.None);
            var second = session.FetchAsync(secondRequest, FetchQueued, CancellationToken.None);
            limited.SetResult(true);
            await observed.Task;
            startSuccess.SetResult(true);
            await second;
            resume.SetResult(true);
            await first;
            Assert.Equal(8, attempts);
        }

        [Theory]
        [InlineData(301, 0)]
        [InlineData(240, 70)]
        public async Task RateLimitWaitBudgetRejectsExcessWithoutRetryingEarly(int firstDelay, int secondDelay)
        {
            var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
            var pacer = new TuneInRequestPacer(() => now, (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                now += delay;
                return Task.CompletedTask;
            });
            var timings = new TuneInRefreshTimings(TimeSpan.FromMinutes(5));
            Task<HttpResponseInfo> Limited(int seconds) => Task.FromResult(new HttpResponseInfo
            {
                StatusCode = (HttpStatusCode)429,
                Headers = new Dictionary<string, string> { ["Retry-After"] = seconds.ToString() }
            });
            using (await pacer.SendAsync(() => Limited(firstDelay), 3, CancellationToken.None, true, timings)) { }
            if (secondDelay != 0)
            {
                using (await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
                    { StatusCode = HttpStatusCode.OK }), 3, CancellationToken.None, true, timings)) { }
                using (await pacer.SendAsync(() => Limited(secondDelay), 3, CancellationToken.None, true, timings)) { }
            }
            var before = now;
            var sent = false;
            await Assert.ThrowsAsync<TuneInRateLimitWaitExceededException>(() => pacer.SendAsync(() =>
            {
                sent = true;
                return Task.FromResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            }, 3, CancellationToken.None, true, timings));
            Assert.False(sent);
            Assert.Equal(before, now);
        }

        [Fact]
        public async Task CatalogSessionUsesSharedPacerWaitBudget()
        {
            var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
            var pacer = new TuneInRequestPacer(() => now, (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            });
            var timings = new TuneInRefreshTimings(TimeSpan.FromMinutes(5));
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => now,
                retryRateLimit: true, timings: timings);
            var attempts = 0;
            await Assert.ThrowsAsync<TuneInRateLimitWaitExceededException>(() => session.FetchAsync(
                new DirectoryRequest(), () => TuneInRequests.ReadCatalogAsync(pacer, _ =>
                {
                    attempts++;
                    return Task.FromResult(new HttpResponseInfo
                    {
                        StatusCode = (HttpStatusCode)429,
                        Headers = new Dictionary<string, string> { ["Retry-After"] = "301" }
                    });
                }, 3, CancellationToken.None, TimeSpan.FromSeconds(90), timings), CancellationToken.None));
            Assert.Equal(1, attempts);
        }

        [Theory]
        [InlineData("45", 45, false, 1)]
        [InlineData("invalid", 30, true, 7)]
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
                Assert.Equal(persistent ? 7 : limitedAttempts + 1, sportAttempts.Count);
                Assert.Equal(TimeSpan.FromSeconds(seconds), sportAttempts[1] - sportAttempts[0]);
                if (limitedAttempts > 1)
                    Assert.Equal(TimeSpan.FromSeconds(60), sportAttempts[2] - sportAttempts[1]);
                var status = refresh.Status();
                Assert.Equal(sportAttempts.Count + 1, status.RequestsStarted);
                Assert.Equal(Math.Min(limitedAttempts, 6), status.RateLimitRetries);
                Assert.Equal(0, status.ActiveRequests);
                Assert.True(status.CatalogPagesCached > 0);
                Assert.Equal(TuneInCatalogueSession.MaxRefreshPages, status.CatalogPagesLimit);
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
                if (Interlocked.Increment(ref fetches) > 1)
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(token);
                }
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
                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.True(refresh.Status().HasCompleteBaseline);
                refresh.CheckSchedules();
                now = now.AddHours(6).AddSeconds(-1);
                refresh.CheckSchedules();
                Assert.Equal("Completed", refresh.Status().State);
                now = now.AddSeconds(1);
                refresh.CheckSchedules();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    refresh.CheckSchedules();
                    Assert.Throws<InvalidOperationException>(() => refresh.Start("talk"));
                    Assert.Equal(2, fetches);
                }
                finally { release.TrySetResult(true); }
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                refresh.CheckSchedules();
                Assert.False(refresh.Status().Busy);
                Assert.Equal(2, fetches);
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
                Assert.False(status.HasCompleteBaseline);
                Assert.Contains("Refresh all enabled categories", status.Phase);
                Assert.Empty(requests);
                Assert.Empty(imports);
                Assert.Null(status.DirtyCategory);

                refresh.Start();
                await WaitUntilIdle(refresh);
                Assert.Equal("Completed", refresh.Status().State);
                Assert.True(refresh.Status().HasCompleteBaseline);
                Assert.True(new TuneInRefreshStore(path).Read().CompleteBaseline);
                var store = new TuneInRefreshStore(path);
                var legacy = store.Read();
                legacy.CompleteBaseline = null;
                store.Write(legacy);
                using (var restarted = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, __, ___, ____) => Task.CompletedTask, null))
                {
                    Assert.True(restarted.Status().HasCompleteBaseline);
                    Assert.True(restarted.ChannelRegistered());
                }
                using (var unregistered = new TuneInRefresh(directory, store, () => null,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, __, ___, ____) => Task.CompletedTask, null))
                    Assert.False(unregistered.ChannelRegistered());
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
                Assert.Contains(Assert.Single(baseline["My Favorites"]), imports[1]["My Favorites"]);
                Assert.Equal(2, imports[1]["My Favorites"].Length);
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ManualRetryReusesFailedImportOnlyWhileDiscoveryIsFresh(bool expired)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, AllowPartialCatalogImport = true
            };
            var now = DateTime.UtcNow;
            var fetches = 0;
            var imports = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                fetches++;
                return Task.FromResult(XDocument.Parse("<opml><body>"
                    + "<outline type='audio' guide_id='s1' text='Radio'/>"
                    + "<outline type='link' URL='https://opml.radiotime.com/Browse.ashx?id=c1#bad'/>"
                    + "</body></opml>"));
            });
            var store = new TuneInRefreshStore(Path.Combine(Path.GetTempPath(), $"tunein-manual-retry-{Guid.NewGuid():N}.xml"));
            var channel = new TuneInChannel(directory);
            try
            {
                using var refresh = new TuneInRefresh(directory, store, () => channel,
                    (c, folder, session, strict, token) => c.GetRefreshChannelItems(folder, session, strict, token),
                    (_, staged, __, token) =>
                    {
                        if (++imports == 1) staged.Read(null, token);
                        else Assert.Single(ReadStagedTree(staged, token)["Talk"]);
                        return Task.CompletedTask;
                    }, null, clock: () => now);
                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Error", refresh.Status().State);
                Assert.Contains("Folders served: 1/2", refresh.GetFailureLog().Text);
                Assert.Equal("talk", store.Read().DirtyCategory);
                if (expired) now = now.AddHours(48);

                refresh.Start("talk");
                await WaitUntilIdle(refresh);
                Assert.Equal("Partial", refresh.Status().State);
                Assert.Equal(1, refresh.Status().SkippedBrowseLinks);
                Assert.Equal(expired ? 2 : 1, fetches);
                Assert.Equal(2, imports);
                Assert.True(store.Read().Categories.Single().DiscoveryPartial);
                Assert.Null(store.Read().DirtyCategory);
            }
            finally
            {
                store.Clear();
                store.ClearDiscovery();
            }
        }

        private static Dictionary<string, string[]> ReadStagedTree(TuneInStagedImport staged, CancellationToken token,
            int maxRefreshLevel = int.MaxValue, bool names = false)
        {
            string[] ReadDescendants(string id, int level) => level > maxRefreshLevel ? Array.Empty<string>()
                : staged.Read(id, token).Items.SelectMany(item => item.Type == ChannelItemType.Folder
                    ? ReadDescendants(item.Id, level + 1) : new[] { names ? item.Name : item.Id }).ToArray();
            return staged.Read(null, token).Items.ToDictionary(root => root.Name, root => ReadDescendants(root.Id, 2));
        }
    }
}
