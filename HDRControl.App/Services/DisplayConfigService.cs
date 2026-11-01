using System.ComponentModel;
using System.Runtime.InteropServices;
using HDRControl.App.Models;

namespace HDRControl.App.Services;

public sealed class DisplayConfigService
{
    private const uint QueryOnlyActivePaths = 0x00000002;
    private const uint QueryVirtualModeAware = 0x00000010;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private const int DeviceInfoGetTargetName = 2;
    private const int DeviceInfoGetAdvancedColorInfo = 9;
    private const int DeviceInfoSetAdvancedColorState = 10;
    private const int DeviceInfoGetAdvancedColorInfo2 = 15;
    private const int DeviceInfoSetHdrState = 16;

    private const uint AdvancedColorSupported = 1 << 0;
    private const uint AdvancedColorEnabled = 1 << 1;
    private const uint HdrSupported = 1 << 4;

    private const int AdvancedColorModeSdr = 0;
    private const int AdvancedColorModeWcg = 1;
    private const int AdvancedColorModeHdr = 2;

    public IReadOnlyList<MonitorInfo> QueryAllDisplays()
    {
        if (DummyDisplays.IsActive)
        {
            return DummyDisplays.Create();
        }

        var paths = QueryActivePaths();
        var displays = new List<MonitorInfo>(paths.Length);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (path.TargetInfo.TargetAvailable == 0)
            {
                continue;
            }

            var adapter = path.TargetInfo.AdapterId;
            var identity = $"{adapter.HighPart:X8}:{adapter.LowPart:X8}:{path.TargetInfo.Id:X8}";
            if (!seenTargets.Add(identity))
            {
                continue;
            }

            string name;
            string? diagnostic = null;
            try
            {
                var target = GetTargetName(adapter, path.TargetInfo.Id);
                name = (target.Flags & 1) != 0
                    ? target.MonitorFriendlyDeviceName.TrimEnd('\0')
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "Unknown display";
                }
            }
            catch (Win32Exception error)
            {
                name = "Unknown display";
                diagnostic = $"Display name could not be read: {error.Message}";
            }

            HdrStatus status;
            var usesHdrSpecificApi = false;
            try
            {
                status = TryGetHdrStatus(adapter, path.TargetInfo.Id, out usesHdrSpecificApi);
            }
            catch (Win32Exception error)
            {
                status = HdrStatus.Unknown;
                var statusError = $"HDR status could not be read: {error.Message}";
                diagnostic = diagnostic is null
                    ? statusError
                    : $"{diagnostic} {statusError}";
            }

