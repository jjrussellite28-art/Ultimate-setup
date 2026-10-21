namespace CouchHud.Core.Security;

/// <summary>
/// What "Paste from PC" takes from the clipboard for a secret or long setting (a token, a key, an address): text only, trimmed, at most
/// <see cref="MaxLength"/> characters, no control characters inside (a token with a line break in the middle is a copy mistake, not a
/// token). Null when nothing usable is there. The text is never logged.
/// </summary>
public static class ClipboardText
{
    public const int MaxLength = 4096;

    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var text = raw.Trim();
        if (text.Length is 0 or > MaxLength) return null;
        return text.Any(char.IsControl) ? null : text;
    }
}
