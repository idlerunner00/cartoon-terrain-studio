using Godot;
namespace Flui;

/// <summary>One ink pass per world, shared by ground, flight and audit cameras.</summary>
public partial class WorldOutline : MeshInstance3D
{
    // Voxel water has local, changing levels. World Y=0 must not erase ink
    // on submerged characters, terrain or water in lower basins.
    public bool WaterCutoff = false;
    private ArrayMesh _triangle = null!;
    private ShaderMaterial _ink = null!;
    private Vector2 _distanceFog;
    private float _pixelScale = 1;
    private WorldInkCoverage? _coverage;
    private bool _coverageReady;
    private int _coverageVersion;
    public bool MultisampleCoverageEnabled = true;
    private static readonly StringName CoverageParameter = "ink_coverage", MultisampleParameter = "ink_msaa";
    private static readonly StringName FogParameter = "distance_fog", PixelScaleParameter = "px_scale";
    public void SetDistanceFog(Vector2 range){_distanceFog=range;_ink?.SetShaderParameter(FogParameter,range);}
    public void SetPixelScale(float scale){_pixelScale=scale;_ink?.SetShaderParameter(PixelScaleParameter,scale);}
    // STUDIO: the ink line width is a look parameter of the terrain studio.
    private float _width = 2.15f;
    private static readonly StringName WidthParameter = "width";
    public void SetWidth(float width){_width=width;_ink?.SetShaderParameter(WidthParameter,width);}
    public override void _Ready()
    {
        Name = "WorldInk";
        TopLevel = true;
        CastShadow = ShadowCastingSetting.Off;
        IgnoreOcclusionCulling = true;
        ExtraCullMargin = 16;
        // A single triangle covers the screen without shading the diagonal twice.
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = new[] {
            new Vector3(-1, -1, 0), new Vector3(-1, 3, 0), new Vector3(3, -1, 0) };
        _triangle = new ArrayMesh();
        _triangle.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        // Only Forward+ provides the normal-roughness buffer; the other renderers ink from depth alone.
        const string outline = "res://assets/shaders/world_outline.gdshader";
        var shader = RenderingServer.GetCurrentRenderingMethod() == "forward_plus"
            ? ComicShaders.Load(outline) : ComicShaders.Variant(outline, "INK_DEPTH_ONLY");
        _ink = new ShaderMaterial { Shader = shader, RenderPriority = 127 };
        _ink.SetShaderParameter("water_cutoff", WaterCutoff);
        _ink.SetShaderParameter(FogParameter,_distanceFog);
        _ink.SetShaderParameter(PixelScaleParameter,_pixelScale);
        _ink.SetShaderParameter(WidthParameter,_width);
        Mesh = _triangle; MaterialOverride = _ink;
        // Fullscreen geometry still has CPU-side bounds. Follow the final camera
        // after all camera controllers, including restaurant/review overrides.
        ProcessPriority = int.MaxValue;
        ProcessMode = ProcessModeEnum.Always;
    }
    /// <summary>The host opts in once for its world viewport. Standalone or unsupported renderers retain the shared fallback.</summary>
    public bool EnableMultisampleCoverage()
    {
        if (_coverage != null) return _coverage.Enabled;
        if (RenderingServer.GetCurrentRenderingMethod() != "forward_plus" || RenderingServer.GetRenderingDevice() == null) return false;
        _coverage = new WorldInkCoverage();
        if (!_coverage.Enabled) return false;
        var compositor = new Compositor();
        compositor.CompositorEffects = new Godot.Collections.Array<CompositorEffect> { _coverage };
        AddChild(new WorldEnvironment { Name = "InkSampleCoverage", Compositor = compositor });
        return true;
    }
    public override void _Process(double delta)
    {
        // Keep CPU frustum bounds at the active camera, even far from world zero.
        var viewport = GetViewport();
        bool ready = MultisampleCoverageEnabled && _coverage?.Ready == true && viewport.Msaa3D != Viewport.Msaa.Disabled;
        int version = _coverage?.Version ?? 0;
        if (ready != _coverageReady || ready && version != _coverageVersion)
        {
            if (ready) _ink.SetShaderParameter(CoverageParameter, _coverage!.Coverage);
            _ink.SetShaderParameter(MultisampleParameter, ready);
            _coverageReady = ready;
            _coverageVersion = version;
        }
        var camera = viewport.GetCamera3D();
        if (camera != null) GlobalPosition = camera.GlobalPosition;
    }
    public override void _ExitTree()
    {
        _coverage?.Shutdown();
        Mesh = null; MaterialOverride = null;
        _triangle?.Dispose(); _ink?.Dispose();
    }
}
