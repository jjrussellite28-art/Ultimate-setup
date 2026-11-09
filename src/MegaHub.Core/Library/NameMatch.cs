using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MegaHub.Core.Library;

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

    /// <summary>Latin letters that no decomposition splits into a base letter.</summary>
    private static readonly Dictionary<char, string> Letters = new()
    {
        ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe", ['ø'] = "o", ['ł'] = "l", ['đ'] = "d", ['þ'] = "th", ['ð'] = "d", ['ı'] = "i"
    };

    /// <summary>
    /// The name reduced to what makes the game: see the class. Empty when the name has letters outside the Latin alphabet (Japanese,
    /// Cyrillic …): the store search answers in English, so such a name is never compared, rather than compared by what is left of it.
    /// </summary>
    public static string Canonical(string name)
    {
        // Marks first (compatibility decomposition turns ™ into "TM"), then KD: "Ⅱ" becomes "II", full-width letters plain ones.
        var s = name.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("℠", " ").Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(s.Length);
        foreach (var raw in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(raw);
            if (Letters.TryGetValue(c, out var latin)) sb.Append(latin);
            else if (char.IsLetter(c) && c is not (>= 'a' and <= 'z')) return "";
            else sb.Append(c);
        }
        // "Assassin's" is "Assassins": an apostrophe joins, it does not split a word.
        s = sb.ToString().Replace("'", "").Replace("’", "").Replace("`", "").Replace("&", " and ");
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

    /// <summary>
    /// The same game: equal canonical names with at least one letter (a bare "2" says nothing), and not two different years in brackets
    /// ("Doom (1993)" is not "Doom (2016)"; one year against none still matches).
    /// </summary>
    public static bool Same(string a, string b)
    {
        var x = Canonical(a);
        if (x.Length == 0 || !x.Any(char.IsAsciiLetter) || x != Canonical(b)) return false;
        return YearOf(a) is not { } ya || YearOf(b) is not { } yb || ya == yb;
    }

    /// <summary>The very same name, only case, trademark signs and spacing apart: decides between several entries that are <see cref="Same"/>.</summary>
    public static bool Exact(string a, string b)
    {
        static string Plain(string s) => string.Join(' ', s.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("℠", " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Plain(a).Length > 0 && Plain(a) == Plain(b);
    }

    /// <summary>
    /// Shorter searches to try when the store finds nothing for <paramref name="query"/>: the store only returns entries that contain every word
    /// ("Mortal Kombat 1 Premium Edition" finds nothing although "Mortal Kombat 1" is there). Trademark signs and punctuation go first, then
    /// edition words at the end, then one trailing word at a time (never below two words), at most <paramref name="max"/> searches. A single word
    /// is searched only when edition words were peeled off ("Doom Deluxe Edition" gives "Doom"), and never a bare number or roman numeral
    /// ("2 Deluxe Edition" gives nothing): that would find unrelated games.
    /// </summary>
    public static IReadOnlyList<string> SearchFallbacks(string query, int max = 3)
    {
        var words = query.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("℠", " ")
            .Split([' ', '	', ':', ';', ',', '(', ')', '[', ']', '-', '–', '—', '/', '|'], StringSplitOptions.RemoveEmptyEntries).ToList();
        var result = new List<string>();
        var peeled = false;
        void Add(List<string> w)
        {
            var text = string.Join(' ', w);
            if (w.Count == 1 && (!peeled || w[0].All(char.IsAsciiDigit) || w[0].All(c => "ivxlcdm".Contains(char.ToLowerInvariant(c))))) return;
            if (w.Count > 0 && result.Count < max && !text.Equals(query, StringComparison.OrdinalIgnoreCase)
                && !result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
        }
        Add(words);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var suffix in EditionSuffixes)
            {
                var parts = suffix.Split(' ');
                if (words.Count > parts.Length && words.Skip(words.Count - parts.Length).Select(x => x.Replace("'", "").Replace("’", "").ToLowerInvariant()).SequenceEqual(parts))
                {
                    words.RemoveRange(words.Count - parts.Length, parts.Length);
                    changed = true;
                    peeled = true;
                    break;
                }
            }
        }
        Add(words);
        while (words.Count > 2 && result.Count < max)
        {
            words.RemoveAt(words.Count - 1);
            Add(words);
        }
        return result;
    }

    /// <summary>Below this <see cref="Confidence"/> a search hit is not taken automatically: no artwork is better than a wrong one.</summary>
    public const double Threshold = 0.8;

    /// <summary>
    /// How sure it is that a search hit is the game: 1 for <see cref="Same"/>, 0.9 for the same letters with other spacing ("HalfLife"),
    /// 0.8 for a typo (one wrong letter per 8, at most 2) with the very same numbers and number of words, else 0. A hit that only starts
    /// like the name ("Mortal Kombat Arcade Kollection" for "Mortal Kombat 1"), another number ("Doom 2" for "Doom 3") or another year in
    /// brackets is 0: sequels and remakes are different games.
    /// </summary>
    public static double Confidence(string name, string hit)
    {
        if (Same(name, hit)) return 1;
        var a = Canonical(name);
        var b = Canonical(hit);
        if (a.Length == 0 || b.Length == 0 || !a.Any(char.IsAsciiLetter)) return 0;
        if (YearOf(name) is { } ya && YearOf(hit) is { } yb && ya != yb) return 0;
        if (a.Replace(" ", "") == b.Replace(" ", "")) return 0.9;
        var ta = a.Split(' ');
        var tb = b.Split(' ');
        if (ta.Length != tb.Length) return 0;
        if (!ta.Where(IsNumber).SequenceEqual(tb.Where(IsNumber))) return 0;
        // Only a trailing "s" apart ("Shadow Warrior" / "Shadow Warriors") is another title, not a typo.
        if (ta.Select(WithoutPlural).SequenceEqual(tb.Select(WithoutPlural))) return 0;
        var allowed = Math.Min(2, Math.Min(a.Length, b.Length) / 8);
        return allowed > 0 && Distance(a, b, allowed) <= allowed ? 0.8 : 0;
    }

    /// <summary>
    /// The hit that is the game, or null: the most certain one at or above <see cref="Threshold"/>; of several equally certain ones the one with the
    /// very name (<see cref="Exact"/>), else the first (the search's own order).
    /// </summary>
    public static T? Best<T>(string name, IEnumerable<T> hits, Func<T, string> nameOf) where T : class
    {
        T? best = null;
        var bestScore = 0.0;
        var bestExact = false;
        var sure = 0;
        foreach (var hit in hits)
        {
            var score = Confidence(name, nameOf(hit));
            if (score < Threshold) continue;
            sure++;
            var exact = Exact(name, nameOf(hit));
            if (best is null || score > bestScore || score == bestScore && exact && !bestExact)
            {
                best = hit;
                bestScore = score;
                bestExact = exact;
            }
        }
        // A typo hit (0.8) is taken only when it is the only candidate: two possible readings of a misspelling are a guess.
        return bestScore < 0.85 && sure > 1 ? null : best;
    }

    private static string WithoutPlural(string token) => token.Length > 3 && token[^1] == 's' ? token[..^1] : token;

    private static bool IsNumber(string token) => token.All(char.IsAsciiDigit);

    /// <summary>Levenshtein distance, given up (limit + 1) as soon as it is over the limit.</summary>
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static string? YearOf(string name) => Year().Match(name) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"\((?:19|20)\d\d\)")]
    private static partial Regex Year();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumeric();
}
