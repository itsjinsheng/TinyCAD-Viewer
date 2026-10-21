using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TinyCAD;

public sealed partial class CadCanvas
{
    public event Action<MeasureResult>? MeasurementLabelSelected;
    private int measurementNumber;
    private readonly Dictionary<MeasureResult, Vector> labelOffsets = new(ReferenceEqualityComparer.Instance);
    private readonly List<(MeasureResult Result, Rect Bounds)> labelHits = [];
    private MeasureResult? draggingLabel;
    private Vector labelGrab;
    private sealed record LabelLayout(Size TextSize, DrawingGroup Drawing, double Width, double Dpi, bool Highlighted);
    private readonly Dictionary<MeasureResult, LabelLayout> labelLayouts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MeasureResult, Point> labelAnchors = new(ReferenceEqualityComparer.Instance);

    private bool UpdateLabelCursor(Point screen)
    {
        bool overLabel = labelHits.Any(hit => hit.Bounds.Contains(screen));
        Cursor = draggingLabel != null || panning || overLabel ? Cursors.SizeAll : Cursors.Cross;
        return overLabel;
    }

    public void ClearMeasurements()
    {
        draggingLabel = null; ReleaseMouseCapture(); labelOffsets.Clear(); labelHits.Clear(); measurementNumber = 0;
        labelLayouts.Clear(); labelAnchors.Clear(); Cursor = Cursors.Cross;
        pending.Clear(); Measurements.Clear(); FocusedMeasurement = null; InvalidateVisual();
    }

    public bool DeleteMeasurement(MeasureResult result)
    {
        int index = Measurements.FindIndex(item => ReferenceEquals(item, result));
        if (index < 0) return false;
        if (ReferenceEquals(draggingLabel, result)) { draggingLabel = null; ReleaseMouseCapture(); }
        Measurements.RemoveAt(index);
        labelOffsets.Remove(result);
        labelLayouts.Remove(result); labelAnchors.Remove(result);
        labelHits.RemoveAll(hit => ReferenceEquals(hit.Result, result));
        if (ReferenceEquals(FocusedMeasurement, result)) FocusedMeasurement = null;
        UpdateLabelCursor(Mouse.GetPosition(this));
        InvalidateVisual();
        return true;
    }

    private bool BeginLabelDrag(Point screen)
    {
        // Last drawn label is on top, including the currently selected measurement.
        for (int i = labelHits.Count - 1; i >= 0; i--)
        {
            var hit = labelHits[i];
            if (!hit.Bounds.Contains(screen)) continue;
            draggingLabel = hit.Result;
            Cursor = Cursors.SizeAll;
            labelGrab = screen - new Point(hit.Bounds.X + 5, hit.Bounds.Y + 3);
            HighlightMeasurement(hit.Result);
            MeasurementLabelSelected?.Invoke(hit.Result);
            return true;
        }
        return false;
    }

    private void DragLabel(Point screen)
    {
        if (draggingLabel is not { } result || result.Points.Count == 0) return;
        // DIP offset follows the drawing anchor during pan/zoom while keeping text readable.
        labelOffsets[result] = screen - labelGrab - ToScreen(result.Points[^1]);
        InvalidateVisual();
    }

    private void DrawMeasurement(DrawingContext dc, MeasureResult result, bool highlighted)
    {
        DrawMeasure(dc, result.Points, result.Closed, highlighted);
        if (result.Points.Count == 0 || result.CurrentText.Length == 0) return;
        var brush = highlighted ? Brushes.Orange : accent;
        double width = Math.Max(50, ActualWidth - 24), dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!labelLayouts.TryGetValue(result, out var layout) || layout.Width != width || layout.Dpi != dpi || layout.Highlighted != highlighted)
        {
            string text = result.CurrentText.Length > 95 ? result.CurrentText[..95] + "…" : result.CurrentText;
            var formattedText = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 12, brush, dpi) { MaxTextWidth = width };
            var textDrawing = new DrawingGroup();
            using (var context = textDrawing.Open()) context.DrawText(formattedText, new Point());
            textDrawing.Freeze();
            labelLayouts[result] = layout = new LabelLayout(new Size(formattedText.Width, formattedText.Height), textDrawing, width, dpi, highlighted);
        }
        var position = ToScreen(result.Points[^1]) + labelOffsets.GetValueOrDefault(result, new Vector(10, -28));
        // Follow the drawing beyond the viewport; the canvas clips the label naturally.
        var bounds = new Rect(position.X - 5, position.Y - 3, layout.TextSize.Width + 10, layout.TextSize.Height + 6);
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        // Measurements are immutable within a scene. Keep the leader attached to its
        // original world point instead of scanning every entity segment on each frame.
        if (!labelAnchors.TryGetValue(result, out var anchor))
            labelAnchors[result] = anchor = MeasurementAnchor(result, ToWorld(center));
        var start = ToScreen(anchor);
        // Connect to the nearest box edge, leaving text and the measured geometry untouched.
        var end = new Point(Math.Clamp(start.X, bounds.Left, bounds.Right), Math.Clamp(start.Y, bounds.Top, bounds.Bottom));
        var leader = new Pen(brush, highlighted ? 1.5 : 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(leader, start, end);
        dc.DrawEllipse(brush, null, start, 2.5, 2.5);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 32, 37, 45)),
            new Pen(brush, highlighted ? 1.2 : .6), bounds, 2, 2);
        dc.PushTransform(new TranslateTransform(position.X, position.Y));
        dc.DrawDrawing(layout.Drawing);
        dc.Pop();
        labelHits.Add((result, bounds));
    }

    private static Point MeasurementAnchor(MeasureResult result, Point target)
    {
        IEnumerable<Segment> segments = result.Shape?.Segments ?? [];
        if (result.Shape == null)
        {
            var lines = new List<Segment>();
            for (int i = 1; i < result.Points.Count; i++) lines.Add(Segment.Line(result.Points[i - 1], result.Points[i]));
            if (result.Closed && result.Points.Count > 2) lines.Add(Segment.Line(result.Points[^1], result.Points[0]));
            segments = lines;
        }
        var nearest = result.Points[^1];
        double best = double.MaxValue;
        foreach (var segment in segments)
        {
            var point = segment.Nearest(target);
            double distance = (point - target).LengthSquared;
            if (distance < best) { best = distance; nearest = point; }
        }
        return nearest;
    }
}
