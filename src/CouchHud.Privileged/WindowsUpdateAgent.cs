using System.Runtime.InteropServices;
using CouchHud.Core.Localization;

namespace CouchHud.Privileged;

/// <summary>What the installation of the pending Windows updates came to.</summary>
public sealed record WindowsUpdateResult(int Installed, bool RebootRequired, string? Error);

/// <summary>Windows Update Agent (COM, late bound): the installation of everything pending, which needs administrator rights.</summary>
public sealed class WindowsUpdateAgent : IWindowsUpdate
{
    public const string SearchCriteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

    public static dynamic CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException($"{progId} not registered"))
        ?? throw new InvalidOperationException($"{progId} not created");

    /// <summary>Searches, downloads and installs everything pending (no reboot is started).</summary>
    public WindowsUpdateResult InstallAll()
    {
        try
        {
            dynamic session = CreateCom("Microsoft.Update.Session");
            session.ClientApplicationID = "CouchHud";
            dynamic found = session.CreateUpdateSearcher().Search(SearchCriteria).Updates;
            dynamic collection = CreateCom("Microsoft.Update.UpdateColl");
            int count = found.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic u = found.Item(i);
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                collection.Add(u);
            }
            if (count == 0)
            {
                return new WindowsUpdateResult(0, false, null);
            }
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = collection;
            downloader.Download();
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = collection;
            dynamic outcome = installer.Install();
            int code = outcome.ResultCode; // 2 succeeded, 3 succeeded with errors
            var installed = 0;
            for (var i = 0; i < count; i++)
            {
                if ((int)outcome.GetUpdateResult(i).ResultCode is 2 or 3) installed++;
            }
            return new WindowsUpdateResult(installed, (bool)outcome.RebootRequired, code is 2 or 3 ? null : Strings.Get("update.installFailed", code));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return new WindowsUpdateResult(0, false, ex.Message);
        }
    }
}
