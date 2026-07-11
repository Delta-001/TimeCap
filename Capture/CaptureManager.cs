using System.IO;
using ScreenClipTool.Config;

namespace ScreenClipTool.Capture;

/// <summary>
/// Orchestre une session de capture par écran sélectionné dans la config.
/// Expose la même surface que la session unique (statut, erreurs, start/stop),
/// et donne accès aux sessions pour l'export (un clip par écran).
/// </summary>
public sealed class CaptureManager : IDisposable
{
    private readonly Func<AppConfig> _cfg;
    private readonly object _lock = new();
    private readonly List<CaptureEngine> _sessions = new();
    private bool _disposed;

    /// <summary>Statut agrégé (celui de la première session, annoté du nombre d'écrans).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Erreur d'une session, préfixée de l'écran concerné en multi-écrans.</summary>
    public event Action<string, string>? Error;

    public CaptureManager(Func<AppConfig> config) => _cfg = config;

    public IReadOnlyList<CaptureEngine> Sessions
    {
        get { lock (_lock) return _sessions.ToList(); }
    }

    public string? FfmpegPath => Sessions.FirstOrDefault()?.FfmpegPath;

    public bool IsRunning => Sessions.Any(s => s.IsRunning);

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureManager));
            TearDownSessions();

            var screens = _cfg().EffectiveScreens.Distinct().OrderBy(i => i).ToList();
            for (int n = 0; n < screens.Count; n++)
            {
                var engine = new CaptureEngine(_cfg, screens[n]);
                bool primary = n == 0;
                int humanIndex = screens[n] + 1;
                engine.StatusChanged += s =>
                {
                    if (primary)
                        StatusChanged?.Invoke(screens.Count > 1 ? $"{s} — {screens.Count} écrans" : s);
                };
                engine.Error += (title, message) =>
                    Error?.Invoke(screens.Count > 1 ? $"Écran {humanIndex} — {title}" : title, message);
                _sessions.Add(engine);
            }

            // La première session lève FfmpegMissingException si aucun ffmpeg
            // utilisable n'existe : l'app hôte installe puis rappelle Start().
            foreach (var session in _sessions)
                session.Start();
        }
    }

    /// <summary>Arrête les process mais garde les sessions : l'export reste possible.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            foreach (var session in _sessions)
                session.Stop();
        }
    }

    public void ClearBuffer()
    {
        lock (_lock)
        {
            foreach (var session in _sessions)
                session.ClearBuffer();
        }
        // Résidus éventuels d'anciens agencements (segments à la racine, écrans désélectionnés)
        try
        {
            var baseDir = CaptureEngine.ResolveBufferBase(_cfg());
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
        catch { /* verrouillé : les sessions actives recréeront leurs dossiers */ }
    }

    private void TearDownSessions()
    {
        foreach (var session in _sessions)
        {
            session.Stop();
            session.Dispose();
        }
        _sessions.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            TearDownSessions();
        }
    }
}
