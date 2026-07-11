using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenClipTool.UI;

/// <summary>
/// Lecteur intégré (MediaElement / codecs Windows). Si le codec du clip n'est
/// pas disponible sur la machine (ex. AV1 sans l'extension gratuite du Store),
/// bascule automatiquement vers le lecteur système.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly string _path;
    private readonly DispatcherTimer _timer;
    private bool _seeking;
    private bool _playing;

    public PlayerWindow(string path)
    {
        InitializeComponent();
        _path = path;
        Title = "TimeCap — " + Path.GetFileName(path);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdatePosition();

        Loaded += (_, _) =>
        {
            Media.Source = new Uri(path);
            Media.Volume = 0.8;
            Play();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            try { Media.Close(); } catch { }
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

    private void Play()
    {
        Media.Play();
        _playing = true;
        PlayPauseButton.Content = "⏸";
        _timer.Start();
    }

    private void Pause()
    {
        Media.Pause();
        _playing = false;
        PlayPauseButton.Content = "▶";
    }

    private void TogglePlayPause()
    {
        if (_playing) Pause();
        else Play();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void Media_Click(object sender, MouseButtonEventArgs e) => TogglePlayPause();

    private void Media_Opened(object sender, RoutedEventArgs e)
    {
        if (Media.NaturalDuration.HasTimeSpan)
            PositionSlider.Maximum = Media.NaturalDuration.TimeSpan.TotalSeconds;
        UpdatePosition();
    }

    private void Media_Ended(object sender, RoutedEventArgs e)
    {
        Media.Position = TimeSpan.Zero;
        Pause();
        UpdatePosition();
    }

    private void Media_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        // Codec absent (AV1/HEVC selon la machine) : on n'affiche pas d'erreur,
        // on délègue au lecteur système qui, lui, sait peut-être le lire.
        Log.Warn($"Lecture intégrée impossible ({Path.GetFileName(_path)}) : {e.ErrorException?.Message}");
        OpenExternal();
        Close();
    }

    private void UpdatePosition()
    {
        if (_seeking || !Media.NaturalDuration.HasTimeSpan) return;
        var position = Media.Position;
        var total = Media.NaturalDuration.TimeSpan;
        PositionSlider.Value = Math.Min(position.TotalSeconds, PositionSlider.Maximum);
        TimeText.Text = $"{Format(position)} / {Format(total)}";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void Slider_SeekStart(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void Slider_SeekEnd(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        Media.Position = TimeSpan.FromSeconds(PositionSlider.Value);
        UpdatePosition();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking)
            Media.Position = TimeSpan.FromSeconds(PositionSlider.Value);
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
