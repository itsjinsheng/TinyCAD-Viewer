using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using TinyCAD;
using Point = System.Windows.Point;
using Color = ACadSharp.Color;

internal static partial class Program
{
    private static int passed;
    private static string root = "";
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            Directory.CreateDirectory(Path.Combine(root, "samples")); Directory.CreateDirectory(Path.Combine(root, "artifacts"));
            if (args.Length > 1 && args[1] == "--perf-labels") { AnnotationPerformance(args.Length > 2 ? args[2] : "current"); return 0; }
            if (args.Length > 2 && args[1] == "--perf") { PerformanceTest(args[2], args.Length > 3 ? args[3] : "current"); return 0; }
            if (args.Length > 2 && args[1] == "--perf-pan") { PerformanceTest(args[2], args.Length > 3 ? args[3] : "pan", true); return 0; }
            if (args.Length > 2 && args[1] == "--perf-interrupt") { InterruptedPreviewTest(args[2]); return 0; }
            if (args.Length > 2 && args[1] == "--perf-screen") { ScreenPerformance(args[2], args.Length > 3 && args[3] == "gpu", args.Length > 4 ? args[4] : "mixed"); return 0; }
            if (args.Length > 1 && args[1] == "--gpu-check")
            {
                var app = new TinyCAD.App(); app.InitializeComponent();
                var window = new MainWindow(); window.Show(); Pump();
                GpuSceneTests(window, (CadCanvas)window.FindName("Canvas")); window.Close();
                Console.WriteLine($"PASS: {passed} GPU assertions"); return 0;
            }
            Environment.SetEnvironmentVariable("TINYCAD_LEGACY_RENDER", "1");
            LocalizationAndUnitTests(); GeometryTests(); MeasurementInputTests(); LoaderTests(); LoadProgressTests(); RenderTest();
            if (args.Length > 1) CorpusTest(args[1]);
            Console.WriteLine($"PASS: {passed} assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Equal(double actual, double expected, string name, double tolerance = 1e-7) => Check(Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected)), $"{name}: {actual:G12}");
    private static void GeometryTests()
    {
        Equal(Segment.Line(new(0, 0), new(3, 4)).Length, 5, "3-4-5 distance");
        var circle = Segment.Arc(new(1e8, 2e8), 10, 0, Geo.Tau);
        Equal(circle.Length, 20 * Math.PI, "circle circumference");
        Equal(circle.SignedArea(circle.Center), 100 * Math.PI, "circle area at large coordinates");
        var arc = Segment.Arc(new(0, 0), 5, Math.PI * 1.5, Math.PI);
        Equal(arc.Bounds.Width, 5, "arc bounds crossing zero");
        Equal(Geo.Distance(arc.Nearest(new(10, 0)), new(5, 0)), 0, "arc analytic nearest");
        var bulge = Segment.Bulge(new(0, 0), new(10, 0), 1);
        Equal(bulge.Length, 5 * Math.PI, "semicircular bulge length");
        Equal(Geo.Distance(bulge.At(1), new(10, 0)), 0, "bulge endpoint");
        var negative = Segment.Bulge(new(0, 0), new(10, 0), -1);
        Equal(negative.Length, 5 * Math.PI, "clockwise bulge length");
        Check(negative.At(.5).Y > 0 && bulge.At(.5).Y < 0, "bulge orientation");
        var m = Matrix.Identity; m.Scale(2, 3); m.Rotate(35); m.Translate(80, -30);
        var el = Segment.Arc(new(0, 0), 5, 0, Geo.Tau).Transform(m);
        Check(!el.IsCircle, "nonuniform scale is ellipse");
        Equal(el.SignedArea(el.Center), 150 * Math.PI, "transformed ellipse exact area");
        Check(el.Length > 2 * Math.PI * 10 && el.Length < 2 * Math.PI * 15, "ellipse circumference range");
        Check(Geo.Intersection(new(0, 0), new(10, 10), new(0, 10), new(10, 0), out var p), "line intersection exists");
        Equal(Geo.Distance(p, new(5, 5)), 0, "line intersection coordinate");
        Check(!Geo.Intersection(new(0, 0), new(1, 0), new(2, -1), new(2, 1), out _), "no extension intersection");
        Equal(Geo.PolygonArea([new(1e9, 1e9), new(1e9 + 20, 1e9), new(1e9 + 20, 1e9 + 10), new(1e9, 1e9 + 10)]), 200, "stable polygon area");
        var cuts = CadCanvas.Intersections(Segment.Line(new(-20, 0), new(20, 0)), Segment.Arc(new(0, 0), 10, 0, Geo.Tau)).ToArray();
        Check(cuts.Length == 2, "line-circle two intersections"); Equal(cuts[0].X, -10, "line-circle coordinate");
        Check(CadCanvas.Intersections(Segment.Arc(new(0, 0), 10, 0, Geo.Tau), Segment.Arc(new(10, 0), 10, 0, Geo.Tau)).Count() == 2, "circle-circle intersections");
    }
    private static void MeasurementInputTests()
    {
        var canvas = new CadCanvas { SnapEnabled = false };
        canvas.Measure(new Size(800, 600)); canvas.Arrange(new Rect(0, 0, 800, 600));
        canvas.FitRect(new Rect(0, 0, 150, 100));
        void Triangle()
        {
            canvas.SetMode(ToolMode.Area);
            canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(100, 0)); canvas.AcceptPoint(new(100, 60));
        }
        Triangle();
        var nearStart = new Point(5 / canvas.Zoom, 0);
        canvas.SnapEnabled = true;
        var closureSnap = canvas.Snap(nearStart);
        Check(closureSnap.point == new Point(0, 0) && closureSnap.kind == "闭合起点", "area snaps to its own start without drawing entities");
        Check(canvas.Snap(new Point(11 / canvas.Zoom, 0)).kind == "", "area start snap respects screen tolerance");
        canvas.ZoomBy(2);
        Check(canvas.Snap(new Point(9 / canvas.Zoom, 0)).kind == "闭合起点", "area start snap tolerance follows zoom");
        canvas.ZoomBy(.5);
        canvas.SnapEnabled = false;
        Check(canvas.Snap(nearStart).point == nearStart && canvas.Snap(nearStart).kind == "", "disabled snapping does not attract area start");
        canvas.AcceptPoint(nearStart);
        Check(canvas.Measurements.Count == 1 && canvas.Measurements[0].Points.Count == 3 && canvas.Measurements[0].Closed,
            "click near first vertex closes area without duplicate vertex");
        Check(canvas.Measurements[0].Text.Contains("3000.000"), "click-to-close computes triangle area");
        canvas.SnapEnabled = true;
        Check(canvas.Snap(nearStart).kind == "", "completed area leaves no stale start snap");
        canvas.SnapEnabled = false;
        canvas.AcceptPoint(new(0, 0), 2);
        Check(canvas.Measurements.Count == 1, "double click on closure does not add another result");
        Triangle(); canvas.AcceptPoint(new(100, 60), 2);
        Check(canvas.Measurements.Count == 2 && canvas.Measurements[1].Points.Count == 3, "double click completes area using first click vertex once");
        canvas.SetMode(ToolMode.Area); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(100, 0)); canvas.AcceptPoint(new(100, 0), 2);
        canvas.SnapEnabled = true;
        Check(canvas.Snap(nearStart).kind == "", "area closure snap requires three vertices");
        canvas.SnapEnabled = false;
        Check(canvas.Measurements.Count == 2, "double click with two vertices does not create area");
        canvas.AcceptPoint(new(100, 60)); canvas.AcceptPoint(new(100, 60), 2);
        Check(canvas.Measurements.Count == 3, "insufficient area can continue after double click");
        foreach (var mode in new[] { ToolMode.Area, ToolMode.Chain, ToolMode.Distance, ToolMode.Angle, ToolMode.Radius, ToolMode.Entity })
        {
            canvas.SetMode(mode);
            if (mode is ToolMode.Area or ToolMode.Chain)
            { canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(100, 0)); canvas.AcceptPoint(new(100, 60)); }
            else if (mode is ToolMode.Distance or ToolMode.Angle) canvas.AcceptPoint(new(0, 0));
            var rightClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
            { RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent };
            canvas.RaiseEvent(rightClick);
            Check(rightClick.Handled && canvas.Mode == ToolMode.Select && canvas.Measurements.Count == 3, $"right click cancels {mode} without completing or deleting records");
        }
        canvas.SetMode(ToolMode.Area); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(100, 60)); canvas.AcceptPoint(new(0, 60)); canvas.AcceptPoint(new(100, 0)); canvas.AcceptPoint(new(100, 0), 2);
        Check(canvas.Measurements.Count == 3, "double click still rejects self-intersection");
        canvas.SetMode(ToolMode.Chain); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(100, 0)); canvas.AcceptPoint(new(100, 60));
        canvas.AcceptPoint(new(100, 60), 2);
        Check(canvas.Measurements.Count == 4 && canvas.Measurements[^1].Points.Count == 3 && canvas.Measurements[^1].Text.Contains("160.000"), "chain double click finishes open path without duplicate segment or closing edge");
        canvas.SetMode(ToolMode.Chain); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(100, 0)); canvas.AcceptPoint(new(100, 60));
        canvas.SnapEnabled = true;
        var chainSnap = canvas.Snap(nearStart);
        Check(chainSnap.point == new Point(0, 0) && chainSnap.kind == "累计起点", "chain captures its own start");
        Check(canvas.Snap(new(11 / canvas.Zoom, 0)).kind == "", "chain start capture respects screen tolerance");
        canvas.SnapEnabled = false;
        Check(canvas.Snap(nearStart).point == nearStart && canvas.Snap(nearStart).kind == "", "chain start capture obeys snap switch");
        canvas.SnapEnabled = true;
        canvas.AcceptPoint(nearStart); canvas.AcceptPoint(nearStart, 2);
        var chainResult = canvas.Measurements[^1];
        Check(canvas.Measurements.Count == 5 && chainResult.Points.Count == 4 && chainResult.Points[^1] == new Point(0, 0) && chainResult.Text.Contains("276.619"), "chain return leg is counted exactly once after snapped double click");
        Check(canvas.Snap(nearStart).kind == "", "completed chain clears start capture");
        canvas.SetMode(ToolMode.Chain); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(0, 0), 2);
        Check(canvas.Measurements.Count == 5, "chain double click requires two points");
        canvas.AcceptPoint(new(100, 0)); canvas.AcceptPoint(new(100, 0), 2);
        Check(canvas.Measurements.Count == 6 && canvas.Measurements[^1].Text.Contains("100.000"), "chain continues after insufficient double click");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var zoomCorner = typeof(CadCanvas).GetMethod("AcceptZoomCorner", flags)!;
        var zoomStartField = typeof(CadCanvas).GetField("zoomStart", flags)!;
        canvas.SetMode(ToolMode.WindowZoom);
        double zoomBefore = canvas.Zoom;
        zoomCorner.Invoke(canvas, [canvas.ToScreen(new(20, 20))]);
        canvas.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent });
        Check(canvas.Mode == ToolMode.WindowZoom && zoomStartField.GetValue(canvas) is Point && canvas.Zoom == zoomBefore,
            "window zoom first click survives mouse release without zooming");
        canvas.ZoomBy(1.2); canvas.PanBy(new(12, 8));
        Equal(Geo.Distance((Point)zoomStartField.GetValue(canvas)!, new(20, 20)), 0, "window zoom first corner stays attached to world coordinates");
        zoomCorner.Invoke(canvas, [canvas.ToScreen(new(60, 50))]);
        Check(canvas.Mode == ToolMode.Select && zoomStartField.GetValue(canvas) == null && canvas.Zoom > zoomBefore,
            "window zoom second click completes and returns to selection");
        Equal(Geo.Distance(canvas.ToWorld(new(canvas.ActualWidth / 2, canvas.ActualHeight / 2)), new(40, 35)), 0, "two click zoom centers the selected rectangle");
        canvas.SetMode(ToolMode.WindowZoom); zoomCorner.Invoke(canvas, [new Point(100, 100)]);
        canvas.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
        { RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent });
        Check(canvas.Mode == ToolMode.Select && zoomStartField.GetValue(canvas) == null, "right click cancels unfinished two click zoom");
    }
    public static CadDocument Demo()
    {
        var doc = new CadDocument(ACadVersion.AC1032); doc.Header.InsUnits = UnitsType.Millimeters;
        var outline = new Layer("01_零件轮廓") { Color = new Color(7) };
        var holes = new Layer("02_孔位") { Color = new Color(4) };
        var center = new Layer("03_中心线") { Color = new Color(3) };
        var notes = new Layer("04_说明") { Color = new Color(2) };
        doc.Layers.Add(outline); doc.Layers.Add(holes); doc.Layers.Add(center); doc.Layers.Add(notes);
        var plate = new LwPolyline { IsClosed = true, Layer = outline };
        foreach (var xy in new[] { new XY(0, 0), new XY(120, 0), new XY(120, 80), new XY(0, 80) }) plate.Vertices.Add(new LwPolyline.Vertex(xy));
        doc.Entities.Add(plate);
        foreach (var xy in new[] { new XY(20, 20), new XY(100, 20), new XY(20, 60), new XY(100, 60) })
        {
            doc.Entities.Add(new Circle { Center = new XYZ(xy.X, xy.Y, 0), Radius = 6, Layer = holes });
            doc.Entities.Add(new Line { StartPoint = new XYZ(xy.X - 10, xy.Y, 0), EndPoint = new XYZ(xy.X + 10, xy.Y, 0), Layer = center });
            doc.Entities.Add(new Line { StartPoint = new XYZ(xy.X, xy.Y - 10, 0), EndPoint = new XYZ(xy.X, xy.Y + 10, 0), Layer = center });
        }
        // Rounded slot with exact bulges: straight lengths 30 + 30 and two R10 semicircles.
        var slot = new LwPolyline { IsClosed = true, Layer = outline };
        slot.Vertices.Add(new(45, 30)); slot.Vertices.Add(new(75, 30) { Bulge = 1 }); slot.Vertices.Add(new(75, 50)); slot.Vertices.Add(new(45, 50) { Bulge = 1 }); doc.Entities.Add(slot);
        doc.Entities.Add(new TextEntity("120 × 80  /  安装板") { InsertPoint = new XYZ(0, 99, 0), Height = 5, Layer = notes });
        doc.Entities.Add(new TextEntity("4-Ø12    中槽 R10") { InsertPoint = new XYZ(0, -13, 0), Height = 3.5, Layer = notes });
        doc.Entities.Add(new TextEntity("TinyCAD  •  二维测量示例  •  mm") { InsertPoint = new XYZ(0, -22, 0), Height = 2.5, Layer = notes });
        // Native dimension graphic for compatibility with readers.
        var dimBlock = new BlockRecord("*D1");
        dimBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 87, 0), EndPoint = new XYZ(120, 87, 0), Layer = notes });
        foreach (var x in new[] { 0, 120 }) dimBlock.Entities.Add(new Line { StartPoint = new XYZ(x, 81, 0), EndPoint = new XYZ(x, 92, 0), Layer = notes });
        dimBlock.Entities.Add(new TextEntity("120") { InsertPoint = new XYZ(56, 89, 0), Height = 3, Layer = notes });
        doc.BlockRecords.Add(dimBlock); doc.Entities.Add(new Insert(dimBlock) { Layer = notes });
        var layout = doc.Layouts.First(l => l.IsPaperSpace);
        layout.AddViewport(new Viewport { Center = new XYZ(100, 70, 0), Width = 170, Height = 120, ViewHeight = 140, ViewCenter = new XY(60, 40), ViewDirection = XYZ.AxisZ });
        return doc;
    }
    private static void LoaderTests()
    {
        var demo = Demo();
        var dxf = Path.Combine(root, "samples", "二维测量示例.dxf"); var dwg = Path.Combine(root, "samples", "二维测量示例.dwg");
        DxfWriter.Write(dxf, demo); DwgWriter.Write(dwg, demo);
        foreach (var path in new[] { dxf, dwg })
        {
            var loaded = CadLoader.Load(path); var model = loaded.Spaces["模型"];
            Check(model.Count >= 20, Path.GetExtension(path) + " read shapes");
            Check(loaded.Units == "Millimeters", Path.GetExtension(path) + " drawing units");
            var plate = model.Single(s => s.Type == "LWPOLYLINE" && Math.Abs(s.Area - 9600) < .01);
            Equal(plate.Length, 400, Path.GetExtension(path) + " plate perimeter");
            var slot = model.Single(s => s.Type == "LWPOLYLINE" && s != plate);
            Equal(slot.Length, 60 + 20 * Math.PI, Path.GetExtension(path) + " slot perimeter");
            Equal(slot.Area, 600 + 100 * Math.PI, Path.GetExtension(path) + " slot area");
            Check(model.Count(s => s.Type == "CIRCLE") == 4, Path.GetExtension(path) + " circle count");
            Check(loaded.Spaces.Values.Skip(1).Any(list => list.Any(s => s.PaperProjection)), Path.GetExtension(path) + " layout viewport content");
            Check(!loaded.Warnings.Keys.Any(k => k.Contains("失败")), Path.GetExtension(path) + " no entity exceptions");
        }
        var custom = new CadDocument(); var block = new BlockRecord("offset-block"); block.BlockEntity.BasePoint = new XYZ(10, 20, 0);
        block.Entities.Add(new Line { StartPoint = new XYZ(10, 20, 0), EndPoint = new XYZ(15, 20, 0) }); custom.BlockRecords.Add(block);
        custom.Entities.Add(new Insert(block) { InsertPoint = new XYZ(100, 200, 0), XScale = 2, YScale = 3, Rotation = Math.PI / 2 });
        var line = CadLoader.FromDocument(custom).Spaces["模型"].Single().Segments.Single();
        Equal(Geo.Distance(line.A, new(100, 200)), 0, "insert base point transform");
        Equal(Geo.Distance(line.B, new(100, 210)), 0, "insert rotation scale transform");
        using var cts = new CancellationTokenSource(); cts.Cancel(); bool canceled = false;
        try { CadLoader.Load(dxf, cts.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "cancellation honored");
        var malformed = Path.Combine(root, "artifacts", "invalid.dxf"); File.WriteAllText(malformed, "not a cad drawing"); bool rejected = false;
        try { CadLoader.Load(malformed); } catch { rejected = true; }
        Check(rejected, "malformed DXF rejected");
    }
    private static void RenderTest()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new TinyCAD.App(); app.InitializeComponent();
        var window = new MainWindow();
        var content = (FrameworkElement)window.Content;
        window.Show();
        content.Measure(new Size(1360, 880)); content.Arrange(new Rect(0, 0, 1360, 880)); content.UpdateLayout();
        Pump();
        LocalizedWindowTests(window);
        ReopenDrawingTests(window);
        window.ApplyDrawing(CadLoader.Load(Path.Combine(root, "samples", "二维测量示例.dwg")));
        var replacement = CadLoader.FromDocument(Demo());
        replacement.FileName = "replacement.dwg";
        window.ApplyDrawing(replacement);
        Check(ReferenceEquals(((CadCanvas)window.FindName("Canvas")).Shapes, replacement.Spaces["模型"]), "opening another drawing with identical space names replaces canvas scene");
        content.UpdateLayout();
        var canvas = (CadCanvas)window.FindName("Canvas"); canvas.Fit(); canvas.UpdateLayout();
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        var snap = canvas.Snap(new Point(20.01, 20.01));
        var searchTabs = (System.Windows.Controls.TabControl)window.FindName("InspectorTabs");
        searchTabs.SelectedIndex = 2; Pump();
        var searchQuery = (System.Windows.Controls.TextBox)window.FindName("SearchQuery");
        Check(searchQuery.IsVisible && window.IsEnabled, "search tab leaves drawing enabled");
        searchQuery.Text = "安装板";
        ((System.Windows.Controls.Button)window.FindName("SearchNextButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(searchTabs.SelectedIndex == 2 && ((System.Windows.Controls.TextBlock)window.FindName("SearchStatus")).Text.StartsWith("第 1 /"), "search finds text and keeps search tab active");
        var searchResults = (System.Windows.Controls.ListBox)window.FindName("SearchResults");
        Check(searchResults.Items.Count == 1 && searchResults.SelectedIndex == 0, "search populates and selects result list");
        searchQuery.Text = "0";
        ((System.Windows.Controls.Button)window.FindName("SearchNextButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(searchResults.Items.Count > 1, "all matching text appears in result list");
        searchResults.SelectedIndex = searchResults.Items.Count - 1;
        var selectedShape = typeof(CadCanvas).GetField("selected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(canvas);
        Check(ReferenceEquals(selectedShape, searchResults.SelectedItem) && searchTabs.SelectedIndex == 2, "result list and drawing select the same object");
        double zoomBeforeSearchWheel = canvas.Zoom;
        canvas.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 120) { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
        Check(canvas.Zoom > zoomBeforeSearchWheel, "wheel zoom works with search tab active");
        Check(window.OwnedWindows.Count == 0, "search does not create popup windows");
        canvas.Fit(); Pump();
        var searchBitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); searchBitmap.Render(content);
        var searchEncoder = new PngBitmapEncoder(); searchEncoder.Frames.Add(BitmapFrame.Create(searchBitmap));
        using (var stream = File.Create(Path.Combine(root, "artifacts", "search-tab.png"))) searchEncoder.Save(stream);
        searchTabs.SelectedIndex = 0;
        Equal(Geo.Distance(snap.point, new(20, 20)), 0, "circle center snap");
        var screen = canvas.ToScreen(new(42.5, 31.25));
        Equal(Geo.Distance(canvas.ToWorld(screen), new(42.5, 31.25)), 0, "camera roundtrip");
        canvas.ZoomBy(1.3); canvas.PanBy(new Vector(27, -18)); Pump();
        var resizeCenter = canvas.ToWorld(new(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
        double resizeZoom = canvas.Zoom;
        foreach (var size in new[] { new Size(1024, 700), new Size(1400, 900), new Size(1280, 820) })
        {
            window.Width = size.Width; window.Height = size.Height; Pump();
            Equal(Geo.Distance(canvas.ToWorld(new(canvas.ActualWidth / 2, canvas.ActualHeight / 2)), resizeCenter), 0, $"resize keeps drawing center at {size.Width}");
            Equal(canvas.Zoom, resizeZoom, $"resize keeps zoom at {size.Width}");
        }
        canvas.Fit(); Pump();
        canvas.SnapEnabled = false;
        canvas.SetMode(ToolMode.Distance); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(120, 80));
        Check(canvas.Measurements.Count == 1 && canvas.Measurements[0].Text.Contains("144.222"), "distance tool result");
        canvas.SetMode(ToolMode.Area); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(120, 0)); canvas.AcceptPoint(new(120, 80)); canvas.AcceptPoint(new(0, 80)); canvas.Finish();
        Check(canvas.Measurements.Count == 2 && canvas.Measurements[1].Text.Contains("9600.000"), "area tool result");
        Check(ReferenceEquals(((System.Windows.Controls.ListBox)window.FindName("Results")).SelectedItem, canvas.Measurements[1]), "new measurement replaces previous record selection");
        Check(ReferenceEquals(canvas.FocusedMeasurement, canvas.Measurements[1]), "new measurement ruler is highlighted");
        canvas.SetMode(ToolMode.Area); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(10, 10)); canvas.AcceptPoint(new(0, 10)); canvas.AcceptPoint(new(10, 0)); canvas.Finish();
        Check(canvas.Measurements.Count == 2, "self-intersecting measurement rejected");
        canvas.SetMode(ToolMode.Angle); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(10, 0)); canvas.AcceptPoint(new(0, 10));
        Check(canvas.Measurements[^1].Text.Contains("90.000"), "angle tool result");
        canvas.SetMode(ToolMode.Radius); canvas.AcceptPoint(new(26, 20));
        Check(canvas.Measurements[^1].Text.Contains("6.000") && canvas.Measurements[^1].Text.Contains("12.000"), "radius tool result");
        canvas.SetMode(ToolMode.Entity); canvas.AcceptPoint(new(60, 0));
        Check(canvas.Measurements[^1].Text.Contains("9600.000"), "entity area tool result");
        var entityMeasurement = canvas.Measurements[^1];
        canvas.FocusMeasurement(entityMeasurement);
        Check(ReferenceEquals(canvas.FocusedMeasurement, entityMeasurement), "entity measurement highlighted");
        var center = canvas.ToScreen(new(60, 40));
        Equal(center.X, canvas.ActualWidth / 2, "entity focus centers full shape X");
        Equal(center.Y, canvas.ActualHeight / 2, "entity focus centers full shape Y");
        var duplicate = new MeasureResult(canvas.Measurements[0].Text, [new(1000, 1000), new(1120, 1080)], false);
        canvas.Measurements.Add(duplicate); canvas.FocusMeasurement(duplicate);
        Check(ReferenceEquals(canvas.FocusedMeasurement, duplicate), "equal text records locate by identity");
        center = canvas.ToScreen(new(1060, 1040));
        Equal(center.X, canvas.ActualWidth / 2, "measurement focus centers X");
        Equal(center.Y, canvas.ActualHeight / 2, "measurement focus centers Y");
        canvas.FocusMeasurement(canvas.Measurements[0]);
        Check(!ReferenceEquals(canvas.FocusedMeasurement, duplicate), "switch record replaces highlight");
        var results = (System.Windows.Controls.ListBox)window.FindName("Results");
        Check(ReferenceEquals(results.Items[0], canvas.Measurements[0]), "record UI preserves measurement identity");
        var previousZoom = canvas.Zoom;
        var previousOrigin = canvas.ToScreen(new(0, 0));
        results.SelectedIndex = 1;
        Check(ReferenceEquals(canvas.FocusedMeasurement, canvas.Measurements[1]), "record selection immediately highlights measurement");
        Equal(canvas.Zoom, previousZoom, "selection preserves zoom");
        Equal(Geo.Distance(canvas.ToScreen(new(0, 0)), previousOrigin), 0, "selection preserves camera position");
        results.SelectedIndex = 0;
        Check(ReferenceEquals(canvas.FocusedMeasurement, canvas.Measurements[0]), "selection switches highlight");
        results.SelectedIndex = -1;
        Check(canvas.FocusedMeasurement == null, "deselect clears highlight");
        Pump(); results.UpdateLayout();
        var recordItem = (System.Windows.Controls.ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(0);
        var doubleClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = recordItem };
        canvas.FocusMeasurement(entityMeasurement);
        results.RaiseEvent(doubleClick);
        Check(ReferenceEquals(canvas.FocusedMeasurement, canvas.Measurements[0]) && doubleClick.Handled, "record double click focuses matching measurement");
        Pump();
        var focusedBitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var resultViewer = Descendants(results).OfType<System.Windows.Controls.ScrollViewer>().First();
        resultViewer.ScrollToBottom(); Pump();
        var scrollBar = Descendants(results).OfType<System.Windows.Controls.Primitives.ScrollBar>().First(b => b.Orientation == System.Windows.Controls.Orientation.Vertical);
        Check(scrollBar.ActualWidth <= 10.5, "dark list scrollbar is narrow");
        double previousOffset = resultViewer.VerticalOffset;
        System.Windows.Controls.Primitives.ScrollBar.PageUpCommand.Execute(null, scrollBar); Pump();
        Check(resultViewer.VerticalOffset < previousOffset, "scrollbar page command moves list");
        resultViewer.ScrollToBottom(); Pump();
        focusedBitmap.Render(content);
        var focusedEncoder = new PngBitmapEncoder(); focusedEncoder.Frames.Add(BitmapFrame.Create(focusedBitmap));
        using (var focusedStream = File.Create(Path.Combine(root, "artifacts", "measurement-focus.png"))) focusedEncoder.Save(focusedStream);
        canvas.IsModel = false; canvas.SetMode(ToolMode.Select); canvas.SetMode(ToolMode.Distance);
        canvas.IsModel = true;
        canvas.AcceptPoint(new(60, 0));
        var propertyBox = (System.Windows.Controls.TextBox)window.FindName("Properties");
        Check(propertyBox.Text.Contains("LWPOLYLINE"), "object selected before Escape");
        results.SelectedIndex = 0;
        var recordCount = canvas.Measurements.Count;
        var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Escape)
        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(escape);
        Check(escape.Handled && propertyBox.Text == "未选中对象", "Escape clears object selection");
        Check(canvas.FocusedMeasurement == null && results.SelectedIndex == -1, "Escape clears record highlight and selection");
        Check(canvas.Measurements.Count == recordCount, "Escape preserves measurements");
        Check(canvas.Mode == ToolMode.Select, "paper space measurements blocked"); canvas.IsModel = true;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var labelField = typeof(CadCanvas).GetField("labelHits", flags)!;
        var offsetField = typeof(CadCanvas).GetField("labelOffsets", flags)!;
        var measured = canvas.Measurements[0];
        results.SelectedIndex = 0; canvas.FocusMeasurement(measured); Pump();
        var hits = (List<(MeasureResult Result, Rect Bounds)>)labelField.GetValue(canvas)!;
        var hit = hits.Last(h => ReferenceEquals(h.Result, measured));
        var press = new Point(hit.Bounds.X + 12, hit.Bounds.Y + 8);
        var cursorMethod = typeof(CadCanvas).GetMethod("UpdateLabelCursor", flags)!;
        cursorMethod.Invoke(canvas, [press]);
        Check(canvas.Cursor == System.Windows.Input.Cursors.SizeAll, "measurement label hover uses move cursor");
        cursorMethod.Invoke(canvas, [new Point(-10, -10)]);
        Check(canvas.Cursor == System.Windows.Input.Cursors.Cross, "outside labels restores cross cursor");
        Check((bool)typeof(CadCanvas).GetMethod("BeginLabelDrag", flags)!.Invoke(canvas, [press])!, "measurement label accepts drag hit");
        Check(canvas.Cursor == System.Windows.Input.Cursors.SizeAll, "dragging label uses move cursor");
        Check(ReferenceEquals(results.SelectedItem, measured), "label click synchronizes record selection");
        var pointsBeforeDrag = measured.Points.ToArray();
        var zoomBeforeDrag = canvas.Zoom;
        var layouts = (System.Collections.IDictionary)typeof(CadCanvas).GetField("labelLayouts", flags)!.GetValue(canvas)!;
        var anchors = (Dictionary<MeasureResult, Point>)typeof(CadCanvas).GetField("labelAnchors", flags)!.GetValue(canvas)!;
        var cachedLayout = layouts[measured]; var cachedAnchor = anchors[measured];
        typeof(CadCanvas).GetMethod("DragLabel", flags)!.Invoke(canvas, [new Point(350, 180)]); Pump();
        var movedHit = hits.Last(h => ReferenceEquals(h.Result, measured));
        Check(movedHit.Bounds.Location != hit.Bounds.Location, "drag changes label position");
        Check(measured.Points.SequenceEqual(pointsBeforeDrag) && canvas.Zoom == zoomBeforeDrag, "drag preserves measurement geometry and camera");
        canvas.CancelSelection();
        var offsets = (Dictionary<MeasureResult, Vector>)offsetField.GetValue(canvas)!;
        var savedOffset = offsets[measured];
        canvas.ZoomBy(1.1); canvas.PanBy(new(10, 10)); canvas.FinishNavigation(); Pump();
        Check(offsets[measured] == savedOffset, "pan and zoom preserve dragged label offset");
        // CancelSelection changes highlighting once; subsequent movement must reuse glyphs.
        var unselectedLayout = layouts[measured];
        canvas.PanBy(new(2, 2)); canvas.FinishNavigation(); Pump();
        Check(ReferenceEquals(layouts[measured], unselectedLayout), "pan reuses cached measurement glyph drawing");
        Check(anchors[measured] == cachedAnchor, "drag and navigation keep leader attached without geometry rescans");
        Check(!ReferenceEquals(cachedLayout, unselectedLayout), "highlight change refreshes label colors");
        double oldWidth = window.Width; window.Width -= 20; Pump();
        Check(!ReferenceEquals(layouts[measured], unselectedLayout), "resize updates label layout cache");
        window.Width = oldWidth; Pump();
        var anchorMethod = typeof(CadCanvas).GetMethod("MeasurementAnchor", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var distanceAnchor = (Point)anchorMethod.Invoke(null, [new MeasureResult("test", [new(0, 0), new(100, 0)], false), new Point(40, 20)])!;
        Equal(Geo.Distance(distanceAnchor, new(40, 0)), 0, "leader terminates on measured line");
        var circleShape = new CadShape(); circleShape.Segments.Add(Segment.Arc(new(0, 0), 10, 0, Geo.Tau));
        var circleAnchor = (Point)anchorMethod.Invoke(null, [new MeasureResult("circle", [new(10, 0)], false, circleShape), new Point(0, 20)])!;
        Equal(Geo.Distance(circleAnchor, new(0, 10)), 0, "leader terminates on measured circle");
        var annotationBitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        annotationBitmap.Render(content);
        var annotationEncoder = new PngBitmapEncoder(); annotationEncoder.Frames.Add(BitmapFrame.Create(annotationBitmap));
        using (var stream = File.Create(Path.Combine(root, "artifacts", "measurement-label-drag.png"))) annotationEncoder.Save(stream);
        results.SelectedItem = measured;
        ((System.Windows.Controls.TabControl)window.FindName("InspectorTabs")).SelectedIndex = 1; Pump();
        int oldListCount = results.Items.Count, oldMeasurementCount = canvas.Measurements.Count;
        var deleteButton = Descendants(window).OfType<System.Windows.Controls.Button>().Single(b => Equals(b.Content, "删除记录"));
        deleteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        Check(canvas.Measurements.Count == oldMeasurementCount - 1 && results.Items.Count == oldListCount - 1, "delete removes exactly one record from canvas and list");
        Check(!offsets.ContainsKey(measured) && !hits.Any(h => ReferenceEquals(h.Result, measured)), "delete removes label and its position");
        Check(!layouts.Contains(measured) && !anchors.ContainsKey(measured), "delete releases annotation caches");
        Check(!ReferenceEquals(canvas.FocusedMeasurement, measured) && !canvas.DeleteMeasurement(measured), "deleted record cannot remain focused or delete another record");
        int maxNumber = canvas.Measurements.Max(r => r.Number);
        canvas.SetMode(ToolMode.Distance); canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(120, 0));
        Check(canvas.Measurements[^1].Number > maxNumber, "new record number remains unique after deletion");
        canvas.ClearMeasurements(); canvas.SetMode(ToolMode.Select);
        Check(offsets.Count == 0 && hits.Count == 0, "clear removes annotation state");
        Check(layouts.Count == 0 && anchors.Count == 0, "clear releases annotation caches");
        Check(canvas.FocusedMeasurement == null, "clear removes measurement highlight");
        canvas.FocusMeasurement(entityMeasurement);
        Check(canvas.FocusedMeasurement == null, "stale record cannot be focused");
        var probe = new DrawingVisual(); using (var dc = probe.RenderOpen()) dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 20, 20));
        var pb = new RenderTargetBitmap(20, 20, 96, 96, PixelFormats.Pbgra32); pb.Render(probe); var pp = new byte[20 * 20 * 4]; pb.CopyPixels(pp, 80, 0);
        Check(canvas.ActualWidth > 500, "full UI layout initialized");
        if (pp.All(b => b == 0))
        {
            Console.WriteLine("SKIP: pixel rendering unavailable in this desktop session (even a solid DrawingVisual returns transparent). Visual acceptance remains pending.");
            window.Close(); return;
        }
        foreach (var size in new[] { new Size(820, 560), new Size(1024, 700), new Size(1280, 820) })
        {
            window.Width = size.Width; window.Height = size.Height;
            Pump(); content.UpdateLayout(); canvas.Fit(); Pump();
            var toolbar = (FrameworkElement)window.FindName("ToolBar");
            Check(toolbar.DesiredSize.Width <= content.ActualWidth + 1, $"toolbar fits {size.Width}");
            Check(canvas.ActualWidth >= 280 && canvas.ActualHeight >= 280, $"drawing area usable at {size.Width}");
            var tabs = (System.Windows.Controls.TabControl)window.FindName("InspectorTabs");
            tabs.SelectedIndex = 0; Pump();
            var properties = (FrameworkElement)window.FindName("Properties");
            Check(properties.IsVisible && properties.ActualHeight > 100, $"properties tab displayed at {size.Width}");
            tabs.SelectedIndex = 1; Pump();
            var records = (FrameworkElement)window.FindName("Results");
            Check(records.IsVisible && records.ActualHeight > 80, $"measurements tab displayed at {size.Width}");
            tabs.SelectedIndex = 0; Pump();
            int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            Check(pixels.Count(b => b != 0) > pixels.Length / 4, $"render is not blank at {size.Width}");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = size.Width == 1280 ? "preview.png" : $"preview-{size.Width}.png";
            using var stream = File.Create(Path.Combine(root, "artifacts", file)); encoder.Save(stream);
        }
        var stressShapes = new List<CadShape>();
        for (int i = 0; i < 5001; i++)
        {
            var shape = new CadShape { Layer = "stress", Color = Colors.Red };
            shape.Segments.Add(Segment.Line(new(i % 100, i / 100), new(i % 100 + .6, i / 100 + .6)));
            shape.UpdateBounds(); stressShapes.Add(shape);
        }
        canvas.ShowGrid = false; canvas.Layers = new() { ["stress"] = true }; canvas.SetScene(stressShapes); WaitForPreview(canvas);
        byte[] CaptureCanvas()
        {
            var bitmap = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
        }
        int RedPixels(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 2] > 150 && pixels[i * 4 + 1] < 60);
        Check(RedPixels(CaptureCanvas()) > 1000, "large scene snapshot contains geometry");
        var worldPoint = new Point(20, 20); var priorScreen = canvas.ToScreen(worldPoint);
        var previewField = typeof(CadCanvas).GetField("scenePreview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var previewBeforePan = previewField.GetValue(canvas);
        canvas.PanBy(new(35, 20)); Pump();
        Equal(Geo.Distance(canvas.ToScreen(worldPoint), priorScreen + new Vector(35, 20)), 0, "cached pan preserves camera mapping");
        Check(RedPixels(CaptureCanvas()) > 1000, "cached pan keeps geometry visible");
        canvas.FinishNavigation(); Pump();
        Check(ReferenceEquals(previewBeforePan, previewField.GetValue(canvas)), "fixed-scale pan release reuses complete preview");
        canvas.ZoomBy(1.2); canvas.FinishNavigation(); WaitForPreview(canvas);
        Check(!ReferenceEquals(previewBeforePan, previewField.GetValue(canvas)), "scale change rebuilds preview");
        Check(RedPixels(CaptureCanvas()) > 1000, "settled zoom redraws geometry");
        canvas.SetVectorOutput(true); Pump();
        Check(RedPixels(CaptureCanvas()) > 1000, "vector output bypass retains geometry");
        canvas.SetVectorOutput(false);
        WaitForPreview(canvas);
        canvas.ZoomBy(1.1); canvas.FinishNavigation();
        Check(canvas.IsRendering, "refinement is queued without blocking UI");
        canvas.Layers["stress"] = false; canvas.Refresh(); WaitForPreview(canvas);
        Check(RedPixels(CaptureCanvas()) == 0, "layer change invalidates cached image");
        canvas.Layers["stress"] = true; canvas.Refresh(); Pump();
        canvas.SetScene([]); WaitForPreview(canvas);
        Check(RedPixels(CaptureCanvas()) == 0 && canvas.Shapes.Count == 0, "old background result cannot overwrite a new scene");
        PrintAreaTests(window, canvas);
        window.Close();
    }
    private static void GpuSceneTests(MainWindow window, CadCanvas canvas)
    {
        var data = CadLoader.Load(Path.Combine(root, "samples", "二维测量示例.dwg"));
        window.ApplyDrawing(data); canvas.ShowGrid = false;
        canvas.SetGpuEnabled(true); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount > 0 && canvas.GpuBatchCount > 0, "retained scene prepares geometry and text");
        int builds = canvas.GpuBuildCount;
        for (int i = 0; i < 12; i++) { canvas.ZoomBy(i % 2 == 0 ? 1.2 : 1 / 1.2); canvas.PanBy(new(2, -1)); Pump(); }
        canvas.FinishNavigation(); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount == builds, "zoom and pan reuse geometry and glyphs without rebuilding");
        canvas.Fit(); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount == builds, "fit reuses retained scene");
        window.Width = 1024; Pump(); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount == builds, "resize reuses retained scene");
        canvas.LightBackground = true; canvas.Refresh(); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount == builds + 1, "background color rebuilds appropriate vector colors");
        canvas.ShowLineWeights = true; canvas.Refresh(); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount == builds + 2, "lineweight change invalidates retained style");
        var layer = canvas.Shapes.First().Layer;
        canvas.Layers[layer] = false; canvas.Refresh(); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount == builds + 3, "layer visibility invalidates retained scene");
        canvas.Layers[layer] = true; canvas.LightBackground = false; canvas.ShowLineWeights = false;
        canvas.Refresh(); WaitForPreview(canvas);
        window.Width = 1280; Pump(); canvas.Fit(); WaitForPreview(canvas);
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(root, "artifacts", "gpu-sample.png"))) encoder.Save(stream);
        var screenshotPixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(screenshotPixels, bitmap.PixelWidth * 4, 0);
        Check(Enumerable.Range(0, screenshotPixels.Length / 4).Count(i => screenshotPixels[4 * i + 2] > 180 && screenshotPixels[4 * i + 1] > 180 && screenshotPixels[4 * i] < 100) > 200,
            "GPU screenshot contains yellow drawing text, not just UI or blank surface");
        int rendered = canvas.GpuRenderedFrames;
        canvas.Measurements.Add(new MeasureResult("GPU overlay", [new(0, 0), new(120, 0)], false)); canvas.InvalidateVisual(); Pump();
        Check(canvas.GpuRenderedFrames == rendered, "measurement overlay reuses native scene frame");
        canvas.ClearMeasurements();
        window.ExportPng(Path.Combine(root, "artifacts", "gpu-export.png"), 1200, 800);
        Check(new FileInfo(Path.Combine(root, "artifacts", "gpu-export.png")).Length > 2000, "GPU mode keeps independent PNG renderer");
        int beforeRefine = canvas.GpuBuildCount;
        canvas.ZoomBy(128); WaitForPreview(canvas);
        Check(canvas.GpuBuildCount == beforeRefine && canvas.GpuEnabled, "deep zoom preserves analytic curves without rebuilding the scene");
        GpuPixelTests(canvas);
        canvas.SetScene(Enumerable.Range(0, 20000).Select(i =>
        {
            var shape = new CadShape { Color = Colors.Red };
            shape.Segments.Add(Segment.Line(new(i, 0), new(i, 100))); shape.UpdateBounds(); return shape;
        }).ToList()); Pump(); canvas.SetScene([]); WaitForPreview(canvas);
        Check(canvas.GpuBatchCount == 0, "old GPU preparation cannot overwrite replaced drawing");
        canvas.ZoomBy(1.01); canvas.SetGpuEnabled(false); WaitForPreview(canvas);
        Check(!canvas.GpuEnabled && canvas.GpuBatchCount == 0, "legacy fallback releases retained scene");
    }
    private static void GpuPixelTests(CadCanvas canvas)
    {
        CadShape Box(double x, System.Windows.Media.Color color)
        {
            var shape = new CadShape { Color = color, Filled = true, Closed = true };
            Point[] points = [new(x, 10), new(x + 20, 10), new(x + 20, 30), new(x, 30)];
            for (int i = 0; i < 4; i++) shape.Segments.Add(Segment.Line(points[i], points[(i + 1) % 4]));
            shape.UpdateBounds(); return shape;
        }
        var green = Box(60, Colors.Lime); green.Clip = new Rect(60, 10, 10, 20);
        canvas.Layers = new(); canvas.SetScene([Box(10, Colors.Red), green]);
        canvas.FitRect(new Rect(0, 0, 100, 50)); WaitForPreview(canvas);
        byte[] Capture()
        {
            var image = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(canvas); var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
        }
        System.Windows.Media.Color Pixel(byte[] bytes, Point world)
        {
            var p = canvas.ToScreen(world); int i = ((int)p.Y * (int)canvas.ActualWidth + (int)p.X) * 4;
            return System.Windows.Media.Color.FromRgb(bytes[i + 2], bytes[i + 1], bytes[i]);
        }
        var first = Capture();
        Check(Pixel(first, new(20, 20)).R > 220, "GPU mesh fills red polygon at expected world location");
        Check(Pixel(first, new(65, 20)).G > 220 && Pixel(first, new(75, 20)).G < 100, "GPU scissor preserves drawing clip region");
        int frames = canvas.GpuRenderedFrames; canvas.PanBy(new(30, 12)); WaitForPreview(canvas); var moved = Capture();
        Check(canvas.GpuRenderedFrames > frames && !first.SequenceEqual(moved) && Pixel(moved, new(20, 20)).R > 220,
            "GPU pan updates surface pixels and preserves world mapping");
        var ellipse = new CadShape { Color = Colors.Red, StrokeWidth = 3 };
        ellipse.Segments.Add(Segment.Arc(new(0, 0), 10, 0, Geo.Tau).Transform(new Matrix(2, 0, 0, 1, 50, 25)));
        ellipse.UpdateBounds(); canvas.SetScene([ellipse]); canvas.FitRect(new Rect(0, 0, 100, 50)); WaitForPreview(canvas);
        var oval = Capture();
        Check(Pixel(oval, new(70, 25)).R > 150 && Pixel(oval, new(50, 35)).R > 150 && Pixel(oval, new(50, 25)).R < 100,
            "GPU analytic ellipse preserves both radii and an unfilled center");
        var top = Box(20, Colors.Lime); top.Clip = new Rect(20, 10, 5, 20);
        canvas.SetScene([Box(10, Colors.Red), top, Box(25, Colors.Blue)]);
        canvas.FitRect(new Rect(0, 0, 100, 50)); WaitForPreview(canvas);
        var overlapping = Capture();
        Check(Pixel(overlapping, new(15, 20)).R > 220 && Pixel(overlapping, new(22, 20)).G > 220
            && Pixel(overlapping, new(27, 20)).B > 220 && canvas.GpuVisibleCount == 3,
            "GPU merged submissions preserve overlapping primitive order and clip transitions");
        foreach (bool clockwise in new[] { false, true })
        {
            canvas.ShowLineWeights = true;
            var arc = new CadShape { Color = Colors.Red, StrokeWidth = 3 };
            arc.Segments.Add(Segment.Arc(new(50, 25), 10, clockwise ? Math.PI / 2 : Math.PI * 1.5,
                clockwise ? -Math.PI : Math.PI)); arc.UpdateBounds();
            canvas.SetScene([arc]); canvas.FitRect(new Rect(0, 0, 100, 50)); WaitForPreview(canvas);
            var half = Capture();
            Check(Pixel(half, new(60, 25)).R > 150 && Pixel(half, new(40, 25)).R < 100
                && Pixel(half, new(50, 15)).R > 150 && Pixel(half, new(50, 35)).R > 150,
                $"GPU analytic arc clips the missing half and preserves endpoints (clockwise={clockwise})");
        }
        var filled = new CadShape { Color = Colors.Lime, Filled = true, Closed = true };
        filled.Segments.Add(Segment.Arc(new(50, 25), 10, 0, Geo.Tau)); filled.UpdateBounds();
        canvas.SetScene([Box(10, Colors.Red), filled, Box(45, Colors.Blue)]); canvas.FitRect(new Rect(0, 0, 100, 50)); WaitForPreview(canvas);
        int beforeRefinement = canvas.GpuBuildCount; canvas.ZoomBy(128); WaitForPreview(canvas);
        Check(canvas.GpuEnabled && canvas.GpuBuildCount == beforeRefinement + 1,
            "filled curved regions still refine asynchronously when deep zoom requires more detail");
        canvas.FitRect(new Rect(0, 0, 100, 50)); WaitForPreview(canvas); var refined = Capture();
        Check(Pixel(refined, new(20, 20)).R > 220 && Pixel(refined, new(42, 25)).G > 220 && Pixel(refined, new(50, 25)).B > 220,
            "curve-only GPU replacement retains interleaved static geometry and overlap order");
    }
    private static void PrintAreaTests(MainWindow window, CadCanvas canvas)
    {
        CadShape Square(double x, System.Windows.Media.Color color)
        {
            var shape = new CadShape { Color = color, Filled = true, Closed = true };
            Point[] points = [new(x, 10), new(x + 20, 10), new(x + 20, 30), new(x, 30)];
            for (int i = 0; i < 4; i++) shape.Segments.Add(Segment.Line(points[i], points[(i + 1) % 4]));
            shape.UpdateBounds(); return shape;
        }
        canvas.Layers = new(); canvas.SetScene([Square(10, Colors.Red), Square(100, Colors.Lime)]);
        canvas.ShowGrid = false; canvas.FitRect(new Rect(0, 0, 150, 70)); Pump();
        var button = (System.Windows.Controls.Button)window.FindName("PrintAreaButton");
        void ClickButton() => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var corner = typeof(CadCanvas).GetMethod("AcceptPrintAreaCorner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ClickButton();
        Check(canvas.Mode == ToolMode.PrintArea && ToolIcon.GetIsActive(button), "print area button enters region tool");
        corner.Invoke(canvas, [new Point(50, 50)]);
        corner.Invoke(canvas, [new Point(0, 0)]); Pump();
        Check(canvas.PrintArea == new Rect(0, 0, 50, 50) && canvas.Mode == ToolMode.Select, "two print corners normalize to world rectangle");
        Check(ToolIcon.GetIsActive(button) && ((SolidColorBrush)button.Foreground).Color == Colors.Orange, "configured print region remains orange after selection ends");
        var output = canvas.CreateOutputDrawing();
        byte[] RenderOutput((DrawingGroup Drawing, Size Size) item)
        {
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawDrawing(item.Drawing);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(item.Size.Width), (int)Math.Ceiling(item.Size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
        }
        var pixels = RenderOutput(output);
        Check(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[4 * i + 2] > 200 && pixels[4 * i + 1] < 50), "print crop includes inside geometry");
        Check(!Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[4 * i + 1] > 200 && pixels[4 * i + 2] < 50), "print crop excludes outside geometry");
        Check(!Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[4 * i + 2] > 200 && pixels[4 * i + 1] > 100), "orange region border is not exported");
        var content = (FrameworkElement)window.Content;
        var screenshot = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
        using (var stream = File.Create(Path.Combine(root, "artifacts", "print-area-ui.png"))) encoder.Save(stream);
        canvas.ZoomBy(2); canvas.PanBy(new(2000, 1500)); canvas.FinishNavigation(); Pump();
        var movedOutput = canvas.CreateOutputDrawing();
        Check(output.Size == movedOutput.Size && pixels.SequenceEqual(RenderOutput(movedOutput)), "offscreen print crop is unchanged by zoom and pan");
        string pngPath = Path.Combine(root, "artifacts", "print-area-export.png");
        window.ExportPng(pngPath);
        using (var stream = File.OpenRead(pngPath))
        {
            var png = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(png.PixelWidth == png.PixelHeight && png.PixelWidth > 100, "PNG uses region dimensions instead of canvas dimensions");
        }
        window.ExportPng(pngPath, 1024, 1024);
        using (var stream = File.OpenRead(pngPath))
        {
            var png = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(png.PixelWidth == 1024 && png.PixelHeight == 1024, "custom PNG resolution matches encoded pixel dimensions");
            var customPixels = new byte[png.PixelWidth * png.PixelHeight * 4]; png.CopyPixels(customPixels, png.PixelWidth * 4, 0);
            Check(Enumerable.Range(0, customPixels.Length / 4).Any(i => customPixels[i * 4 + 2] > 200 && customPixels[i * 4 + 1] < 50)
                && !Enumerable.Range(0, customPixels.Length / 4).Any(i => customPixels[i * 4 + 1] > 200 && customPixels[i * 4 + 2] < 50),
                "high resolution PNG preserves print crop content");
        }
        bool rejected = false;
        try { window.ExportPng(pngPath, 8193, 100); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "oversized PNG request rejected before rendering");
        PngResolutionDialogTests(window);
        ClickButton(); Pump();
        Check(canvas.PrintArea == null && !ToolIcon.GetIsActive(button), "second print area button click clears active region");
        Check(canvas.CreateOutputDrawing().Size == canvas.RenderSize, "clearing print area restores current viewport output");
        ClickButton(); corner.Invoke(canvas, [new Point(0, 0)]); canvas.CancelSelection();
        Check(canvas.PrintArea == null && !ToolIcon.GetIsActive(button), "cancel unfinished region leaves no active crop");
        canvas.SnapEnabled = true;
        double nearby = 3 / canvas.Zoom;
        ClickButton(); corner.Invoke(canvas, [new Point(10 + nearby, 10 + nearby)]);
        var startField = typeof(CadCanvas).GetField("printAreaStart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Check((Point)startField.GetValue(canvas)! == new Point(10, 10), "print area first corner snaps to geometry endpoint");
        corner.Invoke(canvas, [new Point(30 + nearby, 30 + nearby)]);
        Check(canvas.PrintArea == new Rect(10, 10, 20, 20), "print area second corner uses snapped coordinates");
        ClickButton(); canvas.SnapEnabled = false;
        var freeStart = new Point(10 + nearby, 10 + nearby);
        var freeEnd = new Point(30 + nearby, 30 + nearby);
        ClickButton(); corner.Invoke(canvas, [freeStart]); corner.Invoke(canvas, [freeEnd]);
        Check(canvas.PrintArea == new Rect(freeStart, freeEnd), "disabled print area snapping preserves raw coordinates");
        ClickButton(); canvas.SnapEnabled = true;
        ClickButton(); corner.Invoke(canvas, [new Point(0, 0)]); corner.Invoke(canvas, [new Point(50, 50)]);
        canvas.SetScene([]); Pump();
        Check(canvas.PrintArea == null && !ToolIcon.GetIsActive(button), "changing drawing clears print region");
    }
    private static void PngResolutionDialogTests(MainWindow owner)
    {
        var dialog = new PngExportDialog(new Size(800, 400), 1, true) { Owner = owner };
        dialog.Show(); Pump();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var width = (System.Windows.Controls.TextBox)typeof(PngExportDialog).GetField("widthBox", flags)!.GetValue(dialog)!;
        var height = (System.Windows.Controls.TextBox)typeof(PngExportDialog).GetField("heightBox", flags)!.GetValue(dialog)!;
        var export = (System.Windows.Controls.Button)typeof(PngExportDialog).GetField("exportButton", flags)!.GetValue(dialog)!;
        width.Text = "1600";
        Check(dialog.PixelWidth == 1600 && dialog.PixelHeight == 800 && height.Text == "800", "PNG width edit preserves aspect ratio");
        height.Text = "300";
        Check(dialog.PixelWidth == 600 && dialog.PixelHeight == 300 && width.Text == "600", "PNG height edit preserves aspect ratio");
        width.Text = "0"; Check(!export.IsEnabled, "zero PNG dimension disables export");
        width.Text = "8193"; Check(!export.IsEnabled, "oversized PNG dimension disables export");
        width.Text = "abc"; Check(!export.IsEnabled, "non-numeric PNG dimension disables export");
        var preset = Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(b => Equals(b.Content, "4 倍"));
        preset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
        Check(export.IsEnabled && dialog.PixelWidth == 3200 && dialog.PixelHeight == 1600, "PNG preset restores valid proportional dimensions");
        width.Focus(); width.SelectAll(); Pump();
        var content = (FrameworkElement)dialog.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(root, "artifacts", "png-resolution-dialog.png"))) encoder.Save(stream);
        dialog.Close();
        var exportButton = Descendants(owner).OfType<System.Windows.Controls.Button>()
            .Single(b => Equals(b.Content, "导出 PNG"));
        exportButton.Focus();
        owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            owner.OwnedWindows.OfType<PngExportDialog>().Single().Close()));
        exportButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Pump();
        Check(((CadCanvas)owner.FindName("Canvas")).IsKeyboardFocused && !exportButton.IsKeyboardFocused,
            "cancel PNG export returns keyboard focus to drawing");
    }
    private static void ReopenDrawingTests(MainWindow window)
    {
        // Match Application.Run's UI context while this test manually pumps Dispatcher.
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
        var canvas = (CadCanvas)window.FindName("Canvas");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var loadingField = typeof(MainWindow).GetField("loading", flags)!;
        string recentFile = (string)typeof(MainWindow).GetField("recentPath", flags)!.GetValue(window)!;
        byte[]? recentBackup = File.Exists(recentFile) ? File.ReadAllBytes(recentFile) : null;
        try
        {
        void WaitForLoad()
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (loadingField.GetValue(window) != null)
            {
                if (timeout.Elapsed.TotalSeconds > 20) throw new Exception("Drawing load timed out");
                Thread.Sleep(5); Pump();
            }
            Pump();
        }
        var firstPath = Path.Combine(root, "samples", "二维测量示例.dwg");
        var secondPath = Path.Combine(root, "artifacts", "reload-second.dxf");
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(new Line { StartPoint = new XYZ(1000, 2000, 0), EndPoint = new XYZ(1100, 2100, 0) });
        document.Entities.Add(new TextEntity("SECOND-DRAWING") { InsertPoint = new XYZ(1000, 2120, 0), Height = 10 });
        DxfWriter.Write(secondPath, document);
        window.LoadFile(firstPath); WaitForLoad();
        var firstShapes = canvas.Shapes;
        canvas.SnapEnabled = false; canvas.SetMode(ToolMode.Distance);
        canvas.AcceptPoint(new(0, 0)); canvas.AcceptPoint(new(120, 80));
        canvas.TogglePrintArea();
        var corner = typeof(CadCanvas).GetMethod("AcceptPrintAreaCorner", flags)!;
        corner.Invoke(canvas, [new Point(0, 0)]); corner.Invoke(canvas, [new Point(120, 80)]);
        ((System.Windows.Controls.TextBox)window.FindName("SearchQuery")).Text = "安装板";
        ((System.Windows.Controls.Button)window.FindName("SearchNextButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.LoadFile(secondPath); WaitForLoad();
        Check(!ReferenceEquals(canvas.Shapes, firstShapes) && canvas.Shapes.Any(s => s.Text == "SECOND-DRAWING") && canvas.Shapes.Count == 2,
            "async opening second file replaces old geometry");
        Check(canvas.Measurements.Count == 0 && canvas.PrintArea == null && ((System.Windows.Controls.ListBox)window.FindName("Results")).Items.Count == 0,
            "opening another file clears measurements and print region");
        Check(((System.Windows.Controls.ListBox)window.FindName("SearchResults")).Items.Count == 0 && window.Title.Contains("reload-second.dxf"),
            "reopened file title and search results agree with new scene");
        var secondShapes = canvas.Shapes;
        window.LoadFile(secondPath); WaitForLoad();
        Check(!ReferenceEquals(canvas.Shapes, secondShapes) && canvas.Shapes.Count == 2, "reopening same path refreshes scene");
        var spaces = (System.Windows.Controls.ComboBox)window.FindName("Spaces");
        spaces.SelectedIndex = 1;
        window.LoadFile(firstPath); WaitForLoad();
        Check(canvas.IsModel && Equals(spaces.SelectedItem, "模型") && canvas.Shapes.Any(s => s.Text?.Contains("安装板") == true),
            "opening from paper space displays new model space");
        window.LoadFile(firstPath); window.LoadFile(secondPath); WaitForLoad();
        Check(canvas.Shapes.Count == 2 && canvas.Shapes.Any(s => s.Text == "SECOND-DRAWING"), "rapid file replacement keeps newest load");
        canvas.SnapEnabled = true;
        }
        finally
        {
            if (recentBackup != null) File.WriteAllBytes(recentFile, recentBackup);
            else if (File.Exists(recentFile)) File.Delete(recentFile);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private static void AnnotationPerformance(string label)
    {
        var canvas = new CadCanvas { ShowGrid = false };
        var window = new Window { Content = canvas, Width = 1000, Height = 700 }; window.Show(); Pump();
        canvas.FitRect(new Rect(0, 0, 200, 150));
        var curved = new CadShape();
        var transform = Matrix.Identity; transform.Scale(1, .7);
        for (int i = 0; i < 256; i++) curved.Segments.Add(Segment.Arc(new(i % 20 * 8, i / 20 * 8), 3, 0, Geo.Tau).Transform(transform));
        curved.UpdateBounds();
        for (int i = 0; i < 30; i++) canvas.Measurements.Add(new MeasureResult($"距离 {i + 10}.000 mm · ΔX 10.000 · ΔY 0.000", [new(10, i * 4), new(70, i * 4)], false));
        canvas.Measurements.Add(new MeasureResult("复杂曲线实体测量", [curved.Segments[0].At(0)], false, curved));
        var render = typeof(CadCanvas).GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        void Draw() { var visual = new DrawingVisual(); using var context = visual.RenderOpen(); render.Invoke(canvas, [context]); }
        Draw();
        var timings = new List<double>(); long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 40; i++) { var watch = System.Diagnostics.Stopwatch.StartNew(); Draw(); timings.Add(watch.Elapsed.TotalMilliseconds); }
        var report = new { label, records = canvas.Measurements.Count, frames = timings.Count, averageMs = timings.Average(), maxMs = timings.Max(), allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before };
        string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "artifacts", $"annotation-performance-{label}.json"), json); Console.WriteLine(json);
        window.Close();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void WaitForPreview(CadCanvas canvas)
    {
        Pump();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (canvas.IsRendering)
        {
            if (watch.Elapsed.TotalSeconds > 60) throw new Exception("Preview worker timed out");
            Thread.Sleep(5); Pump();
        }
        Pump();
    }
    private static void ScreenPerformance(string path, bool gpu, string scenario)
    {
        if (scenario is not ("mixed" or "pan-full" or "pan-local" or "zoom-burst" or "input-stream" or "input-stream-4k" or "input-stream-long-4k"))
            throw new ArgumentException("Unsupported screen performance scenario: " + scenario);
        Environment.SetEnvironmentVariable("TINYCAD_LEGACY_RENDER", "1");
        var app = new TinyCAD.App(); app.InitializeComponent();
        var window = new MainWindow(); window.Show(); Pump();
        var canvas = (CadCanvas)window.FindName("Canvas");
        var watch = System.Diagnostics.Stopwatch.StartNew(); var data = CadLoader.Load(path);
        double loadMs = watch.Elapsed.TotalMilliseconds;
        canvas.SetGpuEnabled(gpu); watch.Restart(); window.ApplyDrawing(data); WaitForPreview(canvas);
        double sceneMs = watch.Elapsed.TotalMilliseconds;
        bool inputStream = scenario.StartsWith("input-stream", StringComparison.Ordinal);
        bool burst = scenario == "zoom-burst" || inputStream;
        if (burst)
        {
            double dpi = VisualTreeHelper.GetDpi(canvas).DpiScaleX;
            bool large = scenario.EndsWith("-4k", StringComparison.Ordinal);
            canvas.Width = (large ? 3840 : 1920) / dpi; canvas.Height = (large ? 2160 : 1080) / dpi; Pump();
        }
        if (scenario == "pan-local" || burst)
        {
            // Focus the densest cell, rather than the drawing's often empty geometric center.
            var bounds = Rect.Empty;
            foreach (var shape in canvas.Shapes) bounds.Union(shape.Bounds);
            double width = Math.Max(bounds.Width / 8, 1), height = Math.Max(bounds.Height / 8, 1);
            var cell = canvas.Shapes.Where(s => !s.Bounds.IsEmpty).GroupBy(s => (
                X: Math.Clamp((int)((s.Bounds.X + s.Bounds.Width / 2 - bounds.X) / width), 0, 7),
                Y: Math.Clamp((int)((s.Bounds.Y + s.Bounds.Height / 2 - bounds.Y) / height), 0, 7)))
                .OrderByDescending(g => g.Count()).First().Key;
            canvas.FitRect(new Rect(bounds.X + cell.X * width, bounds.Y + cell.Y * height, width, height));
            canvas.FinishNavigation(); WaitForPreview(canvas);
        }
        if (gpu && !canvas.GpuEnabled) throw new Exception("Native GPU failed: " + canvas.GpuFailure);
        Console.WriteLine($"Prepared {canvas.Shapes.Count} shapes; {canvas.GpuBatchCount} batches; {sceneMs:F0} ms");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(canvas.Shapes.GroupBy(s => new { s.Type, s.Filled, Clipped = s.Clip != null }).Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(g => g.Count)));
        Console.WriteLine("Refinement candidates: " + System.Text.Json.JsonSerializer.Serialize(canvas.Shapes
            .Where(s => s.Text == null && s.Segments.Any(segment => segment.IsArc) && (s.Filled || s.Dashes is { Length: > 0 }))
            .GroupBy(s => new { s.Type, s.Filled, Dashed = s.Dashes is { Length: > 0 } }).Select(g => new { g.Key, Count = g.Count() })));
        int builds = canvas.GpuBuildCount;
        double initialZoom = canvas.Zoom;
        var initialCenter = canvas.ToWorld(new(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
        var frameGaps = new List<double>(); var actionCosts = new List<double>(); var nativeCosts = new List<double>();
        var locks = new List<double>(); var draws = new List<double>(); var ends = new List<double>();
        var total = System.Diagnostics.Stopwatch.StartNew();
        var inputQueueMs = new List<double>();
        var slowInputs = new List<object>();
        var allFrameGaps = new List<double>(); double firstCallbackMs = 0;
        int inputIssued = 0, inputApplied = 0, inputProducerDone = 0, maxPendingInput = 0;
        int requestedInputs = inputStream ? (scenario.Contains("long", StringComparison.Ordinal) ? 7200 : 2400) : 0;
        using var inputCancellation = new CancellationTokenSource();
        Thread? inputProducer = null;
        int renderedStart = canvas.GpuRenderedFrames;
        var loop = new System.Windows.Threading.DispatcherFrame();
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        timeout.Tick += (_, _) => loop.Continue = false;
        TimeSpan previous = TimeSpan.Zero; int count = 0;
        double previousWall = 0;
        var slowFrames = new List<object>();
        TimeSpan previousGcPause = GC.GetTotalPauseDuration();
        EventHandler handler = (_, e) =>
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == previous) return;
            double wall = total.Elapsed.TotalMilliseconds;
            var gcPause = GC.GetTotalPauseDuration();
            if (previous == TimeSpan.Zero) firstCallbackMs = wall;
            else allFrameGaps.Add(wall - previousWall);
            if (count > 5 && wall - previousWall > 40) slowFrames.Add(new {
                frame = count, elapsed = wall, gap = wall - previousWall, gcPauseMs = (gcPause - previousGcPause).TotalMilliseconds,
                generation2 = GC.CollectionCount(2), builds = canvas.GpuBuildCount, nativeMs = canvas.GpuFrameMs,
                lockMs = canvas.GpuLockMs, drawMs = canvas.GpuDrawMs, endMs = canvas.GpuEndMs });
            previousGcPause = gcPause;
            if (previous != TimeSpan.Zero && count > 5) frameGaps.Add(wall - previousWall);
            previousWall = wall;
            previous = time;
            nativeCosts.Add(canvas.GpuFrameMs);
            locks.Add(canvas.GpuLockMs); draws.Add(canvas.GpuDrawMs); ends.Add(canvas.GpuEndMs);
            watch.Restart();
            int stage = count++ % 80;
            if (inputStream) { }
            else if (scenario.StartsWith("pan-")) canvas.PanBy(stage < 40 ? new(5, 2) : new(-5, -2));
            else if (burst) { for (int i = 0; i < 6; i++) canvas.ZoomBy(stage % 40 < 20 ? 1.02 : 1 / 1.02); }
            else if (stage < 20) canvas.ZoomBy(1.08);
            else if (stage < 40) canvas.PanBy(new(8, 3));
            else if (stage < 60) canvas.ZoomBy(1 / 1.08);
            else canvas.PanBy(new(-8, -3));
            if (!inputStream) actionCosts.Add(watch.Elapsed.TotalMilliseconds);
            if ((inputStream ? Volatile.Read(ref inputProducerDone) != 0 && inputApplied == Volatile.Read(ref inputIssued) && !canvas.IsRendering
                : count >= (burst ? 640 : 160)) || total.Elapsed.TotalSeconds >= 45) loop.Continue = false;
        };
        if (inputStream)
        {
            inputProducer = new Thread(() =>
            {
                for (int i = 0; i < requestedInputs && !inputCancellation.IsCancellationRequested; i++)
                {
                    double target = i * 1000.0 / 240;
                    while (total.Elapsed.TotalMilliseconds < target && !inputCancellation.IsCancellationRequested) Thread.Sleep(1);
                    if (inputCancellation.IsCancellationRequested) break;
                    int index = i; double queuedAt = total.Elapsed.TotalMilliseconds;
                    Interlocked.Increment(ref inputIssued);
                    canvas.Dispatcher.InvokeAsync(() =>
                    {
                        maxPendingInput = Math.Max(maxPendingInput, Volatile.Read(ref inputIssued) - inputApplied);
                        double appliedAt = total.Elapsed.TotalMilliseconds, queueMs = appliedAt - queuedAt;
                        inputQueueMs.Add(queueMs);
                        if (queueMs > 40 && slowInputs.Count < 30) slowInputs.Add(new { index, queuedAt, appliedAt, queueMs,
                            nativeMs = canvas.GpuFrameMs, lockMs = canvas.GpuLockMs, drawMs = canvas.GpuDrawMs, endMs = canvas.GpuEndMs,
                            builds = canvas.GpuBuildCount });
                        var action = System.Diagnostics.Stopwatch.StartNew();
                        canvas.ZoomBy(index % 120 < 60 ? 1.04 : 1 / 1.04);
                        actionCosts.Add(action.Elapsed.TotalMilliseconds); inputApplied++;
                    }, System.Windows.Threading.DispatcherPriority.Input);
                }
                Volatile.Write(ref inputProducerDone, 1);
            }) { IsBackground = true, Name = "TinyCAD input pressure test" };
            inputProducer.Start();
        }
        timeout.Start(); CompositionTarget.Rendering += handler;
        try { System.Windows.Threading.Dispatcher.PushFrame(loop); }
        finally
        {
            CompositionTarget.Rendering -= handler; timeout.Stop(); inputCancellation.Cancel(); inputProducer?.Join(1000);
        }
        canvas.FinishNavigation(); WaitForPreview(canvas);
        var bitmap = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string label = (gpu ? "gpu" : "legacy") + "-" + scenario;
        using (var stream = File.Create(Path.Combine(root, "artifacts", $"screen-{label}.png"))) encoder.Save(stream);
        double Percentile(List<double> list, double p) => list.Count == 0 ? 0 : list.Order().ElementAt((int)((list.Count - 1) * p));
        double zoomError = Math.Abs(canvas.Zoom / initialZoom - 1);
        double centerError = (canvas.ToScreen(initialCenter) - new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2)).Length;
        bool inputValidationPassed = !inputStream || (inputIssued == requestedInputs && inputApplied == requestedInputs
            && zoomError < 1e-8 && centerError < .001 && (!gpu || canvas.GpuEnabled && canvas.GpuRenderedFrames > renderedStart));
        var report = new { mode = label, scenario, source = Path.GetFileName(path), tier = canvas.HardwareTier, frames = count, loadMs, sceneMs,
            canvasWidth = canvas.ActualWidth, canvasHeight = canvas.ActualHeight,
            physicalWidth = canvas.ActualWidth * VisualTreeHelper.GetDpi(canvas).DpiScaleX,
            physicalHeight = canvas.ActualHeight * VisualTreeHelper.GetDpi(canvas).DpiScaleY,
            nativeEnabled = canvas.GpuEnabled, nativeFailure = canvas.GpuFailure, nativeVisible = canvas.GpuVisibleCount,
            actualNativeFrames = canvas.GpuRenderedFrames - renderedStart,
            nativeMedianMs = Percentile(nativeCosts, .5), nativeP95Ms = Percentile(nativeCosts, .95),
            lockMedianMs = Percentile(locks, .5), drawMedianMs = Percentile(draws, .5), endMedianMs = Percentile(ends, .5),
            objects = canvas.Shapes.Count, batches = canvas.GpuBatchCount, buildsDuringNavigation = canvas.GpuBuildCount - builds,
            medianFrameGapMs = Percentile(frameGaps, .5), p95FrameGapMs = Percentile(frameGaps, .95),
            p99FrameGapMs = Percentile(frameGaps, .99), maxFrameGapMs = frameGaps.DefaultIfEmpty().Max(),
            framesOver50Ms = frameGaps.Count(ms => ms > 50), framesOver100Ms = frameGaps.Count(ms => ms > 100),
            medianInputMs = Percentile(actionCosts, .5), p95InputMs = Percentile(actionCosts, .95),
            requestedInputs, inputIssued, inputApplied, maxPendingInput, inputValidationPassed,
            firstCallbackMs, maxIncludingWarmupMs = allFrameGaps.DefaultIfEmpty().Max(), slowInputs,
            finalZoomRelativeError = zoomError, finalCenterErrorPixels = centerError,
            medianQueueMs = Percentile(inputQueueMs, .5), p95QueueMs = Percentile(inputQueueMs, .95),
            p99QueueMs = Percentile(inputQueueMs, .99), maxQueueMs = inputQueueMs.DefaultIfEmpty().Max(),
            note = "Stopwatch wall-clock intervals between WPF rendering callbacks; GPU completion verified before presenting each shared surface. Not display FPS.",
            slowFrames, frameGaps, actionCosts };
        string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "artifacts", $"screen-{label}.json"), json);
        Console.WriteLine(json); window.Close();
        if (!inputValidationPassed) throw new InvalidOperationException("Input test did not process all expected events, restore the camera, or complete GPU drawing; results are not a valid performance pass.");
    }
    private static void PerformanceTest(string path, string label, bool panOnly = false)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var data = CadLoader.Load(path);
        double loadMs = timer.Elapsed.TotalMilliseconds;
        var app = new TinyCAD.App(); app.InitializeComponent();
        var window = new MainWindow(); window.Show(); Pump();
        timer.Restart(); window.ApplyDrawing(data); Pump();
        WaitForPreview((CadCanvas)window.FindName("Canvas"));
        double sceneMs = timer.Elapsed.TotalMilliseconds;
        var canvas = (CadCanvas)window.FindName("Canvas");
        double RenderFrame(Action action)
        {
            timer.Restart(); action(); Pump();
            var bitmap = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            return timer.Elapsed.TotalMilliseconds;
        }
        var zoom = new List<double>(); var pan = new List<double>();
        if (!panOnly) for (int i = 0; i < 8; i++) zoom.Add(RenderFrame(() => canvas.ZoomBy(1.15)));
        double zoomSettleMs = RenderFrame(canvas.FinishNavigation);
        var releases = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            pan.Add(RenderFrame(() => canvas.PanBy(new Vector(12, 5))));
            if (panOnly) releases.Add(RenderFrame(canvas.FinishNavigation));
        }
        double panSettleMs = RenderFrame(canvas.FinishNavigation);
        WaitForPreview(canvas);
        var preview = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32); preview.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
        using (var stream = File.Create(Path.Combine(root, "artifacts", $"performance-{label}.png"))) encoder.Save(stream);
        var report = new { label, loadMs, sceneMs, objects = data.Spaces["模型"].Count, textObjects = data.Spaces["模型"].Count(s => s.Text != null), zoomMs = zoom, panMs = pan, releaseMs = releases, zoomSettleMs, panSettleMs };
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "artifacts", $"performance-{label}.json"), json); Console.WriteLine(json);
        window.Close();
    }
    private static void InterruptedPreviewTest(string path)
    {
        var app = new TinyCAD.App(); app.InitializeComponent();
        var window = new MainWindow(); window.Show(); Pump();
        window.ApplyDrawing(CadLoader.Load(path));
        var canvas = (CadCanvas)window.FindName("Canvas"); WaitForPreview(canvas);
        canvas.ZoomBy(1.05); canvas.FinishNavigation();
        Check(canvas.IsRendering, "large drawing refinement runs asynchronously");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var gaps = new List<double>(); var costs = new List<double>(); double last = 0;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            double now = watch.Elapsed.TotalMilliseconds; gaps.Add(now - last); last = now;
            var input = System.Diagnostics.Stopwatch.StartNew();
            canvas.ZoomBy(1.015); canvas.FinishNavigation(); costs.Add(input.Elapsed.TotalMilliseconds);
            if (gaps.Count == 24) timer.Stop();
        };
        timer.Start();
        while (timer.IsEnabled)
        {
            if (watch.Elapsed.TotalSeconds > 30) throw new Exception("Input responsiveness test timed out");
            Thread.Sleep(2); Pump();
        }
        WaitForPreview(canvas);
        var previewZoom = (double)typeof(CadCanvas).GetField("previewZoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(canvas)!;
        Equal(previewZoom, canvas.Zoom, "only latest zoom preview is accepted");
        Check(costs.Max() < 250, "zoom requests do not wait for heavy rendering");
        var report = new { objects = canvas.Shapes.Count, inputIntervalMs = 50, inputGapMs = gaps, zoomRequestMs = costs, finalZoom = canvas.Zoom, acceptedPreviewZoom = previewZoom };
        string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "artifacts", "performance-interrupted-preview.json"), json); Console.WriteLine(json);
        window.Close();
    }
    private static void CorpusTest(string directory)
    {
        var report = new List<object>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(p => new[] { ".dwg", ".dxf" }.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var drawing = CadLoader.Load(path);
                report.Add(new { file = Path.GetRelativePath(directory, path), success = true, ms = watch.ElapsedMilliseconds, shapes = drawing.Spaces["模型"].Count, warnings = drawing.Warnings });
                Console.WriteLine($"CORPUS OK: {Path.GetFileName(path)} - {drawing.Spaces["模型"].Count} shapes, {drawing.Warnings.Count} warning categories");
            }
            catch (Exception ex)
            {
                report.Add(new { file = Path.GetRelativePath(directory, path), success = false, error = ex.Message });
                Console.WriteLine($"CORPUS FAILED: {Path.GetFileName(path)} - {ex.Message}");
            }
        }
        File.WriteAllText(Path.Combine(root, "artifacts", "corpus-report.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
