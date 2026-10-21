using System.Buffers.Binary;
using System.Text;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

public class FramingTests
{
    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task A_message_survives_the_round_trip()
    {
        var payload = ProtocolJson.SerializeRequest(Operation.Ping);
        using var pipe = new MemoryStream();
        await FrameCodec.WriteAsync(pipe, payload);
        pipe.Position = 0;
        Assert.Equal(payload, await FrameCodec.ReadAsync(pipe));
        Assert.Null(await FrameCodec.ReadAsync(pipe));   // clean end
    }

    [Fact]
    public async Task Two_messages_come_out_one_by_one()
    {
        using var pipe = new MemoryStream();
        await FrameCodec.WriteAsync(pipe, "{\"a\":1}"u8.ToArray());
        await FrameCodec.WriteAsync(pipe, "{\"b\":2}"u8.ToArray());
        pipe.Position = 0;
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await FrameCodec.ReadAsync(pipe))!));
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString((await FrameCodec.ReadAsync(pipe))!));
    }

    [Fact]
    public async Task The_limit_is_64_KiB_and_checked_before_reading_the_body()
    {
        var exactly = new byte[Protocol.MaxMessageBytes];
        Array.Fill(exactly, (byte)' ');
        using (var ok = new MemoryStream(Frame(exactly))) Assert.Equal(exactly.Length, (await FrameCodec.ReadAsync(ok))!.Length);

        // Only the header of a gigantic message: refused without waiting for (or allocating) the body.
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        using var huge = new MemoryStream(header);
        var ex = await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(huge));
        Assert.Equal(ErrorCodes.TooLarge, ex.Code);

        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)Protocol.MaxMessageBytes + 1);
        using var one = new MemoryStream(header);
        Assert.Equal(ErrorCodes.TooLarge, (await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(one))).Code);
    }

    [Fact]
    public void A_message_that_is_too_large_is_not_written_either()
    {
        var ex = Assert.Throws<FrameException>(() => FrameCodec.Encode(new byte[Protocol.MaxMessageBytes + 1]));
        Assert.Equal(ErrorCodes.TooLarge, ex.Code);
        Assert.Equal(ErrorCodes.BadFrame, Assert.Throws<FrameException>(() => FrameCodec.Encode([])).Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_stream_that_ends_inside_the_length_is_truncated(int bytes)
    {
        using var pipe = new MemoryStream(new byte[bytes]);
        Assert.Equal(ErrorCodes.Truncated, (await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(pipe))).Code);
    }

    [Fact]
    public async Task A_stream_that_ends_inside_the_message_is_truncated()
    {
        var frame = Frame("{\"v\":1,\"op\":\"Ping\"}"u8.ToArray());
        using var pipe = new MemoryStream(frame[..^3]);
        Assert.Equal(ErrorCodes.Truncated, (await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(pipe))).Code);
    }

    [Fact]
    public async Task A_length_of_zero_is_a_bad_frame()
    {
        using var pipe = new MemoryStream(new byte[4]);
        Assert.Equal(ErrorCodes.BadFrame, (await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(pipe))).Code);
    }

    [Fact]
    public async Task Garbage_is_framed_by_its_first_bytes_and_the_json_says_no()
    {
        // "GET " read as a length is about 540 MB: too large. Random bytes with a plausible length pass the frame, not the validator.
        using var http = new MemoryStream("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        Assert.Equal(ErrorCodes.TooLarge, (await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(http))).Code);

        var noise = new byte[200];
        new Random(7).NextBytes(noise);
        using var pipe = new MemoryStream(Frame(noise));
        var payload = (await FrameCodec.ReadAsync(pipe))!;
        Assert.Equal(ErrorCodes.BadJson, new RequestValidator(_ => true).Validate(payload).Code);
    }

    // ---- the stream of setup bytes -------------------------------------------------------------------

    private static byte[] Chunk(byte[] data)
    {
        var chunk = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, (uint)data.Length);
        data.CopyTo(chunk, 4);
        return chunk;
    }

    private static readonly byte[] End = new byte[4];

    [Fact]
    public async Task A_stream_of_bytes_arrives_exactly()
    {
        var data = new byte[200_000];
        new Random(1).NextBytes(data);
        using var pipe = new MemoryStream();
        await StreamCodec.WriteAsync(pipe, new MemoryStream(data), data.Length);
        pipe.Position = 0;
        using var sink = new MemoryStream();
        Assert.Equal(data.Length, await StreamCodec.ReadAsync(pipe, sink, data.Length));
        Assert.Equal(data, sink.ToArray());
    }

    [Fact]
    public async Task More_bytes_than_announced_are_cut_off()
    {
        var big = new byte[StreamCodec.MinChunkBytes];
        using var pipe = new MemoryStream([.. Chunk(big), .. Chunk(big), .. End]);
        var ex = await Assert.ThrowsAsync<FrameException>(async () => await StreamCodec.ReadAsync(pipe, new MemoryStream(), big.Length + 100));
        Assert.Equal(ErrorCodes.StreamTooLarge, ex.Code);
    }

    [Fact]
    public async Task Fewer_bytes_than_announced_are_short()
    {
        var big = new byte[StreamCodec.MinChunkBytes];
        using var pipe = new MemoryStream([.. Chunk(big), .. End]);
        Assert.Equal(ErrorCodes.StreamShort, (await Assert.ThrowsAsync<FrameException>(async () => await StreamCodec.ReadAsync(pipe, new MemoryStream(), big.Length + 15))).Code);
    }

    [Fact]
    public async Task A_stream_that_breaks_off_is_truncated()
    {
        using var inChunk = new MemoryStream(Chunk(new byte[10])[..8]);
        Assert.Equal(ErrorCodes.Truncated, (await Assert.ThrowsAsync<FrameException>(async () => await StreamCodec.ReadAsync(inChunk, new MemoryStream(), 10))).Code);
        using var noEnd = new MemoryStream(Chunk(new byte[10]));
        Assert.Equal(ErrorCodes.Truncated, (await Assert.ThrowsAsync<FrameException>(async () => await StreamCodec.ReadAsync(noEnd, new MemoryStream(), 10))).Code);
    }

    [Fact]
    public async Task The_total_is_limited_to_400_MB_and_a_chunk_to_256_KiB()
    {
        Assert.False(StreamCodec.IsValidSize(0));
        Assert.True(StreamCodec.IsValidSize(400L * 1024 * 1024));
        Assert.False(StreamCodec.IsValidSize(400L * 1024 * 1024 + 1));
        Assert.Equal(ErrorCodes.StreamTooLarge, (await Assert.ThrowsAsync<FrameException>(
            async () => await StreamCodec.ReadAsync(new MemoryStream(), new MemoryStream(), 400L * 1024 * 1024 + 1))).Code);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, StreamCodec.MaxChunkBytes + 1);
        Assert.Equal(ErrorCodes.StreamTooLarge, (await Assert.ThrowsAsync<FrameException>(
            async () => await StreamCodec.ReadAsync(new MemoryStream(header), new MemoryStream(), 1_000_000))).Code);
    }

    [Fact]
    public async Task A_chunk_below_the_minimum_is_refused_unless_it_is_the_last()
    {
        // 1-byte chunks: the slowloris that never lets the inactivity limit run out.
        using var trickle = new MemoryStream([.. Chunk([1]), .. Chunk([2]), .. End]);
        Assert.Equal(ErrorCodes.BadFrame, (await Assert.ThrowsAsync<FrameException>(async () => await StreamCodec.ReadAsync(trickle, new MemoryStream(), 1000))).Code);

        var data = new byte[StreamCodec.MinChunkBytes + 5];
        using var ok = new MemoryStream([.. Chunk(data[..StreamCodec.MinChunkBytes]), .. Chunk(data[StreamCodec.MinChunkBytes..]), .. End]);
        Assert.Equal(data.Length, await StreamCodec.ReadAsync(ok, new MemoryStream(), data.Length));
    }

    [Fact]
    public async Task A_sender_that_is_never_idle_but_too_slow_runs_into_the_overall_deadline()
    {
        // Sends a valid chunk header and one byte of it every 20 ms: never idle for the 1 s limit, but not finishing either.
        var ex = await Assert.ThrowsAsync<FrameException>(async () =>
            await StreamCodec.ReadAsync(new DrippingStream(), new MemoryStream(), 1_000_000, TimeSpan.FromSeconds(1), default, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(ErrorCodes.StreamTimeout, ex.Code);
    }

    [Fact]
    public void The_deadline_grows_with_the_size_at_a_minimum_throughput()
    {
        Assert.True(StreamCodec.DeadlineFor(1) < StreamCodec.DeadlineFor(StreamCodec.MaxTotalBytes));
        Assert.True(StreamCodec.DeadlineFor(StreamCodec.MaxTotalBytes) < TimeSpan.FromMinutes(40));
    }

    [Fact]
    public async Task A_message_read_with_a_timeout_cannot_be_trickled()
    {
        var ex = await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(new StalledStream(), TimeSpan.FromMilliseconds(100)));
        Assert.Equal(ErrorCodes.Timeout, ex.Code);
        // A body that drips is cut off by the same limit.
        ex = await Assert.ThrowsAsync<FrameException>(async () => await FrameCodec.ReadAsync(new DrippingStream(), TimeSpan.FromMilliseconds(300)));
        Assert.Equal(ErrorCodes.Timeout, ex.Code);
        // And a complete message passes.
        using var whole = new MemoryStream(Frame("{}"u8.ToArray()));
        Assert.Equal(2, (await FrameCodec.ReadAsync(whole, TimeSpan.FromSeconds(5)))!.Length);
    }

    /// <summary>Announces a large chunk and delivers it a byte at a time.</summary>
    private sealed class DrippingStream : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            if (_sent < 4)
            {
                // length 60000, little endian: 0x0000EA60
                buffer.Span[0] = new byte[] { 0x60, 0xEA, 0x00, 0x00 }[_sent++];
                return 1;
            }
            buffer.Span[0] = 0;
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_sender_that_stalls_runs_into_the_inactivity_limit()
    {
        var ex = await Assert.ThrowsAsync<FrameException>(async () => await StreamCodec.ReadAsync(new StalledStream(), new MemoryStream(), 10, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(ErrorCodes.StreamTimeout, ex.Code);
    }

    /// <summary>A pipe whose other side is connected and silent.</summary>
    private sealed class StalledStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
