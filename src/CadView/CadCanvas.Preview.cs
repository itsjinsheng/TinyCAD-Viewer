using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TinyCAD;

// Background preview lifecycle is kept separate from input and measurement handling.
public sealed partial class CadCanvas
{
    private BitmapSource? scenePreview;
    private Rect previewWorld;
    private Rect sceneBounds = Rect.Empty;
    private double previewZoom;
    private CancellationToken renderToken;
    private CancellationTokenSource? renderCancellation;
    private int renderGeneration;
    private int requestedGeneration = -1;
    private bool renderRunning;
    private PreviewRequest? queuedPreview;
    public bool IsRendering => renderRunning || queuedPreview != null || nativeBuilding || nativeNavigationPending;
    private sealed record PreviewRequest(int Generation, List<CadShape> Shapes, ShapeIndex Index,
        Dictionary<string, bool> Layers, Size Size, double Zoom, Vector Offset, bool Light, bool Weights,
        double DpiScale, CancellationToken Token);
    private void CancelPreview()
    {
        renderGeneration++; requestedGeneration = -1; queuedPreview = null;
        renderCancellation?.Cancel();
    }
    private void RequestPreview()
    {
        if (requestedGeneration == renderGeneration || ActualWidth <= 0 || ActualHeight <= 0) return;
        requestedGeneration = renderGeneration;
        renderCancellation = new CancellationTokenSource();
        var dpi = VisualTreeHelper.GetDpi(this);
        var request = new PreviewRequest(renderGeneration, Shapes, index, new(Layers), RenderSize, Zoom, offset,
            LightBackground, ShowLineWeights, Math.Min(dpi.DpiScaleX, dpi.DpiScaleY), renderCancellation.Token);
        if (renderRunning) queuedPreview = request; else StartPreview(request);
    }
    private void StartPreview(PreviewRequest request)
    {
        renderRunning = true;
        // One STA worker at a time. WPF geometry/text/bitmap objects are created on that
        // thread and frozen before delivery; the UI never waits for native rasterization.
        var thread = new Thread(() =>
        {
            BitmapSource? bitmap = null; DrawingGroup? vectors = null; Rect bounds = Rect.Empty;
            Exception? error = null;
            try
            {
                request.Token.ThrowIfCancellationRequested();
                var renderer = new CadCanvas();
                renderer.Measure(request.Size); renderer.Arrange(new Rect(request.Size));
                renderer.Shapes = request.Shapes; renderer.index = request.Index; renderer.Layers = request.Layers;
                renderer.Zoom = request.Zoom; renderer.offset = request.Offset;
                renderer.LightBackground = request.Light; renderer.ShowLineWeights = request.Weights;
                renderer.renderToken = request.Token;
                vectors = renderer.BuildDrawing();
                request.Token.ThrowIfCancellationRequested();
                renderer.drawing = vectors; renderer.CacheScenePreview(request.DpiScale);
                request.Token.ThrowIfCancellationRequested();
                bitmap = renderer.scenePreview; bounds = renderer.previewWorld;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex; }
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                renderRunning = false;
                if (request.Generation == renderGeneration && !request.Token.IsCancellationRequested)
                {
                    if (bitmap != null)
                    { scenePreview = bitmap; previewWorld = bounds; previewZoom = request.Zoom; drawing = vectors; InvalidateVisual(); }
                    else if (error != null) Status?.Invoke(L.T("图纸预览生成失败：") + error.Message);
                }
                if (queuedPreview is { } next) { queuedPreview = null; StartPreview(next); }
            }));
        })
        { IsBackground = true, Name = "TinyCAD preview renderer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    private void CacheScenePreview(double? dpiScale = null)
    {
        // A padded, DPI-aware snapshot keeps navigation inexpensive. After input settles,
        // rebuild from the original vectors at the final scale; measurements never use pixels.
        double marginX = ActualWidth * .25, marginY = ActualHeight * .25;
        previewZoom = Zoom;
        double width = ActualWidth + marginX * 2, height = ActualHeight + marginY * 2;
        previewWorld = new Rect(ToWorld(new(-marginX, -marginY)), ToWorld(new(ActualWidth + marginX, ActualHeight + marginY)));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var transform = ViewMatrix; transform.Translate(marginX, marginY);
            context.PushTransform(new MatrixTransform(transform)); context.DrawDrawing(drawing!); context.Pop();
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = Math.Min(dpiScale ?? Math.Min(dpi.DpiScaleX, dpi.DpiScaleY), 4096 / Math.Max(width, height));
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); scenePreview = bitmap;
    }
}
