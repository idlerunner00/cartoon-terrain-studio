// Port of packages/client/src/render/environment/terrainBakeWorker.ts — keep in lockstep with the original.
//
// PORT NOTES (read before editing)
// * The TS module is a Web Worker script: its module-level `let`/`const` state is one worker realm, its
//   `workerScope.onmessage` handler receives requests and `processNext()` runs one job. In C# that realm is ONE
//   <see cref="TerrainBakeWorker"/> INSTANCE: every former module-level variable is an instance field, every module
//   function an instance method, and one instance is created per worker thread (TerrainBakeWorkerHost.cs runs it).
//   A worker instance never touches presentation-thread state: its only inputs are the requests it is handed and
//   its only output is <see cref="ITerrainBakeWorkerScope.postMessage"/>.
// * `workerScope.postMessage(response, transfer)` → <see cref="ITerrainBakeWorkerScope.postMessage"/>. There is no
//   transfer list: the response carries references to the request's input planes and to freshly written geometry
//   stores, and the worker never touches either again after posting (the ownership rule the transfer enforced).
// * `await yieldForControlMessages()` (scheduler.yield / MessageChannel round trip) is where the browser runs the
//   worker's queued `onmessage` tasks — cancellations and newer revisions — in the middle of a job. The port makes
//   that explicit: <see cref="yieldForControlMessages"/> drains the host inbox through <see cref="onmessage"/> on the
//   worker thread. `schedulePump()`'s `setTimeout(processNext, 0)` → <see cref="pumpScheduled"/>, which the host
//   services only after every already-queued message has been handled (a zero-delay timer never overtakes queued
//   messages in practice).
// * The cooperative compiler entry points are synchronous in C# (see TerrainGeometryCompiler.cs); the async
//   checkpoint becomes a `Func&lt;bool&gt;` that yields for control messages and answers `queue.isCurrent(job)`.
// * `performance.now()` → <see cref="ITerrainBakeWorkerScope.now"/> (timings are telemetry only).
// * A terminated host makes every later checkpoint fail so an abandoned job stops at its next emission unit; the
//   browser simply kills the realm. Nothing a terminated worker does is observable either way.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>`TerrainBakeWorkerRequest` (discriminated by <c>kind</c>).</summary>
public abstract class TerrainBakeWorkerRequest
{
}

public sealed class TerrainBakeWorkerBakeRequest : TerrainBakeWorkerRequest
{
    public int id;
    public int serial;
    public int revision;
    /// <summary>TerrainBakePriority (0 | 1 | 2).</summary>
    public int priority;
    public int tx;
    public int ty;
    /// <summary>A JS number: the region's chunk signature (signed int32) or its overview namespace (uint32).</summary>
    public double contentHash;
    /// <summary>Expensive carrier layer identity; dressing-only edits keep this stable.</summary>
    public double structuralHash;
    public string themeKey = "";
    public TerrainBakeFrame frame = null!;
    public byte[] tiles = null!;
    /// <summary>`TerrainElevationLayer` (Int8Array).</summary>
    public sbyte[] elevation = null!;
    public byte[] surface = null!;
    public byte[] theme = null!;
    /// <summary>Bridge span semantics sampled from each complete owning chunk/map before this clipped bake is transferred.</summary>
    public byte[] bridgeSpans = null!;
    /// <summary>Complete-layout Water datums sampled into this clipped bake.</summary>
    public float[] waterLevels = null!;
    public byte[]? floorUsage;
    /// <summary>`undefined` selects fallback scatter; an explicit empty array preserves authored quiet space.</summary>
    public List<TerrainDecorationPlacement>? decorations;
    public TerrainMaterialTileset tileset = null!;
    public WorldStyle style = null!;
    public Biome biome = null!;
    public IReadOnlyList<string> terrainThemePalette = null!;
    public IReadOnlyList<TerrainCompilerThemeVisual> themeVisuals = null!;
    public double vegetationBladeCap;
    public double cliffDressingDensity;
    /// <summary>Bakes the actor-grounding field into static cap vertices; false is the visual-quality rollback path.</summary>
    public bool visualGrounding;
    /// <summary>Explicit CPU-rasterizer tier; strips render lanes which the receiving software path never submits.</summary>
    public bool softwareSafe;
    public string biomeKey = "";
    /// <summary>Selects the taller streamed-world wall shell without changing Hub/finite authored geometry.</summary>
    public bool endlessWallProfile;
    /// <summary>A uint32 as a JS number.</summary>
    public double effectSeed;
}

