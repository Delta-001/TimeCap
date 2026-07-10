using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenClipTool.Config;

/// <summary>
/// Chargement/sauvegarde de config.json.
/// Mode portable : si un config.json existe à côté de l'exe il est utilisé,
/// sinon %APPDATA%\ScreenClipTool\config.json.
/// </summary>
public class ConfigService
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string ConfigPath { get; }

    public ConfigService()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "config.json");
        ConfigPath = File.Exists(portable)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "ScreenClipTool", "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
                loaded.Hotkeys = AppConfig.SortByDuration(loaded.Hotkeys);
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"config.json illisible ({ex.Message}) — sauvegarde en .bak et retour aux valeurs par défaut.");
            try { File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true); } catch { }
        }
        var cfg = new AppConfig();
        try { Save(cfg); } catch (Exception ex) { Log.Warn("Impossible d'écrire la config par défaut : " + ex.Message); }
        return cfg;
    }

    public void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        Log.Info("Config enregistrée : " + ConfigPath);
    }

    public AppConfig Clone(AppConfig cfg) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg, JsonOpts), JsonOpts)!;
}
