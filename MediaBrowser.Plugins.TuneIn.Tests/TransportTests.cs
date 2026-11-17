using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using MediaBrowser.Plugins.TuneIn.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System.Reflection;
using MediaBrowser.Controller.Channels;
using System.Xml.Linq;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class TransportTests
    {
        [Fact]
        [Trait("Category", "Live")]
        public async Task LiveCatalogueWorksThroughPinnedDnsAndVerifiedTls()
        {
            var transport = new SafeHttpTransport();
            int? receivedStatus = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var response = await transport.OpenAsync(
                "https://opml.radiotime.com/Browse.ashx?c=lang", true, timeout.Token, status => receivedStatus = status))
            {
                var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 2 * 1024 * 1024, timeout.Token);
                var document = TuneInDirectory.ParseCatalogue(bytes);
                Assert.NotEmpty(document.Descendants("outline"));
                Assert.Equal(200, receivedStatus);
                Assert.DoesNotContain(document.Descendants("outline"), row => TuneInDirectory.Attribute(row, "type") == "audio");
            }
        }

        [Fact]
        [Trait("Category", "Live")]
        public async Task LiveMusicAlphabetLinksParseThroughPinnedCatalogueTransport()
        {
            var transport = new SafeHttpTransport();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var response = await transport.OpenAsync(
                "https://opml.radiotime.com/Browse.ashx?pivot=name&id=c57941&filter=s", true, timeout.Token))
            {
                var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 2 * 1024 * 1024, timeout.Token);
                var document = TuneInDirectory.ParseCatalogue(bytes);
                var links = document.Descendants("outline")
                    .Where(row => TuneInDirectory.Attribute(row, "type") == "link")
                    .Select(row => DirectoryRequest.ParseLink(TuneInDirectory.Attribute(row, "URL"))).ToArray();
                Assert.Contains(links, link => link.Parameters.TryGetValue("filter", out var filter) && filter == "s:~#");
                Assert.Contains(links, link => link.Parameters.TryGetValue("filter", out var filter) && filter == "s:~A");
            }
        }

        [Fact]
        [Trait("Category", "Live")]
        public async Task LiveMusicHashPaginationParsesThroughPinnedCatalogueTransport()
        {
            var transport = new SafeHttpTransport();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var response = await transport.OpenAsync(
                "https://opml.radiotime.com/Browse.ashx?filter=s%3A~%23&id=c57941", true, timeout.Token))
            {
                var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 2 * 1024 * 1024, timeout.Token);
                var document = TuneInDirectory.ParseCatalogue(bytes);
                var next = Assert.Single(document.Descendants("outline"), row =>
                    TuneInDirectory.Attribute(row, "key") == "nextStations");
                var link = DirectoryRequest.ParseLink(TuneInDirectory.Attribute(next, "URL"));
                Assert.Equal("s:~#", link.Parameters["filter"]);
                Assert.Equal("c57941", link.Parameters["id"]);
                Assert.True(link.Parameters.ContainsKey("offset"));
                Assert.DoesNotContain("#", link.Url);
            }
        }

        [Fact]
        [Trait("Category", "Live")]
        public async Task LiveAdvertisedStationArtworkWorksThroughPinnedCdnTransport()
        {
            var entry = new NavigationEntry
            {
                Category = "talk",
                StationId = "s151799",
                ArtworkReference = "0:/s151799/images/logoq.jpg?t=636355620405200000"
            };
            var artwork = await TuneInArtwork.OpenAsync(entry, new SafeHttpTransport(), CancellationToken.None);
            Assert.Equal("image/jpeg", artwork.ContentType);
            Assert.True(artwork.Bytes.Length > 100);
        }

        [Fact]
        [Trait("Category", "Live")]
        public async Task LiveLanguageMusicSelectionUsesPinnedCatalogueAndRetainsBothScopes()
        {
            var config = new PluginConfiguration
            {
                SchemaVersion = 1,
                ConfigurationCompleted = true,
                EnableLanguage = true,
                EnableMusic = true
            };
            config.Languages.Add(new CatalogSelection { Id = "l1", Name = "Selected language" });
            config.Music.Add(new CatalogSelection { Id = "c57951", Name = "Selected music" });
            var directory = new TuneInDirectory(() => config);
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var snapshot = directory.Snapshot();
            var id = directory.Register(snapshot, new NavigationEntry
            {
                Category = "language",
                SelectionId = "l1",
                ContentCategory = "music",
                Request = DirectoryRequest.ParseLink("https://opml.radiotime.com/Browse.ashx?c=music")
            });
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                var selections = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = id }, timeout.Token);
                var selected = Assert.Single(selections.Items);
                var page = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = selected.Id }, timeout.Token);
                Assert.Contains(page.Items, item => directory.Resolve(snapshot, item.Id).StationId != null);
                Assert.All(page.Items, item =>
                {
                    var entry = directory.Resolve(snapshot, item.Id);
                    Assert.Equal("l1", entry.SelectionId);
                    Assert.Equal("c57951", entry.MusicSelectionId);
                    if (entry.Request != null)
                        Assert.EndsWith("l1", TuneInChannel.CatalogueRequest(snapshot, entry).Parameters["filter"]);
                });
            }
        }

        [Theory]
        [InlineData("127.0.0.1", false)]
        [InlineData("10.1.2.3", false)]
        [InlineData("172.31.1.1", false)]
        [InlineData("192.168.1.1", false)]
        [InlineData("169.254.169.254", false)]
        [InlineData("100.64.1.1", false)]
        [InlineData("198.18.0.1", false)]
        [InlineData("192.0.2.1", false)]
        [InlineData("224.0.0.1", false)]
        [InlineData("::1", false)]
        [InlineData("::ffff:127.0.0.1", false)]
        [InlineData("fe80::1", false)]
        [InlineData("fc00::1", false)]
        [InlineData("2001:db8::1", false)]
        [InlineData("2002:7f00:1::", false)]
        [InlineData("8.8.8.8", true)]
        [InlineData("2606:4700:4700::1111", true)]
        public void SpecialUseDestinationsAreRejected(string address, bool isPublic) =>
            Assert.Equal(isPublic, PublicAddressPolicy.IsPublic(IPAddress.Parse(address)));

        [Theory]
        [InlineData("http://opml.radiotime.com/Browse.ashx?id=r0")]
        [InlineData("https://opml.radiotime.com:444/Browse.ashx?id=r0")]
        [InlineData("https://evil.test/Browse.ashx?id=r0")]
        [InlineData("https://opml.radiotime.com/Browse.ashx/evil")]
        [InlineData("https://user:password@opml.radiotime.com/Browse.ashx")]
        public void CatalogueNetworkPolicyIsHttpsExactHostAndExactPath(string url) =>
            Assert.Throws<InvalidOperationException>(() => PublicAddressPolicy.ValidateUri(url, true));

        [Fact]
        public async Task MixedPublicAndPrivateDnsFailsBeforeConnecting()
        {
            var connections = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
                (_, __, ___) => { connections++; throw new Exception("Must not connect"); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(0, connections);
        }

        [Fact]
        public async Task RedirectToPrivateHostIsRejectedBeforeSecondConnection()
        {
            var connections = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "radio.test" ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback }),
                (_, __, ___) =>
                {
                    connections++;
                    return Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 302 Found\r\nLocation: https://127.0.0.1/secret\r\n\r\n"));
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(1, connections);
        }

        [Fact]
        public async Task TlsDowngradeAndCatalogueCrossHostRedirectsFailClosed()
        {
            foreach (var destination in new[] { "http://opml.radiotime.com/Browse.ashx", "https://evil.test/Browse.ashx" })
            {
                var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                    (_, __, ___) => Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 302 Found\r\nLocation: " + destination + "\r\n\r\n")));
                await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync(
                    "https://opml.radiotime.com/Browse.ashx?id=r0", true, CancellationToken.None));
            }
        }

        [Fact]
        public async Task AddressIsPinnedAndPathCasePreservedAndChunkedBodyDecoded()
        {
            var lookups = 0;
            DuplexStream connection = null;
            var transport = new SafeHttpTransport(_ =>
            {
                lookups++;
                return Task.FromResult(new[] { lookups == 1 ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback });
            }, (address, uri, token) =>
            {
                Assert.Equal("8.8.8.8", address.ToString());
                Assert.Equal("radio.test", uri.DnsSafeHost);
                connection = new DuplexStream("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: audio/mpeg\r\n\r\n4\r\nTest\r\n3\r\n123\r\n0\r\n\r\n");
                return Task.FromResult<Stream>(connection);
            });
            using (var response = await transport.OpenAsync("https://radio.test/CaseSensitive?Token=AbC", false, CancellationToken.None))
            {
                var body = await TuneInDirectory.ReadBoundedAsync(response.Body, 100, CancellationToken.None);
                Assert.Equal("Test123", Encoding.ASCII.GetString(body));
                Assert.Contains("GET /CaseSensitive?Token=AbC HTTP/1.1", connection.Request);
                Assert.Contains("Host: radio.test", connection.Request);
            }
            Assert.Equal(1, lookups);
        }

        [Fact]
        public async Task TruncatedAndOversizedResponsesAreErrorsNotSuccessFallbacks()
        {
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nshort")));
            using (var response = await transport.OpenAsync("https://radio.test/live", false, CancellationToken.None))
                await Assert.ThrowsAsync<EndOfStreamException>(() => TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => TuneInDirectory.ReadBoundedAsync(new MemoryStream(new byte[20]), 10, CancellationToken.None));
        }

        [Fact]
        public void PlaylistParsingPreservesCaseSkipsCommentsAndIndexesPlsCorrectly()
        {
            var urls = TuneInDirectory.PlaylistUrls("[playlist]\nNumberOfEntries=2\nFile1=https://radio.test/CaseA\nFile2=https://radio.test/CaseB\n#comment").ToArray();
            Assert.Equal(new[] { "https://radio.test/CaseA", "https://radio.test/CaseB" }, urls);
            Assert.Throws<InvalidDataException>(() => TuneInDirectory.PlaylistUrls(
                "https://cdn-cms.tunein.com/service/Audio/nostream.enUS.mp3").ToArray());
        }

        [Fact]
        public async Task PlsAlternativesAreTriedWithExplicitWarningsAndNoRemoteUrlHandoff()
        {
            var requests = 0;
            var warnings = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, uri, __) =>
                {
                    requests++;
                    if (uri.Host == "opml.radiotime.com") return Task.FromResult<Stream>(Reply("text/plain", "https://radio.test/playlist.pls"));
                    if (uri.AbsolutePath == "/playlist.pls") return Task.FromResult<Stream>(Reply("audio/x-scpls",
                        "[playlist]\nFile1=https://radio.test/dead\nFile2=https://radio.test/CaseLive"));
                    if (uri.AbsolutePath == "/dead") return Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 503 Unavailable\r\n\r\n"));
                    Assert.Equal("/CaseLive", uri.AbsolutePath);
                    return Task.FromResult<Stream>(Reply("audio/mpeg", "synthetic-test-audio"));
                });
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, transport: transport);
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(),
                new NavigationEntry { Category = "talk", StationId = "s1" }, CancellationToken.None, message =>
                {
                    warnings++;
                    Assert.DoesNotContain("https://", message);
                }))
                Assert.Equal("synthetic-test-audio", Encoding.UTF8.GetString(
                    await TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None)));
            Assert.Equal(4, requests);
            Assert.Equal(1, warnings);
        }

        [Fact]
        public async Task PrivateAlternativeIsRejectedRatherThanSilentlyFallingBack()
        {
            var requests = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "127.0.0.1" ? IPAddress.Loopback : IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) =>
                {
                    requests++;
                    return Task.FromResult<Stream>(Reply("text/plain", "https://127.0.0.1/secret\nhttps://radio.test/live"));
                });
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, transport: transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => directory.OpenStreamAsync(directory.Snapshot(),
                new NavigationEntry { Category = "talk", StationId = "s1" }, CancellationToken.None, _ => throw new Exception("No silent security fallback")));
            Assert.Equal(1, requests);
        }

        private static Stream Reply(string type, string body) => new DuplexStream(
            "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: "
            + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);

        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public async Task RadioResponsesAcceptLfLinesAndRepeatedAdvisoryHeaders(string newline)
        {
            var response = string.Join(newline, new[] { "HTTP/1.1 200 OK", "Content-Type: audio/mpeg",
                "icy-br: 128", "Icy-Br: 128", "Access-Control-Allow-Origin: *", "Access-Control-Allow-Origin: *",
                "Content-Length: 4", "", "LIVE" });
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(response)));
            using (var stream = await transport.OpenAsync("https://radio.test/live", false, CancellationToken.None))
            {
                Assert.Equal("audio/mpeg", stream.ContentType);
                Assert.Equal("LIVE", Encoding.ASCII.GetString(
                    await TuneInDirectory.ReadBoundedAsync(stream.Body, 100, CancellationToken.None)));
            }
        }

        [Theory]
        [InlineData("Content-Length: 4\r\ncontent-length: 4")]
        [InlineData("Transfer-Encoding: chunked\r\ntransfer-encoding: chunked")]
        [InlineData("Content-Encoding: identity\r\ncontent-encoding: identity")]
        [InlineData("Content-Type: audio/mpeg\r\ncontent-type: audio/mpeg")]
        [InlineData("Location: https://radio.test/one\r\nlocation: https://radio.test/two")]
        [InlineData("Content-Length : 4")]
        [InlineData("icy-name: bad\rvalue")]
        public async Task RadioFramingAmbiguityAndMalformedHeadersRemainRejected(string headers)
        {
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 200 OK\r\n" + headers + "\r\n\r\nLIVE")));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
        }

        [Theory]
        [InlineData("HTTP/1.1 200 OK\nContent-Length: 4\n\nLIVE")]
        [InlineData("HTTP/1.1 200 OK\r\nicy-br: 128\r\nicy-br: 128\r\n\r\nLIVE")]
        public async Task CatalogueResponsesRetainStrictLineAndDuplicatePolicy(string response)
        {
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(response)));
            await Assert.ThrowsAsync<InvalidDataException>(() => transport.OpenAsync(
                "https://opml.radiotime.com/Tune.ashx?id=s1", true, CancellationToken.None));
        }

        [Fact]
        public async Task LfRadioRedirectStillRejectsPrivateDnsBeforeConnecting()
        {
            var connections = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "radio.test" ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback }),
                (_, __, ___) =>
                {
                    connections++;
                    return Task.FromResult<Stream>(new DuplexStream(
                        "HTTP/1.1 302 Found\nLocation: https://private.test/live\n\n"));
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(1, connections);
        }

        [Fact]
        public async Task LegacyRadioIdentityTransferIsCloseDelimitedWithoutChunkDecoding()
        {
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(
                    "HTTP/1.1 200 OK\nContent-Type: audio/mpeg\nTransfer-Encoding: identity\n\nLIVE")));
            using (var response = await transport.OpenAsync("https://radio.test/live", false, CancellationToken.None))
                Assert.Equal("LIVE", Encoding.ASCII.GetString(
                    await TuneInDirectory.ReadBoundedAsync(response.Body, 100, CancellationToken.None)));
        }

        [Theory]
        [InlineData("identity\r\nContent-Length: 4")]
        [InlineData("chunked\r\nContent-Length: 4")]
        [InlineData("identity, chunked")]
        [InlineData("gzip")]
        public async Task TransferCompatibilityStillRejectsAmbiguousOrUnsupportedFraming(string transfer)
        {
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(
                    "HTTP/1.1 200 OK\r\nTransfer-Encoding: " + transfer + "\r\n\r\nLIVE")));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
        }

        [Fact]
        public async Task RadioLfCompatibilityDoesNotRelaxChunkFraming()
        {
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream(
                    "HTTP/1.1 200 OK\nTransfer-Encoding: chunked\n\n4\nLIVE\n0\n\n")));
            using (var response = await transport.OpenAsync("https://radio.test/live", false, CancellationToken.None))
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    TuneInDirectory.ReadBoundedAsync(response.Body, 100, CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RelayUsesInjectedFactoryAndTransfersOrReleasesStreamOwnership(bool factoryFails)
        {
            var audio = (DuplexStream)Reply("audio/mpeg", "synthetic-test-audio");
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, uri, __) => Task.FromResult<Stream>(uri.Host == "opml.radiotime.com"
                    ? Reply("text/plain", "https://radio.test/live") : audio));
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, transport: transport);
            var id = directory.Register(directory.Snapshot(), new NavigationEntry { Category = "talk", StationId = "s1" });
            var factory = DispatchProxy.Create<IHttpResultFactory, RelayFactoryProxy>();
            var capture = (RelayFactoryProxy)(object)factory;
            capture.Fail = factoryFails;
            var service = new TuneInService(DispatchProxy.Create<ILogManager, RelayRequestProxy>(), null, factory, directory)
            { Request = DispatchProxy.Create<IRequest, RelayRequestProxy>() };
            if (factoryFails)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.Get(new GetTuneInRelay { Id = id }));
                Assert.True(audio.Disposed);
            }
            else
            {
                using (var result = Assert.IsAssignableFrom<Stream>(await service.Get(new GetTuneInRelay { Id = id })))
                {
                    Assert.False(audio.Disposed);
                    Assert.Equal("audio/mpeg", capture.ContentType);
                    Assert.Equal("synthetic-test-audio", Encoding.UTF8.GetString(
                        await TuneInDirectory.ReadBoundedAsync(result, 1000, CancellationToken.None)));
                }
                Assert.True(audio.Disposed);
            }
        }

        public class RelayRequestProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetLogger") return null;
                if (method.Name == "get_RemoteIp") return IPAddress.Loopback;
                if (method.Name == "get_IsSocketConnectionLocal") return true;
                if (method.Name == "get_CancellationToken") return CancellationToken.None;
                throw new InvalidOperationException("Unexpected relay dependency access.");
            }
        }

        public class RelayFactoryProxy : DispatchProxy
        {
            public bool Fail { get; set; }
            public string ContentType { get; private set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.Equal("GetResult", method.Name);
                ContentType = (string)args[2];
                if (Fail) throw new InvalidOperationException("Synthetic response construction failure.");
                return args[1];
            }
        }

        [Fact]
        public async Task CategoryAndStationArtworkUseLocalUrlsAndSignedCdnReferencesSurviveRestart()
        {
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, (_, __) => Task.FromResult(XDocument.Parse(
                "<opml><head><status>200</status></head><body><outline type='audio' guide_id='s1' text='One'"
                + " image='http://cdn-profiles.tunein.com/s1/images/CaseLogo.png?t=1'/></body></opml>")));
            var channel = new TuneInChannel(ConfigurationAndNavigationTests.ArtworkHost(), null, directory);
            var root = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None)).Items);
            Assert.StartsWith("http://127.0.0.1:8096/emby/TuneInFork/Artwork/", root.ImageUrl);
            var station = Assert.Single((await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = root.Id }, CancellationToken.None)).Items);
            Assert.StartsWith("http://127.0.0.1:8096/emby/TuneInFork/Artwork/", station.ImageUrl);
            var restarted = new TuneInDirectory(() => config);
            Assert.Equal(directory.Register(directory.Snapshot(), new NavigationEntry
            { Category = "talk", StationId = "s1", Name = "One" }), station.Id);
            var entry = restarted.Resolve(restarted.Snapshot(), station.ImageUrl.Substring(station.ImageUrl.LastIndexOf('/') + 1));
            Assert.Equal("0:/s1/images/CaseLogo.png?t=1", entry.ArtworkReference);
            Assert.DoesNotContain("cdn-profiles", station.ImageUrl);
        }

        [Theory]
        [InlineData("local")]
        [InlineData("music")]
        [InlineData("language")]
        [InlineData("location")]
        [InlineData("talk")]
        [InlineData("sports")]
        [InlineData("podcast")]
        [InlineData("presets")]
        public async Task BundledGroupsUseTheirOwnArtworkWithoutRemoteRequests(string category)
        {
            var transport = new SafeHttpTransport(_ => throw new Exception("Group artwork must stay local."));
            var image = await TuneInArtwork.OpenAsync(new NavigationEntry { Category = category }, transport, CancellationToken.None);
            Assert.Equal("image/png", image.ContentType);
            TuneInArtwork.ValidateRaster(image.Bytes, image.ContentType);
            Assert.True(image.Bytes.Length > 1000);
        }

        [Theory]
        [InlineData("music")]
        [InlineData("talk")]
        [InlineData("sports")]
        public async Task LanguageSubgroupsUseTheirContentArtworkRatherThanLanguageArtwork(string group)
        {
            var transport = new SafeHttpTransport(_ => throw new Exception("Bundled artwork must stay local."));
            var expected = await TuneInArtwork.OpenAsync(new NavigationEntry { Category = group }, transport, CancellationToken.None);
            var nested = await TuneInArtwork.OpenAsync(new NavigationEntry { Category = "language", ContentCategory = group },
                transport, CancellationToken.None);
            var language = await TuneInArtwork.OpenAsync(new NavigationEntry { Category = "language" }, transport, CancellationToken.None);
            Assert.Equal(expected.Bytes, nested.Bytes);
            Assert.False(language.Bytes.SequenceEqual(nested.Bytes));
        }

        [Theory]
        [InlineData("https://127.0.0.1/private")]
        [InlineData("https://cdn-profiles.tunein.com.evil.test/logo.png")]
        [InlineData("https://user@cdn-profiles.tunein.com/logo.png")]
        [InlineData("https://cdn-profiles.tunein.com:8443/logo.png")]
        [InlineData("https://cdn-profiles.tunein.com/logo.png#fragment")]
        public void ArtworkMetadataCannotIntroduceOtherHostsCredentialsPortsOrFragments(string url) =>
            Assert.False(TuneInArtwork.TryReference(url, out _));

        [Fact]
        public async Task ArtworkEndpointUsesInjectedFactoryAndPinnedHttpsCdnOnly()
        {
            var png = BundledPng();
            var requests = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, uri, __) =>
                {
                    requests++;
                    Assert.Equal("https", uri.Scheme);
                    Assert.Equal("cdn-profiles.tunein.com", uri.Host);
                    Assert.Equal("/s1/images/CaseLogo.png", uri.AbsolutePath);
                    return Task.FromResult<Stream>(ReplyBinary("image/png", png));
                });
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, transport: transport);
            var id = directory.Register(directory.Snapshot(), new NavigationEntry
            { Category = "talk", StationId = "s1", ArtworkReference = "0:/s1/images/CaseLogo.png" });
            var factory = DispatchProxy.Create<IHttpResultFactory, RelayFactoryProxy>();
            var service = new TuneInService(DispatchProxy.Create<ILogManager, RelayRequestProxy>(), null, factory, directory)
            { Request = DispatchProxy.Create<IRequest, RelayRequestProxy>() };
            var result = Assert.IsType<ReadOnlyMemory<byte>>(await service.Get(new GetTuneInArtwork { Id = id }));
            Assert.Equal(png, result.ToArray());
            Assert.Equal(1, requests);
            config.EnableTalk = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.Get(new GetTuneInArtwork { Id = id }));
            Assert.Equal(1, requests);
        }

        [Fact]
        public async Task ArtworkRedirectAndPrivateDnsCannotEscapeTheTrustedCdnPolicy()
        {
            var calls = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) =>
                {
                    calls++;
                    return Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 302 Found\r\nLocation: https://other.test/logo.png\r\n\r\n"));
                });
            var entry = new NavigationEntry { Category = "talk", StationId = "s1", ArtworkReference = "0:/logo.png" };
            await Assert.ThrowsAsync<InvalidOperationException>(() => TuneInArtwork.OpenAsync(entry, transport, CancellationToken.None));
            Assert.Equal(1, calls);
            transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Loopback }),
                (_, __, ___) => throw new Exception("Private artwork destination must not connect."));
            await Assert.ThrowsAsync<InvalidOperationException>(() => TuneInArtwork.OpenAsync(entry, transport, CancellationToken.None));
        }

        [Fact]
        public void ArtworkRejectsActiveContentAndExcessiveRasterDimensions()
        {
            Assert.Throws<InvalidDataException>(() => TuneInArtwork.ValidateRaster(Encoding.UTF8.GetBytes("<svg/>"), "image/svg+xml"));
            var png = BundledPng();
            png[16] = 127;
            Assert.Throws<InvalidDataException>(() => TuneInArtwork.ValidateRaster(png, "image/png"));
            Assert.Throws<InvalidDataException>(() => TuneInArtwork.ValidateRaster(new byte[] { 255, 216, 255, 217 }, "image/jpeg"));
        }

        [Fact]
        public async Task ArtworkDownloadRejectsBytesBeyondTheTwoMiBLimit()
        {
            var bytes = new byte[2 * 1024 * 1024 + 1];
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult(ReplyBinary("image/png", bytes)));
            await Assert.ThrowsAsync<InvalidDataException>(() => TuneInArtwork.OpenAsync(new NavigationEntry
            { Category = "talk", StationId = "s1", ArtworkReference = "0:/logo.png" }, transport, CancellationToken.None));
        }

        private static byte[] BundledPng()
        {
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("MediaBrowser.Plugins.TuneIn.Images.primary.png"))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static Stream ReplyBinary(string type, byte[] body) => new DuplexStream(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: "
                + body.Length + "\r\n\r\n").Concat(body).ToArray());

        [Fact]
        public async Task UnconfiguredPlaybackMakesZeroRequests()
        {
            var requests = 0;
            var transport = new SafeHttpTransport(_ =>
            {
                requests++;
                throw new Exception("Playback must not reach DNS before setup.");
            });
            var config = new PluginConfiguration();
            var directory = new TuneInDirectory(() => config, transport: transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => directory.OpenStreamAsync(directory.Snapshot(),
                new NavigationEntry { Category = "talk", StationId = "s1" }, CancellationToken.None, _ => { }));
            Assert.Equal(0, requests);
        }

        private sealed class DuplexStream : MemoryStream
        {
            private readonly MemoryStream output = new MemoryStream();
            public bool Disposed { get; private set; }
            public DuplexStream(string response) : base(Encoding.ASCII.GetBytes(response)) { }
            public DuplexStream(byte[] response) : base(response) { }
            public string Request => Encoding.ASCII.GetString(output.ToArray());
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                output.WriteAsync(buffer, offset, count, token);
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Disposed = true; output.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
