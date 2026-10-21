using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchHud.Core.Library;

/// <summary>
/// Decides whether two game names are the same game. Conservative on purpose: a wrong cover is worse
/// than none. Equal after dropping accents, trademarks, a trailing year, edition suffixes ("Game of the
/// Year Edition", "GOTY", "Deluxe Edition", ...) and writing roman numerals II-IX as digits.
/// "Remastered", numbers, and subtitles are kept: those are different products.
/// </summary>
public static partial class NameMatch
{
    private static readonly Dictionary<string, string> Roman = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9"
    };

    // Matched only at the very end of the name.
    private static readonly string[] EditionSuffixes =
    [
        "game of the year edition", "goty edition", "goty", "game of the year",
        "digital deluxe edition", "ultimate edition", "definitive edition", "complete edition",
        "deluxe edition", "enhanced edition", "standard edition", "gold edition", "special edition",
        "collectors edition", "anniversary edition", "premium edition", "legendary edition",
        "digital edition", "director s cut", "directors cut"
    ];

    public static string Canonical(string name)
    {
        var s = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        s = sb.ToString().ToLowerInvariant().Replace("™", "").Replace("®", "").Replace("©", "").Replace("&", " and ");
        s = Year().Replace(s, " ");
        s = NonAlphaNumeric().Replace(s, " ").Trim();

        // "the" in front doesn't make a different game.
        if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..];
        // Edition words can stack ("Deluxe Edition GOTY"): peel until nothing changes.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var suffix in EditionSuffixes)
            {
                if (s.Length > suffix.Length && s.EndsWith(" " + suffix, StringComparison.Ordinal))
                {
                    s = s[..^(suffix.Length + 1)].TrimEnd();
                    changed = true;
                }
            }
        }

        // Not the first token ("II" at the start is rarely a number); "I", "V" and "X" stay as they are.
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < tokens.Length; i++)
        {
            if (Roman.TryGetValue(tokens[i], out var digit)) tokens[i] = digit;
        }
        return string.Join(' ', tokens);
    }

    public static bool Same(string a, string b)
    {
        var x = Canonical(a);
        return x.Length > 0 && x == Canonical(b);
    }

    [GeneratedRegex(@"\((?:19|20)\d\d\)")]
    private static partial Regex Year();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumeric();
}
