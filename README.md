# CleanSlate

A small tray app for Windows 10/11. One click on its icon at the right end of the taskbar:

1. **Closes every window on the taskbar, gracefully** (or every window except the one you are working in). Each window gets the same request as clicking its **X** (or pressing Alt+F4). Nothing is killed, so apps can still ask "save changes?", and apps that minimize to the tray on close still do that.
2. **Grays out your other screens.** Every screen except your focus screen is covered with a plain full-screen gray, taskbar included.

The gray is only a window. CleanSlate never changes your wallpaper, taskbar or settings, so if CleanSlate is closed, crashes or is killed, the gray simply disappears and your screens are exactly as before.

No admin rights and no extra runtime: it uses the .NET Framework 4.8 that ships with Windows.

## Download

Get `CleanSlate.exe` from the [latest release](../../releases/latest) and run it: its icon appears at the right end of the taskbar. Put the exe somewhere permanent first (for example `%LOCALAPPDATA%\Programs\CleanSlate`), then tick **Start with Windows** in its menu.

The exe is not code-signed, so Windows SmartScreen may warn the first time ("More info → Run anyway"). If you prefer, build it yourself from source as below; it takes a few seconds and needs nothing beyond Windows.

## Build and install from source

```powershell
.\build.ps1      # builds bin\CleanSlate.exe with the C# compiler built into Windows
.\install.ps1    # installs to %LOCALAPPDATA%\Programs\CleanSlate, starts it, starts it with Windows
```

`install.ps1 -NoAutoStart` skips "Start with Windows". `install.ps1 -Uninstall` removes everything except your settings and log.

If PowerShell refuses to run the scripts, run them as `powershell -ExecutionPolicy Bypass -File .\install.ps1`.

Windows hides new tray icons behind the **^** arrow. The installer marks CleanSlate's icon as shown, which takes effect the next time you sign in. To show it right away, drag it from the ^ area onto the taskbar, or turn it on in *Settings → Personalization → Taskbar → Other system tray icons*.

## Using it

| You do | CleanSlate does |
|---|---|
| **Left-click** the tray icon | Your left-click action (default: close all windows + gray other screens) |
| **Left-click** while the gray is on | Removes the gray (the icon is blue while the gray is on) |
| **Right-click** the tray icon | The menu below |
| **Right-click** a gray screen | The same menu, handy when the screen with the tray is grayed |

### The menu

- **Close all except the current window + gray other screens**: the app shown on the right of this item stays open.
- **Close all windows + gray other screens**
- **Close all windows only**
- **Gray other screens only**
- **Remove gray**

  The item in **bold** is what a left click does right now.
- **Focus screen (stays visible)** ▸
  - *Primary display*
  - *Display with the active window*
  - each connected display **by its name**, e.g. *DELL U2720Q*, *Built-in display*

  A chosen display is remembered by its hardware id (falling back to its name), so the choice survives rearranging, reconnecting and other setups. If it is not connected, the primary display is used and the menu says so. Changing the focus screen while the gray is on moves the gray immediately.
- **Left-click action** ▸ any of the four actions above.
- **Gray color** ▸ *Light gray* / *Medium gray* / *Dark gray* / *Custom…*
- **Do not disturb while gray is on** ▸ *Turn it on* / *Keep it off* / *Leave it to Windows* (see below).
- **Start with Windows**
- **Keep a log**: off by default.
- **Open log**
- **Exit CleanSlate**

Every choice is saved immediately. Identical monitors are shown as *Name #1*, *Name #2* (left to right).

### Close all except the current window

"Current" is the window you were working in just before you clicked the tray icon (clicking the tray moves focus to the taskbar, so CleanSlate remembers the last real app window). Its app name is shown on the right of the menu item. Every other window on the taskbar is asked to close. If the current window is on a screen that turns gray, it is moved to the focus screen, so you keep working in it.

### Do not disturb

| Choice | While the gray is on |
|---|---|
| **Turn it on** | Do not disturb is switched on, and switched back off when the gray goes off. If notifications are already muted (by you or an automatic rule), CleanSlate leaves them alone and does not switch anything off later. |
| **Keep it off** | Notifications stay on. If one of Windows' automatic rules mutes them (a full-screen video or game, a schedule), CleanSlate pauses that rule exactly as switching Do not disturb off from the notification center does. Windows resumes the rule by itself when its condition ends (you leave full screen, the game closes) or at its next scheduled start. If you switch Do not disturb on yourself, CleanSlate respects that. |
| **Leave it to Windows** (default) | CleanSlate does not touch Do not disturb. |

Either way, the gray itself never counts as a full-screen app: each gray window carries the shell's `NonRudeHWND` marker, so Windows' rule "When using an app in full screen mode" does not fire because of it. (Without the marker it does, even though the gray can never be the active window.)

