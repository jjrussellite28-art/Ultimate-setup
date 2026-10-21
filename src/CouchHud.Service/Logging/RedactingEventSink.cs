using System.Text.RegularExpressions;
using CouchHud.Privileged;

namespace CouchHud.Service.Logging;

/// <summary>
/// The last line before the log: the service never passes arguments, passwords, codes or grants to a sink in the first place; this
/// sink is the backstop. It removes what looks like a grant or a passcode hash, values after secret-sounding names, control
/// characters (no forged log lines) and caps the length.
/// </summary>
public sealed class RedactingEventSink(IEventSink inner) : IEventSink
{
    public const int MaxLength = 1024;
    public const string Mask = "***";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex PasscodeHash = new(@"pbkdf2-sha256\$[^\s""',;]*", Options);
    // 32 bytes in base64 (a grant) and anything longer in base64.
    private static readonly Regex LongBase64 = new(@"[A-Za-z0-9+/]{40,}={0,2}", Options);
    private static readonly Regex NamedSecret = new(@"(?i)(password|passwort|kennwort|secret|grant|passcode|code|sequence|hash|token)(""?\s*[:=]\s*)(""[^""]*""|[^\s,;}]+)", Options);

    private readonly IEventSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Info(string message) => _inner.Info(Redact(message));

    public void Error(string message) => _inner.Error(Redact(message));

    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var text = message.Length > MaxLength * 4 ? message[..(MaxLength * 4)] : message;
        text = PasscodeHash.Replace(text, Mask);
        text = NamedSecret.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        text = LongBase64.Replace(text, Mask);
        var chars = text.Select(c => char.IsControl(c) || char.IsSurrogate(c) ? '?' : c).ToArray();
        var clean = new string(chars);
        return clean.Length <= MaxLength ? clean : clean[..MaxLength] + "…";
    }
}
