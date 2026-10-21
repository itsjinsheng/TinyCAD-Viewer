using System.Windows;
using System.Windows.Media;

namespace TinyCAD;

public sealed partial class CadCanvas
{
    internal const int MaxOutputDimension = 8192;
    public Rect? PrintArea { get; private set; }
    public event Action? PrintAreaChanged;
    private Point? printAreaStart;
    private double printAreaZoom;
    private bool isOutputRendering;

    public void TogglePrintArea()
    {
        if (PrintArea != null || Mode == ToolMode.PrintArea)
        {
            ClearPrintArea();
            SetMode(ToolMode.Select);
            Status?.Invoke(L.T("已取消打印区域，导出和打印恢复为当前视图"));
            return;
        }
        SetMode(ToolMode.PrintArea);
        Status?.Invoke(L.T("请选择打印区域的第一个角点 · 右键或 Esc 取消"));
    }

    private void ClearPrintArea()
    {
        PrintArea = null;
        printAreaStart = null;
        PrintAreaChanged?.Invoke();
        InvalidateVisual();
    }

    private void AcceptPrintAreaCorner(Point world)
    {
        (cursorWorld, snapName) = Snap(world);
        world = cursorWorld;
        if (printAreaStart is not Point start)
        {
            printAreaStart = world;
            Status?.Invoke(L.T("请选择打印区域的第二个角点 · 右键或 Esc 取消"));
            InvalidateVisual();
            return;
        }
        var area = new Rect(start, world);
        if (area.Width * Zoom < 8 || area.Height * Zoom < 8)
        {
            Status?.Invoke(L.T("打印区域过小，请重新选择第二个角点"));
            return;
        }
        PrintArea = area;
        printAreaZoom = Zoom;
        SetMode(ToolMode.Select);
        PrintAreaChanged?.Invoke();
        Status?.Invoke(L.T("已设置打印区域：PNG 和打印/PDF 只输出橙色框内内容 · 再点打印区域可取消"));
    }

    private void DrawPrintArea(DrawingContext context)
    {
        Rect? area = printAreaStart is Point start
            ? new Rect(start, cursorWorld) : PrintArea;
        if (area is not Rect bounds) return;
        var screen = new Rect(ToScreen(bounds.TopLeft), ToScreen(bounds.BottomRight));
        context.DrawRectangle(null, new Pen(Brushes.Orange, 1.5) { DashStyle = DashStyles.Dash }, screen);
    }

    // Render an independent viewport, so regions outside the visible canvas are not lost.
    // PNG and print share this exact crop; UI selection frames never enter the output.
    public Size GetOutputSize() => GetOutputViewport().Size;

    private (Size Size, double Zoom, Vector Offset) GetOutputViewport()
    {
        double outputZoom = PrintArea != null ? printAreaZoom : Zoom;
        var size = PrintArea is Rect region
            ? new Size(region.Width * outputZoom, region.Height * outputZoom) : RenderSize;
        double scale = Math.Min(1, MaxOutputDimension / Math.Max(1, Math.Max(size.Width, size.Height)));
        size = new Size(Math.Max(1, size.Width * scale), Math.Max(1, size.Height * scale));
        outputZoom *= scale;
        var outputOffset = PrintArea is Rect crop
            ? new Vector(-crop.Left * outputZoom, crop.Bottom * outputZoom) : offset * scale;
        return (size, outputZoom, outputOffset);
    }

    public (DrawingGroup Drawing, Size Size) CreateOutputDrawing(double detailScale = 1)
    {
        var viewport = GetOutputViewport();
        var size = viewport.Size;
        var renderer = new CadCanvas();
        renderer.Measure(size);
        renderer.Arrange(new Rect(size));
        renderer.Shapes = Shapes;
        renderer.index = index;
        renderer.Layers = new(Layers);
        renderer.Zoom = viewport.Zoom;
        renderer.offset = viewport.Offset;
        renderer.geometryDetailScale = Math.Max(1, detailScale);
        renderer.ShowGrid = ShowGrid;
        renderer.ShowLineWeights = ShowLineWeights;
        renderer.LightBackground = LightBackground;
        renderer.VectorOutput = true;
        renderer.isOutputRendering = true;
        renderer.drawing = null; // Arrange may have rendered the initially empty scene.
        renderer.Measurements.AddRange(Measurements);
        foreach (var pair in labelOffsets) renderer.labelOffsets[pair.Key] = pair.Value;
        foreach (var pair in labelAnchors) renderer.labelAnchors[pair.Key] = pair.Value;

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.PushClip(new RectangleGeometry(new Rect(size)));
            renderer.OnRender(context);
            context.Pop();
        }
        drawing.Freeze();
        return (drawing, size);
    }
}
