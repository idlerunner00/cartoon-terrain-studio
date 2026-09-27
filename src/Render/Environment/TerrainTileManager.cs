// Port of packages/client/src/render/environment/threeTerrain.ts (class ThreeTerrainLayer) — the TILE half: tile
// records, residency (`update()` tile selection, prefetch, LOD detail, install budget, show/hide, eviction,
// retirement), worker planning (`requestTerrainPlan`, deferral, pruning, worker assignment, stall recovery),
// `acceptTerrainPlan`, prepared-plan installation and the shadow-caster bookkeeping. Keep in lockstep with the
// original. The presentation half is TerrainPresentationState (uniforms, lights, shadow camera, backdrop, render());
// this class implements its ITerrainPresentationHost and hands over at the same points the original does.
//
// PORT NOTES
// * Literal port of the paths the Godot runtime executes: streamed/endless and finite worlds with worker threads.
//   Not ported (browser-only, with the original's behaviour when the feature is absent):
//   - persistent IndexedDB geometry cache (`persistentTerrainCacheKey` → undefined),
//   - the cooperative Hub compiler (only used when `typeof Worker === 'undefined'`; .NET always has threads),
//   - covered Aether transitions / transfer preparation / external prewarm (not part of the terrain system),
//   - the synchronous emergency `bakeTile` (only reached when every worker failed; here a failed pool is
//     restarted after the original's retry back-off instead),
//   - GPU upload slicing: `drainPreparedTerrainGpuUploads` becomes ITerrainTileSink.beginUpload (the Godot layer
//     builds meshes on a worker thread); `preparedTerrainGpuReady` asks the sink whether that finished.
// * Workers: ITerrainBakeWorkerHandle (TerrainBakeWorkerHost.cs); responses arrive on the ClientEventLoop that the
//   runtime drains at the start of each frame, exactly like browser worker messages.
// * three.js groups → TerrainTile flags (`shown` = group mounted in activeTileRoot, `casterMounted` = actorWalls
//   mounted in activeActorWallTileRoot) plus ITerrainTileSink calls that mirror the mount/unmount.
// * Main-thread state only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.ThreeTerrain;
using static Fluitown.Render.TerrainTileLattice;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>`TerrainBake`: one resident bake tile.</summary>
public sealed class TerrainTile
{
    public int tx;
    public int ty;
    public string key = "";
    public TerrainBakeFrame frame = null!;
    public TerrainBakeAudit audit = null!;
    public double contentHash;
    /// <summary>Screen-space geometry tier used to compile this immutable payload.</summary>
    public string detail = "near";
    /// <summary>Dungeon loadedVersion for which contentHash was last proved exact.</summary>
    public int contentVersion;
    public int used;
    /// <summary>`group.parent === activeTileRoot`.</summary>
    public bool shown;
    /// <summary>`actorWalls.parent === activeActorWallTileRoot`.</summary>
    public bool casterMounted;
    /// <summary>`actorWalls.children.length > 0`.</summary>
    public bool hasCasters;
    /// <summary>Actual immutable attribute/index residency.</summary>
    public double? byteSize;
    /// <summary>Return detached stores to the worker that owns the matching exact-size pool.</summary>
    public ITerrainBakeWorkerHandle? recycleWorker;
    public TerrainGeometryPayload geometry = null!;
    /// <summary>The Godot layer's GPU data for this tile (from ITerrainTileSink.beginUpload).</summary>
    public object? gpu;
}

/// <summary>`TerrainViewportReadiness`.</summary>
public sealed class TerrainViewportReadiness
{
    public string stage = "inactive";
    public bool ready = true;
    public int loadedChunks;
    public int requiredChunks;
    public int readyTiles;
    public int requiredTiles;
}

/// <summary>What the Godot layer does for the tile manager (the three.js scene-graph side of the original).</summary>
public interface ITerrainTileSink
{
    /// <summary>Start preparing GPU resources for a worker payload (may run asynchronously; thread choice is the sink's).</summary>
    object beginUpload(TerrainGeometryPayload geometry);
    /// <summary>Whether <see cref="beginUpload"/>'s work has finished.</summary>
    bool uploadComplete(object upload);
    /// <summary>A prepared plan was discarded before installation.</summary>
    void cancelUpload(object upload);
    /// <summary>A tile record takes ownership of its uploaded resources (nodes are created unmounted).</summary>
    void install(TerrainTile tile);
    /// <summary>Mount/unmount the colour lanes (`activeTileRoot.add/remove(group)`).</summary>
    void setShown(TerrainTile tile, bool shown);
    /// <summary>Mount/unmount the shadow-caster lane (`activeActorWallTileRoot.add/remove(actorWalls)`).</summary>
    void setCasterMounted(TerrainTile tile, bool mounted);
    /// <summary>Dispose the tile's resources.</summary>
    void destroy(TerrainTile tile);
}

/// <summary>
/// Optional: the sink builds meshes of its own from a tile's geometry at <see cref="ITerrainTileSink.install"/> (the
/// Godot renderer). The manager then returns the payload's stores to the worker pool right after installation
/// instead of holding every installed tile's arrays until the tile is destroyed.
/// </summary>
public interface ITerrainTileCopyingSink
{
}

/// <summary>Optional publication barrier: keep the previous tile visible and solid until its replacement is complete.</summary>
public interface ITerrainTilePublicationSink
{
    bool readyToPublish(TerrainTile tile);
    void publish(TerrainTile tile);
}

public sealed partial class TerrainTileManager : ITerrainPresentationHost
{
    private sealed class PendingTerrainPlan
    {
        public int id;
        public int serial;
        public int revision;
        public string key = "";
        public double contentHash;
        public double structuralHash;
        public string detail = "near";
        public double effectSeed;
        public TerrainBakeFrame frame = null!;
        public double requestedAt;
        public double sampleMs;
        public int priority;
        public int requestedFrame;
        /// <summary>The worker had not completed its ready handshake when this request was posted.</summary>
        public bool requestedBeforeWorkerReady;
        /// <summary>Cancellation must return to the worker whose keyed queue owns this request.</summary>
        public ITerrainBakeWorkerHandle worker = null!;
    }

    private sealed class PreparedTerrainPlan
    {
        public TerrainBakeWorkerCompletedResponse response = null!;
        public double sampleMs;
        public double latencyMs;
        public double transferMs;
        public double readyAt;
        public int priority;
        public ITerrainBakeWorkerHandle? worker;
        public string detail = "near";
        public bool gpuUploadQueued;
        public bool gpuUploadCancelled;
        public bool gpuUploadPayloadReleased;
        public object? gpuUpload;
        public TerrainTile? gpuBake;
    }

    private sealed class DeferredTerrainPlanRequest
    {
        public int serial;
        public ClientDungeon cd = null!;
        public string key = "";
        public TerrainBakeFrame frame = null!;
        public int tx;
        public int ty;
        public double contentHash;
        public double effectSeed;
        public string detail = "near";
        public int priority;
        public Biome? visualBiome;
        public TerrainTileset? visualTileset;
        public double requestedAt;
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double nowMs() => Clock.Elapsed.TotalMilliseconds;

    private readonly TerrainPresentationState presentation;
    private readonly ITerrainTileSink sink;
    private readonly Func<int, ITerrainBakeWorkerHandle> workerFactory;
    private readonly double hardwareConcurrency;
    private readonly double deviceMemory;
    private readonly bool isMobile;

    private readonly JsMap<string, TerrainTile> tiles = new();
    private readonly JsMap<string, PendingTerrainPlan> pendingTerrainPlans = new();
    private readonly JsMap<string, DeferredTerrainPlanRequest> deferredTerrainPlans = new();
    private readonly JsMap<string, PreparedTerrainPlan> preparedTerrainPlans = new();
    private readonly List<ITerrainBakeWorkerHandle> terrainPlanWorkers = new();
    private readonly HashSet<ITerrainBakeWorkerHandle> readyTerrainPlanWorkers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ITerrainBakeWorkerHandle> terrainPlanWorkerAffinity = new();
    private readonly TerrainSampleBufferPool terrainSampleBuffers = new(false, 64 * 1024, 8);
    private readonly Queue<TerrainTile> retiringTiles = new();
    // A cancelled upload still reads its pooled payload. Retain both until the worker finishes;
    // waiting synchronously here previously stalled zoom/streaming for an entire mesh build.
    private readonly List<PreparedTerrainPlan> retiringUploads = new();
    private HashSet<TerrainTile> terrainShadowCapturedCasters = new(ReferenceEqualityComparer.Instance);

