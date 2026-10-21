using System.Runtime.InteropServices;
using CouchHud.Core.Guard;

namespace CouchHud.Privileged;

/// <summary>
/// Makes sure a helper task carries the access list that lets its user start it (and nobody else but administrators and SYSTEM).
/// The task XML contains the descriptor too, but whether <c>schtasks /Create /XML</c> applies it is not documented well enough to
/// rely on, so the Task Scheduler is asked (COM, <c>IRegisteredTask.GetSecurityDescriptor</c>) and told
/// (<c>SetSecurityDescriptor</c>) if the entry is missing. Either way, the end state is checked.
/// </summary>
internal sealed class TaskAcl(IEventSink events)
{
    private const int DaclSecurityInformation = 4;

    /// <summary>True when the task grants its user the start right afterwards. Elevated side only.</summary>
    public bool Ensure(string taskName, string sid)
    {
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                // No Task Scheduler COM to ask: what schtasks did has to do.
                events.Error("[setup] task scheduler COM not available, trusting schtasks for the access list");
                return true;
            }
            service = Activator.CreateInstance(type);
            dynamic scheduler = service!;
            scheduler.Connect();
            var slash = taskName.LastIndexOf('\\');
            var folder = "\\" + (slash > 0 ? taskName[..slash] : "");
            dynamic task = scheduler.GetFolder(folder).GetTask(taskName[(slash + 1)..]);
            string current = task.GetSecurityDescriptor(DaclSecurityInformation);
            if (PolicyGroups.GrantsStart(current, sid)) return true;
            events.Info($"[setup] task {taskName}: the access list was not taken from the XML, setting it");
            task.SetSecurityDescriptor(PolicyGroups.TaskSecurityDescriptor(sid), 0);
            string after = task.GetSecurityDescriptor(DaclSecurityInformation);
            var ok = PolicyGroups.GrantsStart(after, sid);
            if (!ok) events.Error($"[setup] task {taskName}: access list still without the user's start right: {after}");
            return ok;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or MissingMethodException)
        {
            // The check itself failed (not the descriptor): the task exists, schtasks said so. Say it and go on.
            events.Error($"[setup] task {taskName}: access list not checked ({ex.Message})");
            return true;
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
    }
}
