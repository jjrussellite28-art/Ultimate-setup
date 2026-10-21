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
public interface IRemoteTarget
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
    public static readonly string[] Buttons = ["up", "down", "left", "right", "a", "b", "x", "y", "lb", "rb", "start", "guide"];

    private WebApplication? _app;

    public bool Running => _app is not null;

    public static string PagePath => Path.Combine(AppContext.BaseDirectory, "ui", "remote", "index.html");

    public async Task StartAsync(int port, Pairing pairing, IRemoteTarget target)
    {
        await StopAsync();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, NoLifetime>();
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        var app = builder.Build();
        // The port is open to the LAN, not to the internet: whatever arrives from a public address is turned away.
        app.Use(async (ctx, next) =>
        {
            if (!CouchHud.Core.Net.LanOnly.IsAllowed(ctx.Connection.RemoteIpAddress))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });

        app.MapGet("/", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await ctx.Response.SendFileAsync(PagePath, ctx.RequestAborted);
        });

        app.MapPost("/api/pair", async (HttpContext ctx) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var body = await ReadAsync(ctx);
            var token = pairing.TryPair(Str(body, "code"), Str(body, "name"), DateTimeOffset.UtcNow, ctx.Connection.RemoteIpAddress?.ToString());
            return token is null ? Results.Json(new { error = Strings.Get("remote.codeInvalid") }, statusCode: 400) : Results.Json(new { token });
        });

        app.MapGet("/api/state", (HttpContext ctx) =>
            !Authorized(ctx, pairing) ? Results.StatusCode(StatusCodes.Status401Unauthorized) : Results.Json(new { keyboard = Camel(target.Keyboard) }));

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

        await app.StartAsync();
        _app = app;
        Console.Error.WriteLine($"[remote] listening on port {port}");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
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

    private static object Camel(KeyboardState k) => new { open = k.Open, title = k.Title, secret = k.Secret, text = k.Secret ? "" : k.Text };

    private static bool Authorized(HttpContext ctx, Pairing pairing) => CouchHud.Core.Net.LanOnly.IsAllowed(ctx.Connection.RemoteIpAddress) && pairing.IsValid(ctx.Request.Headers["X-CouchHud-Token"].ToString());

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
