// Port of packages/client/src/dungeon.ts — keep in lockstep with the original.
//
// PORT NOTES (read before editing):
//  * Module-level functions/constants live in `ClientDungeonModule` (the natural name `Dungeon` is already the mapped
//    module class of shared/src/protocol/dungeon.ts in Fluitown.Domain). The class body is split into several
//    `partial` blocks so that the TS declaration order (functions, interfaces, functions, class) is preserved.
//  * Browser globals are injected through <see cref="ClientDungeonHost"/>: `new Worker(...)` → host factories that
//    return <see cref="IClientWorker{TRequest,TResponse}"/> (null factory ≙ `typeof Worker === 'undefined'`),
//    `navigator.hardwareConcurrency/deviceMemory`, `requestIdleCallback`/`setTimeout(fn, 0)` → the host's
//    <see cref="ClientEventLoop"/>, `performance.now()` → `host.now`. The scheduling code itself is literal.
//  * ClientTreeLife is NOT ported (it needs the live server): every tree is whole. The studio is terrain only: the
//    original's colony structures, their carriers and fields are not part of it.
//  * JS NaN stored into a Float32Array has the bit pattern 0x7FC00000 in V8; .NET's `double.NaN` is the negative
//    quiet NaN, so NaN stores into float[] go through <see cref="ClientDungeonModule.jsF32"/>.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.Descriptor;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.EndlessCoordinates;
using static Fluitown.Domain.Grid;
using static Fluitown.Domain.RngModule;
using static Fluitown.Domain.TerrainArtifactModule;
using static Fluitown.Domain.TerrainBridgeSpanModule;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainRules;
using static Fluitown.Render.TerrainAmbientPlanCache;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class ClientDungeonModule
{
    /// <summary>`hash` is a JS number; an empty/undefined value returns it unchanged (possibly still a uint32 literal).</summary>
    internal static double hashAuthoredString(double hash, string? value)
    {
        double next = hash;
        for (int i = 0; i < (value?.Length ?? 0); i++)
            next = Math.imul(Js.ToInt32(next) ^ value![i], 0x01000193);
        return next;
    }

    /// <summary>Spatial content signature for mutable finite editor layouts. Only sampled bakes rebuild after a stroke.</summary>
    internal static int authoredLayoutSignatureForBounds(
        DungeonLayout layout,
        byte[] bridgeSpans,
        double left,
        double top,
        double right,
        double bottom,
        bool includeDressing = true)
    {
        double loX = Math.max(0, Math.floor((Math.min(left, right) - layout.originX) / layout.tileSize));
        double hiX = Math.min(
            layout.width - 1,
            Math.floor((Math.max(left, right) - layout.originX) / layout.tileSize));
        double loY = Math.max(0, Math.floor((Math.min(top, bottom) - layout.originY) / layout.tileSize));
        double hiY = Math.min(
            layout.height - 1,
            Math.floor((Math.max(top, bottom) - layout.originY) / layout.tileSize));
        // `let hash = 0x811c9dc5` is a JS number; every `mix` folds it through ToInt32, and hashAuthoredString may
        // hand back a non-int32 number, so the accumulator stays a double exactly like the original.
        double hash = 0x811c9dc5;
        void mix(double value)
        {
            hash = Math.imul(Js.ToInt32(hash) ^ Js.ToInt32(value), 0x01000193);
        }
        mix(layout.width);
        mix(layout.height);
        mix(Math.round(layout.originX * 16));
        mix(Math.round(layout.originY * 16));
        mix(Math.round(layout.tileSize * 16));
        mix(loX);
        mix(loY);
        mix(hiX);
        mix(hiY);
        DungeonTerrainLayers? terrain = layout.terrain;
        List<double> themeHashes = ((IReadOnlyList<string>?)terrain?.themePalette ?? Array.Empty<string>()).map((theme) =>
            hashAuthoredString(0x811c9dc5, theme));
        if (loX <= hiX && loY <= hiY)
        {
            for (double ty = loY; ty <= hiY; ty++)
            {
                int index = (int)(ty * layout.width + loX);
                for (double tx = loX; tx <= hiX; tx++, index++)
                {
                    mix(Js.InRange(layout.tiles, index) ? layout.tiles[index] : 0);
                    // A Bridge's visible lower volume may be decided by Water/Chasm evidence outside these bounds. Hash
                    // the complete-layout answer sampled at this cell so that remote editor changes invalidate precisely
                    // the clipped bridge bakes whose meaning changed.
                    mix(Js.InRange(bridgeSpans, index) ? bridgeSpans[index] : TERRAIN_BRIDGE_SPAN_NONE);
                    mix(layout.elevation != null && Js.InRange(layout.elevation, index) ? layout.elevation[index] : 0);
                    mix(terrain?.surface != null && Js.InRange(terrain.surface, index) ? terrain.surface[index] : 0);
                    mix(terrain?.variant != null && Js.InRange(terrain.variant, index) ? terrain.variant[index] : 0);
                    mix(terrain?.floorUsage != null && Js.InRange(terrain.floorUsage, index) ? terrain.floorUsage[index] : 0);
                    int themeIndex = terrain?.themeIndex != null && Js.InRange(terrain.themeIndex, index)
                        ? terrain.themeIndex[index]
                        : TERRAIN_THEME_INHERIT;
                    mix(
                        themeIndex == TERRAIN_THEME_INHERIT
                            ? TERRAIN_THEME_INHERIT
                            : (Js.InRange(themeHashes, themeIndex) ? themeHashes[themeIndex] : themeIndex));
                }
            }
        }
        if (!includeDressing) return Js.ToInt32(hash);
        foreach (TerrainDecorationPlacement decoration in (IReadOnlyList<TerrainDecorationPlacement>?)terrain?.decorations ?? Array.Empty<TerrainDecorationPlacement>())
        {
            if (decoration.tx < loX || decoration.tx > hiX || decoration.ty < loY || decoration.ty > hiY)
                continue;
            mix(decoration.tx);
            mix(decoration.ty);
            mix(decoration.seed);
            // A finite authored layout can republish the same tree in another regrowth stage without changing its
            // identity. The stage changes the baked geometry, so it is part of the spatial content signature too;
            // omitting it left already-visible regions stuck as stumps while newly entered regions drew the crown.
            mix((decoration.growthStage ?? 4) + 1);
            hash = hashAuthoredString(hash, decoration.kind);
            hash = hashAuthoredString(hash, decoration.themeKey);
        }
        foreach (TerrainMarker marker in (IReadOnlyList<TerrainMarker>?)terrain?.markers ?? Array.Empty<TerrainMarker>())
        {
            if (marker.tx < loX || marker.tx > hiX || marker.ty < loY || marker.ty > hiY) continue;
            mix(marker.tx);
            mix(marker.ty);
            mix(marker.type);
            mix(marker.length ?? 0);
            hash = hashAuthoredString(hash, marker.id);
        }
        return Js.ToInt32(hash);
    }

    /// <summary>Extra asynchronously generated chunk ring outside the viewport-complete core.</summary>
    internal const int ENDLESS_BUFFER_R = 1;
    /// <summary>
    /// Fallback synchronous radius used until a renderer supplies its actual viewport. It must stay ≥1 so local
    /// collision and a first-frame camera never sample a missing neighbour while the player is near a chunk edge.
    /// </summary>
    internal const int ENDLESS_DEFAULT_CORE_R = 1;
    // Extra synchronous geometry around the reported viewport. Terrain is baked in larger tiles with neighbour
    // context and tall props immediately outside the screen can cast shadows into it. One complete endless chunk
    // covers both concerns without coupling the dungeon mirror to renderer-private bake constants.
    /// <summary>Number of missing chunks to generate per idle pump when Web Workers are unavailable.</summary>
    internal const int ENDLESS_IDLE_GEN_BATCH = 1;

    /// <summary>
    /// Chunk generation is CPU-heavy and independent per coordinate. Keep enough lanes to make the guarded camera
    /// core arrive in one generation wave on desktop, while reserving cores and memory bandwidth for rendering on
    /// small devices. This is deliberately separate from the terrain-plan pool: generated chunks become visible
    /// before optional ambient planning starts.
    /// </summary>
    /// <param name="hardwareConcurrency">`navigator.hardwareConcurrency`; null (TS: undefined) takes the default 4.</param>
    /// <param name="deviceMemoryGiB">`navigator.deviceMemory`; null (TS: undefined) takes the default 4.</param>
    public static int endlessChunkWorkerConcurrency(
        double? hardwareConcurrency = null,
        double? deviceMemoryGiB = null)
    {
        double hardware = hardwareConcurrency ?? 4;
        double deviceMemory = deviceMemoryGiB ?? 4;
        double cores = Number.isFinite(hardware) ? Math.max(1, hardware) : 4;
        double memory = Number.isFinite(deviceMemory) ? Math.max(1, deviceMemory) : 4;
        if (cores <= 2 || memory <= 2) return 1;
        // Terrain geometry planning begins as soon as generated chunks arrive and owns up to four more workers.
        // Leave it and the presentation thread real CPU headroom: the production saturation benchmark is faster at
        // two generation lanes on 8-logical-core hardware than at three/four contending lanes.
        if (cores < 12 || memory <= 4) return 2;
        if (cores < 16 || memory <= 8) return 3;
        return 4;
    }

    /// <summary>Recently evicted immutable chunks. This absorbs viewport-boundary reversals and, importantly, the idle-pump
    /// race where a just-prefetched edge chunk leaves the moving window before the following render update.</summary>
    internal const int ENDLESS_CHUNK_CACHE_LIMIT = 96;
}

