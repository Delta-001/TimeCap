using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenClipTool.UI;

/// <summary>Barre de titre sombre (DWM) — à appeler dans OnSourceInitialized.</summary>
public static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int enabled = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (19 sur les builds Windows 10 antérieurs)
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch { /* cosmétique uniquement */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
