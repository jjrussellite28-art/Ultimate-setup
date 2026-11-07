using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>What the phone page needs to know about the TV right now.</summary>
public sealed record KeyboardState(bool Open, string Title, bool Secret, string Text);

/// <summary>The HUD side of the phone remote; calls arrive on Kestrel threads.</summary>
public interface IRemoteTarget : IRemoteChannelTarget
{
    KeyboardState Keyboard { get; }

    /// <summary>Types the text; the answer is null, or why it was refused (a password field has the focus).</summary>
    Task<string?> TextAsync(string text, bool submit);
    void Button(string button);
}

/// <summary>
/// Small LAN web server for the phone: a page to type into the TV's keyboard (or paste
/// an API key) and a d-pad. Pairing happens once by scanning the QR code on the TV.
/// </summary>
public sealed class RemoteHost : IAsyncDisposable
{
    /// <summary>Largest request body the phone server reads.</summary>
    public const long MaxBodyBytes = 32 * 1024;

    public static readonly string[] Buttons = ["up", "down", "left", "right", "a", "b", "x", "y", "lb", "rb", "start", "guide"];

    private WebApplication? _app;

    public bool Running => _app is not null;

    /// <summary>The sealed channel of the running server (sessions, streams, events); null while the server is off.</summary>
    public RemoteChannel? Channel { get; private set; }

    public static string PagePath => Path.Combine(AppContext.BaseDirectory, "ui", "remote", "index.html");

    public static string SealPath => Path.Combine(AppContext.BaseDirectory, "ui", "remote", "seal.js");

    /// <summary>How often an open event stream sends a comment line, so phones and routers keep it open and a dead one is noticed.</summary>
    public static readonly TimeSpan StreamPing = TimeSpan.FromSeconds(20);

