using System;
using System.Threading;
using System.Windows.Forms;

namespace CleanSlate
{
    internal static class Program
    {
        const string MutexName = @"Local\CleanSlate.Instance.v2";

        [STAThread]
        static int Main(string[] args)
        {
            Log.Enabled = Settings.Load().Logging;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Log.Write("unhandled: {0}", e.Exception); };

            Command cmd;
            try
            {
                cmd = Command.Parse(args);
            }
            catch (ArgumentException ex)
            {
                ShowUsage(ex.Message);
                return 2;
            }

            try
            {
                if (cmd.Kind == CommandKind.Help)
                {
                    ShowUsage(null);
                    return 0;
                }
                if (cmd.Kind == CommandKind.List) return List(cmd);
                return Run(cmd);
            }
            catch (Exception ex)
            {
                Log.Write("fatal: {0}", ex);
                return 1;
            }
        }

        /// <summary>Hands the command to the running CleanSlate, or becomes the running CleanSlate.</summary>
        static int Run(Command cmd)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                bool owned = createdNew;
                for (int i = 0; i < 50 && !owned; i++)
                {
                    IntPtr host = HostWindow.Find();
                    if (host != IntPtr.Zero && HostWindow.Send(host, cmd.ToArgs()))
                    {
                        Log.Write("handed to the running instance: {0}", cmd);
                        return 0;
                    }
                    try
                    {
                        owned = mutex.WaitOne(100);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }
                }
                if (!owned)
                {
                    Log.Write("could not reach the running instance, giving up on: {0}", cmd);
                    return 1;
                }

                try
                {
                    if (cmd.Kind == CommandKind.Exit) return 0;   // nothing running, nothing to stop
                    using (App app = new App())
                    {
                        if (cmd.Kind != CommandKind.Start) app.Post(delegate { app.Execute(cmd); });
                        app.Run();
                    }
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
            return 0;
        }

        static int List(Command cmd)
        {
            AttachConsoleIfNeeded();
            Console.WriteLine("Displays (left to right):");
            foreach (ScreenInfo s in Screens.GetAll())
                Console.WriteLine("  {0}. {1,-32} {2}x{3} at {4},{5}", s.Number, s.Label, s.Bounds.Width, s.Bounds.Height, s.Bounds.Left, s.Bounds.Top);
            Console.WriteLine();
            Console.WriteLine("Windows that would be closed:");
            foreach (IntPtr h in WindowOps.TaskbarWindows(cmd.OnlyPid))
                Console.WriteLine("  {0}{1}", WindowOps.Describe(h), Native.IsWindowEnabled(h) ? "" : "   <- has a dialog open, would be skipped");
            Console.Out.Flush();
            return 0;
        }

        static void ShowUsage(string error)
        {
            string text = (error != null ? error + "\n\n" : "") + Command.Usage;
            AttachConsoleIfNeeded();
            try { Console.Error.WriteLine(text); } catch { }
            MessageBox.Show(text, "CleanSlate", MessageBoxButtons.OK, error != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        static void AttachConsoleIfNeeded()
        {
            // A GUI exe has no console; borrow the parent's unless output is already redirected.
            IntPtr stdout = Native.GetStdHandle(-11);
            if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) Native.AttachConsole(-1);
        }
    }
}
