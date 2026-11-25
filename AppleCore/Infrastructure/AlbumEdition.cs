using System.Text.RegularExpressions;

namespace AppleCore.Infrastructure;

public static partial class AlbumEdition
{
    private const string Keywords =
        @"deluxe|edition|remaster|remastered|expanded|anniversary|bonus|version|special|collector|collectors|reissue|explicit|clean";

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(" + Keywords + @")\b[^\)\]]*[\)\]]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"\s+[-–—]\s+[^-–—]*\b(" + Keywords + @")\b[^-–—]*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashedRegex();

    // "Album (Deluxe Edition)", "Album [2011 Remaster]" and "Album - 20th Anniversary Edition" become "Album".
    public static string Strip(string title)
    {
        string current = title;
        while (true)
        {
            string next = DashedRegex().Replace(BracketedRegex().Replace(current, ""), "");
            if (next == current || next.Trim().Length == 0)
            {
                return current.Trim();
            }

            current = next;
        }
    }
}
