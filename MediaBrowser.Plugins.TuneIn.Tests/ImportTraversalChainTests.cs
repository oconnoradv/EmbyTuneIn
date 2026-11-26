using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class ImportTraversalChainTests
    {
        private static ChannelItemResult Items(params ChannelItemInfo[] items) =>
            new ChannelItemResult { Items = items.ToList() };

        private static ChannelItemInfo Folder(string id) =>
            new ChannelItemInfo { Id = id, Name = id, Type = ChannelItemType.Folder };

        private static ChannelItemInfo Media(string id) =>
            new ChannelItemInfo { Id = id, Name = id, Type = ChannelItemType.Media };

        private sealed class Fixture
        {
            internal TuneInDirectory Directory;
            internal TuneInChannel Channel;
            internal int Fetches;
        }

        private static Fixture CreateFixture()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1,
                ConfigurationCompleted = true,
                EnableTalk = true,
                EnableSports = true
            };
            var fixture = new Fixture();
            fixture.Directory = new TuneInDirectory(() => config, (request, token) =>
            {
                Interlocked.Increment(ref fixture.Fetches);
                return Task.FromResult(XDocument.Parse(
                    "<opml><body><outline type='audio' guide_id='s9' text='Live Radio'/></body></opml>"));
            });
            fixture.Channel = new TuneInChannel(fixture.Directory);
            return fixture;
        }

        private static async Task<TuneInStagedImport> StageTree(Fixture fixture)
        {
            var root = fixture.Channel.GetCategoryRoot(fixture.Directory.Snapshot(), "talk");
            var listings = new Dictionary<string, ChannelItemResult>(StringComparer.Ordinal)
            {
                [root.Id] = Items(Folder("a"), Folder("b")),
                ["a"] = Items(Folder("d")),
                ["b"] = Items(Folder("d")),
                ["d"] = Items(Media("s1"))
            };
            return await TuneInStagedImport.DiscoverAsync(fixture.Channel, root,
                new TuneInCatalogueSession(null, TimeSpan.FromMinutes(1), () => DateTime.UtcNow),
                CancellationToken.None,
                (id, _, _, _) => Task.FromResult(listings[id]), null, null, (_, _) => false);
        }

        [Fact]
        public async Task StagedImportRejectsAFolderReadThatSkipsItsParents()
        {
            var fixture = CreateFixture();
            var staged = await StageTree(fixture);

            Assert.Null(staged.Read("d", CancellationToken.None));

            var failure = Assert.Throws<InvalidOperationException>(() => staged.Verify());
            Assert.Contains("Folders served: 0/4", failure.Message);
        }

        [Fact]
        public async Task StagedImportServesTheTraversalInParentOrder()
        {
            var fixture = CreateFixture();
            var staged = await StageTree(fixture);

            Assert.NotNull(staged.Read(staged.Root.Id, CancellationToken.None));
            Assert.NotNull(staged.Read("a", CancellationToken.None));
            Assert.NotNull(staged.Read("b", CancellationToken.None));
            Assert.NotNull(staged.Read("d", CancellationToken.None));
            staged.Verify();
        }

        [Fact]
        public async Task StagedImportServesARepeatedFolderThroughEitherParent()
        {
            var fixture = CreateFixture();
            var staged = await StageTree(fixture);

            Assert.NotNull(staged.Read(staged.Root.Id, CancellationToken.None));
            Assert.NotNull(staged.Read("b", CancellationToken.None));
            Assert.NotNull(staged.Read("d", CancellationToken.None));
            Assert.NotNull(staged.Read("a", CancellationToken.None));
            staged.Verify();
        }

        [Fact]
        public async Task StagedImportDeclinesAForeignReadWithoutPoisoningTheImport()
        {
            var fixture = CreateFixture();
            var staged = await StageTree(fixture);
            using (var token = new CancellationTokenSource())
            {
                await fixture.Channel.ImportAsync(staged, token.Token, () =>
                {
                    Assert.Null(staged.Read("d", CancellationToken.None));
                    foreach (var id in new[] { staged.Root.Id, "a", "b", "d" })
                        Assert.NotNull(staged.Read(id, token.Token));
                    return Task.CompletedTask;
                });
            }
        }

        [Fact]
        public async Task StagedImportKeepsAChannelReadOutsideItsTreeOnTheNormalPath()
        {
            var fixture = CreateFixture();
            var staged = await StageTree(fixture);
            var sports = fixture.Channel.GetCategoryRoot(fixture.Directory.Snapshot(), "sports").Id;
            var before = fixture.Fetches;
            using (var token = new CancellationTokenSource())
            {
                await fixture.Channel.ImportAsync(staged, token.Token, async () =>
                {
                    var outside = await fixture.Channel.GetChannelItems(
                        new InternalChannelItemQuery { FolderId = sports }, CancellationToken.None);
                    Assert.NotNull(outside);
                    Assert.DoesNotContain(outside.Items, item => item.Name == "s1");
                    Assert.True(fixture.Fetches > before, "the declined read did not use the normal path");
                    Assert.NotNull(staged.Read(staged.Root.Id, token.Token));
                    foreach (var id in new[] { "a", "b", "d" })
                        Assert.NotNull(staged.Read(id, token.Token));
                }, allowChangedToken: true);
            }
        }
    }
}
