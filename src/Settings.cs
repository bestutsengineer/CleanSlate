using System;
using System.Globalization;
using System.IO;

namespace CleanSlate
{
    /// <summary>User settings, stored as key=value lines in %APPDATA%\CleanSlate\settings.ini.</summary>
    internal sealed class Settings
    {
        // Left-click actions
        public const string CloseGray = "close-gray";
        public const string CloseOnly = "close-only";
        public const string GrayOnly = "gray-only";

        // Focus screen kinds
        public const string Primary = "primary";
        public const string Active = "active";
        public const string Display = "display";   // a specific monitor: FocusDisplayId / FocusDisplayName

        public static readonly string[,] Shades =
        {
            { "Light gray", "#A6A6A6" },
            { "Medium gray", "#808080" },
            { "Dark gray", "#404040" }
        };

        /// <summary>What a left click on the tray icon does.</summary>
        public string ClickAction = CloseGray;

        /// <summary>The screen that stays visible: primary | active | display.</summary>
        public string FocusScreen = Primary;

        /// <summary>With FocusScreen=display: the monitor's stable id and its name (the name is the fallback).</summary>
        public string FocusDisplayId = "";
        public string FocusDisplayName = "";

        /// <summary>Color of the gray screens as #RRGGBB.</summary>
        public string GrayColor = "#808080";

        /// <summary>Turn on Windows "Do not disturb" while the gray is on.</summary>
        public bool DoNotDisturb;

        /// <summary>Write a log to %LOCALAPPDATA%\CleanSlate\log.txt (off by default).</summary>
        public bool Logging;

        /// <summary>Internal: the Do not disturb setting to put back, while CleanSlate has it turned on.</summary>
        public string DndRestore = "";

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CleanSlate"); }
        }

        static string FilePath
        {
            get { return Path.Combine(Folder, "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "clickaction": s.ClickAction = NormalizeAction(value) ?? s.ClickAction; break;
                        case "focusscreen": s.FocusScreen = NormalizeFocus(value) ?? s.FocusScreen; break;
                        case "focusdisplayid": s.FocusDisplayId = value; break;
                        case "focusdisplayname": s.FocusDisplayName = value; break;
                        case "graycolor": if (ParseColor(value) >= 0) s.GrayColor = value; break;
                        case "donotdisturb": s.DoNotDisturb = IsOn(value); break;
                        case "logging": s.Logging = IsOn(value); break;
                        case "dndrestore": s.DndRestore = value; break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("could not read settings: {0}", ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string[] lines =
                {
                    "# CleanSlate settings (everything here can also be changed from the tray icon's menu)",
                    "",
                    "# What a left click on the tray icon does: close-gray | close-only | gray-only",
                    "ClickAction=" + ClickAction,
                    "",
                    "# The screen that stays visible:",
                    "#   primary  the primary display",
                    "#   active   the display with the active window",
                    "#   display  the monitor below (found by its id, or else by its name)",
                    "FocusScreen=" + FocusScreen,
                    "FocusDisplayName=" + FocusDisplayName,
                    "FocusDisplayId=" + FocusDisplayId,
                    "",
                    "# Color of the gray screens, as #RRGGBB",
                    "GrayColor=" + GrayColor,
                    "",
                    "# Turn on Windows Do not disturb while the gray is on: on | off",
                    "DoNotDisturb=" + (DoNotDisturb ? "on" : "off"),
                    "",
                    "# Keep a log of what was closed, moved or skipped (%LOCALAPPDATA%\\CleanSlate\\log.txt): on | off",
                    "Logging=" + (Logging ? "on" : "off"),
                    "",
                    "# Internal: the Do not disturb setting to put back while CleanSlate has it on.",
                    "DndRestore=" + DndRestore,
                    ""
                };
                File.WriteAllText(FilePath, string.Join(Environment.NewLine, lines));
            }
            catch (Exception ex)
            {
                Log.Write("could not save settings: {0}", ex.Message);
            }
        }

        static bool IsOn(string value)
        {
            return value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeAction(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            return v == CloseGray || v == CloseOnly || v == GrayOnly ? v : null;
        }

        static string NormalizeFocus(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            return v == Primary || v == Active || v == Display ? v : null;
        }

        /// <summary>#RRGGBB to 0xRRGGBB, or -1 if invalid.</summary>
        public static int ParseColor(string value)
        {
            string hex = (value ?? "").Trim().TrimStart('#');
            int rgb;
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return rgb;
            return -1;
        }

        /// <summary>GrayColor as a Win32 COLORREF (0x00BBGGRR).</summary>
        public int GrayColorRef
        {
            get
            {
                int rgb = ParseColor(GrayColor);
                if (rgb < 0) rgb = 0x808080;
                int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
                return r | (g << 8) | (b << 16);
            }
        }

        public static string ActionText(string action)
        {
            if (action == CloseOnly) return "Close all windows only";
            if (action == GrayOnly) return "Gray other screens only";
            return "Close all windows + gray other screens";
        }
    }
}
