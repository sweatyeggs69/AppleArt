using System;
using Microsoft.Data.Sqlite;

namespace AnimatedArtworks.Infrastructure;

public sealed class MetadataResolutionCache(SqliteDatabase database)
{
    public MetadataResolutionLookup GetLookup(string artist, string album, TimeSpan noMatchTtl)
    {
        string key = CacheKeyNormalizer.BuildMetadataKey(artist, album);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, resolved_apple_music_url, last_resolved, artist, album FROM metadata_resolutions WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        MetadataResolutionStatus status;
        string? resolvedAppleMusicUrl;
        DateTime lastResolved;
        string storedArtist;
        string storedAlbum;

        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return new(MetadataResolutionStatus.None);
            }

            status = (MetadataResolutionStatus)reader.GetInt32(0);
            resolvedAppleMusicUrl = reader.IsDBNull(1) ? null : reader.GetString(1);
            lastResolved = SqliteDatabase.FromUnixMilliseconds(reader.GetInt64(2));
            storedArtist = reader.GetString(3);
            storedAlbum = reader.GetString(4);
        }

        bool sameQuery = IsSameQuery(storedArtist, storedAlbum, artist, album);

        if (status == MetadataResolutionStatus.NoMatch)
        {
            bool isFreshNoMatch = DateTime.UtcNow - lastResolved <= noMatchTtl;
            if (!isFreshNoMatch)
            {
                Delete(connection, key);
                return new(MetadataResolutionStatus.None);
            }

            // A no-match only answers the exact query that produced it, so a punctuation variant
            // that normalizes to the same key can't hide artwork from the real name.
            return sameQuery ? new(MetadataResolutionStatus.NoMatch) : new(MetadataResolutionStatus.None);
        }

        return new(MetadataResolutionStatus.Resolved, resolvedAppleMusicUrl, lastResolved, sameQuery);
    }

    public void SaveResolvedUrl(string artist, string album, string resolvedAppleMusicUrl)
    {
        Save(new MetadataResolutionEntry(
            Artist: artist,
            Album: album,
            ResolvedAppleMusicUrl: resolvedAppleMusicUrl,
            Status: MetadataResolutionStatus.Resolved,
            LastResolved: DateTime.UtcNow
        ));
    }

    public void SaveNoMatch(string artist, string album)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata_resolutions (key, artist, album, resolved_apple_music_url, status, last_resolved)
            VALUES ($key, $artist, $album, NULL, $status, $lastResolved)
            ON CONFLICT (key) DO UPDATE SET
                artist = excluded.artist,
                album = excluded.album,
                resolved_apple_music_url = NULL,
                status = excluded.status,
                last_resolved = excluded.last_resolved
            WHERE metadata_resolutions.status <> $resolvedStatus
            """;
        command.Parameters.AddWithValue("$key", CacheKeyNormalizer.BuildMetadataKey(artist, album));
        command.Parameters.AddWithValue("$artist", artist);
        command.Parameters.AddWithValue("$album", album);
        command.Parameters.AddWithValue("$status", (int)MetadataResolutionStatus.NoMatch);
        command.Parameters.AddWithValue("$resolvedStatus", (int)MetadataResolutionStatus.Resolved);
        command.Parameters.AddWithValue("$lastResolved", SqliteDatabase.ToUnixMilliseconds(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public void RemoveResolvedUrl(string artist, string album)
    {
        using var connection = database.OpenConnection();
        Delete(connection, CacheKeyNormalizer.BuildMetadataKey(artist, album));
    }

    private static bool IsSameQuery(string storedArtist, string storedAlbum, string artist, string album) =>
        string.Equals(storedArtist.Trim(), artist.Trim(), StringComparison.Ordinal) &&
        string.Equals(storedAlbum.Trim(), album.Trim(), StringComparison.Ordinal);

    private void Save(MetadataResolutionEntry entry)
    {
        using var connection = database.OpenConnection();
        using var command = CreateUpsertCommand(connection, overwriteExisting: true);
        BindEntry(command, entry);
        command.ExecuteNonQuery();
    }

    private static void Delete(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metadata_resolutions WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    internal static SqliteCommand CreateUpsertCommand(SqliteConnection connection, bool overwriteExisting)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO metadata_resolutions (key, artist, album, resolved_apple_music_url, status, last_resolved)
            VALUES ($key, $artist, $album, $resolvedUrl, $status, $lastResolved)
            {(overwriteExisting
                ? """
                  ON CONFLICT (key) DO UPDATE SET
                      artist = excluded.artist,
                      album = excluded.album,
                      resolved_apple_music_url = excluded.resolved_apple_music_url,
                      status = excluded.status,
                      last_resolved = excluded.last_resolved
                  """
                : "ON CONFLICT (key) DO NOTHING")}
            """;

        foreach (string name in new[] { "$key", "$artist", "$album", "$resolvedUrl", "$status", "$lastResolved" })
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        return command;
    }

    internal static void BindEntry(SqliteCommand command, MetadataResolutionEntry entry)
    {
        command.Parameters["$key"].Value = CacheKeyNormalizer.BuildMetadataKey(entry.Artist, entry.Album);
        command.Parameters["$artist"].Value = entry.Artist ?? string.Empty;
        command.Parameters["$album"].Value = entry.Album ?? string.Empty;
        command.Parameters["$resolvedUrl"].Value = (object?)entry.ResolvedAppleMusicUrl ?? DBNull.Value;
        command.Parameters["$status"].Value = (int)entry.Status;
        command.Parameters["$lastResolved"].Value = SqliteDatabase.ToUnixMilliseconds(entry.LastResolved);
    }
}

public readonly record struct MetadataResolutionEntry(
    string Artist,
    string Album,
    string? ResolvedAppleMusicUrl,
    MetadataResolutionStatus Status,
    DateTime LastResolved
);

public readonly record struct MetadataResolutionLookup(
    MetadataResolutionStatus Status,
    string? ResolvedAppleMusicUrl = null,
    DateTime LastResolved = default,
    bool SavedForSameQuery = false
);

public enum MetadataResolutionStatus
{
    None,
    Resolved,
    NoMatch
}
