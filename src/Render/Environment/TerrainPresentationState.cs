// Port of packages/client/src/render/environment/threeTerrain.ts (class ThreeTerrainLayer) — the PRESENTATION half:
// every field and method that decides a material uniform, a light, the sun shadow camera, the backdrop transform or the
// scene (world-yaw) matrix. Keep in lockstep with the original. The tile/bake/worker half of ThreeTerrainLayer
// (residency, planning, uploads, eviction) is the integrator's (see ITerrainPresentationHost).
//
// PORT NOTES (binding for all TerrainPresentationState.*.cs parts)
// * Literal port: TS member names, statement order and the design comments are kept. Lines of the original that belong
//   to tile management are replaced by calls into ITerrainPresentationHost at the exact same position.
// * three.js objects → ThreeScene.cs / ThreeWebGL.cs stand-ins (Object3D graph, lights, cameras, materials, a
//   value-only WebGLRenderer). The uniform objects keep three's identity sharing: one ThreeUniform per TS `{ value }`.
// * config.ts (`RENDER_QUALITY`, `PERF`) is not ported yet → the device policy is injected as TerrainRenderQuality
//   (defaults = the desktop policy, which is what the reference captures ran with).
// * `WebGlCapability` → its `executionClass` string. STUDIO: `PostFx` is not ported (the comic look's output pass
//   encodes the image by itself), so the layer runs as the original does with `postFx` undefined.
// * Tile meshes are the host's; the value emulation draws one "lane template" mesh per shared lane material
//   (TerrainLaneTemplate, the material/flag half of `groupsFromTransferPayload`). Uniform values are per material,
//   so one stand-in per material reproduces three's per-draw refresh exactly.
// * Thread safety: ThreeTerrainLayer is main-thread state; nothing here may be touched from a bake worker.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using static Fluitown.Render.ThreeTerrain;
using static Fluitown.Render.TerrainLightRig;
using static Fluitown.Render.VisualQuality;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// The device render policy ThreeTerrainLayer reads from config.ts `RENDER_QUALITY` / `PERF`. Defaults are the desktop
/// policy (`DESKTOP_SHADOW_QUALITY`, `DESKTOP_RASTER_QUALITY`, `PREMIUM_DETAIL_QUALITY`), the one the reference
/// captures ran with.
/// </summary>
public sealed class TerrainRenderQuality
{
    public bool isMobile = false;
    public bool terrainShadows = true;
    public double terrainShadowMapSize = 1_024;
    public bool actorSunShadows = true;
    /// <summary>`ActorShadowTaps` (1 | 4).</summary>
    public int actorGroundShadowTaps = 4;
    public double terrainVegetationBladeCap = 12;
    public double terrainCliffDressingDensity = 0.65;
    public double impactLightCapacity = 3;
    /// <summary>`PERF.maxResolution` (= `RENDER_QUALITY.maxResolution`).</summary>
    public double maxResolution = 2;
    /// <summary>`window.devicePixelRatio` — only read when a sync passes no resolution.</summary>
    public double devicePixelRatio = 1;
}

/// <summary>The argument of `syncCamera`.</summary>
public sealed class TerrainCameraSync
{
    public double width;
    public double height;
    public double offsetX;
    public double offsetY;
    public double zoom;
    public double resolution;
    /// <summary>Axis-aligned source-world bounds when the rendered world carries an orbit transform.</summary>
    public CameraView? worldView;
}

/// <summary>
/// The tile-management half of ThreeTerrainLayer, called at the exact points where the original touches tile state.
/// Implemented by the integrator's tile manager; a null host behaves like a layer with an always-ready, empty-planner
/// visible bank (what the reference viewer shows once its viewport is ready).
/// </summary>
public interface ITerrainPresentationHost
{
    /// <summary>setBiome lines 3606–3625: hub-return prefetch reset, `resetTerrainPlanner()`, covered-transition
    /// fallback, `authoritativeTerrainRevealed = false`, readiness reset, and `retireAllTiles()` / `evictAllTiles()`.</summary>
    void resetForBiome(Biome biome, bool deferSourceDisposal, bool preparedBankActivated);
    /// <summary>setBiome: `runMaterialForCell = createRunTerrainMaterialResolver(tileset, worldStyleOf(biome), hub)`.</summary>
    void biomeTilesetChanged(Biome biome, TerrainTileset tileset);
    /// <summary>setBiome: `terrainShadowCapturedCasters = new WeakSet()`.</summary>
    void resetCapturedShadowCasters();
    /// <summary>configureWebGlCapability: `geometryConfigKey = ''` (compiler reconfiguration).</summary>
    void geometryConfigurationChanged();
    /// <summary>`terrainCachePrewarming`.</summary>
    bool terrainCachePrewarming { get; }
    /// <summary>`activeTileRoot.children.length` (mounted, shown tile groups).</summary>
    int activeTileCount { get; }
    /// <summary>`viewportReadiness.ready`.</summary>
    bool viewportReady { get; }
    /// <summary>`shadowCasterWorkPending()`.</summary>
    bool shadowCasterWorkPending();
    /// <summary>`visibleShadowCastersCaptured()`.</summary>
    bool visibleShadowCastersCaptured();
    /// <summary>refreshTerrainShadowMap: record the mounted, shadow-relevant caster groups as captured.</summary>
    void captureShadowCasters();
}

/// <summary>
/// One lane's material/flags as `groupsFromTransferPayload` configures every tile mesh of that lane (the value
/// emulation's stand-in for the host's tile meshes; the Godot layer can read render order / shadow flags from it).
/// </summary>
public sealed class TerrainLaneTemplate
{
    public readonly ThreeGroup group = new ThreeGroup();
    public readonly ThreeGroup actorWalls = new ThreeGroup();
    public ThreeMesh? surface;
    public ThreeMesh? water;
    public ThreeMesh? mist;
    public ThreeMesh? overlay;
    public ThreeMesh? actorWall;
}

