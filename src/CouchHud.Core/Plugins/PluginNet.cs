using System.Net;
using CouchHud.Core.Net;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>A network service as plugin.json declares it (SDK 5, see <see cref="PluginNetServices"/>).</summary>
public sealed record PluginServiceManifest(string? Id, LocalizedText? Name, IReadOnlyList<string>? Hosts);

/// <summary>
/// A declared service as the signed package statement and the trust list record it (id and hosts only): what the user approved. An update
/// that goes beyond it (a new service, a new host, "*") asks again like a new capability does.
/// </summary>
public sealed record PackageService(string Id, IReadOnlyList<string> Hosts);

/// <summary>A declared service that passed the checks: lower-case host names (or "*" for any public host), a name in both languages.</summary>
public sealed record PluginService(string Id, string NameDe, string NameEn, IReadOnlyList<string> Hosts)
{
    public bool AnyHost => Hosts.Contains(PluginNetServices.AnyHost);

    /// <summary>The plugin may talk to a device in the home network that the user typed in (its own sockets; <see cref="IPluginContext.Http"/> never goes there).</summary>
    public bool Lan => Hosts.Contains(PluginNetServices.LanHost);

    /// <summary>The key of the service's privacy switch: "plugin/service".</summary>
    public static string Key(string pluginId, string serviceId) => $"{pluginId.ToLowerInvariant()}/{serviceId}";

    /// <summary>The part the statement signs and the trust list records.</summary>
    public PackageService ToPackage() => new(Id, Hosts);
}

/// <summary>The checks of declared services and of every request a plugin sends through its host-made client. Pure, so tests can call them.</summary>
public static class PluginNetRules
{
    /// <summary>
    /// The services of a manifest that pass: at most four, valid unique ids, at most 16 hosts each that are DNS names (no address literal, no
    /// port, no wildcard part) or exactly "*". Anything else is dropped with the reason in the log. Below SDK 5 nothing is declared.
    /// </summary>
    public static IReadOnlyList<PluginService> Parse(IReadOnlyList<PluginServiceManifest>? declared, int sdk, Action<string>? log = null)
    {
        var list = new List<PluginService>();
        if (declared is null || sdk < PluginNetServices.MinSdk) return list;
        foreach (var s in declared)
        {
            if (list.Count >= PluginNetServices.MaxPerPlugin) { log?.Invoke("more than four services, the rest is ignored"); break; }
            if (s is null || !PluginFunctionRules.ValidId(s.Id) || list.Any(x => x.Id == s.Id)) { log?.Invoke($"service {s?.Id}: invalid or repeated id"); continue; }
            var hosts = new List<string>();
            foreach (var h in s.Hosts ?? [])
            {
                if (hosts.Count >= PluginNetServices.MaxHosts) break;
                var host = h?.Trim().ToLowerInvariant();
                if (host == PluginNetServices.AnyHost || host == PluginNetServices.LanHost || (host is not null && ValidHostName(host))) { if (!hosts.Contains(host!)) hosts.Add(host!); }
                else log?.Invoke($"service {s.Id}: host '{h}' refused");
            }
            if (hosts.Count == 0) { log?.Invoke($"service {s.Id}: no usable host"); continue; }
            var de = PluginFunctionRules.CleanLabel(s.Name?.For("de"));
            var en = PluginFunctionRules.CleanLabel(s.Name?.For("en"));
            list.Add(new PluginService(s.Id!, de ?? en ?? s.Id!, en ?? de ?? s.Id!, hosts));
        }
        return list;
    }

    /// <summary>
    /// What an offered list of services asks for beyond what was approved, as readable lines ("weather: api.example.com"); empty when it stays
    /// within it. <paramref name="installed"/> null means none were recorded. A service id that is new counts, a host that is new counts, and
    /// "*" counts unless the installed service already had "*". Pure.
    /// </summary>
    public static IReadOnlyList<string> Widened(IEnumerable<PackageService>? installed, IEnumerable<PackageService>? offered)
    {
        var have = (installed ?? []).Where(s => s is not null).GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(s => s.Hosts ?? []).Select(h => h.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var s in offered ?? [])
        {
            if (s is null) continue;
            have.TryGetValue(s.Id, out var known);
            foreach (var h in (s.Hosts ?? []).Select(h => h.ToLowerInvariant()).Distinct())
            {
                if (known is not null && (known.Contains(h) || (h != PluginNetServices.LanHost && known.Contains(AnyHostName)))) continue;
                added.Add($"{s.Id}: {h}");
            }
            if (known is null && (s.Hosts ?? []).Count == 0) added.Add(s.Id + ":");
        }
        return added;
    }

