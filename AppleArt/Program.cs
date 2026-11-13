using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.RateLimiting;
using AnimatedArtworks.Application;
using AnimatedArtworks.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Server;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code
    )
    .CreateLogger();

try 
{
    Log.Information("Starting Artwork Finder Web API...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // The JSON paths are only read once, to import the former JSON caches into the SQLite database.
    var legacyCachePath = builder.Configuration["CACHE_FILE_PATH"] ?? "cache_database.json";
    var legacyMetadataResolutionCachePath = builder.Configuration["METADATA_RESOLUTION_CACHE_FILE_PATH"] ?? "metadata_resolution_cache.json";
    var databasePath = builder.Configuration["DATABASE_FILE_PATH"]
                       ?? Path.Combine(Path.GetDirectoryName(legacyCachePath) ?? string.Empty, "cache.db");

    builder.Services.AddSingleton(_ => new SqliteDatabase(databasePath));
    builder.Services.AddSingleton(new LegacyJsonCachePaths(legacyCachePath, legacyMetadataResolutionCachePath));
    builder.Services.AddSingleton<LegacyJsonCacheImporter>();
    builder.Services.AddSingleton<ArtworkCache>();
    builder.Services.AddSingleton<MetadataResolutionCache>();
    builder.Services.AddHostedService<CacheInitializationHostedService>();

    builder.Services.AddSingleton<SystemStatusService>();
    
    builder.Services.AddSingleton<KeyedLocker>();

    builder.Services.AddSingleton(new AppleLookupLimiter(
        perClientPerMinute: builder.Configuration.GetValue("APPLE_LOOKUPS_PER_CLIENT_PER_MINUTE", 20),
        globalPerMinute: builder.Configuration.GetValue("APPLE_LOOKUPS_PER_MINUTE", 120)));

    builder.Services.AddTransient<AppleEgressHandler>();
    builder.Services.AddHttpClient<IAppleMusicClient, AppleMusicClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    })
    .ConfigurePrimaryHttpMessageHandler(AppleEgressHandler.CreatePrimaryHandler)
    .AddHttpMessageHandler<AppleEgressHandler>();

    builder.Services.AddScoped<ArtworkService>();
    builder.Services.AddSingleton<StaticArtworkCache>();
    builder.Services.AddScoped<StaticArtworkService>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<AlbumDetailsService>();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
        {
            policy.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
        });
    });

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, token) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers["Retry-After"] = retryAfter.TotalSeconds.ToString();
            }

            context.HttpContext.Response.ContentType = "text/plain";
            await context.HttpContext.Response.WriteAsync("Too many requests. Please slow down.", token);
        };

        options.AddPolicy("ApiRateLimit", httpContext =>
        {
            return RateLimitPartition.GetFixedWindowLimiter(ClientIdentity.Resolve(httpContext), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
    });

    const int MaxQueryLength = 200;

    var app = builder.Build();

    app.UseCors("AllowAll");

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseRateLimiter();

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is { Count: > 0 })
        {
            Log.Information("Running on: {Addresses}", string.Join(", ", addresses));
        }
        else
        {
            Log.Information("Started, but no server addresses were available yet.");
        }
    });

    // Optional: when ORIGIN_SECRET is set, /api only answers requests carrying it (sent by api.tromme.app).
    var originSecret = Environment.GetEnvironmentVariable("ORIGIN_SECRET");
    if (!string.IsNullOrEmpty(originSecret))
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") &&
                context.Request.Headers["X-Origin-Secret"] != originSecret)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });
    }

    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            var database = context.RequestServices.GetRequiredService<SqliteDatabase>();

            if (!database.IsInitialized)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Cache is still initializing. Please retry shortly.");
                return;
            }
        }

        await next();
    });
    
    app.MapGet("/api/v1/status", (HttpResponse response, [FromServices] SystemStatusService statusService, [FromServices] ArtworkCache cacheService) =>
    {
        response.Headers.CacheControl = "public, max-age=86400";
        ArtworkCacheStats stats = cacheService.GetStats();

        long totalSearches = stats.TotalSearches;
        long totalDownloads = stats.TotalDownloads;
        long totalCacheEntries = stats.TotalCacheEntries;
        long totalAnimatedEntries = stats.TotalAnimatedEntries;

        if (statusService.IsRateLimited)
        {
            return Results.Ok(new
            {
                status = "degraded",
                message = "Apple Music Rate Limit. May be unstable.",
                totalSearches,
                totalDownloads,
                totalCacheEntries,
                totalAnimatedEntries
            });
        }
        
        return Results.Ok(new
            {
                status = "operational",
                message = "System Operational",
                totalSearches,
                totalDownloads,
                totalCacheEntries,
                totalAnimatedEntries
            });
    }).RequireRateLimiting("ApiRateLimit");

    app.MapGet("/api/v1/artwork/search", async (
        [FromQuery] string artist, 
        [FromQuery] string album, 
        [FromQuery] string? title,
        [FromServices] ArtworkService service,
        [FromServices] ILogger<Program> logger,
        [FromServices] ArtworkCache cacheService,
        HttpContext context,
        HttpResponse response,
        CancellationToken ct) =>
    {
        response.Headers.CacheControl = "no-cache";
        logger.LogInformation("Incoming Request: Metadata Search -> Artist: {Artist}, Album: {Album}, Title: {Title}", 
            artist, album, title ?? "N/A");
        
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return Results.BadRequest("Artist and Album must be provided.");
        
        service.SetClient(ClientIdentity.Resolve(context));
        (ArtworkCacheEntry? entry, bool isCached) = await service.GetArtworkByDetailsAsync(artist, album, title, ct);
        if (entry == null && service.WasThrottled)
            return Throttled(response);

        if (entry != null)
        {
            cacheService.IncrementSearchCount(entry);

            if ((entry.M3u8Url != null && entry.M3u8Url != "NONE") || (entry.M3u8UrlTall != null && entry.M3u8UrlTall != "NONE"))
            {
                return Results.Ok(new { 
                    url = entry.M3u8Url == "NONE" ? null : entry.M3u8Url, 
                    url_tall = entry.M3u8UrlTall == "NONE" ? null : entry.M3u8UrlTall, 
                    artist = entry.Artist, 
                    album = entry.Album, 
                    isCached 
                });
            }
        }

        return Results.NotFound(new { message = "No animated artwork found." });
    }).RequireRateLimiting("ApiRateLimit");

    app.MapGet("/api/v1/artwork/url", async (
        [FromQuery] string url, 
        [FromServices] ArtworkService service,
        [FromServices] ILogger<Program> logger,
        [FromServices] ArtworkCache cacheService,
        HttpContext context,
        HttpResponse response,
        CancellationToken ct) =>
    {
        response.Headers.CacheControl = "public, max-age=86400";
        logger.LogInformation("Incoming Request: URL Search -> {AppleMusicUrl}", url);
        
        if (!AppleMusicUrl.TryParse(url, out _))
            return Results.BadRequest("A valid Apple Music album or playlist URL must be provided.");

        service.SetClient(ClientIdentity.Resolve(context));
        var (entry, isCached) = await service.GetArtworkByUrlAsync(url, ct);
        if (entry == null && service.WasThrottled)
            return Throttled(response);

        if (entry != null)
        {
            cacheService.IncrementSearchCount(entry);
            
            if ((entry.M3u8Url != null && entry.M3u8Url != "NONE") || (entry.M3u8UrlTall != null && entry.M3u8UrlTall != "NONE"))
            {
                return Results.Ok(new { 
                    url = entry.M3u8Url == "NONE" ? null : entry.M3u8Url, 
                    url_tall = entry.M3u8UrlTall == "NONE" ? null : entry.M3u8UrlTall, 
                    artist = entry.Artist, 
                    album = entry.Album, 
                    isCached 
                });
            }
        }
    
        return Results.NotFound(new { message = "No animated artwork found." });
    }).RequireRateLimiting("ApiRateLimit");
    
    app.MapGet("/api/v1/artwork/static/search", async (
        [FromQuery] string? artist,
        [FromQuery] string? album,
        [FromServices] StaticArtworkService service,
        [FromServices] ILogger<Program> logger,
        HttpContext context,
        HttpResponse response,
        CancellationToken ct) =>
    {
        logger.LogInformation("Incoming Request: Static Search -> Artist: {Artist}, Album: {Album}", artist, album);

        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return Results.BadRequest("Artist and Album must be provided.");
        if (artist.Length > MaxQueryLength || album.Length > MaxQueryLength)
            return Results.BadRequest("Artist and Album must be 200 characters or fewer.");

        service.SetClient(ClientIdentity.Resolve(context));
        var (result, isCached) = await service.SearchAsync(artist, album, ct);
        return StaticArtworkResponse(result, isCached, response);
    }).RequireRateLimiting("ApiRateLimit");

    app.MapGet("/api/v1/artwork/static/url", async (
        [FromQuery] string? url,
        [FromServices] StaticArtworkService service,
        [FromServices] ILogger<Program> logger,
        HttpContext context,
        HttpResponse response,
        CancellationToken ct) =>
    {
        logger.LogInformation("Incoming Request: Static URL -> {AppleMusicUrl}", url);

        // iTunes lookup only knows albums; playlist covers are not offered.
        if (!AppleMusicUrl.TryParse(url, out AppleMusicCatalogTarget target) || target.ResourceType != "albums")
            return Results.BadRequest("A valid Apple Music album URL must be provided.");

        service.SetClient(ClientIdentity.Resolve(context));
        var (result, isCached) = await service.LookupAsync(target, ct);
        return StaticArtworkResponse(result, isCached, response);
    }).RequireRateLimiting("ApiRateLimit");

    app.MapGet("/api/v1/artwork/artist/search", async (
        [FromQuery] string? artist,
        [FromServices] StaticArtworkService service,
        [FromServices] ILogger<Program> logger,
        HttpContext context,
        HttpResponse response,
        CancellationToken ct) =>
    {
        logger.LogInformation("Incoming Request: Artist Search -> Artist: {Artist}", artist);

        if (string.IsNullOrWhiteSpace(artist))
            return Results.BadRequest("Artist must be provided.");
        if (artist.Length > MaxQueryLength)
            return Results.BadRequest("Artist must be 200 characters or fewer.");

        service.SetClient(ClientIdentity.Resolve(context));
        var (result, isCached) = await service.SearchArtistAsync(artist, ct);
        return StaticArtworkResponse(result, isCached, response);
    }).RequireRateLimiting("ApiRateLimit");

    app.MapGet("/api/v1/album/details", async (
        [FromQuery] string? id,
        [FromQuery] string? country,
        [FromServices] AlbumDetailsService service,
        [FromServices] ILogger<Program> logger,
        HttpContext context,
        HttpResponse response,
        CancellationToken ct) =>
    {
        logger.LogInformation("Incoming Request: Album Details -> {Id} ({Country})", id, country);

        string storefront = string.IsNullOrWhiteSpace(country) ? "us" : country.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 15 || !id.All(char.IsAsciiDigit))
            return Results.BadRequest("A numeric Apple Music album id must be provided.");
        if (storefront.Length != 2 || !storefront.All(char.IsAsciiLetterLower))
            return Results.BadRequest("Country must be a two-letter storefront code.");

        var (result, isCached) = await service.LookupAsync(id, storefront, ClientIdentity.Resolve(context), ct);
        switch (result.Status)
        {
            case StaticArtworkLookupStatus.Found:
                AppleAlbumDetails details = result.Details!;
                response.Headers.CacheControl = "public, max-age=86400";
                return Results.Ok(new
                {
                    title = details.Title,
                    artist = details.Artist,
                    releaseDate = details.ReleaseDate,
                    genre = details.Genre,
                    copyright = details.Copyright,
                    artwork = details.ArtworkUrl,
                    tracks = details.Tracks.Select(t => new
                    {
                        title = t.Title,
                        artist = t.Artist,
                        trackNumber = t.TrackNumber,
                        discNumber = t.DiscNumber,
                        durationMs = t.DurationMs
                    }),
                    isCached
                });
            case StaticArtworkLookupStatus.NoMatch:
                response.Headers.CacheControl = "public, max-age=3600";
                return Results.NotFound(new { message = "Album not found." });
            case StaticArtworkLookupStatus.RateLimited:
                return Throttled(response);
            default:
                response.Headers.CacheControl = "no-store";
                return Results.Problem("Apple lookup failed.", statusCode: StatusCodes.Status502BadGateway);
        }
    }).RequireRateLimiting("ApiRateLimit");

    app.MapGet("/api/v1/artwork/history", (HttpResponse response, [FromServices] ArtworkCache cache) =>
    {
        response.Headers.CacheControl = "public, max-age=86400";
        var recent = cache.GetRecentSearches(18).Select(x => new 
        {
            artist = x.Artist,
            album = x.Album,
            url = x.M3u8Url == "NONE" ? null : x.M3u8Url,
            url_tall = x.M3u8UrlTall == "NONE" ? null : x.M3u8UrlTall,
            fetchedAt = x.LastFetched
        });
    
        return Results.Ok(recent);
    }).RequireRateLimiting("ApiRateLimit");

    app.Run();

    static IResult StaticArtworkResponse(StaticArtworkLookupResult result, bool isCached, HttpResponse response)
    {
        switch (result.Status)
        {
            case StaticArtworkLookupStatus.Found:
                response.Headers.CacheControl = "public, max-age=86400";
                return Results.Ok(new
                {
                    results = result.Artworks!.Select(a => new
                    {
                        artist = a.Artist,
                        album = a.Album,
                        url = a.Url,
                        thumbnail = a.ThumbnailUrl,
                        appleMusicUrl = a.AppleMusicUrl,
                        releaseDate = a.ReleaseDate,
                        logo = a.LogoUrl
                    }),
                    isCached
                });
            case StaticArtworkLookupStatus.NoMatch:
                response.Headers.CacheControl = "public, max-age=3600";
                return Results.NotFound(new { message = "No artwork found." });
            case StaticArtworkLookupStatus.RateLimited:
                return Throttled(response);
            default:
                response.Headers.CacheControl = "no-store";
                return Results.Problem("Apple lookup failed.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    static IResult Throttled(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.RetryAfter = "60";
        return Results.Text("Too many new lookups. Please slow down.", "text/plain", statusCode: StatusCodes.Status429TooManyRequests);
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

