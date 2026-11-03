using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CouchHud.Sdk;

namespace CouchHud.Core.Integrations.Tv;

/// <summary>Limits of a <see cref="TvLink"/>; the tests point it at a fake TV on loopback with short timeouts.</summary>
public sealed record TvLinkOptions
{
    /// <summary>Only for the tests' fake TV on 127.0.0.1 (and the plugin repositories' tests); the app never sets it.</summary>
    public bool AllowLoopback { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxMessageBytes { get; init; } = 256 * 1024;
    public int MaxSendBytes { get; init; } = 64 * 1024;
    public int MaxHttpBytes { get; init; } = 64 * 1024;
    public int MaxBodyBytes { get; init; } = 16 * 1024;
    public int MaxPathLength { get; init; } = 1024;
    /// <summary>Web sockets open at the same time.</summary>
    public int MaxChannels { get; init; } = 4;
    /// <summary>New connections (web sockets and HTTP requests) within <see cref="OpenWindow"/>; more are refused (a plugin that hammers the TV).</summary>
    public int MaxOpens { get; init; } = 30;
    public TimeSpan OpenWindow { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The only connection to a TV in CouchHUD, handed to a TV plugin as <see cref="ITvLink"/>. Bound to one IP address of the local network
/// (checked again on the address actually connected, no DNS, no proxy, no redirects, no cookies) and to the ports the provider declared.
/// TLS where the port says so, with the TV's self-signed certificate pinned by its public key: at pairing the first key seen is taken
/// (trust on first use) and every further TLS connection of the pairing must show the same; in a session only the pinned key. A plain
/// port that is not key-free needs the user's confirmation at pairing (expert mode) and is never used for a TLS pairing; a key-free port
/// takes only HTTP requests that do not contain the key. Sizes, time and the number of connections are bounded. A refusal of the link is
/// remembered (<see cref="Fault"/>) so a plugin cannot hide it behind another error.
/// </summary>
public sealed class TvLink : ITvLink, IAsyncDisposable
{
    private readonly IPAddress _address;
    private readonly Dictionary<int, TvPort> _ports;
    private readonly TvLinkOptions _o;
    private readonly bool _pairing;
    private readonly bool _secureSession;
    private readonly string? _key;
    private readonly object _lock = new();
    private readonly List<Channel> _channels = [];
    private readonly Queue<long> _opens = new();
    private string? _pin;
    private bool _disposed;
    private TvError? _fault;
    private bool _tlsFailed;

    private TvLink(IPAddress address, IReadOnlyList<TvPort> ports, TvLinkOptions? options, bool pairing, bool plainAllowed, bool secureSession, string? pin, string? key)
    {
        _o = options ?? new TvLinkOptions();
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!TvAddressPolicy.IsAllowed(address, _o.AllowLoopback)) throw new TvException(TvError.NotLocal);
        _address = address;
        _ports = CheckPorts(ports) ?? throw new TvException(TvError.Blocked, "ports");
        _pairing = pairing;
        PlainAllowed = plainAllowed;
        _secureSession = secureSession;
        _pin = pin;
        _key = key;
        Address = TvText.Canonical(address);
    }

    /// <summary>A link for pairing: <paramref name="allowPlain"/> only after the user confirmed an unencrypted pairing (and the caller checked the downgrade history).</summary>
    public static TvLink ForPairing(IPAddress address, IReadOnlyList<TvPort> ports, bool allowPlain, TvLinkOptions? options = null) =>
        new(address, ports, options, pairing: true, plainAllowed: allowPlain, secureSession: false, pin: null, key: null);

    /// <summary>
    /// A link for a session of a stored pairing. <paramref name="secure"/>: a TLS pairing, so every TLS connection must show <paramref name="pin"/>
    /// and plain ports are only the key-free ones. <paramref name="key"/> is only used to refuse requests that would carry it unencrypted.
    /// </summary>
    public static TvLink ForSession(IPAddress address, IReadOnlyList<TvPort> ports, bool secure, string? pin, string key, TvLinkOptions? options = null)
    {
        if (secure && string.IsNullOrEmpty(pin)) throw new TvException(TvError.CertChanged);
        return new TvLink(address, ports, options, pairing: false, plainAllowed: !secure, secureSession: secure, pin: string.IsNullOrEmpty(pin) ? null : pin, key: key);
    }

    /// <summary>The ports a provider may declare: 1 to 65535, no port twice, at most <see cref="TvPort.MaxPorts"/>. Null when the list is not fine.</summary>
    public static Dictionary<int, TvPort>? CheckPorts(IReadOnlyList<TvPort>? ports)
    {
        if (ports is null || ports.Count is 0 or > TvPort.MaxPorts) return null;
        var map = new Dictionary<int, TvPort>();
        foreach (var p in ports)
        {
            if (p is null || p.Port is < 1 or > 65535 || !map.TryAdd(p.Port, p)) return null;
            // TLS and key-free together means TLS (the key-free rule is only for plain ports).
            if (p.Tls && p.KeyFree) map[p.Port] = p with { KeyFree = false };
        }
        return map;
    }

    public string Address { get; }

    public bool PlainAllowed { get; }

    /// <summary>The pin of the TLS certificate seen (pairing) or required (session); null without TLS.</summary>
    public string? Pin
    {
        get { lock (_lock) return _pin; }
    }

    /// <summary>A TLS connection on a port that carries the key was made.</summary>
    public bool UsedTls { get; private set; }

    /// <summary>A plain connection on a port that carries the key was made (the pairing is not encrypted then).</summary>
    public bool UsedPlain { get; private set; }

    /// <summary>The pairing went over TLS only (on the ports that carry the key).</summary>
    public bool Secure => UsedTls && !UsedPlain;

    /// <summary>The link's own refusal (CertChanged, PlainOnly, Blocked, NotLocal), if one happened: it counts over whatever the plugin made of it.</summary>
    public TvError? Fault
    {
        get { lock (_lock) return _fault; }
    }

    /// <summary>
    /// A TLS port was tried and failed at the TLS level (handshake refused, connection reset after it was made): later plain ports that
    /// carry the key are refused in this link (someone in the network may be forcing a fall back to the unencrypted port).
    /// </summary>
    private bool TlsFailed
    {
        get { lock (_lock) return _tlsFailed; }
    }

    private TvException Refuse(TvError code, string detail)
    {
        lock (_lock) _fault ??= code;
        return new TvException(code, detail);
    }

    /// <summary>The checks before any connection: the port, plain or TLS, the rate, the path.</summary>
    private TvPort Admit(int port, string path, bool webSocket)
    {
        lock (_lock)
        {
            if (_disposed) throw new TvException(TvError.Unreachable, "closed");
        }
        if (!_ports.TryGetValue(port, out var declared)) throw Refuse(TvError.Blocked, "port");
        if (!ValidPath(path)) throw Refuse(TvError.Blocked, "path");
        if (!declared.Tls)
        {
            if (declared.KeyFree)
            {
                if (webSocket) throw Refuse(TvError.Blocked, "key-free port");
            }
            else if (TlsFailed)
            {
                throw Refuse(TvError.Blocked, "plain after a TLS failure");
            }
            else if (!PlainAllowed)
            {
                throw Refuse(_pairing ? TvError.PlainOnly : TvError.Blocked, "plain");
            }
        }
        lock (_lock)
        {
            var now = Environment.TickCount64;
            while (_opens.Count > 0 && now - _opens.Peek() > _o.OpenWindow.TotalMilliseconds) _opens.Dequeue();
            if (_opens.Count >= _o.MaxOpens)
            {
                _fault ??= TvError.Blocked;
                throw new TvException(TvError.Blocked, "rate");
            }
            _opens.Enqueue(now);
        }
        return declared;
    }

    private bool ValidPath(string? path) =>
        path is { Length: > 0 } && path.Length <= _o.MaxPathLength && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal)
        && path.All(c => c is > ' ' and < (char)127 and not '\\');

    private static bool ValidHeader(string? value, int max) => value is null || (value.Length <= max && value.All(c => c is >= ' ' and < (char)127));

    /// <summary>
    /// A best-effort safety net against plugin bugs, not a barrier (a plugin is not sandboxed and can hide a key in many ways): in a
    /// session, nothing that goes over a plain port may carry the key as is, URL-encoded (also twice), as JSON unicode escapes or XML numeric
    /// references, in base64 (also URL-safe, at any alignment) or as hex. Keys under 12 characters only count as a whole token, so a
    /// short numeric key does not block an innocent number inside a longer one.
    /// </summary>
    private void CheckNoKey(TvPort port, params string?[] parts)
    {
        if (port.Tls || _key is null) return;
        foreach (var part in parts)
        {
            if (part is not null && KeyGuard.Carries(part, _key)) throw Refuse(TvError.Blocked, "key on a plain port");
        }
    }

    private Uri UriFor(TvPort port, string path, bool webSocket)
    {
        var host = _address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{_address}]" : _address.ToString();
        var scheme = webSocket ? (port.Tls ? "wss" : "ws") : (port.Tls ? "https" : "http");
        var uri = new Uri($"{scheme}://{host}:{port.Port}{path}", UriKind.Absolute);
        // The path cannot move the request elsewhere: same host, same port, or nothing.
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !ip.Equals(_address) || uri.Port != port.Port || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Refuse(TvError.Blocked, "address");
        }
        return uri;
    }

    /// <summary>A handler for exactly one connection to this TV: the connect callback takes only this link's address, TLS is pinned.</summary>
    private HttpMessageInvoker Invoker(TvPort port, Action<string> sawPin)
    {
        var address = _address;
        var allowLoopback = _o.AllowLoopback;
        string? expected;
        lock (_lock) expected = _pin;
        // Whenever a pin exists it is enforced, also in a plain session (a TLS port there still has to show the pairing's key).
        var check = port.Tls && (expected is not null || (!_pairing && _secureSession));
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = _o.ConnectTimeout,
            MaxResponseHeadersLength = 16,
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host.Trim('[', ']');
                if (!IPAddress.TryParse(host, out var ip) || !ip.Equals(address) || !TvAddressPolicy.IsAllowed(ip, allowLoopback)) throw new TvException(TvError.NotLocal);
                var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // The TV's certificate is self-signed: the chain cannot be checked, the key is pinned instead. Validation is replaced on
                // this one handler only, never globally.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    var own = certificate as X509Certificate2 is null ? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()) : null;
                    string seen;
                    try
                    {
                        seen = TvText.SpkiPin(own ?? (X509Certificate2)certificate);
                    }
                    finally
                    {
                        own?.Dispose();
                    }
                    sawPin(seen);
                    return !check || TvText.SamePin(expected, seen);
                }
            }
        };
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        return invoker;
    }

    /// <summary>After a connection: what it tells about the pairing (TLS or plain, the pin).</summary>
    private void Record(TvPort port, string? seenPin)
    {
        lock (_lock)
        {
            if (port.Tls)
            {
                UsedTls = true;
                if (_pairing && _pin is null && seenPin is not null) _pin = seenPin;
                // Two connections of one pairing that raced each other with different certificates: the pairing is not kept.
                else if (_pairing && seenPin is not null && !TvText.SamePin(_pin, seenPin)) _fault ??= TvError.CertChanged;
            }
            else if (!port.KeyFree)
            {
                UsedPlain = true;
            }
        }
    }

    /// <summary>A failed connection as a TvException: a changed certificate first, then TLS, timeout, unreachable.</summary>
    private Exception Fail(Exception ex, TvPort port, string? seenPin, bool timedOut, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new OperationCanceledException(ct);
        if (Find<TvException>(ex) is { } tv)
        {
            if (tv.Code == TvError.NotLocal) lock (_lock) _fault ??= TvError.NotLocal;
            return tv;
        }
        string? expected;
        lock (_lock) expected = _pin;
        if (port.Tls && expected is not null && seenPin is not null && !TvText.SamePin(expected, seenPin)) return Refuse(TvError.CertChanged, "pin");
        if (port.Tls && !timedOut && (Find<AuthenticationException>(ex) is not null || Find<IOException>(ex) is not null))
        {
            lock (_lock) _tlsFailed = true;
        }
        if (Find<AuthenticationException>(ex) is not null) return new TvException(TvError.BadAnswer, "TLS");
        if (timedOut) return new TvException(TvError.Timeout);
        return new TvException(TvError.Unreachable);
    }

    public async Task<ITvChannel> OpenAsync(int port, string pathAndQuery, CancellationToken ct)
    {
        var declared = Admit(port, pathAndQuery, webSocket: true);
        if (!_pairing) CheckNoKey(declared, pathAndQuery);
        lock (_lock)
        {
            _channels.RemoveAll(c => !c.Open);
            if (_channels.Count >= _o.MaxChannels)
            {
                _fault ??= TvError.Blocked;
                throw new TvException(TvError.Blocked, "channels");
            }
        }
        var uri = UriFor(declared, pathAndQuery, webSocket: true);
        string? seenPin = null;
        var invoker = Invoker(declared, p => seenPin = p);
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        ws.Options.HttpVersion = HttpVersion.Version11;
        ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_o.ConnectTimeout);
        try
        {
            await ws.ConnectAsync(uri, invoker, timeout.Token);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            invoker.Dispose();
            throw Fail(ex, declared, seenPin, timeout.IsCancellationRequested, ct);
        }
        Record(declared, seenPin);
        var channel = new Channel(ws, invoker, _o, declared, this);
        lock (_lock)
        {
            if (_disposed)
            {
                channel.Abort();
                throw new TvException(TvError.Unreachable, "closed");
            }
            _channels.Add(channel);
        }
        return channel;
    }

    public async Task<TvHttpResponse> SendAsync(TvHttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var declared = Admit(request.Port, request.Path, webSocket: false);
        var method = request.Method switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            _ => throw Refuse(TvError.Blocked, "method")
        };
        if (!ValidHeader(request.ContentType, 128) || !ValidHeader(request.SoapAction, 256)) throw Refuse(TvError.Blocked, "header");
        if (request.Body is { } b && (method != HttpMethod.Post || Encoding.UTF8.GetByteCount(b) > _o.MaxBodyBytes)) throw Refuse(TvError.Blocked, "body");
        if (!_pairing) CheckNoKey(declared, request.Path, request.Body, request.SoapAction, request.ContentType);
        var uri = UriFor(declared, request.Path, webSocket: false);
        string? seenPin = null;
        using var invoker = Invoker(declared, p => seenPin = p);
        using var message = new HttpRequestMessage(method, uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        if (request.SoapAction is not null) message.Headers.TryAddWithoutValidation("SOAPACTION", request.SoapAction);
        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.Body));
            if (request.ContentType is not null) message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_o.HttpTimeout);
        HttpResponseMessage response;
        try
        {
            response = await invoker.SendAsync(message, timeout.Token);
        }
        catch (Exception ex)
        {
            throw Fail(ex, declared, seenPin, timeout.IsCancellationRequested, ct);
        }
        using (response)
        {
            Record(declared, seenPin);
            try
            {
                if (response.Content.Headers.ContentLength is > 0 and var length && length > _o.MaxHttpBytes) throw new TvException(TvError.BadAnswer, "too large");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[_o.MaxHttpBytes + 1];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
                    if (read == 0) break;
                    total += read;
                }
                if (total > _o.MaxHttpBytes) throw new TvException(TvError.BadAnswer, "too large");
                return new TvHttpResponse((int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, total));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TvException(TvError.Timeout);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
            {
                throw new TvException(TvError.Unreachable, "closed");
            }
        }
    }

    private void Remove(Channel channel)
    {
        lock (_lock) _channels.Remove(channel);
    }

    /// <summary>Closes every web socket of the link; afterwards nothing can be opened through it any more.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Channel> open;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            open = [.. _channels];
            _channels.Clear();
        }
        foreach (var c in open) await c.DisposeAsync();
    }

    internal static T? Find<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is T t) return t;
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    if (Find<T>(inner) is { } found) return found;
                }
            }
        }
        return null;
    }

    /// <summary>One web socket: text only, sizes bounded, one send and one receive at a time.</summary>
    private sealed class Channel(ClientWebSocket ws, HttpMessageInvoker invoker, TvLinkOptions o, TvPort port, TvLink owner) : ITvChannel
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly SemaphoreSlim _receive = new(1, 1);
        private int _disposed;

        public bool Open => Volatile.Read(ref _disposed) == 0 && ws.State == WebSocketState.Open;

        public async Task SendAsync(string text, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(text);
            var data = Encoding.UTF8.GetBytes(text);
            if (data.Length > o.MaxSendBytes) throw new TvException(TvError.Blocked, "message too large");
            if (!Open) throw new TvException(TvError.Unreachable, "closed");
            try
            {
                await _send.WaitAsync(ct);
                try
                {
                    await ws.SendAsync(data, WebSocketMessageType.Text, true, ct);
                }
                finally
                {
                    _send.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                throw new TvException(TvError.Unreachable, "closed");
            }
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1) return null;
            var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                await _receive.WaitAsync(ct);
                try
                {
                    using var buffer = new MemoryStream();
                    while (true)
                    {
                        var r = await ws.ReceiveAsync(chunk.AsMemory(), ct);
                        if (r.MessageType == WebSocketMessageType.Close) return null;
                        if (r.MessageType != WebSocketMessageType.Text) throw new TvException(TvError.BadAnswer, "binary");
                        if (buffer.Length + r.Count > o.MaxMessageBytes) throw new TvException(TvError.BadAnswer, "message too large");
                        buffer.Write(chunk, 0, r.Count);
                        if (r.EndOfMessage) break;
                    }
                    try
                    {
                        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    }
                    catch (ArgumentException)
                    {
                        throw new TvException(TvError.BadAnswer, "not UTF-8");
                    }
                }
                finally
                {
                    _receive.Release();
                }
            }
            catch (TvException)
            {
                // Too large, binary or not UTF-8: the rest of the stream cannot be trusted, so the channel is closed.
                Abort();
                throw;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                if (Volatile.Read(ref _disposed) == 1) return null;
                throw new TvException(TvError.Unreachable, "closed");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { ws.Abort(); }
            catch (ObjectDisposedException) { }
            ws.Dispose();
            invoker.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closed anyway.
            }
            Abort();
            owner.Remove(this);
        }

        public override string ToString() => $"tv channel :{port.Port}";
    }
}
