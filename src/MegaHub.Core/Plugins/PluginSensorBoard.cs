using CouchHud.Sdk;

namespace MegaHub.Core.Plugins;

/// <summary>
/// The sensor board: the last fan speeds and temperatures a plugin with "sensors.publish" (first-party only) put there, for plugins with
/// "sensors.read". Plugins never reach each other through it: a reader gets copies of checked numbers, the source name comes from the
/// host, and values older than <see cref="PluginSensorSnapshot.MaxAge"/> are not handed out. Pure and thread-safe; the clock is injectable.
/// </summary>
public sealed class PluginSensorBoard(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, PluginSensorSnapshot> _byPlugin = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Replaces the values of <paramref name="pluginId"/>; null or nothing valid takes them off. Returns how many were kept.</summary>
    public int Publish(string pluginId, string pluginName, IReadOnlyList<PluginSensorReading>? readings)
    {
        var clean = Clean(readings);
        lock (_gate)
        {
            if (clean.Count == 0) _byPlugin.Remove(pluginId);
            else _byPlugin[pluginId] = new PluginSensorSnapshot(PluginText.Safe(pluginName, pluginId, firstParty: false), _now(), clean);
        }
        return clean.Count;
    }

    /// <summary>Takes a plugin's values off (it was switched off, removed or paused).</summary>
    public void Forget(string pluginId)
    {
        lock (_gate) _byPlugin.Remove(pluginId);
    }

    /// <summary>Keeps only the values of publishers <paramref name="isActive"/> accepts (the others were switched off, uninstalled, unloaded or paused).</summary>
    public void Retain(Func<string, bool> isActive)
    {
        lock (_gate)
        {
            foreach (var id in _byPlugin.Keys.Where(id => !isActive(id)).ToList()) _byPlugin.Remove(id);
        }
    }

    /// <summary>
    /// The newest values that are not too old, or null; with <paramref name="isActive"/> only those of publishers it accepts. Exactly one
    /// first-party publisher is assumed (CaseHUD today): with several, the newest snapshot wins as a whole, it is not merged.
    /// </summary>
    public PluginSensorSnapshot? Read(Func<string, bool>? isActive = null)
    {
        var now = _now();
        lock (_gate)
        {
            PluginSensorSnapshot? best = null;
            foreach (var (id, snap) in _byPlugin)
            {
                if (isActive is not null && !isActive(id)) continue;
                var age = now - snap.Taken;
                // A clock that jumped back makes "future" values: they are as untrustworthy as old ones.
                if (age > PluginSensorSnapshot.MaxAge || age < -TimeSpan.FromSeconds(5)) continue;
                if (best is null || snap.Taken > best.Taken) best = snap;
            }
            return best;
        }
    }

    /// <summary>The valid readings: known kind, a name left after stripping, a finite value in range; fans first, at most the cap.</summary>
    public static IReadOnlyList<PluginSensorReading> Clean(IReadOnlyList<PluginSensorReading>? readings)
    {
        if (readings is null || readings.Count == 0) return [];
        // Fans and temperatures have their own budgets, so junk fans cannot push the temperatures out (and the other way round).
        var fans = new List<PluginSensorReading>();
        var temps = new List<PluginSensorReading>();
        var half = PluginSensorSnapshot.MaxReadings / 2;
        // Looking further than a few times the cap is pointless; a plugin that sends thousands gets the first ones looked at.
        foreach (var r in readings.Take(PluginSensorSnapshot.MaxReadings * 4))
        {
            if (r is null || !InRange(r.Kind, r.Value)) continue;
            var list = r.Kind == PluginSensorKind.Fan ? fans : temps;
            if (list.Count >= half) continue;
            var name = PluginText.Strip(r.Name, PluginSensorReading.MaxName);
            if (name.Length == 0) continue;
            list.Add(new PluginSensorReading(r.Kind, name, Math.Round(r.Value, 1)));
        }
        return [.. fans, .. temps];
    }

    private static bool InRange(PluginSensorKind kind, double value) => double.IsFinite(value) && kind switch
    {
        PluginSensorKind.Fan => value is >= 0 and <= PluginSensorReading.MaxRpm,
        PluginSensorKind.Temperature => value is >= PluginSensorReading.MinCelsius and <= PluginSensorReading.MaxCelsius,
        _ => false
    };
}
