using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenClipTool.Capture;
using ScreenClipTool.Config;

namespace ScreenClipTool.UI;

public partial class MainWindow : Window
{
    private sealed class ClipRow : INotifyPropertyChanged
    {
        public string Name { get; init; } = "";
        public string Meta { get; init; } = "";
        public string FullPath { get; init; } = "";

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly CaptureEngine _capture;
    private readonly Func<AppConfig> _cfg;
    private readonly Func<HotkeyBinding, Task> _saveClip;
    private readonly Action _openSettings;
    private readonly Action _openClipsFolder;
    private readonly DispatcherTimer _timer;
    private FileSystemWatcher? _clipsWatcher;
    private string? _watchedDir;
    private CancellationTokenSource? _thumbCts;

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
        bool active = status.StartsWith("Enregistrement en cours", StringComparison.OrdinalIgnoreCase);
        bool warning = status.Contains("interrompu", StringComparison.OrdinalIgnoreCase)
                       || status.Contains("reprise", StringComparison.OrdinalIgnoreCase);
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

        GranularityHint.Text = $"précision : {cfg.SegmentLengthS} s";
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
                        Meta = $"{FormatSize(f.Length)} · {f.LastWriteTime:dd/MM/yyyy HH:mm}",
                        FullPath = f.FullName,
                    })
                    .ToList();
            }
            ClipsList.ItemsSource = items;
            ClipsEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _thumbCts?.Cancel();
            _thumbCts = new CancellationTokenSource();
            _ = LoadThumbnailsAsync(items, _thumbCts.Token);
        }
        catch (Exception ex) { Log.Warn("Liste des clips : " + ex.Message); }
    }

    /// <summary>Génère/charge les miniatures en arrière-plan, du plus récent au plus ancien.</summary>
    private async Task LoadThumbnailsAsync(List<ClipRow> rows, CancellationToken ct)
    {
        var ffmpeg = _capture.FfmpegPath ?? FfmpegLocator.Find(_cfg().FfmpegPath);
        if (ffmpeg is null) return;
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) return;
            var image = await Task.Run(() =>
            {
                var thumb = ClipThumbnails.GetOrCreate(ffmpeg, row.FullPath);
                return thumb is null ? null : LoadBitmap(thumb);
            }, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            if (image != null)
                await Dispatcher.BeginInvoke(() => row.Thumbnail = image);
        }
    }

    /// <summary>Bitmap figé (Freeze) : chargeable hors thread UI, fichier non verrouillé.</summary>
    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 336;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
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
