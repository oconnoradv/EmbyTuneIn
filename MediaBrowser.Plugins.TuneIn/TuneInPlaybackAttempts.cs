using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInPlaybackAttempts<T> where T : class
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Func<DateTime> clock;
        private readonly int capacity;
        private readonly TimeSpan lifetime;

        internal TuneInPlaybackAttempts(Func<DateTime> clock = null, int capacity = 64, TimeSpan? lifetime = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.capacity = capacity;
            this.lifetime = lifetime ?? TimeSpan.FromSeconds(30);
        }

        internal string Add(TuneInDirectory directory, string configuration, string id, T plan)
        {
            var bytes = new byte[24];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var handle = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            lock (gate)
            {
                var now = clock();
                foreach (var key in entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                    entries.Remove(key);
                if (entries.Count >= capacity)
                    entries.Remove(entries.OrderBy(pair => pair.Value.Expires).First().Key);
                entries.Add(handle, new Entry(directory, configuration, id, plan, now + lifetime));
            }
            return handle;
        }

        internal T Take(TuneInDirectory directory, string configuration, string id, string handle)
        {
            if (handle.Length != 32 || handle.Any(c => !(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')
                && !(c >= '0' && c <= '9') && c != '-' && c != '_'))
                throw new InvalidOperationException("Invalid TuneIn playback attempt handle.");
            lock (gate)
            {
                if (!entries.TryGetValue(handle, out var entry)) return null;
                if (entry.Expires <= clock()) { entries.Remove(handle); return null; }
                if (!ReferenceEquals(entry.Directory, directory) || entry.Configuration != configuration || entry.Id != id)
                    throw new InvalidOperationException("TuneIn playback attempt is outside its original scope.");
                entries.Remove(handle);
                return entry.Plan;
            }
        }

        private sealed class Entry
        {
            internal readonly TuneInDirectory Directory;
            internal readonly string Configuration;
            internal readonly string Id;
            internal readonly T Plan;
            internal readonly DateTime Expires;

            internal Entry(TuneInDirectory directory, string configuration, string id, T plan, DateTime expires)
            {
                Directory = directory;
                Configuration = configuration;
                Id = id;
                Plan = plan;
                Expires = expires;
            }
        }
    }
}
