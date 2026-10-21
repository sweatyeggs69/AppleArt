using System.Collections.Generic;

public enum AppleMusicWebSearchStatus
{
    Found,
    NoMatch,
    RateLimited,
    Error
}

public readonly record struct AppleMusicWebSearchResult(
    AppleMusicWebSearchStatus Status,
    // Candidate album URLs, closest album-title match first.
    IReadOnlyList<string>? Urls = null
);
