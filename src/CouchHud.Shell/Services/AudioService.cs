using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CouchHud.Core.Platform;
using CouchHud.Shell.Native;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

public sealed record AudioDevice(string Id, string Name, string Kind, bool IsDefault);

public sealed record AudioState(int Volume, bool Muted, IReadOnlyList<AudioDevice> Devices, bool CanSwitch);

/// <summary>
/// Volume, mute and output device through Windows Core Audio. All COM calls
/// happen on the UI thread; device notifications only raise events, which the
/// caller marshals back before touching anything.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _client;
    private IPolicyConfig? _policy;

    public AudioService()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _client = new NotificationClient(this);
        Check(_enumerator.RegisterEndpointNotificationCallback(_client));
        try
        {
            _policy = (IPolicyConfig)new PolicyConfigComObject();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Console.Error.WriteLine($"[audio] switching outputs not available: {ex.Message}");
        }
    }

    /// <summary>A device became active (plugged in or enabled). Raised on a COM thread.</summary>
    public event Action<string>? DeviceArrived;

    /// <summary>Devices or the default changed. Raised on a COM thread.</summary>
    public event Action? DevicesChanged;

    public AudioState GetState()
    {
        var devices = Devices();
        using var volume = DefaultVolume();
        var level = 0f;
        var muted = false;
        if (volume is not null)
        {
            Check(volume.Value.GetMasterVolumeLevelScalar(out level));
            Check(volume.Value.GetMute(out muted));
        }
        return new AudioState((int)Math.Round(level * 100), muted, devices, _policy is not null);
    }

    public void SetVolume(int percent)
    {
        using var volume = DefaultVolume() ?? throw new InvalidOperationException(Strings.Get("audio.noOutput"));
        var context = Guid.Empty;
        Check(volume.Value.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context));
        if (percent > 0) Check(volume.Value.SetMute(false, ref context));
    }

    public void SetMute(bool mute)
    {
        using var volume = DefaultVolume() ?? throw new InvalidOperationException(Strings.Get("audio.noOutput"));
        var context = Guid.Empty;
        Check(volume.Value.SetMute(mute, ref context));
    }

    public void SetDefault(string deviceId)
    {
        if (_policy is null) throw new InvalidOperationException(Strings.Get("audio.cannotSwitch"));
        foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
        {
            Check(_policy.SetDefaultEndpoint(deviceId, role));
        }
    }

    /// <summary>Active output device with that id, or null (e.g. it's a microphone).</summary>
    public AudioDevice? FindOutput(string deviceId) => Devices().FirstOrDefault(d => d.Id == deviceId);

    public IReadOnlyList<AudioDevice> Devices()
    {
        var defaultId = "";
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var defaultDevice) == 0)
        {
            defaultDevice.GetId(out defaultId);
        }

        Check(_enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var collection));
        Check(collection.GetCount(out var count));
        var result = new List<AudioDevice>();
        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out var device) != 0) continue;
            device.GetId(out var id);
            var (name, formFactor) = Describe(device);
            result.Add(new AudioDevice(id, name, KindOf(formFactor), id == defaultId));
        }
        return result;
    }

    private static (string Name, uint FormFactor) Describe(IMMDevice device)
    {
        if (device.OpenPropertyStore(CoreAudioNative.StgmRead, out var store) != 0) return (Strings.Get("audio.device"), 0);
        var name = Strings.Get("audio.device");
        uint formFactor = 0;

        var key = PropertyKey.FriendlyName;
        if (store.GetValue(ref key, out var value) == 0)
        {
            if (value.VarType == PropVariant.VtLpwstr && value.Pointer != IntPtr.Zero) name = Marshal.PtrToStringUni(value.Pointer) ?? name;
            CoreAudioNative.PropVariantClear(ref value);
        }
        key = PropertyKey.FormFactor;
        if (store.GetValue(ref key, out value) == 0)
        {
            if (value.VarType == PropVariant.VtUi4) formFactor = value.UInt32;
            CoreAudioNative.PropVariantClear(ref value);
        }
        return (name, formFactor);
    }

    private static string KindOf(uint formFactor) => formFactor switch
    {
        FormFactor.Headphones or FormFactor.Headset => "headphones",
        FormFactor.DigitalAudioDisplayDevice => "tv",
        FormFactor.Speakers => "speakers",
        _ => "other"
    };

    // -----------------------------------------------------------------------
    // Per-app volume (audio sessions of all active outputs)

    private readonly Dictionary<int, string> _processNames = [];

    /// <summary>Apps currently holding an audio session on an active output, the running game first.</summary>
    public IReadOnlyList<AppVolumeEntry> AppVolumes(IReadOnlySet<int> gamePids)
    {
        var raw = new List<RawAudioSession>();
        var seen = new HashSet<int>();
        var own = new HashSet<int>();
        ForEachSession((control, volume) =>
        {
            if (control.GetState(out var state) == 0 && state == CoreAudioNative.AudioSessionStateExpired) return;
            var system = control.IsSystemSoundsSession() == 0;
            control.GetProcessId(out var pid);
            if (!system && pid == 0) return;
            control.GetDisplayName(out var display);
            volume.GetMasterVolume(out var level);
            volume.GetMute(out var muted);
            if (!system)
            {
                seen.Add((int)pid);
                if (IsOwnProcess((int)pid)) own.Add((int)pid);
            }
            raw.Add(new RawAudioSession((int)pid, system ? "" : ProcessName((int)pid), display, system, level, muted));
        });
        // Process ids get reused: forget names of processes that no longer hold a session.
        foreach (var pid in _processNames.Keys.Where(p => !seen.Contains(p)).ToList()) _processNames.Remove(pid);
        return AppVolume.Arrange(raw, gamePids, own);
    }

    /// <summary>Sets one app (a process id, or "system") on all of its sessions.</summary>
    public void SetAppVolume(string id, int percent) => ForSessionsOf(id, volume =>
    {
        var context = Guid.Empty;
        Check(volume.SetMasterVolume(Math.Clamp(percent, 0, 100) / 100f, ref context));
        if (percent > 0) Check(volume.SetMute(false, ref context));
    });

    public void SetAppMute(string id, bool mute) => ForSessionsOf(id, volume =>
    {
        var context = Guid.Empty;
        Check(volume.SetMute(mute, ref context));
    });

    private void ForSessionsOf(string id, Action<ISimpleAudioVolume> action)
    {
        var found = false;
        ForEachSession((control, volume) =>
        {
            var system = control.IsSystemSoundsSession() == 0;
            control.GetProcessId(out var pid);
            var key = system ? AppVolume.SystemId : ((int)pid).ToString(CultureInfo.InvariantCulture);
            if (key != id) return;
            found = true;
            action(volume);
        });
        if (!found) throw new InvalidOperationException(Strings.Get("audio.appSilent"));
    }

    /// <summary>Visits the sessions of every active output device; all COM objects are released.</summary>
    private void ForEachSession(Action<IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        if (_enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var devices) != 0) return;
        try
        {
            if (devices.GetCount(out var deviceCount) != 0) return;
            for (uint d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0) continue;
                try
                {
                    VisitDevice(device, visit);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(devices);
        }
    }

    private static void VisitDevice(IMMDevice device, Action<IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        var iid = CoreAudioNative.AudioSessionManager2Id;
        if (device.Activate(ref iid, CoreAudioNative.ClsctxAll, IntPtr.Zero, out var instance) != 0) return;
        var manager = (IAudioSessionManager2)instance;
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) != 0) return;
            try
            {
                Check(sessions.GetCount(out var count));
                for (var i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var control) != 0) continue;
                    try
                    {
                        // The session object also answers for ISimpleAudioVolume.
                        if (control is ISimpleAudioVolume volume) visit(control, volume);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private string ProcessName(int pid)
    {
        if (_processNames.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = "";
        }
        return _processNames[pid] = name;
    }

    /// <summary>CouchHud itself or one of its WebView2 helpers (they play the UI sounds and music).</summary>
    private static bool IsOwnProcess(int pid)
    {
        var self = Environment.ProcessId;
        for (var depth = 0; depth < 8 && pid > 0; depth++)
        {
            if (pid == self) return true;
            pid = ParentOf(pid);
        }
        return false;
    }

    private static int ParentOf(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            return CoreAudioNative.NtQueryInformationProcess(handle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private sealed class VolumeHandle(IAudioEndpointVolume value) : IDisposable
    {
        public IAudioEndpointVolume Value { get; } = value;

        public void Dispose() => Marshal.ReleaseComObject(Value);
    }

    private VolumeHandle? DefaultVolume()
    {
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0) return null;
        var iid = CoreAudioNative.AudioEndpointVolumeId;
        Check(device.Activate(ref iid, CoreAudioNative.ClsctxAll, IntPtr.Zero, out var instance));
        return new VolumeHandle((IAudioEndpointVolume)instance);
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_client);
    }

    [ComVisible(true)]
    private sealed class NotificationClient(AudioService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, uint newState)
        {
            if (newState == DeviceState.Active) owner.DeviceArrived?.Invoke(deviceId);
            owner.DevicesChanged?.Invoke();
        }

        public void OnDeviceAdded(string deviceId) => owner.DevicesChanged?.Invoke();

        public void OnDeviceRemoved(string deviceId) => owner.DevicesChanged?.Invoke();

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia) owner.DevicesChanged?.Invoke();
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
        }
    }
}
