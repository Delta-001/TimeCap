using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using ScreenClipTool.Capture;

namespace ScreenClipTool.UI;

/// <summary>
/// Moteur de lecture universel : ffmpeg décode la vidéo en frames brutes BGRA
/// (pipe → WriteableBitmap) et l'audio en PCM (pipe → NAudio). Lit tout ce que
/// ffmpeg sait décoder — dont l'AV1 de nos clips, que le MediaElement WPF
/// (base DirectShow) ne supporte pas. La pause s'appuie sur la contre-pression
/// des pipes : on cesse de consommer, ffmpeg attend.
/// </summary>
public sealed class FfmpegPlayback : IDisposable
{
    public sealed record MediaInfo(int Width, int Height, double Fps, double DurationSeconds);

    /// <summary>Dimensions / cadence / durée du flux vidéo, ou null si illisible.</summary>
    public static MediaInfo? Probe(string ffprobePath, string file)
    {
        try
        {
            var (code, stdout, _) = ProcessUtil.Run(ffprobePath,
                $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate -of csv=p=0 \"{file}\"");
            if (code != 0) return null;
            var parts = stdout.Trim().Split(',');
            if (parts.Length < 3) return null;
            int width = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int height = int.Parse(parts[1], CultureInfo.InvariantCulture);
            double fps = 30;
            var rate = parts[2].Split('/');
            if (rate.Length == 2
                && double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                && double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                && den > 0 && num > 0)
                fps = num / den;
            double duration = ProcessUtil.ProbeDurationSeconds(ffprobePath, file) ?? 0;
            return new MediaInfo(width, height, Math.Clamp(fps, 1, 240), duration);
        }
        catch { return null; }
    }

    private readonly Dispatcher _dispatcher;
    private readonly Process _videoProc;
    private readonly Process? _audioProc;
    private readonly Thread _videoThread;
    private readonly Thread? _audioThread;
    private readonly Stopwatch _clock = new();
    private readonly double _fps;
    private readonly TimeSpan _startOffset;
    private readonly int _width;
    private readonly int _height;
    private readonly BufferedWaveProvider? _waveProvider;
    private readonly WaveOutEvent? _waveOut;
    private volatile bool _paused = true;
    private volatile bool _disposed;
    private long _framesShown;

    public WriteableBitmap Bitmap { get; }
    public bool IsPaused => _paused;
    public TimeSpan Position => _startOffset + _clock.Elapsed;

    /// <summary>Fin du flux vidéo atteinte (levé hors thread UI).</summary>
    public event Action? Ended;

    public double Volume
    {
        set { if (_waveOut != null) _waveOut.Volume = (float)Math.Clamp(value, 0, 1); }
    }

    public FfmpegPlayback(string ffmpegPath, string file, MediaInfo info, double startSeconds, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _fps = info.Fps;
        _startOffset = TimeSpan.FromSeconds(startSeconds);

        // Décodage à la taille d'affichage (≤1280 de large) : divise par ~4 le
        // débit du pipe et le coût de rendu par rapport au 1440p natif.
        _width = Math.Min(info.Width, 1280);
        _width -= _width % 2;
        _height = (int)Math.Round((double)info.Height * _width / info.Width);
        _height -= _height % 2;
        if (_width <= 0 || _height <= 0) { _width = 640; _height = 360; }
        Bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);

        string seek = startSeconds > 0.05
            ? $"-ss {startSeconds.ToString("0.###", CultureInfo.InvariantCulture)} "
            : "";
        _videoProc = StartPipe(ffmpegPath,
            $"-hide_banner -v error {seek}-i \"{file}\" -f rawvideo -pix_fmt bgra -vf scale={_width}:{_height} -an pipe:1");
        try
        {
            _audioProc = StartPipe(ffmpegPath,
                $"-hide_banner -v error {seek}-i \"{file}\" -f s16le -ar 48000 -ac 2 -vn pipe:1");
            _waveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(4),
            };
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_waveProvider);
            _waveOut.Volume = 0.8f;
        }
        catch (Exception ex)
        {
            Log.Warn("Lecture audio indisponible : " + ex.Message);
        }

        _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "TimeCapPlaybackVideo" };
        if (_audioProc != null)
            _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "TimeCapPlaybackAudio" };
    }

    /// <summary>Démarre le décodage ; la première frame s'affiche même en pause (aperçu du seek).</summary>
    public void Begin(bool paused)
    {
        _paused = paused;
        if (!paused)
        {
            _clock.Start();
            _waveOut?.Play();
        }
        _videoThread.Start();
        _audioThread?.Start();
    }

    public void Pause()
    {
        _paused = true;
        _clock.Stop();
        _waveOut?.Pause();
    }

    public void Resume()
    {
        if (_disposed) return;
        _paused = false;
        _clock.Start();
        _waveOut?.Play();
    }

    private void VideoLoop()
    {
        try
        {
            var stream = _videoProc.StandardOutput.BaseStream;
            int frameBytes = _width * _height * 4;
            var buffer = new byte[frameBytes];
            var rect = new Int32Rect(0, 0, _width, _height);
            bool first = true;

            while (!_disposed)
            {
                if (!first)
                {
                    // Pause = on cesse de lire le pipe, ffmpeg attend.
                    while (_paused && !_disposed) Thread.Sleep(30);
                }
                if (_disposed) return;
                if (!ReadExactly(stream, buffer, frameBytes)) break; // EOF

                if (!first)
                {
                    double pts = _framesShown / _fps;
                    while (!_disposed && !_paused && _clock.Elapsed.TotalSeconds < pts)
                        Thread.Sleep(2);
                    if (_disposed) return;
                }
                _framesShown++;
                first = false;

                // Invoke synchrone : le buffer est réutilisé pour la frame suivante.
                _dispatcher.Invoke(() =>
                {
                    if (!_disposed)
                        Bitmap.WritePixels(rect, buffer, _width * 4, 0);
                }, DispatcherPriority.Render);
            }
            if (!_disposed) Ended?.Invoke();
        }
        catch (Exception ex)
        {
            if (!_disposed) Log.Warn("Lecture vidéo interrompue : " + ex.Message);
        }
    }

    private void AudioLoop()
    {
        try
        {
            var stream = _audioProc!.StandardOutput.BaseStream;
            var buffer = new byte[32768];
            while (!_disposed)
            {
                if (_paused || _waveProvider!.BufferedDuration > TimeSpan.FromSeconds(2))
                {
                    Thread.Sleep(40);
                    continue;
                }
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                _waveProvider.AddSamples(buffer, 0, read);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) Log.Warn("Lecture audio interrompue : " + ex.Message);
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    private static Process StartPipe(string exe, string args)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        proc.Start();
        ChildProcessJob.Attach(proc);
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();
        return proc;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Pas de Join : les threads sortent d'eux-mêmes (flag + pipes cassés) ;
        // joindre ici pourrait s'interbloquer avec un Invoke en cours vers l'UI.
        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }
        KillQuietly(_videoProc);
        if (_audioProc != null) KillQuietly(_audioProc);
    }

    private static void KillQuietly(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        try { proc.Dispose(); } catch { }
    }
}
