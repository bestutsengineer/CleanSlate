using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CleanSlate
{
    /// <summary>
    /// Real monitor names (e.g. "DELL U2720Q") and stable monitor ids, read from the Windows display
    /// configuration. Keyed by GDI device name (\\.\DISPLAY1), which is how monitors are enumerated.
    /// </summary>
    internal static class DisplayNames
    {
        public sealed class Info
        {
            public string Name;       // monitor name from its EDID, or "Built-in display"
            public string DevicePath; // stable id of the physical monitor on this PC
        }

        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const uint GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;
        const uint TECH_DISPLAYPORT_EMBEDDED = 11, TECH_UDI_EMBEDDED = 13, TECH_INTERNAL = 0x80000000;

        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct PathSource { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
        [StructLayout(LayoutKind.Sequential)]
        struct PathTarget
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
            public uint scaling; public uint refreshNumerator, refreshDenominator; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource source; public PathTarget target; public uint flags; }
        [StructLayout(LayoutKind.Sequential)] struct ModeInfo { public uint infoType; public uint id; public LUID adapterId; public ulong d0, d1, d2, d3, d4, d5; }
        [StructLayout(LayoutKind.Sequential)] struct Header { public uint type; public uint size; public LUID adapterId; public uint id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TargetName
        {
            public Header header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName
        {
            public Header header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName request);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName request);

        public static Dictionary<string, Info> Get()
        {
            Dictionary<string, Info> result = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
            try
            {
                uint pathCount, modeCount;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0) return result;
                PathInfo[] paths = new PathInfo[pathCount];
                ModeInfo[] modes = new ModeInfo[modeCount];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

                for (int i = 0; i < pathCount; i++)
                {
                    SourceName source = new SourceName();
                    source.header.type = GET_SOURCE_NAME;
                    source.header.size = (uint)Marshal.SizeOf(typeof(SourceName));
                    source.header.adapterId = paths[i].source.adapterId;
                    source.header.id = paths[i].source.id;
                    if (DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrEmpty(source.viewGdiDeviceName)) continue;
                    if (result.ContainsKey(source.viewGdiDeviceName)) continue;   // duplicated (mirrored) screens: keep the first monitor

                    TargetName target = new TargetName();
                    target.header.type = GET_TARGET_NAME;
                    target.header.size = (uint)Marshal.SizeOf(typeof(TargetName));
                    target.header.adapterId = paths[i].target.adapterId;
                    target.header.id = paths[i].target.id;
                    if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                    uint tech = target.outputTechnology;
                    bool builtIn = tech == TECH_INTERNAL || tech == TECH_DISPLAYPORT_EMBEDDED || tech == TECH_UDI_EMBEDDED;
                    string name = (target.monitorFriendlyDeviceName ?? "").Trim();
                    Info info = new Info();
                    info.Name = builtIn ? "Built-in display" : name;
                    info.DevicePath = target.monitorDevicePath ?? "";
                    result[source.viewGdiDeviceName] = info;
                }
            }
            catch (Exception ex)
            {
                Log.Write("could not read display names: {0}", ex.Message);
            }
            return result;
        }
    }
}
