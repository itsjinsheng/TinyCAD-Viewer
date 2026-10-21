using System.Windows;

namespace TinyCAD;

public sealed partial class CadCanvas
{
    private const double SnapRadiusPixels = 10;
    private const string AreaStartSnap = "闭合起点";
    private const string ChainStartSnap = "累计起点";

    private bool IsCompletionDoubleClick(int clickCount) =>
        clickCount == 2 && Mode is ToolMode.Area or ToolMode.Chain;

    private bool IsNearMeasurementStart(Point point)
    {
        int minimumPoints = Mode switch
        {
            ToolMode.Area => 3,
            ToolMode.Chain => 2,
            _ => int.MaxValue
        };
        return pending.Count >= minimumPoints && Geo.Distance(point, pending[0]) <= SnapRadiusPixels / Zoom;
    }

    public void AcceptPoint(Point raw, int clickCount = 1)
    {
        // The first click already added the last vertex. The second only finishes the measurement.
        if (IsCompletionDoubleClick(clickCount))
        {
            if (pending.Count > 0) Finish();
            return;
        }
        if (Mode == ToolMode.Area && IsNearMeasurementStart(raw))
        { Finish(); return; }
        (cursorWorld, snapName) = Snap(raw); ApplyOrtho();
        if (Mode is ToolMode.Select or ToolMode.Radius or ToolMode.Entity)
        {
            selected = Pick(raw);
            if (selected != null)
            {
                var description = CreateShapeDescription(selected);
                Selection?.Invoke(description());
                if (Mode == ToolMode.Radius)
                {
                    var seg = selected.Segments.Where(s => s.IsCircle).MinBy(s => Geo.Distance(raw, s.Nearest(raw)));
                    if (seg != null) CompleteValue("半径 {0} · 直径 {1} {2}", [Value(seg.U.Length), Value(seg.U.Length * 2), UnitLabel], [seg.Center, seg.Nearest(raw)], unitIndex: 2);
                    else Status?.Invoke(L.T("请选择圆或圆弧；非等比例缩放的椭圆没有单一半径。"));
                }
                if (Mode == ToolMode.Entity) Complete(() => description().Replace("\n", " · "), selected.Segments.Count > 0 ? [selected.Segments[0].At(0)] : [selected.TextPoint], shape: selected);
            }
            else Selection?.Invoke(L.T("未选中对象"));
            InvalidateVisual(); return;
        }
        if (pending.Count > 0 && Geo.Distance(pending[^1], cursorWorld) < 1e-12) return;
        pending.Add(cursorWorld);
        if (Mode == ToolMode.Distance && pending.Count == 2)
        {
            var d = pending[1] - pending[0];
            CompleteValue("距离 {0} {1} · ΔX {2} · ΔY {3}", [Value(d.Length), UnitLabel, Value(Math.Abs(d.X)), Value(Math.Abs(d.Y))], [.. pending], unitIndex: 1); pending.Clear();
        }
        if (Mode == ToolMode.Angle && pending.Count == 3)
        {
            var a = pending[1] - pending[0]; var b = pending[2] - pending[0];
            if (a.Length > 1e-12 && b.Length > 1e-12) CompleteValue("夹角 {0}°", [Value(Math.Abs(Vector.AngleBetween(a, b)), 0)], [pending[1], pending[0], pending[2]]);
            else Status?.Invoke(L.T("角的两条边长度必须大于零。")); pending.Clear();
        }
        InvalidateVisual();
    }
    public void Finish()
    {
        if (Mode == ToolMode.Chain && pending.Count >= 2)
            CompleteValue("累计长度 {0} {1}", [Value(pending.Zip(pending.Skip(1), Geo.Distance).Sum()), UnitLabel], [.. pending], unitIndex: 1);
        else if (Mode == ToolMode.Area && pending.Count >= 3)
        {
            bool crosses = false;
            for (int i = 0; i < pending.Count; i++) for (int j = i + 2; j < pending.Count; j++)
                    if (!(i == 0 && j == pending.Count - 1) && Geo.Intersection(pending[i], pending[(i + 1) % pending.Count], pending[j], pending[(j + 1) % pending.Count], out _)) crosses = true;
            if (crosses) { Status?.Invoke(L.T("多边形有自交，无法给出有效面积；请撤回交叉点。")); return; }
            var perimeter = pending.Select((p, i) => Geo.Distance(p, pending[(i + 1) % pending.Count])).Sum();
            CompleteValue("面积 {0} {1}² · 周长 {2} {1}", [Value(Geo.PolygonArea(pending), 2), UnitLabel, Value(perimeter)], [.. pending], true, unitIndex: 1);
        }
        else if (pending.Count > 0) { Status?.Invoke(L.T("点数不足：累计长度至少 2 点，面积至少 3 点。")); return; }
        pending.Clear(); InvalidateVisual();
    }
    private void CompleteValue(string template, object[] values, List<Point> points, bool closed = false, int unitIndex = -1)
    {
        // Store raw values and source units so repeated switches never compound rounding.
        if (unitIndex >= 0) values[unitIndex] = new UnitText(this, UnitKey);
        Complete(() => L.Format(template, values), points, closed);
    }
    private sealed record UnitText(CadCanvas Canvas, string Key)
    {
        public override string ToString() => Canvas.DisplayLabel(Key);
    }
    private void Complete(Func<string> text, List<Point> points, bool closed = false, CadShape? shape = null)
    {
        var result = new MeasureResult(text(), points, closed, shape) { Number = ++measurementNumber, TextFactory = text };
        Measurements.Add(result); Measured?.Invoke(result); Status?.Invoke(result.CurrentText); InvalidateVisual();
    }
}