    private string terrainGeometryDetail = "near";
    private int terrainPlanSerial = 0;
    private int terrainPlanRequestId = 0;
    private int terrainPlanRevision = 0;
    private int frameCounter = 0;
    private double terrainInstallEmaMs = 0;
    private TerrainViewportReadiness viewportReadiness = new() { stage = "inactive", ready = true };
    private double terrainStreamCenterX = double.NaN;
    private double terrainStreamCenterY = double.NaN;
    private int terrainRapidTraversalFrames = 0;
    private bool hubReturnPrefetchSuppressed = false;
    private double hubReturnPrefetchAnchorX = double.NaN;
    private double hubReturnPrefetchAnchorY = double.NaN;
    private DungeonLayout? terrainResidencyAnchor;
    private int terrainResidencyVersion = -1;
    private double terrainResidencyTxMin = double.NaN;
    private double terrainResidencyTxMax = double.NaN;
    private double terrainResidencyTyMin = double.NaN;
    private double terrainResidencyTyMax = double.NaN;
    private bool terrainResidencyCovered = false;
    private bool terrainResidencyPrefetchSuppressed = false;
    private bool terrainResidencyHubReturnSuppressed = false;
    private bool terrainPlanWorkerExecutionFailed = false;
    private bool terrainPlanWorkerUnavailable = false;
    private double terrainPlanWorkerRetryAt = 0;
    private int terrainPlanWorkerFailures = 0;
    private byte[] discardBuf = Array.Empty<byte>();
    private string terrainCompilerThemeCatalogKey = "";
    private IReadOnlyList<TerrainCompilerThemeVisual> terrainCompilerThemeCatalog = Array.Empty<TerrainCompilerThemeVisual>();

    // Diagnostics the original keeps as counters.
    public int terrainPlanSuperseded, terrainPlanBackpressure, terrainPlanStaleResults, terrainPlanCancelled, terrainPlanAccepted,
        terrainPreparedBakes, terrainResidencySkips, terrainVisibleCoverageMisses, terrainPlanRequests;
    public double terrainWorkerMaterializeMs, terrainWorkerPlanMs, terrainWorkerBakeMs, terrainWorkerMaxLatencyMs;

    public TerrainTileManager(
        TerrainPresentationState presentation,
        ITerrainTileSink sink,
        Func<int, ITerrainBakeWorkerHandle> workerFactory,
        double hardwareConcurrency,
        double deviceMemory = 8,
        bool isMobile = false)
    {
        this.presentation = presentation;
        this.sink = sink;
        this.workerFactory = workerFactory;
        this.hardwareConcurrency = hardwareConcurrency;
        this.deviceMemory = deviceMemory;
        this.isMobile = isMobile;
    }

    /* ── ITerrainPresentationHost ───────────────────────────────────────────────────────────────────────── */

    public void resetForBiome(Biome biome, bool deferSourceDisposal, bool preparedBankActivated)
    {
        if (!preparedBankActivated || biome.key != "hub")
        {
            this.hubReturnPrefetchSuppressed = false;
            this.hubReturnPrefetchAnchorX = double.NaN;
            this.hubReturnPrefetchAnchorY = double.NaN;
        }
        this.resetTerrainPlanner();
        this.viewportReadiness = new TerrainViewportReadiness { stage = "inactive", ready = false };
        if (!preparedBankActivated)
        {
            if (deferSourceDisposal) this.retireAllTiles();
            else this.evictAllTiles();
        }
    }

    // The worker owns its own resolver (`createRunTerrainMaterialResolver` in processNext); the main thread only
    // needs it for the synchronous fallback bake, which is not ported (see header).
    public void biomeTilesetChanged(Biome biome, TerrainTileset tileset) { }

    public void resetCapturedShadowCasters() => this.terrainShadowCapturedCasters = new HashSet<TerrainTile>(ReferenceEqualityComparer.Instance);

    // Resets the main-thread compiler key of the synchronous fallback; nothing to do without that fallback.
    public void geometryConfigurationChanged() { }

    public bool terrainCachePrewarming => false;

    public int activeTileCount
    {
        get
        {
            // Read every frame by the shadow snapshot gate: a plain loop, no LINQ iterator.
            int count = 0;
            foreach (var t in this.tiles.values())
                if (t.shown) count++;
            return count;
        }
    }

    public bool viewportReady => this.viewportReadiness.ready;

    public TerrainViewportReadiness readiness() => this.viewportReadiness;

    /// <summary>Whether an active-world compiler can still change the pending caster snapshot.</summary>
    public bool shadowCasterWorkPending()
    {
        bool affects(TerrainBakeFrame frame) =>
            terrainBakeIntersectsShadowWindow(
                frame,
                this.presentation.lastShadowCx,
                this.presentation.lastShadowCz,
                this.presentation.lastShadowRadius + SHADOW_CASTER_REACH);
        foreach (var (key, pending) in this.pendingTerrainPlans)
            if (!key.Contains('\0') && affects(pending.frame)) return true;
        foreach (var (key, deferred) in this.deferredTerrainPlans)
            if (!key.Contains('\0') && affects(deferred.frame)) return true;
        foreach (var (key, prepared) in this.preparedTerrainPlans)
            if (!key.Contains('\0') && affects(prepared.response.frame)) return true;
        return false;
    }

    /// <summary>Every shadow-proxy-bearing tile in the exact camera rectangle must occur in the latest depth snapshot.</summary>
    public bool visibleShadowCastersCaptured()
    {
        // Non-world/unit harness states carry no viewport rectangle and therefore no caster obligation.
        if (this.viewportReadiness.requiredTiles == 0) return true;
        if (
            !this.viewportReadiness.ready ||
            !double.IsFinite(this.terrainResidencyTxMin) ||
            !double.IsFinite(this.terrainResidencyTxMax) ||
            !double.IsFinite(this.terrainResidencyTyMin) ||
            !double.IsFinite(this.terrainResidencyTyMax))
            return false;
        foreach (var tile in this.tiles.values())
        {
            if (
                tile.tx < this.terrainResidencyTxMin ||
                tile.tx > this.terrainResidencyTxMax ||
                tile.ty < this.terrainResidencyTyMin ||
                tile.ty > this.terrainResidencyTyMax ||
                !tile.hasCasters)
                continue;
            if (!tile.casterMounted || !this.terrainShadowCapturedCasters.Contains(tile)) return false;
        }
        return true;
    }

    /// <summary>refreshTerrainShadowMap: the mounted, shadow-relevant caster groups are now in the snapshot.</summary>
    public void captureShadowCasters()
    {
        var capturedCasters = new HashSet<TerrainTile>(ReferenceEqualityComparer.Instance);
        foreach (var tile in this.tiles.values())
            if (tile.casterMounted && this.tileAffectsCurrentShadow(tile)) capturedCasters.Add(tile);
        this.terrainShadowCapturedCasters = capturedCasters;
    }

    /* ── per frame ──────────────────────────────────────────────────────────────────────────────────────── */

    public int lastPrefetchPad { get; private set; } = -1;

