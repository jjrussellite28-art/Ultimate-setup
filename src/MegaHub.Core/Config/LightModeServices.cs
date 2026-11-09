namespace MegaHub.Core.Config;

/// <summary>
/// The services the change to light mode resumes (Hud.SetIntegrationMode): the applied list (<see cref="AppConfig.CouchServicesInstalled"/>),
/// never the selection (<see cref="AppConfig.CouchServices"/>). A selection emptied but not applied yet (or emptied by an undo before this
/// step) still leaves the applied services paused, and light mode never resumes them otherwise; a selection not applied yet paused nothing.
/// </summary>
public static class LightModeServices
{
    /// <summary>The applied services, a copy (empty: nothing to resume).</summary>
    public static IReadOnlyList<string> ToResume(AppConfig config) => [.. config.CouchServicesInstalled];
}
