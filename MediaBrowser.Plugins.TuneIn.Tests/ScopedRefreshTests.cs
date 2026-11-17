using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ScopedRefreshTests
    {
        private static readonly CancellationToken None = CancellationToken.None;
        private static PluginConfiguration Ready() => new PluginConfiguration
        { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
        private static XDocument Opml(string rows = "") =>
            XDocument.Parse("<opml><head><status>200</status></head><body>" + rows + "</body></opml>");
        private static string More => "<outline type='link' text='More' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=25'/>";
        private static string Station => "<outline type='audio' text='Station' guide_id='s1'/>";
        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Consume(TuneInChannel channel, string root, CancellationToken token)
        {
            var pending = new Queue<string>();
            var seen = new HashSet<string>();
            pending.Enqueue(root);
            while (pending.Count != 0)
            {
                var folder = pending.Dequeue();
                if (!seen.Add(folder)) continue;
                var result = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
                foreach (var item in result.Items.Where(item => item.Type == ChannelItemType.Folder)) pending.Enqueue(item.Id);
            }
        }

        private static Task Run(TuneInRefresh refresh, TuneInChannel channel, Func<string, CancellationToken, Task> import = null,
            CancellationToken token = default, IProgress<double> progress = null) =>
            refresh.ExecuteAsync(() => channel, import ?? ((folder, cancellation) => Consume(channel, folder, cancellation)),
                Mock.Of<ILogger>(), token, progress);

        private static async Task Drained(TuneInRefresh refresh)
        {
            using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (refresh.Status().Busy) await Task.Delay(10, limit.Token);
        }

        private sealed class Progress : IProgress<double>
        {
            internal readonly List<double> Values = new List<double>();
            public void Report(double value) { lock (Values) Values.Add(value); }
        }

        [Fact]
        public async Task DisabledScopesNeverResolveTheChannelOrContactTheProvider()
        {
            var config = new PluginConfiguration { EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("No network."));
            var refresh = new TuneInRefresh(directory);
            var manager = new Mock<IChannelManager>(MockBehavior.Strict);
            var task = new RefreshTuneInTask(manager.Object, refresh);
            await task.Execute(None, new Progress());
            config.ConfigurationCompleted = true;
            config.SchemaVersion = 1;
            config.EnableTalk = false;
            config.EnableMusic = true;
            config.Music.Add(new CatalogSelection { Id = "g1", Enabled = false });
            await task.Execute(None, new Progress());
            Assert.Equal("No configured scopes", refresh.Status().Phase);
            Assert.Equal(100, refresh.Status().Progress);
            manager.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task NativeTaskStagesOnlyEnabledSelectionsAndSharesOnlyEquivalentRequests()
        {
            var config = Ready();
            config.EnableTalk = false;
            config.EnableLocalRadio = config.EnableLocation = true;
            config.LocalCities.Add(new CatalogSelection { Id = "r1" });
            config.LocalCities.Add(new CatalogSelection { Id = "r2", Enabled = false });
            config.Locations.Add(new CatalogSelection { Id = "r1" });
            config.EnableMusic = true; // No saved music scope.
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, _) =>
            { requests.Add(request); return Task.FromResult(Opml(Station)); });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            var manager = new Mock<IChannelManager>(MockBehavior.Strict);
            var categories = new List<string>();
            manager.Setup(value => value.GetChannel<TuneInChannel>()).Returns(channel);
            manager.Setup(value => value.RefreshChannelContent(channel, 8, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<IChannel, int, string, CancellationToken>(async (_, depth, folder, token) =>
                {
                    var root = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery(), token)).Items);
                    Assert.Equal(folder, root.Id);
                    categories.Add(directory.Resolve(directory.Snapshot(), folder).Category);
                    await Consume(channel, folder, token);
                });
            await new RefreshTuneInTask(manager.Object, refresh).Execute(None, new Progress());
            Assert.Equal(new[] { "local", "location" }, categories);
            Assert.Equal("r1", Assert.Single(requests).Parameters["id"]);
            Assert.Equal(1, refresh.Status().CacheHits);
            Assert.Equal(2, refresh.Status().CategoriesCompleted);
            manager.VerifyAll();
        }

        [Fact]
        public async Task DiscoveryCompletesBeforeImportAndProgressWaitsForNativeCompletion()
        {
            var config = Ready();
            config.EnableSports = true;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                calls++;
                return Task.FromResult(Opml(request.Parameters.GetValueOrDefault("c") == "talk"
                    && !request.Parameters.ContainsKey("offset") ? More : Station));
            });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            var progress = new Progress();
            var entered = Signal();
            var release = Signal();
            var execution = Run(refresh, channel, async (folder, token) =>
            {
                if (directory.Resolve(directory.Snapshot(), folder).Category == "talk")
                {
                    Assert.Equal(2, calls);
                    Assert.Equal("Importing", refresh.Status().Phase);
                    Assert.Equal(2, refresh.Status().ImportFoldersTotal);
                    Assert.Equal(0, refresh.Status().CategoriesCompleted);
                    await Consume(channel, folder, token);
                    Assert.Equal(2, calls);
                    Assert.Equal(2, refresh.Status().ImportFoldersServed);
                    entered.TrySetResult(true);
                    await release.Task;
                }
                else
                {
                    Assert.Equal(1, refresh.Status().CategoriesCompleted);
                    await Consume(channel, folder, token);
                }
            }, progress: progress);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(100, progress.Values);
            Assert.True(progress.Values.Last() < 50);
            release.TrySetResult(true);
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Completed", refresh.Status().State);
            Assert.Equal(3, refresh.Status().RequestsCompleted);
            Assert.Equal(100, progress.Values.Last());
            Assert.Equal(progress.Values.OrderBy(value => value), progress.Values);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NoOpOrSwallowedOutOfScopeImportCannotReportSuccess(bool swallow)
        {
            var config = Ready();
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(Station)));
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            var progress = new Progress();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel, async (folder, token) =>
            {
                if (swallow)
                {
                    try { await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = "unstaged" }, token); }
                    catch (InvalidOperationException) { }
                    await Consume(channel, folder, token);
                }
            }, progress: progress));
            Assert.Equal("Error", refresh.Status().State);
            Assert.True(refresh.Status().ImportMayHavePartiallyApplied);
            Assert.DoesNotContain(100, progress.Values);
            Assert.False(refresh.Status().Resumable);
        }

        [Fact]
        public async Task VisitingOnlyOneOfMultipleSelectionsIsIncomplete()
        {
            var config = Ready();
            config.EnableTalk = false;
            config.EnableMusic = true;
            config.Music.AddRange(new[] { new CatalogSelection { Id = "g1" }, new CatalogSelection { Id = "g2" } });
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(Station)));
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel, async (folder, token) =>
            {
                var first = (await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token)).Items[0];
                await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = first.Id }, token);
            }));
            Assert.Equal(3, refresh.Status().ImportFoldersTotal);
            Assert.Equal(2, refresh.Status().ImportFoldersServed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ProviderAndNativeFailuresAreSanitizedAndNeverCommitFailedRunPages(bool native)
        {
            var config = Ready();
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(native ? Opml(Station) : XDocument.Parse("<opml><head><status>500</status></head><body/></opml>"));
            });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            var imports = 0;
            for (var index = 0; index < 2; index++)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel, (_, __) =>
                { imports++; throw new IOException("https://private.example/?username=secret"); }));
                Assert.DoesNotContain("private", error.ToString());
                Assert.DoesNotContain("secret", error.ToString());
                Assert.Equal("Error", refresh.Status().State);
                Assert.False(refresh.Status().Busy);
            }
            Assert.Equal(native ? 2 : 0, imports);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task SuccessfulCacheExpiresAndSettingsChangesInvalidateItsIdentity()
        {
            var config = Ready();
            var now = DateTime.UtcNow;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            { calls++; return Task.FromResult(Opml(Station)); });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory, clock: () => now);
            await Run(refresh, channel);
            await Run(refresh, channel);
            Assert.Equal(1, calls);
            Assert.Equal(1, refresh.Status().CacheHits);
            now = now.AddMinutes(config.CatalogueCacheMinutes);
            await Run(refresh, channel);
            Assert.Equal(2, calls);
            var oldId = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", StationId = "s1" });
            refresh.UpdateConfiguration(() => config.ApiRequestIntervalSeconds = 4);
            Assert.Throws<InvalidOperationException>(() => directory.Resolve(directory.Snapshot(), oldId));
            await Run(refresh, channel);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task RefreshRevalidatesLanguagesInsteadOfTrustingAnOlderSelectorCache()
        {
            var config = Ready();
            config.EnableTalk = false;
            config.EnableLanguage = true;
            config.Languages.Add(new CatalogSelection { Id = "l1" });
            var removed = false;
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(removed ? Opml() : Opml(
                    "<outline type='link' text='Language' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l1'/>"));
            });
            Assert.Single((await directory.SelectAsync("language", null, false, None)).Items);
            removed = true;
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel, (_, __) =>
                throw new Exception("Import must not begin.")));
            Assert.Equal(2, calls);
            Assert.Equal(1, refresh.Status().RequestsCompleted);
            Assert.Equal("Discovering", refresh.Status().Phase);
            Assert.Null(refresh.Status().LastHttpStatus); // Injected XML is not an HTTP response.
        }

        [Fact]
        public async Task PageBudgetFailsBeforeAnotherRequestInsteadOfImportingAPartialCatalogue()
        {
            var config = Ready();
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                calls++;
                return Task.FromResult(Opml(request.Parameters.ContainsKey("offset") ? Station
                    : string.Concat(Enumerable.Range(1, 128).Select(index => "<outline type='link' text='Page " + index
                        + "' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset=" + index + "'/>"))));
            });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel, (_, __) =>
                throw new Exception("No partial import allowed.")));
            Assert.Equal(128, calls);
            Assert.Equal(128, refresh.Status().RequestsStarted);
            Assert.Equal(128, refresh.Status().RequestsCompleted);
            Assert.Equal("Discovering", refresh.Status().Phase);
            Assert.False(refresh.Status().ImportMayHavePartiallyApplied);
            Assert.Equal("Error", refresh.Status().State);
            Assert.False(refresh.Status().Busy);
        }

        [Fact]
        public async Task CatalogueSessionEnforcesExactPageAndByteBudgets()
        {
            var calls = 0;
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => DateTime.UtcNow, _ => { }, () => { });
            for (var index = 0; index < TuneInCatalogueSession.MaxPages; index++)
            {
                var request = new DirectoryRequest();
                request.Parameters["id"] = "r" + (index + 1);
                await session.FetchAsync(request, () => { calls++; return Task.FromResult(Opml()); }, None);
            }
            var extra = new DirectoryRequest();
            extra.Parameters["id"] = "r999";
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                session.FetchAsync(extra, () => { calls++; return Task.FromResult(Opml()); }, None));
            Assert.Equal(128, calls);
            var large = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(15), () => DateTime.UtcNow, _ => { }, () => { });
            var document = Opml("<outline text='" + new string('x', 1750000) + "'/>");
            for (var index = 0; index < 4; index++)
            {
                var request = new DirectoryRequest();
                request.Parameters["id"] = "r" + (index + 1);
                await large.FetchAsync(request, () => Task.FromResult(document), None);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => large.FetchAsync(extra, () => Task.FromResult(document), None));
            Assert.True(large.Capture().Sum(page => (long)page.Bytes.Length) <= 8 * 1024 * 1024);
        }

        [Fact]
        public async Task DepthLimitRejectsIncompleteDiscoveryBeforeNativeImport()
        {
            var config = Ready();
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            {
                calls++;
                return Task.FromResult(Opml("<outline type='link' text='Deeper' URL='https://opml.radiotime.com/Browse.ashx?c=talk&amp;offset="
                    + calls + "'/>"));
            });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel, (_, __) =>
                throw new Exception("Import must not start.")));
            Assert.Equal(9, calls);
            Assert.Equal("Discovering", refresh.Status().Phase);
            Assert.False(refresh.Status().ImportMayHavePartiallyApplied);
        }

        [Fact]
        public async Task TimeoutRetainsValidatedPagesAndNextRunRecovers()
        {
            var config = Ready();
            var blocked = true;
            var started = Signal();
            var calls = 0;
            var directory = new TuneInDirectory(() => config, async (request, token) =>
            {
                calls++;
                if (!request.Parameters.ContainsKey("offset")) return Opml(More);
                if (blocked)
                {
                    started.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Opml(Station);
            });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory, timeoutOverride: TimeSpan.FromSeconds(1));
            var execution = Run(refresh, channel);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
            await Drained(refresh);
            Assert.Equal("TimedOut", refresh.Status().State);
            Assert.Equal(1, refresh.Status().RequestsCompleted);
            Assert.True(refresh.Status().Resumable);
            blocked = false;
            await Run(refresh, channel);
            Assert.Equal("Completed", refresh.Status().State);
            Assert.True(refresh.Status().Resumed);
            Assert.Equal(1, refresh.Status().CacheHits);
            Assert.Equal(3, calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancelledOrTimedOutUncooperativeWorkKeepsOwnershipUntilDrained(bool timeout)
        {
            var config = Ready();
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            var directory = new TuneInDirectory(() => config, async (_, __) =>
            { calls++; entered.TrySetResult(true); await release.Task; return Opml(Station); });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory, timeoutOverride: timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(20));
            using (var cancellation = new CancellationTokenSource())
            {
                var execution = Run(refresh, channel, token: cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (!timeout) cancellation.Cancel();
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
                else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(timeout ? "TimedOut" : "Cancelled", refresh.Status().State);
                Assert.True(refresh.Status().Busy);
                Assert.False(refresh.Status().Resumable);
                Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => config.EnableSports = true));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel));
                Assert.Equal(1, calls);
                release.TrySetResult(true);
                await Drained(refresh);
                await Run(refresh, channel);
                Assert.Equal(2, calls); // Cancelled responses are not cached.
                Assert.Equal("Completed", refresh.Status().State);
            }
        }

        [Fact]
        public async Task NativeImportCancellationReplaysFromRootWithoutRefetchOrConcurrentSave()
        {
            var config = Ready();
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (_, __) =>
            { calls++; return Task.FromResult(Opml(Station)); });
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            using (var cancellation = new CancellationTokenSource())
            {
                var execution = Run(refresh, channel, async (folder, token) =>
                {
                    await Consume(channel, folder, token);
                    entered.TrySetResult(true);
                    await release.Task; // Simulates a native importer ignoring cancellation.
                }, cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Throws<InvalidOperationException>(() => refresh.UpdateConfiguration(() => config.EnableTalk = false));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(refresh.Status().ImportMayHavePartiallyApplied);
                Assert.True(refresh.Status().Busy);
                release.TrySetResult(true);
                await Drained(refresh);
                var imports = 0;
                await Run(refresh, channel, async (folder, token) => { imports++; await Consume(channel, folder, token); });
                Assert.Equal(1, imports);
                Assert.Equal(1, calls);
                Assert.True(refresh.Status().Resumed);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task PersistedCheckpointResumesOnlyAnUnexpiredMatchingConfiguration(bool changeSettings, bool expire)
        {
            var path = Path.Combine(Path.GetTempPath(), "tunein-refresh-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var config = Ready();
                config.EnableSports = true;
                var now = DateTime.UtcNow;
                var requests = new List<string>();
                var block = true;
                var entered = Signal();
                var directory = new TuneInDirectory(() => config, async (request, token) =>
                {
                    var category = request.Parameters["c"];
                    requests.Add(category);
                    if (category == "sports" && block)
                    {
                        entered.TrySetResult(true);
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    return Opml(Station);
                });
                var channel = new TuneInChannel(directory);
                var store = new TuneInRefreshStore(path);
                var first = new TuneInRefresh(directory, store, () => now);
                using (var cancellation = new CancellationTokenSource())
                {
                    var execution = Run(first, channel, token: cancellation.Token);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
                    await Drained(first);
                }
                Assert.Equal(new[] { "talk" }, store.Read().CompletedCategories);
                if (changeSettings) config.Username = "different";
                if (expire) now = now.AddMinutes(config.CatalogueCacheMinutes);
                block = false;
                var restarted = new TuneInRefresh(directory, new TuneInRefreshStore(path), () => now);
                Assert.Equal("Interrupted", restarted.Status().State);
                Assert.Equal(!changeSettings && !expire, restarted.Status().Resumable);
                var imported = new List<string>();
                await Run(restarted, channel, async (folder, token) =>
                {
                    imported.Add(directory.Resolve(directory.Snapshot(), folder).Category);
                    await Consume(channel, folder, token);
                });
                Assert.Equal(!changeSettings && !expire ? new[] { "sports" } : new[] { "talk", "sports" }, imported);
                Assert.Equal(!changeSettings && !expire ? 1 : 2, requests.Count(category => category == "talk"));
                Assert.False(File.Exists(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public async Task ConfigurationInvalidationCannotBeHiddenByTheNativeImporter()
        {
            var config = Ready();
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(Station)));
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(refresh, channel, async (folder, token) =>
            {
                config.EnableTalk = false; // Bypasses the supported save path deliberately.
                try { await Consume(channel, folder, token); }
                catch (OperationCanceledException) { }
            }));
            Assert.Equal("Cancelled", refresh.Status().State);
            Assert.False(refresh.Status().Resumable);
        }

        [Fact]
        public async Task NativeQueryCancellationCannotBeHiddenAfterOtherFoldersWereConsumed()
        {
            var config = Ready();
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml()));
            var channel = new TuneInChannel(directory);
            var refresh = new TuneInRefresh(directory);
            using (var cancellation = new CancellationTokenSource())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(refresh, channel, async (folder, token) =>
                {
                    await Consume(channel, folder, token);
                    cancellation.Cancel();
                    try { await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, cancellation.Token); }
                    catch (OperationCanceledException) { }
                }));
            }
            Assert.Equal("Cancelled", refresh.Status().State);
            Assert.Equal(0, refresh.Status().CategoriesCompleted);
        }

        [Fact]
        public async Task CorruptCheckpointFailsExplicitlyAndCanRecoverOnNextRun()
        {
            var path = Path.Combine(Path.GetTempPath(), "tunein-refresh-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///private'>]><x>&x;</x>");
                var config = Ready();
                var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(Station)));
                var channel = new TuneInChannel(directory);
                var refresh = new TuneInRefresh(directory, new TuneInRefreshStore(path));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel));
                Assert.Equal("Error", refresh.Status().State);
                Assert.False(refresh.Status().Busy);
                await Run(refresh, channel);
                Assert.Equal("Completed", refresh.Status().State);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public async Task InvalidOnDiskConfigurationDoesNotLeaveAnOlderSuccessInDiagnostics()
        {
            var config = Ready();
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml()));
            var refresh = new TuneInRefresh(directory);
            var channel = new TuneInChannel(directory);
            await Run(refresh, channel);
            config.RefreshTimeoutMinutes = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refresh, channel));
            Assert.Equal("Error", refresh.Status().State);
            Assert.False(refresh.Status().Busy);
        }

        [Fact]
        public async Task RequestPacingHonorsConfiguredMinimumRetryAfterAndCancellation()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var delays = new List<TimeSpan>();
            var pacer = new TuneInRequestPacer(() => now, (delay, token) =>
            { token.ThrowIfCancellationRequested(); delays.Add(delay); now += delay; return Task.CompletedTask; });
            Task<HttpResponseInfo> Ok() => Task.FromResult(new HttpResponseInfo { StatusCode = HttpStatusCode.OK });
            await pacer.SendAsync(Ok, 3, None);
            await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
            {
                StatusCode = (HttpStatusCode)429,
                Headers = new Dictionary<string, string> { ["Retry-After"] = "30" }
            }), 7, None);
            await pacer.SendAsync(Ok, 3, None);
            Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(30) }, delays);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    pacer.SendAsync(() => throw new Exception("Must not send."), 3, cancellation.Token));
            }
        }

        [Fact]
        public async Task IgnoredProviderCancellationDoesNotReleaseSharedRequestPacingOwnership()
        {
            var entered = Signal();
            var release = Signal();
            var now = DateTime.UtcNow;
            var pacer = new TuneInRequestPacer(() => now, (delay, _) => { now += delay; return Task.CompletedTask; });
            using (var cancellation = new CancellationTokenSource())
            {
                var first = pacer.SendAsync(async () =>
                {
                    entered.TrySetResult(true);
                    await release.Task;
                    return new HttpResponseInfo();
                }, 3, cancellation.Token);
                await entered.Task;
                cancellation.Cancel();
                var secondSent = false;
                var second = pacer.SendAsync(() =>
                {
                    secondSent = true;
                    return Task.FromResult(new HttpResponseInfo());
                }, 3, None);
                Assert.False(secondSent);
                release.TrySetResult(true);
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(secondSent);
            }
        }

        [Fact]
        public void DiagnosticsAreAdminOnlyReadOnlySanitizedAndDoNotFetchProviderData()
        {
            var auth = typeof(GetTuneInRefreshStatus).GetCustomAttribute<AuthenticatedAttribute>();
            Assert.Equal("Admin", auth.Roles);
            Assert.False(auth.AllowLocal);
            Assert.Equal("GET", typeof(GetTuneInRefreshStatus).GetCustomAttribute<RouteAttribute>().Verbs);
            var config = Ready();
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("No network."));
            var service = new TuneInRefreshService(new TuneInRefresh(directory));
            Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInRefreshStatus()));
            service.Request = Mock.Of<IRequest>();
            Assert.Equal("NeverRun", service.Get(new GetTuneInRefreshStatus()).State);
            Assert.Single(typeof(RefreshTuneInTask).Assembly.GetTypes(), type => typeof(IScheduledTask).IsAssignableFrom(type));
            var task = new RefreshTuneInTask(null, new TuneInRefresh(directory));
            Assert.Empty(task.GetDefaultTriggers());
            Assert.False(task.IsHidden);
            Assert.Equal("Refresh TuneIn", task.Name);
        }

        [Theory]
        [InlineData(2, 20, 15)]
        [InlineData(61, 20, 15)]
        [InlineData(3, 0, 15)]
        [InlineData(3, 181, 15)]
        [InlineData(3, 20, 0)]
        [InlineData(3, 20, 121)]
        public void InvalidRefreshLimitsAreRejectedBeforeSaving(int interval, int timeout, int cache)
        {
            var config = Ready();
            config.ApiRequestIntervalSeconds = interval;
            config.RefreshTimeoutMinutes = timeout;
            config.CatalogueCacheMinutes = cache;
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
        }
    }
}
