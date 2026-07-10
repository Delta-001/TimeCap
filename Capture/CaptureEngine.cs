using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenClipTool.Audio;
using ScreenClipTool.Config;

namespace ScreenClipTool.Capture;

public sealed record SegmentFile(string Path, int Number, DateTime LastWriteUtc);

/// <summary>
/// Capture continue de l'écran : lance et surveille le process ffmpeg
/// (ddagrab → NVENC → segments mp4 de N secondes), redémarre en cas de crash,
/// et borne le disque en supprimant les segments plus vieux que la fenêtre max.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly Func<AppConfig> _cfg;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _cleanupTimer;
    private readonly Queue<DateTime> _recentRestarts = new();
    private readonly Queue<string> _stderrTail = new();

    private Process? _proc;
    private AudioCaptureService? _audio;
    private bool _stopRequested;
    private bool _disposed;
    private bool _audioDisabledForSession;

    public string? FfmpegPath { get; private set; }
    public string? VideoEncoder { get; private set; }
    public string BufferDir => ResolveBufferDir(_cfg());

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    /// <summary>Texte de statut court (affiché dans le tray).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Erreur à notifier (titre, message).</summary>
    public event Action<string, string>? Error;

    public CaptureEngine(Func<AppConfig> config)
    {
        _cfg = config;
        _cleanupTimer = new System.Timers.Timer(15_000) { AutoReset = true };
        _cleanupTimer.Elapsed += (_, _) => CleanupOldSegments();
    }

    public static string ResolveBufferDir(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.BufferDir)
            ? Path.Combine(Path.GetTempPath(), "ScreenClipTool", "buffer")
            : cfg.BufferDir!;

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureEngine));
            if (_proc is { HasExited: false }) return;
            _stopRequested = false;

            var cfg = _cfg();
            FfmpegPath ??= FfmpegLocator.Find(cfg.FfmpegPath)
                ?? throw new InvalidOperationException(
                    "ffmpeg.exe introuvable. Placez-le à côté de l'application ou dans le PATH " +
                    "(ou renseignez ffmpeg_path dans config.json).");

            var caps = FfmpegCapabilities.Probe(FfmpegPath);
            if (!caps.HasDdagrab)
                throw new InvalidOperationException(
                    "Ce build ffmpeg ne supporte pas le filtre ddagrab (requis, dispo depuis ffmpeg 6.0).");
            VideoEncoder = caps.BestNvencEncoder
                ?? throw new InvalidOperationException(
                    "Aucun encodeur NVENC utilisable (av1_nvenc / hevc_nvenc). GPU NVIDIA requis.");

            var bufferDir = ResolveBufferDir(cfg);
            Directory.CreateDirectory(bufferDir);
            DeleteTrailingInvalidSegment(bufferDir);

            _audio?.Dispose();
            _audio = null;
            if (cfg.AudioEnabled && !_audioDisabledForSession)
            {
                try { _audio = AudioCaptureService.Create(cfg.MicEnabled, s => StatusChanged?.Invoke(s)); }
                catch (Exception ex)
                {
                    Log.Warn("Audio indisponible : " + ex.Message);
                    StatusChanged?.Invoke("Audio indisponible — capture vidéo seule");
                }
            }

            int startNumber = GetSegments(bufferDir).Select(s => s.Number).DefaultIfEmpty(-1).Max() + 1;
            var args = BuildArgs(cfg, bufferDir, startNumber);
            Log.Info($"Démarrage ffmpeg : {FfmpegPath} {args}");

            var psi = new ProcessStartInfo(FfmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(e.Data);
                    while (_stderrTail.Count > 60) _stderrTail.Dequeue();
                }
                Log.Info("[ffmpeg] " + e.Data);
            };
            p.Exited += (_, _) => OnProcessExited(p);
            p.Start();
            ChildProcessJob.Attach(p);
            p.BeginErrorReadLine();
            _proc = p;

            try
            {
                _audio?.StartStreaming();
            }
            catch (Exception ex)
            {
                // Sans données audio, le muxer attendrait indéfiniment :
                // on relance la session en vidéo seule.
                Log.Warn("Démarrage audio impossible, relance en vidéo seule : " + ex.Message);
                _audioDisabledForSession = true;
                Task.Run(() =>
                {
                    StopProcess(graceful: false);
                    lock (_lock) { _audio?.Dispose(); _audio = null; }
                    try { Start(); }
                    catch (Exception ex2) { Error?.Invoke("Capture impossible", ex2.Message); }
                });
                return;
            }

            _cleanupTimer.Start();
            var desc = VideoEncoder == "av1_nvenc" ? "AV1" : "HEVC";
            if (_audio?.Loopback != null) desc += _audio.Mic != null ? " + audio + micro" : " + audio";
            StatusChanged?.Invoke($"Capture active ({desc})");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopRequested = true;
            _audioDisabledForSession = false;
        }
        _cleanupTimer.Stop();
        // Arrêt propre : 'q' sur stdin pour que ffmpeg finalise le segment en
        // cours (moov écrit) avant de sortir — on ne perd pas les dernières secondes.
        StopProcess(graceful: true);
        lock (_lock)
        {
            _audio?.Dispose();
            _audio = null;
        }
        StatusChanged?.Invoke("Capture arrêtée");
    }

    private void StopProcess(bool graceful)
    {
        Process? p;
        lock (_lock)
        {
            p = _proc;
            _proc = null; // détaché : OnProcessExited ignorera cette sortie
        }
        if (p is null) return;
        try
        {
            if (!p.HasExited && graceful)
            {
                try
                {
                    p.StandardInput.Write('q');
                    p.StandardInput.Flush();
                }
                catch { }
                if (!p.WaitForExit(4_000))
                    p.Kill(entireProcessTree: true);
            }
            else if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
            p.WaitForExit(2_000);
        }
        catch { }
        finally { p.Dispose(); }
    }

    private void OnProcessExited(Process p)
    {
        bool restart;
        lock (_lock)
        {
            if (_proc != p || _stopRequested || _disposed) return; // arrêt volontaire
            _proc = null;
            var now = DateTime.UtcNow;
            _recentRestarts.Enqueue(now);
            while (_recentRestarts.Count > 0 && now - _recentRestarts.Peek() > TimeSpan.FromSeconds(60))
                _recentRestarts.Dequeue();
            restart = _recentRestarts.Count <= 5;
        }

        string tail;
        lock (_stderrTail) tail = string.Join("\n", _stderrTail.TakeLast(8));
        Log.Warn("ffmpeg s'est arrêté de façon inattendue.\n" + tail);

        if (restart)
        {
            StatusChanged?.Invoke("Capture interrompue — redémarrage…");
            Thread.Sleep(1_000);
            try { Start(); }
            catch (Exception ex) { Error?.Invoke("Redémarrage de la capture impossible", ex.Message); }
        }
        else
        {
            StatusChanged?.Invoke("Capture arrêtée (crashs répétés)");
            Error?.Invoke("Capture arrêtée",
                "ffmpeg a crashé plusieurs fois d'affilée. Dernières erreurs :\n" + tail);
        }
    }

    private string BuildArgs(AppConfig cfg, string bufferDir, int startNumber)
    {
        var loop = _audio?.Loopback;
        var mic = _audio?.Mic;
        var a = new StringBuilder();
        a.Append("-hide_banner -loglevel warning -y ");
        // Vidéo : Desktop Duplication API, frames D3D11 consommées directement
        // par NVENC — aucun aller-retour CPU.
        a.Append($"-f lavfi -i ddagrab=output_idx={cfg.OutputIdx}:framerate={cfg.Fps} ");
        if (loop != null)
            a.Append($"-f {loop.SampleFmt} -ar {loop.Format.SampleRate} -ac {loop.Format.Channels} " +
                     $"-thread_queue_size 2048 -i \\\\.\\pipe\\{loop.PipeName} ");
        if (mic != null)
            a.Append($"-f {mic.SampleFmt} -ar {mic.Format.SampleRate} -ac {mic.Format.Channels} " +
                     $"-thread_queue_size 2048 -i \\\\.\\pipe\\{mic.PipeName} ");
        a.Append("-map 0:v ");
        if (loop != null) a.Append("-map 1:a ");
        if (mic != null) a.Append($"-map {(loop != null ? 2 : 1)}:a ");

        int gop = Math.Max(1, cfg.Fps * cfg.SegmentLengthS);
        a.Append($"-c:v {VideoEncoder} -rc vbr -cq {cfg.Cq} -b:v 0 -preset p4 -g {gop} ");
        // Keyframe forcée à chaque frontière de segment → coupes exactes du muxer segment.
        a.Append($"-force_key_frames expr:gte(t,n_forced*{cfg.SegmentLengthS}) ");
        if (VideoEncoder == "hevc_nvenc") a.Append("-tag:v hvc1 ");
        if (loop != null || mic != null) a.Append("-c:a aac -b:a 160k ");

        a.Append($"-f segment -segment_time {cfg.SegmentLengthS} -reset_timestamps 1 ");
        a.Append($"-segment_start_number {startNumber} ");
        a.Append($"\"{Path.Combine(bufferDir, "seg_%05d.mp4")}\"");
        return a.ToString();
    }

    // ---- Segments ----

    public static IEnumerable<SegmentFile> GetSegments(string bufferDir)
    {
        if (!Directory.Exists(bufferDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(bufferDir, "seg_*.mp4"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (int.TryParse(name.AsSpan(4), out var n))
                yield return new SegmentFile(f, n, File.GetLastWriteTimeUtc(f));
        }
    }

    /// <summary>
    /// Segments terminés, du plus ancien au plus récent. Le segment en cours
    /// d'écriture (dernier numéro) est exclu tant que ffmpeg tourne.
    /// </summary>
    public List<SegmentFile> GetCompletedSegments()
    {
        var list = GetSegments(BufferDir).OrderBy(s => s.Number).ToList();
        if (IsRunning && list.Count > 0)
            list.RemoveAt(list.Count - 1);
        return list;
    }

    public void ClearBuffer()
    {
        try
        {
            var dir = BufferDir;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "seg_*.mp4"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private void CleanupOldSegments()
    {
        try
        {
            var cfg = _cfg();
            var dir = ResolveBufferDir(cfg);
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, cfg.MaxBufferMinutes));
            foreach (var seg in GetSegments(dir))
            {
                if (seg.LastWriteUtc < cutoff)
                {
                    try { File.Delete(seg.Path); }
                    catch { /* verrouillé : au prochain passage */ }
                }
            }
        }
        catch (Exception ex) { Log.Warn("Nettoyage du buffer : " + ex.Message); }
    }

    /// <summary>
    /// Après un crash (de l'app ou de ffmpeg), le dernier segment peut être
    /// tronqué (moov jamais écrit) : il casserait un futur concat, on le supprime.
    /// </summary>
    private void DeleteTrailingInvalidSegment(string bufferDir)
    {
        try
        {
            var last = GetSegments(bufferDir).OrderBy(s => s.Number).LastOrDefault();
            if (last is null || FfmpegPath is null) return;
            var ffprobe = FfmpegLocator.FindProbe(FfmpegPath);
            if (ffprobe is null) return;
            var duration = ProcessUtil.ProbeDurationSeconds(ffprobe, last.Path);
            if (duration is null or <= 0)
            {
                Log.Warn($"Segment final invalide supprimé : {last.Path}");
                try { File.Delete(last.Path); } catch { }
            }
        }
        catch (Exception ex) { Log.Warn("Vérification du dernier segment : " + ex.Message); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _stopRequested = true;
        }
        _cleanupTimer.Dispose();
        StopProcess(graceful: true);
        _audio?.Dispose();
        _audio = null;
    }
}
