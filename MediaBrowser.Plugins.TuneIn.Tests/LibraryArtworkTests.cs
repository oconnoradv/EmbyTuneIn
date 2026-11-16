using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Plugins.TuneIn.Configuration;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class LibraryArtworkTests
    {
        [Fact]
        public async Task MainLibraryArtworkIsWideAndDoesNotAdvertiseContentChangeEvents()
        {
            var channel = new TuneInChannel(null, null, new TuneInDirectory());
            Assert.False(typeof(IHasChangeEvent).IsAssignableFrom(typeof(TuneInChannel)));
            foreach (var type in channel.GetSupportedChannelImages())
            {
                var image = await channel.GetChannelImage(type, CancellationToken.None);
                using (image.Stream)
                using (var bytes = new System.IO.MemoryStream())
                {
                    await image.Stream.CopyToAsync(bytes);
                    Assert.Equal(544, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.ToArray().AsSpan(16)));
                    Assert.Equal(306, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.ToArray().AsSpan(20)));
                }
            }
        }

        [Fact]
        public void GroupRefreshMigratesOnlyItsOwnParentChannelBrandingOnce()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("No artwork discovery requests."));
            var rootId = directory.Register(directory.Snapshot(), TuneInChannel.CategoryEntry("talk"));
            var root = Folder(1, rootId);
            root.ParentId = 100;
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            var proxy = (LibraryProxy)library;
            proxy.Root = root;
            proxy.Parent = new Channel { InternalId = 100 };
            proxy.Parent.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "old-portrait.png" }, 0);
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            channel.ReplaceFolderArtwork(rootId, CancellationToken.None);
            Assert.Contains(proxy.Parent, proxy.Updated);
            Assert.All(channel.GetSupportedChannelImages(), type =>
                Assert.EndsWith("/TuneInFork/Branding?v=1", proxy.Parent.GetImagePath(type, 0)));
            channel.ReplaceFolderArtwork(rootId, CancellationToken.None);
            Assert.Single(proxy.Updated, item => item == proxy.Parent);
        }

        [Fact]
        public void RefreshReplacesPersistedFolderPrimaryImagesWithoutChangingStationsOrOtherGroups()
        {
            var config = new PluginConfiguration
            { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Artwork replacement must not query TuneIn."));
            var snapshot = directory.Snapshot();
            var rootId = directory.Register(snapshot, TuneInChannel.CategoryEntry("talk"));
            var root = Folder(1, rootId);
            var child = Folder(2, directory.Register(snapshot, new NavigationEntry
            {
                Category = "talk",
                Name = "Nested talk",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=talk&offset=1")
            }));
            var station = Folder(3, directory.Register(snapshot, new NavigationEntry { Category = "talk", StationId = "s1" }));
            var sports = Folder(4, directory.Register(snapshot, TuneInChannel.CategoryEntry("sports")));
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            var proxy = (LibraryProxy)library;
            proxy.Root = root;
            proxy.Descendants = new BaseItem[] { child, station, sports };
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory, library);
            channel.ReplaceFolderArtwork(rootId, CancellationToken.None);
            Assert.Equal(new long[] { 1, 2 }, proxy.Updated.Select(item => item.InternalId));
            Assert.All(proxy.Updated, item =>
            {
                var image = item.GetImageInfo(ImageType.Primary, 0);
                Assert.Contains("/TuneInFork/Artwork/", image.Path);
                Assert.EndsWith("?v=3", image.Path);
                Assert.Equal(ImageType.Primary, image.Type);
                Assert.Equal("3:talk", item.GetProviderId("TuneInForkArtwork"));
            });
            Assert.Equal("old-primary.png", station.GetImagePath(ImageType.Primary, 0));
            Assert.Equal("old-primary.png", sports.GetImagePath(ImageType.Primary, 0));
            channel.ReplaceFolderArtwork(rootId, CancellationToken.None);
            Assert.Equal(2, proxy.Updated.Count);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => channel.ReplaceFolderArtwork(rootId, cancellation.Token));
            }
        }

        private static Folder Folder(long id, string externalId)
        {
            var folder = new Folder { InternalId = id, ExternalId = externalId };
            folder.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "old-primary.png" }, 0);
            return folder;
        }

        public class LibraryProxy : DispatchProxy
        {
            public BaseItem Root { get; set; }
            public BaseItem Parent { get; set; }
            public BaseItem[] Descendants { get; set; } = Array.Empty<BaseItem>();
            public List<BaseItem> Updated { get; } = new List<BaseItem>();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetItemList")
                {
                    var query = (InternalItemsQuery)args[0];
                    Assert.True(query.IsFolder);
                    if (query.ExternalId != null)
                    {
                        Assert.Equal(Root.ExternalId, query.ExternalId);
                        return new[] { Root };
                    }
                    Assert.Equal(new[] { Root.InternalId }, query.ParentIds);
                    Assert.True(query.Recursive);
                    return Descendants.Skip(query.StartIndex ?? 0).Take(query.Limit ?? 100).ToArray();
                }
                if (method.Name == "GetItemById") return Parent?.InternalId == (long)args[0] ? Parent : null;
                if (method.Name == "UpdateItem")
                {
                    Assert.True(((ItemUpdateType)args[2]).HasFlag(ItemUpdateType.ImageUpdate));
                    Updated.Add((BaseItem)args[0]);
                    return null;
                }
                throw new InvalidOperationException(method.Name);
            }
        }
    }
}
