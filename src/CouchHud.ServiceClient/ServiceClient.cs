using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CouchHud.Service.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.ServiceClient;

/// <summary>How a call ended, before the service's own code is looked at.</summary>
public enum CallOutcome
{
    /// <summary>The service answered ok.</summary>
    Ok,

    /// <summary>The service answered and said no (<see cref="ServiceReply.Code"/> says why).</summary>
    Refused,

    /// <summary>The service is not installed.</summary>
    NotInstalled,

    /// <summary>The service is installed but not running (stopped, starting, stopping).</summary>
    NotRunning,

    /// <summary>The SCM or the pipe could not be reached in time, or the connection broke.</summary>
    Unreachable,

    /// <summary>
    /// The pipe is held by a process that is not the service, or the service stopped because its pipe name was taken. Nothing was
    /// sent: a password or a code never goes to such a server.
    /// </summary>
    Squatted,

    /// <summary>The answer broke the protocol (wrong version, ok with an error code, unknown fields, not JSON).</summary>
    Inconsistent,

    /// <summary>No answer within the time limit.</summary>
    Timeout
}

/// <summary>
/// The answer to one call. <see cref="Code"/> is the service's code (see <see cref="ErrorCodes"/>) when it answered, otherwise a
/// local one ("notInstalled", "squatted", …). The data stays raw JSON until the caller asks for its type.
/// </summary>
public sealed class ServiceReply
{
    private readonly JsonElement? _data;

    private ServiceReply(CallOutcome outcome, string code, JsonElement? data)
    {
        Outcome = outcome;
        Code = code;
        _data = data;
    }

    public CallOutcome Outcome { get; }

    public string Code { get; }

    public bool Ok => Outcome == CallOutcome.Ok;

    public bool HasData => _data is not null;

    /// <summary>The data as <typeparamref name="T"/>; null when there is none or it does not fit (the caller decides what that means).</summary>
    public T? DataAs<T>(JsonTypeInfo<T> info) where T : class
    {
        if (_data is not { } data) return null;
        try
        {
            return data.Deserialize(info);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static ServiceReply Local(CallOutcome outcome) => new(outcome, LocalCode(outcome), null);

    internal static ServiceReply Answer(bool ok, string code, JsonElement? data) =>
        new(ok ? CallOutcome.Ok : CallOutcome.Refused, code, data);

    public static string LocalCode(CallOutcome outcome) => outcome switch
    {
        CallOutcome.NotInstalled => "notInstalled",
        CallOutcome.NotRunning => "notRunning",
        CallOutcome.Unreachable => "unreachable",
        CallOutcome.Squatted => "squatted",
        CallOutcome.Inconsistent => "inconsistent",
        CallOutcome.Timeout => ErrorCodes.Timeout,
        _ => ErrorCodes.Ok
    };

    /// <summary>For logs: the outcome and the code, never the data.</summary>
    public override string ToString() => $"{Outcome} ({Code})";
}

/// <summary>Reads an answer of the service: <c>{v, ok, code, data?}</c>, nothing else. Pure, never throws.</summary>
public static class ReplyParser
{
    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "v", "ok", "code", "data" };

    public static ServiceReply Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0) return ServiceReply.Local(CallOutcome.Inconsistent);
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), Strict);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ServiceReply.Local(CallOutcome.Inconsistent);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name)) return ServiceReply.Local(CallOutcome.Inconsistent);
            }
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Protocol.Version)
            {
                return ServiceReply.Local(CallOutcome.Inconsistent);
            }
            if (!root.TryGetProperty("ok", out var okElement) || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return ServiceReply.Local(CallOutcome.Inconsistent);
            if (!root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String) return ServiceReply.Local(CallOutcome.Inconsistent);
            var ok = okElement.ValueKind == JsonValueKind.True;
            var code = codeElement.GetString()!;
            if (!IsCodeText(code)) return ServiceReply.Local(CallOutcome.Inconsistent);
            // ok goes with "ok" or "streamReady" only, and a failure never says "ok": anything else is a service that contradicts itself.
            var okCode = code is ErrorCodes.Ok or ErrorCodes.StreamReady;
            if (ok != okCode) return ServiceReply.Local(CallOutcome.Inconsistent);
            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            if (!ok && data is not null) return ServiceReply.Local(CallOutcome.Inconsistent);
            return ServiceReply.Answer(ok, code, data);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return ServiceReply.Local(CallOutcome.Inconsistent);
        }
    }

    /// <summary>A code is a short word of ASCII letters (a newer service may add codes; they are shown as a general failure).</summary>
    private static bool IsCodeText(string code) => code.Length is > 0 and <= 40 && code.All(char.IsAsciiLetter);
}

