// The terrain world as the original's reference viewer runs it: ClientDungeon streaming, the game Camera and the
// ThreeTerrainLayer port (TerrainPresentationState + TerrainTileManager), driven one animation frame at a time in the
// viewer's exact call order. Engine-free: the Godot runtime supplies the tile sink and the worker threads.
//
// Viewer frame (main.ts `frame()`), reproduced by Frame():
//   lift = dungeon.elevationAt(focus) · STEP (once the chunk exists)  → camera.setFraming / snapTo / forceZoom /
//   applyTo → view = viewBoundsForHeightRangeInto(…) + 64 px margin → dungeon.update(focus, view) → terrain.resetState →
//   syncCamera → beginPostFrame → update(view, dungeon, clock, …, zoom) → presentPostFrame(dt).
// Browser worker messages are delivered between animation frames; here the owner's ClientEventLoop runs first.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class TerrainWorldSessionOptions
{
    /// <summary>Instance id: `world` (the permanent world) or `theme:&lt;biomeKey&gt;`.</summary>
    public string instanceId = "world";
    public double focusX;
    public double focusY;
    /// <summary>Pinned camera zoom; null = CAMERA.baseZoom (the viewer default).</summary>
    public double? zoom;
    /// <summary>Day phase 0..1 (0.5 = noon).</summary>
    public double dayPhase = 0.5;
    public double width = 1600;
    public double height = 900;
    public double resolution = 1;
    /// <summary>`navigator.hardwareConcurrency` / `deviceMemory` (Chrome caps the latter at 8).</summary>
    public double hardwareConcurrency = System.Environment.ProcessorCount;
    public double deviceMemory = 8;
    public ClientDungeonHost? dungeonHost;
    /// <summary>
    /// Interactive runtime: damp the focus lift towards the ground (1/sec, the game's CAMERA.elevationStiffness) instead
    /// of snapping it like the reference viewer, so panning across a terrace does not step the whole picture. 0 = snap.
    /// </summary>
    public double liftStiffness;
    /// <summary>Creates terrain bake worker <c>index</c> (threaded in the runtime, inline in tests).</summary>
    public Func<ClientEventLoop, int, ITerrainBakeWorkerHandle>? workerFactory;
    public ITerrainTileSink? sink;
    /// <summary>
    /// STUDIO: a local authored layout (a painted or generated map) instead of <see cref="instanceId"/>. The session
    /// installs it through <see cref="ClientDungeon.setAuthored"/> and wears the layout's biome. Edits to its arrays are
    /// published with <see cref="TerrainWorldSession.invalidateAuthored"/>; the tile manager re-bakes exactly the tiles
    /// whose content hash changed.
    /// </summary>
    public DungeonLayout? authoredLayout;
}

public sealed class TerrainWorldSession
{
    /// <summary>Forwards ITerrainPresentationHost to the tile manager (created after the presentation state).</summary>
    private sealed class HostProxy : ITerrainPresentationHost
    {
        public TerrainTileManager? target;
        public void resetForBiome(Biome biome, bool deferSourceDisposal, bool preparedBankActivated) => this.target?.resetForBiome(biome, deferSourceDisposal, preparedBankActivated);
        public void biomeTilesetChanged(Biome biome, TerrainTileset tileset) => this.target?.biomeTilesetChanged(biome, tileset);
        public void resetCapturedShadowCasters() => this.target?.resetCapturedShadowCasters();
        public void geometryConfigurationChanged() => this.target?.geometryConfigurationChanged();
        public bool terrainCachePrewarming => this.target?.terrainCachePrewarming ?? false;
        public int activeTileCount => this.target?.activeTileCount ?? 0;
        public bool viewportReady => this.target?.viewportReady ?? false;
        public bool shadowCasterWorkPending() => this.target?.shadowCasterWorkPending() ?? false;
        public bool visibleShadowCastersCaptured() => this.target?.visibleShadowCastersCaptured() ?? true;
        public void captureShadowCasters() => this.target?.captureShadowCasters();
    }

    private sealed class NullSink : ITerrainTileSink
    {
        public object beginUpload(TerrainGeometryPayload geometry) => geometry;
        public bool uploadComplete(object upload) => true;
        public void cancelUpload(object upload) { }
        public void install(TerrainTile tile) { }
        public void setShown(TerrainTile tile, bool shown) { }
        public void setCasterMounted(TerrainTile tile, bool mounted) { }
        public void destroy(TerrainTile tile) { }
    }

    private static readonly double minHeight = TerrainModel.CHASM_ABYSS_SURFACE_Z * TerrainProjection.TERRAIN_ELEVATION_STEP_PX;
    private static readonly double maxHeight =
        (Elevation.MAX_ELEVATION + TerrainRules.TERRAIN_ENDLESS_WALL_MAX_RISE + 4) * TerrainProjection.TERRAIN_ELEVATION_STEP_PX;

