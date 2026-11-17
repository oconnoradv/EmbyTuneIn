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
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class RadioGroupRefreshTests
    {
        private static PluginConfiguration Ready() => new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true };
        private static XDocument Opml(string rows = "") => XDocument.Parse("<opml><head><status>200</status></head><body>" + rows + "</body></opml>");

        internal static async Task Synchronize(TuneInChannel channel, TuneInDirectory directory,
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

        [Fact]
        public async Task TimeoutCanResumeOnlyFromSameConfigurationAndErrorsRemainNonResumable()
        {
            var config = Ready();
            config.EnableTalk = true;
            var requests = 0;
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                if (Interlocked.Increment(ref requests) == 1) await Task.Delay(Timeout.Infinite, token);
                return Opml();
            });
            var historyPath = Path.Combine(Environment.CurrentDirectory, "tunein-timeout-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var refresh = new TuneInRefresh(directory, new TuneInRefreshStateStore(historyPath),
                    TimeSpan.FromMilliseconds(30));
                var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
                var synchronize = (string folder, int depth, CancellationToken token) =>
                    Synchronize(channel, directory, "talk", folder, depth, token);
                var timedOut = refresh.Start("talk", synchronize);
                await refresh.Completion("talk");

                config.ApiRequestIntervalSeconds++;
                Assert.False(refresh.CategoryStatus("talk").Resumable);
                config.ApiRequestIntervalSeconds--;

                var restarted = new TuneInRefresh(directory, new TuneInRefreshStateStore(historyPath),
                    TimeSpan.FromMilliseconds(30));
                var timeoutStatus = restarted.CategoryStatus("talk");
                Assert.Equal("TimedOut", timeoutStatus.Outcome);
                Assert.True(timeoutStatus.Resumable);

                var resumed = restarted.ResumeTimedOutCategory("talk", timedOut.Id, synchronize, null);
                Assert.Equal(timedOut.Id, resumed.ResumedFromId);
                await restarted.Completion("talk");
                Assert.Equal("Completed", restarted.CategoryStatus("talk").State);
            }
            finally
            {
                if (File.Exists(historyPath)) File.Delete(historyPath);
                if (File.Exists(historyPath + ".tmp")) File.Delete(historyPath + ".tmp");
            }

            var failedDirectory = new TuneInDirectory(() => config, (_, __) => throw new IOException("failed"));
            var failedRefresh = new TuneInRefresh(failedDirectory, timeoutOverride: TimeSpan.FromSeconds(5));
            var failedChannel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, failedDirectory);
            var failed = failedRefresh.Start("talk", (folder, depth, token) =>
                Synchronize(failedChannel, failedDirectory, "talk", folder, depth, token));
            await failedRefresh.Completion("talk");
            Assert.Equal("Error", failedRefresh.CategoryStatus("talk").Outcome);
            Assert.False(failedRefresh.CategoryStatus("talk").Resumable);
            Assert.Throws<InvalidOperationException>(() => failedRefresh.ResumeTimedOutCategory(
                "talk", failed.Id, (_, __, ___) => Task.CompletedTask, null));
        }

        [Fact]
        public async Task TimedOutCategoryReusesValidatedPagesAcrossMultipleWindowsAndRestartsImportFromRoot()
        {
            var config = Ready();
            config.EnableTalk = true;
            var successfulRequests = 0;
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                await Task.Delay(300, token);
                Interlocked.Increment(ref successfulRequests);
                request.Parameters.TryGetValue("offset", out var offset);
                if (offset == null)
                    return Opml("<outline type='link' text='Page 2' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=25'/>");
                if (offset == "25")
                    return Opml("<outline type='link' text='Page 3' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=50'/>");
                return Opml("<outline type='audio' guide_id='s1' text='Station'/>");
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            Func<string, int, CancellationToken, Task> synchronize = async (folder, depth, token) =>
            {
                var root = await channel.GetChannelItems(new InternalChannelItemQuery(), token);
                Assert.Contains(root.Items, item => item.Id == folder);
                var pending = new Queue<string>();
                pending.Enqueue(folder);
                while (pending.Count != 0)
                {
                    var result = await channel.GetChannelItems(new InternalChannelItemQuery
                    { FolderId = pending.Dequeue() }, token);
                    foreach (var child in result.Items.Where(item => item.Type == ChannelItemType.Folder))
                        pending.Enqueue(child.Id);
                }
            };
            var statePath = Path.Combine(Environment.CurrentDirectory, "tunein-resume-pages-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var refresh = new TuneInRefresh(directory, new TuneInRefreshStateStore(statePath),
                    TimeSpan.FromMilliseconds(350));
                refresh.Start("talk", synchronize);
                var timedOutWindows = 0;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    await refresh.Completion("talk");
                    var status = refresh.CategoryStatus("talk");
                    if (status.State == "Completed") break;
                    Assert.Equal("TimedOut", status.State);
                    Assert.True(status.Resumable);
                    timedOutWindows++;
                    refresh = new TuneInRefresh(directory, new TuneInRefreshStateStore(statePath),
                        TimeSpan.FromMilliseconds(350));
                    refresh.ResumeTimedOutCategory("talk", status.Id, synchronize, null);
                }

                Assert.Equal("Completed", refresh.CategoryStatus("talk").State);
                Assert.Equal(2, timedOutWindows);
                Assert.Equal(3, successfulRequests);
            }
            finally
            {
                if (File.Exists(statePath)) File.Delete(statePath);
                if (File.Exists(statePath + ".tmp")) File.Delete(statePath + ".tmp");
            }
        }

        [Fact]
        public async Task TimedOutFullRefreshResumesRemainingCategoriesWithoutReimportingCompletedOnes()
        {
            var config = Ready();
            config.EnableTalk = true;
            config.EnableSports = true;
            var successfulRequests = 0;
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                await Task.Delay(10, token);
                Interlocked.Increment(ref successfulRequests);
                request.Parameters.TryGetValue("c", out var category);
                request.Parameters.TryGetValue("offset", out var offset);
                if (category == "talk" && offset == null)
                    return Opml("<outline type='link' text='Talk 2' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=25'/>");
                if (category == "talk" && offset == "25")
                    return Opml("<outline type='link' text='Talk 3' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=50'/>");
                if (category == "talk" && offset == "50")
                    return Opml("<outline type='link' text='Talk 4' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=75'/>");
                if (category == "talk" && offset == "75")
                    return Opml("<outline type='audio' guide_id='s1' text='Talk station'/>");
                return Opml("<outline type='audio' guide_id='p1' text='Sports station'/>");
            });
            var snapshot = directory.Snapshot();
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            foreach (var category in snapshot.Categories)
                libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
                {
                    InternalId = libraryProxy.Roots.Count + 1,
                    ParentId = 100,
                    ExternalId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category))
                });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var managerProxy = (TaskManagerProxy)(object)manager;
            managerProxy.Channel = channel;
            managerProxy.TraverseFolders = false;
            var importAttempts = 0;
            var capturedPageCount = -1;
            managerProxy.BeforeImport = async () =>
            {
                capturedPageCount = directory.CaptureRefreshPages(out _).Count;
                if (Interlocked.Increment(ref importAttempts) == 1)
                    await Task.Delay(2000);
            };
            var statePath = Path.Combine(Environment.CurrentDirectory, "tunein-full-resume-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var refresh = new TuneInRefresh(directory, new TuneInRefreshStateStore(statePath),
                    TimeSpan.FromSeconds(1));
                await Assert.ThrowsAsync<TimeoutException>(() => refresh.ExecuteConfiguredAsync(
                    manager, null, CancellationToken.None, new InlineProgress()));

                refresh = new TuneInRefresh(directory, new TuneInRefreshStateStore(statePath),
                    TimeSpan.FromSeconds(10));
                var timedOutWindows = 1;
                Assert.Equal(4, capturedPageCount);
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var checkpoint = refresh.LastTimedOutCategory();
                    Assert.NotNull(checkpoint);
                    var persistedCheckpoint = new TuneInRefreshStateStore(statePath).TimeoutCheckpoint;
                    Assert.NotNull(persistedCheckpoint);
                    Assert.True(persistedCheckpoint.PagesComplete,
                        "The category page checkpoint should be complete; saved pages: " + persistedCheckpoint.Pages.Count);
                    refresh.ResumeTimedOutCategory(checkpoint.Category, checkpoint.Id, manager, null);
                    try { await refresh.ConfiguredCompletion; }
                    catch (TimeoutException) { timedOutWindows++; }
                    if (refresh.LastConfiguredOutcome == "Completed") break;
                }

                Assert.Equal(1, timedOutWindows);
                Assert.Equal("Completed", refresh.LastConfiguredOutcome);
                Assert.Equal(new[] { "Completed", "Completed" }, refresh.Status()
                    .Where(status => status.Category == "talk" || status.Category == "sports")
                    .Select(status => status.LastAttemptState).ToArray());
                var refreshedCategories = managerProxy.RefreshedFolders
                    .Select(folder => directory.Resolve(directory.Snapshot(), folder).Category).ToArray();
                Assert.Contains("talk", refreshedCategories);
                Assert.Contains("sports", refreshedCategories);
                var duplicateFolder = Assert.Single(managerProxy.RefreshedFolders
                    .GroupBy(folder => folder, StringComparer.Ordinal), group => group.Count() > 1);
                Assert.Equal(2, duplicateFolder.Count());
                Assert.Equal("talk", directory.Resolve(directory.Snapshot(), duplicateFolder.Key).Category);
                Assert.Equal(5, successfulRequests);
            }
            finally
            {
                if (File.Exists(statePath)) File.Delete(statePath);
                if (File.Exists(statePath + ".tmp")) File.Delete(statePath + ".tmp");
            }
        }

        [Fact]
        public void IncompleteBoundedPageCheckpointIsNotAdvertisedOrResumed()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config);
            var snapshot = directory.Snapshot();
            var history = new TuneInRefreshStateStore();
            history.RecordTimeoutCheckpoint("talk", "attempt", snapshot.SettingsKey, 60, false, DateTime.UtcNow,
                pagesComplete: false);
            var refresh = new TuneInRefresh(directory, history);

            var status = refresh.CategoryStatus("talk");

            Assert.Equal("TimedOut", status.State);
            Assert.False(status.Resumable);
            Assert.Contains("128-page/8 MiB", status.Error);
            Assert.Throws<InvalidOperationException>(() => refresh.ResumeTimedOutCategory(
                "talk", "attempt", (_, __, ___) => Task.CompletedTask, null));
            history.RecordTimeoutCheckpoint("talk", "full-attempt", snapshot.SettingsKey, 60, true, DateTime.UtcNow,
                pagesComplete: false);
            Assert.False(refresh.LastTimedOutCategory().Resumable);
        }

        [Fact]
        public async Task HostCancellationIsReportedAsCancelledNotTimedOutOrError()
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
            var refresh = new TuneInRefresh(directory, timeoutOverride: TimeSpan.FromSeconds(5));
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var managerProxy = (TaskManagerProxy)(object)manager;
            managerProxy.Channel = channel;
            managerProxy.TraverseFolders = true;
            using (var cancellation = new CancellationTokenSource())
            {
                var running = refresh.ExecuteScheduledAsync("talk", manager, null, cancellation.Token,
                    new InlineProgress());
                await started.Task;
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            var status = refresh.CategoryStatus("talk");
            Assert.Equal("Cancelled", status.Outcome);
            Assert.False(status.Resumable);
            Assert.Equal("Refresh was externally cancelled.", status.Error);
        }

        [Fact]
        public async Task ConfigurationUpdateCannotOverlapAnActiveRefreshAndLockDoesNotCancelIt()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config);
            var refresh = new TuneInRefresh(directory, timeoutOverride: TimeSpan.FromSeconds(5));
            var store = new TuneInSettingsLockStore();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = refresh.Start("talk", async (_, __, token) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            });
            await started.Task;

            var configurationKey = directory.Snapshot().Key;
            Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => store.IsLocked, () => { }));
            var locked = refresh.SetSettingsLock(store, true);
            Assert.True(locked.Locked);
            Assert.True(locked.RefreshActive);
            Assert.Equal("Running", refresh.CategoryStatus("talk").State);
            Assert.Equal(configurationKey, directory.Snapshot().Key);
            Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => store.IsLocked, () => { }));

            refresh.Cancel("talk", job.Id);
            await refresh.Completion("talk");
            refresh.SetSettingsLock(store, false);
            var updates = 0;
            refresh.UpdateConfiguration(() => store.IsLocked, () => updates++);
            Assert.Equal(1, updates);
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
            var response = "HTTP/1.1 " + code + " Response\r\n"
                + (code == 429 ? "Retry-After: 60\r\n" : "")
                + "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body;
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
        public async Task RateLimitRetriesAfterTheProviderDelayAndRetainsMinimumSpacing()
        {
            var attempts = 0;
            var times = new List<DateTime>();
            var body = Opml().ToString();
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) =>
                {
                    times.Add(DateTime.UtcNow);
                    var response = ++attempts == 1
                        ? "HTTP/1.1 429 Rate Limited\r\nRetry-After: 0\r\nContent-Length: 0\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body;
                    return Task.FromResult<Stream>(new DuplexStream(response));
                });
            var directory = new TuneInDirectory(() => Ready(), transport: transport);

            await directory.FetchAsync(new DirectoryRequest(), CancellationToken.None);

            Assert.Equal(2, attempts);
            Assert.True((times[1] - times[0]).TotalMilliseconds >= 2800);
        }

        [Fact]
        public async Task LongRetryAfterBlocksLaterCatalogueRequestsWithoutRetryingEarly()
        {
            var attempts = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) =>
                {
                    attempts++;
                    return Task.FromResult<Stream>(new DuplexStream(
                        "HTTP/1.1 429 Rate Limited\r\nRetry-After: 60\r\nContent-Length: 0\r\n\r\n"));
                });
            var directory = new TuneInDirectory(() => Ready(), transport: transport);
            await Assert.ThrowsAsync<CatalogueRateLimitException>(() =>
                directory.FetchAsync(new DirectoryRequest(), CancellationToken.None));
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    directory.FetchAsync(new DirectoryRequest(), cancellation.Token));
            Assert.Equal(1, attempts);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        public async Task ConfiguredIntervalControlsCatalogueRequestSpacing(int interval)
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var delays = new List<TimeSpan>();
            var config = Ready();
            config.ApiRequestIntervalSeconds = interval;
            var body = Opml().ToString();
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(
                    "HTTP/1.1 200 OK\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body)));
            var directory = new TuneInDirectory(() => config, transport: transport, utcNow: () => now,
                wait: (duration, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    delays.Add(duration);
                    now = now.Add(duration);
                    return Task.CompletedTask;
                });

            await directory.FetchAsync(new DirectoryRequest(), CancellationToken.None);
            await directory.FetchAsync(new DirectoryRequest(), CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(interval), Assert.Single(delays));
        }

        [Fact]
        public async Task ProviderRetryAfterOverridesShorterConfiguredInterval()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var delays = new List<TimeSpan>();
            var config = Ready();
            config.ApiRequestIntervalSeconds = 1;
            var attempts = 0;
            var body = Opml().ToString();
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(++attempts == 1
                    ? "HTTP/1.1 429 Rate Limited\r\nRetry-After: 5\r\nContent-Length: 0\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body)));
            var directory = new TuneInDirectory(() => config, transport: transport, utcNow: () => now,
                wait: (duration, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    delays.Add(duration);
                    now = now.Add(duration);
                    return Task.CompletedTask;
                });

            await directory.FetchAsync(new DirectoryRequest(), CancellationToken.None);
            Assert.Equal(2, attempts);
            Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(delays));
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
                directory.Resolve(directory.Snapshot(), folder).Category).Distinct().ToArray();
            Assert.Equal(new[] { "music", "talk", "sports" }, categories);
            Assert.Equal(100d, progress.Values.Last());
            Assert.DoesNotContain("local", categories);
        }

        [Fact]
        public async Task HostTopLevelRestrictionImportsNestedFoldersThroughCategoryRoot()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                var offset = request.Parameters.GetValueOrDefault("offset");
                return Task.FromResult(offset == null
                    ? Opml("<outline type='link' text='Page 2' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=25'/>")
                    : Opml("<outline type='audio' guide_id='s1' text='Station'/>"));
            });
            var snapshot = directory.Snapshot();
            var root = directory.Register(snapshot, TuneInChannel.CategoryEntry("talk"));
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
            { InternalId = 1, ParentId = 100, ExternalId = root });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.TraverseFolders = true;
            proxy.RestrictToTopLevel = true;
            proxy.TopLevelFolders.Add(root);
            var refresh = new TuneInRefresh(directory);

            await refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());

            Assert.Equal(new[] { root }, proxy.RefreshedFolders);
            Assert.Equal(8, proxy.LastRefreshDepth);
            Assert.Contains(proxy.ImportedStationIds, id =>
                directory.Resolve(snapshot, id).StationId == "s1");
            var status = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal("Completed", status.State);
            Assert.Equal(status.ImportFoldersTotal, status.ImportFoldersCompleted);
        }

        [Fact]
        public async Task ScheduledRefreshDiscoversBeforeImportAndDoesNotRefetchDuringImport()
        {
            var config = Ready();
            config.EnableTalk = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                calls++;
                return Task.FromResult(request.Parameters.ContainsKey("offset")
                    ? Opml("<outline type='audio' guide_id='s1' text='Station'/>")
                    : Opml("<outline type='link' text='More' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=25'/>"));
            });
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
            proxy.TraverseFolders = true;
            proxy.RequestRoot = true;
            proxy.OnRefresh = () => Assert.Equal(2, calls);
            var refresh = new TuneInRefresh(directory);

            await refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());

            Assert.Equal(2, calls);
            Assert.Single(proxy.RefreshedFolders);
            Assert.Equal(2, proxy.RootFolders.Count);
            var categoryRoot = proxy.RefreshedFolders[0];
            Assert.All(proxy.RefreshedFolders, folder =>
                Assert.Equal("talk", directory.Resolve(directory.Snapshot(), folder).Category));
            Assert.All(proxy.RootFolders, root => Assert.Equal(categoryRoot, Assert.Single(root)));
            var status = refresh.Status().Single(item => item.Category == "talk");
            Assert.Equal("Completed", status.State);
            Assert.NotNull(status.DiscoveryMilliseconds);
            Assert.NotNull(status.ImportMilliseconds);
            Assert.Equal(2, refresh.CurrentBaseline().TotalRequests);
            Assert.Equal(2, refresh.CurrentBaseline().Categories.Single().Requests);
            config.ApiRequestIntervalSeconds++;
            Assert.Null(refresh.CurrentBaseline());
        }

        [Fact]
        public async Task AggregateProgressStaysBelowCompletionDuringEarlyCategoryImportsWithAndWithoutBaseline()
        {
            var config = Ready();
            config.EnableTalk = true;
            config.EnableSports = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml()));
            var snapshot = directory.Snapshot();
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            foreach (var category in snapshot.Categories)
                libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
                {
                    InternalId = libraryProxy.Roots.Count + 1,
                    ParentId = 100,
                    ExternalId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category))
                });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.TraverseFolders = true;
            var refresh = new TuneInRefresh(directory, timeoutOverride: TimeSpan.FromSeconds(5));

            var releaseImport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var importing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeforeImport = async () => { importing.TrySetResult(true); await releaseImport.Task; };
            var firstRun = refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());
            try
            {
                await importing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(refresh.CurrentBaseline());
                Assert.True(refresh.CurrentConfiguredStatus().EstimatedProgress < 99);
                releaseImport.TrySetResult(true);
                await firstRun;
            }
            finally { releaseImport.TrySetResult(true); }

            releaseImport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            importing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeforeImport = async () => { importing.TrySetResult(true); await releaseImport.Task; };
            var secondRun = refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());
            try
            {
                await importing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(refresh.CurrentBaseline());
                Assert.True(refresh.CurrentConfiguredStatus().EstimatedProgress < 99);
                releaseImport.TrySetResult(true);
                await secondRun;
            }
            finally { releaseImport.TrySetResult(true); }
        }

        [Fact]
        public async Task NativeStatusReportsOnlyTheActiveTaskCategoryAndStage()
        {
            var config = Ready();
            config.EnableTalk = true;
            var discovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDiscovery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var importing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseImport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = new TuneInDirectory(() => config, async (_, token) =>
            {
                discovered.TrySetResult(true);
                await releaseDiscovery.Task;
                token.ThrowIfCancellationRequested();
                return Opml();
            });
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
            var channelManager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var channelProxy = (TaskManagerProxy)(object)channelManager;
            channelProxy.Channel = channel;
            channelProxy.BeforeImport = async () => { importing.TrySetResult(true); await releaseImport.Task; };
            var refresh = new TuneInRefresh(directory);
            var task = new RefreshTuneInTask(channelManager, null, refresh);
            var worker = DispatchProxy.Create<IScheduledTaskWorker, ScheduleTests.WorkerProxy>();
            var workerProxy = (ScheduleTests.WorkerProxy)worker;
            workerProxy.Task = task;
            workerProxy.State = TaskState.Running;
            var taskManager = DispatchProxy.Create<ITaskManager, ScheduleTests.ManagerProxy>();
            ((ScheduleTests.ManagerProxy)taskManager).Workers = new[] { worker };
            var diagnostics = new TuneInChannelRefresh(taskManager, refresh);

            var running = task.Execute(CancellationToken.None, new InlineProgress());
            try
            {
                await discovered.Task;
                var discovery = diagnostics.Status().Current;
                Assert.Equal("talk", discovery.Category);
                Assert.Contains("Discovering", discovery.Phase);
                Assert.Equal(1, discovery.CategoryIndex);
                Assert.Equal(1, discovery.CategoryCount);
                Assert.Equal(1, discovery.StartedRequests);
                Assert.Equal(0, discovery.CompletedRequests);
                workerProxy.State = TaskState.Idle;
                Assert.Null(diagnostics.Status().Current);
                workerProxy.State = TaskState.Running;
                releaseDiscovery.TrySetResult(true);
                await importing.Task;
                var import = diagnostics.Status().Current;
                Assert.Contains("Importing", import.Phase);
                Assert.Equal(1, import.CompletedRequests);
                releaseImport.TrySetResult(true);
                await running;
                Assert.Null(diagnostics.Status().Current);
            }
            finally
            {
                releaseDiscovery.TrySetResult(true);
                releaseImport.TrySetResult(true);
                await running;
            }
        }

        [Fact]
        public async Task DiscoveryFailureDoesNotStartEmbyImport()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => throw new IOException("Provider unavailable"));
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            var refresh = new TuneInRefresh(directory);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress()));

            Assert.Empty(proxy.RefreshedFolders);
            Assert.Null(refresh.CurrentConfiguredStatus());
        }

        [Fact]
        public async Task CancellingDiscoveryDoesNotStartEmbyImport()
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
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            var refresh = new TuneInRefresh(directory);
            using (var cancellation = new CancellationTokenSource())
            {
                var running = refresh.ExecuteConfiguredAsync(manager, null, cancellation.Token, new InlineProgress());
                await started.Task;
                Assert.NotNull(refresh.CurrentConfiguredStatus());
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            Assert.Empty(proxy.RefreshedFolders);
            Assert.Null(refresh.CurrentConfiguredStatus());
        }

        [Fact]
        public async Task ConfiguredRefreshTimeoutCoversCategoryWorkAndCreatesResumableCheckpoint()
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
            var refresh = new TuneInRefresh(directory, timeoutOverride: TimeSpan.FromMilliseconds(500));
            var running = refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => running);

            var status = refresh.CategoryStatus("talk");
            Assert.Equal("TimedOut", status.Outcome);
            Assert.True(status.Resumable);
            Assert.Equal("TimedOut", refresh.LastConfiguredOutcome);
            Assert.Null(refresh.CurrentConfiguredStatus());
        }

        [Fact]
        public async Task ImportCannotFetchAnUnstagedFolderEvenIfHostSwallowsFailure()
        {
            var config = Ready();
            config.EnableTalk = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(Opml());
            });
            var snapshot = directory.Snapshot();
            var request = new DirectoryRequest();
            request.Parameters["c"] = "talk";
            request.Parameters["offset"] = "25";
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.SwallowFailure = true;
            proxy.AdditionalFolder = directory.Register(snapshot, new NavigationEntry
            { Category = "talk", Request = request, Name = "Unstaged" });
            var refresh = new TuneInRefresh(directory);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress()));
            Assert.Equal(1, calls);
            Assert.Contains("Phase: Importing staged section into Emby", failure.Message);
            Assert.Contains("outside the staged refresh", failure.ToString());
        }

        [Fact]
        public async Task RefreshRunSharesOnlyIdenticalScopedRequestsAndRevalidatesOnNextRun()
        {
            var config = Ready();
            config.EnableTalk = true;
            config.EnableSports = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(Opml("<outline type='audio' guide_id='s1' text='Station'/>"));
            });
            var snapshot = directory.Snapshot();
            var request = new DirectoryRequest();
            request.Parameters["id"] = "g1";
            var other = request.Copy();
            other.Parameters["filter"] = "l1";
            async Task Fetch(string category, DirectoryRequest value)
            {
                await directory.SynchronizeCategoryAsync(category, 1,
                    () => directory.FetchCategoryAsync(snapshot, category, value, CancellationToken.None),
                    _ => { });
            }

            await directory.WithRefreshRunAsync(async () =>
            {
                await Fetch("talk", request);
                await Fetch("sports", request);
                await Fetch("sports", other);
            });
            Assert.Equal(2, calls);
            directory.InvalidateCategory("talk");
            directory.InvalidateCategory("sports");
            await directory.WithRefreshRunAsync(() => Fetch("sports", request));
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task ScheduledRefreshSharesIdenticalLocalAndLocationPagesWithoutLosingScope()
        {
            var config = Ready();
            config.EnableLocalRadio = true;
            config.EnableLocation = true;
            config.LocalCities.Add(new CatalogSelection { Id = "r1" });
            config.Locations.Add(new CatalogSelection { Id = "r1" });
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(Opml("<outline type='audio' guide_id='s1' text='Station'/>"));
            });
            var snapshot = directory.Snapshot();
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            foreach (var category in snapshot.Categories)
                libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
                {
                    InternalId = libraryProxy.Roots.Count + 1,
                    ParentId = 100,
                    ExternalId = directory.Register(snapshot, TuneInChannel.CategoryEntry(category))
                });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.TraverseFolders = true;
            var refresh = new TuneInRefresh(directory);

            await refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());

            Assert.Equal(1, calls);
            var stations = proxy.ImportedStationIds.Select(id => directory.Resolve(snapshot, id)).ToArray();
            Assert.Equal(new[] { "local", "location" }, stations.Select(item => item.Category).Distinct());
            Assert.All(stations, entry => Assert.Equal("s1", entry.StationId));
            Assert.Equal(1, refresh.Status().Single(item => item.Category == "location").CacheHits);
        }

        [Fact]
        public async Task SuccessfulRefreshReusesRecentPagesButRevalidatesAfterExpiryOrReset()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var config = Ready();
            config.EnableTalk = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(Opml("<outline type='audio' guide_id='s1' text='Station " + calls + "'/>"));
            }, utcNow: () => now);
            var snapshot = directory.Snapshot();
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, RefreshTaskLibraryProxy>();
            var libraryProxy = (RefreshTaskLibraryProxy)(object)library;
            libraryProxy.Parent = new MediaBrowser.Controller.Channels.Channel { InternalId = 100 };
            libraryProxy.Roots.Add(new MediaBrowser.Controller.Entities.Folder
            {
                InternalId = 1,
                ParentId = 100,
                ExternalId = directory.Register(snapshot, TuneInChannel.CategoryEntry("talk"))
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            var proxy = (TaskManagerProxy)(object)manager;
            proxy.Channel = channel;
            proxy.TraverseFolders = true;
            var refresh = new TuneInRefresh(directory);
            async Task Run() => await refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());

            await Run();
            await Run();
            Assert.Equal(1, calls);
            Assert.Equal(1, refresh.Status().Single(item => item.Category == "talk").CacheHits);
            now = now.AddMinutes(11);
            await Run();
            Assert.Equal(2, calls);
            Assert.Equal("Station 2", directory.Resolve(directory.Snapshot(), proxy.ImportedStationIds.Last()).Name);
            directory.Reset();
            await Run();
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task FailedImportDoesNotCommitDiscoveryForTheNextRun()
        {
            var config = Ready();
            config.EnableTalk = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(Opml());
            });
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
            proxy.OnRefresh = () => throw new IOException("Import failed");
            var refresh = new TuneInRefresh(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress()));
            proxy.OnRefresh = null;
            await refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress());
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task RefreshTraversesProviderAlphabeticFoldersWithoutEscapingSelectedMusic()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.Add(new CatalogSelection { Id = "g1" });
            var filters = new List<string>();
            var offsets = new List<string>();
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                Assert.Equal("g1", request.Parameters["id"]);
                var filter = request.Parameters.GetValueOrDefault("filter");
                filters.Add(filter);
                offsets.Add(request.Parameters.GetValueOrDefault("offset"));
                if (request.Parameters.ContainsKey("pivot"))
                    return Task.FromResult(Opml("<outline type='link' text='#' URL='http://opml.radiotime.com/Browse.ashx?id=g1&amp;filter=s:~%23'/>"
                        + "<outline type='link' text='E' URL='http://opml.radiotime.com/Browse.ashx?id=g1&amp;filter=s:~%C3%89'/>"));
                if (filter == "s:~#" && !request.Parameters.ContainsKey("offset"))
                    return Task.FromResult(Opml("<outline type='link' key='nextStations' text='Next' URL='http://opml.radiotime.com/Browse.ashx?offset=50&amp;id=g1&amp;filter=s:~#'/>"));
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
            Assert.Contains("50", offsets);
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

        [Fact]
        public async Task NativeTaskMessageContainsSafeRejectedDestinationReason()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(
                "<outline type='link' text='Bad' URL='https://private.example/path?secret=value'/>")));
            var manager = DispatchProxy.Create<IChannelManager, TaskManagerProxy>();
            ((TaskManagerProxy)(object)manager).Channel = new TuneInChannel(
                ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var refresh = new TuneInRefresh(directory);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                refresh.ExecuteConfiguredAsync(manager, null, CancellationToken.None, new InlineProgress()));

            Assert.Contains("Untrusted catalogue link rejected (untrusted host).", failure.Message);
            Assert.DoesNotContain("private", failure.Message);
            Assert.DoesNotContain("secret", failure.Message);
        }

        public class TaskManagerProxy : DispatchProxy
        {
            public TuneInChannel Channel { get; set; }
            public bool SwallowCancellation { get; set; }
            public bool SwallowFailure { get; set; }
            public bool TraverseFolders { get; set; }
            public bool RestrictToTopLevel { get; set; }
            public bool RequestRoot { get; set; }
            public Action OnRefresh { get; set; }
            public Func<Task> BeforeImport { get; set; }
            public string AdditionalFolder { get; set; }
            public List<string> RefreshedFolders { get; } = new List<string>();
            public HashSet<string> TopLevelFolders { get; } = new HashSet<string>(StringComparer.Ordinal);
            public List<string> ImportedStationIds { get; } = new List<string>();
            public List<string[]> RootFolders { get; } = new List<string[]>();
            public int LastRefreshDepth { get; private set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetLogger") return null;
                if (method.Name == "GetChannel") return Channel;
                if (method.Name == "RefreshChannelContent")
                {
                    var folder = (string)args[2];
                    if (RestrictToTopLevel && !TopLevelFolders.Contains(folder))
                        return Task.CompletedTask;
                    OnRefresh?.Invoke();
                    RefreshedFolders.Add(folder);
                    LastRefreshDepth = (int)args[1];
                    return Refresh(folder, TraverseFolders ? LastRefreshDepth : 0, (CancellationToken)args[3]);
                }
                throw new InvalidOperationException("Unexpected native task call.");
            }
            private async Task Refresh(string folder, int maxDepth, CancellationToken token)
            {
                try
                {
                    if (BeforeImport != null) await BeforeImport();
                    if (RequestRoot)
                    {
                        foreach (var rootId in new[] { null, string.Empty })
                        {
                            var root = await Channel.GetChannelItems(new InternalChannelItemQuery { FolderId = rootId }, token);
                            RootFolders.Add(root.Items.Select(item => item.Id).ToArray());
                        }
                    }
                    var pending = new Queue<(string Folder, int Depth)>();
                    pending.Enqueue((folder, 0));
                    while (pending.Count != 0)
                    {
                        var current = pending.Dequeue();
                        var result = await Channel.GetChannelItems(
                            new InternalChannelItemQuery { FolderId = current.Folder }, token);
                        ImportedStationIds.AddRange(result.Items.Where(item => item.Type == ChannelItemType.Media).Select(item => item.Id));
                        if (current.Depth < maxDepth)
                            foreach (var item in result.Items.Where(item => item.Type == ChannelItemType.Folder))
                                pending.Enqueue((item.Id, current.Depth + 1));
                    }
                    if (AdditionalFolder != null)
                        await Channel.GetChannelItems(new InternalChannelItemQuery { FolderId = AdditionalFolder }, token);
                }
                catch (OperationCanceledException) when (SwallowCancellation) { }
                catch (IOException) when (SwallowFailure) { }
                catch (InvalidDataException) when (SwallowFailure) { }
                catch (InvalidOperationException) when (SwallowFailure) { }
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
