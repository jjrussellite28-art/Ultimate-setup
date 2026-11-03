using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The logic behind "set Wand up only for a signature by its publisher": the publisher name compared field by field, the chain to the trust
/// anchors that are handed in (certificates made here, nothing written to any Windows store), the Code Signing usage, the hash that makes a
/// changed file check again, and the package folder rule of the player profiles.
/// </summary>
public sealed class WandSignatureTests : IDisposable
{
    private readonly List<IDisposable> _keep = [];

    public void Dispose()
    {
        foreach (var item in _keep) item.Dispose();
    }

    // ---- the publisher name

    [Theory]
    [InlineData("CN=WeMod LLC, O=WeMod LLC, C=US")]
    [InlineData("O=WeMod LLC")]
    [InlineData("CN=wemod llc")]
    [InlineData("O=\"WeMod, Inc.\", C=US")]
    [InlineData("CN=WeMod Inc., C=US")]
    [InlineData("CN=Somebody, O=WeMod LLC")]
    [InlineData("CN=WeMod LLC, O=Somebody")]
    public void A_known_publisher_in_o_or_cn_matches_exactly(string subject) => Assert.True(WandSetup.SignerMatches(subject));

    [Theory]
    [InlineData("CN=Evil, OU=Wand")]
    [InlineData("CN=Evil, OU=WeMod LLC")]
    [InlineData("O=Wandera")]
    [InlineData("O=Wand")]
    [InlineData("CN=WeMod Fake, O=Evil")]
    [InlineData("CN=WeMod LLC.evil.example, O=Evil")]
    [InlineData("CN=Evil, O=WeMod LLC Holdings")]
    [InlineData("CN=Evil, O=Evil, L=WeMod LLC, S=WeMod LLC")]
    [InlineData("CN=Evil Corp")]
    [InlineData("")]
    [InlineData("not a name")]
    public void Anything_else_does_not_match_not_a_substring_not_another_field(string subject) => Assert.False(WandSetup.SignerMatches(subject));

    [Fact]
    public void No_name_is_no_publisher()
    {
        Assert.False(WandSetup.SignerMatches((string?)null));
        Assert.False(WandSetup.SignerMatches((X500DistinguishedName?)null));
        // The list is in one place, small, and holds no thumbprint.
        Assert.All(WandSetup.KnownPublishers, p => Assert.DoesNotMatch("^[0-9A-Fa-f]{40,}$", p));
        Assert.Contains("WeMod LLC", WandSetup.KnownPublishers);
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CouchHud.Core", "Library", "WandSetup.cs"));
        Assert.Contains("Kevin: Herausgeber in der VM mit Get-AuthenticodeSignature prüfen", source);
    }

    // ---- the chain, with certificates made here