/// <summary>
/// The presentation state of `ThreeTerrainLayer`: lights, shadow window, backdrop, world yaw, and every terrain
/// material's uniform set. Engine-free; the Godot layer binds <see cref="materialUniformSets"/>,
/// <see cref="sceneMatrixWorld"/> etc. after each <see cref="presentFrame"/>.
/// </summary>
public sealed partial class TerrainPresentationState
{
    private readonly ITerrainPresentationHost? host;
    /// <summary>`RENDER_QUALITY` / `PERF` (see TerrainRenderQuality).</summary>
    public readonly TerrainRenderQuality quality;
    /// <summary>`SHADOW_MAP_SIZE = terrainShadowMapSize(RENDER_QUALITY.terrainShadowMapSize, RENDER_QUALITY.isMobile)`.</summary>
    internal readonly double SHADOW_MAP_SIZE;

    private ThreeWebGLRenderer? renderer;
    /// <summary>`webglCapability?.executionClass` (null before configureWebGlCapability).</summary>
    private string? webglExecutionClass;
    internal bool terrainShadowsEnabled;
    internal double terrainVegetationBladeCap;
    internal double terrainCliffDressingDensity;
    internal bool visualGroundingEnabled = VISUAL_QUALITY.visualGrounding;
    public readonly ThreeScene scene = new ThreeScene();
    /// <summary>Physical skill illumination for terrain; fixed capacity keeps Three's light program family stable.</summary>
    private readonly ImpactLightPool impactLights;
    private double aetherDestinationSkyBlend = 0;
    private readonly ThreeColor terrainClearColor = new ThreeColor(TERRAIN_CLEAR_COLOR);
    private readonly ThreeColor aetherDestinationBlendedClearColor = new ThreeColor(TERRAIN_CLEAR_COLOR);
    /// <summary>Separate scene containing ONLY exact visible SOLID-wall surfaces. Re-rendered depth-only for actor cards.</summary>
    public readonly ThreeScene actorWallScene = new ThreeScene();
    /// <summary>Oblique ortho camera: identity view, hand-built projection (see `configureOrthographicProjection`).</summary>
    public readonly ThreeCamera camera = new ThreeCamera();
    public readonly ThreeGroup root = new ThreeGroup();
    public readonly ThreeGroup actorWallRoot = new ThreeGroup();
    /// <summary>Active and prepared tile banks are children of stable scene roots. Committing a fully uploaded destination
    /// therefore changes two child pointers; it never reparents every mesh or recreates the destination.</summary>
    public readonly ThreeGroup activeTileRoot = new ThreeGroup();
    public readonly ThreeGroup activeActorWallTileRoot = new ThreeGroup();
    public readonly ThreeAmbientLight ambient = new ThreeAmbientLight(0xffffff, LIGHT_RIG.ambient);
    public readonly ThreeHemisphereLight skyLight = new ThreeHemisphereLight(0xf4f6ef, 0x27352f, LIGHT_RIG.hemi);
    public readonly ThreeDirectionalLight sun = new ThreeDirectionalLight(0xfff2d0, LIGHT_RIG.sun);
    private readonly ThreeGroup sunTarget = new ThreeGroup();
    /// <summary>The actor scene's per-frame sun depth map, re-projected onto this scene's receivers so characters, pets,
    ///  monsters and statues ground with the same real shadow the wall/prop proxy batch gives trees.</summary>
    internal bool richStreamedTerrain = richStreamedTerrainRequested();
    private ActorShadowProjection actorShadows;
    /// <summary>Lightweight wall-only scene owns shadow-map refreshes; the visible surface remains a receiver only.</summary>
    private readonly ThreeDirectionalLight shadowProxySun = new ThreeDirectionalLight(0xffffff, 1);
    private readonly ThreeGroup shadowProxySunTarget = new ThreeGroup();
    public readonly ThreeDirectionalLight fill = new ThreeDirectionalLight(0xbfd9e8, LIGHT_RIG.fill);
    private readonly ThreeGroup fillTarget = new ThreeGroup();
    private readonly ThreeVector3 sunDirection = new ThreeVector3(
        LIGHT_RIG.sunDir.x,
        LIGHT_RIG.sunDir.y,
        LIGHT_RIG.sunDir.z);
    private readonly ThreeVector3 fillDirection = new ThreeVector3(
        LIGHT_RIG.fillDir.x,
        LIGHT_RIG.fillDir.y,
        LIGHT_RIG.fillDir.z);
    /// <summary>`new Mesh(new PlaneGeometry(1, 1), new MeshBasicMaterial({ color: BACKDROP_FALLBACK, side: DoubleSide,
    /// depthTest: false, depthWrite: false }))`. The geometry is a unit XY plane rotated −π/2 about X in init
    /// (`geometry.rotateX`), i.e. a unit quad in the XZ plane; the Godot layer builds that quad itself.</summary>
    public readonly ThreeMesh backdrop = new ThreeMesh(createBackdropMaterial());
    /// <summary>Compact materials are an explicit software/diagnostic tier. Hardware uses the complete authored material,
    /// lighting, atmosphere and shadow graph; startup scheduling must never trade those features for a faster gate.</summary>
    private ThreeMeshBasicMaterial? compactSurfaceMaterial;
    private ThreeMeshBasicMaterial? compactWaterMaterial;
    private ThreeMeshBasicMaterial? softwareSurfaceMaterial;
    private ThreeMeshBasicMaterial? softwareWaterMaterial;
    private ThreeMeshStandardMaterial? surfaceMaterial;
    /// <summary>Explicit shadow-pass twin of the surface vertex deformation; Three's implicit depth material would
    /// otherwise cast the old ruler-flat silhouette even though the colour pass renders rolling ground.</summary>
    private ThreeMeshDepthMaterial? surfaceDepthMaterial;
    private ThreeMeshStandardMaterial? waterMaterial;
    private ThreeMeshBasicMaterial? mistMaterial;
    private ThreeMeshBasicMaterial? overlayMaterial;
    private ThreeMeshBasicMaterial? actorWallMaterial;
    private ThreeMeshDepthMaterial? actorWallDepthMaterial;
    /// <summary>Single per-frame uniform driving ALL water animation — no geometry or material rebuilds, ever.</summary>
    private readonly ThreeUniform<double> waterTime = WorldSurface.UNIFORMS.time;
    private readonly ThreeUniform<ThreeColor> waterFoamColor = new(new ThreeColor(0xfffdf3));
    private readonly ThreeUniform<ThreeColor> waterShallowColor = new(new ThreeColor(0x849095));
    /// <summary>The authored deep-water pole is sampled by the liquid itself, not only by the hidden basin shell.</summary>
    private readonly ThreeUniform<ThreeColor> waterDeepColor = new(new ThreeColor(0x323c40));
    /// <summary>Theme-aware colour pole used only to keep exposed basin cross-sections out of display black crush.</summary>
    private readonly ThreeUniform<ThreeColor> waterBasinColor = new(new ThreeColor(0x49575c));
    /// <summary>Per-theme WORLD STYLE uniforms (see WorldStyle) — set once in setBiome, shared by every
    ///  tile: (floorGrain, rockGrain, strata) pattern gains, the painterly hue-drift pole pair, and the water's
    ///  animation character (waveSpeed, foam, glint, shallow-caustic gain). Pure uniforms: theme identity
    ///  without a single rebake.</summary>
    private readonly ThreeUniform<ThreeVector4> styleGains = new(new ThreeVector4(1, 1, 1, 0));
    private readonly ThreeUniform<ThreeColor> styleDriftA = new(new ThreeColor(1.017, 1.004, 0.982));
    private readonly ThreeUniform<ThreeColor> styleDriftB = new(new ThreeColor(0.985, 1.0, 1.021));
    /// <summary>Biome-derived material poles for the texture-free floor splat (base pigment is the third pole).</summary>
    private readonly ThreeUniform<ThreeColor> floorSplatLush = new(new ThreeColor(0x769b68));
    /// <summary>
    /// The pole a turf mat grows toward. Shared verbatim with the bake through `terrainFloorTurfPole`, so
    /// the ground the shader paints green and the blades the compiler roots in it agree by construction.
    /// </summary>
    private readonly ThreeUniform<ThreeColor> floorTurf = new(new ThreeColor(0x668c52));
    private readonly ThreeUniform<ThreeColor> floorSplatDry = new(new ThreeColor(0xa58b63));
    private readonly ThreeUniform<ThreeColor> floorSplatMineral = new(new ThreeColor(0x858c82));
    /// <summary>Authored-procedural biome form/material controls; shared by visible and depth materials.</summary>
    private readonly ThreeUniform<ThreeVector4> terrainFormA = new(new ThreeVector4(360, 132, 2.55, -0.42));
    private readonly ThreeUniform<ThreeVector4> terrainFormB = new(new ThreeVector4(42, 0.08, 0.08, 168));
    /// <summary>CPU-resolved organic-ground rotation and reciprocal wavelengths.</summary>
    private readonly ThreeUniform<ThreeVector4> terrainFormResolved = new(
        new ThreeVector4(Math.cos(-0.42), Math.sin(-0.42), 1.0 / 360, 1.0 / 132));
    private readonly ThreeUniform<double> terrainRidgeInverse = new(1.0 / 168);
    private readonly ThreeUniform<ThreeVector4> terrainMaterialA = new(new ThreeVector4(390, 108, 30, -0.34));
    /// <summary>CPU-resolved material rotation/frequencies: immutable per biome, never recomputed for every fragment.</summary>
    private readonly ThreeUniform<ThreeVector2> terrainMaterialRotation = new(
        new ThreeVector2(Math.cos(-0.34), Math.sin(-0.34)));
    private readonly ThreeUniform<ThreeVector3> terrainMaterialFrequency = new(
        new ThreeVector3(1.0 / 390, 1.0 / 108, 1.0 / 30));
    /// <summary>(cos, sin, resolution) — the drawn sheet's two frame-constant facts. The rotation keeps the sheet (laid
    ///  paper, hatch) oriented on the page while the world turns under it, written by setWorldYaw; the
    ///  resolution scale states the sheet's mip threshold in the pixels the player is actually shown rather than
    ///  in render-target fragments, written by syncCamera. One CPU write per frame, no per-fragment
    ///  trigonometry and no second derivative.</summary>
    private readonly ThreeUniform<ThreeVector3> paperSheet = WorldSurface.UNIFORMS.paperSheet;
    private readonly ThreeUniform<ThreeVector4> terrainMaterialB = new(new ThreeVector4(0.2, 0.11, 0.08, 0.72));
    /// <summary>(structured-panel blend, pattern gain, along-wall period px, vertical course px). Chasm continuations use
    /// this to inherit the active construction language from existing material samples without textures/passes.
    /// Semantic BRDF library: (floor roughness, rock roughness, timber roughness, safe cap sheen).</summary>
    private readonly ThreeUniform<ThreeVector4> terrainMaterialFinish = new(new ThreeVector4(0.82, 0.91, 0.72, 0.56));
    /// <summary>(horizontal amplitude px, world scale px, reserved, reserved).</summary>
    private readonly ThreeUniform<ThreeVector4> terrainContour = new(new ThreeVector4(0, 210, 0, 0));
    private double terrainHazeGain = 0.92;
    private double weatherHazeScale = 1;
    // STUDIO: the game's weather timeline is not ported, so there is no weather frame; the studio's weather is part of
    // its look (LookSettings: wetness, rain, snow, sun).
    private WeatherFrame? weather = null;
    private double weatherPresentationScale = 0;
    private bool weatherLightningFlashes = true;
    private DayNightFrame? dayNight;
    private readonly ThreeUniform<double> terrainNight = new(0);
    private readonly ThreeUniform<double> terrainChasmGlow = new(0);
    private readonly ThreeUniform<ThreeColor> terrainChasmGlowColor = new(new ThreeColor(0x63d9cf));
    /// <summary>(wet ground, cloud shadow, precipitation, wind), shared by every terrain tile.</summary>
    private readonly ThreeUniform<ThreeVector4> weatherSurface = WorldSurface.UNIFORMS.weatherSurface;
    /// <summary>Deterministic lying snow. It outlasts the precipitation state that deposited it.</summary>
    private readonly ThreeUniform<double> weatherSnow = WorldSurface.UNIFORMS.weatherSnow;
    private readonly ThreeUniform<ThreeVector4> waterStyle = new(new ThreeVector4(1, 1, 1, 0));
    /// <summary>Depth-weighted share of the aerial-haze sky colour open water reflects (∝ the theme's hemisphere dome;
    ///  see waterMaterialFor — constant under the shear-ortho, deliberately NOT a faked Fresnel).</summary>
    private readonly ThreeUniform<double> waterSky = new(0.16);
    /// <summary>(direction radians, strength, tempo, gust envelope), shared by every wind-weighted tree vertex.</summary>
    private readonly ThreeUniform<ThreeVector4> terrainWind = new(new ThreeVector4(-0.38, 0.8, 0.9, 0.65));
    /// <summary>CPU-resolved unit wind vector shared by foliage and the cloud field.</summary>
    private readonly ThreeUniform<ThreeVector2> terrainWindDirection = WorldSurface.UNIFORMS.windDirection;
    /// <summary>World-px -> presented CSS-px. Wind uses it to freeze deformation that rasterises only as coverage noise.</summary>
    private readonly ThreeUniform<double> terrainViewZoom = new(1);
    /// <summary>Aerial-depth haze (shared by surface/water/overlay/backdrop shaders; per-frame uniform update only).
    ///  These are the WORLD atmosphere's uniform objects, held by identity: the actor scene's materials bind
    ///  the exact same three objects (`aerialHaze.ts`), so figures and ground can never stand in different air
    ///  and a frame update stays three writes for the whole picture.</summary>
    private readonly ThreeUniform<ThreeColor> hazeColor = AerialHazeModule.WORLD_AERIAL_HAZE.color;
    private readonly ThreeUniform<ThreeVector4> hazeCfg = AerialHazeModule.WORLD_AERIAL_HAZE.cfg;
    private readonly ThreeUniform<ThreeVector4> hazeBody = AerialHazeModule.WORLD_AERIAL_HAZE.body;
    /// <summary>HANDINK art-direction uniforms (hatch/wash/shadow-ink/paper-fibre) — one shared set for all materials;
    ///  all strengths ride INK_LOOK and are 0 when the direction is switched off.</summary>
    private readonly InkLookUniforms ink = WorldSurface.UNIFORMS.ink;
    /// <summary>Backdrop framing (view centre + inverse extents for the edge-darkening vignette).</summary>
    private readonly ThreeUniform<ThreeVector4> backdropView = new(new ThreeVector4(0, 0, 0.001, 0.001));
    /// <summary>Reuses the boundary-free terrain continuum as an atmospheric sky during Aether descent. A uniform keeps
    /// the shader family stable: no landing-frame material swap, program link or projected replacement plane.</summary>
    private readonly ThreeUniform<double> aetherDestinationSky = new(0);
    private readonly ThreeUniform<ThreeColor> aetherDestinationSkyColor = new(new ThreeColor(AETHER_DESTINATION_SKY_CLEAR_COLOR));
    /// <summary>The terrain is static and the sun follows the view in texel snaps — re-render the shadow map ONLY when
    ///  either actually changed (tile baked/shown/hidden or light anchor moved), never on idle frames.</summary>
    internal bool shadowDirty = true;
    /// <summary>True only after a visible active-world tile bank, never an empty/hidden scene, populated the depth map.</summary>
    internal bool terrainShadowSnapshotInitialized = false;
    /// <summary>Consecutive rendered frames with no pending caster mutation or relevant compiler job.</summary>
    internal int terrainShadowSnapshotStableFrames = 0;
    /// <summary>
    /// Streamed caster changes are accumulated until the last relevant terrain job has completed. The previously
    /// rendered depth texture remains authoritative while that batch is incomplete; otherwise the 5 Hz sun tick
    /// exposes every one-tile cooperative Hub install as a different global shadow snapshot.
    /// </summary>
    internal bool shadowCasterSnapshotPending = false;
    /// <summary>A mounted/retired caster belongs to the currently presented tile rectangle. Unlike speculative cache-ring
    /// work, this batch may not wait for unrelated workers once viewport readiness says the rectangle is whole.</summary>
    internal bool visibleShadowCasterSnapshotPending = false;
    internal double lastShadowCx = double.NaN;
    internal double lastShadowCz = double.NaN;
    internal double lastShadowRadius = double.NaN;
    /// <summary>Last camera tuple used for the oblique projection matrix and inverse. NaN forces the first build.</summary>
    private double lastProjW = double.NaN;
    private double lastProjH = double.NaN;
    private double lastProjZoom = double.NaN;
    private double lastProjOffX = double.NaN;
    private double lastProjOffY = double.NaN;
    private Biome? biome;
    /// <summary>CPU-authored organic cap geometry and actor grounding consume this same immutable biome field.</summary>
    internal TerrainSurfaceProfile terrainSurfaceProfile = TerrainSurfaceProfileModule.terrainSurfaceProfileForBiome(null);
    internal int floorDryPole = 0xa58b63;
    private TerrainTileset? tileset;
    /// <summary>True when the active theme's tileset is the `city` construction language.</summary>
    internal bool cityTileset = false;
    /// <summary>True when the active theme is the olympian sky-borough construction language.</summary>
    internal bool olympianTileset = false;
    /// <summary>True when the active theme is the star-cathedral construction language.</summary>
    internal bool cathedralTileset = false;
    /// <summary>True when the active theme is the Sugarstorm-Carnival construction language.</summary>
    internal bool carnivalTileset = false;
    /// <summary>True when the active theme is the clockwork moon bazaar construction language.</summary>
    internal bool clockworkTileset = false;
    /// <summary>True when the active theme is the mirror-glass archive construction language.</summary>
    internal bool prismglassTileset = false;
    /// <summary>The theme's strata gain (WorldStyle.strata) — scales how strongly natural cliff faces band, so an
    ///  icy rift, an ashen scarp and an alpine wall each carry their own geological character from pure data.</summary>
    internal double styleStrata = 1;
    private double width = 1;
    private double height = 1;
    private double rendererWidth = 0;
    private double rendererHeight = 0;
    private double pixelRatio = 0;
    private double zoom = 1;
    private double offsetX = 0;
    private double offsetY = 0;
    internal double ts = 40;
    private bool initialized = false;

