using System;
using System.Threading;
using System.Threading.Tasks;
using AnimatedArtworks.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnimatedArtworks.Application;

public class AlbumDetailsService(
    IAppleMusicClient appleMusicClient,
    IMemoryCache cache,
    KeyedLocker locker,
    AppleLookupLimiter lookupLimiter)
{
    private static readonly TimeSpan FoundTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan NoMatchTtl = TimeSpan.FromHours(1);

    public async Task<(AppleAlbumDetailsResult Result, bool IsCached)> LookupAsync(string id, string storefront, string client, CancellationToken ct)
    {
        string key = $"album-details|{storefront}|{id}";
        if (cache.TryGetValue(key, out AppleAlbumDetailsResult cached))
        {
            return (cached, true);
        }

        SemaphoreSlim semaphore = locker.GetLock(key);
        await semaphore.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached))
            {
                return (cached, true);
            }

            if (!lookupLimiter.TryAcquire(client))
            {
                Log.Information("Uncached Apple lookup limit reached for client {Client}", client);
                return (new AppleAlbumDetailsResult(StaticArtworkLookupStatus.RateLimited), false);
            }

            AppleAlbumDetailsResult result = await appleMusicClient.LookupAlbumDetailsAsync(id, storefront, ct);
            switch (result.Status)
            {
                case StaticArtworkLookupStatus.Found:
                    cache.Set(key, result, FoundTtl);
                    break;
                case StaticArtworkLookupStatus.NoMatch:
                    cache.Set(key, result, NoMatchTtl);
                    break;
            }
            return (result, false);
        }
        finally
        {
            semaphore.Release();
            locker.RemoveLock(key);
        }
    }
}
