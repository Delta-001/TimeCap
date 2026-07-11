using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ScreenClipTool.UI;

/// <summary>
/// Opérations du menu contextuel des clips : compression à taille cible,
/// conversion GIF, extraction d'image, hébergement temporaire pour partage
/// par lien. Toutes s'appuient sur le ffmpeg déjà installé.
/// </summary>
public static class ClipTools
{
    /// <summary>Réencode vers une taille cible (H264+AAC, lisible partout). Renvoie le fichier produit.</summary>
    public static async Task<string> CompressAsync(string ffmpeg, string ffprobe, string file, double targetMB)
    {
        double duration = ProcessUtil.ProbeDurationSeconds(ffprobe, file)
            ?? throw new InvalidOperationException("Durée du clip illisible.");
        // Budget en kbps avec 8 % de marge de mux, audio d'abord.
        double totalKbps = targetMB * 8192 / Math.Max(1, duration) * 0.92;
        int audioKbps = totalKbps > 400 ? 96 : 64;
        int videoKbps = Math.Max(80, (int)(totalKbps - audioKbps));
        var output = UniquePath(Path.Combine(
            Path.GetDirectoryName(file)!,
            Path.GetFileNameWithoutExtension(file) + "_discord.mp4"));

        return await Task.Run(() =>
        {
            var (code, _, stderr) = ProcessUtil.Run(ffmpeg,
                $"-hide_banner -v error -y -i \"{file}\" " +
                $"-c:v libx264 -preset fast -b:v {videoKbps}k -maxrate {videoKbps}k -bufsize {videoKbps * 2}k " +
                "-vf \"scale='min(1280,iw)':-2\" " +
                $"-c:a aac -b:a {audioKbps}k -movflags +faststart \"{output}\"",
                600_000);
            if (code != 0 || !File.Exists(output))
                throw new InvalidOperationException("La compression a échoué : " + Tail(stderr));
            return output;
        });
    }

    /// <summary>Convertit (les 30 premières secondes max) en GIF à palette optimisée.</summary>
    public static async Task<string> ToGifAsync(string ffmpeg, string file)
    {
        var output = UniquePath(Path.Combine(
            Path.GetDirectoryName(file)!,
            Path.GetFileNameWithoutExtension(file) + ".gif"));
        return await Task.Run(() =>
        {
            var (code, _, stderr) = ProcessUtil.Run(ffmpeg,
                $"-hide_banner -v error -y -t 30 -i \"{file}\" " +
                "-vf \"fps=15,scale=480:-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" " +
                $"\"{output}\"",
                600_000);
            if (code != 0 || !File.Exists(output))
                throw new InvalidOperationException("La conversion GIF a échoué : " + Tail(stderr));
            return output;
        });
    }

    /// <summary>Extrait une image PNG pleine résolution (~0,3 s dans le clip).</summary>
    public static async Task<string> ExtractFrameAsync(string ffmpeg, string file)
    {
        var output = Path.Combine(Path.GetTempPath(), "ScreenClipTool",
            $"frame_{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        return await Task.Run(() =>
        {
            var (code, _, _) = ProcessUtil.Run(ffmpeg,
                $"-hide_banner -v error -y -ss 0.3 -i \"{file}\" -frames:v 1 \"{output}\"", 60_000);
            if (code != 0 || !File.Exists(output))
            {
                (code, _, _) = ProcessUtil.Run(ffmpeg,
                    $"-hide_banner -v error -y -i \"{file}\" -frames:v 1 \"{output}\"", 60_000);
            }
            if (code != 0 || !File.Exists(output))
                throw new InvalidOperationException("Extraction de l'image impossible.");
            return output;
        });
    }

    private const long UploadLimitBytes = 1L << 30; // 1 Go (limite Litterbox)

    /// <summary>
    /// Héberge le clip sur Litterbox (service temporaire de catbox.moe, sans
    /// compte) et renvoie le lien, valable 72 h. Le fichier part sur un serveur
    /// tiers : réservé à ce que l'utilisateur veut partager.
    /// </summary>
    public static async Task<string> UploadForLinkAsync(string file, IProgress<string>? status)
    {
        var info = new FileInfo(file);
        if (info.Length > UploadLimitBytes)
            throw new InvalidOperationException(
                "Clip trop volumineux pour le lien (limite : 1 Go) — compressez-le d'abord.");

        status?.Report("Envoi du clip…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        await using var stream = File.OpenRead(file);
        using var content = new MultipartFormDataContent
        {
            { new StringContent("fileupload"), "reqtype" },
            { new StringContent("72h"), "time" },
            { new StreamContent(stream), "fileToUpload", Path.GetFileName(file) },
        };
        var response = await http.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", content);
        response.EnsureSuccessStatusCode();
        var url = (await response.Content.ReadAsStringAsync()).Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Réponse inattendue de l'hébergeur : " + Tail(url));
        Log.Info($"Clip hébergé (72 h) : {url}");
        return url;
    }

    public static string UniquePath(string path)
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

    private static string Tail(string s) => s.Length > 300 ? s[^300..] : s;
}

/// <summary>Clips épinglés (affichés en tête de galerie), par nom de fichier/dossier.</summary>
public static class PinStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenClipTool", "pinned.json");

    private static HashSet<string>? _names;

    private static HashSet<string> Names
    {
        get
        {
            if (_names is null)
            {
                try
                {
                    _names = File.Exists(StorePath)
                        ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StorePath))
                          ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                catch { _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
            }
            return _names;
        }
    }

    public static bool IsPinned(string name) => Names.Contains(name);

    public static void Toggle(string name)
    {
        if (!Names.Remove(name)) Names.Add(name);
        Save();
    }

    public static void Rename(string oldName, string newName)
    {
        if (Names.Remove(oldName))
        {
            Names.Add(newName);
            Save();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Names));
        }
        catch (Exception ex) { Log.Warn("Sauvegarde des épingles : " + ex.Message); }
    }
}
