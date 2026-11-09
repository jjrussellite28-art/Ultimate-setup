using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MegaHub.Core.Remote;

/// <summary>Which way a sealed message goes; part of every tag, so an answer can never be sent back as a request.</summary>
public enum SealDirection : byte
{
    Request = 1,
    Response = 2,
    Event = 3
}

/// <summary>
/// The phone remote's encrypted channel ("seal v1"). The phone page is served over plain HTTP in the home network, where browsers give a
/// page no WebCrypto (that needs a secure context), so both ends use nothing but SHA-256 and HMAC-SHA256: the phone a small JavaScript
/// copy (ui/remote/seal.js, checked against the same test vectors), the PC .NET's own. A message is
/// <c>"1." + seq + "." + base64url(nonce ‖ ciphertext ‖ tag)</c> with
/// <list type="bullet">
/// <item>encKey = HMAC(key, "couchhud-remote-v1 enc"), macKey = HMAC(key, "couchhud-remote-v1 mac");</item>
/// <item>keystream block i = HMAC(encKey, nonce ‖ u32be(i)) (HMAC as a PRF in counter mode), ciphertext = plaintext XOR keystream;</item>
/// <item>tag = HMAC(macKey, dir ‖ u64be(seq) ‖ u32be(len(aad)) ‖ aad ‖ nonce ‖ ciphertext) (encrypt-then-MAC, checked in constant time first).</item>
/// </list>
/// The 16-byte nonce is random per message; seq and the direction are bound by the tag, so the receiver's replay window
/// (<see cref="ReplayWindow"/>) and the direction check make a recorded message worthless. Keys come from the QR code's secret (pairing), the
/// device key handed over sealed at pairing, and per session from both sides' random nonces (<see cref="SessionKey"/>).
/// </summary>
public static class RemoteSeal
{
    public const int KeySize = 32;
    public const int NonceSize = 16;
    public const int TagSize = 32;
    private const string Version = "1";
    private const string Label = "couchhud-remote-v1 ";

    /// <summary>A labelled key from another key: HMAC(key, "couchhud-remote-v1 " + label ‖ parts).</summary>
    public static byte[] Derive(byte[] key, string label, params byte[][] parts)
    {
        var head = Encoding.ASCII.GetBytes(Label + label);
        var data = new byte[head.Length + parts.Sum(p => p.Length)];
        head.CopyTo(data, 0);
        var at = head.Length;
        foreach (var p in parts)
        {
            p.CopyTo(data, at);
            at += p.Length;
        }
        return HMACSHA256.HashData(key, data);
    }

    /// <summary>The key of the pairing messages, from the secret that only the QR code on the TV carries.</summary>
    public static byte[] PairKey(byte[] secret) => Derive(secret, "pair");

    /// <summary>A session's key: the device key with both sides' fresh nonces (a recorded session opening gives no usable session).</summary>
    public static byte[] SessionKey(byte[] deviceKey, byte[] clientNonce, byte[] serverNonce) => Derive(deviceKey, "session", clientNonce, serverNonce);

    /// <summary>A short proof for a URL (the event stream, an image): hex of HMAC(macKey, purpose ‖ value), 32 characters.</summary>
    public static string UrlProof(byte[] key, string purpose, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(Derive(key, "mac"), Encoding.UTF8.GetBytes(purpose + "|" + value)))[..32].ToLowerInvariant();