/// <summary>Minimal renderer-independent world rectangle accepted by <see cref="ClientDungeon.update"/>.</summary>
public sealed class DungeonViewport
{
    public double left;
    public double right;
    public double top;
    public double bottom;

    public DungeonViewport() { }
}

public static partial class ClientDungeonModule
{
    // ── port helpers (not in the TS) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The Float32 bit pattern V8 stores for NaN (the canonical quiet NaN 0x7FC00000).</summary>
    internal static readonly float JS_NAN_F32 = BitConverter.Int32BitsToSingle(0x7FC00000);

    /// <summary>A JS number stored into a Float32Array: rounded to float, NaN canonicalised as V8 does.</summary>
    internal static float jsF32(double value) => double.IsNaN(value) ? JS_NAN_F32 : (float)value;

    /// <summary>`performance.now()` fallback of <see cref="ClientDungeonHost.now"/>.</summary>
    private static readonly Stopwatch performanceClock = Stopwatch.StartNew();

    internal static double performanceNow() => performanceClock.Elapsed.TotalMilliseconds;
}

/// <summary>A value (the tile manager asks for it every frame): no allocation.</summary>
public readonly struct EndlessTerrainCoverage
{
    public readonly int loaded;
    public readonly int required;
    public readonly bool complete;

    public EndlessTerrainCoverage(int loaded, int required, bool complete)
    {
        this.loaded = loaded;
        this.required = required;
        this.complete = complete;
    }
}

/// <summary>
/// Client mirror of the active dungeon's geometry — one model for BOTH modes, fed by authoritative layouts:
///  - FINITE  (sealed raids): the authoritative bounded layout arrives once via DungeonGeometry.
///  - ENDLESS (the standard run): the server publishes the exact collision-resident layouts and removals.
///    Offline tools may still regenerate deterministic chunks from the instance-id descriptor.
/// The renderer and minimap simply iterate <see cref="loaded"/>; a finite raid is fully visible from entry.
///
/// THREADING: an instance is main-thread state (the browser's single JS thread). Every method must be called from
/// the thread that runs <see cref="ClientDungeonHost.loop"/>; worker lanes only ever run the pure job bodies
/// (<see cref="EndlessChunkWorker.onmessage"/>, <see cref="TerrainAmbientPlanWorker.onmessage"/>) and hand their
/// results back through that loop.
/// </summary>
public sealed class ClientDungeon
{
    private sealed class BridgeSpanCacheEntry
    {
        public int version;
        public byte[] spans = null!;
    }

    private sealed class WaterLevelCacheEntry
    {
        public int version;
        public float[] levels = null!;
    }

    private sealed class GenerationWorkerRequest
    {
        public int id;
        public int serial;
        public DungeonDescriptor descriptor = null!;
        public int cx;
        public int cy;
        public double startedAt;
    }

    private sealed class AmbientPlanRequest
    {
        public int id;
        public int serial;
        public double key;
        public DungeonLayout layout = null!;
        public double startedAt;
    }

    /// <summary>The injected browser environment (workers, event loop, navigator, clock). Not in the TS.</summary>
    public readonly ClientDungeonHost host;

    public ClientDungeon(ClientDungeonHost? host = null)
    {
        this.host = host ?? new ClientDungeonHost();
    }

    public DungeonDescriptor? descriptor;
    /// <summary>Exact instance salt used by shared visual planning; kept separately because descriptors retain only the
    /// procedural seed fields.</summary>
    private string instanceId = "";
    /// <summary>Hashed numeric seed of the active space — the elevation field's seed (matches the geometry seed).</summary>
    private double? seedNum;
    /// <summary>Biome elevation skew (bias/gain). Neutral unless the renderer supplies a biome profile on entry.</summary>
    private ElevationProfile elevationProfile = NEUTRAL_ELEVATION_PROFILE;
    private JsMap<double, DungeonLayout> chunks = new();
    private readonly JsMap<double, DungeonLayout> chunkCache = new();
    private readonly List<DungeonLayout> loadedSnapshot = new();
    private int chunkVersion = 0;
    /// <summary>Complete-layout Bridge semantics, invalidated by the same terrain epoch as tile/elevation edits.</summary>
    private readonly ConditionalWeakTable<DungeonLayout, BridgeSpanCacheEntry> bridgeSpanCache = new();
    /// <summary>Complete-layout hydrology paired with the Bridge classification above.</summary>
    private readonly ConditionalWeakTable<DungeonLayout, WaterLevelCacheEntry> waterLevelCache = new();
    private readonly HashSet<double> keepScratch = new();
    private readonly List<int> missCx = new();
    private readonly List<int> missCy = new();
    private double winLoX = double.NaN;
    private double winHiX = double.NaN;
    private double winLoY = double.NaN;
    private double winHiY = double.NaN;
    private double coreLoX = double.NaN;
    private double coreHiX = double.NaN;
    private double coreLoY = double.NaN;
    private double coreHiY = double.NaN;
    /// <summary>Last camera rectangle relative to the player. Collision prediction calls update without a rectangle before
    /// the render step; retaining these offsets prevents that call from shrinking a zoomed-out render window.</summary>
    private double viewOffsetLeft = double.NaN;
    private double viewOffsetRight = double.NaN;
    private double viewOffsetTop = double.NaN;
    private double viewOffsetBottom = double.NaN;
    /// <summary>Player position paired with the retained camera offsets. Prediction/collision may use a slightly older
    /// authoritative position before the next render; keeping the streaming anchor stable prevents the two
    /// positions from alternately evicting opposite chunk columns when they straddle a chunk boundary.</summary>
    private double viewAnchorX = double.NaN;
    private double viewAnchorY = double.NaN;
    /// <summary>The one cold-start core allowed to build under the opening presentation cover. This is an epoch flag,
    /// not `chunks.size`: a fast pan may temporarily evict every old chunk while workers catch up.</summary>
    private bool initialCoreSeeded = false;
    private int pendingChunks = 0;
    private bool outerGenScheduled = false;
    private int generationSerial = 0;
    private int generationRequestId = 0;
    private readonly List<IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse>> generationWorkers = new();
    private readonly Dictionary<IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse>, GenerationWorkerRequest> workerRequests =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>Chunk key -> request id. Prevents parallel lanes from duplicating a coordinate after a camera turn.</summary>
    private readonly Dictionary<double, int> generationInFlight = new();
    private bool workerUnavailable = false;
    private IClientWorker<TerrainAmbientPlanWorkerRequest, TerrainAmbientPlanWorkerResponse>? ambientPlanWorker;
    private bool ambientPlanWorkerUnavailable = false;
    private readonly JsMap<double, DungeonLayout> ambientPlanQueue = new();
    private AmbientPlanRequest? ambientPlanRequest;

    private byte[] bridgeSpansForLayout(DungeonLayout layout)
    {
        if (bridgeSpanCache.TryGetValue(layout, out BridgeSpanCacheEntry? cached) && cached.version == chunkVersion)
            return cached.spans;
        byte[] spans = classifyTerrainBridgeSpans(layout.tiles, layout.width, layout.height);
        bridgeSpanCache.AddOrUpdate(layout, new BridgeSpanCacheEntry { version = chunkVersion, spans = spans });
        return spans;
    }

    private float[] waterLevelsForLayout(DungeonLayout layout)
    {
        if (waterLevelCache.TryGetValue(layout, out WaterLevelCacheEntry? cached) && cached.version == chunkVersion)
            return cached.levels;
        double[] derived = deriveTerrainWaterLevels(
            layout.tiles,
            bridgeSpansForLayout(layout),
            layout.elevation,
            layout.width,
            layout.height,
            new TerrainModelOptions());
        // `Float32Array.from(derived)`.
        var levels = new float[derived.Length];
        for (int i = 0; i < derived.Length; i++) levels[i] = ClientDungeonModule.jsF32(derived[i]);
        waterLevelCache.AddOrUpdate(layout, new WaterLevelCacheEntry { version = chunkVersion, levels = levels });
        return levels;
    }

    /// <summary>Finite raid state: the sealed boss-gate door(s) + objective nests already cleared (empty for endless).</summary>
    public JsSet<int> sealedDoors = new();
    public JsSet<int> clearedRooms = new();

    public bool active => descriptor != null;

    public bool isEndless => descriptor?.mode == DungeonMode.Endless;

