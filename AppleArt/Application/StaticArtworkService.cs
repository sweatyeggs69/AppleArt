using System;
using System.Threading;
using System.Threading.Tasks;
using AnimatedArtworks.Infrastructure;
using Serilog;

namespace AnimatedArtworks.Application;

public class StaticArtworkService(
    IAppleMusicClient appleMusicClient,
    StaticArtworkCache cache,
    KeyedLocker locker,
    AppleLookupLimiter lookupLimiter)
{
    private string _client = "unknown";

    public bool WasThrottled { get; private set; }

    public void SetClient(string client) => _client = client;

    public Task<(StaticArtworkLookupResult Result, bool IsCached)> SearchAsync(string artist, string album, CancellationToken ct) =>
        GetAsync(StaticArtworkCache.SearchKey(artist, album), () => appleMusicClient.SearchStaticArtworkAsync(artist, album, ct), ct);

    public Task<(StaticArtworkLookupResult Result, bool IsCached)> LookupAsync(AppleMusicCatalogTarget target, CancellationToken ct) =>
        GetAsync(StaticArtworkCache.LookupKey(target), () => appleMusicClient.LookupStaticArtworkAsync(target, ct), ct);

    private async Task<(StaticArtworkLookupResult Result, bool IsCached)> GetAsync(
        string key, Func<Task<StaticArtworkLookupResult>> fetch, CancellationToken ct)
    {
        if (cache.TryGet(key, out StaticArtworkLookupResult cached))
        {
            return (cached, true);
        }

        // The lock key is prefixed so it never collides with an animated artwork lookup for the same URL.
        string lockKey = "static|" + key;
        SemaphoreSlim semaphore = locker.GetLock(lockKey);
        await semaphore.WaitAsync(ct);

        try
        {
            if (cache.TryGet(key, out cached))
            {
                return (cached, true);
            }

            if (!lookupLimiter.TryAcquire(_client))
            {
                WasThrottled = true;
                Log.Information("Uncached Apple lookup limit reached for client {Client}", _client);
                return (new StaticArtworkLookupResult(StaticArtworkLookupStatus.RateLimited), false);
            }

            StaticArtworkLookupResult result = await fetch();
            cache.Save(key, result);
            return (result, false);
        }
        finally
        {
            semaphore.Release();
            locker.RemoveLock(lockKey);
        }
    }
}
