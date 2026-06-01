using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Protractor;

using Application = System.Windows.Application;

public partial class App : Application
{
    private WinForms.NotifyIcon? _tray;
    private OverlayWindow? _overlay;
    private Icon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _overlay = new OverlayWindow();
        _overlay.Show();

        _trayIcon = CreateTrayIcon();

        var menu = new WinForms.ContextMenuStrip();

        var toggleItem = new WinForms.ToolStripMenuItem("显示 / 隐藏 量角器");
        toggleItem.Click += (_, _) => ToggleOverlay();

        var resetItem = new WinForms.ToolStripMenuItem("重置位置");
        resetItem.Click += (_, _) => _overlay?.ResetToCenter();

        var modeItem = new WinForms.ToolStripMenuItem("切换 直尺 / 量角器");
        modeItem.Click += (_, _) => _overlay?.ToggleMode();

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(toggleItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(modeItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "屏幕量角器",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ToggleOverlay();
    }

    private void ToggleOverlay()
    {
        if (_overlay is null)
            return;

        if (_overlay.IsVisible)
            _overlay.Hide();
        else
        {
            _overlay.Show();
            _overlay.Activate();
        }
    }

    internal void ExitApp()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _overlay?.Close();
        Shutdown();
    }

    /// <summary>Draws a small angle/protractor glyph for the notification-area icon.</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var vertex = new PointF(6, 26);
            using var pen = new Pen(Color.FromArgb(255, 30, 144, 255), 3f);
            // two arms forming an angle
            g.DrawLine(pen, vertex, new PointF(29, 26));
            g.DrawLine(pen, vertex, new PointF(24, 6));
            // arc between the arms
            using var arcPen = new Pen(Color.FromArgb(255, 255, 140, 0), 2f);
            g.DrawArc(arcPen, vertex.X - 12, vertex.Y - 12, 24, 24, -65, 65);
        }

        IntPtr hIcon = bmp.GetHicon();
        // Clone so we own a managed copy and can release the GDI handle.
        using var temp = Icon.FromHandle(hIcon);
        var icon = (Icon)temp.Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
