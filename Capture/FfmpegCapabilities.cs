namespace ScreenClipTool.Capture;

public sealed record FfmpegCaps(bool HasDdagrab, bool HasAv1Nvenc, bool HasHevcNvenc, string? BestNvencEncoder);

/// <summary>
/// Détection des capacités du build ffmpeg : filtre ddagrab, puis choix de
/// l'encodeur — av1_nvenc si un test d'encodage réel réussit (le GPU doit le
/// supporter, pas seulement le build), sinon fallback hevc_nvenc.
/// </summary>
public static class FfmpegCapabilities
{
    private static FfmpegCaps? _cached;
    private static string? _cachedPath;
    private static readonly object Lock = new();

    public static FfmpegCaps Probe(string ffmpegPath)
    {
        lock (Lock)
        {
            if (_cached != null && _cachedPath == ffmpegPath)
                return _cached;

            var (_, _, filters) = SafeRun(ffmpegPath, "-hide_banner -filters");
            var (_, _, encoders) = SafeRun(ffmpegPath, "-hide_banner -encoders");
            // ffmpeg écrit ces listings sur stdout ; SafeRun concatène stdout+stderr.
            bool ddagrab = filters.Contains("ddagrab");
            bool av1 = encoders.Contains("av1_nvenc");
            bool hevc = encoders.Contains("hevc_nvenc");

            string? best = null;
            if (av1 && TestEncode(ffmpegPath, "av1_nvenc")) best = "av1_nvenc";
            else if (hevc && TestEncode(ffmpegPath, "hevc_nvenc")) best = "hevc_nvenc";

            _cached = new FfmpegCaps(ddagrab, av1, hevc, best);
            _cachedPath = ffmpegPath;
            Log.Info($"Capacités ffmpeg : ddagrab={ddagrab}, av1_nvenc={av1}, hevc_nvenc={hevc}, encodeur retenu={best ?? "aucun"}");
            return _cached;
        }
    }

    private static bool TestEncode(string ffmpegPath, string encoder)
    {
        try
        {
            var (code, _, _) = ProcessUtil.Run(ffmpegPath,
                $"-hide_banner -v error -f lavfi -i color=black:s=320x240:r=30:d=0.2 -c:v {encoder} -frames:v 3 -f null -",
                20_000);
            return code == 0;
        }
        catch { return false; }
    }

    private static (int Code, string Out, string Combined) SafeRun(string exe, string args)
    {
        try
        {
            var (code, stdout, stderr) = ProcessUtil.Run(exe, args);
            return (code, stdout, stdout + stderr);
        }
        catch (Exception ex)
        {
            Log.Warn($"Probe ffmpeg impossible ({args}) : {ex.Message}");
            return (-1, "", "");
        }
    }
}
