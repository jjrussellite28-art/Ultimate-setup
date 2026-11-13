using System.Text.RegularExpressions;
using MegaHub.Core.Remote;

namespace MegaHub.Core.Tests;

/// <summary>
/// Power from the phone, pinned from the sources: the endpoints check origin and pairing first and spend the ticket before anything runs,
/// the host checks the couch's gates again and ends in the same call as the couch's power entries (no own operation, no service), and
/// the list of entries that ask for the couch lock matches ui/core/power.js.
/// </summary>
public sealed class RemotePowerContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MegaHub.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path])).Replace("\r\n", "\n");

    /// <summary>From a line that starts with <paramref name="start"/> to the first line that is exactly <paramref name="end"/>.</summary>
    private static string Block(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"not found: {start}");
        var to = text.IndexOf("\n" + end + "\n", from, StringComparison.Ordinal);
        Assert.True(to > from, $"no end for: {start}");
        return text[from..to];
    }

    private static int At(string text, string what)
    {
        var at = text.IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, $"missing: {what}");
        return at;
    }

    [Fact]
    public void The_entries_that_ask_for_the_couch_lock_match_power_js()
    {
        var js = Read("ui", "core", "power.js");
        var list = Block(js, "export const POWER_ACTIONS = [", "];");
        var withUnlock = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Regex.Split(list, @"\n  \{\n").Skip(1))
        {
            var id = Regex.Match(entry, @"id: '([a-z\-]+)'").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(id));
            if (entry.Contains("unlock()", StringComparison.Ordinal)) withUnlock.Add(id);
        }
        Assert.True(withUnlock.SetEquals(RemotePower.EntriesWithUnlock), $"power.js: {string.Join(", ", withUnlock.Order())}");
        // The phone's actions are entries of the couch's menu, each with the same host call.
        foreach (var action in RemotePower.Actions)
        {
            Assert.Contains($"id: '{RemotePower.EntryId(action)}'", list);
            Assert.Contains($"api.power('{action}')", list);
        }
    }

    [Fact]
    public void The_endpoints_check_origin_and_pairing_first_and_spend_the_ticket_before_anything_runs()
    {
        // RemoteHost only maps: origin and address (Trusted), the token, the body as a callback RemotePowerApi calls after the pairing check.
        var host = Read("src", "MegaHub.Shell", "Services", "RemoteHost.cs");
        Assert.Contains("app.MapPost(\"/api/power/prepare\", async (HttpContext ctx) =>", host);
        Assert.Contains("app.MapPost(\"/api/power\", async (HttpContext ctx) =>", host);
        // The body is a function the API calls only after its own checks (a local Func: the route handler itself returns the reply).
        Assert.Contains("Func<Task<JsonNode?>> body = () => ReadAsync(ctx);\n            return Reply(await power.PrepareAsync(Trusted(ctx), Token(ctx), body));", host);
        Assert.Contains("Func<Task<JsonNode?>> body = () => ReadAsync(ctx);\n            return Reply(await power.RunAsync(Trusted(ctx), Token(ctx), body));", host);
        Assert.Contains("private static bool Trusted(HttpContext ctx) => SameOrigin(ctx) && MegaHub.Core.Net.LanOnly.IsAllowed(ctx.Connection.RemoteIpAddress);", host);
        Assert.Contains("power = target.PowerEnabled", host);
        // Request bodies are capped for every endpoint, also the unauthenticated /api/pair.
        Assert.Contains("o.Limits.MaxRequestBodySize = MaxBodyBytes;", host);
        Assert.Contains("public const long MaxBodyBytes = 32 * 1024;", host);

        var api = Read("src", "MegaHub.Core", "Remote", "RemotePower.cs");
        // The token endpoints check origin and pairing, then read the body and hand over to the steps the encrypted channel uses too
        // (its phone is known from the sealed session).
        var prepareGate = Block(api, "    public async Task<RemotePowerReply> PrepareAsync(", "    }");
        var runGate = Block(api, "    public async Task<RemotePowerReply> RunAsync(", "    }");
        foreach (var (endpoint, next) in new[] { (prepareGate, "return await PrepareForAsync(device, await readBody());"), (runGate, "return await RunForAsync(device, await readBody());") })
        {
            var gate = At(endpoint, "if (!trusted || pairing.Find(token, _now()) is not { } device) return RemotePowerReply.Unauthorized;");
            Assert.True(At(endpoint, next) > gate, "nothing is read before origin and pairing are checked");
        }
        var prepare = Block(api, "    public async Task<RemotePowerReply> PrepareForAsync(", "    }");
        var run = Block(api, "    public async Task<RemotePowerReply> RunForAsync(", "    }");
        foreach (var endpoint in new[] { prepare, run })
        {
            At(endpoint, "RemotePower.IsAction(action)");
            Assert.DoesNotContain("AdminService", endpoint);
            Assert.DoesNotContain("ShellManager", endpoint);
        }
        // Every request counts, refused and unknown ones too, before the HUD is asked.
        Assert.True(At(prepare, "RemotePower.IsAction(action)") > At(prepare, "Tickets.Count(device.Id, _now())"));
        Assert.True(At(prepare, "target.PowerCheckAsync(action, device)") > At(prepare, "RemotePower.IsAction(action)"));
        Assert.True(At(prepare, "Tickets.Issue(") > At(prepare, "target.PowerCheckAsync(action, device)"));
        Assert.True(At(run, "target.PowerAsync(action, device)") > At(run, "Tickets.Redeem(device.Id, action,"));
        Assert.DoesNotContain("PowerCheckAsync", run);
    }

    [Fact]
    public void The_host_checks_the_gates_again_and_runs_the_couchs_own_power_call()
    {
        var hud = Read("src", "MegaHub.Shell", "Hud.cs");
        var power = Block(hud, "    async Task<RemotePowerResult> IRemotePowerTarget.PowerAsync(string action, RemoteDevice device)", "    }");
        var decide = At(power, "RemotePower.Decide(action, RemotePowerNow())");
        var call = At(power, "await PowerAsync(action);");
        Assert.True(call > decide);
        // The TV names the phone only once the call went through; the phone never sees the exception.
        Assert.True(At(power, "Emit(\"toast\", Strings.Get($\"remote.power.{action}\", RemotePower.CleanName(device.Name)));") > call);
        Assert.DoesNotContain("ex.Message;", power);
        Assert.DoesNotContain("ShellManager", power);
        Assert.DoesNotContain("AdminService", power);
        Assert.DoesNotContain("restart-pc", power);
        // The same three facts the bridge looks at for the couch's power call, plus PC mode and the owner's setting.
        Assert.Contains("new(Locked, ProfileHeld, Areas.AllowsMethod(RemotePower.BridgeMethod), _pcMode, config.Current.RemotePower);", hud);
        Assert.Contains("bool IRemotePowerTarget.PowerEnabled => config.Current.RemotePower;", hud);
        var bridge = Read("src", "MegaHub.Shell", "UiHost", "UiBridge.cs");
        Assert.Contains("case \"power\":\n                await hud.PowerAsync(StringArg(0));", bridge);
        // The setting is a normal one: behind the couch lock like every setting that is not on the harmless list.
        Assert.DoesNotContain("remotePower", MegaHub.Core.Guard.LockPolicy.HarmlessSettings);
    }

    [Fact]
    public void The_phone_page_asks_before_it_switches_anything_off()
    {
        var page = Read("ui", "remote", "index.html");
        // Buttons only ask; only the question's confirm button sends the ticket back.
        Assert.Contains("btn.addEventListener('click', () => askPower(btn.dataset.p))", page);
        Assert.Contains("$('sheetConfirm').onclick = runPower;", page);
        Assert.Single(Regex.Matches(page, Regex.Escape("api('/api/power',")));
        Assert.Contains("api('/api/power/prepare', { action })", page);
        // The section shows only when the PC allows it; the question stays until the confirmed request is answered.
        Assert.Contains("$('powerCard').hidden = s.power !== true;", page);
        Assert.Contains("id=\"powerCard\" aria-labelledby=\"powerTitle\" hidden>", page);
        Assert.Contains("if (pending?.running) return;", page);
        // A connection that died with the PC does not block the page for good.
        Assert.Contains("await api('/api/power', { action, ticket }, abort.signal);", page);
        Assert.Contains("const POWER_TIMEOUT_MS = 90000;", page);
    }
}
