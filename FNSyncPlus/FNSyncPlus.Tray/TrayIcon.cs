using System.Drawing;
using System.Windows.Forms;

namespace FNSyncPlus.Tray;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _image;

    public TrayIcon(Action show, Action exit, string displayName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _image = File.Exists(path) ? new Icon(path, SystemInformation.SmallIconSize) : (Icon)SystemIcons.Application.Clone();
        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开主界面", null, (_, _) => show());
        _menu.Items.Add("退出程序", null, (_, _) => exit());
        _icon = new NotifyIcon { Text = displayName, Icon = _image, ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => show();
    }

    public string DisplayText => _icon.Text;

    public void Dispose()
    {
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }
}
