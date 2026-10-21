using System;
using System.Diagnostics;
using System.IO;

namespace CleanSlate
{
    /// <summary>Small append-only log in %LOCALAPPDATA%\CleanSlate\log.txt (rotated at 512 KB). Off unless enabled in the settings.</summary>
    internal static class Log
    {
        const long MaxBytes = 512 * 1024;
        static readonly object Gate = new object();
        static readonly int Pid = Process.GetCurrentProcess().Id;

        /// <summary>Set from the "Keep a log" setting at startup and when it is toggled.</summary>
        public static bool Enabled;

        public static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSlate", "log.txt");
            }
        }

        public static void Write(string format, params object[] args)
        {
            if (!Enabled) return;
            string message = args.Length == 0 ? format : string.Format(format, args);
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.Now, Pid, message, Environment.NewLine);
            lock (Gate)
            {
                try
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, line);
                }
                catch
                {
                    // Logging must never break the app (another instance may hold the file briefly).
                }
            }
        }
    }
}
