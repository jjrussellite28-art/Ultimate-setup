using System.Security.Cryptography.X509Certificates;

namespace CouchHud.Core.Library;

/// <summary>
/// The parts of a code signature check that are plain logic (tested with certificates made in the test, not with the machine's stores):
/// the Code Signing usage, the chain to trust anchors that are handed in, and the decision at the start of a signed companion.
/// </summary>
public static class CodeSigning
{
    /// <summary>The Code Signing extended key usage (1.3.6.1.5.5.7.3.3).</summary>
    public const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    /// <summary>The certificate states the Code Signing usage. No extension, or a list without it, is "no".</summary>
    public static bool HasCodeSigningEku(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension eku && eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == CodeSigningEku)) return true;
        }
        return false;
    }

    /// <summary>
    /// The signer's chain ends at one of <paramref name="trustedRoots"/> and nowhere else: CustomRootTrust with exactly these anchors (the
    /// user's own certificate stores play no part), the Code Signing usage required of the signer, no revocation lookup (nothing goes over the
    /// network). The time is not judged here: WinVerifyTrust already did that, with the timestamp, and an expired signer with a valid timestamp
    /// is a valid signature. Anything that does not hold is false.
    /// </summary>
    public static bool ChainTrusted(X509Certificate2 signer, IEnumerable<X509Certificate2> intermediates, X509Certificate2Collection trustedRoots)
    {
        if (!HasCodeSigningEku(signer) || trustedRoots.Count == 0) return false;
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.AddRange(trustedRoots);
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        policy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(CodeSigningEku));
        foreach (var certificate in intermediates) policy.ExtraStore.Add(certificate);
        try
        {
            if (!chain.Build(signer)) return false;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        if (chain.ChainStatus.Any(s => s.Status != X509ChainStatusFlags.NoError)) return false;
        // The last element is a trust anchor we handed in (not just any self-signed certificate that came along).
        var root = chain.ChainElements[^1].Certificate;
        return trustedRoots.Cast<X509Certificate2>().Any(r => r.RawData.AsSpan().SequenceEqual(root.RawData));
    }
}

/// <summary>What a signed companion (a rule that holds the file's hash) does at its start.</summary>
/// <param name="Start">Whether it may start.</param>
/// <param name="NewHash">The file changed and its signature still holds: the hash to keep from now on. Null when nothing changes.</param>
/// <param name="SignatureInvalid">The file changed and its signature does not hold: not started, shown as "signature invalid".</param>
public sealed record SignedStartDecision(bool Start, string? NewHash, bool SignatureInvalid);

public static class SignedProgram
{
    public static bool IsHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);

    /// <summary>What <see cref="CompanionRules.Sanitize"/> keeps in place of a hash that is not one (a hand-edited file): not empty, not a hash.</summary>
    public const string Broken = "invalid";

    /// <summary>
    /// The rule once had a hash (signature rule) but what is kept is no hash: fail closed. It is not turned into "no hash" (that would make a
    /// damaged signature rule an ordinary companion that needs no signature); it does not start and shows as "signature invalid".
    /// </summary>
    public static bool IsBroken(string? text) => !string.IsNullOrEmpty(text) && !IsHash(text);

    /// <summary>
    /// <paramref name="ruleHash"/> is the SHA-256 the rule keeps (empty: not a signed rule, nothing to check). The same hash starts at once. A
    /// different one (the program updated itself, or was replaced) starts only if the signature check runs again on the held file and holds;
    /// then its hash is kept, so the next start is quick. The check is not asked when the hash is the same.
    /// </summary>
    public static SignedStartDecision Decide(string? ruleHash, string actualHash, Func<bool> signatureHolds)
    {
        if (string.IsNullOrEmpty(ruleHash)) return new SignedStartDecision(true, null, false);
        // A damaged hash is not "no hash": nothing to compare with, nothing starts, and the signature is not even asked.
        if (IsBroken(ruleHash)) return new SignedStartDecision(false, null, true);
        if (string.Equals(ruleHash, actualHash, StringComparison.OrdinalIgnoreCase)) return new SignedStartDecision(true, null, false);
        return signatureHolds()
            ? new SignedStartDecision(true, actualHash.ToLowerInvariant(), false)
            : new SignedStartDecision(false, null, true);
    }
}