    /// <summary>
    /// `update(view, state, nowSec, coveredTransition, suppressSpeculativePrefetch, requireShadowPrefetch, viewZoom)`:
    /// the tile half, then the presentation tail (`updateBackdrop`, `waterTime`, `render()`).
    /// </summary>
    public void update(
        CameraView view,
        ClientDungeon cd,
        double nowSec = 0,
        bool coveredTransition = false,
        bool? suppressSpeculativePrefetchArg = null,
        bool requireShadowPrefetch = false,
        double viewZoom = 1)
    {
        bool suppressSpeculativePrefetch = suppressSpeculativePrefetchArg ?? coveredTransition;
        Biome? biome = this.presentation.currentBiome;
        TerrainTileset? tileset = this.presentation.currentTileset;
        DungeonLayout? anchor = cd.loaded.Count > 0 ? cd.loaded[0] : null;
        if (!cd.active || biome == null || tileset == null || anchor == null)
        {
            // A transiently empty chunk list on a still-active dungeon must not evict the presented world. Keep the
            // immutable tile bank moving with the camera over the biome backdrop until exact destination chunks arrive.
            if (cd.active && this.tiles.size > 0)
            {
                this.viewportReadiness = new TerrainViewportReadiness { stage = "chunks", ready = false, requiredChunks = 1 };
                this.presentation.presentFrame(view, Js.Truthy(this.presentation.ts) ? this.presentation.ts : Grid.TILE_SIZE, nowSec);
                return;
            }
            this.presentation.root.visible = false;
            this.viewportReadiness = new TerrainViewportReadiness { stage = "inactive", ready = !cd.active };
            this.terrainStreamCenterX = double.NaN;
            this.terrainStreamCenterY = double.NaN;
            if (this.pendingTerrainPlans.size > 0 || this.preparedTerrainPlans.size > 0) this.resetTerrainPlanner();
            this.evictAllTiles();
            this.presentation.render();
            return;
        }

        double ts = Js.Truthy(anchor.tileSize) ? anchor.tileSize : Grid.TILE_SIZE;
        this.presentation.ts = ts;
        this.setTerrainGeometryDetail(
            TerrainGeometryDetailModule.terrainGeometryDetailForProjectedCell(ts * viewZoom, this.terrainGeometryDetail));
        double originX = cd.gridOriginX;
        double originY = cd.gridOriginY;
        this.frameCounter++;

        double tileSpan = TILE_CELLS * ts;
        int txMin = (int)Math.floor((view.left - originX) / tileSpan);
        int txMax = (int)Math.floor((view.right - originX) / tileSpan);
        int tyMin = (int)Math.floor((view.top - originY) / tileSpan);
        int tyMax = (int)Math.floor((view.bottom - originY) / tileSpan);
        int requiredTiles = (txMax - txMin + 1) * (tyMax - tyMin + 1);
        EndlessTerrainCoverage chunkCoverage = cd.coverageForBounds(
            originX + (txMin * TILE_CELLS - TILE_BORDER + 0.5) * ts,
            originY + (tyMin * TILE_CELLS - TILE_BORDER + 0.5) * ts,
            originX + ((txMax + 1) * TILE_CELLS + TILE_BORDER - 0.5) * ts,
            originY + ((tyMax + 1) * TILE_CELLS + TILE_BORDER - 0.5) * ts);
        // Do not return on partial rectangle coverage. Each independently complete tile can already be sampled and
        // compiled while the remaining chunks are in flight; the per-tile coverage check below prevents a missing
        // neighbour from ever being baked as the collision-solid fallback.
        this.presentation.root.visible = !coveredTransition;
        // PORT FIX: a prepared plan keeps the priority it was requested with. After a jump of the view (a large map
        // framed whole, then a close-up of it) the ready buffer could fill with visible-priority plans of tiles that
        // are no longer visible; the visible tiles' results can never displace those, so nothing installed again.
        // A prepared plan off the view therefore ranks as speculative prefetch.
        foreach (var prepared in this.preparedTerrainPlans.values())
        {
            int tx = prepared.response.tx, ty = prepared.response.ty;
            if (tx < txMin || tx > txMax || ty < tyMin || ty > tyMax) prepared.priority = System.Math.Max(prepared.priority, 2);
        }
        if (this.preparedTerrainPlans.size > TILE_PLAN_READY_LIMIT || this.pendingTerrainPlans.size > TILE_PLAN_PENDING_LIMIT)
            this.pruneTerrainPlansOutside(txMin - TILE_EVICT_PAD, txMax + TILE_EVICT_PAD, tyMin - TILE_EVICT_PAD, tyMax + TILE_EVICT_PAD);
        double streamCenterX = (view.left + view.right) * 0.5;
        double streamCenterY = (view.top + view.bottom) * 0.5;
        double streamDx = double.IsFinite(this.terrainStreamCenterX) ? streamCenterX - this.terrainStreamCenterX : 0;
        double streamDy = double.IsFinite(this.terrainStreamCenterY) ? streamCenterY - this.terrainStreamCenterY : 0;
        this.terrainStreamCenterX = streamCenterX;
        this.terrainStreamCenterY = streamCenterY;
        // A dash/warp that crosses half a compiled tile per display frame makes two speculative rings obsolete
        // before a worker can return them. Prioritise the complete visible rectangle and a one-tile guard only.
        bool rapidTraversal = Math.max(Math.abs(streamDx), Math.abs(streamDy)) >= tileSpan * 0.5;
        if (rapidTraversal)
        {
            this.terrainRapidTraversalFrames++;
            this.pruneTerrainPlansOutside(txMin - 1, txMax + 1, tyMin - 1, tyMax + 1);
        }
        if (biome.key == "hub" && this.hubReturnPrefetchSuppressed)
        {
            if (suppressSpeculativePrefetch || !double.IsFinite(this.hubReturnPrefetchAnchorX) || !double.IsFinite(this.hubReturnPrefetchAnchorY))
            {
                this.hubReturnPrefetchAnchorX = streamCenterX;
                this.hubReturnPrefetchAnchorY = streamCenterY;
            }
            else if (Math.hypot(streamCenterX - this.hubReturnPrefetchAnchorX, streamCenterY - this.hubReturnPrefetchAnchorY) >= ts * 2)
            {
                this.hubReturnPrefetchSuppressed = false;
            }
        }

        bool residencyUnchanged =
            this.tiles.size > 0 &&
            this.viewportReadiness.ready &&
            this.preparedTerrainPlans.size == 0 &&
            ReferenceEquals(this.terrainResidencyAnchor, anchor) &&
            this.terrainResidencyVersion == cd.loadedVersion &&
            this.terrainResidencyTxMin == txMin &&
            this.terrainResidencyTxMax == txMax &&
            this.terrainResidencyTyMin == tyMin &&
            this.terrainResidencyTyMax == tyMax &&
            this.terrainResidencyCovered == coveredTransition &&
            this.terrainResidencyPrefetchSuppressed == suppressSpeculativePrefetch &&
            this.terrainResidencyHubReturnSuppressed == this.hubReturnPrefetchSuppressed;
        if (residencyUnchanged)
        {
            this.terrainResidencySkips++;
            this.presentation.presentFrame(view, ts, nowSec);
            return;
        }
        this.terrainResidencyAnchor = anchor;
        this.terrainResidencyVersion = cd.loadedVersion;
        this.terrainResidencyTxMin = txMin;
        this.terrainResidencyTxMax = txMax;
        this.terrainResidencyTyMin = tyMin;
        this.terrainResidencyTyMax = tyMax;
        this.terrainResidencyCovered = coveredTransition;
        this.terrainResidencyPrefetchSuppressed = suppressSpeculativePrefetch;
        this.terrainResidencyHubReturnSuppressed = this.hubReturnPrefetchSuppressed;

        int bakes = 0;
        double installFrameStarted = nowMs();
        int planRequests = 0;
        bool coldFill = this.tiles.size == 0;
        bool hubTerrain = biome.key == "hub";
        // .NET always has worker threads: the browser's Hub-only lazy pool and the cooperative fallback collapse
        // into the one pool (`typeof Worker !== 'undefined'`).
        IReadOnlyList<ITerrainBakeWorkerHandle> terrainWorkers = this.ensureTerrainPlanWorkers();
        int pendingLimit = System.Math.Min(
            TILE_PLAN_PENDING_LIMIT,
            System.Math.Max(TILE_PLAN_PENDING_PER_WORKER, terrainWorkers.Count * TILE_PLAN_PENDING_PER_WORKER));
        int planRequestLimit = coveredTransition
            ? System.Math.Max(
                TILE_COVERED_TRANSITION_PLAN_REQUESTS_PER_WORKER_PER_FRAME,
                terrainWorkers.Count * TILE_COVERED_TRANSITION_PLAN_REQUESTS_PER_WORKER_PER_FRAME)
            : coldFill
                ? pendingLimit
                : System.Math.Max(
                    TILE_PLAN_REQUESTS_PER_WORKER_PER_FRAME,
                    terrainWorkers.Count * TILE_PLAN_REQUESTS_PER_WORKER_PER_FRAME);
        bool canPrepareTerrain = terrainWorkers.Count > 0;
        // A worker which accepted work but stayed silent beyond both a frame and wall-clock budget: restart the pool.
        if (canPrepareTerrain && (coveredTransition || (coldFill && cd.isStreamed)) && this.pendingTerrainPlans.size > 0)
        {
            double planNow = nowMs();
            foreach (var pending in this.pendingTerrainPlans.values().ToList())
            {
                int pendingTx = (int)Math.floor((double)(pending.frame.i0 + TILE_BORDER) / TILE_CELLS);
                int pendingTy = (int)Math.floor((double)(pending.frame.j0 + TILE_BORDER) / TILE_CELLS);
                if (pendingTx < txMin || pendingTx > txMax || pendingTy < tyMin || pendingTy > tyMax) continue;
                if (this.terrainPlanWithinStallBudget(pending, planNow)) continue;
                this.disableTerrainPlanWorker(pending.worker);
                canPrepareTerrain = false;
                break;
            }
        }
        bool stagedTerrain = canPrepareTerrain || cd.isStreamed || hubTerrain;
        bool ensure(int tx, int ty, bool force, bool priority)
        {
            string key = tileKey(tx, ty);
            TerrainTile? t = this.tiles.get(key);
            // Once this exact tile has been proved for the current epoch, an unrelated prepared-tile completion must
            // not rescan its coverage and border signature.
            if (t != null && t.contentVersion == cd.loadedVersion && t.detail == this.terrainGeometryDetail)
            {
                t.used = this.frameCounter;
                return true;
            }
            TerrainBakeFrame frame = this.terrainBakeFrame(tx, ty, ts, originX, originY);
            if (cd.isStreamed)
            {
                EndlessTerrainCoverage tileCoverage = cd.coverageForBounds(
                    originX + (frame.i0 + 0.5) * ts,
                    originY + (frame.j0 + 0.5) * ts,
                    originX + (frame.i0 + frame.width - 0.5) * ts,
                    originY + (frame.j0 + frame.height - 0.5) * ts);
                if (!tileCoverage.complete)
                {
                    if (t != null) t.used = this.frameCounter;
                    return false;
                }
            }
            // Content signature over the SAME region the bake samples — own cells plus the TILE_BORDER of neighbour
            // context, so a chunk streaming in under that border flips the signature too.
            double hash = TerrainGeometryDetailModule.terrainContentHashForDetail(
                cd.chunkSignatureForBounds(
                    originX + (tx * TILE_CELLS - TILE_BORDER) * ts,
                    originY + (ty * TILE_CELLS - TILE_BORDER) * ts,
                    originX + (tx * TILE_CELLS + TILE_CELLS + TILE_BORDER) * ts,
                    originY + (ty * TILE_CELLS + TILE_CELLS + TILE_BORDER) * ts),
                this.terrainGeometryDetail);
            bool stale = t != null && t.contentHash != hash;
            if (t == null || stale)
            {
                double effectSeed = cd.effectSeedForRegion(frame.i0, frame.j0);
                PreparedTerrainPlan? prepared = this.preparedTerrainPlanFor(key, frame, hash, effectSeed);
                int ordinaryInstallBudget = coveredTransition
                    ? TILE_COVERED_TRANSITION_BAKES_PER_FRAME
                    : coldFill
                        ? TILE_COLD_BAKES_PER_FRAME
                        : force
                            ? TILE_VISIBLE_BAKES_PER_FRAME
                            : TILE_BAKES_PER_FRAME;
                int installBudget =
                    ordinaryInstallBudget == 1 &&
                    canPrepareTerrain &&
                    !this.isMobile &&
                    !this.presentation.softwareExecution &&
                    this.terrainInstallEmaMs <= TILE_INSTALL_EMA_BUDGET_MS
                        ? TILE_ADAPTIVE_INSTALL_MAX
                        : ordinaryInstallBudget;
                bool insideInstallFrameBudget = bakes == 0 || nowMs() - installFrameStarted < TILE_INSTALL_FRAME_BUDGET_MS;
                if (prepared != null && this.preparedTerrainGpuReady(prepared) && bakes < installBudget && insideInstallFrameBudget)
                {
                    TerrainTile replacement = this.takePreparedTerrainTile(prepared, force);
                    replacement.contentVersion = cd.loadedVersion;
                    this.preparedTerrainPlans.delete(key);
                    if (t != null) this.queueRetiringTile(t);
                    t = replacement;
                    this.tiles.set(key, replacement);
                    bakes++;
                }
                else if (canPrepareTerrain)
                {
                    if (planRequests < planRequestLimit)
                    {
                        int workerPriority = force ? 0 : priority ? 1 : 2;
                        if (this.requestTerrainPlan(cd, key, frame, tx, ty, hash, effectSeed, workerPriority, this.terrainGeometryDetail))
                            planRequests++;
                    }
                    // During traversal an old tile remains valid visual coverage until its exact replacement is ready.
                    if (stagedTerrain || !coldFill || t != null)
                    {
                        if (t != null) t.used = this.frameCounter;
                        return t != null && t.contentHash == hash;
                    }
                }
                if (t != null && t.contentHash == hash)
                {
                    t.used = this.frameCounter;
                    return true;
                }
                // The original's remaining branches end in the synchronous emergency bake (not ported): the old
                // tile — if any — stays as coverage and the next frame retries.
                if (t != null) t.used = this.frameCounter;
                return false;
            }
            t!.contentVersion = cd.loadedVersion;
            t.used = this.frameCounter;
            return t.contentHash == hash;
        }

        // The bake budget applies only to speculative off-screen warming. Every tile intersecting the current viewport
        // is a hard coverage requirement and must exist before the shadow map and colour pass render.
        int visibleCoverageMisses = 0;
        for (int ty = tyMin; ty <= tyMax; ty++)
            for (int tx = txMin; tx <= txMax; tx++)
                if (!ensure(tx, ty, true, true)) visibleCoverageMisses++;
        this.terrainVisibleCoverageMisses += visibleCoverageMisses;
        {
            bool ready = chunkCoverage.complete && visibleCoverageMisses == 0;
            this.presentation.root.visible = coveredTransition ? ready : true;
            this.viewportReadiness = new TerrainViewportReadiness
            {
                stage = ready ? "ready" : chunkCoverage.complete ? "bakes" : "chunks",
                ready = ready,
                loadedChunks = chunkCoverage.loaded,
                requiredChunks = chunkCoverage.required,
                readyTiles = requiredTiles - visibleCoverageMisses,
                requiredTiles = requiredTiles,
            };
        }
        double prefetchResidentBytes = 0;
        foreach (var tile in this.tiles.values()) prefetchResidentBytes += this.terrainBakeByteSize(tile);
        int prefetchPad = terrainPrefetchPad(
            streamDx != 0 || streamDy != 0,
            this.pendingTerrainPlans.size + this.deferredTerrainPlans.size,
            this.terrainPlanWorkers.Count,
            Math.max((double)this.tiles.size / MAX_CACHED_TILES, prefetchResidentBytes / this.liveTerrainCacheByteBudget()),
            this.isMobile);
        this.lastPrefetchPad = prefetchPad;
        if (
            prefetchPad > 0 &&
            !this.presentation.softwareExecution &&
            !rapidTraversal &&
            !suppressSpeculativePrefetch &&
            !this.hubReturnPrefetchSuppressed)
        {
            // Fill the leading edge first; planning is independent of the one-tile installation budget.
            if (Math.abs(streamDx) >= Math.abs(streamDy) && streamDx != 0)
            {
                for (int ring = 1; ring <= prefetchPad; ring++)
                {
                    int tx = streamDx > 0 ? txMax + ring : txMin - ring;
                    for (int ty = tyMin - ring; ty <= tyMax + ring; ty++) ensure(tx, ty, false, true);
                }
            }
            else if (streamDy != 0)
            {
                for (int ring = 1; ring <= prefetchPad; ring++)
                {
                    int ty = streamDy > 0 ? tyMax + ring : tyMin - ring;
                    for (int tx = txMin - ring; tx <= txMax + ring; tx++) ensure(tx, ty, false, true);
                }
            }
            for (int ring = 1; ring <= prefetchPad; ring++)
            {
                for (int ty = tyMin - ring; ty <= tyMax + ring; ty++)
                {
                    for (int tx = txMin - ring; tx <= txMax + ring; tx++)
                    {
                        bool onRing = tx == txMin - ring || tx == txMax + ring || ty == tyMin - ring || ty == tyMax + ring;
                        if (onRing) ensure(tx, ty, false, false);
                    }
                }
            }
        }

        foreach (var t in this.tiles.values())
        {
            bool inViewport = t.tx >= txMin && t.tx <= txMax && t.ty >= tyMin && t.ty <= tyMax;
            bool inShow =
                t.tx >= txMin - TILE_SHOW_PAD &&
                t.tx <= txMax + TILE_SHOW_PAD &&
                t.ty >= tyMin - TILE_SHOW_PAD &&
                t.ty <= tyMax + TILE_SHOW_PAD;
            if (inShow && !t.shown) this.showTile(t);
            // Retain the last immutable bank until the destination rectangle is complete.
            else if (!inShow && t.shown && chunkCoverage.complete) this.hideTile(t);
            // A cached wall may project a long shadow into the view even when its colour tile is hidden: keep the
            // compact proxy mounted for the cache lifetime.
            this.showShadowTile(t, inViewport);
        }
        foreach (var (key, t) in this.tiles)
        {
            if (
                chunkCoverage.complete &&
                (t.tx < txMin - TILE_EVICT_PAD ||
                    t.tx > txMax + TILE_EVICT_PAD ||
                    t.ty < tyMin - TILE_EVICT_PAD ||
                    t.ty > tyMax + TILE_EVICT_PAD))
            {
                this.queueRetiringTile(t);
                this.tiles.delete(key);
            }
        }
        double cachedTerrainBytes = 0;
        foreach (var tile in this.tiles.values()) cachedTerrainBytes += this.terrainBakeByteSize(tile);
        double cacheByteBudget = this.liveTerrainCacheByteBudget();
        if (this.tiles.size > MAX_CACHED_TILES || cachedTerrainBytes > cacheByteBudget)
        {
            // Normal traversal exceeds the cap by one speculative tile at a time: select that one victim in place.
            while (this.tiles.size > MAX_CACHED_TILES || cachedTerrainBytes > cacheByteBudget)
            {
                TerrainTile? oldest = null;
                foreach (var tile in this.tiles.values())
                {
                    if (tile.shown || (oldest != null && tile.used >= oldest.used)) continue;
                    oldest = tile;
                }
                if (oldest == null) break;
                cachedTerrainBytes -= this.terrainBakeByteSize(oldest);
                this.queueRetiringTile(oldest);
                this.tiles.delete(oldest.key);
            }
        }

        this.presentation.presentFrame(view, ts, nowSec);
    }

