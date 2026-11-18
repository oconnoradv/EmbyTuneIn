using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Plugins.TuneIn.Configuration;
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

            Assert.Equal(HttpUtility.UrlDecode(filter),
                HttpUtility.ParseQueryString(new Uri(request).Query)["filter"]);
            Assert.Equal("", new Uri(request).Fragment);
            Assert.Contains("%23", request);
        }

        [Theory]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~%23")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?filter=p%3A~%23&id=c57941&offset=26")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~A")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~%C3%89")]
        public async Task OtherAlphabetFiltersKeepTheirValues(string url)
        {
            var request = await BrowseProviderLink(url);

            Assert.Equal(HttpUtility.ParseQueryString(new Uri(url).Query)["filter"],
                HttpUtility.ParseQueryString(new Uri(request).Query)["filter"]);
            Assert.Equal("", new Uri(request).Fragment);
        }

        [Theory]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~#section")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&id=c57941#")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&filter=s:~#")]
        [InlineData("http://example.com/Browse.ashx?filter=s:~#")]
        [InlineData("http://opml.radiotime.com/Tune.ashx?filter=s:~#")]
        public void InvalidPaginationDestinationsRemainRejected(string url)
        {
            Assert.Throws<InvalidDataException>(() => DirectoryRequest.ParseLink(url));
        }

        private static async Task<string> BrowseProviderLink(string url)
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1,
                ConfigurationCompleted = true,
                EnableTalk = true
            };
            var requests = new List<string>();
            var directory = new TuneInDirectory(() => config, (request, token) =>
            {
                requests.Add(request.Url);
                var body = new XElement("body");
                if (requests.Count == 1)
                    body.Add(new XElement("outline", new XAttribute("type", "link"),
                        new XAttribute("key", "nextStations"), new XAttribute("text", "More Stations"),
                        new XAttribute("URL", url)));
                return Task.FromResult(new XDocument(new XElement("opml",
                    new XElement("head", new XElement("status", "200")), body)));
            });
            var start = new DirectoryRequest();
            start.Parameters["id"] = "c57941";
            var folder = directory.Register(directory.Snapshot(), new NavigationEntry
            {
                Category = "talk",
                Request = start
            });
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var page = await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = folder
            }, CancellationToken.None);
            var next = Assert.Single(page.Items);
            await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = next.Id
            }, CancellationToken.None);

            Assert.Equal(2, requests.Count);
            var requestUri = new Uri(requests[1]);
            Assert.Equal("https", requestUri.Scheme);
            Assert.Equal("opml.radiotime.com", requestUri.Host);
            Assert.Equal("/Browse.ashx", requestUri.AbsolutePath);
            var parameters = HttpUtility.ParseQueryString(requestUri.Query);
            Assert.Equal("c57941", parameters["id"]);
            Assert.Equal("26", parameters["offset"]);
            return requests[1];
        }
    }
}
