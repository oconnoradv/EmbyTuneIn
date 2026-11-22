using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
                else if (snapshot.UsesSelectedTopics(entry.Category))
                {
                    var topics = await SelectedTopicsAsync(snapshot, entry.Category, token, session).ConfigureAwait(false);
                    foreach (var id in snapshot.TopicIds(entry.Category))
                    {
                        var topic = topics[id];
                        items.Add(Folder(snapshot, new NavigationEntry
                        {
                            Category = entry.Category, SelectionId = id, Name = topic.Name, Request = topic.TopicRequest,
                            TopicBranchKey = TopicBranchKey(topic.TopicRequest)
                        }));
                    }
                }
                else if (entry.Category == "music" || entry.Category == "language" || entry.Category == "location")
                {
                    if (snapshot.UsesSelectedSelections(entry.Category))
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
                    }
                    items = Deduplicate(snapshot, items);
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

        private async Task<Dictionary<string, SelectorItem>> SelectedTopicsAsync(ConfigurationSnapshot snapshot, string category,
            CancellationToken token, TuneInCatalogueSession session)
        {
            var ids = snapshot.TopicIds(category).ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException("Choose at least one TuneIn topic or switch the category to All.");
            var root = await directory.ReadTopicRootAsync(snapshot, category, token, session).ConfigureAwait(false);
            var topics = root.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
            if (ids.Any(id => !topics.ContainsKey(id)))
                throw new InvalidOperationException("A selected TuneIn topic is no longer available in this category. Reload its selector and review the saved choices.");
            return topics;
        }

        private static string TopicBranchKey(DirectoryRequest request)
        {
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(request.Url)));
        }

        private async Task<List<ChannelItemInfo>> BrowseAsync(ConfigurationSnapshot snapshot, NavigationEntry entry,
            CancellationToken token, TuneInCatalogueSession session)
        {
            if (IsLanguageMusicHub(entry) && snapshot.LanguageUsesMusicGenres)
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
            if (entry.Category == "language" && entry.SelectionId != null)
            {
                var languages = await directory.SelectAsync("language", null, false, token, session).ConfigureAwait(false);
                if (!languages.Items.Any(i => i.Id == entry.SelectionId))
                    throw new InvalidOperationException("Selected TuneIn language is unavailable; load the language selector again.");
            }
            Dictionary<string, SelectorItem> topics = null;
            if (snapshot.UsesSelectedTopics(entry.Category))
            {
                topics = await SelectedTopicsAsync(snapshot, entry.Category, token, session).ConfigureAwait(false);
                if (entry.TopicBranchKey != TopicBranchKey(topics[entry.SelectionId].TopicRequest))
                    throw new InvalidOperationException("The selected TuneIn topic changed. Return to the category root.");
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
                            MusicSelectionId = entry.MusicSelectionId, TopicBranchKey = entry.TopicBranchKey,
                            StationId = station, Name = name
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
                    if (topics != null)
                    {
                        var topicId = next.Parameters.GetValueOrDefault("id");
                        if (next.Parameters.ContainsKey("c") || topicId == null
                            || topicId != entry.SelectionId && topics.ContainsKey(topicId)
                            || topicId == entry.SelectionId && entry.Category == "podcast"
                                && next.Parameters.GetValueOrDefault("filter") != "p:topic")
                            continue;
                    }
                    var contentCategory = entry.Category == "language" ? LanguageGroup(entry, next) : null;
                    if (entry.ContentCategory != null && entry.ContentCategory != "language" && contentCategory != entry.ContentCategory) continue;
                    if (entry.MusicSelectionId != null && next.Parameters.ContainsKey("c")) continue;
                    var classifier = next.Parameters.GetValueOrDefault("c");
                    if (classifier != null && classifier != entry.Category
                        && !(entry.Category == "language" && new[] { "lang", "music", "talk", "sports" }.Contains(classifier))) continue;
                    var rowKey = TuneInDirectory.Attribute(row, "key") ?? "";
                    if (rowKey.StartsWith("next", StringComparison.Ordinal)
                        && !IsSamePaginationNode(entry, request, next))
                    {
                        // Unknown page jumps are not followed; refreshes record them so the result is Partial and saved stations are retained.
                        session?.TrySkipCatalogLink(entry.Category, "TuneIn pagination escaped its selected node ("
                            + (request.Parameters.GetValueOrDefault("id") ?? "(no ID)") + " to "
                            + (next.Parameters.GetValueOrDefault("id") ?? "(no ID)") + ")", request,
                            TuneInDirectory.Attribute(row, "URL"));
                        continue;
                    }
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
                        MusicSelectionId = entry.MusicSelectionId, TopicBranchKey = entry.TopicBranchKey,
                        Request = next, Name = name
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
                || parent.MusicSelectionId != child.MusicSelectionId || parent.TopicBranchKey != child.TopicBranchKey)
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
            if (from == null || current.Parameters.ContainsKey("c") || next.Parameters.ContainsKey("c")
                || !int.TryParse(next.Parameters.GetValueOrDefault("offset"), out var offset) || offset <= 0)
                return false;
            if (CanonicalPaginationIds.TryGetValue(from, out var canonical)) return canonical == to;
            // TuneIn pages a genre's More Stations/Shows under that genre's own category ID; refuse IDs owned by another genre.
            return Regex.IsMatch(from, @"\Ag[1-9][0-9]{0,9}\z") && to != null
                && Regex.IsMatch(to, @"\Ac[1-9][0-9]{0,9}\z") && !CanonicalPaginationIds.ContainsValue(to)
                && (next.Parameters.GetValueOrDefault("filter") == "s" || next.Parameters.GetValueOrDefault("filter") == "p");
        }

        // Known canonical category IDs for legacy genre IDs in Music, Talk and Sports, including when By Language reaches
        // them. Other genres may page to their own unlisted category ID, but never to one listed here for another genre.
        private static readonly Dictionary<string, string> CanonicalPaginationIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["g3"] = "c57935", ["g19"] = "c57951", ["g22"] = "c57954", ["g71"] = "c100002515",
            ["g214"] = "c100000971", ["g217"] = "c100001374", ["g260"] = "c100001376", ["g307"] = "c100002786",
            ["g308"] = "c100002785", ["g312"] = "c100002782", ["g313"] = "c100005369", ["g322"] = "c100000899",
            ["g323"] = "c100000211", ["g326"] = "c100002783", ["g373"] = "c100002519", ["g2698"] = "c100002520",
            ["g2723"] = "c57933", ["g2733"] = "c100006068", ["g2737"] = "c100001101", ["g2751"] = "c23980497",
            ["g2762"] = "c100001675", ["g3078"] = "c100001377", ["g3165"] = "c100000599", ["g4161"] = "c100002539",
            ["g4421"] = "c100006414"
        };

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
                if (entry.SelectionId != null)
                {
                    var tokens = (request.Parameters.GetValueOrDefault("filter") ?? "").Split(':')
                        .Where(s => s.Length > 0 && !Regex.IsMatch(s, @"\Al[0-9]+\z")).ToList();
                    if (tokens.Any(s => !Regex.IsMatch(s,
                        @"\A(s|p|topic|show|station|popular|topvoted|topclick|random|bit(32|64|128)[*-]?|up-(low|med|hi)|~[\p{L}\p{Nd}#])\z")))
                        throw new InvalidDataException("TuneIn returned an unsupported language filter.");
                    tokens.Add(entry.SelectionId);
                    request.Parameters["filter"] = string.Join(":", tokens);
                }
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
            if (snapshot.UsesSelectedTopics(category))
                throw new InvalidOperationException("Selected TuneIn topics require provider validation.");
            var entries = new List<NavigationEntry>();
            if (category == "local" && snapshot.LocalSource == "coordinates")
            {
                var request = new DirectoryRequest();
                request.Parameters["c"] = "local";
                entries.Add(new NavigationEntry { Category = category, SelectionId = "coordinates", Request = request });
            }
            else if (new[] { "local", "language", "location", "music" }.Contains(category)
                && snapshot.UsesSelectedSelections(category))
            {
                foreach (var selection in snapshot.Selections(category))
                {
                    var request = new DirectoryRequest();
                    if (category == "language") request.Parameters["c"] = "lang";
                    else request.Parameters["id"] = selection.Id;
                    entries.Add(new NavigationEntry { Category = category, SelectionId = selection.Id, Request = request });
                }
            }
            else if (new[] { "local", "language", "location", "music" }.Contains(category))
            {
                var request = new DirectoryRequest();
                if (category == "language") request.Parameters["c"] = "lang";
                else if (category == "music") request.Parameters["c"] = "music";
                else request.Parameters["id"] = "r0";
                entries.Add(new NavigationEntry { Category = category, Request = request });
            }
            else
            {
                var request = new DirectoryRequest();
                request.Parameters["c"] = category;
                entries.Add(new NavigationEntry { Category = category, Request = request });
            }
            return entries;
        }

        internal static IEnumerable<XElement> ContentRows(XElement parent)
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
