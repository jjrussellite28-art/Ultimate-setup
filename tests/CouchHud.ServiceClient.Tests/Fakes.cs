using System.IO.Pipes;
using CouchHud.Core.Platform;
using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient.Tests;

/// <summary>An SCM that says what the test wants; counts the start requests.</summary>
internal sealed class FakeScm(ScmInfo info) : IServiceControl
{
    public ScmInfo Info { get; set; } = info;

    public int Starts { get; private set; }

    /// <summary>What a start turns the state into (null: the start is refused).</summary>
    public ScmInfo? AfterStart { get; set; }

    public static FakeScm RunningHere() => new(new ScmInfo(ScmState.Running, Environment.ProcessId));

    public ScmInfo Query() => Info;

    public bool TryStart()
    {
        Starts++;
        if (AfterStart is null) return false;
        Info = AfterStart;
        return true;
    }
}

/// <summary>
/// An in-process pipe server under a random name, message mode like the service. <see cref="Handler"/> gets each request (the raw JSON)
/// and the pipe, and returns the answer to send (null: send nothing). Records what arrived. Never the real service's name.
/// </summary>
internal sealed class FakePipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<byte[]> _requests = [];
    private int _connections;

    public FakePipeServer(Func<byte[], Stream, Task<byte[]?>> handler)
    {
        Name = "CouchHud.Test." + Guid.NewGuid().ToString("N");
        Handler = handler;
        var ready = new TaskCompletionSource();
        _loop = Task.Run(() => LoopAsync(ready));
        ready.Task.Wait(TimeSpan.FromSeconds(5));
    }

    public FakePipeServer(Func<byte[], byte[]?> handler) : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    public string Name { get; }

    public Func<byte[], Stream, Task<byte[]?>> Handler { get; set; }

    public int Connections => Volatile.Read(ref _connections);

    public int RequestCount
    {
        get { lock (_requests) return _requests.Count; }
    }

    public IReadOnlyList<string> Ops
    {
        get { lock (_requests) return _requests.Select(OpOf).ToList(); }
    }

    public byte[] Request(int index)
    {
        lock (_requests) return _requests[index];
    }

    public ServiceClientOptions Options(TimeSpan? read = null) =>
        new(Name, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), read ?? TimeSpan.FromSeconds(5)) { StreamVerifyTimeout = TimeSpan.FromSeconds(5) };

    private async Task LoopAsync(TaskCompletionSource ready)
    {
        while (!_stop.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 8, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            ready.TrySetResult();
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => ServeAsync(server));
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                while (true)
                {
                    var request = await FrameCodec.ReadAsync(server, TimeSpan.FromSeconds(10));
                    if (request is null) return;
                    lock (_requests) _requests.Add(request);
                    var answer = await Handler(request, server);
                    if (answer is null) continue;
                    await FrameCodec.WriteAsync(server, answer);
                }
            }
            catch (Exception ex) when (ex is IOException or FrameException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static string OpOf(byte[] request)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(request);
        return doc.RootElement.GetProperty("op").GetString()!;
    }

    public static byte[] Ok() => ProtocolJson.Serialize(Response.Success());

    public static byte[] Fail(string code) => ProtocolJson.Serialize(Response.Failure(code));

    public static byte[] Ok<T>(T data) => ProtocolJson.Serialize(Response<T>.Success(data));

    public static byte[] Hello(string version = "0.4.0", bool unsafeInstall = false, int protocol = Protocol.Version) =>
        Ok(new HelloData(protocol, version, ["Hello", "Ping", "Status"], unsafeInstall, "none"));

    public static byte[] Raw(string json) => System.Text.Encoding.UTF8.GetBytes(json);
}

/// <summary>In-memory registry: values per key, DWORDs as int, strings as string.</summary>
internal sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, Dictionary<string, object>> _data = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, object> Key(string key) =>
        _data.TryGetValue(key, out var v) ? v : _data[key] = new(StringComparer.OrdinalIgnoreCase);

    public string? ReadString(string key, string name) => _data.TryGetValue(key, out var v) && v.TryGetValue(name, out var o) ? o as string : null;

    public IReadOnlyDictionary<string, string> ReadValues(string key) =>
        _data.TryGetValue(key, out var v) ? v.Where(p => p.Value is string).ToDictionary(p => p.Key, p => (string)p.Value, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>();

    public byte[]? ReadBinary(string key, string name) => null;

    public int? ReadDword(string key, string name) => _data.TryGetValue(key, out var v) && v.TryGetValue(name, out var o) && o is int i ? i : null;

    public void WriteString(string key, string name, string value) => Key(key)[name] = value;

    public void WriteDword(string key, string name, int value) => Key(key)[name] = value;

    public void DeleteValue(string key, string name)
    {
        if (_data.TryGetValue(key, out var v)) v.Remove(name);
    }
}
