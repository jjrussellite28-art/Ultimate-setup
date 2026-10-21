using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CouchHud.Service.Contracts;

/// <summary>
/// The bytes of a setup, sent after the header request (<see cref="SetupStreamArgs"/>) was answered with "streamReady":
/// chunks, each with the length prefix of a frame (at most <see cref="MaxChunkBytes"/>), and a chunk of length zero at the end.
/// The total is announced beforehand and must be exact: not a byte more (cut off at once), not a byte less. The service never
/// opens a path of the caller, it writes these bytes into its own protected folder.
/// </summary>
public static class StreamCodec
{
    /// <summary>The most a setup may have (the same limit as the download of a release).</summary>
    public const long MaxTotalBytes = 400L * 1024 * 1024;

    public const int MaxChunkBytes = 256 * 1024;

    /// <summary>
    /// A chunk is at least this big, except the last one (the one that completes the announced size). Without it a sender could
    /// pass the inactivity limit with a one-byte chunk every 29 seconds.
    /// </summary>
    public const int MinChunkBytes = 16 * 1024;

    /// <summary>The slowest sender that is still waited for (bytes per second over the whole stream).</summary>
    public const int MinBytesPerSecond = 256 * 1024;

    /// <summary>Time added to the throughput-based deadline for the start of the stream.</summary>
    public static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(30);

    /// <summary>The time a stream of this size may take in all: the size at <see cref="MinBytesPerSecond"/> plus <see cref="DeadlineGrace"/>.</summary>
    public static TimeSpan DeadlineFor(long size) => DeadlineGrace + TimeSpan.FromSeconds((double)size / MinBytesPerSecond);

    /// <summary>How long the sender may pause between two chunks.</summary>
    public static readonly TimeSpan DefaultInactivity = TimeSpan.FromSeconds(30);

    private const int WriteChunkBytes = 64 * 1024;

    /// <summary>Whether the announced size is acceptable at all.</summary>
    public static bool IsValidSize(long size) => size is > 0 and <= MaxTotalBytes;

    /// <summary>Sends exactly <paramref name="size"/> bytes of <paramref name="source"/> as chunks, then the end mark.</summary>
    public static async Task WriteAsync(Stream pipe, Stream source, long size, CancellationToken cancellation = default)
    {
        if (!IsValidSize(size)) throw new FrameException(ErrorCodes.StreamTooLarge, $"stream of {size} bytes, at most {MaxTotalBytes}");
        var buffer = new byte[WriteChunkBytes];
        var header = new byte[FrameCodec.HeaderBytes];
        long sent = 0;
        while (sent < size)
        {
            var want = (int)Math.Min(buffer.Length, size - sent);
            var got = await FrameCodec.ReadFullyAsync(source, buffer.AsMemory(0, want), cancellation).ConfigureAwait(false);
            if (got < want) throw new FrameException(ErrorCodes.StreamShort, "the source ended before the announced size");
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)got);
            await pipe.WriteAsync(header, cancellation).ConfigureAwait(false);
            await pipe.WriteAsync(buffer.AsMemory(0, got), cancellation).ConfigureAwait(false);
            sent += got;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0);
        await pipe.WriteAsync(header, cancellation).ConfigureAwait(false);
        await pipe.FlushAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// A chunk smaller than <see cref="MinChunkBytes"/> that is not the last is refused (<see cref="ErrorCodes.BadFrame"/>), and the
    /// whole stream must be done within <paramref name="deadline"/> (default <see cref="DeadlineFor"/>), else <see cref="ErrorCodes.StreamTimeout"/>:
    /// a sender that is just fast enough not to be idle cannot hold the service's one setup slot for ever.
    /// Copies the chunks into <paramref name="sink"/> and returns the byte count (equal to <paramref name="declaredSize"/>, else it throws).
    /// Throws <see cref="FrameException"/>: <see cref="ErrorCodes.StreamTooLarge"/> (announced above the limit, a chunk too big or more
    /// bytes than announced), <see cref="ErrorCodes.StreamShort"/> (end mark before the announced size), <see cref="ErrorCodes.Truncated"/>
    /// (the pipe ended in the middle) and <see cref="ErrorCodes.StreamTimeout"/> (nothing arrived within the inactivity time).
    /// </summary>
    public static async Task<long> ReadAsync(Stream pipe, Stream sink, long declaredSize, TimeSpan? inactivity = null, CancellationToken cancellation = default, TimeSpan? deadline = null)
    {
        if (!IsValidSize(declaredSize)) throw new FrameException(ErrorCodes.StreamTooLarge, $"stream of {declaredSize} bytes, at most {MaxTotalBytes}");
        var limit = inactivity ?? DefaultInactivity;
        var header = new byte[FrameCodec.HeaderBytes];
        var buffer = new byte[MaxChunkBytes];
        long total = 0;
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        whole.CancelAfter(deadline ?? DeadlineFor(declaredSize));
        try
        {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(whole.Token);
            idle.CancelAfter(limit);
            try
            {
                var got = await FrameCodec.ReadFullyAsync(pipe, header, idle.Token).ConfigureAwait(false);
                if (got < header.Length) throw new FrameException(ErrorCodes.Truncated, "the stream ended inside a chunk header");
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (length == 0)
                {
                    if (total != declaredSize) throw new FrameException(ErrorCodes.StreamShort, $"{total} bytes arrived, {declaredSize} announced");
                    return total;
                }
                if (length > MaxChunkBytes) throw new FrameException(ErrorCodes.StreamTooLarge, $"chunk of {length} bytes, at most {MaxChunkBytes}");
                if (total + length > declaredSize) throw new FrameException(ErrorCodes.StreamTooLarge, $"more than the announced {declaredSize} bytes");
                if (length < MinChunkBytes && total + length != declaredSize) throw new FrameException(ErrorCodes.BadFrame, $"chunk of {length} bytes, at least {MinChunkBytes} unless it is the last");
                var chunk = buffer.AsMemory(0, (int)length);
                var read = 0;
                while (read < chunk.Length)
                {
                    var n = await pipe.ReadAsync(chunk[read..], idle.Token).ConfigureAwait(false);
                    if (n == 0) throw new FrameException(ErrorCodes.Truncated, "the stream ended inside a chunk");
                    read += n;
                }
                await sink.WriteAsync(chunk, cancellation).ConfigureAwait(false);
                total += length;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new FrameException(ErrorCodes.StreamTimeout, whole.IsCancellationRequested ? "the stream took too long" : $"nothing arrived for {limit.TotalSeconds:0} s");
            }
        }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
