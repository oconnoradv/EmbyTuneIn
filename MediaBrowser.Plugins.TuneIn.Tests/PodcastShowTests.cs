using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class PodcastShowTests
    {
        private static string Row(string episode, string show, string date = "", string host = "opml.radiotime.com", string urlId = null) =>
            $"<outline type='audio' guide_id='{episode}' date='{date}' URL='https://{host}/Tune.ashx?id={urlId ?? episode}&amp;sid={show}'/>";

        private static string Listing(string rows, string order = "") =>
            $"<opml><head><status>200</status></head><body><outline key='topics' order='{order}'>{rows}</outline></body></opml>";

        [Theory]
        [InlineData("Latest", "t2")]
        [InlineData("Oldest", "t1")]
        [InlineData("Provider", "t2")]
        public void DatedEpisodesHonorRequestedOrder(string order, string expected)
        {
            var listing = Listing(Row("t1", "p1", "2026-01-01") + Row("t2", "p1", "2026-02-01"));
            Assert.Equal(expected, TuneInPlayback.SelectPodcastEpisode(listing, "p1", order, out var inferred));
            Assert.False(inferred);
        }

        [Fact]
        public void ExplicitProviderOrderIsUsedInsteadOfChronologicalOrder()
        {
            var listing = Listing(Row("t1", "p1", "2026-01-01") + Row("t2", "p1", "2026-02-01"), "asc");
            Assert.Equal("t1", TuneInPlayback.SelectPodcastEpisode(listing, "p1", "Provider", out var inferred));
            Assert.False(inferred);
        }

        [Theory]
        [InlineData("Latest", "t1")]
        [InlineData("Oldest", "t2")]
        [InlineData("Provider", "t1")]
        public void MissingDatesUsesObservedOrderAndReportsFallback(string order, string expected)
        {
            var listing = Listing(Row("t1", "p1") + Row("t2", "p1"));
            Assert.Equal(expected, TuneInPlayback.SelectPodcastEpisode(listing, "p1", order, out var inferred));
            Assert.True(inferred);
        }

        [Fact]
        public void EpisodeMustMatchShowAndTrustedTuneLink()
        {
            var listing = Listing(Row("t1", "p2") + Row("t2", "p1", host: "example.com")
                + Row("t3", "p1", urlId: "t4") + Row("t5", "p1"));
            Assert.Equal("t5", TuneInPlayback.SelectPodcastEpisode(listing, "p1", "Latest", out _));
            Assert.Throws<InvalidDataException>(() => TuneInPlayback.SelectPodcastEpisode(
                Listing(Row("t1", "p2")), "p1", "Latest", out _));
        }

        [Theory]
        [InlineData("podcast", "p760", "p760", 1)]
        [InlineData("podcast", "p760", "p761", 0)]
        [InlineData("music", "p760", "p760", 0)]
        public async Task ShowLinksRequirePodcastCategoryAndMatchingIds(string category, string guide, string linkId, int count)
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true,
                EnablePodcasts = true, EnableMusic = true };
            var row = $"<outline type='link' text='Show' guide_id='{guide}' URL='https://opml.radiotime.com/Tune.ashx?c=pbrowse&amp;id={linkId}'/>";
            var directory = new TuneInDirectory(() => config,
                (_, __) => Task.FromResult(XDocument.Parse("<opml><body>" + row + "</body></opml>")));
            var snapshot = directory.Snapshot();
            var parent = new NavigationEntry { Category = category,
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?id=g2804&filter=p:topic") };
            var items = (await new TuneInChannel(directory).GetChannelItems(new InternalChannelItemQuery
                { FolderId = directory.Register(snapshot, parent) }, CancellationToken.None)).Items.ToArray();
            Assert.Equal(count, items.Length);
            if (count != 0)
            {
                Assert.Equal(ChannelItemType.Media, items[0].Type);
                Assert.True(directory.Resolve(snapshot, items[0].Id).PodcastShow);
            }
        }
    }
}
