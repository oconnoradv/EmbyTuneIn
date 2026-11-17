using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaBrowser.Plugins.TuneIn
{
    public class TuneInChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
    {
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly TuneInDirectory directory;

        public TuneInChannel(IHttpClient httpClient, ILogManager logManager, IApplicationHost appHost)
            : this(TuneInRequests.Directory(httpClient, appHost), httpClient, logManager.GetLogger(typeof(TuneInChannel).Name))
        {
        }

        internal TuneInChannel(TuneInDirectory directory, IHttpClient httpClient = null, ILogger logger = null)
        {
            this.directory = directory;
            _httpClient = httpClient;
            _logger = logger;
        }

        public string DataVersion => "47";
        public string Description => "Listen to online radio, find streaming music radio and streaming talk radio with TuneIn.";
        public bool IsEnabledFor(string userId) => true;

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken) =>
            new TuneInBrowsing(directory, GetDefaultImages).GetItemsAsync(query.FolderId, cancellationToken);

        private Task<HttpResponseInfo> SendRequest(HttpRequestOptions options, string method) =>
            TuneInRequests.SendAsync(_httpClient, options, method);

        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var snapshot = directory.Snapshot();
            var entry = directory.Resolve(snapshot, id);
            if (!TuneInBrowsing.ValidStation(entry.StationId))
                throw new InvalidOperationException("TuneIn item is not playable.");
            var request = new DirectoryRequest { Method = "Tune.ashx" };
            request.Parameters["id"] = entry.StationId;
            request.Parameters["partnerid"] = "uD1X52pA";
            request.Parameters["formats"] = "mp3,aac";
            if (entry.Category == "presets") request.Parameters["username"] = snapshot.Value.Username;
            var items = new List<MediaSourceInfo>();

            using (var outerResponse = await SendRequest(new HttpRequestOptions
            {
                Url = request.Url,
                CancellationToken = cancellationToken
            }, "GET").ConfigureAwait(false))
            {
                using (var site = outerResponse.Content)
                {
                    using (var reader = new StreamReader(site))
                    {
                        while (!reader.EndOfStream)
                        {
                            directory.CheckCurrent(snapshot, cancellationToken);
                            var url = await reader.ReadLineAsync().ConfigureAwait(false);
                            _logger.Debug("FILE NAME : " + url.Split('/').Last().Split('?').First());

                            var ext = Path.GetExtension(url.Split('/').Last().Split('?').First());

                            _logger.Debug("URL : " + url);
                            if (!string.IsNullOrEmpty(ext))
                            {
                                _logger.Debug("Extension : " + ext);
                                if (string.Equals(ext, ".pls", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        using (var response = await SendRequest(new HttpRequestOptions
                                        {
                                            Url = url,
                                            CancellationToken = cancellationToken

                                        }, "GET").ConfigureAwait(false))
                                        {
                                            using (var value = response.Content)
                                            {
                                                var parser = new IniParser(value);
                                                var count = Convert.ToInt16(parser.GetSetting("playlist", "NumberOfEntries"));
                                                _logger.Debug("COUNT : " + count);
                                                for (var i = 0; i < count; i++)
                                                {
                                                    var file = parser.GetSetting("playlist", "File" + count);
                                                    _logger.Debug("FILE : " + count + " - " + file);

                                                    items.Add(new ChannelMediaInfo
                                                    {
                                                        Path = file.ToLower()
                                                    }.ToMediaSource());
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Error(ex.ToString());
                                    }
                                }
                                else if (string.Equals(ext, ".m3u", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        using (var response = await SendRequest(new HttpRequestOptions
                                        {
                                            Url = url,
                                            CancellationToken = cancellationToken

                                        }, "GET").ConfigureAwait(false))
                                        {
                                            using (var value = response.Content)
                                            {
                                                using (var reader2 = new StreamReader(value))
                                                {
                                                    while (!reader2.EndOfStream)
                                                    {
                                                        var url2 = await reader2.ReadLineAsync().ConfigureAwait(false);
                                                        items.Add(new ChannelMediaInfo
                                                        {
                                                            Path = url2
                                                        }.ToMediaSource());
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Error(ex.ToString());
                                    }
                                }
                                else
                                {
                                    items.Add(GetMediaInfoFromUrl(url));
                                }
                            }
                            else
                            {
                                _logger.Debug("Normal URL");
                                items.Add(GetMediaInfoFromUrl(url));
                            }
                        }
                    }
                }
            }

            directory.CheckCurrent(snapshot, cancellationToken);
            return items;
        }

        private MediaSourceInfo GetMediaInfoFromUrl(string url)
        {
            var container = url.EndsWith("aac", StringComparison.OrdinalIgnoreCase) ? "aac" : "mp3";
            return new ChannelMediaInfo
            {
                Path = url,
                Container = container,
                AudioCodec = container,
                AudioBitrate = 128000,
                AudioChannels = 2,
                SupportsDirectPlay = true
            }.ToMediaSource();
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            switch (type)
            {
                case ImageType.Thumb:
                case ImageType.Backdrop:
                case ImageType.Primary:
                    {
                        var path = GetType().Namespace + ".Images." + type.ToString().ToLower() + ".png";

                        return Task.FromResult(new DynamicImageResponse
                        {
                            Format = ImageFormat.Png,
                            Stream = GetType().Assembly.GetManifestResourceStream(path)
                        });
                    }
                default:
                    throw new ArgumentException("Unsupported image type: " + type);
            }
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new List<ImageType>
            {
                ImageType.Thumb,
                ImageType.Backdrop,
                ImageType.Primary
            };
        }

        public string Name
        {
            get { return "TuneIn"; }
        }

        public InternalChannelFeatures GetChannelFeatures()
        {
            return new InternalChannelFeatures
            {
                ContentTypes = new List<ChannelMediaContentType>
                {
                    ChannelMediaContentType.Song
                },
                MediaTypes = new List<ChannelMediaType>
                {
                    ChannelMediaType.Audio
                }
            };
        }

        public string HomePageUrl
        {
            get { return "http://www.tunein.com/"; }
        }

        public ChannelParentalRating ParentalRating
        {
            get { return ChannelParentalRating.GeneralAudience; }
        }

        public string GetCacheKey(string userId) => directory.Snapshot().Key;

        public String GetDefaultImages(String name)
        {
            if (name == "Local Radio")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-localradio.png";
            if (name == "By Language")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-bylanguage.png";
            if (name == "By Location")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-bylocation.png";
            if (name == "Music")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-music.png";
            if (name == "My Favorites")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-myfavs.png";
            if (name == "Podcasts")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-podcasts.png";
            if (name == "Sports")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-sports.png";
            if (name == "Talk")
                return "https://raw.githubusercontent.com/snazy2000/MediaBrowser.Channels/master/MediaBrowser.Plugins.TuneIn/Images/tunein-talk.png";
            return "";
        }
    }
}