    public static bool CheckUrlProof(byte[] key, string purpose, string value, string? proof) =>
        proof is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(UrlProof(key, purpose, value)), Encoding.ASCII.GetBytes(proof));

    public static string Seal(byte[] key, SealDirection dir, long seq, string aad, string plaintext) =>
        Seal(key, dir, seq, aad, Encoding.UTF8.GetBytes(plaintext), RandomNumberGenerator.GetBytes(NonceSize));

    /// <summary>With a given nonce: for the test vectors only (a real message always takes a fresh random one).</summary>
    internal static string Seal(byte[] key, SealDirection dir, long seq, string aad, byte[] plaintext, byte[] nonce)
    {
        if (key.Length != KeySize) throw new ArgumentException("key size");
        if (seq < 0) throw new ArgumentOutOfRangeException(nameof(seq));
        var encKey = Derive(key, "enc");
        var macKey = Derive(key, "mac");
        var body = new byte[NonceSize + plaintext.Length + TagSize];
        nonce.CopyTo(body, 0);
        var ct = body.AsSpan(NonceSize, plaintext.Length);
        plaintext.CopyTo(ct);
        Xor(encKey, nonce, ct);
        Tag(macKey, dir, seq, aad, nonce, ct).CopyTo(body, NonceSize + plaintext.Length);
        return $"{Version}.{seq}.{Base64Url(body)}";
    }

    /// <summary>
    /// Checks and opens a message; null for anything that is not exactly a message of this key, direction and context (tampered, other key,
    /// other direction, malformed, too long). <paramref name="seq"/> is the sequence number the tag vouches for.
    /// </summary>
    public static byte[]? Open(byte[] key, SealDirection dir, string aad, string? message, out long seq, int maxLength = 1 << 20)
    {
        seq = -1;
        if (key.Length != KeySize || string.IsNullOrEmpty(message) || message.Length > maxLength) return null;
        var parts = message.Split('.');
        if (parts.Length != 3 || parts[0] != Version) return null;
        if (parts[1].Length is 0 or > 15 || !parts[1].All(char.IsAsciiDigit) || (parts[1].Length > 1 && parts[1][0] == '0')) return null;
        var number = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var body = FromBase64Url(parts[2]);
        if (body is null || body.Length < NonceSize + TagSize) return null;
        var nonce = body.AsSpan(0, NonceSize).ToArray();
        var ct = body.AsSpan(NonceSize, body.Length - NonceSize - TagSize);
        var tag = body.AsSpan(body.Length - TagSize);
        var macKey = Derive(key, "mac");
        if (!CryptographicOperations.FixedTimeEquals(Tag(macKey, dir, number, aad, nonce, ct), tag)) return null;
        var plain = ct.ToArray();
        Xor(Derive(key, "enc"), nonce, plain);
        seq = number;
        return plain;
    }

    public static string? OpenText(byte[] key, SealDirection dir, string aad, string? message, out long seq, int maxLength = 1 << 20)
    {
        var plain = Open(key, dir, aad, message, out seq, maxLength);
        if (plain is null) return null;
        try
        {
            return new UTF8Encoding(false, true).GetString(plain);
        }
        catch (DecoderFallbackException)
        {
            seq = -1;
            return null;
        }
    }

    private static byte[] Tag(byte[] macKey, SealDirection dir, long seq, string aad, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ct)
    {
        var aadBytes = Encoding.UTF8.GetBytes(aad);
        var data = new byte[1 + 8 + 4 + aadBytes.Length + nonce.Length + ct.Length];
        data[0] = (byte)dir;
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(1), seq);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(9), aadBytes.Length);
        aadBytes.CopyTo(data, 13);
        nonce.CopyTo(data.AsSpan(13 + aadBytes.Length));
        ct.CopyTo(data.AsSpan(13 + aadBytes.Length + nonce.Length));
        return HMACSHA256.HashData(macKey, data);
    }

    private static void Xor(byte[] encKey, ReadOnlySpan<byte> nonce, Span<byte> data)
    {
        Span<byte> input = stackalloc byte[NonceSize + 4];
        nonce.CopyTo(input);
        Span<byte> block = stackalloc byte[32];
        for (var i = 0; i * 32 < data.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(input[NonceSize..], (uint)i);
            HMACSHA256.HashData(encKey, input, block);
            var chunk = data.Slice(i * 32, Math.Min(32, data.Length - i * 32));
            for (var j = 0; j < chunk.Length; j++) chunk[j] ^= block[j];
        }
    }

    public static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which sequence numbers a session has seen (like IPsec's window): a number once only, and none more than <see cref="Size"/> behind the
/// highest. Requests from the phone may overtake each other, a recorded one never counts twice.
/// </summary>
public sealed class ReplayWindow
{
    public const int Size = 64;
    private long _highest = -1;
    private ulong _seen;

    /// <summary>True the first time a number in the window comes; false for repeats, numbers too far back, and negatives.</summary>
    public bool Accept(long seq)
    {
        if (seq < 0) return false;
        if (seq > _highest)
        {
            var shift = seq - _highest;
            _seen = shift >= Size ? 0 : _seen << (int)shift;
            _seen |= 1;
            _highest = seq;
            return true;
        }
        var back = _highest - seq;
        if (back >= Size) return false;
        var bit = 1UL << (int)back;
        if ((_seen & bit) != 0) return false;
        _seen |= bit;
        return true;
    }
}
