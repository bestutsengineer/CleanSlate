using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CleanSlate
{
    internal sealed class ScreenInfo
    {
        public IntPtr Handle;
        public string Device;   // GDI name, e.g. \\.\DISPLAY1 (can change when monitors are reconnected)
        public string Id;       // stable id of the physical monitor (empty if Windows does not report one)
        public string Name;     // monitor name, e.g. "DELL U2720Q"
        public RECT Bounds;     // physical pixels (the process is per-monitor DPI aware)
        public RECT Work;       // bounds minus the taskbar
        public bool Primary;
        public int Number;      // 1-based, left to right (or top to bottom); for the command line
        public string Label;    // what menus and the log show, e.g. "DELL U2720Q (primary)"
    }

    internal static class Screens
    {
        public static List<ScreenInfo> GetAll()
        {
            List<ScreenInfo> list = new List<ScreenInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data)
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (Native.GetMonitorInfo(h, ref mi))
                {
                    ScreenInfo s = new ScreenInfo();
                    s.Handle = h;
                    s.Device = mi.szDevice;
                    s.Bounds = mi.rcMonitor;
                    s.Work = mi.rcWork;
                    s.Primary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
                    list.Add(s);
                }
                return true;
            }, IntPtr.Zero);
            Number(list);
            Name(list);
            return list;
        }

        static void Number(List<ScreenInfo> list)
        {
            list.Sort(delegate(ScreenInfo a, ScreenInfo b)
            {
                return a.Bounds.Left != b.Bounds.Left ? a.Bounds.Left.CompareTo(b.Bounds.Left) : a.Bounds.Top.CompareTo(b.Bounds.Top);
            });
            bool horizontal = true;
            for (int i = 1; i < list.Count; i++)
                if (list[i].Bounds.Left < list[i - 1].Bounds.Right) horizontal = false;
            if (!horizontal)
            {
                list.Sort(delegate(ScreenInfo a, ScreenInfo b)
                {
                    return a.Bounds.Top != b.Bounds.Top ? a.Bounds.Top.CompareTo(b.Bounds.Top) : a.Bounds.Left.CompareTo(b.Bounds.Left);
                });
            }
            for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        }

        static void Name(List<ScreenInfo> list)
        {
            Dictionary<string, DisplayNames.Info> names = DisplayNames.Get();
            foreach (ScreenInfo s in list)
            {
                DisplayNames.Info info;
                if (names.TryGetValue(s.Device, out info))
                {
                    s.Name = info.Name;
                    s.Id = info.DevicePath;
                }
                if (string.IsNullOrEmpty(s.Name)) s.Name = "Display " + s.Number;
                if (s.Id == null) s.Id = "";
            }
            // Identical monitors get a number so they can be told apart (in left-to-right order).
            foreach (ScreenInfo s in list)
            {
                string name = s.Name;
                List<ScreenInfo> same = list.FindAll(delegate(ScreenInfo x) { return x.Name == name; });
                string label = same.Count > 1 ? name + " #" + (same.IndexOf(s) + 1) : name;
                s.Label = s.Primary ? label + " (primary)" : label;
            }
        }

        public static ScreenInfo Primary(List<ScreenInfo> list)
        {
            return list.Find(delegate(ScreenInfo s) { return s.Primary; }) ?? list[0];
        }

        public static ScreenInfo FromWindow(List<ScreenInfo> list, IntPtr hwnd)
        {
            IntPtr h = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTOPRIMARY);
            return list.Find(delegate(ScreenInfo s) { return s.Handle == h; }) ?? Primary(list);
        }

        /// <summary>Finds a specific monitor: by its stable id first, then by name.</summary>
        public static ScreenInfo FindDisplay(List<ScreenInfo> list, string id, string name)
        {
            ScreenInfo found = null;
            if (!string.IsNullOrEmpty(id))
                found = list.Find(delegate(ScreenInfo s) { return string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase); });
            if (found == null && !string.IsNullOrEmpty(name))
            {
                // Exact name or label first, then a partial name ("dell" finds "DELL U2720Q").
                found = list.Find(delegate(ScreenInfo s) { return string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Label, name, StringComparison.OrdinalIgnoreCase); })
                     ?? list.Find(delegate(ScreenInfo s) { return s.Label.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0; });
            }
            return found;
        }

        /// <summary>
        /// Resolves a focus screen:
        ///   "primary", "active" (screen of the last active window), "display" (the monitor given by id/name),
        ///   a number (left to right), or any other text as a monitor name.
        /// Anything that is not connected falls back to the primary screen.
        /// </summary>
        public static ScreenInfo Resolve(string target, string displayId, string displayName, List<ScreenInfo> list, IntPtr activeWindow)
        {
            string t = (target ?? Settings.Primary).Trim();
            if (string.Equals(t, Settings.Primary, StringComparison.OrdinalIgnoreCase)) return Primary(list);
            if (string.Equals(t, Settings.Active, StringComparison.OrdinalIgnoreCase))
                return activeWindow != IntPtr.Zero && Native.IsWindow(activeWindow) ? FromWindow(list, activeWindow) : Primary(list);

            ScreenInfo found;
            int n;
            if (string.Equals(t, Settings.Display, StringComparison.OrdinalIgnoreCase)) found = FindDisplay(list, displayId, displayName);
            else if (int.TryParse(t, out n)) found = list.Find(delegate(ScreenInfo s) { return s.Number == n; });
            else found = FindDisplay(list, null, t);

            if (found != null) return found;
            Log.Write("display '{0}' is not connected; using the primary screen", t == Settings.Display ? displayName : t);
            return Primary(list);
        }

        /// <summary>Changes whenever a screen is added, removed, moved or resized.</summary>
        public static string Stamp(List<ScreenInfo> list)
        {
            List<string> parts = new List<string>();
            foreach (ScreenInfo s in list) parts.Add(s.Device + "@" + s.Bounds + (s.Primary ? "*" : ""));
            return string.Join(";", parts.ToArray());
        }
    }
}
