using System;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
namespace Flui;

/// <summary>Evaluate ink on current-frame MSAA depth samples before their coverage is lost by resolve.</summary>
public partial class WorldInkCoverage : CompositorEffect
{
    public Texture2Drd Coverage { get; } = new();
    public bool Ready => Volatile.Read(ref _ready);
    public int Version => Volatile.Read(ref _version);
    private bool _ready;
    private int _version;
    private int _stopped;
    private readonly RenderingDevice _device;
    private readonly RDShaderSpirV _spirv;
    private Rid _shader, _pipeline, _sampler, _mask, _set, _depth, _normal;
    private Vector2I _size;
    private static readonly StringName Forward = "forward_clustered", Normals = "normal_roughness_msaa";

    public WorldInkCoverage()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PreTransparent;
        NeedsNormalRoughness = true;
        _device = RenderingServer.GetRenderingDevice();
        using var file = GD.Load<RDShaderFile>("res://assets/shaders/world_ink_msaa.glsl");
        _spirv = file.GetSpirV();
        if (_spirv.CompileErrorCompute.Length != 0) { GD.PushError(_spirv.CompileErrorCompute); Enabled = false; }
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        using var sceneBuffers = renderData.GetRenderSceneBuffers();
        using var sceneData = renderData.GetRenderSceneData();
        if (sceneBuffers is not RenderSceneBuffersRD buffers || _device == null || sceneData == null) return;
        var size = buffers.GetInternalSize();
        if (size.X == 0 || size.Y == 0 || buffers.GetMsaa3D() == RenderingServer.ViewportMsaa.Disabled ||
            buffers.GetViewCount() != 1 || !buffers.HasTexture(Forward, Normals)) { Volatile.Write(ref _ready, false); return; }
        if (!_shader.IsValid)
        {
            _shader = _device.ShaderCreateFromSpirV(_spirv);
            if (!_shader.IsValid) return;
            _pipeline = _device.ComputePipelineCreate(_shader);
            using var state = new RDSamplerState();
            _sampler = _device.SamplerCreate(state);
        }
        var depth = buffers.GetDepthTexture(true);
        var normal = buffers.GetTexture(Forward, Normals);
        if (_size != size)
        {
            var oldMask = _mask;
            using var format = new RDTextureFormat { Width = (uint)size.X, Height = (uint)size.Y,
                Format = RenderingDevice.DataFormat.R8Unorm, TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit };
            using var view = new RDTextureView();
            _mask = _device.TextureCreate(format, view);
            // Replace the backing RD texture without clearing the stable
            // Texture2D RID already bound by the spatial material.
            Coverage.TextureRdRid = _mask;
            if (oldMask.IsValid) _device.FreeRid(oldMask);
            _size = size;
            Interlocked.Increment(ref _version);
        }
        if (!_set.IsValid || !_device.UniformSetIsValid(_set) || _depth != depth || _normal != normal)
        {
            if (_set.IsValid && _device.UniformSetIsValid(_set)) _device.FreeRid(_set);
            using var d = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
            using var n = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 };
            using var m = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 };
            d.AddId(_sampler); d.AddId(depth); n.AddId(_sampler); n.AddId(normal); m.AddId(_mask);
            var uniforms = new Godot.Collections.Array<RDUniform> { d, n, m };
            _set = _device.UniformSetCreate(uniforms, _shader, 0);
            _depth = depth; _normal = normal;
        }
        var projection = sceneData.GetCamProjection();
        // Only the depth block is needed. Projection.Inverse() treats the tiny
        // full-matrix determinant of a wide orthographic camera as singular,
        // returning ZERO at far zoom. Invert this well-conditioned 2x2 block
        // directly in double precision instead (also covers perspective).
        double zClip = projection.Z.Z, wClip = projection.W.Z, zDiv = projection.Z.W, wDiv = projection.W.W;
        double determinant = zClip * wDiv - wClip * zDiv;
        float izz = (float)(wDiv / determinant), iwz = (float)(-wClip / determinant);
        float izw = (float)(-zDiv / determinant), iww = (float)(zClip / determinant);
        float scale = size.Y / (float)buffers.GetTargetSize().Y;
        Span<float> parameters = stackalloc float[12] {
            2 * scale / (Math.Abs(projection.Y.Y) * size.Y), Math.Abs(izz) * .00000025f, 0, projection.W.W > .5f ? 1 : 0,
            size.X, size.Y, scale, 1 << (int)buffers.GetTextureSamples(),
            izz, iwz, izw, iww };
        long list = _device.ComputeListBegin();
        _device.ComputeListBindComputePipeline(list, _pipeline);
        _device.ComputeListBindUniformSet(list, _set, 0);
        _device.ComputeListSetPushConstant(list, MemoryMarshal.AsBytes(parameters), 48);
        _device.ComputeListDispatch(list, (uint)(size.X + 7) / 8, (uint)(size.Y + 7) / 8, 1);
        _device.ComputeListEnd();
        Volatile.Write(ref _ready, true);
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0 || _device == null) return;
        Enabled = false;
        // RIDs are released on the render thread after outstanding frame work.
        RenderingServer.CallOnRenderThread(Callable.From(() => {
            Coverage.TextureRdRid = default;
            if (_shader.IsValid) _device.FreeRid(_shader);
            if (_sampler.IsValid) _device.FreeRid(_sampler);
            if (_mask.IsValid) _device.FreeRid(_mask);
            Coverage.Dispose(); _spirv.Dispose();
        }));
    }
    public override void _Notification(int what) { if (what == NotificationPredelete) Shutdown(); }
}
