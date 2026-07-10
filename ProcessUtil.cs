using System.Diagnostics;
using System.Globalization;

namespace ScreenClipTool;

public static class ProcessUtil
{
    /// <summary>Lance un exécutable sans fenêtre, capture stdout/stderr, avec timeout.</summary>
    public static (int ExitCode, string StdOut, string StdErr) Run(string exe, string args, int timeoutMs = 15_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Impossible de lancer {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit();
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Durée d'un fichier média via ffprobe, ou null si illisible.</summary>
    public static double? ProbeDurationSeconds(string ffprobePath, string mediaFile)
    {
        try
        {
            var (code, stdout, _) = Run(ffprobePath,
                $"-v error -show_entries format=duration -of csv=p=0 \"{mediaFile}\"", 10_000);
            if (code == 0 && double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        catch { }
        return null;
    }
}
