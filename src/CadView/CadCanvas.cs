using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TinyCAD;

public enum ToolMode { Select, Distance, Chain, Angle, Area, Radius, Entity, WindowZoom, PrintArea }
public sealed class MeasureResult(string text, List<Point> points, bool closed, CadShape? shape = null) : System.ComponentModel.INotifyPropertyChanged
{
    public string Text { get; } = text;
    public List<Point> Points { get; } = points;
    public bool Closed { get; } = closed;
    public CadShape? Shape { get; } = shape;
    public int Number { get; init; }
    public Func<string>? TextFactory { get; init; }
    private string? localizedText;
    public string CurrentText => localizedText ?? Text;
    public string RecordTitle => L.F($"记录 {Number:00}");
    public string DisplayText => CurrentText.Replace(" · ", "\n");
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage()
    {
        localizedText = TextFactory?.Invoke() ?? Text;
        PropertyChanged?.Invoke(this, new(nameof(DisplayText)));
        PropertyChanged?.Invoke(this, new(nameof(RecordTitle)));
    }
}

public sealed partial class CadCanvas : FrameworkElement
{
    public List<CadShape> Shapes { get; private set; } = [];
    public Dictionary<string, bool> Layers { get; set; } = [];
    public ToolMode Mode { get; private set; }
    public bool SnapEnabled { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowLineWeights { get; set; }
    public bool LightBackground { get; set; }
    public bool IsModel { get; set; } = true;
    public int Precision { get; set; } = 3;
    public string UnitLabel => DisplayLabel(UnitKey);
    public List<MeasureResult> Measurements { get; } = [];
    public MeasureResult? FocusedMeasurement { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? CoordinatesChanged;
    public event Action<string>? Selection;
    public event Action<MeasureResult>? Measured;
    public event Action<ToolMode>? ModeChanged;
    public event Action<bool>? SnapToggled;
    public double Zoom { get; private set; } = 1;
    private Vector offset;
    private Point cursorWorld;
    private string snapName = "";
    private readonly List<Point> pending = [];
    private bool panning;
    private Point lastMouse;
    private Point? zoomStart;
    private CadShape? selected;
    private DrawingGroup? drawing;
    public bool VectorOutput { get; private set; }
    public void SetVectorOutput(bool enabled)
    {
        VectorOutput = enabled;
        if (enabled) { CancelPreview(); FinishNavigation(); drawing = null; }
        else if (Shapes.Count >= 5000) { scenePreview = null; requestedGeneration = -1; }
        InvalidateVisual(); UpdateLayout();
    }
    private bool navigating;
    private readonly DispatcherTimer navigationTimer;
    private readonly Dictionary<(string Text, double Height, Color Color), FormattedText> textCache = [];
    private readonly Dictionary<Color, Brush> brushCache = [];
    private readonly Dictionary<CadShape, Geometry> geometryCache = [];
    private double geometryDetailScale = 1;
    private ShapeIndex index = new([]);
    private readonly Brush accent = new SolidColorBrush(Color.FromRgb(128, 199, 255));
    public CadCanvas()
    {
        Focusable = true; ClipToBounds = true; Cursor = Cursors.Cross;
        navigationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        navigationTimer.Tick += (_, _) => { if (!panning) FinishNavigation(); };
        Unloaded += (_, _) => { navigationTimer.Stop(); CancelPreview(); StopNativeRendering(); };
        Loaded += (_, _) => { if (GpuEnabled && nativeNavigationPending) RequestNativeNavigationFrame(); };
        SizeChanged += (_, e) =>
        {
            if (e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0 &&
                e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                offset += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2,
                    (e.NewSize.Height - e.PreviousSize.Height) / 2);
            }
            Refresh();
        };
        LostMouseCapture += (_, _) => { panning = false; draggingLabel = null; UpdateLabelCursor(Mouse.GetPosition(this)); };
        MouseLeave += (_, _) =>
        {
            if (draggingLabel == null && !panning) Cursor = Cursors.Cross;
            if (snapName.Length > 0) { snapName = ""; InvalidateVisual(); }
        };
    }
    public Point ToScreen(Point p) => new(p.X * Zoom + offset.X, -p.Y * Zoom + offset.Y);
    public Point ToWorld(Point p) => new((p.X - offset.X) / Zoom, -(p.Y - offset.Y) / Zoom);
    private Matrix ViewMatrix => new(Zoom, 0, 0, -Zoom, offset.X, offset.Y);
    public void SetScene(List<CadShape> shapes)
    {
        ResetNativeScene();
        Shapes = shapes; index = new(shapes); selected = null; zoomStart = null; ClearPrintArea(); ClearMeasurements(); geometryCache.Clear(); textCache.Clear(); Fit();
        if (Mode == ToolMode.PrintArea) SetMode(ToolMode.Select);
        sceneBounds = Rect.Empty;
        foreach (var shape in shapes) sceneBounds.Union(shape.Bounds);
    }
    public void SetMode(ToolMode mode)
    {
        if (!IsModel && mode is not ToolMode.Select and not ToolMode.WindowZoom and not ToolMode.PrintArea)
        { Status?.Invoke(L.T("请切换到模型空间测量。布局只用于查看，避免纸面比例造成误测。")); return; }
        Mode = mode; pending.Clear(); zoomStart = null; printAreaStart = null; ModeChanged?.Invoke(mode); Focus(); InvalidateVisual();
        Status?.Invoke(mode switch { ToolMode.Distance => L.T("依次点击两点测量距离 · Shift 正交 · Esc 退出"), ToolMode.Chain => L.T("连续取点 · 双击或 Enter 完成 · 右键取消"), ToolMode.Area => L.T("依次取点 · 点击起点或双击闭合 · 右键取消"), ToolMode.Angle => L.T("依次点击：角的顶点 → 第一边 → 第二边"), ToolMode.Radius => L.T("点击圆或圆弧，读取半径和直径"), ToolMode.Entity => L.T("点击实体，读取长度、周长和封闭面积"), ToolMode.WindowZoom => L.T("依次点击两个角点框选放大 · 右键或 Esc 取消"), _ => L.T("点击查询对象 · 滚轮缩放 · 中键拖动 · F 全图") });
    }
    public void CancelSelection()
    {
        draggingLabel = null; ReleaseMouseCapture();
        UpdateLabelCursor(Mouse.GetPosition(this));
        selected = null;
        FocusedMeasurement = null;
        snapName = "";
        SetMode(ToolMode.Select);
        Selection?.Invoke(L.T("未选中对象"));
        Status?.Invoke(L.T("已取消选择"));
    }
    public void Fit()
    {
        var bounds = Rect.Empty;
        foreach (var s in Shapes.Where(Visible))
        {
            var b = s.Bounds; if (s.Clip is Rect c) b.Intersect(c); bounds.Union(b);
        }
        if (bounds.IsEmpty) bounds = new Rect(0, 0, 200, 150);
        FitRect(bounds);
    }
    public void FitRect(Rect bounds)
    {
        double w = Math.Max(ActualWidth, 100), h = Math.Max(ActualHeight, 100);
        Zoom = Math.Clamp(Math.Min((w - 90) / Math.Max(bounds.Width, 1), (h - 90) / Math.Max(bounds.Height, 1)), 1e-9, 1e9);
        offset = new(w / 2 - (bounds.X + bounds.Width / 2) * Zoom, h / 2 + (bounds.Y + bounds.Height / 2) * Zoom);
        Refresh(true);
    }
    public void ZoomBy(double factor) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), factor);
    private void ZoomAt(Point screen, double factor)
    {
        BeginNavigation();
        var anchor = ToWorld(screen); Zoom = Math.Clamp(Zoom * factor, 1e-9, 1e9);
        offset = new(screen.X - anchor.X * Zoom, screen.Y + anchor.Y * Zoom); NavigationChanged();
    }
    public void PanBy(Vector delta) { BeginNavigation(); offset += delta; NavigationChanged(); }
    private void BeginNavigation()
    {
        if (UseGpuDrawing) return;
        CancelPreview();
        navigating = scenePreview != null || Shapes.Count >= 5000;
        navigationTimer.Stop();
    }
    private void NavigationChanged()
    {
        if (UseGpuDrawing) { RequestNativeNavigationFrame(); return; }
        if (navigating) { navigationTimer.Start(); InvalidateVisual(); }
        else Refresh(true);
    }
    public void FinishNavigation()
    {
        navigationTimer.Stop();
        if (UseGpuDrawing) { navigating = false; InvalidateVisual(); return; }
        if (!navigating) return;
        navigating = false;
        bool sameScale = Math.Abs(Zoom / previewZoom - 1) <= 1e-9;
        var needed = new Rect(ToWorld(new(0, 0)), ToWorld(new(ActualWidth, ActualHeight)));
        needed.Intersect(sceneBounds);
        // Translation does not reduce snapshot resolution. Reuse it after release when
        // it already includes every drawing region needed by the new viewport.
        if (!VectorOutput && sameScale && scenePreview != null && (needed.IsEmpty || previewWorld.Contains(needed)))
        { InvalidateVisual(); return; }
        if (!sameScale) geometryCache.Clear();
        if (!VectorOutput && Shapes.Count >= 5000)
        { drawing = null; RequestPreview(); InvalidateVisual(); return; }
        drawing = null; scenePreview = null; InvalidateVisual();
    }
    public void Refresh(bool rebuildGeometry = false)
    { CancelPreview(); navigationTimer.Stop(); navigating = false; scenePreview = null; textCache.Clear(); if (rebuildGeometry) geometryCache.Clear(); drawing = null; InvalidateVisual(); }
    public void HighlightMeasurement(MeasureResult? result)
    {
        if (result != null && !Measurements.Any(item => ReferenceEquals(item, result))) return;
        FocusedMeasurement = result;
        InvalidateVisual();
    }
    public void FocusMeasurement(MeasureResult result)
    {
        if (!Measurements.Any(item => ReferenceEquals(item, result))) return;
        var bounds = result.Shape?.Bounds ?? Rect.Empty;
        foreach (var point in result.Points) bounds.Union(point);
        if (bounds.IsEmpty) return;
        SetMode(ToolMode.Select);
        selected = null;
        FocusedMeasurement = result;
        double padding = Math.Max(Math.Max(bounds.Width, bounds.Height) * 0.25, 1);
        bounds.Inflate(padding, padding);
        FitRect(bounds);
        Status?.Invoke(L.T("已定位测量记录 · 橙色为当前测量尺 · ") + result.CurrentText);
    }
    public void FocusShape(CadShape shape)
    {
        selected = shape;
        var b = shape.Bounds; b.Inflate(Math.Max(10, b.Width / 2), Math.Max(10, b.Height)); FitRect(b);
        Selection?.Invoke(CreateShapeDescription(shape)());
    }
    public bool Visible(CadShape s) => Layers.GetValueOrDefault(s.Layer, true) && s.RequiredLayers.All(l => Layers.GetValueOrDefault(l, true));
    public string Number(double value) => value.ToString("F" + Precision, L.Culture);
    private IEnumerable<CadShape> Near(Point p, double pixels = 12) => index.Query(new Rect(p.X - pixels / Zoom, p.Y - pixels / Zoom, pixels * 2 / Zoom, pixels * 2 / Zoom)).Where(s => Visible(s) && (s.Clip == null || s.Clip.Value.Contains(p)));
    public (Point point, string kind) Snap(Point p)
    {
        if (!SnapEnabled) return (p, "");
        double limit = SnapRadiusPixels / Zoom, best = limit;
        // The current measurement start takes priority over nearby drawing entities.
        if (IsNearMeasurementStart(p))
            return (pending[0], L.T(Mode == ToolMode.Area ? AreaStartSnap : ChainStartSnap));
        Point chosen = p; string kind = "";
        var near = Near(p).ToList();
        void Candidate(Point q, string name)
        {
            var d = Geo.Distance(p, q);
            if (d < best) { best = d; chosen = q; kind = name; }
        }
        foreach (var shape in near.Where(s => s.Text == null && !s.Approximate))
            foreach (var seg in shape.Segments)
            {
                if (!seg.Full) { Candidate(seg.At(0), L.T("端点")); Candidate(seg.At(1), L.T("端点")); Candidate(seg.At(.5), L.T("中点")); }
                if (seg.IsArc)
                {
                    Candidate(seg.Center, L.T("中心"));
                    foreach (var a in new[] { 0d, Math.PI / 2, Math.PI, Math.PI * 1.5 })
                        if (seg.ContainsAngle(a)) Candidate(seg.Center + seg.U * Math.Cos(a) + seg.V * Math.Sin(a), L.T("象限点"));
                }
                if (pending.Count > 0 && !seg.IsArc)
                {
                    var d = seg.B - seg.A;
                    double t = d.LengthSquared > 1e-20 ? Vector.Multiply(pending[^1] - seg.A, d) / d.LengthSquared : -1;
                    if (t >= 0 && t <= 1) Candidate(seg.A + d * t, L.T("垂足"));
                }
            }
        var segs = near.Where(s => s.Text == null && !s.Approximate).SelectMany(s => s.Segments).Where(s => Geo.Distance(p, s.Nearest(p)) < limit * 2).Take(60).ToArray();
        for (int i = 0; i < segs.Length; i++)
            for (int j = i + 1; j < segs.Length; j++)
                foreach (var point in Intersections(segs[i], segs[j])) Candidate(point, L.T("交点"));
        // Prefer discrete snaps to an arbitrary nearest point.
        if (kind.Length == 0)
            foreach (var seg in segs) Candidate(seg.Nearest(p), L.T("最近点"));
        return (chosen, kind);
    }
    public static IEnumerable<Point> Intersections(Segment a, Segment b)
    {
        if (!a.IsArc && !b.IsArc)
        { if (Geo.Intersection(a.A, a.B, b.A, b.B, out var p)) yield return p; yield break; }
        if (!a.IsArc && b.IsCircle)
        {
            var d = a.B - a.A; var f = a.A - b.Center;
            double aa = d.LengthSquared, bb = 2 * Vector.Multiply(f, d), cc = f.LengthSquared - b.U.LengthSquared;
            var disc = bb * bb - 4 * aa * cc;
            if (aa < 1e-20 || disc < 0) yield break;
            foreach (var t in new[] { (-bb - Math.Sqrt(disc)) / (2 * aa), (-bb + Math.Sqrt(disc)) / (2 * aa) })
                if (t >= 0 && t <= 1) { var q = a.A + d * t; if (Geo.Distance(q, b.Nearest(q)) < 1e-7 * Math.Max(1, b.U.Length)) yield return q; }
        }
        else if (a.IsCircle && !b.IsArc) { foreach (var p in Intersections(b, a)) yield return p; }
        else if (a.IsCircle && b.IsCircle)
        {
            double d = Geo.Distance(a.Center, b.Center), r = a.U.Length, s = b.U.Length;
            if (d < 1e-12 || d > r + s || d < Math.Abs(r - s)) yield break;
            var x = (r * r - s * s + d * d) / (2 * d); var h = Math.Sqrt(Math.Max(0, r * r - x * x));
            var u = (b.Center - a.Center) / d; var basePoint = a.Center + u * x;
            foreach (var p in new[] { basePoint + new Vector(-u.Y, u.X) * h, basePoint - new Vector(-u.Y, u.X) * h })
                if (Geo.Distance(p, a.Nearest(p)) < 1e-7 * Math.Max(1, r) && Geo.Distance(p, b.Nearest(p)) < 1e-7 * Math.Max(1, s)) yield return p;
        }
    }
    private CadShape? Pick(Point p)
    {
        double best = 10 / Zoom; CadShape? found = null;
        foreach (var s in Near(p))
        {
            double dist = s.Text != null && s.Bounds.Contains(p) ? best * .9 : s.Segments.Count == 0 ? double.MaxValue : s.Segments.Min(seg => Geo.Distance(p, seg.Nearest(p)));
            if (dist < best) { best = dist; found = s; }
        }
        return found;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); ZoomAt(e.GetPosition(this), Math.Pow(1.2, e.Delta / 120.0)); e.Handled = true; }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space))
        { panning = true; Cursor = Cursors.SizeAll; lastMouse = e.GetPosition(this); CaptureMouse(); return; }
        if (e.ChangedButton == MouseButton.Right) { CancelSelection(); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Left) return;
        if (Mode == ToolMode.PrintArea) { AcceptPrintAreaCorner(ToWorld(e.GetPosition(this))); e.Handled = true; return; }
        if (IsCompletionDoubleClick(e.ClickCount))
        { AcceptPoint(ToWorld(e.GetPosition(this)), e.ClickCount); e.Handled = true; return; }
        if (Mode == ToolMode.WindowZoom) { AcceptZoomCorner(e.GetPosition(this)); e.Handled = true; return; }
        if (BeginLabelDrag(e.GetPosition(this))) { CaptureMouse(); e.Handled = true; return; }
        AcceptPoint(ToWorld(e.GetPosition(this)));
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left && draggingLabel != null)
        { DragLabel(e.GetPosition(this)); draggingLabel = null; ReleaseMouseCapture(); UpdateLabelCursor(e.GetPosition(this)); e.Handled = true; return; }
        panning = false; ReleaseMouseCapture();
    }
    private void AcceptZoomCorner(Point screen)
    {
        if (zoomStart is not Point start)
        {
            zoomStart = ToWorld(screen);
            Status?.Invoke(L.T("请选择框选的第二个角点 · 右键或 Esc 取消"));
            InvalidateVisual();
            return;
        }
        var end = ToWorld(screen);
        zoomStart = null;
        if (Geo.Distance(ToScreen(start), screen) > 8) FitRect(new Rect(start, end));
        SetMode(ToolMode.Select);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var p = e.GetPosition(this);
        if (draggingLabel != null) { DragLabel(p); e.Handled = true; return; }
        if (panning) { PanBy(p - lastMouse); lastMouse = p; return; }
        bool overLabel = UpdateLabelCursor(p);
        bool hadSnap = snapName.Length > 0;
        bool skipSnap = (overLabel && Mode != ToolMode.PrintArea) || Mode is ToolMode.Select or ToolMode.WindowZoom;
        (cursorWorld, snapName) = skipSnap ? (ToWorld(p), "") : Snap(ToWorld(p)); ApplyOrtho();
        CoordinatesChanged?.Invoke($"X {LengthNumber(cursorWorld.X)}   Y {LengthNumber(cursorWorld.Y)}");
        if (pending.Count > 0) Status?.Invoke(L.F($"{snapName}   当前段 {LengthNumber(Geo.Distance(pending[^1], cursorWorld))} {UnitLabel}"));
        if (pending.Count > 0 || zoomStart != null || printAreaStart != null || hadSnap || snapName.Length > 0) InvalidateVisual();
    }
    private void ApplyOrtho()
    {
        if (snapName == L.T(AreaStartSnap) || snapName == L.T(ChainStartSnap)) return;
        if (pending.Count == 0 || !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift)) return;
        var a = pending[^1]; var d = cursorWorld - a;
        cursorWorld = Math.Abs(d.X) > Math.Abs(d.Y) ? new(cursorWorld.X, a.Y) : new(a.X, cursorWorld.Y); snapName = L.T("正交");
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { CancelSelection(); e.Handled = true; }
        if (e.Key == Key.Enter) Finish();
        if (e.Key == Key.Back && pending.Count > 0) { pending.RemoveAt(pending.Count - 1); InvalidateVisual(); }
        if (e.Key == Key.F) Fit();
        if (e.Key == Key.F3) { SnapEnabled = !SnapEnabled; SnapToggled?.Invoke(SnapEnabled); Status?.Invoke(SnapEnabled ? L.T("捕捉已开启") : L.T("捕捉已关闭")); }
    }
    private Geometry GeometryFor(CadShape s)
    {
        if (geometryCache.TryGetValue(s, out var cached)) return cached;
        var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var ctx = g.Open())
        {
            for (int i = 0; i < s.Segments.Count; i++)
            {
                renderToken.ThrowIfCancellationRequested();
                var seg = s.Segments[i];
                if (s.FigureStarts.Contains(i)) ctx.BeginFigure(seg.At(0), s.Filled, s.Closed);
                ctx.PolyLineTo(seg.Samples(Zoom * geometryDetailScale).Skip(1).ToArray(), true, false);
            }
        }
        g.Freeze(); geometryCache[s] = g; return g;
    }
    private DrawingGroup BuildDrawing()
    {
        var group = new DrawingGroup();
        using var dc = group.Open();
        var view = new Rect(ToWorld(new(0, 0)), ToWorld(new(ActualWidth, ActualHeight)));
        if (Shapes.Count >= 5000) view.Inflate(view.Width * .25, view.Height * .25);
        foreach (var s in index.Query(view).Where(Visible)) { renderToken.ThrowIfCancellationRequested(); DrawShape(dc, s, false); }
        dc.Close(); group.Freeze();
        return group;
    }
    private void DrawShape(DrawingContext dc, CadShape s, bool highlight)
    {
        Color color = s.Color;
        if (LightBackground && color.R > 200 && color.G > 200 && color.B > 200) color = Color.FromRgb(30, 40, 50);
        if (!brushCache.TryGetValue(color, out var colorBrush)) { colorBrush = new SolidColorBrush(color); colorBrush.Freeze(); brushCache[color] = colorBrush; }
        var brush = highlight ? accent : colorBrush;
        if (s.Clip is Rect clip) dc.PushClip(new RectangleGeometry(clip));
        if (s.Text != null)
        {
            var textKey = (s.Text, s.TextHeight, color);
            if (highlight || !textCache.TryGetValue(textKey, out var formatted))
            {
                formatted = new FormattedText(s.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), Math.Max(.001, s.TextHeight), brush, 1);
                if (!highlight) textCache[textKey] = formatted;
            }
            var m = Matrix.Identity; m.Scale(s.TextWidthFactor, -1); m.Rotate(s.TextRotation * 180 / Math.PI); m.Translate(s.TextPoint.X, s.TextPoint.Y);
            dc.PushTransform(new MatrixTransform(m));
            double x = s.TextAlign == 1 ? -formatted.Width / 2 : s.TextAlign == 2 ? -formatted.Width : 0;
            dc.DrawText(formatted, new Point(x, s.Type == "MTEXT" ? 0 : -formatted.Baseline)); dc.Pop();
        }
        else
        {
            var pen = new Pen(brush, (highlight ? 2 : ShowLineWeights ? s.StrokeWidth : 1) / Zoom);
            if (s.Dashes != null && !highlight) pen.DashStyle = new DashStyle(s.Dashes.Select(d => Math.Min(10000, d * Zoom / Math.Max(1, s.StrokeWidth))), 0);
            dc.DrawGeometry(s.Filled ? brush : null, pen, GeometryFor(s));
            if (s.Type == "POINT" && s.Segments.Count > 0) dc.DrawEllipse(brush, null, s.Segments[0].A, 2 / Zoom, 2 / Zoom);
        }
        if (s.Clip != null) dc.Pop();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(LightBackground ? Color.FromRgb(247, 248, 250) : Color.FromRgb(33, 40, 48)), null, new Rect(RenderSize));
        if (ShowGrid)
        {
            double step = Math.Pow(10, Math.Floor(Math.Log10(90 / Zoom))) * Zoom;
            if (step < 28) step *= 5;
            var pen = new Pen(new SolidColorBrush(LightBackground ? Color.FromRgb(229, 233, 236) : Color.FromRgb(44, 53, 64)), 1);
            for (double x = (offset.X % step + step) % step; x < ActualWidth; x += step) dc.DrawLine(pen, new(x, 0), new(x, ActualHeight));
            for (double y = (offset.Y % step + step) % step; y < ActualHeight; y += step) dc.DrawLine(pen, new(0, y), new(ActualWidth, y));
        }
        if (!UseGpuDrawing && (VectorOutput || Shapes.Count < 5000))
        {
            drawing ??= BuildDrawing();
        }
        else if (!UseGpuDrawing && scenePreview == null && !navigating) RequestPreview();
        if (UseGpuDrawing) DrawNativeScene(dc);
        if (!UseGpuDrawing && !VectorOutput && scenePreview != null)
            dc.DrawImage(scenePreview, new Rect(ToScreen(new(previewWorld.Left, previewWorld.Top)), ToScreen(new(previewWorld.Right, previewWorld.Bottom))));
        dc.PushTransform(new MatrixTransform(ViewMatrix));
        if (!UseGpuDrawing && (VectorOutput || Shapes.Count < 5000) && drawing != null) dc.DrawDrawing(drawing);
        if (selected != null && Visible(selected)) DrawShape(dc, selected, true);
        dc.Pop();
        labelHits.Clear();
        foreach (var result in Measurements.Where(r => !ReferenceEquals(r, FocusedMeasurement))) DrawMeasurement(dc, result, false);
        if (FocusedMeasurement is { } focused)
        {
            if (focused.Shape is { } shape)
            {
                dc.PushTransform(new MatrixTransform(ViewMatrix));
                dc.DrawGeometry(null, new Pen(Brushes.Orange, 2.5 / Zoom), GeometryFor(shape));
                dc.Pop();
            }
            DrawMeasurement(dc, focused, true);
        }
        if (pending.Count > 0) DrawMeasure(dc, [.. pending, cursorWorld], Mode == ToolMode.Area);
        if (snapName.Length > 0 && IsMouseOver && !panning)
        {
            var q = ToScreen(cursorWorld); dc.DrawRectangle(null, new Pen(accent, 1.5), new Rect(q.X - 5, q.Y - 5, 10, 10));
            Label(dc, snapName, q + new Vector(12, 12));
        }
        if (zoomStart is Point start) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 80, 160, 255)), new Pen(accent, 1), new Rect(ToScreen(start), Mouse.GetPosition(this)));
        if (Shapes.Count == 0) Label(dc, L.T("拖入 DWG / DXF 图纸，或点击“打开图纸”"), new Point(Math.Max(24, ActualWidth / 2 - 180), ActualHeight / 2));
        else if (UseGpuDrawing && nativeScene == null) Label(dc, L.T("正在准备 GPU 图形…"), new Point(20, 20));
        else if (!UseGpuDrawing && scenePreview == null && Shapes.Count >= 5000 && !VectorOutput) Label(dc, L.T("正在生成图纸预览…"), new Point(20, 20));
        if (!isOutputRendering) { DrawPrintArea(dc); DrawAxis(dc); }
    }
    private void DrawMeasure(DrawingContext dc, List<Point> points, bool closed, bool highlighted = false)
    {
        var brush = highlighted ? Brushes.Orange : accent;
        var pen = new Pen(brush, highlighted ? 2.5 : 1.5);
        for (int i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i]); dc.DrawEllipse(brush, null, p, highlighted ? 4 : 3, highlighted ? 4 : 3);
            if (i > 0) dc.DrawLine(pen, ToScreen(points[i - 1]), p);
        }
        if (closed && points.Count > 2) dc.DrawLine(pen, ToScreen(points[^1]), ToScreen(points[0]));
    }
    private void Label(DrawingContext dc, string text, Point p, Brush? brush = null)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, brush ?? accent, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        ft.MaxTextWidth = Math.Max(50, ActualWidth - 24);
        p = new Point(Math.Clamp(p.X, 8, Math.Max(8, ActualWidth - ft.Width - 8)), Math.Clamp(p.Y, 6, Math.Max(6, ActualHeight - ft.Height - 6)));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 32, 37, 45)), null, new Rect(p.X - 5, p.Y - 3, ft.Width + 10, ft.Height + 6), 2, 2); dc.DrawText(ft, p);
    }
    private void DrawAxis(DrawingContext dc)
    {
        var o = new Point(35, Math.Max(50, ActualHeight - 35));
        dc.DrawLine(new Pen(Brushes.IndianRed, 2), o, o + new Vector(30, 0));
        dc.DrawLine(new Pen(accent, 2), o, o + new Vector(0, -30));
        Label(dc, "X", o + new Vector(35, -8)); Label(dc, "Y", o + new Vector(-5, -50));
    }
}
