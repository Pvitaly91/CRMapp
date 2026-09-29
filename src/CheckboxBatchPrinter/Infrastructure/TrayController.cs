using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CheckboxBatchPrinter.Infrastructure;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _ownedIcon;
    private bool _tipShown;

    public TrayController(Action open, Action refresh, Action pause, Action settings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Відкрити CRMapp", null, (_, _) => open());
        menu.Items.Add("Оновити зараз", null, (_, _) => refresh());
        menu.Items.Add("Призупинити / відновити автооновлення", null, (_, _) => pause());
        menu.Items.Add("Налаштування", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Вийти", null, (_, _) => exit());
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.FillEllipse(Brushes.SteelBlue, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("C", font, Brushes.White, 8, 5);
        }
        var handle = bitmap.GetHicon();
        try { _ownedIcon = (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
        _icon = new Forms.NotifyIcon { Icon = _ownedIcon, Text = "CRMapp — фонове оновлення", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();
    }

    public void ExplainFirstHide()
    {
        if (_tipShown) return;
        _tipShown = true;
        _icon.ShowBalloonTip(5000, "CRMapp працює у треї",
            "Подвійний клік відкриє вікно. Для повного закриття виберіть «Вийти» у меню значка.", Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _ownedIcon.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
