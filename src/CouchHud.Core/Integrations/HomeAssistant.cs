using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations;

public sealed record HaEntity(
    string Id,
    string Domain,
    string Name,
    string State,
    string? Unit,
    string? DeviceClass,
    bool Controllable);

/// <summary>
/// Home Assistant through its REST API with a long-lived access token
/// (Profile → Security in Home Assistant). Read states, toggle switches and
/// lights, run scenes and scripts.
/// </summary>
public sealed class HomeAssistantClient(HttpClient http)
{
    /// <summary>Domains we can switch with one button press.</summary>
    private static readonly HashSet<string> Toggleable = ["light", "switch", "fan", "input_boolean", "cover", "media_player", "automation"];

    /// <summary>Domains that run something once.</summary>
    private static readonly HashSet<string> Runnable = ["scene", "script", "button", "input_button"];

    /// <summary>Domains worth showing on a TV at all.</summary>
    private static readonly HashSet<string> Shown = ["light", "switch", "fan", "input_boolean", "cover", "media_player", "scene", "script", "sensor", "binary_sensor", "climate", "button", "input_button", "automation", "weather", "lock"];

    public string? BaseUrl { get; set; }

    public string? Token { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

    public async Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, "/api/states");
        using var response = await http.SendAsync(request, ct);
        EnsureOk(response);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseStates(doc.RootElement);
    }

    /// <summary>Toggles or runs an entity, depending on its domain.</summary>
    public async Task ActivateAsync(string entityId, CancellationToken ct = default)
    {
        if (!HomeAssistantPolicy.IsValidEntityId(entityId)) throw new InvalidOperationException(Strings.Get("ha.invalidEntity"));
        var domain = entityId.Split('.')[0];
        var (serviceDomain, service) = domain switch
        {
            _ when Toggleable.Contains(domain) && domain is "cover" => ("cover", "toggle"),
            _ when Toggleable.Contains(domain) => ("homeassistant", "toggle"),
            "scene" or "script" => (domain, "turn_on"),
            "button" or "input_button" => (domain, "press"),
            _ => throw new InvalidOperationException(Strings.Get("ha.cannotToggle", entityId))
        };
        using var request = Request(HttpMethod.Post, $"/api/services/{serviceDomain}/{service}");
        request.Content = JsonContent.Create(new { entity_id = entityId });
        using var response = await http.SendAsync(request, ct);
        EnsureOk(response);
    }

    internal static IReadOnlyList<HaEntity> ParseStates(JsonElement root)
    {
        var result = new List<HaEntity>();
        foreach (var e in root.EnumerateArray())
        {
            var id = e.GetProperty("entity_id").GetString() ?? "";
            var domain = id.Split('.')[0];
            if (!Shown.Contains(domain)) continue;
            var state = e.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            if (state is "unavailable") continue;
            var attributes = e.TryGetProperty("attributes", out var a) ? a : default;
            string? Attr(string name) =>
                attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            result.Add(new HaEntity(
                id,
                domain,
                Attr("friendly_name") ?? id,
                state,
                Attr("unit_of_measurement"),
                Attr("device_class"),
                Toggleable.Contains(domain) || Runnable.Contains(domain)));
        }
        return result.OrderBy(e => DomainOrder(e.Domain)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static int DomainOrder(string domain) => domain switch
    {
        "scene" => 0,
        "light" => 1,
        "switch" or "input_boolean" or "fan" => 2,
        "media_player" or "cover" or "lock" => 3,
        "climate" => 4,
        "sensor" or "binary_sensor" or "weather" => 5,
        _ => 6
    };

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!Configured) throw new InvalidOperationException(Strings.Get("ha.notConfigured"));
        var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl!.TrimEnd('/') + "/"), path.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static void EnsureOk(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var reason = (int)response.StatusCode switch
        {
            401 => Strings.Get("ha.tokenInvalid"),
            404 => Strings.Get("ha.badAddress"),
            _ => Strings.Get("ha.status", (int)response.StatusCode)
        };
        throw new HttpRequestException(reason, null, response.StatusCode);
    }
}
