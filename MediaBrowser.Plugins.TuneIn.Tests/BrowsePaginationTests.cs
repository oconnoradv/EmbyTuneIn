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
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~#section")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&id=c57941#")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&filter=s:~#")]
        [InlineData("http://example.com/Browse.ashx?filter=s:~#")]
        [InlineData("http://opml.radiotime.com/Tune.ashx?filter=s:~#")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~#:country")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=r100016&filter=s:~#:country&c=music")]
        public void UnusableLinksAreNotFollowed(string url)
        {
            Assert.Throws<InvalidDataException>(() => DirectoryRequest.ParseLink(url));
        }

        [Theory]
        [InlineData("s:~#:country", "s:~#:country")]
        [InlineData("s:~#", "s:~#")]
        [InlineData("s:~A:country", "s:~A:country")]
        [InlineData("s:~%23%3Acountry", "s:~#:country")]
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
        [InlineData("g373", "c100002519", "26", "s", "nextStations")]
        [InlineData("g373", "c100002519", "6", "p", "nextShows")]
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
        public async Task LegacyGenrePaginationRejectsUnrelatedIdsAndNonPageLinks(string genre, string query)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BrowseProviderLink("https://opml.radiotime.com/Browse.ashx?" + query, musicId: genre));
            Assert.Contains("pagination escaped", error.Message);
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