    public async Task StartAsync(int port, Pairing pairing, IRemoteTarget target)
    {
        await StopAsync();
        // Fresh with every start: tickets and counters never outlive the server.
        var power = new RemotePowerApi(pairing, target, line => Console.Error.WriteLine(line));
        var channel = new RemoteChannel(pairing, target, power, line => Console.Error.WriteLine(line));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, NoLifetime>();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenAnyIP(port);
            // Small bodies only, also for /api/pair, which anyone on the LAN may call: the largest legitimate one is /api/text (4000
            // characters, as JSON at most 6 bytes each with escapes); anything bigger is cut off by Kestrel before it is parsed.
            o.Limits.MaxRequestBodySize = MaxBodyBytes;
        });
        var app = builder.Build();
        // The port is open to the LAN, not to the internet: whatever arrives from a public address is turned away.
        app.Use(async (ctx, next) =>
        {
            if (!CouchHud.Core.Net.LanOnly.IsAllowed(ctx.Connection.RemoteIpAddress))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            // DNS rebinding: a page of another name that resolves to this PC is not the phone page. The QR link uses the address, so
            // only an IP literal as Host is accepted (with or without the port).
            if (!RemoteChannel.HostIsAddress(ctx.Request.Host.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers.CacheControl = "no-store";
            await next();
        });

        app.MapGet("/", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Content-Security-Policy"] = RemoteChannel.PageCsp;
            await ctx.Response.SendFileAsync(PagePath, ctx.RequestAborted);
        });

        // The phone's half of the seal (no WebCrypto on a plain-HTTP page), a module from the same origin.
        app.MapGet("/seal.js", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/javascript; charset=utf-8";
            await ctx.Response.SendFileAsync(SealPath, ctx.RequestAborted);
        });

        // These three lambdas name their return type on purpose: without it C# reads an expression-bodied async lambda as a plain RequestDelegate,
        // throws the IResult away and every answer goes out as an empty 200 (the phone then cannot open the pairing answer).
        // Pairing: only sealed with the key from the QR code's secret (RemoteChannel/Pairing.TryPairSealed); there is no plain pairing any more.
        app.MapPost("/api/pair", async Task<IResult> (HttpContext ctx) =>
            Reply(channel.Pair(Trusted(ctx), ctx.Connection.RemoteIpAddress?.ToString(), await ReadTextAsync(ctx))));

        // A session per page visit: the device id in the header, the body sealed with the phone's channel key.
        app.MapPost("/api/session", async Task<IResult> (HttpContext ctx) =>
            Reply(channel.OpenSession(Trusted(ctx), ctx.Request.Headers["X-CouchHud-Device"].ToString(), await ReadTextAsync(ctx), ctx.Connection.RemoteIpAddress?.ToString())));

        // Every call of a paired phone of the channel: sealed, numbered once, and checked against the phone's level and the couch's gates.
        app.MapPost("/api/rpc", async Task<IResult> (HttpContext ctx) =>
            Reply(await channel.CallAsync(Trusted(ctx), ctx.Request.Headers["X-CouchHud-Session"].ToString(), await ReadTextAsync(ctx), ctx.Connection.RemoteIpAddress?.ToString())));

        // Server-sent events: signals only ("library changed"), sealed; the phone fetches the data itself over /api/rpc.
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            if (channel.OpenStream(Trusted(ctx), q["sid"].ToString(), q["seq"].ToString(), q["p"].ToString()) is not { } stream)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            try
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                await ctx.Response.WriteAsync(": ok\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                while (!ctx.RequestAborted.IsCancellationRequested && channel.Sessions.Alive(stream.Session, DateTimeOffset.UtcNow))
                {
                    var next = await stream.Queue.TakeAsync(StreamPing, ctx.RequestAborted);
                    if (stream.Queue.Closed) break;
                    var line = next is { } e ? "data: " + RemoteChannel.SealEvent(stream.Session, e.Type, e.Json) + "\n\n" : ": ping\n\n";
                    await ctx.Response.WriteAsync(line, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                channel.Events.Unsubscribe(stream.Queue);
            }
        });

        // Covers for the library list: only from the artwork cache, only for a session of a phone with the library level and a proof
        // for exactly this image (an <img> cannot send headers).
        app.MapGet("/art/{hash}", (HttpContext ctx, string hash) =>
        {
            if (!channel.MayLoadArt(Trusted(ctx), ctx.Request.Query["sid"].ToString(), hash, ctx.Request.Query["p"].ToString()))
                return Results.StatusCode(StatusCodes.Status404NotFound);
            if (CouchHud.Core.Library.ArtworkCache.Shared?.Read("/" + hash) is not { } art) return Results.StatusCode(StatusCodes.Status404NotFound);
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.Bytes(art.Data, art.ContentType);
        });

        app.MapGet("/api/state", (HttpContext ctx) =>
            !Authorized(ctx, pairing) ? Results.StatusCode(StatusCodes.Status401Unauthorized) : Results.Json(new { keyboard = Camel(target.Keyboard), power = target.PowerEnabled }));

        app.MapPost("/api/text", async (HttpContext ctx) =>
        {
            if (!SameOrigin(ctx) || !Authorized(ctx, pairing)) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            // The phone link is plain HTTP in the LAN: no passwords through it.
            if (target.Keyboard.Secret) return Results.Json(new { error = Strings.Get("remote.passwordOnTv") }, statusCode: 403);
            var body = await ReadAsync(ctx);
            var text = Str(body, "text");
            if (text.Length > 4000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var refused = await target.TextAsync(text, body?["submit"]?.GetValueKind() == JsonValueKind.True);
            return refused is null ? Results.Json(new { ok = true }) : Results.Json(new { error = refused }, statusCode: 403);
        });

        app.MapPost("/api/button", async (HttpContext ctx) =>
        {
            if (!SameOrigin(ctx) || !Authorized(ctx, pairing)) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            var button = Str(await ReadAsync(ctx), "button");
            if (!Buttons.Contains(button)) return Results.StatusCode(StatusCodes.Status400BadRequest);
            target.Button(button);
            return Results.Json(new { ok = true });
        });

        // Power (shut down, restart, standby) in two steps: the check gives a one-time ticket, the confirmation on the phone brings it back
        // (RemotePowerApi/RemotePowerTickets: same phone, same action, 0.8 to 30 seconds, once; rate limit). Both steps check the couch's gates.
        // The body is read by a function the power API calls only after its own checks (a plain Func, not a route handler: ASP0016 mistook it for one).
        app.MapPost("/api/power/prepare", async (HttpContext ctx) =>
        {
            Func<Task<JsonNode?>> body = () => ReadAsync(ctx);
            return Reply(await power.PrepareAsync(Trusted(ctx), Token(ctx), body));
        });

        app.MapPost("/api/power", async (HttpContext ctx) =>
        {
            Func<Task<JsonNode?>> body = () => ReadAsync(ctx);
            return Reply(await power.RunAsync(Trusted(ctx), Token(ctx), body));
        });

        await app.StartAsync();
        _app = app;
        Channel = channel;
        Console.Error.WriteLine($"[remote] listening on port {port}");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        Channel = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(timeout.Token);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private static IResult Reply(RemoteChannelReply reply) => new ChannelResult(reply);

    /// <summary>The sealed answer written by hand (status, length, text), with a header that says what the channel meant to send (for the phone page's error text).</summary>
    private sealed class ChannelResult(RemoteChannelReply reply) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = reply.Status;
            ctx.Response.Headers["X-MegaHub-Reply"] = $"{reply.Status}/{reply.Body?.Length ?? -1}";
            if (reply.Body is null) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(reply.Body);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        }
    }

    /// <summary>A sealed body as text, at most <see cref="RemoteChannel.MaxRequestLength"/> characters (Kestrel cuts at <see cref="MaxBodyBytes"/>).</summary>
    private static async Task<string?> ReadTextAsync(HttpContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8);
            var buffer = new char[RemoteChannel.MaxRequestLength + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await reader.ReadAsync(buffer.AsMemory(read), ctx.RequestAborted);
                if (n == 0) break;
                read += n;
            }
            return read > RemoteChannel.MaxRequestLength ? null : new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private static object Camel(KeyboardState k) => new { open = k.Open, title = k.Title, secret = k.Secret, text = k.Secret ? "" : k.Text };

    private static bool Authorized(HttpContext ctx, Pairing pairing) => CouchHud.Core.Net.LanOnly.IsAllowed(ctx.Connection.RemoteIpAddress) && pairing.IsValid(ctx.Request.Headers["X-CouchHud-Token"].ToString());

    /// <summary>Same origin and an address from the home network (the pairing is checked by <see cref="RemotePowerApi"/>, before the body is read).</summary>
    private static bool Trusted(HttpContext ctx) => SameOrigin(ctx) && CouchHud.Core.Net.LanOnly.IsAllowed(ctx.Connection.RemoteIpAddress);

    private static string Token(HttpContext ctx) => ctx.Request.Headers["X-CouchHud-Token"].ToString();

    private static IResult Reply(RemotePowerReply reply) =>
        reply.Body is null ? Results.StatusCode(reply.Status) : Results.Json(reply.Body, statusCode: reply.Status);

    /// <summary>Only the page served from here may post; other web pages in the phone's browser may not.</summary>
    private static bool SameOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        return origin.Length == 0 || origin.Equals($"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonNode?> ReadAsync(HttpContext ctx)
    {
        try
        {
            return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonNode? body, string name) =>
        body?[name]?.GetValueKind() == JsonValueKind.String ? body[name]!.GetValue<string>() : "";

    private sealed class NoLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>QR codes for the TV (pairing link), as inline SVG.</summary>
public static class Qr
{
    public static string Svg(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        // The module matrix already includes the quiet zone; one path keeps the SVG small.
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var path = new System.Text.StringBuilder();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (matrix[y][x]) path.Append('M').Append(x).Append(',').Append(y).Append("h1v1h-1z");
            }
        }
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\"><rect width=\"{size}\" height=\"{size}\" fill=\"#fff\"/><path d=\"{path}\" fill=\"#000\"/></svg>";
    }
}

