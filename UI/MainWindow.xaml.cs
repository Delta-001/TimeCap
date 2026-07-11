using System.Collections.Specialized;
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

        /// <summary>Fichier .mp4, ou dossier de clips multi-écrans.</summary>
        public string FullPath { get; init; } = "";

        /// <summary>Groupe multi-écrans (dossier Clip_… contenant ScreenN.mp4).</summary>
        public bool IsGroup { get; init; }

        /// <summary>Vidéo utilisée pour la miniature (le fichier lui-même, ou Screen1 du groupe).</summary>
        public string ThumbSource { get; init; } = "";

        public Visibility StackVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

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

    private readonly CaptureManager _capture;
    private readonly Func<AppConfig> _cfg;
    private readonly Func<HotkeyBinding, Task> _saveClip;
    private readonly Action _openSettings;
    private readonly Action _openClipsFolder;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _reloadDebounce;
    private FileSystemWatcher? _clipsWatcher;
    private string? _watchedDir;
    private CancellationTokenSource? _thumbCts;
    private Point _dragStart;

    /// <summary>Dossier de clips multi-écrans ouvert dans la galerie (null = racine).</summary>
    private string? _currentFolder;

    public MainWindow(CaptureManager capture, Func<AppConfig> config,
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
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); LoadClips(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateBuffer(); _timer.Start(); }
            else _timer.Stop();
        };

        ClipsList.ContextMenu = BuildClipContextMenu();
        ClipsList.ContextMenuOpening += ClipsList_ContextMenuOpening;
        ClipsList.PreviewMouseRightButtonDown += ClipsList_PreviewMouseRightButtonDown;
        ClipsList.KeyDown += ClipsList_KeyDown;

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
            // Pastille REC qui pulse doucement.
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
        NavigateTo(null, animate: false);
        UpdateBuffer();
    }

    // ---- Navigation dans la galerie ----

    /// <summary>Ouvre un dossier de clips multi-écrans dans la galerie (null = racine).</summary>
    private void NavigateTo(string? folder, bool animate = true)
    {
        _currentFolder = folder;
        BackButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        ClipsTitle.Text = folder is null
            ? "CLIPS RÉCENTS"
            : Path.GetFileName(folder).ToUpperInvariant();
        LoadClips();
        if (animate)
        {
            ClipsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigateTo(null);

    // ---- Buffer ----

    private void UpdateBuffer()
    {
        try
        {
            var cfg = _cfg();
            int segLen = Math.Max(1, cfg.SegmentLengthS);
            var sessions = _capture.Sessions;
            int seconds = 0;
            long bytes = 0;
            foreach (var session in sessions)
            {
                var segments = session.GetCompletedSegments();
                // Couverture commune : le plus petit historique parmi les écrans.
                int s = segments.Count * segLen;
                seconds = seconds == 0 ? s : Math.Min(seconds, s);
                foreach (var seg in segments)
                {
                    try { bytes += new FileInfo(seg.Path).Length; } catch { }
                }
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
            // Filtre large : les clips simples sont des .mp4, les clips
            // multi-écrans des dossiers — rechargement « debouncé ».
            _clipsWatcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler handler = (_, _) => Dispatcher.BeginInvoke(ScheduleReload);
            _clipsWatcher.Created += handler;
            _clipsWatcher.Deleted += handler;
            _clipsWatcher.Changed += handler;
            _clipsWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleReload);
            _clipsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Warn("Surveillance du dossier clips : " + ex.Message); }
    }

    private void ScheduleReload()
    {
        _reloadDebounce.Stop();
        _reloadDebounce.Start();
    }

    private void LoadClips()
    {
        try
        {
            // Dans un dossier multi-écrans, si celui-ci a disparu → retour racine.
            if (_currentFolder != null && !Directory.Exists(_currentFolder))
            {
                NavigateTo(null, animate: false);
                return;
            }

            var dir = _currentFolder ?? _cfg().OutputDir;
            var items = new List<ClipRow>();
            if (Directory.Exists(dir))
            {
                var root = new DirectoryInfo(dir);
                var entries = new List<(DateTime Date, ClipRow Row)>();

                foreach (var f in root.GetFiles("*.mp4"))
                {
                    bool pinned = _currentFolder is null && PinStore.IsPinned(f.Name);
                    entries.Add((f.LastWriteTime, new ClipRow
                    {
                        Name = f.Name,
                        Meta = $"{(pinned ? "★ · " : "")}{FormatSize(f.Length)} · {f.LastWriteTime:dd/MM/yyyy HH:mm}",
                        FullPath = f.FullName,
                        ThumbSource = f.FullName,
                    }));
                }

                // Clips multi-écrans (racine uniquement) : sous-dossiers avec vidéos.
                if (_currentFolder is null)
                {
                    foreach (var d in root.GetDirectories())
                    {
                        var videos = d.GetFiles("*.mp4").OrderBy(v => v.Name).ToList();
                        if (videos.Count == 0) continue;
                        long size = videos.Sum(v => v.Length);
                        bool pinned = PinStore.IsPinned(d.Name);
                        entries.Add((d.LastWriteTime, new ClipRow
                        {
                            Name = d.Name,
                            Meta = $"{(pinned ? "★ · " : "")}{videos.Count} écrans · {FormatSize(size)} · {d.LastWriteTime:dd/MM/yyyy HH:mm}",
                            FullPath = d.FullName,
                            IsGroup = true,
                            ThumbSource = videos[0].FullName,
                        }));
                    }
                }

                items = entries
                    .OrderByDescending(e => _currentFolder is null && PinStore.IsPinned(e.Row.Name))
                    .ThenByDescending(e => e.Date)
                    .Take(200)
                    .Select(e => e.Row)
                    .ToList();
                if (_currentFolder != null)
                    items = items.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
                var thumb = ClipThumbnails.GetOrCreate(ffmpeg, row.ThumbSource);
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
            bmp.DecodePixelWidth = 736;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private ClipRow? SelectedClip => ClipsList.SelectedItem as ClipRow;

    /// <summary>Clip simple → lecteur intégré ; groupe multi-écrans → navigation dans l'app.</summary>
    private void OpenClip(ClipRow? clip)
    {
        if (clip is null) return;
        try
        {
            if (clip.IsGroup)
            {
                if (Directory.Exists(clip.FullPath))
                    NavigateTo(clip.FullPath);
            }
            else if (File.Exists(clip.FullPath))
            {
                var player = new PlayerWindow(clip.FullPath) { Owner = this };
                player.Show();
            }
        }
        catch (Exception ex) { Log.Warn("Ouverture du clip : " + ex.Message); }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => OpenClip(SelectedClip);

    private void ClipsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenClip(SelectedClip);

    private void RevealClip_Click(object sender, RoutedEventArgs e)
    {
        var clip = SelectedClip;
        try
        {
            if (clip != null && (File.Exists(clip.FullPath) || Directory.Exists(clip.FullPath)))
                Process.Start("explorer.exe", $"/select,\"{clip.FullPath}\"");
            else
                _openClipsFolder();
        }
        catch (Exception ex) { Log.Warn("Ouverture de l'emplacement : " + ex.Message); }
    }

    // ---- Menu contextuel ----

    private MenuItem _miLink = null!, _miCompress = null!, _miGif = null!, _miFrame = null!, _miTrim = null!, _miPin = null!;
    private bool _clipToolBusy;

    private ContextMenu BuildClipContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, string gesture = "")
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }

        Item("Lire / Ouvrir", () => OpenClip(SelectedClip), "Double-clic");
        Item("Copier le fichier", CopySelected);
        menu.Items.Add(new Separator());
        _miLink = Item("Obtenir un lien de partage (72 h)", () => _ = LinkSelectedAsync());
        _miCompress = Item("Compresser pour Discord (< 10 Mo)", () => _ = CompressSelectedAsync());
        _miGif = Item("Convertir en GIF", () => _ = GifSelectedAsync());
        _miFrame = Item("Copier une image du clip", () => _ = CopyFrameSelectedAsync());
        _miTrim = Item("Découper dans le lecteur…", () => OpenClip(SelectedClip));
        menu.Items.Add(new Separator());
        _miPin = Item("Épingler", TogglePinSelected);
        Item("Renommer…", RenameSelected, "F2");
        Item("Supprimer", DeleteSelected, "Suppr");
        menu.Items.Add(new Separator());
        Item("Propriétés", ShowPropertiesSelected);
        Item("Ouvrir l'emplacement", () => RevealClip_Click(this, new RoutedEventArgs()));
        return menu;
    }

    private void ClipsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var clip = SelectedClip;
        if (clip is null)
        {
            e.Handled = true;
            return;
        }
        bool file = !clip.IsGroup;
        _miLink.IsEnabled = file && !_clipToolBusy;
        _miCompress.IsEnabled = file && !_clipToolBusy;
        _miGif.IsEnabled = file && !_clipToolBusy;
        _miFrame.IsEnabled = file && !_clipToolBusy;
        _miTrim.IsEnabled = file;
        _miPin.IsEnabled = _currentFolder is null;
        _miPin.Header = PinStore.IsPinned(clip.Name) ? "Désépingler" : "Épingler";
    }

    /// <summary>Le clic droit sélectionne la carte sous le curseur (comportement attendu).</summary>
    private void ClipsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem item)
            item.IsSelected = true;
    }

    private void ClipsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { RenameSelected(); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
    }

    private (string Ffmpeg, string Ffprobe)? Tools()
    {
        var ffmpeg = _capture.FfmpegPath ?? FfmpegLocator.Find(_cfg().FfmpegPath);
        var ffprobe = ffmpeg is null ? null : FfmpegLocator.FindProbe(ffmpeg);
        if (ffmpeg is null || ffprobe is null)
        {
            ShareHint.Text = "Moteur vidéo indisponible.";
            return null;
        }
        return (ffmpeg, ffprobe);
    }

    /// <summary>Exécute une opération d'outil sur le clip sélectionné (une à la fois).</summary>
    private async Task RunClipToolAsync(string startHint, Func<ClipRow, Task<string?>> work)
    {
        var clip = SelectedClip;
        if (clip is null || clip.IsGroup || _clipToolBusy) return;
        _clipToolBusy = true;
        ShareHint.Text = startHint;
        try
        {
            var doneHint = await work(clip);
            if (doneHint != null) ShareHint.Text = doneHint;
        }
        catch (Exception ex)
        {
            Log.Warn("Outil clip : " + ex.Message);
            ShareHint.Text = "Échec — " + ex.Message;
        }
        finally { _clipToolBusy = false; }
    }

    private Task LinkSelectedAsync() => RunClipToolAsync("Envoi du clip vers l'hébergeur…", async clip =>
    {
        var url = await ClipTools.UploadForLinkAsync(clip.FullPath,
            new Progress<string>(s => Dispatcher.BeginInvoke(() => ShareHint.Text = s)));
        Clipboard.SetText(url);
        return $"Lien copié ✓ (valable 72 h) : {url}";
    });

    private Task CompressSelectedAsync() => RunClipToolAsync("Compression en cours…", async clip =>
    {
        if (Tools() is not { } t) return null;
        var output = await ClipTools.CompressAsync(t.Ffmpeg, t.Ffprobe, clip.FullPath, 9.5);
        Clipboard.SetFileDropList(new StringCollection { output });
        return $"Compressé ✓ ({FormatSize(new FileInfo(output).Length)}) — copié, collez-le (Ctrl+V)";
    });

    private Task GifSelectedAsync() => RunClipToolAsync("Conversion en GIF…", async clip =>
    {
        if (Tools() is not { } t) return null;
        var output = await ClipTools.ToGifAsync(t.Ffmpeg, clip.FullPath);
        Clipboard.SetFileDropList(new StringCollection { output });
        return $"GIF créé ✓ ({FormatSize(new FileInfo(output).Length)}) — copié, collez-le (Ctrl+V)";
    });

    private Task CopyFrameSelectedAsync() => RunClipToolAsync("Extraction de l'image…", async clip =>
    {
        if (Tools() is not { } t) return null;
        var png = await ClipTools.ExtractFrameAsync(t.Ffmpeg, clip.FullPath);
        var frame = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Clipboard.SetImage(frame);
        return "Image copiée ✓ — collez-la (Ctrl+V)";
    });

    private void TogglePinSelected()
    {
        var clip = SelectedClip;
        if (clip is null || _currentFolder != null) return;
        PinStore.Toggle(clip.Name);
        LoadClips();
    }

    private void RenameSelected()
    {
        var clip = SelectedClip;
        if (clip is null) return;
        var initial = clip.IsGroup ? clip.Name : Path.GetFileNameWithoutExtension(clip.Name);
        var newName = Dialogs.Prompt(this, "Renommer le clip", "Nouveau nom :", initial);
        if (newName is null || newName == initial) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShareHint.Text = "Nom invalide.";
            return;
        }
        try
        {
            if (!clip.IsGroup && !Path.HasExtension(newName))
                newName += Path.GetExtension(clip.FullPath);
            var target = Path.Combine(Path.GetDirectoryName(clip.FullPath)!, newName);
            if (File.Exists(target) || Directory.Exists(target))
            {
                ShareHint.Text = "Un clip porte déjà ce nom.";
                return;
            }
            if (clip.IsGroup) Directory.Move(clip.FullPath, target);
            else File.Move(clip.FullPath, target);
            PinStore.Rename(clip.Name, newName);
            LoadClips();
        }
        catch (Exception ex)
        {
            Log.Warn("Renommage : " + ex.Message);
            ShareHint.Text = "Renommage impossible — " + ex.Message;
        }
    }

    private void DeleteSelected()
    {
        var clip = SelectedClip;
        if (clip is null) return;
        var confirm = MessageBox.Show(this,
            $"Envoyer « {clip.Name} » à la corbeille ?",
            "Supprimer le clip", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            if (clip.IsGroup)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(clip.FullPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(clip.FullPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            LoadClips();
        }
        catch (Exception ex)
        {
            Log.Warn("Suppression : " + ex.Message);
            ShareHint.Text = "Suppression impossible — " + ex.Message;
        }
    }

    private void ShowPropertiesSelected()
    {
        var clip = SelectedClip;
        if (clip is null) return;
        try
        {
            var lines = new List<string> { $"Nom : {clip.Name}", $"Emplacement : {clip.FullPath}" };
            if (clip.IsGroup)
            {
                var videos = new DirectoryInfo(clip.FullPath).GetFiles("*.mp4").OrderBy(v => v.Name).ToList();
                lines.Add($"Écrans : {videos.Count} ({string.Join(", ", videos.Select(v => Path.GetFileNameWithoutExtension(v.Name)))})");
                lines.Add($"Taille totale : {FormatSize(videos.Sum(v => v.Length))}");
                lines.Add($"Date : {new DirectoryInfo(clip.FullPath).LastWriteTime:dd/MM/yyyy HH:mm:ss}");
            }
            else
            {
                var info = new FileInfo(clip.FullPath);
                lines.Add($"Taille : {FormatSize(info.Length)}");
                lines.Add($"Date : {info.LastWriteTime:dd/MM/yyyy HH:mm:ss}");
                if (Tools() is { } t && FfmpegPlayback.Probe(t.Ffprobe, clip.FullPath) is { } media)
                {
                    lines.Add($"Durée : {TimeSpan.FromSeconds(media.DurationSeconds):mm\\:ss}");
                    lines.Add($"Résolution : {media.Width}×{media.Height} @ {media.Fps:0.#} i/s");
                    var (code, streams, _) = ProcessUtil.Run(t.Ffprobe,
                        $"-v error -show_entries stream=codec_name,codec_type -of csv=p=0 \"{clip.FullPath}\"");
                    if (code == 0)
                        lines.Add("Flux : " + string.Join(" · ", streams.Trim().Replace("\r", "").Split('\n')));
                }
            }
            Dialogs.Info(this, "Propriétés du clip", string.Join("\n", lines));
        }
        catch (Exception ex) { Log.Warn("Propriétés : " + ex.Message); }
    }

    // ---- Partage ----

    /// <summary>Copie le clip (fichier ou dossier) : collable tel quel dans Discord, WhatsApp, un mail…</summary>
    private void CopySelected()
    {
        var clip = SelectedClip;
        if (clip is null) return;
        try
        {
            Clipboard.SetFileDropList(new StringCollection { clip.FullPath });
            ShareHint.Text = "Copié ✓ — collez (Ctrl+V) dans Discord, WhatsApp, un mail…";
        }
        catch (Exception ex) { Log.Warn("Copie du clip : " + ex.Message); }
    }

    private void CopyClip_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void ClipsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    /// <summary>Glisser-déposer un clip vers Discord, l'Explorateur, un mail…</summary>
    private void ClipsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SelectedClip is null) return;
        var delta = _dragStart - e.GetPosition(null);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { SelectedClip.FullPath });
            DragDrop.DoDragDrop(ClipsList, data, DragDropEffects.Copy);
        }
        catch (Exception ex) { Log.Warn("Glisser-déposer : " + ex.Message); }
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
