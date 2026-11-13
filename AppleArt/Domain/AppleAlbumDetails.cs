using System.Collections.Generic;

public record AppleAlbumDetails(
    string Title,
    string Artist,
    string? ReleaseDate,
    string? Genre,
    string? Copyright,
    string ArtworkUrl,
    IReadOnlyList<AppleAlbumTrack> Tracks
);
