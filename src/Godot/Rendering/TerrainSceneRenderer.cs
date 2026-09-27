using System;
using System.Collections.Generic;
using System.Linq;
using Flui;
using Fluitown.GodotApp.Rendering.Vegetation;
using Fluitown.Render;
using Godot;

namespace Fluitown.GodotApp.Rendering;

/// <summary>
/// The Godot side of the terrain renderer, in the comic look: the terrain is drawn with the comic pipeline — the
/// original's material math as pigment, lit by the central comic light() ramp with the world's Godot sun, ambient light
/// and shadows, hatched by comic_surface and inked by the world ink. It owns the HDR scene viewport, the orthographic
/// camera reproducing the original's oblique projection, the six terrain materials, per-tile mesh nodes (and their
/// shadow casters), the backdrop plane, the world's Godot light (sun with shadows, ambient) and the output pass. The
/// scene Environment tone-maps with Filmic, glow and adjustments; the output pass only encodes sRGB
/// (comic_composite.gdshader). Everything that decides WHAT to draw and with WHICH uniform values lives in the
/// engine-free port (Fluitown.Render); this class only turns those decisions into nodes.
/// </summary>
public sealed partial class TerrainSceneRenderer : Node
{
    /// <summary>The world's sun, the one key light of the terrain (casts its shadows).</summary>
    public DirectionalLight3D Sun { get; private set; } = null!;

    /// <summary>Materials of the terrain programs (captured program numbers in parentheses).</summary>
    public ShaderMaterial Surface { get; private set; } = null!;   // 006
    public ShaderMaterial Water { get; private set; } = null!;     // 007
    public ShaderMaterial MistBack { get; private set; } = null!;  // 009 (back faces, drawn first)
    public ShaderMaterial MistFront { get; private set; } = null!; // 008
    public ShaderMaterial Overlay { get; private set; } = null!;   // 010
    public ShaderMaterial Backdrop { get; private set; } = null!;  // 005
    public ShaderMaterial ShadowDepth { get; private set; } = null!;

    /// <summary>Visual enhancements beyond the original; <see cref="TerrainVisualQuality.Original"/> is browser-exact.</summary>
    public TerrainVisualQuality Quality { get; private set; } = TerrainVisualQuality.Original;
    /// <summary>Internal 3D pixels per output pixel (supersampling) for the current window size.</summary>
    public float RenderScale { get; private set; } = 1;
    private float _pixelShare = 1;

    /// <summary>The materials that receive the scene/light uniforms (everything but depth and composite).</summary>
    public IReadOnlyList<ShaderMaterial> TerrainMaterials => _terrainMaterials;
    private ShaderMaterial[] _terrainMaterials = Array.Empty<ShaderMaterial>();

    // The comic surface's compile-time variant without the relief normal, for profiles without
    // TerrainVisualQuality.SurfaceRelief. Index 0 is the shader itself, 1 the variant.
    private const string SurfaceNoReliefFlag = "FLUITOWN_TERRAIN_NO_RELIEF";
    private readonly Shader?[] _surfaceVariants = new Shader?[2];
    private int _surfaceVariant;
    private bool _surfaceRelief = true;

    // Per-frame parameter names: a string argument would allocate a StringName on every call.
    private static readonly StringName ThreeViewParameter = "u_three_view";
    private readonly List<ThreeLight> _lights = new();

    public SubViewport SceneViewport { get; private set; } = null!;

    /// <summary>
    /// Terrain camera mirrored vertically (basis y negated): the framebuffer is bottom-up like ANGLE's GL-on-D3D11, so
    /// gl_FragCoord, dFdy, the fill rule and the 2× MSAA sample diagonal match the browser exactly. Used by the replay
    /// and the terrain-only world. With the Flui (Godot materials cannot follow a mirror — Godot inverts FRONT_FACING
    /// under it) the terrain renders in Godot's orientation and its shaders restore GL's conventions themselves.
    /// </summary>
    public bool Mirrored
    {
        get => _mirrored;
        set
        {
            _mirrored = value;
            if (Surface == null) return;
            foreach (var material in TerrainMaterials) material.SetShaderParameter("fluitown_mirrored", value);
            _composite.SetShaderParameter("fluitown_mirrored", value);
        }
    }
    private bool _mirrored;

    /// <summary>
    /// Compile space (the original's world pixels, y = height) → Godot world. Identity for the replay; the runtime
    /// scales the terrain to metres so that the physical Flui controller shares the world with it. Every three.js
    /// view-space term is unaffected: <c>u_three_view</c> absorbs the inverse of this transform.
    /// </summary>
    public Transform3D CompileToWorld { get; private set; } = Transform3D.Identity;

