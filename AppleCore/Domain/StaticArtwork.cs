public record StaticArtwork(
    string Artist,
    string Album,
    string Url,
    string ThumbnailUrl,
    string AppleMusicUrl,
    string? ReleaseDate,
    string? LogoUrl = null,
    string? ReleaseType = null,
    bool? IsExplicit = null,
    string? Description = null,
    string? Genre = null
);
