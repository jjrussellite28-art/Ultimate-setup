using System.Security.Cryptography;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.Logging;

namespace CouchHud.Service.Pipe;

/// <summary>At most <see cref="PerAccount"/> open connections per account, so one account cannot hold all pipe instances.</summary>
public sealed class ConnectionQuota(int perAccount)
{
    public int PerAccount { get; } = perAccount;

    private readonly Dictionary<string, int> _open = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>A lease to dispose when the connection ends, or null when the account is at its limit.</summary>
    public IDisposable? TryEnter(string sid)
    {
        lock (_lock)
        {
            _open.TryGetValue(sid, out var count);
            if (count >= PerAccount) return null;
            _open[sid] = count + 1;
        }
        return new Lease(this, sid);
    }

    private void Leave(string sid)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(sid, out var count)) return;
            if (count <= 1) _open.Remove(sid);
            else _open[sid] = count - 1;
        }
    }

    private sealed class Lease(ConnectionQuota owner, string sid) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Leave(sid);
        }
    }
}

/// <summary>Limits of one connection.</summary>
public sealed record ConnectionLimits(TimeSpan ReadTimeout, TimeSpan WriteTimeout, int MaxRequests)
{
    /// <summary>5 s for a whole request (header and body), 5 s for the client to take an answer, 8 requests per connection.</summary>
    public static readonly ConnectionLimits Default = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 8);
}

/// <summary>
/// One connection over any stream (the pipe in the service, an in-memory pair in tests): caller check before anything is read,
/// the per-account quota, then up to <see cref="ConnectionLimits.MaxRequests"/> framed requests, each within the read timeout.
/// A broken frame ends the connection (the stream is out of step after it).
/// </summary>
public sealed class ConnectionHandler(Dispatcher dispatcher, ConnectionQuota quota, DenyLog log, ConnectionLimits limits)
{
    public async Task HandleAsync(Stream stream, CallerInfo? caller, CancellationToken cancellation)
    {
        if (Authorizer.CheckCaller(caller) is { } problem)
        {
            log.Error(caller?.Sid, $"[deny] connection refused: {problem}; {caller?.Describe() ?? "no identity"}");
            await TryWriteAsync(stream, Dispatcher.Fail(ErrorCodes.Denied), cancellation).ConfigureAwait(false);
            return;
        }

        using var lease = quota.TryEnter(caller!.Sid);
        if (lease is null)
        {
            log.Error(caller.Sid, $"[deny] too many connections for the account; {caller.Describe()}");
            await TryWriteAsync(stream, Dispatcher.Fail(ErrorCodes.Busy), cancellation).ConfigureAwait(false);
            return;
        }

        for (var i = 0; i < limits.MaxRequests && !cancellation.IsCancellationRequested; i++)
        {
            byte[]? frame;
            try
            {
                frame = await FrameCodec.ReadAsync(stream, limits.ReadTimeout, Protocol.MaxMessageBytes, cancellation).ConfigureAwait(false);
            }
            catch (FrameException ex)
            {
                log.Error(caller.Sid, $"[reject] frame: {ex.Code}; {caller.Describe()}");
                await TryWriteAsync(stream, Dispatcher.Fail(ex.Code), cancellation).ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                return;   // the client went away
            }
            if (frame is null) return;   // the client ended cleanly

            DispatchResult result;
            try
            {
                result = await dispatcher.DispatchWithStreamAsync(caller, frame, cancellation).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
            }
            using var receiver = result.Receiver;
            try
            {
                if (!await TryWriteAsync(stream, result.Response, cancellation).ConfigureAwait(false)) return;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(result.Response);
            }
            if (receiver is not null)
            {
                // The bytes of a setup follow (StreamCodec, with its own size and time limits); one answer after them, then the
                // connection ends: after a stream, broken or not, nothing else on it is trusted to be in step.
                var final = await ReceiveAsync(receiver, stream, caller, cancellation).ConfigureAwait(false);
                await TryWriteAsync(stream, final, cancellation).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task<byte[]> ReceiveAsync(StreamReceiver receiver, Stream stream, CallerInfo caller, CancellationToken cancellation)
    {
        try
        {
            return await receiver.ReceiveAsync(stream, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            log.Error(caller.Sid, $"[error] stream failed: {ex.GetType().Name}; {caller.Describe()}");
            return Dispatcher.Fail(ErrorCodes.Internal);
        }
    }

    /// <summary>Writes one answer within the write timeout; false when the client did not take it (the connection ends).</summary>
    private async Task<bool> TryWriteAsync(Stream stream, byte[] payload, CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(limits.WriteTimeout);
        try
        {
            await FrameCodec.WriteAsync(stream, payload, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }
}
