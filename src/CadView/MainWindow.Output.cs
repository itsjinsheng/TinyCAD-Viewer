using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TinyCAD;

public partial class MainWindow
{
    private void PrintAreaClick(object sender, RoutedEventArgs e) => Canvas.TogglePrintArea();

    private void UpdatePrintAreaButton() => ToolIcon.SetIsActive(PrintAreaButton,
        Canvas.PrintArea != null || Canvas.Mode == ToolMode.PrintArea);

    private static DrawingVisual CreateOutputVisual(DrawingGroup drawing, double scale = 1)
    {
        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        context.PushTransform(new ScaleTransform(scale, scale));
        context.DrawDrawing(drawing);
        context.Pop();
        return visual;
    }

    public void ExportPng(string path)
    {
        var size = Canvas.GetOutputSize();
        var dpi = VisualTreeHelper.GetDpi(Canvas);
        double scale = Math.Min(Math.Min(dpi.DpiScaleX, dpi.DpiScaleY),
            CadCanvas.MaxOutputDimension / Math.Max(size.Width, size.Height));
        ExportPng(path, Math.Clamp((int)Math.Ceiling(size.Width * scale), 1, CadCanvas.MaxOutputDimension),
            Math.Clamp((int)Math.Ceiling(size.Height * scale), 1, CadCanvas.MaxOutputDimension));
    }

    public void ExportPng(string path, int width, int height)
    {
        if (width < 1 || height < 1 || width > CadCanvas.MaxOutputDimension || height > CadCanvas.MaxOutputDimension)
            throw new ArgumentOutOfRangeException(nameof(width), L.T("PNG 宽高须在 1–8192 像素内。"));
        var size = Canvas.GetOutputSize();
        double scale = Math.Min(width / size.Width, height / size.Height);
        var output = Canvas.CreateOutputDrawing(scale);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(CreateOutputVisual(output.Drawing, scale));

        // Check one row at a time without duplicating a large high-resolution image.
        var row = new byte[width * 4];
        bool visible = false;
        for (int y = 0; y < height && !visible; y++)
        {
            bitmap.CopyPixels(new Int32Rect(0, y, width, 1), row, row.Length, 0);
            visible = row.Any(b => b != 0);
        }
        if (!visible)
            throw new InvalidOperationException(L.T("当前桌面会话无法生成图像，请在已解锁的本地桌面重试导出。"));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(Canvas);
            var settings = new PngExportDialog(Canvas.GetOutputSize(), Math.Min(dpi.DpiScaleX, dpi.DpiScaleY), Canvas.PrintArea != null) { Owner = this };
            if (settings.ShowDialog() != true) return;
            var dialog = new SaveFileDialog { Filter = L.T("PNG 图片|*.png"), FileName = L.T("图纸预览.png") };
            if (dialog.ShowDialog(this) != true) return;
            ExportPng(dialog.FileName, settings.PixelWidth, settings.PixelHeight);
            StatusText.Text = L.T("已导出") + (Canvas.PrintArea != null ? L.T("打印区域：") : L.T("当前视图：")) + dialog.FileName;
        }
        finally
        {
            Canvas.Focus();
        }
    }

    private void PrintClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true) return;
            var output = Canvas.CreateOutputDrawing();
            double scale = Math.Min(dialog.PrintableAreaWidth / output.Size.Width,
                dialog.PrintableAreaHeight / output.Size.Height);
            string title = AppInfo.Name + " · " + (Canvas.PrintArea != null ? L.T("打印区域") : L.T("当前视图"))
                + L.T("（适应纸张，非工程比例出图）");
            dialog.PrintVisual(CreateOutputVisual(output.Drawing, scale), title);
        }
        finally
        {
            Canvas.Focus();
        }
    }
}