    /// <summary>The scene environment (clear colour; the Flui perspective adds sky light and distance fog).</summary>
    public Godot.Environment SceneEnvironment => _environment;
    public SubViewport ShadowViewport { get; private set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    public Camera3D ShadowCamera { get; private set; } = null!;

    private Godot.Environment _environment = null!;
    private MeshInstance3D _backdrop = null!;
    private bool _perspectiveView;
    private Vector2 _perspectiveFog;
    private ColorRect _compositeRect = null!;
    // The output pass on _compositeRect (sRGB encoding and dither).
    private ShaderMaterial _composite = null!;
    // three's `root` (tiles + backdrop): hidden while the terrain layer is not presented.
    private Node3D _terrainRoot = null!;
    private Node3D _casterRoot = null!;
    private readonly Dictionary<string, TileNodes> _tiles = new();
    private int _width;
    private int _height;

    /// <summary>The trees, bushes and meadow grass of the tiles.</summary>
    public TerrainVegetationLayer? Vegetation { get; private set; }

    /// <summary>The nodes of one installed bake tile (the runtime keeps one per TerrainTile record).</summary>
    public sealed class TileNodes
    {
        public readonly List<MeshInstance3D> Visual = new();
        /// <summary>The tile's plants and meadow (TerrainVegetationLayer.Create).</summary>
        public Node3D? Vegetation;
        public MeshInstance3D? Caster;
        public readonly List<MeshInstance3D> CasterParts = new();
        public bool Visible = true;
        public bool CasterActive = true;
        public bool Freed;
        internal PreparedTile? Meshes;
    }

    /// <summary>Builds viewports, cameras, materials and the composite. Call once after adding to the tree.</summary>
    public void Initialize(int width, int height)
    {
        // The comic look's geometry, set before the first bake (bakes run on worker threads; the studio's terrain shape
        // switches change some of these later): smooth closed foliage puffs with crown-shaped shadow proxies; trees,
        // bushes and meadow grass as placements that grow in the vegetation layer; organic terrain (rounded, bent, domed
        // rock with boulders); pools flat at their own level with shores and water plants; uneven open floors carried
        // by the collision; wildflowers and wall plants as real low-poly plants.
        FluitownSoftFoliage.Enabled = true;
        TerrainComicGeometry.ClosedShells = true;
        FluitownVegetation.Enabled = true;
        TerrainOrganicForm.Enabled = true;
        Fluitown.Domain.TerrainVisualContour.OrganicCorners = TerrainOrganicForm.Enabled;
        TerrainGroundRelief.Enabled = true;
        FluitownFlowers.Enabled = true;
        FluitownWallPlants.Enabled = true;
        _width = width;
        _height = height;
        SceneViewport = new SubViewport
        {
            Size = new Vector2I(width, height),
            UseHdr2D = true,
            // postfx.ts worldHdrMsaaSamples: desktop default 2 samples on the RGBA16F world target.
            Msaa3D = Viewport.Msaa.Msaa2X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
        };
        AddChild(SceneViewport);
        ShadowViewport = new SubViewport
        {
            Size = new Vector2I(1024, 1024),
            UseHdr2D = true,
            // The shadows are Godot's own (the world sun); the viewport only holds the casters, its depth pass never runs.
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        SceneViewport.AddChild(ShadowViewport);
        _terrainRoot = new Node3D { Name = "TerrainRoot" };
        SceneViewport.AddChild(_terrainRoot);
        _casterRoot = new Node3D { Name = "ShadowCasters" };
        ShadowViewport.AddChild(_casterRoot);
        if (FluitownVegetation.Enabled)
        {
            Vegetation = new TerrainVegetationLayer { Name = "Vegetation", Renderer = this };
            SceneViewport.AddChild(Vegetation);
        }

        // godot-flui's world (TerrainWorld.BuildTerrainLighting): Filmic, glow only on real highlights (no bloom), a touch of
        // contrast. The HDR 2D scene target keeps the tone-mapped image linear; comic_composite encodes sRGB. The glow
        // itself follows the profile (TerrainVisualQuality.Glow, ApplyOutputQuality).
        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0, 0, 0),
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = FluiLookExposure,
            GlowIntensity = .24f,
            GlowBloom = 0,
            GlowHdrThreshold = 1.6f,
            AdjustmentEnabled = true,
            AdjustmentContrast = 1.025f,
            AdjustmentSaturation = 1f,
        };
        // The comic pipeline admits the camera and gives its world the world ink (ComicRendering).
        Camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Environment = _environment,
            CullMask = 1,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
        };
        SceneViewport.AddChild(Camera);
        ShadowCamera = Exempt(new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                // Cleared to the far plane (packed depth 1.0): unoccluded.
                BackgroundColor = new Color(1, 1, 0),
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
            CullMask = 2,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
        });
        ShadowViewport.AddChild(ShadowCamera);

        Surface = NewMaterial("res://shaders/terrain/terrain_surface_comic.gdshader");
        // Rock facets and weathering of the organic terrain (TerrainOrganicForm shapes the geometry), and the strata, moss
        // and crest greenery of the mountain form.
        Surface.SetShaderParameter("fluitown_organic", TerrainOrganicForm.Enabled);
        Surface.SetShaderParameter("fluitown_mountain", TerrainOrganicForm.Enabled);
        _surfaceVariants[0] = Surface.Shader;
        Surface.SetShaderParameter("fluitown_material_noise", GD.Load<Texture2D>("res://assets/terrain/material_noise.png"));
        Water = NewMaterial("res://shaders/terrain/terrain_water_comic.gdshader");
        MistBack = NewMaterial("res://shaders/terrain/terrain_mist_back_comic.gdshader");
        MistFront = NewMaterial("res://shaders/terrain/terrain_mist_front_comic.gdshader");
        Overlay = NewMaterial("res://shaders/terrain/terrain_overlay_comic.gdshader");
        Backdrop = NewMaterial("res://shaders/terrain/terrain_backdrop_comic.gdshader");
        ShadowDepth = NewMaterial("res://shaders/terrain/shadow_depth.gdshader");
        _terrainMaterials = new[] { Surface, Water, MistBack, MistFront, Overlay, Backdrop };
        // three draws transparent DoubleSide meshes back faces first, then front faces; renderOrder mist (8)
        // precedes overlay (10).
        MistBack.NextPass = MistFront;
        MistBack.RenderPriority = 1;
        MistFront.RenderPriority = 2;
        Overlay.RenderPriority = 3;
        foreach (var material in new[] { Surface, Water })
        {
            material.SetShaderParameter("u_shadow_depth", ShadowViewport.GetTexture());
            material.SetShaderParameter("dfgLUT", ThreeDfgLut.Texture);
        }
        Surface.SetShaderParameter("uMmoratActorShadowMap", ShadowViewport.GetTexture());

        _backdrop = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(1, 1) },
            MaterialOverride = Backdrop,
            Layers = 1,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384,
            Visible = false,
        };
        _terrainRoot.AddChild(_backdrop);

        _composite = NewMaterial("res://shaders/terrain/comic_composite.gdshader");
        _composite.SetShaderParameter("tDiffuse", SceneViewport.GetTexture());
        _compositeRect = new ColorRect { Size = new Vector2(width, height), Material = _composite,
            MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_compositeRect);
        // The world's light, taken from the terrain's own three.js lights every frame (SyncLights): one sun and the
        // ambient light, like godot-flui's own worlds; the comic light() ramp evaluates it.
        Sun = new DirectionalLight3D
        {
            Name = "WorldSun",
            ShadowEnabled = true,
            // Above godot-flui's .075 / 1.0: the organic terrain's rounded rock (outside corners, domed caps) has
            // narrow facets almost parallel to the sun, which shadowed themselves in stripes that flipped whenever the
            // shadow map moved with the camera (a flickering band down a pillar). Measured with FlickerProbe: 1.0
            // flickers, 2.0 less, 3.0 not; the shadows themselves look the same.
            ShadowBias = .1f,
            ShadowNormalBias = 3.0f,
            ShadowBlur = 1.2f,
            LightAngularDistance = .5f,
        };
        SceneViewport.AddChild(Sun);
        _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        Mirrored = _mirrored;
        SetQuality(Quality);
    }

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        SceneViewport.Size = new Vector2I(width, height);
        _compositeRect.Size = new Vector2(width, height);
        ApplyRenderScale();
    }

    // ── visual enhancements (TerrainVisualQuality) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Switches the enhancement profile (callers re-bind the uniform binder).
    /// </summary>
    public void SetQuality(TerrainVisualQuality quality)
    {
        Quality = quality;
        if (Surface == null) return;
        SceneViewport.Msaa3D = quality.Msaa;
        Surface.SetShaderParameter("fluitown_fast_noise", quality.FastMaterialNoise);
        foreach (var material in new[] { Surface, Water })
            material.SetShaderParameter("fluitown_shadow_taps", quality.ShadowTaps);
        ApplyOutputQuality();
        if (_surfaceVariants[0] != null && quality.SurfaceRelief != _surfaceRelief)
        {
            _surfaceRelief = quality.SurfaceRelief;
            SyncSurfaceVariant();
        }
        Vegetation?.SetReach(quality.MeadowReach);
        Vegetation?.SetDensity(quality.MeadowDensity);
        Vegetation?.SetClumps(quality.MeadowClumps);
        Vegetation?.SetLeafShells(quality.LeafShells);
        ApplyRenderScale();
        ApplyEnhancements();
        ApplySunShadowMode();
    }

    /// <summary>
    /// The perspective view splits the sun's shadow along its view like godot-flui's world (unless the profile keeps one
    /// map, <see cref="TerrainVisualQuality.SunShadowSplits"/>); the bird's-eye camera is orthographic, so one split spans
    /// its whole depth (SyncLights keeps the range at the camera's far plane).
    /// </summary>
    private void ApplySunShadowMode()
    {
        if (Sun == null) return;
        Sun.DirectionalShadowMode = _perspectiveView && Quality.SunShadowSplits
            ? DirectionalLight3D.ShadowMode.Parallel2Splits
            : DirectionalLight3D.ShadowMode.Orthogonal;
        // Contact-hardening soft shadows (PCSS) come from the sun's angular size; without it Godot filters plain PCF with
        // the same blur.
        Sun.LightAngularDistance = Quality.SunSoftShadows ? .5f : 0f;
    }

    /// <summary>
    /// The view the enhancements are tuned for: the bird's-eye view gets silhouette ink, the Flui perspective (with
    /// godot-flui's own world ink and fog) gets the water's sky reflection and occlusion that fades into the fog.
    /// </summary>
    public void SetViewMode(bool fluiPerspective, float fogBegin, float fogEnd, Color skyZenith, Color skyHorizon)
    {
        _perspectiveView = fluiPerspective;
        ApplyAntialiasing();
        _perspectiveFog = new Vector2(fogBegin, fogEnd);
        Water.SetShaderParameter("fluitown_sky_zenith", new Vector3(skyZenith.R, skyZenith.G, skyZenith.B));
        Water.SetShaderParameter("fluitown_sky_horizon", new Vector3(skyHorizon.R, skyHorizon.G, skyHorizon.B));
        Vegetation?.SetPerspective(fluiPerspective);
        ApplyEnhancements();
        ApplySunShadowMode();
        _inkApplied = false; _inkSearchDelay = 0;
        ApplyWorldInk();
    }

    // ── the world's lights and ink ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The world's Godot light from the terrain's own lights of this frame: the shadow-casting sun (direction, colour,
    /// intensity) and the hemisphere sky plus ambient as ambient light (a sky environment of the Flui perspective
    /// provides its own). The original's weak fill light has no Godot counterpart: the comic ramp bands every
    /// directional light, and a second one draws a second set of bands across the Flui. Intensities are three's as they
    /// are — the light level godot-flui's Flui pigments are made for; the terrain's three.js pigments enter the comic
    /// ramp converted instead (COMIC_PIGMENT).
    /// </summary>
    /// <param name="focus">The world point framed at the centre of the view (the terrain focus).</param>
    public void SyncLights(TerrainPresentationState presentation, Vector3 focus)
    {
        // Godot's directional shadows cover a distance from the current camera. The Flui camera keeps godot-flui's tight
        // range; the bird's-eye camera sits far away, so its range reaches from the eye past the focus to the top of the
        // view (the ground there lies 1.12 × half the view height deeper, see ThreeCameraMath).
        var camera = SceneViewport.GetCamera3D();
        if (camera != null)
            Sun.DirectionalShadowMaxDistance = camera == Camera ? camera.GlobalPosition.DistanceTo(focus) + Camera.Size * .56f + 20 : 60 * Quality.SunShadowRange;
        ThreeDirectionalLight? sun = null;
        ThreeHemisphereLight? hemi = null;
        ThreeAmbientLight? ambient = null;
        presentation.sceneLights(_lights);
        foreach (var light in _lights)
        {
            if (light is ThreeDirectionalLight directional && directional.castShadow && sun == null) sun = directional;
            else if (light is ThreeHemisphereLight h && hemi == null) hemi = h;
            else if (light is ThreeAmbientLight a && ambient == null) ambient = a;
        }
        Aim(Sun, sun);
        // godot-flui's light at noon, carried through the day by the three.js lights' own course: strength
        // relative to three's clear noon, colour through the per-channel filter that turns three's noon colour into
        // godot-flui's (dusk stays orange, night blue). Night dims both further: three's night rig is generous because its
        // ACES curve crushes the darks, which Filmic lifts (the surface's uMmoratNight is the presentation's night, 0–1).
        float nightDim = 1;
        if (Surface.GetShaderParameter(NightParameter) is { VariantType: Variant.Type.Float } night)
            nightDim = 1 - FluiNightDim * Math.Clamp(night.AsSingle(), 0, 1);
        if (sun != null)
        {
            Sun.LightColor = Filtered(Sun.LightColor, SunFilter);
            Sun.LightEnergy = FluiSunEnergy * Math.Clamp((float)(sun.intensity / ThreeNoonSun), 0, 1.25f) * nightDim;
        }
        if (AmbientFromLights || _environment.BackgroundMode != Godot.Environment.BGMode.Sky)
        {
            var sky = hemi != null ? new Color((float)hemi.color.r, (float)hemi.color.g, (float)hemi.color.b).LinearToSrgb() : new Color("b6cbd0");
            _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            _environment.AmbientLightColor = sky;
            _environment.AmbientLightEnergy = (float)Math.Clamp((hemi?.intensity ?? 0.6) + (ambient?.intensity ?? 0), 0.2, 3);
            // Bird's-eye view: godot-flui's sky light (the perspective view takes its ambient from its sky, energy .6),
            // carried through the day like the key.
            _environment.AmbientLightColor = Filtered(sky, AmbientFilter);
            _environment.AmbientLightEnergy = FluiAmbientEnergy * Math.Clamp(_environment.AmbientLightEnergy / ThreeNoonAmbient, .2f, 1) * nightDim;
        }
    }

    /// <summary>STUDIO: keep deriving the ambient light from the terrain's lights while a sky is the background (the
    /// studio's 3D view has a sky but no sky-lit character pipeline).</summary>
    public bool AmbientFromLights { get; set; }

    // godot-flui's world light and exposure (TerrainWorld.BuildTerrainLighting).
    private const float FluiSunEnergy = 1.35f, FluiAmbientEnergy = .6f, FluiLookExposure = 1.1f;
    /// <summary>Share of the light taken away in the dead of night (tuned on captures at day 0.95).</summary>
    private const float FluiNightDim = .35f;
    private static readonly StringName NightParameter = "uMmoratNight";
    // The three.js lights at noon in clear weather (WorldLighting, measured in the hub world 2026-09-19): key 2.05, colour
    // (1, .784, .509) linear (≈ #ffe5bd, next to godot-flui's #ffe9c8); hemisphere 0.79 + ambient 0.11, colour
    // (.888, .863, .823) linear (near-white, where godot-flui's sky light is a cool #b6cbd0).
    private const double ThreeNoonSun = 2.05;
    private const float ThreeNoonAmbient = .904f;
    private static readonly Color SunFilter = Filter(new Color(0xffe9c8ff), new Color(1f, .784f, .509f).LinearToSrgb());
    private static readonly Color AmbientFilter = Filter(new Color(0xb6cbd0ff), new Color(.888f, .863f, .823f).LinearToSrgb());

    /// <summary>The per-channel factor that turns <paramref name="three"/> into <paramref name="flui"/> (sRGB colours).</summary>
    private static Color Filter(Color flui, Color three) => new(flui.R / three.R, flui.G / three.G, flui.B / three.B);

    private static Color Filtered(Color colour, Color filter) =>
        new(Math.Min(colour.R * filter.R, 1), Math.Min(colour.G * filter.G, 1), Math.Min(colour.B * filter.B, 1));

    private static void Aim(DirectionalLight3D target, ThreeDirectionalLight? source)
    {
        target.Visible = source != null && source.intensity > 0;
        if (source == null) return;
        var from = new Vector3((float)source.position.x, (float)source.position.y, (float)source.position.z);
        var to = source.target is { } aim ? new Vector3((float)aim.position.x, (float)aim.position.y, (float)aim.position.z) : Vector3.Zero;
        var direction = (to - from).Normalized();
        if (direction.IsFinite() && direction.LengthSquared() > .5f)
        {
            var up = MathF.Abs(direction.Y) > .99f ? Vector3.Forward : Vector3.Up;
            target.GlobalTransform = new Transform3D(Basis.LookingAt(direction, up), Vector3.Zero);
        }
        // three works in linear sRGB; Godot light colours are sRGB-encoded.
        target.LightColor = new Color((float)source.color.r, (float)source.color.g, (float)source.color.b).LinearToSrgb();
        target.LightEnergy = (float)Math.Clamp(source.intensity, 0, 8);
    }

    private bool _inkApplied, _multisampleInk;

    /// <summary>Configure the single shared outline; WorldOutline retains ownership of its material.
    /// Projection adaptation is in the shader and applies equally to every surface.</summary>
    private void ApplyWorldInk()
    {
        if (_inkApplied) return;
        // The search allocates (Godot array, list): while no outline exists yet it runs every 15th frame, not every frame.
        if (_inkSearchDelay > 0) { _inkSearchDelay--; return; }
        var outlines = SceneViewport.FindChildren("*", "MeshInstance3D", true, false).OfType<WorldOutline>().ToList(); // alloc-ok: until the outline exists
        foreach (var outline in outlines)
        {
            if (outline.MaterialOverride == null) { _inkSearchDelay = 15; return; } // not _Ready yet
            outline.Visible = true;
            _multisampleInk = outline.EnableMultisampleCoverage();
            outline.SetPixelScale(RenderScale);
            outline.SetDistanceFog(_perspectiveView ? _perspectiveFog : Vector2.Zero);
        }
        _inkApplied = outlines.Count > 0;
        if (_inkApplied) ApplyAntialiasing();
        if (!_inkApplied) _inkSearchDelay = 15;
    }
    private int _inkSearchDelay;

    /// <summary>Supersampling through Godot's 3D scaling (the viewport, its 2D content and input keep their size).</summary>
    private void ApplyRenderScale()
    {
        RenderScale = Quality.EffectiveRenderScale(_width, _height, _pixelShare);
        // Supersampling downsamples bilinearly (a box filter at 2×); a window beyond the pixel budget renders smaller and
        // is upscaled with FSR.
        SceneViewport.Scaling3DMode = RenderScale < 1 ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        SceneViewport.Scaling3DScale = RenderScale;
        ApplyAntialiasing();
        // FSR's default sharpening and automatic negative mip bias exaggerate
        // tiny ink, normal-map details and shadow noise. Filter textures for the
        // buffer actually sampled, and keep reconstruction sharpening restrained.
        SceneViewport.FsrSharpness = 1.5f;
        SceneViewport.TextureMipmapBias = RenderScale < 1 ? -MathF.Log2(RenderScale) : 0;
        foreach (var material in new[] { Surface, Water })
            material.SetShaderParameter("fluitown_lod_scale", RenderScale);
        _inkApplied = false; _inkSearchDelay = 0;
        ApplyWorldInk();
    }

    private void ApplyAntialiasing()
    {
        // Temporal accumulation ghosts the animated Flui mesh and its depth
        // contour. All gameplay views use current-frame samples exclusively.
        SceneViewport.UseTaa = false;
        // Coverage AA retains sharp colour and tiny contours. A second FXAA pass
        // erased the last visible ink pixels on distant four-pixel characters.
        // STUDIO: the Compatibility renderer has no screen-space AA pass.
        bool screenSpace = RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility";
        SceneViewport.ScreenSpaceAA = !screenSpace || _multisampleInk && SceneViewport.Msaa3D != Viewport.Msaa.Disabled
            ? Viewport.ScreenSpaceAAEnum.Disabled : Quality.ScreenSpaceAA;
    }

    private void ApplyEnhancements()
    {
        if (Water == null) return;
        Water.SetShaderParameter("fluitown_view_reflection", _perspectiveView ? Quality.WaterViewReflection : 0f);
        ApplyComicQuality();
    }

    /// <summary>
    /// The world sun's shadow atlas and soft-shadow filter, and Godot's SSAO on the comic surfaces (as in godot-flui's
    /// world). Shadow atlas and filter are engine-wide settings.
    /// </summary>
    private void ApplyComicQuality()
    {
        var q = Quality;
        RenderingServer.DirectionalShadowAtlasSetSize(q.SunShadowAtlas, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(q.SunShadowFilter);
        _environment.SsaoEnabled = q.AmbientOcclusion > 0 && q.Ssao;
        if (!_environment.SsaoEnabled) return;
        // godot-flui's own world values (MountainRun): contact shade in creases and at wall feet. Stronger or wider
        // occlusion reaches across a whole ~1 m Flui and lays a second, soft shading over its comic bands.
        _environment.SsaoRadius = .65f;
        _environment.SsaoIntensity = .8f;
        _environment.SsaoPower = 1.5f;
        _environment.SsaoDetail = 0.5f;
        _environment.SsaoSharpness = 0.98f;
        _environment.SsaoLightAffect = .12f;
        // The bird's-eye camera is orthographic and far away, so the occlusion must not fade with its distance; the
        // Flui perspective lets it fade before the fog.
        float fadeFrom = _perspectiveView ? _perspectiveFog.X : 1e5f;
        float fadeTo = _perspectiveView ? _perspectiveFog.Y : 2e5f;
        RenderingServer.EnvironmentSetSsaoQuality(RenderingServer.EnvironmentSsaoQuality.High, q.AmbientOcclusionHalfSize, 0.5f, 2, fadeFrom, fadeTo);
    }

    /// <summary>The output pass's dither and the Environment's glow for the current profile.</summary>
    private void ApplyOutputQuality()
    {
        _composite.SetShaderParameter("fluitown_dither", Quality.Dither);
        _environment.GlowEnabled = Quality.Glow;
    }

    /// <summary>Per frame: the world ink is styled once its outline exists.</summary>
    public void SyncEnhancements()
    {
        ApplyWorldInk();
    }

    /// <summary>Draws the surface variant of the profile (without the relief normal where the profile has none).</summary>
    private void SyncSurfaceVariant()
    {
        if (_surfaceVariants[0] == null) return;
        int variant = _surfaceRelief ? 0 : 1;
        if (variant == _surfaceVariant) return;
        _surfaceVariant = variant;
        Surface.Shader = _surfaceVariants[variant] ??= new Shader { Code = $"#define {SurfaceNoReliefFlag}\n" + _surfaceVariants[0]!.Code };
    }

    private static ShaderMaterial NewMaterial(string path) => new() { Shader = GD.Load<Shader>(path) };

    /// <summary>
    /// The comic pipeline admits every 3D node (ComicRendering); the depth-only helpers keep their own shaders.
    /// </summary>
    private static T Exempt<T>(T node) where T : Node
    {
        node.SetMeta(ComicRendering.ExemptMeta, true);
        return node;
    }

    // ── camera, backdrop ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The original's oblique orthographic projection and yawed scene root (three view space = yawed world).
    /// Also publishes the yaw to every terrain material (<c>u_three_view</c>).
    /// </summary>
    public void SetCamera(double[] projection, double[] sceneMatrix)
    {
        var cam = ThreeCameraMath.TerrainCamera(projection, sceneMatrix, _width, _height);
        float scale = CompileScale;
        Camera.Size = cam.Size * scale;
        Camera.Near = cam.Near * scale;
        Camera.Far = cam.Far * scale;
        var basis = cam.Transform.Basis;
        if (_mirrored) basis.Y = -basis.Y;
        Camera.GlobalTransform = new Transform3D(basis, CompileToWorld * cam.Transform.Origin);
        var view = ThreeCameraMath.ToProjection(sceneMatrix) * WorldToCompileProjection();
        if (view == _threeView) return;
        _threeView = view;
        foreach (var material in _terrainMaterials) material.SetShaderParameter(ThreeViewParameter, view);
    }

    private Projection _threeView;

    /// <summary>Uniform scale of <see cref="CompileToWorld"/>.</summary>
    public float CompileScale => CompileToWorld.Basis.X.Length();

    /// <summary>Places the terrain (tiles, backdrop, shadow casters) in the Godot world.</summary>
    public void SetCompileToWorld(float scale, Vector3 offset)
    {
        CompileToWorld = new Transform3D(Basis.Identity.Scaled(Vector3.One * scale), offset);
        _terrainRoot.Transform = CompileToWorld;
        _casterRoot.Transform = CompileToWorld;
        ApplyEnhancements();
    }

    private Projection WorldToCompileProjection()
    {
        var inverse = CompileToWorld.AffineInverse();
        return new Projection(inverse);
    }

    /// <summary>The backdrop plane: <paramref name="matrixWorld"/> is the original mesh's world matrix.</summary>
    public void SetBackdrop(double[] matrixWorld, double[] sceneMatrix, bool visible = true)
    {
        _backdrop.Transform = ThreeCameraMath.ToTransform(sceneMatrix).AffineInverse() * ThreeCameraMath.ToTransform(matrixWorld);
        _backdrop.Visible = visible;
    }

    /// <summary>`root.visible`: tiles and backdrop are drawn only while the terrain layer presents a frame.</summary>
    public void SetTerrainVisible(bool visible)
    {
        if (_terrainRoot.Visible != visible) _terrainRoot.Visible = visible;
        if (Vegetation != null && Vegetation.Visible != visible) Vegetation.Visible = visible;
    }

    /// <summary>Renderer clear colour (linear), visible only where neither terrain nor backdrop draws.</summary>
    public void SetClearColor(Vector3 linear) => _environment.BackgroundColor = new Color(linear.X, linear.Y, linear.Z);

    // ── tiles ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The GPU meshes of one bake tile, built off the main thread by <see cref="PrepareTile"/>.</summary>
    public sealed class PreparedTile : IDisposable
    {
        public ArrayMesh? Surface, Water, Mist, Overlay, Caster;
        public ArrayMesh[] SurfaceParts = Array.Empty<ArrayMesh>(), CasterParts = Array.Empty<ArrayMesh>();
        /// <summary>The tile's plants and meadow, grown from the payload's vegetation lane.</summary>
        public TerrainVegetation.Prepared? Vegetation;

        public void Dispose()
        {
            Surface?.Dispose(); Water?.Dispose(); Mist?.Dispose(); Overlay?.Dispose(); Caster?.Dispose();
            Surface = Water = Mist = Overlay = Caster = null;
            foreach (var mesh in SurfaceParts) mesh.Dispose();
            foreach (var mesh in CasterParts) mesh.Dispose();
            SurfaceParts = CasterParts = Array.Empty<ArrayMesh>();
            Vegetation?.Dispose();
            Vegetation = null;
        }
    }

    /// <summary>
    /// Builds the lane meshes of a bake payload. Thread-safe (no scene-tree access): the runtime calls it on the
    /// bake worker so the main thread only instantiates nodes (building a spawn tile costs ~25 ms).
    /// </summary>
    public static PreparedTile PrepareTile(TerrainGeometryPayload payload, Transform3D? compileToWorld = null)
    {
        var prepared = new PreparedTile();
        if (payload.vegetation != null && compileToWorld is { } toWorld)
            prepared.Vegetation = TerrainVegetation.Prepare(payload.vegetation, toWorld);
        bool spatial = FluitownVegetation.Enabled;
        if (payload.surface is { } s)
        {
            if (spatial) prepared.SurfaceParts = TerrainRenderMesh.Surface(s.position, s.normal, s.color, s.surface, s.emissive, s.ground, s.index);
            else prepared.Surface = TerrainLaneMeshes.Surface(s.position, s.normal, s.color, s.surface, s.emissive, s.ground, s.index);
        }
        if (payload.water is { } w)
            prepared.Water = TerrainLaneMeshes.Water(w.position, w.normal, w.color, w.water, w.fold, w.reflection, w.index);
        if (payload.mist is { } m)
            prepared.Mist = TerrainLaneMeshes.Mist(m.position, m.color, m.mist, m.index);
        if (payload.overlay is { } o)
            prepared.Overlay = TerrainLaneMeshes.Overlay(o.position, o.color, o.index);
        if (payload.actorWall is { } a)
        {
            if (spatial) prepared.CasterParts = TerrainRenderMesh.Casters(a.position, a.index);
            else prepared.Caster = TerrainLaneMeshes.PositionOnly(a.position, a.index);
        }
        return prepared;
    }

    /// <summary>
    /// Instantiates the nodes of a prepared tile without a key (main thread). Two records of the same tile position
    /// can coexist (a retiring tile and its replacement), so the runtime addresses tiles by handle.
    /// </summary>
    public TileNodes CreateTile(PreparedTile prepared, bool visible, bool casterActive)
    {
        var nodes = new TileNodes { Visible = visible, CasterActive = casterActive, Meshes = prepared };
        void Visual(ArrayMesh? mesh, Material material)
        {
            if (mesh == null) return;
            var instance = new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = material,
                Layers = 1,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = visible,
            };
            _terrainRoot.AddChild(instance);
            nodes.Visual.Add(instance);
        }
        Visual(prepared.Surface, Surface);
        foreach (var part in prepared.SurfaceParts) Visual(part, Surface);
        Visual(prepared.Water, Water);
        Visual(prepared.Mist, MistBack);
        Visual(prepared.Overlay, Overlay);
        nodes.Vegetation = Vegetation?.Create(prepared.Vegetation, visible);
        if (prepared.Caster != null)
        {
            // The original's shadow casters (the actor-wall lane: walls, trees), cast into the world sun's shadow map,
            // shadow-only on the visible layer (Godot takes a directional shadow's casters from the layers the camera sees).
            nodes.Caster = Exempt(new MeshInstance3D
            {
                Mesh = prepared.Caster,
                MaterialOverride = ShadowDepth,
                Layers = 1,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
                Visible = casterActive,
            });
            _casterRoot.AddChild(nodes.Caster);
        }
        foreach (var part in prepared.CasterParts)
        {
            var caster = Exempt(new MeshInstance3D { Mesh = part, MaterialOverride = ShadowDepth,
                Layers = 1,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
                Visible = casterActive });
            nodes.CasterParts.Add(caster);
            _casterRoot.AddChild(caster);
        }
        return nodes;
    }

    public static void SetTileVisible(TileNodes nodes, bool visible)
    {
        if (nodes.Freed || nodes.Visible == visible) return;
        nodes.Visible = visible;
        foreach (var instance in nodes.Visual) instance.Visible = visible;
        if (nodes.Vegetation != null) nodes.Vegetation.Visible = visible;
    }

    public static void SetTileShadowCaster(TileNodes nodes, bool active)
    {
        if (nodes.Freed || nodes.CasterActive == active) return;
        nodes.CasterActive = active;
        if (nodes.Caster != null) nodes.Caster.Visible = active;
        foreach (var caster in nodes.CasterParts) caster.Visible = active;
    }

    public void RemoveTile(string key)
    {
        if (_tiles.Remove(key, out var nodes)) FreeTile(nodes);
    }

    /// <summary>Frees the nodes of a tile (the meshes are released with their last reference).</summary>
    public static void FreeTile(TileNodes nodes)
    {
        if (nodes.Freed) return;
        nodes.Freed = true;
        foreach (var instance in nodes.Visual) instance.QueueFree();
        nodes.Visual.Clear();
        nodes.Vegetation?.QueueFree();
        nodes.Vegetation = null;
        nodes.Caster?.QueueFree();
        nodes.Caster = null;
        foreach (var caster in nodes.CasterParts) caster.QueueFree();
        nodes.CasterParts.Clear();
        // The task result also references these wrappers. Release their native ownership now, instead of waiting
        // for a managed finalizer/GC to retire several streamed tiles' worth of GPU buffers at once.
        nodes.Meshes?.Dispose();
        nodes.Meshes = null;
    }

    public void RemoveAllTiles()
    {
        foreach (var key in new List<string>(_tiles.Keys)) RemoveTile(key);
    }
}
