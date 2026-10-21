using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CleanSlate
{
    /// <summary>"Start with Windows": a value under HKCU\...\CurrentVersion\Run (no admin rights needed).</summary>
    internal static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "CleanSlate";

        static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        string value = key == null ? null : key.GetValue(ValueName) as string;
                        return value != null && string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
            Log.Write("start with Windows: {0}", enabled ? "on" : "off");
        }
    }
}
