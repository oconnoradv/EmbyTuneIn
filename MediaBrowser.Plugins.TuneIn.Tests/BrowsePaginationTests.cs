using System;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System.Xml.Linq;
using System.IO;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class BrowsePaginationTests
    {
        [Theory]
        [InlineData("http", "s:~#")]
        [InlineData("http", "p:~#")]
        [InlineData("http", "s%3A~#")]
        [InlineData("http", "p%3A~#")]
        [InlineData("https", "s:~#")]
        public async Task MoreStationsPreservesHashAlphabetFilter(string scheme, string filter)
        {
            var url = scheme + "://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=" + filter;
            var request = await BrowseProviderLink(url);

            Assert.Equal("", new Uri(request).Fragment);
            Assert.Equal(HttpUtility.UrlDecode(filter),
                HttpUtility.ParseQueryString(new Uri(request).Query)["filter"]);
        }

        [Theory]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~%23")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?filter=p%3A~%23&id=c57941&offset=26")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~A")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~%C3%89")]
        public async Task ValidEncodedFiltersArePreserved(string url)
        {
            Assert.Equal(HttpUtility.ParseQueryString(new Uri(url).Query)["filter"],
                HttpUtility.ParseQueryString(new Uri(await BrowseProviderLink(url)).Query)["filter"]);
        }

        [Theory]
        [InlineData("s:~%00", "s:~#")]
        [InlineData("p:~%00", "p:~#")]
        public void NulTerminatedHashFiltersAreRecovered(string filter, string expected)
        {
            var link = DirectoryRequest.ParseLink(
                "https://opml.radiotime.com/Browse.ashx?id=c57941&filter=" + filter);

            Assert.Equal(expected, link.Parameters["filter"]);
        }

        [Theory]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~#section")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&id=c57941#")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&filter=s:~#")]
        [InlineData("http://example.com/Browse.ashx?filter=s:~#")]
        [InlineData("http://opml.radiotime.com/Tune.ashx?filter=s:~#")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~#:country")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=r100016&filter=s:~#:country&c=music")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=c57941&filter=s:~%00:country")]
        [InlineData("https://opml.radiotime.com/Tune.ashx?id=r100016&filter=s:~%00:country")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=r100016&filter=s:~%00:country&filter=s:~A:country")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=r100016&filter=s:~%00:country:extra")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=r100016&filter=s:~%00%00:country")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=r100016&filter=s:~%5Cu0000:country")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?id=r100016&filter=s:~%00:country#section")]
        public void UnusableLinksAreNotFollowed(string url)
        {
            Assert.Throws<InvalidDataException>(() => DirectoryRequest.ParseLink(url));
        }

        [Theory]
        [InlineData("s:~#:country", "s:~#:country")]
        [InlineData("s:~#", "s:~#")]
        [InlineData("s:~A:country", "s:~A:country")]
        [InlineData("s:~%23%3Acountry", "s:~#:country")]
        [InlineData("s:~%00:country", "s:~#:country")]
        [InlineData("p:~%00:country", "p:~#:country")]
        public async Task LocationFollowsProviderNameIndex(string filter, string expected)
        {
            var request = await BrowseProviderLink("https://opml.radiotime.com/Browse.ashx?offset=26&id=r100016&filter=" + filter, true);
            Assert.Equal(expected, HttpUtility.ParseQueryString(new Uri(request).Query)["filter"]);
            Assert.Equal("", new Uri(request).Fragment);
        }

        [Theory]
        [InlineData("g3", "c57935", "26", "s", "nextStations")]
        [InlineData("g19", "c57951", "26", "s", "nextStations")]
        [InlineData("g22", "c57954", "26", "s", "nextStations")]
        [InlineData("g71", "c100002515", "26", "s", "nextStations")]
        [InlineData("g71", "c100002515", "6", "p", "nextShows")]
        [InlineData("g373", "c100002519", "26", "s", "nextStations")]
        [InlineData("g373", "c100002519", "6", "p", "nextShows")]
        [InlineData("g2698", "c100002520", "26", "s", "nextStations")]
        [InlineData("g3078", "c100001377", "26", "p", "nextShows")]
        [InlineData("g214", "c100000971", "26", "s", "nextStations")]
        [InlineData("g2801", "c100002500", "26", "s", "nextStations")]
        [InlineData("g2801", "c100002500", "6", "p", "nextShows")]
        [InlineData("g2758", "c100002502", "26", "s", "nextStations")]
        [InlineData("g403", "c100002503", "6", "p", "nextShows")]
        public async Task LegacyGenrePaginationFollowsItsCanonicalMusicCategory(
            string genre, string category, string offset, string filter, string key)
        {
            var url = $"https://opml.radiotime.com/Browse.ashx?offset={offset}&id={category}&filter={filter}";
            var request = await BrowseProviderLink(url, musicId: genre, key: key);
            var query = HttpUtility.ParseQueryString(new Uri(request).Query);
            Assert.Equal(category, query["id"]);
            Assert.Equal(offset, query["offset"]);
            Assert.Equal(filter, query["filter"]);
        }

        [Theory]
        [InlineData("g19", "offset=26&id=c57954&filter=s")]
        [InlineData("g19", "id=c57951&filter=s")]
        [InlineData("g19", "offset=26&id=c100002519&filter=s")]
        [InlineData("g2698", "offset=26&id=c100002519&filter=s")]
        [InlineData("g71", "offset=26&id=c100002519&filter=s")]
        [InlineData("g71", "id=c100002515&filter=s")]
        [InlineData("g71", "offset=0&id=c100002515&filter=s")]
        [InlineData("g71", "offset=26&id=c100002515&filter=s&c=music")]
        [InlineData("g2801", "offset=26&id=c100002519&filter=s")]
        [InlineData("g2801", "offset=26&id=c100002500&filter=g1")]
        [InlineData("g2801", "offset=26&id=g2758&filter=s")]
        [InlineData("g2801", "offset=26&id=c57938&filter=s")]
        public async Task LegacyGenrePaginationDoesNotFollowUnrelatedIdsOrNonPageLinks(string genre, string query)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableMusic = true };
            config.Music.Add(new CatalogSelection { Id = genre, Enabled = true });
            var directory = new TuneInDirectory(() => config, (request, token) => Task.FromResult(XDocument.Parse(
                "<opml><body><outline type='link' key='nextStations' text='More' URL='https://opml.radiotime.com/Browse.ashx?"
                + query.Replace("&", "&amp;") + "'/></body></opml>")));
            var snapshot = directory.Snapshot();
            var page = await new TuneInChannel(directory).GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = directory.Register(snapshot, Assert.Single(TuneInBrowsing.StartingEntries(snapshot, "music")))
            }, CancellationToken.None);
            Assert.Empty(page.Items);
        }

        [Theory]
        [InlineData("offset=26&id=c100001377&filter=p", true)]
        [InlineData("offset=26&id=c100002519&filter=p", false)]
        public async Task LanguageBranchPaginationFollowsKnownCanonicalCategory(string query, bool allowed)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableLanguage = true };
            config.Languages.Add(new CatalogSelection { Id = "l122", Enabled = true });
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (request, token) => Task.FromResult(XDocument.Parse(
                !request.Parameters.ContainsKey("id")
                ? "<opml><body><outline type='link' text='Spanish' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=l122'/></body></opml>"
                : calls++ == 0
                ? "<opml><body><outline type='link' key='nextShows' text='More' URL='https://opml.radiotime.com/Browse.ashx?"
                    + query.Replace("&", "&amp;") + "'/></body></opml>"
                : "<opml><body/></opml>")));
            var snapshot = directory.Snapshot();
            var folder = directory.Register(snapshot, new NavigationEntry
            {
                Category = "language", SelectionId = "l122", ContentCategory = "talk",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=g3078&filter=l122")
            });
            var items = (await new TuneInChannel(directory).GetChannelItems(new InternalChannelItemQuery { FolderId = folder }, CancellationToken.None)).Items;
            if (allowed) Assert.Single(items);
            else Assert.Empty(items);
        }

        [Theory]
        [InlineData("g4150", "offset=26&id=c100002522&filter=s", true)]
        [InlineData("g4152", "offset=26&id=c100002659&filter=s", true)]
        [InlineData("g4152", "offset=6&id=c100002659&filter=p", true)]
        [InlineData("g4150", "offset=26&id=c100002519&filter=s", false)]
        [InlineData("g4150", "offset=26&id=g4152&filter=s", false)]
        [InlineData("g4150", "offset=0&id=c100002522&filter=s", false)]
        [InlineData("g4150", "offset=26&id=c100002522&filter=s:popular", false)]
        [InlineData("g54", "offset=26&id=c57938&filter=l1", true, "l1", "nextStations")]
        [InlineData("g54", "offset=6&id=c57938&filter=l1", true, "l1", "nextShows")]
        [InlineData("g54", "offset=26&id=c57935&filter=l1", false, "l1")]
        [InlineData("g54", "id=c57938&filter=l1", false, "l1")]
        [InlineData("g4150", "offset=26&id=c100002522&filter=l1", true, "l1")]
        [InlineData("g4150", "offset=6&id=c100002522&filter=l1", true, "l1", "nextShows")]
        [InlineData("g4150", "offset=26&id=c57938&filter=l1", false, "l1")]
        [InlineData("g4150", "offset=26&id=c100002522&filter=l1&c=music", false, "l1")]
        [InlineData("g4150", "offset=26&id=c100002522&filter=l1", true, "l1", "nextStations", false)]
        [InlineData("g4150", "offset=6&id=c100002522&filter=l1", true, "l1", "nextShows", false)]
        [InlineData("g4150", "offset=26&id=c100002522&filter=l2", false, "l1", "nextStations", false)]
        [InlineData("g54", "offset=26&id=c57938&filter=l2", false, "l1", "nextStations", false)]
        public async Task LanguageGenrePaginationKeepsItsLanguageScope(string genre, string query, bool allowed,
            string language = "l122", string key = "nextStations", bool selected = true)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true,
                EnableLanguage = true, LanguageMode = selected ? "selected" : "all" };
            config.Languages.Add(new CatalogSelection { Id = language, Enabled = true });
            var calls = 0;
            string requestedFilter = null;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requestedFilter = request.Parameters.GetValueOrDefault("filter");
                var xml = !request.Parameters.ContainsKey("id")
                    ? "<opml><body><outline type='link' text='Selected language' URL='https://opml.radiotime.com/Browse.ashx?c=lang&amp;filter=" + language + "'/></body></opml>"
                    : calls++ == 0
                    ? "<opml><body><outline type='link' key='" + key + "' text='More' URL='https://opml.radiotime.com/Browse.ashx?"
                        + query.Replace("&", "&amp;") + "'/></body></opml>"
                    : "<opml><body/></opml>";
                return Task.FromResult(XDocument.Parse(xml));
            });
            var snapshot = directory.Snapshot();
            var parent = new NavigationEntry
            {
                Category = "language", SelectionId = selected ? language : null, ContentCategory = "music",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=" + genre + "&filter=" + language)
            };
            var channel = new TuneInChannel(directory);
            var items = (await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = directory.Register(snapshot, parent)
            }, CancellationToken.None)).Items;
            if (!allowed) { Assert.Empty(items); return; }
            var child = directory.Resolve(snapshot, Assert.Single(items).Id);
            Assert.True(TuneInBrowsing.IsForwardPage(snapshot, parent, child));
            await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = items[0].Id }, CancellationToken.None);
            Assert.Equal(query.Contains("filter=p") ? "p:" + language
                : query.Contains("filter=s") ? "s:" + language : language, requestedFilter);
        }

        [Theory]
        [InlineData("g3403", "c100001371", "26", "p:topic", true)]
        [InlineData("g3422", "c100000625", "26", "p:topic", true)]
        [InlineData("g3428", "c100000892", "26", "p:topic", true)]
        [InlineData("g3429", "c100000887", "26", "p:topic", true)]
        [InlineData("g3430", "c100000886", "26", "p:topic", true)]
        [InlineData("g3429", "c100000886", "26", "p:topic", false)]
        [InlineData("g3428", "c100000625", "26", "p:topic", false)]
        [InlineData("g3430", "c100000887", "26", "p:topic", false)]
        [InlineData("g3431", "c100000886", "26", "p:topic", false)]
        [InlineData("g3430", "c100000886", "0", "p:topic", false)]
        [InlineData("g3430", "c100000886", "26", "p", false)]
        [InlineData("g3429", "c100000887", "26", "s", true, "s")]
        [InlineData("g3429", "c100000887", "26", "p", true, "p")]
        public async Task PodcastPaginationAcceptsOnlyTheObservedTopicTransition(
            string source, string destination, string offset, string filter, bool allowed, string currentFilter = "p:topic")
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnablePodcasts = true };
            var calls = 0;
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                calls++;
                var body = calls == 1
                    ? $"<outline type='link' key='nextShows' text='More' URL='https://opml.radiotime.com/Browse.ashx?offset={offset}&amp;id={destination}&amp;filter={filter}'/>"
                    : "";
                return Task.FromResult(XDocument.Parse("<opml><body>" + body + "</body></opml>"));
            });
            var snapshot = directory.Snapshot();
            var parent = new NavigationEntry
            {
                Category = "podcast",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=" + source + "&filter=" + currentFilter)
            };
            var channel = new TuneInChannel(directory);
            var items = (await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = directory.Register(snapshot, parent)
            }, CancellationToken.None)).Items;
            if (!allowed) { Assert.Empty(items); return; }
            var child = directory.Resolve(snapshot, Assert.Single(items).Id);
            Assert.True(TuneInBrowsing.IsForwardPage(snapshot, parent, child));
            Assert.False(TuneInBrowsing.IsForwardPage(snapshot, child, child));
            await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = items[0].Id }, CancellationToken.None);
            Assert.Equal(2, calls);
        }

        private static async Task<string> BrowseProviderLink(string url, bool location = false,
            string musicId = "c57941", string key = "nextStations")
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true,
                EnableLocation = location, EnableMusic = !location };
            if (location) config.Locations.Add(new CatalogSelection { Id = "r100016", Enabled = true });
            else config.Music.Add(new CatalogSelection { Id = musicId, Enabled = true });
            var calls = 0;
            string requestedUrl = null;
            var directory = new TuneInDirectory(() => config, (request, token) =>
                {
                    requestedUrl = request.Url;
                    var xml = calls++ == 0
                        ? $"<opml><body><outline type='link' key='{key}' text='More' url='"
                            + url.Replace("&", "&amp;") + "'/></body></opml>"
                        : "<opml><body/></opml>";
                    return Task.FromResult(XDocument.Parse(xml));
                });

            var channel = new TuneInChannel(directory);
            var snapshot = directory.Snapshot();
            var page = await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = directory.Register(snapshot, Assert.Single(TuneInBrowsing.StartingEntries(snapshot, location ? "location" : "music")))
            }, CancellationToken.None);
            var next = Assert.Single(page.Items);
            await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = next.Id
            }, CancellationToken.None);
            Assert.Equal(2, calls);
            return requestedUrl;
        }
    }
}
