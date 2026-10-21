using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

public class LineWriterTests
{
    private static readonly DateTime Moment = new(2026, 10, 20, 12, 30, 5, 123);

    /// <summary>Counts the write calls: a torn line would be two of them.</summary>
    private sealed class RecordingStream : MemoryStream
    {
        public List<string> Writes { get; } = [];

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes.Add(System.Text.Encoding.UTF8.GetString(buffer));
            base.Write(buffer);
        }
    }

    [Fact]
    public void A_line_is_one_write_call_with_a_time_stamp()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        writer.WriteLine("[app] started");
        Assert.Equal(["2026-10-20 12:30:05.123 [app] started\r\n"], stream.Writes);
    }

    [Fact]
    public void A_multi_line_message_stays_in_one_piece()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        writer.WriteLine("[app] fatal: System.Exception: x\r\n   at A()\r\n   at B()");
        Assert.Single(stream.Writes);
        Assert.EndsWith("   at B()\r\n", stream.Writes[0]);
    }

    [Fact]
    public void Text_without_a_newline_waits_for_the_rest_of_its_line()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        writer.Write("[a] ");
        writer.Write("b");
        Assert.Empty(stream.Writes);
        writer.WriteLine("c");
        Assert.Equal(["2026-10-20 12:30:05.123 [a] bc\r\n"], stream.Writes);
    }

    [Fact]
    public void Two_writers_on_threads_never_tear_a_line()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        Parallel.For(0, 4, t =>
        {
            for (var i = 0; i < 500; i++) writer.WriteLine($"[t{t}] line {i} " + new string('x', 200));
        });
        Assert.Equal(2000, stream.Writes.Count);
        Assert.All(stream.Writes, w =>
        {
            Assert.EndsWith("\r\n", w);
            Assert.Single(w.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        });
    }

    [Fact]
    public void A_broken_stream_never_throws_into_the_program()
    {
        var stream = new MemoryStream();
        var writer = new LineWriter(stream, () => Moment);
        stream.Dispose();
        writer.WriteLine("after the end");
    }
}