    /// <summary>Depth-map republications since the last presented frame, and the view radius that drove the fit.
    ///  Both are written unconditionally (one store each) so the recorder cannot alter the path it measures.</summary>
    private int flickerShadowRebuilds = 0;
    private int flickerVisibleShadowLatticeRebuilds = 0;
    internal double lastRenderedShadowCx = double.NaN;
    internal double lastRenderedShadowCz = double.NaN;
    internal double lastRenderedShadowRadius = double.NaN;
    private double flickerViewRadius = 0;

    /// <summary>The lane materials' template meshes (see TerrainLaneTemplate); created by precompileMaterials.</summary>
    private TerrainLaneTemplate? laneTemplate;

    public TerrainPresentationState(TerrainRenderQuality? quality = null, ITerrainPresentationHost? host = null)
    {
        this.quality = quality ?? new TerrainRenderQuality();
        this.host = host;
        this.SHADOW_MAP_SIZE = TerrainRuntimeHelpers.terrainShadowMapSize(
            this.quality.terrainShadowMapSize,
            this.quality.isMobile);
        this.terrainShadowsEnabled = this.quality.terrainShadows;
        this.terrainVegetationBladeCap = this.quality.terrainVegetationBladeCap;
        this.terrainCliffDressingDensity = this.quality.terrainCliffDressingDensity;
        this.impactLights = new ImpactLightPool(this.scene, new ImpactLightPoolOptions
        {
            capacity = VISUAL_QUALITY.worldLights ? this.quality.impactLightCapacity : 0,
        });
        this.actorShadows = new ActorShadowProjection(
            this.quality.terrainShadows &&
            this.quality.actorSunShadows &&
            VISUAL_QUALITY.actorGroundShadows &&
            this.richStreamedTerrain);
    }

