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
        CaptureManager? capture = null;
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

            // Multi-écrans testé pour de vrai quand la machine a plusieurs moniteurs.
            int screenCount = System.Windows.Forms.Screen.AllScreens.Length;
            var cfg = new AppConfig
            {
                BufferDir = Path.Combine(root, "buffer"),
                OutputDir = Path.Combine(root, "out"),
                AudioEnabled = true,
                MicEnabled = false,
                MaxBufferMinutes = 5,
                Screens = screenCount > 1 ? new List<int> { 0, 1 } : new List<int> { 0 },
            };
            L($"Écrans utilisés : {cfg.Screens.Count}/{screenCount}");

            capture = new CaptureManager(() => cfg);
            capture.StatusChanged += s => L("[statut] " + s);
            capture.Error += (t, m) => L($"[erreur] {t} : {m}");

            capture.Start();
            L($"Capture démarrée — encodeur : {capture.Sessions.FirstOrDefault()?.VideoEncoder}");
            await Task.Delay(9_000);

            var exporter = new ClipExporter(capture, () => cfg);
            var result = await exporter.ExportAsync(ClipDuration.FromSeconds(5));
            L($"Export : success={result.Success} message={result.Message} path={result.Path}");

            capture.Stop();
            L("Capture arrêtée proprement.");

            if (result.Success && result.Path is not null)
            {
                clipPath = result.Path;
                // Multi-écrans : un dossier ScreenN.mp4 ; sinon un fichier unique.
                var videos = result.IsFolder
                    ? Directory.GetFiles(result.Path, "*.mp4").OrderBy(f => f).ToList()
                    : new List<string> { result.Path };
                sizeBytes = videos.Sum(v => new FileInfo(v).Length);
                var ffprobe = capture.FfmpegPath is null ? null : FfmpegLocator.FindProbe(capture.FfmpegPath);
                if (ffprobe != null && videos.Count > 0)
                {
                    durationSeconds = ProcessUtil.ProbeDurationSeconds(ffprobe, videos[0]) ?? 0;
                    var (code, stdout, _) = ProcessUtil.Run(ffprobe,
                        $"-v error -show_entries stream=codec_type,codec_name -of csv=p=0 \"{videos[0]}\"");
                    if (code == 0) streams = stdout.Trim().Replace("\r", "");
                }
                L($"Vidéos : {videos.Count} ({string.Join(", ", videos.Select(Path.GetFileName))}), " +
                  $"{sizeBytes} octets, {durationSeconds:0.##} s, flux : {streams?.Replace("\n", " | ")}");
                ok = videos.Count == cfg.Screens.Count
                     && videos.All(v => new FileInfo(v).Length > 50_000)
                     && durationSeconds > 3.5;
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