            displays.Add(new MonitorInfo(
                name,
                path.TargetInfo.Id,
                adapter.LowPart,
                adapter.HighPart,
                status,
                diagnostic,
                usesHdrSpecificApi));
        }

        return displays;
    }

    public void SetHdrEnabled(MonitorInfo monitor, bool enabled)
    {
        if (DummyDisplays.IsActive)
        {
            return;
        }

        if (monitor.Status is not (HdrStatus.Enabled or HdrStatus.Disabled))
        {
            throw new InvalidOperationException(
                $"HDR cannot be changed for \"{monitor.Name}\".");
        }

        var adapter = new Luid(monitor.AdapterLowPart, monitor.AdapterHighPart);
        if (monitor.UsesHdrSpecificApi)
        {
            var request = new SetHdrState
            {
                Header = DeviceInfoHeader.Create<SetHdrState>(
                    DeviceInfoSetHdrState, adapter, monitor.TargetId),
                Value = enabled ? 1u : 0u
            };
            ThrowIfFailed(DisplayConfigSetDeviceInfo(ref request), "HDR could not be changed");
        }
        else
        {
            var request = new SetAdvancedColorState
            {
                Header = DeviceInfoHeader.Create<SetAdvancedColorState>(
                    DeviceInfoSetAdvancedColorState, adapter, monitor.TargetId),
                Value = enabled ? 1u : 0u
            };
            ThrowIfFailed(
                DisplayConfigSetDeviceInfo(ref request),
                "Advanced color settings could not be changed");
        }
    }

    private static DisplayConfigPathInfo[] QueryActivePaths()
    {
        const uint flags = QueryOnlyActivePaths | QueryVirtualModeAware;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var result = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            ThrowIfFailed(result, "GetDisplayConfigBufferSizes failed");

            var paths = new DisplayConfigPathInfo[Math.Max(pathCount, 1)];
            var modes = new DisplayConfigModeInfo[Math.Max(modeCount, 1)];
            result = QueryDisplayConfig(
                flags,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);

            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }

            ThrowIfFailed(result, "QueryDisplayConfig failed");
            Array.Resize(ref paths, checked((int)pathCount));
            return paths;
        }

        throw new Win32Exception(
            ErrorInsufficientBuffer,
            "The display configuration changed repeatedly during detection.");
    }

    private static HdrStatus TryGetHdrStatus(Luid adapter, uint targetId, out bool usesHdrSpecificApi)
    {
        var advancedInfo = new AdvancedColorInfo2
        {
            Header = DeviceInfoHeader.Create<AdvancedColorInfo2>(
                DeviceInfoGetAdvancedColorInfo2, adapter, targetId)
        };
        var result = DisplayConfigGetDeviceInfo(ref advancedInfo);
        if (result == ErrorSuccess)
        {
            usesHdrSpecificApi = true;
            if ((advancedInfo.Value & HdrSupported) == 0)
            {
                return HdrStatus.NotSupported;
            }

            return advancedInfo.ActiveColorMode switch
            {
                AdvancedColorModeHdr => HdrStatus.Enabled,
                AdvancedColorModeSdr or AdvancedColorModeWcg => HdrStatus.Disabled,
                _ => HdrStatus.Unknown
            };
        }

        usesHdrSpecificApi = false;
        var legacyInfo = new AdvancedColorInfo
        {
            Header = DeviceInfoHeader.Create<AdvancedColorInfo>(
                DeviceInfoGetAdvancedColorInfo, adapter, targetId)
        };
        ThrowIfFailed(
            DisplayConfigGetDeviceInfo(ref legacyInfo),
            "HDR status could not be read with the legacy DisplayConfig API");

        if ((legacyInfo.Value & AdvancedColorSupported) == 0)
        {
            return HdrStatus.NotSupported;
        }

        return (legacyInfo.Value & AdvancedColorEnabled) != 0
            ? HdrStatus.Enabled
            : HdrStatus.Disabled;
    }

    private static TargetDeviceName GetTargetName(Luid adapter, uint targetId)
    {
        var request = new TargetDeviceName
        {
            Header = DeviceInfoHeader.Create<TargetDeviceName>(
                DeviceInfoGetTargetName, adapter, targetId),
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty
        };
        ThrowIfFailed(
            DisplayConfigGetDeviceInfo(ref request),
            "DisplayConfigGetDeviceInfo (display name) failed");
        return request;
    }

    private static void ThrowIfFailed(int errorCode, string message)
    {
        if (errorCode != ErrorSuccess)
        {
            throw new Win32Exception(
                errorCode,
                $"{message} (Windows error code {errorCode})");
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numberOfPathArrayElements,
        out uint numberOfModeInfoArrayElements);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numberOfPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numberOfModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2 requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigSetDeviceInfo(ref SetAdvancedColorState setPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo", ExactSpelling = true)]
    private static extern int DisplayConfigSetDeviceInfo(ref SetHdrState setPacket);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Luid(uint lowPart, int highPart)
    {
        public readonly uint LowPart = lowPart;
        public readonly int HighPart = highPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public DisplayConfigRational RefreshRate;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public int InfoType;
        public uint Id;
        public Luid AdapterId;
        public DisplayConfigModeInfoUnion ModeInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DisplayConfigModeInfoUnion
    {
        [FieldOffset(0)]
        private ulong _field0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;

        public static DeviceInfoHeader Create<T>(int type, Luid adapter, uint id)
            where T : struct =>
            new()
            {
                Type = type,
                Size = (uint)Marshal.SizeOf<T>(),
                AdapterId = adapter,
                Id = id
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo2
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
        public int ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetAdvancedColorState
    {
        public DeviceInfoHeader Header;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetHdrState
    {
        public DeviceInfoHeader Header;
        public uint Value;
    }
}