    /// <summary>
    /// The browser's `requestIdleCallback` retirement: release one detached tile per call (the runtime calls this
    /// once per frame after presenting, when the frame has headroom).
    /// </summary>
    public void processRetiringTiles(int maxTiles = 1)
    {
        for (int i = this.retiringUploads.Count - 1; i >= 0 && maxTiles > 0; i--)
        {
            var prepared = this.retiringUploads[i];
            if (prepared.gpuUpload != null && !this.sink.uploadComplete(prepared.gpuUpload)) continue;
            this.retiringUploads.RemoveAt(i);
            this.disposePreparedTerrainPlan(prepared);
            maxTiles--;
        }
        for (int i = 0; i < maxTiles && this.retiringTiles.Count > 0; i++) this.destroyTile(this.retiringTiles.Dequeue());
    }

    /// <summary>`destroy()` / teardown: cancel every plan, stop the workers and release every tile.</summary>
    public void dispose()
    {
        this.resetTerrainPlanner(true);
        // Teardown may wait; a playing frame never does. cancelUpload joins before releasing the payload.
        // PORT: the workers are terminated above, so nothing here reuses a payload; a sink may release unfinished
        // uploads without joining (the studio's does, see StudioTileSink.cancelUpload).
        foreach (var prepared in this.retiringUploads)
        {
            if (prepared.gpuUpload != null) this.sink.cancelUpload(prepared.gpuUpload);
            prepared.gpuUpload = null;
            this.disposePreparedTerrainPlan(prepared);
        }
        this.retiringUploads.Clear();
        this.evictAllTiles();
        while (this.retiringTiles.Count > 0) this.destroyTile(this.retiringTiles.Dequeue());
    }

