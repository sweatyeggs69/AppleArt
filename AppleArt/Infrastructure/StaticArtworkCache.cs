using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AnimatedArtworks.Infrastructure;

// Persisted like animated artwork: found results are kept for good, no-matches expire after 30 days.
// Artist results carry the logo, which Apple can change, so they are rechecked after 30 days too.
// Keyed by the exact (trimmed, case-folded) query, so a no-match only answers that same query.
public sealed class StaticArtworkCache(SqliteDatabase database)
{
    private static readonly TimeSpan NoMatchTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan ArtistRecheckInterval = TimeSpan.FromDays(30);
    private const string ArtistKeyPrefix = "artist|";

    public static string SearchKey(string artist, string album) =>
        $"search|{artist.Trim().ToLowerInvariant()}|{album.Trim().ToLowerInvariant()}";

    public static string ArtistKey(string artist) => $"{ArtistKeyPrefix}{artist.Trim().ToLowerInvariant()}";

    public static string LookupKey(AppleMusicCatalogTarget target) => $"lookup|{target.Storefront}|{target.Id}";

    // A stale result is returned too, so it can still be served when the recheck fails.
    public bool TryGet(string key, out StaticArtworkLookupResult result, out bool isStale)
    {
        result = default;
        isStale = false;

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, results_json, last_fetched FROM static_artworks WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var status = (StaticArtworkLookupStatus)reader.GetInt32(0);
        DateTime lastFetched = SqliteDatabase.FromUnixMilliseconds(reader.GetInt64(2));

        if (status == StaticArtworkLookupStatus.NoMatch)
        {
            if (DateTime.UtcNow - lastFetched >= NoMatchTtl)
            {
                return false;
            }

            result = new StaticArtworkLookupResult(StaticArtworkLookupStatus.NoMatch);
            return true;
        }

        List<StaticArtwork>? artworks = reader.IsDBNull(1)
            ? null
            : JsonSerializer.Deserialize<List<StaticArtwork>>(reader.GetString(1));
        if (artworks is not { Count: > 0 })
        {
            return false;
        }

        result = new StaticArtworkLookupResult(StaticArtworkLookupStatus.Found, artworks);
        isStale = key.StartsWith(ArtistKeyPrefix, StringComparison.Ordinal) &&
                  DateTime.UtcNow - lastFetched >= ArtistRecheckInterval;
        return true;
    }

    public void Save(string key, StaticArtworkLookupResult result)
    {
        if (result.Status is not (StaticArtworkLookupStatus.Found or StaticArtworkLookupStatus.NoMatch))
        {
            return;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO static_artworks (key, status, results_json, last_fetched)
            VALUES ($key, $status, $results, $lastFetched)
            ON CONFLICT(key) DO UPDATE SET
                status = excluded.status,
                results_json = excluded.results_json,
                last_fetched = excluded.last_fetched
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$status", (int)result.Status);
        command.Parameters.AddWithValue("$results", result.Status == StaticArtworkLookupStatus.Found
            ? JsonSerializer.Serialize(result.Artworks)
            : DBNull.Value);
        command.Parameters.AddWithValue("$lastFetched", SqliteDatabase.ToUnixMilliseconds(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }
}
