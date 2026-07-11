using System.IO;

namespace ScreenClipTool.Capture;

public static class FfmpegLocator
{
    /// <summary>Premier ffmpeg.exe trouvé : chemin configuré → dossier de l'app → PATH → installation gérée.</summary>
    public static string? Find(string? configuredPath) => FindAll(configuredPath).FirstOrDefault();

    /// <summary>
    /// Tous les ffmpeg.exe présents, par ordre de préférence. Un exe trouvé plus
    /// tôt peut être inutilisable (vieux build d'un autre logiciel dans le PATH,
    /// sans capture ni encodeur) : l'appelant essaie chaque candidat.
    /// </summary>
    public static IEnumerable<string> FindAll(string? configuredPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            var full = Path.GetFullPath(configuredPath);
            if (seen.Add(full)) yield return full;
        }

        var appDirCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
        };
        foreach (var c in appDirCandidates)
            if (File.Exists(c) && seen.Add(c)) yield return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string p;
            try { p = Path.Combine(dir.Trim(), "ffmpeg.exe"); }
            catch { continue; /* entrée PATH invalide */ }
            if (File.Exists(p) && seen.Add(p)) yield return p;
        }

        // Installation gérée (téléchargée automatiquement au premier lancement)
        if (FfmpegInstaller.FindInstalled() is { } managed && seen.Add(managed))
            yield return managed;
    }

    /// <summary>ffprobe.exe attendu à côté de ffmpeg.exe, ou null.</summary>
    public static string? FindProbe(string ffmpegPath)
    {
        var p = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        return File.Exists(p) ? p : null;
    }
}