    /* ── tiles ──────────────────────────────────────────────────────────────────────────────────────────── */

    private void setTerrainGeometryDetail(string detail)
    {
        if (detail == this.terrainGeometryDetail) return;
        this.terrainGeometryDetail = detail;
        // Re-enter residency even if the camera remains inside the same tile window. Existing exact geometry stays
        // mounted until its asynchronously prepared replacement is ready, so the switch never creates holes.
        this.terrainResidencyVersion = -1;
    }

    private TerrainBakeFrame terrainBakeFrame(int tx, int ty, double ts, double originX, double originY) =>
        new()
        {
            i0 = tx * TILE_CELLS - TILE_BORDER,
            j0 = ty * TILE_CELLS - TILE_BORDER,
            width = TILE_CELLS + TILE_BORDER * 2,
            height = TILE_CELLS + TILE_BORDER * 2,
            originX = originX,
            originY = originY,
            tileSize = ts,
        };

    private void showTile(TerrainTile t)
    {
        t.shown = true;
        this.sink.setShown(t, true);
    }

    private void hideTile(TerrainTile t)
    {
        t.shown = false;
        this.sink.setShown(t, false);
    }

    private void showShadowTile(TerrainTile t, bool visible = false)
    {
        if (t.casterMounted)
        {
            // Promote this exact uncaptured proxy when it becomes visible instead of waiting for outer-ring jobs.
            if (visible && this.tileAffectsCurrentShadow(t) && !this.terrainShadowCapturedCasters.Contains(t))
                this.presentation.visibleShadowCasterSnapshotPending = true;
            return;
        }
        t.casterMounted = true;
        this.sink.setCasterMounted(t, true);
        if (this.tileAffectsCurrentShadow(t))
        {
            this.presentation.shadowCasterSnapshotPending = true;
            if (visible) this.presentation.visibleShadowCasterSnapshotPending = true;
        }
    }

    private void destroyTile(TerrainTile t)
    {
        if (t.casterMounted && this.tileAffectsCurrentShadow(t))
        {
            this.presentation.shadowCasterSnapshotPending = true;
            if (t.shown) this.presentation.visibleShadowCasterSnapshotPending = true;
        }
        if (t.shown) this.sink.setShown(t, false);
        if (t.casterMounted) this.sink.setCasterMounted(t, false);
        t.casterMounted = false;
        this.sink.destroy(t);
        this.recycleTerrainGeometry(t.geometry, t.recycleWorker);
        t.shown = false;
    }

    private double terrainBakeByteSize(TerrainTile tile) => tile.byteSize ??= payloadBytes(tile.geometry);

    // PORT: three's BufferAttributes keep a tile's arrays for its whole life; with a copying sink they go back to the
    // worker pool at once. The byte size is taken first, since the live-cache budget still counts the tile.
    private void releaseInstalledGeometry(TerrainTile tile)
    {
        tile.byteSize = payloadBytes(tile.geometry);
        this.recycleTerrainGeometry(tile.geometry, tile.recycleWorker);
        tile.geometry = new TerrainGeometryPayload();
    }

    private static double payloadBytes(TerrainGeometryPayload payload)
    {
        double bytes = 0;
        foreach (Array array in payloadArrays(payload)) bytes += (double)array.Length * 4;
        return bytes;
    }

    private static IEnumerable<Array> payloadArrays(TerrainGeometryPayload p)
    {
        Array?[] all =
        {
            p.surface?.position, p.surface?.normal, p.surface?.color, p.surface?.surface, p.surface?.emissive, p.surface?.ground, p.surface?.index,
            p.water?.position, p.water?.normal, p.water?.color, p.water?.water, p.water?.fold, p.water?.reflection, p.water?.index,
            p.mist?.position, p.mist?.color, p.mist?.mist, p.mist?.index,
            p.overlay?.position, p.overlay?.color, p.overlay?.index,
            p.actorWall?.position, p.actorWall?.index,
        };
        foreach (var a in all) if (a != null && a.Length > 0) yield return a;
    }

    private void recordTerrainInstall(double durationMs)
    {
        if (!double.IsFinite(durationMs) || durationMs < 0) return;
        this.terrainInstallEmaMs = this.terrainInstallEmaMs == 0 ? durationMs : this.terrainInstallEmaMs * 0.8 + durationMs * 0.2;
    }

    private double liveTerrainCacheByteBudget() =>
        terrainLiveCacheByteBudget(this.deviceMemory, this.isMobile, this.presentation.softwareExecution);

    private bool tileAffectsCurrentShadow(TerrainTile t) =>
        terrainBakeIntersectsShadowWindow(
            t.frame,
            this.presentation.lastShadowCx,
            this.presentation.lastShadowCz,
            this.presentation.lastShadowRadius + SHADOW_CASTER_REACH);

    private void recycleTerrainGeometry(TerrainGeometryPayload? payload, ITerrainBakeWorkerHandle? worker = null)
    {
        if (payload == null) return;
        worker ??= this.terrainPlanWorkers.Count > 0 ? this.terrainPlanWorkers[0] : null;
        var buffers = payloadArrays(payload).ToList();
        if (worker == null || buffers.Count == 0 || this.terrainPlanWorkers.IndexOf(worker) < 0) return;
        try
        {
            worker.postMessage(new TerrainBakeWorkerRecycleRequest { buffers = buffers });
        }
        catch
        {
            // A worker teardown may race disposal. The store reuse is optional.
        }
    }

    private void evictAllTiles()
    {
        foreach (var tile in this.tiles.values()) this.destroyTile(tile);
        this.tiles.clear();
        this.presentation.terrainShadowSnapshotInitialized = false;
        this.presentation.terrainShadowSnapshotStableFrames = 0;
        this.presentation.shadowCasterSnapshotPending = false;
        this.presentation.visibleShadowCasterSnapshotPending = false;
        this.terrainShadowCapturedCasters = new HashSet<TerrainTile>(ReferenceEqualityComparer.Instance);
    }

    /// <summary>Detach the complete obsolete viewport in one cheap operation, then release its geometry in idle slices.</summary>
    private void retireAllTiles()
    {
        foreach (var tile in this.tiles.values())
        {
            if (tile.shown) this.hideTile(tile);
            this.retiringTiles.Enqueue(tile);
        }
        this.tiles.clear();
    }

    private void queueRetiringTile(TerrainTile tile)
    {
        if (tile.casterMounted && this.tileAffectsCurrentShadow(tile))
        {
            this.presentation.shadowCasterSnapshotPending = true;
            if (tile.shown) this.presentation.visibleShadowCasterSnapshotPending = true;
        }
        if (tile.shown) this.sink.setShown(tile, false);
        tile.shown = false;
        if (tile.casterMounted) this.sink.setCasterMounted(tile, false);
        tile.casterMounted = false;
        this.retiringTiles.Enqueue(tile);
    }

    private void ensureScratch(int count)
    {
        if (this.discardBuf.Length >= count) return;
        this.discardBuf = new byte[count];
    }

    /* ── workers ────────────────────────────────────────────────────────────────────────────────────────── */

    private IReadOnlyList<ITerrainBakeWorkerHandle> ensureTerrainPlanWorkers()
    {
        if (this.terrainPlanWorkerExecutionFailed && nowMs() < this.terrainPlanWorkerRetryAt) return Array.Empty<ITerrainBakeWorkerHandle>();
        if (this.terrainPlanWorkerUnavailable)
        {
            if (nowMs() < this.terrainPlanWorkerRetryAt) return Array.Empty<ITerrainBakeWorkerHandle>();
            this.terrainPlanWorkerUnavailable = false;
            // PORT: the browser keeps a failed pool on the synchronous fallback for the session; without that
            // fallback a failed pool is restarted after the same back-off.
            this.terrainPlanWorkerExecutionFailed = false;
        }
        if (this.terrainPlanWorkers.Count > 0) return this.terrainPlanWorkers;
        try
        {
            // Software rasterizers keep one worker (four parses of the compiler contend with the rasterizer).
            int count = this.presentation.softwareExecution
                ? 1
                : terrainPlanWorkerConcurrency(this.hardwareConcurrency, this.isMobile, this.deviceMemory);
            for (int index = 0; index < count; index++)
            {
                ITerrainBakeWorkerHandle worker = this.workerFactory(index);
                worker.onmessage = response =>
                {
                    if (response is TerrainBakeWorkerReadyResponse)
                    {
                        this.readyTerrainPlanWorkers.Add(worker);
                        this.terrainPlanWorkerFailures = 0;
                        this.terrainPlanWorkerRetryAt = 0;
                        return;
                    }
                    if (response is TerrainBakeWorkerResultResponse result) this.acceptTerrainPlan(result, worker);
                };
                worker.onerror = (message, error) => this.failTerrainPlanWorker(worker, string.IsNullOrEmpty(message) ? "worker execution failed" : message, error);
                this.terrainPlanWorkers.Add(worker);
            }
            return this.terrainPlanWorkers;
        }
        catch
        {
            foreach (var worker in this.terrainPlanWorkers) worker.terminate();
            this.terrainPlanWorkers.Clear();
            this.deferTerrainPlanWorkerRetry();
            return Array.Empty<ITerrainBakeWorkerHandle>();
        }
    }

