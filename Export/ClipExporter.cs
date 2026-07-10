using System.IO;
using ScreenClipTool.Capture;
using ScreenClipTool.Config;

namespace ScreenClipTool.Export;

public sealed record ExportResult(bool Success, string Message, string? Path, int Seconds)
{
    public static ExportResult Fail(string message) => new(false, message, null, 0);
    public static ExportResult Ok(string path, int seconds) => new(true, "OK", path, seconds);
}

/// <summary>
/// Export d'un clip : sélectionne les derniers segments couvrant la durée
/// demandée puis les concatène en copie de flux (aucun réencodage → quasi
/// instantané).
/// </summary>
public sealed class ClipExporter
{
    private readonly CaptureEngine _capture;
    private readonly Func<AppConfig> _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipExporter(CaptureEngine capture, Func<AppConfig> config)
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

        int segLen = Math.Max(1, cfg.SegmentLengthS);
        var segments = _capture.GetCompletedSegments();
        if (segments.Count == 0)
            return ExportResult.Fail("Rien à sauvegarder pour l'instant — laissez l'enregistrement tourner quelques secondes.");

        int count = duration.IsFull
            ? segments.Count
            : Math.Clamp((int)Math.Ceiling(duration.Seconds / (double)segLen), 1, segments.Count);
        var chosen = segments.GetRange(segments.Count - count, count);

        var listDir = Path.Combine(Path.GetTempPath(), "ScreenClipTool");
        Directory.CreateDirectory(listDir);
        var listPath = Path.Combine(listDir, $"concat_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(listPath, chosen.Select(s =>
            $"file '{s.Path.Replace('\\', '/').Replace("'", @"'\''")}'"));

        try
        {
            Directory.CreateDirectory(cfg.OutputDir);
            var outPath = UniqueOutputPath(cfg.OutputDir);
            var (code, _, stderr) = ProcessUtil.Run(ffmpeg,
                $"-hide_banner -v error -y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outPath}\"",
                120_000);
            if (code == 0 && File.Exists(outPath))
            {
                Log.Info($"Clip exporté : {outPath} ({count} segments ≈ {count * segLen} s)");
                return ExportResult.Ok(outPath, count * segLen);
            }
            var tail = stderr.Length > 400 ? stderr[^400..] : stderr;
            return ExportResult.Fail("L'assemblage du clip a échoué : " + tail.Trim());
        }
        finally
        {
            try { File.Delete(listPath); } catch { }
        }
    }

    private static string UniqueOutputPath(string outputDir)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(outputDir, $"Clip_{stamp}.mp4");
        int i = 2;
        while (File.Exists(path))
            path = Path.Combine(outputDir, $"Clip_{stamp}_{i++}.mp4");
        return path;
    }
}
