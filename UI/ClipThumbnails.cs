using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ScreenClipTool.UI;

/// <summary>
/// Miniatures des clips : une image extraite par ffmpeg, mise en cache dans
/// %TEMP%\ScreenClipTool\thumbs (clé = chemin + date + taille du clip, donc
/// invalidée automatiquement si le fichier change).
/// </summary>
public static class ClipThumbnails
{
    private static readonly string CacheDir =
        Path.Combine(Path.GetTempPath(), "ScreenClipTool", "thumbs");

    /// <summary>Largeur des vignettes générées ; fait partie de la clé de cache,
    /// donc l'augmenter régénère automatiquement les anciennes miniatures.</summary>
    private const int TargetWidth = 640;

    private static bool _pruned;

    /// <summary>Renvoie le chemin du jpg de la miniature, ou null si échec.</summary>
    public static string? GetOrCreate(string ffmpegPath, string clipPath)
    {
        try
        {
            var fi = new FileInfo(clipPath);
            if (!fi.Exists) return null;
            Directory.CreateDirectory(CacheDir);
            PruneOnce();

            var key = Convert.ToHexString(MD5.HashData(
                Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{TargetWidth}")));
            var thumb = Path.Combine(CacheDir, key + ".jpg");
            if (File.Exists(thumb)) return thumb;

            // Image à ~0,3 s (évite une éventuelle première frame noire),
            // nouvel essai à 0 s pour les clips très courts.
            var (code, _, _) = ProcessUtil.Run(ffmpegPath,
                $"-hide_banner -v error -y -ss 0.3 -i \"{clipPath}\" -frames:v 1 -vf scale={TargetWidth}:-2 -q:v 4 \"{thumb}\"",
                15_000);
            if (code != 0 || !File.Exists(thumb))
            {
                (code, _, _) = ProcessUtil.Run(ffmpegPath,
                    $"-hide_banner -v error -y -i \"{clipPath}\" -frames:v 1 -vf scale={TargetWidth}:-2 -q:v 4 \"{thumb}\"",
                    15_000);
            }
            return code == 0 && File.Exists(thumb) ? thumb : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Miniature : " + ex.Message);
            return null;
        }
    }

    /// <summary>Purge les miniatures orphelines de plus de 90 jours (une fois par session).</summary>
    private static void PruneOnce()
    {
        if (_pruned) return;
        _pruned = true;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-90);
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.jpg"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
