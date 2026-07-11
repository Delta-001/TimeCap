using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace ScreenClipTool.Capture;

/// <summary>
/// Aucun ffmpeg utilisable sur la machine et l'installation gérée n'existe pas
/// encore : signale à l'app hôte de lancer l'installation automatique.
/// </summary>
public sealed class FfmpegMissingException : Exception
{
    public FfmpegMissingException() : base("FFmpeg absent ou inutilisable — installation automatique requise.") { }
}

/// <summary>
/// Installation automatique du moteur vidéo au premier lancement : télécharge
/// le build « essentials » de gyan.dev (~30 Mo, inclut ddagrab + NVENC), en
/// extrait ffmpeg.exe / ffprobe.exe vers %LOCALAPPDATA%\TimeCap\ffmpeg, et
/// vérifie que le binaire démarre. Rend l'application plug-and-play : aucun
/// prérequis à installer à la main.
/// </summary>
public static class FfmpegInstaller
{
    /// <summary>Sources essayées dans l'ordre : la première joignable gagne.</summary>
    public static readonly string[] DownloadUrls =
    {
        // Build « essentials » de gyan.dev (~31 Mo) — la source documentée du projet
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        // Miroir de secours : builds officiels BtbN sur GitHub (~160 Mo, URL stable)
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip",
    };

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimeCap", "ffmpeg");

    /// <summary>ffmpeg.exe de l'installation gérée, ou null s'il n'est pas encore installé.</summary>
    public static string? FindInstalled()
    {
        var p = Path.Combine(InstallDir, "ffmpeg.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// Télécharge et installe ffmpeg. <paramref name="status"/> reçoit des
    /// messages de progression grand public. Lève une exception en cas d'échec
    /// (pas de connexion, archive inattendue, binaire invalide).
    /// </summary>
    public static async Task<string> InstallAsync(IProgress<string>? status, CancellationToken ct = default)
    {
        Directory.CreateDirectory(InstallDir);
        var zipPath = Path.Combine(InstallDir, "ffmpeg_download.zip");
        try
        {
            Exception? lastError = null;
            foreach (var url in DownloadUrls)
            {
                try
                {
                    await DownloadAsync(url, zipPath, status, ct);
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                           && !ct.IsCancellationRequested)
                {
                    Log.Warn($"Source indisponible ({url}) : {ex.Message}");
                    lastError = ex;
                }
            }
            if (lastError != null)
                throw new HttpRequestException("Aucune source de téléchargement accessible.", lastError);

            status?.Report("Installation du moteur vidéo…");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        if (name == "ffmpeg.exe")
                            throw new InvalidOperationException("Archive inattendue : ffmpeg.exe absent du téléchargement.");
                        continue; // ffprobe est un bonus (miniatures), pas bloquant
                    }
                    entry.ExtractToFile(Path.Combine(InstallDir, name), overwrite: true);
                }
            }

            var ffmpeg = Path.Combine(InstallDir, "ffmpeg.exe");
            var (exitCode, _, _) = ProcessUtil.Run(ffmpeg, "-version");
            if (exitCode != 0)
                throw new InvalidOperationException("Le moteur vidéo téléchargé ne démarre pas.");

            Log.Info("ffmpeg installé : " + ffmpeg);
            return ffmpeg;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    private static async Task DownloadAsync(string url, string zipPath, IProgress<string>? status, CancellationToken ct)
    {
        Log.Info($"Téléchargement de ffmpeg : {url}");
        status?.Report("Téléchargement du moteur vidéo…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(zipPath);
        var buffer = new byte[81920];
        long done = 0;
        int read, lastPercent = -1;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                int percent = (int)(done * 100 / total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    status?.Report($"Téléchargement du moteur vidéo… {percent} %");
                }
            }
        }
    }
}
