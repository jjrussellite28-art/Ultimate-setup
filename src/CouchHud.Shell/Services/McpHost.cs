using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Assistant;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CouchHud.Shell.Services;

/// <summary>
/// Streamable HTTP transport for the MCP server: POST /mcp with JSON-RPC,
/// answered as plain JSON. Every request needs the bearer token from the settings.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    private WebApplication? _app;

    public bool Running => _app is not null;

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Addresses a client can use, e.g. http://192.168.1.20:8765/mcp.</summary>
    public static IReadOnlyList<string> Urls(int port, bool lan)
    {
        var urls = new List<string> { $"http://localhost:{port}/mcp" };
        if (!lan) return urls;
        urls.AddRange(Network.LanAddresses().Select(a => $"http://{a}:{port}/mcp"));
        return urls;
    }

    public async Task StartAsync(McpServer server, int port, bool lan, string token)
    {
        await StopAsync();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        // The default console lifetime hooks ProcessExit and would hold up quitting the shell.
        builder.Services.AddSingleton<IHostLifetime, NoLifetime>();
        builder.WebHost.ConfigureKestrel(o =>
        {
            if (lan) o.ListenAnyIP(port);
            else o.ListenLocalhost(port);
        });
        var app = builder.Build();
        // Without the LAN option only this machine may ask; with it, only the local network (never a public address).
        app.Use(async (ctx, next) =>
        {
            var peer = ctx.Connection.RemoteIpAddress;
            if (lan ? !CouchHud.Core.Net.LanOnly.IsAllowed(peer) : !CouchHud.Core.Net.LanOnly.IsLoopback(peer))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
        var expected = Encoding.UTF8.GetBytes($"Bearer {token}");

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            // Browsers send an Origin; MCP clients don't. Blocks DNS rebinding from web pages.
            if (ctx.Request.Headers.Origin.Count > 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var auth = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(auth, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(ctx, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" }
                });
                return;
            }

            JsonNode? response;
            if (body is JsonArray batch)
            {
                var answers = new JsonArray();
                foreach (var message in batch)
                {
                    if (await server.HandleAsync(message, ctx.RequestAborted) is { } answer) answers.Add(answer);
                }
                response = answers.Count > 0 ? answers : null;
            }
            else
            {
                response = await server.HandleAsync(body, ctx.RequestAborted);
            }

            if (response is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }
            await WriteAsync(ctx, response);
        });
        // No server-initiated stream and no sessions to end.
        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapDelete("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

        await app.StartAsync();
        _app = app;
        Console.Error.WriteLine($"[mcp] listening on port {port}{(lan ? " (LAN)" : "")}");
    }

    private static async Task WriteAsync(HttpContext ctx, JsonNode response)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(response.ToJsonString(), ctx.RequestAborted);
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

    private sealed class NoLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
