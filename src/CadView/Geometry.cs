using System.Windows;
using System.Windows.Media;

namespace TinyCAD;

public static class Geo
{
    public const double Tau = Math.PI * 2;
    public static double Cross(Vector a, Vector b) => a.X * b.Y - a.Y * b.X;
    public static double Positive(double a) => (a % Tau + Tau) % Tau;
    public static double Distance(Point a, Point b) => (a - b).Length;
    public static Point NearestLine(Point p, Point a, Point b)
    {
        var v = b - a;
        return a + v * (v.LengthSquared < 1e-24 ? 0 : Math.Clamp(Vector.Multiply(p - a, v) / v.LengthSquared, 0, 1));
    }
    public static bool Intersection(Point a, Point b, Point c, Point d, out Point p)
    {
        p = default;
        var u = b - a; var v = d - c;
        var det = Cross(u, v);
        if (Math.Abs(det) < 1e-12 * Math.Max(1, u.Length * v.Length)) return false;
        var t = Cross(c - a, v) / det; var s = Cross(c - a, u) / det;
        if (t < -1e-10 || t > 1 + 1e-10 || s < -1e-10 || s > 1 + 1e-10) return false;
        p = a + u * t;
        return true;
    }
    public static double PolygonArea(IReadOnlyList<Point> pts)
    {
        if (pts.Count < 3) return 0;
        var origin = pts[0];
        double a = 0;
        for (int i = 1; i + 1 < pts.Count; i++) a += Cross(pts[i] - origin, pts[i + 1] - origin);
        return Math.Abs(a / 2);
    }
}

// All geometry is kept in drawing coordinates. Display tessellation never drives measurements.
public sealed record Segment
{
    public bool IsArc { get; init; }
    public Point A { get; init; }
    public Point B { get; init; }
    public Point Center { get; init; }
    public Vector U { get; init; }
    public Vector V { get; init; }
    public double Start { get; init; }
    public double Sweep { get; init; }
    public bool IsCircle => IsArc && Math.Abs(U.Length - V.Length) <= 1e-8 * Math.Max(U.Length, V.Length) && Math.Abs(Vector.Multiply(U, V)) <= 1e-8 * U.Length * V.Length;
    public bool Full => IsArc && Math.Abs(Sweep) >= Geo.Tau - 1e-9;
    public Point At(double t) => IsArc ? Center + U * Math.Cos(Start + Sweep * t) + V * Math.Sin(Start + Sweep * t) : A + (B - A) * t;
    public static Segment Line(Point a, Point b) => new() { A = a, B = b };
    public static Segment Arc(Point c, double r, double start, double sweep) => new() { IsArc = true, Center = c, U = new(r, 0), V = new(0, r), Start = start, Sweep = sweep };
    public static Segment Bulge(Point a, Point b, double bulge)
    {
        if (Math.Abs(bulge) < 1e-12 || Geo.Distance(a, b) < 1e-12) return Line(a, b);
        var d = b - a;
        var c = a + d * 0.5 + new Vector(-d.Y, d.X) * ((1 - bulge * bulge) / (4 * bulge));
        return Arc(c, Geo.Distance(a, c), Math.Atan2(a.Y - c.Y, a.X - c.X), 4 * Math.Atan(bulge));
    }
    public Segment Transform(Matrix m) => IsArc ? this with { Center = m.Transform(Center), U = m.Transform(U), V = m.Transform(V) } : Line(m.Transform(A), m.Transform(B));
    public bool ContainsAngle(double angle)
    {
        if (Full) return true;
        return (Sweep >= 0 ? Geo.Positive(angle - Start) : Geo.Positive(Start - angle)) <= Math.Abs(Sweep) + 1e-10;
    }
    public Point Nearest(Point p)
    {
        if (!IsArc) return Geo.NearestLine(p, A, B);
        if (IsCircle)
        {
            var q = p - Center;
            double angle = Math.Atan2(Vector.Multiply(q, V) / V.LengthSquared, Vector.Multiply(q, U) / U.LengthSquared);
            if (ContainsAngle(angle)) return Center + U * Math.Cos(angle) + V * Math.Sin(angle);
            return Geo.Distance(p, At(0)) <= Geo.Distance(p, At(1)) ? At(0) : At(1);
        }
        // Bounded minimization around every coarse local minimum, for affine ellipses.
        double best = double.MaxValue, bestT = 0;
        for (int i = 0; i <= 64; i++)
        {
            var t = i / 64.0; var dist = (At(t) - p).LengthSquared;
            if (dist < best) { best = dist; bestT = t; }
        }
        double lo = Math.Max(0, bestT - 1.0 / 64), hi = Math.Min(1, bestT + 1.0 / 64);
        for (int i = 0; i < 40; i++)
        {
            var l = lo + (hi - lo) / 3; var r = hi - (hi - lo) / 3;
            if ((At(l) - p).LengthSquared < (At(r) - p).LengthSquared) hi = r; else lo = l;
        }
        var candidate = At((lo + hi) / 2);
        return (candidate - p).LengthSquared < best ? candidate : At(bestT);
    }
    public double Length
    {
        get
        {
            if (!IsArc) return Geo.Distance(A, B);
            if (IsCircle) return U.Length * Math.Abs(Sweep);
            double sum = 0; const int n = 1024;
            for (int i = 0; i <= n; i++)
            {
                double a = Start + Sweep * i / n;
                double f = (-U * Math.Sin(a) + V * Math.Cos(a)).Length;
                sum += f * (i == 0 || i == n ? 1 : i % 2 == 0 ? 2 : 4);
            }
            return sum * Math.Abs(Sweep) / (3 * n);
        }
    }
    public double SignedArea(Point origin)
    {
        if (!IsArc) return Geo.Cross(A - origin, B - origin) / 2;
        return (Geo.Cross(Center - origin, At(1) - At(0)) + Geo.Cross(U, V) * Sweep) / 2;
    }
    public IEnumerable<Point> Samples(double scale)
    {
        int n = IsArc ? Math.Clamp((int)Math.Ceiling(Math.Abs(Sweep) * Math.Sqrt(Math.Max(1, (U.Length + V.Length) * scale))), 8, 8192) : 1;
        for (int i = 0; i <= n; i++) yield return At((double)i / n);
    }
    public Rect Bounds
    {
        get
        {
            var r = new Rect(At(0), At(1));
            if (IsArc)
                foreach (var a in new[] { Math.Atan2(V.X, U.X), Math.Atan2(V.X, U.X) + Math.PI, Math.Atan2(V.Y, U.Y), Math.Atan2(V.Y, U.Y) + Math.PI })
                    if (ContainsAngle(a)) r.Union(Center + U * Math.Cos(a) + V * Math.Sin(a));
            return r;
        }
    }
}