    private void disableTerrainPlanWorker(ITerrainBakeWorkerHandle worker)
    {
        if (!this.terrainPlanWorkers.Contains(worker)) return;
        this.resetTerrainPlanner(true);
        this.deferTerrainPlanWorkerRetry();
    }

    /// <summary>A module still queued behind fetch/parse is not a live worker which stopped answering. Both limits must expire.</summary>
    private bool terrainPlanWithinStallBudget(PendingTerrainPlan pending, double now)
    {
        int frameBudget = pending.requestedBeforeWorkerReady ? TERRAIN_PLAN_STARTUP_STALL_FRAMES : TERRAIN_PLAN_VISIBLE_STALL_FRAMES;
        double timeBudget = pending.requestedBeforeWorkerReady ? TERRAIN_PLAN_STARTUP_STALL_MS : TERRAIN_PLAN_VISIBLE_STALL_MS;
        return this.frameCounter - pending.requestedFrame < frameBudget || now - pending.requestedAt < timeBudget;
    }

    private void failTerrainPlanWorker(ITerrainBakeWorkerHandle worker, string reason, Exception? cause = null)
    {
        // Terminating a sharded pool can deliver one trailing error per sibling: only the first live member counts.
        if (!this.terrainPlanWorkers.Contains(worker)) return;
        JsConsole.error($"[terrain-plan-worker] {reason}{(cause == null ? "" : " " + cause)}");
        this.terrainPlanWorkerExecutionFailed = true;
        this.resetTerrainPlanner(true);
        this.deferTerrainPlanWorkerRetry();
    }

    private void deferTerrainPlanWorkerRetry()
    {
        this.terrainPlanWorkerFailures++;
        this.terrainPlanWorkerUnavailable = true;
        this.terrainPlanWorkerRetryAt =
            nowMs() +
            Math.min(
                TERRAIN_PLAN_WORKER_RETRY_MAX_MS,
                TERRAIN_PLAN_WORKER_RETRY_BASE_MS * Math.pow(2, Math.min(5, this.terrainPlanWorkerFailures - 1)));
    }

    /// <summary>Deterministic least-loaded dispatch. A pending tile keeps its assigned worker for revision/cancel ordering;
    /// a new tile starts at its stable hash and may use another equally capable idle shard.</summary>
    private ITerrainBakeWorkerHandle? terrainPlanWorkerForKey(string key)
    {
        IReadOnlyList<ITerrainBakeWorkerHandle> workers = this.ensureTerrainPlanWorkers();
        if (workers.Count == 0) return null;
        if (this.terrainPlanWorkerAffinity.TryGetValue(key, out var affine) && workers.Contains(affine)) return affine;
        double hash = 0x811c9dc5;
        for (int index = 0; index < key.Length; index++)
            hash = JsMath.imul(Js.ToInt32(hash) ^ key[index], 0x01000193);
        int start = (int)(Js.ToUint32(hash) % (uint)workers.Count);
        var loads = new Dictionary<ITerrainBakeWorkerHandle, int>(ReferenceEqualityComparer.Instance);
        foreach (var worker in workers) loads[worker] = 0;
        foreach (var pending in this.pendingTerrainPlans.values())
            loads[pending.worker] = loads.GetValueOrDefault(pending.worker) + 1;
        ITerrainBakeWorkerHandle selected = workers[start];
        int selectedLoad = loads.GetValueOrDefault(selected);
        for (int offset = 1; offset < workers.Count; offset++)
        {
            ITerrainBakeWorkerHandle candidate = workers[(start + offset) % workers.Count];
            int load = loads.GetValueOrDefault(candidate);
            if (load >= selectedLoad) continue;
            selected = candidate;
            selectedLoad = load;
        }
        this.terrainPlanWorkerAffinity[key] = selected;
        return selected;
    }

    private void resetTerrainPlanner(bool terminateWorker = false)
    {
        foreach (var pending in this.pendingTerrainPlans.values())
        {
            try
            {
                pending.worker.postMessage(new TerrainBakeWorkerCancelRequest { id = pending.id, serial = pending.serial });
            }
            catch
            {
                // A worker error can race planner teardown. Local state is authoritative from here on.
            }
        }
        foreach (var prepared in this.preparedTerrainPlans.values())
        {
            try { this.disposePreparedTerrainPlan(prepared); }
            catch { /* collected with the terminated worker */ }
        }
        this.terrainPlanSerial++;
        this.pendingTerrainPlans.clear();
        this.deferredTerrainPlans.clear();
        this.preparedTerrainPlans.clear();
        if (terminateWorker)
        {
            foreach (var worker in this.terrainPlanWorkers) worker.terminate();
            this.terrainPlanWorkers.Clear();
            this.readyTerrainPlanWorkers.Clear();
            this.terrainPlanWorkerAffinity.Clear();
        }
    }

    private IReadOnlyList<TerrainCompilerThemeVisual> terrainCompilerThemeVisuals(IReadOnlyList<string> palette)
    {
        string key = string.Join("\0", palette);
        if (key == this.terrainCompilerThemeCatalogKey) return this.terrainCompilerThemeCatalog;
        this.terrainCompilerThemeCatalogKey = key;
        this.terrainCompilerThemeCatalog = palette.Select(themeKey =>
        {
            Biome biome = Theme.biomeForKey(themeKey);
            return new TerrainCompilerThemeVisual
            {
                key = themeKey,
                biome = biome,
                tileset = TerrainTilesetModule.terrainTilesetForBiome(biome).ToMaterialTileset(),
                style = Theme.worldStyleOf(biome),
            };
        }).ToList();
        return this.terrainCompilerThemeCatalog;
    }

    /// <summary>Rare overflow maintenance only: work inside the retained coverage lattice is never displaced.</summary>
    private void pruneTerrainPlansOutside(int minTx, int maxTx, int minTy, int maxTy)
    {
        foreach (var (key, prepared) in this.preparedTerrainPlans)
        {
            int tx = prepared.response.tx, ty = prepared.response.ty;
            if (tx >= minTx && tx <= maxTx && ty >= minTy && ty <= maxTy) continue;
            this.preparedTerrainPlans.delete(key);
            this.disposePreparedTerrainPlan(prepared);
        }
        foreach (var (key, pending) in this.pendingTerrainPlans)
        {
            int tx = (int)Math.floor((double)(pending.frame.i0 + TILE_BORDER) / TILE_CELLS);
            int ty = (int)Math.floor((double)(pending.frame.j0 + TILE_BORDER) / TILE_CELLS);
            if (tx >= minTx && tx <= maxTx && ty >= minTy && ty <= maxTy) continue;
            pending.worker.postMessage(new TerrainBakeWorkerCancelRequest { id = pending.id, serial = pending.serial });
            this.pendingTerrainPlans.delete(key);
            this.terrainPlanSuperseded++;
        }
        foreach (var (key, deferred) in this.deferredTerrainPlans)
        {
            if (deferred.tx >= minTx && deferred.tx <= maxTx && deferred.ty >= minTy && deferred.ty <= maxTy) continue;
            this.deferredTerrainPlans.delete(key);
            this.terrainPlanSuperseded++;
        }
    }

