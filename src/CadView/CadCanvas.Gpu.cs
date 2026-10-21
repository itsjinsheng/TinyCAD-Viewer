using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;


namespace TinyCAD;

public sealed partial class CadCanvas
{
    public bool GpuEnabled { get; private set; }
    public event Action<bool>? GpuModeChanged;
    public int HardwareTier => RenderCapability.Tier >> 16;
    public int GpuBuildCount { get; private set; }
    public int GpuBatchCount => nativeScene?.BatchCount ?? 0;
    public int GpuVisibleCount => nativeScene?.LastVisibleCount ?? 0;
    public double GpuFrameMs { get; private set; }
    public int GpuRenderedFrames => nativeSurface?.RenderedFrames ?? 0;
    public double GpuLockMs => nativeSurface?.LockMs ?? 0;
    public double GpuDrawMs => nativeSurface?.DrawMs ?? 0;
    public double GpuEndMs => nativeSurface?.EndMs ?? 0;
    public string? GpuFailure { get; private set; }
    private bool UseGpuDrawing => GpuEnabled && !VectorOutput && !isOutputRendering;
    private Direct3DSurface? nativeSurface;
    private Direct3DBatchScene? nativeScene;
    private CancellationTokenSource? nativeCancellation;
    private int nativeGeneration;
    private bool nativeBuilding, nativeLight, nativeWeights;
    private bool nativeNavigationPending, nativeFrameHooked;
    private TimeSpan lastNativeCompositionTime;
    private int nativeIdleTicks;
    private DispatcherOperation? nativeFrameDispatch;
    private Dictionary<string, bool>? nativeLayers;
    private (double Zoom, Vector Offset, Size Size, double Dpi)? nativeFrame;

    private void RequestNativeNavigationFrame()
    {
        nativeNavigationPending = true;
        nativeIdleTicks = 0;
        if (!nativeFrameHooked && IsLoaded)
        {
            CompositionTarget.Rendering += OnNativeNavigationFrame;
            nativeFrameHooked = true;
        }
    }

