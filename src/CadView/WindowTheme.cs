using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TinyCAD;

internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            int enabled = 1;
            // Unsupported optional DWM attributes return an HRESULT and leave the OS default intact.
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            int caption = 0x00322A25; // COLORREF: #252A32
            int text = 0x00F5F3F1;    // COLORREF: #F1F3F5
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        };
    }
}