/// <summary>
/// Opens the firewall for the phone remote and the MCP server on private networks, for their ports only and only for the local
/// subnet, through the CouchHUD service: the rule's name and program (this CouchHud.exe) are fixed in the service, only the ports
/// come from here (checked here and again by the service). The rule goes again when the last of the two is switched off.
/// Opening needs the parents' code or an administrator (the service's rule); removing is allowed to everyone.
/// </summary>
public static class Firewall
{
    /// <summary>The rules of the Windows firewall (see <see cref="IFirewall.Rules"/>).</summary>
    public static IReadOnlyList<CouchHud.Core.Net.FirewallRuleInfo>? Rules() => PrivilegedServices.Firewall.Rules();

    /// <summary>Uninstall (elevated): our rule goes with the program, and the rules of the earlier name that are verifiably ours.</summary>
    public static void Remove() => FirewallOps.Remove(PrivilegedServices.Firewall, Environment.ProcessPath);

    /// <summary>Is there a rule of ours (readable without administrator rights)? Also one of the earlier name.</summary>
    public static bool Exists() => FirewallOps.Exists(PrivilegedServices.Firewall, Environment.ProcessPath);

    /// <summary>Windows' own block rules for this program (they beat our allow rule): their names, empty when there are none.</summary>
    public static IReadOnlyList<string> BlockingRuleNames() => FirewallOps.BlockingRuleNames(PrivilegedServices.Firewall, Environment.ProcessPath);

    /// <summary>Opens the ports (and lets the service remove the verifiably ours rules of the earlier name).</summary>
    public static async Task AllowAsync(IEnumerable<int> ports)
    {
        var checkedPorts = CouchHud.Core.Net.FirewallRule.ParsePorts(string.Join(',', ports)) ?? throw new InvalidOperationException(Strings.Get("remote.shareNothing"));
        // Opening needs a grant: the unlock's (code entered here) or, without one, a parent's or an administrator's (Windows' prompt).
        var result = await AdminService.WithGrantAsync(CouchHud.Service.Contracts.Operation.SetRemoteFirewall, () => AdminService.Ops.SetRemoteFirewallAsync(checkedPorts));
        if (result.Failure == CouchHud.ServiceClient.FailureKind.NeedGrant) throw new InvalidOperationException(Strings.Get("remote.shareNeedsParent"));
        AdminService.ThrowIfFailed(result);
        var legacy = await AdminService.Ops.RemoveLegacyFirewallRulesAsync();
        if (!legacy.Ok) Console.Error.WriteLine($"[remote] old firewall rules stay: {legacy}");
    }

    /// <summary>Our rule goes (and the verifiably ours of the earlier name).</summary>
    public static async Task RemoveAsync()
    {
        AdminService.ThrowIfFailed(await AdminService.Ops.RemoveRemoteFirewallAsync());
        var legacy = await AdminService.Ops.RemoveLegacyFirewallRulesAsync();
        if (!legacy.Ok) Console.Error.WriteLine($"[remote] old firewall rules stay: {legacy}");
    }
}
