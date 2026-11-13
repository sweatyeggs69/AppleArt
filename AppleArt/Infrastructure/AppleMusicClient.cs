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
    
    [GeneratedRegex(@"href=""(https://music\.apple\.com/[a-z]{2}/album/([^/""?]+)/\d+)""", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private partial Regex AppleMusicLinkRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex NonAlphanumericRegex();

    private const string AppleMusicSearchUrl = "https://music.apple.com/us/search?term=";
    private const string ItunesSearchUrl = "https://itunes.apple.com/search";
    private const string ItunesLookupUrl = "https://itunes.apple.com/lookup";
    
    public async Task<AppleMusicWebSearchResult> GetAppleMusicUrlViaWebSearchAsync(string artist, string album, string? title = null, CancellationToken ct = default)
    {
        List<string> searchParts = [artist, album];
        if (!string.IsNullOrWhiteSpace(title)) searchParts.Add(title);

        string query = Uri.EscapeDataString(string.Join(" ", searchParts));
        string searchUrl = AppleMusicSearchUrl + query;

        if (statusService.IsInBackoff())
        {
            Log.Warning("Global rate-limit backoff active. Skipping search request for {SearchUrl}", searchUrl);
            return new(AppleMusicWebSearchStatus.RateLimited);
        }

        await _appleMusicSemaphore.WaitAsync(ct);
        try
        {
            if (statusService.IsInBackoff())
            {
                Log.Warning("Global rate-limit backoff active (after lock). Skipping search request for {SearchUrl}", searchUrl);
                return new(AppleMusicWebSearchStatus.RateLimited);
            }

            Log.Information("Outgoing search request: {SearchUrl}", searchUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);

            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using HttpResponseMessage response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                Log.Warning("Rate Limit hit on AM search: {StatusCode}", response.StatusCode);
                statusService.ReportRateLimit();
                return new(AppleMusicWebSearchStatus.RateLimited);
            }

            response.EnsureSuccessStatusCode();
            statusService.ReportSuccess();

            string htmlContent = await response.Content.ReadAsStringAsync(ct);

            MatchCollection matches = AppleMusicLinkRegex().Matches(htmlContent);

            // Closest edition first, not the first hit: an exact title beats "The Encore" or
            // "30th Anniversary" variants that merely contain the requested name. The search page
            // does not show artists, so the caller verifies the artist on each candidate.
            List<(string Url, int Rank)> candidates = [];

            foreach (Match match in matches)
            {
                string foundUrl = match.Groups[1].Value;
                string foundAlbumSlug = Uri.UnescapeDataString(match.Groups[2].Value).Replace('-', ' ');

                int rank = RankAlbumMatch(foundAlbumSlug, album);
                if (rank < 0)
                {
                    Log.Debug("Rejected non-matching album hit. Requested: {RequestedAlbum}, Found: {FoundAlbum}, Url: {Url}", album, foundAlbumSlug, foundUrl);
                    continue;
                }

                if (candidates.All(c => c.Url != foundUrl))
                {
                    candidates.Add((foundUrl, rank));
                }
            }

            if (candidates.Count > 0)
            {
                List<string> urls = candidates.OrderBy(c => c.Rank).Select(c => c.Url).Take(5).ToList();
                Log.Debug("Found {Count} candidate albums for query: {Query}", urls.Count, query);
                return new(AppleMusicWebSearchStatus.Found, urls);
            }

            Log.Debug("Found no matching album links in HTML for query: {Query}", query);
            return new(AppleMusicWebSearchStatus.NoMatch);
        }
        catch (Exception ex)
        {
            Log.Error("Apple Music Search Scrape failed: {Message}", ex.Message);
            return new(AppleMusicWebSearchStatus.Error);
        }
        finally
        {
            _appleMusicSemaphore.Release();
        }
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

    public async Task<string?> GetAppleMusicUrlViaItunesAsync(string artist, string album, string? title = null, CancellationToken ct = default)
    {
        List<string> searchParts = [artist, album];
        if (!string.IsNullOrWhiteSpace(title)) searchParts.Add(title);

        string query = Uri.EscapeDataString(string.Join(" ", searchParts));
        string entity = string.IsNullOrWhiteSpace(title) ? "album" : "song";
        string searchUrl = ItunesSearchUrl + $"?term={query}&entity={entity}&limit=5&explicit=Yes";

        await _appleMusicSemaphore.WaitAsync(ct);
        try
        {
            Log.Information("Outgoing request to iTunes search: {SearchUrl}", searchUrl);
            using HttpResponseMessage response = await httpClient.GetAsync(searchUrl, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                Log.Warning("Rate Limit hit on iTunes API: {StatusCode}", response.StatusCode);
                statusService.ReportRateLimit();
                return null;
            }

            response.EnsureSuccessStatusCode();
            statusService.ReportSuccess();

            string jsonString = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = JsonNode.Parse(jsonString);

            JsonArray? results = json?["results"]?.AsArray();
            if (results is { Count: > 0 })
            {
                string? collectionViewUrl = results[0]?["collectionViewUrl"]?.ToString();

                if (!string.IsNullOrEmpty(collectionViewUrl))
                {
                    Uri uri = new(collectionViewUrl);
                    return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
                }
            }

            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Error("iTunes API request failed: {Message}", ex.Message);
            return null;
        }
        finally
        {
            _appleMusicSemaphore.Release();
        }
    }

    private const int MaxStaticArtworks = 10;

    public async Task<StaticArtworkLookupResult> SearchStaticArtworkAsync(string artist, string album, CancellationToken ct)
    {
        string query = Uri.EscapeDataString($"{artist} {album}");
        string searchUrl = ItunesSearchUrl + $"?term={query}&entity=album&limit=50&explicit=Yes";

        StaticArtworkLookupResult result = await FetchStaticArtworkAsync(searchUrl, ct);
        if (result.Status != StaticArtworkLookupStatus.Found)
        {
            return result;
        }

        // iTunes matches loosely, so keep only the requested artist and albums containing the requested title.
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

    public Task<StaticArtworkLookupResult> LookupStaticArtworkAsync(AppleMusicCatalogTarget target, CancellationToken ct)
    {
        string lookupUrl = ItunesLookupUrl + $"?id={Uri.EscapeDataString(target.Id)}&entity=album&country={target.Storefront}";
        return FetchStaticArtworkAsync(lookupUrl, ct);
    }

    private async Task<StaticArtworkLookupResult> FetchStaticArtworkAsync(string requestUrl, CancellationToken ct)
    {
        if (statusService.IsInBackoff())
        {
            Log.Warning("Global rate-limit backoff active. Skipping static artwork request for {Url}", requestUrl);
            return new(StaticArtworkLookupStatus.RateLimited);
        }

        await _appleMusicSemaphore.WaitAsync(ct);
        try
        {
            Log.Information("Outgoing static artwork request: {Url}", requestUrl);
            using HttpResponseMessage response = await httpClient.GetAsync(requestUrl, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                Log.Warning("Rate Limit hit on iTunes API: {StatusCode}", response.StatusCode);
                statusService.ReportRateLimit();
                return new(StaticArtworkLookupStatus.RateLimited);
            }

            response.EnsureSuccessStatusCode();
            statusService.ReportSuccess();

            JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            List<StaticArtwork> artworks = [];

            foreach (JsonNode? item in json?["results"]?.AsArray() ?? [])
            {
                if (item?["wrapperType"]?.ToString() != "collection")
                {
                    continue;
                }

                string? artistName = item["artistName"]?.ToString();
                string? albumName = item["collectionName"]?.ToString();
                string? artworkUrl = item["artworkUrl100"]?.ToString();
                string? viewUrl = item["collectionViewUrl"]?.ToString();

                if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumName) ||
                    !TryBuildArtworkUrl(artworkUrl, 1000, out string fullUrl) ||
                    !TryBuildArtworkUrl(artworkUrl, 600, out string thumbnailUrl) ||
                    !Uri.TryCreate(viewUrl, UriKind.Absolute, out Uri? viewUri) ||
                    !AppleMusicUrl.TryParse(viewUri.GetLeftPart(UriPartial.Path), out _))
                {
                    continue;
                }

                if (artworks.Any(a => a.Url == fullUrl))
                {
                    continue;
                }

                artworks.Add(new StaticArtwork(
                    artistName,
                    albumName,
                    fullUrl,
                    thumbnailUrl,
                    viewUri.GetLeftPart(UriPartial.Path),
                    item["releaseDate"]?.ToString()));
            }

            return artworks.Count > 0
                ? new(StaticArtworkLookupStatus.Found, artworks)
                : new(StaticArtworkLookupStatus.NoMatch);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Error("Static artwork request failed: {Message}", ex.Message);
            return new(StaticArtworkLookupStatus.Error);
        }
        finally
        {
            _appleMusicSemaphore.Release();
        }
    }

    private const int MaxArtistCandidates = 3;

    public async Task<StaticArtworkLookupResult> SearchArtistArtworkAsync(string artist, CancellationToken ct)
    {
        if (statusService.IsInBackoff())
        {
            Log.Warning("Global rate-limit backoff active. Skipping artist artwork lookup for {Artist}", artist);
            return new(StaticArtworkLookupStatus.RateLimited);
        }

        List<string> artistIds = [];
        string searchUrl = ItunesSearchUrl + $"?term={Uri.EscapeDataString(artist)}&entity=musicArtist&limit=25";

        await _appleMusicSemaphore.WaitAsync(ct);
        try
        {
            Log.Information("Outgoing artist search request: {Url}", searchUrl);
            using HttpResponseMessage response = await httpClient.GetAsync(searchUrl, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                Log.Warning("Rate Limit hit on iTunes API: {StatusCode}", response.StatusCode);
                statusService.ReportRateLimit();
                return new(StaticArtworkLookupStatus.RateLimited);
            }

            response.EnsureSuccessStatusCode();
            statusService.ReportSuccess();

            JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            List<(string Id, bool Exact)> candidates = [];
            foreach (JsonNode? item in json?["results"]?.AsArray() ?? [])
            {
                string? name = item?["artistName"]?.ToString();
                string? id = item?["artistId"]?.ToString();
                if (item?["wrapperType"]?.ToString() != "artist" || string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit) ||
                    !CacheKeyNormalizer.ArtistsMatch(artist, name))
                {
                    continue;
                }

                candidates.Add((id, CacheKeyNormalizer.Normalize(name) == CacheKeyNormalizer.Normalize(artist)));
            }

            // "Gorillaz" also matches "Gorillaz Day"; an exact name wins when there is one.
            bool hasExact = candidates.Any(c => c.Exact);
            artistIds = candidates.Where(c => c.Exact || !hasExact).Select(c => c.Id).Distinct().Take(MaxArtistCandidates).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Error("Artist search failed: {Message}", ex.Message);
            return new(StaticArtworkLookupStatus.Error);
        }
        finally
        {
            _appleMusicSemaphore.Release();
        }

        if (artistIds.Count == 0)
        {
            return new(StaticArtworkLookupStatus.NoMatch);
        }

        string? token = await GetBearerTokenAsync($"https://music.apple.com/us/artist/{artistIds[0]}", ct);
        if (token == null)
        {
            return new(StaticArtworkLookupStatus.Error);
        }

        string apiUrl = $"https://amp-api.music.apple.com/v1/catalog/us/artists?ids={string.Join(',', artistIds)}&platform=web";

        await _appleMusicSemaphore.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Headers.Add("Origin", "https://music.apple.com");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            Log.Information("Outgoing artist catalog request: {Url}", apiUrl);
            using HttpResponseMessage response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                Log.Warning("Rate Limit hit while calling AMP API: {StatusCode}", response.StatusCode);
                statusService.ReportRateLimit();
                return new(StaticArtworkLookupStatus.RateLimited);
            }

            response.EnsureSuccessStatusCode();
            statusService.ReportSuccess();

            JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            List<StaticArtwork> artworks = [];

            foreach (JsonNode? item in json?["data"]?.AsArray() ?? [])
            {
                JsonNode? attrs = item?["attributes"];
                string? name = attrs?["name"]?.ToString();
                string? template = attrs?["artwork"]?["url"]?.ToString();
                string? pageUrl = attrs?["url"]?.ToString();

                if (string.IsNullOrWhiteSpace(name) ||
                    !TryBuildTemplateArtworkUrl(template, 1000, out string fullUrl) ||
                    !TryBuildTemplateArtworkUrl(template, 600, out string thumbnailUrl) ||
                    !Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? pageUri) ||
                    !string.Equals(pageUri.IdnHost, AppleMusicUrl.Host, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                artworks.Add(new StaticArtwork(name, string.Empty, fullUrl, thumbnailUrl, pageUri.GetLeftPart(UriPartial.Path), null));
            }

            return artworks.Count > 0
                ? new(StaticArtworkLookupStatus.Found, artworks)
                : new(StaticArtworkLookupStatus.NoMatch);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Error("Artist catalog request failed: {Message}", ex.Message);
            return new(StaticArtworkLookupStatus.Error);
        }
        finally
        {
            _appleMusicSemaphore.Release();
        }
    }

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

    // Apple serves any size up to the source image by rewriting the trailing "{w}x{h}bb.jpg" segment.
    private static bool TryBuildArtworkUrl(string? artworkUrl, int size, out string url)
    {
        url = string.Empty;

        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IdnHost.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length > 0)
        {
            return false;
        }

        int lastSlash = uri.AbsolutePath.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return false;
        }

        url = $"https://{uri.IdnHost}{uri.AbsolutePath[..lastSlash]}/{size}x{size}bb.jpg";
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
        if (statusService.IsInBackoff())
        {
            Log.Warning("Global rate-limit backoff active. Skipping album page request for {Url}", target.PageUrl);
            return new(AppleMusicPageParseStatus.RateLimited);
        }

        bool semaphoreAcquired = false;
        try
        {
            if (statusService.IsInBackoff())
            {
                Log.Warning("Global rate-limit backoff active (after lock). Skipping album page request for {Url}", target.PageUrl);
                return new(AppleMusicPageParseStatus.RateLimited);
            }

            string? token = await GetBearerTokenAsync(target.PageUrl, ct);
            if (token == null) return new AppleMusicPageParseResult(AppleMusicPageParseStatus.Error);

            string apiUrl = $"https://amp-api.music.apple.com/v1/catalog/{target.Storefront}/{target.ResourceType}/{Uri.EscapeDataString(target.Id)}?extend=editorialVideo&platform=web";

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Headers.Add("Origin", "https://music.apple.com");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            Log.Information("Outgoing request to album page: {Url}", apiUrl);
            await _appleMusicSemaphore.WaitAsync(ct);
            semaphoreAcquired = true;
            using var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                Log.Warning("Rate Limit hit while calling AMP API: {StatusCode}", response.StatusCode);
                statusService.ReportRateLimit();
                return new(AppleMusicPageParseStatus.RateLimited);
            }

            response.EnsureSuccessStatusCode();
            statusService.ReportSuccess();

            var jsonString = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = JsonNode.Parse(jsonString);

            JsonNode? data = json?["data"]?[0];
            string? returnedId = data?["id"]?.ToString();
            string? returnedType = data?["type"]?.ToString();
            if (!string.Equals(returnedId, target.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(returnedType, target.ResourceType, StringComparison.Ordinal))
            {
                Log.Warning("Catalog response did not match the requested {ResourceType} {Id} (got {ReturnedType} {ReturnedId})",
                    target.ResourceType, target.Id, returnedType, returnedId);
                return new AppleMusicPageParseResult(AppleMusicPageParseStatus.Error);
            }

            var attrs = data?["attributes"];
            if (attrs != null)
            {
                string artistName = attrs["artistName"]?.ToString()
                                    ?? attrs["curatorName"]?.ToString()
                                    ?? "Unknown Artist";
                string albumName = attrs["name"]?.ToString() ?? "Unknown Album";

                string? urlSquare = null;
                string? urlTall = null;
                var videos = attrs["editorialVideo"];
                if (videos != null)
                {
                    urlSquare = videos["motionDetailSquare"]?["video"]?.ToString();
                    urlTall = videos["motionDetailTall"]?["video"]?.ToString();
                }

                return new AppleMusicPageParseResult(AppleMusicPageParseStatus.Success, urlSquare, urlTall, artistName, albumName);
            }

            return new AppleMusicPageParseResult(AppleMusicPageParseStatus.Error);
        }
        catch (HttpRequestException ex)
        {
            Log.Error("Network error in ParseAppleMusicPageAsync: {Message}", ex.Message);
            return new AppleMusicPageParseResult(AppleMusicPageParseStatus.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Unexpected error in ParseAppleMusicPageAsync: {Message}", ex.Message);
            return new AppleMusicPageParseResult(AppleMusicPageParseStatus.Error);
        }
        finally
        {
            if (semaphoreAcquired)
            {
                _appleMusicSemaphore.Release();
            }
        }
    }
}