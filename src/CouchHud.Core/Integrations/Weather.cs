using System.Globalization;
using System.Text.Json;

namespace CouchHud.Core.Integrations;

public sealed record Place(string Name, string Region, double Latitude, double Longitude);

public sealed record DayForecast(string Date, double Min, double Max, int Code);

public sealed record WeatherReport(string Place, double Temperature, int Code, bool IsDay, IReadOnlyList<DayForecast> Days);

/// <summary>
/// Weather from Open-Meteo: free, no API key, no account. Reports are cached
/// for a quarter of an hour, which is more than fresh enough for a TV corner.
/// </summary>
public sealed class WeatherService(HttpClient http)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    private (Place Place, DateTimeOffset At, WeatherReport Report)? _cached;

    public async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=8&language={Localization.Strings.Language}&format=json";
        using var doc = await GetJson(url, ct);
        return ParsePlaces(doc.RootElement);
    }

    public async Task<WeatherReport> GetAsync(Place place, CancellationToken ct = default)
    {
        if (_cached is { } c && c.Place == place && DateTimeOffset.UtcNow - c.At < CacheFor) return c.Report;
        var lat = place.Latitude.ToString(CultureInfo.InvariantCulture);
        var lon = place.Longitude.ToString(CultureInfo.InvariantCulture);
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                  "&current=temperature_2m,weather_code,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                  "&timezone=auto&forecast_days=4";
        using var doc = await GetJson(url, ct);
        var report = ParseReport(doc.RootElement, place.Name);
        _cached = (place, DateTimeOffset.UtcNow, report);
        return report;
    }

    internal static IReadOnlyList<Place> ParsePlaces(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        return results.EnumerateArray()
            .Where(r => r.TryGetProperty("latitude", out _) && r.TryGetProperty("longitude", out _))
            .Select(r => new Place(
                Str(r, "name") ?? "?",
                string.Join(", ", new[] { Str(r, "admin1"), Str(r, "country") }.Where(s => !string.IsNullOrEmpty(s))),
                r.GetProperty("latitude").GetDouble(),
                r.GetProperty("longitude").GetDouble()))
            .ToList();
    }

    internal static WeatherReport ParseReport(JsonElement root, string placeName)
    {
        var current = root.GetProperty("current");
        var days = new List<DayForecast>();
        if (root.TryGetProperty("daily", out var daily))
        {
            var dates = daily.GetProperty("time").EnumerateArray().Select(d => d.GetString() ?? "").ToList();
            var codes = daily.GetProperty("weather_code").EnumerateArray().Select(d => d.GetInt32()).ToList();
            var max = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(d => d.GetDouble()).ToList();
            var min = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(d => d.GetDouble()).ToList();
            for (var i = 0; i < dates.Count && i < codes.Count && i < max.Count && i < min.Count; i++)
            {
                days.Add(new DayForecast(dates[i], min[i], max[i], codes[i]));
            }
        }
        return new WeatherReport(
            placeName,
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.TryGetProperty("is_day", out var isDay) && isDay.GetInt32() == 1,
            days);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<JsonDocument> GetJson(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