    private bool requestTerrainPlan(
        ClientDungeon cd,
        string key,
        TerrainBakeFrame frame,
        int tx,
        int ty,
        double contentHash,
        double effectSeed,
        int priority,
        string detail,
        Biome? visualBiome = null,
        TerrainTileset? visualTileset = null)
    {
        Biome? biome = visualBiome ?? this.presentation.currentBiome;
        TerrainTileset? tileset = visualTileset ?? this.presentation.currentTileset;
        if (biome == null || tileset == null) return false;
        DeferredTerrainPlanRequest? deferred = this.deferredTerrainPlans.get(key);
        if (deferred != null)
        {
            if (deferred.serial == this.terrainPlanSerial && deferred.contentHash == contentHash && deferred.effectSeed == effectSeed && deferred.priority <= priority)
                return false;
            this.deferredTerrainPlans.delete(key);
        }
        PreparedTerrainPlan? prepared = this.preparedTerrainPlans.get(key);
        if (prepared != null)
        {
            if (terrainPlanMatches(prepared.response.contentHash, prepared.response.effectSeed, prepared.response.frame, frame, contentHash, effectSeed)) return false;
            this.preparedTerrainPlans.delete(key);
            this.disposePreparedTerrainPlan(prepared);
            this.terrainPlanStaleResults++;
        }
        PendingTerrainPlan? existing = this.pendingTerrainPlans.get(key);
        if (existing != null)
        {
            if (terrainPlanMatches(existing.contentHash, existing.effectSeed, existing.frame, frame, contentHash, effectSeed) && existing.priority <= priority)
                return false;
            existing.worker.postMessage(new TerrainBakeWorkerCancelRequest { id = existing.id, serial = existing.serial });
            this.pendingTerrainPlans.delete(key);
            this.terrainPlanSuperseded++;
        }
        // (persistentTerrainCacheKey: IndexedDB only — not ported.)
        // Revisions and priority promotions stay on the worker which owns the active keyed job; new tiles use the
        // least-loaded deterministic shard so a visible request never waits behind a saturated hash bucket.
        ITerrainBakeWorkerHandle? worker = existing?.worker ?? this.terrainPlanWorkerForKey(key);
        if (worker == null) return false;
        int pendingLimit = System.Math.Min(
            TILE_PLAN_PENDING_LIMIT,
            System.Math.Max(TILE_PLAN_PENDING_PER_WORKER, this.terrainPlanWorkers.Count * TILE_PLAN_PENDING_PER_WORKER));
        if (this.pendingTerrainPlans.size >= pendingLimit)
        {
            string? displacedKey = null;
            PendingTerrainPlan? displaced = null;
            foreach (var (candidateKey, candidate) in this.pendingTerrainPlans)
            {
                if (candidate.priority <= priority) continue;
                if (
                    displaced == null ||
                    candidate.priority > displaced.priority ||
                    (candidate.priority == displaced.priority && candidate.requestedAt > displaced.requestedAt))
                {
                    displacedKey = candidateKey;
                    displaced = candidate;
                }
            }
            if (displaced == null || displacedKey == null)
            {
                this.deferTerrainPlanRequest(new DeferredTerrainPlanRequest
                {
                    serial = this.terrainPlanSerial,
                    cd = cd,
                    key = key,
                    frame = frame,
                    tx = tx,
                    ty = ty,
                    contentHash = contentHash,
                    effectSeed = effectSeed,
                    detail = detail,
                    priority = priority,
                    visualBiome = visualBiome,
                    visualTileset = visualTileset,
                    requestedAt = nowMs(),
                });
                return false;
            }
            displaced.worker.postMessage(new TerrainBakeWorkerCancelRequest { id = displaced.id, serial = displaced.serial });
            this.pendingTerrainPlans.delete(displacedKey);
            this.terrainPlanSuperseded++;
            this.terrainPlanBackpressure++;
        }
        double started = nowMs();
        int count = frame.width * frame.height;
        this.ensureScratch(count);
        byte[] tilesPlane = this.terrainSampleBuffers.acquireUint8(count);
        sbyte[] elevation = this.terrainSampleBuffers.acquireInt8(count);
        byte[] surface = this.terrainSampleBuffers.acquireUint8(count);
        byte[] theme = this.terrainSampleBuffers.acquireUint8(count);
        byte[] bridgeSpans = this.terrainSampleBuffers.acquireUint8(count);
        float[] waterLevels = this.terrainSampleBuffers.acquireFloat32(count);
        cd.sampleRegionInto(frame.i0, frame.j0, frame.width, frame.height, frame.tileSize, tilesPlane, elevation, surface, this.discardBuf, theme, bridgeSpans, waterLevels);
        List<TerrainDecorationPlacement>? decorations = cd.terrainDecorationsForRegion(frame.i0, frame.j0, frame.width, frame.height, frame.tileSize);
        byte[]? floorUsage = cd.terrainFloorUsageForRegion(frame.i0, frame.j0, frame.width, frame.height, frame.tileSize);
        // `[...new Set([...palette, ...decorationThemes])]`
        var terrainVisualThemeKeys = new JsSet<string>();
        foreach (string themeKey in cd.terrainThemePalette) terrainVisualThemeKeys.add(themeKey);
        if (decorations != null)
            foreach (var decoration in decorations)
                if (Js.Truthy(decoration.themeKey)) terrainVisualThemeKeys.add(decoration.themeKey!);
        List<string> visualThemeKeys = terrainVisualThemeKeys.ToList();
        double sampledAt = nowMs();
        int id = ++this.terrainPlanRequestId;
        double structuralHash = TerrainGeometryDetailModule.terrainContentHashForDetail(
            cd.terrainStructuralSignatureForBounds(
                frame.originX + frame.i0 * frame.tileSize,
                frame.originY + frame.j0 * frame.tileSize,
                frame.originX + (frame.i0 + frame.width) * frame.tileSize,
                frame.originY + (frame.j0 + frame.height) * frame.tileSize),
            detail);
        var pending = new PendingTerrainPlan
        {
            id = id,
            serial = this.terrainPlanSerial,
            revision = ++this.terrainPlanRevision,
            key = key,
            contentHash = contentHash,
            structuralHash = structuralHash,
            detail = detail,
            effectSeed = effectSeed,
            frame = frame,
            requestedAt = sampledAt,
            sampleMs = sampledAt - started,
            priority = priority,
            requestedFrame = this.frameCounter,
            requestedBeforeWorkerReady = !this.readyTerrainPlanWorkers.Contains(worker),
            worker = worker,
        };
        this.pendingTerrainPlans.set(key, pending);
        TerrainGeometryDetailProfile profile = TerrainGeometryDetailModule.terrainGeometryDetailProfile(
            detail,
            this.presentation.terrainVegetationBladeCap,
            this.presentation.terrainCliffDressingDensity);
        var request = new TerrainBakeWorkerBakeRequest
        {
            id = id,
            serial = pending.serial,
            revision = pending.revision,
            priority = priority,
            tx = tx,
            ty = ty,
            contentHash = contentHash,
            structuralHash = structuralHash,
            themeKey = $"{biome.key}:{tileset.id}:{detail}:{Js.Str(profile.vegetationBladeCap)}:{Js.Str(profile.cliffDressingDensity)}:{(this.presentation.visualGroundingEnabled ? 1 : 0)}:{string.Join(",", visualThemeKeys)}",
            frame = frame,
            tiles = tilesPlane,
            elevation = elevation,
            surface = surface,
            theme = theme,
            bridgeSpans = bridgeSpans,
            waterLevels = waterLevels,
            floorUsage = floorUsage,
            decorations = decorations,
            tileset = tileset.ToMaterialTileset(),
            style = Theme.worldStyleOf(biome),
            biome = biome,
            terrainThemePalette = cd.terrainThemePalette,
            themeVisuals = this.terrainCompilerThemeVisuals(visualThemeKeys),
            vegetationBladeCap = profile.vegetationBladeCap,
            cliffDressingDensity = profile.cliffDressingDensity,
            visualGrounding = this.presentation.visualGroundingEnabled,
            // The compact vertex-pigment path consumes only the structural surface/water payload.
            softwareSafe = this.presentation.softwareExecution || !this.presentation.richStreamedTerrain,
            biomeKey = cd.descriptor?.biomeKey ?? biome.key,
            endlessWallProfile = cd.isEndless,
            effectSeed = effectSeed,
        };
        this.terrainPlanRequests++;
        try
        {
            worker.postMessage(request);
            return true;
        }
        catch (Exception error)
        {
            this.pendingTerrainPlans.delete(key);
            this.releaseSamplePlanes(tilesPlane, elevation, surface, theme, bridgeSpans, waterLevels, floorUsage);
            this.failTerrainPlanWorker(worker, "request could not be cloned", error);
            return false;
        }
    }

    private void releaseSamplePlanes(params Array?[] planes)
    {
        foreach (Array? plane in planes)
            if (plane != null) this.terrainSampleBuffers.release(plane);
    }

    private void deferTerrainPlanRequest(DeferredTerrainPlanRequest request)
    {
        DeferredTerrainPlanRequest? existing = this.deferredTerrainPlans.get(request.key);
        if (existing == null) this.terrainPlanBackpressure++;
        if (existing == null && this.deferredTerrainPlans.size >= TILE_PLAN_PENDING_LIMIT)
        {
            string? displacedKey = null;
            DeferredTerrainPlanRequest? displaced = null;
            foreach (var (candidateKey, candidate) in this.deferredTerrainPlans)
            {
                if (candidate.priority <= request.priority) continue;
                if (
                    displaced == null ||
                    candidate.priority > displaced.priority ||
                    (candidate.priority == displaced.priority && candidate.requestedAt > displaced.requestedAt))
                {
                    displacedKey = candidateKey;
                    displaced = candidate;
                }
            }
            if (displaced == null || displacedKey == null) return;
            this.deferredTerrainPlans.delete(displacedKey);
            this.terrainPlanSuperseded++;
        }
        if (
            existing == null ||
            request.priority < existing.priority ||
            request.contentHash != existing.contentHash ||
            request.effectSeed != existing.effectSeed)
            this.deferredTerrainPlans.set(request.key, request);
    }

