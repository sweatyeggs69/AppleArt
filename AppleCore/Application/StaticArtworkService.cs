using System;
using System.Threading;
using System.Threading.Tasks;
using AppleCore.Infrastructure;
using Serilog;

namespace AppleCore.Application;

public class StaticArtworkService(
    IAppleMusicClient appleMusicClient,
    StaticArtworkCache cache,
    KeyedLocker locker)
{
    public Task<(StaticArtworkLookupResult Result, bool IsCached)> SearchAsync(string artist, string album, CancellationToken ct) =>
        GetAsync(StaticArtworkCache.SearchKey(artist, album), () => appleMusicClient.SearchStaticArtworkAsync(artist, album, ct), ct);

    public Task<(StaticArtworkLookupResult Result, bool IsCached)> LookupAsync(AppleMusicCatalogTarget target, CancellationToken ct) =>
        GetAsync(StaticArtworkCache.LookupKey(target), () => appleMusicClient.LookupStaticArtworkAsync(target, ct), ct);

    public Task<(StaticArtworkLookupResult Result, bool IsCached)> SearchArtistAsync(string artist, CancellationToken ct) =>
        GetAsync(StaticArtworkCache.ArtistKey(artist), () => appleMusicClient.SearchArtistArtworkAsync(artist, ct), ct);

    private async Task<(StaticArtworkLookupResult Result, bool IsCached)> GetAsync(
        string key, Func<Task<StaticArtworkLookupResult>> fetch, CancellationToken ct)
    {
        if (cache.TryGet(key, out StaticArtworkLookupResult cached, out bool isStale) && !isStale)
        {
            return (cached, true);
        }

        // The lock key is prefixed so it never collides with an animated artwork lookup for the same URL.
        string lockKey = "static|" + key;
        IDisposable keyLock = await locker.LockAsync(lockKey, ct);

        try
        {
            bool hasCached = cache.TryGet(key, out cached, out isStale);
            if (hasCached && !isStale)
            {
                return (cached, true);
            }

            StaticArtworkLookupResult result = await fetch();
            if (hasCached && result.Status is not (StaticArtworkLookupStatus.Found or StaticArtworkLookupStatus.NoMatch))
            {
                return (cached, true);
            }

            cache.Save(key, result);
            return (result, false);
        }
        finally
        {
            keyLock.Dispose();
        }
    }
}
