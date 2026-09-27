using System;
using Fluitown.Domain;
using Fluitown.GodotApp.Rendering;
using Fluitown.GodotApp.Runtime;
using Fluitown.Render;
using Godot;

namespace TerrainStudio.Terrain;

/// <summary>What the terrain view is currently showing.</summary>
public enum TerrainSource
{
    None,
    /// <summary>A local, bounded map (painted or generated) installed through <c>ClientDungeon.setAuthored</c>.</summary>
    Authored,
    /// <summary>The unbounded, chunk-streamed procedural world of a seed and theme.</summary>
    OpenWorld,
}

/// <summary>
/// The studio's terrain: one <see cref="TerrainSceneRenderer"/> (the comic look: the ported three.js material maths as
/// pigment, lit by one comic light ramp, hatched and inked) driven every frame by an engine-free
/// <see cref="TerrainWorldSession"/> (tile manager with threaded bake workers, presentation state, day/night).
/// A session shows either an authored map or the open world; switching replaces the session and keeps the renderer.
/// Camera = the original's oblique orthographic bird's-eye view, steered through the session's focus and zoom.
/// </summary>
public sealed partial class TerrainView : Node, ITerrainHost
{
    private TerrainSceneRenderer _renderer = null!;
    private TerrainWorldSession? _session;
    private StudioTileSink _sink = null!;
    private readonly LiveUniformBinder _binder = new();
    private TerrainVisualQuality _quality = TerrainVisualQuality.High;
    private WorldRegionPaint? _regionPaint;
    private readonly ThreeColor _clearColor = new();
    private Vector2I _size;
    private double _clock;
    private double _dayPhase = 0.5;
    private bool _animateClock = true;
    private int _readyFrame = -1;

    public TerrainSceneRenderer Renderer => _renderer;
    public TerrainWorldSession Session => _session!;
    public bool HasSession => _session != null;
    public TerrainVisualQuality Quality => _quality;
    public TerrainSource Source { get; private set; }
    /// <summary>The instance id of the open world (theme:&lt;biome&gt;:&lt;seed&gt; or world).</summary>
    public string WorldId { get; private set; } = "";
    /// <summary>The authored layout on screen (null in the open world).</summary>
    public DungeonLayout? Layout { get; private set; }

    /// <summary>Whether every tile of the current view is baked and shown.</summary>
    public bool ViewReady { get; private set; }
    /// <summary>Tile-manager stage text of the last frame (loading, baking, ready …).</summary>
    public string Stage { get; private set; } = "";
    public int ActiveTiles => _session?.tiles.activeTileCount ?? 0;
    public int InstalledTiles => _sink?.InstalledTiles ?? 0;
    public int TotalInstalls => _sink?.TotalInstalls ?? 0;

    /// <summary>Zoom limits of the bird's-eye camera for the current source.</summary>
    public double MinZoom { get; private set; } = 0.3;
    public double MaxZoom { get; private set; } = 2.5;

    /// <summary>Raised once per frame after the terrain was presented (overlays follow the camera here).</summary>
    public event Action? Presented;
    /// <summary>Raised when a new session replaced the previous one.</summary>
    public event Action? SessionChanged;

    public override void _Ready()
    {
        // Runs after the UI and the tools have moved the focus this frame.
        ProcessPriority = 100;
        _size = (Vector2I)GetViewport().GetVisibleRect().Size;
        _renderer = new TerrainSceneRenderer { Name = "TerrainRenderer" };
        AddChild(_renderer);
        _renderer.Initialize(_size.X, _size.Y);
        _renderer.Mirrored = false;
        // The terrain in metres (the comic hatching, ink and shadows are metric): 1/25 m per world px, lifted by 2 m.
        _renderer.SetCompileToWorld(1f / (float)FluitownVegetation.PxPerMetre, new Vector3(0, 2, 0));
        _renderer.SetQuality(_quality);
        _binder.SetQuality(_quality);
        _renderer.SetViewMode(false, 0, 0, new Color(0.62f, 0.78f, 0.92f), new Color(0.93f, 0.9f, 0.84f));
        _sink = new StudioTileSink(_renderer);
        // The comic look's image pipeline paints the start region with its own pigments (WorldLook in the game).
        WorldRegions.FluiPalette = true;
        AerialHaze.DepthBandScale = 0f;
        ApplyQualitySwitches();
    }

