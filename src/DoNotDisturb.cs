using System;
using System.Runtime.InteropServices;

namespace CleanSlate
{
    /// <summary>
    /// Windows "Do not disturb". There is no public API for it, so this uses the same interfaces the
    /// Settings app uses. Every call is guarded: if Windows changes them, the feature reports itself
    /// as unavailable instead of failing.
    /// </summary>
    internal static class DoNotDisturb
    {
        /// <summary>The profile the Windows 11 "Do not disturb" switch selects.</summary>
        public const string OnProfile = "Microsoft.QuietHoursProfile.PriorityOnly";

        /// <summary>The switch in its "off" position; automatic rules can still mute notifications.</summary>
        public const string OffProfile = "Microsoft.QuietHoursProfile.Unrestricted";

        public enum Step { None, TurnOn, Dismiss }

        /// <summary>
        /// What CleanSlate should do about Do not disturb while the gray is on. Pure, so it can be tested.
        ///   mode       Settings.DndTurnOn / DndKeepOff / DndLeave
        ///   grayJustOn the gray has just been switched on (the "turn on" mode acts only then)
        ///   inEffect   notifications are muted right now, by the switch or by an automatic rule
        ///   selected   the switch's profile (null if unreadable)
        ///   ours       CleanSlate itself turned it on (and will switch it back off)
        /// </summary>
        public static Step Decide(string mode, bool grayJustOn, bool inEffect, string selected, bool ours)
        {
            if (ours || selected == null) return Step.None;
            if (mode == Settings.DndTurnOn)
                return grayJustOn && !inEffect ? Step.TurnOn : Step.None;
            if (mode == Settings.DndKeepOff)
                // Only an automatic rule (switch still off) is paused. If you switched it on yourself, it stays on.
                return inEffect && string.Equals(selected, OffProfile, StringComparison.OrdinalIgnoreCase) ? Step.Dismiss : Step.None;
            return Step.None;
        }

        static readonly Guid ClsidQuietHoursSettings = new Guid("f53321fa-34f8-4b7f-b9a3-361877cb94cf");

        [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IQuietHoursSettings
        {
            [PreserveSig] int get_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string profileId);
            [PreserveSig] int put_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
        }

        // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED: the profile in effect right now, whether chosen by
        // the user or by an automatic rule (0 = off, 1 = priority only, 2 = alarms only).
        const ulong ActiveProfileState = 0x0D83063EA3BF1C75UL;

        [DllImport("ntdll.dll")]
        static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, byte[] buffer, ref uint bufferSize);

        static bool? _available;

        public static bool Available
        {
            get
            {
                if (_available == null) _available = GetSelected() != null;
                return _available.Value;
            }
        }

        /// <summary>The profile chosen with the Do not disturb switch, or null if it cannot be read.</summary>
        public static string GetSelected()
        {
            try
            {
                string id;
                return Create().get_UserSelectedProfile(out id) == 0 ? id : null;
            }
            catch (Exception ex)
            {
                Log.Write("do not disturb: cannot read it ({0})", ex.Message);
                return null;
            }
        }

        public static bool SetSelected(string profileId)
        {
            try
            {
                int hr = Create().put_UserSelectedProfile(profileId);
                if (hr != 0) Log.Write("do not disturb: could not set {0} (0x{1:X8})", profileId, hr);
                return hr == 0;
            }
            catch (Exception ex)
            {
                Log.Write("do not disturb: could not set {0} ({1})", profileId, ex.Message);
                return false;
            }
        }

        /// <summary>True if notifications are muted right now for any reason (the switch or an automatic rule).</summary>
        public static bool IsInEffect()
        {
            try
            {
                ulong name = ActiveProfileState;
                uint stamp;
                byte[] buffer = new byte[16];
                uint size = (uint)buffer.Length;
                if (NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out stamp, buffer, ref size) == 0 && size >= 4)
                    return BitConverter.ToInt32(buffer, 0) != 0;
            }
            catch (Exception ex)
            {
                Log.Write("do not disturb: cannot read the active state ({0})", ex.Message);
            }
            // Fall back to the switch alone.
            string selected = GetSelected();
            return selected != null && !selected.EndsWith(".Unrestricted", StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

        /// <summary>
        /// True while Windows sees a full-screen app (a full-screen video, game or presentation).
        /// Windows' own rule "When using an app in full screen mode" turns Do not disturb on then.
        /// </summary>
        public static bool FullScreenAppOpen()
        {
            try
            {
                int state;
                return SHQueryUserNotificationState(out state) == 0 &&
                       (state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE);
            }
            catch
            {
                return false;
            }
        }

        static IQuietHoursSettings Create()
        {
            return (IQuietHoursSettings)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidQuietHoursSettings, true));
        }
    }
}
