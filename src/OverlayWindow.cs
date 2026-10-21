using System;
using System.Windows.Forms;

namespace CleanSlate
{
    /// <summary>
    /// A plain gray, topmost, full-screen window covering one screen (taskbar included).
    /// It is only a window: the wallpaper and taskbar are never changed, so if the process
    /// dies for any reason Windows removes the window and the screen is back to normal.
    /// </summary>
    internal sealed class OverlayWindow : NativeWindow
    {
        readonly App _app;

        public ScreenInfo Screen { get; private set; }

        public OverlayWindow(App app, ScreenInfo screen)
        {
            _app = app;
            Screen = screen;

            CreateParams cp = new CreateParams();
            cp.Caption = "CleanSlate gray screen";
            cp.Style = Native.WS_POPUP;
            // Tool window: no taskbar button, not in Alt+Tab, and shown on every virtual desktop.
            // No-activate: the gray never becomes the active window, not even when you close a window and
            // Windows picks the next one. An active window that fills a screen counts as a "full-screen app",
            // which makes Windows switch on Do not disturb by itself.
            cp.ExStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            cp.X = screen.Bounds.Left;
            cp.Y = screen.Bounds.Top;
            cp.Width = screen.Bounds.Width;
            cp.Height = screen.Bounds.Height;
            CreateHandle(cp);
            Place();
            Native.UpdateWindow(Handle);
        }

        public void MoveTo(ScreenInfo screen)
        {
            Screen = screen;
            Place();
        }

        /// <summary>Re-applies the exact screen bounds and puts the window back on top of the taskbar.</summary>
        public void Place()
        {
            RECT b = Screen.Bounds;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
        }

        /// <summary>Repaints with the app's current gray brush (after a color change).</summary>
        public void Repaint()
        {
            Native.InvalidateRect(Handle, IntPtr.Zero, true);
        }

        public void Close()
        {
            DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_ERASEBKGND:
                {
                    RECT rc;
                    Native.GetClientRect(m.HWnd, out rc);
                    Native.FillRect(m.WParam, ref rc, _app.GrayBrush);
                    m.Result = new IntPtr(1);
                    return;
                }
                case Native.WM_PAINT:
                {
                    PAINTSTRUCT ps;
                    IntPtr dc = Native.BeginPaint(m.HWnd, out ps);
                    Native.FillRect(dc, ref ps.rcPaint, _app.GrayBrush);
                    Native.EndPaint(m.HWnd, ref ps);
                    m.Result = IntPtr.Zero;
                    return;
                }
                case Native.WM_MOUSEACTIVATE:
                    // Clicks on the gray never take focus from the screen you work on.
                    m.Result = new IntPtr(Native.MA_NOACTIVATE);
                    return;
                case Native.WM_RBUTTONUP:
                    // The menu belongs to CleanSlate's hidden window, so the gray itself is never activated.
                    _app.ShowMenu(_app.MenuOwner);
                    return;
                case Native.WM_DPICHANGED:
                    // Keep exact physical bounds instead of the size Windows suggests.
                    Place();
                    m.Result = IntPtr.Zero;
                    return;
                case Native.WM_CLOSE:
                    // Alt+F4 on the gray (or anyone closing it) turns the gray off.
                    _app.RequestRemoveGray("gray screen was closed");
                    m.Result = IntPtr.Zero;
                    return;
            }
            base.WndProc(ref m);
        }
    }
}
