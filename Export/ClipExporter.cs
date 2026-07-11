using System.IO;
using ScreenClipTool.Capture;
using ScreenClipTool.Config;

namespace ScreenClipTool.Export;

public sealed record ExportResult(bool Success, string Message, string? Path, int Seconds, bool IsFolder)
{
    public static ExportResult Fail(string message) => new(false, message, null, 0, false);
    public static ExportResult Ok(string path, int seconds, bool isFolder = false) => new(true, "OK", path, seconds, isFolder);
}

/// <summary>
/// Export d'un clip : pour chaque écran capturé, sélectionne les derniers
/// segments couvrant la durée demandée et les concatène en copie de flux
/// (aucun réencodage → quasi instantané). Un seul écran → Clip_date.mp4 ;
/// plusieurs → dossier Clip_date contenant Screen1.mp4, Screen2.mp4…
/// </summary>
public sealed class ClipExporter
{
    private readonly CaptureManager _capture;
    private readonly Func<AppConfig> _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipExporter(CaptureManager capture, Func<AppConfig> config)
    {
        _capture = capture;
        _cfg = config;
    }

    public async Task<ExportResult> ExportAsync(ClipDuration duration)
    {
        if (!await _gate.WaitAsync(0))
            return ExportResult.Fail("Une sauvegarde est déjà en cours — réessayez dans un instant.");
        try
        {
            return await Task.Run(() => DoExport(duration));
        }
        finally { _gate.Release(); }
    }

    private ExportResult DoExport(ClipDuration duration)
    {
        var cfg = _cfg();
        var ffmpeg = _capture.FfmpegPath ?? FfmpegLocator.Find(cfg.FfmpegPath);
        if (ffmpeg is null)
            return ExportResult.Fail("FFmpeg est introuvable — impossible d'assembler le clip.");

        var sessions = _capture.Sessions;
        if (sessions.Count == 0)
            return ExportResult.Fail("L'enregistrement n'est pas démarré.");

        int segLen = Math.Max(1, cfg.SegmentLengthS);
        var plans = new List<(CaptureEngine Session, List<SegmentFile> Chosen)>();
        foreach (var session in sessions)
        {
            var segments = session.GetCompletedSegments();
            if (segments.Count == 0) continue;
            int count = duration.IsFull
                ? segments.Count
                : Math.Clamp((int)Math.Ceiling(duration.Seconds / (double)segLen), 1, segments.Count);
            plans.Add((session, segments.GetRange(segments.Count - count, count)));
        }
        if (plans.Count == 0)
            return ExportResult.Fail("Rien à sauvegarder pour l'instant — laissez l'enregistrement tourner quelques secondes.");

        Directory.CreateDirectory(cfg.OutputDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        int seconds = plans.Min(p => p.Chosen.Count) * segLen;

        if (plans.Count == 1)
        {
            var outPath = UniquePath(Path.Combine(cfg.OutputDir, $"Clip_{stamp}.mp4"));
            var error = ConcatSegments(ffmpeg, plans[0].Chosen, outPath);
            if (error != null) return ExportResult.Fail(error);
            Log.Info($"Clip exporté : {outPath} ({plans[0].Chosen.Count} segments ≈ {seconds} s)");
            return ExportResult.Ok(outPath, plans[0].Chosen.Count * segLen);
        }

        // Multi-écrans : un dossier qui porte le nom de base, une vidéo par écran.
        var folder = UniquePath(Path.Combine(cfg.OutputDir, $"Clip_{stamp}"));
        Directory.CreateDirectory(folder);
        var failures = new List<string>();
        foreach (var (session, chosen) in plans)
        {
            var outPath = Path.Combine(folder, $"Screen{session.ScreenIndex + 1}.mp4");
            var error = ConcatSegments(ffmpeg, chosen, outPath);
            if (error != null) failures.Add($"Screen{session.ScreenIndex + 1} — {error}");
        }
        if (failures.Count == plans.Count)
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
            return ExportResult.Fail(string.Join("\n", failures));
        }
        Log.Info($"Clips exportés : {folder} ({plans.Count} écrans ≈ {seconds} s)" +
                 (failures.Count > 0 ? " — échecs partiels : " + string.Join(" ; ", failures) : ""));
        return ExportResult.Ok(folder, seconds, isFolder: true);
    }

    /// <summary>Concat en -c copy ; renvoie null si OK, sinon le message d'erreur.</summary>
    private static string? ConcatSegments(string ffmpeg, List<SegmentFile> segments, string outPath)
    {
        var listDir = Path.Combine(Path.GetTempPath(), "ScreenClipTool");
        Directory.CreateDirectory(listDir);
        var listPath = Path.Combine(listDir, $"concat_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(listPath, segments.Select(s =>
            $"file '{s.Path.Replace('\\', '/').Replace("'", @"'\''")}'"));
        try
        {
            var (code, _, stderr) = ProcessUtil.Run(ffmpeg,
                $"-hide_banner -v error -y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outPath}\"",
                120_000);
            if (code == 0 && File.Exists(outPath))
                return null;
            var tail = stderr.Length > 400 ? stderr[^400..] : stderr;
            return "L'assemblage du clip a échoué : " + tail.Trim();
        }
        finally
        {
            try { File.Delete(listPath); } catch { }
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int i = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{name}_{i++}{ext}"); }
        while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }
}
