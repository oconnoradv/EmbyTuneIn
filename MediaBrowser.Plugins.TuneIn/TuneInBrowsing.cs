using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
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
    internal sealed class TuneInBrowsing
    {
        private readonly TuneInDirectory directory;
        private readonly Func<string, string> image;

        internal TuneInBrowsing(TuneInDirectory directory, Func<string, string> image)
        {
            this.directory = directory;
            this.image = image;
        }

        public async Task<ChannelItemResult> GetItemsAsync(string folderId, CancellationToken token,
            TuneInCatalogueSession session = null, ConfigurationSnapshot snapshot = null)
        {
            snapshot = snapshot ?? directory.Snapshot();
            var items = new List<ChannelItemInfo>();
            if (!snapshot.Ready) return new ChannelItemResult { Items = items };
            directory.CheckCurrent(snapshot, token);
            if (string.IsNullOrEmpty(folderId))
            {
                foreach (var category in snapshot.Categories)
                    items.Add(CategoryRoot(snapshot, category));
            }
            else
            {
                var entry = directory.Resolve(snapshot, folderId);
                if (entry.StationId != null) throw new InvalidOperationException("TuneIn item is not a folder.");
                if (entry.Request != null)
                    items = await BrowseAsync(snapshot, entry, token, session).ConfigureAwait(false);
                else if (entry.Category == "music" || entry.Category == "language" || entry.Category == "location")
                {
                    foreach (var starting in StartingEntries(snapshot, entry.Category))
                    {
                        var selection = snapshot.Selections(entry.Category).First(s => s.Id == starting.SelectionId);
                        starting.Name = selection.Name ?? selection.Id;
                        items.Add(Folder(snapshot, starting));
                    }
                }
                else
                {
                    foreach (var starting in StartingEntries(snapshot, entry.Category))
                        items.AddRange(await BrowseAsync(snapshot, starting, token, session).ConfigureAwait(false));
                    items = Deduplicate(snapshot, items);
                }
            }
            directory.CheckCurrent(snapshot, token);
            return new ChannelItemResult { Items = items };
        }

        internal ChannelItemInfo CategoryRoot(ConfigurationSnapshot snapshot, string category) =>
            Folder(snapshot, new NavigationEntry { Category = category, Name = CategoryName(category) });

        private ChannelItemInfo Folder(ConfigurationSnapshot snapshot, NavigationEntry entry) => new ChannelItemInfo
        {
            Id = directory.Register(snapshot, entry), Name = entry.Name, Type = ChannelItemType.Folder,
            ImageUrl = image(CategoryName(entry.Category))
        };

        private async Task<List<ChannelItemInfo>> BrowseAsync(ConfigurationSnapshot snapshot, NavigationEntry entry,
            CancellationToken token, TuneInCatalogueSession session)
        {
            if (IsLanguageMusicHub(entry))
                return snapshot.Selections("music").Select(selection =>
                {
                    var selected = new DirectoryRequest();
                    selected.Parameters["id"] = selection.Id;
                    return Folder(snapshot, new NavigationEntry
                    {
                        Category = "language", SelectionId = entry.SelectionId, ContentCategory = "music",
                        MusicSelectionId = selection.Id, Request = selected, Name = selection.Name ?? selection.Id
                    });
                }).ToList();
            if (entry.Category == "language")
            {
                var languages = await directory.SelectAsync("language", null, false, token, session).ConfigureAwait(false);
                if (!languages.Items.Any(i => i.Id == entry.SelectionId))
                    throw new InvalidOperationException("Selected TuneIn language is unavailable; load the language selector again.");
            }
            var request = CatalogueRequest(snapshot, entry);
            var document = await directory.FetchAsync(snapshot, request, token, session).ConfigureAwait(false);
            var items = new List<ChannelItemInfo>();
            foreach (var row in ContentRows(document.Root.Element("body")))
            {
                var type = TuneInDirectory.Attribute(row, "type");
                var name = TuneInDirectory.Attribute(row, "text") ?? "TuneIn";
                if (name.Length > 256) throw new InvalidDataException("TuneIn label exceeds limit.");
                if (type == "audio")
                {
                    var station = TuneInDirectory.Attribute(row, "guide_id");
                    if (!ValidStation(station))
                    {
                        var link = DirectoryRequest.ParseLink(TuneInDirectory.Attribute(row, "URL"),
                            reason => (entry.Category == "music" || snapshot.Value.AllowPartialCatalogImport)
                                && session?.TrySkipCatalogLink(entry.Category, reason, request,
                                    TuneInDirectory.Attribute(row, "URL"), stationUrl: true) == true,
                            allowSkippingTune: true);
                        if (link == null) continue;
                        station = link.Parameters.GetValueOrDefault("id");
                    }
                    if (!ValidStation(station)) throw new InvalidDataException("Invalid TuneIn station identifier.");
                    items.Add(new ChannelItemInfo
                    {
                        Id = directory.Register(snapshot, new NavigationEntry
                        {
                            Category = entry.Category, SelectionId = entry.SelectionId,
                            ContentCategory = entry.Category == "language" ? LanguageGroup(entry, entry.Request) : null,
                            MusicSelectionId = entry.MusicSelectionId, StationId = station, Name = name
                        }),
                        Name = name, Type = ChannelItemType.Media,
                        ContentType = station[0] == 's' ? ChannelMediaContentType.Song : ChannelMediaContentType.Podcast,
                        MediaType = ChannelMediaType.Audio, ImageUrl = TuneInDirectory.Attribute(row, "image")
                    });
                }
                else if (type == "link")
                {
                    var link = DirectoryRequest.ParseLink(TuneInDirectory.Attribute(row, "URL"),
                        reason => session?.TrySkipCatalogLink(entry.Category, reason, request,
                            TuneInDirectory.Attribute(row, "URL")) == true,
                        allowSkippingTune: entry.Category == "music" || snapshot.Value.AllowPartialCatalogImport);
                    if (link == null || link.Method != "Browse.ashx") continue;
                    var next = ScopedRequest(snapshot, entry, link);
                    var contentCategory = entry.Category == "language" ? LanguageGroup(entry, next) : null;
                    if (contentCategory != null && contentCategory != "language" && !snapshot.Enabled(contentCategory)) continue;
                    if (entry.ContentCategory != null && entry.ContentCategory != "language" && contentCategory != entry.ContentCategory) continue;
                    if (entry.MusicSelectionId != null && next.Parameters.ContainsKey("c")) continue;
                    var classifier = next.Parameters.GetValueOrDefault("c");
                    if (classifier != null && classifier != entry.Category
                        && !(entry.Category == "language" && new[] { "lang", "music", "talk", "sports" }.Contains(classifier))) continue;
                    var rowKey = TuneInDirectory.Attribute(row, "key") ?? "";
                    if (rowKey.StartsWith("next", StringComparison.Ordinal)
                        && !IsSamePaginationNode(entry, request, next))
                        throw new InvalidDataException("TuneIn pagination escaped its selected node ("
                            + (request.Parameters.GetValueOrDefault("id") ?? "(no ID)") + " to "
                            + (next.Parameters.GetValueOrDefault("id") ?? "(no ID)") + ").");
                    if (snapshot.Value.HideRegionalGenres && HidesGenreAliases(entry.Category)
                        && !rowKey.StartsWith("next", StringComparison.Ordinal) && !next.Parameters.ContainsKey("offset")
                        && !IsGenrePivot(request) && !IsGenrePivot(next)
                        && Regex.IsMatch(next.Parameters.GetValueOrDefault("filter") ?? "",
                            @"\A(?:g[1-9][0-9]{0,9}|s:g[1-9][0-9]{0,9}:country)\z"))
                        continue;
                    var id = next.Parameters.GetValueOrDefault("id");
                    if (id == null && classifier != null && (entry.Category == "music" || entry.Category == "location"
                        || entry.Category == "local" && entry.SelectionId != "coordinates")) continue;
                    if ((entry.Category == "location" || entry.Category == "local") && id != null
                        && !ConfigurationSnapshot.ValidId(id, "location")) continue;
                    if ((entry.Category == "music" || entry.Category == "language") && id != null
                        && !ConfigurationSnapshot.ValidId(id, "music")) continue;
                    if (EscapesSelection(snapshot, entry, id)) continue;
                    items.Add(Folder(snapshot, new NavigationEntry
                    {
                        Category = entry.Category, SelectionId = entry.SelectionId, ContentCategory = contentCategory,
                        MusicSelectionId = entry.MusicSelectionId, Request = next, Name = name
                    }));
                }
            }
            return Deduplicate(snapshot, items);
        }

        private static bool HidesGenreAliases(string category) =>
            category == "local" || category == "location" || category == "talk" ||
            category == "sports" || category == "podcast";

        private static bool IsGenrePivot(DirectoryRequest request)
        {
            var pivot = request.Parameters.GetValueOrDefault("pivot");
            return pivot == "genre" || pivot == "genres";
        }

        internal static bool IsForwardPage(ConfigurationSnapshot snapshot, NavigationEntry parent, NavigationEntry child)
        {
            if (parent.Request == null || child.Request == null || parent.Category != child.Category
                || parent.SelectionId != child.SelectionId || parent.ContentCategory != child.ContentCategory
                || parent.MusicSelectionId != child.MusicSelectionId)
                return false;
            var current = CatalogueRequest(snapshot, parent);
            var next = CatalogueRequest(snapshot, child);
            if (!int.TryParse(next.Parameters.GetValueOrDefault("offset"), out var offset) || offset <= 0
                || !IsSamePaginationNode(parent, current, next))
                return false;
            var currentOffset = current.Parameters.GetValueOrDefault("offset");
            if (currentOffset != null && (!int.TryParse(currentOffset, out var previous) || previous < 0 || offset <= previous))
                return false;
            if (new[] { "c", "pivot", "sid" }.Any(key =>
                current.Parameters.GetValueOrDefault(key) != next.Parameters.GetValueOrDefault(key)))
                return false;
            var filter = current.Parameters.GetValueOrDefault("filter");
            // The first More link can select stations or shows from an unfiltered listing.
            return filter == next.Parameters.GetValueOrDefault("filter")
                || currentOffset == null && (filter == null || parent.Category == "language" && filter == parent.SelectionId);
        }

        private static bool IsSamePaginationNode(NavigationEntry entry, DirectoryRequest current, DirectoryRequest next)
        {
            var from = current.Parameters.GetValueOrDefault("id");
            var to = next.Parameters.GetValueOrDefault("id");
            if (from == to) return true;
            if (entry.Category != "music" && entry.MusicSelectionId == null) return false;
            if (current.Parameters.ContainsKey("c") || next.Parameters.ContainsKey("c")
                || !int.TryParse(next.Parameters.GetValueOrDefault("offset"), out var offset) || offset <= 0)
                return false;
            // TuneIn emits the canonical category ID in More Stations/Shows links for these legacy genre IDs.
            switch (from)
            {
                case "g3": return to == "c57935";
                case "g19": return to == "c57951";
                case "g22": return to == "c57954";
                case "g71": return to == "c100002515";
                case "g373": return to == "c100002519";
                case "g2698": return to == "c100002520";
                default: return false;
            }
        }

        private static bool EscapesSelection(ConfigurationSnapshot snapshot, NavigationEntry entry, string id)
        {
            if (id == null) return false;
            var category = entry.MusicSelectionId == null ? entry.Category : "music";
            var selected = entry.MusicSelectionId ?? entry.SelectionId;
            var selection = snapshot.Selections(category).FirstOrDefault(s => s.Id == selected);
            // Provider links may ascend to a saved selection's parents; never sign those as descendants.
            return (selection?.AncestorIds ?? new List<string>()).Contains(id) && id != selected
                || category == "music" && (id == "c424724" || id == "c424725" || id == "c424726");
        }

        internal static DirectoryRequest ScopedRequest(ConfigurationSnapshot snapshot, NavigationEntry entry, DirectoryRequest source)
        {
            var request = source.Copy();
            if (entry.Category == "language")
            {
                var id = request.Parameters.GetValueOrDefault("id");
                if (id == "c424724" || id == "c424725" || id == "c424726")
                {
                    request.Parameters.Remove("id");
                    request.Parameters["c"] = id == "c424724" ? "music" : id == "c424725" ? "talk" : "sports";
                }
                var tokens = (request.Parameters.GetValueOrDefault("filter") ?? "").Split(':')
                    .Where(s => s.Length > 0 && !Regex.IsMatch(s, @"\Al[0-9]+\z")).ToList();
                if (tokens.Any(s => !Regex.IsMatch(s,
                    @"\A(s|p|topic|show|station|popular|topvoted|topclick|random|bit(32|64|128)[*-]?|up-(low|med|hi)|~[\p{L}\p{Nd}#])\z")))
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
                throw new InvalidOperationException("TuneIn language sub-group is disabled.");
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
            if (classifier == "lang")
                return id == null ? entry.ContentCategory ?? "language"
                    : entry.ContentCategory == "language" ? "" : entry.ContentCategory ?? "";
            return entry.ContentCategory ?? (id == null ? "language" : "");
        }

        internal static bool IsLanguageMusicHub(NavigationEntry entry) =>
            entry.Category == "language" && entry.MusicSelectionId == null && entry.StationId == null
            && entry.Request != null && (entry.Request.Parameters.GetValueOrDefault("id") == "c424724"
                || !entry.Request.Parameters.ContainsKey("id") && entry.Request.Parameters.GetValueOrDefault("c") == "music");

        internal static List<NavigationEntry> StartingEntries(ConfigurationSnapshot snapshot, string category)
        {
            if (!snapshot.Enabled(category)) throw new InvalidOperationException("Configure and enable this TuneIn group first.");
            var entries = new List<NavigationEntry>();
            if (category == "local" && snapshot.LocalSource == "coordinates")
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
                if (new[] { "related", "recommendations", "affiliates", "pivotLocation", "localCountry" }.Contains(key)) continue;
                if (TuneInDirectory.Attribute(row, "type") != null) yield return row;
                else foreach (var child in ContentRows(row)) yield return child;
            }
        }

        private List<ChannelItemInfo> Deduplicate(ConfigurationSnapshot snapshot, List<ChannelItemInfo> items)
        {
            var seen = new HashSet<string>();
            return items.Where(item =>
            {
                var entry = directory.Resolve(snapshot, item.Id);
                return seen.Add(entry.StationId != null ? "station:" + entry.StationId
                    : entry.Category + ":" + entry.SelectionId + ":" + entry.Request?.Url + ":" + entry.Name);
            }).ToList();
        }

        internal static bool ValidStation(string value) => value != null && Regex.IsMatch(value, @"\A[spt][1-9][0-9]{0,9}\z");

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
                case "presets": return "My Favorites";
                default: throw new ArgumentException("Invalid TuneIn category.");
            }
        }
    }
}
