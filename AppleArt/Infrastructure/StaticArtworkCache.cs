using System;
using Microsoft.Extensions.Caching.Memory;

namespace AnimatedArtworks.Infrastructure;

// Keyed by the exact (trimmed, case-folded) query, so a no-match only answers that same query.
public sealed class StaticArtworkCache : IDisposable
{
    private static readonly TimeSpan FoundTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan NoMatchTtl = TimeSpan.FromDays(1);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public static string SearchKey(string artist, string album) =>
        $"search|{artist.Trim().ToLowerInvariant()}|{album.Trim().ToLowerInvariant()}";

    public static string LookupKey(AppleMusicCatalogTarget target) => $"lookup|{target.Storefront}|{target.Id}";

    public bool TryGet(string key, out StaticArtworkLookupResult result) => _cache.TryGetValue(key, out result);

    public void Save(string key, StaticArtworkLookupResult result)
    {
        if (result.Status is not (StaticArtworkLookupStatus.Found or StaticArtworkLookupStatus.NoMatch))
        {
            return;
        }

        _cache.Set(key, result, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = result.Status == StaticArtworkLookupStatus.Found ? FoundTtl : NoMatchTtl
        });
    }

    public void Dispose() => _cache.Dispose();
}
