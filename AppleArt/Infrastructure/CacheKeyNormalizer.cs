using System;
using System.Text;

namespace AnimatedArtworks.Infrastructure;

public static class CacheKeyNormalizer
{
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(input.Length);
        foreach (char character in input)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
    }

    public static string BuildMetadataKey(string? artist, string? album)
    {
        return $"{Normalize(artist)}|{Normalize(album)}";
    }

    /// <summary>
    /// Whether two artist names are the same act: equal once punctuation is ignored, or one
    /// contains the other as whole words ("Artist" vs "Artist &amp; Guest"). Unrelated names never match.
    /// </summary>
    public static bool ArtistsMatch(string? requested, string? found)
    {
        string a = NormalizeWords(requested);
        string b = NormalizeWords(found);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return a == b || $" {b} ".Contains($" {a} ", StringComparison.Ordinal) || $" {a} ".Contains($" {b} ", StringComparison.Ordinal);
    }

    private static string NormalizeWords(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(input.Length);
        foreach (char character in input)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
            else if (character is not ('\'' or '\u2019' or '\u2018' or '\u02BC'))
            {
                normalized.Append(' ');
            }
        }

        return string.Join(' ', normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool HasAnimatedArtwork(string? m3U8Url, string? m3U8UrlTall)
    {
        return (m3U8Url != null && m3U8Url != "NONE") || (m3U8UrlTall != null && m3U8UrlTall != "NONE");
    }
}
