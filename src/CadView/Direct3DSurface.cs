using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using D9 = Vortice.Direct3D9;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace TinyCAD;

// D3D9Ex shared surface is the bridge required by WPF D3DImage. All actual drawing
// takes place on a hardware D3D11 device, without CPU bitmap copies.
internal sealed class Direct3DSurface : IDisposable
{
    public D3DImage Image { get; } = new();
    private ID3D11Device? device;
    private ID3D11DeviceContext? immediate;
    private IDirect3D9Ex? d9;
    private IDirect3DDevice9Ex? device9;
    private IDirect3DTexture9? texture9;
    private IDirect3DSurface9? surface9;
    private ID3D11Texture2D? texture;
    private ID3D11RenderTargetView? renderTarget;
    private ID3D11Texture2D? multisample;
    private ID3D11Query? completed;
    public ID3D11Device Device => device!;
    public ID3D11DeviceContext Immediate => immediate!;
    private int width, height;
    public int RenderedFrames { get; private set; }
    public double LockMs { get; private set; }
    public double DrawMs { get; private set; }
    public double EndMs { get; private set; }

    public Direct3DSurface(IntPtr window)
    {
        try
        {
            device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0);
            immediate = device.ImmediateContext;
            completed = device.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));
            d9 = D3D9.Direct3DCreate9Ex();
            device9 = d9.CreateDeviceEx(0, D9.DeviceType.Hardware, window,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                new D9.PresentParameters { Windowed = true, SwapEffect = D9.SwapEffect.Discard,
                    DeviceWindowHandle = window, BackBufferWidth = 1, BackBufferHeight = 1,
                    BackBufferFormat = D9.Format.Unknown, PresentationInterval = PresentInterval.Default });
        }
        catch { Dispose(); throw; }
    }

    public void Resize(int w, int h)
    {
        if (w == width && h == height && renderTarget != null) return;
        ReleaseTarget();
        width = w; height = h;
        IntPtr handle = IntPtr.Zero;
        texture9 = device9!.CreateTexture((uint)w, (uint)h, 1, D9.Usage.RenderTarget,
            D9.Format.A8R8G8B8, Pool.Default, ref handle);
        surface9 = texture9.GetSurfaceLevel(0);
        texture = device!.OpenSharedResource<ID3D11Texture2D>(handle);
        uint sampleCount = device.CheckMultisampleQualityLevels(Vortice.DXGI.Format.B8G8R8A8_UNorm, 4) > 0 ? 4u : 1u;
        if (sampleCount > 1)
            multisample = device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h,
                MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(sampleCount, 0), BindFlags = BindFlags.RenderTarget });
        renderTarget = device.CreateRenderTargetView(multisample ?? texture);
        Image.Lock();
        try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface9.NativePointer, true); }
        finally { Image.Unlock(); }
    }

    public bool RenderDirect(Action draw)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew(); Image.Lock(); LockMs = watch.Elapsed.TotalMilliseconds;
        try
        {
            watch.Restart();
            immediate!.OMSetRenderTargets(renderTarget!);
            immediate.ClearRenderTargetView(renderTarget!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            draw(); DrawMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            immediate.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
            if (multisample != null) immediate.ResolveSubresource(texture!, 0, multisample, 0, Vortice.DXGI.Format.B8G8R8A8_UNorm);
            immediate.End(completed!); immediate.Flush();
            while (immediate.GetData(completed!, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush).Code != 0)
            {
                if (watch.Elapsed.TotalSeconds > 2) throw new InvalidOperationException(L.T("GPU 未及时完成绘制，回退稳定渲染。"));
                Thread.Yield();
            }
            EndMs = watch.Elapsed.TotalMilliseconds;
            Image.AddDirtyRect(new Int32Rect(0, 0, width, height)); RenderedFrames++; return true;
        }
        finally { Image.Unlock(); }
    }

    private void ReleaseTarget()
    {
        Image.Lock();
        try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
        finally { Image.Unlock(); }
        renderTarget?.Dispose(); renderTarget = null; multisample?.Dispose(); multisample = null;
        texture?.Dispose(); texture = null;
        surface9?.Dispose(); surface9 = null; texture9?.Dispose(); texture9 = null;
    }
    public void Dispose()
    {
        ReleaseTarget();
        completed?.Dispose(); immediate?.Dispose(); device?.Dispose(); device9?.Dispose(); d9?.Dispose();
    }
}
