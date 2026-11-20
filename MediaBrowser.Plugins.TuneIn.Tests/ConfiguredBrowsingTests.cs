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
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ConfiguredBrowsingTests
    {
        private static PluginConfiguration Ready() => new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true };

        [Fact]
        public async Task RefreshCacheAcceptsSixtyThousandPagesAndRejectsAnother()
        {
            var session = new TuneInCatalogueSession(null, TimeSpan.FromHours(1), () => DateTime.UtcNow,
                maxPages: TuneInCatalogueSession.MaxRefreshPages, maxBytes: TuneInCatalogueSession.MaxRefreshBytes);
            var document = Opml("");
            for (var index = 0; index < TuneInCatalogueSession.MaxRefreshPages; index++)
            {
                var request = new DirectoryRequest();
                request.Parameters["id"] = "c" + index;
                await session.FetchAsync(request, () => Task.FromResult(document), Token);
            }
            Assert.Equal(60000, session.PageCount);
            var extra = new DirectoryRequest();
            extra.Parameters["id"] = "c60000";
            await Assert.ThrowsAsync<InvalidDataException>(() => session.FetchAsync(extra,
                () => Task.FromResult(document), Token));
        }

        [Fact]
        public async Task RecentPagesExpireWithoutRenewalAndNeverReplaceLiveValidation()
        {
            var now = DateTime.UtcNow;
            var request = new DirectoryRequest();
            request.Parameters["id"] = "c1";
            var calls = 0;
            Task<XDocument> Fetch()
            {
                calls++;
                return Task.FromResult(Opml("<outline type='audio' guide_id='s1'/>"));
            }
            var first = new TuneInCatalogueSession(null, TimeSpan.FromHours(1), () => now);
            await first.FetchAsync(request, Fetch, Token);
            var recent = first.SnapshotFetchedPages();
            now = now.AddMinutes(9);
            var second = new TuneInCatalogueSession(null, TimeSpan.FromHours(1), () => now, recentPages: recent);
            await second.FetchAsync(request, Fetch, Token);
            Assert.Equal(1, calls);
            Assert.Empty(second.SnapshotFetchedPages());
            await second.FetchAsync(request, Fetch, Token, liveOnly: true);
            Assert.Equal(2, calls);
            now = now.AddMinutes(1);
            var expired = new TuneInCatalogueSession(null, TimeSpan.FromHours(1), () => now, recentPages: recent);
            await expired.FetchAsync(request, Fetch, Token);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task RegionalGenreOptOutKeepsExistingIdsAndOrdinaryListings()
        {
            var config = Ready();
            config.HideRegionalGenres = false;
            config.HasStartedCatalogRefresh = true;
            config.EnableLocation = true;
            config.Locations.Add(new CatalogSelection { Id = "r10" });
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(
                "<outline type='link' text='Rock' URL='https://opml.radiotime.com/Browse.ashx?id=r10&amp;filter=g79'/>"
                + "<outline type='link' text='Country' URL='https://opml.radiotime.com/Browse.ashx?id=r10&amp;filter=s:g2754:country'/>"
                + "<outline type='link' text='Popular' URL='https://opml.radiotime.com/Browse.ashx?id=r10&amp;filter=s:popular'/>"
                + "<outline type='audio' text='Radio' guide_id='s1'/>")));
            var before = directory.Snapshot();
            var legacyOverrides = new System.Xml.Serialization.XmlAttributeOverrides();
            foreach (var member in new[] { "RefreshSchedules", "ApiRequestIntervalSeconds", "HideRegionalGenres",
                "AllowPartialCatalogImport", "HasStartedCatalogRefresh",
                "SettingsSaved", "TalkMode", "TalkIds", "SportsMode", "SportsIds", "PodcastsMode", "PodcastIds" })
                legacyOverrides.Add(typeof(PluginConfiguration), member, new System.Xml.Serialization.XmlAttributes { XmlIgnore = true });
            using (var xml = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            {
                new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration), legacyOverrides).Serialize(xml, before.Value);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml.ToString()))), before.Key);
            }
            var folder = directory.Register(before, TuneInBrowsing.StartingEntries(before, "location").Single());
            var browser = new TuneInBrowsing(directory, _ => null);
            Assert.Equal(4, (await browser.GetItemsAsync(folder, Token)).Items.Count);
            config.HideRegionalGenres = true;
            var after = directory.Snapshot();
            Assert.Equal(before.Key, after.Key);
            Assert.NotEqual(before.DiscoveryKey, after.DiscoveryKey);
            Assert.Equal(new[] { "Popular", "Radio" },
                (await browser.GetItemsAsync(folder, Token)).Items.Select(item => item.Name).OrderBy(name => name));
        }

        [Theory]
        [InlineData("local")]
        [InlineData("location")]
        [InlineData("talk")]
        [InlineData("sports")]
        [InlineData("podcast")]
        public async Task KeepsProviderGenrePivotAndItsContentsButNotIndividualAliases(string category)
        {
            var config = Ready();
            config.EnableLocation = config.EnableTalk = config.EnableSports = config.EnablePodcasts = true;
            config.EnableLocalRadio = true;
            config.LocalRadioSource = "regions";
            config.LocalCities.Add(new CatalogSelection { Id = "r10" });
            config.Locations.Add(new CatalogSelection { Id = "r10" });
            var id = category == "location" || category == "local" ? "r10" : "c100";
            var directory = new TuneInDirectory(() => config, (request, _) => Task.FromResult(Opml(
                (request.Parameters.ContainsKey("pivot") ? "" :
                    "<outline type='link' text='By Genre' URL='https://opml.radiotime.com/Browse.ashx?id=" + id +
                    "&amp;pivot=genre&amp;filter=country'/>") +
                "<outline type='link' text='Rock' URL='https://opml.radiotime.com/Browse.ashx?id=" + id + "&amp;filter=g79'/>" +
                "<outline type='audio' text='Radio' guide_id='s1'/>")));
            var snapshot = directory.Snapshot();
            var browser = new TuneInBrowsing(directory, _ => null);
            var listing = category == "location"
                ? directory.Register(snapshot, TuneInBrowsing.StartingEntries(snapshot, category).Single())
                : browser.CategoryRoot(snapshot, category).Id;
            var visible = (await browser.GetItemsAsync(listing, Token)).Items;
            Assert.DoesNotContain(visible, item => item.Name == "Rock");
            var pivot = Assert.Single(visible, item => item.Name == "By Genre");
            var grouped = (await browser.GetItemsAsync(pivot.Id, Token)).Items;
            Assert.Contains(grouped, item => item.Name == "Rock");
            Assert.Contains(grouped, item => item.Name == "Radio");
        }

        [Theory]
        [InlineData("", 1)]
        [InlineData("<ApiRequestIntervalSeconds>3</ApiRequestIntervalSeconds>", 3)]
        [InlineData("<ApiRequestIntervalSeconds>17</ApiRequestIntervalSeconds>", 17)]
        public void RequestIntervalDefaultsOnlyWhenNotSaved(string setting, int expected)
        {
            using var reader = new StringReader("<PluginConfiguration>" + setting + "</PluginConfiguration>");
            var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader);
            Assert.Equal(expected, config.ApiRequestIntervalSeconds);
        }

        [Fact]
        public async Task DiscoveryCoalescesIdenticalInFlightPagesWithoutMergingDifferentQueries()
        {
            var requests = new List<bool>();
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow,
                requested: hit => requests.Add(hit), timings: new TuneInRefreshTimings());
            var request = new DirectoryRequest();
            request.Parameters.Add("id", "r10");
            request.Parameters.Add("filter", "s:popular");
            var page = new TaskCompletionSource<XDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = session.FetchAsync(request, () => page.Task, Token);
            var same = session.FetchAsync(request.Copy(), () => throw new InvalidOperationException("Duplicate fetch"), Token);
            Assert.False(same.IsCompleted);
            page.SetResult(Opml("<outline type='audio' guide_id='s1'/>"));
            await Task.WhenAll(first, same);
            foreach (var difference in new[] { "offset", "pivot", "username", "latlon" })
            {
                var other = request.Copy();
                other.Parameters[difference] = "2";
                await session.FetchAsync(other, () => Task.FromResult(Opml("<outline type='audio' guide_id='s2'/>")), Token);
            }
            Assert.Equal(new[] { false, true, false, false, false, false }, requests);
        }
        [Fact]
        public async Task PartialImportPolicyPreservesNavigationAndDiscoveryIdentity()
        {
            var config = Ready();
            config.AllowPartialCatalogImport = false;
            config.EnableTalk = true;
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(Opml(
                "<outline type='audio' guide_id='s1' text='Radio'/>")));
            var before = directory.Snapshot();
            var browser = new TuneInBrowsing(directory, _ => null);
            var root = browser.CategoryRoot(before, "talk");
            config.AllowPartialCatalogImport = true;
            var after = directory.Snapshot();
            Assert.Equal(before.Key, after.Key);
            Assert.Equal(before.DiscoveryKey, after.DiscoveryKey);
            Assert.Equal(root.Id, browser.CategoryRoot(after, "talk").Id);
            Assert.Single((await browser.GetItemsAsync(root.Id, Token)).Items);
            Assert.True(after.Value.AllowPartialCatalogImport);
        }

        [Theory]
        [InlineData("")]
        [InlineData("<HideRegionalGenres>false</HideRegionalGenres><AllowPartialCatalogImport>false</AllowPartialCatalogImport>")]
        [InlineData("<HideRegionalGenres>true</HideRegionalGenres><AllowPartialCatalogImport>true</AllowPartialCatalogImport>")]
        public void BrowsingOptionsDefaultOnAndPreserveSavedChoices(string settings)
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var reader = new StringReader("<PluginConfiguration>" + settings + "</PluginConfiguration>");
            var config = (PluginConfiguration)serializer.Deserialize(reader);
            var expected = !settings.Contains("false");
            Assert.Equal(expected, config.HideRegionalGenres);
            Assert.Equal(expected, config.AllowPartialCatalogImport);
            using var saved = new StringWriter();
            serializer.Serialize(saved, config);
            using var reloaded = new StringReader(saved.ToString());
            var roundTrip = (PluginConfiguration)serializer.Deserialize(reloaded);
            Assert.Equal(expected, roundTrip.HideRegionalGenres);
            Assert.Equal(expected, roundTrip.AllowPartialCatalogImport);
        }

        private static CatalogSelection Selection(string id, params string[] parents) =>
            new CatalogSelection { Id = id, Name = id, AncestorIds = parents.ToList() };
        private static XDocument Opml(string rows) => TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(
            "<opml><head><status>200</status></head><body>" + rows + "</body></opml>"));
        private static string Link(string id, string name = "Choice") =>
            "<outline type='link' text='" + name + "' URL='https://opml.radiotime.com/Browse.ashx?id=" + id + "'/>";
        private static readonly CancellationToken Token = CancellationToken.None;

        [Theory]
        [InlineData("talk", "g214", "c25212376", null)]
        [InlineData("sports", "c100006105", "g2723", null)]
        [InlineData("podcast", "c23980497", "c583296", "p:topic")]
        public async Task SelectedTopicsOnlyFollowOptionsFromTheirOwnRoot(string category, string selected, string other,
            string filter)
        {
            var config = Ready();
            config.EnableTalk = config.EnableSports = config.EnablePodcasts = true;
            config.TalkMode = config.SportsMode = config.PodcastsMode = "selected";
            config.TalkIds = config.SportsIds = config.PodcastIds = new List<string> { selected };
            string Context(string id) => id + (filter == null ? "" : "&amp;filter=" + filter);
            var requests = new List<DirectoryRequest>();
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                requests.Add(request);
                if (request.Parameters.TryGetValue("c", out var classifier))
                {
                    Assert.Equal(category, classifier);
                    return Task.FromResult(Opml("<outline text='Options'>"
                        + Link(Context(other), "Other branch") + Link(Context(selected), "Selected branch")
                        + Link("c1000&amp;filter=s:popular", "Unsupported context")
                        + Link(Context("c1001") + "&amp;offset=10", "Next page")
                        + "<outline type='link' text='Mismatch' guide_id='c1003' URL='https://opml.radiotime.com/Browse.ashx?id="
                        + Context("c1002") + "'/>"
                        + "<outline type='link' text='Foreign host' URL='https://example.test/Browse.ashx?id=c1004'/>"
                        + "<outline key='related'>" + Link(Context("c1005")) + "</outline>"
                        + "<outline type='audio' guide_id='s1' text='Unselected station'/>"
                        + "</outline>"));
                }
                Assert.Equal(selected, request.Parameters["id"]);
                Assert.Equal(filter, request.Parameters.GetValueOrDefault("filter"));
                return Task.FromResult(Opml(Link(Context(other), "Other branch")
                    + "<outline type='link' text='All' URL='https://opml.radiotime.com/Browse.ashx?c=" + category + "'/>"
                    + "<outline type='audio' guide_id='s9' text='Selected station'/>"));
            });
            var browser = new TuneInBrowsing(directory, _ => null);
            Assert.Equal(3, (await browser.GetItemsAsync(null, Token)).Items.Count);
            Assert.Empty(requests);
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SelectAsync(category, selected, false, Token));
            var choices = await directory.SelectAsync(category, null, false, Token);
            Assert.True(choices.ContainsStationMetadata);
            Assert.NotNull(choices.Warning);
            Assert.Equal(new[] { other, selected }.OrderBy(id => id), choices.Items.Select(item => item.Id).OrderBy(id => id));
            Assert.All(choices.Items, item => { Assert.True(item.CanSelect); Assert.False(item.CanExpand); });
            await directory.SelectAsync(category, null, false, Token);
            Assert.Single(requests);

            var snapshot = directory.Snapshot();
            var root = browser.CategoryRoot(snapshot, category);
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow);
            var branch = Assert.Single((await browser.GetItemsAsync(root.Id, Token, session)).Items);
            Assert.Equal("Selected branch", branch.Name);
            var station = Assert.Single((await browser.GetItemsAsync(branch.Id, Token, session)).Items);
            Assert.Equal("s9", directory.Resolve(snapshot, station.Id).StationId);
            Assert.Equal(3, requests.Count); // One selector call, one live session root, one selected content call.
            Assert.Single(session.SnapshotPages()); // The live root cannot be replayed from a discovery checkpoint.
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SelectedTopicDiscoveryFailsClosedForEmptyOrStaleIds(bool empty)
        {
            var config = Ready();
            config.EnablePodcasts = true;
            config.PodcastsMode = "selected";
            if (!empty) config.PodcastIds.Add("c23980497");
            var option = Link("c23980497&amp;filter=p:topic", "Tennis");
            var requests = 0;
            var directory = new TuneInDirectory(() => config, (request, _) =>
            {
                Assert.Equal("podcast", request.Parameters["c"]);
                requests++;
                return Task.FromResult(Opml(option));
            });
            var browser = new TuneInBrowsing(directory, _ => null);
            var root = browser.CategoryRoot(directory.Snapshot(), "podcast");
            var session = new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow);
            if (!empty)
            {
                Assert.Single((await directory.SelectAsync("podcast", null, false, Token)).Items);
                Assert.Single((await browser.GetItemsAsync(root.Id, Token, session)).Items);
                // Same ID without its approved category context no longer identifies this Podcast branch.
                option = Link("c23980497", "Tennis");
                session = new TuneInCatalogueSession(session.SnapshotPages(), TimeSpan.FromMinutes(1), () => DateTime.UtcNow);
            }
            var channel = new TuneInChannel(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => TuneInStagedImport.DiscoverAsync(channel, root,
                session, Token, (id, catalogue, _, token) => browser.GetItemsAsync(id, token, catalogue), null, null, (_, __) => false));
            Assert.Equal(empty ? 0 : 3, requests);
        }

        [Fact]
        public void TopicScopesInvalidateOnlyRelatedCategoryRebindingAndKeepAllCompatible()
        {
            var config = Ready();
            config.EnableTalk = config.EnableSports = true;
            var directory = new TuneInDirectory(() => config, (_, __) => throw new InvalidOperationException());
            var browser = new TuneInBrowsing(directory, _ => null);
            var before = directory.Snapshot();
            var talk = browser.CategoryRoot(before, "talk").Id;
            var sports = browser.CategoryRoot(before, "sports").Id;
            config.TalkMode = "ALL";
            config.TalkIds.Add("g214");
            Assert.Equal(before.Key, directory.Snapshot().Key);
            config.TalkMode = "selected";
            config.TalkIds.Add("c25212376");
            var after = directory.Snapshot();
            Assert.NotEqual(before.Key, after.Key);
            Assert.NotEqual(before.DiscoveryKey, after.DiscoveryKey);
            Assert.Throws<InvalidOperationException>(() => directory.Resolve(after, talk));
            Assert.Throws<InvalidOperationException>(() => directory.CategoryRebinder(before, after, "talk"));
            Assert.Equal(browser.CategoryRoot(after, "sports").Id, directory.CategoryRebinder(before, after, "sports")(sports));
            config.TalkIds.Reverse();
            config.TalkIds.Add("g214");
            Assert.Equal(after.Key, directory.Snapshot().Key);
            Assert.DoesNotContain("c25212376", before.TopicIds("talk"));
            config.TalkIds.Add("https://example.test/");
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SettingsSaveHistoryDistinguishesFreshAndLegacyBrowsingWithoutChangingScope(bool browsingEnabled)
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var empty = new StringReader("<PluginConfiguration/>");
            var fresh = (PluginConfiguration)serializer.Deserialize(empty);
            Assert.False(fresh.SettingsSaved);
            var key = new ConfigurationSnapshot(fresh).Key;
            fresh.SettingsSaved = true;
            Assert.Equal(key, new ConfigurationSnapshot(fresh).Key);
            var legacyXml = "<PluginConfiguration><SchemaVersion>1</SchemaVersion><ConfigurationCompleted>"
                + XmlConvert.ToString(browsingEnabled) + "</ConfigurationCompleted>";
            using var legacy = new StringReader(legacyXml + "</PluginConfiguration>");
            var existing = (PluginConfiguration)serializer.Deserialize(legacy);
            Assert.True(existing.SettingsSaved);
            Assert.Equal("all", existing.TalkMode);
            Assert.Equal("all", existing.SportsMode);
            Assert.Equal("all", existing.PodcastsMode);
            using var explicitFalse = new StringReader(legacyXml + "<SettingsSaved>false</SettingsSaved></PluginConfiguration>");
            Assert.False(((PluginConfiguration)serializer.Deserialize(explicitFalse)).SettingsSaved);
        }

        [Fact]
        public void SettingsAreRegisteredInAdvancedAndRemainTheMainPluginPage()
        {
            var plugin = new Plugin(Mock.Of<MediaBrowser.Common.Configuration.IApplicationPaths>(),
                Mock.Of<MediaBrowser.Model.Serialization.IXmlSerializer>());
            var pages = plugin.GetPages().ToList();
            var settings = Assert.Single(pages, page => page.EnableInMainMenu);
            Assert.Equal("tunein", settings.Name);
            Assert.Equal("TuneIn", settings.DisplayName);
            Assert.Equal("advanced", settings.MenuSection);
            Assert.True(settings.IsMainConfigPage);
            Assert.False(settings.EnableInUserMenu);
            Assert.EndsWith(".Configuration.configPage.html", settings.EmbeddedResourcePath);
            Assert.False(pages.Single(page => page.Name == "tuneinjs").EnableInMainMenu);
        }

        [Fact]
        public void PluginAssemblyEmbedsTheCurrentControlPanel()
        {
            var assembly = typeof(Plugin).Assembly;
            using var htmlStream = assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Configuration.configPage.html");
            using var jsStream = assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Configuration.configPage.js");
            Assert.NotNull(htmlStream);
            Assert.NotNull(jsStream);
            using var htmlReader = new StreamReader(htmlStream);
            using var jsReader = new StreamReader(jsStream);
            var html = htmlReader.ReadToEnd();
            var script = jsReader.ReadToEnd();
            Assert.Contains("Global TuneIn Settings", html);
            Assert.Contains("Enable TuneIn Station Filtering", html);
            Assert.Contains("Clean Up Library Folders", html);
            Assert.Contains("<div class=\"auto-center tuneInPanelLayout\">", html);
            Assert.Contains("class=\"tuneInSection tuneInNeutralPanel tuneInRefresh\"", html);
            Assert.Contains("class=\"tuneInActivity maintenanceIndicator\"", html);
            Assert.Contains("class=\"tuneInActivity refreshIndicator\"", html);
            Assert.Contains("Please go to the Catalog Refresh section", script);
        }

        [Fact]
        public async Task LegacyConfigurationAndChannelRootNeverCrawlTheCatalog()
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
            var directory = new TuneInDirectory(() => config, (_, __) => throw new Exception("Unexpected catalog request."));
            var id = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", StationId = "s1" });
            var client = new Mock<IHttpClient>(MockBehavior.Strict);
            string requested = null;
            client.Setup(c => c.SendAsync(It.IsAny<HttpRequestOptions>(), "GET"))
                .Callback<HttpRequestOptions, string>((request, method) => requested = request.Url)
                .ReturnsAsync(new HttpResponseInfo { StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new MemoryStream(Encoding.UTF8.GetBytes("https://example.com/live.mp3\n")) });
            var source = Assert.Single(await new TuneInChannel(directory, client.Object, Mock.Of<ILogger>())
                .GetChannelItemMediaInfo(id, Token));
            Assert.Equal("https://example.com/live.mp3", source.Path);
            Assert.StartsWith("https://opml.radiotime.com/Tune.ashx?", requested);
            Assert.Contains("id=s1", requested);
            client.VerifyAll();
        }

        private sealed class TestCryptoProvider : ICryptoProvider
        {
            public Guid GetMD5(string value) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(value)));
            public async Task<byte[]> ComputeMD5Async(Stream stream, CancellationToken token) =>
                await MD5.HashDataAsync(stream, token);
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
            Assert.Equal(1, new PluginConfiguration().ApiRequestIntervalSeconds);
            config.ApiRequestIntervalSeconds = 0;
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            config.ApiRequestIntervalSeconds = 31;
            Assert.Throws<ArgumentException>(() => ConfigurationSnapshot.Validate(config));
            config.ApiRequestIntervalSeconds = 1;
            ConfigurationSnapshot.Validate(config);
            config.ApiRequestIntervalSeconds = 30;
            ConfigurationSnapshot.Validate(config);
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
