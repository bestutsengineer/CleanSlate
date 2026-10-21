using System;
using System.Runtime.InteropServices;

namespace CleanSlate
{
    /// <summary>
    /// A native Windows popup menu: looks like every other Windows 11 menu and scales correctly on each screen.
    /// Destroying the top menu also destroys its submenus.
    /// </summary>
    internal sealed class PopupMenu : IDisposable
    {
        readonly IntPtr _handle;
        readonly bool _owned;

        public PopupMenu() : this(Native.CreatePopupMenu(), true) { }

        PopupMenu(IntPtr handle, bool owned)
        {
            _handle = handle;
            _owned = owned;
        }

        /// <param name="isDefault">Shown in bold: the action a left click performs.</param>
        /// <param name="radio">Show the check as a dot (one choice out of a group).</param>
        public void Item(int id, string text, bool enabled = true, bool isDefault = false, bool check = false, bool radio = false)
        {
            MENUITEMINFO mi = NewInfo();
            mi.fMask = Native.MIIM_ID | Native.MIIM_STRING | Native.MIIM_STATE | Native.MIIM_FTYPE;
            mi.fType = radio ? Native.MFT_RADIOCHECK : 0;
            mi.fState = (enabled ? 0 : Native.MFS_DISABLED) | (check ? Native.MFS_CHECKED : 0) | (isDefault ? Native.MFS_DEFAULT : 0);
            mi.wID = (uint)id;
            mi.dwTypeData = text;
            Insert(ref mi);
        }

        public void Separator()
        {
            MENUITEMINFO mi = NewInfo();
            mi.fMask = Native.MIIM_FTYPE;
            mi.fType = Native.MFT_SEPARATOR;
            Insert(ref mi);
        }

        public PopupMenu Submenu(string text, bool enabled = true)
        {
            IntPtr sub = Native.CreatePopupMenu();
            MENUITEMINFO mi = NewInfo();
            mi.fMask = Native.MIIM_STRING | Native.MIIM_SUBMENU | Native.MIIM_STATE;
            mi.fState = enabled ? 0 : Native.MFS_DISABLED;
            mi.hSubMenu = sub;
            mi.dwTypeData = text;
            Insert(ref mi);
            return new PopupMenu(sub, false);
        }

        /// <summary>Shows the menu at a screen point and returns the chosen id (0 if dismissed).</summary>
        public int Show(IntPtr owner, POINT at)
        {
            // Without this the menu would not close when clicking elsewhere (classic tray-menu quirk).
            Native.SetForegroundWindow(owner);
            int id = Native.TrackPopupMenuEx(_handle, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, at.X, at.Y, owner, IntPtr.Zero);
            Native.PostMessage(owner, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return id;
        }

        public void Dispose()
        {
            if (_owned) Native.DestroyMenu(_handle);
        }

        static MENUITEMINFO NewInfo()
        {
            MENUITEMINFO mi = new MENUITEMINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MENUITEMINFO));
            return mi;
        }

        void Insert(ref MENUITEMINFO mi)
        {
            Native.InsertMenuItem(_handle, (uint)Native.GetMenuItemCount(_handle), true, ref mi);
        }
    }
}
