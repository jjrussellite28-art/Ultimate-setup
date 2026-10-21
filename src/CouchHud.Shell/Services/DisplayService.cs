using System.Runtime.InteropServices;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

public sealed record DisplayMode(int Width, int Height, int Hz);

public sealed record DisplayState(DisplayMode Current, IReadOnlyList<DisplayMode> Modes, bool HdrSupported, bool HdrOn);

/// <summary>
/// Resolution, refresh rate and HDR of the main display, like the video settings of a
/// console. Changes go through the same Win32 calls the Windows settings app uses.
/// </summary>
public static class DisplayService
{
    public static DisplayState Get()
    {
        var current = CurrentMode() ?? new DisplayMode(0, 0, 0);
        var modes = new HashSet<DisplayMode>();
        var dm = NewDevMode();
        for (var i = 0; EnumDisplaySettingsW(null, i, ref dm); i++)
        {
            // Only full colour modes the TV can show at a sensible size.
            if (dm.dmBitsPerPel == 32 && dm.dmPelsWidth >= 1024 && dm.dmDisplayFrequency > 1)
            {
                modes.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            }
            dm = NewDevMode();
        }
        var (hdrSupported, hdrOn) = Hdr.Get();
        return new DisplayState(
            current,
            modes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Hz).ToList(),
            hdrSupported,
            hdrOn);
    }

    public static void SetMode(DisplayMode mode)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref dm)) throw new InvalidOperationException(Strings.Get("display.notFound"));
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

        // Ask first: an unsupported mode would otherwise leave a black screen.
        var test = ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != DispChangeSuccessful) throw new InvalidOperationException(Strings.Get("display.modeUnsupported", mode.Width, mode.Height, mode.Hz));
        var result = ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (result != DispChangeSuccessful) throw new InvalidOperationException(Strings.Get("display.switchFailed", result));
    }

    public static void SetHdr(bool on)
    {
        if (!Hdr.Set(on)) throw new InvalidOperationException(Strings.Get("display.hdrFailed"));
    }

    private static DisplayMode? CurrentMode()
    {
        var dm = NewDevMode();
        return EnumDisplaySettingsW(null, EnumCurrentSettings, ref dm) ? new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    private const int EnumCurrentSettings = -1;
    private const int DmPelsWidth = 0x80000;
    private const int DmPelsHeight = 0x100000;
    private const int DmDisplayFrequency = 0x400000;
    private const int CdsUpdateRegistry = 0x1;
    private const int CdsTest = 0x2;
    private const int DispChangeSuccessful = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr param);

    /// <summary>HDR ("advanced color") through the display configuration API, for every active display.</summary>
    private static class Hdr
    {
        private const uint QdcOnlyActivePaths = 0x2;
        private const int GetAdvancedColorInfo = 9;
        private const int SetAdvancedColorState = 10;

        public static (bool Supported, bool On) Get()
        {
            var supported = false;
            var on = false;
            foreach (var (adapter, id) in Targets())
            {
                var info = new AdvancedColorInfo
                {
                    header = new Header { type = GetAdvancedColorInfo, size = Marshal.SizeOf<AdvancedColorInfo>(), adapterId = adapter, id = id }
                };
                if (DisplayConfigGetDeviceInfo(ref info) != 0) continue;
                supported |= (info.value & 0x1) != 0;
                on |= (info.value & 0x2) != 0;
            }
            return (supported, on);
        }

        public static bool Set(bool on)
        {
            var changed = false;
            foreach (var (adapter, id) in Targets())
            {
                var state = new AdvancedColorState
                {
                    header = new Header { type = SetAdvancedColorState, size = Marshal.SizeOf<AdvancedColorState>(), adapterId = adapter, id = id },
                    value = on ? 1u : 0u
                };
                changed |= DisplayConfigSetDeviceInfo(ref state) == 0;
            }
            return changed;
        }

        private static List<(Luid Adapter, uint Id)> Targets()
        {
            var result = new List<(Luid, uint)>();
            try
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return result;
                var paths = new PathInfo[pathCount];
                var modes = new ModeInfo[modeCount];
                if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;
                for (var i = 0; i < pathCount; i++) result.Add((paths[i].targetAdapterId, paths[i].targetId));
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            public int type;
            public int size;
            public Luid adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AdvancedColorInfo
        {
            public Header header;
            public uint value;
            public int colorEncoding;
            public int bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AdvancedColorState
        {
            public Header header;
            public uint value;
        }

        // DISPLAYCONFIG_PATH_INFO, flattened: source info (20 bytes), target info (48), flags.
        [StructLayout(LayoutKind.Sequential)]
        private struct PathInfo
        {
            public Luid sourceAdapterId;
            public uint sourceId;
            public uint sourceModeInfoIdx;
            public uint sourceStatusFlags;
            public Luid targetAdapterId;
            public uint targetId;
            public uint targetModeInfoIdx;
            public int outputTechnology;
            public int rotation;
            public int scaling;
            public uint refreshNumerator;
            public uint refreshDenominator;
            public int scanLineOrdering;
            public int targetAvailable;
            public uint targetStatusFlags;
            public uint flags;
        }

        // DISPLAYCONFIG_MODE_INFO: only its size matters here.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct ModeInfo
        {
            public int infoType;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out int numPathArrayElements, out int numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref int numPathArrayElements, [Out] PathInfo[] pathArray, ref int numModeInfoArrayElements, [Out] ModeInfo[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(ref AdvancedColorState setPacket);
    }
}
