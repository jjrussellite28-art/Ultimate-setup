using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Shell.Services;

/// <summary>
/// Stands in for the library providers of plugins while those are still loading in the background:
/// empty until then (the app rescans once they are ready), afterwards all of them in parallel.
/// </summary>
public sealed class PluginLibraryProvider(PluginManager plugins) : ILibraryProvider
{
    public string Name => "Plugins";

    public async Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken)
    {
        if (!plugins.Loaded.IsCompleted) return [];
        var results = await Task.WhenAll(plugins.LibraryProviders.Select(async p =>
        {
            try
            {
                return await p.GetItemsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[plugins] {p.Name}: {ex.Message}");
                return [];
            }
        }));
        return [.. results.SelectMany(r => r)];
    }
}