    /// <summary>
    /// Terrain residency. It equals <see cref="isEndless"/> now: the ancestor project also streamed a bounded
    /// authored Hub through this path (`DungeonDescriptor.streamed`), while this project's Hub is one finite
    /// authored garden that is built whole the moment its instance id arrives.
    /// </summary>
    public bool isStreamed => isEndless;

    public IReadOnlyList<DungeonLayout> loaded => loadedSnapshot;

    public int loadedVersion => chunkVersion;

    private double streamTileSize => TILE_SIZE;

    private double streamChunkWorld => ENDLESS_CHUNK_WORLD;

    private int streamChunkCoordX(double x)
    {
        return endlessChunkCoordX(x);
    }

    private int streamChunkCoordY(double y)
    {
        return endlessChunkCoordY(y);
    }

    private static readonly IReadOnlyList<string> EMPTY_THEME_PALETTE = Array.Empty<string>();

    /// <summary>Palette used by the authored finite layout's dense theme layer. Endless layouts inherit one run biome.</summary>
    public IReadOnlyList<string> terrainThemePalette =>
        (loadedSnapshot.Count > 0 ? loadedSnapshot[0].terrain?.themePalette : null) ?? EMPTY_THEME_PALETTE;

    /// <summary>Stable procedural-effect salt for a sampled render region. Endless chunks reuse local tile coordinates in
    /// their materialized patches, so the render plan also needs the instance seed + global region origin.</summary>
    /// <returns>A uint32 as a JS number.</returns>
    public double effectSeedForRegion(double i0, double j0)
    {
        int h = Js.ToInt32(seedNum ?? 0);
        h = Math.imul(h ^ Js.ToInt32(i0), 0x45d9f3b);
        h = Math.imul(h ^ (int)((uint)h >> 16) ^ Js.ToInt32(j0), 0x45d9f3b);
        return (uint)(h ^ (int)((uint)h >> 16));
    }

    /// <summary>
    /// Change signature for everything inside a world-space bake region that can change what it looks like.
    ///
    /// Two facts, and the bake is stale when either of them moves:
    ///
    ///  1. **Chunk membership.** Endless generation warms a 5x5 chunk window around the camera over multiple
    ///     frames; most outer-buffer chunks are outside the padded bake and should not invalidate the visible
    ///     terrain when they arrive. Both the coordinate and whether it is present are hashed, so a missing
    ///     in-view chunk becoming available still forces the correct redraw.
    ///  2. **The world's edits to that ground** — a mine floor, a plank over water. They arrive on their own lane
    ///     and are applied into the chunk's own arrays, which the baker reads directly; what it cannot see for
    ///     itself is *that they changed*, which is what the epoch says.
    ///
    /// Both are read **over the region**, never globally: an edit re-bakes the ground it touched, not the whole
    /// visible world.
    /// </summary>
    /// <returns>A signed int32 (`| 0`).</returns>
    public int chunkSignatureForBounds(double left, double top, double right, double bottom)
    {
        DungeonDescriptor? d = descriptor;
        if (d == null) return 0;
        if (!isStreamed)
        {
            DungeonLayout? layout = chunks.get(0);
            int @base =
                d.local == true && layout != null
                    ? ClientDungeonModule.authoredLayoutSignatureForBounds(
                        layout,
                        bridgeSpansForLayout(layout),
                        left,
                        top,
                        right,
                        bottom)
                    : chunkVersion;
            return Math.imul(@base, 0x01000193);
        }
        double minX = Math.min(left, right);
        double maxX = Math.max(left, right);
        double minY = Math.min(top, bottom);
        double maxY = Math.max(top, bottom);
        int loX = streamChunkCoordX(minX);
        int hiX = streamChunkCoordX(maxX);
        int loY = streamChunkCoordY(minY);
        int hiY = streamChunkCoordY(maxY);
        int h = unchecked((int)0x811c9dc5);
        for (int cy = loY; cy <= hiY; cy++)
        {
            for (int cx = loX; cx <= hiX; cx++)
            {
                double key = endlessChunkKey(cx, cy);
                // `h ^ key`: the chunk key is a ~2^41 number; ToInt32 keeps its low 32 bits.
                h = Math.imul(h ^ Js.ToInt32(key), 0x01000193);
                h = Math.imul(h ^ (chunks.has(key) ? 1 : 0), 0x01000193);
            }
        }
        return h;
    }

    /// <summary>
    /// Signature of the expensive immutable compiler layer: terrain carriers, hydrology, themes and floor usage.
    /// Tree-life and authored dressing changes deliberately do not move it, allowing
    /// the bake worker to retain structural geometry and rebuild only its much smaller decoration suffix.
    /// </summary>
    /// <returns>A signed int32 (`| 0`).</returns>
    public int terrainStructuralSignatureForBounds(
        double left,
        double top,
        double right,
        double bottom)
    {
        DungeonDescriptor? d = descriptor;
        if (d == null) return 0;
        if (!isStreamed)
        {
            DungeonLayout? layout = chunks.get(0);
            double ground =
                d.local == true && layout != null
                    ? ClientDungeonModule.authoredLayoutSignatureForBounds(
                        layout,
                        bridgeSpansForLayout(layout),
                        left,
                        top,
                        right,
                        bottom,
                        false)
                    : layout != null
                        ? ClientDungeonModule.hashAuthoredString(0x811c9dc5, $"{Js.Str(layout.seed)}:{Js.Str(layout.width)}:{Js.Str(layout.height)}")
                        : 0;
            return Math.imul(Js.ToInt32(ground), 0x01000193);
        }
        double minX = Math.min(left, right);
        double maxX = Math.max(left, right);
        double minY = Math.min(top, bottom);
        double maxY = Math.max(top, bottom);
        int loX = streamChunkCoordX(minX);
        int hiX = streamChunkCoordX(maxX);
        int loY = streamChunkCoordY(minY);
        int hiY = streamChunkCoordY(maxY);
        int hash = unchecked((int)0x811c9dc5);
        for (int cy = loY; cy <= hiY; cy++)
        {
            for (int cx = loX; cx <= hiX; cx++)
            {
                double key = endlessChunkKey(cx, cy);
                hash = Math.imul(hash ^ Js.ToInt32(key), 0x01000193);
                hash = Math.imul(hash ^ (chunks.has(key) ? 1 : 0), 0x01000193);
            }
        }
        return hash;
    }

    /// <summary>Exact authoritative residency over a renderer rectangle. This is intentionally separate from sampling:
    /// missing chunks remain collision-solid, but must never be baked and presented as final visual terrain.</summary>
    public EndlessTerrainCoverage coverageForBounds(
        double left,
        double top,
        double right,
        double bottom)
    {
        if (!isStreamed) return new EndlessTerrainCoverage(loaded: 0, required: 0, complete: true);
        int loX = streamChunkCoordX(Math.min(left, right));
        int hiX = streamChunkCoordX(Math.max(left, right));
        int loY = streamChunkCoordY(Math.min(top, bottom));
        int hiY = streamChunkCoordY(Math.max(top, bottom));
        int required = (hiX - loX + 1) * (hiY - loY + 1);
        int loaded = 0;
        for (int cy = loY; cy <= hiY; cy++)
        {
            for (int cx = loX; cx <= hiX; cx++)
            {
                if (chunks.has(endlessChunkKey(cx, cy))) loaded++;
            }
        }
        return new EndlessTerrainCoverage(loaded, required, loaded == required);
    }

    /// <summary>
    /// The STABLE world origin of the cell lattice the terrain bake phases its grid to. Endless runs stream chunks
    /// in/out under the player, so <see cref="loaded"/>`[0]` (the first map entry) changes as the trailing chunk is
    /// evicted — anchoring the bake to it shifted every cell index, which silently re-hashed all the per-cell floor
    /// decoration (the "deco circles" appeared to rearrange while walking, even though the geometry never moved).
    /// The endless lattice has ONE fixed global origin (<see cref="ENDLESS_GRID_ORIGIN"/>); a finite raid is a single
    /// fixed chunk. Either way this never changes for the life of the space, so the decoration hash is stable.
    /// </summary>
    public double gridOriginX
    {
        get
        {
            if (descriptor?.mode == DungeonMode.Endless) return ENDLESS_GRID_ORIGIN;
            return chunks.get(0)?.originX ?? 0;
        }
    }

    public double gridOriginY
    {
        get
        {
            if (descriptor?.mode == DungeonMode.Endless) return ENDLESS_GRID_ORIGIN;
            return chunks.get(0)?.originY ?? 0;
        }
    }

