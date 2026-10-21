using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TinyCAD;

public sealed class PngExportDialog : Window
{
    private readonly Size sourceSize;
    private readonly TextBox widthBox = new() { Height = 34, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox heightBox = new() { Height = 34, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock message = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button exportButton = new() { Content = L.T("选择保存位置…"), IsDefault = true };
    private bool updating;
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }

    public PngExportDialog(Size sourceSize, double dpiScale, bool cropped)
    {
        this.sourceSize = sourceSize;
        Title = L.T("导出 PNG · 分辨率");
        Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowTheme.Attach(this);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = cropped ? L.T("输出范围：已设定的打印区域") : L.T("输出范围：当前视图"), Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = L.T("宽度（像素）") }); panel.Children.Add(widthBox);
        panel.Children.Add(new TextBlock { Text = L.T("高度（像素）") }); panel.Children.Add(heightBox);
        panel.Children.Add(new TextBlock { Text = L.T("宽高自动保持比例，最长边不超过 8192 像素。"), TextWrapping = TextWrapping.Wrap });
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (int multiplier in new[] { 1, 2, 4 })
        {
            var button = new Button { Content = multiplier + L.T(" 倍"), Padding = new Thickness(14, 5, 14, 5) };
            button.Click += (_, _) => SetScale(dpiScale * multiplier);
            presets.Children.Add(button);
        }
        panel.Children.Add(presets); panel.Children.Add(message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("取消"), IsCancel = true };
        actions.Children.Add(cancel); actions.Children.Add(exportButton); panel.Children.Add(actions);
        Content = new Border { Background = (Brush)FindResource("Chrome"), Padding = new Thickness(20), Child = panel };
        widthBox.TextChanged += (_, _) => UpdateDimensions(true);
        heightBox.TextChanged += (_, _) => UpdateDimensions(false);
        exportButton.Click += (_, _) => { if (exportButton.IsEnabled) DialogResult = true; };
        SetScale(dpiScale);
    }

    private void SetScale(double scale)
    {
        scale = Math.Min(scale, CadCanvas.MaxOutputDimension / Math.Max(sourceSize.Width, sourceSize.Height));
        bool widthPrimary = sourceSize.Width >= sourceSize.Height;
        (widthPrimary ? widthBox : heightBox).Text = Math.Max(1,
            (int)Math.Round((widthPrimary ? sourceSize.Width : sourceSize.Height) * scale)).ToString(CultureInfo.InvariantCulture);
        UpdateDimensions(widthPrimary);
    }

    private void UpdateDimensions(bool widthChanged)
    {
        if (updating) return;
        var edited = widthChanged ? widthBox : heightBox;
        if (!int.TryParse(edited.Text, out int value) || value < 1 || value > CadCanvas.MaxOutputDimension)
        { InvalidSize(); return; }
        double ratio = widthChanged ? sourceSize.Height / sourceSize.Width : sourceSize.Width / sourceSize.Height;
        double otherValue = Math.Max(1, Math.Round(value * ratio));
        if (otherValue > CadCanvas.MaxOutputDimension) { InvalidSize(); return; }
        int other = (int)otherValue;
        updating = true;
        try { (widthChanged ? heightBox : widthBox).Text = other.ToString(CultureInfo.InvariantCulture); }
        finally { updating = false; }
        PixelWidth = widthChanged ? value : other;
        PixelHeight = widthChanged ? other : value;
        exportButton.IsEnabled = true;
        message.Text = L.F($"导出尺寸：{PixelWidth} × {PixelHeight} 像素");
    }

    private void InvalidSize()
    {
        exportButton.IsEnabled = false;
        message.Text = L.T("请输入有效的整数尺寸，宽高均须在 1–8192 像素内。");
    }
}