    private X509Certificate2 Root(string name = "CN=Test Root")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1));
        _keep.Add(cert);
        return cert;
    }

    private X509Certificate2 Issue(X509Certificate2 issuer, string name, string? eku, bool ca = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, ca, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        if (eku is not null) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        var cert = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(5), Serial());
        _keep.Add(cert);
        // The public part only, as the verification state of WinVerifyTrust hands it over.
        return new X509Certificate2(cert.RawData);
    }

    // A positive serial number whose first byte is neither zero nor has the high bit set (a random one is sometimes refused).
    private static byte[] Serial()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        bytes[0] = (byte)((bytes[0] & 0x7F) | 0x10);
        return bytes;
    }

    private static X509Certificate2Collection Anchors(params X509Certificate2[] roots)
    {
        var collection = new X509Certificate2Collection();
        foreach (var root in roots) collection.Add(new X509Certificate2(root.RawData));
        return collection;
    }

    [Fact]
    public void A_code_signing_certificate_from_a_trusted_root_holds()
    {
        var root = Root();
        var leaf = Issue(root, "CN=WeMod LLC, O=WeMod LLC", CodeSigning.CodeSigningEku);
        Assert.True(CodeSigning.ChainTrusted(leaf, [], Anchors(root)));
    }

    [Fact]
    public void A_root_that_is_not_handed_in_does_not_hold_so_user_roots_play_no_part()
    {
        var root = Root();
        var leaf = Issue(root, "CN=WeMod LLC, O=WeMod LLC", CodeSigning.CodeSigningEku);
        // Another root in the anchors, an empty list, and the chain's own root left out: the leaf is not trusted.
        Assert.False(CodeSigning.ChainTrusted(leaf, [], Anchors(Root("CN=Another Root"))));
        Assert.False(CodeSigning.ChainTrusted(leaf, [], new X509Certificate2Collection()));
    }

    [Fact]
    public void A_self_signed_certificate_in_nobodys_store_is_rejected()
    {
        // The attacker signs with their own certificate called WeMod LLC: the publisher name would match, the chain does not.
        var self = Root("CN=WeMod LLC, O=WeMod LLC");
        Assert.True(WandSetup.SignerMatches(self.SubjectName));
        var other = Root();
        Assert.False(CodeSigning.ChainTrusted(new X509Certificate2(self.RawData), [], Anchors(other)));
    }

    [Fact]
    public void The_code_signing_usage_is_required()
    {
        var root = Root();
        Assert.False(CodeSigning.ChainTrusted(Issue(root, "CN=WeMod LLC", null), [], Anchors(root)));
        Assert.False(CodeSigning.ChainTrusted(Issue(root, "CN=WeMod LLC", "1.3.6.1.5.5.7.3.1"), [], Anchors(root)));
        Assert.True(CodeSigning.HasCodeSigningEku(Issue(root, "CN=x", CodeSigning.CodeSigningEku)));
    }

    [Fact]
    public void An_intermediate_has_to_come_with_the_signature()
    {
        var root = Root();
        var leaf = IssueFromCa(root, "CN=WeMod LLC, O=WeMod LLC", out var chainCa);
        Assert.True(CodeSigning.ChainTrusted(leaf, [chainCa], Anchors(root)));
        Assert.False(CodeSigning.ChainTrusted(leaf, [], Anchors(root)));
    }

    private X509Certificate2 IssueFromCa(X509Certificate2 root, string leafName, out X509Certificate2 ca)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Test Code Signing CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var caCert = caRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), Serial());
        using var caWithKey = caCert.CopyWithPrivateKey(caKey);
        _keep.Add(caWithKey);
        ca = new X509Certificate2(caCert.RawData);
        return Issue(caWithKey, leafName, CodeSigning.CodeSigningEku);
    }

    // ---- the hash that makes a changed file check again

    [Fact]
    public void The_same_hash_starts_without_asking_the_signature()
    {
        var asked = 0;
        var same = SignedProgram.Decide(new string('a', 64), new string('A', 64), () => { asked++; return false; });
        Assert.Equal(new SignedStartDecision(true, null, false), same);
        Assert.Equal(0, asked);
        // No hash in the rule: not a signed rule, nothing to check.
        Assert.True(SignedProgram.Decide("", new string('b', 64), () => { asked++; return false; }).Start);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void A_changed_hash_checks_again_and_starts_only_when_the_signature_holds()
    {
        var asked = 0;
        var old = new string('a', 64);
        var now = new string('B', 64);
        var good = SignedProgram.Decide(old, now, () => { asked++; return true; });
        Assert.True(good.Start);
        Assert.Equal(new string('b', 64), good.NewHash);
        Assert.False(good.SignatureInvalid);
        var bad = SignedProgram.Decide(old, now, () => { asked++; return false; });
        Assert.False(bad.Start);
        Assert.Null(bad.NewHash);
        Assert.True(bad.SignatureInvalid);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void The_hash_of_the_held_file_is_its_sha256_and_a_rule_keeps_only_a_real_hash()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-hash-").FullName;
        try
        {
            var exe = Path.Combine(dir, "wand.exe");
            File.WriteAllBytes(exe, [1, 2, 3, 4, 5]);
            using var held = CompanionPath.Hold(exe)!;
            Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4, 5])).ToLowerInvariant(), held.Sha256Hex());
            // Twice, from the start each time.
            Assert.Equal(held.Sha256Hex(), held.Sha256Hex());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        var rules = CompanionRules.Sanitize([
            new CouchHud.Core.Config.CompanionRule { AppId = "a", Sha256 = new string('F', 64) },
            new CouchHud.Core.Config.CompanionRule { AppId = "b", Sha256 = "nothex" },
            new CouchHud.Core.Config.CompanionRule { AppId = "c", Sha256 = new string('g', 64) }
        ]);
        // A hash that is not one is not turned into "no hash": it stays marked, so the rule does not start (see below).
        Assert.Equal([new string('f', 64), SignedProgram.Broken, SignedProgram.Broken], rules.Select(r => r.Sha256));
        // Only a rule that never had a hash is an ordinary companion.
        Assert.Equal(["", ""], CompanionRules.Sanitize([new CouchHud.Core.Config.CompanionRule { AppId = "a", Sha256 = "" }, new CouchHud.Core.Config.CompanionRule { AppId = "b", Sha256 = "  " }]).Select(r => r.Sha256));
    }

    [Fact]
    public void A_damaged_hash_fails_closed_and_shows_as_signature_invalid()
    {
        var wand = new LibraryItem { Id = "wand", Source = "custom", Name = "Wand", Type = ItemType.App, Launch = new LaunchTarget(LaunchKind.Exe, @"C:\Wand\Wand.exe") };
        var game = new LibraryItem { Id = "g", Source = "custom", Name = "g", Type = ItemType.Game, Launch = new LaunchTarget(LaunchKind.Exe, @"D:\g.exe") };
        var plain = new LibraryItem { Id = "plain", Source = "custom", Name = "plain", Type = ItemType.App, Launch = new LaunchTarget(LaunchKind.Exe, @"C:\Apps\plain.exe") };
        CouchHud.Core.Config.CompanionRule[] rules =
        [
            new() { AppId = "wand", Mode = "all", Sha256 = "garbage" },
            new() { AppId = "plain", Mode = "all", Sha256 = "" }
        ];
        var plan = CompanionRules.Plan(game, rules, id => id == "wand" ? wand : plain, i => i.Launch, _ => null, false, @"C:\Windows", p => p);
        Assert.Equal(["plain"], plan.Start.Select(s => s.App.Id));
        Assert.Contains(plan.Skipped, s => s is { AppId: "wand", Reason: "signature" });
        // The start gate agrees: a broken hash never starts and never asks the signature.
        var asked = 0;
        var decision = SignedProgram.Decide(SignedProgram.Broken, new string('a', 64), () => { asked++; return true; });
        Assert.Equal(new SignedStartDecision(false, null, true), decision);
        Assert.Equal(0, asked);
        Assert.True(SignedProgram.IsBroken("garbage"));
        Assert.False(SignedProgram.IsBroken(""));
        Assert.False(SignedProgram.IsBroken(new string('a', 64)));
    }

    [Fact]
    public void The_wintrust_structures_have_the_size_windows_expects()
    {
        // cbStruct is what WinVerifyTrust is told: WINTRUST_DATA with pSignatureSettings is 88 bytes on x64 (64 on x86).
        Assert.Equal(IntPtr.Size == 8 ? 88 : 64, System.Runtime.InteropServices.Marshal.SizeOf<CouchHud.Core.Platform.WintrustData>());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 16, System.Runtime.InteropServices.Marshal.SizeOf<CouchHud.Core.Platform.WintrustFileInfo>());
        Assert.Equal(IntPtr.Size == 8 ? 80 : 56, (int)System.Runtime.InteropServices.Marshal.OffsetOf<CouchHud.Core.Platform.WintrustData>(nameof(CouchHud.Core.Platform.WintrustData.pSignatureSettings)));
    }

    // ---- the package folder of a Store player

    private static LibraryItem StoreKodi() => new()
    {
        Id = "custom:kodi",
        Source = "custom",
        Name = "Kodi",
        Type = ItemType.App,
        Launch = new LaunchTarget(LaunchKind.Url, @"shell:AppsFolder\XBMCFoundation.Kodi_4n2hpmxwrvr6p!Kodi")
    };

    [Theory]
    [InlineData(@"D:\fake\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe", true)]
    [InlineData(@"d:/fake/windowsapps/XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p/./kodi.exe", true)]
    // A folder of that name somewhere else is not the real package folder.
    [InlineData(@"D:\other\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe", false)]
    [InlineData(@"C:\Users\A\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe", false)]
    // Dots are resolved before the comparison.
    [InlineData(@"D:\fake\WindowsApps\..\Evil\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe", false)]
    [InlineData(@"D:\fake\WindowsApps\Other.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe", false)]
    [InlineData(@"D:\fake\WindowsApps\XBMCFoundation.Kodi_21.0_x64__ffffffffffff\kodi.exe", false)]
    [InlineData(@"D:\fake\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\notkodi.exe", false)]
    public void A_store_player_counts_only_from_the_real_package_folder(string image, bool expected)
    {
        var profile = PlayerProfiles.Find(StoreKodi().Launch)!;
        Assert.Equal(expected, PlayerProfiles.IsProcessOf(profile, StoreKodi(), image, @"D:\fake\WindowsApps"));
    }

    [Fact]
    public void The_default_package_folder_is_program_files_windowsapps_from_the_system()
    {
        var root = PlayerProfiles.DefaultWindowsAppsRoot();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), root);
        var profile = PlayerProfiles.Find(StoreKodi().Launch)!;
        Assert.True(PlayerProfiles.IsProcessOf(profile, StoreKodi(), Path.Combine(root, "XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p", "kodi.exe")));
        Assert.False(PlayerProfiles.IsProcessOf(profile, StoreKodi(), @"C:\FakeProgramFiles\WindowsApps\XBMCFoundation.Kodi_21.0_x64__4n2hpmxwrvr6p\kodi.exe"));
    }

    [Fact]
    public void An_exe_entry_compares_normalised_paths()
    {
        var item = new LibraryItem
        {
            Id = "custom:kodi",
            Source = "custom",
            Name = "Kodi",
            Type = ItemType.App,
            InstallDir = @"C:\Program Files\Kodi",
            Launch = new LaunchTarget(LaunchKind.Exe, @"C:\Program Files\Kodi\kodi.exe")
        };
        var profile = PlayerProfiles.Find(item.Launch)!;
        Assert.True(PlayerProfiles.IsProcessOf(profile, item, @"c:/program files/kodi/./kodi.exe"));
        Assert.True(PlayerProfiles.IsProcessOf(profile, item, @"C:\Program Files\Kodi\sub\..\kodi.exe"));
        Assert.False(PlayerProfiles.IsProcessOf(profile, item, @"C:\Program Files\Kodi\..\Evil\kodi.exe"));
        Assert.False(PlayerProfiles.IsProcessOf(profile, item, @"C:\Program Files\KodiEvil\kodi.exe"));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