    /// <summary>
    /// Batch-sample a grid-aligned terrain region for the renderer. The old bake path asked `tileAt`,
    /// `surfaceAt`, `variantAt` and `elevationAt` separately for every cell, which repeats chunk-coordinate math
    /// and Map lookups four times per bake cell. This fills the renderer's typed scratch buffers in one pass.
    ///
    /// `i0/j0` are global cell coordinates relative to <see cref="gridOriginX"/>/<see cref="gridOriginY"/>; `tileSize` is the
    /// active layout tile size. Returns the highest sampled ground elevation.
    /// </summary>
    /// <param name="elevationOut">`TerrainElevationLayer` (Int8Array).</param>
    public double sampleRegionInto(
        int i0,
        int j0,
        int width,
        int height,
        double tileSize,
        byte[] tileOut,
        sbyte[] elevationOut,
        byte[] surfaceOut,
        byte[] variantOut,
        byte[]? themeOut = null,
        byte[]? bridgeSpanOut = null,
        float[]? waterLevelOut = null)
    {
        // A terrain bake is smaller than an owning chunk. Classify each participating complete layout once so a
        // wide deck cannot lose the Water/Chasm evidence which happens to sit outside the bake's context border.
        byte bridgeSpanAt(DungeonLayout layout, int localIndex)
        {
            byte[] spans = bridgeSpansForLayout(layout);
            return Js.InRange(spans, localIndex) ? spans[localIndex] : (byte)TERRAIN_BRIDGE_SPAN_NONE;
        }
        DungeonDescriptor? d = descriptor;
        if (d == null || seedNum == null)
        {
            int count = width * height;
            tileOut.fill((byte)TileType.Solid, 0, count);
            elevationOut.fill((sbyte)0, 0, count);
            surfaceOut.fill((byte)TerrainSurface.Stone, 0, count);
            variantOut.fill((byte)0, 0, count);
            themeOut?.fill((byte)TERRAIN_THEME_INHERIT, 0, count);
            bridgeSpanOut?.fill((byte)TERRAIN_BRIDGE_SPAN_NONE, 0, count);
            waterLevelOut?.fill(ClientDungeonModule.JS_NAN_F32, 0, count);
            return 0;
        }
        double seed = seedNum.Value;

        double ox = gridOriginX;
        double oy = gridOriginY;
        double maxElevation = 0;

        if (isStreamed)
        {
            for (int y = 0; y < height; y++)
            {
                int gj = j0 + y;
                double worldY = oy + gj * tileSize + tileSize * 0.5;
                int cy = streamChunkCoordY(worldY);
                for (int x = 0; x < width; x++)
                {
                    int gi = i0 + x;
                    double worldX = ox + gi * tileSize + tileSize * 0.5;
                    int idx = y * width + x;
                    int cx = streamChunkCoordX(worldX);
                    DungeonLayout? ch = chunks.get(endlessChunkKey(cx, cy));
                    if (ch == null)
                    {
                        // Missing server residency is collision-solid. Render the same visible rock truth instead of the
                        // old neutral floor placeholder, which advertised a walkable surface in front of a blocked chunk.
                        tileOut[idx] = TileType.Solid;
                        elevationOut[idx] = 0;
                        surfaceOut[idx] = TerrainSurface.Stone;
                        variantOut[idx] = 0;
                        if (themeOut != null) themeOut[idx] = TERRAIN_THEME_INHERIT;
                        if (bridgeSpanOut != null) bridgeSpanOut[idx] = TERRAIN_BRIDGE_SPAN_NONE;
                        if (waterLevelOut != null) waterLevelOut[idx] = ClientDungeonModule.JS_NAN_F32;
                    }
                    else
                    {
                        double tx = Math.floor((worldX - ch.originX) / ch.tileSize);
                        double ty = Math.floor((worldY - ch.originY) / ch.tileSize);
                        bool inBounds = tx >= 0 && ty >= 0 && tx < ch.width && ty < ch.height;
                        if (!inBounds)
                        {
                            tileOut[idx] = TileType.Solid;
                            elevationOut[idx] = 0;
                            surfaceOut[idx] = TerrainSurface.Stone;
                            variantOut[idx] = 0;
                            if (themeOut != null) themeOut[idx] = TERRAIN_THEME_INHERIT;
                            if (bridgeSpanOut != null) bridgeSpanOut[idx] = TERRAIN_BRIDGE_SPAN_NONE;
                            if (waterLevelOut != null) waterLevelOut[idx] = ClientDungeonModule.JS_NAN_F32;
                        }
                        else
                        {
                            int local = (int)(ty * ch.width + tx);
                            tileOut[idx] = ch.tiles[local];
                            elevationOut[idx] =
                                ch.elevation != null && Js.InRange(ch.elevation, local)
                                    ? ch.elevation[local]
                                    : Js.I8(elevationLevelAt(seed, gi, gj, elevationProfile));
                            surfaceOut[idx] = Js.U8(terrainSurfaceAt(ch, (int)tx, (int)ty));
                            variantOut[idx] = Js.U8(terrainVariantAt(ch, (int)tx, (int)ty));
                            if (themeOut != null) themeOut[idx] = TERRAIN_THEME_INHERIT;
                            if (bridgeSpanOut != null) bridgeSpanOut[idx] = bridgeSpanAt(ch, local);
                            if (waterLevelOut != null) waterLevelOut[idx] = waterLevelsForLayout(ch)[local];
                        }
                    }
                    if (elevationOut[idx] > maxElevation) maxElevation = elevationOut[idx];
                }
            }
            return maxElevation;
        }

        DungeonLayout? single = chunks.size == 1 ? loadedSnapshot.at(0) : null;
        for (int y = 0; y < height; y++)
        {
            int gj = j0 + y;
            double worldY = oy + gj * tileSize + tileSize * 0.5;
            for (int x = 0; x < width; x++)
            {
                int gi = i0 + x;
                double worldX = ox + gi * tileSize + tileSize * 0.5;
                int idx = y * width + x;
                DungeonLayout? ch = single;
                if (ch != null)
                {
                    if (worldX < ch.originX || worldY < ch.originY) ch = null;
                    else
                    {
                        double stx = Math.floor((worldX - ch.originX) / ch.tileSize);
                        double sty = Math.floor((worldY - ch.originY) / ch.tileSize);
                        if (stx < 0 || sty < 0 || stx >= ch.width || sty >= ch.height) ch = null;
                    }
                }
                if (ch == null)
                {
                    foreach (DungeonLayout candidate in chunks.values())
                    {
                        if (worldX < candidate.originX || worldY < candidate.originY) continue;
                        double ctx = Math.floor((worldX - candidate.originX) / candidate.tileSize);
                        double cty = Math.floor((worldY - candidate.originY) / candidate.tileSize);
                        if (ctx < 0 || cty < 0 || ctx >= candidate.width || cty >= candidate.height) continue;
                        ch = candidate;
                        break;
                    }
                }
                if (ch == null)
                {
                    tileOut[idx] = TileType.Solid;
                    elevationOut[idx] = 0;
                    surfaceOut[idx] = TerrainSurface.Stone;
                    variantOut[idx] = 0;
                    if (themeOut != null) themeOut[idx] = TERRAIN_THEME_INHERIT;
                    if (bridgeSpanOut != null) bridgeSpanOut[idx] = TERRAIN_BRIDGE_SPAN_NONE;
                    if (waterLevelOut != null) waterLevelOut[idx] = ClientDungeonModule.JS_NAN_F32;
                    continue;
                }
                int tx = (int)Math.floor((worldX - ch.originX) / ch.tileSize);
                int ty = (int)Math.floor((worldY - ch.originY) / ch.tileSize);
                int local = ty * ch.width + tx;
                tileOut[idx] = ch.tiles[local];
                elevationOut[idx] =
                    ch.elevation != null && Js.InRange(ch.elevation, local)
                        ? ch.elevation[local]
                        : Js.I8(elevationLevelAt(seed, gi, gj, elevationProfile));
                surfaceOut[idx] = Js.U8(terrainSurfaceAt(ch, tx, ty));
                variantOut[idx] = Js.U8(terrainVariantAt(ch, tx, ty));
                if (themeOut != null)
                    themeOut[idx] = ch.terrain?.themeIndex != null && Js.InRange(ch.terrain.themeIndex, local)
                        ? ch.terrain.themeIndex[local]
                        : (byte)TERRAIN_THEME_INHERIT;
                if (bridgeSpanOut != null) bridgeSpanOut[idx] = bridgeSpanAt(ch, local);
                if (waterLevelOut != null) waterLevelOut[idx] = waterLevelsForLayout(ch)[local];
                if (elevationOut[idx] > maxElevation) maxElevation = elevationOut[idx];
            }
        }
        return maxElevation;
    }

