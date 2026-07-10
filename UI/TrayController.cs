using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using ScreenClipTool.Config;

namespace ScreenClipTool.UI;

/// <summary>Icône de zone de notification : statut, menu, notifications.</summary>
public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _taskbar;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _saveMenu;
    private readonly Action<HotkeyBinding> _saveClip;

    public TrayController(Action openMain, Action openSettings, Action<HotkeyBinding> saveClip,
                          Action openClipsFolder, Action exit)
    {
        _saveClip = saveClip;

        _statusItem = new MenuItem { Header = "Démarrage…", IsEnabled = false };
        _saveMenu = new MenuItem { Header = "Sauvegarder un clip", IsEnabled = false };

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Ouvrir ScreenClipTool", openMain));
        menu.Items.Add(_saveMenu);
        menu.Items.Add(MakeItem("Ouvrir le dossier des clips", openClipsFolder));
        menu.Items.Add(MakeItem("Réglages…", openSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Quitter", exit));

        _taskbar = new TaskbarIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "TimeCap — clip the moment",
            ContextMenu = menu,
        };
        _taskbar.TrayMouseDoubleClick += (_, _) => openMain();
    }

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public void UpdateBindings(IEnumerable<HotkeyBinding> bindings)
    {
        _saveMenu.Items.Clear();
        foreach (var b in bindings)
        {
            var binding = b;
            _saveMenu.Items.Add(MakeItem($"{binding.DescribeDuration()}  ({binding.Describe()})",
                () => _saveClip(binding)));
        }
        _saveMenu.IsEnabled = _saveMenu.Items.Count > 0;
    }

    public void SetStatus(string status)
    {
        _statusItem.Header = status;
        _taskbar.ToolTipText = "TimeCap — " + status;
    }

    public void Notify(string title, string message) =>
        _taskbar.ShowBalloonTip(title, message, BalloonIcon.Info);

    public void NotifyError(string title, string message) =>
        _taskbar.ShowBalloonTip(title, message, BalloonIcon.Error);

    public void Dispose() => _taskbar.Dispose();
}

/// <summary>
/// Icône du tray : celle embarquée dans l'exe (logo TimeCap, via ApplicationIcon),
/// avec en secours une pastille dessinée reprenant le motif du logo
/// (écran bleu + rewind blanc).
/// </summary>
internal static class TrayIconFactory
{
    public static System.Drawing.Icon Create()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch { /* on retombe sur le dessin */ }
        return DrawFallback();
    }

    private static System.Drawing.Icon DrawFallback()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var rect = new System.Drawing.RectangleF(1, 6, 30, 20);
        const float r = 5f;
        path.AddArc(rect.X, rect.Y, 2 * r, 2 * r, 180, 90);
        path.AddArc(rect.Right - 2 * r, rect.Y, 2 * r, 2 * r, 270, 90);
        path.AddArc(rect.Right - 2 * r, rect.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        using (var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 47, 107, 228)))
            g.FillPath(blue, path);
        var white = System.Drawing.Brushes.White;
        g.FillPolygon(white, new[]
        {
            new System.Drawing.PointF(9, 16), new System.Drawing.PointF(15, 11), new System.Drawing.PointF(15, 21),
        });
        g.FillPolygon(white, new[]
        {
            new System.Drawing.PointF(16, 16), new System.Drawing.PointF(22, 11), new System.Drawing.PointF(22, 21),
        });
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