public sealed class TerrainBakeWorkerCancelRequest : TerrainBakeWorkerRequest
{
    public int id;
    public int serial;
}

public sealed class TerrainBakeWorkerRecycleRequest : TerrainBakeWorkerRequest
{
    /// <summary>`ArrayBuffer[]` → the typed stores themselves (`float[]` / `uint[]`).</summary>
    public List<Array> buffers = new();
}

/// <summary>`TerrainBakeCancellationStage` literals.</summary>
public static class TerrainBakeCancellationStage
{
    public const string Queued = "queued";
    public const string OlderRevision = "older-revision";
    public const string BeforeMaterialize = "before-materialize";
    public const string AfterMaterialize = "after-materialize";
    public const string AfterPlan = "after-plan";
    public const string DuringBake = "during-bake";
    public const string AfterBake = "after-bake";
}

/// <summary>`TerrainBakeWorkerResponse` (discriminated by <c>kind</c>).</summary>
public abstract class TerrainBakeWorkerResponse
{
}

public sealed class TerrainBakeWorkerReadyResponse : TerrainBakeWorkerResponse
{
}

/// <summary>`TerrainBakeWorkerResponseBase` — the fields shared by completed and cancelled results.</summary>
public abstract class TerrainBakeWorkerResultResponse : TerrainBakeWorkerResponse
{
    public int id;
    public int serial;
    public int revision;
    public int tx;
    public int ty;
    public double contentHash;
    public double effectSeed;
    public TerrainBakeFrame frame = null!;
    /// <summary>Input stores return to the main thread and enter its exact-size sample pool.</summary>
    public byte[] tiles = null!;
    public sbyte[] elevation = null!;
    public byte[] surface = null!;
    public byte[] theme = null!;
    public byte[] bridgeSpans = null!;
    public float[] waterLevels = null!;
    public byte[]? floorUsage;
    public abstract bool cancelled { get; }
    /// <summary>Worker-local ready-to-first-request delay; the owner handshake supplies construction/fetch/parse time.</summary>
    public double workerStartParseMs;
    public double workerQueueMs;
    public double materializeMs;
    public double planMs;
    public double meshMs;
    public double inputBytes;
    public double geometryBytes;
    public double transferBytes;
    public double staleCpuMs;
    public double avoidedGeometryTransferBytes;
    public double poolAllocatedBytes;
    public double poolRetainedBytes;
    public double poolPeakRetainedBytes;
    public double queueSuperseded;
    public double queueCancelled;
}

public sealed class TerrainBakeWorkerCompletedResponse : TerrainBakeWorkerResultResponse
{
    public override bool cancelled => false;
    public TerrainGeometryPayload geometry = null!;
    public TerrainBakeAudit audit = null!;
    public double reusedGeometryBuffers;
    public bool reusedStructuralGeometry;
    public double structuralCacheBytes;
}

public sealed class TerrainBakeWorkerCancelledResponse : TerrainBakeWorkerResultResponse
{
    public override bool cancelled => true;
    /// <summary>A <see cref="TerrainBakeCancellationStage"/> literal.</summary>
    public string cancellationStage = "";
}

/// <summary>
/// The worker realm's view of its host: `workerScope.postMessage`, the queued-but-unhandled `onmessage` tasks and
/// `performance.now()`. Implemented by TerrainBakeWorkerHost.cs; everything here is called on the worker thread.
/// </summary>
public interface ITerrainBakeWorkerScope
{
    /// <summary>`workerScope.postMessage(message, transfer)`. Must be thread-safe (delivered on the owner's loop).</summary>
    void postMessage(TerrainBakeWorkerResponse response);

    /// <summary>Take the next message the owner has posted but the worker has not handled yet (non-blocking).</summary>
    bool tryTakeMessage(out TerrainBakeWorkerRequest request);

