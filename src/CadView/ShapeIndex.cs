using System.Windows;

namespace TinyCAD;

// Bounding-volume tree keeps hit testing local without a grid exploding for very large coordinates.
internal sealed class ShapeIndex
{
    private readonly Rect bounds;
    private readonly ShapeIndex? left, right;
    private readonly List<CadShape>? leaf;
    public ShapeIndex(IEnumerable<CadShape> items) : this(items.ToList(), 0) { }
    private ShapeIndex(List<CadShape> items, int depth)
    {
        bounds = Rect.Empty; foreach (var s in items) bounds.Union(s.Bounds);
        if (items.Count <= 24 || depth >= 24) { leaf = items; return; }
        bool x = bounds.Width >= bounds.Height;
        items.Sort((a, b) => (x ? a.Bounds.X + a.Bounds.Width / 2 : a.Bounds.Y + a.Bounds.Height / 2).CompareTo(x ? b.Bounds.X + b.Bounds.Width / 2 : b.Bounds.Y + b.Bounds.Height / 2));
        int half = items.Count / 2; left = new(items.GetRange(0, half), depth + 1); right = new(items.GetRange(half, items.Count - half), depth + 1);
    }
    public IEnumerable<CadShape> Query(Rect area)
    {
        if (!bounds.IntersectsWith(area)) yield break;
        if (leaf != null) { foreach (var s in leaf) if (s.Bounds.IntersectsWith(area)) yield return s; }
        else { foreach (var s in left!.Query(area)) yield return s; foreach (var s in right!.Query(area)) yield return s; }
    }
}