/// <summary>Limits of the client. The defaults follow the design: connect 2 s, write 5 s, answer 10 s (longer per call where needed).</summary>
public sealed record ServiceClientOptions(string PipeName, TimeSpan ConnectTimeout, TimeSpan WriteTimeout, TimeSpan ReadTimeout)
{
    public static readonly ServiceClientOptions Default = new(Protocol.PipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    /// <summary>How long the service may take to check a streamed setup and start it (hashing up to 400 MB).</summary>
    public TimeSpan StreamVerifyTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>
/// The pipe client of the shell. Every call is one connection:
/// <list type="number">
/// <item>The SCM must report the service running with a process id (otherwise nothing is connected at all).</item>
/// <item>Connect with exactly ReadData | WriteData | ReadAttributes | WriteAttributes | Synchronize (not InOut: that holds
/// FILE_CREATE_PIPE_INSTANCE), impersonation level Identification (a false server cannot act as the user), no inheritance.</item>
/// <item>Before a single byte is sent: <c>GetNamedPipeServerProcessId</c> must be the SCM's process id of the service and not 0.
/// Otherwise the pipe is squatted and the call ends as <see cref="CallOutcome.Squatted"/>.</item>
/// <item>Message mode, one framed request (<see cref="FrameCodec"/>), one framed answer, each within its time limit.</item>
/// </list>
/// Arguments and answers are never logged; request bytes are wiped after sending.
/// </summary>
public sealed class ServiceClient(IServiceControl scm, ServiceClientOptions? options = null, Func<SafePipeHandle, int?>? serverProcessId = null)
{
    private readonly ServiceClientOptions _options = options ?? ServiceClientOptions.Default;
    private readonly Func<SafePipeHandle, int?> _serverProcessId = serverProcessId ?? PipeServer.ProcessIdOf;

    /// <summary>The access the client asks for (design §2, P0 spike): nothing that could create a pipe instance.</summary>
    public const PipeAccessRights ClientAccess =
        PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.WriteAttributes | PipeAccessRights.Synchronize;

    public ServiceClientOptions Options => _options;

    /// <summary>A request without arguments.</summary>
    public Task<ServiceReply> CallAsync(Operation op, string? grant = null, TimeSpan? readTimeout = null, CancellationToken cancellation = default) =>
        SendAsync(() => ProtocolJson.SerializeRequest(op, grant), readTimeout, null, cancellation);

    /// <summary>A request with its typed arguments (a password in them is only in the wiped request bytes and the caller's string).</summary>
    public Task<ServiceReply> CallAsync<TArgs>(Operation op, TArgs args, string? grant = null, TimeSpan? readTimeout = null, CancellationToken cancellation = default) =>
        SendAsync(() => ProtocolJson.SerializeRequest(op, args, grant), readTimeout, null, cancellation);

    /// <summary>A request whose JSON the caller builds itself (e.g. without a .NET string of a password); the bytes are wiped after sending.</summary>
    public Task<ServiceReply> CallBuiltAsync(Func<byte[]> build, TimeSpan? readTimeout = null, CancellationToken cancellation = default) =>
        SendAsync(build, readTimeout, null, cancellation);

    /// <summary>
    /// A setup for the service: the header, then (after "streamReady") exactly <see cref="SetupStreamArgs.Size"/> bytes of
    /// <paramref name="source"/>, then the service's verdict. The service verifies everything again; it never opens a path of ours.
    /// </summary>
    public Task<ServiceReply> StreamAsync(Operation op, SetupStreamArgs header, Stream source, string? grant = null, CancellationToken cancellation = default) =>
        SendAsync(() => ProtocolJson.SerializeRequest(op, header, grant), null, async (pipe, first) =>
        {
            if (!first.Ok) return first;
            // Only "streamReady" lets the bytes go; a plain "ok" before the bytes is a service that is not following the protocol.
            if (first.Code != ErrorCodes.StreamReady) return ServiceReply.Local(CallOutcome.Inconsistent);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                limit.CancelAfter(StreamCodec.DeadlineFor(header.Size));
                try
                {
                    await StreamCodec.WriteAsync(pipe, source, header.Size, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    return ServiceReply.Local(CallOutcome.Timeout);
                }
            }
            return await ReadReplyAsync(pipe, _options.StreamVerifyTimeout, cancellation).ConfigureAwait(false);
        }, cancellation);

    private async Task<ServiceReply> SendAsync(Func<byte[]> build, TimeSpan? readTimeout, Func<NamedPipeClientStream, ServiceReply, Task<ServiceReply>>? afterFirst, CancellationToken cancellation)
    {
        var (pipe, refusal) = await ConnectAsync(cancellation).ConfigureAwait(false);
        if (pipe is null) return refusal!;
        await using (pipe)
        {
            try
            {
                var payload = build();
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    limit.CancelAfter(_options.WriteTimeout);
                    await FrameCodec.WriteAsync(pipe, payload, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    return ServiceReply.Local(CallOutcome.Timeout);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
                var reply = await ReadReplyAsync(pipe, readTimeout ?? _options.ReadTimeout, cancellation).ConfigureAwait(false);
                return afterFirst is null ? reply : await afterFirst(pipe, reply).ConfigureAwait(false);
            }
            catch (FrameException ex) when (ex.Code is ErrorCodes.StreamShort or ErrorCodes.StreamTooLarge)
            {
                // Our own source did not have the announced bytes: nothing the service did.
                return ServiceReply.Answer(false, ex.Code, null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
            {
                return ServiceReply.Local(CallOutcome.Unreachable);
            }
        }
    }

    private async Task<ServiceReply> ReadReplyAsync(Stream pipe, TimeSpan timeout, CancellationToken cancellation)
    {
        byte[]? frame;
        try
        {
            frame = await FrameCodec.ReadAsync(pipe, timeout, Protocol.MaxMessageBytes, cancellation).ConfigureAwait(false);
        }
        catch (FrameException ex) when (ex.Code == ErrorCodes.Timeout)
        {
            return ServiceReply.Local(CallOutcome.Timeout);
        }
        catch (FrameException)
        {
            return ServiceReply.Local(CallOutcome.Inconsistent);
        }
        if (frame is null) return ServiceReply.Local(CallOutcome.Unreachable);
        try
        {
            return ReplyParser.Parse(frame);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }
    }

    /// <summary>The checked connection, or why there is none (then nothing was sent).</summary>
    private async Task<(NamedPipeClientStream? Pipe, ServiceReply? Refusal)> ConnectAsync(CancellationToken cancellation)
    {
        var before = scm.Query();
        if (ServerCheck.BeforeConnect(before) is { } notRunning) return (null, ServiceReply.Local(notRunning));

        var pipe = new NamedPipeClientStream(".", _options.PipeName, ClientAccess, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(_options.ConnectTimeout, cancellation).ConfigureAwait(false);
            // The server has to be the service before anything is written: a squatter gets nothing, not even the operation's name.
            var server = _serverProcessId(pipe.SafePipeHandle);
            var verdict = ServerCheck.AfterConnect(server, before.ProcessId);
            if (verdict == ServerVerdict.Retry)
            {
                // The service may have restarted between the SCM query and the connect: ask the SCM once more.
                verdict = ServerCheck.AfterConnect(server, scm.Query().ProcessId) == ServerVerdict.Service ? ServerVerdict.Service : ServerVerdict.Squatted;
            }
            // And once more after the match: the SCM must still report the service running as exactly that process (review L1).
            if (verdict == ServerVerdict.Service && !ServerCheck.StillTheService(scm.Query(), server)) verdict = ServerVerdict.Squatted;
            if (verdict != ServerVerdict.Service)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return (null, ServiceReply.Local(CallOutcome.Squatted));
            }
            pipe.ReadMode = PipeTransmissionMode.Message;
            return (pipe, null);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return (null, ServiceReply.Local(CallOutcome.Unreachable));
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>GetNamedPipeServerProcessId: who holds the server end.</summary>
    public static class PipeServer
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        public static int? ProcessIdOf(SafePipeHandle pipe)
        {
            try
            {
                return GetNamedPipeServerProcessId(pipe, out var pid) && pid is > 0 and <= int.MaxValue ? (int)pid : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }
}

/// <summary>The verdict of the server check.</summary>
public enum ServerVerdict
{
    /// <summary>The pipe server is the service's process.</summary>
    Service,

    /// <summary>The numbers differ: ask the SCM again (the service may have just restarted).</summary>
    Retry,

    /// <summary>Not the service: send nothing.</summary>
    Squatted
}

/// <summary>The rules of the server check, without any Windows call (tested on their own).</summary>
public static class ServerCheck
{
    /// <summary>Before connecting: null when the SCM says the service runs with a process; otherwise why no connection is tried.</summary>
    public static CallOutcome? BeforeConnect(ScmInfo info) => info.State switch
    {
        ScmState.Running when info.ProcessId > 0 => null,
        ScmState.Running => CallOutcome.Unreachable,
        ScmState.NotInstalled => CallOutcome.NotInstalled,
        // Stopped with the service's own "someone holds my pipe name" code: whatever answers on that name now is not the service.
        ScmState.Stopped when info.StoppedBySquatting => CallOutcome.Squatted,
        ScmState.Stopped or ScmState.StartPending or ScmState.StopPending => CallOutcome.NotRunning,
        _ => CallOutcome.Unreachable
    };

    /// <summary>After the match: the SCM still reports the service running, and as the same process as the pipe's server.</summary>
    public static bool StillTheService(ScmInfo after, int? serverProcessId) =>
        after.State == ScmState.Running && serverProcessId is > 0 && after.ProcessId == serverProcessId;

    /// <summary>After connecting, before sending: the server's process must be the SCM's process of the service, and neither may be 0 or unknown.</summary>
    public static ServerVerdict AfterConnect(int? serverProcessId, int serviceProcessId)
    {
        if (serverProcessId is not > 0) return ServerVerdict.Squatted;
        if (serviceProcessId <= 0) return ServerVerdict.Squatted;
        return serverProcessId == serviceProcessId ? ServerVerdict.Service : ServerVerdict.Retry;
    }
}
