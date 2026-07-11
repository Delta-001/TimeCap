using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenClipTool.Audio;
using ScreenClipTool.Config;

namespace ScreenClipTool.Capture;

public sealed record SegmentFile(string Path, int Number, DateTime LastWriteUtc);

/// <summary>
/// Session de capture continue d'UN écran : lance et surveille le process
/// ffmpeg (ddagrab → encodeur → segments mp4 de N secondes), redémarre en cas
/// de crash, et borne le disque en supprimant les segments plus vieux que la
/// fenêtre max. Plusieurs écrans = plusieurs sessions (voir CaptureManager).
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly Func<AppConfig> _cfg;
    private readonly int _screen;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _cleanupTimer;
    private readonly Queue<DateTime> _recentRestarts = new();
    private readonly Queue<string> _stderrTail = new();

    private Process? _proc;
    private AudioCaptureService? _audio;
    private bool _stopRequested;
    private bool _disposed;
    private bool _audioDisabledForSession;

    private EncoderChoice? _encoder;

    public string? FfmpegPath { get; private set; }
    public string? VideoEncoder => _encoder?.Name;
    public int ScreenIndex => _screen;

    /// <summary>Dossier des segments de cette session (sous-dossier par écran).</summary>
    public string BufferDir => Path.Combine(ResolveBufferBase(_cfg()), $"screen{_screen}");

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    /// <summary>Texte de statut court (affiché dans le tray).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Erreur à notifier (titre, message).</summary>
    public event Action<string, string>? Error;

    public CaptureEngine(Func<AppConfig> config, int screenIndex = 0)
    {
        _cfg = config;
        _screen = screenIndex;
        _cleanupTimer = new System.Timers.Timer(15_000) { AutoReset = true };
        _cleanupTimer.Elapsed += (_, _) => CleanupOldSegments();
    }

    /// <summary>Racine du buffer (chaque session écrit dans son sous-dossier screenN).</summary>
    public static string ResolveBufferBase(AppConfig cfg) =>
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
            (FfmpegPath, _encoder) = SelectFfmpeg(cfg);

            var bufferDir = BufferDir;
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
                    StatusChanged?.Invoke("Son du PC indisponible — enregistrement vidéo uniquement");
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
                    catch (Exception ex2) { Error?.Invoke("Enregistrement impossible", ex2.Message); }
                });
                return;
            }

            _cleanupTimer.Start();
            // Détail technique (encodeur) réservé au log ; statut grand public.
            var desc = _audio?.Loopback != null
                ? (_audio.Mic != null ? "vidéo + son + micro" : "vidéo + son")
                : "vidéo seule";
            if (_encoder is { IsSoftware: true }) desc += ", encodage logiciel";
            StatusChanged?.Invoke($"Enregistrement en cours ({desc})");
        }
    }

    /// <summary>
    /// Essaie chaque ffmpeg présent (config → dossier de l'app → PATH →
    /// installation gérée) et retient le premier qui sait à la fois capturer
    /// l'écran et encoder sur cette machine. Un vieux ffmpeg d'un autre
    /// logiciel dans le PATH ne bloque donc plus l'application.
    /// </summary>
    private static (string Path, EncoderChoice Encoder) SelectFfmpeg(AppConfig cfg)
    {
        bool foundAny = false;
        foreach (var path in FfmpegLocator.FindAll(cfg.FfmpegPath))
        {
            foundAny = true;
            var caps = FfmpegCapabilities.Probe(path);
            if (caps.IsUsable)
                return (path, caps.Encoder!);
            Log.Warn($"ffmpeg inutilisable ({path}) : ddagrab={caps.HasDdagrab}, encodeur={caps.Encoder?.Name ?? "aucun"}");
        }

        // Rien d'utilisable : si l'installation gérée n'existe pas encore, elle
        // peut débloquer la situation (build complet et récent) — on la demande.
        if (FfmpegInstaller.FindInstalled() is null)
            throw new FfmpegMissingException();

        throw new InvalidOperationException(foundAny
            ? "Impossible de démarrer l'enregistrement : aucun encodeur vidéo ne fonctionne sur cette machine. " +
              "Mettez à jour vos pilotes graphiques puis relancez l'application."
            : "Le moteur vidéo est introuvable. Relancez l'application pour retenter l'installation automatique.");
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
        StatusChanged?.Invoke("Enregistrement arrêté");
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
            StatusChanged?.Invoke("Enregistrement interrompu — reprise automatique…");
            Thread.Sleep(1_000);
            try { Start(); }
            catch (Exception ex) { Error?.Invoke("Reprise de l'enregistrement impossible", ex.Message); }
        }
        else
        {
            StatusChanged?.Invoke("Enregistrement arrêté (échecs répétés)");
            Error?.Invoke("Enregistrement arrêté",
                "Le moteur vidéo s'est arrêté plusieurs fois d'affilée. Détails :\n" + tail);
        }
    }

    private string BuildArgs(AppConfig cfg, string bufferDir, int startNumber)
    {
        var loop = _audio?.Loopback;
        var mic = _audio?.Mic;
        var a = new StringBuilder();
        a.Append("-hide_banner -loglevel warning -y ");
        // Vidéo : Desktop Duplication API de l'écran de cette session.
        a.Append($"-f lavfi -i ddagrab=output_idx={_screen}:framerate={cfg.Fps} ");
        if (loop != null)
            a.Append($"-f {loop.SampleFmt} -ar {loop.Format.SampleRate} -ac {loop.Format.Channels} " +
                     $"-thread_queue_size 2048 -i \\\\.\\pipe\\{loop.PipeName} ");
        if (mic != null)
            a.Append($"-f {mic.SampleFmt} -ar {mic.Format.SampleRate} -ac {mic.Format.Channels} " +
                     $"-thread_queue_size 2048 -i \\\\.\\pipe\\{mic.PipeName} ");
        a.Append("-map 0:v ");
        if (loop != null) a.Append("-map 1:a ");
        if (mic != null) a.Append($"-map {(loop != null ? 2 : 1)}:a ");

        var enc = _encoder!;
        // Les encodeurs non-NVENC ne consomment pas les frames D3D11 de ddagrab :
        // rapatriement en RAM + conversion NV12 (copie CPU, mais universel).
        if (!enc.DirectD3D11)
            a.Append("-vf hwdownload,format=bgra,format=nv12 ");

        int gop = Math.Max(1, cfg.Fps * cfg.SegmentLengthS);
        a.Append($"-c:v {enc.Name} ");
        // Qualité constante ~équivalente (échelle 0-51) selon la famille d'encodeur.
        a.Append(enc.Family switch
        {
            "nvenc" => $"-rc vbr -cq {cfg.Cq} -b:v 0 -preset p4 ",
            "amf" => $"-rc cqp -qp_i {cfg.Cq} -qp_p {cfg.Cq} ",
            "qsv" => $"-global_quality {cfg.Cq} ",
            _ => $"-crf {cfg.Cq} -preset veryfast ",
        });
        a.Append($"-g {gop} ");
        // Keyframe forcée à chaque frontière de segment → coupes exactes du muxer segment.
        a.Append($"-force_key_frames expr:gte(t,n_forced*{cfg.SegmentLengthS}) ");
        if (enc.IsHevc) a.Append("-tag:v hvc1 ");
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
            var dir = BufferDir;
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
