using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MediaBrowser.Plugins.TuneIn
{
    public class TuneInChannel : IChannel, IRequiresMediaInfoCallback, IHasChannelFeatures, ISupportsMediaProbe
    {
        private readonly IServerApplicationHost host;
        private readonly ILogger logger;
        private readonly TuneInDirectory directory;
        private readonly ILibraryManager library;
        private const string ArtworkVersion = "3";
        private const string ArtworkVersionKey = "TuneInForkArtwork";

        public TuneInChannel(IServerApplicationHost host, ILogManager logManager, ILibraryManager libraryManager)
            : this(host, logManager.GetLogger(typeof(TuneInChannel).Name), TuneInDirectory.Instance, libraryManager)
        {
        }

        internal TuneInChannel(IServerApplicationHost host, ILogger logger, TuneInDirectory directory, ILibraryManager library = null)
        {
            this.host = host;
            this.logger = logger;
            this.directory = directory;
            this.library = library;
        }

        public string Name => "TuneIn (fork)";
        public string Description => "Configuration-first TuneIn radio.";
        public string HomePageUrl => "https://tunein.com/";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
        public bool IsEnabledFor(string userId) => true;
        public ChannelFeatures Features => new ChannelFeatures { ShowRootFoldersAtTopLevel = false };

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken token)
        {
            try { return await GetChannelItemsCore(query, token).ConfigureAwait(false); }
            catch (Exception error) { directory.RecordRefreshFailure(error); throw; }
        }

        private async Task<ChannelItemResult> GetChannelItemsCore(InternalChannelItemQuery query, CancellationToken token)
        {
            var snapshot = directory.Snapshot();
            var items = new List<ChannelItemInfo>();
            if (!snapshot.Ready)
                return new ChannelItemResult { Items = items };
            using (var operation = directory.Operation(snapshot, token))
            {
                if (string.IsNullOrEmpty(query.FolderId))
                {
                    foreach (var category in snapshot.Categories)
                        items.Add(Folder(snapshot, CategoryEntry(category)));
                }
                else
                {
                    var entry = directory.Resolve(snapshot, query.FolderId);
                    directory.AssertCategoryRefresh(entry.Category);
                    if (entry.Request != null)
                        items = await BrowseAsync(snapshot, entry, operation.Token).ConfigureAwait(false);
                    else if (entry.Category == "music" || entry.Category == "language" || entry.Category == "location")
                    {
                        foreach (var startingEntry in StartingEntries(snapshot, entry.Category))
                        {
                            var selection = snapshot.Selections(entry.Category).First(s => s.Id == startingEntry.SelectionId);
                            startingEntry.Name = selection.Name ?? selection.Id;
                            items.Add(Folder(snapshot, startingEntry));
                        }
                    }
                    else
                    {
                        foreach (var startingEntry in StartingEntries(snapshot, entry.Category))
                            items.AddRange(await BrowseAsync(snapshot, startingEntry, operation.Token).ConfigureAwait(false));
                        items = Deduplicate(items);
                    }
                }
                operation.Token.ThrowIfCancellationRequested();
                return new ChannelItemResult { Items = items };
            }
        }

        private ChannelItemInfo Folder(ConfigurationSnapshot snapshot, NavigationEntry entry)
        {
            var id = directory.Register(snapshot, entry);
            return new ChannelItemInfo
            {
                Id = id,
                Name = entry.Name,
                Type = ChannelItemType.Folder,
                ImageUrl = ArtworkUrl(id) + "?v=" + ArtworkVersion
            };
        }

        private async Task<List<ChannelItemInfo>> BrowseAsync(ConfigurationSnapshot snapshot, NavigationEntry entry, CancellationToken token)
        {
            if (IsLanguageMusicHub(entry))
            {
                return snapshot.Selections("music").Select(selection =>
                {
                    var selected = new DirectoryRequest();
                    selected.Parameters["id"] = selection.Id;
                    return Folder(snapshot, new NavigationEntry
                    {
                        Category = "language",
                        SelectionId = entry.SelectionId,
                        ContentCategory = "music",
                        MusicSelectionId = selection.Id,
                        Request = selected,
                        Name = selection.Name ?? selection.Id
                    });
                }).ToList();
            }
            if (entry.Category == "language")
            {
                var languages = await directory.SelectAsync("language", null, null, false, token).ConfigureAwait(false);
                if (!languages.Items.Any(i => i.Id == entry.SelectionId))
                    throw new InvalidOperationException("Selected TuneIn language is unavailable; load the language selector again.");
            }
            var request = CatalogueRequest(snapshot, entry);
            var document = await directory.FetchCategoryAsync(snapshot, entry.Category, request, token).ConfigureAwait(false);
            var items = new List<ChannelItemInfo>();
            foreach (var row in ContentRows(document.Root.Element("body")))
            {
                var type = TuneInDirectory.Attribute(row, "type");
                var name = TuneInDirectory.Attribute(row, "text") ?? "TuneIn";
                if (type == "audio")
                {
                    var station = TuneInDirectory.Attribute(row, "guide_id");
                    if (!ValidStation(station))
                    {
                        var link = DirectoryRequest.ParseLink(TuneInDirectory.Attribute(row, "URL"));
                        station = link.Parameters.GetValueOrDefault("id");
                    }
                    if (!ValidStation(station))
                        throw new InvalidDataException("Invalid TuneIn station identifier.");
                    var stationEntry = new NavigationEntry
                    {
                        Category = entry.Category,
                        SelectionId = entry.SelectionId,
                        ContentCategory = entry.Category == "language" ? LanguageGroup(entry, entry.Request) : null,
                        MusicSelectionId = entry.MusicSelectionId,
                        StationId = station,
                        Name = name
                    };
                    var id = directory.Register(snapshot, stationEntry);
                    stationEntry.ArtworkReference = ArtworkReference(row);
                    var imageId = stationEntry.ArtworkReference == null ? id : directory.Register(snapshot, stationEntry);
                    items.Add(new ChannelItemInfo
                    {
                        Id = id,
                        Name = name,
                        Type = ChannelItemType.Media,
                        ContentType = station[0] == 's' ? ChannelMediaContentType.Song : ChannelMediaContentType.Podcast,
                        MediaType = ChannelMediaType.Audio,
                        IsLiveStream = station[0] == 's',
                        ImageUrl = ArtworkUrl(imageId) + (stationEntry.ArtworkReference == null ? "?v=" + ArtworkVersion : "")
                    });
                }
                else if (type == "link")
                {
                    var link = DirectoryRequest.ParseLink(TuneInDirectory.Attribute(row, "URL"));
                    if (link.Method != "Browse.ashx") continue;
                    var next = ScopedRequest(snapshot, entry, link);
                    var contentCategory = entry.Category == "language" ? LanguageGroup(entry, next) : null;
                    if (contentCategory != null && contentCategory != "language" && !snapshot.Enabled(contentCategory))
                        continue;
                    if (entry.ContentCategory != null && entry.ContentCategory != "language"
                        && contentCategory != entry.ContentCategory) continue;
                    if (entry.MusicSelectionId != null && next.Parameters.ContainsKey("c")) continue;
                    var classifier = next.Parameters.GetValueOrDefault("c");
                    if (classifier != null && classifier != entry.Category
                        && !(entry.Category == "language" && new[] { "lang", "music", "talk", "sports" }.Contains(classifier))
                        && !(entry.Category == "local" && classifier == "local")) continue;
                    var rowKey = TuneInDirectory.Attribute(row, "key") ?? "";
                    if (rowKey.StartsWith("next", StringComparison.Ordinal)
                        && next.Parameters.GetValueOrDefault("id") != request.Parameters.GetValueOrDefault("id"))
                        throw new InvalidDataException("TuneIn pagination escaped its selected node.");
                    var id = next.Parameters.GetValueOrDefault("id");
                    if (id == null && classifier != null && (entry.Category == "music" || entry.Category == "location"
                        || entry.Category == "local" && entry.SelectionId != "coordinates")) continue;
                    if ((entry.Category == "location" || entry.Category == "local") && id != null
                        && !ConfigurationSnapshot.ValidId(id, "location")) continue;
                    if (entry.Category == "music" && id != null && !ConfigurationSnapshot.ValidId(id, "music")) continue;
                    if (entry.Category == "language" && id != null && !ConfigurationSnapshot.ValidId(id, "music")) continue;
                    items.Add(Folder(snapshot, new NavigationEntry
                    {
                        Category = entry.Category,
                        SelectionId = entry.SelectionId,
                        ContentCategory = contentCategory,
                        MusicSelectionId = entry.MusicSelectionId,
                        Request = next,
                        Name = name
                    }));
                }
            }
            return Deduplicate(items);
        }

        internal static DirectoryRequest ScopedRequest(ConfigurationSnapshot snapshot, NavigationEntry entry, DirectoryRequest source)
        {
            var request = source.Copy();
            if (entry.Category == "language")
            {
                var id = request.Parameters.GetValueOrDefault("id");
                // Current language hubs use generic classifier IDs; their language filter is ignored.
                if (id == "c424724" || id == "c424725" || id == "c424726")
                {
                    request.Parameters.Remove("id");
                    request.Parameters["c"] = id == "c424724" ? "music" : id == "c424725" ? "talk" : "sports";
                }
                var filter = request.Parameters.GetValueOrDefault("filter") ?? "";
                var tokens = filter.Split(':').Where(s => s.Length > 0 && !Regex.IsMatch(s, @"^l[0-9]+$")).ToList();
                if (tokens.Any(s => !Regex.IsMatch(s,
                    @"^(s|p|topic|show|station|popular|topvoted|topclick|random|bit(32|64|128)[*-]?|up-(low|med|hi))$")))
                    throw new InvalidDataException("TuneIn returned an unsupported language filter.");
                request.Parameters["filter"] = string.Join(":", tokens.Concat(new[] { entry.SelectionId }));
            }
            else if (entry.Category == "local" && entry.SelectionId == "coordinates")
            {
                if (!ConfigurationSnapshot.TryCoordinates(snapshot.Value.LatLon, out var coordinates))
                    throw new InvalidOperationException("Local coordinates are not configured.");
                request.Parameters["latlon"] = coordinates;
            }
            if (entry.Category == "presets") request.Parameters["username"] = snapshot.Value.Username;
            else request.Parameters.Remove("username");
            return request;
        }

        internal static DirectoryRequest CatalogueRequest(ConfigurationSnapshot snapshot, NavigationEntry entry)
        {
            if (entry.Category == "language" && !TuneInDirectory.LanguageContentAllowed(snapshot, entry))
                throw new InvalidOperationException("TuneIn language sub-group is disabled. Refresh the language group.");
            var request = ScopedRequest(snapshot, entry, entry.Request);
            request.Parameters["formats"] = "mp3,aac";
            request.Parameters["partnerid"] = "uD1X52pA";
            return request;
        }

        internal static string LanguageGroup(NavigationEntry entry, DirectoryRequest request)
        {
            var id = request?.Parameters.GetValueOrDefault("id");
            if (id == "c424724") return "music";
            if (id == "c424725") return "talk";
            if (id == "c424726") return "sports";
            var classifier = request?.Parameters.GetValueOrDefault("c");
            if (classifier == "music" || classifier == "talk" || classifier == "sports") return classifier;
            if (classifier == "lang") return entry.ContentCategory ?? "language";
            return entry.ContentCategory ?? (id == null ? "language" : "");
        }

        internal static bool IsLanguageMusicHub(NavigationEntry entry) =>
            entry.Category == "language" && entry.MusicSelectionId == null && entry.StationId == null
            && entry.Request != null && (entry.Request.Parameters.GetValueOrDefault("id") == "c424724"
                || !entry.Request.Parameters.ContainsKey("id") && entry.Request.Parameters.GetValueOrDefault("c") == "music");

        internal static NavigationEntry CategoryEntry(string category) =>
            new NavigationEntry { Category = category, Name = CategoryName(category) };

        internal static List<NavigationEntry> StartingEntries(ConfigurationSnapshot snapshot, string category)
        {
            if (!snapshot.Enabled(category)) throw new InvalidOperationException("Save configuration and enable this group with its required selections first.");
            var entries = new List<NavigationEntry>();
            if (category == "local" && snapshot.Value.UseCoordinates)
            {
                var request = new DirectoryRequest();
                request.Parameters["c"] = "local";
                entries.Add(new NavigationEntry { Category = category, SelectionId = "coordinates", Request = request });
            }
            if (new[] { "local", "language", "location", "music" }.Contains(category))
            {
                foreach (var selection in snapshot.Selections(category))
                {
                    var request = new DirectoryRequest();
                    if (category == "language") request.Parameters["c"] = "lang";
                    else request.Parameters["id"] = selection.Id;
                    entries.Add(new NavigationEntry { Category = category, SelectionId = selection.Id, Request = request });
                }
            }
            else
            {
                var request = new DirectoryRequest();
                request.Parameters["c"] = category;
                entries.Add(new NavigationEntry { Category = category, Request = request });
            }
            return entries;
        }

        private static IEnumerable<XElement> ContentRows(XElement parent)
        {
            foreach (var row in parent.Elements("outline"))
            {
                var key = TuneInDirectory.Attribute(row, "key") ?? "";
                if (key == "related" || key == "recommendations" || key == "affiliates"
                    || key == "pivotLocation" || key == "localCountry") continue;
                if (TuneInDirectory.Attribute(row, "type") != null) yield return row;
                else foreach (var child in ContentRows(row)) yield return child;
            }
        }

        private List<ChannelItemInfo> Deduplicate(List<ChannelItemInfo> items)
        {
            var snapshot = directory.Snapshot();
            var seen = new HashSet<string>();
            return items.Where(item =>
            {
                var entry = directory.Resolve(snapshot, item.Id);
                return seen.Add(entry.StationId != null ? "station:" + entry.StationId
                    : entry.Category + ":" + entry.SelectionId + ":" + entry.Request?.Url + ":" + entry.Name);
            }).ToList();
        }

        private static bool ValidStation(string value) => value != null && Regex.IsMatch(value, @"^[spt][1-9][0-9]{0,9}$");

        private string ArtworkUrl(string id) =>
            host.GetLocalApiUrl(System.Net.IPAddress.Loopback).TrimEnd('/') + "/TuneInFork/Artwork/" + id;

        internal void ReplaceFolderArtwork(string folderId, CancellationToken token)
        {
            if (library == null) throw new InvalidOperationException("The TuneIn library manager is unavailable.");
            token.ThrowIfCancellationRequested();
            var snapshot = directory.Snapshot();
            var category = directory.Resolve(snapshot, folderId).Category;
            var roots = library.GetItemList(new InternalItemsQuery { ExternalId = folderId, IsFolder = true }, token);
            if (roots.Length != 1) throw new InvalidOperationException("The refreshed TuneIn section folder is missing or ambiguous.");
            ReplaceChannelArtwork(library.GetItemById(roots[0].ParentId));
            Replace(roots[0]);
            for (var offset = 0; ; offset += 100)
            {
                token.ThrowIfCancellationRequested();
                var folders = library.GetItemList(new InternalItemsQuery
                {
                    ParentIds = new[] { roots[0].InternalId },
                    Recursive = true,
                    IsFolder = true,
                    StartIndex = offset,
                    Limit = 100
                }, token);
                foreach (var folder in folders) Replace(folder);
                if (folders.Length < 100) break;
            }

            void Replace(BaseItem folder)
            {
                token.ThrowIfCancellationRequested();
                if (!(folder is MediaBrowser.Controller.Entities.Folder)) return;
                NavigationEntry entry;
                try { entry = directory.Resolve(snapshot, folder.ExternalId); }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is InvalidDataException)
                {
                    logger?.Warn("Skipped stale or invalid TuneIn folder artwork ({0}).", error.GetType().Name);
                    return;
                }
                if (entry.Category != category || entry.StationId != null) return;
                var version = ArtworkVersion + ":" + TuneInArtwork.Category(entry);
                if (folder.GetProviderId(ArtworkVersionKey) == version && folder.HasImage(ImageType.Primary, 0)) return;
                // Native channel import ignores ImageUrl once an item already has a primary image.
                folder.SetImage(new ItemImageInfo
                {
                    Path = ArtworkUrl(folder.ExternalId) + "?v=" + ArtworkVersion,
                    Type = ImageType.Primary,
                    DateModified = DateTimeOffset.UtcNow
                }, 0);
                folder.SetProviderId(ArtworkVersionKey, version);
                library.UpdateItem(folder, library.GetItemById(folder.ParentId), ItemUpdateType.ImageUpdate | ItemUpdateType.MetadataImport);
            }
        }

        private void ReplaceChannelArtwork(BaseItem parent)
        {
            if (!(parent is Channel) || parent.GetProviderId("TuneInForkBranding") == "1"
                && GetSupportedChannelImages().All(type => parent.HasImage(type, 0))) return;
            foreach (var type in GetSupportedChannelImages())
                parent.SetImage(new ItemImageInfo
                {
                    Path = host.GetLocalApiUrl(System.Net.IPAddress.Loopback).TrimEnd('/') + "/TuneInFork/Branding?v=1",
                    Type = type,
                    DateModified = DateTimeOffset.UtcNow
                }, 0);
            parent.SetProviderId("TuneInForkBranding", "1");
            library.UpdateItem(parent, library.GetItemById(parent.ParentId), ItemUpdateType.ImageUpdate | ItemUpdateType.MetadataImport);
        }

        private string ArtworkReference(XElement row)
        {
            var value = TuneInDirectory.Attribute(row, "image");
            if (string.IsNullOrEmpty(value)) return null;
            if (TuneInArtwork.TryReference(value, out var reference)) return reference;
            logger?.Warn("TuneIn station artwork destination rejected; using bundled group artwork.");
            return null;
        }

        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken token)
        {
            var snapshot = directory.Snapshot();
            var entry = directory.Resolve(snapshot, id);
            if (!ValidStation(entry.StationId)) throw new InvalidOperationException("TuneIn item is not playable.");
            logger.Debug("Preparing protected TuneIn radio relay.");
            token.ThrowIfCancellationRequested();
            var local = host.GetLocalApiUrl(System.Net.IPAddress.Loopback);
            // Change the source identity so an earlier ad-only MP3 probe is not reused for PCM playback.
            var path = local.TrimEnd('/') + "/TuneInFork/Relay/" + id + "?playback=2";
            var source = new ChannelMediaInfo { Path = path, SupportsDirectPlay = false }.ToMediaSource();
            source.SupportsDirectStream = false;
            source.SupportsTranscoding = true;
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { source });
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken token)
        {
            if (!GetSupportedChannelImages().Contains(type)) throw new ArgumentException("Unsupported image type.");
            return Task.FromResult(new DynamicImageResponse
            {
                Format = ImageFormat.Png,
                Stream = GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Images." + type.ToString().ToLowerInvariant() + ".png")
            });
        }

        public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop };

        internal static string CategoryName(string category)
        {
            switch (category)
            {
                case "local": return "Local Radio";
                case "music": return "Music";
                case "language": return "By Language";
                case "location": return "By Location";
                case "talk": return "Talk";
                case "sports": return "Sports";
                case "podcast": return "Podcasts";
                default: return "My Favorites";
            }
        }
    }
}
