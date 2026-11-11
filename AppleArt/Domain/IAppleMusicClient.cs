using System.Threading;
using System.Threading.Tasks;
using AnimatedArtworks.Infrastructure;

public interface IAppleMusicClient
{
    Task<string?> GetAppleMusicUrlViaItunesAsync(string artist, string album, string? title = null,
        CancellationToken ct = default);

    Task<AppleMusicWebSearchResult> GetAppleMusicUrlViaWebSearchAsync(string artist, string album, string? title = null,
        CancellationToken ct = default);
    
    Task<AppleMusicPageParseResult> ParseAppleMusicPageAsync(AppleMusicCatalogTarget target, CancellationToken ct);
}