    /// <summary>
    /// Collect stable world-owned decorations into the renderer's current region-local cell space. This includes
    /// authored finite placements and semantic Endless Country dressing; bake-local fallback scatter remains in
    /// the render plan only for layouts that do not carry an explicit composition.
    /// </summary>
    public List<TerrainDecorationPlacement>? terrainDecorationsForRegion(
        int i0,
        int j0,
        int width,
        int height,
        double tileSize)
    {
        var @out = new List<TerrainDecorationPlacement>();
        bool explicitlyAuthored = false;
        double gridOriginX = this.gridOriginX;
        double gridOriginY = this.gridOriginY;
        foreach (DungeonLayout layout in loadedSnapshot)
        {
            if (layout.tileSize != tileSize) continue;
            double baseX = Math.round((layout.originX - gridOriginX) / tileSize);
            double baseY = Math.round((layout.originY - gridOriginY) / tileSize);
            if (
                baseX + layout.width <= i0 ||
                baseY + layout.height <= j0 ||
                baseX >= i0 + width ||
                baseY >= j0 + height)
                continue;
            List<TerrainDecorationPlacement>? decorations = layout.terrain?.decorations;
            if (decorations == null) continue;
            explicitlyAuthored = true;
            foreach (TerrainDecorationPlacement decoration in decorations)
            {
                double tx = baseX + decoration.tx - i0;
                double ty = baseY + decoration.ty - j0;
                if (tx < 0 || ty < 0 || tx >= width || ty >= height) continue;
                TerrainDecorationPlacement placed = decoration.Clone();
                placed.tx = (int)tx;
                placed.ty = (int)ty;
                @out.push(placed);
            }
        }
        // Endless artifacts commonly carry an explicit but empty decoration array. Treating that as a complete
        // authored court disabled the render plan's bounded macro-patch fallback across entire ordinary chunks,
        // which is why representative gameplay frames contained only grass flecks. Real authored placements remain
        // authoritative; finite Hub/raid keep-outs still preserve an intentional empty composition.
        if (explicitlyAuthored && @out.Count == 0 && descriptor?.mode == DungeonMode.Endless)
            return null;
        return explicitlyAuthored ? @out : null;
    }

    /// <summary>Sample the generator's semantic circulation field into the renderer's current bake region.</summary>
    public byte[]? terrainFloorUsageForRegion(
        int i0,
        int j0,
        int width,
        int height,
        double tileSize)
    {
        byte[]? @out = null;
        double gridOriginX = this.gridOriginX;
        double gridOriginY = this.gridOriginY;
        foreach (DungeonLayout layout in loadedSnapshot)
        {
            byte[]? usage = layout.terrain?.floorUsage;
            if (usage == null || layout.tileSize != tileSize) continue;
            double baseX = Math.round((layout.originX - gridOriginX) / tileSize);
            double baseY = Math.round((layout.originY - gridOriginY) / tileSize);
            double left = Math.max(i0, baseX);
            double top = Math.max(j0, baseY);
            double right = Math.min(i0 + width, baseX + layout.width);
            double bottom = Math.min(j0 + height, baseY + layout.height);
            if (left >= right || top >= bottom) continue;
            @out ??= new byte[width * height];
            // All four bounds are now integral and inside [i0, i0 + width] × [j0, j0 + height].
            for (int worldY = (int)top; worldY < bottom; worldY++)
            {
                int sourceRow = (int)((worldY - baseY) * layout.width);
                int targetRow = (worldY - j0) * width;
                for (int worldX = (int)left; worldX < right; worldX++)
                {
                    int source = (int)(sourceRow + worldX - baseX);
                    @out[targetRow + worldX - i0] = Js.InRange(usage, source) ? usage[source] : (byte)0;
                }
            }
        }
        return @out;
    }

    /// <summary>Enter a procedural world from its instance id (authored layouts use <see cref="setAuthored"/>).</summary>
    public void enter(string instanceId)
    {
        reset();
        this.instanceId = instanceId;
        descriptor = descriptorFromInstanceId(instanceId);
        // The elevation field is seeded from the instance id exactly as the geometry is (generate.ts hashes
        // params.seed), so every client derives byte-identical terraces with nothing to stream.
        seedNum = descriptor != null ? hashSeed(descriptor.seed) : null;
        // The biome's landform skew (a mountain run skews high/contrasty) — the SAME profile the generator's
        // rivers and the server use, keyed off the descriptor's biome so the terraces read as that biome's hills.
        elevationProfile = descriptor != null
            ? Terrain.elevationProfileFor(descriptor.biomeKey)
            : NEUTRAL_ELEVATION_PROFILE;
        // Start module evaluation before the first camera rectangle arrives. Worker startup overlaps the opening
        // presentation instead of becoming part of the first visible chunk's latency.
        if (descriptor?.mode == DungeonMode.Endless)
            ensureGenerationWorkers();
        // STUDIO: the finite spaces of the game (the Hub, raids) are gone; endless chunks stream in via update().
    }

    public void reset()
    {
        generationSerial++;
        outerGenScheduled = false;
        generationInFlight.Clear();
        ambientPlanRequest = null;
        ambientPlanQueue.clear();
        descriptor = null;
        instanceId = "";
        seedNum = null;
        elevationProfile = NEUTRAL_ELEVATION_PROFILE;
        chunks.clear();
        chunkCache.clear();
        refreshLoadedSnapshot();
        keepScratch.Clear();
        missCx.Clear();
        missCy.Clear();
        winLoX = double.NaN;
        winHiX = double.NaN;
        winLoY = double.NaN;
        winHiY = double.NaN;
        coreLoX = double.NaN;
        coreHiX = double.NaN;
        coreLoY = double.NaN;
        coreHiY = double.NaN;
        viewOffsetLeft = double.NaN;
        viewOffsetRight = double.NaN;
        viewOffsetTop = double.NaN;
        viewOffsetBottom = double.NaN;
        viewAnchorX = double.NaN;
        viewAnchorY = double.NaN;
        initialCoreSeeded = false;
        pendingChunks = 0;
        sealedDoors.clear();
        clearedRooms.clear();
    }

    /// <summary>
    /// The visual ground-surface LEVEL under a world point — the single sampler the terrain bake, entity
    /// ground-lift and minimap read, so they agree on the exact surface an actor appears to stand on. Floors use
    /// their stored walk elevation. Water and bridges use the shared terrain model's `surfaceZ`: a bridge deck
    /// deliberately sits above its logical walk elevation, and returning the latter would bury actor feet in the
    /// deck once both meet in the depth buffer.
    ///
    /// Endless chunks and local authored spaces can carry structural plateau heights beyond the legacy standard
    /// 0..MAX_ELEVATION noise range; older finite/generated layouts fall back to the global noise field. Returns 0
    /// outside an active dungeon.
    /// </summary>
    public double elevationAt(double worldX, double worldY)
    {
        DungeonDescriptor? d = descriptor;
        if (d == null || seedNum == null) return 0;
        if (isStreamed)
        {
            int cx = streamChunkCoordX(worldX);
            int cy = streamChunkCoordY(worldY);
            DungeonLayout? chunk = chunks.get(endlessChunkKey(cx, cy));
            if (chunk != null)
            {
                double tx = Math.floor((worldX - chunk.originX) / chunk.tileSize);
                double ty = Math.floor((worldY - chunk.originY) / chunk.tileSize);
                if (tx >= 0 && ty >= 0 && tx < chunk.width && ty < chunk.height)
                {
                    int local = (int)(ty * chunk.width + tx);
                    int tile = chunk.tiles[local];
                    if (tile == TileType.Water || tile == TileType.Bridge || tile == TileType.Chasm)
                    {
                        return terrainCellSurfaceLevelAt(
                            chunk.tiles,
                            chunk.elevation,
                            chunk.width,
                            chunk.height,
                            (int)tx,
                            (int)ty);
                    }
                    if (chunk.elevation != null) return chunk.elevation[local];
                }
            }
            double gtx = Math.floor((worldX - gridOriginX) / streamTileSize);
            double gty = Math.floor((worldY - gridOriginY) / streamTileSize);
            return elevationLevelAt(seedNum.Value, gtx, gty, elevationProfile);
        }
        DungeonLayout? layout = chunks.get(0);
        if (layout == null) return 0;
        double ftx = Math.floor((worldX - layout.originX) / layout.tileSize);
        double fty = Math.floor((worldY - layout.originY) / layout.tileSize);
        // A hand-authored layout (the Hub) carries its terrace heights directly; read them so the renderer paints
        // exactly the authored plateaus. Other finite spaces fall back to the global noise field.
        sbyte[]? authored = layout.elevation;
        if (ftx < 0 || fty < 0 || ftx >= layout.width || fty >= layout.height) return 0;
        int flocal = (int)(fty * layout.width + ftx);
        int ftile = layout.tiles[flocal];
        if (ftile == TileType.Water || ftile == TileType.Bridge || ftile == TileType.Chasm)
        {
            return terrainCellSurfaceLevelAt(
                layout.tiles,
                authored,
                layout.width,
                layout.height,
                (int)ftx,
                (int)fty);
        }
        if (authored != null) return authored[flocal];
        return elevationLevelAt(seedNum.Value, ftx, fty, elevationProfile);
    }

