using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AnimatedArtworks.Infrastructure;
public partial class AppleMusicClient(HttpClient httpClient, SystemStatusService statusService) : IAppleMusicClient
{
    private static readonly SemaphoreSlim _appleMusicSemaphore = new(2, 2);

    [GeneratedRegex(@"(/assets/[^""]+\.js)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private partial Regex JsAssetRegex();

    [GeneratedRegex(@"[""'](eyJ[A-Za-z0-9-_=]+\.[A-Za-z0-9-_=]+\.[A-Za-z0-9-_=]+)[""']", RegexOptions.NonBacktracking)]
    private partial Regex JwtRegex();
    
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex NonAlphanumericRegex();

    public async Task<AppleMusicWebSearchResult> GetAppleMusicUrlViaWebSearchAsync(string artist, string album, string? title = null, CancellationToken ct = default)
    {
        // The catalog search lists albums with their artist; the caller still verifies the artist per candidate.
        string query = Uri.EscapeDataString($"{artist} {album}");
        string searchUrl = $"https://amp-api.music.apple.com/v1/catalog/us/search?term={query}&types=albums&limit=25&platform=web";

        var (status, json) = await GetCatalogJsonAsync(searchUrl, ct);
        switch (status)
        {
            case StaticArtworkLookupStatus.RateLimited:
                return new(AppleMusicWebSearchStatus.RateLimited);
            case StaticArtworkLookupStatus.NoMatch:
                return new(AppleMusicWebSearchStatus.NoMatch);
            case StaticArtworkLookupStatus.Error:
                return new(AppleMusicWebSearchStatus.Error);
        }

        // Closest edition first, not the first hit: an exact title beats "The Encore" or
        // "30th Anniversary" variants that merely contain the requested name.
        List<(string Url, int Rank)> candidates = [];
        foreach (JsonNode? item in json?["results"]?["albums"]?["data"]?.AsArray() ?? [])
        {
            string? name = item?["attributes"]?["name"]?.ToString();
            string? pageUrl = item?["attributes"]?["url"]?.ToString();
            if (name == null || !Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? pageUri))
            {
                continue;
            }

            string foundUrl = pageUri.GetLeftPart(UriPartial.Path);
            int rank = RankAlbumMatch(name, album);
            if (rank < 0 || !AppleMusicUrl.TryParse(foundUrl, out _) || candidates.Any(c => c.Url == foundUrl))
            {
                continue;
            }

            candidates.Add((foundUrl, rank));
        }

        if (candidates.Count == 0)
        {
            Log.Debug("Found no matching albums in the catalog for query: {Query}", query);
            return new(AppleMusicWebSearchStatus.NoMatch);
        }

        return new(AppleMusicWebSearchStatus.Found, candidates.OrderBy(c => c.Rank).Select(c => c.Url).Take(5).ToList());
    }

    /// <summary>
    /// -1 when the found album does not contain the requested name as whole words, otherwise the
    /// number of extra words (0 = exact title). Lower is closer.
    /// </summary>
    private static int RankAlbumMatch(string foundAlbumName, string requestedAlbumName)
    {
        string found = NormalizeForComparison(foundAlbumName);
        string requested = NormalizeForComparison(requestedAlbumName);

        if (found.Length == 0 || requested.Length == 0)
        {
            return -1;
        }

        if (found == requested)
        {
            return 0;
        }

        if (!$" {found} ".Contains($" {requested} ", StringComparison.Ordinal))
        {
            return -1;
        }

        return found.Split(' ').Length - requested.Split(' ').Length;
    }

    private static string NormalizeForComparison(string input)
    {
        // Apostrophes vanish instead of splitting words, so "Taylor's" matches Apple's slug "taylors".
        string normalized = input.Trim().ToLowerInvariant()
            .Replace("'", "").Replace("\u2019", "").Replace("\u2018", "").Replace("\u02BC", "");
        normalized = NonAlphanumericRegex().Replace(normalized, " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static readonly TimeSpan CatalogTokenLifetime = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim _catalogTokenLock = new(1, 1);
    private static string? _catalogToken;
    private static DateTime _catalogTokenFetchedAt = DateTime.MinValue;

    private async Task<string?> GetCatalogTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _catalogToken != null && DateTime.UtcNow - _catalogTokenFetchedAt < CatalogTokenLifetime)
        {
            return _catalogToken;
        }

        await _catalogTokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _catalogToken != null && DateTime.UtcNow - _catalogTokenFetchedAt < CatalogTokenLifetime)
            {
                return _catalogToken;
            }

            string? token = await GetBearerTokenAsync("https://music.apple.com/us/browse", ct);
            if (token != null)
            {
                _catalogToken = token;
                _catalogTokenFetchedAt = DateTime.UtcNow;
            }

            return token;
        }
        finally
        {
            _catalogTokenLock.Release();
        }
    }

    // Found carries the parsed body; a 404 is a confirmed NoMatch. An expired token is refreshed once.
    private async Task<(StaticArtworkLookupStatus Status, JsonNode? Json)> GetCatalogJsonAsync(string apiUrl, CancellationToken ct)
    {
        if (statusService.IsInBackoff())
        {
            Log.Warning("Global rate-limit backoff active. Skipping catalog request for {Url}", apiUrl);
            return (StaticArtworkLookupStatus.RateLimited, null);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            string? token = await GetCatalogTokenAsync(forceRefresh: attempt > 0, ct);
            if (token == null)
            {
                return (StaticArtworkLookupStatus.Error, null);
            }

            await _appleMusicSemaphore.WaitAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.Add("Authorization", $"Bearer {token}");
                request.Headers.Add("Origin", "https://music.apple.com");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                Log.Information("Outgoing catalog request: {Url}", apiUrl);
                using HttpResponseMessage response = await httpClient.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    Log.Warning("Rate Limit hit while calling AMP API: {StatusCode}", response.StatusCode);
                    statusService.ReportRateLimit();
                    return (StaticArtworkLookupStatus.RateLimited, null);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (StaticArtworkLookupStatus.NoMatch, null);
                }

                response.EnsureSuccessStatusCode();
                statusService.ReportSuccess();
                return (StaticArtworkLookupStatus.Found, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)));
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                Log.Error("Catalog request failed: {Message}", ex.Message);
                return (StaticArtworkLookupStatus.Error, null);
            }
            finally
            {
                _appleMusicSemaphore.Release();
            }
        }

        return (StaticArtworkLookupStatus.Error, null);
    }

    private static StaticArtwork? ParseCatalogAlbum(JsonNode? item)
    {
        JsonNode? attrs = item?["attributes"];
        string? artistName = attrs?["artistName"]?.ToString();
        string? albumName = attrs?["name"]?.ToString();
        string? template = attrs?["artwork"]?["url"]?.ToString();
        string? pageUrl = attrs?["url"]?.ToString();

        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumName) ||
            !TryBuildTemplateArtworkUrl(template, 1000, out string fullUrl) ||
            !TryBuildTemplateArtworkUrl(template, 600, out string thumbnailUrl) ||
            !Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? pageUri) ||
            !AppleMusicUrl.TryParse(pageUri.GetLeftPart(UriPartial.Path), out _))
        {
            return null;
        }

        return new StaticArtwork(artistName, albumName, fullUrl, thumbnailUrl, pageUri.GetLeftPart(UriPartial.Path),
            attrs?["releaseDate"]?.ToString());
    }

    private static StaticArtworkLookupResult CatalogAlbumsResult(JsonNode? data)
    {
        List<StaticArtwork> artworks = [];
        foreach (JsonNode? item in data?.AsArray() ?? [])
        {
            if (ParseCatalogAlbum(item) is { } artwork && artworks.All(a => a.Url != artwork.Url))
            {
                artworks.Add(artwork);
            }
        }

        return artworks.Count > 0
            ? new(StaticArtworkLookupStatus.Found, artworks)
            : new(StaticArtworkLookupStatus.NoMatch);
    }

    private const int MaxStaticArtworks = 10;

    public async Task<StaticArtworkLookupResult> SearchStaticArtworkAsync(string artist, string album, CancellationToken ct)
    {
        string query = Uri.EscapeDataString($"{artist} {album}");
        string catalogUrl = $"https://amp-api.music.apple.com/v1/catalog/us/search?term={query}&types=albums&limit=25&platform=web";

        var (status, json) = await GetCatalogJsonAsync(catalogUrl, ct);
        StaticArtworkLookupResult result = status switch
        {
            StaticArtworkLookupStatus.Found => CatalogAlbumsResult(json?["results"]?["albums"]?["data"]),
            _ => new(status)
        };

        if (result.Status != StaticArtworkLookupStatus.Found)
        {
            return result;
        }

        // Search matches loosely, so keep only the requested artist and albums containing the requested title.
        List<StaticArtwork> matches = result.Artworks!
            .Select(artwork => (Artwork: artwork, Rank: RankAlbumMatch(artwork.Album, album)))
            .Where(match => match.Rank >= 0 && CacheKeyNormalizer.ArtistsMatch(artist, match.Artwork.Artist))
            .OrderBy(match => match.Rank)
            .Select(match => match.Artwork)
            .Take(MaxStaticArtworks)
            .ToList();

        return matches.Count > 0
            ? new(StaticArtworkLookupStatus.Found, matches)
            : new(StaticArtworkLookupStatus.NoMatch);
    }

    public async Task<StaticArtworkLookupResult> LookupStaticArtworkAsync(AppleMusicCatalogTarget target, CancellationToken ct)
    {
        string catalogUrl = $"https://amp-api.music.apple.com/v1/catalog/{target.Storefront}/albums/{Uri.EscapeDataString(target.Id)}?platform=web";
        var (status, json) = await GetCatalogJsonAsync(catalogUrl, ct);
        return status switch
        {
            StaticArtworkLookupStatus.Found => CatalogAlbumsResult(json?["data"]),
            _ => new(status)
        };
    }

    public async Task<AppleAlbumDetailsResult> LookupAlbumDetailsAsync(string id, string storefront, CancellationToken ct)
    {
        string catalogUrl = $"https://amp-api.music.apple.com/v1/catalog/{storefront}/albums/{Uri.EscapeDataString(id)}?include=tracks&platform=web";
        var (status, json) = await GetCatalogJsonAsync(catalogUrl, ct);
        return status switch
        {
            StaticArtworkLookupStatus.Found => ParseCatalogAlbumDetails(json?["data"]?[0]),
            _ => new(status)
        };
    }

    private static AppleAlbumDetailsResult ParseCatalogAlbumDetails(JsonNode? album)
    {
        JsonNode? attrs = album?["attributes"];
        string? title = attrs?["name"]?.ToString();
        string? artist = attrs?["artistName"]?.ToString();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) ||
            !TryBuildTemplateArtworkUrl(attrs?["artwork"]?["url"]?.ToString(), 1400, out string artworkUrl))
        {
            return new(StaticArtworkLookupStatus.NoMatch);
        }

        List<AppleAlbumTrack> tracks = [];
        foreach (JsonNode? item in album?["relationships"]?["tracks"]?["data"]?.AsArray() ?? [])
        {
            JsonNode? track = item?["attributes"];
            string? trackTitle = track?["name"]?.ToString();
            string? trackArtist = track?["artistName"]?.ToString();
            if (item?["type"]?.ToString() != "songs" || string.IsNullOrWhiteSpace(trackTitle) || string.IsNullOrWhiteSpace(trackArtist))
            {
                continue;
            }

            tracks.Add(new AppleAlbumTrack(
                trackTitle,
                trackArtist,
                (int?)track?["trackNumber"],
                (int?)track?["discNumber"],
                (int?)track?["durationInMillis"]));
        }

        tracks.Sort((a, b) => (a.DiscNumber ?? 1, a.TrackNumber ?? 0).CompareTo((b.DiscNumber ?? 1, b.TrackNumber ?? 0)));

        return new(StaticArtworkLookupStatus.Found, new AppleAlbumDetails(
            title,
            artist,
            attrs?["releaseDate"]?.ToString(),
            attrs?["genreNames"]?[0]?.ToString(),
            attrs?["copyright"]?.ToString(),
            artworkUrl,
            tracks));
    }

    private const int MaxArtistCandidates = 3;

    public async Task<StaticArtworkLookupResult> SearchArtistArtworkAsync(string artist, CancellationToken ct)
    {
        string searchUrl = $"https://amp-api.music.apple.com/v1/catalog/us/search?term={Uri.EscapeDataString(artist)}&types=artists&limit=25&platform=web";
        var (searchStatus, searchJson) = await GetCatalogJsonAsync(searchUrl, ct);
        if (searchStatus != StaticArtworkLookupStatus.Found)
        {
            return new(searchStatus);
        }

        List<(string Id, bool Exact)> candidates = [];
        foreach (JsonNode? item in searchJson?["results"]?["artists"]?["data"]?.AsArray() ?? [])
        {
            string? name = item?["attributes"]?["name"]?.ToString();
            string? id = item?["id"]?.ToString();
            if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit) || !CacheKeyNormalizer.ArtistsMatch(artist, name))
            {
                continue;
            }

            candidates.Add((id, CacheKeyNormalizer.Normalize(name) == CacheKeyNormalizer.Normalize(artist)));
        }

        // "Gorillaz" also matches "Gorillaz Day"; an exact name wins when there is one.
        bool hasExact = candidates.Any(c => c.Exact);
        List<string> artistIds = candidates.Where(c => c.Exact || !hasExact).Select(c => c.Id).Distinct().Take(MaxArtistCandidates).ToList();
        if (artistIds.Count == 0)
        {
            return new(StaticArtworkLookupStatus.NoMatch);
        }

        string apiUrl = $"https://amp-api.music.apple.com/v1/catalog/us/artists?ids={string.Join(',', artistIds)}&platform=web&extend=editorialArtwork,editorialVideo";
        var (status, json) = await GetCatalogJsonAsync(apiUrl, ct);
        if (status != StaticArtworkLookupStatus.Found)
        {
            return new(status);
        }

        List<StaticArtwork> artworks = [];
        foreach (JsonNode? item in json?["data"]?.AsArray() ?? [])
        {
            JsonNode? attrs = item?["attributes"];
            string? name = attrs?["name"]?.ToString();
            string? template = attrs?["artwork"]?["url"]?.ToString();
            string? pageUrl = attrs?["url"]?.ToString();

            // Artists without a photo get one of their album covers, served from a "Music…" path.
            if (string.IsNullOrWhiteSpace(name) ||
                !TryBuildTemplateArtworkUrl(template, 1000, out string fullUrl) ||
                new Uri(fullUrl).AbsolutePath.StartsWith("/image/thumb/Music", StringComparison.OrdinalIgnoreCase) ||
                !TryBuildTemplateArtworkUrl(template, 600, out string thumbnailUrl) ||
                !Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? pageUri) ||
                !string.Equals(pageUri.IdnHost, AppleMusicUrl.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            artworks.Add(new StaticArtwork(name, string.Empty, fullUrl, thumbnailUrl, pageUri.GetLeftPart(UriPartial.Path), null,
                BuildLogoUrl(attrs?["editorialArtwork"]?["musicContentColorLogoTrimmed"]),
                BuildMotionVideoUrl(attrs?["editorialVideo"]?["motionArtistSquare1x1"]?["video"])));
        }

        return artworks.Count > 0
            ? new(StaticArtworkLookupStatus.Found, artworks)
            : new(StaticArtworkLookupStatus.NoMatch);
    }

    // The logo is a white wordmark; HEIC keeps its transparent background at the smallest size.
    private static string? BuildLogoUrl(JsonNode? logo)
    {
        string? template = logo?["url"]?.ToString();
        if (logo?["width"]?.GetValue<int>() is not int width || logo["height"]?.GetValue<int>() is not int height ||
            width <= 0 || height <= 0 || string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        int targetWidth = Math.Min(width, 1000);
        int targetHeight = Math.Max(1, (int)Math.Round(targetWidth * (double)height / width));
        string filled = template.Replace("{w}", targetWidth.ToString()).Replace("{h}", targetHeight.ToString())
            .Replace("{c}", "bb").Replace("{f}", "heic");
        if (filled.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            filled = filled[..^4] + ".heic";
        }

        return Uri.TryCreate(filled, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.IdnHost.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase) &&
               uri.Query.Length == 0 && !filled.Contains('{')
            ? uri.GetLeftPart(UriPartial.Path)
            : null;
    }

    // Motion video playlists are served from Apple's video CDN (e.g. mvod.itunes.apple.com), not mzstatic.
    private static string? BuildMotionVideoUrl(JsonNode? video) =>
        Uri.TryCreate(video?.ToString(), UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        (uri.IdnHost.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase)) &&
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;

    // Catalog artwork URLs are templates such as ".../{w}x{h}bb.{f}" or ".../{w}x{h}{c}.{f}".
    private static bool TryBuildTemplateArtworkUrl(string? template, int size, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        string filled = template
            .Replace("{w}", size.ToString())
            .Replace("{h}", size.ToString())
            .Replace("{c}", "bb")
            .Replace("{f}", "jpg");

        if (!Uri.TryCreate(filled, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IdnHost.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length > 0 ||
            filled.Contains('{'))
        {
            return false;
        }

        url = uri.GetLeftPart(UriPartial.Path);
        return true;
    }

    private async Task<string?> GetBearerTokenAsync(string albumUrl, CancellationToken ct)
    {
        await _appleMusicSemaphore.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, albumUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            var jsPaths = JsAssetRegex().Matches(html)
                .Select(m => m.Groups[1].Value)
                .Where(p => p.Contains("index") || p.Contains("web-client") || p.Contains("apple-music"))
                .Distinct();

            foreach (var jsPath in jsPaths)
            {
                var jsUrl = $"https://music.apple.com{jsPath}";
                using var jsResponse = await httpClient.GetAsync(jsUrl, ct);
                if (!jsResponse.IsSuccessStatusCode) continue;

                var jsText = await jsResponse.Content.ReadAsStringAsync(ct);
                var tokenMatch = JwtRegex().Match(jsText);
                if (tokenMatch.Success)
                {
                    return tokenMatch.Groups[1].Value;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Token extraction failed: {Message}", ex.Message);
            return null;
        }
        finally
        {
            _appleMusicSemaphore.Release();
        }
    }

    public async Task<AppleMusicPageParseResult> ParseAppleMusicPageAsync(AppleMusicCatalogTarget target, CancellationToken ct)
    {
        string apiUrl = $"https://amp-api.music.apple.com/v1/catalog/{target.Storefront}/{target.ResourceType}/{Uri.EscapeDataString(target.Id)}?extend=editorialVideo&platform=web";

        var (status, json) = await GetCatalogJsonAsync(apiUrl, ct);
        if (status == StaticArtworkLookupStatus.RateLimited)
        {
            return new(AppleMusicPageParseStatus.RateLimited);
        }

        if (status != StaticArtworkLookupStatus.Found)
        {
            return new(AppleMusicPageParseStatus.Error);
        }

        JsonNode? data = json?["data"]?[0];
        string? returnedId = data?["id"]?.ToString();
        string? returnedType = data?["type"]?.ToString();
        if (!string.Equals(returnedId, target.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(returnedType, target.ResourceType, StringComparison.Ordinal))
        {
            Log.Warning("Catalog response did not match the requested {ResourceType} {Id} (got {ReturnedType} {ReturnedId})",
                target.ResourceType, target.Id, returnedType, returnedId);
            return new(AppleMusicPageParseStatus.Error);
        }

        JsonNode? attrs = data?["attributes"];
        if (attrs == null)
        {
            return new(AppleMusicPageParseStatus.Error);
        }

        string artistName = attrs["artistName"]?.ToString()
                            ?? attrs["curatorName"]?.ToString()
                            ?? "Unknown Artist";
        string albumName = attrs["name"]?.ToString() ?? "Unknown Album";
        JsonNode? videos = attrs["editorialVideo"];

        return new AppleMusicPageParseResult(
            AppleMusicPageParseStatus.Success,
            videos?["motionDetailSquare"]?["video"]?.ToString(),
            videos?["motionDetailTall"]?["video"]?.ToString(),
            artistName,
            albumName);
    }
}