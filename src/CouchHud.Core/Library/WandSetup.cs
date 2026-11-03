namespace CouchHud.Core.Library;

/// <summary>
/// Wand (formerly WeMod) is a normal app now, started next to a game as a companion app. The Wand plugin is retired; a household that still
/// has it gets one hint (never removed silently) and an offer to set Wand up, when Wand.exe is found at one of its usual places.
/// </summary>
public static class WandSetup
{
    public const string PluginId = "wand";

    public const string AppName = "Wand";

    /// <summary>The places the Wand plugin looked at (Squirrel puts a launcher stub in the root folder).</summary>
    public static IEnumerable<string> Candidates(string localAppData) =>
        string.IsNullOrWhiteSpace(localAppData)
            ?
            []
            :
            [
                Path.Combine(localAppData, "Wand", "Wand.exe"),
                Path.Combine(localAppData, "WeMod", "WeMod.exe"),
                Path.Combine(localAppData, "Programs", "Wand", "Wand.exe")
            ];

    /// <summary>Wand.exe at a usual place, or null. The page never names a path: the host looks here and nowhere else.</summary>
    public static string? FindExe(string localAppData, Func<string, bool> fileExists) =>
        Candidates(localAppData).FirstOrDefault(fileExists);

    /// <summary>
    /// The publisher names a Wand or WeMod signature may carry, compared exactly (organisation O or common name CN, ignoring case), nothing
    /// else: no substring, no other field. Kevin: Herausgeber in der VM mit Get-AuthenticodeSignature prüfen (public scanner reports show
    /// "WeMod LLC"; the current name of Wand's signer is not known for sure). One list, here, and no thumbprint (the certificate rotates).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownPublishers = ["WeMod LLC", "WeMod, LLC", "WeMod Inc.", "WeMod, Inc."];

    /// <summary>The signer's O or CN is exactly one of <see cref="KnownPublishers"/>. Anything else (OU, a longer name, a lookalike) is "no".</summary>
    public static bool SignerMatches(System.Security.Cryptography.X509Certificates.X500DistinguishedName? name)
    {
        if (name is null) return false;
        try
        {
            foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
            {
                if (rdn.GetSingleElementType().Value is not ("2.5.4.10" or "2.5.4.3")) continue;
                var value = rdn.GetSingleElementValue();
                if (value is not null && KnownPublishers.Contains(value, StringComparer.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // A multi-valued part or a broken name: not a publisher we know.
        }
        return false;
    }

    /// <summary>The same for a subject written as text ("CN=WeMod LLC, O=WeMod LLC, C=US").</summary>
    public static bool SignerMatches(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        try
        {
            return SignerMatches(new System.Security.Cryptography.X509Certificates.X500DistinguishedName(subject));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The hint comes once, and only to a household that has the plugin.</summary>
    public static bool HintDue(bool alreadyShown, bool pluginInstalled) => !alreadyShown && pluginInstalled;
}
