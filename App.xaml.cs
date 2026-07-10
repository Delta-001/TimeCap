using System.IO;
using System.Windows;
using ScreenClipTool.Capture;
using ScreenClipTool.Config;
using ScreenClipTool.Export;
using ScreenClipTool.Input;
using ScreenClipTool.UI;

namespace ScreenClipTool;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ConfigService? _configService;
    private AppConfig _config = new();
    private CaptureEngine? _capture;
    private HotkeyManager? _hotkeys;
    private ClipExporter? _exporter;
    private TrayController? _tray;
    private SettingsWindow? _settingsWindow;
    private MainWindow? _mainWindow;
    private string _lastStatus = "Démarrage…";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int selftest = Array.IndexOf(e.Args, "--selftest");
        if (selftest >= 0)
        {
            RunSelfTest(selftest + 1 < e.Args.Length
                ? e.Args[selftest + 1]
                : Path.Combine(Path.GetTempPath(), "screencliptool_selftest.json"));
            return;
        }

        int uitest = Array.IndexOf(e.Args, "--uitest");
        if (uitest >= 0)
        {
            RunUiTest(uitest + 1 < e.Args.Length
                ? e.Args[uitest + 1]
                : Path.Combine(Path.GetTempPath(), "screencliptool_uitest.txt"));
            return;
        }

        _singleInstanceMutex = new Mutex(true, @"Local\ScreenClipTool_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "TimeCap est déjà en cours d'exécution (icône dans la zone de notification).",
                "TimeCap", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Exception UI non gérée : " + ex.Exception);
            _tray?.NotifyError("Erreur inattendue", ex.Exception.Message);
            ex.Handled = true;
        };

        _configService = new ConfigService();
        _config = _configService.Load();

        _capture = new CaptureEngine(() => _config);
        _capture.StatusChanged += s => Dispatcher.BeginInvoke(() => SetStatus(s));
        _capture.Error += (title, msg) => Dispatcher.BeginInvoke(() => _tray?.NotifyError(title, msg));
        _exporter = new ClipExporter(_capture, () => _config);

        _tray = new TrayController(
            openMain: OpenMainWindow,
            openSettings: OpenSettings,
            saveClip: TriggerClip,
            openClipsFolder: OpenClipsFolder,
            exit: Shutdown);
        _tray.UpdateBindings(_config.Hotkeys);

        _hotkeys = new HotkeyManager();
        _hotkeys.HotkeyPressed += TriggerClip;
        ApplyHotkeys();

        OpenMainWindow();
        StartCaptureInBackground();
    }

    private void SetStatus(string status)
    {
        _lastStatus = status;
        _tray?.SetStatus(status);
        _mainWindow?.SetStatus(status);
    }

    private void OpenMainWindow()
    {
        if (_mainWindow is { IsLoaded: true })
        {
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            return;
        }
        _mainWindow = new MainWindow(_capture!, () => _config, TriggerClipAsync, OpenSettings, OpenClipsFolder);
        _mainWindow.SetStatus(_lastStatus);
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void RunSelfTest(string resultPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = Task.Run(async () =>
        {
            try { await SelfTest.RunAsync(resultPath); }
            catch (Exception ex)
            {
                try { File.WriteAllText(resultPath, "{\"ok\": false, \"error\": " + System.Text.Json.JsonSerializer.Serialize(ex.ToString()) + "}"); }
                catch { }
            }
            finally { await Dispatcher.InvokeAsync(Shutdown); }
        });
    }

    /// <summary>
    /// Mode --uitest [fichier] : instancie et affiche les deux fenêtres (thème,
    /// templates, bindings) sans démarrer la capture, écrit un rapport puis quitte.
    /// </summary>
    private void RunUiTest(string resultPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            _configService = new ConfigService();
            _config = _configService.Load();
            _capture = new CaptureEngine(() => _config);
            var main = new MainWindow(_capture, () => _config, _ => Task.CompletedTask, () => { }, () => { });
            main.SetStatus("Enregistrement en cours (test)");
            main.Show();
            var settings = new SettingsWindow(_configService);
            settings.Show();
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                bool ok = main.IsVisible && settings.IsVisible;
                File.WriteAllText(resultPath, ok ? "ok" : "fenêtres non visibles");
                Shutdown();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(resultPath, "erreur : " + ex); } catch { }
            Shutdown();
        }
    }

    private void StartCaptureInBackground() => Task.Run(() =>
    {
        try { _capture!.Start(); }
        catch (Exception ex)
        {
            Log.Error("Démarrage capture : " + ex);
            Dispatcher.BeginInvoke(() =>
            {
                _tray?.SetStatus("Enregistrement inactif");
                _tray?.NotifyError("Impossible de démarrer l'enregistrement", ex.Message);
            });
        }
    });

    private void ApplyHotkeys()
    {
        var failures = _hotkeys!.Apply(_config.Hotkeys);
        if (failures.Count > 0)
            _tray?.NotifyError("Raccourcis indisponibles",
                "Déjà utilisés par une autre application : " + string.Join(", ", failures));
    }

    private async void TriggerClip(HotkeyBinding binding) => await TriggerClipAsync(binding);

    private async Task TriggerClipAsync(HotkeyBinding binding)
    {
        var result = await _exporter!.ExportAsync(binding.DurationSeconds);
        if (result.Success)
            _tray?.Notify("Clip sauvegardé", $"{Path.GetFileName(result.Path)} (≈{result.Seconds} s)");
        else
            _tray?.NotifyError("Sauvegarde impossible", result.Message);
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_configService!);
        _settingsWindow.Saved += OnSettingsSaved;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(bool restartCapture)
    {
        _config = _configService!.Load();
        ApplyHotkeys();
        _tray?.UpdateBindings(_config.Hotkeys);
        _mainWindow?.RefreshFromConfig();
        if (restartCapture)
        {
            Task.Run(() =>
            {
                try
                {
                    _capture!.Stop();
                    _capture.ClearBuffer();
                    _capture.Start();
                }
                catch (Exception ex)
                {
                    Log.Error("Redémarrage capture : " + ex);
                    Dispatcher.BeginInvoke(() =>
                        _tray?.NotifyError("Reprise de l'enregistrement impossible", ex.Message));
                }
            });
        }
    }

    private void OpenClipsFolder()
    {
        try
        {
            // Path.GetFullPath normalise les "/" de config.json en "\" :
            // explorer.exe ne comprend pas "C:/Clips" et ouvrirait Documents.
            var dir = Path.GetFullPath(_config.OutputDir);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex) { _tray?.NotifyError("Dossier des clips", ex.Message); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _hotkeys?.Dispose(); } catch { }
        try
        {
            _capture?.Stop();
            _capture?.ClearBuffer(); // le buffer est du temporaire : on nettoie
            _capture?.Dispose();
        }
        catch { }
        try { _tray?.Dispose(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
