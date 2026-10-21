using System.Security.Cryptography;
using System.Text;

namespace CouchHud.Shell.Services;

/// <summary>
/// Keeps tokens and API keys out of config.json in plain text: they are
/// encrypted for the current Windows user (DPAPI) and only readable by them.
/// </summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CouchHud");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    /// <summary>Decrypts a stored value; plain values (typed into the file by hand) pass through.</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored ?? "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Console.Error.WriteLine($"[secrets] could not decrypt a stored secret: {ex.Message}");
            return "";
        }
    }
}
