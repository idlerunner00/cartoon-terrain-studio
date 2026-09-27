// Port of packages/client/src/render/environment/threeTerrain.ts — module-level tile constants and budget functions
// (TS lines 296–420). Keep in lockstep with the original. See TerrainTileManager.cs for the tile half of the class.
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class ThreeTerrain
{
    // Prepared neighbours stay cached, but drawing the whole one-tile ring rendered up to 25 full terrain tiles
    // for a viewport which needed nine. That invisible overdraw is especially destructive at a 6.94 ms/144 Hz
    // budget. Exact viewport tiles already include their seam context, so no visible geometry depends on the ring.
    internal const int TILE_SHOW_PAD = 0;
    internal const int TILE_PREFETCH_PAD_MAX = 2;
    internal const int TILE_EVICT_PAD = 4;
    /// <summary>Prepared geometry installation stays bounded during traversal, but a covered cold entry can commit a small
    /// batch without extending the loading gate by one animation frame per tile.</summary>
    internal const int TILE_BAKES_PER_FRAME = 1;
    internal const int TILE_VISIBLE_BAKES_PER_FRAME = 2;
    // A prepared tile is cheap to materialise in JavaScript, but its first GPU upload can still compile a driver
    // variant. Installing a cold bank four-at-once turned that hidden startup frame into a multi-second browser
    // stall on software/low-end WebGL. The worker may prepare the whole viewport in parallel; submission remains
    // deliberately one tile per animation frame so input, networking and the loading animation keep progressing.
    internal const int TILE_COLD_BAKES_PER_FRAME = 1;
    /// <summary>A covered authority swap values frame pacing over reveal latency. One immutable upload per frame keeps the
    /// physical cloud deck animating while both finite Hub tiles and worker-prepared Endless tiles become atomic.</summary>
    internal const int TILE_COVERED_TRANSITION_BAKES_PER_FRAME = 1;
    internal const int TILE_ADAPTIVE_INSTALL_MAX = 2;
    internal const double TILE_INSTALL_FRAME_BUDGET_MS = 2.5;
    internal const double TILE_INSTALL_EMA_BUDGET_MS = 1.25;
    /// <summary>Sampling decorations is bounded per worker. Filling all lanes in the first covered frame removes
    /// the artificial multi-frame queue ramp while the physical cloud deck already hides the destination.</summary>
    internal const int TILE_COVERED_TRANSITION_PLAN_REQUESTS_PER_WORKER_PER_FRAME = 2;
    /// <summary>Worker planning starts several tiles before visibility. Sampling is cheap but still bounded so a resize or
    /// teleport cannot enqueue an unbounded clone burst in one render frame.</summary>
    internal const int TILE_PLAN_REQUESTS_PER_WORKER_PER_FRAME = 2;
    internal const int TILE_PLAN_PENDING_PER_WORKER = 6;
    internal const int TILE_PLAN_PENDING_LIMIT = 24;
    internal const int TILE_PLAN_READY_LIMIT = 24;
    internal const int TERRAIN_PLAN_WORKER_MAX = 4;
    internal const double TERRAIN_PLAN_WORKER_RETRY_BASE_MS = 250;
    internal const double TERRAIN_PLAN_WORKER_RETRY_MAX_MS = 5_000;
    /// <summary>A created worker can fail silently without firing `error`/`messageerror`. A bounded stall restarts the
    /// pool; ordinary authoritative streaming never falls back to a synchronous cold bake on the presentation thread.</summary>
    internal const int TERRAIN_PLAN_VISIBLE_STALL_FRAMES = 60;
    internal const double TERRAIN_PLAN_VISIBLE_STALL_MS = 1_500;
    /// <summary>A cold module worker must fetch, parse and evaluate the complete procedural compiler before its handshake.
    /// Give pre-handshake requests their own bounded window, then use the tight live-worker limit.</summary>
    internal const int TERRAIN_PLAN_STARTUP_STALL_FRAMES = 60;
    internal const double TERRAIN_PLAN_STARTUP_STALL_MS = 5_000;
    internal const int MAX_CACHED_TILES = 120;

    /// <summary>`tileKey(tx, ty)`. PORT ADDITION: one string per coordinate and thread, reused — the tile manager asks
    /// for the keys of every tile around the view every frame, and a fresh string each time was most of the main
    /// thread's per-frame garbage. Same value, so the same behaviour.</summary>
    internal static string tileKey(int tx, int ty)
    {
        var keys = tileKeys ??= new System.Collections.Generic.Dictionary<long, string>();
        long id = ((long)tx << 32) | (uint)ty;
        if (!keys.TryGetValue(id, out string? key))
        {
            if (keys.Count >= 1 << 16) keys.Clear();
            keys[id] = key = $"{tx},{ty}";
        }
        return key;
    }

    [System.ThreadStatic] private static System.Collections.Generic.Dictionary<long, string>? tileKeys;

    /// <summary>Live CPU/GPU residency is bounded by real payload bytes. Tile count remains a corruption/teleport guard,
    /// but a dense rich tile and a compact empty tile are not remotely the same memory cost.</summary>
    public static double terrainLiveCacheByteBudget(double deviceMemoryGiB = 8, bool mobile = false, bool software = false)
    {
        double memory = double.IsFinite(deviceMemoryGiB) ? Math.max(1, deviceMemoryGiB) : 8;
        if (software || memory <= 2) return 32 * 1024 * 1024;
        if (mobile || memory <= 4) return 64 * 1024 * 1024;
        if (memory <= 8) return 128 * 1024 * 1024;
        return 192 * 1024 * 1024;
    }

    public static int terrainPrefetchPad(bool moving, int pendingPlans, int workerCount, double cachePressure, bool mobile = false)
    {
        if (cachePressure >= 0.88) return 0;
        int workers = System.Math.Max(1, workerCount);
        if (pendingPlans >= workers * TILE_PLAN_PENDING_PER_WORKER) return 0;
        if (pendingPlans >= workers * 3) return 1;
        if (mobile || !moving) return 1;
        return TILE_PREFETCH_PAD_MAX;
    }

    /// <summary>Device-class policy for the bounded pool: phone/tablet stays at roughly two workers, while a desktop with
    /// sufficient CPU and memory can keep three or four independent tile compilers busy.</summary>
    public static int terrainPlanWorkerConcurrency(double logicalProcessors, bool mobile = false, double deviceMemoryGiB = 8)
    {
        double processors = double.IsFinite(logicalProcessors) ? Math.max(1, Math.floor(logicalProcessors)) : 1;
        double memory = double.IsFinite(deviceMemoryGiB) ? Math.max(1, deviceMemoryGiB) : 8;
        double memoryCap = memory <= 2 ? 1 : memory <= 4 ? 2 : TERRAIN_PLAN_WORKER_MAX;
        double cpuTarget = mobile
            ? processors >= 4 ? 2 : 1
            : processors >= 8 ? 4 : processors >= 6 ? 3 : processors >= 4 ? 2 : 1;
        return (int)Math.max(1, Math.min(TERRAIN_PLAN_WORKER_MAX, Math.min(memoryCap, cpuTarget)));
    }
}
