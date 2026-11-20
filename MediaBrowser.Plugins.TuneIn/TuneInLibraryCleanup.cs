using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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
    }

    public sealed class TuneInCleanupStatus
    {
        public string State { get; set; } = "Idle";
        public int Total { get; set; }
        public int Removed { get; set; }
        public int Failed { get; set; }
        public string Message { get; set; }
        internal TuneInCleanupStatus Copy() => (TuneInCleanupStatus)MemberwiseClone();
    }

    internal sealed class TuneInLibraryCleanup
    {
        internal const int BatchSize = 50;
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
            var targets = plan.Targets.ToDictionary(item => item.Id);
            var options = new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false };
            for (var start = 0; start < plan.Targets.Length; start += BatchSize)
            {
                if (getRoot()?.InternalId != plan.Root)
                    throw new InvalidOperationException("The TuneIn channel root changed after preview.");
                var end = Math.Min(start + BatchSize, plan.Targets.Length);
                for (var index = start; index < end; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var target = plan.Targets[index];
                    try
                    {
                        var current = library.GetItemById(target.Id);
                        if (!target.Matches(current) || !StillUnderRoot(plan.Root, target, targets) ||
                            (current is Folder && library.GetItemList(new InternalItemsQuery
                            {
                                ParentIds = new[] { current.InternalId }, Limit = 1
                            }, token).Length != 0))
                            throw new InvalidOperationException("Item changed or contains entries not removed by cleanup.");
                        library.DeleteItem(current, options);
                        if (library.GetItemById(current.InternalId) != null)
                            throw new InvalidOperationException("Emby still reports the library entry after deletion.");
                        removed++;
                    }
                    catch (Exception error) when (!(error is OperationCanceledException))
                    {
                        failed++;
                        logger?.Error("TuneIn cleanup could not remove library item {0} ({1}).", target.Id, error.GetType().Name);
                    }
                }
                progress(removed, failed);
                if (end < plan.Targets.Length) await Task.Yield();
            }
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
