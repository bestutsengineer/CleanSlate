using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CleanSlate
{
    /// <summary>
    /// The resident app: tray icon and menu, closing windows, showing/hiding the gray screens,
    /// keeping the gray on top, and moving windows that end up on a gray screen to the focus screen
    /// (for example a "save changes?" prompt from an app that was asked to close).
    /// </summary>
    internal sealed class App : IDisposable
    {
        const int TickMs = 500;
        // A window we asked to close gets this long to go away before it is treated as a straggler.
        const int CloseGraceMs = 3000;
        // Moves are asynchronous: give one time to land before looking at that window again.
        const int MoveCooldownMs = 1500;
        // Stop fighting a window that keeps moving itself back.
        const int MaxMovesPerWindow = 5;

        // "Keep it off": how often to pause an automatic Do not disturb rule, and when to give up.
        const int DismissIntervalMs = 10000;
        const int MaxDismissAttempts = 3;

        // Menu ids
        const int IdCloseExceptCurrent = 5, IdCloseGray = 1, IdCloseOnly = 2, IdGrayOnly = 3, IdRemove = 4;
        const int IdFocusPrimary = 100, IdFocusActive = 101, IdFocusMissing = 102, IdFocusDisplay = 110;   // 110 + screen index
        const int IdClickCloseExceptCurrent = 204, IdClickCloseGray = 201, IdClickCloseOnly = 202, IdClickGrayOnly = 203;
        const int IdShade = 301, IdCustomColor = 350;                // 301 + shade index
        const int IdStartup = 401, IdOpenLog = 402, IdLogging = 404, IdExit = 500;
        const int IdDndOn = 451, IdDndKeepOff = 452, IdDndLeave = 453;

        sealed class MoveRecord
        {
            public int Count;
            public DateTime Last;
        }

        readonly Queue<Action> _queue = new Queue<Action>();
        readonly List<OverlayWindow> _overlays = new List<OverlayWindow>();
        readonly Dictionary<IntPtr, DateTime> _closing = new Dictionary<IntPtr, DateTime>();
        readonly Dictionary<IntPtr, MoveRecord> _moves = new Dictionary<IntPtr, MoveRecord>();
        // Windows that were already on a gray screen when "gray only" started: left alone under the gray
        // until you switch to one of them.
        readonly HashSet<IntPtr> _parked = new HashSet<IntPtr>();

        readonly HostWindow _host;
        readonly TrayIcon _tray;
        readonly Native.WinEventProc _hookProc;   // kept in a field so the GC cannot collect the callback
        readonly IntPtr _hook;
        System.Windows.Forms.Timer _timer;
        IntPtr _brush;
        IntPtr _lastActive;

        string _focusDevice;
        string _layoutStamp;
        string _pendingStamp;
        bool _dryRun;
        uint _onlyPid;
        bool _accepting = true;
        bool _menuOpen;
        bool _busy;
        DateTime _lastDismiss = DateTime.MinValue;
        int _dismissAttempts;

        public App()
        {
            _host = new HostWindow(this);
            _tray = new TrayIcon(this);
            _hookProc = OnForegroundChanged;
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            RememberActive(Native.GetForegroundWindow());
            // If CleanSlate was killed while it had Do not disturb on, put it back now.
            RestoreDoNotDisturb();
            UpdateTray();
        }

        public bool GrayActive { get { return _overlays.Count > 0; } }

        /// <summary>False once shutting down, so other instances stop handing us commands.</summary>
        public bool Accepting { get { return _accepting; } }

        public IntPtr MenuOwner { get { return _host.Handle; } }

        public IntPtr GrayBrush
        {
            get
            {
                if (_brush == IntPtr.Zero) _brush = Native.CreateSolidBrush(Settings.Load().GrayColorRef);
                return _brush;
            }
        }

        // ---------------------------------------------------------------- commands

        public void Execute(Command c)
        {
            Log.Write("command: {0}", c);
            Settings s = Settings.Load();
            switch (c.Kind)
            {
                case CommandKind.Start:
                    if (c.Remote) _tray.Notify("CleanSlate is already running. Its icon is at the right end of the taskbar.");
                    break;
                case CommandKind.Click:
                    DoClickAction(c);
                    break;
                case CommandKind.CloseExceptCurrent:
                    CloseAndGray(c, true);
                    break;
                case CommandKind.CloseGray:
                    CloseAndGray(c, false);
                    break;
                case CommandKind.CloseOnly:
                    CloseWindows(c);
                    break;
                case CommandKind.GrayOnly:
                    GrayOnly(c);
                    break;
                case CommandKind.Remove:
                    RemoveGray();
                    break;
                case CommandKind.Exit:
                    Quit();
                    return;
                default:
                    Log.Write("ignored here: {0}", c.Kind);
                    break;
            }
            UpdateTray();
        }

        public void ExecuteRemote(string[] args)
        {
            try
            {
                Execute(Command.Parse(args));
            }
            catch (ArgumentException ex)
            {
                Log.Write("bad command from another instance: {0}", ex.Message);
            }
        }

        /// <summary>Left click on the tray icon: removes the gray if it is on, otherwise does the chosen action.</summary>
        public void OnTrayClick()
        {
            Log.Write("tray icon clicked");
            Execute(new Command { Kind = CommandKind.Click });
        }

        void DoClickAction(Command c)
        {
            if (GrayActive)
            {
                RemoveGray();
                return;
            }
            Settings s = Settings.Load();
            if (s.ClickAction == Settings.CloseOnly) CloseWindows(c);
            else if (s.ClickAction == Settings.GrayOnly) GrayOnly(c);
            else CloseAndGray(c, s.ClickAction == Settings.CloseExceptCurrent);
        }

        /// <summary>
        /// The window you were working in before clicking the tray icon (the taskbar takes focus on the click),
        /// or IntPtr.Zero if there is none any more.
        /// </summary>
        IntPtr CurrentWindow()
        {
            return _lastActive != IntPtr.Zero && Native.IsWindow(_lastActive) && WindowOps.IsTaskbarWindow(_lastActive) ? _lastActive : IntPtr.Zero;
        }

        /// <summary>The screen given on the command line, or else the focus screen chosen in the menu.</summary>
        ScreenInfo ResolveFocus(Command c, List<ScreenInfo> screens)
        {
            if (c.Screen != null) return Screens.Resolve(c.Screen, null, null, screens, _lastActive);
            Settings s = Settings.Load();
            return Screens.Resolve(s.FocusScreen, s.FocusDisplayId, s.FocusDisplayName, screens, _lastActive);
        }

        /// <param name="keepCurrent">Leave the window you were working in open. If it is on a gray screen,
        /// it is moved to the focus screen like any other window there.</param>
        void CloseAndGray(Command c, bool keepCurrent)
        {
            List<ScreenInfo> screens = Screens.GetAll();
            ScreenInfo focus = ResolveFocus(c, screens);
            // Snapshot the windows first so our own new windows can never be part of it.
            List<IntPtr> windows = WindowOps.TaskbarWindows(c.OnlyPid);
            if (keepCurrent)
            {
                IntPtr current = CurrentWindow();
                if (current != IntPtr.Zero && windows.Remove(current)) Log.Write("keeping the current window: {0}", WindowOps.Describe(current));
                else if (current == IntPtr.Zero) Log.Write("no current window found; closing all windows");
            }
            if (screens.Count > 1)
            {
                _parked.Clear();
                ShowGray(screens, focus, c.DryRun, c.OnlyPid);
            }
            else
            {
                Log.Write("only one screen connected: closing windows without gray");
            }
            Close(windows, c.DryRun);
        }

        void CloseWindows(Command c)
        {
            Close(WindowOps.TaskbarWindows(c.OnlyPid), c.DryRun);
        }

        void Close(List<IntPtr> windows, bool dryRun)
        {
            // A parked window we now ask to close may come back as a straggler (e.g. "save changes?").
            foreach (IntPtr h in windows) _parked.Remove(h);
            WindowOps.CloseAll(windows, dryRun, _closing);
        }

        void GrayOnly(Command c)
        {
            List<ScreenInfo> screens = Screens.GetAll();
            if (screens.Count < 2)
            {
                _tray.Notify("Only one screen is connected, so there is nothing to gray out.");
                return;
            }
            ScreenInfo focus = ResolveFocus(c, screens);
            ShowGray(screens, focus, c.DryRun, c.OnlyPid);
            ParkWindowsOnGray(screens);
        }

        // ---------------------------------------------------------------- gray screens

        void ShowGray(List<ScreenInfo> screens, ScreenInfo focus, bool dryRun, uint onlyPid)
        {
            bool turningOn = !GrayActive;
            _dryRun = dryRun;
            _onlyPid = onlyPid;
            _focusDevice = focus.Device;

            // Drop overlays for screens that are gone or are now the focus screen; refit the rest.
            for (int i = _overlays.Count - 1; i >= 0; i--)
            {
                OverlayWindow o = _overlays[i];
                ScreenInfo s = screens.Find(delegate(ScreenInfo x) { return x.Device == o.Screen.Device; });
                if (s == null || s.Device == focus.Device)
                {
                    o.Close();
                    _overlays.RemoveAt(i);
                }
                else
                {
                    o.MoveTo(s);
                }
            }

            List<string> grayed = new List<string>();
            foreach (ScreenInfo s in screens)
            {
                if (s.Device == focus.Device) continue;
                grayed.Add(s.Label);
                string device = s.Device;
                if (!_overlays.Exists(delegate(OverlayWindow o) { return o.Screen.Device == device; }))
                    _overlays.Add(new OverlayWindow(this, s));
            }

            _layoutStamp = Screens.Stamp(screens);
            _pendingStamp = null;
            StartTimer();
            Log.Write("gray on {0}; focus on {1}", string.Join(", ", grayed.ToArray()), focus.Label);
            if (turningOn)
            {
                _dismissAttempts = 0;
                _lastDismiss = DateTime.MinValue;
                ApplyDoNotDisturb(true);
            }
        }

        void ParkWindowsOnGray(List<ScreenInfo> screens)
        {
            _parked.Clear();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (WindowOps.IsOthersWindow(h, _onlyPid) && WindowOps.IsMovable(h) && OnGrayScreen(h, screens) != null) _parked.Add(h);
                return true;
            }, IntPtr.Zero);
            if (_parked.Count > 0) Log.Write("{0} window(s) left under the gray", _parked.Count);
        }

        void RemoveGray()
        {
            if (_overlays.Count == 0) return;
            foreach (OverlayWindow o in _overlays) o.Close();
            _overlays.Clear();
            StopTimer();
            _closing.Clear();
            _moves.Clear();
            _parked.Clear();
            Log.Write("gray off");
            RestoreDoNotDisturb();
            UpdateTray();
        }

        // ---------------------------------------------------------------- do not disturb

        /// <summary>
        /// Applies the Do not disturb choice while the gray is on. Runs when the gray goes on and on every tick,
        /// so "keep it off" also catches a rule that kicks in later (for example a video going full screen).
        /// </summary>
        void ApplyDoNotDisturb(bool grayJustOn)
        {
            if (!GrayActive || !DoNotDisturb.Available) return;
            Settings s = Settings.Load();
            if (s.DoNotDisturb == Settings.DndLeave) return;
            if (s.DoNotDisturb == Settings.DndTurnOn && !grayJustOn) return;
            bool inEffect = DoNotDisturb.IsInEffect();
            if (s.DoNotDisturb == Settings.DndKeepOff && !inEffect) return;   // the common case: nothing muted
            string selected = DoNotDisturb.GetSelected();
            switch (DoNotDisturb.Decide(s.DoNotDisturb, grayJustOn, inEffect, selected, s.DndRestore.Length > 0))
            {
                case DoNotDisturb.Step.TurnOn:
                    SwitchDoNotDisturbOn(selected);
                    break;
                case DoNotDisturb.Step.Dismiss:
                    PauseAutomaticRule();
                    break;
                default:
                    if (grayJustOn && inEffect && s.DoNotDisturb == Settings.DndTurnOn) Log.Write("do not disturb is already on; leaving it as it is");
                    break;
            }
        }

        /// <summary>"Turn it on": switches Do not disturb on and remembers what to put back afterwards.</summary>
        void SwitchDoNotDisturbOn(string previous)
        {
            Settings s = Settings.Load();
            // Save first, so a crash right after still leaves a note to undo it on the next start.
            s.DndRestore = previous;
            s.Save();
            if (DoNotDisturb.SetSelected(DoNotDisturb.OnProfile))
            {
                Log.Write("do not disturb on (was {0})", previous);
            }
            else
            {
                s.DndRestore = "";
                s.Save();
            }
        }

        /// <summary>
        /// "Keep it off": an automatic rule (full-screen app, game, schedule...) muted notifications while the
        /// gray is on. Switch Do not disturb on and, a moment later, off again: that is what switching it off in
        /// the notification center does, and Windows then pauses the rule until its condition ends (you leave
        /// full screen, the game closes) or its next scheduled start. No Windows setting is changed.
        /// </summary>
        void PauseAutomaticRule()
        {
            if (_dismissAttempts >= MaxDismissAttempts || (DateTime.UtcNow - _lastDismiss).TotalMilliseconds < DismissIntervalMs) return;
            _dismissAttempts++;
            _lastDismiss = DateTime.UtcNow;
            string reason = DoNotDisturb.FullScreenAppOpen() ? "a full-screen app" : "an automatic rule";

            // The "off" step reuses the restore note, so it also happens after a crash (on the next start),
            // when the gray goes off, or when CleanSlate exits.
            Settings s = Settings.Load();
            s.DndRestore = DoNotDisturb.OffProfile;
            s.Save();
            if (!DoNotDisturb.SetSelected(DoNotDisturb.OnProfile))
            {
                s.DndRestore = "";
                s.Save();
                return;
            }
            int attempt = _dismissAttempts;
            System.Windows.Forms.Timer second = new System.Windows.Forms.Timer();
            second.Interval = 1000;
            second.Tick += delegate
            {
                second.Stop();
                second.Dispose();
                RestoreDoNotDisturb();
                Log.Write("do not disturb kept off: paused {0} (attempt {1} of {2})", reason, attempt, MaxDismissAttempts);
                if (attempt >= MaxDismissAttempts) Log.Write("do not disturb: giving up for this gray session if Windows turns it back on again");
            };
            second.Start();
        }

        /// <summary>Puts Do not disturb back, if CleanSlate turned it on and you have not changed it since.</summary>
        public void RestoreDoNotDisturb()
        {
            Settings s = Settings.Load();
            if (s.DndRestore.Length == 0) return;
            string current = DoNotDisturb.GetSelected();
            if (current == DoNotDisturb.OnProfile)
            {
                if (DoNotDisturb.SetSelected(s.DndRestore)) Log.Write("do not disturb restored to {0}", s.DndRestore);
            }
            else
            {
                Log.Write("do not disturb was changed meanwhile ({0}); leaving it as it is", current ?? "unknown");
            }
            s.DndRestore = "";
            s.Save();
        }

        void SetDoNotDisturbMode(string mode)
        {
            Settings s = Settings.Load();
            s.DoNotDisturb = mode;
            s.Save();
            Log.Write("do not disturb while gray is on: {0}", mode);
            if (!GrayActive) return;
            // Takes effect right away while the gray is on.
            if (mode != Settings.DndTurnOn) RestoreDoNotDisturb();
            _dismissAttempts = 0;
            _lastDismiss = DateTime.MinValue;
            ApplyDoNotDisturb(mode == Settings.DndTurnOn);
        }

        public void RequestRemoveGray(string reason)
        {
            Log.Write("removing gray: {0}", reason);
            Post(RemoveGray);
        }

        /// <summary>
        /// The menu text for the Do not disturb option. When notifications are muted but not by CleanSlate,
        /// it says why, so it is clear the option itself is not misbehaving.
        /// </summary>
        static string DoNotDisturbText(Settings s)
        {
            const string text = "Do not disturb while gray is on";
            if (!DoNotDisturb.Available) return text + " (not available)";
            if (s.DndRestore.Length > 0 || !DoNotDisturb.IsInEffect()) return text;
            return DoNotDisturb.FullScreenAppOpen()
                ? text + " (on now: a full-screen app is open)"
                : text + " (on now: set in Windows)";
        }

        static void ToggleLogging()
        {
            Settings s = Settings.Load();
            if (s.Logging) Log.Write("logging turned off");
            s.Logging = !s.Logging;
            s.Save();
            Log.Enabled = s.Logging;
            if (s.Logging) Log.Write("logging turned on");
        }

        void SetGrayColor(string hex)
        {
            Settings s = Settings.Load();
            s.GrayColor = hex;
            s.Save();
            IntPtr old = _brush;
            _brush = Native.CreateSolidBrush(s.GrayColorRef);
            foreach (OverlayWindow o in _overlays) o.Repaint();
            if (old != IntPtr.Zero) Native.DeleteObject(old);
            Log.Write("gray color: {0}", hex);
        }

        // ---------------------------------------------------------------- watching

        void StartTimer()
        {
            if (_timer != null) return;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = TickMs;
            _timer.Tick += delegate { Watch(true); };
            _timer.Start();
        }

        void StopTimer()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Dispose();
            _timer = null;
        }

        void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            RememberActive(hwnd);
            // The taskbar may have jumped above the gray, or a window on a gray screen was brought up.
            // React now instead of waiting for the next tick.
            Watch(false);
        }

        /// <summary>Remembers the last real app window that was active, for the "screen of the active window" option.</summary>
        void RememberActive(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
            if (root != IntPtr.Zero && WindowOps.IsTaskbarWindow(root) && WindowOps.IsOthersWindow(root, 0)) _lastActive = root;
        }

        void Watch(bool checkLayout)
        {
            if (!GrayActive || _busy || _menuOpen) return;
            _busy = true;
            try
            {
                List<ScreenInfo> screens = Screens.GetAll();
                string stamp = Screens.Stamp(screens);
                if (checkLayout && stamp != _layoutStamp)
                {
                    // Screens changed; wait until the new layout holds for two ticks before rebuilding.
                    if (stamp == _pendingStamp) Relayout(screens);
                    else _pendingStamp = stamp;
                }
                foreach (OverlayWindow o in _overlays) o.Place();
                if (!_dryRun) MoveStragglers(screens);
                if (checkLayout)
                {
                    Prune();
                    ApplyDoNotDisturb(false);
                }
            }
            catch (Exception ex)
            {
                Log.Write("watch error: {0}", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        void Relayout(List<ScreenInfo> screens)
        {
            if (screens.Count < 2)
            {
                Log.Write("only one screen left: removing gray");
                RemoveGray();
                return;
            }
            ScreenInfo focus = screens.Find(delegate(ScreenInfo s) { return s.Device == _focusDevice; }) ?? Screens.Primary(screens);
            Log.Write("screen layout changed, refitting the gray");
            ShowGray(screens, focus, _dryRun, _onlyPid);
        }

        ScreenInfo OnGrayScreen(IntPtr h, List<ScreenInfo> screens)
        {
            IntPtr monitor = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONULL);
            ScreenInfo s = screens.Find(delegate(ScreenInfo x) { return x.Handle == monitor; });
            return s != null && s.Device != _focusDevice ? s : null;
        }

        /// <summary>Moves windows sitting on a gray screen to the focus screen so nothing is hidden under the gray.</summary>
        void MoveStragglers(List<ScreenInfo> screens)
        {
            ScreenInfo focus = screens.Find(delegate(ScreenInfo s) { return s.Device == _focusDevice; });
            if (focus == null) return;
            DateTime now = DateTime.UtcNow;
            IntPtr foreground = Native.GetForegroundWindow();
            List<KeyValuePair<IntPtr, ScreenInfo>> todo = new List<KeyValuePair<IntPtr, ScreenInfo>>();

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!WindowOps.IsOthersWindow(h, _onlyPid) || !WindowOps.IsMovable(h)) return true;
                ScreenInfo from = OnGrayScreen(h, screens);
                if (from == null) return true;
                if (_parked.Contains(h) && h != foreground) return true;
                DateTime asked;
                if (_closing.TryGetValue(h, out asked) && (now - asked).TotalMilliseconds < CloseGraceMs) return true;
                MoveRecord moved;
                if (_moves.TryGetValue(h, out moved) &&
                    (moved.Count >= MaxMovesPerWindow || (now - moved.Last).TotalMilliseconds < MoveCooldownMs)) return true;
                if (WindowOps.IsBeingDragged(h)) return true;
                todo.Add(new KeyValuePair<IntPtr, ScreenInfo>(h, from));
                return true;
            }, IntPtr.Zero);

            if (todo.Count == 0) return;
            RECT primaryWork = Screens.Primary(screens).Work;
            foreach (KeyValuePair<IntPtr, ScreenInfo> item in todo)
            {
                MoveRecord record;
                if (!_moves.TryGetValue(item.Key, out record))
                {
                    record = new MoveRecord();
                    _moves[item.Key] = record;
                }
                record.Count++;
                record.Last = DateTime.UtcNow;
                _parked.Remove(item.Key);
                Log.Write("moving off the gray to {0}: {1}", focus.Label, WindowOps.Describe(item.Key));
                WindowOps.MoveToScreen(item.Key, item.Value, focus, primaryWork);
            }
        }

        void Prune()
        {
            List<IntPtr> dead = new List<IntPtr>();
            foreach (IntPtr h in _closing.Keys) if (!Native.IsWindow(h)) dead.Add(h);
            foreach (IntPtr h in _moves.Keys) if (!Native.IsWindow(h)) dead.Add(h);
            foreach (IntPtr h in _parked) if (!Native.IsWindow(h)) dead.Add(h);
            foreach (IntPtr h in dead)
            {
                _closing.Remove(h);
                _moves.Remove(h);
                _parked.Remove(h);
            }
        }

        // ---------------------------------------------------------------- menu (tray icon and gray screens)

        public void ShowMenu(IntPtr owner)
        {
            if (_menuOpen) return;
            Settings s = Settings.Load();
            List<ScreenInfo> screens = Screens.GetAll();
            bool multi = screens.Count > 1;
            POINT at;
            Native.GetCursorPos(out at);
            int id;

            _menuOpen = true;   // the gray must not be re-raised above the menu while it is open
            try
            {
                using (PopupMenu menu = new PopupMenu())
                {
                    // Actions. The bold one is what a left click on the icon does.
                    // The window that stays open is shown on the right, like a shortcut hint.
                    IntPtr current = CurrentWindow();
                    string keeps = current != IntPtr.Zero ? "\t" + WindowOps.AppName(current).Replace("&", "&&") : "";
                    menu.Item(IdCloseExceptCurrent, Settings.ActionText(Settings.CloseExceptCurrent) + keeps, true, !GrayActive && s.ClickAction == Settings.CloseExceptCurrent);
                    menu.Item(IdCloseGray, Settings.ActionText(Settings.CloseGray), multi, !GrayActive && s.ClickAction == Settings.CloseGray);
                    menu.Item(IdCloseOnly, Settings.ActionText(Settings.CloseOnly), true, !GrayActive && s.ClickAction == Settings.CloseOnly);
                    menu.Item(IdGrayOnly, Settings.ActionText(Settings.GrayOnly), multi, !GrayActive && s.ClickAction == Settings.GrayOnly);
                    menu.Item(IdRemove, "Remove gray", GrayActive, GrayActive);
                    menu.Separator();

                    PopupMenu focus = menu.Submenu("Focus screen (stays visible)", multi);
                    focus.Item(IdFocusPrimary, "Primary display", true, false, s.FocusScreen == Settings.Primary, true);
                    focus.Item(IdFocusActive, "Display with the active window", true, false, s.FocusScreen == Settings.Active, true);
                    focus.Separator();
                    ScreenInfo saved = s.FocusScreen == Settings.Display ? Screens.FindDisplay(screens, s.FocusDisplayId, s.FocusDisplayName) : null;
                    for (int i = 0; i < screens.Count; i++)
                        focus.Item(IdFocusDisplay + i, screens[i].Label, true, false, saved == screens[i], true);
                    if (s.FocusScreen == Settings.Display && saved == null)
                        focus.Item(IdFocusMissing, s.FocusDisplayName + " (not connected: using the primary display)", false, false, true, true);

                    PopupMenu click = menu.Submenu("Left-click action");
                    click.Item(IdClickCloseExceptCurrent, Settings.ActionText(Settings.CloseExceptCurrent), true, false, s.ClickAction == Settings.CloseExceptCurrent, true);
                    click.Item(IdClickCloseGray, Settings.ActionText(Settings.CloseGray), true, false, s.ClickAction == Settings.CloseGray, true);
                    click.Item(IdClickCloseOnly, Settings.ActionText(Settings.CloseOnly), true, false, s.ClickAction == Settings.CloseOnly, true);
                    click.Item(IdClickGrayOnly, Settings.ActionText(Settings.GrayOnly), true, false, s.ClickAction == Settings.GrayOnly, true);

                    PopupMenu color = menu.Submenu("Gray color");
                    bool preset = false;
                    for (int i = 0; i < Settings.Shades.GetLength(0); i++)
                    {
                        bool chosen = string.Equals(s.GrayColor, Settings.Shades[i, 1], StringComparison.OrdinalIgnoreCase);
                        preset |= chosen;
                        color.Item(IdShade + i, Settings.Shades[i, 0], true, false, chosen, true);
                    }
                    color.Item(IdCustomColor, preset ? "Custom..." : "Custom (" + s.GrayColor + ")...", true, false, !preset, true);

                    PopupMenu dnd = menu.Submenu(DoNotDisturbText(s), DoNotDisturb.Available);
                    dnd.Item(IdDndOn, "Turn it on", true, false, s.DoNotDisturb == Settings.DndTurnOn, true);
                    dnd.Item(IdDndKeepOff, "Keep it off (pause Windows' automatic rules)", true, false, s.DoNotDisturb == Settings.DndKeepOff, true);
                    dnd.Item(IdDndLeave, "Leave it to Windows", true, false, s.DoNotDisturb == Settings.DndLeave, true);

                    menu.Separator();
                    menu.Item(IdStartup, "Start with Windows", true, false, AutoStart.IsEnabled);
                    menu.Item(IdLogging, "Keep a log", true, false, s.Logging);
                    menu.Item(IdOpenLog, "Open log", System.IO.File.Exists(Log.FilePath));
                    menu.Separator();
                    menu.Item(IdExit, "Exit CleanSlate");

                    id = menu.Show(owner, at);
                }
            }
            finally
            {
                _menuOpen = false;
            }
            if (id != 0) Post(delegate { OnMenu(id, screens); });
        }

        void OnMenu(int id, List<ScreenInfo> screens)
        {
            Settings s = Settings.Load();
            switch (id)
            {
                case IdCloseExceptCurrent: Execute(new Command { Kind = CommandKind.CloseExceptCurrent }); return;
                case IdCloseGray: Execute(new Command { Kind = CommandKind.CloseGray }); return;
                case IdCloseOnly: Execute(new Command { Kind = CommandKind.CloseOnly }); return;
                case IdGrayOnly: Execute(new Command { Kind = CommandKind.GrayOnly }); return;
                case IdRemove: Execute(new Command { Kind = CommandKind.Remove }); return;
                case IdFocusPrimary: SetFocusScreen(Settings.Primary, null); return;
                case IdFocusActive: SetFocusScreen(Settings.Active, null); return;
                case IdDndOn: SetDoNotDisturbMode(Settings.DndTurnOn); UpdateTray(); return;
                case IdDndKeepOff: SetDoNotDisturbMode(Settings.DndKeepOff); UpdateTray(); return;
                case IdDndLeave: SetDoNotDisturbMode(Settings.DndLeave); UpdateTray(); return;
                case IdClickCloseExceptCurrent: s.ClickAction = Settings.CloseExceptCurrent; break;
                case IdClickCloseGray: s.ClickAction = Settings.CloseGray; break;
                case IdClickCloseOnly: s.ClickAction = Settings.CloseOnly; break;
                case IdClickGrayOnly: s.ClickAction = Settings.GrayOnly; break;
                case IdCustomColor: PickCustomColor(s.GrayColor); return;
                case IdStartup: AutoStart.Set(!AutoStart.IsEnabled); return;
                case IdOpenLog: OpenLog(); return;
                case IdLogging: ToggleLogging(); return;
                case IdExit: Quit(); return;
                default:
                    if (id >= IdFocusDisplay && id < IdFocusDisplay + screens.Count)
                    {
                        SetFocusScreen(Settings.Display, screens[id - IdFocusDisplay]);
                    }
                    else if (id >= IdShade && id < IdShade + Settings.Shades.GetLength(0))
                    {
                        SetGrayColor(Settings.Shades[id - IdShade, 1]);
                    }
                    return;
            }
            s.Save();
            Log.Write("left-click action: {0}", s.ClickAction);
            UpdateTray();
        }

        /// <summary>Saves the focus screen; a specific display is remembered by its stable id and its name.</summary>
        void SetFocusScreen(string kind, ScreenInfo display)
        {
            Settings s = Settings.Load();
            s.FocusScreen = kind;
            if (display != null)
            {
                s.FocusDisplayId = display.Id;
                s.FocusDisplayName = display.Name;
            }
            s.Save();
            Log.Write("focus screen: {0}", display != null ? display.Label : kind);
            // If the gray is on, move it right away (without closing anything).
            if (GrayActive) GrayOnly(new Command { Kind = CommandKind.GrayOnly, DryRun = _dryRun, OnlyPid = _onlyPid });
            UpdateTray();
        }

        void PickCustomColor(string current)
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                int rgb = Settings.ParseColor(current);
                if (rgb >= 0) dialog.Color = Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                dialog.FullOpen = true;
                if (dialog.ShowDialog() == DialogResult.OK)
                    SetGrayColor(string.Format("#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B));
            }
        }

        static void OpenLog()
        {
            try
            {
                Process.Start(Log.FilePath);
            }
            catch (Exception ex)
            {
                Log.Write("could not open the log: {0}", ex.Message);
            }
        }

        void UpdateTray()
        {
            Settings s = Settings.Load();
            string tip = !GrayActive ? "CleanSlate: click to " + Settings.ActionShort(s.ClickAction)
                : s.DndRestore.Length > 0 ? "CleanSlate: gray and do not disturb are on. Click to undo."
                : "CleanSlate: gray is on. Click to remove it.";
            _tray.Update(GrayActive, tip);
        }

        // ---------------------------------------------------------------- lifetime

        /// <summary>Runs an action on the UI thread after the current message is handled.</summary>
        public void Post(Action action)
        {
            _queue.Enqueue(action);
            Native.PostMessage(_host.Handle, HostWindow.WM_RUN_QUEUE, IntPtr.Zero, IntPtr.Zero);
        }

        public void RunQueue()
        {
            while (_queue.Count > 0)
            {
                Action action = _queue.Dequeue();
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Write("error: {0}", ex);
                }
            }
        }

        public void Run()
        {
            Log.Write("running in the tray");
            Application.Run();
        }

        void Quit()
        {
            Log.Write("exit requested");
            _accepting = false;
            RemoveGray();
            Application.ExitThread();
        }

        public void Dispose()
        {
            _accepting = false;
            RemoveGray();
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
            _tray.Dispose();
            _host.Close();
            if (_brush != IntPtr.Zero)
            {
                Native.DeleteObject(_brush);
                _brush = IntPtr.Zero;
            }
        }
    }
}
