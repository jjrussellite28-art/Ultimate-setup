using System.Text.RegularExpressions;

namespace CouchHud.Core.Remote;

/// <summary>What was found out about the field that has the focus in another program.</summary>
/// <param name="PasswordFlag">The field itself says it hides its input (ES_PASSWORD, UI Automation IsPassword); null if unknown.</param>
/// <param name="Labels">Name, automation id and help text of the field, for fields that do not say so (a "PIN" box).</param>
public sealed record FocusedField(bool? PasswordFlag, IReadOnlyList<string?> Labels);

/// <summary>
/// The phone types over plain HTTP in the LAN, so no secret may go through it: not into a password box of another
/// program (Windows' own sign-in dialog, a launcher login) and not into fields that are named like one.
/// </summary>
public static class SecretFields
{
    private static readonly Regex Named = new(
        // A word of its own ("Password", "Enter your PIN") or the end of a camel-case name ("txtPassword"); not inside another word ("Spinning").
        @"(?:(?<![\p{L}\p{N}])|(?<=(?-i:\p{Ll}))(?=(?-i:\p{Lu})))(pass(word|wort|phrase|wd|code)?|kennwort|pin|pw|secret|geheim\w*|token|api[ _\-]?key|(product|license|lizenz|activation|cd)[ _\-]?key|security[ _\-]?code|cvv|cvc|otp|tan|2fa|authenticator|recovery[ _\-]?key)(?!(?-i:\p{Ll})|\p{N})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Does the label of a field read like a secret ("Password", "Kennwort", "PIN", "API key" …)?</summary>
    public static bool LooksSecret(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 200) return false;
        try
        {
            return Named.IsMatch(label);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static readonly string[] Browsers = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "msedgewebview2"];

    /// <summary>
    /// A field nobody could tell about (no password flag) in a program where logins are the whole point: a game client or launcher
    /// window, a browser, or CouchHUD's own browser. There "unknown" is not good enough, the phone does not type (the TV does).
    /// Games and other programs stay allowed with an unknown field; they rarely expose anything.
    /// </summary>
    public static bool CannotBeVerified(FocusedField field, string? processName, bool ownProcess)
    {
        if (field.PasswordFlag is not null) return false;
        if (ownProcess) return true;
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return Guard.ForegroundPolicy.Classify(name) is { } family && family != "nuisance"
               || Browsers.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True if typing into this field is refused. An unknown field is allowed (games and launchers often expose nothing).</summary>
    public static bool IsSecret(FocusedField field) =>
        field.PasswordFlag == true || field.Labels.Any(LooksSecret);
}