    /// <summary>`performance.now()` in milliseconds.</summary>
    double now();

    /// <summary>The owner called `worker.terminate()`.</summary>
    bool terminated { get; }
}

/// <summary>
/// One terrain bake worker realm (terrainBakeWorker.ts). Create exactly one per worker thread; it owns its geometry
/// compiler, resolver/theme caches, materialized/plan scratch, exact-size geometry pool, structural layer cache and
/// latest-wins job queue. All members must be called on that thread.
/// </summary>
public sealed class TerrainBakeWorker
{
    private sealed class QueuedTerrainBake
    {
        public TerrainBakeWorkerBakeRequest request = null!;
        public double receivedAt;
    }

    private sealed class TerrainBakeTimings
    {
        public double startedAt;
        public double materializedAt;
        public double plannedAt;
        public double bakedAt;
    }

    private sealed class StructuralGeometryEntry
    {
        public string identity = "";
        public TerrainGeometryPayload geometry = null!;
        public TerrainBakeAudit audit = null!;
        public double bytes;
    }

    private readonly ITerrainBakeWorkerScope workerScope;

    private readonly double WORKER_MODULE_READY_AT;
    private double workerStartParseMs = 0;
    private bool firstRequestObserved = false;
    private MaterializedTerrain? materializedScratch;
    private TerrainRenderPlan? planScratch;
    private string resolverTheme = "";
    private Func<TerrainCell, double, TerrainMaterial>? materialForCell;
    private readonly TerrainGeometryCompiler geometryCompiler = new(true);
    private string geometryTheme = "";
    // Four desktop workers may coexist. A per-worker 32 MiB / four-per-size ceiling preserves useful exact-size
    // reuse while keeping total retained ArrayBuffers bounded on both two-worker mobile and four-worker desktop.
    private readonly TerrainTransferBufferPool geometryBuffers = new(32 * 1024 * 1024, 4);
    private readonly JsMap<string, StructuralGeometryEntry> structuralGeometryCache = new();
    private const double STRUCTURAL_GEOMETRY_CACHE_BYTES = 24 * 1024 * 1024;
    private double structuralGeometryCacheBytes = 0;
    private readonly TerrainLatestPriorityQueue<QueuedTerrainBake> queue = new();
    private bool pumpScheduled = false;
    private TerrainQueuedJob<QueuedTerrainBake>? activeJob;

    /// <summary>Evaluating the module: the realm state above plus the `{ kind: 'ready' }` handshake.</summary>
    public TerrainBakeWorker(ITerrainBakeWorkerScope workerScope)
    {
        this.workerScope = workerScope;
        WORKER_MODULE_READY_AT = workerScope.now();
        // This handshake lets the owner measure construction + module fetch/parse/evaluation independently from the
        // first request's queue, bake and transfer time. Worker time origins are otherwise not portable to the owner.
        workerScope.postMessage(new TerrainBakeWorkerReadyResponse());
    }

    /// <summary>A `setTimeout(processNext, 0)` is pending (the host runs <see cref="runScheduledPump"/>).</summary>
    public bool isPumpScheduled => pumpScheduled;

    /// <summary>`workerScope.onmessage = (event) => …`.</summary>
    public void onmessage(TerrainBakeWorkerRequest request)
    {
        if (!firstRequestObserved)
        {
            firstRequestObserved = true;
            workerStartParseMs = Math.max(0, workerScope.now() - WORKER_MODULE_READY_AT);
        }
        if (request is TerrainBakeWorkerRecycleRequest recycle)
        {
            geometryBuffers.releaseAll(recycle.buffers);
            return;
        }
        if (request is TerrainBakeWorkerCancelRequest cancel)
        {
            TerrainQueueInvalidation<QueuedTerrainBake>? invalidated = queue.cancel(cancel.serial, cancel.id);
            if (invalidated != null && !invalidated.wasActive)
                postCancelled(
                    invalidated.job.value,
                    TerrainBakeCancellationStage.Queued,
                    emptyTimings(invalidated.job.value.receivedAt));
            return;
        }

        var bake = (TerrainBakeWorkerBakeRequest)request;
        double receivedAt = workerScope.now();
        TerrainQueueEnqueueResult<QueuedTerrainBake> result = queue.enqueue(new TerrainQueueDescriptor<QueuedTerrainBake>
        {
            tileKey = $"{Js.Str(bake.tx)},{Js.Str(bake.ty)}",
            generation = bake.serial,
            requestId = bake.id,
            revision = bake.revision,
            priority = bake.priority,
            value = new QueuedTerrainBake { request = bake, receivedAt = receivedAt },
        });
        if (result.superseded != null && !result.superseded.wasActive)
            postCancelled(
                result.superseded.job.value,
                TerrainBakeCancellationStage.Queued,
                emptyTimings(result.superseded.job.value.receivedAt));
        if (!result.accepted)
        {
            postCancelled(
                new QueuedTerrainBake { request = bake, receivedAt = receivedAt },
                TerrainBakeCancellationStage.OlderRevision,
                emptyTimings(receivedAt));
            return;
        }
        schedulePump();
    }