Safety: if you change Do not disturb yourself while the gray is on, CleanSlate leaves your choice alone. If CleanSlate is killed while it has Do not disturb switched on, it switches it back the next time it starts.

When notifications are muted but not by CleanSlate, the menu says why: *(on now: a full-screen app is open)* or *(on now: set in Windows)*.

Windows has no official API for Do not disturb; CleanSlate uses the interface the Settings app uses. If a future Windows changes it, the menu item shows *(not available)* and everything else keeps working.

### Windows on a gray screen

- **Close actions:** windows that do not close (for example one asking "save changes?") get 3 seconds. If one is still there on a gray screen after that, it is moved to the focus screen so you can answer it.
- **Gray only:** windows on the grayed screens stay where they are, under the gray. The one you are working in, and any you switch to later (taskbar, Alt+Tab), move to the focus screen.
- **New windows** that open on a gray screen are moved to the focus screen.

Windows that already have a dialog open are skipped, the same as clicking a disabled X. Windows of programs running as administrator cannot be closed by a normal program; CleanSlate logs them and leaves them alone.

## Command line

The running copy in the tray receives these, so they work from shortcuts, scripts or hotkey tools:

```
CleanSlate.exe                       start it in the tray
CleanSlate.exe --click               do the left-click action (removes the gray if it is on)
CleanSlate.exe --close-except-current [screen]
                                     close all windows except the current one, gray every screen except [screen]
CleanSlate.exe --close-gray [screen] close all windows, gray every screen except [screen]
CleanSlate.exe --close-only          close all windows
CleanSlate.exe --gray-only [screen]  gray every screen except [screen]
CleanSlate.exe --remove              remove the gray
CleanSlate.exe --exit                quit CleanSlate
CleanSlate.exe --list                print the displays and the windows that would be closed (try: CleanSlate.exe --list | more)

[screen]: primary | active | a display name ("DELL", part of it is enough) | 1, 2, ... (left to right)
          default: the focus screen chosen in the menu; --list shows the display names
--dry-run       show the gray but only log what would be closed
--only-pid N    only close/move windows of process N (for testing)
```

## Settings and log

`%APPDATA%\CleanSlate\settings.ini`, all changeable from the menu:

| Key | Values |
|---|---|
| `ClickAction` | `close-except-current`, `close-gray` (default), `close-only`, `gray-only` |
| `FocusScreen` | `primary` (default), `active`, `display` (+ `FocusDisplayName`, `FocusDisplayId`) |
| `GrayColor` | `#RRGGBB` (default `#808080`) |
| `DoNotDisturb` | `on`, `keep-off`, `windows` (default; version 1.0's `off` reads as this) |
| `Logging` | `on`, `off` (default) |

`%LOCALAPPDATA%\CleanSlate\log.txt`: what was closed, moved or skipped, and why. Only written while **Keep a log** is on.

## How it works

- **Which windows:** the same rule the taskbar uses. Visible top-level windows that are unowned and not tool windows, or that ask for a taskbar button. Windows on other virtual desktops and hidden store-app frames are left alone.
- **Closing:** `WM_SYSCOMMAND / SC_CLOSE` is posted to each window, the message the X button and Alt+F4 produce.
- **Gray:** one borderless, topmost window per covered screen, sized in physical pixels (the app is per-monitor DPI aware). It is re-raised whenever the foreground window changes and every 0.5 s, so the taskbar cannot pop up over it. It can never become the active window, so clicks on it do not steal keyboard focus, and it is marked `NonRudeHWND` so the shell does not treat it as a full-screen app.
- **Current window:** CleanSlate listens for foreground changes (`SetWinEventHook`) and remembers the last real app window.
- **Menus:** native Windows menus, so they look and scale like the rest of Windows 11.
- **Display names:** read from each monitor's EDID through the Windows display configuration API (`QueryDisplayConfig` / `DisplayConfigGetDeviceInfo`); laptop panels show as *Built-in display*.
- **Screen changes:** plugging or unplugging a monitor refits the gray. With a single screen left, the gray turns off.

## Building from source

`build.ps1` compiles `src\*.cs` with `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (C# 5, WinForms). `tools\make-icon.ps1` draws `assets\CleanSlate.ico` and `assets\CleanSlate-on.ico`.

## Contributing

A [gitleaks](https://github.com/gitleaks/gitleaks) pre-commit hook blocks commits that contain secrets. Enable it once per clone:

```powershell
winget install Gitleaks.Gitleaks
git config core.hooksPath .githooks
```

(Or, with the [pre-commit](https://pre-commit.com) framework: `pre-commit install`, which uses `.pre-commit-config.yaml`.)

## License

[MIT](LICENSE)
