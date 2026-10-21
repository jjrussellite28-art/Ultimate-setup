using System.Diagnostics;
using CouchHud.Privileged;

namespace CouchHud.Service.Logging;

/// <summary>
/// The Windows event log (Application, source <see cref="Source"/>). The setup registers the source (P8); the service never
/// registers one itself (that writes under HKLM\SYSTEM\…\EventLog, the installer's job). If the source is missing or cannot be
/// checked, the lines are dropped: a log that cannot be written must not take the service down. Always wrap it in a
/// <see cref="RedactingEventSink"/>.
/// </summary>
public sealed class EventLogSink : IEventSink
{
    public const string Source = "CouchHud.Service";
    public const string LogName = "Application";

    private readonly Lazy<bool> _available = new(SourceRegistered, LazyThreadSafetyMode.ExecutionAndPublication);

    public void Info(string message) => Write(message, EventLogEntryType.Information);

    public void Error(string message) => Write(message, EventLogEntryType.Warning);

    private static bool SourceRegistered()
    {
        try
        {
            return EventLog.SourceExists(Source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void Write(string message, EventLogEntryType type)
    {
        if (!_available.Value) return;
        try
        {
            EventLog.WriteEntry(Source, message, type);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nowhere left to report to.
        }
    }
}
