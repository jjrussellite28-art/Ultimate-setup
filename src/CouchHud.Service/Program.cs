using System.ServiceProcess;
using CouchHud.Service.Logging;

namespace CouchHud.Service;

internal static class Program
{
    private static int Main()
    {
        var log = new RedactingEventSink(new EventLogSink());

        // An exception nobody caught: log its type and let the process end; the SCM failure actions restart the service.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error($"[crash] unhandled {(e.ExceptionObject as Exception)?.GetType().Name ?? "exception"}; the service ends and the SCM restarts it");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error($"[error] unobserved task exception {e.Exception.InnerException?.GetType().Name ?? e.Exception.GetType().Name}");
            e.SetObserved();
        };

        // Only under the SCM: started any other way, ServiceBase.Run fails and the process ends (no console, no window).
        ServiceBase.Run(new CouchHudWindowsService(log));
        return 0;
    }
}
