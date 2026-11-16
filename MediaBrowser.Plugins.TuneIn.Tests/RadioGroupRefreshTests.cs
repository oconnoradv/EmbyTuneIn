using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Plugins.TuneIn.Configuration;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;
using System.Reflection;
using System.Collections.Generic;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class RadioGroupRefreshTests
    {
        private static PluginConfiguration Ready() => new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true };
        private static XDocument Opml(string rows = "") => XDocument.Parse("<opml><head><status>200</status></head><body>" + rows + "</body></opml>");

        private static async Task Synchronize(TuneInChannel channel, TuneInDirectory directory,
            string category, string folder, int depth, CancellationToken token)
        {
            var root = await channel.GetChannelItems(new InternalChannelItemQuery(), token);
            Assert.Contains(root.Items, item => item.Id == folder);
            Assert.Equal(8, depth);
            var children = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
            if (category == "music" || category == "location" || category == "language")
                foreach (var child in children.Items)
                    await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = child.Id }, token);
            Assert.Equal(category, directory.Resolve(directory.Snapshot(), folder).Category);
        }

        [Fact]
        public void NativeRefreshDiagnosticsAreAdministratorOnlyAndReadOnly()
        {
            foreach (var type in new[] { typeof(GetTuneInChannelRefresh), typeof(GetTuneInChannelRefreshFailure) })
            {
                var auth = (AuthenticatedAttribute)Attribute.GetCustomAttribute(type, typeof(AuthenticatedAttribute));
                Assert.Equal("Admin", auth.Roles);
                Assert.False(auth.AllowLocal);
                var route = (MediaBrowser.Model.Services.RouteAttribute)Attribute.GetCustomAttribute(type, typeof(MediaBrowser.Model.Services.RouteAttribute));
                Assert.Equal("GET", route.Verbs);
            }
        }

        [Fact]
        public async Task HistoryTracksLastSuccessSeparatelyFromLaterFailedAttemptsWithoutInvalidatingNavigation()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml()));
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var history = new TuneInRefreshStateStore();
            var refresh = new TuneInRefresh(directory, history);
            var originalKey = directory.Snapshot().Key;
            Assert.Null(refresh.Status().Single(item => item.Category == "talk").LastSuccessfulUtc);
            refresh.Start("talk", (folder, depth, token) => Synchronize(channel, directory, "talk", folder, depth, token));
            await refresh.Completion("talk");
            var success = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal(success.FinishedUtc, success.LastSuccessfulUtc);
            refresh.Start("talk", (_, __, ___) => throw new IOException("Native refresh failed"));
            await refresh.Completion("talk");
            var failed = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal("Failed", failed.LastAttemptState);
            Assert.Equal(success.LastSuccessfulUtc, failed.LastSuccessfulUtc);
            Assert.Equal(failed.FinishedUtc, failed.LastAttemptUtc);
            var restarted = new TuneInRefresh(directory, history);
            Assert.Equal(failed.LastAttemptUtc, restarted.Status().Single(item => item.Category == "talk").LastAttemptUtc);
            Assert.Contains("Failed", restarted.HistorySummary("talk"));
            Assert.Equal(originalKey, directory.Snapshot().Key);
        }

        [Fact]
        public void StatusAndRejectedStartsMakeNoApiRequests()
        {
            var config = new PluginConfiguration { EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("No API request allowed"));
            var refresh = new TuneInRefresh(directory);
            Assert.All(refresh.Status(), status => { Assert.False(status.Enabled); Assert.Equal("Idle", status.State); });
            Assert.Throws<InvalidOperationException>(() => refresh.Start("talk", (_, __, ___) => Task.CompletedTask));
            Assert.Throws<ArgumentException>(() => refresh.Start("https://127.0.0.1", (_, __, ___) => Task.CompletedTask));
            config.ConfigurationCompleted = true;
            config.SchemaVersion = 1;
            config.EnableMusic = true;
            Assert.Throws<InvalidOperationException>(() => refresh.Start("music", (_, __, ___) => Task.CompletedTask));
        }

        [Fact]
        public async Task LocalRefreshUsesOnlyEnabledAreasAndCoordinatesAndReusesPagesForNativeSynchronization()
        {
            var config = Ready();
            config.EnableLocalRadio = true;
            config.EnableMusic = true;
            config.UseCoordinates = true;
            config.LatLon = "1.5,-2.75";
            config.Username = "private-account";
            config.LocalCities.Add(new CatalogSelection { Id = "r1" });
            config.LocalCities.Add(new CatalogSelection { Id = "r2", Enabled = false });
            config.Music.Add(new CatalogSelection { Id = "g1" });
            var calls = new ConcurrentQueue<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                calls.Enqueue(request);
                return Task.FromResult(Opml("<outline type='audio' guide_id='s1' text='Radio'/>"));
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);
            var music = TuneInChannel.StartingEntries(directory.Snapshot(), "music").Single();
            await directory.FetchCategoryAsync(directory.Snapshot(), "music", TuneInChannel.CatalogueRequest(directory.Snapshot(), music), CancellationToken.None);
            refresh.Start("local", (folder, depth, token) => Synchronize(channel, directory, "local", folder, depth, token));
            await refresh.Completion("local");
            var status = refresh.Status().Single(item => item.Category == "local");
            Assert.Equal("Completed", status.State);
            Assert.Equal(2, status.CompletedRequests);
            Assert.Equal(100, status.ApiPercent);
            Assert.Equal(2, status.StationRows);
            Assert.Null(status.LastHttpStatus); // An injected document is not evidence of HTTP 200.
            Assert.Equal(3, calls.Count);
            Assert.DoesNotContain(calls, request => request.Parameters.Values.Contains("r2"));
            Assert.All(calls, request => Assert.False(request.Parameters.ContainsKey("username")));
            Assert.Single(calls, request => request.Parameters.ContainsKey("latlon"));
            await directory.FetchCategoryAsync(directory.Snapshot(), "music", TuneInChannel.CatalogueRequest(directory.Snapshot(), music), CancellationToken.None);
            Assert.Equal(3, calls.Count);
        }

        [Fact]
        public async Task LanguageRefreshCountsValidationAndPreservesEachSelectedLanguage()
        {
            var config = Ready();
            config.EnableLanguage = true;
            config.Languages.AddRange(new[] { new CatalogSelection { Id = "l1" }, new CatalogSelection { Id = "l2" } });
            var calls = new ConcurrentQueue<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                calls.Enqueue(request);
                return Task.FromResult(request.Parameters.ContainsKey("filter") ? Opml() : Opml(
                    "<outline type='link' text='One' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l1'/>"
                    + "<outline type='link' text='Two' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l2'/>"));
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);
            refresh.Start("language", (folder, depth, token) => Synchronize(channel, directory, "language", folder, depth, token));
            await refresh.Completion("language");
            Assert.Equal(3, calls.Count);
            Assert.Equal(new[] { "l1", "l2" }, calls.Where(request => request.Parameters.ContainsKey("filter"))
                .Select(request => request.Parameters["filter"]).ToArray());
            var status = refresh.Status().Single(item => item.Category == "language");
            Assert.Equal("Completed", status.State);
            Assert.Equal(3, status.TotalRequests);
            Assert.Equal(3, status.CompletedRequests);
        }

        [Fact]
        public async Task ProgressDuplicateStartCancellationAndConfigurationInvalidationAreObservable()
        {
            var config = Ready();
            config.EnableTalk = true;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Opml();
            });
            var refresh = new TuneInRefresh(directory);
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var first = refresh.Start("talk", (folder, depth, token) => Synchronize(channel, directory, "talk", folder, depth, token));
            await started.Task;
            var running = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal("Running", running.State);
            Assert.Equal(1, running.TotalRequests);
            Assert.Equal(0, running.CompletedRequests);
            Assert.Equal(first.Id, refresh.Start("talk", (_, __, ___) => Task.CompletedTask).Id);
            Assert.Throws<ArgumentException>(() => refresh.Cancel("talk", "stale"));
            refresh.Cancel("talk", first.Id);
            await refresh.Completion("talk");
            Assert.Equal("Cancelled by administrator.", refresh.Status().Single(item => item.Category == "talk").Error);
            var second = refresh.Start("talk", (folder, depth, token) => Synchronize(channel, directory, "talk", folder, depth, token));
            Assert.NotEqual(first.Id, second.Id);
            directory.Reset();
            await refresh.Completion("talk");
            Assert.Equal("Cancelled", refresh.Status().Single(item => item.Category == "talk").State);
        }

        [Theory]
        [InlineData(200, false, "Completed", 1)]
        [InlineData(200, true, "Failed", 0)]
        [InlineData(429, false, "Failed", 0)]
        public async Task ActualHttpResponsesAndBodyFaultsDetermineStatus(int code, bool fault, string state, int completed)
        {
            var config = Ready();
            config.EnableTalk = true;
            var body = fault ? "<opml><head><status>200</status><fault>provider error</fault></head><body/></opml>" : Opml().ToString();
            var response = "HTTP/1.1 " + code + " Response\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(response)));
            var directory = new TuneInDirectory(() => config, transport: transport);
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);
            refresh.Start("talk", (folder, depth, token) => Synchronize(channel, directory, "talk", folder, depth, token));
            await refresh.Completion("talk");
            var status = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal(state, status.State);
            Assert.Equal(code, status.LastHttpStatus);
            Assert.Equal(completed, status.CompletedRequests);
            if (state == "Failed") Assert.NotNull(status.Error);
        }

        [Fact]
        public async Task SynchronizationCannotEscapeCategoryEvenIfHostSwallowsFailure()
        {
            var config = Ready();
            config.EnableTalk = true;
            config.EnableSports = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(Opml("<outline type='link' text='Next' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=25'/>"));
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);
            refresh.Start("talk", async (folder, depth, token) =>
            {
                var children = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
                var sports = directory.Register(directory.Snapshot(), TuneInChannel.CategoryEntry("sports"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => channel.GetChannelItems(
                    new InternalChannelItemQuery { FolderId = sports }, token));
                await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = children.Items.Single().Id }, token);
            });
            await refresh.Completion("talk");
            Assert.Equal("Failed", refresh.Status().Single(item => item.Category == "talk").State);
            Assert.Equal(2, calls);
            Assert.Contains("did not complete", refresh.Status().Single(item => item.Category == "talk").Error);
        }

        [Fact]
        public async Task NativeNoOpIsNotReportedAsSynchronizationSuccess()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml()));
            var refresh = new TuneInRefresh(directory);
            refresh.Start("talk", (_, __, ___) => Task.CompletedTask);
            await refresh.Completion("talk");
            var status = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal(0, status.ApiPercent);
            Assert.Equal("Failed", status.State);
            Assert.NotNull(status.Error);
        }

        [Fact]
        public async Task NestedStationsPaginationAndRepeatedPagesArePopulatedWithinOneGroup()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.EnableSports = true;
            config.Music.Add(new CatalogSelection { Id = "g1" });
            var calls = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                calls.Add(request);
                if (request.Parameters["id"] == "g1")
                    return Task.FromResult(Opml("<outline type='link' text='Stations' URL='https://opml.radiotime.com/Browse.ashx?id=g2'/>"
                        + "<outline type='link' text='All music' URL='https://opml.radiotime.com/Browse.ashx?c=music'/>"));
                if (!request.Parameters.ContainsKey("offset"))
                    return Task.FromResult(Opml("<outline type='audio' guide_id='s1' text='One'/>"
                        + "<outline key='next' type='link' text='Next' URL='https://opml.radiotime.com/Browse.ashx?id=g2&amp;offset=25'/>"));
                return Task.FromResult(Opml("<outline type='audio' guide_id='s2' text='Two'/>"));
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);
            var stations = new List<string>();
            refresh.Start("music", async (folder, depth, token) =>
            {
                Assert.Equal(8, depth);
                var queue = new Queue<(string Id, int Level)>();
                queue.Enqueue((folder, 2));
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var result = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = current.Id }, token);
                    foreach (var item in result.Items)
                    {
                        if (item.Type == ChannelItemType.Media) stations.Add(item.Name);
                        else if (current.Level < depth) queue.Enqueue((item.Id, current.Level + 1));
                    }
                }
                var starting = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
                await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = starting.Items[0].Id }, token);
            });
            await refresh.Completion("music");
            Assert.Equal(new[] { "One", "Two" }, stations);
            Assert.Equal(3, calls.Count);
            Assert.All(calls, request => Assert.False(request.Parameters.ContainsKey("c")));
            var status = refresh.Status().Single(item => item.Category == "music");
            Assert.Equal("Completed", status.State);
            Assert.True(status.DiscoveryComplete);
            Assert.Equal(3, status.TotalRequests);
            Assert.Equal(2, status.StationRows);
        }

        [Fact]
        public async Task RequestLimitIsAnIncompleteFailureEvenIfNativeHostSwallowsIt()
        {
            var config = Ready();
            config.EnableTalk = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) => { calls++; return Task.FromResult(Opml()); });
            var snapshot = directory.Snapshot();
            var first = TuneInChannel.CatalogueRequest(snapshot, TuneInChannel.StartingEntries(snapshot, "talk").Single());
            await Assert.ThrowsAsync<InvalidOperationException>(() => directory.SynchronizeCategoryAsync("talk", 1, async () =>
            {
                for (var i = 0; i < 1001; i++)
                {
                    var request = first.Copy();
                    request.Parameters["offset"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    try { await directory.FetchCategoryAsync(snapshot, "talk", request, CancellationToken.None); }
                    catch (InvalidOperationException) { }
                }
            }, _ => { }));
            Assert.Equal(1000, calls);
        }

        [Fact]
        public async Task VisitingDescendantsCannotHideAnUnvisitedSavedSelection()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.AddRange(new[] { new CatalogSelection { Id = "g1" }, new CatalogSelection { Id = "g2" } });
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                calls++;
                return Task.FromResult(request.Parameters["id"] == "g1"
                    ? Opml("<outline type='link' text='Nested' URL='https://opml.radiotime.com/Browse.ashx?id=g3'/>")
                    : Opml("<outline type='audio' guide_id='s1' text='One'/>"));
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);
            refresh.Start("music", async (folder, _, token) =>
            {
                var selections = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
                var children = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = selections.Items[0].Id }, token);
                await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = children.Items[0].Id }, token);
            });
            await refresh.Completion("music");
            Assert.Equal(2, calls);
            var status = refresh.Status().Single(item => item.Category == "music");
            Assert.Equal("Failed", status.State);
            Assert.False(status.DiscoveryComplete);
        }

        [Fact]
        public async Task ScheduledStartSharesActiveButtonJobAndReportsCompletionOnlyAfterPopulation()
        {
            var config = Ready();
            config.EnableTalk = true;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                calls++;
                started.TrySetResult(true);
                await finish.Task.WaitAsync(token);
                return Opml("<outline type='audio' guide_id='s1' text='One'/>");
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);
            var job = refresh.Start("talk", (folder, depth, token) => Synchronize(channel, directory, "talk", folder, depth, token));
            await started.Task;
            var progress = new InlineProgress();
            var execution = refresh.ExecuteScheduledAsync("talk", null, null, CancellationToken.None, progress);
            Assert.Equal(job.Id, refresh.Status().Single(item => item.Category == "talk").Id);
            Assert.Equal(new[] { 0d }, progress.Values);
            finish.SetResult(true);
            await execution;
            Assert.Equal(new[] { 0d, 100d }, progress.Values);
            Assert.Equal(1, calls);
            Assert.True(refresh.Status().Single(item => item.Category == "talk").DiscoveryComplete);
        }

        [Fact]
        public void PluginRegistersOneVisibleRefreshTaskAndNoLegacyScheduleMutationRoutes()
        {
            var taskType = Assert.Single(typeof(Plugin).Assembly.GetTypes(), type =>
                !type.IsAbstract && typeof(IScheduledTask).IsAssignableFrom(type));
            Assert.Equal(typeof(RefreshTuneInTask), taskType);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var task = new RefreshTuneInTask(manager, null, new TuneInRefresh(new TuneInDirectory(() => Ready())));
            Assert.Equal("Refresh TuneIn", task.Name);
            Assert.Equal("Internet Channels", task.Category);
            Assert.Contains("only enabled TuneIn categories", task.Description);
            Assert.Contains("saved sub-category selections", task.Description);
            Assert.Empty(task.GetDefaultTriggers());
            Assert.False(task.IsHidden);
            Assert.True(task.IsEnabled);
            Assert.DoesNotContain(typeof(Plugin).Assembly.GetTypes().SelectMany(type =>
                type.GetCustomAttributes<MediaBrowser.Model.Services.RouteAttribute>()),
                route => route.Path.StartsWith("/TuneInFork/Schedule", StringComparison.Ordinal)
                    || route.Path.StartsWith("/TuneInFork/Refresh/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RefreshTuneInTaskOnlyTraversesEnabledConfiguredCategories()
        {
            var config = Ready();
            config.EnableTalk = true;
            config.EnableSports = true;
            config.EnableLocalRadio = true;
            config.EnableMusic = true;
            config.Music.Add(new MediaBrowser.Plugins.TuneIn.Configuration.CatalogSelection { Id = "g1" });
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml()));
            var snapshot = directory.Snapshot();
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            foreach (var category in snapshot.Categories)
            {
                var folderId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category));
                libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
                { InternalId = libraryProxy.Roots.Count + 1, ParentId = 100, ExternalId = folderId });
            }
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var managerProxy = (TaskManagerProxy)(object)manager;
            managerProxy.Channel = channel;
            managerProxy.TraverseFolders = true;
            var refresh = new TuneInRefresh(directory);
            var task = new RefreshTuneInTask(manager, null, refresh);
            var progress = new InlineProgress();

            await task.Execute(CancellationToken.None, progress);

            var categories = managerProxy.RefreshedFolders.Select(folder =>
                directory.Resolve(directory.Snapshot(), folder).Category).ToArray();
            Assert.Equal(new[] { "music", "talk", "sports" }, categories);
            Assert.Equal(100d, progress.Values.Last());
            Assert.DoesNotContain("local", categories);
        }

        [Fact]
        public async Task RefreshTraversesProviderAlphabeticFoldersWithoutEscapingSelectedMusic()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.Add(new CatalogSelection { Id = "g1" });
            var filters = new List<string>();
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                Assert.Equal("g1", request.Parameters["id"]);
                var filter = request.Parameters.GetValueOrDefault("filter");
                filters.Add(filter);
                if (request.Parameters.ContainsKey("pivot"))
                    return Task.FromResult(Opml("<outline type='link' text='#' URL='http://opml.radiotime.com/Browse.ashx?id=g1&amp;filter=s:~%23'/>"
                        + "<outline type='link' text='E' URL='http://opml.radiotime.com/Browse.ashx?id=g1&amp;filter=s:~%C3%89'/>"));
                if (filter != null)
                    return Task.FromResult(Opml("<outline type='audio' text='Station' guide_id='s1'/>"));
                return Task.FromResult(Opml("<outline type='link' text='Find by Name' key='pivotName' URL='http://opml.radiotime.com/Browse.ashx?pivot=name&amp;id=g1&amp;filter=s'/>"));
            });
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
            {
                InternalId = 1,
                ParentId = 100,
                ExternalId = directory.Register(directory.Snapshot(), TuneInChannel.CategoryEntry("music"))
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.TraverseFolders = true;
            var refresh = new TuneInRefresh(directory);

            await refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());

            Assert.Contains("s:~#", filters);
            Assert.Contains("s:~É", filters);
            Assert.Equal("Completed", refresh.Status().Single(item => item.Category == "music").State);
        }

        [Fact]
        public async Task RefreshTuneInTaskWithNoEnabledCategoriesCompletesWithoutChannelCalls()
        {
            var directory = new TuneInDirectory(() => new PluginConfiguration());
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var task = new RefreshTuneInTask(manager, null, new TuneInRefresh(directory));
            var progress = new InlineProgress();

            await task.Execute(CancellationToken.None, progress);

            Assert.Equal(new[] { 100d }, progress.Values);
        }

        [Fact]
        public async Task SettingsStartUsesNativeExecutionAndBothEntryPointsUpdateSharedHistory()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml()));
            var snapshot = directory.Snapshot();
            var folderId = directory.Register(snapshot, TuneInChannel.CategoryEntry("talk"));
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, LibraryArtworkTests.LibraryProxy>();
            ((LibraryArtworkTests.LibraryProxy)library).Root = new MediaBrowser.Controller.Entities.Folder { InternalId = 1, ExternalId = folderId };
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            ((TaskManagerProxy)(object)manager).Channel = channel;
            var refresh = new TuneInRefresh(directory);
            var worker = DispatchProxy.Create<IScheduledTaskWorker, ScheduleTests.WorkerProxy>();
            var workerProxy = (ScheduleTests.WorkerProxy)worker;
            workerProxy.Task = new LegacyTalkTask();
            var tasks = DispatchProxy.Create<ITaskManager, NativeManagerProxy>();
            var native = (NativeManagerProxy)tasks;
            native.Workers = new[] { worker };
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            native.ExecuteTask = async () =>
            {
                await release.Task;
                await refresh.ExecuteScheduledAsync("talk", manager, null, CancellationToken.None, new InlineProgress());
                workerProxy.LastResult = new TaskResult { Status = TaskCompletionStatus.Completed, EndTimeUtc = DateTimeOffset.UtcNow };
            };
            var first = refresh.StartNative("talk", tasks, manager, null);
            Assert.Equal("Queued", first.State);
            Assert.Null(worker.LastExecutionResult);
            Assert.Equal(0, first.TotalRequests);
            Assert.Equal(first.Id, refresh.StartNative("talk", tasks, manager, null).Id);
            Assert.Equal(1, native.Executions);
            release.SetResult(true);
            await native.Execution;
            var completed = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal("Completed", completed.State);
            Assert.NotNull(completed.LastSuccessfulUtc);
            Assert.Equal(TaskCompletionStatus.Completed, worker.LastExecutionResult.Status);
            await tasks.Execute(worker, new TaskOptions());
            var scheduled = refresh.Status().Single(item => item.Category == "talk");
            Assert.NotEqual(first.Id, scheduled.Id);
            Assert.True(scheduled.LastSuccessfulUtc >= completed.LastSuccessfulUtc);
            Assert.Contains("Last successful group refresh", refresh.HistorySummary("talk"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task NativeLaunchFailureOrNoOpCannotStartProviderRequestsOrReportSuccess(bool fail)
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("No catalogue request allowed."));
            var refresh = new TuneInRefresh(directory);
            var worker = DispatchProxy.Create<IScheduledTaskWorker, ScheduleTests.WorkerProxy>();
            ((ScheduleTests.WorkerProxy)worker).Task = new LegacyTalkTask();
            var tasks = DispatchProxy.Create<ITaskManager, NativeManagerProxy>();
            var native = (NativeManagerProxy)tasks;
            native.Workers = new[] { worker };
            native.ExecuteTask = () => fail ? Task.FromException(new IOException("Native execution unavailable")) : Task.CompletedTask;
            refresh.StartNative("talk", tasks, null, null);
            await refresh.Completion("talk");
            var status = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal("Failed", status.State);
            Assert.Equal(0, status.TotalRequests);
            Assert.Null(status.LastSuccessfulUtc);
        }

        [Fact]
        public async Task CancellingBeforeNativeStartCannotRelaunchTheCancelledJob()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Cancelled job must not query TuneIn."));
            var refresh = new TuneInRefresh(directory);
            var worker = DispatchProxy.Create<IScheduledTaskWorker, ScheduleTests.WorkerProxy>();
            ((ScheduleTests.WorkerProxy)worker).Task = new LegacyTalkTask();
            var tasks = DispatchProxy.Create<ITaskManager, NativeManagerProxy>();
            var native = (NativeManagerProxy)tasks;
            native.Workers = new[] { worker };
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            native.ExecuteTask = async () =>
            {
                await release.Task;
                await refresh.ExecuteScheduledAsync("talk", null, null, CancellationToken.None, new InlineProgress());
            };
            var job = refresh.StartNative("talk", tasks, null, null);
            refresh.Cancel("talk", job.Id);
            await refresh.Completion("talk");
            release.SetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.Execution);
            var status = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal(job.Id, status.Id);
            Assert.Equal("Cancelled", status.State);
            Assert.Equal(0, status.TotalRequests);
        }

        private sealed class LegacyTalkTask : IScheduledTask
        {
            public string Key => "TuneInForkRefresh_talk";
            public string Name => Key;
            public string Description => Key;
            public string Category => "Internet Channels";
            public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
            public Task Execute(CancellationToken token, IProgress<double> progress) => Task.CompletedTask;
        }

        public class NativeManagerProxy : DispatchProxy
        {
            public IScheduledTaskWorker[] Workers { get; set; }
            public Func<Task> ExecuteTask { get; set; }
            public int Executions { get; set; }
            public Task Execution { get; private set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_ScheduledTasks") return Workers;
                if (method.Name == "Execute")
                {
                    Assert.Same(Workers.Single(), args[0]);
                    Executions++;
                    return Execution = ExecuteTask();
                }
                throw new InvalidOperationException(method.Name);
            }
        }

        [Fact]
        public async Task ScheduledExecutionPropagatesNativeFailureAndCancellationToHost()
        {
            var config = Ready();
            config.EnableTalk = true;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Opml();
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            ((TaskManagerProxy)(object)manager).Channel = channel;
            ((TaskManagerProxy)(object)manager).SwallowCancellation = true;
            var refresh = new TuneInRefresh(directory);
            using (var cancellation = new CancellationTokenSource())
            {
                var execution = refresh.ExecuteScheduledAsync("talk", manager, null, cancellation.Token, new InlineProgress());
                await started.Task;
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            }
            ((TaskManagerProxy)(object)manager).Channel = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                refresh.ExecuteScheduledAsync("talk", manager, null, CancellationToken.None, new InlineProgress()));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ScheduledFailureRetainsCategoryContextAndOriginalCauseEvenWhenHostSwallowsIt(bool swallowFailure)
        {
            var config = Ready();
            config.EnableTalk = true;
            var cause = new IOException("Synthetic catalogue connection failure");
            var directory = new TuneInDirectory(() => config, (_, __) => throw cause);
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
            {
                InternalId = 1,
                ParentId = 100,
                ExternalId = directory.Register(directory.Snapshot(), TuneInChannel.CategoryEntry("talk"))
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.SwallowFailure = swallowFailure;
            var refresh = new TuneInRefresh(directory);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress()));

            Assert.Contains("category 'talk'", failure.Message);
            Assert.Contains("Catalogue requests completed/started: 0/1", failure.Message);
            Assert.Contains("Last HTTP status: not recorded", failure.Message);
            Assert.Same(cause, failure.GetBaseException());
            Assert.Contains(nameof(IOException), failure.ToString());
            Assert.DoesNotContain(cause.Message, refresh.Status().Single(item => item.Category == "talk").Error);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NativeTaskMessageContainsSafeRejectedParameterDetailsWhenHostDropsInnerException(bool swallowFailure)
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(
                "<outline type='link' text='Bad' URL='http://opml.radiotime.com/Browse.ashx?c=talk&amp;filter=private%2Fvalue'/>")));
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
            {
                InternalId = 1,
                ParentId = 100,
                ExternalId = directory.Register(directory.Snapshot(), TuneInChannel.CategoryEntry("talk"))
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.SwallowFailure = swallowFailure;
            var refresh = new TuneInRefresh(directory);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress()));

            Assert.Contains("Invalid catalogue link parameter 'filter' (contains unsupported punctuation; length 13; occurrences 1).", failure.Message);
            Assert.DoesNotContain("private", failure.Message);
            Assert.DoesNotContain("radiotime.com", failure.Message);
        }

        public class TaskManagerProxy : DispatchProxy
        {
            public TuneInChannel Channel { get; set; }
            public bool SwallowCancellation { get; set; }
            public bool SwallowFailure { get; set; }
            public bool TraverseFolders { get; set; }
            public List<string> RefreshedFolders { get; } = new List<string>();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetLogger") return null;
                if (method.Name == "GetChannel") return Channel;
                if (method.Name == "RefreshChannelContent")
                {
                    RefreshedFolders.Add((string)args[2]);
                    return Refresh((string)args[2], (CancellationToken)args[3]);
                }
                throw new InvalidOperationException("Unexpected native task call.");
            }
            private async Task Refresh(string folder, CancellationToken token)
            {
                try
                {
                    var pending = new Queue<(string Folder, int Depth)>();
                    pending.Enqueue((folder, 0));
                    while (pending.Count != 0)
                    {
                        var current = pending.Dequeue();
                        var result = await Channel.GetChannelItems(
                            new InternalChannelItemQuery { FolderId = current.Folder }, token);
                        if (TraverseFolders && current.Depth < 8)
                            foreach (var item in result.Items.Where(item => item.Type == ChannelItemType.Folder))
                                pending.Enqueue((item.Id, current.Depth + 1));
                    }
                }
                catch (OperationCanceledException) when (SwallowCancellation) { }
                catch (IOException) when (SwallowFailure) { }
                catch (InvalidDataException) when (SwallowFailure) { }
            }
        }

        public class RefreshTaskLibraryProxy : DispatchProxy
        {
            public List<MediaBrowser.Controller.Entities.Folder> Roots { get; } =
                new List<MediaBrowser.Controller.Entities.Folder>();
            public MediaBrowser.Controller.Channels.Channel Parent { get; set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetItemList")
                {
                    var query = (MediaBrowser.Controller.Entities.InternalItemsQuery)args[0];
                    if (query.ExternalId != null)
                        return Roots.Where(item => item.ExternalId == query.ExternalId).ToArray();
                    return Array.Empty<MediaBrowser.Controller.Entities.BaseItem>();
                }
                if (method.Name == "GetItemById") return Parent;
                if (method.Name == "UpdateItem") return null;
                throw new InvalidOperationException("Unexpected refresh task library call: " + method.Name);
            }
        }
        private sealed class InlineProgress : IProgress<double>
        {
            public List<double> Values { get; } = new List<double>();
            public void Report(double value) => Values.Add(value);
        }

        private sealed class DuplexStream : Stream
        {
            private readonly MemoryStream input;
            public DuplexStream(string response) { input = new MemoryStream(Encoding.UTF8.GetBytes(response)); }
            public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => input.ReadAsync(buffer, offset, count, token);
            public override void Write(byte[] buffer, int offset, int count) { }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.CompletedTask;
            protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