    private static ThreeMeshBasicMaterial createBackdropMaterial()
    {
        ThreeMeshBasicMaterial material = new ThreeMeshBasicMaterial
        {
            side = ThreeConstants.DoubleSide,
            depthTest = false,
            depthWrite = false,
        };
        material.color.set(BACKDROP_FALLBACK);
        return material;
    }

    /// <summary>Apply the one capability decision made from the already-created production context. Full authored terrain
    /// is mandatory on hardware. Only a positively identified software rasterizer (or the explicit compact debug
    /// query) may enter the structural safety tier. Must run before init.</summary>
    public void configureWebGlCapability(string executionClass)
    {
        if (this.initialized) return;
        this.webglExecutionClass = executionClass;
        if (executionClass == "software") this.richStreamedTerrain = false;
        if (!this.richStreamedTerrain)
        {
            // Four blades retain readable authored vegetation clusters while bounding the previous 24-blade fan-out.
            this.terrainVegetationBladeCap = Math.min(this.terrainVegetationBladeCap, 4);
            this.terrainCliffDressingDensity = Math.min(this.terrainCliffDressingDensity, 0.35);
            this.visualGroundingEnabled = false;
            // Basic vertex pigment does not sample the terrain shadow map. Building the duplicate actor-wall bank
            // and holding reveal for its depth snapshot would spend memory/driver time on an unreachable result.
            this.terrainShadowsEnabled = false;
            this.actorShadows = new ActorShadowProjection(false);
        }
        if (executionClass != "software")
        {
            this.host?.geometryConfigurationChanged();
            return;
        }
        this.terrainShadowsEnabled = false;
        // Zero is the compiler's explicit structural tier: the terrain structure remains while volumetric ecology is
        // left out instead of sending hundreds of thousands of decorative vertices through a CPU rasterizer.
        this.terrainVegetationBladeCap = 0;
        this.terrainCliffDressingDensity = 0;
        this.visualGroundingEnabled = false;
        this.actorShadows = new ActorShadowProjection(false);
        this.host?.geometryConfigurationChanged();
    }