    private void schedulePump()
    {
        if (pumpScheduled || activeJob != null || queue.size == 0) return;
        pumpScheduled = true;
    }

    /// <summary>The zero-delay timer of <see cref="schedulePump"/> fires: `pumpScheduled = false; void processNext()`.</summary>
    public bool runScheduledPump()
    {
        if (!pumpScheduled) return false;
        pumpScheduled = false;
        processNext();
        return true;
    }

    public void processNext()
    {
        if (activeJob != null) return;
        TerrainQueuedJob<QueuedTerrainBake>? job = queue.dequeue();
        if (job == null) return;
        activeJob = job;
        QueuedTerrainBake queued = job.value;
        TerrainBakeWorkerBakeRequest request = queued.request;
        TerrainBakeTimings timings = emptyTimings(workerScope.now());

        yieldForControlMessages();
        if (cancelIfInvalid(job, TerrainBakeCancellationStage.BeforeMaterialize, timings)) return;

        TerrainBakeFrame frame = request.frame;
        MaterializedTerrain terrain = TerrainModel.materializeTerrainGrid(
            request.tiles,
            frame.width,
            frame.height,
            request.elevation,
            new TerrainModelOptions
            {
                bridgeSpans = request.bridgeSpans,
                resolvedWaterLevels = request.waterLevels,
                solidWallRiseAt = (lx, ly, stored) =>
                    request.endlessWallProfile
                        ? TerrainRules.endlessWallRiseAt(frame.i0 + lx, frame.j0 + ly, stored)
                        : TerrainRules.standardWallRiseAt(frame.i0 + lx, frame.j0 + ly, stored),
            },
            materializedScratch);
        materializedScratch = terrain;
        terrain.surface = request.surface;
        terrain.decorations = request.decorations?.map(decoration => decoration.Clone());
        terrain.floorUsage = request.floorUsage;
        timings.materializedAt = workerScope.now();

        yieldForControlMessages();
        if (cancelIfInvalid(job, TerrainBakeCancellationStage.AfterMaterialize, timings)) return;

        if (materialForCell == null || resolverTheme != request.themeKey)
        {
            resolverTheme = request.themeKey;
            materialForCell = TerrainMaterialCompiler.createRunTerrainMaterialResolver(
                request.tileset,
                request.style,
                request.biome.key == "hub");
        }
        Func<TerrainCell, double, TerrainMaterial> resolver = materialForCell;
        TerrainRenderPlan plan = TerrainRenderPlanModule.createTerrainRenderPlan(
            terrain,
            new TerrainRenderPlanOptions
            {
                materialForCell = (cell, _terrain, _water, moisture) => resolver(cell, moisture),
                biomeKey = request.biomeKey,
                effectSeed = request.effectSeed,
                originCellX = frame.originX / frame.tileSize + frame.i0,
                originCellY = frame.originY / frame.tileSize + frame.j0,
            },
            planScratch);
        planScratch = plan;
        timings.plannedAt = workerScope.now();

        yieldForControlMessages();
        if (cancelIfInvalid(job, TerrainBakeCancellationStage.AfterPlan, timings)) return;

        if (geometryTheme != request.themeKey)
        {
            geometryTheme = request.themeKey;
            geometryCompiler.setBiome(
                request.biome,
                request.tileset,
                request.style,
                request.terrainThemePalette,
                request.themeVisuals,
                // A JS number the compiler treats as a count; the detail profile only ever yields small integers.
                (int)request.vegetationBladeCap,
                request.cliffDressingDensity,
                request.visualGrounding);
        }
        TerrainTransferBufferPoolAudit poolBefore = geometryBuffers.audit();
        // `effectSeed` carries the instance salt and structural render-plan noise. Two worlds can own the same tile
        // coordinates, theme and topology hash while still generating different carriers; never let a worker which
        // survives that transition reuse the former world's base layer.
        string structuralIdentity =
            $"{request.themeKey}|{Js.Str(Js.ToUint32(request.structuralHash))}|{Js.Str(Js.ToUint32(request.effectSeed))}";
        string tileCacheKey = $"{Js.Str(request.tx)},{Js.Str(request.ty)}";
        StructuralGeometryEntry? cachedStructural = takeStructuralGeometry(tileCacheKey, structuralIdentity);
        Func<bool> checkpoint = () =>
        {
            yieldForControlMessages();
            return queue.isCurrent(job) && !workerScope.terminated;
        };
        TerrainGeometryPayload completeGeometry;
        TerrainBakeAudit bakeAudit;
        bool reusedStructuralGeometry = false;
        if (cachedStructural != null)
        {
            TerrainGeometryCompileResult dressing = geometryCompiler.buildTransferableTerrainDressingTileCooperatively(
                frame,
                terrain,
                plan,
                checkpoint,
                geometryBuffers,
                request.theme);
            if (dressing.cancelled)
            {
                timings.bakedAt = workerScope.now();
                finishCancelled(job, TerrainBakeCancellationStage.DuringBake, timings, 0);
                return;
            }
            completeGeometry = TerrainGeometryCompilerModule.mergeTerrainGeometryLayers(
                new TerrainGeometryLayers { @base = cachedStructural.geometry, dynamic = dressing.geometry! },
                geometryBuffers);
            geometryBuffers.releaseAll(geometryArrayBuffers(dressing.geometry!));
            bakeAudit = cachedStructural.audit;
            reusedStructuralGeometry = true;
        }
        else
        {
            TerrainGeometryLayerCompileResult built = geometryCompiler.buildTransferableTerrainTileLayersCooperatively(
                frame,
                terrain,
                plan,
                checkpoint,
                16,
                request.theme);
            if (built.cancelled)
            {
                timings.bakedAt = workerScope.now();
                finishCancelled(job, TerrainBakeCancellationStage.DuringBake, timings, 0);
                return;
            }
            rememberStructuralGeometry(tileCacheKey, structuralIdentity, built.layers!.@base, built.audit!);
            completeGeometry = TerrainGeometryCompilerModule.mergeTerrainGeometryLayers(built.layers, geometryBuffers);
            // The base stays worker-resident; the dynamic suffix is only an input to the merged transferable payload.
            // Retain its exact stores for the next dressing-only edit instead of leaving them for GC.
            geometryBuffers.releaseAll(geometryArrayBuffers(built.layers.dynamic));
            bakeAudit = built.audit!;
        }
        // Fluitown comic look: organic terrain form on the fresh merged payload (never on the cached base layer).
        if (TerrainOrganicForm.Enabled) TerrainOrganicForm.apply(completeGeometry, frame, terrain, geometryBuffers);
        // Fluitown comic look: every region paints its rock (WorldRegions.Bake.cs).
        if (WorldRegions.Enabled) WorldRegions.Apply(completeGeometry, frame, terrain);
        timings.bakedAt = workerScope.now();
        var omittedBuffers = new List<Array>();
        float[] omit(float[]? array)
        {
            if (array != null) omittedBuffers.Add(array);
            return Array.Empty<float>();
        }
        // Compact Basic materials consume only position, baked vertex pigment and indices. Do not send normals or
        // the six rich shader lanes merely because the shared compiler produced them: across a six-tile fresh Guest
        // viewport those unreachable arrays accounted for roughly 9 MB of structured-clone/driver traffic.
        TerrainGeometryPayload geometry = request.softwareSafe
            ? new TerrainGeometryPayload
            {
                surface = completeGeometry.surface != null
                    ? new TerrainGeometryPayload.SurfaceLane
                    {
                        position = completeGeometry.surface.position,
                        normal = omit(completeGeometry.surface.normal),
                        color = completeGeometry.surface.color,
                        surface = omit(completeGeometry.surface.surface),
                        emissive = omit(completeGeometry.surface.emissive),
                        ground = omit(completeGeometry.surface.ground),
                        index = completeGeometry.surface.index,
                        bounds = completeGeometry.surface.bounds,
                    }
                    : null,
                water = completeGeometry.water != null
                    ? new TerrainGeometryPayload.WaterLane
                    {
                        position = completeGeometry.water.position,
                        normal = omit(completeGeometry.water.normal),
                        color = completeGeometry.water.color,
                        water = omit(completeGeometry.water.water),
                        fold = omit(completeGeometry.water.fold),
                        reflection = omit(completeGeometry.water.reflection),
                        index = completeGeometry.water.index,
                        bounds = completeGeometry.water.bounds,
                    }
                    : null,
            }
            : completeGeometry;
        if (request.softwareSafe)
        {
            var omitted = new TerrainGeometryPayload
            {
                mist = completeGeometry.mist,
                overlay = completeGeometry.overlay,
                actorWall = completeGeometry.actorWall,
            };
            var released = new List<Array>(omittedBuffers);
            released.AddRange(geometryArrayBuffers(omitted));
            geometryBuffers.releaseAll(released);
        }
        double geometryBytes = terrainGeometryByteLength(geometry);

        // A final task boundary prevents a completed-but-superseded tile from consuming transfer bandwidth. Its exact
        // stores never leave this worker and return directly to the geometry pool.
        yieldForControlMessages();
        if (!queue.isCurrent(job))
        {
            geometryBuffers.releaseAll(geometryArrayBuffers(geometry));
            finishCancelled(job, TerrainBakeCancellationStage.AfterBake, timings, geometryBytes);
            return;
        }
        if (!queue.complete(job))
        {
            geometryBuffers.releaseAll(geometryArrayBuffers(geometry));
            finishCancelled(job, TerrainBakeCancellationStage.AfterBake, timings, geometryBytes);
            return;
        }

        double inputBytes = terrainInputByteLength(request);
        TerrainTransferBufferPoolAudit poolAfter = geometryBuffers.audit();
        TerrainQueueAudit queueAudit = queue.audit();
        workerScope.postMessage(new TerrainBakeWorkerCompletedResponse
        {
            id = request.id,
            serial = request.serial,
            revision = request.revision,
            tx = request.tx,
            ty = request.ty,
            contentHash = request.contentHash,
            effectSeed = request.effectSeed,
            frame = request.frame,
            tiles = request.tiles,
            elevation = request.elevation,
            surface = request.surface,
            theme = request.theme,
            bridgeSpans = request.bridgeSpans,
            waterLevels = request.waterLevels,
            floorUsage = request.floorUsage,
            geometry = geometry,
            audit = bakeAudit,
            workerStartParseMs = workerStartParseMs,
            workerQueueMs = Math.max(0, timings.startedAt - queued.receivedAt),
            materializeMs = timings.materializedAt - timings.startedAt,
            planMs = timings.plannedAt - timings.materializedAt,
            meshMs = timings.bakedAt - timings.plannedAt,
            inputBytes = inputBytes,
            geometryBytes = geometryBytes,
            transferBytes = transferableTerrainInputBytes(request) + geometryBytes,
            staleCpuMs = 0,
            avoidedGeometryTransferBytes = 0,
            reusedGeometryBuffers = poolAfter.hits - poolBefore.hits,
            reusedStructuralGeometry = reusedStructuralGeometry,
            structuralCacheBytes = structuralGeometryCacheBytes,
            poolAllocatedBytes = poolAfter.allocatedBytes,
            poolRetainedBytes = poolAfter.retainedBytes,
            poolPeakRetainedBytes = poolAfter.peakRetainedBytes,
            queueSuperseded = queueAudit.superseded,
            queueCancelled = queueAudit.cancelled,
        });
        activeJob = null;
        schedulePump();
    }

