using System.ServiceProcess;
using CouchHud.Core.Startup;

namespace CouchHud.Privileged;

/// <summary>The service control manager through <see cref="ServiceController"/>.</summary>
public sealed class WindowsScm : IScm
{
    public ServiceStartMode GetStartType(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.StartType;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ServiceStartMode.Manual;
        }
    }

    public bool? IsRunning(string name)
    {
        if (!GamingServices.IsAllowed(name)) return null;
        try
        {
            using var service = new ServiceController(name);
            return service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending
                or ServiceControllerStatus.Paused or ServiceControllerStatus.PausePending;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public bool Stop(string name, TimeSpan timeout) => Change(name, ServiceControllerStatus.Stopped, timeout);

    public bool Start(string name, TimeSpan timeout) => Change(name, ServiceControllerStatus.Running, timeout);

    private static bool Change(string name, ServiceControllerStatus wanted, TimeSpan timeout)
    {
        // Only the services CouchHUD may pause (the same fixed list the service validates against): never "any service by name".
        if (!GamingServices.IsAllowed(name)) return false;
        try
        {
            using var service = new ServiceController(name);
            if (service.Status == wanted) return true;
            if (wanted == ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            }
            else if (service.Status != ServiceControllerStatus.StartPending)
            {
                service.Start();
            }
            service.WaitForStatus(wanted, timeout);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }
}