    /// <summary>`init(target)` without a GL target: builds the value-only renderer and the light/scene rig.</summary>
    public void init()
    {
        if (this.initialized) return;
        // (terrainProgramWarmupReady / pixel-store reset / context-restore listener / renderer diagnostics are GL
        // plumbing with no value effect.)
        this.renderer = new ThreeWebGLRenderer();
        this.renderer.setClearColor(
            this.aetherDestinationBlendedClearColor
                .copy(this.terrainClearColor)
                .lerp(this.aetherDestinationSkyColor.value, this.aetherDestinationSkyBlend),
            1);
        this.renderer.autoClear = true;
        this.renderer.shadowMap.enabled = this.terrainShadowsEnabled;
        this.renderer.shadowMap.type = ThreeConstants.PCFShadowMap;
        // The world is static geometry under a texel-snapped sun — the shadow pass re-renders ONLY when tiles or
        // the light anchor changed (see shadowDirty), not per frame; a big idle/GPU win, especially on mobile.
        this.renderer.shadowMap.autoUpdate = false;

        // The oblique projection is authored by hand each frame; the view matrix stays identity so world space IS
        // view space — lights and normals need no transform gymnastics.
        this.camera.matrixAutoUpdate = false;
        this.camera.matrixWorldInverse.identity();

        this.scene.add(this.ambient);
        this.scene.add(this.skyLight);
        this.sun.castShadow = this.terrainShadowsEnabled;
        this.sun.shadow!.mapSize.set(this.SHADOW_MAP_SIZE, this.SHADOW_MAP_SIZE);
        // Bias pair tuned against the blocky 15px-step geometry: a whisper of depth bias against acne, a couple of
        // world-px of normal bias so the PCF kernel never self-shadows the cap mesh it filters across. The tighter
        // value preserves contact under the tessellated relief while keeping the independently rasterised, exactly
        // coplanar Solid-cap proxy from printing its cell triangulation back onto the visible plateau.
        this.sun.shadow.bias = -0.0003;
        this.sun.shadow.normalBias = 1.8;
        // Three r185's PCF path is a rotated five-sample Vogel disk over hardware PCF (effectively 20 taps).
        // A wider static-terrain kernel is therefore the desired Poisson-quality filter without a custom fork.
        this.sun.shadow.radius = VISUAL_QUALITY.highQualityShadows ? 3.5 : 1;
        this.sun.target = this.sunTarget;
        // Both lights share ONE shadow resource. Only the proxy light is submitted when the depth map is dirty;
        // the visible sun keeps sampling that map without traversing the complete surface mesh.
        //
        // This is deliberately NOT double-buffered. A back buffer was tried against a reported light/dark flash on
        // the theory that the visible pass could sample a half-written depth target — it cannot: the proxy shadow
        // pass and the following visible draw are submitted to the SAME GL context, so the write-before-read
        // dependency is the driver's to honour, not ours. What the swap did add was a depth texture whose identity
        // alternated on every publish (rebinding the terrain sampler and the actor-shadow fallback each time) plus
        // a second resident 1024² depth target, and shadow-camera state split across two LightShadow objects that
        // every other seam here then had to keep in sync.
        this.shadowProxySun.shadow = this.sun.shadow;
        this.shadowProxySun.castShadow = this.terrainShadowsEnabled;
        this.shadowProxySun.target = this.shadowProxySunTarget;
        this.fill.castShadow = false;
        this.fill.target = this.fillTarget;
        this.scene.add(this.sunTarget, this.sun, this.fillTarget, this.fill);
        this.scene.add(this.root);
        this.actorWallScene.add(this.shadowProxySunTarget, this.shadowProxySun, this.actorWallRoot);
        this.root.add(this.activeTileRoot);
        this.actorWallRoot.add(this.activeActorWallTileRoot);
        // Keep the continuum below the terrain. A camera-facing plane becomes an opaque, screen-wide fog overlay
        // screen-wide fog overlay whenever streamed Endless geometry has not filled the complete view yet.
        // (`backdrop.geometry.rotateX(-Math.PI / 2)`: geometry only — see the `backdrop` field.)
        this.backdrop.frustumCulled = false;
        // Draw the fallback first without depth ownership. It fills genuine terrain gaps, while every real surface
        // submitted afterwards deterministically replaces it. A projected "far" plane drawn last is not portable:
        // ANGLE/SwiftShader have both admitted it ahead of valid tiles under the hand-authored oblique projection.
        this.backdrop.renderOrder = TERRAIN_BACKDROP_RENDER_ORDER;
        // Once front-loaded, the backdrop shades every pixel before terrain replaces most of them. Hardware keeps
        // the authored atmospheric continuum; an explicit software rasterizer uses the already climate-tinted
        // Basic fallback instead, avoiding a full-screen procedural-noise pass and preserving the 140 Hz budget.
        if (this.webglExecutionClass != "software") this.installBackdropShader();
        this.root.add(this.backdrop);
        this.precompileMaterials();
        this.initialized = true;
    }

