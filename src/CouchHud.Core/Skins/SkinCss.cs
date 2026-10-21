using System.Text.RegularExpressions;

namespace CouchHud.Core.Skins;

/// <summary>
/// A skin is a stylesheet from somewhere. The page may load pictures from the web (artwork, news photos), so a skin could
/// call a stranger's server with every background it uses and tell that server when someone is on the couch. Where
/// local.couchhud serves a skin's CSS, external addresses are replaced; the skin's own files and inline data stay.
/// </summary>
public static partial class SkinCss
{
    public const string Blank = "about:blank";

    // An absolute or protocol-relative web address, not preceded by a letter or digit (so "a//b" in a text stays). Allowed:
    // the two virtual hosts of the app and the XML namespace of inline SVG (data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg'>).
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])(?:https?:)?//(?!(?:local|art|app)\.couchhud(?:[/:""')\s]|$)|www\.w3\.org/)[^\s""')]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex External();

    // @import "x.css" / @import url(x.css): a relative file is fine, the external ones are caught by External().
    public static string Sanitize(string css)
    {
        try
        {
            return External().Replace(css, Blank);
        }
        catch (RegexMatchTimeoutException)
        {
            // A stylesheet built to stall the matcher is not served.
            return "";
        }
    }
}
