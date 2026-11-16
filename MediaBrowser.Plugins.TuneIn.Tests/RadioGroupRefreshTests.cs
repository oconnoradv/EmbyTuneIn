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
            Assert.Equal(category == "music" || category == "location" || category == "language" ? 3 : 2, depth);
            var children = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, token);
            if (depth == 3)
                foreach (var child in children.Items)
                    await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = child.Id }, token);
            Assert.Equal(category, directory.Resolve(directory.Snapshot(), folder).Category);
        }

        [Fact]
        public void RefreshRoutesAreAdminOnlyAndMutationIsPostOnly()
        {
            foreach (var type in new[] { typeof(GetTuneInRefresh), typeof(StartTuneInRefresh), typeof(CancelTuneInRefresh) })
            {
                var auth = (AuthenticatedAttribute)Attribute.GetCustomAttribute(type, typeof(AuthenticatedAttribute));
                Assert.Equal("Admin", auth.Roles);
                Assert.False(auth.AllowLocal);
                var route = (MediaBrowser.Model.Services.RouteAttribute)Attribute.GetCustomAttribute(type, typeof(MediaBrowser.Model.Services.RouteAttribute));
                Assert.Equal(type == typeof(GetTuneInRefresh) ? "GET" : "POST", route.Verbs);
            }
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
            var channel = new TuneInChannel(null, null, directory);
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
            var channel = new TuneInChannel(null, null, directory);
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
            var first = refresh.Start("talk", (_, __, ___) => Task.CompletedTask);
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
            var second = refresh.Start("talk", (_, __, ___) => Task.CompletedTask);
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
            var channel = new TuneInChannel(null, null, directory);
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
        public async Task SynchronizationCannotEscapeCategoryOrCrawlBeyondCachedStartingPages()
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
            var channel = new TuneInChannel(null, null, directory);
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
            Assert.Equal(1, calls);
            Assert.Contains("Emby did not complete", refresh.Status().Single(item => item.Category == "talk").Error);
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
            Assert.Equal(100, status.ApiPercent);
            Assert.Equal("Failed", status.State);
            Assert.NotNull(status.Error);
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
