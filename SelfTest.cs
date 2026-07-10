using System.IO;
using System.Text;
using System.Text.Json;
using ScreenClipTool.Capture;
using ScreenClipTool.Config;
using ScreenClipTool.Export;

namespace ScreenClipTool;

/// <summary>
/// Mode --selftest [fichier_resultat.json] : valide le pipeline complet sans UI —
/// capture ~9 s (vidéo + audio loopback), export d'un clip de 5 s par concat,
/// vérification de la durée via ffprobe. Écrit un rapport JSON puis quitte.
/// </summary>
public static class SelfTest
{
    public static async Task RunAsync(string resultPath)
    {
        var log = new StringBuilder();
        void L(string s)
        {
            log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
            Log.Info("[selftest] " + s);
        }

        bool ok = false;
        string? clipPath = null;
        string? error = null;
        string? streams = null;
        long sizeBytes = 0;
        double durationSeconds = 0;

        var root = Path.Combine(Path.GetTempPath(), "ScreenClipTool", "selftest");
        CaptureEngine? capture = null;
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

            var cfg = new AppConfig
            {
                BufferDir = Path.Combine(root, "buffer"),
                OutputDir = Path.Combine(root, "out"),
                AudioEnabled = true,
                MicEnabled = false,
                MaxBufferMinutes = 5,
            };

            capture = new CaptureEngine(() => cfg);
            capture.StatusChanged += s => L("[statut] " + s);
            capture.Error += (t, m) => L($"[erreur] {t} : {m}");

            capture.Start();
            L($"Capture démarrée — encodeur : {capture.VideoEncoder}");
            await Task.Delay(9_000);

            var exporter = new ClipExporter(capture, () => cfg);
            var result = await exporter.ExportAsync(ClipDuration.FromSeconds(5));
            L($"Export : success={result.Success} message={result.Message} path={result.Path}");

            capture.Stop();
            L("Capture arrêtée proprement.");

            if (result.Success && result.Path is not null && File.Exists(result.Path))
            {
                clipPath = result.Path;
                sizeBytes = new FileInfo(result.Path).Length;
                var ffprobe = capture.FfmpegPath is null ? null : FfmpegLocator.FindProbe(capture.FfmpegPath);
                if (ffprobe != null)
                {
                    durationSeconds = ProcessUtil.ProbeDurationSeconds(ffprobe, result.Path) ?? 0;
                    var (code, stdout, _) = ProcessUtil.Run(ffprobe,
                        $"-v error -show_entries stream=codec_type,codec_name -of csv=p=0 \"{result.Path}\"");
                    if (code == 0) streams = stdout.Trim().Replace("\r", "");
                }
                L($"Clip : {sizeBytes} octets, {durationSeconds:0.##} s, flux : {streams?.Replace("\n", " | ")}");
                ok = sizeBytes > 50_000 && durationSeconds > 3.5;
            }
            else
            {
                error = result.Message;
            }
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            L("Exception : " + ex.Message);
            try { capture?.Stop(); } catch { }
        }

        var report = JsonSerializer.Serialize(new
        {
            ok,
            clip = clipPath,
            size_bytes = sizeBytes,
            duration_seconds = durationSeconds,
            streams,
            error,
            log = log.ToString(),
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(resultPath, report);
    }
}