    public readonly TerrainWorldSessionOptions options;
    public readonly ClientEventLoop loop;
    public readonly ClientDungeon dungeon;
    public readonly Camera camera;
    public readonly CameraTransform transform = new();
    public readonly TerrainPresentationState presentation;
    public readonly TerrainTileManager tiles;
    public readonly Biome biome;
    private readonly List<ITerrainBakeWorkerHandle> workers = new();
    private readonly DayNightFrame dayNight;

    public double focusX;
    public double focusY;
    public double zoom;
    public double dayPhase;
    public double focusLift;
    private bool liftSettled;
    public double width;
    public double height;
    /// <summary>The last frame's world view (camera bounds for the height range + 64 px margin).</summary>
    public CameraView view = new();
    /// <summary>
    /// Streaming rectangle replacing the camera view (world px), e.g. a square around a third-person player whose
    /// perspective camera is not the terrain camera. The terrain camera and presentation still follow the focus.
    /// </summary>
    public CameraView? streamingViewOverride;
    /// <summary>
    /// Height (world px) the camera frames instead of the ground under the focus — a third-person body that jumps,
    /// glides or flies stays centred. Damped like the ground lift.
    /// </summary>
    public double? focusLiftOverride;
    /// <summary>Zoom used for the geometry detail tier while <see cref="streamingViewOverride"/> is set.</summary>
    public double streamingDetailZoom = 1;
    public int frames;
    private readonly CameraView frameView = new();
    private readonly DungeonViewport frameViewport = new();
    private readonly TerrainCameraSync cameraSync = new();

    public TerrainWorldSession(TerrainWorldSessionOptions options)
    {
        this.options = options;
        this.width = options.width;
        this.height = options.height;
        this.focusX = options.focusX;
        this.focusY = options.focusY;
        this.dayPhase = options.dayPhase;
        this.loop = options.dungeonHost?.loop ?? new ClientEventLoop();
        Device.bindHostWindow(false, false, options.width, options.height);

        var host = new HostProxy();
        this.presentation = new TerrainPresentationState(null, host);
        this.tiles = new TerrainTileManager(
            this.presentation,
            options.sink ?? new NullSink(),
            index =>
            {
                ITerrainBakeWorkerHandle worker = options.workerFactory != null
                    ? options.workerFactory(this.loop, index)
                    : new ThreadedTerrainBakeWorker(this.loop, $"fluitown-terrain-plans-{index + 1}");
                this.workers.Add(worker);
                return worker;
            },
            options.hardwareConcurrency,
            options.deviceMemory);
        host.target = this.tiles;

        // Viewer start-up: configureWebGlCapability(hardware) → init → setWorldYaw → setBiome → setDayNight.
        this.presentation.configureWebGlCapability("hardware");
        this.presentation.init();
        this.presentation.setWorldYaw(Config.CAMERA.worldYaw, 0, 0);
        this.biome = options.authoredLayout is { } authored
            ? Theme.biomeForKey(authored.biomeKey)
            : Theme.resolveBiome(InstanceKind.Hub, options.instanceId);
        this.presentation.setBiome(this.biome, InstanceKind.Hub);

        this.dungeon = new ClientDungeon(options.dungeonHost ?? ClientDungeonHost.threaded(this.loop, options.hardwareConcurrency, options.deviceMemory));
        if (options.authoredLayout is { } layout) this.dungeon.setAuthored(layout);
        else this.dungeon.enter(options.instanceId);

        this.camera = new Camera(Config.CAMERA.worldYaw);
        this.zoom = options.zoom ?? Config.CAMERA.baseZoom;
        this.camera.forceZoom(this.zoom);

        this.dayNight = DayNightCycle.createDayNightFrame();
        this.setDayPhase(options.dayPhase);
    }

    /// <summary>`sampleDayNightCycleInto(frame, InstanceKind.Hub, 0, day)` + `terrain.setDayNight(frame)`.</summary>
    public void setDayPhase(double day)
    {
        this.dayPhase = day;
        DayNightCycle.sampleDayNightCycleInto(this.dayNight, InstanceKind.Hub, 0, day);
        this.presentation.setDayNight(this.dayNight);
    }

    /// <summary>Viewport size in CSS px (window resize).</summary>
    public void resize(double width, double height)
    {
        this.width = width;
        this.height = height;
        Device.setHostViewportSize(width, height);
    }