    private bool cancelIfInvalid(
        TerrainQueuedJob<QueuedTerrainBake> job,
        string stage,
        TerrainBakeTimings timings)
    {
        if (queue.isCurrent(job) && !workerScope.terminated) return false;
        finishCancelled(job, stage, timings, 0);
        return true;
    }

    private void finishCancelled(
        TerrainQueuedJob<QueuedTerrainBake> job,
        string stage,
        TerrainBakeTimings timings,
        double avoidedGeometryTransferBytes)
    {
        postCancelled(job.value, stage, timings, avoidedGeometryTransferBytes);
        if (activeJob == job) activeJob = null;
        schedulePump();
    }

    private void postCancelled(
        QueuedTerrainBake queued,
        string cancellationStage,
        TerrainBakeTimings timings,
        double avoidedGeometryTransferBytes = 0)
    {
        TerrainBakeWorkerBakeRequest request = queued.request;
        double inputBytes = terrainInputByteLength(request);
        double now = workerScope.now();
        double materializeMs =
            timings.materializedAt > timings.startedAt ? timings.materializedAt - timings.startedAt : 0;
        double planMs =
            timings.plannedAt > timings.materializedAt ? timings.plannedAt - timings.materializedAt : 0;
        double meshMs = timings.bakedAt > timings.plannedAt ? timings.bakedAt - timings.plannedAt : 0;
        TerrainQueueAudit queueAudit = queue.audit();
        TerrainTransferBufferPoolAudit poolAudit = geometryBuffers.audit();
        workerScope.postMessage(new TerrainBakeWorkerCancelledResponse
        {
            id = request.id,
            serial = request.serial,
            revision = request.revision,
            tx = request.tx,
            ty = request.ty,
            contentHash = request.contentHash,
            effectSeed = request.effectSeed,
            frame = request.frame,
            tiles = request.tiles,
            elevation = request.elevation,
            surface = request.surface,
            theme = request.theme,
            bridgeSpans = request.bridgeSpans,
            waterLevels = request.waterLevels,
            floorUsage = request.floorUsage,
            cancellationStage = cancellationStage,
            workerStartParseMs = workerStartParseMs,
            workerQueueMs = Math.max(
                0,
                (cancellationStage == TerrainBakeCancellationStage.Queued ||
                    cancellationStage == TerrainBakeCancellationStage.OlderRevision
                    ? now
                    : timings.startedAt) - queued.receivedAt),
            materializeMs = materializeMs,
            planMs = planMs,
            meshMs = meshMs,
            inputBytes = inputBytes,
            geometryBytes = avoidedGeometryTransferBytes,
            transferBytes = transferableTerrainInputBytes(request),
            staleCpuMs = materializeMs + planMs + meshMs,
            avoidedGeometryTransferBytes = avoidedGeometryTransferBytes,
            poolAllocatedBytes = poolAudit.allocatedBytes,
            poolRetainedBytes = poolAudit.retainedBytes,
            poolPeakRetainedBytes = poolAudit.peakRetainedBytes,
            queueSuperseded = queueAudit.superseded,
            queueCancelled = queueAudit.cancelled,
        });
    }

