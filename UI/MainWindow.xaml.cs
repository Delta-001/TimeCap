using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenClipTool.Capture;
using ScreenClipTool.Config;

namespace ScreenClipTool.UI;

public partial class MainWindow : Window
{
    private sealed class ClipRow
    {
        public string Name { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string DateText { get; init; } = "";
        public string FullPath { get; init; } = "";
    }

    private readonly CaptureEngine _capture;
    private readonly Func<AppConfig> _cfg;
    private readonly Func<HotkeyBinding, Task> _saveClip;
    private readonly Action _openSettings;
    private readonly Action _openClipsFolder;
    private readonly DispatcherTimer _timer;
    private FileSystemWatcher? _clipsWatcher;
    private string? _watchedDir;

    public MainWindow(CaptureEngine capture, Func<AppConfig> config,
                      Func<HotkeyBinding, Task> saveClip, Action openSettings, Action openClipsFolder)
    {
        InitializeComponent();
        _capture = capture;
        _cfg = config;
        _saveClip = saveClip;
        _openSettings = openSettings;
        _openClipsFolder = openClipsFolder;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateBuffer();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateBuffer(); _timer.Start(); }
            else _timer.Stop();
        };

        RefreshFromConfig();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    /// <summary>Fermer la fenêtre = la masquer, l'app vit dans le tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // ---- Statut ----

    public void SetStatus(string status)
    {
        StatusText.Text = status;
        bool active = status.StartsWith("Capture active", StringComparison.OrdinalIgnoreCase);
        bool warning = status.Contains("redémarrage", StringComparison.OrdinalIgnoreCase)
                       || status.Contains("interrompue", StringComparison.OrdinalIgnoreCase);
        SetDot(active, warning);
    }

    private void SetDot(bool active, bool warning)
    {
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
        if (active)
        {
            // Pastille REC rouge qui pulse doucement.
            StatusDot.Fill = (Brush)FindResource("AccentBrush");
            StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            StatusDot.Fill = warning
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xA0, 0x33))
                : new SolidColorBrush(Color.FromRgb(0x5A, 0x5E, 0x6B));
        }
    }

    // ---- Synchronisation avec la config ----

    public void RefreshFromConfig()
    {
        var cfg = _cfg();

        SaveButtons.Children.Clear();
        foreach (var b in cfg.Hotkeys)
        {
            var binding = b;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = "⏺",
                Foreground = (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{binding.DescribeDuration()}   ·   {binding.Describe()}",
                VerticalAlignment = VerticalAlignment.Center,
            });
            var btn = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8) };
            btn.Click += async (_, _) => await _saveClip(binding);
            SaveButtons.Children.Add(btn);
        }

        GranularityHint.Text = $"granularité : segments de {cfg.SegmentLengthS} s";
        SetupClipsWatcher(cfg.OutputDir);
        LoadClips();
        UpdateBuffer();
    }

    // ---- Buffer ----

    private void UpdateBuffer()
    {
        try
        {
            var cfg = _cfg();
            int segLen = Math.Max(1, cfg.SegmentLengthS);
            var segments = _capture.GetCompletedSegments();
            int seconds = segments.Count * segLen;
            long bytes = 0;
            foreach (var s in segments)
            {
                try { bytes += new FileInfo(s.Path).Length; } catch { }
            }
            int maxSeconds = Math.Max(1, cfg.MaxBufferMinutes * 60);
            BufferBar.Maximum = maxSeconds;
            BufferBar.Value = Math.Min(seconds, maxSeconds);
            BufferText.Text = $"{FormatDuration(seconds)} / {cfg.MaxBufferMinutes} min — {FormatSize(bytes)}";
        }
        catch (Exception ex) { Log.Warn("Jauge buffer : " + ex.Message); }
    }

    // ---- Clips ----

    private void SetupClipsWatcher(string dir)
    {
        if (_watchedDir == dir) return;
        _clipsWatcher?.Dispose();
        _clipsWatcher = null;
        _watchedDir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            _clipsWatcher = new FileSystemWatcher(dir, "*.mp4");
            FileSystemEventHandler handler = (_, _) => Dispatcher.BeginInvoke(LoadClips);
            _clipsWatcher.Created += handler;
            _clipsWatcher.Deleted += handler;
            _clipsWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(LoadClips);
            _clipsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Warn("Surveillance du dossier clips : " + ex.Message); }
    }

    private void LoadClips()
    {
        try
        {
            var dir = _cfg().OutputDir;
            List<ClipRow> items = new();
            if (Directory.Exists(dir))
            {
                items = new DirectoryInfo(dir).GetFiles("*.mp4")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(200)
                    .Select(f => new ClipRow
                    {
                        Name = f.Name,
                        SizeText = FormatSize(f.Length),
                        DateText = f.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss"),
                        FullPath = f.FullName,
                    })
                    .ToList();
            }
            ClipsList.ItemsSource = items;
            ClipsEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { Log.Warn("Liste des clips : " + ex.Message); }
    }

    private ClipRow? SelectedClip => ClipsList.SelectedItem as ClipRow;

    private void PlayClip(ClipRow? clip)
    {
        if (clip is null || !File.Exists(clip.FullPath)) return;
        try { Process.Start(new ProcessStartInfo(clip.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Lecture du clip : " + ex.Message); }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => PlayClip(SelectedClip);

    private void ClipsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlayClip(SelectedClip);

    private void RevealClip_Click(object sender, RoutedEventArgs e)
    {
        var clip = SelectedClip;
        try
        {
            if (clip != null && File.Exists(clip.FullPath))
                Process.Start("explorer.exe", $"/select,\"{clip.FullPath}\"");
            else
                _openClipsFolder();
        }
        catch (Exception ex) { Log.Warn("Ouverture de l'emplacement : " + ex.Message); }
    }

    private void OpenClipsFolder_Click(object sender, RoutedEventArgs e) => _openClipsFolder();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => _openSettings();

    // ---- Formatage ----

    private static string FormatDuration(int seconds) => seconds < 60
        ? $"{seconds} s"
        : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60} min {seconds % 60:00} s";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} Mo",
        _ => $"{bytes / 1024.0:0} Ko",
    };
}
