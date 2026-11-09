using System.Text;
using System.Text.RegularExpressions;

namespace MegaHub.Core.Skins;

/// <summary>
/// A skin is a stylesheet from somewhere. The page may load pictures from the web (artwork, news photos), so a skin could
/// call a stranger's server with every background it uses and tell that server when someone is on the couch. Where
/// local.megahub serves a skin's CSS, external addresses are replaced; the skin's own files and inline data stay.
/// <para>
/// A skin may not use <c>@import</c> (a second stylesheet nobody checked, with <c>layer()</c> and <c>supports()</c>) and may not use <c>@layer</c> (a layer
/// declared first beats the important rules of everything after it, so a skin could take the core's pins apart). Both are removed, reading the text the way
/// CSS Syntax Level 3 tokenizes it: comments, strings (which end at a newline, a carriage return or a form feed), unquoted <c>url( ... )</c> tokens (which
/// run to the closing bracket and ignore comment and quote characters inside), numbers with units, escapes. A <c>@layer</c> block is unwrapped into ordinary
/// rules (dropping it would silently lose rules of an honest skin). Then a backstop: whatever is left is searched, escapes resolved, anywhere (strings,
/// comments, url(), anything) for "@layer" or "@import"; if one is there the whole stylesheet is refused (an honest skin has no reason to contain those
/// words, so a false positive is acceptable). The text is read as UTF-8 only: the host sends charset=utf-8, an @charset naming another charset refuses the
/// stylesheet, and so do control characters (ESC, NUL ...) that another charset could use to hide text. The same code exists in scripts/skin-css.js; both read tests/skin-css-cases.json.
/// </para>
/// </summary>
public static partial class SkinCss
{
    public const string Blank = "about:blank";

