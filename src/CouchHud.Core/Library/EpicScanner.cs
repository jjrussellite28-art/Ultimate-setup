using System.Text.Json;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

public sealed class EpicScanner(string? manifestDir = null, string? catalogFile = null) : ILibraryProvider
{
    public static string DefaultManifestDir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public string Name => "Epic Games";

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
        Task.Run(Scan, cancellationToken);

    private IReadOnlyList<LibraryItem> Scan()
    {
        var dir = manifestDir ?? DefaultManifestDir;
        if (!Directory.Exists(dir)) return [];

        var items = new List<LibraryItem>();
        // The launcher's own catalog knows the box art of everything installed: no lookup needed.
        var catalog = LauncherArtwork.ReadEpicCatalog(catalogFile);
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (ParseItem(doc.RootElement) is { } item)
                {
                    var catalogId = doc.RootElement.TryGetProperty("CatalogItemId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    items.Add(catalogId is not null && catalog.TryGetValue(catalogId, out var art) ? item with { Art = art } : item);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[epic] {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return items;
    }

    internal static LibraryItem? ParseItem(JsonElement json)
    {
        string? Str(string name) =>
            json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var displayName = Str("DisplayName");
        var appName = Str("AppName");
        if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(appName)) return null;
        if (json.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return null;

        // DLCs carry their parent's AppName in MainGameAppName.
        var main = Str("MainGameAppName");
        if (!string.IsNullOrEmpty(main) && main != appName) return null;

        if (json.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
            && !cats.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "games"))
        {
            return null;
        }

        var launchId = string.Join("%3A", new[] { Str("CatalogNamespace"), Str("CatalogItemId"), appName }
            .Select(s => Uri.EscapeDataString(s ?? "")));

        var installSize = json.TryGetProperty("InstallSize", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;

        return new LibraryItem
        {
            Id = $"epic:{appName}",
            Source = "epic",
            Name = displayName,
            InstallDir = Str("InstallLocation"),
            SizeOnDisk = installSize,
            Launch = new LaunchTarget(LaunchKind.Url, $"com.epicgames.launcher://apps/{launchId}?action=launch&silent=true")
        };
    }
}
