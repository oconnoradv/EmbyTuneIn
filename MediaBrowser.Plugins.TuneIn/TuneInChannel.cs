using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
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
    public class TuneInChannel : IChannel, IRequiresMediaInfoCallback, IHasChangeEvent, IHasChannelFeatures, ISupportsMediaProbe
    {
        private readonly IServerApplicationHost host;
        private readonly ILogger logger;
        private readonly TuneInDirectory directory;

        public TuneInChannel(IServerApplicationHost host, ILogManager logManager)
            : this(host, logManager.GetLogger(typeof(TuneInChannel).Name), TuneInDirectory.Instance)
        {
        }

        internal TuneInChannel(IServerApplicationHost host, ILogger logger, TuneInDirectory directory)
        {
            this.host = host;
            this.logger = logger;
            this.directory = directory;
        }

        public string Name => "TuneIn (fork)";
        public string Description => "Configuration-first TuneIn radio.";
        public string HomePageUrl => "https://tunein.com/";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
        public bool IsEnabledFor(string userId) => true;
        public event EventHandler ContentChanged
        {
            add { directory.Changed += value; }
            remove { directory.Changed -= value; }
        }
        public ChannelFeatures Features => new ChannelFeatures { ShowRootFoldersAtTopLevel = false };

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken token)
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

        private ChannelItemInfo Folder(ConfigurationSnapshot snapshot, NavigationEntry entry) => new ChannelItemInfo
        {
            Id = directory.Register(snapshot, entry),
            Name = entry.Name,
            Type = ChannelItemType.Folder
        };

        private async Task<List<ChannelItemInfo>> BrowseAsync(ConfigurationSnapshot snapshot, NavigationEntry entry, CancellationToken token)
        {
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
                    var id = directory.Register(snapshot, new NavigationEntry
                    {
                        Category = entry.Category,
                        SelectionId = entry.SelectionId,
                        StationId = station,
                        Name = name
                    });
                    items.Add(new ChannelItemInfo
                    {
                        Id = id,
                        Name = name,
                        Type = ChannelItemType.Media,
                        ContentType = station[0] == 's' ? ChannelMediaContentType.Song : ChannelMediaContentType.Podcast,
                        MediaType = ChannelMediaType.Audio,
                        IsLiveStream = station[0] == 's'
                    });
                }
                else if (type == "link")
                {
                    var link = DirectoryRequest.ParseLink(TuneInDirectory.Attribute(row, "URL"));
                    if (link.Method != "Browse.ashx") continue;
                    var next = ScopedRequest(snapshot, entry, link);
                    var classifier = next.Parameters.GetValueOrDefault("c");
                    if (classifier != null && classifier != entry.Category
                        && !(entry.Category == "language" && new[] { "lang", "music", "talk", "sports" }.Contains(classifier))
                        && !(entry.Category == "local" && classifier == "local")) continue;
                    var rowKey = TuneInDirectory.Attribute(row, "key") ?? "";
                    if (rowKey.StartsWith("next", StringComparison.Ordinal)
                        && next.Parameters.GetValueOrDefault("id") != request.Parameters.GetValueOrDefault("id"))
                        throw new InvalidDataException("TuneIn pagination escaped its selected node.");
                    var id = next.Parameters.GetValueOrDefault("id");
                    if ((entry.Category == "location" || entry.Category == "local") && id != null
                        && !ConfigurationSnapshot.ValidId(id, "location")) continue;
                    if (entry.Category == "music" && id != null && !ConfigurationSnapshot.ValidId(id, "music")) continue;
                    if (entry.Category == "language" && id != null && !ConfigurationSnapshot.ValidId(id, "music")) continue;
                    items.Add(Folder(snapshot, new NavigationEntry
                    {
                        Category = entry.Category,
                        SelectionId = entry.SelectionId,
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
            var request = ScopedRequest(snapshot, entry, entry.Request);
            request.Parameters["formats"] = "mp3,aac";
            request.Parameters["partnerid"] = "uD1X52pA";
            return request;
        }

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

        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken token)
        {
            var snapshot = directory.Snapshot();
            var entry = directory.Resolve(snapshot, id);
            if (!ValidStation(entry.StationId)) throw new InvalidOperationException("TuneIn item is not playable.");
            logger.Debug("Preparing protected TuneIn radio relay.");
            token.ThrowIfCancellationRequested();
            var local = host.GetLocalApiUrl(System.Net.IPAddress.Loopback);
            var path = local.TrimEnd('/') + "/TuneInFork/Relay/" + id;
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

        private static string CategoryName(string category)
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
