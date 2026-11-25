using System;
using System.Threading;
using System.Threading.Tasks;
using AnimatedArtworks.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnimatedArtworks.Application;

public partial class ArtworkService(
    IAppleMusicClient appleMusicClient,
    ArtworkCache cache,
    MetadataResolutionCache metadataResolutionCache,
    KeyedLocker locker,
    AppleLookupLimiter lookupLimiter,
    IMemoryCache notFoundCache)
{
    public bool WasThrottled => lookupLimiter.CurrentRequestThrottled;

    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan NoArtworkRecheckInterval = TimeSpan.FromDays(90);

    private static string NotFoundKey(string normalizedUrl) => $"artwork-not-found|{normalizedUrl}";

    private static readonly TimeSpan NegativeSearchCacheTtl = TimeSpan.FromDays(30);

    // Aliases saved before artist and exact-edition checks may point at another edition (e.g. the
    // 20th Anniversary release for the plain album); those are looked up again once.
    private static readonly DateTime EditionCheckCutoff = new(2026, 10, 21, 16, 0, 0, DateTimeKind.Utc);

    private static bool NeedsRefresh(ArtworkCacheEntry entry)
    {
        bool hasSquareArtwork = !string.IsNullOrWhiteSpace(entry.M3u8Url) && entry.M3u8Url != "NONE";
        bool missingTallArtwork = string.IsNullOrWhiteSpace(entry.M3u8UrlTall);
        bool updatedBeforeTallImplement = entry.LastFetched.Date < DateTimeOffset.FromUnixTimeSeconds(1777391211).UtcDateTime;

        bool hasNoArtwork = !CacheKeyNormalizer.HasAnimatedArtwork(entry.M3u8Url, entry.M3u8UrlTall);
        bool noArtworkIsOld = DateTime.UtcNow - entry.LastFetched >= NoArtworkRecheckInterval;

        return (hasSquareArtwork && missingTallArtwork && updatedBeforeTallImplement) || (hasNoArtwork && noArtworkIsOld);
    }

    public async Task<(ArtworkCacheEntry? Entry, bool IsCached)> GetArtworkByUrlAsync(string appleMusicUrl,
        CancellationToken ct = default)
    {
        var (entry, isCached, _) = await LookupUrlAsync(appleMusicUrl, ct);
        return (entry, isCached);
    }

    private async Task<(ArtworkCacheEntry? Entry, bool IsCached, bool NotFound)> LookupUrlAsync(string appleMusicUrl,
        CancellationToken ct)
    {
        if (!AppleMusicUrl.TryParse(appleMusicUrl, out AppleMusicCatalogTarget target))
        {
            Log.Information("Rejected non-catalog Apple Music URL: {AppleMusicUrl}", appleMusicUrl);
            return (null, false, false);
        }

        string normalizedUrl = target.CacheKey;

        ArtworkCacheEntry? cachedEntry = cache.GetByUrl(normalizedUrl);
        if (cachedEntry != null && !NeedsRefresh(cachedEntry))
        {
            Log.Debug("URL cache hit: {AppleMusicUrl}", normalizedUrl);
            return (cachedEntry, true, false);
        }

        if (cachedEntry == null && notFoundCache.TryGetValue(NotFoundKey(normalizedUrl), out _))
        {
            return (null, false, true);
        }

        Log.Debug(
            cachedEntry != null
                ? "Cache entry is missing tall artwork: {AppleMusicUrl}"
                : "URL cache miss: {AppleMusicUrl}", normalizedUrl);

        IDisposable keyLock = await locker.LockAsync(normalizedUrl, ct);

        try
        {
            cachedEntry = cache.GetByUrl(normalizedUrl);
            if (cachedEntry != null && !NeedsRefresh(cachedEntry))
            {
                Log.Debug("URL cache hit after lock: {AppleMusicUrl}", normalizedUrl);
                return (cachedEntry, true, false);
            }

            AppleMusicPageParseResult parseResult = await appleMusicClient.ParseAppleMusicPageAsync(target, ct);

            if (parseResult.Status == AppleMusicPageParseStatus.RateLimited)
            {
                Log.Information("Album page request was rate limited for URL: {AppleMusicUrl}.", normalizedUrl);

                // Return cached entry if available, even if it may be stale.
                return (cachedEntry, cachedEntry != null, false);
            }

            if (parseResult.Status == AppleMusicPageParseStatus.NotFound)
            {
                notFoundCache.Set(NotFoundKey(normalizedUrl), true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = NotFoundTtl, Size = 1 });
                return (cachedEntry, cachedEntry != null, cachedEntry == null);
            }

            if (parseResult.Status == AppleMusicPageParseStatus.Error)
            {
                Log.Information("Album page request failed for URL: {AppleMusicUrl}.", normalizedUrl);
                return (null, false, false);
            }

            string? m3U8Url = parseResult.UrlSquare;
            string? m3U8UrlTall = parseResult.UrlTall;
            string artist = parseResult.Artist;
            string album = parseResult.Album;
            int downloadCount = cachedEntry?.DownloadCount ?? 0;
            int searchCount = cachedEntry?.SearchCount ?? 0;

            ArtworkCacheEntry newEntry = new(
                AppleMusicUrl: normalizedUrl,
                Artist: artist,
                Album: album,
                M3u8Url: m3U8Url ?? "NONE",
                M3u8UrlTall: m3U8UrlTall ?? "NONE",
                LastFetched: DateTime.UtcNow,
                DownloadCount: downloadCount,
                SearchCount: searchCount
            );

            cache.SaveEntry(newEntry);
            Log.Information("Added artwork entry: {AppleMusicUrl}, Artist: {Artist}, Album: {Album}, HasAnimatedArtwork: {HasAnimatedArtwork}",
                normalizedUrl,
                artist,
                album,
                m3U8Url != null || m3U8UrlTall != null);

            return (newEntry, false, false);
        }
        finally
        {
            keyLock.Dispose();
        }
    }

    public async Task<(ArtworkCacheEntry? Entry, bool IsCached)> GetArtworkByDetailsAsync(string artist, string album, string? title = null,
        CancellationToken ct = default)
    {
        MetadataResolutionLookup lookup = metadataResolutionCache.GetLookup(artist, album, NegativeSearchCacheTtl);

        if (lookup.Status == MetadataResolutionStatus.NoMatch)
        {
            Log.Debug("Metadata resolution no-match cache hit: Artist={Artist}, Album={Album}.", artist, album);
            return (null, false);
        }

        if (lookup.Status == MetadataResolutionStatus.Resolved && !string.IsNullOrWhiteSpace(lookup.ResolvedAppleMusicUrl))
        {
            Log.Debug("Metadata resolution cache hit: Artist={Artist}, Album={Album} -> {AppleMusicUrl}", artist, album, lookup.ResolvedAppleMusicUrl);

            (ArtworkCacheEntry? resolvedEntry, bool resolvedIsCached) = await GetArtworkByUrlAsync(lookup.ResolvedAppleMusicUrl, ct);
            if (WasThrottled)
            {
                return (resolvedEntry, resolvedIsCached);
            }
            bool outdatedEdition = lookup.LastResolved < EditionCheckCutoff
                                   && resolvedEntry != null
                                   && CacheKeyNormalizer.Normalize(resolvedEntry.Album) != CacheKeyNormalizer.Normalize(album);
            if (resolvedEntry != null && !outdatedEdition && CacheKeyNormalizer.ArtistsMatch(artist, resolvedEntry.Artist))
            {
                return (resolvedEntry, resolvedIsCached);
            }

            // Only the query that saved the alias may drop it; a variant spelling must not erase it for everyone.
            if (lookup.SavedForSameQuery)
            {
                Log.Information("Metadata resolution cache entry is stale, has the wrong artist or is an old edition match. Removing alias for Artist={Artist}, Album={Album}", artist, album);
                metadataResolutionCache.RemoveResolvedUrl(artist, album);
            }
        }

        ArtworkCacheEntry? cachedEntry = cache.GetByArtistAndAlbum(artist, album);
        if (cachedEntry != null && !CacheKeyNormalizer.ArtistsMatch(artist, cachedEntry.Artist))
        {
            cachedEntry = null;
        }
        if (cachedEntry != null)
        {
            if (!NeedsRefresh(cachedEntry))
            {
                Log.Debug("Cache hit (by metadata): Artist={Artist}, Album={Album}.", artist, album);

                // Remember the match, so the next request for this pair is a key lookup instead of a fuzzy scan.
                metadataResolutionCache.SaveResolvedUrl(artist, album, cachedEntry.AppleMusicUrl);
                return (cachedEntry, true);
            }

            Log.Information("Cache entry is missing tall artwork (by metadata): Artist={Artist}, Album={Album}.", artist, album);
        }
        else
        {
            Log.Debug("Cache miss (by metadata): Artist={Artist}, Album={Album}", artist, album);
        }

        Log.Debug("Triggering web search: Artist={Artist}, Album={Album}, Title={Title}", artist, album, title ?? "");

        AppleMusicWebSearchResult webSearchResult = await appleMusicClient.GetAppleMusicUrlViaWebSearchAsync(artist, album, title, ct);

        if (webSearchResult.Status == AppleMusicWebSearchStatus.NoMatch)
        {
            metadataResolutionCache.SaveNoMatch(artist, album);
            Log.Information("Search returned no match for Artist={Artist}, Album={Album}", artist, album);
            return (null, false);
        }

        if (webSearchResult.Status == AppleMusicWebSearchStatus.RateLimited)
        {
            Log.Information("Search was rate limited for Artist={Artist}, Album={Album}", artist, album);
            // Return cached entry if available, even if it may be stale.
            return (cachedEntry, cachedEntry != null);
        }

        if (webSearchResult.Status == AppleMusicWebSearchStatus.Error)
        {
            Log.Information("Search failed unexpectedly for Artist={Artist}, Album={Album}", artist, album);
            return (null, false);
        }

        if (webSearchResult.Urls is not { Count: > 0 })
        {
            Log.Warning("Search returned status Found without URL for Artist={Artist}, Album={Album}", artist, album);
            return (null, false);
        }

        // The search page lists albums by title only, so take the first candidate by the requested artist.
        bool lookupFailed = false;
        foreach (string candidateUrl in webSearchResult.Urls ?? [])
        {
            (ArtworkCacheEntry? candidateEntry, bool candidateIsCached, bool candidateNotFound) = await LookupUrlAsync(candidateUrl, ct);
            if (candidateEntry == null)
            {
                lookupFailed |= !candidateNotFound;
                continue;
            }

            if (!CacheKeyNormalizer.ArtistsMatch(artist, candidateEntry.Artist))
            {
                Log.Information("Rejected candidate with wrong artist. Requested: {Artist}, Found: {FoundArtist}, Url: {AppleMusicUrl}",
                    artist, candidateEntry.Artist, candidateUrl);
                continue;
            }

            Log.Debug("Search resolved URL: {AppleMusicUrl}", candidateUrl);
            metadataResolutionCache.SaveResolvedUrl(artist, album, candidateUrl);
            return (candidateEntry, candidateIsCached);
        }

        if (!lookupFailed)
        {
            metadataResolutionCache.SaveNoMatch(artist, album);
        }

        Log.Information("No candidate matched the artist for Artist={Artist}, Album={Album}", artist, album);
        return (null, false);
    }
}