    /// <summary>
    /// One viewer animation frame. <paramref name="clock"/> is the terrain clock (seconds) passed to update (water,
    /// wind, clouds). Returns the tile manager's viewport readiness.
    /// </summary>
    public TerrainViewportReadiness frame(double dt, double clock)
    {
        FrameSections.begin();
        // Worker messages (chunks, bakes) delivered since the previous frame.
        this.loop.runQueued();
        FrameSections.mark(FrameSections.Messages);

        double sw = this.width;
        double sh = this.height;
        double dpr = this.options.resolution;
        // The focus lift is the ground under the focus once its chunk exists (the game damps towards it).
        double lift = this.focusLiftOverride
            ?? (this.dungeon.active ? this.dungeon.elevationAt(this.focusX, this.focusY) * TerrainProjection.TERRAIN_ELEVATION_STEP_PX : 0);
        if (double.IsFinite(lift))
        {
            if (this.options.liftStiffness > 0 && this.liftSettled)
                this.focusLift += (lift - this.focusLift) * (1 - Math.exp(-this.options.liftStiffness * Math.max(0, dt)));
            else
                this.focusLift = lift;
            if (this.dungeon.active) this.liftSettled = true;
        }
        this.camera.setFraming(sw, sh, false);
        this.camera.snapTo(this.focusX, this.focusY, this.focusLift);
        this.camera.forceZoom(this.zoom);
        this.camera.applyTo(this.transform, sw, sh, dpr);
        // PORT (performance): the view, the dungeon viewport and the camera sync are reused objects — every consumer
        // reads them within the frame and none keeps a reference.
        CameraView view = this.camera.viewBoundsForHeightRangeInto(sw, sh, minHeight, maxHeight, this.frameView);
        const double margin = 64;
        view.left -= margin;
        view.top -= margin;
        view.right += margin;
        view.bottom += margin;
        this.view = view;
        CameraView streaming = this.streamingViewOverride ?? view;
        FrameSections.mark(FrameSections.Camera);
        DungeonViewport viewport = this.frameViewport;
        viewport.left = streaming.left;
        viewport.right = streaming.right;
        viewport.top = streaming.top;
        viewport.bottom = streaming.bottom;
        this.dungeon.update(this.focusX, this.focusY, viewport);
        FrameSections.mark(FrameSections.Dungeon);

        this.presentation.resetState();
        TerrainCameraSync sync = this.cameraSync;
        sync.width = sw;
        sync.height = sh;
        sync.offsetX = this.transform.x;
        sync.offsetY = this.transform.y;
        sync.zoom = this.zoom;
        sync.resolution = dpr;
        sync.worldView = view;
        this.presentation.syncCamera(sync);
        this.presentation.beginPostFrame();
        FrameSections.mark(FrameSections.PresentationBegin);
        this.tiles.update(streaming, this.dungeon, clock, false, false, false, this.streamingViewOverride != null ? this.streamingDetailZoom : this.zoom);
        FrameSections.mark(FrameSections.Tiles);
        this.presentation.presentPostFrame(dt);
        FrameSections.mark(FrameSections.PresentationEnd);
        this.tiles.processRetiringTiles();
        this.frames++;
        var readiness = this.tiles.readiness();
        FrameSections.mark(FrameSections.Retire);
        return readiness;
    }

    /// <summary>
    /// STUDIO: the authored layout's arrays were edited in place (a paint stroke, a generator reveal). Bumps the chunk
    /// version so every tile re-hashes its region; only tiles whose cells changed are re-baked, the old tile stays on
    /// screen until its replacement is ready.
    /// </summary>
    public void invalidateAuthored() => this.dungeon.invalidateAuthored();

    /// <summary>STUDIO: re-plan the authored world's ambient life (fish, birds) once the edits have settled.</summary>
    public void refreshAuthoredAmbientPlan() => this.dungeon.refreshAuthoredAmbientPlan();

    /// <summary>Stop workers and release every tile.</summary>
    public void dispose()
    {
        this.tiles.dispose();
        foreach (var worker in this.workers) worker.terminate();
        this.workers.Clear();
        // STUDIO: sessions come and go; the dungeon's own workers must stop with them (ClientDungeon.shutdown).
        this.dungeon.shutdown();
    }
}

/// <summary>
/// PORT ADDITION: main-thread time and managed allocations per section of <see cref="TerrainWorldSession.frame"/> — the
/// frame budget's view into the port. Two timestamp reads per section, always on; main thread only.
/// </summary>
public static class FrameSections
{
    public const int Messages = 0, Camera = 1, Dungeon = 2, PresentationBegin = 3, Tiles = 4, PresentationEnd = 5, Retire = 6, Count = 7;
    private static readonly long[] ticks = new long[Count];
    private static readonly long[] bytes = new long[Count];
    private static long lastTicks, lastBytes;

    public static void begin()
    {
        lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        lastBytes = GC.GetAllocatedBytesForCurrentThread();
    }

    public static void mark(int section)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        ticks[section] += now - lastTicks;
        bytes[section] += allocated - lastBytes;
        lastTicks = now;
        lastBytes = allocated;
    }
}