    /// <summary>The void beyond the generated world: one boundary-free remote continuum with large mottled noise, a
    /// framing falloff toward the view edges, and restrained aerial haze. One quad, one shader, no component copy.</summary>
    private void installBackdropShader()
    {
        ThreeMeshBasicMaterial material = (ThreeMeshBasicMaterial)this.backdrop.material;
        // The compact streamed bank normally covers this fallback completely. Compiling its full procedural
        // haze/night dialect nevertheless forced ANGLE to reflect another program during the first terrain frame.
        if (this.webglExecutionClass == "software" || !this.richStreamedTerrain) return;
        ThreeUniform<ThreeColor> hazeColor = this.hazeColor;
        ThreeUniform<ThreeVector4> hazeCfg = this.hazeCfg;
        ThreeUniform<ThreeVector4> hazeBody = this.hazeBody;
        ThreeUniform<ThreeVector4> backdropView = this.backdropView;
        ThreeUniform<double> voidTime = this.waterTime;
        ThreeUniform<double> terrainNight = this.terrainNight;
        ThreeUniform<double> aetherDestinationSky = this.aetherDestinationSky;
        ThreeUniform<ThreeColor> aetherDestinationSkyColor = this.aetherDestinationSkyColor;
        material.onBeforeCompile = (shader) =>
        {
            shader.uniforms["uMmoratHaze"] = hazeColor;
            shader.uniforms["uMmoratHazeCfg"] = hazeCfg;
            shader.uniforms["uMmoratHazeBody"] = hazeBody;
            shader.uniforms["uMmoratView"] = backdropView;
            shader.uniforms["uMmoratVoidTime"] = voidTime;
            shader.uniforms["uMmoratNight"] = terrainNight;
            shader.uniforms["uMmoratAetherSky"] = aetherDestinationSky;
            shader.uniforms["uMmoratAetherSkyColor"] = aetherDestinationSkyColor;
            // vertex/fragment chunk surgery: ported in shaders/terrain/terrain_backdrop_comic.gdshader.
        };
        material.customProgramCacheKey = () =>
            "fluitown-terrain-backdrop-v15-distance-independent-chasm-readable-multiscale-depth-values-reused-sample-fine-depth-animation-night-depth-lift-aether-sky-continuum-haze-body-air-mix-v2";
    }

