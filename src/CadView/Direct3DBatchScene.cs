using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vector = System.Windows.Vector;

namespace TinyCAD;

internal sealed class Direct3DBatchScene : IDisposable
{
    private static readonly Lazy<byte[]> VertexBytecode = new(() => Compiler.Compile(Shader, "VSMain", "TinyCAD", "vs_5_0", ShaderFlags.OptimizationLevel3).ToArray());
    private static readonly Lazy<byte[]> PixelBytecode = new(() => Compiler.Compile(Shader, "PSMain", "TinyCAD", "ps_4_0", ShaderFlags.OptimizationLevel3).ToArray());
    private readonly GpuMesh.Batch[] batches;
    internal sealed class GeometryBuffer
    {
        private readonly ID3D11Buffer? buffer;
        public ID3D11ShaderResourceView? View { get; }
        private int references = 1;
        public GeometryBuffer(ID3D11Device device, GpuPrimitive[] data)
        {
            if (data.Length == 0) return;
            buffer = device.CreateBuffer(data, BindFlags.ShaderResource, ResourceUsage.Immutable,
                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, structureByteStride: 68);
            try { View = device.CreateShaderResourceView(buffer); }
            catch { buffer.Dispose(); throw; }
        }
        public GeometryBuffer Retain() { Interlocked.Increment(ref references); return this; }
        public void Release() { if (Interlocked.Decrement(ref references) == 0) { View?.Dispose(); buffer?.Dispose(); } }
    }
    private readonly GeometryBuffer? geometry;
    private readonly GeometryBuffer? curves;
    private bool disposed;
    public GeometryBuffer? RetainFixedGeometry() => geometry?.Retain();
    private readonly ID3D11Buffer? visibleIndices;
    private readonly int[] indices;
    private readonly List<(int Start, int Count, int Left, int Top, int Right, int Bottom)> commands = new();
    private readonly ID3D11Buffer constants;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11InputLayout layout;
    private readonly ID3D11BlendState blend;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState depth;
    public int BatchCount => batches.Length;
    public int LastVisibleCount { get; private set; }
    public double DetailScale { get; }
    public GpuMesh.FixedPlan FixedPlan { get; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Camera { public Vector4 Position; public Vector4 View; }
    public Direct3DBatchScene(ID3D11Device device, GpuMesh mesh, GeometryBuffer? reusable = null)
    {
        try
        {
            batches = mesh.Batches; DetailScale = mesh.DetailScale; FixedPlan = mesh.Plan;
            if (reusable == null && mesh.Primitives.Length != mesh.Plan.PrimitiveCount)
                throw new InvalidOperationException(L.T("细化场景缺少固定图形缓存。"));
            var vs = VertexBytecode.Value; var ps = PixelBytecode.Value;
            vertexShader = device.CreateVertexShader(vs); pixelShader = device.CreatePixelShader(ps);
            layout = device.CreateInputLayout([new InputElementDescription("INDEX", 0, Format.R32_UInt, 0, 0, InputClassification.PerInstanceData, 1)], vs);
            indices = new int[mesh.Plan.PrimitiveCount + mesh.Curves.Length];
            geometry = reusable?.Retain() ?? new GeometryBuffer(device, mesh.Primitives);
            curves = new GeometryBuffer(device, mesh.Curves);
            if (indices.Length > 0)
            {
                visibleIndices = device.CreateBuffer((uint)(indices.Length * 4), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            }
            constants = device.CreateBuffer(32, BindFlags.ConstantBuffer);
            var description = new BlendDescription();
            description.RenderTarget[0] = new RenderTargetBlendDescription { BlendEnable = true,
                SourceBlend = Blend.One, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All };
            blend = device.CreateBlendState(description);
            rasterizer = device.CreateRasterizerState(new RasterizerDescription { FillMode = FillMode.Solid, CullMode = CullMode.None,
                ScissorEnable = true, MultisampleEnable = true, DepthClipEnable = true });
            depth = device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = false, StencilEnable = false });
        }
        catch { Dispose(); throw; }
    }