    /// <summary>
    /// Exact rendered top-surface level under a world point. Unlike <see cref="elevationAt"/>, which is also the actor
    /// walk/deck anchor, this includes the full solid-wall rise. World volumes such as the flood must use this
    /// sampler so their surface reaches the visible tops of cliffs instead of stopping at stored elevation.
    /// </summary>
    public double visualSurfaceAt(double worldX, double worldY)
    {
        DungeonDescriptor? d = descriptor;
        if (d == null || seedNum == null) return 0;
        if (isStreamed)
        {
            DungeonLayout? chunk = chunks.get(
                endlessChunkKey(streamChunkCoordX(worldX), streamChunkCoordY(worldY)));
            if (chunk != null)
            {
                double tx = Math.floor((worldX - chunk.originX) / chunk.tileSize);
                double ty = Math.floor((worldY - chunk.originY) / chunk.tileSize);
                if (tx >= 0 && ty >= 0 && tx < chunk.width && ty < chunk.height)
                {
                    int local = (int)(ty * chunk.width + tx);
                    int tile = chunk.tiles[local];
                    double stored = chunk.elevation != null && Js.InRange(chunk.elevation, local)
                        ? chunk.elevation[local]
                        : elevationAt(worldX, worldY);
                    double baseTx = Math.round((chunk.originX - gridOriginX) / chunk.tileSize);
                    double baseTy = Math.round((chunk.originY - gridOriginY) / chunk.tileSize);
                    if (tile == TileType.Solid || tile == TileType.Cleft)
                    {
                        return stored + endlessWallRiseAt((int)(baseTx + tx), (int)(baseTy + ty), stored);
                    }
                    if (tile == TileType.Underpass)
                    {
                        return
                            terrainUnderpassProfileAt(
                                chunk.tiles,
                                chunk.elevation,
                                chunk.width,
                                chunk.height,
                                (int)tx,
                                (int)ty,
                                (lx, ly, level) => endlessWallRiseAt((int)(baseTx + lx), (int)(baseTy + ly), level))?.deckTop ?? stored;
                    }
                    if (tile == TileType.Water || tile == TileType.Bridge || tile == TileType.Chasm)
                    {
                        return terrainCellSurfaceLevelAt(
                            chunk.tiles,
                            chunk.elevation,
                            chunk.width,
                            chunk.height,
                            (int)tx,
                            (int)ty);
                    }
                    return stored;
                }
            }
            return elevationAt(worldX, worldY);
        }

        DungeonLayout? layout = chunks.get(0);
        if (layout == null) return 0;
        double ftx = Math.floor((worldX - layout.originX) / layout.tileSize);
        double fty = Math.floor((worldY - layout.originY) / layout.tileSize);
        if (ftx < 0 || fty < 0 || ftx >= layout.width || fty >= layout.height) return 0;
        int flocal = (int)(fty * layout.width + ftx);
        int ftile = layout.tiles[flocal];
        double fstored = layout.elevation != null && Js.InRange(layout.elevation, flocal)
            ? layout.elevation[flocal]
            : elevationAt(worldX, worldY);
        if (ftile == TileType.Solid || ftile == TileType.Cleft)
            return fstored + standardWallRiseAt((int)ftx, (int)fty, fstored);
        if (ftile == TileType.Underpass)
        {
            return
                terrainUnderpassProfileAt(
                    layout.tiles,
                    layout.elevation,
                    layout.width,
                    layout.height,
                    (int)ftx,
                    (int)fty,
                    standardWallRiseAt)?.deckTop ?? fstored;
        }
        if (ftile == TileType.Water || ftile == TileType.Bridge || ftile == TileType.Chasm)
        {
            return terrainCellSurfaceLevelAt(
                layout.tiles,
                layout.elevation,
                layout.width,
                layout.height,
                (int)ftx,
                (int)fty);
        }
        return fstored;
    }

    /// <summary>Receive a finite raid's authoritative layout (one chunk, index 0).</summary>
    public void setFinite(DungeonLayout layout)
    {
        chunks.clear();
        chunks.set(0, layout);
        // Finite/local spaces use the same ambient terrain plan as Endless chunks. Queue it as soon as Welcome
        // installs the layout, while an Aether journey is still fully cloud-covered; otherwise the Hub module's
        // first visible update synchronously materializes the complete authored map on the render thread.
        queueAmbientPlan(0, layout);
        refreshLoadedSnapshot();
    }

    /// <summary>Set a local authored/editor layout for tools that need the real terrain renderer without a live server.</summary>
    public void setAuthored(DungeonLayout layout)
    {
        reset();
        descriptor = new DungeonDescriptor
        {
            seed = Js.Str(layout.seed),
            biomeKey = layout.biomeKey,
            tier = layout.tier,
            mode = DungeonMode.Finite,
            access = DungeonAccess.Open,
            style = layout.style,
            local = true,
        };
        seedNum = layout.seed;
        elevationProfile = Terrain.elevationProfileFor(layout.biomeKey);
        setFinite(layout);
    }

    /// <summary>
    /// Republish an authored layout whose arrays were edited **in place**.
    ///
    /// <see cref="setAuthored"/> is the full installation: it resets the stream, re-derives the descriptor and queues a
    /// fresh ambient plan. That is right when a different world arrives, and far too much when the same layout
    /// object simply grew another band of cells — as it does on every frame of the map generator's build
    /// cinematic. This is the minimal contract instead: drop the cached collision view and bump the chunk version
    /// so the renderer's per-tile content hashes resample. The terrain baker reads the layout arrays directly, so
    /// it sees the edit without any reinstallation at all.
    /// </summary>
    public void invalidateAuthored()
    {
        if (isStreamed || !chunks.has(0)) return;
        chunkVersion++;
    }

    /// <summary>
    /// Recompute the authored world's ambient life against the cells it holds **now**.
    ///
    /// <see cref="invalidateAuthored"/> deliberately does not do this: it runs on every frame of a generator build and
    /// re-planning the whole ambient graph at that rate would cost more than the build. But a world that is
    /// rewritten in place and never re-planned keeps the ambient plan it was given when it was still empty —
    /// which is why a finished simulated map had no fish in the lakes it had just dug and no birds over the
    /// woods it had just grown. The caller that knows the world has settled asks for this once.
    /// </summary>
    public void refreshAuthoredAmbientPlan()
    {
        if (isStreamed) return;
        DungeonLayout? layout = chunks.get(0);
        if (layout == null) return;
        dropPreparedTerrainAmbientPlans(layout);
        ambientPlanRequest = null;
        queueAmbientPlan(0, layout);
    }