    /// <summary>Compile all four tile shaders (plus the backdrop) at init against throwaway micro-geometry, so the
    ///  FIRST tile bake never eats the shader-compile hitch mid-walk (the old first-crossing frame spike).</summary>
    private void precompileMaterials()
    {
        if (this.renderer == null) return;
        if (!this.richStreamedTerrain)
        {
            // Reflect the exact two production Basic materials before any large tile buffer reaches ANGLE.
            // (`prewarmTerrainUpload(warmup, …, 'compact-bootstrap')` submits them once: uniforms are built there.)
            ThreeGroup compactWarmup = new ThreeGroup();
            compactWarmup.add(
                new ThreeMesh(this.terrainSurfaceMaterialFor()) { castShadow = false, receiveShadow = false },
                new ThreeMesh(this.terrainWaterMaterialFor()) { castShadow = false, receiveShadow = false });
            this.scene.add(compactWarmup);
            this.renderer.compile(this.scene, this.camera);
            this.scene.remove(compactWarmup);
            this.laneTemplate = this.terrainLaneTemplateFor();
            this.activeTileRoot.add(this.laneTemplate.group);
            this.activeActorWallTileRoot.add(this.laneTemplate.actorWalls);
            return;
        }
        // Initialise the terrain sun's attached comparison-depth texture before surface/water install their custom
        // actor-shadow sampler. ANGLE validates sampler bindings even when the strength-zero shader branch returns
        // before sampling, so the first bootstrap draw must already have a real depth target to bind.
        this.prewarmShadowBootstrap();
        ThreeGroup warmup = new ThreeGroup();
        ThreeMesh surfaceMesh = new ThreeMesh(this.surfaceMaterialFor());
        surfaceMesh.castShadow = false;
        // Production terrain receives the static sun shadow. Without this flag the bootstrap compiled only the
        // unshadowed colour variant and the first biome tile paid ANGLE's full shader-link wait during travel.
        surfaceMesh.receiveShadow = true;
        surfaceMesh.customDepthMaterial = this.surfaceDepthMaterialFor();
        ThreeMesh waterMesh = new ThreeMesh(this.waterMaterialFor());
        waterMesh.receiveShadow = true;
        ThreeMesh overlayMesh = new ThreeMesh(this.overlayMaterialFor());
        ThreeMesh mistMesh = new ThreeMesh(this.mistMaterialFor());
        ThreeMesh actorWallMesh = new ThreeMesh(this.actorWallMaterialFor());
        actorWallMesh.customDepthMaterial = this.actorWallDepthMaterialFor();
        actorWallMesh.castShadow = true;
        warmup.add(surfaceMesh, waterMesh, mistMesh, overlayMesh, actorWallMesh);
        this.scene.add(warmup);
        // (The presented world compiles against PostFx's 1×1 HDR warm-up target, then reflects one family per
        // animation frame through `prewarmTerrainUpload`; only `compile` builds uniform objects.)
        this.renderer.compile(this.scene, this.camera);
        this.scene.remove(warmup);
        // The depth twins are compiled by the per-family 'initialize-shadow' / 'lit-preserve-shadow' warm-ups; their
        // uniform objects are built the first time the shadow pass draws them (see ThreeWebGLRenderer.render).
        this.laneTemplate = this.terrainLaneTemplateFor();
        this.activeTileRoot.add(this.laneTemplate.group);
        this.activeActorWallTileRoot.add(this.laneTemplate.actorWalls);
    }

    /// <summary>
    /// `prewarmTerrainUpload(new Group(), new Group(), 'initialize-shadow')`: one private render of the (empty) live
    /// scene with the shadow pass forced, so the terrain sun owns its comparison-depth target before any receiver
    /// program is built. Only the camera's warm-up layer is drawn, i.e. nothing but the lights.
    /// </summary>
    private void prewarmShadowBootstrap()
    {
        ThreeWebGLRenderer? renderer = this.renderer;
        if (renderer == null) return;
        const int EXTERNAL_PREWARM_LAYER = 31;
        List<(ThreeObject3D @object, int layerMask)> lightStates = new List<(ThreeObject3D, int)>();
        this.scene.traverse(@object =>
        {
            if (!@object.isLight) return;
            lightStates.Add((@object, @object.layers.mask));
            @object.layers.enable(EXTERNAL_PREWARM_LAYER);
        });
        int cameraMask = this.camera.layers.mask;
        bool previousShadowUpdate = renderer.shadowMap.needsUpdate;
        int shadowCameraMask = this.sun.shadow!.camera.layers.mask;
        try
        {
            this.camera.layers.set(EXTERNAL_PREWARM_LAYER);
            this.sun.shadow.camera.layers.enable(EXTERNAL_PREWARM_LAYER);
            renderer.shadowMap.needsUpdate = true;
            renderer.render(this.scene, this.camera);
        }
        finally
        {
            this.camera.layers.mask = cameraMask;
            this.sun.shadow.camera.layers.mask = shadowCameraMask;
            foreach ((ThreeObject3D @object, int layerMask) in lightStates) @object.layers.mask = layerMask;
            renderer.shadowMap.needsUpdate = previousShadowUpdate;
        }
        this.bindActorShadowFallback();
    }

