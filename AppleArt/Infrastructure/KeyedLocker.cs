using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnimatedArtworks.Infrastructure;

public sealed class KeyedLocker
{
    private readonly Dictionary<string, Entry> _entries = new();

    public async Task<IDisposable> LockAsync(string key, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new Handle(this, key, entry);
    }

    private void Release(string key, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_entries)
        {
            if (--entry.RefCount == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private sealed class Handle(KeyedLocker owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(key, entry, held: true);
            }
        }
    }
}
