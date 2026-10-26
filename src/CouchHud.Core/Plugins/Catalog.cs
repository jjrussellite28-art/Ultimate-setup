using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>Semantic version (major.minor.patch[-pre]); build metadata after '+' is ignored.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Pre) : IComparable<SemVer>
{
    /// <summary>Optional fourth number (1.2.3.4), as in assembly versions; compared right after the patch.</summary>
    public int Revision { get; init; }

    private static readonly Regex Pattern = new(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        var m = Pattern.Match(text?.Trim() ?? "");
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
        var patch = 0;
        if (m.Groups[3].Success && !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
        var revision = 0;
        if (m.Groups[4].Success && !int.TryParse(m.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision)) return false;
        version = new SemVer(major, minor, patch, m.Groups[5].Value) { Revision = revision };
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;
        // A release is newer than any of its pre-releases.
        if (Pre.Length == 0 || other.Pre.Length == 0) return other.Pre.Length.CompareTo(Pre.Length);
        return ComparePre(Pre, other.Pre);
    }

    private static int ComparePre(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var nb = int.TryParse(pb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Revision > 0 ? "." + Revision : "")}{(Pre.Length > 0 ? "-" + Pre : "")}";
}

public static class CatalogDefaults
{
    public const string IndexUrl = "https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json";
}

public enum CatalogKind
{
    Plugin,
    Skin
}

/// <summary>One plugin or skin of a catalog index. <see cref="Problem"/> set means it is listed but cannot be installed.</summary>
public sealed record CatalogEntry(
    string Id,
    string Name,
    string Description,
    string? Author,
    CatalogKind Kind,
    string Version,
    string? MinCouchHud,
    string? DownloadUrl,
    string? Sha256,
    string? Homepage,
    string? IconUrl,
    string Folder,
    string Source,
    string? Problem,
    /// <summary>From a source without a valid signature (only possible for sources the user added).</summary>
    bool Unsigned = false);

/// <summary>Status of a catalog entry relative to what is installed and which CouchHud runs.</summary>
public enum CatalogStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    /// <summary>Needs a newer CouchHud (minCouchHud) or has a problem; cannot be installed.</summary>
    Unavailable
}

public sealed class CatalogException(string message) : Exception(message);

/// <summary>Parses the index.json format described in docs/PLUGINS.md. Broken entries are skipped, a broken index throws.</summary>
public static class CatalogParser
{
    public const int MaxEntries = 500;
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex HashPattern = new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <param name="allowLocal">Developer switch: also accept http://127.0.0.1, localhost and file: URLs.</param>
    /// <param name="language">"de" or "en"; falls back to the other language.</param>
    public static IReadOnlyList<CatalogEntry> Parse(string json, string source, string language = "de", bool allowLocal = false)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new CatalogException(Strings.Get("catalog.invalidJson", ex.Message));
        }

        using (doc)
        {
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array ? e
                : throw new CatalogException(Strings.Get("catalog.noEntries"));

            var result = new List<CatalogEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (result.Count >= MaxEntries) break;
                var entry = ParseEntry(item, source, language, allowLocal);
                if (entry is not null && seen.Add($"{entry.Kind}:{entry.Id}")) result.Add(entry);
            }
            return result;
        }
    }

    private static CatalogEntry? ParseEntry(JsonElement item, string source, string language, bool allowLocal)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = Str(item, "id");
        var name = Str(item, "name");
        CatalogKind? kind = Str(item, "type")?.ToLowerInvariant() switch
        {
            "plugin" => CatalogKind.Plugin,
            "skin" => CatalogKind.Skin,
            _ => null
        };
        var version = Str(item, "version");
        if (id is null || !IdPattern.IsMatch(id) || name is null || kind is null || !SemVer.TryParse(version, out _)) return null;

        var folder = Str(item, "folder") ?? id;
        var min = Str(item, "minCouchHud");
        var url = Str(item, "download");
        var hash = Str(item, "sha256");

        string? problem = null;
        if (!IdPattern.IsMatch(folder)) problem = Strings.Get("catalog.badFolder");
        else if (min is not null && !SemVer.TryParse(min, out _)) problem = Strings.Get("catalog.badMin");
        else if (url is null || !IsAllowedUrl(url, allowLocal)) problem = Strings.Get("catalog.badUrl");
        else if (hash is null || !HashPattern.IsMatch(hash)) problem = Strings.Get("catalog.noChecksum");

        var homepage = Str(item, "homepage");
        var icon = Str(item, "icon");
        return new CatalogEntry(
            id, name, Localized(item, "description", language) ?? "", Str(item, "author"), kind.Value, version!, min,
            problem is null ? url : null, hash?.ToLowerInvariant(),
            homepage is not null && IsAllowedUrl(homepage, false) ? homepage : null,
            icon is not null && IsAllowedUrl(icon, allowLocal) ? icon : null,
            folder, source, problem);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    /// <summary>"description": "text" or { "de": "…", "en": "…" }.</summary>
    private static string? Localized(JsonElement o, string name, string language)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind != JsonValueKind.Object) return null;
        var other = language == "de" ? "en" : "de";
        return Str(v, language) ?? Str(v, other);
    }

    /// <summary>https only. With <paramref name="allowLocal"/> also loopback http and file: (developer switch).</summary>
    public static bool IsAllowedUrl(string url, bool allowLocal)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri.UserInfo.Length == 0;
        if (!allowLocal) return false;
        // file: only on local drives; \\server\share (UNC) would reach out to the network.
        if (uri.Scheme == Uri.UriSchemeFile) return !uri.IsUnc && uri.Host.Length == 0;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}

