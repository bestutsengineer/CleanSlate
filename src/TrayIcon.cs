using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CleanSlate
{
    /// <summary>
    /// The icon in the notification area (right end of the taskbar).
    /// Left click: the chosen action. Right click: the menu. It turns blue while the gray is on.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        readonly NotifyIcon _icon = new NotifyIcon();
        readonly Icon _normal, _grayOn;
        DateTime _lastLeftClick = DateTime.MinValue;

        public TrayIcon(App app)
        {
            Size size = SystemInformation.SmallIconSize;
            _normal = LoadIcon("CleanSlate.ico", size);
            _grayOn = LoadIcon("CleanSlate-on.ico", size);
            _icon.Icon = _normal;
            _icon.Text = "CleanSlate";
            _icon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    app.ShowMenu(app.MenuOwner);
                }
                else if (e.Button == MouseButtons.Left)
                {
                    // A double click must not run the action and then undo it.
                    if ((DateTime.UtcNow - _lastLeftClick).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
                    _lastLeftClick = DateTime.UtcNow;
                    app.OnTrayClick();
                }
            };
            _icon.Visible = true;
        }

        public void Update(bool grayOn, string tooltip)
        {
            _icon.Icon = grayOn ? _grayOn : _normal;
            // NotifyIcon.Text is limited to 63 characters.
            _icon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        public void Notify(string text)
        {
            _icon.ShowBalloonTip(4000, "CleanSlate", text, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _normal.Dispose();
            _grayOn.Dispose();
        }

        static Icon LoadIcon(string name, Size size)
        {
            using (Stream s = typeof(TrayIcon).Assembly.GetManifestResourceStream(name))
                return new Icon(s, size);
        }
    }
}