    // ── sessions ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Shows the unbounded procedural world of an instance id (<c>world</c> or <c>theme:&lt;biome&gt;[:&lt;seed&gt;]</c>).</summary>
    public void OpenWorld(string instanceId, double focusX = 0, double focusY = 0, double? zoom = null, bool regions = true)
    {
        var descriptor = Descriptor.descriptorFromInstanceId(instanceId)
            ?? throw new ArgumentException($"unknown world id '{instanceId}'");
        WorldRegions.Enabled = regions;
        WorldRegions.Configure(RngModule.hashSeed(descriptor.seed), descriptor.biomeKey);
        MinZoom = 0.3;
        MaxZoom = 2.5;
        StartSession(new TerrainWorldSessionOptions { instanceId = instanceId, focusX = focusX, focusY = focusY, zoom = zoom });
        Source = TerrainSource.OpenWorld;
        WorldId = instanceId;
        Layout = null;
        SetRegionPaint(regions);
        SessionChanged?.Invoke();
    }

    /// <summary>Shows a bounded authored map (painted or generated). The layout's arrays may later be edited in place
    /// and published with <see cref="InvalidateLayout"/>.</summary>
    public void OpenAuthored(DungeonLayout layout, double? focusX = null, double? focusY = null, double? zoom = null)
    {
        WorldRegions.Enabled = false;
        double spanX = layout.width * layout.tileSize, spanY = layout.height * layout.tileSize;
        MinZoom = Math.Clamp(0.55 * Math.Min(_size.X / spanX, _size.Y / (spanY * TerrainProjection.TERRAIN_VIEW_GROUND_SCALE)), 0.05, 0.3);
        MaxZoom = 3.0;
        StartSession(new TerrainWorldSessionOptions
        {
            authoredLayout = layout,
            focusX = focusX ?? layout.originX + spanX / 2,
            focusY = focusY ?? layout.originY + spanY / 2,
            zoom = zoom ?? FitZoom(layout),
        });
        Source = TerrainSource.Authored;
        WorldId = "";
        Layout = layout;
        SetRegionPaint(false);
        SessionChanged?.Invoke();
    }

    /// <summary>The zoom that frames the whole authored map.</summary>
    public double FitZoom(DungeonLayout layout)
    {
        double spanX = layout.width * layout.tileSize;
        double spanY = layout.height * layout.tileSize * TerrainProjection.TERRAIN_VIEW_GROUND_SCALE + 400;
        double zoom = 0.86 * Math.Min(_size.X / spanX, _size.Y / spanY);
        return Math.Clamp(zoom, MinZoom, 1.2);
    }

    /// <summary>Frames the whole authored map.</summary>
    public void FitView()
    {
        if (_session == null || Layout == null) return;
        _session.focusX = Layout.originX + Layout.width * Layout.tileSize / 2;
        _session.focusY = Layout.originY + Layout.height * Layout.tileSize / 2;
        _session.zoom = FitZoom(Layout);
    }

    /// <summary>The authored layout's arrays changed in place: re-bake the tiles whose cells changed.</summary>
    public void InvalidateLayout() => _session?.invalidateAuthored();

    /// <summary>Re-plans ambient life of the authored map (after a stroke or a generator build has settled).</summary>
    public void RefreshAmbient() => _session?.refreshAuthoredAmbientPlan();

    private void StartSession(TerrainWorldSessionOptions options)
    {
        double focusX = options.focusX, focusY = options.focusY;
        _session?.dispose();
        _renderer.RemoveAllTiles();
        options.width = _size.X;
        options.height = _size.Y;
        options.dayPhase = _dayPhase;
        options.sink = _sink;
        options.liftStiffness = Config.CAMERA.elevationStiffness;
        _session = new TerrainWorldSession(options);
        _session.focusX = focusX;
        _session.focusY = focusY;
        _readyFrame = -1;
        ViewReady = false;
    }

    private void SetRegionPaint(bool enabled)
    {
        _regionPaint?.QueueFree();
        _regionPaint = null;
        _renderer.Surface.SetShaderParameter("fluitown_regions", false);
        if (!enabled) return;
        _regionPaint = new WorldRegionPaint { Name = "WorldRegionPaint", World = this };
        AddChild(_regionPaint);
    }

