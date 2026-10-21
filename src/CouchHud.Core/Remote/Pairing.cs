using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Remote;

/// <summary>A phone that was paired by scanning the QR code. Only a hash of its token is stored.</summary>
public sealed class RemoteDevice
{
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset Paired { get; set; }

    /// <summary>Last request with this token; the device token runs out after <see cref="Pairing.TokenLifetime"/> without use.</summary>
    public DateTimeOffset? LastUsed { get; set; }

    /// <summary>Stable name of the device for "unpair this phone" (the start of the token hash; the token itself is never stored).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id => TokenHash.Length >= 16 ? TokenHash[..16] : TokenHash;
}

/// <summary>
/// Pairing for the phone remote: the TV shows a short-lived code in a QR link, the
/// phone trades it for a long device token, and every later request carries that token.
/// </summary>
/// <param name="devices">Reads the paired devices.</param>
/// <param name="change">Changes and saves them (the config store's update).</param>
public sealed class Pairing(Func<IReadOnlyList<RemoteDevice>> devices, Action<Action<IList<RemoteDevice>>> change)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A device token runs out this long after its last use (every use renews it).</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(180);

    // Renewing is a config write: once a day is enough.
    private static readonly TimeSpan RenewAfter = TimeSpan.FromDays(1);
    private const int MaxSources = 256;
    // No 0/O, 1/I/L: the code may also be typed by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly object _lock = new();
    private string? _code;
    private DateTimeOffset _codeExpires;
    // Wrong tries are counted per source address: one device on the LAN must not keep the others from pairing.
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = [];

    /// <summary>The current code, or a fresh one if it expired.</summary>
    public string CurrentCode(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_code is null || now >= _codeExpires)
            {
                NewCodeLocked(now);
            }
            return _code!;
        }
    }

    /// <summary>Replaces the code, e.g. when the user asks for a new QR code.</summary>
    public string NewCode(DateTimeOffset now)
    {
        lock (_lock)
        {
            NewCodeLocked(now);
            return _code!;
        }
    }

    private void NewCodeLocked(DateTimeOffset now)
    {
        _code = new string(RandomNumberGenerator.GetItems<char>(Alphabet, 8));
        _codeExpires = now + CodeLifetime;
        _failures.Clear();
    }

    /// <summary>Returns the new device token, or null for a wrong or expired code.</summary>
    /// <param name="source">Address the request came from; wrong tries are counted and paused per source.</param>
    public string? TryPair(string code, string deviceName, DateTimeOffset now, string? source = null)
    {
        var key = source ?? "";
        lock (_lock)
        {
            if (_code is null || now >= _codeExpires) return null;
            if (_blockedUntil.TryGetValue(key, out var until))
            {
                if (now < until) return null;
                _blockedUntil.Remove(key);
            }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()), Encoding.UTF8.GetBytes(_code)))
            {
                // Guessing is pointless anyway (31^8). A pause after a few wrong tries keeps
                // others on the LAN from hammering, without killing the QR code on the TV.
                if (_failures.Count >= MaxSources || _blockedUntil.Count >= MaxSources)
                {
                    _failures.Clear();
                    foreach (var stale in _blockedUntil.Where(b => b.Value <= now).Select(b => b.Key).ToList()) _blockedUntil.Remove(stale);
                }
                var failures = _failures.GetValueOrDefault(key) + 1;
                if (failures >= 5)
                {
                    _failures.Remove(key);
                    _blockedUntil[key] = now + TimeSpan.FromSeconds(30);
                }
                else
                {
                    _failures[key] = failures;
                }
                return null;
            }
            // One code, one phone.
            _code = null;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var name = string.IsNullOrWhiteSpace(deviceName) ? Strings.Get("remote.defaultDevice") : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 40)];
        change(list => list.Add(new RemoteDevice { Name = name, TokenHash = Hash(token), Paired = now, LastUsed = now }));
        return token;
    }

    public bool IsValid(string? token) => IsValid(token, DateTimeOffset.UtcNow);

    /// <summary>True for a known token that has not run out; a use renews it.</summary>
    public bool IsValid(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return false;
        var hash = Encoding.UTF8.GetBytes(Hash(token));
        var found = devices().FirstOrDefault(d => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(d.TokenHash), hash));
        if (found is null) return false;
        var last = found.LastUsed ?? found.Paired;
        if (now - last > TokenLifetime)
        {
            change(list => RemoveWhere(list, d => d.TokenHash == found.TokenHash));
            return false;
        }
        if (now - last > RenewAfter) change(list =>
        {
            foreach (var d in list.Where(d => d.TokenHash == found.TokenHash)) d.LastUsed = now;
        });
        return true;
    }

    public void RevokeAll()
    {
        change(list => list.Clear());
    }

    /// <summary>Unpairs one phone (<see cref="RemoteDevice.Id"/>); false if there is none with that id.</summary>
    public bool Revoke(string id)
    {
        var removed = false;
        if (string.IsNullOrEmpty(id)) return false;
        change(list => removed = RemoveWhere(list, d => d.Id == id) > 0);
        return removed;
    }

    private static int RemoveWhere(IList<RemoteDevice> list, Func<RemoteDevice, bool> match)
    {
        var count = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!match(list[i])) continue;
            list.RemoveAt(i);
            count++;
        }
        return count;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
