using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenClipTool.Config;

namespace ScreenClipTool.Input;

/// <summary>
/// Hotkeys globaux via RegisterHotKey (Win32) sur une fenêtre message-only.
/// Le set complet est réenregistré à chaque changement de config
/// (UnregisterHotKey de tout, puis RegisterHotKey du nouveau set) — aucun
/// combo codé en dur, pas de redémarrage nécessaire.
/// À créer sur le thread UI (la fenêtre message-only a besoin de sa boucle de messages).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyBinding> _byId = new();
    private int _nextId = 1;
    private bool _disposed;

    public event Action<HotkeyBinding>? HotkeyPressed;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("ScreenClipToolHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE : fenêtre message-only
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Remplace tous les hotkeys enregistrés ; renvoie les combos refusés.</summary>
    public List<string> Apply(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        var failures = new List<string>();
        foreach (var b in bindings)
        {
            if (!TryResolve(b, out uint mods, out uint vk))
            {
                failures.Add($"{b.Describe()} (touche non reconnue)");
                continue;
            }
            int id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk))
                _byId[id] = b;
            else
                failures.Add(b.Describe());
        }
        return failures;
    }

    private void UnregisterAll()
    {
        foreach (var id in _byId.Keys)
            UnregisterHotKey(_source.Handle, id);
        _byId.Clear();
    }

    private static bool TryResolve(HotkeyBinding b, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (var m in b.Modifiers)
        {
            switch (m.Trim().ToLowerInvariant())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl":
                case "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win":
                case "windows": mods |= MOD_WIN; break;
                default: return false;
            }
        }
        if (!Enum.TryParse<Key>(b.Key, ignoreCase: true, out var key) || key == Key.None)
            return false;
        int v = KeyInterop.VirtualKeyFromKey(key);
        if (v == 0) return false;
        vk = (uint)v;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _byId.TryGetValue(wParam.ToInt32(), out var binding))
        {
            handled = true;
            HotkeyPressed?.Invoke(binding);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
