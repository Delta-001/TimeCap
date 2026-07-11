using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenClipTool.Capture;

namespace ScreenClipTool.UI;

/// <summary>
/// Lecteur intégré : décodage ffmpeg rendu directement dans la fenêtre
/// (voir <see cref="FfmpegPlayback"/>) — lit tous nos clips, AV1 compris,
/// sans dépendre des codecs installés sur Windows.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly string _path;
    private readonly DispatcherTimer _timer;
    private string? _ffmpeg;
    private FfmpegPlayback.MediaInfo? _info;
    private FfmpegPlayback? _playback;
    private bool _seeking;
    private bool _ended;

    public PlayerWindow(string path)
    {
        InitializeComponent();
        _path = path;
        Title = "TimeCap — " + Path.GetFileName(path);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => UpdatePosition();

        Loaded += (_, _) => Open();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _playback?.Dispose();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { TogglePlayPause(); e.Handled = true; }
            else if (e.Key == Key.Escape) Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    private void Open()
    {
        _ffmpeg = FfmpegLocator.Find(null);
        var ffprobe = _ffmpeg is null ? null : FfmpegLocator.FindProbe(_ffmpeg);
        _info = ffprobe is null ? null : FfmpegPlayback.Probe(ffprobe, _path);
        if (_ffmpeg is null || _info is null)
        {
            // Sans moteur vidéo exploitable, on délègue au lecteur système.
            Log.Warn($"Lecteur intégré indisponible pour {Path.GetFileName(_path)} — bascule externe.");
            OpenExternal();
            Close();
            return;
        }
        PositionSlider.Maximum = Math.Max(0.1, _info.DurationSeconds);
        StartAt(0, playing: true);
        _timer.Start();
    }

    private void StartAt(double seconds, bool playing)
    {
        _playback?.Dispose();
        _ended = false;
        _playback = new FfmpegPlayback(_ffmpeg!, _path, _info!, seconds, Dispatcher);
        _playback.Volume = VolumeSlider.Value;
        _playback.Ended += () => Dispatcher.BeginInvoke(OnPlaybackEnded);
        VideoImage.Source = _playback.Bitmap;
        _playback.Begin(paused: !playing);
        PlayPauseButton.Content = playing ? "⏸" : "▶";
    }

    private void OnPlaybackEnded()
    {
        _ended = true;
        PlayPauseButton.Content = "▶";
        PositionSlider.Value = PositionSlider.Maximum;
        UpdateTimeText(TimeSpan.FromSeconds(_info?.DurationSeconds ?? 0));
    }

    private void TogglePlayPause()
    {
        if (_playback is null) return;
        if (_ended)
        {
            StartAt(0, playing: true);
        }
        else if (_playback.IsPaused)
        {
            _playback.Resume();
            PlayPauseButton.Content = "⏸";
        }
        else
        {
            _playback.Pause();
            PlayPauseButton.Content = "▶";
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void Video_Click(object sender, MouseButtonEventArgs e) => TogglePlayPause();

    private void UpdatePosition()
    {
        if (_playback is null || _seeking || _ended) return;
        var position = _playback.Position;
        var max = TimeSpan.FromSeconds(PositionSlider.Maximum);
        if (position > max) position = max;
        PositionSlider.Value = position.TotalSeconds;
        UpdateTimeText(position);
    }

    private void UpdateTimeText(TimeSpan position) =>
        TimeText.Text = $"{Format(position)} / {Format(TimeSpan.FromSeconds(_info?.DurationSeconds ?? 0))}";

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void Slider_SeekStart(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void Slider_SeekEnd(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        if (_playback is null) return;
        bool resume = !_playback.IsPaused && !_ended;
        StartAt(PositionSlider.Value, playing: resume);
        UpdateTimeText(TimeSpan.FromSeconds(PositionSlider.Value));
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_playback != null)
            _playback.Volume = e.NewValue;
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", $"/select,\"{_path}\""); }
        catch (Exception ex) { Log.Warn("Ouverture de l'emplacement : " + ex.Message); }
    }

    private void External_Click(object sender, RoutedEventArgs e)
    {
        OpenExternal();
        Close();
    }

    private void OpenExternal()
    {
        try { Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Lecteur externe : " + ex.Message); }
    }
}
