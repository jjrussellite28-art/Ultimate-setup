using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// The key of the encrypted channel (0.7), protected for the Windows user (DPAPI) when stored; empty for phones paired before 0.7, which
    /// only have their token and keep exactly the rights they had (buttons, text, power).
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>"control", "library" or "full" (<see cref="RemoteRights"/>), set by the parents; nothing is "control".</summary>
    public string? Level { get; set; }

    /// <summary>The profile that was on the couch when the phone was paired.</summary>
    public string? Profile { get; set; }

    /// <summary>Paired while a child's profile was on the couch with parental controls on: never more than that child has (RemoteRights.Effective).</summary>
    public bool Child { get; set; }

    /// <summary>Stable name of the device for "unpair this phone" (the start of the token hash; the token itself is never stored).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id => TokenHash.Length >= 16 ? TokenHash[..16] : TokenHash;
}

/// <summary>
/// Pairing for the phone remote: the TV shows a short-lived secret in a QR link, the phone proves it has it by sealing its request with it,
/// and gets its own channel key back, sealed the same way (<see cref="TryPairSealed"/>). Every later request is sealed with that key.
/// Phones paired before 0.7 carry a long device token instead (<see cref="Find"/>) and keep what they could do.
/// </summary>
/// <param name="devices">Reads the paired devices.</param>
/// <param name="change">Changes and saves them (the config store's update).</param>
/// <param name="protect">Protects a channel key before it is stored (DPAPI in the HUD); null keeps it as it is (tests).</param>
/// <param name="unprotect">The other way.</param>
public sealed class Pairing(Func<IReadOnlyList<RemoteDevice>> devices, Action<Action<IList<RemoteDevice>>> change,
    Func<string, string>? protect = null, Func<string, string>? unprotect = null)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A device token runs out this long after its last use (every use renews it).</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(180);

    // Renewing is a config write: once a day is enough.
    private static readonly TimeSpan RenewAfter = TimeSpan.FromDays(1);

    private readonly object _lock = new();
    private string? _code;
    private DateTimeOffset _codeExpires;
    // Wrong tries are counted per source address (the same guard as the encrypted channel's): one device on the LAN must not keep the others from pairing.
    private readonly SourceGuard _guard = new();

    /// <summary>
    /// The current pairing secret (32 random bytes, base64url), or a fresh one if it expired. It only travels in the QR code's fragment
    /// (<c>#k=</c>, which browsers never send): the phone proves it has it by sealing its pairing request with it, it never goes over the network.
    /// </summary>
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
        _code = RemoteSeal.Base64Url(RandomNumberGenerator.GetBytes(32));
        _codeExpires = now + CodeLifetime;
    }

    /// <summary>
    /// The pairing step for the secret as such: returns a device token, or null for a wrong or expired one. Since 0.7 the HTTP side only
    /// pairs through <see cref="TryPairSealed"/> (no secret and no token in clear); this stays for the checks it shares and its tests.
    /// </summary>
    /// <param name="source">Address the request came from; wrong tries are counted and paused per source.</param>
    public string? TryPair(string code, string deviceName, DateTimeOffset now, string? source = null)
    {
        lock (_lock)
        {
            if (!OpenLocked(source, now)) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code.Trim()), Encoding.UTF8.GetBytes(_code!)))
            {
                FailedLocked(source, now);
                return null;
            }
            // One code, one phone.
            _code = null;
            _guard.Succeeded(source);
        }
        var token = NewToken();
        change(list => list.Add(new RemoteDevice { Name = CleanName(deviceName), TokenHash = Hash(token), Paired = now, LastUsed = now }));
        return token;
    }

    /// <summary>A sealed pairing: the new phone and the answer for it (sealed with the QR code's secret).</summary>
    public sealed record SealedPair(RemoteDevice Device, string Response);

    /// <summary>
    /// Pairing over the encrypted channel: the request must be sealed with the current QR secret (<see cref="RemoteSeal.PairKey"/>, direction
    /// request, context "pair") and carry the phone's name. A request that does not open counts as a wrong try (per source, as for codes).
    /// The answer, sealed with the same key, hands over the phone's id and its own random channel key; the secret is used up.
    /// </summary>
    /// <param name="profile">The profile on the couch right now.</param>
    /// <param name="child">That profile is a child's with parental controls on (the phone then never gets more than that child).</param>
    public SealedPair? TryPairSealed(string? envelope, DateTimeOffset now, string? source, string? profile, bool child)
    {
        string secret;
        lock (_lock)
        {
            if (!OpenLocked(source, now)) return null;
            secret = _code!;
        }
        var pairKey = RemoteSeal.PairKey(RemoteSeal.FromBase64Url(secret)!);
        var text = RemoteSeal.OpenText(pairKey, SealDirection.Request, "pair", envelope, out _, 4096);
        string? name = null;
        if (text is not null)
        {
            try
            {
                var body = JsonNode.Parse(text);
                name = body is JsonObject && body["name"]?.GetValueKind() == JsonValueKind.String ? body["name"]!.GetValue<string>() : "";
            }
            catch (JsonException)
            {
                name = null;
            }
        }
        lock (_lock)
        {
            if (name is null)
            {
                FailedLocked(source, now);
                return null;
            }
            // Someone else was faster with the same secret (or a new QR code came): one secret, one phone.
            if (_code != secret) return null;
            _code = null;
            _guard.Succeeded(source);
        }
        var channelKey = RandomNumberGenerator.GetBytes(RemoteSeal.KeySize);
        var device = new RemoteDevice
        {
            Name = CleanName(name),
            TokenHash = Hash(NewToken()),
            Paired = now,
            LastUsed = now,
            Key = (protect ?? (k => k))(RemoteSeal.Base64Url(channelKey)),
            Level = RemoteRights.Name(RemoteLevel.Control),
            Profile = profile,
            Child = child
        };
        change(list => list.Add(device));
        var answer = JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = device.Id, ["key"] = RemoteSeal.Base64Url(channelKey), ["name"] = device.Name });
        return new SealedPair(device, RemoteSeal.Seal(pairKey, SealDirection.Response, 0, "pair", answer));
    }

    /// <summary>Is the code open for a try from this source (not run out, the source not paused)?</summary>
    private bool OpenLocked(string? source, DateTimeOffset now) =>
        _code is not null && now < _codeExpires && !_guard.Blocked(source, now);

    // Guessing is pointless anyway (2^256). A pause after a few wrong tries keeps others on the LAN from hammering,
    // without killing the QR code on the TV.
    private void FailedLocked(string? source, DateTimeOffset now) => _guard.Failed(source, now);

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>The name the phone gave, without control characters, at most 40 characters; the default name for none.</summary>
    private static string CleanName(string? deviceName)
    {
        var clean = new string((deviceName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? Strings.Get("remote.defaultDevice") : clean[..Math.Min(clean.Length, 40)];
    }

    public bool IsValid(string? token) => IsValid(token, DateTimeOffset.UtcNow);

    /// <summary>True for a known token that has not run out; a use renews it.</summary>
    public bool IsValid(string? token, DateTimeOffset now) => Find(token, now) is not null;

    /// <summary>
    /// The paired phone a token belongs to, or null (unknown, run out); a use renews it like <see cref="IsValid(string?, DateTimeOffset)"/>.
    /// Only phones paired before 0.7 have a token: a phone of the encrypted channel never gets in this way.
    /// </summary>
    public RemoteDevice? Find(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var hash = Encoding.UTF8.GetBytes(Hash(token));
        var found = devices().FirstOrDefault(d => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(d.TokenHash), hash));
        if (found is null || found.Key.Length > 0) return null;
        return Renewed(found, now);
    }

    /// <summary>
    /// A phone of the encrypted channel by its id, with its channel key; null for an unknown id, a phone without a key (paired before 0.7)
    /// or one that ran out. Looking it up renews nothing, see <see cref="Used"/>.
    /// </summary>
    public (RemoteDevice Device, byte[] Key)? FindChannel(string? id, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 16) return null;
        var wanted = Encoding.ASCII.GetBytes(id);
        var found = devices().FirstOrDefault(d => d.Key.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.Id), wanted));
        if (found is null) return null;
        byte[]? key;
        try
        {
            key = RemoteSeal.FromBase64Url((unprotect ?? (k => k))(found.Key));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            key = null;
        }
        if (key is not { Length: RemoteSeal.KeySize }) return null;
        return Expired(found, now) ? null : (found, key);
    }

    /// <summary>
    /// A phone of the encrypted channel proved itself (its request's seal opened): its 180 days start again. <see cref="FindChannel"/>
    /// renews nothing, so a request that only names the id and fails the seal never keeps a phone alive.
    /// </summary>
    public void Used(RemoteDevice device, DateTimeOffset now) => Renewed(device, now);

    /// <summary>Ran out? Then it is removed (as in <see cref="Renewed"/>), without a renewal.</summary>
    private bool Expired(RemoteDevice found, DateTimeOffset now)
    {
        if (now - (found.LastUsed ?? found.Paired) <= TokenLifetime) return false;
        change(list => RemoveWhere(list, d => d.TokenHash == found.TokenHash));
        return true;
    }

    /// <summary>The phone with this id as it is stored now (any kind), or null.</summary>
    public RemoteDevice? ById(string? id) => string.IsNullOrEmpty(id) ? null : devices().FirstOrDefault(d => d.Id == id);

    /// <summary>The phone's level as the parents set it ("control", "library", "full"); false for an unknown id or level.</summary>
    public bool SetLevel(string id, string level)
    {
        if (string.IsNullOrEmpty(id) || !RemoteRights.IsName(level)) return false;
        var found = false;
        change(list =>
        {
            foreach (var d in list.Where(d => d.Id == id))
            {
                d.Level = level;
                found = true;
            }
        });
        return found;
    }

    private RemoteDevice? Renewed(RemoteDevice found, DateTimeOffset now)
    {
        var last = found.LastUsed ?? found.Paired;
        if (now - last > TokenLifetime)
        {
            change(list => RemoveWhere(list, d => d.TokenHash == found.TokenHash));
            return null;
        }
        if (now - last > RenewAfter) change(list =>
        {
            foreach (var d in list.Where(d => d.TokenHash == found.TokenHash)) d.LastUsed = now;
        });
        return found;
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