    /// <summary>
    /// The material/flag half of `groupsFromTransferPayload`: which material, render order and shadow flags every tile
    /// mesh of each lane receives. The value emulation mounts one such template as the stand-in for all shown tiles.
    /// </summary>
    public TerrainLaneTemplate terrainLaneTemplateFor()
    {
        TerrainLaneTemplate template = new TerrainLaneTemplate();
        bool softwareSafe = this.webglExecutionClass == "software" || !this.richStreamedTerrain;
        {
            ThreeMesh mesh = new ThreeMesh(this.terrainSurfaceMaterialFor());
            mesh.castShadow = false;
            mesh.receiveShadow = !softwareSafe;
            if (!softwareSafe) mesh.customDepthMaterial = this.surfaceDepthMaterialFor();
            mesh.frustumCulled = false;
            template.group.add(mesh);
            template.surface = mesh;
        }
        {
            ThreeMesh mesh = new ThreeMesh(this.terrainWaterMaterialFor());
            mesh.castShadow = false;
            mesh.receiveShadow = !softwareSafe;
            mesh.renderOrder = 5;
            mesh.frustumCulled = false;
            template.group.add(mesh);
            template.water = mesh;
        }
        if (!softwareSafe)
        {
            ThreeMesh mesh = new ThreeMesh(this.mistMaterialFor());
            mesh.castShadow = false;
            mesh.receiveShadow = false;
            mesh.renderOrder = 8;
            mesh.frustumCulled = false;
            template.group.add(mesh);
            template.mist = mesh;
        }
        // Transparent micro-decals are the least useful and most overdraw-heavy lane on an explicit software
        // rasterizer. Base vertex pigment still carries the complete terrain/material identity there.
        if (!softwareSafe)
        {
            ThreeMesh mesh = new ThreeMesh(this.overlayMaterialFor());
            mesh.castShadow = false;
            mesh.receiveShadow = false;
            mesh.renderOrder = 10;
            mesh.frustumCulled = false;
            template.group.add(mesh);
            template.overlay = mesh;
        }
        if (!softwareSafe)
        {
            ThreeMesh mesh = new ThreeMesh(this.actorWallMaterialFor());
            mesh.customDepthMaterial = this.actorWallDepthMaterialFor();
            mesh.castShadow = true;
            mesh.receiveShadow = false;
            mesh.frustumCulled = false;
            template.actorWalls.add(mesh);
            template.actorWall = mesh;
        }
        return template;
    }

    /* ── Godot-facing read API ───────────────────────────────────────────────────────────────────────────── */

    /// <summary>PORT ADDITION: <c>materialUniformSets()</c> into a list the caller reuses (the Godot binder runs
    /// every frame and must not allocate). The list is cleared first.</summary>
    public void materialUniformSets(List<(string family, ThreeMaterial material, ThreeUniformSet? uniforms)> sets)
    {
        sets.Clear();
        this.addUniformSet(sets, "surface", this.surfaceMaterial ?? (ThreeMaterial?)this.compactSurfaceMaterial ?? this.softwareSurfaceMaterial);
        this.addUniformSet(sets, "surfaceDepth", this.surfaceDepthMaterial);
        this.addUniformSet(sets, "water", this.waterMaterial ?? (ThreeMaterial?)this.compactWaterMaterial ?? this.softwareWaterMaterial);
        this.addUniformSet(sets, "mist", this.mistMaterial);
        this.addUniformSet(sets, "overlay", this.overlayMaterial);
        this.addUniformSet(sets, "backdrop", this.backdrop.material);
        this.addUniformSet(sets, "actorWall", this.actorWallMaterial);
        this.addUniformSet(sets, "actorWallDepth", this.actorWallDepthMaterial);
    }

    private void addUniformSet(List<(string, ThreeMaterial, ThreeUniformSet?)> sets, string family, ThreeMaterial? material)
    {
        if (material != null) sets.Add((family, material, this.renderer?.uniformsOf(material)));
    }

    /// <summary>`scene.matrixWorld` — the world-yaw matrix ("three view space" = this × compile space).</summary>
    public ThreeMatrix4 sceneMatrixWorld => this.scene.matrixWorld;

    /// <summary>The terrain camera's hand-built oblique projection (identity view matrix).</summary>
    public ThreeMatrix4 cameraProjectionMatrix => this.camera.projectionMatrix;

    /// <summary>`backdrop.matrixWorld` (yaw × translate × scale of the unit XZ quad).</summary>
    public ThreeMatrix4 backdropMatrixWorld => this.backdrop.matrixWorld;

    private List<ThreeLight>? lightSink;
    private Action<ThreeObject3D>? collectLight;

    /// <summary>PORT ADDITION: <c>sceneLights()</c> into a list the caller reuses (per frame, no allocation).
    /// The list is cleared first.</summary>
    public void sceneLights(List<ThreeLight> lights)
    {
        lights.Clear();
        this.lightSink = lights;
        this.scene.traverse(this.collectLight ??= @object =>
        {
            if (@object is ThreeLight light) this.lightSink!.Add(light);
        });
        this.lightSink = null;
    }

    /// <summary>PORT ADDITION: <c>clearColor</c> into a colour the caller reuses.</summary>
    public ThreeColor clearColorInto(ThreeColor target) =>
        this.renderer?.getClearColor(target) ?? target.copy(this.terrainClearColor);

    /// <summary>`root.visible` (the host sets it exactly where `update()` does).</summary>
    public bool rootVisible
    {
        get => this.root.visible;
        set => this.root.visible = value;
    }
}
