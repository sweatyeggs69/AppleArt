using System;
using System.Collections.Generic;
using System.Linq;
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

    // Cached albums are answered without Apple; the rest share one catalog request and one lookup permit.
    public async Task<(StaticArtworkLookupStatus Status, IReadOnlyDictionary<string, AppleAlbumDetailsResult> Albums)> LookupBatchAsync(
        IReadOnlyList<string> ids, string storefront, string client, CancellationToken ct)
    {
        Dictionary<string, AppleAlbumDetailsResult> albums = [];
        List<string> missing = [];
        foreach (string id in ids.Distinct())
        {
            if (cache.TryGetValue(Key(id, storefront), out AppleAlbumDetailsResult cached))
            {
                albums[id] = cached;
            }
            else
            {
                missing.Add(id);
            }
        }
        if (missing.Count == 0)
        {
            return (StaticArtworkLookupStatus.Found, albums);
        }

        if (!lookupLimiter.TryAcquire(client))
        {
            Log.Information("Uncached Apple lookup limit reached for client {Client}", client);
            return (StaticArtworkLookupStatus.RateLimited, albums);
        }

        var (status, fetched) = await appleMusicClient.LookupAlbumDetailsBatchAsync(missing, storefront, ct);
        if (status != StaticArtworkLookupStatus.Found)
        {
            return (status, albums);
        }

        foreach (var (id, result) in fetched)
        {
            switch (result.Status)
            {
                case StaticArtworkLookupStatus.Found:
                    cache.Set(Key(id, storefront), result, FoundTtl);
                    break;
                case StaticArtworkLookupStatus.NoMatch:
                    cache.Set(Key(id, storefront), result, NoMatchTtl);
                    break;
            }
            albums[id] = result;
        }
        return (StaticArtworkLookupStatus.Found, albums);
    }

    private static string Key(string id, string storefront) => $"album-details|{storefront}|{id}";

    public async Task<(AppleAlbumDetailsResult Result, bool IsCached)> LookupAsync(string id, string storefront, string client, CancellationToken ct)
    {
        string key = Key(id, storefront);
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