    /// <summary>
    /// Offline/tool mode: regenerate the 2-D endless chunk window around the actual camera viewport. Live
    /// authoritative gameplay returns immediately because server terrain packets own membership. Every chunk
    /// intersecting the viewport plus its caster/bake guard is treated as the priority core. Only the player's
    /// first anchor chunk is generated synchronously behind the opening presentation gate; the warmed adaptive
    /// worker pool generates every other miss off-thread. This keeps procedural generation out of movement frames.
    ///
    /// Calls without `viewport` come from collision prediction before the renderer runs. They reuse the last
    /// camera-relative rectangle instead of collapsing back to a fixed 3x3 window; before the first render the
    /// conservative default core is used. A no-op for finite spaces, whose full layout is already authoritative.
    /// </summary>
    public void update(double playerX, double playerY, DungeonViewport? viewport = null)
    {
        DungeonDescriptor? d = descriptor;
        if (d == null || !isStreamed) return;
        {
            double streamX = playerX;
            double streamY = playerY;
            if (
                viewport != null &&
                Number.isFinite(viewport.left) &&
                Number.isFinite(viewport.right) &&
                Number.isFinite(viewport.top) &&
                Number.isFinite(viewport.bottom))
            {
                viewAnchorX = playerX;
                viewAnchorY = playerY;
                viewOffsetLeft = Math.min(viewport.left, viewport.right) - playerX;
                viewOffsetRight = Math.max(viewport.left, viewport.right) - playerX;
                viewOffsetTop = Math.min(viewport.top, viewport.bottom) - playerY;
                viewOffsetBottom = Math.max(viewport.top, viewport.bottom) - playerY;
            }
            else if (Number.isFinite(viewAnchorX) && Number.isFinite(viewAnchorY))
            {
                // Calls without a viewport are collision/prediction upkeep. The last render anchor already
                // materialized a guarded core around both authoritative and predicted positions, so moving the
                // window here only creates render↔prediction membership churn within the same animation frame.
                streamX = viewAnchorX;
                streamY = viewAnchorY;
            }
            int cx = streamChunkCoordX(streamX);
            int cy = streamChunkCoordY(streamY);

            int coreLoX = cx - ClientDungeonModule.ENDLESS_DEFAULT_CORE_R;
            int coreHiX = cx + ClientDungeonModule.ENDLESS_DEFAULT_CORE_R;
            int coreLoY = cy - ClientDungeonModule.ENDLESS_DEFAULT_CORE_R;
            int coreHiY = cy + ClientDungeonModule.ENDLESS_DEFAULT_CORE_R;
            if (
                Number.isFinite(viewOffsetLeft) &&
                Number.isFinite(viewOffsetRight) &&
                Number.isFinite(viewOffsetTop) &&
                Number.isFinite(viewOffsetBottom))
            {
                coreLoX = Math.min(
                    coreLoX,
                    streamChunkCoordX(streamX + viewOffsetLeft - streamChunkWorld));
                coreHiX = Math.max(
                    coreHiX,
                    streamChunkCoordX(streamX + viewOffsetRight + streamChunkWorld));
                coreLoY = Math.min(
                    coreLoY,
                    streamChunkCoordY(streamY + viewOffsetTop - streamChunkWorld));
                coreHiY = Math.max(
                    coreHiY,
                    streamChunkCoordY(streamY + viewOffsetBottom + streamChunkWorld));
            }
            int buffer = ClientDungeonModule.ENDLESS_BUFFER_R;
            int loX = coreLoX - buffer;
            int hiX = coreHiX + buffer;
            int loY = coreLoY - buffer;
            int hiY = coreHiY + buffer;
            if (
                loX == winLoX &&
                hiX == winHiX &&
                loY == winLoY &&
                hiY == winHiY &&
                coreLoX == this.coreLoX &&
                coreHiX == this.coreHiX &&
                coreLoY == this.coreLoY &&
                coreHiY == this.coreHiY &&
                pendingChunks == 0)
                return;
            winLoX = loX;
            winHiX = hiX;
            winLoY = loY;
            winHiY = hiY;
            this.coreLoX = coreLoX;
            this.coreHiX = coreHiX;
            this.coreLoY = coreLoY;
            this.coreHiY = coreHiY;

            HashSet<double> keep = keepScratch;
            List<int> missCx = this.missCx;
            List<int> missCy = this.missCy;
            // Cold entry needs one collision/render anchor. Building the old viewport-complete core here could run
            // dozens of 10-20 ms generators back-to-back and was the seconds-long first-entry freeze. Generate only
            // the player's own chunk synchronously; the already-warm worker pool fills the ordered core in parallel.
            bool coldStart = !initialCoreSeeded;
            initialCoreSeeded = true;
            bool changed = false;
            keep.Clear();
            int missN = 0;
            for (int yy = loY; yy <= hiY; yy++)
            {
                for (int xx = loX; xx <= hiX; xx++)
                {
                    double key = endlessChunkKey(xx, yy);
                    keep.Add(key);
                    if (chunks.has(key)) continue;
                    DungeonLayout? cached = takeCachedChunk(key);
                    if (cached != null)
                    {
                        chunks.set(key, cached);
                        queueAmbientPlan(key, cached);
                        changed = true;
                        continue;
                    }
                    if (coldStart && xx == cx && yy == cy)
                    {
                        DungeonLayout? generated = generateEndlessChunk(d, xx, yy);
                        if (generated != null)
                        {
                            chunks.set(key, generated);
                            queueAmbientPlan(key, generated);
                            changed = true;
                        }
                        continue;
                    }
                    // Retain the allocation-free snapshot for diagnostics. Scheduling reads the live window so a turn
                    // cannot waste time draining coordinates which have already moved behind the camera.
                    // (`missCx[missN] = xx` grows the JS array by one when missN === length.)
                    if (missN < missCx.Count) missCx[missN] = xx;
                    else missCx.Add(xx);
                    if (missN < missCy.Count) missCy[missN] = yy;
                    else missCy.Add(yy);
                    missN++;
                }
            }
            foreach (double k in chunks.keys())
            {
                if (!keep.Contains(k))
                {
                    DungeonLayout? chunk = chunks.get(k);
                    if (chunk != null) cacheChunk(k, chunk);
                    chunks.delete(k);
                    changed = true;
                }
            }
            pendingChunks = missN;
            if (changed) refreshLoadedSnapshot();
            if (missN > 0) scheduleOuterGeneration();
        }
    }

    private void refreshLoadedSnapshot()
    {
        loadedSnapshot.Clear();
        foreach (DungeonLayout chunk in chunks.values()) loadedSnapshot.push(chunk);
        chunkVersion++;
    }

    private void cacheChunk(double key, DungeonLayout chunk)
    {
        chunkCache.delete(key);
        chunkCache.set(key, chunk);
        if (chunkCache.size <= ClientDungeonModule.ENDLESS_CHUNK_CACHE_LIMIT) return;
        foreach (double oldest in chunkCache.keys())
        {
            chunkCache.delete(oldest);
            break;
        }
    }

    private DungeonLayout? takeCachedChunk(double key)
    {
        DungeonLayout? chunk = chunkCache.get(key);
        if (chunk != null) chunkCache.delete(key);
        return chunk;
    }

    private void scheduleOuterGeneration()
    {
        if (pendingChunks <= 0) return;
        DungeonDescriptor? d = descriptor;
        if (d == null || d.mode != DungeonMode.Endless) return;
        int serial = generationSerial;
        IReadOnlyList<IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse>> workers = ensureGenerationWorkers();
        if (workers.Count > 0)
        {
            // Fill every idle lane in one scheduling pass. Visible/caster-core coordinates remain first because
            // nextMissingChunk ranks against the live camera window and ignores requests already in flight.
            foreach (IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse> worker in workers)
            {
                if (workerRequests.ContainsKey(worker)) continue;
                (int cx, int cy)? next = nextMissingChunk();
                if (next == null) break;
                int id = ++generationRequestId;
                var request = new GenerationWorkerRequest
                {
                    id = id,
                    serial = serial,
                    descriptor = d,
                    cx = next.Value.cx,
                    cy = next.Value.cy,
                    startedAt = host.now(),
                };
                workerRequests[worker] = request;
                generationInFlight[endlessChunkKey(next.Value.cx, next.Value.cy)] = id;
                var message = new EndlessChunkWorkerRequest
                {
                    id = id,
                    descriptor = d,
                    cx = next.Value.cx,
                    cy = next.Value.cy,
                };
                worker.postMessage(message);
            }
            return;
        }
        if (outerGenScheduled) return;
        outerGenScheduled = true;
        void pump()
        {
            outerGenScheduled = false;
            DungeonDescriptor? live = descriptor;
            if (
                live == null ||
                live.mode != DungeonMode.Endless ||
                !ReferenceEquals(live, d) ||
                serial != generationSerial)
                return;
            int generated = 0;
            while (generated < ClientDungeonModule.ENDLESS_IDLE_GEN_BATCH)
            {
                (int cx, int cy)? nextMissing = nextMissingChunk();
                if (nextMissing == null)
                {
                    pendingChunks = 0;
                    return;
                }
                int bestX = nextMissing.Value.cx;
                int bestY = nextMissing.Value.cy;
                DungeonLayout generatedChunk = generateEndlessChunk(live, bestX, bestY);
                chunks.set(
                    endlessChunkKey(bestX, bestY),
                    generatedChunk);
                queueAmbientPlan(endlessChunkKey(bestX, bestY), generatedChunk);
                refreshLoadedSnapshot();
                pendingChunks = Math.max(0, pendingChunks - 1);
                generated++;
            }
            if (pendingChunks > 0) scheduleOuterGeneration();
        }
        // `typeof requestIdleCallback === 'function' ? requestIdleCallback(pump, { timeout: 100 }) : setTimeout(pump, 0)`.
        if (host.requestIdleCallback != null) host.requestIdleCallback(pump);
        else host.loop.post(pump);
    }

    /// <summary>Visible/caster core work always wins over speculative buffer work.</summary>
    private (int cx, int cy)? nextMissingChunk()
    {
        double centerX = Math.floor((coreLoX + coreHiX) / 2);
        double centerY = Math.floor((coreLoY + coreHiY) / 2);
        double bestX = 0;
        double bestY = 0;
        double bestPriority = double.PositiveInfinity;
        double bestDistance = double.PositiveInfinity;
        for (double yy = winLoY; yy <= winHiY; yy++)
        {
            for (double xx = winLoX; xx <= winHiX; xx++)
            {
                double key = endlessChunkKey(xx, yy);
                if (chunks.has(key) || chunkCache.has(key) || generationInFlight.ContainsKey(key))
                    continue;
                double priority =
                    xx >= coreLoX && xx <= coreHiX && yy >= coreLoY && yy <= coreHiY
                        ? 0
                        : 1;
                double dx = xx - centerX;
                double dy = yy - centerY;
                double distance = dx * dx + dy * dy;
                if (priority > bestPriority || (priority == bestPriority && distance >= bestDistance))
                    continue;
                bestPriority = priority;
                bestDistance = distance;
                bestX = xx;
                bestY = yy;
            }
        }
        return bestDistance == double.PositiveInfinity ? null : ((int)bestX, (int)bestY);
    }

    private IReadOnlyList<IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse>> ensureGenerationWorkers()
    {
        if (workerUnavailable || host.createEndlessChunkWorker == null) return generationWorkers;
        int target = ClientDungeonModule.endlessChunkWorkerConcurrency(host.hardwareConcurrency, host.deviceMemory);
        try
        {
            while (generationWorkers.Count < target)
            {
                int lane = generationWorkers.Count;
                IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse> worker =
                    host.createEndlessChunkWorker($"fluitown-endless-chunks-{Js.Str(lane + 1)}");
                worker.onmessage = (data) => acceptWorkerChunk(worker, data);
                worker.onerror = () => failGenerationWorkers();
                generationWorkers.push(worker);
            }
        }
        catch (Exception)
        {
            failGenerationWorkers();
        }
        return generationWorkers;
    }

