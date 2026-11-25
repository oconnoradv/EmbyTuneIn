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
        private ActiveImport activeImport;
        private readonly AsyncLocal<ActiveImport> importContext = new AsyncLocal<ActiveImport>();
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
            TuneInRefreshService.EnsureNoCleanup();
            var snapshot = directory.Snapshot();
            directory.CheckCurrent(snapshot, cancellationToken);
            if (snapshot.Ready && !string.IsNullOrEmpty(query.FolderId))
                directory.Resolve(snapshot, query.FolderId);
            var active = Volatile.Read(ref activeImport);
            if (active != null && (active.Token == cancellationToken
                || active.AllowChangedToken && ReferenceEquals(active, importContext.Value)))
                return Task.FromResult(active.Staged.Read(query.FolderId, cancellationToken));

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
            return new TuneInBrowsing(directory, GetDefaultImages, FolderImage).GetItemsAsync(query.FolderId, cancellationToken, session, snapshot);
        }

        internal Task<ChannelItemResult> GetRefreshChannelItems(string folder, TuneInCatalogueSession session,
            bool strict, CancellationToken token) =>
            new TuneInBrowsing(directory, GetDefaultImages, FolderImage).GetItemsAsync(folder, token, session);

        internal ChannelItemInfo GetCategoryRoot(ConfigurationSnapshot snapshot, string category) =>
            new TuneInBrowsing(directory, GetDefaultImages, FolderImage).CategoryRoot(snapshot, category);

        internal bool DiscoveryBridgeEnabled()
        {
            var settings = directory.Snapshot().Value;
            return settings.EnableDiscoveryProgressiveImport
                && settings.DiscoveryImportRiskVersion == Plugin.DiscoveryImportRiskVersion
                && settings.DiscoveryImportRiskAcceptedUtc.HasValue;
        }

        internal async Task ImportAsync(TuneInStagedImport staged, CancellationToken token, Func<Task> import,
            bool allowChangedToken = false)
        {
            if (staged == null || import == null) throw new ArgumentNullException(staged == null ? nameof(staged) : nameof(import));
            // The token identifies the import traversal, so it must be unique to this import.
            if (!token.CanBeCanceled) throw new ArgumentException("A TuneIn import requires its own cancellation token.", nameof(token));
            var active = new ActiveImport(staged, token, allowChangedToken);
            if (Interlocked.CompareExchange(ref activeImport, active, null) != null)
                throw new InvalidOperationException("A TuneIn channel import is already active.");
            var previous = importContext.Value;
            if (allowChangedToken) importContext.Value = active;
            try
            {
                await import().ConfigureAwait(false);
                staged.Verify();
            }
            finally
            {
                Interlocked.CompareExchange(ref activeImport, null, active);
                if (allowChangedToken) importContext.Value = previous;
            }
        }

        private sealed class ActiveImport
        {
            internal ActiveImport(TuneInStagedImport staged, CancellationToken token, bool allowChangedToken)
            {
                Staged = staged;
                Token = token;
                AllowChangedToken = allowChangedToken;
            }

            internal TuneInStagedImport Staged { get; }
            internal CancellationToken Token { get; }
            internal bool AllowChangedToken { get; }
        }

        internal HashSet<string> CategoryStationIds(string folderId, CancellationToken token)
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
            if (roots.Length == 0) return new HashSet<string>(StringComparer.Ordinal);
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
            return stations;
        }

        internal int? CountCategoryStations(string folderId, CancellationToken token) =>
            CategoryStationIds(folderId, token)?.Count;

        internal TuneInCategorySnapshot ReadCategorySnapshot(ConfigurationSnapshot snapshot, string category, CancellationToken token,
            TuneInCategorySnapshot previous = null)
        {
            if (library == null) return null;
            directory.CheckCurrent(snapshot, token);
            var channels = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Channel) } }, token)
                .OfType<Channel>().Where(item => item.Name == Name).ToArray();
            if (channels.Length > 1) throw new InvalidOperationException("The TuneIn channel is ambiguous.");
            if (channels.Length == 0) return null;
            var rootId = GetCategoryRoot(snapshot, category).Id;
            var categoryRoots = library.GetItemList(new InternalItemsQuery
            { ParentIds = new[] { channels[0].InternalId }, IsFolder = true, Recursive = false, Limit = TuneInStagedImport.MaxFolders + 1 }, token);
            if (categoryRoots.Length > TuneInStagedImport.MaxFolders)
                throw new InvalidOperationException("Existing TuneIn channel exceeds its root folder limit.");
            var roots = categoryRoots.Where(item => TuneInDirectory.GeneratedCategory(snapshot, item.ExternalId, true) == category
                && (previous != null || category != "presets" && (category != "local" || snapshot.LocalSource != "coordinates")
                    || item.ExternalId == rootId)).ToArray();
            if (roots.Length == 0) return null;
            var items = library.GetItemList(new InternalItemsQuery
            { ParentIds = roots.Select(item => item.InternalId).ToArray(), Recursive = true, Limit = TuneInStagedImport.MaxItems + 1 }, token);
            if (items.Length > TuneInStagedImport.MaxItems)
                throw new InvalidOperationException("Existing TuneIn category exceeds its inventory limit.");
            // Recursive queries over several roots can repeat items reachable through more than one root.
            var rootIds = new HashSet<long>(roots.Select(item => item.InternalId));
            items = items.Where(item => !rootIds.Contains(item.InternalId))
                .GroupBy(item => item.InternalId).Select(group => group.First()).ToArray();
            var inventory = roots.Concat(items).ToDictionary(item => item.InternalId);
            var reachable = new HashSet<long>(roots.Select(item => item.InternalId));
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var ancestry = new HashSet<long>();
                var parentId = item.InternalId;
                while (!reachable.Contains(parentId))
                {
                    if (!ancestry.Add(parentId) || !inventory.TryGetValue(parentId, out var parent))
                        throw new InvalidOperationException("Existing TuneIn category contains cyclic or unreachable entries.");
                    parentId = parent.ParentId;
                }
                reachable.UnionWith(ancestry);
            }
            var metadata = (previous?.Folders.SelectMany(folder => folder.Items) ?? Enumerable.Empty<TuneInItemSnapshot>())
                .GroupBy(item => directory.MembershipKey(snapshot, item.Id), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var folders = new Dictionary<string, TuneInFolderSnapshot>(StringComparer.Ordinal)
            { [rootId] = new TuneInFolderSnapshot { Id = rootId } };
            var children = items.ToLookup(item => item.ParentId);
            var pending = new Queue<(long InternalId, string FolderId)>(roots.Select(item => (item.InternalId, rootId)));
            var seen = new HashSet<long>(roots.Select(item => item.InternalId));
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var parent = pending.Dequeue();
                foreach (var item in children[parent.InternalId])
                {
                    if (!seen.Add(item.InternalId))
                        throw new InvalidOperationException("Existing TuneIn category contains cyclic entries.");
                    if (TuneInDirectory.GeneratedCategory(snapshot, item.ExternalId) != category)
                        throw new InvalidOperationException("Existing TuneIn category contains unverified entries; preservation cannot safely proceed.");
                    var id = directory.RebindOwned(snapshot, item.ExternalId);
                    if (id == null) continue;
                    var isFolder = item is Folder;
                    if (!isFolder && (!(item is MediaBrowser.Controller.Entities.Audio.Audio)
                        || !TuneInDirectory.GeneratedStation(snapshot, item.ExternalId)))
                        throw new InvalidOperationException("Existing TuneIn category contains an unsupported item type.");
                    var listing = folders[parent.FolderId];
                    if (!listing.Items.Any(saved => saved.Id == id))
                    {
                        metadata.TryGetValue(directory.MembershipKey(snapshot, id), out var savedMetadata);
                        listing.Items.Add(new TuneInItemSnapshot
                        {
                            Id = id, Name = item.Name ?? "", Type = isFolder ? "Folder" : "Media",
                            MediaType = savedMetadata?.MediaType ?? "Audio", ContentType = savedMetadata?.ContentType ?? "Song",
                            ImageUrl = savedMetadata?.ImageUrl ?? ""
                        });
                    }
                    if (isFolder)
                    {
                        if (folders.Count == TuneInStagedImport.MaxFolders && !folders.ContainsKey(id))
                            throw new InvalidOperationException("Existing TuneIn category exceeds its folder limit.");
                        if (!folders.ContainsKey(id)) folders[id] = new TuneInFolderSnapshot { Id = id };
                        pending.Enqueue((item.InternalId, id));
                    }
                }
            }
            directory.CheckCurrent(snapshot, token);
            var result = new TuneInCategorySnapshot
            {
                Category = category, RootId = rootId, Folders = folders.Values.ToList(),
                DiscoveryPartial = previous?.DiscoveryPartial == true,
                DiscoveryLimitReason = previous?.DiscoveryLimitReason
            };
            result.Fingerprint = TuneInStagedImport.Fingerprint(result.Folders);
            return result;
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
                var artwork = TuneInCategoryArtwork.ArtworkFor(entry);
                if (artwork.Id == null && TuneInCategoryArtwork.PackFile("categories", entry.Category) != null)
                    artwork = ("categories", entry.Category);
                var version = TuneInCategoryArtwork.Revision + ":" + entry.Category
                    + (artwork.Id == null ? "" : ":" + (artwork.Scope == "genres" ? "" : artwork.Scope + ":")
                        + artwork.Id + ":" + TuneInCategoryArtwork.PackRevision(artwork.Scope, artwork.Id));
                var previousVersion = folder.GetProviderId("TuneInCategoryArtwork");
                const string packProvider = "TuneInPackArtworkRevision";
                const string packRevision = "1";
                var packVersion = artwork.Id == null ? null : version + ":" + packRevision;
                var previousPackVersion = packVersion == null ? null : folder.GetProviderId(packProvider);
                if (previousVersion == version && folder.HasImage(ImageType.Primary, 0)
                    && (packVersion == null || previousPackVersion == packVersion)) return;
                var image = artwork.Scope == "categories" ? CategoryArtworkUrl(entry.Category)
                    : FolderImage(entry) ?? CategoryArtworkUrl(entry.Category);
                folder.SetImage(new ItemImageInfo
                {
                    Path = packVersion == null ? image : image + "&packRevision=" + packRevision, Type = ImageType.Primary,
                    DateModified = DateTimeOffset.UtcNow
                }, 0);
                folder.SetProviderId("TuneInCategoryArtwork", version);
                if (packVersion != null) folder.SetProviderId(packProvider, packVersion);
                try { library.UpdateItem(folder, library.GetItemById(folder.ParentId), ItemUpdateType.ImageUpdate); }
                catch
                {
                    folder.SetProviderId("TuneInCategoryArtwork", previousVersion ?? "");
                    if (packVersion != null) folder.SetProviderId(packProvider, previousPackVersion ?? "");
                    throw;
                }
            }
        }

        internal bool UpdateLibraryArtwork(CancellationToken token)
        {
            if (serverHost == null || library == null) return false;
            var channel = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Channel) } }, token)
                .OfType<Channel>().SingleOrDefault(item => string.Equals(item.Name, Name, StringComparison.Ordinal));
            if (channel == null) return false;
            UpdateLibraryArtwork(channel);
            return true;
        }

        private void UpdateLibraryArtwork(Channel channel)
        {
            const string provider = "TuneInLibraryArtwork";
            var url = CategoryArtworkUrl(TuneInCategoryArtwork.ChannelArtwork);
            var previousRevision = channel.GetProviderId(provider);
            if (previousRevision == TuneInCategoryArtwork.LibraryRevision &&
                ChannelImageTypes.All(type => channel.HasImage(type, 0))) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var type in ChannelImageTypes)
                channel.SetImage(new ItemImageInfo { Path = url, Type = type, DateModified = now }, 0);
            channel.SetProviderId(provider, TuneInCategoryArtwork.LibraryRevision);
            try { library.UpdateItem(channel, library.GetItemById(channel.ParentId), ItemUpdateType.ImageUpdate); }
            catch
            {
                channel.SetProviderId(provider, previousRevision ?? "");
                throw;
            }
        }

        private string FolderImage(NavigationEntry entry)
        {
            var artwork = TuneInCategoryArtwork.ArtworkFor(entry);
            return artwork.Id == null || serverHost == null ? null
                : TuneInCategoryArtwork.PackUrl(serverHost.GetLocalApiUrl(System.Net.IPAddress.Loopback), artwork.Scope, artwork.Id);
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
            var category = ConfigurationSnapshot.CategoryKeys
                .FirstOrDefault(value => TuneInBrowsing.CategoryName(value) == name);
            if (category == null || serverHost == null) return "";
            return CategoryArtworkUrl(category);
        }
    }
}
