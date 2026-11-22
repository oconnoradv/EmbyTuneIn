using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    public sealed class TuneInCleanupPreview
    {
        public string Token { get; set; }
        public int Folders { get; set; }
        public int Stations { get; set; }
        public string Message { get; set; }
        public string Scope { get; set; }
        public string StationName { get; set; }
        public string StationReference { get; set; }
    }

    public sealed class TuneInCleanupStationMatch
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }

    public sealed class TuneInCleanupStationSearch
    {
        public TuneInCleanupStationMatch[] Matches { get; set; } = Array.Empty<TuneInCleanupStationMatch>();
        public bool More { get; set; }
    }

    public sealed class TuneInCleanupStatus
    {
        public string State { get; set; } = "Idle";
        public int Total { get; set; }
        public int Removed { get; set; }
        public int Failed { get; set; }
        public bool Cancelling { get; set; }
        public string Message { get; set; }
        internal TuneInCleanupStatus Copy() => (TuneInCleanupStatus)MemberwiseClone();
    }

    internal sealed class TuneInLibraryCleanup
    {
        internal const int BatchSize = 500;
        internal const int ParallelRemovals = 4;
        private readonly ILibraryManager library;
        private readonly Func<Channel> getRoot;
        private readonly ILogger logger;

        internal TuneInLibraryCleanup(ILibraryManager library, Func<Channel> getRoot, ILogger logger)
        {
            this.library = library;
            this.getRoot = getRoot;
            this.logger = logger;
        }

        internal sealed class Plan
        {
            internal long Root;
            internal Target[] Targets;
            internal int Unverified;
            internal string Category;
            internal bool SingleStation;
            internal Target[] Ancestors;
            internal string StationName;
        }

        internal Plan PreviewStation(ConfigurationSnapshot snapshot, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference) || reference.Length > 2048)
                throw new ArgumentException("Enter an Emby item ID or station details URL.");
            var id = reference.Trim();
            if (Uri.TryCreate(id, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                var match = Regex.Match(uri.Query + uri.Fragment, @"[?&]id=([^&#]+)", RegexOptions.IgnoreCase);
                if (!match.Success) throw new ArgumentException("The Emby details URL must contain an item id.");
                id = Uri.UnescapeDataString(match.Groups[1].Value);
            }
            BaseItem item;
            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId) && internalId > 0)
                item = library.GetItemById(internalId);
            else if (Guid.TryParse(id, out var guid) && guid != Guid.Empty)
                item = library.GetItemById(guid);
            else throw new ArgumentException("Enter a numeric or GUID Emby item ID, not a station name or TuneIn station ID.");
            if (!(item is MediaBrowser.Controller.Entities.Audio.Audio)
                || !TuneInDirectory.GeneratedStation(snapshot, item.ExternalId))
                throw new InvalidOperationException("The reference is not a verified TuneIn station entry.");
            var root = getRoot() ?? throw new InvalidOperationException("TuneIn channel is not registered.");
            var category = TuneInDirectory.GeneratedCategory(snapshot, item.ExternalId);
            if (category == null) throw new InvalidOperationException("The TuneIn station category cannot be verified.");
            var ancestors = new List<Target>();
            var seen = new HashSet<long> { item.InternalId };
            var parentId = item.ParentId;
            while (parentId != root.InternalId)
            {
                if (!seen.Add(parentId)) throw new InvalidOperationException("The station ancestor chain is cyclic.");
                var parent = library.GetItemById(parentId);
                if (!(parent is Folder) || parent is Channel
                    || TuneInDirectory.GeneratedCategory(snapshot, parent.ExternalId) != category)
                    throw new InvalidOperationException("The station is not beneath its verified TuneIn category.");
                ancestors.Add(new Target(parent));
                parentId = parent.ParentId;
            }
            if (ancestors.Count == 0 || TuneInDirectory.GeneratedCategory(snapshot, ancestors.Last().ExternalId, true) != category)
                throw new InvalidOperationException("The station category root cannot be verified.");
            return new Plan
            {
                Root = root.InternalId, Category = category, Targets = new[] { new Target(item) },
                Ancestors = ancestors.ToArray(), SingleStation = true, StationName = item.Name
            };
        }

        internal const int MaxStationMatches = 50;

        internal TuneInCleanupStationSearch SearchStations(ConfigurationSnapshot snapshot, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length < 2 || name.Length > 100) throw new ArgumentException("Enter 2 to 100 characters of the station name.");
            var root = getRoot();
            if (root == null) return new TuneInCleanupStationSearch();
            if (root.InternalId <= 0) throw new InvalidOperationException("The TuneIn channel has no library ID.");
            var items = library.GetItemList(new InternalItemsQuery
            {
                ParentIds = new[] { root.InternalId }, Recursive = true, Limit = TuneInStagedImport.MaxItems + 1
            }, CancellationToken.None);
            if (items.Length > TuneInStagedImport.MaxItems)
                throw new InvalidOperationException("The TuneIn station search exceeds its library item limit.");
            var byId = new Dictionary<long, BaseItem>();
            foreach (var item in items) byId[item.InternalId] = item;
            string PathOf(BaseItem item)
            {
                var names = new List<string>();
                var seen = new HashSet<long> { item.InternalId };
                var parentId = item.ParentId;
                while (parentId != root.InternalId)
                {
                    if (!seen.Add(parentId) || !byId.TryGetValue(parentId, out var parent)) return null;
                    names.Add(parent.Name);
                    parentId = parent.ParentId;
                }
                names.Reverse();
                return names.Count == 0 ? null : string.Join(" › ", names);
            }
            var matches = items
                .Where(item => item is MediaBrowser.Controller.Entities.Audio.Audio && item.InternalId > 0
                    && (item.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    && TuneInDirectory.GeneratedStation(snapshot, item.ExternalId))
                .Select(item => new TuneInCleanupStationMatch
                {
                    Id = item.InternalId.ToString(CultureInfo.InvariantCulture), Name = item.Name, Path = PathOf(item)
                })
                .Where(match => match.Path != null)
                .OrderBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxStationMatches + 1).ToArray();
            return new TuneInCleanupStationSearch
            {
                Matches = matches.Take(MaxStationMatches).ToArray(), More = matches.Length > MaxStationMatches
            };
        }

        internal sealed class Target
        {
            internal readonly long Id;
            internal readonly long ParentId;
            internal readonly string ExternalId;
            internal readonly Type ItemType;
            internal readonly bool IsFolder;

            internal Target(BaseItem item)
            {
                Id = item.InternalId;
                ParentId = item.ParentId;
                ExternalId = item.ExternalId;
                ItemType = item.GetType();
                IsFolder = item is Folder;
            }

            internal bool Matches(BaseItem current) =>
                current != null && current.InternalId == Id && current.ParentId == ParentId &&
                current.GetType() == ItemType && current.ExternalId == ExternalId;
        }

        internal Plan Preview(ConfigurationSnapshot snapshot, string category = null)
        {
            if (category != null) TuneInBrowsing.CategoryName(category);
            var root = getRoot();
            if (root == null) return new Plan { Targets = Array.Empty<Target>() };
            if (root.InternalId <= 0) throw new InvalidOperationException("The TuneIn channel has no library ID.");
            var items = library.GetItemList(new InternalItemsQuery
            {
                ParentIds = new[] { root.InternalId }, Recursive = true, Limit = TuneInStagedImport.MaxItems + 1
            }, CancellationToken.None);
            if (items.Length > TuneInStagedImport.MaxItems)
                throw new InvalidOperationException("The TuneIn cleanup preview exceeds its library item limit.");

            var children = items.ToLookup(item => item.ParentId);
            var seen = new HashSet<long> { root.InternalId };
            var targets = new List<Target>();
            var pending = new Stack<long>();
            var candidates = items.Length;
            if (category == null) pending.Push(root.InternalId);
            else
            {
                var roots = children[root.InternalId].Where(item => item is Folder && !(item is Channel)
                    && TuneInDirectory.GeneratedCategory(snapshot, item.ExternalId, true) == category).ToArray();
                var scoped = new HashSet<long>();
                var scopePending = new Stack<long>(roots.Select(item => item.InternalId));
                while (scopePending.Count != 0)
                {
                    var id = scopePending.Pop();
                    if (!scoped.Add(id)) continue;
                    foreach (var child in children[id]) scopePending.Push(child.InternalId);
                }
                candidates = scoped.Count;
                foreach (var item in roots)
                {
                    if (item.InternalId <= 0 || !seen.Add(item.InternalId)) continue;
                    targets.Add(new Target(item));
                    pending.Push(item.InternalId);
                }
            }
            while (pending.Count != 0)
            {
                foreach (var item in children[pending.Pop()])
                {
                    if (item.InternalId <= 0 || !seen.Add(item.InternalId) || item is Channel ||
                        (category == null ? !TuneInDirectory.IsGeneratedId(snapshot, item.ExternalId)
                            : TuneInDirectory.GeneratedCategory(snapshot, item.ExternalId) != category))
                        continue;
                    targets.Add(new Target(item));
                    if (item is Folder) pending.Push(item.InternalId);
                }
            }
            targets.Reverse(); // Children must be attempted before their parents.
            return new Plan { Root = root.InternalId, Targets = targets.ToArray(), Unverified = candidates - targets.Count,
                Category = category };
        }

        internal async Task RunAsync(Plan plan, Action<int, int> progress, CancellationToken token)
        {
            var removed = 0;
            var failed = 0;
            var reportedRemoved = -1;
            var reportedFailed = -1;
            var targets = plan.Targets.Concat(plan.Ancestors ?? Array.Empty<Target>()).ToDictionary(item => item.Id);
            void Report()
            {
                if (reportedRemoved == removed && reportedFailed == failed) return;
                progress(removed, failed);
                reportedRemoved = removed;
                reportedFailed = failed;
            }
            void Remove(Target target)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var current = library.GetItemById(target.Id);
                    if (!target.Matches(current) || !StillUnderRoot(plan.Root, target, targets) ||
                        (current is Folder && library.GetItemList(new InternalItemsQuery
                        {
                            ParentIds = new[] { current.InternalId }, Limit = 1
                        }, token).Length != 0))
                        throw new InvalidOperationException("Item changed or contains entries not removed by cleanup.");
                    token.ThrowIfCancellationRequested();
                    library.DeleteItem(current, new DeleteOptions
                    {
                        DeleteFileLocation = false, DeleteFromExternalProvider = false
                    });
                    if (library.GetItemById(current.InternalId) != null)
                        throw new InvalidOperationException("Emby still reports the library entry after deletion.");
                    Interlocked.Increment(ref removed);
                }
                catch (Exception error) when (!(error is OperationCanceledException))
                {
                    Interlocked.Increment(ref failed);
                    logger?.Error("TuneIn cleanup could not remove library item {0} ({1}).", target.Id, error.GetType().Name);
                }
            }
            try
            {
                for (var start = 0; start < plan.Targets.Length; start += BatchSize)
                {
                    var end = Math.Min(start + BatchSize, plan.Targets.Length);
                    for (var index = start; index < end;)
                    {
                        token.ThrowIfCancellationRequested();
                        if (getRoot()?.InternalId != plan.Root)
                            throw new InvalidOperationException("The TuneIn channel root changed after preview.");
                        var parent = plan.Targets[index].ParentId;
                        var tasks = new List<Task>(ParallelRemovals);
                        while (index < end && tasks.Count < ParallelRemovals && plan.Targets[index].ParentId == parent)
                        {
                            var target = plan.Targets[index++];
                            tasks.Add(Task.Run(() => Remove(target)));
                        }
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    Report();
                    if (end < plan.Targets.Length) await Task.Yield();
                }
            }
            finally { Report(); }
        }

        private bool StillUnderRoot(long root, Target item, Dictionary<long, Target> targets)
        {
            var parentId = item.ParentId;
            while (parentId != root)
            {
                if (!targets.TryGetValue(parentId, out var expected)) return false;
                var current = library.GetItemById(parentId);
                if (!expected.Matches(current)) return false;
                parentId = expected.ParentId;
            }
            return true;
        }
    }
}
