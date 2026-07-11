namespace ScreenClipTool.Capture;

/// <summary>
/// Un encodeur vidéo candidat. <see cref="DirectD3D11"/> : accepte directement
/// les frames GPU de ddagrab (pipeline sans copie CPU) ; sinon les frames sont
/// rapatriées en RAM (hwdownload) avant encodage.
/// </summary>
public sealed record EncoderChoice(string Name, string Family, bool DirectD3D11)
{
    public bool IsHevc => Name.Contains("hevc", StringComparison.Ordinal);
    public bool IsSoftware => Family == "x264";
}

public sealed record FfmpegCaps(bool HasDdagrab, EncoderChoice? Encoder)
{
    public bool IsUsable => HasDdagrab && Encoder is not null;
}

/// <summary>
/// Détection des capacités d'un build ffmpeg : filtre ddagrab (capture), puis
/// choix du meilleur encodeur par un test d'encodage réel — un encodeur listé
/// n'est retenu que si le matériel/pilote le fait effectivement tourner.
/// </summary>
public static class FfmpegCapabilities
{
    // Du plus efficace au plus universel : NVENC (NVIDIA), AMF (AMD),
    // QSV (Intel), puis encodage logiciel x264 — fonctionne sur n'importe
    // quelle machine, au prix d'une charge CPU plus élevée.
    private static readonly EncoderChoice[] Candidates =
    {
        new("av1_nvenc", "nvenc", DirectD3D11: true),
        new("hevc_nvenc", "nvenc", DirectD3D11: true),
        new("h264_nvenc", "nvenc", DirectD3D11: true),
        new("hevc_amf", "amf", DirectD3D11: false),
        new("h264_amf", "amf", DirectD3D11: false),
        new("av1_qsv", "qsv", DirectD3D11: false),
        new("hevc_qsv", "qsv", DirectD3D11: false),
        new("h264_qsv", "qsv", DirectD3D11: false),
        new("libx264", "x264", DirectD3D11: false),
    };

    private static readonly Dictionary<string, FfmpegCaps> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    public static FfmpegCaps Probe(string ffmpegPath)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(ffmpegPath, out var cached))
                return cached;

            var filters = SafeRun(ffmpegPath, "-hide_banner -filters");
            var encoders = SafeRun(ffmpegPath, "-hide_banner -encoders");
            bool ddagrab = filters.Contains("ddagrab");

            EncoderChoice? chosen = null;
            if (ddagrab)
            {
                foreach (var candidate in Candidates)
                {
                    if (!encoders.Contains(candidate.Name)) continue;
                    if (TestEncode(ffmpegPath, candidate.Name))
                    {
                        chosen = candidate;
                        break;
                    }
                    Log.Info($"Encodeur {candidate.Name} listé mais inopérant sur cette machine — candidat suivant.");
                }
            }

            var caps = new FfmpegCaps(ddagrab, chosen);
            Cache[ffmpegPath] = caps;
            Log.Info($"Capacités ffmpeg ({ffmpegPath}) : ddagrab={ddagrab}, encodeur retenu={chosen?.Name ?? "aucun"}");
            return caps;
        }
    }

    /// <summary>Encode réellement quelques frames : valide encodeur + pilote + matériel.</summary>
    private static bool TestEncode(string ffmpegPath, string encoder)
    {
        try
        {
            var (code, _, _) = ProcessUtil.Run(ffmpegPath,
                $"-hide_banner -v error -f lavfi -i color=black:s=1280x720:r=30:d=0.2 -c:v {encoder} -frames:v 3 -f null -",
                20_000);
            return code == 0;
        }
        catch { return false; }
    }

    private static string SafeRun(string exe, string args)
    {
        try
        {
            var (_, stdout, stderr) = ProcessUtil.Run(exe, args);
            return stdout + stderr;
        }
        catch (Exception ex)
        {
            Log.Warn($"Probe ffmpeg impossible ({args}) : {ex.Message}");
            return "";
        }
    }
}