    // ── camera and time ─────────────────────────────────────────────────────────────────────────────────────

    public double FocusX { get => _session?.focusX ?? 0; set { if (_session != null) _session.focusX = value; } }
    public double FocusY { get => _session?.focusY ?? 0; set { if (_session != null) _session.focusY = value; } }
    public double Zoom
    {
        get => _session?.zoom ?? 1;
        set { if (_session != null) _session.zoom = Math.Clamp(value, MinZoom, MaxZoom); }
    }

    /// <summary>Time of day, 0..1 (0.5 = noon).</summary>
    public double DayPhase
    {
        get => _dayPhase;
        set
        {
            _dayPhase = value - Math.Floor(value);
            _session?.setDayPhase(_dayPhase);
        }
    }

    /// <summary>Whether water, wind and clouds move.</summary>
    public bool Animate { get => _animateClock; set => _animateClock = value; }

    /// <summary>Pans by a screen-space displacement (pixels) under the yawed oblique camera.</summary>
    public void PanScreen(double sx, double sy)
    {
        if (_session == null) return;
        if (Orbit)
        {
            float metresPerPixel = OrbitDistance * 0.0016f;
            OrbitMove((float)sx * metresPerPixel, (float)-sy * metresPerPixel);
            return;
        }
        double yaw = Config.CAMERA.worldYaw;
        double zoom = _session.zoom;
        double rx = sx / zoom;
        double ry = sy / (zoom * TerrainProjection.TERRAIN_VIEW_GROUND_SCALE);
        _session.focusX += Math.Cos(yaw) * rx - Math.Sin(yaw) * ry;
        _session.focusY += Math.Sin(yaw) * rx + Math.Cos(yaw) * ry;
        ClampFocus();
    }

    /// <summary>Zooms by a factor keeping the terrain point under <paramref name="screen"/> in place.</summary>
    public void ZoomAt(Vector2 screen, double factor)
    {
        if (_session == null) return;
        if (Orbit) { OrbitZoom((float)factor); return; }
        bool anchored = TryPickGround(screen, out double ax, out double ay);
        double before = _session.zoom;
        Zoom = before * factor;
        if (!anchored || _session.zoom == before) return;
        // Keep the anchor under the pointer: the displacement from the focus scales with 1/zoom.
        double k = 1 - before / _session.zoom;
        _session.focusX += (ax - _session.focusX) * k;
        _session.focusY += (ay - _session.focusY) * k;
        ClampFocus();
    }

    private void ClampFocus()
    {
        if (_session == null || Layout == null) return;
        double margin = 6 * Layout.tileSize;
        _session.focusX = Math.Clamp(_session.focusX, Layout.originX - margin, Layout.originX + Layout.width * Layout.tileSize + margin);
        _session.focusY = Math.Clamp(_session.focusY, Layout.originY - margin, Layout.originY + Layout.height * Layout.tileSize + margin);
    }

    // ── form (baked geometry switches) ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the comic look's geometry switches (read by the bake workers) from the look's form group. Returns whether
    /// anything changed; the caller then re-opens the current terrain so every tile is re-baked.
    /// </summary>
    public bool ApplyForm(TerrainStudio.Core.LookSettings look)
    {
        bool organic = look.Flag("form.organic"), relief = look.Flag("form.relief");
        bool vegetation = look.Flag("form.vegetation") && _renderer.Vegetation != null;
        bool herbs = vegetation && look.Flag("form.herbs");
        bool changed = organic != TerrainOrganicForm.Enabled || relief != TerrainGroundRelief.Enabled
            || vegetation != FluitownVegetation.Enabled || herbs != FluitownFlowers.Enabled;
        TerrainOrganicForm.Enabled = organic;
        Fluitown.Domain.TerrainVisualContour.OrganicCorners = organic;
        TerrainGroundRelief.Enabled = relief;
        FluitownVegetation.Enabled = vegetation;
        FluitownFlowers.Enabled = herbs;
        FluitownWallPlants.Enabled = herbs;
        _renderer.Surface.SetShaderParameter("fluitown_organic", organic);
        return changed;
    }

