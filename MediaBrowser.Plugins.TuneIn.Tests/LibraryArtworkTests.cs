using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class LibraryArtworkTests
    {
        [Fact]
        public async Task ConfiguredCategoriesUseDistinctBundledImages()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
            var host = DispatchProxy.Create<IServerApplicationHost, HostProxy>();
            var channel = new TuneInChannel(directory, serverHost: host);
            var categories = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
            Assert.Equal(new[] { "http://127.0.0.1:8096/emby/TuneIn/CategoryArtwork/talk?v=1",
                "http://127.0.0.1:8096/emby/TuneIn/CategoryArtwork/sports?v=1" },
                categories.Items.Select(item => item.ImageUrl));
            var hashes = new HashSet<string>();
            foreach (var category in new[] { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" })
            {
                using var stream = TuneInCategoryArtwork.Open(category);
                using var hash = System.Security.Cryptography.SHA256.Create();
                Assert.True(hashes.Add(Convert.ToHexString(hash.ComputeHash(stream))));
            }
            Assert.Throws<ArgumentException>(() => TuneInCategoryArtwork.Open("../primary"));
            Assert.Throws<UnauthorizedAccessException>(() =>
                new TuneInCategoryArtworkService(Moq.Mock.Of<IHttpResultFactory>()).Get(new GetTuneInCategoryArtwork { Category = "talk" }));
            Assert.Single(typeof(GetTuneInCategoryArtwork).GetCustomAttributes(typeof(AuthenticatedAttribute), false));
        }

        [Fact]
        public void MigrationUpdatesOnlyValidFoldersInTheRequestedCategoryAndIsIdempotent()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
            var snapshot = directory.Snapshot();
            var rootId = directory.Register(snapshot, new NavigationEntry { Category = "talk", Name = "Talk" });
            var root = Folder(1, rootId);
            var child = Folder(2, directory.Register(snapshot, new NavigationEntry
            {
                Category = "talk", Name = "Nested talk",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=talk&offset=1")
            }));
            var station = Folder(3, directory.Register(snapshot, new NavigationEntry { Category = "talk", StationId = "s1" }));
            var other = Folder(4, directory.Register(snapshot, new NavigationEntry { Category = "sports", Name = "Sports" }));
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            var proxy = (LibraryProxy)library;
            proxy.Root = root;
            proxy.Descendants = new BaseItem[] { child, station, other };
            var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>(), library: library);
            channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
            Assert.Equal(new long[] { 1, 2 }, proxy.Updated.Select(item => item.InternalId));
            Assert.All(proxy.Updated, item =>
            {
                Assert.EndsWith("/TuneIn/CategoryArtwork/talk?v=1", item.GetImagePath(ImageType.Primary, 0));
                Assert.Equal("1:talk", item.GetProviderId("TuneInCategoryArtwork"));
            });
            Assert.Equal("old.png", station.GetImagePath(ImageType.Primary, 0));
            Assert.Equal("old.png", other.GetImagePath(ImageType.Primary, 0));
            channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
            Assert.Equal(2, proxy.Updated.Count);
            Assert.Throws<InvalidOperationException>(() => channel.UpdateCategoryArtwork(child.ExternalId, CancellationToken.None));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => channel.UpdateCategoryArtwork(rootId, cancel.Token));
        }

        [Fact]
        public void FailedImageUpdateIsRetriedOnTheNextRefresh()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
            var rootId = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", Name = "Talk" });
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            var proxy = (LibraryProxy)library;
            proxy.Root = Folder(1, rootId);
            proxy.FailNextUpdate = true;
            var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>(), library: library);
            Assert.Throws<InvalidOperationException>(() => channel.UpdateCategoryArtwork(rootId, CancellationToken.None));
            channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
            Assert.Single(proxy.Updated);
            Assert.Equal("1:talk", proxy.Root.GetProviderId("TuneInCategoryArtwork"));
        }

        private static Folder Folder(long id, string externalId)
        {
            var folder = new Folder { InternalId = id, ExternalId = externalId };
            folder.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "old.png" }, 0);
            return folder;
        }

        public class HostProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.Equal("GetLocalApiUrl", method.Name);
                Assert.True(IPAddress.IsLoopback((IPAddress)args[0]));
                return "http://127.0.0.1:8096/emby";
            }
        }

        public class LibraryProxy : DispatchProxy
        {
            public BaseItem Root { get; set; }
            public BaseItem[] Descendants { get; set; } = Array.Empty<BaseItem>();
            public List<BaseItem> Updated { get; } = new List<BaseItem>();
            public bool FailNextUpdate { get; set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetItemList")
                {
                    var query = (InternalItemsQuery)args[0];
                    if (query.ExternalId != null) return new[] { Root };
                    Assert.Equal(new[] { Root.InternalId }, query.ParentIds);
                    return Descendants.Skip(query.StartIndex ?? 0).Take(query.Limit ?? 100).ToArray();
                }
                if (method.Name == "GetItemById") return null;
                if (method.Name == "UpdateItem")
                {
                    if (FailNextUpdate) { FailNextUpdate = false; throw new InvalidOperationException("Image update failed."); }
                    Updated.Add((BaseItem)args[0]);
                    return null;
                }
                throw new InvalidOperationException(method.Name);
            }
        }
    }
}
