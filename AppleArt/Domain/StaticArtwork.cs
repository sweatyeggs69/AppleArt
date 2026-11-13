public record StaticArtwork(
    string Artist,
    string Album,
    string Url,
    string ThumbnailUrl,
    string AppleMusicUrl,
    string? ReleaseDate,
    string? LogoUrl = null,
    string? VideoUrl = null
);
