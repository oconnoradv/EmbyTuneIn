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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class LibraryArtworkTests
    {
        [Fact]
        public void PluginLibraryThumbnailUsesLibraryArtwork()
        {
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            using var actual = plugin.GetThumbImage();
            using var expected = typeof(Plugin).Assembly.GetManifestResourceStream(
                "MediaBrowser.Plugins.TuneIn.Images.tunein-library.png");
            using var actualBuffer = new System.IO.MemoryStream();
            using var expectedBuffer = new System.IO.MemoryStream();
            Assert.NotNull(actual);
            Assert.NotNull(expected);
            actual.CopyTo(actualBuffer);
            expected.CopyTo(expectedBuffer);
            Assert.Equal(expectedBuffer.ToArray(), actualBuffer.ToArray());
            Assert.Equal(1280, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(actualBuffer.ToArray().AsSpan(16, 4)));
            Assert.Equal(720, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(actualBuffer.ToArray().AsSpan(20, 4)));
        }

        [Fact]
        public async Task ChannelImagesUseLibraryArtworkAndInvalidateCachedVersion()
        {
            var directory = new TuneInDirectory(() => new PluginConfiguration(), (_, __) => throw new Exception("Unexpected provider request."));
            var channel = new TuneInChannel(directory);
            Assert.Equal("49", channel.DataVersion);
            using var expected = typeof(TuneInChannel).Assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Images.tunein-library.png");
            using var expectedBuffer = new System.IO.MemoryStream();
            Assert.NotNull(expected);
            expected.CopyTo(expectedBuffer);
            foreach (var type in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop })
            {
                var image = await channel.GetChannelImage(type, CancellationToken.None);
                using (image.Stream)
                using (var actualBuffer = new System.IO.MemoryStream())
                {
                    image.Stream.CopyTo(actualBuffer);
                    var png = actualBuffer.ToArray();
                    Assert.Equal(expectedBuffer.ToArray(), png);
                    Assert.Equal(1280, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
                    Assert.Equal(720, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
                }
            }
        }

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
        public void RefreshReplacesPersistedArtworkOnTheTuneInChannelParent()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
            var rootId = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", Name = "Talk" });
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            var proxy = (LibraryProxy)library;
            proxy.Root = Folder(1, rootId);
            proxy.Root.ParentId = 10;
            var parent = new Channel { InternalId = 10, Name = "Other channel" };
            var types = new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop };
            foreach (var type in types)
                parent.SetImage(new ItemImageInfo { Type = type, Path = "old.png" }, 0);
            proxy.Parent = parent;
            var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>(), library: library);

            channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
            Assert.All(types, type => Assert.Equal("old.png", parent.GetImagePath(type, 0)));
            parent.Name = "TuneIn";
            proxy.FailNextUpdate = true;
            Assert.Throws<InvalidOperationException>(() => channel.UpdateCategoryArtwork(rootId, CancellationToken.None));
            channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
            channel.UpdateCategoryArtwork(rootId, CancellationToken.None);

            Assert.Single(proxy.Updated, item => item == parent);
            Assert.All(types, type => Assert.Equal("http://127.0.0.1:8096/emby/TuneIn/CategoryArtwork/channel?v=2",
                parent.GetImagePath(type, 0)));
            Assert.EndsWith("/TuneIn/CategoryArtwork/talk?v=1", proxy.Root.GetImagePath(ImageType.Primary, 0));
        }

        [Fact]
        public void MusicGenreFoldersUseInstalledArtworkPackCards()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tunein-art-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            var previous = TuneInCategoryArtwork.GenreFolder;
            try
            {
                var card = System.IO.Path.Combine(folder, "c900001.jpg");
                System.IO.File.WriteAllBytes(card, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "notes.txt"), new byte[] { 1 });
                TuneInCategoryArtwork.GenreFolder = folder;
                var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true };
                var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
                var snapshot = directory.Snapshot();
                var rootId = directory.Register(snapshot, new NavigationEntry { Category = "music", Name = "Music" });
                NavigationEntry Genre(string id, string category = "music") => new NavigationEntry
                {
                    Category = category, Name = id, Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=" + id)
                };
                Assert.Equal("c900001", TuneInCategoryArtwork.GenreId(Genre("c900001")));
                Assert.Null(TuneInCategoryArtwork.GenreId(Genre("g900002")));
                Assert.Null(TuneInCategoryArtwork.GenreId(Genre("c900001", "talk")));
                var languageGenre = Genre("c900001", "language");
                languageGenre.ContentCategory = "music";
                Assert.Equal("c900001", TuneInCategoryArtwork.GenreId(languageGenre));
                Assert.Equal(1, TuneInCategoryArtwork.CountGenreFiles());
                Assert.Null(TuneInCategoryArtwork.GenreFile("..\\c900001"));
                Assert.Null(TuneInCategoryArtwork.GenreFile("notes"));
                Assert.Throws<ArgumentException>(() => new TuneInCategoryArtworkService(Moq.Mock.Of<IHttpResultFactory>())
                    .Get(new GetTuneInGenreArtwork { GenreId = "../music" }));
                Assert.Throws<UnauthorizedAccessException>(() => new TuneInCategoryArtworkService(Moq.Mock.Of<IHttpResultFactory>())
                    .Get(new GetTuneInGenreArtwork { GenreId = "c900001" }));
                Assert.Single(typeof(GetTuneInGenreArtwork).GetCustomAttributes(typeof(AuthenticatedAttribute), false));

                var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
                var proxy = (LibraryProxy)library;
                proxy.Root = Folder(1, rootId);
                var rock = Folder(2, directory.Register(snapshot, Genre("c900001")));
                rock.SetProviderId("TuneInCategoryArtwork", "1:music:c900001:3");
                var dubstep = Folder(3, directory.Register(snapshot, Genre("g900002")));
                dubstep.SetProviderId("TuneInCategoryArtwork", "1:music:g900002:3");
                proxy.Descendants = new BaseItem[] { rock, dubstep };
                var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>(), library: library);
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                var revision = TuneInCategoryArtwork.GenreRevision("c900001");
                Assert.Equal("http://127.0.0.1:8096/emby/TuneIn/GenreArtwork/c900001?v=" + revision, rock.GetImagePath(ImageType.Primary, 0));
                Assert.Equal("1:music:c900001:" + revision, rock.GetProviderId("TuneInCategoryArtwork"));
                Assert.EndsWith("/TuneIn/CategoryArtwork/music?v=1", dubstep.GetImagePath(ImageType.Primary, 0));
                Assert.Equal("1:music", dubstep.GetProviderId("TuneInCategoryArtwork"));

                // Removing the pack returns the folder to the Music category image on the next refresh.
                System.IO.File.Delete(card);
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.EndsWith("/TuneIn/CategoryArtwork/music?v=1", rock.GetImagePath(ImageType.Primary, 0));
                Assert.Equal("1:music", rock.GetProviderId("TuneInCategoryArtwork"));
            }
            finally
            {
                TuneInCategoryArtwork.GenreFolder = previous;
                System.IO.Directory.Delete(folder, true);
            }
        }

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

        [Fact]
        public void CategoryStationCountsReadScopedEmbyInventoryAndDeduplicateStationIds()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true, EnableSports = true
            };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("No provider request expected."));
            var snapshot = directory.Snapshot();
            var rootId = directory.Register(snapshot, new NavigationEntry { Category = "talk", Name = "Talk" });
            BaseItem Station(string category, string station, string name) =>
                new MediaBrowser.Controller.Entities.Audio.Audio
                {
                    ExternalId = directory.Register(snapshot, new NavigationEntry
                    { Category = category, StationId = station, Name = name })
                };
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            var proxy = (LibraryProxy)library;
            proxy.Root = Folder(1, rootId);
            proxy.Descendants = new[]
            {
                Station("talk", "s1", "First"), Station("talk", "s1", "Duplicate listing"),
                Station("talk", "s2", "Second"), Station("sports", "s3", "Other category")
            };
            var channel = new TuneInChannel(directory, library: library);
            Assert.Equal(2, channel.CountCategoryStations(rootId, CancellationToken.None));
            Assert.True(proxy.LastInventoryQuery.Recursive);
            Assert.False(proxy.LastInventoryQuery.IsFolder);
            Assert.Equal(TuneInStagedImport.MaxItems + 1, proxy.LastInventoryQuery.Limit);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => channel.CountCategoryStations(rootId, cancel.Token));
            Assert.Throws<InvalidOperationException>(() =>
                channel.CountCategoryStations(proxy.Descendants[0].ExternalId, CancellationToken.None));

            proxy.Descendants = Enumerable.Repeat(proxy.Descendants[0], TuneInStagedImport.MaxItems + 1).ToArray();
            Assert.Throws<InvalidOperationException>(() => channel.CountCategoryStations(rootId, CancellationToken.None));
            proxy.Descendants = new BaseItem[] { new MediaBrowser.Controller.Entities.Audio.Audio { ExternalId = "stale" } };
            Assert.Throws<InvalidOperationException>(() => channel.CountCategoryStations(rootId, CancellationToken.None));
            proxy.Root = null;
            Assert.Equal(0, channel.CountCategoryStations(rootId, CancellationToken.None));
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
            public BaseItem Parent { get; set; }
            public BaseItem[] Descendants { get; set; } = Array.Empty<BaseItem>();
            public List<BaseItem> Updated { get; } = new List<BaseItem>();
            public bool FailNextUpdate { get; set; }
            public InternalItemsQuery LastInventoryQuery { get; private set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetItemList")
                {
                    var query = (InternalItemsQuery)args[0];
                    if (query.ExternalId != null) return Root == null ? Array.Empty<BaseItem>() : new[] { Root };
                    Assert.Equal(new[] { Root.InternalId }, query.ParentIds);
                    if (query.IsFolder == false) LastInventoryQuery = query;
                    return Descendants.Skip(query.StartIndex ?? 0).Take(query.Limit ?? 100).ToArray();
                }
                if (method.Name == "GetItemById")
                    return Parent != null && args[0] is long id && Parent.InternalId == id ? Parent : null;
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
