namespace CouchHud.Sdk;

// Sensor board (additive, no SDK version change): one plugin that reads the PC's sensors (Mega Hub's own CaseHUD plugin, which gets
// them from CaseHUD and its LibreHardwareMonitor service) publishes fan speeds and temperatures, and other plugins read them (the
// FanControl plugin shows them on its page). The plugins never talk to each other: the host keeps the last values, checks and bounds
// them, names their source itself and drops them when they get old. Reading grants nothing but the numbers: no access to the
// publishing plugin, to CaseHUD or to the hardware.

/// <summary>What a <see cref="PluginSensorReading"/> measures, and so its unit.</summary>
public enum PluginSensorKind
{
    /// <summary>A fan or pump speed in revolutions per minute.</summary>
    Fan,

    /// <summary>A temperature in degrees Celsius.</summary>
    Temperature
}

/// <summary>One sensor value on the board.</summary>
/// <param name="Kind">Fan (RPM) or temperature (°C).</param>
/// <param name="Name">Plain text, e.g. "CPU Package" or "Fan #2"; the host strips decoration and caps it at <see cref="MaxName"/> characters.</param>
/// <param name="Value">RPM from 0 to <see cref="MaxRpm"/>, or °C from <see cref="MinCelsius"/> to <see cref="MaxCelsius"/>; anything else (NaN too) is dropped.</param>
public sealed record PluginSensorReading(PluginSensorKind Kind, string Name, double Value)
{
    public const int MaxName = 40;
    public const double MaxRpm = 20000;
    public const double MinCelsius = -40;
    public const double MaxCelsius = 150;
}

/// <summary>The values a reader gets (<see cref="IPluginContext.ReadSensors"/>).</summary>
/// <param name="Source">The display name of the plugin that published them, set by the host (never by the publisher).</param>
/// <param name="Taken">When the host received them.</param>
/// <param name="Readings">At most <see cref="MaxReadings"/>, fans first, each kind in the publisher's order.</param>
public sealed record PluginSensorSnapshot(string Source, DateTimeOffset Taken, IReadOnlyList<PluginSensorReading> Readings)
{
    public const int MaxReadings = 32;

    /// <summary>Values older than this are not handed out any more (the publisher stopped, was paused or switched off).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
}
