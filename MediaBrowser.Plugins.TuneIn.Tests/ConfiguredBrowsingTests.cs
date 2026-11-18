using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ConfiguredBrowsingTests
    {
        private static PluginConfiguration Ready() => new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true };
        private static CatalogSelection Selection(string id, params string[] parents) =>
            new CatalogSelection { Id = id, Name = id, AncestorIds = parents.ToList() };
        private static XDocument Opml(string rows) => TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(
            "<opml><head><status>200</status></head><body>" + rows + "</body></opml>"));
        private static string Link(string id, string name = "Choice") =>
            "<outline type='link' text='" + name + "' URL='https://opml.radiotime.com/Browse.ashx?id=" + id + "'/>";
        private static readonly CancellationToken Token = CancellationToken.None;

        [Fact]
        public async Task LegacyConfigurationAndChannelRootNeverCrawlTheCatalogue()
        {
            var config = new PluginConfiguration { Username = "legacy", LatLon = "10,20", EnableTalk = true };
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (request, token) => { calls++; return Task.FromResult(Opml("")); });
            var channel = new TuneInChannel(directory);
            Assert.Empty((await channel.GetChannelItems(new InternalChannelItemQuery(), Token)).Items);
            Assert.Empty((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = "subcat_http://localhost" }, Token)).Items);
            config.SchemaVersion = 1;
            config.ConfigurationCompleted = true;
            config.EnableLanguage = true;
            config.EnableLocalRadio = true;
            var roots = (await channel.GetChannelItems(new InternalChannelItemQuery(), Token)).Items;
            Assert.Equal("Talk", Assert.Single(roots).Name);
            Assert.Equal(0, calls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.GetChannelItemMediaInfo("stream_http://localhost", Token));
        }

        [Fact]
        public async Task AllEightConfiguredCategoriesAppearWithoutRequests()
        {
            var config = Ready();
            config.EnableLocalRadio = config.EnableMusic = config.EnableLanguage = config.EnableLocation = true;
            config.EnableTalk = config.EnableSports = config.EnablePodcasts = config.EnableFavorites = true;
            config.Username = "listener";
            config.LocalCities.Add(Selection("r1"));
            config.Music.Add(Selection("g1"));
            config.Languages.Add(Selection("l1"));
            config.Locations.Add(Selection("r2"));
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected network request."));
            var root = await new TuneInChannel(directory).GetChannelItems(new InternalChannelItemQuery(), Token);
            Assert.Equal(new[] { "Local Radio", "Music", "By Language", "By Location", "Talk", "Sports", "Podcasts", "My Favorites" },
                root.Items.Select(i => i.Name));
        }

        [Fact]
        public async Task SavedSelectionsAreListedLocallyBeforeBrowsingTheirExactNode()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.Add(Selection("g19", "music"));
            config.Music.Add(new CatalogSelection { Id = "g20", Enabled = false });
            var calls = new List<string>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            { calls.Add(request.Url); return Task.FromResult(Opml("")); });
            var channel = new TuneInChannel(directory);
            var root = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery(), Token)).Items);
            var selection = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Id }, Token)).Items);
            Assert.Equal("g19", selection.Name);
            Assert.Empty(calls);
            await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = selection.Id }, Token);
            Assert.Contains("id=g19", Assert.Single(calls));
        }

        [Fact]
        public async Task LocalRadioRequiresOneSourceAndRetainsOtherSourceValues()
        {
            var config = Ready();
            config.EnableLocalRadio = true;
            config.LocalCities.Add(Selection("r10"));
            config.LocalCities.Add(Selection("r20"));
            config.UseCoordinates = true;
            config.LatLon = " 10.00, -20.0 ";
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request);
                return Task.FromResult(Opml("<outline type='audio' text='Station' guide_id='s1'/>"));
            });
            var channel = new TuneInChannel(directory);
            Assert.Empty((await channel.GetChannelItems(new InternalChannelItemQuery(), Token)).Items);
            Assert.Empty(requests);
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            config.LocalRadioSource = "coordinates";
            ConfigurationSnapshot.Validate(config);
            var root = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery(), Token)).Items);
            Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Id }, Token)).Items);
            Assert.Equal("10,-20", Assert.Single(requests).Parameters["latlon"]);
            config.LocalRadioSource = "regions";
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            config.UseCoordinates = false;
            ConfigurationSnapshot.Validate(config);
            var regions = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery(), Token)).Items);
            Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = regions.Id }, Token)).Items);
            Assert.Equal(new[] { "r10", "r20" }, requests.Skip(1).Select(r => r.Parameters["id"]));
            Assert.Equal(" 10.00, -20.0 ", config.LatLon);
            config.EnableLocalRadio = false;
            Assert.Empty((await channel.GetChannelItems(new InternalChannelItemQuery(), Token)).Items);
            Assert.Equal(3, requests.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("NaN,0")]
        [InlineData("Infinity,0")]
        [InlineData("0,-Infinity")]
        [InlineData("91,0")]
        [InlineData("0,181")]
        [InlineData("1,2,3")]
        [InlineData("1,")]
        public void InvalidCoordinatesAreRejectedOnlyWhenOptedIn(string coordinates)
        {
            var config = Ready();
            config.LatLon = coordinates;
            config.UseCoordinates = true;
            config.EnableLocalRadio = true;
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            Assert.False(new ConfigurationSnapshot(config).Enabled("local"));
            config.EnableLocalRadio = false;
            ConfigurationSnapshot.Validate(config);
        }

        [Fact]
        public void CoordinatesNormalizeUsingInvariantCultureAndAcceptBounds()
        {
            var old = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                Assert.True(ConfigurationSnapshot.TryCoordinates("-90.0,180.0", out var value));
                Assert.Equal("-90,180", value);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = old; }
        }

        [Fact]
        public async Task SelectorsAreLazyCacheBranchesAndRejectUnloadedParents()
        {
            var config = Ready();
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request);
                var id = request.Parameters.GetValueOrDefault("id");
                return Task.FromResult(Opml(id == "r0" ? Link("r2", "City") + "<outline type='audio' guide_id='s1'/>"
                    : id == "r2" ? Link("r2", "City") + Link("r3", "District") : Link("g1", "Genre")));
            });
            Assert.Empty(requests);
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SelectAsync("music", "g7", false, Token));
            Assert.Empty(requests);
            var root = await directory.SelectAsync("music", null, false, Token);
            Assert.Equal("music", Assert.Single(requests).Parameters["c"]);
            Assert.Equal(new[] { "music" }, Assert.Single(root.Items).AncestorIds);
            await directory.SelectAsync("music", null, false, Token);
            Assert.Single(requests);
            await directory.SelectAsync("music", null, true, Token);
            Assert.Equal(2, requests.Count);
            var locations = await directory.SelectAsync("location", null, false, Token);
            Assert.True(locations.ContainsStationMetadata);
            var child = await directory.SelectAsync("location", "r2", false, Token);
            Assert.Equal(new[] { "r0", "r2" }, Assert.Single(child.Items).AncestorIds);
            Assert.Equal("City / District", child.Items[0].Path);
            Assert.All(requests, r => Assert.Equal("Browse.ashx", r.Method));
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SelectAsync("presets", null, false, Token));
        }

        [Fact]
        public async Task LanguageExpandsProviderGroupsWithoutSavingThemAsLanguages()
        {
            var config = Ready();
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request);
                var markup = request.Parameters.ContainsKey("filter")
                    ? "<outline type='link' text='Music' URL='http://opml.radiotime.com/Browse.ashx?id=c424724&amp;filter=l122'/>"
                    : "<outline type='link' text='Arabic' URL='http://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l122'/>";
                return Task.FromResult(Opml(markup));
            });
            var language = Assert.Single((await directory.SelectAsync("language", null, false, Token)).Items);
            Assert.True(language.CanExpand && language.CanSelect);
            Assert.Single(requests);
            var group = Assert.Single((await directory.SelectAsync("language", language.Id, false, Token)).Items);
            Assert.Equal("Arabic / Music", group.Path);
            Assert.False(group.CanSelect || group.CanExpand);
            Assert.Equal("l122", requests[1].Parameters["filter"]);
            await directory.SelectAsync("language", language.Id, false, Token);
            Assert.Equal(2, requests.Count);
        }

        [Fact]
        public async Task SelectorChangesInvalidateCacheAndRejectInflightResults()
        {
            var config = Ready();
            var response = new TaskCompletionSource<XDocument>();
            var directory = new TuneInDirectory(() => config, (request, token) => response.Task);
            var operation = directory.SelectAsync("music", null, false, Token);
            config.EnableTalk = true;
            response.SetResult(Opml(Link("g1")));
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SelectAsync("music", "g1", false, Token));
        }

        [Fact]
        public async Task SignedNavigationSurvivesSerializedRestartButRejectsChangesAndTampering()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml("")));
            var id = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", StationId = "s1" });
            Assert.True(id.Length < 6000);
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, config);
                using (var reader = new StringReader(writer.ToString())) config = (PluginConfiguration)serializer.Deserialize(reader);
            }
            var restarted = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected request."));
            Assert.Equal("s1", restarted.Resolve(restarted.Snapshot(), id).StationId);
            var tampered = (id[0] == 'A' ? "B" : "A") + id.Substring(1);
            Assert.Throws<InvalidOperationException>(() => restarted.Resolve(restarted.Snapshot(), tampered));
            Assert.Throws<InvalidOperationException>(() => restarted.Resolve(restarted.Snapshot(), new string('A', 6001)));
            config.EnableTalk = false;
            var channel = new TuneInChannel(restarted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.GetChannelItemMediaInfo(id, Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.GetChannelItems(new InternalChannelItemQuery { FolderId = id }, Token));
        }

        [Fact]
        public async Task RemovedSelectionInvalidatesPreviouslyPlayableStations()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.Add(Selection("g1"));
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected request."));
            var id = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "music", SelectionId = "g1", StationId = "s1" });
            config.Music[0].Enabled = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => new TuneInChannel(directory).GetChannelItemMediaInfo(id, Token));
        }

        [Fact]
        public async Task ValidSignedStationsStillUseTheBasePlaybackResolver()
        {
            BaseExtensions.CryptographyProvider = new TestCryptoProvider();
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected catalogue request."));
            var id = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", StationId = "s1" });
            var client = new Mock<IHttpClient>(MockBehavior.Strict);
            string requested = null;
            client.Setup(c => c.SendAsync(It.IsAny<HttpRequestOptions>(), "GET"))
                .Callback<HttpRequestOptions, string>((request, method) => requested = request.Url)
                .ReturnsAsync(new HttpResponseInfo { Content = new MemoryStream(Encoding.UTF8.GetBytes("https://example.com/live.mp3\n")) });
            var source = Assert.Single(await new TuneInChannel(directory, client.Object, Mock.Of<ILogger>())
                .GetChannelItemMediaInfo(id, Token));
            Assert.Equal("https://example.com/live.mp3", source.Path);
            Assert.StartsWith("https://opml.radiotime.com/Tune.ashx?", requested);
            Assert.Contains("id=s1", requested);
            client.VerifyAll();
        }

        private sealed class TestCryptoProvider : ICryptoProvider
        {
            public Guid GetMD5(ReadOnlySpan<char> value) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(value.ToString())));
            public byte[] ComputeMD5(Stream stream) { using (var hash = MD5.Create()) return hash.ComputeHash(stream); }
            public ReadOnlySpan<byte> ComputeMD5(ReadOnlySpan<byte> value) => MD5.HashData(value);
            public ReadOnlySpan<byte> ComputeSHA1(ReadOnlySpan<byte> value) => SHA1.HashData(value);
        }

        [Fact]
        public async Task MusicAndGeographyRejectParentAndCrossCategoryLinks()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.Add(Selection("g19", "music", "c4"));
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(
                Link("c4", "Parent") + Link("g20", "Child") + Link("r1", "Geography")
                + "<outline type='link' text='Talk' URL='https://opml.radiotime.com/Browse.ashx?c=talk'/>"
                + "<outline key='related'><outline type='audio' guide_id='s99'/></outline>")));
            var snapshot = directory.Snapshot();
            var start = TuneInBrowsing.StartingEntries(snapshot, "music").Single();
            var folder = directory.Register(snapshot, start);
            var result = await new TuneInChannel(directory).GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, Token);
            Assert.Equal("Child", Assert.Single(result.Items).Name);
        }

        [Fact]
        public async Task LanguageMusicUsesSavedGenresAndKeepsLanguageAndGenreScopesAtEveryDepth()
        {
            var config = Ready();
            config.EnableLanguage = config.EnableMusic = true;
            config.Languages.Add(Selection("l122", "lang"));
            config.Music.Add(Selection("g19", "music", "c4"));
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request);
                if (!request.Parameters.ContainsKey("filter"))
                    return Task.FromResult(Opml("<outline type='link' text='English' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l122'/>"));
                if (request.Parameters.ContainsKey("id"))
                {
                    Assert.Contains("l122", request.Parameters["filter"].Split(':'));
                    return Task.FromResult(Opml("<outline type='audio' text='Allowed station' guide_id='s1'/>"
                        + Link("c4", "Parent") + Link("g50", "Deeper")
                        + "<outline type='link' text='Other content' URL='https://opml.radiotime.com/Browse.ashx?c=sports'/>"));
                }
                return Task.FromResult(Opml(Link("c424724", "Music") + Link("c424725", "Talk") + Link("c424726", "Sports")));
            });
            var channel = new TuneInChannel(directory);
            var snapshot = directory.Snapshot();
            var language = directory.Register(snapshot, TuneInBrowsing.StartingEntries(snapshot, "language").Single());
            var hub = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = language }, Token)).Items);
            Assert.Equal("Music", hub.Name);
            var calls = requests.Count;
            var genre = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = hub.Id }, Token)).Items);
            Assert.Equal("g19", genre.Name);
            Assert.Equal(calls, requests.Count);
            var items = (await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = genre.Id }, Token)).Items;
            Assert.Equal(2, items.Count);
            var station = items.Single(i => i.Type == ChannelItemType.Media);
            var entry = directory.Resolve(directory.Snapshot(), station.Id);
            Assert.Equal("l122", entry.SelectionId);
            Assert.Equal("g19", entry.MusicSelectionId);
            Assert.Equal("music", entry.ContentCategory);
            var deeper = items.Single(i => i.Type == ChannelItemType.Folder);
            var nested = (await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = deeper.Id }, Token)).Items;
            Assert.Equal(2, nested.Count);
            config.EnableMusic = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.GetChannelItemMediaInfo(station.Id, Token));
        }

        [Fact]
        public void InvalidAdminConfigurationIsRejectedServerSide()
        {
            var config = Ready();
            config.Languages.Add(Selection("r1"));
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            config.Languages.Clear();
            config.Music.Add(Selection("g1", "r0"));
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            config.Music.Clear();
            config.Music.AddRange(Enumerable.Range(1, 101).Select(i => Selection("g" + i)));
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            config.Music.Clear();
            config.SchemaVersion = 2;
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
        }

        [Fact]
        public void LanguageDescendantsCannotBypassGroupOrSavedMusicAncestry()
        {
            var config = Ready();
            config.EnableLanguage = config.EnableMusic = true;
            config.Languages.Add(Selection("l122"));
            config.Music.Add(Selection("g19"));
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected request."));
            var snapshot = directory.Snapshot();
            foreach (var entry in new[]
            {
                new NavigationEntry { Category = "language", SelectionId = "l122", StationId = "s1" },
                new NavigationEntry { Category = "language", SelectionId = "l122", ContentCategory = "music", StationId = "s1" },
                new NavigationEntry { Category = "language", SelectionId = "l122", ContentCategory = "music",
                    MusicSelectionId = "g54", StationId = "s1" },
                new NavigationEntry { Category = "language", SelectionId = "l122",
                    Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=lang&id=g54") },
                new NavigationEntry { Category = "language", SelectionId = "l122", ContentCategory = "language",
                    Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=lang&id=g54") }
            })
                Assert.Throws<InvalidOperationException>(() => directory.Register(snapshot, entry));
            Assert.Throws<InvalidDataException>(() => directory.Register(snapshot,
                new NavigationEntry { Category = "music", SelectionId = "g19", Name = new string('A', 4096) }));
        }

        [Fact]
        public async Task UnknownLanguagesCannotFallBackToUnfilteredStations()
        {
            var config = Ready();
            config.EnableLanguage = true;
            config.Languages.Add(Selection("l999"));
            var requests = 0;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests++;
                Assert.Equal("lang", request.Parameters["c"]);
                Assert.False(request.Parameters.ContainsKey("filter"));
                return Task.FromResult(Opml("<outline type='link' text='Known' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l1'/>"));
            });
            var id = directory.Register(directory.Snapshot(), TuneInBrowsing.StartingEntries(directory.Snapshot(), "language").Single());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new TuneInChannel(directory).GetChannelItems(new InternalChannelItemQuery { FolderId = id }, Token));
            Assert.Equal(1, requests);
        }

        [Fact]
        public async Task SelectorsDeclareNativeAdministratorAuthorizationAndValidateInput()
        {
            var auth = typeof(GetTuneInSelectors).GetCustomAttribute<AuthenticatedAttribute>();
            Assert.Equal("Admin", auth.Roles);
            var directory = new TuneInDirectory(Ready, (_, __) => throw new Exception("Unexpected request."));
            var service = new TuneInSelectorService(directory);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Get(new GetTuneInSelectors()));
            service.Request = Mock.Of<IRequest>();
            await Assert.ThrowsAsync<ArgumentException>(() => service.Get(new GetTuneInSelectors { Category = "invalid" }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.Get(new GetTuneInSelectors { Category = "location", Parent = "r999" }));
        }

        [Theory]
        [InlineData("http://127.0.0.1/Browse.ashx?id=g1")]
        [InlineData("https://opml.radiotime.com:444/Browse.ashx?id=g1")]
        [InlineData("https://opml.radiotime.com/Other.ashx?id=g1")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=g1&id=g2")]
        public void ArbitraryDestinationsCannotBeStoredInNavigation(string link) =>
            Assert.Throws<InvalidDataException>(() => DirectoryRequest.ParseLink(link));

        [Theory]
        [InlineData("<opml><body/><body/></opml>")]
        [InlineData("<opml><head><status>404</status></head><body/></opml>")]
        [InlineData("<opml><body><unexpected/></body></opml>")]
        public void InvalidCatalogueResponsesFailVisibly(string xml) =>
            Assert.Throws<InvalidDataException>(() => TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(xml)));
    }
}