public sealed class CadShape
{
    public string Layer { get; set; } = "0";
    public string Type { get; set; } = "";
    public string Handle { get; set; } = "";
    public Color Color { get; set; } = Colors.White;
    public List<Segment> Segments { get; } = [];
    public bool Closed { get; set; }
    public bool Filled { get; set; }
    public bool Approximate { get; set; }
    public string? Text { get; set; }
    public Point TextPoint { get; set; }
    public double TextHeight { get; set; } = 2.5;
    public double TextRotation { get; set; }
    public double TextWidthFactor { get; set; } = 1;
    public int TextAlign { get; set; }
    public double StrokeWidth { get; set; } = 1;
    public double[]? Dashes { get; set; }
    public HashSet<int> FigureStarts { get; } = [0];
    public HashSet<string> RequiredLayers { get; } = [];
    public Rect? Clip { get; set; }
    public bool PaperProjection { get; set; }
    public Rect Bounds { get; set; }
    public double Length => Segments.Sum(s => s.Length);
    public double Area => Closed && Segments.Count > 0 ? Math.Abs(Segments.Sum(s => s.SignedArea(Segments[0].At(0)))) : 0;
    public void UpdateBounds()
    {
        var r = Rect.Empty;
        foreach (var s in Segments) r.Union(s.Bounds);
        if (Text != null)
        {
            var m = Matrix.Identity; m.Rotate(TextRotation * 180 / Math.PI); m.Translate(TextPoint.X, TextPoint.Y);
            double w = Math.Max(1, Text.Split('\n').Max(l => l.Sum(c => c > 255 ? 1.1 : .7))) * TextHeight * TextWidthFactor;
            double h = (Text.Count(c => c == '\n') + 1) * TextHeight * 1.6;
            double left = TextAlign == 1 ? -w / 2 : TextAlign == 2 ? -w : 0;
            double bottom = Type == "MTEXT" ? -h : -TextHeight * .4;
            foreach (var p in new[] { new Point(left, bottom), new Point(left + w, bottom), new Point(left + w, bottom + h), new Point(left, bottom + h) }) r.Union(m.Transform(p));
        }
        Bounds = r;
    }
}
