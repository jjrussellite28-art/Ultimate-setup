namespace CouchHud.Shell.Services;

/// <summary>
/// The secret for the HMAC over files that must not be changed by a program that only writes files (the play time file,
/// the list of approved plugins): random, protected with DPAPI for this Windows user, in one file of the data folder.
/// </summary>
public static class DataKey
{
    private static readonly object Gate = new();
    private static byte[]? _cached;

    /// <summary>The key, created on first use; null when it cannot be stored (then nothing is signed).</summary>
    public static byte[]? Get(string dataDir)
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached;
            var file = Path.Combine(dataDir, "parental.key");
            try
            {
                if (File.Exists(file))
                {
                    var stored = Secrets.Unprotect(File.ReadAllText(file));
                    if (stored.Length > 0) return _cached = Convert.FromBase64String(stored);
                }
                var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(file, Secrets.Protect(Convert.ToBase64String(key)));
                return _cached = key;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Console.Error.WriteLine($"[key] no key for signed files: {ex.Message}");
                return null;
            }
        }
    }
}
