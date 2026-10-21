using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CouchHud.Service.Contracts;

/// <summary>A frame that cannot be accepted. <see cref="Code"/> is one of <see cref="ErrorCodes"/>.</summary>
public sealed class FrameException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// One message on the pipe: four bytes length (unsigned, little endian) and then that many bytes of UTF-8 JSON. The length is
/// checked before anything is allocated, so a length of four gigabytes costs nothing. At most <see cref="Protocol.MaxMessageBytes"/>
/// (streams of setup bytes use <see cref="StreamCodec"/>).
/// </summary>
public static class FrameCodec
{
    public const int HeaderBytes = 4;

    public static byte[] Encode(ReadOnlySpan<byte> payload, int maxBytes = Protocol.MaxMessageBytes)
    {
        if (payload.Length == 0) throw new FrameException(ErrorCodes.BadFrame, "empty message");
        if (payload.Length > maxBytes) throw new FrameException(ErrorCodes.TooLarge, $"message of {payload.Length} bytes, at most {maxBytes}");
        var frame = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    /// <summary>Writes one message; the copy that carries the length prefix is wiped afterwards (the caller wipes its own payload).</summary>
    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellation = default)
    {
        var frame = Encode(payload.Span);
        try
        {
            await stream.WriteAsync(frame, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }
    }

    /// <summary>
    /// <see cref="ReadAsync(Stream, int, CancellationToken)"/> with a time limit for the whole message, header and body: a sender that
    /// trickles a byte at a time cannot hold the connection. Throws <see cref="FrameException"/> with <see cref="ErrorCodes.Timeout"/>.
    /// </summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, TimeSpan timeout, int maxBytes = Protocol.MaxMessageBytes, CancellationToken cancellation = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            return await ReadAsync(stream, maxBytes, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new FrameException(ErrorCodes.Timeout, $"no complete message within {timeout.TotalSeconds:0.#} s");
        }
    }

    /// <summary>
    /// Reads one message (the returned bytes may hold a secret: the caller wipes them with <see cref="CryptographicOperations.ZeroMemory"/>
    /// when it is done; the strings made from them cannot be wiped). Null when the other side ended cleanly before a message started. Throws <see cref="FrameException"/>:
    /// <see cref="ErrorCodes.TooLarge"/> for a length above <paramref name="maxBytes"/>, <see cref="ErrorCodes.BadFrame"/> for a
    /// length of zero and <see cref="ErrorCodes.Truncated"/> when the stream ends inside the header or the message.
    /// </summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, int maxBytes = Protocol.MaxMessageBytes, CancellationToken cancellation = default)
    {
        var header = new byte[HeaderBytes];
        var got = await ReadFullyAsync(stream, header, cancellation).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < HeaderBytes) throw new FrameException(ErrorCodes.Truncated, "the message ended inside its length");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0) throw new FrameException(ErrorCodes.BadFrame, "empty message");
        if (length > maxBytes) throw new FrameException(ErrorCodes.TooLarge, $"message of {length} bytes, at most {maxBytes}");
        var payload = new byte[length];
        try
        {
            if (await ReadFullyAsync(stream, payload, cancellation).ConfigureAwait(false) < payload.Length)
            {
                throw new FrameException(ErrorCodes.Truncated, "the message ended before its announced length");
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
        return payload;
    }

    /// <summary>Reads until the buffer is full or the stream ends; returns how many bytes arrived.</summary>
    internal static async ValueTask<int> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellation)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellation).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