    private void OnNativeNavigationFrame(object? sender, EventArgs args)
    {
        var time = ((RenderingEventArgs)args).RenderingTime;
        if (time == lastNativeCompositionTime) return;
        lastNativeCompositionTime = time;
        if (nativeFrameDispatch != null) return;
        // Let already queued input update the camera before requesting a high-priority render pass.
        nativeFrameDispatch = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            nativeFrameDispatch = null;
            if (!GpuEnabled || !IsLoaded) return;
            if (nativeNavigationPending)
            {
                nativeNavigationPending = false; nativeIdleTicks = 0;
                InvalidateVisual();
            }
            else if (++nativeIdleTicks >= 2) CancelNativeNavigationFrame();
        }));
    }

    private void CancelNativeNavigationFrame()
    {
        nativeFrameDispatch?.Abort(); nativeFrameDispatch = null;
        if (nativeFrameHooked) CompositionTarget.Rendering -= OnNativeNavigationFrame;
        nativeFrameHooked = false; nativeNavigationPending = false;
    }

    public void SetGpuEnabled(bool enabled)
    {
        if (GpuEnabled == enabled) return;
        GpuEnabled = enabled; GpuModeChanged?.Invoke(enabled); GpuFailure = null;
        CancelPreview(); navigationTimer.Stop(); navigating = false;
        ResetNativeScene();
        if (!enabled) { CancelNativeNavigationFrame(); nativeSurface?.Dispose(); nativeSurface = null; }
        scenePreview = null; drawing = null; InvalidateVisual();
        Status?.Invoke(enabled ? L.T("Direct3D 11 硬件加速试验") : L.T("已切回稳定版预览渲染"));
    }

    private void ResetNativeScene()
    {
        nativeGeneration++;
        nativeCancellation?.Cancel(); nativeCancellation?.Dispose(); nativeCancellation = null;
        nativeScene?.Dispose(); nativeScene = null; nativeLayers = null;
        nativeBuilding = false; nativeFrame = null;
    }

    private void StopNativeRendering()
    {
        CancelNativeNavigationFrame(); ResetNativeScene(); nativeSurface?.Dispose(); nativeSurface = null;
    }

    private void FailNativeRendering(Exception error)
    {
        SetGpuEnabled(false); GpuFailure = error.ToString();
        Status?.Invoke(L.T("Direct3D 11 未能运行，已回退：") + error.Message);
    }

    private void EnsureNativeScene()
    {
        bool compatible = nativeLayers != null && nativeLight == LightBackground && nativeWeights == ShowLineWeights
            && nativeLayers.Count == Layers.Count && Layers.All(p => nativeLayers.GetValueOrDefault(p.Key) == p.Value);
        if (compatible && (nativeBuilding || nativeScene != null && Zoom <= nativeScene.DetailScale)) return;
        bool refine = compatible && nativeScene != null;
        if (!refine) ResetNativeScene();
        else
        {
            nativeGeneration++; nativeCancellation?.Cancel(); nativeCancellation?.Dispose();
        }
        if (nativeSurface == null)
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source == null) return;
            if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly || HardwareTier == 0)
                throw new InvalidOperationException(L.T("当前桌面会话不支持 WPF 硬件合成。"));
            nativeSurface = new Direct3DSurface(source.Handle);
            nativeSurface.Image.IsFrontBufferAvailableChanged += (_, _) => { nativeFrame = null; InvalidateVisual(); };
        }
        nativeLight = LightBackground; nativeWeights = ShowLineWeights; nativeLayers = new(Layers);
        var shapes = Shapes; var layers = nativeLayers; bool light = nativeLight, weights = nativeWeights;
        nativeCancellation = new(); var token = nativeCancellation.Token;
        int generation = nativeGeneration; nativeBuilding = true;
        double detailScale = Zoom * (refine ? 16 : 64);
        // Keep an independent COM reference while the window may close or replace its surface.
        var device = nativeSurface.Device.QueryInterface<Vortice.Direct3D11.ID3D11Device>();
        var reusable = refine ? nativeScene?.RetainFixedGeometry() : null;
        var fixedPlan = refine ? nativeScene?.FixedPlan : null;
        var worker = new Thread(() =>
        {
            Direct3DBatchScene? prepared = null; Exception? error = null;
            try
            {
                var mesh = GpuMesh.Build(shapes, layers, light, weights, detailScale, token, fixedPlan);
                token.ThrowIfCancellationRequested();
                // Device resource creation is thread-safe; the immediate context stays on the UI thread.
                prepared = new Direct3DBatchScene(device, mesh, reusable);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex; }
            finally { reusable?.Release(); device.Dispose(); }
            if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            { prepared?.Dispose(); return; }
            var completion = Dispatcher.InvokeAsync(() =>
            {
                if (generation != nativeGeneration || token.IsCancellationRequested) { prepared?.Dispose(); return; }
                nativeBuilding = false;
                if (prepared != null)
                {
                    try
                    {
                        nativeScene?.Dispose(); nativeScene = prepared;
                        GpuBuildCount++; nativeFrame = null; InvalidateVisual();
                    }
                    catch (Exception ex) { FailNativeRendering(ex); }
                }
                else if (error != null) FailNativeRendering(error);
            }, DispatcherPriority.Background);
            _ = completion.Task.ContinueWith(task => { if (task.IsCanceled) prepared?.Dispose(); }, TaskScheduler.Default);
        }) { IsBackground = true, Name = "TinyCAD Direct3D 11 geometry" };
        try { worker.SetApartmentState(ApartmentState.STA); worker.Start(); }
        catch { reusable?.Release(); device.Dispose(); throw; }
    }

    private void DrawNativeScene(DrawingContext dc)
    {
        try
        {
            EnsureNativeScene();
            if (nativeScene == null || nativeSurface == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var key = (Zoom, offset, RenderSize, dpi);
            if (nativeFrame != key)
            {
                var watch = Stopwatch.StartNew();
                nativeSurface.Resize(Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi)), Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi)));
                if (nativeSurface.RenderDirect(() => nativeScene.Draw(nativeSurface, Zoom, offset, RenderSize, dpi))) nativeFrame = key;
                GpuFrameMs = watch.Elapsed.TotalMilliseconds;
            }
            dc.DrawImage(nativeSurface.Image, new Rect(RenderSize));
        }
        catch (Exception error) { FailNativeRendering(error); }
    }
}
