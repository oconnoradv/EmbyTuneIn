using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaBrowser.Plugins.TuneIn
{
    public class TuneInChannel : IChannel, IRequiresMediaInfoCallback, IHasChannelFeatures
    {
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly TuneInDirectory directory;
        private readonly IServerApplicationHost serverHost;
        private readonly ILibraryManager library;
        private readonly AsyncLocal<TuneInStagedImport> stagedImport = new AsyncLocal<TuneInStagedImport>();
        private readonly object catalogueGate = new object();
        private string browseKey;
        private TuneInCatalogueSession browseCache;

        public TuneInChannel(IHttpClient httpClient, ILogManager logManager, IApplicationHost appHost,
            IServerApplicationHost serverHost, ILibraryManager libraryManager)
            : this(TuneInRequests.Directory(httpClient, appHost), httpClient,
                logManager.GetLogger(typeof(TuneInChannel).Name),
                serverHost ?? throw new ArgumentNullException(nameof(serverHost)),
                libraryManager ?? throw new ArgumentNullException(nameof(libraryManager))) { }

        internal TuneInChannel(TuneInDirectory directory, IHttpClient httpClient = null, ILogger logger = null,
            IServerApplicationHost serverHost = null, ILibraryManager library = null)
        {
            this.directory = directory;
            _httpClient = httpClient;
            _logger = logger;
            this.serverHost = serverHost;
            this.library = library;
        }

        public string DataVersion => "47";
        public ChannelFeatures Features => new ChannelFeatures { ShowRootFoldersAtTopLevel = false };
        public string Description => "Listen to online radio, find streaming music radio and streaming talk radio with TuneIn.";
        public bool IsEnabledFor(string userId) => true;

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            if (stagedImport.Value != null)
                return Task.FromResult(stagedImport.Value.Read(query.FolderId, cancellationToken));

            var snapshot = directory.Snapshot();
            directory.CheckCurrent(snapshot, cancellationToken);
            if (snapshot.Ready && !string.IsNullOrEmpty(query.FolderId))
            {
                directory.Resolve(snapshot, query.FolderId);
                if (TuneInRefreshService.TryGetCommitted(snapshot, query.FolderId, out var old))
                {
                    directory.CheckCurrent(snapshot, cancellationToken);
                    return Task.FromResult(old);
                }
            }
            TuneInCatalogueSession session;
            lock (catalogueGate)
            {
                if (browseKey != snapshot.Key)
                {
                    browseKey = snapshot.Key;
                    browseCache = new TuneInCatalogueSession(null, TimeSpan.FromSeconds(30), () => DateTime.UtcNow, strictBudget: false);
                }
                session = browseCache;
            }
            return new TuneInBrowsing(directory, GetDefaultImages).GetItemsAsync(query.FolderId, cancellationToken, session, snapshot);
        }

        internal Task<ChannelItemResult> GetRefreshChannelItems(string folder, TuneInCatalogueSession session,
            bool strict, CancellationToken token) =>
            new TuneInBrowsing(directory, GetDefaultImages).GetItemsAsync(folder, token, session);

        internal async Task ImportAsync(TuneInStagedImport staged, Func<Task> import)
        {
            var previous = stagedImport.Value;
            stagedImport.Value = staged;
            try
            {
                await import().ConfigureAwait(false);
                staged.Verify();
            }
            finally { stagedImport.Value = previous; }
        }

        internal void UpdateCategoryArtwork(string folderId, CancellationToken token)
        {
            if (serverHost == null || library == null) throw new InvalidOperationException("TuneIn artwork services are unavailable.");
            var snapshot = directory.Snapshot();
            var rootEntry = directory.Resolve(snapshot, folderId);
            if (rootEntry.StationId != null || rootEntry.SelectionId != null || rootEntry.Request != null)
                throw new InvalidOperationException("TuneIn artwork update requires a category root.");
            var roots = library.GetItemList(new InternalItemsQuery { ExternalId = folderId, IsFolder = true }, token);
            if (roots.Length > 1) throw new InvalidOperationException("TuneIn category folder is ambiguous.");
            if (roots.Length == 0) return;
            Replace(roots[0]);
            for (var offset = 0; ; offset += 100)
            {
                directory.CheckCurrent(snapshot, token);
                var folders = library.GetItemList(new InternalItemsQuery
                {
                    ParentIds = new[] { roots[0].InternalId }, Recursive = true, IsFolder = true,
                    StartIndex = offset, Limit = 100
                }, token);
                foreach (var folder in folders) Replace(folder);
                if (folders.Length < 100) break;
            }

            void Replace(BaseItem folder)
            {
                directory.CheckCurrent(snapshot, token);
                if (!(folder is Folder)) return;
                NavigationEntry entry;
                try { entry = directory.Resolve(snapshot, folder.ExternalId); }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is InvalidDataException)
                {
                    _logger?.Warn("Skipped stale or invalid TuneIn folder artwork ({0}).", error.GetType().Name);
                    return;
                }
                if (entry.Category != rootEntry.Category || entry.StationId != null) return;
                var version = TuneInCategoryArtwork.Revision + ":" + entry.Category;
                var previousVersion = folder.GetProviderId("TuneInCategoryArtwork");
                if (previousVersion == version && folder.HasImage(ImageType.Primary, 0)) return;
                folder.SetImage(new ItemImageInfo
                {
                    Path = CategoryArtworkUrl(entry.Category), Type = ImageType.Primary, DateModified = DateTimeOffset.UtcNow
                }, 0);
                folder.SetProviderId("TuneInCategoryArtwork", version);
                try { library.UpdateItem(folder, library.GetItemById(folder.ParentId), ItemUpdateType.ImageUpdate); }
                catch
                {
                    folder.SetProviderId("TuneInCategoryArtwork", previousVersion ?? "");
                    throw;
                }
            }
        }

        private string CategoryArtworkUrl(string category) =>
            TuneInCategoryArtwork.Url(serverHost.GetLocalApiUrl(System.Net.IPAddress.Loopback), category);

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
                        var imageName = type == ImageType.Primary ? "tunein-poweredby" : type.ToString().ToLowerInvariant();
                        var path = GetType().Namespace + ".Images." + imageName + ".png";

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

        public string HomePageUrl
        {
            get { return "http://www.tunein.com/"; }
        }

        public ChannelParentalRating ParentalRating
        {
            get { return ChannelParentalRating.GeneralAudience; }
        }

        public String GetDefaultImages(String name)
        {
            var category = new[] { "local", "music", "language", "location", "talk", "sports", "podcast", "presets" }
                .FirstOrDefault(value => TuneInBrowsing.CategoryName(value) == name);
            if (category == null || serverHost == null) return "";
            return CategoryArtworkUrl(category);
        }
    }
}