    public void Draw(Direct3DSurface surface, double zoom, Vector offset, Size size, double dpi)
    {
        var dc = surface.Immediate;
        double x = (size.Width / 2 - offset.X) / zoom, y = (offset.Y - size.Height / 2) / zoom;
        float hx = (float)x, hy = (float)y;
        var camera = new Camera { Position = new(hx, hy, (float)(x - hx), (float)(y - hy)),
            View = new((float)(size.Width * dpi), (float)(size.Height * dpi), (float)(zoom * dpi), (float)dpi) };
        dc.UpdateSubresource(camera, constants);
        dc.IASetInputLayout(layout); dc.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        dc.VSSetShader(vertexShader); dc.PSSetShader(pixelShader); dc.VSSetConstantBuffer(0, constants);
        dc.RSSetState(rasterizer); dc.OMSetBlendState(blend); dc.OMSetDepthStencilState(depth);
        dc.RSSetViewport(0, 0, (float)(size.Width * dpi), (float)(size.Height * dpi));
        if (visibleIndices == null) return;
        dc.IASetVertexBuffer(0, visibleIndices, 4, 0);
        dc.VSSetShaderResource(0, geometry!.View); dc.VSSetShaderResource(1, curves!.View);
        var view = new Rect(x - size.Width / zoom / 2, y - size.Height / zoom / 2, size.Width / zoom, size.Height / zoom);
        view.Inflate(8 / zoom, 8 / zoom); LastVisibleCount = 0;
        commands.Clear(); int visibleCount = 0;
        foreach (var batch in batches)
        {
            if (zoom * dpi < batch.MinScale || zoom * dpi >= batch.MaxScale) continue;
            if (!batch.Bounds.IntersectsWith(view)) continue;
            int left = 0, top = 0, right = (int)Math.Ceiling(size.Width * dpi), bottom = (int)Math.Ceiling(size.Height * dpi);
            if (batch.Clip is Rect clip)
            {
                left = Math.Max(left, (int)Math.Floor((clip.Left * zoom + offset.X) * dpi));
                top = Math.Max(top, (int)Math.Floor((-clip.Bottom * zoom + offset.Y) * dpi));
                right = Math.Min(right, (int)Math.Ceiling((clip.Right * zoom + offset.X) * dpi));
                bottom = Math.Min(bottom, (int)Math.Ceiling((-clip.Top * zoom + offset.Y) * dpi));
            }
            if (right <= left || bottom <= top) continue;
            int start = visibleCount;
            int mask = batch.Curved ? int.MinValue : 0;
            for (int i = 0; i < batch.Count; i++) indices[visibleCount++] = (batch.Start + i) | mask;
            if (commands.Count > 0 && commands[^1].Left == left && commands[^1].Top == top
                && commands[^1].Right == right && commands[^1].Bottom == bottom)
            {
                var previous = commands[^1]; previous.Count += batch.Count; commands[^1] = previous;
            }
            else commands.Add((start, batch.Count, left, top, right, bottom));
        }
        if (visibleCount == 0) return;
        var mapped = dc.Map(visibleIndices!, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try { Marshal.Copy(indices, 0, mapped.DataPointer, visibleCount); }
        finally { dc.Unmap(visibleIndices!, 0); }
        foreach (var command in commands)
        {
            dc.RSSetScissorRect(command.Left, command.Top, command.Right - command.Left, command.Bottom - command.Top);
            dc.DrawInstanced(6, (uint)command.Count, 0, (uint)command.Start); LastVisibleCount++;
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        geometry?.Release(); curves?.Release(); visibleIndices?.Dispose(); constants?.Dispose(); vertexShader?.Dispose(); pixelShader?.Dispose(); layout?.Dispose();
        blend?.Dispose(); rasterizer?.Dispose(); depth?.Dispose();
    }

    private const string Shader = """
cbuffer Camera : register(b0) { float4 camera; float4 view; };
struct Input { float2 ah; float2 al; float2 bh; float2 bl; float2 ch; float2 cl;
uint color; float width; uint mode; float arcStart; float arcSweep; };
StructuredBuffer<Input> geometry:register(t0);
StructuredBuffer<Input> curves:register(t1);
struct Output { float4 position:SV_POSITION; nointerpolation float4 color:COLOR;
nointerpolation float2 a:TEXCOORD0; nointerpolation float2 b:TEXCOORD1;
nointerpolation float width:TEXCOORD2; nointerpolation uint mode:TEXCOORD3;
nointerpolation float2 c:TEXCOORD4; nointerpolation float2 angles:TEXCOORD5; };
float2 screen(float2 hi,float2 lo) {
    precise float2 delta = (hi-camera.xy)+(lo-camera.zw);
    return delta*float2(view.z,-view.z)+view.xy*.5;
}
Output VSMain(uint index:INDEX,uint id:SV_VertexID) {
    Input v;
    if(index&0x80000000) v=curves[index&0x7fffffff]; else v=geometry[index];
    Output o; o.color=float4(v.color&255,(v.color>>8)&255,(v.color>>16)&255,(v.color>>24)&255)/255.0;
    o.a=screen(v.ah,v.al); o.b=screen(v.bh,v.bl);
    o.width=v.width*view.w*.5; o.mode=v.mode; o.c=0; o.angles=float2(v.arcStart,v.arcSweep); float2 p;
    if(v.mode==1) {
        p=id==0?o.a:id==1?o.b:id==2?screen(v.ch,v.cl):o.a;
    } else if(v.mode==2 || v.mode==3) {
        o.b-=o.a; o.c=screen(v.ch,v.cl)-o.a;
        float2 extent=sqrt(o.b*o.b+o.c*o.c)+o.width+1;
        float sx=(id==2||id==4||id==5)?1:-1;
        float sy=(id==1||id==3||id==4)?1:-1;
        p=o.a+extent*float2(sx,sy);
    } else {
        float2 direction=o.b-o.a; float lengthD=length(direction);
        direction=lengthD>.0001?direction/lengthD:float2(1,0);
        float2 normal=float2(-direction.y,direction.x);
        float along=(id==2||id==4||id==5)?1:0;
        float side=(id==1||id==3||id==4)?1:-1;
        float radius=o.width+1;
        p=lerp(o.a,o.b,along)+direction*(along*2-1)*radius+normal*side*radius;
    }
    o.position=float4(p.x/view.x*2-1,1-p.y/view.y*2,0,1); return o;
}
float4 PSMain(Output v):SV_TARGET {
    float coverage=1;
    if(v.mode==0) {
        float2 ab=v.b-v.a;
        float t=saturate(dot(v.position.xy-v.a,ab)/max(dot(ab,ab),.000001));
        float distance=length(v.position.xy-(v.a+t*ab));
        coverage=saturate(v.width+.5-distance);
    } else if(v.mode==2 || v.mode==3) {
        float2 delta=v.position.xy-v.a;
        float determinant=v.b.x*v.c.y-v.b.y*v.c.x;
        float distance;
        if(abs(determinant)<.000001) distance=length(delta);
        else {
            float2 row0=float2(v.c.y,-v.c.x)/determinant;
            float2 row1=float2(-v.b.y,v.b.x)/determinant;
            float2 q=float2(dot(delta,row0),dot(delta,row1));
            float norm=length(q);
            distance=norm<.00001?min(length(v.b),length(v.c)):
                abs(norm-1)/max(length(row0*q.x/norm+row1*q.y/norm),.000001);
            if(v.mode==3) {
                float angle=atan2(q.y,q.x);
                float directed=fmod((angle-v.angles.x)*sign(v.angles.y)+12.566370614,6.283185307);
                if(directed>abs(v.angles.y)) {
                    float2 start=v.b*cos(v.angles.x)+v.c*sin(v.angles.x);
                    float2 end=v.b*cos(v.angles.x+v.angles.y)+v.c*sin(v.angles.x+v.angles.y);
                    distance=min(length(delta-start),length(delta-end));
                }
            }
        }
        coverage=saturate(v.width+.5-distance);
    }
    return float4(v.color.rgb*v.color.a,v.color.a)*coverage;
}
""";
}
