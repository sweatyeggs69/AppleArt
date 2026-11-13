using System.Collections.Generic;

public enum StaticArtworkLookupStatus
{
    Found,
    NoMatch,
    RateLimited,
    Error
}

public readonly record struct StaticArtworkLookupResult(
    StaticArtworkLookupStatus Status,
    // Closest album-title match first.
    IReadOnlyList<StaticArtwork>? Artworks = null
);
