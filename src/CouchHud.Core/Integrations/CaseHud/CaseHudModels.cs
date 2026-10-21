using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// Where CaseHUD lives and how its release is recognized. CaseHUD is a separate program (github.com/kewe87/CaseHUD);
/// CouchHUD only talks to its local API v1 and never contains its code.
/// </summary>
public static class CaseHudInfo
{
    /// <summary>Inno Setup AppId of CaseHUD (the uninstall key is "{AppId}_is1"); this is the one place it is written down.</summary>
    public const string AppId = "{6F3A9C2E-5B71-4C0D-9E8A-CA5E0D1A2B30}";

    public const string UninstallKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";
    public const string ExeName = "CaseHUD.exe";
    public const string Repository = "kewe87/CaseHUD";
    public const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string SetupPrefix = "CaseHUD-Setup-";
    public const string ChecksumName = "SHA256SUMS.txt";
    public const int ApiVersion = 1;

    /// <summary>The first version that is offered: the line starts again at 1.0.0 and rises from there. Older tags are no CaseHUD release for CouchHUD.</summary>
    public static readonly Plugins.SemVer MinimumVersion = new(1, 0, 0, "");

    /// <summary>Signature context of CaseHUD releases (the signed bytes are this line followed by the checksum file).</summary>
    public const string ReleaseContext = "casehud-release-v1\n";

    /// <summary>
    /// ECDSA P-256 public key (PEM) that signs CaseHUD releases. SHA-256 over its SPKI DER bytes:
    /// <see cref="ReleaseKeyFingerprint"/> (a test pins it, so a typo or a swapped key shows at once).
    /// Empty = no signature required (the SHA-256 is always required); set, the signature is mandatory.
    /// The signature file is base64 in one line, the signature itself ASN.1 DER (not IEEE P1363).
    /// </summary>
    public const string ReleasePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEI0dFQKFrFdKb4r6IRQxD+9Cor8th
        hUgMDlJnn1Y6rUE9bkFsOty4u+JVtFpxfiRIc+tmLG8UzbMNdf5fJU1XCw==
        -----END PUBLIC KEY-----
        """;

    public const string ReleaseKeyFingerprint = "cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf";
}

/// <summary>Content of CaseHUD's api.json. The token never appears in <see cref="ToString"/>.</summary>
public sealed class CaseHudApiInfo
{
    public int ApiVersion { get; init; }
    public string Version { get; init; } = "";
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public int Pid { get; init; }
    public string Exe { get; init; } = "";

    public override string ToString() => $"CaseHUD {Version} on port {Port} (pid {Pid})";
}

/// <summary>Installed CaseHUD as the uninstall entry describes it. Whether it can be controlled is not read off the version, see <see cref="CaseHudCapabilityStore"/>.</summary>
public sealed record CaseHudInstall(string Exe, string Directory, string Version);

/// <summary>
/// CaseHUD's setup puts "CaseHUD.exe --minimized" into the Run key; CouchHUD runs those entries itself as the shell. For that
/// one entry the start becomes <c>--background</c> (plus <c>--no-tray</c> without a taskbar). The entry is rewritten, not skipped:
/// CouchHUD does not start CaseHUD by itself, so skipping would leave it off. A build that was probed and found old stays as
/// it is (it ends on every flag it does not know); one that was not probed yet keeps its entry too: rewriting it blindly would stop an
/// old build from starting at boot. The manager records a running CaseHUD with a valid api.json as capable, so the next boot rewrites it.
/// </summary>
public static class CaseHudAutostart
{
    /// <summary>The adapter for the shell's autostart: looks up the installed CaseHUD once and adapts the entry that starts it.</summary>
    public static Func<string, string> ForShell(Platform.IRegistry registry, bool noTray, Func<string, string>? canonicalPath = null, Func<string, bool>? exists = null, CaseHudCapabilityStore? capabilities = null)
    {
        var install = new CaseHudDiscovery("", _ => null, registry, exists, canonicalPath).FindInstall();
        var capability = install is null ? CaseHudCapability.Unknown : capabilities?.Get(install) ?? CaseHudCapability.Unknown;
        return command => Adapt(command, install, noTray, canonicalPath, capability);
    }

    /// <param name="canonicalPath">Resolves short names and links, so the entry is recognised however its path is written.</param>
    public static string Adapt(string commandLine, CaseHudInstall? install, bool noTray, Func<string, string>? canonicalPath = null, CaseHudCapability capability = CaseHudCapability.Unknown)
    {
        if (install is null || capability != CaseHudCapability.Capable) return commandLine;
        try
        {
            var (file, arguments) = Platform.CommandLine.Split(commandLine);
            string Normal(string path)
            {
                var full = Path.GetFullPath(path);
                return canonicalPath?.Invoke(full) ?? full;
            }
            if (!string.Equals(Normal(file), Normal(install.Exe), StringComparison.OrdinalIgnoreCase)) return commandLine;
            var args = Platform.CommandLine.SplitArguments(arguments).ToList();
            var at = args.FindIndex(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (at < 0) return commandLine;
            args[at] = "--background";
            if (noTray && !args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase)) args.Insert(at + 1, "--no-tray");
            return "\"" + file + "\" " + string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        }
        catch (ArgumentException)
        {
            return commandLine;
        }
    }
}

public sealed record CaseHudPanel(
    string Id, string Name, string Model, int W, int H, bool Connected, string Mode, string Design,
    int Brightness, int Rotation, int FanNo, string Error = "", string Theme = "dark");

public sealed record CaseHudSensors(bool Ok, string Error = "");
public sealed record CaseHudGaming(bool Active, string Source = "", string Game = "");
public sealed record CaseHudRotation(bool Enabled, string Scene = "");
public sealed record CaseHudAwtrixDevice(string Name, string Host, string Model, bool Ok);
public sealed record CaseHudAwtrix(bool Enabled, IReadOnlyList<CaseHudAwtrixDevice>? Devices = null);

public sealed record CaseHudStatus(
    string Version, int ApiVersion, CaseHudSensors Sensors, bool Power, CaseHudGaming Gaming,
    CaseHudRotation Rotation, CaseHudAwtrix Awtrix, IReadOnlyList<CaseHudPanel> Panels, string UpdateAvailable = "",
    // CaseHUD 1.0: missing in older builds, so null means "unknown" (not "refused"); see CaseHudLighting.NeedsDisclaimer / Supported.
    bool? DisclaimerAccepted = null, CaseHudLightingSummary? Lighting = null);

public sealed record CaseHudDesign(string Id, string Name, string Set, string Theme, bool Builtin, int W, int H);
public sealed record CaseHudSet(string Set, string Theme, string Name);
public sealed record CaseHudScene(string Id, string Name, bool Active);
public sealed record CaseHudCommunityDesign(string Id, string Name, string Author, string Set, string Theme);

/// <summary>Fields of a panel patch; null = leave unchanged.</summary>
public sealed record CaseHudPanelPatch(string? Design = null, string? Mode = null, int? Brightness = null, int? Rotation = null, string? Theme = null)
{
    public static CaseHudPanelPatch FromJson(JsonElement e)
    {
        string? Str(string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Num(string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        return new CaseHudPanelPatch(Str("design"), Str("mode"), Num("brightness"), Num("rotation"), Str("theme"));
    }
}

/// <summary>API error: HTTP status plus the code and text of <c>{"error":{"code","message"}}</c>. Code "unreachable" = no answer at all.</summary>
public sealed class CaseHudException(string code, string message, int status = 0) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

internal static class CaseHudJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // CaseHUD rejects unknown fields and wants only what is meant: no "game":null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