    private static TerrainBakeTimings emptyTimings(double startedAt)
    {
        return new TerrainBakeTimings
        {
            startedAt = startedAt,
            materializedAt = startedAt,
            plannedAt = startedAt,
            bakedAt = startedAt,
        };
    }

    private StructuralGeometryEntry? takeStructuralGeometry(string tileKey, string identity)
    {
        StructuralGeometryEntry? cached = structuralGeometryCache.get(tileKey);
        if (cached == null || cached.identity != identity) return null;
        // Map insertion order doubles as a tiny LRU without another allocation-heavy index.
        structuralGeometryCache.delete(tileKey);
        structuralGeometryCache.set(tileKey, cached);
        return cached;
    }

    private void rememberStructuralGeometry(
        string tileKey,
        string identity,
        TerrainGeometryPayload geometry,
        TerrainBakeAudit audit)
    {
        double bytes = terrainGeometryByteLength(geometry);
        if (bytes <= 0 || bytes > STRUCTURAL_GEOMETRY_CACHE_BYTES) return;
        StructuralGeometryEntry? previous = structuralGeometryCache.get(tileKey);
        if (previous != null)
        {
            structuralGeometryCache.delete(tileKey);
            structuralGeometryCacheBytes -= previous.bytes;
        }
        structuralGeometryCache.set(
            tileKey,
            new StructuralGeometryEntry { identity = identity, geometry = geometry, audit = audit, bytes = bytes });
        structuralGeometryCacheBytes += bytes;
        while (structuralGeometryCacheBytes > STRUCTURAL_GEOMETRY_CACHE_BYTES)
        {
            string? oldestKey = null;
            foreach (string key in structuralGeometryCache.keys())
            {
                oldestKey = key;
                break;
            }
            if (oldestKey == null) break;
            StructuralGeometryEntry oldest = structuralGeometryCache.get(oldestKey)!;
            structuralGeometryCache.delete(oldestKey);
            structuralGeometryCacheBytes -= oldest.bytes;
        }
    }

