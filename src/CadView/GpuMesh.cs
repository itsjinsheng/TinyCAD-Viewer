using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using LibTessDotNet;
using Point = System.Windows.Point;

namespace TinyCAD;

[StructLayout(LayoutKind.Sequential)]
internal struct GpuPrimitive
{
    public Vector2 AHigh, ALow, BHigh, BLow, CHigh, CLow;
    public uint Color;
    public float Width;
    public uint Mode;
    public float ArcStart, ArcSweep;
    public static GpuPrimitive Create(Point a, Point b, Point c, Color color, float width, uint mode, float arcStart = 0, float arcSweep = 0)
    {
        var ah = new Vector2((float)a.X, (float)a.Y); var bh = new Vector2((float)b.X, (float)b.Y); var ch = new Vector2((float)c.X, (float)c.Y);
        return new() { AHigh = ah, ALow = new((float)(a.X - ah.X), (float)(a.Y - ah.Y)),
            BHigh = bh, BLow = new((float)(b.X - bh.X), (float)(b.Y - bh.Y)),
            CHigh = ch, CLow = new((float)(c.X - ch.X), (float)(c.Y - ch.Y)),
            Color = (uint)(color.R | color.G << 8 | color.B << 16 | color.A << 24), Width = width, Mode = mode, ArcStart = arcStart, ArcSweep = arcSweep };
    }
}

