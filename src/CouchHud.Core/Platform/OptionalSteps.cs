namespace CouchHud.Core.Platform;

/// <summary>
/// Start-up steps that are nice to have but never worth a black screen: as the shell there is no explorer, so
/// whatever talks to the taskbar, the notification area or other explorer-hosted COM objects may fail with
/// E_NOTIMPL, "class not registered" or the like. Each step runs on its own; a failure is logged and only that
/// feature is missing, the rest of the start goes on.
/// </summary>
public sealed class OptionalSteps(Action<string> log)
{
    private readonly List<string> _failed = [];

    /// <summary>Names of the steps that threw, in order.</summary>
    public IReadOnlyList<string> Failed => _failed;

    /// <returns>True when the step ran through.</returns>
    public bool Run(string name, Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failed.Add(name);
            log($"[startup] {name} not available, going on without it: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The result of a step, or the fallback when it threw.</summary>
    public T Run<T>(string name, Func<T> step, T fallback)
    {
        var result = fallback;
        Run(name, () => result = step());
        return result;
    }
}