    /// <summary>
    /// The declared services cut down to what was approved (the trust list's record from the signed statement): a service or host beyond it is
    /// dropped, so a plugin.json that changed after the approval gets no more reach. Pure.
    /// </summary>
    public static IReadOnlyList<PluginService> Restrict(IReadOnlyList<PluginService> declared, IEnumerable<PackageService>? approved, Action<string>? log = null)
    {
        var list = new List<PluginService>();
        var ok = (approved ?? []).Where(a => a is not null).ToList();
        foreach (var s in declared)
        {
            var allowed = ok.Where(a => a.Id == s.Id).SelectMany(a => a.Hosts ?? []).Select(h => h.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var hosts = s.Hosts.Where(h => allowed.Contains(h) || (h != PluginNetServices.LanHost && allowed.Contains(AnyHostName))).ToList();
            if (hosts.Count < s.Hosts.Count) log?.Invoke($"service {s.Id}: hosts beyond the approved ones are dropped");
            if (hosts.Count > 0) list.Add(s with { Hosts = hosts });
        }
        return list;
    }

    private const string AnyHostName = PluginNetServices.AnyHost;

    /// <summary>A DNS name with at least one dot: letters, digits, "-" and dots, no address literal, at most 253 characters.</summary>
    internal static bool ValidHostName(string host)
    {
        if (host.Length is < 3 or > 253 || !host.Contains('.') || host.StartsWith('.') || host.EndsWith('.') || host.Contains("..")) return false;
        if (IPAddress.TryParse(host, out _)) return false;
        if (!host.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')) return false;
        // The last label is a top-level domain: never only digits (which would be an address in disguise).
        return !host[(host.LastIndexOf('.') + 1)..].All(char.IsAsciiDigit);
    }

    /// <summary>May a request of this service go to this address? http(s) only, a declared host (or any for "*"), never userinfo in the address.</summary>
    public static bool AllowsUri(PluginService service, Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0) return false;
        // "lan" is a marker, not a name: a request to a host called "lan" never counts as a declared host.
        if (uri.IdnHost.Equals(PluginNetServices.LanHost, StringComparison.OrdinalIgnoreCase)) return false;
        // "*" (news feeds of any site) takes http and https, but only on the ordinary ports 80 and 443.
        if (service.AnyHost) return uri.Port is 80 or 443;
        // A named host is reached over https only, on the default port.
        var host = uri.IdnHost.ToLowerInvariant();
        return uri.Scheme == "https" && service.Hosts.Contains(host) && uri.IsDefaultPort;
    }
}

/// <summary>
/// The handler below a plugin's client: before each request and each redirect hop it asks whether the service may go out now (Offline, the
/// service's switch, the plugin not paused) and whether the address is a declared host; it follows redirects of GET and HEAD itself (never
/// from https to http, at most five) and sends headers that identify or authorize the caller only to the host they were meant for. Below it
/// connects only to public addresses (<see cref="SafeNet.CreateHandler"/>), so a declared name that points into the home network gets nowhere.
/// </summary>
internal sealed class PluginHttpHandler(string pluginId, PluginService service, Func<bool> allowed, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        for (var hop = 0; ; hop++)
        {
            Check(current.RequestUri);
            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location || (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) || hop >= MaxRedirects)
            {
                return response;
            }
            var target = current.RequestUri is { IsAbsoluteUri: true } from ? new Uri(from, location) : location;
            if (target.Scheme is not ("http" or "https") || (current.RequestUri?.Scheme == "https" && target.Scheme == "http")) return response;
            response.Dispose();
            var next = new HttpRequestMessage(request.Method, target) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            var sameHost = request.RequestUri is { } first && string.Equals(first.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == target.Port;
            foreach (var header in request.Headers)
            {
                if (!sameHost && !SafeOnRedirect.Contains(header.Key)) continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            current = next;
        }
    }

    private void Check(Uri? uri)
    {
        var host = uri is { IsAbsoluteUri: true } ? uri.IdnHost : "?";
        if (!allowed()) throw new NetBlockedException(NetService.Plugin, host, offline: false);
        if (!PluginNetRules.AllowsUri(service, uri))
        {
            Console.Error.WriteLine($"[plugins] {pluginId}/{service.Id}: {host} is not a declared host, refused");
            throw new NetBlockedException(NetService.Plugin, host, offline: false);
        }
    }

    private static readonly HashSet<string> SafeOnRedirect = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Accept-Charset", "User-Agent", "If-None-Match", "If-Modified-Since", "If-Range", "Range"
    };

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

/// <summary>
/// Caps what a plugin can pull in through its client: an answer that announces more than the limit is refused at once, one that turns out
/// larger (chunked, no length) fails while it is read, whether the plugin buffers it or reads the stream. Keeps a hostile or broken server
/// from filling the memory of the shell.
/// </summary>
internal sealed class PluginResponseLimitHandler(long maxBytes, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content is null) return response;
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            response.Dispose();
            throw new HttpRequestException($"the answer is larger than {maxBytes} bytes");
        }
        response.Content = new LimitedContent(response.Content, maxBytes);
        return response;
    }

    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly long _max;

        public LimitedContent(HttpContent inner, long max)
        {
            _inner = inner;
            _max = max;
            foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await CopyAsync(stream, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => await CopyAsync(stream, cancellationToken).ConfigureAwait(false);

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var limited = new LimitedStream(_inner.ReadAsStream(cancellationToken), _max);
            limited.CopyTo(stream);
        }

        private async Task CopyAsync(Stream target, CancellationToken ct)
        {
            await using var limited = new LimitedStream(await _inner.ReadAsStreamAsync(ct).ConfigureAwait(false), _max);
            await limited.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new LimitedStream(_inner.ReadAsStream(cancellationToken), _max);

        protected override async Task<Stream> CreateContentReadStreamAsync() => new LimitedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _max);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;

        private int Count(int n)
        {
            _read += n;
            if (_read > max) throw new HttpRequestException($"the answer is larger than {max} bytes");
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
