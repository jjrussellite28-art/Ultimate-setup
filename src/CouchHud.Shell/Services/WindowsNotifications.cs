using CouchHud.Core.Notifications;
using HudNotification = CouchHud.Core.Notifications.Notification;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace CouchHud.Shell.Services;

/// <summary>
/// Mirrors Windows toasts (WhatsApp, Discord, Teams …) into CouchHud's notification
/// center. Windows only grants this with the user's consent, and for apps without
/// package identity it may refuse altogether; then the status says so.
/// </summary>
public sealed class WindowsNotifications(NotificationCenter center)
{
    private readonly HashSet<uint> _seen = [];
    private CancellationTokenSource? _cts;

    /// <summary>"allowed", "denied", "unspecified" or "unavailable".</summary>
    public string Status { get; private set; } = "off";

    public async Task<string> StartAsync()
    {
        Stop();
        try
        {
            var listener = UserNotificationListener.Current;
            var access = await listener.RequestAccessAsync();
            Status = access switch
            {
                UserNotificationListenerAccessStatus.Allowed => "allowed",
                UserNotificationListenerAccessStatus.Denied => "denied",
                _ => "unspecified"
            };
            if (access != UserNotificationListenerAccessStatus.Allowed) return Status;

            // What is already in the action center counts as seen; only new toasts show up.
            foreach (var n in await listener.GetNotificationsAsync(NotificationKinds.Toast)) _seen.Add(n.Id);
            var cts = _cts = new CancellationTokenSource();
            _ = Task.Run(() => PollAsync(listener, cts.Token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Everything counts: without explorer the notification platform answers with all kinds of errors
            // (E_NOTIMPL, class not registered, a missing WinRT projection …).
            Console.Error.WriteLine($"[notifications] not available: {ex.Message}");
            Status = "unavailable";
        }
        return Status;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        if (Status == "allowed") Status = "off";
    }

    private async Task PollAsync(UserNotificationListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var n in await listener.GetNotificationsAsync(NotificationKinds.Toast))
                {
                    if (!_seen.Add(n.Id)) continue;
                    if (ToEntry(n) is { } entry) center.Add(entry);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[notifications] {ex.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private static HudNotification? ToEntry(UserNotification n)
    {
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
        if (texts.Count == 0) return null;
        string app;
        try
        {
            app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Windows";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            app = "Windows";
        }
        return new HudNotification(
            $"win:{n.Id}",
            "windows",
            app,
            texts[0],
            string.Join(" · ", texts.Skip(1)),
            n.CreationTime.ToUnixTimeMilliseconds());
    }
}
