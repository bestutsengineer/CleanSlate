using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CleanSlate
{
    /// <summary>
    /// Hidden window that owns the tray menu, runs work posted with App.Post, and receives
    /// commands from a second CleanSlate.exe (for example "CleanSlate.exe --remove") via WM_COPYDATA.
    /// </summary>
    internal sealed class HostWindow : NativeWindow
    {
        public const string Title = "CleanSlate.Host.v2";
        public const int WM_RUN_QUEUE = Native.WM_APP + 1;
        static readonly IntPtr Magic = new IntPtr(0x434C534C); // "CLSL"

        readonly App _app;

        public HostWindow(App app)
        {
            _app = app;
            CreateParams cp = new CreateParams();
            cp.Caption = Title;
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_TOOLWINDOW;
            CreateHandle(cp);   // never shown
        }

        public static IntPtr Find()
        {
            return Native.FindWindow(null, Title);
        }

        /// <summary>Sends a command line to the running instance. False if it did not take it.</summary>
        public static bool Send(IntPtr target, string[] args)
        {
            string payload = string.Join("\n", args);
            IntPtr buffer = Marshal.StringToHGlobalUni(payload);
            try
            {
                COPYDATASTRUCT cds = new COPYDATASTRUCT();
                cds.dwData = Magic;
                cds.cbData = (payload.Length + 1) * 2;
                cds.lpData = buffer;
                IntPtr result;
                IntPtr ok = Native.SendMessageTimeout(target, Native.WM_COPYDATA, IntPtr.Zero, ref cds, Native.SMTO_ABORTIFHUNG, 3000, out result);
                return ok != IntPtr.Zero && result != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Close()
        {
            DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_COPYDATA)
            {
                COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
                if (cds.dwData == Magic && _app.Accepting)
                {
                    string payload = Marshal.PtrToStringUni(cds.lpData) ?? "";
                    string[] args = payload.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    _app.Post(delegate { _app.ExecuteRemote(args); });
                    m.Result = new IntPtr(1);
                }
                else
                {
                    m.Result = IntPtr.Zero;   // shutting down: the sender will start its own instance
                }
                return;
            }
            if (m.Msg == WM_RUN_QUEUE)
            {
                _app.RunQueue();
                return;
            }
            base.WndProc(ref m);
        }
    }
}
