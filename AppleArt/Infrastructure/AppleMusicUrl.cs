using System;
using System.Text.RegularExpressions;

namespace AnimatedArtworks.Infrastructure;

public readonly record struct AppleMusicCatalogTarget(string Storefront, string ResourceType, string Id)
{
    private string PathSegment => ResourceType == "albums" ? "album" : "playlist";

    public string CacheKey => $"https://music.apple.com/{PathSegment}/{Id}";

    public string PageUrl => $"https://music.apple.com/{Storefront}/{PathSegment}/{Id}";
}

public static partial class AppleMusicUrl
{
    public const string Host = "music.apple.com";

    [GeneratedRegex(@"^/(?:([a-z]{2})/)?album/(?:[^/]+/)?(\d+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumPathRegex();

    [GeneratedRegex(@"^/(?:([a-z]{2})/)?playlist/(?:[^/]+/)?(pl\.[a-z0-9]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistPathRegex();

    public static bool TryParse(string? url, out AppleMusicCatalogTarget target)
    {
        target = default;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.Equals(uri.IdnHost, Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0)
        {
            return false;
        }

        Match album = AlbumPathRegex().Match(uri.AbsolutePath);
        if (album.Success)
        {
            target = new AppleMusicCatalogTarget(Storefront(album), "albums", album.Groups[2].Value);
            return true;
        }

        Match playlist = PlaylistPathRegex().Match(uri.AbsolutePath);
        if (playlist.Success)
        {
            target = new AppleMusicCatalogTarget(Storefront(playlist), "playlists", playlist.Groups[2].Value.ToLowerInvariant());
            return true;
        }

        return false;
    }

    private static string Storefront(Match match) =>
        match.Groups[1].Success ? match.Groups[1].Value.ToLowerInvariant() : "us";
}
