using CouchHud.Core.Saves;

namespace CouchHud.Core.Tests;

public sealed class RealManifestSmokeTests
{
    /// <summary>Opt-in: set COUCHHUD_REAL_MANIFEST to a downloaded manifest.yaml.</summary>
    [Fact]
    public void RealManifestParses()
    {
        var path = Environment.GetEnvironmentVariable("COUCHHUD_REAL_MANIFEST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var reader = new StreamReader(path);
        var index = ManifestIndex.Load(reader);
        var elden = index.Find(1245620, "Elden Ring");
        Assert.True(index.Count > 10000 && elden is not null && elden.Files.Any(f => f.Path.Contains("EldenRing")),
            $"games={index.Count} ms={watch.ElapsedMilliseconds} elden={elden?.Name} files={elden?.Files.Count}");
        Assert.True(watch.ElapsedMilliseconds < 20000, $"slow: {watch.ElapsedMilliseconds}");
        Console.WriteLine($"REAL games={index.Count} ms={watch.ElapsedMilliseconds}");
    }
}
