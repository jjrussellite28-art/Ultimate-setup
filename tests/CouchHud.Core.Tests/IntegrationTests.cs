using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Integrations;

namespace CouchHud.Core.Tests;

public class IntegrationTests
{
    [Fact]
    public void Weather_places_and_report_are_parsed()
    {
        using var places = JsonDocument.Parse("""
            { "results": [
              { "name": "Berlin", "latitude": 52.52, "longitude": 13.41, "admin1": "Berlin", "country": "Deutschland" },
              { "name": "Nowhere" }
            ] }
            """);
        var place = Assert.Single(WeatherService.ParsePlaces(places.RootElement));
        Assert.Equal("Berlin, Deutschland", place.Region);

        using var report = JsonDocument.Parse("""
            {
              "current": { "temperature_2m": 12.4, "weather_code": 61, "is_day": 0 },
              "daily": {
                "time": ["2026-10-18", "2026-10-19"],
                "weather_code": [61, 0],
                "temperature_2m_max": [14.1, 16.0],
                "temperature_2m_min": [6.0, 7.5]
              }
            }
            """);
        var weather = WeatherService.ParseReport(report.RootElement, "Berlin");
        Assert.Equal(12.4, weather.Temperature);
        Assert.False(weather.IsDay);
        Assert.Equal(2, weather.Days.Count);
        Assert.Equal(16.0, weather.Days[1].Max);
    }

    [Fact]
    public void Home_assistant_states_are_filtered_and_sorted()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "entity_id": "sensor.temp", "state": "21.4", "attributes": { "friendly_name": "Temperatur", "unit_of_measurement": "°C", "device_class": "temperature" } },
              { "entity_id": "light.decke", "state": "on", "attributes": { "friendly_name": "Decke" } },
              { "entity_id": "scene.kino", "state": "2026-10-17T20:00:00", "attributes": { "friendly_name": "Kino" } },
              { "entity_id": "update.core", "state": "off", "attributes": {} },
              { "entity_id": "switch.gone", "state": "unavailable", "attributes": {} }
            ]
            """);

        var entities = HomeAssistantClient.ParseStates(doc.RootElement);

        Assert.Equal(new[] { "scene.kino", "light.decke", "sensor.temp" }, entities.Select(e => e.Id));
        Assert.True(entities[1].Controllable);
        Assert.False(entities[2].Controllable);
        Assert.Equal("°C", entities[2].Unit);
    }

    [Fact]
    public async Task Home_assistant_calls_the_right_services_with_the_token()
    {
        var requests = new List<(string Url, string Body, string? Auth)>();
        var handler = new Recorder(requests);
        var client = new HomeAssistantClient(new HttpClient(handler)) { BaseUrl = "http://ha.local:8123/", Token = "secret" };

        await client.ActivateAsync("light.decke");
        await client.ActivateAsync("scene.kino");
        await client.ActivateAsync("cover.rollo");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ActivateAsync("sensor.temp"));

        Assert.Equal("http://ha.local:8123/api/services/homeassistant/toggle", requests[0].Url);
        Assert.Contains("\"entity_id\":\"light.decke\"", requests[0].Body);
        Assert.Equal("Bearer secret", requests[0].Auth);
        Assert.Equal("http://ha.local:8123/api/services/scene/turn_on", requests[1].Url);
        Assert.Equal("http://ha.local:8123/api/services/cover/toggle", requests[2].Url);
    }

    [Fact]
    public async Task Home_assistant_explains_a_bad_token()
    {
        var client = new HomeAssistantClient(new HttpClient(new Status(HttpStatusCode.Unauthorized))) { BaseUrl = "http://ha", Token = "x" };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetEntitiesAsync());
        Assert.Contains("Token", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HomeAssistantClient(new HttpClient()).GetEntitiesAsync());
    }

    private sealed class Recorder(List<(string, string, string?)> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            requests.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Status(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code));
    }
}