    /// <summary>Re-bakes everything: the current terrain is opened again with the same camera.</summary>
    public void Rebuild()
    {
        if (_session == null) return;
        double fx = _session.focusX, fy = _session.focusY, zoom = _session.zoom;
        if (Source == TerrainSource.Authored && Layout != null) OpenAuthored(Layout, fx, fy, zoom);
        else if (Source == TerrainSource.OpenWorld) OpenWorld(WorldId, fx, fy, zoom, WorldRegions.Enabled);
    }

    // ── quality ─────────────────────────────────────────────────────────────────────────────────────────────

    public void SetQuality(TerrainVisualQuality quality)
    {
        _quality = quality;
        _renderer.SetQuality(quality);
        _binder.SetQuality(quality);
        ApplyQualitySwitches();
    }

    private static readonly StringName GroundPaintName = "fluitown_ground_paint", MountainName = "fluitown_mountain",
        GroundLayersName = "fluitown_ground_layers", RockLayersName = "fluitown_rock_layers";

    /// <summary>The comic surface's world-look switches that follow the profile (WorldLook in the game).</summary>
    private void ApplyQualitySwitches()
    {
        var surface = _renderer.Surface;
        surface.SetShaderParameter(GroundPaintName, _quality.GroundPaint);
        surface.SetShaderParameter(GroundLayersName, _quality.GroundLayers);
        surface.SetShaderParameter(RockLayersName, _quality.RockLayers);
        surface.SetShaderParameter(MountainName, TerrainOrganicForm.Enabled && _quality.RockDetail);
    }

