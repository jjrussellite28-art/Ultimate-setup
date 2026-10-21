namespace CouchHud.Core.Security;

/// <summary>
/// An elevated CouchHud.exe is a .NET program: before any of its code runs, the runtime reads variables from the environment that
/// decide which code it loads (a profiler DLL, another runtime, startup hooks). The elevated process gets the environment of the
/// user, including the user's own HKCU\Environment, which any program running as the user can write. So CouchHud refuses to start
/// an elevated copy of itself while such a variable exists. This is a check on the starting side, not a complete fix (a variable
/// set between the check and the start slips through); the complete fix is not to elevate a .NET program at all.
/// </summary>
public static class ElevationEnvironment
{
    private static readonly string[] Prefixes = ["DOTNET_", "COMPLUS_", "CORECLR_", "COR_"];

    // Settings of the .NET tools that change nothing about what code is loaded.
    private static readonly HashSet<string> Harmless = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_CLI_UI_LANGUAGE",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE", "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT", "DOTNET_CLI_HOME", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH",
        "DOTNET_MULTILEVEL_LOOKUP"
    };

    /// <summary>The first variable name that the runtime would act on, or null.</summary>
    public static string? Offending(IEnumerable<string> variableNames) =>
        variableNames.FirstOrDefault(n => Prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !Harmless.Contains(n));
}
