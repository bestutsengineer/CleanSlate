using System;
using System.Collections.Generic;
using System.Globalization;

namespace CleanSlate
{
    internal enum CommandKind
    {
        Start,        // no arguments: just run in the tray
        Click,        // --click: the left-click action
        CloseGray,    // --close-gray [screen]
        CloseOnly,    // --close-only
        GrayOnly,     // --gray-only [screen]
        Remove,       // --remove
        Exit,         // --exit
        List,         // --list
        Help          // --help
    }

    internal sealed class Command
    {
        public CommandKind Kind = CommandKind.Start;
        public string Screen;     // focus screen override; null = the setting
        public bool DryRun;       // log instead of closing / moving windows
        public uint OnlyPid;      // only touch windows of this process (testing aid)
        public bool Remote;       // came from a second CleanSlate.exe

        public const string Usage =
            "CleanSlate lives in the notification area (right end of the taskbar).\n" +
            "Left-click its icon for your chosen action, right-click it for everything else.\n\n" +
            "CleanSlate.exe                       start it in the tray\n" +
            "CleanSlate.exe --click               do the left-click action (removes the gray if it is on)\n" +
            "CleanSlate.exe --close-gray [screen] close all windows, gray every screen except [screen]\n" +
            "CleanSlate.exe --close-only          close all windows\n" +
            "CleanSlate.exe --gray-only [screen]  gray every screen except [screen]\n" +
            "CleanSlate.exe --remove              remove the gray\n" +
            "CleanSlate.exe --exit                quit CleanSlate\n" +
            "CleanSlate.exe --list                print the windows that would be closed\n\n" +
            "[screen]: primary | active (display of the active window) | a display name, e.g. \"DELL U2720Q\"\n" +
            "          (part of the name is enough) | 1, 2, ... (left to right). See --list for the names.\n" +
            "Without it, the focus screen chosen in the menu is used.\n\n" +
            "Options:\n" +
            "  --dry-run       show the gray but only log what would be closed\n" +
            "  --only-pid N    only close/move windows of process N (for testing)\n";

        public static Command Parse(string[] args)
        {
            Command c = new Command();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].Trim();
                if (arg.Length == 0) continue;
                switch (arg.ToLowerInvariant())
                {
                    case "--click": c.Kind = CommandKind.Click; break;
                    case "--close-gray": c.Kind = CommandKind.CloseGray; c.Screen = OptionalScreen(args, ref i); break;
                    case "--close-only": c.Kind = CommandKind.CloseOnly; break;
                    case "--gray-only": c.Kind = CommandKind.GrayOnly; c.Screen = OptionalScreen(args, ref i); break;
                    case "--remove": c.Kind = CommandKind.Remove; break;
                    case "--exit": c.Kind = CommandKind.Exit; break;
                    case "--list": c.Kind = CommandKind.List; break;
                    case "--dry-run": c.DryRun = true; break;
                    case "--remote": c.Remote = true; break;
                    case "--only-pid":
                    {
                        string v = i + 1 < args.Length ? args[++i].Trim() : "";
                        if (!uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out c.OnlyPid) || c.OnlyPid == 0)
                            throw new ArgumentException("--only-pid needs a process id");
                        break;
                    }
                    case "--help":
                    case "-h":
                    case "/?":
                        c.Kind = CommandKind.Help;
                        break;
                    default:
                        throw new ArgumentException("Unknown option: " + arg);
                }
            }
            return c;
        }

        static string OptionalScreen(string[] args, ref int i)
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) return null;
            string value = args[++i].Trim();
            return value.Length > 0 ? value : null;
        }

        /// <summary>Command line that reproduces this command in the running instance.</summary>
        public string[] ToArgs()
        {
            List<string> a = new List<string>();
            switch (Kind)
            {
                case CommandKind.Click: a.Add("--click"); break;
                case CommandKind.CloseGray: a.Add("--close-gray"); break;
                case CommandKind.CloseOnly: a.Add("--close-only"); break;
                case CommandKind.GrayOnly: a.Add("--gray-only"); break;
                case CommandKind.Remove: a.Add("--remove"); break;
                case CommandKind.Exit: a.Add("--exit"); break;
            }
            if (Screen != null && (Kind == CommandKind.CloseGray || Kind == CommandKind.GrayOnly)) a.Add(Screen);
            if (DryRun) a.Add("--dry-run");
            if (OnlyPid != 0) { a.Add("--only-pid"); a.Add(OnlyPid.ToString(CultureInfo.InvariantCulture)); }
            a.Add("--remote");
            return a.ToArray();
        }

        public override string ToString()
        {
            string s = Kind.ToString();
            if (Screen != null) s += " screen " + Screen;
            if (DryRun) s += " (dry run)";
            if (OnlyPid != 0) s += " (only pid " + OnlyPid + ")";
            if (Remote) s += " (from another instance)";
            return s;
        }
    }
}
