using System.IO;
using System.Windows;
using System.Windows.Media;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace TinyCAD;

public sealed class DrawingData
{
    public string FileName { get; set; } = "";
    public string Units { get; set; } = "图纸单位";
    public Dictionary<string, List<CadShape>> Spaces { get; } = [];
    public Dictionary<string, bool> Layers { get; } = [];
    public Dictionary<string, int> Warnings { get; } = [];
    private readonly Dictionary<string, (string Template, object?[] Arguments)> warningMessages = [];
    public void Warn(string template, params object?[] arguments)
    {
        var text = arguments.Length == 0 ? template : string.Format(System.Globalization.CultureInfo.InvariantCulture, template, arguments);
        Warnings[text] = Warnings.GetValueOrDefault(text) + 1;
        warningMessages[text] = (template, arguments);
    }
    public string LocalizeWarning(string key) => warningMessages.TryGetValue(key, out var message)
        ? message.Arguments.Length == 0 ? L.T(message.Template) : L.Format(message.Template, message.Arguments) : key;
}

public sealed class CadLoader
{
    private readonly DrawingData result = new();
    private CancellationToken token;
    private int entityCount;
    private CadDocument doc = null!;
    private IProgress<CadLoadProgress>? progress;
    private readonly System.Diagnostics.Stopwatch progressClock = System.Diagnostics.Stopwatch.StartNew();
    private void Report(string stage, bool force = false)
    {
        if (!force && progressClock.ElapsedMilliseconds < 100) return;
        progress?.Report(new(stage, entityCount)); progressClock.Restart();
    }
    public static DrawingData Load(string path, CancellationToken cancellation = default, IProgress<CadLoadProgress>? progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var loader = new CadLoader { token = cancellation, progress = progress };
        loader.Report("正在解析图纸…", true);
        loader.result.FileName = Path.GetFullPath(path);
        void Notice(object sender, NotificationEventArgs e)
        {
            cancellation.ThrowIfCancellationRequested();
            if (loader.result.Warnings.Count < 150) loader.result.Warn("解析器：{0}", e.Message);
        }
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".dwg" and not ".dxf") throw new NotSupportedException(L.T("请选择 DWG 或 DXF 文件。"));
        using var stream = new CancelableReadStream(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), cancellation);
        using ICadReader reader = extension == ".dwg" ? new DwgReader(stream, Notice) : new DxfReader(stream, Notice);
        reader.OnProgress += (_, _) => cancellation.ThrowIfCancellationRequested();
        loader.doc = reader.Read();
        cancellation.ThrowIfCancellationRequested();
        return loader.Build();
    }
    public static DrawingData FromDocument(CadDocument document) => new CadLoader { doc = document }.Build();
    private DrawingData Build()
    {
        Report("正在整理图层和空间…", true);
        result.Units = doc.Header.InsUnits.ToString();
        if ((int)doc.Header.InsUnits == 0) result.Units = "图纸单位（未指定）";
        foreach (var l in doc.Layers) result.Layers[l.Name] = l.IsOn && !l.Flags.HasFlag(LayerFlags.Frozen);
        foreach (var style in doc.TextStyles)
            if (!string.IsNullOrWhiteSpace(style.Filename)) result.Warn("字体替代：{0} → Microsoft YaHei；字形和排版可能不同", style.Filename);
        var model = new List<CadShape>();
        result.Spaces["模型"] = model;
        Report("正在转换图形…", true);
        Walk(doc.ModelSpace.Entities, Matrix.Identity, model, null, null, [], 0);
        foreach (var layout in doc.Layouts.Where(l => l.IsPaperSpace).OrderBy(l => l.TabOrder))
        {
            token.ThrowIfCancellationRequested();
            var shapes = new List<CadShape>();
            result.Spaces[layout.Name] = shapes;
            Walk(layout.AssociatedBlock.Entities, Matrix.Identity, shapes, null, null, [], 0);
            foreach (var vp in layout.Viewports.Where(v => !v.RepresentsPaper && v.ActiveStatus != 0))
            {
                if (vp.ViewHeight <= 0 || vp.Height <= 0 || vp.Width <= 0) continue;
                if (vp.Boundary != null || Math.Abs(vp.ViewDirection.X) > 1e-9 || Math.Abs(vp.ViewDirection.Y) > 1e-9 || vp.ViewDirection.Z <= 0)
                { result.Warn("布局存在非矩形裁剪或非俯视视口，未显示其模型内容"); continue; }
                var m = Matrix.Identity;
                m.Translate(-vp.ViewTarget.X, -vp.ViewTarget.Y);
                m.Rotate(-vp.TwistAngle * 180 / Math.PI);
                m.Translate(-vp.ViewCenter.X, -vp.ViewCenter.Y);
                m.Scale(vp.ScaleFactor, vp.ScaleFactor);
                m.Translate(vp.Center.X, vp.Center.Y);
                var clip = new Rect(vp.Center.X - vp.Width / 2, vp.Center.Y - vp.Height / 2, vp.Width, vp.Height);
                var frozen = vp.FrozenLayers.Select(l => l.Name).ToHashSet();
                foreach (var shape in model.Where(s => !frozen.Contains(s.Layer) && !s.RequiredLayers.Overlaps(frozen)))
                {
                    var copy = TransformShape(shape, m); copy.Clip = clip; copy.PaperProjection = true;
                    if (copy.Bounds.IntersectsWith(clip)) shapes.Add(copy);
                }
            }
        }
        if (result.Spaces.Values.All(s => s.Count == 0)) result.Warn("没有可显示的二维实体");
        Report("正在准备显示…", true);
        return result;
    }
    private static Point P(XYZ p) => new(p.X, p.Y);
    private static Point P(XY p) => new(p.X, p.Y);
    private static Color ToColor(ACadSharp.Color c) => c.Index == 7 || c.IsByBlock || c.IsByLayer ? Color.FromRgb(222, 231, 240) : Color.FromRgb(c.R, c.G, c.B);
    private void Walk(IEnumerable<Entity> entities, Matrix parent, List<CadShape> target, string? parentLayer, Color? parentColor, HashSet<string> ancestors, int depth)
    {
        if (depth > 32) { result.Warn("块嵌套超过 32 层或存在循环引用，已停止展开"); return; }
        foreach (var e in entities)
        {
            token.ThrowIfCancellationRequested();
            if (++entityCount > 2_000_000) throw new InvalidDataException(L.T("图纸超过本版本的 200 万展开实体限制。"));
            if (entityCount % 256 == 0) Report("正在转换图形…");
            if (e.IsInvisible) continue;
            try
            {
                var layer = e.Layer?.Name ?? "0";
                if (layer == "0" && parentLayer != null) layer = parentLayer;
                result.Layers.TryAdd(layer, true);
                var color = e.Color.IsByBlock ? parentColor ?? ToColor(e.GetActiveColor()) : e.Color.IsByLayer && e.Layer?.Name == "0" && parentLayer != null ? ToColor(doc.Layers[parentLayer].Color) : ToColor(e.GetActiveColor());
                var m = parent;
                if (e is IOrientable oriented && e is not Line && e is not Spline)
                {
                    if (Math.Abs(oriented.Normal.X) > 1e-8 || Math.Abs(oriented.Normal.Y) > 1e-8)
                    { result.Warn("非 XY 平面实体已跳过：{0}", e.ObjectName); continue; }
                    if (oriented.Normal.Z < 0 && e is not Ellipse && e is not MText)
                    { m = new Matrix(-1, 0, 0, 1, 0, 0); m.Append(parent); }
                }
                if (e is Insert insert)
                {
                    if (insert.Block == null) { result.Warn("块定义缺失"); continue; }
                    if (insert.Block.Flags.ToString().Contains("XRef", StringComparison.OrdinalIgnoreCase) || insert.Block.IsUnloaded)
                        result.Warn("外部引用未自动加载：{0}", insert.Block.Name);
                    if (insert.Block.IsDynamic) result.Warn("动态块按保存的块几何显示，动态参数未求值");
                    var deps = new HashSet<string>(ancestors) { layer };
                    var bp = insert.Block.BlockEntity.BasePoint;
                    for (int row = 0; row < Math.Max(1, (int)insert.RowCount); row++)
                        for (int col = 0; col < Math.Max(1, (int)insert.ColumnCount); col++)
                        {
                            token.ThrowIfCancellationRequested();
                            var local = Matrix.Identity;
                            local.Translate(-bp.X, -bp.Y);
                            local.Scale(insert.XScale, insert.YScale);
                            local.Translate(col * insert.ColumnSpacing, row * insert.RowSpacing);
                            local.Rotate(insert.Rotation * 180 / Math.PI);
                            local.Translate(insert.InsertPoint.X, insert.InsertPoint.Y);
                            local.Append(m);
                            Walk(insert.Block.Entities.Where(x => x is not AttributeDefinition), local, target, layer, color, deps, depth + 1);
                        }
                    Walk(insert.Attributes, parent, target, layer, color, deps, depth + 1);
                    continue;
                }
                if (e is Dimension dimension)
                {
                    if (dimension.Block?.Entities.Count > 0) Walk(dimension.Block.Entities, parent, target, layer, color, ancestors, depth + 1);
                    else result.Warn("标注缺少显示块，未显示：{0}", e.Handle.ToString("X"));
                    continue;
                }
                if (e is Viewport) continue;
                var s = new CadShape { Layer = layer, Color = color, Type = e.ObjectName, Handle = e.Handle.ToString("X") };
                s.RequiredLayers.UnionWith(ancestors);
                var lw = (int)e.GetActiveLineWeightType();
                s.StrokeWidth = lw > 0 ? Math.Max(1, lw / 100.0 * 3.78) : 1;
                var lt = e.GetActiveLineType();
                if (lt?.Segments.Any() == true)
                {
                    if (lt.Segments.Any(x => x.IsShape || x.IsText)) result.Warn("复杂线型中的文字/形状未显示：{0}", lt.Name);
                    var values = lt.Segments.Select(x => Math.Max(.05, Math.Abs(x.Length * e.LineTypeScale * doc.Header.LineTypeScale))).ToArray();
                    if (values.Length % 2 == 0) s.Dashes = values;
                    else result.Warn("特殊线型按连续线显示：{0}", lt.Name);
                }
                switch (e)
                {
                    case Line line: s.Segments.Add(Segment.Line(P(line.StartPoint), P(line.EndPoint))); break;
                    case Arc arc: s.Segments.Add(Segment.Arc(P(arc.Center), arc.Radius, arc.StartAngle, Geo.Positive(arc.EndAngle - arc.StartAngle))); break;
                    case Circle circle: s.Segments.Add(Segment.Arc(P(circle.Center), circle.Radius, 0, Geo.Tau)); s.Closed = true; break;
                    case LwPolyline pl:
                        AddPolyline(s, pl.Vertices.Select(v => (P(v.Location), v.Bulge)).ToArray(), pl.IsClosed);
                        if (pl.ConstantWidth > 0 || pl.Vertices.Any(v => v.StartWidth > 0 || v.EndWidth > 0)) result.Warn("宽多段线按中心线显示");
                        break;
                    case Polyline2D pl: AddPolyline(s, pl.Vertices.Select(v => (P(v.Location), v.Bulge)).ToArray(), pl.IsClosed); break;
                    case Ellipse el:
                        s.Segments.Add(new Segment { IsArc = true, Center = P(el.Center), U = new(el.MajorAxisEndPoint.X, el.MajorAxisEndPoint.Y), V = new(el.MinorAxisEndPoint.X, el.MinorAxisEndPoint.Y), Start = el.StartParameter, Sweep = el.IsFullEllipse ? Geo.Tau : Geo.Positive(el.EndParameter - el.StartParameter) });
                        s.Closed = el.IsFullEllipse; break;
                    case Spline spline:
                        if (!spline.TryPolygonalVertexes(512, out var pts)) { result.Warn("无效样条已跳过"); continue; }
                        AddPolyline(s, pts.Select(v => (P(v), 0.0)).ToArray(), spline.IsClosed);
                        s.Approximate = true; result.Warn("样条采用折线近似显示与查询"); break;
                    case TextEntity t:
                        s.Text = CleanText(t.Value); s.TextPoint = P(t.HorizontalAlignment != TextHorizontalAlignment.Left ? t.AlignmentPoint : t.InsertPoint);
                        s.TextHeight = Math.Max(.001, t.Height); s.TextRotation = t.Rotation; s.TextWidthFactor = Math.Max(.01, t.WidthFactor);
                        s.TextAlign = (int)t.HorizontalAlignment == 1 ? 1 : (int)t.HorizontalAlignment == 2 ? 2 : 0;
                        if ((int)t.HorizontalAlignment > 2 || (int)t.VerticalAlignment != 0 || t.ObliqueAngle != 0 || (int)t.Mirror != 0) result.Warn("特殊文字对齐、倾斜或镜像采用简化排版");
                        break;
                    case MText t:
                        s.Text = t.PlainText; s.TextPoint = P(t.InsertPoint); s.TextHeight = Math.Max(.001, t.Height); s.TextRotation = t.Rotation;
                        s.TextAlign = ((int)t.AttachmentPoint - 1) % 3;
                        result.Warn("多行文字按纯文本显示，复杂格式和自动换行未还原"); break;
                    case Solid solid:
                        AddPolyline(s, new[] { (P(solid.FirstCorner), 0d), (P(solid.SecondCorner), 0d), (P(solid.FourthCorner), 0d), (P(solid.ThirdCorner), 0d) }, true); s.Filled = true; break;
                    case Hatch hatch:
                        foreach (var path in hatch.Paths)
                        {
                            s.FigureStarts.Add(s.Segments.Count);
                            foreach (var edge in path.Edges) AddHatchEdge(s, edge);
                        }
                        s.Filled = hatch.IsSolid; s.Closed = true;
                        if (!hatch.IsSolid) result.Warn("图案填充仅显示边界");
                        s.Approximate = true; break;
                    case ACadSharp.Entities.Point point:
                        s.Segments.Add(Segment.Line(P(point.Location), P(point.Location))); break;
                    default: result.Warn("未支持的实体：{0}", e.ObjectName); continue;
                }
                s = TransformShape(s, m);
                if (!s.Bounds.IsEmpty && double.IsFinite(s.Bounds.X) && double.IsFinite(s.Bounds.Y) && double.IsFinite(s.Bounds.Width) && double.IsFinite(s.Bounds.Height)) target.Add(s);
                else result.Warn("无效几何范围：{0}", e.ObjectName);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Warn("实体 {0} 显示失败：{1}", e.ObjectName, ex.Message); }
        }
    }
    private static string CleanText(string text) => text.Replace("%%d", "°").Replace("%%p", "±").Replace("%%c", "Ø");
    private static void AddPolyline(CadShape s, (Point p, double bulge)[] vertices, bool closed)
    {
        s.Closed = closed;
        for (int i = 0; i < vertices.Length - (closed ? 0 : 1); i++)
            s.Segments.Add(Segment.Bulge(vertices[i].p, vertices[(i + 1) % vertices.Length].p, vertices[i].bulge));
    }
    private void AddHatchEdge(CadShape s, Hatch.BoundaryPath.Edge edge)
    {
        switch (edge)
        {
            case Hatch.BoundaryPath.Polyline p: AddPolyline(s, p.Vertices.Select(v => (P(v), v.Z)).ToArray(), p.IsClosed); break;
            case Hatch.BoundaryPath.Arc a:
                double sweep = a.CounterClockWise ? Geo.Positive(a.EndAngle - a.StartAngle) : -Geo.Positive(a.StartAngle - a.EndAngle);
                if (Math.Abs(sweep) < 1e-12) sweep = a.CounterClockWise ? Geo.Tau : -Geo.Tau;
                s.Segments.Add(Segment.Arc(P(a.Center), a.Radius, a.StartAngle, sweep)); break;
            default:
                var e = edge.ToEntity();
                if (e is Line line) s.Segments.Add(Segment.Line(P(line.StartPoint), P(line.EndPoint)));
                else if (e is Ellipse el) s.Segments.Add(new Segment { IsArc = true, Center = P(el.Center), U = new(el.MajorAxisEndPoint.X, el.MajorAxisEndPoint.Y), V = new(el.MinorAxisEndPoint.X, el.MinorAxisEndPoint.Y), Start = el.StartParameter, Sweep = el.IsFullEllipse ? Geo.Tau : Geo.Positive(el.EndParameter - el.StartParameter) });
                else if (e is Spline sp && sp.TryPolygonalVertexes(512, out var pts)) AddPolyline(s, pts.Select(p => (P(p), 0d)).ToArray(), false);
                else result.Warn("填充边界未完整解析");
                break;
        }
    }
    public static CadShape TransformShape(CadShape source, Matrix m)
    {
        var direction = m.Transform(new Vector(Math.Cos(source.TextRotation), Math.Sin(source.TextRotation)));
        var up = m.Transform(new Vector(-Math.Sin(source.TextRotation), Math.Cos(source.TextRotation)));
        var s = new CadShape { Layer = source.Layer, Type = source.Type, Handle = source.Handle, Color = source.Color, Closed = source.Closed, Filled = source.Filled, Approximate = source.Approximate, Text = source.Text, TextPoint = m.Transform(source.TextPoint), TextHeight = source.TextHeight * up.Length, TextWidthFactor = source.TextWidthFactor * direction.Length / Math.Max(1e-12, up.Length), TextRotation = Math.Atan2(direction.Y, direction.X), TextAlign = source.TextAlign, StrokeWidth = source.StrokeWidth, Dashes = source.Dashes };
        s.FigureStarts.UnionWith(source.FigureStarts); s.RequiredLayers.UnionWith(source.RequiredLayers);
        s.Segments.AddRange(source.Segments.Select(segment => segment.Transform(m))); s.UpdateBounds(); return s;
    }
}
