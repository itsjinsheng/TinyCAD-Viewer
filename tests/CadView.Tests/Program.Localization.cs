using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TinyCAD;

internal static partial class Program
{
    private static void LocalizationAndUnitTests()
    {
        Check(AppInfo.DisplayName == "TinyCAD Viewer 1.0", "public product name");
        Check(L.Language == "en-US", "initial UI language is English");
        L.SetLanguage("zh-CN");
        foreach (var pair in L.Messages)
        {
            Check(pair.Value.Length == 3 && pair.Value.All(s => !string.IsNullOrWhiteSpace(s)), "three translations: " + pair.Key);
            int arguments = CompositeFormat.Parse(pair.Value[0]).MinimumArgumentCount;
            Check(pair.Value.All(s => CompositeFormat.Parse(s).MinimumArgumentCount == arguments), "matching format arguments: " + pair.Key);
        }
        var settingsFile = Path.Combine(root, "artifacts", "settings-test-" + Guid.NewGuid() + ".json");
        try
        {
            Check(AppSettings.Load(settingsFile).DisplayUnit == "mm" && AppSettings.Load(settingsFile).Language == "en-US", "first launch defaults to English and mm");
            foreach (string language in new[] { "zh-CN", "en-US", "ko-KR" })
                foreach (string unit in new[] { "mm", "in" })
                {
                    new AppSettings { Language = language, DisplayUnit = unit }.Save(settingsFile);
                    var loaded = AppSettings.Load(settingsFile);
                    Check(loaded.Language == language && loaded.DisplayUnit == unit, "language and unit persist together");
                }
            File.WriteAllText(settingsFile, "{broken");
            Check(AppSettings.Load(settingsFile).Language == "en-US", "corrupt settings recover");
            File.WriteAllText(settingsFile, """{"Language":"invalid","DisplayUnit":"in"}""");
            Check(AppSettings.Load(settingsFile).Language == "en-US" && AppSettings.Load(settingsFile).DisplayUnit == "in", "invalid language preserves valid unit");
            File.WriteAllText(settingsFile, """{"Language":"ko-KR","DisplayUnit":"invalid"}""");
            Check(AppSettings.Load(settingsFile).Language == "ko-KR" && AppSettings.Load(settingsFile).DisplayUnit == "mm", "invalid unit preserves valid language");
        }
        finally { if (File.Exists(settingsFile)) File.Delete(settingsFile); }
        Equal(MeasurementUnits.Factor("Inches", "mm"), 25.4, "inch source converts to millimeters");
        Equal(MeasurementUnits.Factor("Millimeters", "in"), 1 / 25.4, "millimeter source converts to inches");
        Check(MeasurementUnits.MillimetersPerUnit("图纸单位（未指定）") == null, "unitless drawing cannot be converted");

        var canvas = new CadCanvas { UnitKey = "Millimeters", SnapEnabled = false };
        canvas.Measure(new Size(800, 600)); canvas.Arrange(new Rect(0, 0, 800, 600));
        canvas.SetMode(ToolMode.Distance); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(25.4, 0));
        canvas.SetMode(ToolMode.Area); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(25.4, 0)); canvas.AcceptPoint(new(25.4, 25.4)); canvas.AcceptPoint(new(0, 25.4)); canvas.Finish();
        canvas.SetMode(ToolMode.Angle); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(10, 0)); canvas.AcceptPoint(new(0, 10));
        var records = canvas.Measurements.ToArray();
        var original = records.Select(r => r.CurrentText).ToArray();
        var points = records.SelectMany(r => r.Points).ToArray();
        double zoom = canvas.Zoom;
        foreach (string language in new[] { "en-US", "ko-KR", "zh-CN" })
        {
            L.SetLanguage(language); canvas.DisplayUnit = "in"; canvas.RefreshLanguage();
            Check(records[0].CurrentText.Contains("1.000 in"), "existing distance converts in " + language);
            Check(records[1].CurrentText.Contains("1.000 in²") && records[1].CurrentText.Contains("4.000 in"), "area and perimeter convert in " + language);
            Check(records[2].CurrentText.Contains("90.000°"), "angle stays unchanged");
        }
        for (int i = 0; i < 20; i++) { canvas.DisplayUnit = i % 2 == 0 ? "in" : "mm"; canvas.RefreshLanguage(); }
        Check(records.Select(r => r.CurrentText).SequenceEqual(original), "round trips preserve original formatted measurements");
        Check(records.SequenceEqual(canvas.Measurements) && records.SelectMany(r => r.Points).SequenceEqual(points) && canvas.Zoom == zoom, "language and units preserve records geometry and view");
        canvas.UnitKey = "Inches"; canvas.ClearMeasurements(); canvas.SetMode(ToolMode.Distance);
        canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(1, 0));
        Check(canvas.Measurements[0].CurrentText.Contains("25.400 mm"), "inch source measurement displays millimeters");
        var drawing = new DrawingData(); drawing.Warn("解析器：{0}", "native {message}");
        L.SetLanguage("en-US"); Check(drawing.LocalizeWarning(drawing.Warnings.Keys.Single()).Contains("native {message}"), "parser diagnostic remains verbatim");
        L.SetLanguage("zh-CN");
    }

    private sealed class InlineProgress(Action<CadLoadProgress> action) : IProgress<CadLoadProgress>
    { public void Report(CadLoadProgress value) => action(value); }
    private static void LoadProgressTests()
    {
        var reports = new List<CadLoadProgress>();
        var file = Path.Combine(root, "samples", "二维测量示例.dwg");
        CadLoader.Load(file, progress: new InlineProgress(reports.Add));
        Check(reports.First().Stage == "正在解析图纸…" && reports.Last().Stage == "正在准备显示…" && reports.Last().Objects > 0, "loader reports real stages and object counts");
        Check(reports.Zip(reports.Skip(1)).All(p => p.First.Objects <= p.Second.Objects), "processed object count is monotonic");
        using var cancellation = new CancellationTokenSource();
        bool canceled = false;
        try { CadLoader.Load(file, cancellation.Token, new InlineProgress(p => { if (p.Stage == "正在转换图形…") cancellation.Cancel(); })); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "loading cancels during conversion");
    }

    private static void LocalizedWindowTests(MainWindow window)
    {
        var canvas = (CadCanvas)window.FindName("Canvas");
        var tabs = (TabControl)window.FindName("InspectorTabs");
        window.ApplyDrawing(CadLoader.FromDocument(Demo()));
        canvas.SetMode(ToolMode.Distance); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(120, 80));
        var record = canvas.Measurements.Single();
        foreach (string language in new[] { "en-US", "ko-KR", "zh-CN" })
        {
            L.SetLanguage(language); Pump();
            Check(L.Language == language, "selected language remains active");
            Check(((TabItem)tabs.Items[3]).Header.ToString() == L.T("图层"), "live layer tab translation " + language);
            Check(record.RecordTitle.Contains("01") && ReferenceEquals(record, canvas.Measurements.Single()), "record identity after translation");
            foreach (double width in new[] { 1360d, 900d })
            {
                window.Width = width; window.Height = 880; Pump();
                Check(((ComboBox)window.FindName("LanguageSelector")).IsVisible && canvas.ActualWidth > 400, "usable localized layout " + language + width);
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(root, "artifacts", $"language-{language}-{width}.png")); encoder.Save(stream);
            }
        }
        var unitless = CadLoader.FromDocument(Demo()); unitless.Units = "图纸单位（未指定）"; window.ApplyDrawing(unitless);
        Check(!((ComboBox)window.FindName("UnitSelector")).IsEnabled, "unit conversion disabled for unitless drawing");
        window.Width = 1360; window.Height = 880;
    }
}
