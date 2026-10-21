namespace CouchHud.Core.Platform;

/// <summary>
/// A warm screen in the evening, like the night mode on handhelds: a gamma ramp that
/// keeps red, lowers green a bit and blue more. Works in games too.
/// </summary>
public static class NightLight
{
    public static readonly TimeOnly EveningStart = new(19, 0);
    public static readonly TimeOnly EveningEnd = new(7, 0);

    public static bool IsActive(string mode, TimeOnly now) => mode switch
    {
        "on" => true,
        "evening" => now >= EveningStart || now < EveningEnd,
        _ => false
    };

    /// <summary>Multipliers for red, green and blue at a strength of 0-100.</summary>
    public static (double Red, double Green, double Blue) Tint(int strength)
    {
        var s = Math.Clamp(strength, 0, 100) / 100.0;
        return (1.0, 1.0 - 0.28 * s, 1.0 - 0.55 * s);
    }

    /// <summary>The 3 × 256 ramp SetDeviceGammaRamp expects (red, green, blue).</summary>
    public static ushort[] Ramp(int strength)
    {
        var (r, g, b) = Tint(strength);
        var ramp = new ushort[3 * 256];
        for (var i = 0; i < 256; i++)
        {
            var value = i * 257;
            ramp[i] = (ushort)Math.Round(value * r);
            ramp[256 + i] = (ushort)Math.Round(value * g);
            ramp[512 + i] = (ushort)Math.Round(value * b);
        }
        return ramp;
    }
}
