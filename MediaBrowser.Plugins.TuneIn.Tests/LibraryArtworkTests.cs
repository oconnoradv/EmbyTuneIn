using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
        public void SettingsPageLibraryArtworkIsServedToRemoteAdministrators()
        {
            var request = new Moq.Mock<IRequest>();
            request.SetupGet(value => value.RemoteIp).Returns(IPAddress.Parse("192.168.1.20"));
            request.SetupGet(value => value.IsSocketConnectionLocal).Returns(false);
            var results = new Moq.Mock<IHttpResultFactory>();
            results.Setup(value => value.GetResult(request.Object, Moq.It.Is<ReadOnlyMemory<byte>>(bytes => bytes.Length > 0), "image/png", null))
                .Returns("ok");
            var service = new TuneInCategoryArtworkService(results.Object) { Request = request.Object };

            Assert.Equal("ok", service.Get(new GetTuneInLibraryArtwork()));
            Assert.Throws<UnauthorizedAccessException>(() => service.Get(new GetTuneInCategoryArtwork { Category = "channel" }));
            var auth = Assert.IsType<AuthenticatedAttribute>(Assert.Single(typeof(GetTuneInLibraryArtwork).GetCustomAttributes(typeof(AuthenticatedAttribute), false)));
            Assert.Equal("Admin", auth.Roles);
        }

        [Fact]
        public void SettingsPagePanelArtworkIsSeparateAndServedToRemoteAdministrators()
        {
            var request = new Moq.Mock<IRequest>();
            request.SetupGet(value => value.RemoteIp).Returns(IPAddress.Parse("192.168.1.20"));
            using var panel = TuneInCategoryArtwork.OpenPanel();
            using var buffer = new System.IO.MemoryStream();
            panel.CopyTo(buffer);
            var png = buffer.ToArray();
            Assert.Equal(1280, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
            Assert.Equal(360, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
            var results = new Moq.Mock<IHttpResultFactory>();
            results.Setup(value => value.GetResult(request.Object, Moq.It.Is<ReadOnlyMemory<byte>>(bytes => bytes.ToArray().SequenceEqual(png)), "image/png", null))
                .Returns("ok");
            var service = new TuneInCategoryArtworkService(results.Object) { Request = request.Object };

            Assert.Equal("ok", service.Get(new GetTuneInPanelArtwork()));
            var auth = Assert.IsType<AuthenticatedAttribute>(Assert.Single(typeof(GetTuneInPanelArtwork).GetCustomAttributes(typeof(AuthenticatedAttribute), false)));
            Assert.Equal("Admin", auth.Roles);
        }

        [Fact]
        public void GenrePackManifestReportsIdentityAndHandlesMissingOrInvalidMetadata()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tunein-manifest-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            var previous = TuneInCategoryArtwork.GenreFolder;
            try
            {
                TuneInCategoryArtwork.GenreFolder = folder;
                Assert.Equal("missing", TuneInCategoryArtwork.ReadPackManifest().Status);
                var path = System.IO.Path.Combine(folder, "tunein-artwork.json");
                System.IO.File.WriteAllText(path, "{\"schemaVersion\":1,\"packName\":\"Cartoon\",\"imageCount\":123}");
                var pack = TuneInCategoryArtwork.ReadPackManifest();
                Assert.Equal("identified", pack.Status);
                Assert.Equal("Cartoon", pack.Name);
                Assert.Equal(123, pack.ImageCount);
                var bytes = new byte[] { 1, 2, 3 };
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "g19.jpg"), bytes);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = Convert.ToHexString(sha.ComputeHash(bytes));
                System.IO.File.WriteAllText(path, "{\"schemaVersion\":1,\"packName\":\"Cartoon\",\"imageCount\":1,\"images\":[{\"id\":\"g19\",\"file\":\"g19.jpg\",\"sha256\":\"" + hash + "\"}]}");
                pack = TuneInCategoryArtwork.ReadPackManifest();
                Assert.Equal("identified", pack.Status);
                Assert.Equal(1, pack.MatchedCount);
                Assert.Equal(0, pack.AddedCount);
                System.IO.File.WriteAllText(path, "{\"schemaVersion\":2,\"packName\":\"Cartoon\",\"imageCount\":123}");
                Assert.Equal("invalid", TuneInCategoryArtwork.ReadPackManifest().Status);
                System.IO.File.WriteAllText(path, "{invalid");
                Assert.Equal("invalid", TuneInCategoryArtwork.ReadPackManifest().Status);
            }
            finally
            {
                TuneInCategoryArtwork.GenreFolder = previous;
                System.IO.Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void ScopedPackStatusCountsMatchedModifiedAndAddedCards()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tunein-status-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            var previous = TuneInCategoryArtwork.LanguageFolder;
            try
            {
                TuneInCategoryArtwork.LanguageFolder = folder;
                var matching = new byte[] { 1, 2, 3 };
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "l122.jpg"), matching);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "l123.png"), new byte[] { 4 });
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "l124.jpg"), new byte[] { 5 });
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "r101227.jpg"), new byte[] { 6 });
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = Convert.ToHexString(sha.ComputeHash(matching)).ToLowerInvariant();
                var manifest = System.IO.Path.Combine(folder, "tunein-artwork.json");
                System.IO.File.WriteAllText(manifest, "{\"schemaVersion\":1,\"scope\":\"languages\",\"packName\":\"Test\",\"imageCount\":2,\"images\":["
                    + "{\"id\":\"l122\",\"file\":\"l122.jpg\",\"sha256\":\"" + hash + "\"},"
                    + "{\"id\":\"l123\",\"file\":\"l123.png\",\"sha256\":\"" + hash + "\"}]}");
                var status = TuneInCategoryArtwork.ReadPackStatus("languages");
                Assert.Equal("identified", status.Status);
                Assert.Equal(3, status.InstalledCount);
                Assert.Equal(1, status.MatchedCount);
                Assert.Equal(1, status.ModifiedCount);
                Assert.Equal(1, status.AddedCount);
                System.IO.File.Delete(System.IO.Path.Combine(folder, "l122.jpg"));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "l122.png"), matching);
                status = TuneInCategoryArtwork.ReadPackStatus("languages");
                Assert.Equal(0, status.MatchedCount);
                Assert.Equal(2, status.ModifiedCount);
                Assert.Equal(1, status.AddedCount);
                System.IO.File.WriteAllText(manifest, "{\"schemaVersion\":1,\"scope\":\"locations\",\"packName\":\"Wrong\",\"imageCount\":0,\"images\":[]}");
                Assert.Equal("invalid", TuneInCategoryArtwork.ReadPackStatus("languages").Status);
                System.IO.File.WriteAllText(manifest, "{\"schemaVersion\":1,\"scope\":\"languages\",\"packName\":\"Bad\",\"imageCount\":1,\"images\":[{\"id\":\"l122\",\"file\":\"../l122.jpg\",\"sha256\":\"" + hash + "\"}]}");
                Assert.Equal("invalid", TuneInCategoryArtwork.ReadPackStatus("languages").Status);
                System.IO.File.WriteAllText(manifest, "{\"schemaVersion\":1,\"packName\":\"Legacy\"}");
                Assert.Equal("invalid", TuneInCategoryArtwork.ReadPackStatus("languages").Status);
                System.IO.File.Delete(manifest);
                status = TuneInCategoryArtwork.ReadPackStatus("languages");
                Assert.Equal("missing", status.Status);
                Assert.Equal(3, status.InstalledCount);
                Assert.Null(status.AddedCount);
                TuneInCategoryArtwork.LanguageFolder = null;
                Assert.Equal("unavailable", TuneInCategoryArtwork.ReadPackStatus("languages").Status);
            }
            finally
            {
                TuneInCategoryArtwork.LanguageFolder = previous;
                System.IO.Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void LocationPackAcceptsLargeManifestButStillValidatesEntriesAndSize()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tunein-large-manifest-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            var previous = TuneInCategoryArtwork.LocationFolder;
            try
            {
                TuneInCategoryArtwork.LocationFolder = folder;
                var path = System.IO.Path.Combine(folder, "tunein-artwork.json");
                var image = new byte[] { 1, 2, 3 };
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "r100000.jpg"), image);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = Convert.ToHexString(sha.ComputeHash(image)).ToLowerInvariant();
                var entries = Enumerable.Range(100000, 10000).Select(id =>
                    "{\"id\":\"r" + id + "\",\"file\":\"r" + id + ".jpg\",\"sha256\":\"" + hash + "\"}");
                var json = "{\"schemaVersion\":1,\"scope\":\"locations\",\"packName\":\"Futuristic\","
                    + "\"imageCount\":10000,\"images\":[" + string.Join(",", entries) + "]}";
                System.IO.File.WriteAllText(path, json);
                Assert.True(new System.IO.FileInfo(path).Length > 1024 * 1024);
                var status = TuneInCategoryArtwork.ReadPackStatus("locations");
                Assert.Equal("identified", status.Status);
                Assert.Equal("Futuristic", status.Name);
                Assert.Equal(10000, status.ImageCount);
                Assert.Equal(1, status.MatchedCount);

                System.IO.File.WriteAllText(path, json.Replace("\"file\":\"r100001.jpg\"", "\"file\":\"../r100001.jpg\""));
                Assert.Equal("invalid", TuneInCategoryArtwork.ReadPackStatus("locations").Status);

                var padded = json.PadRight(TuneInCategoryArtwork.MaxPackManifestBytes, ' ');
                System.IO.File.WriteAllText(path, padded);
                Assert.Equal(TuneInCategoryArtwork.MaxPackManifestBytes, new System.IO.FileInfo(path).Length);
                Assert.Equal("identified", TuneInCategoryArtwork.ReadPackStatus("locations").Status);
                System.IO.File.AppendAllText(path, " ");
                Assert.Equal("invalid", TuneInCategoryArtwork.ReadPackStatus("locations").Status);
            }
            finally
            {
                TuneInCategoryArtwork.LocationFolder = previous;
                System.IO.Directory.Delete(folder, true);
            }
        }

        [Theory]
        [InlineData("language", "languages", "l122")]
        [InlineData("location", "locations", "r101227")]
        public void ScopedArtworkMigratesOnAdditionReplacementAndRemoval(string category, string scope, string id)
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tunein-scoped-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            var previousLanguage = TuneInCategoryArtwork.LanguageFolder;
            var previousLocation = TuneInCategoryArtwork.LocationFolder;
            try
            {
                TuneInCategoryArtwork.LanguageFolder = folder;
                TuneInCategoryArtwork.LocationFolder = folder;
                var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableLanguage = true, EnableLocation = true };
                var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
                var snapshot = directory.Snapshot();
                var rootId = directory.Register(snapshot, new NavigationEntry { Category = category, Name = category });
                var request = category == "language"
                    ? DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=lang&filter=" + id)
                    : DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=" + id);
                var entry = new NavigationEntry { Category = category, Request = request, Name = id };
                var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
                var proxy = (LibraryProxy)library;
                proxy.Root = Folder(1, rootId);
                var item = Folder(2, directory.Register(snapshot, entry));
                proxy.Descendants = new BaseItem[] { item };
                var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>(), library: library);
                var card = System.IO.Path.Combine(folder, id + ".jpg");
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.EndsWith("/TuneIn/CategoryArtwork/" + category + "?v=1", item.GetImagePath(ImageType.Primary, 0));
                System.IO.File.WriteAllBytes(card, new byte[] { 1 });
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                var first = item.GetImagePath(ImageType.Primary, 0);
                Assert.Contains("/TuneIn/PackArtwork/" + scope + "/" + id + "?v=", first);
                Assert.Throws<UnauthorizedAccessException>(() => new TuneInCategoryArtworkService(Moq.Mock.Of<IHttpResultFactory>())
                    .Get(new GetTuneInPackArtwork { Scope = scope, Id = id }));
                var httpRequest = new Moq.Mock<IRequest>();
                httpRequest.SetupGet(value => value.RemoteIp).Returns(IPAddress.Loopback);
                httpRequest.SetupGet(value => value.IsSocketConnectionLocal).Returns(true);
                var factory = new Moq.Mock<IHttpResultFactory>();
                factory.Setup(value => value.GetResult(httpRequest.Object, Moq.It.IsAny<ReadOnlyMemory<byte>>(), "image/jpeg", null))
                    .Returns("ok");
                Assert.Equal("ok", new TuneInCategoryArtworkService(factory.Object) { Request = httpRequest.Object }
                    .Get(new GetTuneInPackArtwork { Scope = scope, Id = id }));
                var modified = System.IO.File.GetLastWriteTimeUtc(card);
                System.IO.File.WriteAllBytes(card, new byte[] { 2 });
                System.IO.File.SetLastWriteTimeUtc(card, modified);
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.NotEqual(first, item.GetImagePath(ImageType.Primary, 0));
                System.IO.File.Delete(card);
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.EndsWith("/TuneIn/CategoryArtwork/" + category + "?v=1", item.GetImagePath(ImageType.Primary, 0));
                Assert.Null(TuneInCategoryArtwork.PackFile(scope, "..\\l122"));
                Assert.Null(TuneInCategoryArtwork.PackFile("invalid", id));
                Assert.Throws<ArgumentException>(() => new TuneInCategoryArtworkService(Moq.Mock.Of<IHttpResultFactory>())
                    .Get(new GetTuneInPackArtwork { Scope = "../languages", Id = id }));
                Assert.Single(typeof(GetTuneInPackArtwork).GetCustomAttributes(typeof(AuthenticatedAttribute), false));
            }
            finally
            {
                TuneInCategoryArtwork.LanguageFolder = previousLanguage;
                TuneInCategoryArtwork.LocationFolder = previousLocation;
                System.IO.Directory.Delete(folder, true);
            }
        }

        [Fact]
        public async Task LanguageRootAndMusicHubsKeepTheirArtworkScopes()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tunein-scopes-" + Guid.NewGuid().ToString("N"));
            var genres = System.IO.Path.Combine(root, "genres");
            var languages = System.IO.Path.Combine(root, "languages");
            var locations = System.IO.Path.Combine(root, "locations");
            System.IO.Directory.CreateDirectory(genres);
            System.IO.Directory.CreateDirectory(languages);
            System.IO.Directory.CreateDirectory(locations);
            var oldGenre = TuneInCategoryArtwork.GenreFolder;
            var oldLanguage = TuneInCategoryArtwork.LanguageFolder;
            var oldLocation = TuneInCategoryArtwork.LocationFolder;
            try
            {
                TuneInCategoryArtwork.GenreFolder = genres;
                TuneInCategoryArtwork.LanguageFolder = languages;
                TuneInCategoryArtwork.LocationFolder = locations;
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(languages, "l122.jpg"), new byte[] { 1 });
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(languages, "l216.jpg"), new byte[] { 1 });
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(genres, "g19.jpg"), new byte[] { 1 });
                var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableLanguage = true };
                var directory = new TuneInDirectory(() => config, (request, token) =>
                {
                    var rows = request.Parameters.GetValueOrDefault("filter") == null
                        ? "<outline type='link' text='Arabic' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l122'/>"
                          + "<outline type='link' text='Bashkir' URL='https://opml.radiotime.com/Browse.ashx?id=c424724&amp;filter=l216'/>"
                        : "<outline type='link' text='Music' URL='https://opml.radiotime.com/Browse.ashx?id=c424724&amp;filter=l122'/>";
                    return Task.FromResult(XDocument.Parse("<opml><body>" + rows + "</body></opml>"));
                });
                var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>());
                var snapshot = directory.Snapshot();
                var rootId = directory.Register(snapshot, new NavigationEntry { Category = "language", Name = "Language" });
                var items = (await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = rootId }, CancellationToken.None)).Items;
                Assert.Equal(2, items.Count);
                Assert.Contains("/PackArtwork/languages/l122?", items[0].ImageUrl);
                Assert.Contains("/PackArtwork/languages/l216?", items[1].ImageUrl);
                var bashkir = directory.Resolve(snapshot, items[1].Id);
                Assert.Equal("l216", bashkir.LanguageArtworkId);
                var hub = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = items[0].Id }, CancellationToken.None)).Items);
                Assert.DoesNotContain("/PackArtwork/languages/", hub.ImageUrl);
                var genre = new NavigationEntry
                {
                    Category = "language", ContentCategory = "music",
                    Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=g19&filter=l122")
                };
                Assert.Equal(("genres", "g19"), TuneInCategoryArtwork.ArtworkFor(genre));
                genre.StationId = "s1";
                Assert.Equal((null, null), TuneInCategoryArtwork.ArtworkFor(genre));
                Assert.Null(TuneInCategoryArtwork.PackFile("locations", "l122"));
                Assert.Null(TuneInCategoryArtwork.PackFile("genres", "l122"));
            }
            finally
            {
                TuneInCategoryArtwork.GenreFolder = oldGenre;
                TuneInCategoryArtwork.LanguageFolder = oldLanguage;
                TuneInCategoryArtwork.LocationFolder = oldLocation;
                System.IO.Directory.Delete(root, true);
            }
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
        public void StartupReplacesOutdatedLibraryArtworkWithoutAnImport()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true };
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            var proxy = (LibraryProxy)library;
            var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>(), library: library);

            Assert.False(channel.UpdateLibraryArtwork(CancellationToken.None));
            var item = new Channel { InternalId = 10, Name = "TuneIn" };
            item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "old.png" }, 0);
            item.SetProviderId("TuneInLibraryArtwork", "1");
            proxy.Channels = new BaseItem[] { new Channel { InternalId = 11, Name = "Other" }, item };

            Assert.True(channel.UpdateLibraryArtwork(CancellationToken.None));
            Assert.True(channel.UpdateLibraryArtwork(CancellationToken.None));
            Assert.Single(proxy.Updated, updated => updated == item);
            Assert.EndsWith("/TuneIn/CategoryArtwork/channel?v=2", item.GetImagePath(ImageType.Primary, 0));
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
                Assert.Equal("http://127.0.0.1:8096/emby/TuneIn/GenreArtwork/c900001?v=" + revision + "&packRevision=1", rock.GetImagePath(ImageType.Primary, 0));
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

        [Theory]
        [InlineData("music", "genres", "g2698", false)]
        [InlineData("language", "genres", "g2698", true)]
        [InlineData("language", "languages", "l122", false)]
        [InlineData("location", "locations", "r101227", false)]
        public void PackRevisionReappliesExistingCardsOnce(string category, string scope, string id, bool languageGenre)
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tunein-art-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            var oldGenre = TuneInCategoryArtwork.GenreFolder;
            var oldLanguage = TuneInCategoryArtwork.LanguageFolder;
            var oldLocation = TuneInCategoryArtwork.LocationFolder;
            try
            {
                TuneInCategoryArtwork.GenreFolder = scope == "genres" ? folder : null;
                TuneInCategoryArtwork.LanguageFolder = scope == "languages" ? folder : null;
                TuneInCategoryArtwork.LocationFolder = scope == "locations" ? folder : null;
                var card = System.IO.Path.Combine(folder, id + ".jpg");
                System.IO.File.WriteAllBytes(card, new byte[] { 1, 2, 3 });
                var config = new PluginConfiguration
                {
                    SchemaVersion = 1, ConfigurationCompleted = true,
                    EnableMusic = true, EnableLanguage = true, EnableLocation = true
                };
                var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected provider request."));
                var snapshot = directory.Snapshot();
                var rootId = directory.Register(snapshot, new NavigationEntry { Category = category, Name = category });
                var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
                var proxy = (LibraryProxy)library;
                proxy.Root = Folder(1, rootId);
                proxy.Root.SetProviderId("TuneInCategoryArtwork", "1:" + category);
                var request = scope == "languages"
                    ? DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=lang&filter=" + id)
                    : DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=" + id);
                var item = Folder(2, directory.Register(snapshot, new NavigationEntry
                {
                    Category = category, ContentCategory = languageGenre ? "music" : null, Name = id, Request = request
                }));
                var version = "1:" + category + ":" + (scope == "genres" ? "" : scope + ":")
                    + id + ":" + TuneInCategoryArtwork.PackRevision(scope, id);
                item.SetProviderId("TuneInCategoryArtwork", version);
                if (scope == "genres") item.SetProviderId("TuneInFuturisticSpotlightArtworkTest", version + ":1");
                var fallback = Folder(3, directory.Register(snapshot, new NavigationEntry
                {
                    Category = category, Name = "Fallback",
                    Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?"
                        + (category == "language" ? "c=lang&filter=l123"
                            : "id=" + (category == "location" ? "r101228" : "g4144")))
                }));
                fallback.SetProviderId("TuneInCategoryArtwork", "1:" + category);
                proxy.Descendants = new BaseItem[] { item, fallback };
                var channel = new TuneInChannel(directory, serverHost: DispatchProxy.Create<IServerApplicationHost, HostProxy>(), library: library);

                proxy.FailNextUpdate = true;
                Assert.Throws<InvalidOperationException>(() => channel.UpdateCategoryArtwork(rootId, CancellationToken.None));
                Assert.True(string.IsNullOrEmpty(item.GetProviderId("TuneInPackArtworkRevision")));
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.Same(item, Assert.Single(proxy.Updated));
                Assert.Equal(version, item.GetProviderId("TuneInCategoryArtwork"));
                Assert.Equal(version + ":1", item.GetProviderId("TuneInPackArtworkRevision"));
                Assert.EndsWith("&packRevision=1", item.GetImagePath(ImageType.Primary, 0));
                Assert.Equal("old.png", fallback.GetImagePath(ImageType.Primary, 0));
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.Single(proxy.Updated);
                System.IO.File.WriteAllBytes(card, new byte[] { 4, 5, 6 });
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.Equal(2, proxy.Updated.Count);
                Assert.NotEqual(version, item.GetProviderId("TuneInCategoryArtwork"));
                System.IO.File.Delete(card);
                channel.UpdateCategoryArtwork(rootId, CancellationToken.None);
                Assert.Equal(3, proxy.Updated.Count);
                Assert.EndsWith("/TuneIn/CategoryArtwork/" + category + "?v=1", item.GetImagePath(ImageType.Primary, 0));
            }
            finally
            {
                TuneInCategoryArtwork.GenreFolder = oldGenre;
                TuneInCategoryArtwork.LanguageFolder = oldLanguage;
                TuneInCategoryArtwork.LocationFolder = oldLocation;
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
            public BaseItem[] Channels { get; set; } = Array.Empty<BaseItem>();
            public List<BaseItem> Updated { get; } = new List<BaseItem>();
            public bool FailNextUpdate { get; set; }
            public InternalItemsQuery LastInventoryQuery { get; private set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetItemList")
                {
                    var query = (InternalItemsQuery)args[0];
                    if (query.IncludeItemTypes?.Contains(nameof(Channel)) == true) return Channels;
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