    // ── frame ───────────────────────────────────────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        var size = (Vector2I)GetViewport().GetVisibleRect().Size;
        if (size != _size && size.X > 0 && size.Y > 0)
        {
            _size = size;
            _renderer.Resize(size.X, size.Y);
            _session?.resize(size.X, size.Y);
        }
        if (_session == null) return;
        double dt = Math.Min(0.1, delta);
        if (_animateClock) _clock += dt;
        if (Orbit) OrbitStreaming();
        else ClampStreamingToMap();
        var readiness = _session.frame(dt, _clock);
        Stage = readiness.stage;
        if (readiness.ready && _readyFrame < 0) _readyFrame = _session.frames;
        ViewReady = readiness.ready;
        var focus = _renderer.CompileToWorld * new Vector3((float)_session.focusX, (float)_session.focusLift, (float)_session.focusY);
        _renderer.SyncLights(_session.presentation, focus);
        UpdateOrbitCamera();
        Present();
        Presented?.Invoke();
    }

    private readonly CameraView _mapStreaming = new();

    /// <summary>
    /// An authored map ends at its border: outside it every cell samples as solid rock, so streaming the camera view
    /// alone would bake an endless rock field around the map. The streamed rectangle is the camera view clipped to the
    /// map (plus a thin margin of the rock that frames it).
    /// </summary>
    private void ClampStreamingToMap()
    {
        var session = _session!;
        if (Source != TerrainSource.Authored || Layout == null) { session.streamingViewOverride = null; return; }
        double margin = Layout.tileSize * 1.5;
        double left = Layout.originX - margin, top = Layout.originY - margin;
        double right = Layout.originX + Layout.width * Layout.tileSize + margin, bottom = Layout.originY + Layout.height * Layout.tileSize + margin;
        var view = session.view;
        bool hasView = view.right > view.left && view.bottom > view.top;
        _mapStreaming.left = hasView ? Math.Max(left, view.left) : left;
        _mapStreaming.right = hasView ? Math.Min(right, view.right) : right;
        _mapStreaming.top = hasView ? Math.Max(top, view.top) : top;
        _mapStreaming.bottom = hasView ? Math.Min(bottom, view.bottom) : bottom;
        if (_mapStreaming.right <= _mapStreaming.left || _mapStreaming.bottom <= _mapStreaming.top)
        {
            double cx = Math.Clamp(session.focusX, left, right), cy = Math.Clamp(session.focusY, top, bottom);
            _mapStreaming.left = cx - 1; _mapStreaming.right = cx + 1; _mapStreaming.top = cy - 1; _mapStreaming.bottom = cy + 1;
        }
        session.streamingViewOverride = _mapStreaming;
        session.streamingDetailZoom = session.zoom;
    }

    private void Present()
    {
        var session = _session!;
        var presentation = session.presentation;
        double[] sceneMatrix = presentation.sceneMatrixWorld.elements;
        _renderer.SetCamera(presentation.cameraProjectionMatrix.elements, sceneMatrix);
        _renderer.SetTerrainVisible(presentation.rootVisible);
        _renderer.SetBackdrop(presentation.backdropMatrixWorld.elements, sceneMatrix, !Orbit);
        var clear = presentation.clearColorInto(_clearColor);
        _renderer.SetClearColor(new Vector3((float)clear.r, (float)clear.g, (float)clear.b));
        _binder.BindTerrain(presentation, _renderer);
        LookOverrides?.Invoke(_renderer);
        _renderer.SyncEnhancements();
    }

    /// <summary>
    /// Applied every frame right after the uniform binder wrote the port's values: the material look sliders override
    /// or scale uniforms here, so the binder never undoes them.
    /// </summary>
    public Action<TerrainSceneRenderer>? LookOverrides;

    // ── picking ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Terrain surface height (world px) under a world point, as the bake draws it.</summary>
    public double SurfaceHeightPx(double worldX, double worldY)
    {
        if (_session == null || !_session.dungeon.active) return 0;
        double level = _session.dungeon.visualSurfaceAt(worldX, worldY);
        return double.IsFinite(level) ? level * TerrainProjection.TERRAIN_ELEVATION_STEP_PX : 0;
    }

    /// <summary>
    /// The terrain point under a screen position: the camera ray is marched in compile space against the visual ground
    /// surface (the same sampler the bake uses), from above the highest terrace down to the abyss.
    /// </summary>
    public bool TryPickGround(Vector2 screen, out double worldX, out double worldY)
    {
        worldX = worldY = 0;
        if (_session == null) return false;
        var camera = ActiveCamera;
        var toCompile = _renderer.CompileToWorld.AffineInverse();
        var origin = toCompile * camera.ProjectRayOrigin(screen);
        var direction = (toCompile.Basis * camera.ProjectRayNormal(screen)).Normalized();
        if (direction.Y > -1e-4f) return false;
        const double top = 30 * 15, bottom = -40 * 15;
        // Start where the ray crosses the top of the terrain band.
        double t = (origin.Y - top) / -direction.Y;
        double tEnd = (origin.Y - bottom) / -direction.Y;
        double step = 10.0; // compile px along the ray
        double prevT = t, prevGap = double.NaN;
        for (int i = 0; i < 4000 && t <= tEnd; i++, t += step)
        {
            double x = origin.X + direction.X * t, y = origin.Y + direction.Y * t, z = origin.Z + direction.Z * t;
            double gap = y - SurfaceHeightPx(x, z);
            if (gap <= 0)
            {
                // Refine between the last point above the ground and this one.
                double lo = double.IsNaN(prevGap) ? t : prevT, hi = t;
                for (int k = 0; k < 12; k++)
                {
                    double mid = (lo + hi) / 2;
                    double mx = origin.X + direction.X * mid, my = origin.Y + direction.Y * mid, mz = origin.Z + direction.Z * mid;
                    if (my - SurfaceHeightPx(mx, mz) > 0) lo = mid; else hi = mid;
                }
                worldX = origin.X + direction.X * hi;
                worldY = origin.Z + direction.Z * hi;
                return true;
            }
            prevT = t;
            prevGap = gap;
        }
        // Nothing hit (e.g. over the abyss): fall back to the ground plane at the focus height.
        double planeT = (origin.Y - _session.focusLift) / -direction.Y;
        worldX = origin.X + direction.X * planeT;
        worldY = origin.Z + direction.Z * planeT;
        return true;
    }

    /// <summary>The screen position of a world point at a height (world px).</summary>
    public Vector2 WorldToScreen(double worldX, double worldY, double heightPx)
    {
        var world = _renderer.CompileToWorld * new Vector3((float)worldX, (float)heightPx, (float)worldY);
        var camera = ActiveCamera;
        return camera.IsPositionBehind(world) ? new Vector2(-99999, -99999) : camera.UnprojectPosition(world);
    }

    public override void _ExitTree()
    {
        _session?.dispose();
        _session = null;
    }
}