    /// <summary>Fill newly opened worker slots from a stable priority queue. Sampling happens only after admission.</summary>
    private void drainDeferredTerrainPlans()
    {
        if (this.deferredTerrainPlans.size == 0 || this.terrainPlanWorkers.Count == 0) return;
        int pendingLimit = System.Math.Min(
            TILE_PLAN_PENDING_LIMIT,
            System.Math.Max(TILE_PLAN_PENDING_PER_WORKER, this.terrainPlanWorkers.Count * TILE_PLAN_PENDING_PER_WORKER));
        while (this.pendingTerrainPlans.size < pendingLimit && this.deferredTerrainPlans.size > 0)
        {
            DeferredTerrainPlanRequest? selected = null;
            foreach (var candidate in this.deferredTerrainPlans.values())
            {
                if (candidate.serial != this.terrainPlanSerial) continue;
                if (selected == null || candidate.priority < selected.priority || (candidate.priority == selected.priority && candidate.requestedAt < selected.requestedAt))
                    selected = candidate;
            }
            if (selected == null)
            {
                this.deferredTerrainPlans.clear();
                return;
            }
            this.deferredTerrainPlans.delete(selected.key);
            this.requestTerrainPlan(
                selected.cd,
                selected.key,
                selected.frame,
                selected.tx,
                selected.ty,
                selected.contentHash,
                selected.effectSeed,
                selected.priority,
                selected.detail,
                selected.visualBiome,
                selected.visualTileset);
        }
    }

    private void acceptTerrainPlan(TerrainBakeWorkerResultResponse response, ITerrainBakeWorkerHandle worker)
    {
        this.releaseSamplePlanes(response.tiles, response.elevation, response.surface, response.theme, response.bridgeSpans, response.waterLevels, response.floorUsage);
        this.terrainWorkerMaterializeMs += response.materializeMs;
        this.terrainWorkerPlanMs += response.planMs;
        this.terrainWorkerBakeMs += response.meshMs;
        string key = tileKey(response.tx, response.ty);
        PendingTerrainPlan? pending = this.pendingTerrainPlans.get(key);
        // Transfer-prepared requests are namespaced away from the source tile lattice while the worker protocol keeps
        // physical tx/ty untouched. Resolve that bounded ownership only on response.
        if (pending == null || pending.id != response.id || pending.revision != response.revision)
        {
            foreach (var (candidateKey, candidate) in this.pendingTerrainPlans)
            {
                if (candidate.id != response.id || candidate.revision != response.revision) continue;
                key = candidateKey;
                pending = candidate;
                break;
            }
        }
        if (pending == null || pending.id != response.id || pending.revision != response.revision)
        {
            if (response.cancelled) this.terrainPlanCancelled++;
            else
            {
                this.recycleTerrainGeometry(((TerrainBakeWorkerCompletedResponse)response).geometry, worker);
                this.terrainPlanStaleResults++;
            }
            return;
        }
        this.pendingTerrainPlans.delete(key);
        this.drainDeferredTerrainPlans();
        if (response.cancelled)
        {
            this.terrainPlanCancelled++;
            return;
        }
        var completed = (TerrainBakeWorkerCompletedResponse)response;
        if (
            response.serial != this.terrainPlanSerial ||
            response.serial != pending.serial ||
            response.revision != pending.revision ||
            response.contentHash != pending.contentHash ||
            response.effectSeed != pending.effectSeed)
        {
            this.recycleTerrainGeometry(completed.geometry, worker);
            this.terrainPlanStaleResults++;
            return;
        }
        double readyAt = nowMs();
        double latencyMs = readyAt - pending.requestedAt;
        this.terrainWorkerMaxLatencyMs = Math.max(this.terrainWorkerMaxLatencyMs, latencyMs);
        if (this.preparedTerrainPlans.size >= TILE_PLAN_READY_LIMIT)
        {
            string? displacedKey = null;
            PreparedTerrainPlan? displaced = null;
            foreach (var (candidateKey, candidate) in this.preparedTerrainPlans)
            {
                if (candidate.priority <= pending.priority) continue;
                if (displaced == null || candidate.priority > displaced.priority || (candidate.priority == displaced.priority && candidate.readyAt > displaced.readyAt))
                {
                    displacedKey = candidateKey;
                    displaced = candidate;
                }
            }
            if (displaced == null || displacedKey == null)
            {
                this.recycleTerrainGeometry(completed.geometry, worker);
                this.terrainPlanBackpressure++;
                return;
            }
            this.preparedTerrainPlans.delete(displacedKey);
            this.disposePreparedTerrainPlan(displaced);
            this.terrainPlanBackpressure++;
        }
        this.preparedTerrainPlans.set(key, new PreparedTerrainPlan
        {
            response = completed,
            detail = pending.detail,
            sampleMs = pending.sampleMs,
            latencyMs = latencyMs,
            transferMs = Math.max(0, latencyMs - response.workerQueueMs - response.materializeMs - response.planMs - response.meshMs),
            readyAt = readyAt,
            priority = pending.priority,
            worker = worker,
        });
        this.terrainPlanAccepted++;
    }

    private static bool terrainPlanMatches(double planHash, double planSeed, TerrainBakeFrame planFrame, TerrainBakeFrame frame, double contentHash, double effectSeed) =>
        planHash == contentHash &&
        planSeed == effectSeed &&
        planFrame.i0 == frame.i0 &&
        planFrame.j0 == frame.j0 &&
        planFrame.originX == frame.originX &&
        planFrame.originY == frame.originY &&
        planFrame.tileSize == frame.tileSize;

    private void disposePreparedTerrainPlan(PreparedTerrainPlan prepared)
    {
        prepared.gpuUploadCancelled = true;
        TerrainTile? tile = prepared.gpuBake;
        if (tile != null)
        {
            prepared.gpuBake = null;
            this.destroyTile(tile);
            return;
        }
        if (prepared.gpuUpload != null)
        {
            if (!this.sink.uploadComplete(prepared.gpuUpload))
            {
                if (!this.retiringUploads.Contains(prepared)) this.retiringUploads.Add(prepared);
                return;
            }
            this.sink.cancelUpload(prepared.gpuUpload);
            prepared.gpuUpload = null;
        }
        if (prepared.gpuUploadPayloadReleased) return;
        this.recycleTerrainGeometry(prepared.response.geometry, prepared.worker);
        prepared.gpuUploadPayloadReleased = true;
    }

    private PreparedTerrainPlan? preparedTerrainPlanFor(string key, TerrainBakeFrame frame, double contentHash, double effectSeed)
    {
        PreparedTerrainPlan? prepared = this.preparedTerrainPlans.get(key);
        if (prepared == null) return null;
        TerrainBakeWorkerCompletedResponse response = prepared.response;
        if (response.serial != this.terrainPlanSerial || !terrainPlanMatches(response.contentHash, response.effectSeed, response.frame, frame, contentHash, effectSeed))
        {
            this.preparedTerrainPlans.delete(key);
            this.disposePreparedTerrainPlan(prepared);
            this.terrainPlanStaleResults++;
            return null;
        }
        this.enqueuePreparedTerrainGpuUpload(prepared);
        return prepared;
    }

    /// <summary>`enqueuePreparedTerrainGpuUpload`: the Godot layer builds the meshes off the main thread.</summary>
    private void enqueuePreparedTerrainGpuUpload(PreparedTerrainPlan prepared)
    {
        if (prepared.gpuUploadQueued || prepared.gpuUploadCancelled) return;
        prepared.gpuUploadQueued = true;
        prepared.gpuUpload = this.sink.beginUpload(prepared.response.geometry);
    }

    private bool preparedTerrainGpuReady(PreparedTerrainPlan prepared)
    {
        if (prepared.gpuBake != null) return this.sink is not ITerrainTilePublicationSink pending || pending.readyToPublish(prepared.gpuBake);
        if (prepared.gpuUpload == null || !this.sink.uploadComplete(prepared.gpuUpload)) return false;
        prepared.gpuBake = this.bakePreparedTile(prepared);
        return this.sink is not ITerrainTilePublicationSink publication || publication.readyToPublish(prepared.gpuBake);
    }

    private TerrainTile takePreparedTerrainTile(PreparedTerrainPlan prepared, bool forced)
    {
        TerrainTile? uploaded = prepared.gpuBake;
        uploaded ??= this.bakePreparedTile(prepared);
        if (this.sink is ITerrainTilePublicationSink publication) publication.publish(uploaded);
        prepared.gpuBake = null;
        return uploaded;
    }

    private TerrainTile bakePreparedTile(PreparedTerrainPlan prepared)
    {
        double started = nowMs();
        TerrainBakeWorkerCompletedResponse response = prepared.response;
        var tile = new TerrainTile
        {
            tx = response.tx,
            ty = response.ty,
            key = tileKey(response.tx, response.ty),
            frame = response.frame,
            audit = response.audit,
            contentHash = response.contentHash,
            detail = prepared.detail,
            contentVersion = -1,
            used = this.frameCounter,
            shown = false,
            recycleWorker = prepared.worker,
            geometry = response.geometry,
            gpu = prepared.gpuUpload,
            hasCasters = response.geometry.actorWall is { } casters && casters.position.Length > 0 && casters.index.Length > 0,
        };
        prepared.gpuUpload = null;
        prepared.gpuUploadPayloadReleased = true;
        this.sink.install(tile);
        if (this.sink is ITerrainTileCopyingSink) this.releaseInstalledGeometry(tile);
        this.recordTerrainInstall(nowMs() - started);
        this.terrainPreparedBakes++;
        return tile;
    }
}
