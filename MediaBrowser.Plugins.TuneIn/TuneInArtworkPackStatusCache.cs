using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MediaBrowser.Plugins.TuneIn
{
    // Optional display/status scans only. Never use cached results to validate an installation.
    internal sealed class TuneInArtworkPackStatusCache
    {
        internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
        private readonly TimeSpan maxAge;
        private readonly Func<string, TuneInArtworkPackStatus> scan;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly Func<TimeSpan> elapsedNow;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal TuneInArtworkPackStatusCache(TimeSpan? maxAge = null)
            : this(maxAge ?? MaximumAge, TuneInCategoryArtwork.ReadPackStatus,
                () => DateTimeOffset.UtcNow,
                () => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency))
        {
        }

        internal TuneInArtworkPackStatusCache(TimeSpan maxAge, Func<string, TuneInArtworkPackStatus> scan,
            Func<DateTimeOffset> utcNow, Func<TimeSpan> elapsedNow)
        {
            if (maxAge <= TimeSpan.Zero || maxAge > MaximumAge) throw new ArgumentOutOfRangeException(nameof(maxAge));
            this.maxAge = maxAge;
            this.scan = scan ?? throw new ArgumentNullException(nameof(scan));
            this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            this.elapsedNow = elapsedNow ?? throw new ArgumentNullException(nameof(elapsedNow));
        }

        // Skipping returns the last snapshot (even if expired), or null if none exists.
        // Force takes precedence over skipping. No timer or background scan is started.
        internal TuneInArtworkPackStatusSnapshot Read(string scope, bool scanIfNeeded = false, bool forceRescan = false)
        {
            ValidateScope(scope);
            lock (TuneInCategoryArtwork.PackGate)
            {
                entries.TryGetValue(scope, out var entry);
                if (!forceRescan)
                {
                    if (!scanIfNeeded) return entry?.Snapshot;
                    if (entry != null)
                    {
                        var age = elapsedNow() - entry.CompletedAt;
                        if (age >= TimeSpan.Zero && age < maxAge) return entry.Snapshot;
                    }

                }

                var status = scan(scope);
                var snapshot = new TuneInArtworkPackStatusSnapshot(status, utcNow());
                entries[scope] = new Entry(snapshot, elapsedNow());
                return snapshot;
            }
        }

        internal bool TryRead(string scope, bool scanIfNeeded, out TuneInArtworkPackStatusSnapshot snapshot)
        {
            ValidateScope(scope);
            if (!Monitor.TryEnter(TuneInCategoryArtwork.PackGate))
            {
                snapshot = null;
                return false;
            }
            try
            {
                snapshot = Read(scope, scanIfNeeded);
                return true;
            }
            finally { Monitor.Exit(TuneInCategoryArtwork.PackGate); }
        }

        // Call after installation, inside the installer's PackGate transaction when possible,
        // so publication of the new files and invalidation are atomic to status readers.
        internal void Invalidate(string scope = null)
        {
            if (scope != null) ValidateScope(scope);
            lock (TuneInCategoryArtwork.PackGate)
            {
                if (scope == null) entries.Clear();
                else entries.Remove(scope);
            }
        }

        private static void ValidateScope(string scope)
        {
            if (Array.IndexOf(TuneInCategoryArtwork.Scopes, scope) < 0)
                throw new ArgumentException("Unknown artwork scope.", nameof(scope));
        }

        private sealed class Entry
        {
            internal readonly TuneInArtworkPackStatusSnapshot Snapshot;
            internal readonly TimeSpan CompletedAt;

            internal Entry(TuneInArtworkPackStatusSnapshot snapshot, TimeSpan completedAt)
            {
                Snapshot = snapshot;
                CompletedAt = completedAt;
            }
        }
    }

    internal sealed class TuneInArtworkPackStatusSnapshot
    {
        private readonly TuneInArtworkPackStatus status;
        internal DateTimeOffset CheckedAtUtc { get; }
        internal TuneInArtworkPackStatus Status => Copy(status);

        internal TuneInArtworkPackStatusSnapshot(TuneInArtworkPackStatus status, DateTimeOffset checkedAtUtc)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            this.status = Copy(status);
            CheckedAtUtc = checkedAtUtc.ToUniversalTime();
        }

        private static TuneInArtworkPackStatus Copy(TuneInArtworkPackStatus value) => new TuneInArtworkPackStatus
        {
            Scope = value.Scope,
            Path = value.Path,
            Status = value.Status,
            Name = value.Name,
            ImageCount = value.ImageCount,
            InstalledCount = value.InstalledCount,
            MatchedCount = value.MatchedCount,
            ModifiedCount = value.ModifiedCount,
            AddedCount = value.AddedCount
        };
    }
}
