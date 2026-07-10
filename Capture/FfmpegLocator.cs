using System.IO;

namespace ScreenClipTool.Capture;

public static class FfmpegLocator
{
    /// <summary>Cherche ffmpeg.exe : chemin configuré → dossier de l'app → PATH → installation gérée.</summary>
    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return Path.GetFullPath(configuredPath);

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* entrée PATH invalide */ }
        }

        // Installation gérée (téléchargée automatiquement au premier lancement)
        return FfmpegInstaller.FindInstalled();
    }

    /// <summary>ffprobe.exe attendu à côté de ffmpeg.exe, ou null.</summary>
    public static string? FindProbe(string ffmpegPath)
    {
        var p = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        return File.Exists(p) ? p : null;
    }
}
