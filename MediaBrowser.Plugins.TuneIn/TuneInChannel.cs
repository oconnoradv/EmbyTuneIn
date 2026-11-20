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
        private static readonly ImageType[] ChannelImageTypes = { ImageType.Thumb, ImageType.Backdrop, ImageType.Primary };
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly TuneInDirectory directory;
        private readonly IServerApplicationHost serverHost;
        private readonly ILibraryManager library;
        private TuneInStagedImport stagedImport;
        private readonly object catalogueGate = new object();
        private string browseKey;
        private TuneInCatalogueSession browseCache;

        public TuneInChannel(IHttpClient httpClient, ILogManager logManager, IApplicationHost appHost,
            IServerApplicationHost serverHost, ILibraryManager libraryManager)
            : this(TuneInRequests.Directory(httpClient, appHost), null,
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

        public string DataVersion => "49";
        public ChannelFeatures Features => new ChannelFeatures { ShowRootFoldersAtTopLevel = false };
        public string Description => "Listen to online radio, find streaming music radio and streaming talk radio with TuneIn.";
        public bool IsEnabledFor(string userId) => true;

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var snapshot = directory.Snapshot();
            directory.CheckCurrent(snapshot, cancellationToken);
            if (snapshot.Ready && !string.IsNullOrEmpty(query.FolderId))
                directory.Resolve(snapshot, query.FolderId);
            var staged = Volatile.Read(ref stagedImport);
            if (staged != null)
                return Task.FromResult(staged.Read(query.FolderId, cancellationToken));

            if (snapshot.Ready && !string.IsNullOrEmpty(query.FolderId))
            {
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

        internal ChannelItemInfo GetCategoryRoot(ConfigurationSnapshot snapshot, string category) =>
            new TuneInBrowsing(directory, GetDefaultImages).CategoryRoot(snapshot, category);

        internal async Task ImportAsync(TuneInStagedImport staged, Func<Task> import)
        {
            // Emby can request listings in a different execution context from the import caller.
            if (Interlocked.CompareExchange(ref stagedImport, staged, null) != null)
                throw new InvalidOperationException("A TuneIn channel import is already active.");
            try
            {
                await import().ConfigureAwait(false);
                staged.Verify();
            }
            finally { Volatile.Write(ref stagedImport, null); }
        }

        internal int? CountCategoryStations(string folderId, CancellationToken token)
        {
            if (library == null) return null;
            var snapshot = directory.Snapshot();
            directory.CheckCurrent(snapshot, token);
            var rootEntry = directory.Resolve(snapshot, folderId);
            if (rootEntry.StationId != null || rootEntry.SelectionId != null || rootEntry.Request != null)
                throw new InvalidOperationException("TuneIn station counts require a category root.");
            var roots = library.GetItemList(new InternalItemsQuery
            {
                ExternalId = folderId, IsFolder = true, Limit = 2
            }, token);
            if (roots.Length > 1) throw new InvalidOperationException("TuneIn category folder is ambiguous.");
            if (roots.Length == 0) return 0;
            var items = library.GetItemList(new InternalItemsQuery
            {
                ParentIds = new[] { roots[0].InternalId }, Recursive = true, IsFolder = false,
                Limit = TuneInStagedImport.MaxItems + 1
            }, token);
            if (items.Length > TuneInStagedImport.MaxItems)
                throw new InvalidOperationException("TuneIn station inventory exceeded its item limit.");
            var stations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var entry = directory.Resolve(snapshot, item.ExternalId);
                if (entry.Category == rootEntry.Category && entry.StationId != null)
                    stations.Add(entry.StationId);
            }
            directory.CheckCurrent(snapshot, token);
            return stations.Count;
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
            directory.CheckCurrent(snapshot, token);
            if (roots[0] is Folder && roots[0].ExternalId == folderId && roots[0].ParentId != 0 &&
                library.GetItemById(roots[0].ParentId) is Channel channel && channel.Name == Name)
                UpdateLibraryArtwork(channel);
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

        private void UpdateLibraryArtwork(Channel channel)
        {
            const string provider = "TuneInLibraryArtwork";
            var url = CategoryArtworkUrl(TuneInCategoryArtwork.ChannelArtwork);
            var previousRevision = channel.GetProviderId(provider);
            if (previousRevision == TuneInCategoryArtwork.Revision &&
                ChannelImageTypes.All(type => channel.HasImage(type, 0))) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var type in ChannelImageTypes)
                channel.SetImage(new ItemImageInfo { Path = url, Type = type, DateModified = now }, 0);
            channel.SetProviderId(provider, TuneInCategoryArtwork.Revision);
            try { library.UpdateItem(channel, library.GetItemById(channel.ParentId), ItemUpdateType.ImageUpdate); }
            catch
            {
                channel.SetProviderId(provider, previousRevision ?? "");
                throw;
            }
        }

        private string CategoryArtworkUrl(string category) =>
            TuneInCategoryArtwork.Url(serverHost.GetLocalApiUrl(System.Net.IPAddress.Loopback), category);

        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken) =>
            new TuneInPlayback(directory, _httpClient, message => _logger?.Warn(message))
                .GetSourcesAsync(id, serverHost, cancellationToken);

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ChannelImageTypes.Contains(type)) throw new ArgumentException("Unsupported image type: " + type, nameof(type));
            return Task.FromResult(new DynamicImageResponse
            {
                Format = ImageFormat.Png,
                Stream = TuneInCategoryArtwork.Open(TuneInCategoryArtwork.ChannelArtwork)
            });
        }

        public IEnumerable<ImageType> GetSupportedChannelImages() => ChannelImageTypes.ToArray();

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
