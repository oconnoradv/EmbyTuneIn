using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Channels;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Text;
using System.Reflection;
using MediaBrowser.Model.Services;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ConfigurationAndNavigationTests
    {
        private static PluginConfiguration Ready() => new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true };
        private static CatalogSelection Selection(string id) => new CatalogSelection { Id = id, Name = id };
        private static XDocument Opml(string rows) => XDocument.Parse("<opml><head><status>200</status></head><body>" + rows + "</body></opml>");

        [Theory]
        [InlineData("<opml><body/><body/></opml>")]
        [InlineData("<opml><body><unexpected><outline/></unexpected></body></opml>")]
        [InlineData("<opml><body><outline><unexpected/></outline></body></opml>")]
        public void CatalogueSchemaRejectsAmbiguousBodiesAndUnknownRowStructures(string xml)
        {
            Assert.Throws<System.IO.InvalidDataException>(() =>
                TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(xml)));
        }

        [Theory]
        [InlineData("4.9.1.80", false)]
        [InlineData("4.9.1.81", false)]
        [InlineData("4.9.1.90", true)]
        [InlineData("4.9.2.6", false)]
        [InlineData("4.9.2.7", true)]
        [InlineData("4.9.3.0", true)]
        public void VulnerableHostVersionsCannotLoadThePlugin(string version, bool supported) =>
            Assert.Equal(supported, Plugin.SupportsServerVersion(Version.Parse(version)));

        [Fact]
        public void XmlEntityExpansionAndHttp200ApiFaultsAreRejected()
        {
            Assert.Throws<System.Xml.XmlException>(() => TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(
                "<!DOCTYPE opml [<!ENTITY x SYSTEM 'file:///secret'>]><opml><body>&x;</body></opml>")));
            Assert.Throws<System.IO.InvalidDataException>(() => TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(
                "<opml><head><status>200</status><fault>Invalid method</fault></head><body/></opml>")));
            Assert.Throws<System.IO.InvalidDataException>(() => TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(
                "<opml><head><status>404</status></head><body/></opml>")));
        }

        [Theory]
        [InlineData(31, false)]
        [InlineData(32, true)]
        public void CatalogueNestingIsBoundedBeforeRecursiveNavigation(int levels, bool rejected)
        {
            var xml = "<opml><head><status>200</status></head><body>"
                + string.Concat(Enumerable.Repeat("<outline>", levels))
                + string.Concat(Enumerable.Repeat("</outline>", levels)) + "</body></opml>";
            if (rejected)
                Assert.Throws<System.IO.InvalidDataException>(() => TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(xml)));
            else
                Assert.NotNull(TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(xml)));
        }

        [Fact]
        public async Task RelayRejectsRemoteRequestsBeforeLookingUpAnyStation()
        {
            var service = new TuneInService { Request = DispatchProxy.Create<IRequest, RemoteRequestProxy>() };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Get(new GetTuneInRelay { Id = "arbitrary" }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Get(new GetTuneInArtwork { Id = "arbitrary" }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Get(new GetTuneInBranding()));
        }

        public class RemoteRequestProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_IsSocketConnectionLocal") return false;
                if (method.Name == "get_RemoteIp") return System.Net.IPAddress.Parse("8.8.8.8");
                throw new InvalidOperationException("Unexpected request access before authorization.");
            }
        }

        [Fact]
        public async Task LegacyAndUnconfiguredRootsMakeZeroRequestsEvenWithStaleIds()
        {
            var config = new PluginConfiguration { Username = "legacy", LatLon = "10,20", EnableTalk = true };
            var requests = 0;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            { requests++; return Task.FromResult(Opml("")); });
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            Assert.Empty((await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None)).Items);
            Assert.Empty((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = "subcat_http://127.0.0.1" }, CancellationToken.None)).Items);
            Assert.Equal(0, requests);
        }

        [Fact]
        public async Task ConfiguredRootIsLocalAndDisabledOrIncompleteCategoriesAreAbsent()
        {
            var config = Ready();
            config.EnableTalk = true;
            config.EnableLanguage = true;
            config.EnableLocalRadio = true;
            config.EnableFavorites = true;
            config.EnableMusic = true;
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected API request"));
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            var root = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
            Assert.Equal("Talk", Assert.Single(root.Items).Name);
            Assert.Throws<InvalidOperationException>(() => directory.Resolve(directory.Snapshot(), "stream_http://127.0.0.1"));
        }

        [Theory]
        [InlineData("90,180", true)]
        [InlineData("-90,-180", true)]
        [InlineData("1.5,2.75", true)]
        [InlineData("91,0", false)]
        [InlineData("0,-181", false)]
        [InlineData("NaN,0", false)]
        [InlineData("Infinity,0", false)]
        [InlineData("1,2,3", false)]
        [InlineData("", false)]
        public void CoordinatesAreFiniteRangeCheckedInvariantPairs(string value, bool valid) =>
            Assert.Equal(valid, ConfigurationSnapshot.TryCoordinates(value, out _));

        [Fact]
        public async Task LocalCoordinatesPersistOnEveryDescendantAndUnrelatedLinksAreRemoved()
        {
            var config = Ready();
            config.EnableLocalRadio = true;
            config.UseCoordinates = true;
            config.LatLon = "1.5,-2.75";
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request);
                return Task.FromResult(Opml("<outline type='link' text='Local branch' URL='http://opml.radiotime.com/Browse.ashx?id=r123'/>"
                    + "<outline key='related'><outline type='link' text='World' URL='http://opml.radiotime.com/Browse.ashx?id=r0'/></outline>"));
            });
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            var root = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
            var first = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Items[0].Id }, CancellationToken.None);
            await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = Assert.Single(first.Items).Id }, CancellationToken.None);
            Assert.Equal(2, requests.Count);
            Assert.All(requests, r => Assert.Equal("1.5,-2.75", r.Parameters["latlon"]));
            Assert.All(requests, r => Assert.False(r.Parameters.ContainsKey("username")));
        }

        [Fact]
        public async Task LocalCitiesDeduplicateStationsAndNeverUseIpFallback()
        {
            var config = Ready();
            config.EnableLocalRadio = true;
            config.LocalCities.AddRange(new[] { Selection("r1"), Selection("r2") });
            config.LocalCities.Add(new CatalogSelection { Id = "r3", Enabled = false });
            var requests = new List<string>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request.Parameters["id"]);
                Assert.False(request.Parameters.ContainsKey("c"));
                Assert.False(request.Parameters.ContainsKey("latlon"));
                return Task.FromResult(Opml("<outline type='audio' text='Station' guide_id='s1'/>"));
            });
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            var root = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
            var stations = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Items[0].Id }, CancellationToken.None);
            Assert.Single(stations.Items);
            Assert.Equal(new[] { "r1", "r2" }, requests);
        }

        [Fact]
        public void LanguageFilterIsCarriedAndGenericHubsAreNormalized()
        {
            var snapshot = new ConfigurationSnapshot(Ready());
            var entry = new NavigationEntry { Category = "language", SelectionId = "l122" };
            var link = DirectoryRequest.ParseLink("http://opml.radiotime.com/Browse.ashx?id=c424724&filter=l999");
            var scoped = TuneInChannel.ScopedRequest(snapshot, entry, link);
            Assert.Equal("music", scoped.Parameters["c"]);
            Assert.Equal("l122", scoped.Parameters["filter"]);
            Assert.False(scoped.Parameters.ContainsKey("id"));
            var page = DirectoryRequest.ParseLink("http://opml.radiotime.com/Browse.ashx?id=g22&filter=s:popular&offset=50");
            var next = TuneInChannel.ScopedRequest(snapshot, entry, page);
            Assert.Equal("s:popular:l122", next.Parameters["filter"]);
            Assert.Equal("50", next.Parameters["offset"]);
            Assert.Throws<System.IO.InvalidDataException>(() => TuneInChannel.ScopedRequest(snapshot, entry,
                DirectoryRequest.ParseLink("http://opml.radiotime.com/Browse.ashx?id=g22&filter=l122,l999")));
        }

        [Theory]
        [InlineData("s:~A", "s%3A~A")]
        [InlineData("s:~#", "s%3A~%23")]
        [InlineData("s:~É", "s%3A~%C3%89")]
        [InlineData("p:~湘", "p%3A~%E6%B9%98")]
        public void AlphabeticCatalogueFiltersAreEncodedAndPreserveLanguageScope(string filter, string encoded)
        {
            var link = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=c57941&filter=" + encoded);
            Assert.Equal(filter, link.Parameters["filter"]);
            Assert.Contains("filter=" + encoded, link.Url);
            var scoped = TuneInChannel.ScopedRequest(new ConfigurationSnapshot(Ready()),
                new NavigationEntry { Category = "language", SelectionId = "l122" }, link);
            Assert.Equal(filter + ":l122", scoped.Parameters["filter"]);
        }

        [Theory]
        [InlineData("filter=s%3A~%26username%3Dsecret")]
        [InlineData("filter=s%3A~%0A")]
        [InlineData("filter=s%3A~%2F")]
        [InlineData("filter=s%3A~AB")]
        [InlineData("id=s%3A~A")]
        public void AlphabeticFilterSupportRejectsUnsafeOrUnexpectedParameters(string query) =>
            Assert.Throws<System.IO.InvalidDataException>(() =>
                DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=c57941&" + query));

        [Theory]
        [InlineData("music", "c424724")]
        [InlineData("talk", "c424725")]
        [InlineData("sports", "c424726")]
        public async Task LanguageHubsRespectGroupSwitchesAndDescendantsKeepTheirScope(string group, string hub)
        {
            var config = Ready();
            config.EnableLanguage = true;
            config.Languages.Add(Selection("l122"));
            config.EnableMusic = true;
            config.Music.Add(Selection("g19"));
            config.EnableTalk = true;
            config.EnableSports = true;
            var requests = 0;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests++;
                if (!request.Parameters.ContainsKey("filter"))
                    return Task.FromResult(Opml("<outline type='link' text='Language' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l122'/>"));
                Assert.EndsWith("l122", request.Parameters["filter"]);
                if (request.Parameters.GetValueOrDefault("c") == "lang")
                    return Task.FromResult(Opml("<outline type='link' text='Hub' URL='https://opml.radiotime.com/Browse.ashx?id=" + hub + "'/>"
                        + "<outline type='link' text='Classifier' URL='https://opml.radiotime.com/Browse.ashx?c=" + group + "'/>"
                        + "<outline type='link' text='Other enabled group' URL='https://opml.radiotime.com/Browse.ashx?c="
                        + (group == "sports" ? "talk" : "sports") + "'/>"));
                return Task.FromResult(Opml("<outline type='link' text='Descendant' URL='https://opml.radiotime.com/Browse.ashx?id=g22'/>"
                    + "<outline type='audio' text='Station' guide_id='s1'/>"));
            });
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            var snapshot = directory.Snapshot();
            var selected = directory.Register(snapshot, TuneInChannel.StartingEntries(snapshot, "language").Single());
            var hubs = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = selected }, CancellationToken.None);
            Assert.Equal(3, hubs.Items.Count);
            var children = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = hubs.Items[0].Id }, CancellationToken.None);
            if (group == "music")
            {
                var genre = Assert.Single(children.Items);
                Assert.Equal("g19", directory.Resolve(directory.Snapshot(), genre.Id).MusicSelectionId);
                children = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = genre.Id }, CancellationToken.None);
            }
            Assert.Equal(2, children.Items.Count);
            var restarted = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected request"));
            Assert.All(children.Items, item => Assert.Equal(group, restarted.Resolve(restarted.Snapshot(), item.Id).ContentCategory));
            var descendants = children.Items.Select(item => restarted.Resolve(restarted.Snapshot(), item.Id)).ToList();

            if (group == "music") config.EnableMusic = false;
            if (group == "talk") config.EnableTalk = false;
            if (group == "sports") config.EnableSports = false;
            snapshot = directory.Snapshot();
            selected = directory.Register(snapshot, TuneInChannel.StartingEntries(snapshot, "language").Single());
            Assert.Equal("Other enabled group", Assert.Single((await channel.GetChannelItems(
                new InternalChannelItemQuery { FolderId = selected }, CancellationToken.None)).Items).Name);
            var before = requests;
            foreach (var stale in descendants)
            {
                var currentId = directory.Register(snapshot, stale);
                await Assert.ThrowsAsync<InvalidOperationException>(() => channel.GetChannelItems(
                    new InternalChannelItemQuery { FolderId = currentId }, CancellationToken.None));
                Assert.Throws<InvalidOperationException>(() => directory.Resolve(snapshot, currentId));
            }
            Assert.Equal(before, requests);
        }

        [Theory]
        [InlineData("l122")]
        [InlineData("l99")]
        public async Task LanguageMusicUsesOnlySavedSelectionsAndPreservesBothScopesAtEveryDepth(string language)
        {
            var config = Ready();
            config.EnableLanguage = true;
            config.Languages.AddRange(new[] { Selection("l122"), Selection("l99") });
            config.EnableMusic = true;
            config.EnableTalk = true;
            config.Music.AddRange(new[] { Selection("g19"), Selection("c57944"),
                new CatalogSelection { Id = "g54", Enabled = false } });
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request.Copy());
                if (!request.Parameters.ContainsKey("filter"))
                    return Task.FromResult(Opml("<outline type='link' text='English' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l122'/>"
                        + "<outline type='link' text='Other language' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l99'/>"));
                Assert.EndsWith(language, request.Parameters["filter"]);
                Assert.False(request.Parameters.ContainsKey("c"));
                Assert.Contains(request.Parameters["id"], new[] { "g19", "g115", "c57944" });
                return Task.FromResult(Opml("<outline type='audio' text='Station' guide_id='s1' image='https://cdn-profiles.tunein.com/logo.png'/>"
                    + "<outline type='link' text='Subgenre' URL='https://opml.radiotime.com/Browse.ashx?id=g115&amp;filter=s:l999'/>"
                    + "<outline type='link' text='More' key='nextStations' URL='https://opml.radiotime.com/Browse.ashx?id="
                    + request.Parameters["id"] + "&amp;offset=50&amp;filter=s:l999'/>"
                    + "<outline type='link' text='Global music' URL='https://opml.radiotime.com/Browse.ashx?c=music'/>"
                    + "<outline type='link' text='Talk escape' URL='https://opml.radiotime.com/Browse.ashx?id=c424725'/>"
                    + "<outline key='related'><outline type='link' text='Other genre' URL='https://opml.radiotime.com/Browse.ashx?id=g54'/></outline>"));
            });
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            var snapshot = directory.Snapshot();
            var hub = directory.Register(snapshot, new NavigationEntry
            {
                Category = "language",
                SelectionId = language,
                ContentCategory = "music",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=music&filter=" + language)
            });
            var genres = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = hub }, CancellationToken.None);
            Assert.Equal(new[] { "g19", "c57944" }, genres.Items.Select(item => item.Name));
            Assert.Empty(requests);
            var page = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = genres.Items[0].Id }, CancellationToken.None);
            Assert.Equal(new[] { "Station", "Subgenre", "More" }, page.Items.Select(item => item.Name));
            var child = await channel.GetChannelItems(new InternalChannelItemQuery
            { FolderId = page.Items.Single(item => item.Name == "Subgenre").Id }, CancellationToken.None);
            await channel.GetChannelItems(new InternalChannelItemQuery
            { FolderId = page.Items.Single(item => item.Name == "More").Id }, CancellationToken.None);
            await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = genres.Items[1].Id }, CancellationToken.None);
            Assert.Equal(5, requests.Count);
            Assert.Contains(requests, request => request.Parameters.GetValueOrDefault("offset") == "50"
                && request.Parameters["filter"] == "s:" + language);
            var restarted = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected API request"));
            var descendants = page.Items.Concat(child.Items).Select(item => restarted.Resolve(restarted.Snapshot(), item.Id)).ToList();
            foreach (var descendant in descendants)
            {
                Assert.Equal(language, descendant.SelectionId);
                Assert.Equal("g19", descendant.MusicSelectionId);
                Assert.Equal("music", descendant.ContentCategory);
            }
            var artworkId = page.Items[0].ImageUrl.Split('/').Last();
            Assert.Equal("g19", restarted.Resolve(restarted.Snapshot(), artworkId).MusicSelectionId);
            config.Music[0].Enabled = false;
            snapshot = directory.Snapshot();
            foreach (var descendant in descendants)
            {
                Assert.Throws<InvalidOperationException>(() => directory.Resolve(snapshot, directory.Register(snapshot, descendant)));
                if (descendant.Request != null)
                    Assert.Throws<InvalidOperationException>(() => TuneInChannel.CatalogueRequest(snapshot, descendant));
            }
            var currentHub = directory.Register(snapshot, new NavigationEntry
            {
                Category = "language",
                SelectionId = language,
                ContentCategory = "music",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=c424724")
            });
            Assert.Equal("c57944", Assert.Single((await channel.GetChannelItems(
                new InternalChannelItemQuery { FolderId = currentHub }, CancellationToken.None)).Items).Name);
            Assert.Equal(5, requests.Count);
        }

        [Fact]
        public void LegacyLanguageMusicItemsWithoutSavedGenreScopeCannotBypassSelections()
        {
            var config = Ready();
            config.EnableLanguage = true;
            config.Languages.Add(Selection("l122"));
            config.EnableMusic = true;
            config.Music.Add(Selection("g19"));
            var directory = new TuneInDirectory(() => config);
            var snapshot = directory.Snapshot();
            foreach (var entry in new[]
            {
                new NavigationEntry { Category = "language", SelectionId = "l122", ContentCategory = "music", StationId = "s1" },
                new NavigationEntry { Category = "language", SelectionId = "l122", ContentCategory = "music",
                    Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=g54&filter=l122") },
                new NavigationEntry { Category = "language", SelectionId = "l122", ContentCategory = "music",
                    MusicSelectionId = "g54", StationId = "s1" }
            })
                Assert.Throws<InvalidOperationException>(() => directory.Resolve(snapshot, directory.Register(snapshot, entry)));
        }

        [Fact]
        public void LegacyLanguageDescendantsWithoutGroupAncestryCannotBypassSwitches()
        {
            var config = Ready();
            config.EnableLanguage = true;
            config.Languages.Add(Selection("l122"));
            var directory = new TuneInDirectory(() => config);
            var snapshot = directory.Snapshot();
            foreach (var entry in new[]
            {
                new NavigationEntry { Category = "language", SelectionId = "l122", StationId = "s1" },
                new NavigationEntry { Category = "language", SelectionId = "l122",
                    Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=g22&filter=l122") },
                new NavigationEntry { Category = "language", SelectionId = "l122",
                    Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=c424725&filter=l122") }
            })
                Assert.Throws<InvalidOperationException>(() => directory.Resolve(snapshot, directory.Register(snapshot, entry)));
        }

        [Fact]
        public async Task UnknownLanguageCannotSilentlyFallBackToUnfilteredStations()
        {
            var config = Ready();
            config.EnableLanguage = true;
            config.Languages.Add(Selection("l999999"));
            var requests = 0;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests++;
                Assert.Equal("lang", request.Parameters["c"]);
                Assert.False(request.Parameters.ContainsKey("filter"));
                return Task.FromResult(Opml("<outline type='link' text='Known language' URL='http://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l1'/>"));
            });
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            var root = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
            var languages = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Items[0].Id }, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.GetChannelItems(new InternalChannelItemQuery
            { FolderId = languages.Items[0].Id }, CancellationToken.None));
            Assert.Equal(1, requests);
        }

        [Fact]
        public async Task SelectorExpansionIsExplicitBoundedCachedAndDiscardsAudio()
        {
            var count = 0;
            var config = Ready();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                count++;
                return Task.FromResult(Opml("<outline type='link' text='City &quot;quoted&quot;' URL='http://opml.radiotime.com/Browse.ashx?id=r123'/>"
                    + "<outline type='audio' guide_id='s1' text='Incidental station'/>"));
            });
            var root = await directory.SelectAsync("location", null, null, false, CancellationToken.None);
            var item = Assert.Single(root.Items);
            Assert.True(root.ContainsStationMetadata);
            Assert.Equal(new[] { "r0" }, item.AncestorIds);
            Assert.Contains("\"quoted\"", item.Name);
            await directory.SelectAsync("location", null, null, false, CancellationToken.None);
            Assert.Equal(1, count);
            var child = await directory.SelectAsync("location", item.Id, "<injected path>", false, CancellationToken.None);
            Assert.DoesNotContain("<injected", child.Items[0].Path);
            Assert.Equal(new[] { "r0", "r123" }, child.Items[0].AncestorIds);
            Assert.Equal(2, count);
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SelectAsync("location", "r999", null, false, CancellationToken.None));
            Assert.Equal(2, count);
        }

        [Fact]
        public void CacheCoversAllSettingsInvalidatesNavigationAndCancelsRequests()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.Add(Selection("g1"));
            var directory = new TuneInDirectory(() => config);
            var original = directory.Snapshot();
            using (var operation = directory.Operation(original, CancellationToken.None))
            {
                var id = directory.Register(original, new NavigationEntry { Category = "music", SelectionId = "g1" });
                config.Music[0].Enabled = false;
                var current = directory.Snapshot();
                Assert.NotEqual(original.Key, current.Key);
                Assert.True(operation.IsCancellationRequested);
                Assert.Throws<InvalidOperationException>(() => directory.Resolve(current, id));
            }
            var nextProcess = new TuneInDirectory(() => config);
            Assert.Equal(directory.Snapshot().Key, nextProcess.Snapshot().Key);
        }

        [Fact]
        public void SignedNavigationSurvivesRestartAndRejectsClientTampering()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.Music.Add(Selection("g1"));
            var directory = new TuneInDirectory(() => config);
            var snapshot = directory.Snapshot();
            var request = new DirectoryRequest();
            request.Parameters["id"] = "g1";
            var id = directory.Register(snapshot, new NavigationEntry
            { Category = "music", SelectionId = "g1", Request = request, Name = "Selected genre" });
            var restarted = new TuneInDirectory(() => config);
            Assert.Equal("g1", restarted.Resolve(restarted.Snapshot(), id).Request.Parameters["id"]);
            var tampered = (id[0] == 'A' ? "B" : "A") + id.Substring(1);
            Assert.Throws<InvalidOperationException>(() => restarted.Resolve(restarted.Snapshot(), tampered));
        }

        [Fact]
        public async Task PlaybackSourceOnlyTargetsLoopbackAndForcesServerTranscoding()
        {
            var config = Ready();
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Media source creation must not query TuneIn."));
            var id = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", StationId = "s1" });
            var channel = new TuneInChannel(DispatchProxy.Create<IServerApplicationHost, SourceHostProxy>(),
                DispatchProxy.Create<ILogger, QuietLoggerProxy>(), directory);
            var source = Assert.Single(await channel.GetChannelItemMediaInfo(id, CancellationToken.None));
            Assert.StartsWith("http://127.0.0.1:8096/emby/TuneInFork/Relay/", source.Path);
            Assert.EndsWith("?playback=2", source.Path);
            Assert.False(source.SupportsDirectPlay);
            Assert.False(source.SupportsDirectStream);
            Assert.True(source.SupportsTranscoding);
        }

        public class SourceHostProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_ApplicationVersion") return new Version(4, 9, 1, 90);
                Assert.Equal("GetLocalApiUrl", method.Name);
                Assert.True(System.Net.IPAddress.IsLoopback((System.Net.IPAddress)args[0]));
                return "http://127.0.0.1:8096/emby";
            }

        }

        public static IServerApplicationHost ArtworkHost() => DispatchProxy.Create<IServerApplicationHost, SourceHostProxy>();

        public class QuietLoggerProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.Equal("Debug", method.Name);
                return null;
            }
        }

        [Theory]
        [InlineData("http://127.0.0.1/Browse.ashx?id=r1")]
        [InlineData("https://opml.radiotime.com.evil.test/Browse.ashx?id=r1")]
        [InlineData("http://opml.radiotime.com:8080/Browse.ashx?id=r1")]
        [InlineData("https://opml.radiotime.com/Search.ashx?query=test")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=r1%26username%3Dsecret")]
        public void CatalogueLinksCannotIntroduceArbitraryTargetsOrParameters(string value) =>
            Assert.Throws<System.IO.InvalidDataException>(() => DirectoryRequest.ParseLink(value));

        [Theory]
        [InlineData("filter=private%20value", "filter", "contains whitespace", 13, 1)]
        [InlineData("id=private%26value", "id", "contains unsupported punctuation", 13, 1)]
        [InlineData("sid=", "sid", "empty", 0, 1)]
        [InlineData("offset=1&offset=2%2F", "offset", "contains unsupported punctuation", 4, 2)]
        [InlineData("pivot=%C3%A9", "pivot", "contains non-ASCII characters", 1, 1)]
        [InlineData("c=%01", "c", "contains a control character", 1, 1)]
        public void RejectedCatalogueParameterDiagnosticsDoNotExposeValues(string query, string key, string reason, int length, int occurrences)
        {
            var error = Assert.Throws<System.IO.InvalidDataException>(() =>
                DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?" + query));
            Assert.Contains("'" + key + "'", error.Message);
            Assert.Contains(reason, error.Message);
            Assert.Contains("length " + length, error.Message);
            Assert.Contains("occurrences " + occurrences, error.Message);
            Assert.DoesNotContain("private", error.Message);
            Assert.DoesNotContain("radiotime.com", error.Message);
        }

        [Fact]
        public void RejectedOverlongCatalogueParameterReportsOnlyLength()
        {
            var value = new string('x', 81);
            var error = Assert.Throws<System.IO.InvalidDataException>(() =>
                DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?filter=" + value));
            Assert.Contains("'filter' (longer than 80 characters; length 81; occurrences 1)", error.Message);
            Assert.DoesNotContain(value, error.Message);
        }

        [Fact]
        public void MetadataEndpointsRequireAdministratorAndRelayRequiresLocalServer()
        {
            var selector = (AuthenticatedAttribute)Attribute.GetCustomAttribute(typeof(GetTuneInSelectors), typeof(AuthenticatedAttribute));
            var relay = (AuthenticatedAttribute)Attribute.GetCustomAttribute(typeof(GetTuneInRelay), typeof(AuthenticatedAttribute));
            var artwork = (AuthenticatedAttribute)Attribute.GetCustomAttribute(typeof(GetTuneInArtwork), typeof(AuthenticatedAttribute));
            var branding = (AuthenticatedAttribute)Attribute.GetCustomAttribute(typeof(GetTuneInBranding), typeof(AuthenticatedAttribute));
            Assert.Equal("Admin", selector.Roles);
            Assert.False(selector.AllowLocal);
            Assert.True(relay.AllowLocalOnly);
            Assert.True(artwork.AllowLocalOnly);
            Assert.True(branding.AllowLocalOnly);
        }

        [Fact]
        public void ForkConfigurationPagesUseIndependentIdentityAndBothDashboardEntryPoints()
        {
            var previous = Plugin.Instance;
            try
            {
                var plugin = new Plugin(
                    DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, ConfigPathsProxy>(),
                    DispatchProxy.Create<MediaBrowser.Model.Serialization.IXmlSerializer, UnusedSerializerProxy>(),
                    DispatchProxy.Create<IServerApplicationHost, SourceHostProxy>());
                var pages = plugin.GetPages().ToList();
                var main = Assert.Single(pages, page => page.IsMainConfigPage);
                var script = Assert.Single(pages, page => !page.IsMainConfigPage);
                Assert.True(main.EnableInMainMenu);
                Assert.False(main.EnableInUserMenu);
                Assert.Equal("advanced", main.MenuSection);
                Assert.Equal("TuneIn", main.DisplayName);
                Assert.False(script.EnableInMainMenu);
                Assert.NotEqual(main.Name, script.Name);
                Assert.Equal(Plugin.ConfigurationPageName, main.Name);
                Assert.NotEqual("tuneinfork", main.Name);
                Assert.NotEqual(new Guid("10b82431-f939-4507-a8e2-9509dd3fd6ae"), plugin.Id);
                Assert.Equal("MediaBrowser.Plugins.TuneIn.Fork", typeof(Plugin).Assembly.GetName().Name);
                Assert.Equal("MediaBrowser.Plugins.TuneIn.Fork.xml", plugin.ConfigurationFileName);
                using (var html = new System.IO.StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream(main.EmbeddedResourcePath)))
                using (var js = new System.IO.StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream(script.EmbeddedResourcePath)))
                {
                    Assert.Contains("__plugin/" + script.Name, html.ReadToEnd());
                    Assert.Contains(plugin.Id.ToString(), js.ReadToEnd());
                }
            }
            finally
            {
                typeof(Plugin).GetProperty(nameof(Plugin.Instance)).SetValue(null, previous);
            }
        }

        public class ConfigPathsProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.Contains(method.Name, new[] { "get_PluginConfigurationsPath", "get_DataPath" });
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TuneInForkPageTest");
            }
        }

        public class UnusedSerializerProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args) =>
                throw new InvalidOperationException("Page registration must not read or write configuration.");
        }

        [Fact]
        public void UnversionedBundledHtmlParserIsNotInThePluginBinary()
        {
            Assert.DoesNotContain(typeof(Plugin).Assembly.GetTypes(), type => type.Namespace == "HtmlAgilityPack");
            Assert.Contains("MediaBrowser.Plugins.TuneIn.LICENSE.md", typeof(Plugin).Assembly.GetManifestResourceNames());
        }

        [Fact]
        public async Task MusicSelectionsDoNotIntersectWithUnrelatedLanguageSettingsOrCrawlPages()
        {
            var config = Ready();
            config.EnableMusic = true;
            config.EnableLanguage = true;
            config.Music.Add(Selection("g1"));
            config.Languages.Add(Selection("l122"));
            var count = 0;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                count++;
                Assert.Equal("g1", request.Parameters["id"]);
                Assert.False(request.Parameters.ContainsKey("filter"));
                return Task.FromResult(Opml("<outline type='audio' text='Station' guide_id='s1'/>"
                    + "<outline type='link' text='Next' key='nextStations' URL='http://opml.radiotime.com/Browse.ashx?id=g1&amp;offset=50'/>"));
            });
            var channel = new TuneInChannel(ArtworkHost(), null, directory);
            var root = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
            var genres = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Items.Single(i => i.Name == "Music").Id }, CancellationToken.None);
            Assert.Equal(0, count);
            var page = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = genres.Items[0].Id }, CancellationToken.None);
            Assert.Equal(1, count);
            Assert.Equal(2, page.Items.Count);
        }
    }
}