    /// <summary>The `inputBytes` sum shared by completed and cancelled responses.</summary>
    private static double terrainInputByteLength(TerrainBakeWorkerBakeRequest request)
    {
        return
            request.tiles.Length +
            request.elevation.Length +
            request.surface.Length +
            request.theme.Length +
            request.bridgeSpans.Length +
            (double)request.waterLevels.Length * 4 +
            (request.floorUsage?.Length ?? 0);
    }

    private static List<Array> terrainInputViews(TerrainBakeWorkerBakeRequest request)
    {
        var views = new List<Array>
        {
            request.tiles,
            request.elevation,
            request.surface,
            request.theme,
            request.bridgeSpans,
            request.waterLevels,
        };
        if (request.floorUsage != null) views.Add(request.floorUsage);
        return views;
    }

    /// <summary>`view.buffer instanceof ArrayBuffer` is always true for a C# array.</summary>
    private static List<Array> transferableTerrainInputs(TerrainBakeWorkerBakeRequest request)
    {
        return terrainInputViews(request);
    }

    private static double transferableTerrainInputBytes(TerrainBakeWorkerBakeRequest request)
    {
        double bytes = 0;
        foreach (Array buffer in transferableTerrainInputs(request)) bytes += TerrainTransferBufferPool.ByteLength(buffer);
        return bytes;
    }