internal sealed record GpuMesh(GpuPrimitive[] Primitives, GpuPrimitive[] Curves, GpuMesh.Batch[] Batches, double DetailScale, GpuMesh.FixedPlan Plan)
{
    public sealed record Batch(int Start, int Count, Rect Bounds, Rect? Clip, double MinScale, double MaxScale, bool Curved, int Order);
    public sealed record FixedPlan(int PrimitiveCount, Batch[] Batches);
    public static GpuMesh Build(List<CadShape> shapes, Dictionary<string, bool> layers, bool light, bool weights,
        double detailScale, CancellationToken token, FixedPlan? fixedPlan = null)
    {
        var fixedPrimitives = new List<GpuPrimitive>(); var curves = new List<GpuPrimitive>();
        var primitives = fixedPrimitives; var batches = new List<Batch>();
        var texts = new Dictionary<(string, double, int, bool), Point[]>();
        int batchStart = 0; Rect batchBounds = Rect.Empty; Rect? batchClip = null;
        double minScale = 0, maxScale = double.PositiveInfinity;
        bool needsRefinement = false;
        bool curvedBatch = false;
        int shapeIndex = 0, batchOrder = 0;
        void Flush()
        {
            if (primitives.Count > batchStart) batches.Add(new(batchStart, primitives.Count - batchStart, batchBounds, batchClip, minScale, maxScale, curvedBatch, batchOrder));
            batchStart = primitives.Count; batchBounds = Rect.Empty;
        }
        for (; shapeIndex < shapes.Count; shapeIndex++)
        {
            var shape = shapes[shapeIndex];
            token.ThrowIfCancellationRequested();
            if (!layers.GetValueOrDefault(shape.Layer, true) || !shape.RequiredLayers.All(l => layers.GetValueOrDefault(l, true))) continue;
            bool curved = shape.Text == null && (shape.Filled || shape.Dashes is { Length: > 0 }) && shape.Segments.Any(s => s.IsArc);
            // A refinement reuses the fixed GPU buffer and its exact ordering metadata.
            // Do not rebuild glyph triangles or duplicate the fixed CPU vertex array.
            if (fixedPlan != null && !curved) { Flush(); continue; }
            if (curved != curvedBatch)
            {
                Flush(); curvedBatch = curved; primitives = curved ? curves : fixedPrimitives;
                batchStart = primitives.Count;
            }
            if (primitives.Count - batchStart >= 2048 || shape.Clip != batchClip) Flush();
            if (primitives.Count == batchStart) batchOrder = shapeIndex;
            batchClip = shape.Clip; batchBounds.Union(shape.Bounds);
            var color = light && shape.Color.R > 200 && shape.Color.G > 200 && shape.Color.B > 200 ? Color.FromRgb(30, 40, 50) : shape.Color;
            float width = (float)(weights ? shape.StrokeWidth : 1);
            if (shape.Text != null)
            {
                var key = (shape.Text, shape.TextHeight, shape.TextAlign, shape.Type == "MTEXT");
                if (!texts.TryGetValue(key, out var triangles))
                {
                    var ft = new FormattedText(shape.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei"), Math.Max(.001, shape.TextHeight), Brushes.White, 1);
                    double x = shape.TextAlign == 1 ? -ft.Width / 2 : shape.TextAlign == 2 ? -ft.Width : 0;
                    var outline = ft.BuildGeometry(new(x, shape.Type == "MTEXT" ? 0 : -ft.Baseline))
                        .GetFlattenedPathGeometry(Math.Max(.00001, shape.TextHeight / 2000), ToleranceType.Absolute);
                    var contours = new List<List<Point>>();
                    foreach (var figure in outline.Figures)
                    {
                        var points = new List<Point> { figure.StartPoint };
                        foreach (var segment in figure.Segments)
                        {
                            if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                            else if (segment is LineSegment line) points.Add(line.Point);
                        }
                        contours.Add(points);
                    }
                    triangles = Tessellate(contours, outline.FillRule == FillRule.EvenOdd);
                    texts.Add(key, triangles);
                }
                var matrix = Matrix.Identity; matrix.Scale(shape.TextWidthFactor, -1);
                matrix.Rotate(shape.TextRotation * 180 / Math.PI); matrix.Translate(shape.TextPoint.X, shape.TextPoint.Y);
                Flush(); batchOrder = shapeIndex;
                if (triangles.Length > 0)
                {
                    // Text below four physical pixels is represented by a light baseline.
                    // The exact glyph triangles remain resident and become visible immediately on zoom.
                    var bounds = Rect.Empty; foreach (var p in triangles) bounds.Union(p);
                    batchBounds = shape.Bounds; maxScale = 4 / Math.Max(.001, shape.TextHeight);
                    var faint = Color.FromArgb(160, color.R, color.G, color.B);
                    primitives.Add(GpuPrimitive.Create(matrix.Transform(new Point(bounds.Left, bounds.Y + bounds.Height / 2)),
                        matrix.Transform(new Point(bounds.Right, bounds.Y + bounds.Height / 2)), default, faint, 1, 0));
                    Flush(); minScale = maxScale; maxScale = double.PositiveInfinity; batchBounds = shape.Bounds;
                }
                for (int i = 0; i < triangles.Length; i += 3)
                    primitives.Add(GpuPrimitive.Create(matrix.Transform(triangles[i]), matrix.Transform(triangles[i + 1]),
                        matrix.Transform(triangles[i + 2]), color, 0, 1));
                Flush(); minScale = 0; maxScale = double.PositiveInfinity;
            }
            else if (shape.Type == "POINT" && shape.Segments.Count > 0)
                primitives.Add(GpuPrimitive.Create(shape.Segments[0].A, shape.Segments[0].A, default, color, 4, 0));
            else if (!shape.Filled && shape.Dashes is not { Length: > 0 })
            {
                Point? first = null, last = null;
                void CloseFigure()
                {
                    if (shape.Closed && first is Point a && last is Point b && a != b)
                        primitives.Add(GpuPrimitive.Create(b, a, default, color, width, 0));
                }
                for (int i = 0; i < shape.Segments.Count; i++)
                {
                    var segment = shape.Segments[i];
                    if (shape.FigureStarts.Contains(i)) { CloseFigure(); first = segment.At(0); }
                    if (segment.IsArc)
                        primitives.Add(GpuPrimitive.Create(segment.Center, segment.Center + segment.U, segment.Center + segment.V,
                            color, width, segment.Full ? 2u : 3u, (float)Geo.Positive(segment.Start), (float)segment.Sweep));
                    else primitives.Add(GpuPrimitive.Create(segment.A, segment.B, default, color, width, 0));
                    last = segment.At(1);
                }
                CloseFigure();
            }
            else
            {
                needsRefinement |= shape.Segments.Any(s => s.IsArc);
                var contours = new List<List<Point>>(); List<Point>? points = null;
                for (int i = 0; i < shape.Segments.Count; i++)
                {
                    token.ThrowIfCancellationRequested(); var segment = shape.Segments[i];
                    if (shape.FigureStarts.Contains(i)) { points = [segment.At(0)]; contours.Add(points); }
                    points?.AddRange(segment.Samples(detailScale).Skip(1));
                }
                if (shape.Filled)
                {
                    var triangles = Tessellate(contours, true);
                    for (int i = 0; i < triangles.Length; i += 3)
                        primitives.Add(GpuPrimitive.Create(triangles[i], triangles[i + 1], triangles[i + 2], color, 0, 1));
                }
                foreach (var contour in contours)
                {
                    if (shape.Closed && contour.Count > 1 && contour[0] != contour[^1]) contour.Add(contour[0]);
                    var dashes = shape.Dashes?.Select(d => Math.Abs(d) * width / Math.Max(1, shape.StrokeWidth)).ToArray();
                    if (dashes?.Length % 2 == 1) dashes = [.. dashes, .. dashes];
                    int dashIndex = 0; double dashLeft = dashes is { Length: > 0 } ? Math.Max(1e-6, dashes[0]) : 0;
                    bool dashed = dashes is { Length: > 0 } && dashes.Sum() > 1e-6;
                    for (int i = 1; i < contour.Count; i++)
                    {
                        var a = contour[i - 1]; var b = contour[i];
                        if (!dashed) { primitives.Add(GpuPrimitive.Create(a, b, default, color, width, 0)); continue; }
                        double length = Geo.Distance(a, b), done = 0;
                        while (done < length)
                        {
                            token.ThrowIfCancellationRequested();
                            double step = Math.Min(dashLeft, length - done);
                            if (dashIndex % 2 == 0)
                                primitives.Add(GpuPrimitive.Create(a + (b - a) * (done / length), a + (b - a) * ((done + step) / length), default, color, width, 0));
                            done += step; dashLeft -= step;
                            if (dashLeft <= 1e-10) { dashIndex = (dashIndex + 1) % dashes!.Length; dashLeft = Math.Max(1e-6, dashes[dashIndex]); }
                            if ((fixedPlan?.PrimitiveCount ?? fixedPrimitives.Count) + curves.Count > 8000000) throw new InvalidOperationException(L.T("GPU 图形缓存超过试验版容量，使用稳定渲染。"));
                        }
                    }
                }
            }
            if ((fixedPlan?.PrimitiveCount ?? fixedPrimitives.Count) + curves.Count > 8000000) throw new InvalidOperationException(L.T("GPU 图形缓存超过试验版容量，使用稳定渲染。"));
        }
        Flush();
        var plan = fixedPlan ?? new FixedPlan(fixedPrimitives.Count, batches.Where(b => !b.Curved).ToArray());
        var ordered = fixedPlan == null ? batches.ToArray() : batches.Concat(plan.Batches).OrderBy(b => b.Order).ToArray();
        return new(fixedPrimitives.ToArray(), curves.ToArray(), ordered, needsRefinement ? detailScale : double.PositiveInfinity, plan);
    }

    private static Point[] Tessellate(List<List<Point>> contours, bool evenOdd)
    {
        var origin = contours.FirstOrDefault(c => c.Count > 0)?.FirstOrDefault() ?? default;
        var tess = new Tess();
        foreach (var contour in contours.Where(c => c.Count >= 3))
            tess.AddContour(contour.Select(p => new ContourVertex { Position = new Vec3 { X = (float)(p.X - origin.X), Y = (float)(p.Y - origin.Y) } }).ToArray());
        tess.Tessellate(evenOdd ? WindingRule.EvenOdd : WindingRule.NonZero, ElementType.Polygons, 3);
        var points = new List<Point>();
        for (int i = 0; i < tess.ElementCount; i++)
        {
            int a = tess.Elements[i * 3], b = tess.Elements[i * 3 + 1], c = tess.Elements[i * 3 + 2];
            if (a == Tess.Undef || b == Tess.Undef || c == Tess.Undef) continue;
            foreach (int index in new[] { a, b, c })
            {
                var p = tess.Vertices[index].Position; points.Add(new(origin.X + p.X, origin.Y + p.Y));
            }
        }
        return points.ToArray();
    }
}
