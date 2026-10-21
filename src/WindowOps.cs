using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanSlate
{
    /// <summary>Finding, closing and moving other applications' top-level windows.</summary>
    internal static class WindowOps
    {
        static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;

        // Desktop and taskbar windows are never touched.
        static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

        /// <summary>True if the window belongs to another process (and to onlyPid, when that is set).</summary>
        public static bool IsOthersWindow(IntPtr h, uint onlyPid)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            return pid != OwnPid && (onlyPid == 0 || pid == onlyPid);
        }

        /// <summary>
        /// The taskbar's own rule for which windows get a button: visible, and either WS_EX_APPWINDOW,
        /// or unowned and not a tool / no-activate window. Cloaked windows (other virtual desktops,
        /// suspended store apps) are skipped because the taskbar does not show them either.
        /// </summary>
        public static bool IsTaskbarWindow(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_APPWINDOW) == 0)
            {
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
                if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0) return false;
            }
            if (IsCloaked(h) || IsShellWindow(h)) return false;
            return true;
        }

        public static List<IntPtr> TaskbarWindows(uint onlyPid)
        {
            List<IntPtr> result = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (IsTaskbarWindow(h) && IsOthersWindow(h, onlyPid)) result.Add(h);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// Asks each window to close exactly like its X button does (WM_SYSCOMMAND / SC_CLOSE, same as Alt+F4).
        /// Apps can still show "save changes?" prompts or refuse. Nothing is killed.
        /// </summary>
        public static int CloseAll(List<IntPtr> windows, bool dryRun, Dictionary<IntPtr, DateTime> closing)
        {
            int sent = 0;
            foreach (IntPtr h in windows)
            {
                if (!Native.IsWindow(h)) continue;
                string what = Describe(h);
                if (!Native.IsWindowEnabled(h))
                {
                    // A disabled window has a modal dialog open; its X button would do nothing either.
                    Log.Write("skip (window is busy with a dialog): {0}", what);
                    continue;
                }
                if (dryRun)
                {
                    Log.Write("dry run, would close: {0}", what);
                    continue;
                }
                if (Native.PostMessage(h, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_CLOSE), IntPtr.Zero))
                {
                    closing[h] = DateTime.UtcNow;
                    sent++;
                    Log.Write("close: {0}", what);
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Write("could not close ({0}): {1}", err == 5 ? "access denied, probably an admin window" : "error " + err, what);
                }
            }
            Log.Write("asked {0} of {1} window(s) to close", sent, windows.Count);
            return sent;
        }

        /// <summary>Windows worth moving off a gray screen: real, visible, not minimized, with a title bar or a taskbar button.</summary>
        public static bool IsMovable(IntPtr h)
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
            int style = Native.GetWindowLong(h, Native.GWL_STYLE);
            if ((style & Native.WS_CHILD) != 0) return false;
            if (IsCloaked(h) || IsShellWindow(h)) return false;
            bool hasTitleBar = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
            return hasTitleBar || IsTaskbarWindow(h);
        }

        /// <summary>True while the user is dragging or resizing the window.</summary>
        public static bool IsBeingDragged(IntPtr h)
        {
            uint pid;
            uint thread = Native.GetWindowThreadProcessId(h, out pid);
            GUITHREADINFO info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            return Native.GetGUIThreadInfo(thread, ref info) && (info.flags & Native.GUI_INMOVESIZE) != 0;
        }

        /// <summary>Moves a window from one screen to the same relative spot on another, keeping it maximized if it was.</summary>
        public static void MoveToScreen(IntPtr h, ScreenInfo from, ScreenInfo to, RECT primaryWork)
        {
            if (Native.IsHungAppWindow(h)) return;

            if (Native.IsZoomed(h))
            {
                WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (!Native.GetWindowPlacement(h, ref wp)) return;
                // rcNormalPosition is in workspace coordinates (relative to the primary work area) unless it is a tool window.
                bool tool = (Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0;
                int ox = tool ? 0 : primaryWork.Left, oy = tool ? 0 : primaryWork.Top;
                RECT normal = wp.rcNormalPosition.Offset(ox, oy);
                if (Native.MonitorFromRect(ref normal, Native.MONITOR_DEFAULTTONEAREST) != to.Handle)
                    normal = Fit(normal, from.Work, to.Work);
                wp.rcNormalPosition = normal.Offset(-ox, -oy);
                // Re-maximizing an already maximized window does not move it, so restore it onto the
                // focus screen first and maximize it there. Both calls are queued to the window's own
                // thread in order, so a busy app cannot block us.
                wp.showCmd = Native.SW_SHOWNOACTIVATE;
                wp.flags = Native.WPF_ASYNCWINDOWPLACEMENT;
                Native.SetWindowPlacement(h, ref wp);
                Native.ShowWindowAsync(h, Native.SW_SHOWMAXIMIZED);
                return;
            }

            RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            RECT target = r.Same(from.Bounds) ? to.Bounds : Fit(r, from.Work, to.Work);
            Native.SetWindowPos(h, Native.HWND_TOP, target.Left, target.Top, target.Width, target.Height,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNCWINDOWPOS);
        }

        /// <summary>Maps a rectangle from one area to another, keeping its relative position and shrinking it if needed.</summary>
        static RECT Fit(RECT r, RECT src, RECT dst)
        {
            int w = Math.Min(r.Width, dst.Width), h = Math.Min(r.Height, dst.Height);
            double fx = src.Width > r.Width ? (double)(r.Left - src.Left) / (src.Width - r.Width) : 0.5;
            double fy = src.Height > r.Height ? (double)(r.Top - src.Top) / (src.Height - r.Height) : 0.5;
            fx = Math.Max(0, Math.Min(1, fx));
            fy = Math.Max(0, Math.Min(1, fy));
            int x = dst.Left + (int)Math.Round(fx * (dst.Width - w));
            int y = dst.Top + (int)Math.Round(fy * (dst.Height - h));
            return new RECT(x, y, x + w, y + h);
        }

        public static bool IsCloaked(IntPtr h)
        {
            int cloaked;
            return Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }

        static bool IsShellWindow(IntPtr h)
        {
            return Array.IndexOf(ShellClasses, ClassOf(h)) >= 0;
        }

        public static string ClassOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            Native.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TitleOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            Native.GetWindowText(h, sb, sb.Capacity);
            string t = sb.ToString();
            return t.Length > 80 ? t.Substring(0, 77) + "..." : t;
        }

        public static string Describe(IntPtr h)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            string process;
            try
            {
                using (Process p = Process.GetProcessById((int)pid)) process = p.ProcessName;
            }
            catch
            {
                process = "pid " + pid;
            }
            return string.Format("{0} \"{1}\" [{2}]", process, TitleOf(h), ClassOf(h));
        }
    }
}
