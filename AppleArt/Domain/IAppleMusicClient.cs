using System.Threading;
using System.Threading.Tasks;
using AnimatedArtworks.Infrastructure;

public interface IAppleMusicClient
{
    Task<AppleMusicWebSearchResult> GetAppleMusicUrlViaWebSearchAsync(string artist, string album, string? title = null,
        CancellationToken ct = default);
    
    Task<AppleMusicPageParseResult> ParseAppleMusicPageAsync(AppleMusicCatalogTarget target, CancellationToken ct);

    Task<StaticArtworkLookupResult> SearchStaticArtworkAsync(string artist, string album, CancellationToken ct);

    Task<StaticArtworkLookupResult> LookupStaticArtworkAsync(AppleMusicCatalogTarget target, CancellationToken ct);

    Task<StaticArtworkLookupResult> SearchArtistArtworkAsync(string artist, CancellationToken ct);

    Task<AppleAlbumDetailsResult> LookupAlbumDetailsAsync(string id, string storefront, CancellationToken ct);
}
