namespace ScreenClipTool.Config;

public class AppConfig
{
    /// <summary>Résolution native attendue (informatif : ddagrab capture toujours l'écran natif).</summary>
    public string Resolution { get; set; } = "2560x1440";

    public int Fps { get; set; } = 60;

    /// <summary>Qualité NVENC (CQ) : plus bas = meilleure qualité / fichiers plus gros.</summary>
    public int Cq { get; set; } = 27;

    public int SegmentLengthS { get; set; } = 2;

    /// <summary>Fenêtre max du buffer circulaire (borne le disque).</summary>
    public int MaxBufferMinutes { get; set; } = 15;

    public string OutputDir { get; set; } = "C:/Clips";

    public bool AudioEnabled { get; set; } = true;

    /// <summary>Micro capturé sur une piste audio séparée.</summary>
    public bool MicEnabled { get; set; } = false;

    /// <summary>Index du moniteur capturé (ddagrab output_idx).</summary>
    public int OutputIdx { get; set; } = 0;

    /// <summary>Dossier des segments ; null = %TEMP%\ScreenClipTool\buffer.</summary>
    public string? BufferDir { get; set; }

    /// <summary>Chemin de ffmpeg.exe ; null = détection auto (dossier de l'app, puis PATH).</summary>
    public string? FfmpegPath { get; set; }

    public List<HotkeyBinding> Hotkeys { get; set; } = new()
    {
        new HotkeyBinding { Modifiers = new List<string> { "Alt" }, Key = "X", DurationSeconds = ClipDuration.FromSeconds(15) },
        new HotkeyBinding { Modifiers = new List<string> { "Alt" }, Key = "C", DurationSeconds = ClipDuration.FromSeconds(600) },
        new HotkeyBinding { Modifiers = new List<string>(), Key = "F11", DurationSeconds = ClipDuration.Full },
    };

    /// <summary>Tri par durée croissante ("buffer complet" en dernier) — ordre d'affichage partout.</summary>
    public static List<HotkeyBinding> SortByDuration(IEnumerable<HotkeyBinding> bindings) =>
        bindings.OrderBy(b => b.DurationSeconds.IsFull ? long.MaxValue : b.DurationSeconds.Seconds).ToList();
}

public class HotkeyBinding
{
    /// <summary>Modificateurs : "Ctrl", "Alt", "Shift", "Win" (0 à n).</summary>
    public List<string> Modifiers { get; set; } = new();

    /// <summary>Nom de touche WPF (System.Windows.Input.Key) : "X", "F11", "OemComma"…</summary>
    public string Key { get; set; } = "";

    public ClipDuration DurationSeconds { get; set; } = ClipDuration.FromSeconds(15);

    public string Describe() => Format(Modifiers, Key);

    public static string Format(IEnumerable<string> modifiers, string key) =>
        string.Join("+", modifiers.Append(key));

    public string DescribeDuration()
    {
        if (DurationSeconds.IsFull) return "Tout l'historique";
        var s = DurationSeconds.Seconds;
        return s >= 60 && s % 60 == 0 ? $"{s / 60} min" : $"{s} s";
    }

    /// <summary>Deux bindings avec la même combinaison (indépendamment de l'ordre/casse des modificateurs).</summary>
    public bool SameCombo(HotkeyBinding other)
    {
        if (!Key.Equals(other.Key, StringComparison.OrdinalIgnoreCase)) return false;
        var a = Modifiers.Select(Normalize).ToHashSet();
        var b = other.Modifiers.Select(Normalize).ToHashSet();
        return a.SetEquals(b);
    }

    private static string Normalize(string modifier) => modifier.Trim().ToLowerInvariant() switch
    {
        "control" => "ctrl",
        "windows" => "win",
        var m => m,
    };
}
