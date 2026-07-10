using System.Diagnostics;
using System.IO;

namespace ScreenClipTool;

/// <summary>Journal fichier minimal (%APPDATA%\ScreenClipTool\screencliptool.log).</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenClipTool", "screencliptool.log");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000)
                File.Delete(LogPath);
        }
        catch { /* le log ne doit jamais faire tomber l'app */ }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { }
        }
    }
}