    /// <summary>
    /// `await yieldForControlMessages()`: run every `onmessage` task the owner has queued meanwhile (cancellations,
    /// newer revisions, recycled stores). See the port note at the top of the file.
    /// </summary>
    private void yieldForControlMessages()
    {
        while (workerScope.tryTakeMessage(out TerrainBakeWorkerRequest message)) onmessage(message);
    }

    /// <summary>Every store of a payload in lane order (zero-length stores included, like the original).</summary>
    internal static List<Array> geometryArrayBuffers(TerrainGeometryPayload payload)
    {
        var transfer = new List<Array>();
        void add(Array? array)
        {
            if (array != null) transfer.Add(array);
        }
        add(payload.surface?.position);
        add(payload.surface?.normal);
        add(payload.surface?.color);
        add(payload.surface?.surface);
        add(payload.surface?.emissive);
        add(payload.surface?.ground);
        add(payload.surface?.index);
        add(payload.water?.position);
        add(payload.water?.normal);
        add(payload.water?.color);
        add(payload.water?.water);
        add(payload.water?.fold);
        add(payload.water?.reflection);
        add(payload.water?.index);
        add(payload.mist?.position);
        add(payload.mist?.color);
        add(payload.mist?.mist);
        add(payload.mist?.index);
        add(payload.overlay?.position);
        add(payload.overlay?.color);
        add(payload.overlay?.index);
        add(payload.actorWall?.position);
        add(payload.actorWall?.index);
        return transfer;
    }

    internal static double terrainGeometryByteLength(TerrainGeometryPayload payload)
    {
        double bytes = 0;
        foreach (Array buffer in geometryArrayBuffers(payload)) bytes += TerrainTransferBufferPool.ByteLength(buffer);
        return bytes;
    }
}