    /// <summary>
    /// STUDIO: stop every worker this dungeon started (chunk generation lanes and the ambient planner). The game kept one
    /// dungeon for its whole run; the studio replaces the dungeon with each session, and a worker left running keeps the
    /// old dungeon and all of its chunks alive.
    /// </summary>
    public void shutdown()
    {
        reset();
        foreach (IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse> worker in generationWorkers) worker.terminate();
        generationWorkers.Clear();
        workerRequests.Clear();
        generationInFlight.Clear();
        ambientPlanWorker?.terminate();
        ambientPlanWorker = null;
    }

    private void failGenerationWorkers()
    {
        workerUnavailable = true;
        outerGenScheduled = false;
        foreach (IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse> worker in generationWorkers) worker.terminate();
        generationWorkers.Clear();
        workerRequests.Clear();
        generationInFlight.Clear();
        if (pendingChunks > 0) scheduleOuterGeneration();
    }

    private void acceptWorkerChunk(
        IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse> worker,
        EndlessChunkWorkerResponse response)
    {
        if (!workerRequests.TryGetValue(worker, out GenerationWorkerRequest? request) || response.id != request.id) return;
        workerRequests.Remove(worker);
        double requestKey = endlessChunkKey(request.cx, request.cy);
        if (generationInFlight.TryGetValue(requestKey, out int inFlightId) && inFlightId == request.id)
            generationInFlight.Remove(requestKey);
        if (
            request.serial == generationSerial &&
            ReferenceEquals(request.descriptor, descriptor) &&
            request.cx == response.cx &&
            request.cy == response.cy)
        {
            DungeonLayout layout = response.layout;
            double key = endlessChunkKey(response.cx, response.cy);
            if (!chunks.has(key) && !chunkCache.has(key))
            {
                if (
                    response.cx >= winLoX &&
                    response.cx <= winHiX &&
                    response.cy >= winLoY &&
                    response.cy <= winHiY)
                {
                    chunks.set(key, layout);
                    queueAmbientPlan(key, layout);
                    refreshLoadedSnapshot();
                }
                else
                {
                    cacheChunk(key, layout);
                }
            }
        }
        pendingChunks = countMissingChunks();
        if (pendingChunks > 0) scheduleOuterGeneration();
    }

    private void queueAmbientPlan(double key, DungeonLayout layout)
    {
        if (ambientPlanWorkerUnavailable || string.IsNullOrEmpty(instanceId)) return;
        string cacheKey = terrainAmbientPlanCacheKey(instanceId, layout.biomeKey);
        if (getPreparedTerrainAmbientPlan(layout, cacheKey) != null) return;
        if (ReferenceEquals(ambientPlanRequest?.layout, layout)) return;
        ambientPlanQueue.set(key, layout);
        scheduleAmbientPlan();
    }

    private void scheduleAmbientPlan()
    {
        if (ambientPlanRequest != null || ambientPlanQueue.size == 0) return;
        IClientWorker<TerrainAmbientPlanWorkerRequest, TerrainAmbientPlanWorkerResponse>? worker = ensureAmbientPlanWorker();
        if (worker == null) return;
        KeyValuePair<double, DungeonLayout>? next = null;
        foreach (KeyValuePair<double, DungeonLayout> entry in ambientPlanQueue.entries())
        {
            next = entry;
            break;
        }
        if (next == null) return;
        (double key, DungeonLayout layout) = (next.Value.Key, next.Value.Value);
        ambientPlanQueue.delete(key);
        int id = ++generationRequestId;
        ambientPlanRequest = new AmbientPlanRequest
        {
            id = id,
            serial = generationSerial,
            key = key,
            layout = layout,
            startedAt = host.now(),
        };
        var request = new TerrainAmbientPlanWorkerRequest
        {
            id = id,
            kind = "prepare-ambient",
            cx = streamChunkCoordX(layout.originX + layout.tileSize * 0.5),
            cy = streamChunkCoordY(layout.originY + layout.tileSize * 0.5),
            ambientInstanceId = instanceId,
            ambientBiomeKey = layout.biomeKey,
            layout = layout,
        };
        worker.postMessage(request);
    }

    private IClientWorker<TerrainAmbientPlanWorkerRequest, TerrainAmbientPlanWorkerResponse>? ensureAmbientPlanWorker()
    {
        if (ambientPlanWorkerUnavailable || host.createTerrainAmbientPlanWorker == null) return null;
        if (ambientPlanWorker != null) return ambientPlanWorker;
        try
        {
            IClientWorker<TerrainAmbientPlanWorkerRequest, TerrainAmbientPlanWorkerResponse> worker =
                host.createTerrainAmbientPlanWorker("fluitown-terrain-ambient");
            worker.onmessage = (data) => acceptAmbientPlan(data);
            worker.onerror = () =>
            {
                ambientPlanWorkerUnavailable = true;
                ambientPlanRequest = null;
                ambientPlanQueue.clear();
                worker.terminate();
                ambientPlanWorker = null;
            };
            ambientPlanWorker = worker;
            return worker;
        }
        catch (Exception)
        {
            ambientPlanWorkerUnavailable = true;
            return null;
        }
    }

    private void acceptAmbientPlan(TerrainAmbientPlanWorkerResponse response)
    {
        AmbientPlanRequest? request = ambientPlanRequest;
        if (request == null || response.id != request.id || response.kind != "prepare-ambient") return;
        ambientPlanRequest = null;
        if (request.serial == generationSerial)
        {
            primePreparedTerrainAmbientPlan(request.layout, response.ambientCacheKey, new PreparedTerrainAmbientPlan
            {
                surfaceZ = response.ambientSurfaceZ,
                effects = response.ambientEffects,
                waterfalls = response.ambientWaterfalls,
            });
        }
        scheduleAmbientPlan();
    }

    private int countMissingChunks()
    {
        int missing = 0;
        for (double yy = winLoY; yy <= winHiY; yy++)
        {
            for (double xx = winLoX; xx <= winHiX; xx++)
            {
                double key = endlessChunkKey(xx, yy);
                if (!chunks.has(key) && !chunkCache.has(key)) missing++;
            }
        }
        return missing;
    }
}

// ── Injected browser environment (not in the TS) ───────────────────────────────────────────────────────────────

/// <summary>
/// The browser globals <see cref="ClientDungeon"/> reads, made explicit. Everything defaults to "no workers": then
/// the original's idle-pump path generates one chunk per <see cref="ClientEventLoop"/> task (exactly what the
/// browser does when `typeof Worker === 'undefined'`).
/// </summary>
public sealed class ClientDungeonHost
{
    /// <summary>The main-thread task queue (worker messages, `setTimeout(fn, 0)`); the owner runs it every frame.</summary>
    public ClientEventLoop loop = new();

    /// <summary>`new Worker(new URL('./endlessChunkWorker.ts'), { type: 'module', name })`; null ≙ `typeof Worker === 'undefined'`.</summary>
    public Func<string, IClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse>>? createEndlessChunkWorker;

    /// <summary>`new Worker(new URL('./terrainAmbientPlanWorker.ts'), { name })`; null ≙ `typeof Worker === 'undefined'`.</summary>
    public Func<string, IClientWorker<TerrainAmbientPlanWorkerRequest, TerrainAmbientPlanWorkerResponse>>? createTerrainAmbientPlanWorker;

    /// <summary>`navigator.hardwareConcurrency` (null ≙ undefined).</summary>
    public double? hardwareConcurrency;

    /// <summary>`navigator.deviceMemory` in GiB (null ≙ undefined).</summary>
    public double? deviceMemory;

    /// <summary>`requestIdleCallback(fn, { timeout: 100 })` when the host has one; null falls back to `setTimeout(fn, 0)` = <see cref="loop"/>.</summary>
    public Action<Action>? requestIdleCallback;

    /// <summary>`performance.now()` in milliseconds.</summary>
    public Func<double> now = ClientDungeonModule.performanceNow;

    /// <summary>Production host: every Worker is a dedicated background thread; responses arrive through <paramref name="loop"/>.</summary>
    public static ClientDungeonHost threaded(ClientEventLoop loop, double? hardwareConcurrency = null, double? deviceMemory = null)
    {
        return new ClientDungeonHost
        {
            loop = loop,
            hardwareConcurrency = hardwareConcurrency ?? Environment.ProcessorCount,
            deviceMemory = deviceMemory,
            createEndlessChunkWorker = (name) =>
                new ThreadedClientWorker<EndlessChunkWorkerRequest, EndlessChunkWorkerResponse>(loop, EndlessChunkWorker.onmessage, name),
            createTerrainAmbientPlanWorker = (name) =>
                new ThreadedClientWorker<TerrainAmbientPlanWorkerRequest, TerrainAmbientPlanWorkerResponse>(loop, TerrainAmbientPlanWorker.onmessage, name),
        };
    }
}
