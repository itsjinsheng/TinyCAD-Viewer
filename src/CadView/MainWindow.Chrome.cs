using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TinyCAD;

public partial class MainWindow
{
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindowClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void AttachCaptionInsets()
    {
        SourceInitialized += (_, _) => UpdateCaptionInsets();
        StateChanged += (_, _) => UpdateCaptionInsets();
        SizeChanged += (_, _) => UpdateCaptionInsets();
    }

    private void UpdateCaptionInsets()
    {
        if (WindowState != WindowState.Maximized) { WindowFrame.Margin = new Thickness(0); return; }
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds) ||
            !GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
        // Windows places a maximized resize frame outside the monitor work area.
        // Keep the custom caption and status bar inside it, including at non-default DPI.
        var dpi = VisualTreeHelper.GetDpi(this);
        WindowFrame.Margin = new Thickness(
            Math.Max(0, monitor.Work.Left - bounds.Left) / dpi.DpiScaleX,
            Math.Max(0, monitor.Work.Top - bounds.Top) / dpi.DpiScaleY,
            Math.Max(0, bounds.Right - monitor.Work.Right) / dpi.DpiScaleX,
            Math.Max(0, bounds.Bottom - monitor.Work.Bottom) / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
