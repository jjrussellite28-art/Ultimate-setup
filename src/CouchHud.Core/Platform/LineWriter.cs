using System.Text;

namespace CouchHud.Core.Platform;

/// <summary>
/// A log writer that hands every line to the stream as exactly one write call. The HUD and the watcher process write to the
/// same log file; with a stream opened for appending only (the file system then appends at the current end for every write
/// call) a line is never torn apart or overwritten by the other process. Lines get a time stamp; text written without a
/// newline waits for the rest of its line.
/// </summary>
public sealed class LineWriter(Stream stream, Func<DateTime>? now = null) : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _partial = new();
    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);

    public override Encoding Encoding { get; } = new UTF8Encoding(false);

    public override void Write(char value)
    {
        lock (_gate)
        {
            _partial.Append(value);
            if (value == '\n') FlushPartial();
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_gate)
        {
            _partial.Append(value);
            if (value.Contains('\n')) FlushPartial();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            var line = _partial.Length > 0 ? _partial.ToString() + value : value;
            _partial.Clear();
            Emit($"{_now():yyyy-MM-dd HH:mm:ss.fff} {line}\r\n");
        }
    }

    public override void Flush()
    {
        lock (_gate) stream.Flush();
    }

    /// <summary>Text with newlines written through Write(): the complete lines go out in one piece, the rest waits.</summary>
    private void FlushPartial()
    {
        var text = _partial.ToString();
        var end = text.LastIndexOf('\n') + 1;
        _partial.Clear().Append(text, end, text.Length - end);
        Emit(text[..end].Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    private void Emit(string text)
    {
        try
        {
            stream.Write(Encoding.GetBytes(text));
            stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging is best effort.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) stream.Dispose();
        base.Dispose(disposing);
    }
}