    // An absolute or protocol-relative web address, not preceded by a letter or digit (so "a//b" in a text stays). Allowed:
    // the two virtual hosts of the app and the XML namespace of inline SVG (data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg'>).
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])(?:https?:)?//(?!(?:local|art|app)\.megahub(?:[/:""') \t\n\r\f]|$)|www\.w3\.org/)[^ \t\n\r\f""')]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex External();

    /// <summary>The stylesheet as the host serves it; "" when it is refused (see <see cref="Sanitize(string, out bool)"/>).</summary>
    public static string Sanitize(string css) => Sanitize(css, out _);

    /// <param name="css">The skin's stylesheet.</param>
    /// <param name="refused">True when the result is empty because the stylesheet still mentioned @layer or @import after they were removed, or stalled the matcher.</param>
    public static string Sanitize(string css, out bool refused)
    {
        refused = false;
        try
        {
            // A control character other than tab, LF, FF and CR: in a stylesheet decoded from another charset (iso-2022-jp: ESC ( B produces no character at all)
            // it would hide "@layer" or "https://" from this scan while the browser reads it. An honest skin has none.
            if (HasForbiddenControl(css))
            {
                refused = true;
                return "";
            }
            // The text that is served is the text that was scanned: the address replacement can swallow a backslash, a quote or a comment mark and so
            // change what is a string or a comment, so strip and scan run again on its result until it changes nothing (a few rounds at most, else refused).
            var text = css;
            for (var round = 0; round < 6; round++)
            {
                var clean = StripLayersAndImports(text, out var badCharset);
                if (badCharset || MentionsLayerOrImport(clean))
                {
                    refused = true;
                    return "";
                }
                var replaced = External().Replace(clean, Blank);
                if (string.Equals(replaced, clean, StringComparison.Ordinal))
                {
                    // What the replacement leaves is judged as the URL parser reads it (escapes, deleted tabs and line breaks, backslashes): an address it could not see is refused.
                    if (HasBadAddress(clean))
                    {
                        refused = true;
                        return "";
                    }
                    return clean;
                }
                text = replaced;
            }
            refused = true;
            return "";
        }
        catch (RegexMatchTimeoutException)
        {
            // A stylesheet built to stall the matcher is not served.
            refused = true;
            return "";
        }
    }

    /// <summary>
    /// The same for the bytes of the file: a UTF-16 byte order mark or bytes that are not valid UTF-8 refuse the stylesheet (a UTF-8 byte order mark is dropped).
    /// The host also sends "charset=utf-8" in the header, which beats an @charset, so the text read here is the text the browser reads.
    /// </summary>
    public static string Sanitize(byte[] bytes, out bool refused)
    {
        refused = false;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            refused = true;
            return "";
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            refused = true;
            return "";
        }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return Sanitize(text, out refused);
    }

    private static bool HasForbiddenControl(string css)
    {
        foreach (var c in css)
        {
            if (c <= 0x08 || c == 0x0B || (c >= 0x0E && c <= 0x1F)) return true;
        }
        return false;
    }

    private static bool IsNl(char c) => c is '\n' or '\r' or '\f';
    private static bool IsWs(char c) => c is ' ' or '\t' || IsNl(c);
    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsNameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '\0' || c > 127;
    private static bool IsNameChar(char c) => IsNameStart(c) || IsDigit(c) || c == '-';

    /// <summary>The character at an index, or NUL-less "none" (-1 as an int) past the end.</summary>
    private static int At(string s, int i) => i >= 0 && i < s.Length ? s[i] : -1;

    private static bool ValidEscape(string css, int i) => i < css.Length && css[i] == '\\' && i + 1 < css.Length && !IsNl(css[i + 1]);

    /// <summary>The index after an escape whose backslash is at i - 1 (i is the first character after it).</summary>
    private static int EscapeEnd(string css, int i)
    {
        if (i >= css.Length) return i;
        var j = i;
        var d = 0;
        while (j < css.Length && d < 6 && IsHex(css[j])) { j++; d++; }
        if (d > 0)
        {
            if (j < css.Length && IsWs(css[j])) { if (css[j] == '\r' && At(css, j + 1) == '\n') j++; j++; }
            return j;
        }
        return i + 1;
    }

    private static bool StartsIdent(string css, int i)
    {
        if (i >= css.Length) return false;
        var c = css[i];
        if (c == '-')
        {
            var n = At(css, i + 1);
            return n == '-' || (n >= 0 && IsNameStart((char)n)) || ValidEscape(css, i + 1);
        }
        return IsNameStart(c) || ValidEscape(css, i);
    }

    private static int IdentEnd(string css, int i)
    {
        var j = i;
        while (j < css.Length)
        {
            if (IsNameChar(css[j])) j++;
            else if (ValidEscape(css, j)) j = EscapeEnd(css, j + 1);
            else break;
        }
        return j;
    }

    private static string DecodeIdent(string css, int start, int end)
    {
        var sb = new StringBuilder();
        var i = start;
        while (i < end)
        {
            if (css[i] == '\\' && i + 1 < end)
            {
                var j = i + 1;
                var hex = 0;
                var digits = 0;
                while (j < end && digits < 6 && IsHex(css[j])) { hex = hex * 16 + Convert.ToInt32(css[j].ToString(), 16); j++; digits++; }
                if (digits > 0)
                {
                    sb.Append(hex is > 0 and <= 0x10FFFF && (hex < 0xD800 || hex > 0xDFFF) ? char.ConvertFromUtf32(hex) : "�");
                    if (j < end && IsWs(css[j])) { if (css[j] == '\r' && At(css, j + 1) == '\n') j++; j++; }
                }
                else { sb.Append(css[i + 1]); j = i + 2; }
                i = j;
            }
            else { sb.Append(css[i]); i++; }
        }
        return sb.ToString();
    }

    /// <summary>The index after a string that starts at i (the quote). A newline, carriage return or form feed ends it (a bad string); that character is not part of it.</summary>
    private static int SkipString(string css, int i)
    {
        var q = css[i];
        var j = i + 1;
        while (j < css.Length)
        {
            var c = css[j];
            if (c == q) return j + 1;
            if (IsNl(c)) return j;
            if (c == '\\')
            {
                if (j + 1 >= css.Length) return css.Length;
                if (IsNl(css[j + 1])) j += css[j + 1] == '\r' && At(css, j + 2) == '\n' ? 3 : 2;
                else j = EscapeEnd(css, j + 1);
            }
            else j++;
        }
        return css.Length;
    }

    /// <summary>The rest of a bad url: to the closing bracket (escapes honoured) or the end.</summary>
    private static int BadUrlRest(string css, int i)
    {
        var j = i;
        while (j < css.Length)
        {
            if (css[j] == ')') return j + 1;
            if (ValidEscape(css, j)) j = EscapeEnd(css, j + 1);
            else j++;
        }
        return css.Length;
    }

    /// <summary>The index after an unquoted url token whose body starts at i (after "url(" and the white space).</summary>
    private static int SkipUrlBody(string css, int i)
    {
        var j = i;
        while (j < css.Length)
        {
            var c = css[j];
            if (c == ')') return j + 1;
            if (IsWs(c))
            {
                while (j < css.Length && IsWs(css[j])) j++;
                if (j >= css.Length) return j;
                return css[j] == ')' ? j + 1 : BadUrlRest(css, j);
            }
            if (c is '"' or '\'' or '(' || c <= 8 || c == 0x0b || (c >= 0x0e && c <= 0x1f) || c == 0x7f) return BadUrlRest(css, j + 1);
            if (c == '\\')
            {
                if (!ValidEscape(css, j)) return BadUrlRest(css, j + 1);
                j = EscapeEnd(css, j + 1);
            }
            else j++;
        }
        return css.Length;
    }

    /// <summary>One lexical unit that is not structure: a comment, a string, an identifier (with a url token when it is url( ), a number with its unit, an escape, or one character. Returns the end.</summary>
    private static int AtomEnd(string css, int i)
    {
        var c = css[i];
        if (c == '/' && At(css, i + 1) == '*')
        {
            var e = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return e < 0 ? css.Length : e + 2;
        }
        if (c is '"' or '\'') return SkipString(css, i);
        if (StartsIdent(css, i))
        {
            var e = IdentEnd(css, i);
            if (At(css, e) == '(' && DecodeIdent(css, i, e).Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                var j = e + 1;
                while (j < css.Length && IsWs(css[j])) j++;
                if (At(css, j) is '"' or '\'') return e + 1; // url( "...": an ordinary function with a string
                return SkipUrlBody(css, j);
            }
            return e;
        }
        var n1 = At(css, i + 1);
        var n2 = At(css, i + 2);
        if (IsDigit(c) || ((c == '+' || c == '-') && ((n1 >= 0 && IsDigit((char)n1)) || (n1 == '.' && n2 >= 0 && IsDigit((char)n2)))) || (c == '.' && n1 >= 0 && IsDigit((char)n1)))
        {
            var j = i;
            if (css[j] == '+' || css[j] == '-') j++;
            while (j < css.Length && IsDigit(css[j])) j++;
            if (At(css, j) == '.' && At(css, j + 1) >= 0 && IsDigit((char)At(css, j + 1))) { j++; while (j < css.Length && IsDigit(css[j])) j++; }
            var ex = At(css, j);
            var e1 = At(css, j + 1);
            var e2 = At(css, j + 2);
            if ((ex == 'e' || ex == 'E') && ((e1 >= 0 && IsDigit((char)e1)) || ((e1 == '+' || e1 == '-') && e2 >= 0 && IsDigit((char)e2)))) { j += 2; while (j < css.Length && IsDigit(css[j])) j++; }
            if (StartsIdent(css, j)) j = IdentEnd(css, j);
            else if (At(css, j) == '%') j++;
            return j;
        }
        if (c == '\\') return ValidEscape(css, i) ? EscapeEnd(css, i + 1) : i + 1;
        return i + 1;
    }

    /// <summary>Skips an at-rule's prelude the way CSS does: to the semicolon (consumed) or to the opening brace (consumed, reported); brackets and nested blocks are balanced.</summary>
    private static (int End, bool Block) SkipStatement(string css, int start)
    {
        var closers = new Stack<char>();
        var i = start;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '(') { closers.Push(')'); i++; }
            else if (c == '[') { closers.Push(']'); i++; }
            else if (c == '{')
            {
                if (closers.Count == 0) return (i + 1, true);
                closers.Push('}');
                i++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (closers.Count > 0 && closers.Peek() == c) closers.Pop();
                else if (closers.Count == 0 && c == '}') return (i, false);
                i++;
            }
            else if (c == ';' && closers.Count == 0) return (i + 1, false);
            else i = AtomEnd(css, i);
        }
        return (css.Length, false);
    }

    /// <summary>@import statements dropped, @layer statements dropped, @layer blocks unwrapped into ordinary rules.</summary>
    internal static string StripLayersAndImports(string css) => StripLayersAndImports(css, out _);

    /// <summary>The same; <paramref name="badCharset"/> is true when an @charset named anything but utf-8 (the rule itself is dropped either way: the host forces UTF-8).</summary>
    internal static string StripLayersAndImports(string css, out bool badCharset)
    {
        badCharset = false;
        var sb = new StringBuilder(css.Length);
        var blocks = new Stack<bool>();
        var i = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '@' && StartsIdent(css, i + 1))
            {
                var e = IdentEnd(css, i + 1);
                var name = DecodeIdent(css, i + 1, e).ToLowerInvariant();
                if (name == "import") i = SkipStatement(css, e).End;
                else if (name == "charset")
                {
                    var st = SkipStatement(css, e);
                    if (!css[e..st.End].TrimEnd(';').Trim().Equals("\"utf-8\"", StringComparison.OrdinalIgnoreCase)) badCharset = true;
                    i = st.End;
                }
                else if (name == "layer")
                {
                    var s = SkipStatement(css, e);
                    if (s.Block) blocks.Push(true);
                    i = s.End;
                }
                else { sb.Append(css, i, e - i); i = e; }
            }
            else if (c == '{') { blocks.Push(false); sb.Append(c); i++; }
            else if (c == '}')
            {
                if (blocks.Count > 0 && blocks.Pop()) { i++; continue; }
                sb.Append(c);
                i++;
            }
            else { var e = AtomEnd(css, i); sb.Append(css, i, e - i); i = e; }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The text of a url or a string the way a URL parser reads it: CSS escapes decoded (a backslash and a line break is a continuation and vanishes), then TAB, LF and CR
    /// deleted (the URL parser does that), a backslash made a slash (it does that for web addresses), leading control characters and spaces cut.
    /// </summary>
    internal static string AddressText(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\') { sb.Append(c); continue; }
            if (i + 1 >= s.Length) break;
            var n = s[i + 1];
            if (IsNl(n)) { if (n == '\r' && At(s, i + 2) == '\n') i++; i++; continue; }
            if (IsHex(n))
            {
                var j = i + 1;
                var digits = 0;
                var code = 0;
                while (j < s.Length && digits < 6 && IsHex(s[j])) { code = code * 16 + Convert.ToInt32(s[j].ToString(), 16); j++; digits++; }
                if (code > 0 && code <= 0x10ffff && (code < 0xd800 || code > 0xdfff)) sb.Append(char.ConvertFromUtf32(code)); else sb.Append('�');
                if (j < s.Length && IsWs(s[j])) { if (s[j] == '\r' && At(s, j + 1) == '\n') j++; j++; }
                i = j - 1;
            }
            else { sb.Append(n); i++; }
        }
        var t = IgnoredInAddress().Replace(sb.ToString(), "").Replace('\\', '/');
        var start = 0;
        while (start < t.Length && t[start] <= ' ') start++;
        return t[start..];
    }

    // TAB, LF and CR (the URL parser deletes them) and the code points the host name mapping (IDNA) drops or the text hides: they are deleted before an address is judged.
    [GeneratedRegex("[\t\n\r\u00AD\u034F\u180B-\u180D\u200B-\u200D\u2060\uFE00-\uFE0F\uFEFF]", RegexOptions.CultureInvariant, 200)]
    private static partial Regex IgnoredInAddress();

    // The two virtual hosts of the app and the art host may be written out (the same exceptions as External).
    [GeneratedRegex(@"^(?:https?:)?//(?:local|art|app)\.megahub(?::[0-9]+)?(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex LocalAddress();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*):", RegexOptions.CultureInvariant, 200)]
    private static partial Regex SchemeStart();

    // Schemes that reach out (or run code); a string that is no fetch (an attribute value like "user:nova", "a: b") may use any other scheme-like start, but not one of these.
    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.Ordinal)
    {
        "http", "https", "ftp", "ftps", "ws", "wss", "file", "blob", "filesystem", "javascript", "vbscript", "view-source", "chrome", "chrome-extension", "edge", "ms-appx", "ms-appx-web"
    };

    /// <summary>
    /// Whether a url token or string is an address that leaves the app, judged as the URL parser would read it (after <see cref="AddressText"/>), whether or not it contains a literal "//":
    /// it starts with "//", or with a scheme other than data: and about:blank (in a place that fetches: any scheme; elsewhere only a scheme of NetworkSchemes or one followed by a slash,
    /// so "a: b" in a content string and "user:nova" in an attribute selector pass). The app's own hosts, written out, pass.
    /// </summary>
    internal static bool BadAddress(string raw, bool fetching)
    {
        var t = AddressText(raw);
        // "//" and more slashes before a host; "// " (a decorative string) or "//" alone has no host.
        if (t.StartsWith("//", StringComparison.Ordinal))
        {
            var rest = t.TrimStart('/');
            return rest.Length > 0 && rest[0] != ' ' && !LocalAddress().IsMatch(t);
        }
        var m = SchemeStart().Match(t);
        if (!m.Success) return false;
        var scheme = m.Groups[1].Value.ToLowerInvariant();
        if (scheme == "data") return false;
        if (scheme == "about") return !t.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase);
        if (LocalAddress().IsMatch(t)) return false;
        return fetching || NetworkSchemes.Contains(scheme) || (m.Length < t.Length && t[m.Length] == '/');
    }

    // Functions whose arguments (url tokens, strings) are fetched.
    private static readonly HashSet<string> UrlishFunctions = new(StringComparer.Ordinal)
    {
        "url", "src", "image", "image-set", "-webkit-image-set", "cross-fade", "-webkit-cross-fade", "element"
    };

    /// <summary>
    /// Whether any url token or any string in the sheet (custom properties included: var() carries a string into a fetch) is a bad address (<see cref="BadAddress"/>).
    /// Fail closed: the whole stylesheet is refused; an honest skin writes its paths plainly.
    /// </summary>
    internal static bool HasBadAddress(string css)
    {
        var stack = new Stack<bool>();
        var custom = false; // inside the value of a custom property (var() can carry its strings into a fetch): strings are judged as strictly as a url
        var braces = 0; // { } nested inside that value
        var last = '\0'; // the last character that was not white space or a comment
        var i = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c is '"' or '\'')
            {
                var e = SkipString(css, i);
                var body = css.Substring(i + 1, e - i - 1);
                if (body.EndsWith(c)) body = body[..^1];
                if (BadAddress(body, custom || (stack.Count > 0 && stack.Peek()))) return true;
                i = e;
                last = c;
            }
            else if (StartsIdent(css, i))
            {
                var e = IdentEnd(css, i);
                var name = DecodeIdent(css, i, e).ToLowerInvariant();
                if (At(css, e) == '(')
                {
                    if (name == "attr") return true; // attr() can hand an attribute's text to url() or image-set()
                    var j = e + 1;
                    while (j < css.Length && IsWs(css[j])) j++;
                    if (name == "url" && At(css, j) != '"' && At(css, j) != '\'')
                    {
                        var end = SkipUrlBody(css, j);
                        if (BadAddress(TrailingParen().Replace(css.Substring(j, end - j), ""), true)) return true;
                        i = end;
                        last = ')';
                    }
                    else { stack.Push(UrlishFunctions.Contains(name)); i = e + 1; last = '('; }
                }
                else
                {
                    if (name.StartsWith("--", StringComparison.Ordinal) && stack.Count == 0 && last is '\0' or '{' or ';' or '}')
                    {
                        var j = e;
                        while (j < css.Length && IsWs(css[j])) j++;
                        if (At(css, j) == ':') custom = true;
                    }
                    i = e;
                    last = 'a';
                }
            }
            else if (c == '(') { stack.Push(false); i++; last = c; }
            else if (c == ')') { if (stack.Count > 0) stack.Pop(); i++; last = c; }
            else if (c == '{') { if (custom) braces++; i++; last = c; }
            else if (c == '}') { if (custom) { if (braces > 0) braces--; else custom = false; } i++; last = c; }
            else if (c == ';') { if (custom && braces == 0 && stack.Count == 0) custom = false; i++; last = c; }
            else
            {
                var e = AtomEnd(css, i);
                if (!(c == '/' && At(css, i + 1) == '*') && !IsWs(c)) last = c;
                i = e;
            }
        }
        return false;
    }

    [GeneratedRegex(@"\s*\)?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex TrailingParen();

    /// <summary>
    /// Whether an at-keyword token "@layer" or "@import" (upper or lower case, escapes in the name resolved) is anywhere in the text, in any nesting.
    /// The text is read again token by token, the way <see cref="Strip"/> reads it: what is inside a comment, a string, an unquoted url( ) token or an
    /// identifier (a selector like .a\@layer, an attribute value, a custom property's string) is not an at-keyword to any parser and does not count.
    /// Text that two removed statements joined into "@layer" is a token now and is found.
    /// </summary>
    internal static bool MentionsLayerOrImport(string css)
    {
        var i = 0;
        while (i < css.Length)
        {
            if (css[i] == '@' && StartsIdent(css, i + 1))
            {
                var e = IdentEnd(css, i + 1);
                var name = DecodeIdent(css, i + 1, e).ToLowerInvariant();
                if (name is "layer" or "import") return true;
                i = e;
            }
            else i = AtomEnd(css, i);
        }
        return false;
    }
}
