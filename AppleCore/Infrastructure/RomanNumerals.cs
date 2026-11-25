using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AppleCore.Infrastructure;

public static partial class RomanNumerals
{
    private static readonly (int Value, string Numeral)[] Table =
        [(10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];

    // Case-sensitive: only capital I, V and X count, so ordinary words are left alone.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[IVX]+(?![\p{L}\p{N}])")]
    private static partial Regex RomanWordRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])([1-9]|[1-3][0-9])(?![\p{L}\p{N}])")]
    private static partial Regex NumberWordRegex();

    public static string ToArabic(string input) =>
        RomanWordRegex().Replace(input, match => Parse(match.Value) is { } value ? value.ToString() : match.Value);

    // Apple and Plex disagree on "IV" versus "4", so a search that found nothing is retried with the other spelling.
    public static string? AlternateSpelling(string input)
    {
        bool hasRoman = RomanWordRegex().Matches(input).Any(match =>
            Parse(match.Value) != null && (match.Length > 1 || match.Index + match.Length == input.TrimEnd().Length));
        if (hasRoman)
        {
            string arabic = RomanWordRegex().Replace(input, match =>
                Parse(match.Value) is { } value && (match.Length > 1 || match.Index + match.Length == input.TrimEnd().Length)
                    ? value.ToString()
                    : match.Value);
            return arabic != input ? arabic : null;
        }

        string roman = NumberWordRegex().Replace(input, match => Format(int.Parse(match.Value)));
        return roman != input ? roman : null;
    }

    private static int? Parse(string numeral)
    {
        int total = 0;
        string rest = numeral;
        foreach (var (value, symbol) in Table)
        {
            while (rest.StartsWith(symbol, StringComparison.Ordinal))
            {
                total += value;
                rest = rest[symbol.Length..];
            }
        }

        return rest.Length == 0 && total is > 0 and < 40 && Format(total) == numeral ? total : null;
    }

    private static string Format(int value)
    {
        string result = "";
        foreach (var (amount, symbol) in Table)
        {
            while (value >= amount)
            {
                result += symbol;
                value -= amount;
            }
        }

        return result;
    }
}