public static class CatalogRules
{
    /// <summary>The CouchHud version this assembly belongs to (all projects share Directory.Build.props).</summary>
    public static SemVer AppVersion
    {
        get
        {
            var assembly = typeof(CatalogRules).Assembly;
            // The informational version keeps a pre-release tag (0.4.0-beta.1) that the assembly version loses.
            var info = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            if (SemVer.TryParse(info, out var v)) return v;
            return SemVer.TryParse(assembly.GetName().Version?.ToString(3), out v) ? v : default;
        }
    }

    public static bool IsCompatible(CatalogEntry entry, SemVer app) =>
        entry.MinCouchHud is null || (SemVer.TryParse(entry.MinCouchHud, out var min) && min <= app);

    /// <summary>An installed package of unknown version counts as up to date.</summary>
    public static CatalogStatus StatusOf(CatalogEntry entry, bool installed, string? installedVersion, SemVer app)
    {
        if (entry.Problem is not null || !IsCompatible(entry, app))
        {
            return installed ? CatalogStatus.Installed : CatalogStatus.Unavailable;
        }
        if (!installed) return CatalogStatus.NotInstalled;
        return SemVer.TryParse(installedVersion, out var have) && SemVer.TryParse(entry.Version, out var offered) && offered > have
            ? CatalogStatus.UpdateAvailable
            : CatalogStatus.Installed;
    }

    /// <summary>
    /// Updates only come from the source a package was installed from. Bundled and hand-installed plugins
    /// (no record) can only be updated from the default source.
    /// </summary>
    public static string? OriginProblem(CatalogEntry entry, bool installed, string? recordSource)
    {
        if (!installed) return null;
        var origin = string.IsNullOrEmpty(recordSource) ? CatalogDefaults.IndexUrl : recordSource;
        return entry.Source.Equals(origin, StringComparison.OrdinalIgnoreCase) ? null : Strings.Get("catalog.otherSource");
    }

    public static string? Reason(CatalogEntry entry, SemVer app) =>
        entry.Problem ?? (IsCompatible(entry, app) ? null : Strings.Get("catalog.needsVersion", entry.MinCouchHud, app));

    /// <summary>The hash check is mandatory: no expected hash, or a different one, means the file is rejected.</summary>
    public static void VerifySha256(string file, string? expected)
    {
        if (expected is null || !Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$")) throw new CatalogException(Strings.Get("catalog.noChecksumInstall"));
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new CatalogException(Strings.Get("catalog.checksumMismatch"));
    }
}

/// <summary>
/// The index of the default source is signed (ECDSA P-256 over SHA-256, IEEE P1363 signature, Base64 in
/// index.json.sig, over "couchhud-catalog-v1\n" followed by the exact bytes of index.json). The public key is part of the app; the private key
/// never leaves the maintainer's user profile (scripts/catalog-sign.ps1).
/// </summary>
public static class CatalogSignature
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKAXgwgjwj5Rjl37hmf04Cj+/vP8S
        JIKCCqRKrBy7MlNVIntuH2DjOkZMiobyQwlIZ3A20ypGgx77rtHg8vs88Q==
        -----END PUBLIC KEY-----
        """;

    public static string SigUrl(string indexUrl) => indexUrl + ".sig";

    /// <summary>
    /// Domain separation: the one key signs different things, and a signature only counts for the context
    /// it was made for (the signed bytes are the context line followed by the data). A signed catalog index
    /// can therefore never pass as a release checksum file or the other way round.
    /// </summary>
    public const string CatalogContext = "couchhud-catalog-v1\n";
    public const string ReleaseContext = "couchhud-release-v1\n";

    /// <summary>The bytes that are actually signed.</summary>
    public static byte[] WithContext(string context, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(context), .. data];

    /// <param name="format">The catalog uses IEEE P1363; CaseHUD releases use ASN.1 DER.</param>
    public static bool Verify(byte[] data, string? signatureBase64, string? publicKeyPem = null, string context = CatalogContext,
        DSASignatureFormat format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportFromPem(publicKeyPem ?? PublicKeyPem);
            return key.VerifyData(WithContext(context, data), signature, HashAlgorithmName.SHA256, format);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
