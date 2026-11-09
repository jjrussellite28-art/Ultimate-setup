using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MegaHub.Core.Integrations.Tv;

/// <summary>
/// Does a piece of text that goes over a plain port carry the pairing key? A best-effort safety net against plugin bugs, not a barrier:
/// a plugin is not sandboxed and can hide a key in ways no check finds. It looks at the text as is and after decoding URL escapes
/// (repeatedly), JSON unicode escapes and XML numeric references, for the key itself, its base64 (also URL-safe, at any alignment) and
/// its hex form. A key under 12 characters only counts as a whole token (not inside a longer run of letters or digits).
/// </summary>
public static partial class KeyGuard
{
    public const int MinSubstringKeyLength = 12;

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})",RegexOptions.CultureInvariant)]
    private static partial Regex JsonUnicode();

    [GeneratedRegex(@"&#(?:[xX]([0-9a-fA-F]{1,6})|([0-9]{1,7}));", RegexOptions.CultureInvariant)]
    private static partial Regex XmlNumeric();

    public static bool Carries(string text, string key)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = text;
        for (var pass = 0; pass < 6 && seen.Add(current); pass++)
        {
            if (Matches(current, key)) return true;
            current = Decode(current);
        }
        return false;
    }

    private static bool Matches(string text, string key)
    {
        if (key.Length >= MinSubstringKeyLength ? text.Contains(key, StringComparison.OrdinalIgnoreCase) : ContainsToken(text, key)) return true;
        var bytes = Encoding.UTF8.GetBytes(key);
        if (text.Contains(Convert.ToHexString(bytes), StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var form in Base64Forms(bytes))
        {
            if (text.Contains(form, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The key delimited by anything but a letter or digit (or by the ends), compared without regard to case.</summary>
    private static bool ContainsToken(string text, string key)
    {
        for (var at = text.IndexOf(key, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var end = at + key.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>The characters of the key's base64 that do not depend on the neighbouring bytes, for each of the three alignments.</summary>
    private static IEnumerable<string> Base64Forms(byte[] key)
    {
        for (var pad = 0; pad < 3; pad++)
        {
            var data = new byte[pad + key.Length];
            key.CopyTo(data, pad);
            var all = Convert.ToBase64String(data);
            var skip = pad switch { 0 => 0, 1 => 2, _ => 3 };
            var take = data.Length / 3 * 4 - skip;
            if (take < 8) continue;
            var core = all.Substring(skip, take);
            yield return core;
            yield return core.Replace('+', '-').Replace('/', '_');
        }
    }

    private static string Decode(string text)
    {
        var s = text;
        try { s = Uri.UnescapeDataString(s); }
        catch (UriFormatException) { /* leave as is */ }
        s = JsonUnicode().Replace(s, m =>
        {
            var c = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return c is >= 0xD800 and <= 0xDFFF ? m.Value : char.ConvertFromUtf32(c);
        });
        s = XmlNumeric().Replace(s, m =>
        {
            var ok = m.Groups[1].Success
                ? int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
                : int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out cp);
            return ok && cp is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(cp) : m.Value;
        });
        return s;
    }
}
