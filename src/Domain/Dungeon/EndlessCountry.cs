// Port of packages/shared/src/domain/dungeon/endlessCountry.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.EndlessCoordinates;
using static Fluitown.Domain.EndlessCountryField;
using Math = Fluitown.Runtime.JsMath;
using SALT = Fluitown.Domain.ENDLESS_COUNTRY_SALT;

namespace Fluitown.Domain;

// Semantic Country composition for Endless generation V4.
//
// Streaming still owns immutable 32x32 chunks, but composition no longer resets on that boundary. Four-by-four
// chunk Countries select one WFC-constrained landscape grammar, large landform features and a sparse route
// hierarchy. Every subchunk participates in a deterministic tree, every Country participates in a deterministic
// tree rooted at the run origin, and optional loops add route choice without turning every seam into a port.
//
// PORT NOTE: the TS module re-exports `ENDLESS_COUNTRY_MAZE_KIND_COUNT`, `EndlessCountryMazeKind` and
// `EndlessCountryMazePlan` from endlessCountryMaze.ts. In C# they live in `EndlessCountryMaze` / the namespace.

/// <summary>One seam-stable mountain source whose polyline descends into the Country's primary water body.</summary>
public sealed class EndlessCountryMountainStream
{
    public EndlessCountry.Point source = null!;
    public EndlessCountry.Point mouth = null!;
    /// <summary>World-space path samples from source to mouth. Adjacent samples form the authoritative channel.</summary>
    public IReadOnlyList<EndlessCountry.Point> points = null!;
    public double halfWidth;
    public double sourcePoolRadius;
    /// <summary>Both levels lie on the shared five-level hydraulic ladder.</summary>
    public double sourceLevel;
    public double mouthLevel;
    public double minX;
    public double minY;
    public double maxX;
    public double maxY;
}

public sealed class EndlessCountryLandmarkPlan : EndlessCountry.ReliefDatum
{
    public double x;
    public double y;
    public int ordinal;
    public double angle;
    public EndlessLandmarkVariant variant = null!;
    // Base field sampled at the landmark's centre and perimeter (inherited `centreLevel`, `boundaryMinLevel`,
    // `boundaryMaxLevel`); relief uses these to meet the host terrain without an unclimbable lip even when the
    // landmark crosses an immutable chunk seam.
}

public sealed class EndlessCountryPlan
{
    public int version;
    public double seed;
    /// <summary>The world/theme key this Country was composed for — districts redraw their grammar with its affinities.</summary>
    public string biomeKey = "";
    public int countryX;
    public int countryY;
    public int minChunkX;
    public int minChunkY;
    public int minTileX;
    public int minTileY;
    public int rootChunkX;
    public int rootChunkY;
    public EndlessCountry.Point anchor = null!;
    public EndlessCountryLandscape landscape = null!;
    public EndlessCountry.CountryEllipse primaryShelf = null!;
    public EndlessCountry.CountryEllipse secondaryShelf = null!;
    public EndlessCountry.CountryFlowFeature water = null!;
    public IReadOnlyList<EndlessCountryMountainStream> mountainStreams = null!;
    public EndlessCountry.CountryFlowFeature chasm = null!;
    public EndlessCountryMazePlan maze = null!;
    public IReadOnlyList<EndlessCountryLandmarkPlan> landmarks = null!;
    public double quietAngle;
    public double dressingDensity;

    /// <summary>Shallow copy, the port of <c>{ ...plan }</c> (every nested object stays shared by reference).</summary>
    public EndlessCountryPlan Clone() => (EndlessCountryPlan)MemberwiseClone();
}

public static class EndlessCountryPortSide
{
    public const string West = "west";
    public const string East = "east";
    public const string North = "north";
    public const string South = "south";
}

public sealed class EndlessCountryChunkPort
{
    /// <summary>An <see cref="EndlessCountryPortSide"/> value.</summary>
    public string side = "";
    public int tx;
    public int ty;
}

public sealed class EndlessCountryMountainStreamSample
{
    public double progress;
    public double level;
}

public static partial class EndlessCountry
{
    /// <summary>Latest generation. V15 balances the opportunity rate of V14's compact regional bridges.</summary>
    public const int ENDLESS_GENERATION_VERSION = EndlessBridgeBudget.ENDLESS_BALANCED_BRIDGE_BUDGET_GENERATION_VERSION;
    /// <summary>V14 replaces broad route-painted boardwalks with a sparse, functional bridge budget.</summary>
    private const int ENDLESS_BRIDGE_BUDGET_GENERATION_VERSION = 14;
    /// <summary>V13 turns landmark variants into heightfield events rather than only plan-view masks.</summary>
    private const int ENDLESS_LANDMARK_RELIEF_GENERATION_VERSION = 13;
    /// <summary>V12 makes both renewable terrain resources local invariants instead of regional generation luck.</summary>
    private const int ENDLESS_CHUNK_RESOURCES_GENERATION_VERSION = 12;
    /// <summary>V11 trades ubiquitous wall fill for sparse mountain massifs and source-to-river highland streams.</summary>
    private const int ENDLESS_WILD_HYDROLOGY_GENERATION_VERSION = 11;
    /// <summary>V11 re-opens every sparse seam contract after all destructive terrain repairs have finished.</summary>
    private const int ENDLESS_SEAM_ACCESS_GENERATION_VERSION = 11;
    /// <summary>V10 opens a shortest one-tile bank approach through enclosing mountain mass.</summary>
    private const int ENDLESS_ACCESSIBLE_HYDROLOGY_GENERATION_VERSION = 10;
    /// <summary>V8 retires free-standing scrub anchors; historical V7 descriptors remain byte-reproducible.</summary>
    private const int ENDLESS_LIVING_GROUND_GENERATION_VERSION = 8;
    /// <summary>V9 retires the world-wide lattice of solitary stumps.</summary>
    private const int ENDLESS_BALANCED_DRESSING_GENERATION_VERSION = 9;
    /// <summary>Historical V5 threshold for intra-Country districts, faster landscape chapters and dense theme DNA.</summary>
    private const int ENDLESS_DISTRICT_GENERATION_VERSION = 5;
    /// <summary>Historical V4 threshold. Never move this with the latest-version constant.</summary>
    private const int ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION = 4;
    public const int ENDLESS_WAVE_GENERATION_VERSION = 3;
    public const int ENDLESS_COUNTRY_GENERATION_VERSION = 2;
    public const int ENDLESS_COUNTRY_CHUNKS = 4;
    public const int ENDLESS_COUNTRY_TILES = ENDLESS_COUNTRY_CHUNKS * ENDLESS_CHUNK_TILES;

    private const int COUNTRY_CHUNK_OFFSET = ENDLESS_COUNTRY_CHUNKS >> 1;
    private const double ROUTE_RADIUS = 2.65;
    /// <summary>Physical and visual half-width of the one world-spanning travel corridor. At this width the route reads
    /// as a road-sized piece of landscape at gameplay zoom and also carries the complete Aether landing footprint.
    /// Local maze lanes remain walkable, but deliberately do not masquerade as additional primary roads.</summary>
    private const double FLOOD_SPINE_ROUTE_RADIUS = 3.8;
    /// <summary>The landing-safe topology is wider than the visibly compacted track. Keeping those responsibilities
    /// separate avoids turning every safe corridor into a screen-filling mud plaza.</summary>
    private const double FLOOD_SPINE_VISUAL_ROUTE_RADIUS = 3.05;
    private const double PRIMARY_ROUTE_EDGE_USAGE = 42;
    private const double FLOOD_SPINE_LANDMARK_START = 6_200;
    private const double FLOOD_SPINE_LANDMARK_SPACING = 8_800;
    private const double FLOOD_SPINE_LANDMARK_JITTER = 1_400;
    private const double COURT_RADIUS = 6.25;
    private const int PORT_MARGIN = 5;
    private const int PLAN_CACHE_LIMIT = 192;
    private const int SPINE_CACHE_LIMIT = 16;

    // `const SALT = ENDLESS_COUNTRY_SALT;` is the file-level `using SALT = ...` alias.

    /// <summary>
    /// Side length, in tiles, of one maze DISTRICT.
    ///
    /// A Country spans 4 chunks — 128 tiles, 10,000 world units, roughly a minute of travel and nearly three
    /// camera views in each direction — and used to carry exactly ONE maze grammar: one kind, one lattice angle,
    /// one wall pitch, one phase, everywhere. However varied the Countries were against each other, the ground
    /// under the player therefore never changed while crossing one, which is the "too little variation inside a
    /// run" half of the problem. Districts subdivide that body so the grammar turns over roughly every 3,600
    /// units — about two camera views — while the Country keeps owning the landform, hydrology and set pieces
    /// that make the region one place.
    /// </summary>
    private const int COUNTRY_DISTRICT_TILES = 46;

    public sealed class Point
    {
        public double x;
        public double y;

        public Point(double x, double y)
        {
            this.x = x;
            this.y = y;
        }
    }

    /// <summary>TS `CountryEllipse extends Point`.</summary>
    public sealed class CountryEllipse
    {
        public double x;
        public double y;
        public double rx;
        public double ry;
        public double angle;
    }

    public sealed class CountryFlowFeature
    {
        public bool enabled;
        public double angle;
        public double offset;
        public double halfWidth;
        public double length;
        public double amplitude;
        public double wavelength;
        public double phase;
        public CountryEllipse? basin;
    }

    /// <summary>TS `ChunkNode extends Point`.</summary>
    private sealed class ChunkNode
    {
        public int tx;
        public int ty;
        public int x;
        public int y;
    }

    private static bool guaranteedFeatureCountry(double seed, double salt, int countryX, int countryY)
    {
        if (countryX == 0 && countryY == 0) return true;
        int blockX = (int)Math.floor((double)countryX / 2);
        int blockY = (int)Math.floor((double)countryY / 2);
        int localX = countryX - blockX * 2;
        int localY = countryY - blockY * 2;
        double selected = Math.floor(countryHash(seed, salt, blockX, blockY) * 4);
        return localY * 2 + localX == selected;
    }

    private static bool samePoint(Point? a, Point b) => a != null && a.x == b.x && a.y == b.y;

    public static int endlessCountryCoordForChunk(int chunkCoord) =>
        (int)Math.floor((double)(chunkCoord + COUNTRY_CHUNK_OFFSET) / ENDLESS_COUNTRY_CHUNKS);

    public static int endlessCountryCoordForTile(double tileCoord) =>
        (int)Math.floor((tileCoord + (double)ENDLESS_COUNTRY_TILES / 2) / ENDLESS_COUNTRY_TILES);

    private static int countryMinChunk(int countryCoord) => countryCoord * ENDLESS_COUNTRY_CHUNKS - COUNTRY_CHUNK_OFFSET;

    private static int localChunkCoord(int chunkCoord, int countryCoord) => chunkCoord - countryMinChunk(countryCoord);

    private static Point countryRootLocal(double seed, double countryX, double countryY)
    {
        if (countryX == 0 && countryY == 0) return new Point(2, 2);
        return new Point(
            1 + Math.floor(countryHash(seed, SALT.rootX, countryX, countryY) * 2),
            1 + Math.floor(countryHash(seed, SALT.rootY, countryX, countryY) * 2));
    }

    private static ChunkNode chunkNodeAt(double seed, int cx, int cy)
    {
        bool spawn = cx == 0 && cy == 0;
        int tx = spawn ? 15 : 10 + (int)Math.floor(countryHash(seed, SALT.chunkNodeX, cx, cy) * 12);
        int ty = spawn ? 15 : 10 + (int)Math.floor(countryHash(seed, SALT.chunkNodeY, cx, cy) * 12);
        return new ChunkNode { tx = tx, ty = ty, x = cx * ENDLESS_CHUNK_TILES + tx, y = cy * ENDLESS_CHUNK_TILES + ty };
    }

    private static Point? localParent(double seed, double countryX, double countryY, double localX, double localY)
    {
        Point root = countryRootLocal(seed, countryX, countryY);
        if (localX == root.x && localY == root.y) return null;
        double dx = root.x - localX;
        double dy = root.y - localY;
        if (dx == 0) return new Point(localX, localY + Math.sign(dy));
        if (dy == 0) return new Point(localX + Math.sign(dx), localY);
        return countryHash(seed, SALT.internalParent, countryX * 11 + localX, countryY * 11 + localY) < 0.5
            ? new Point(localX + Math.sign(dx), localY)
            : new Point(localX, localY + Math.sign(dy));
    }

    private static (Point lo, Point hi) canonicalAdjacent(Point a, Point b) =>
        a.y < b.y || (a.y == b.y && a.x <= b.x) ? (a, b) : (b, a);

    private static readonly (int dx, int dy)[] TREE_DEGREE_STEPS = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    private static bool internalChunkEdgeActive(
        double seed,
        double countryX,
        double countryY,
        Point a,
        Point b,
        int generationVersion)
    {
        if (samePoint(localParent(seed, countryX, countryY, a.x, a.y), b)) return true;
        if (samePoint(localParent(seed, countryX, countryY, b.x, b.y), a)) return true;
        var (lo, hi) = canonicalAdjacent(a, b);
        double loopChance = 0.2;
        if (generationVersion >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION)
        {
            int treeDegree(Point point)
            {
                int degree = 0;
                foreach (var (dx, dy) in TREE_DEGREE_STEPS)
                {
                    var neighbour = new Point(point.x + dx, point.y + dy);
                    if (
                        neighbour.x < 0 ||
                        neighbour.y < 0 ||
                        neighbour.x >= ENDLESS_COUNTRY_CHUNKS ||
                        neighbour.y >= ENDLESS_COUNTRY_CHUNKS)
                        continue;
                    if (
                        samePoint(localParent(seed, countryX, countryY, point.x, point.y), neighbour) ||
                        samePoint(localParent(seed, countryX, countryY, neighbour.x, neighbour.y), point))
                        degree++;
                }
                return degree;
            }
            // Spend added edges where they eliminate a tree leaf. Dense junction-to-junction edges receive only a
            // small uplift, preserving readable walls instead of degenerating into the old four-port checkerboard.
            loopChance = Math.min(treeDegree(a), treeDegree(b)) <= 1 ? 0.48 : 0.23;
        }
        return
            countryHash(
                seed,
                SALT.internalLoop,
                countryX * 31 + lo.x * 5 + hi.x,
                countryY * 31 + lo.y * 5 + hi.y) < loopChance;
    }

    /// <summary>Every Country has one parent that strictly reduces Manhattan distance to `(0,0)`.</summary>
    public static Point? endlessCountryParentAt(double seed, double countryX, double countryY)
    {
        if (countryX == 0 && countryY == 0) return null;
        if (countryX == 0) return new Point(0, countryY - Math.sign(countryY));
        if (countryY == 0) return new Point(countryX - Math.sign(countryX), 0);
        return countryHash(seed, SALT.countryParent, countryX, countryY) < 0.5
            ? new Point(countryX - Math.sign(countryX), countryY)
            : new Point(countryX, countryY - Math.sign(countryY));
    }

    public static bool endlessCountryEdgeActive(
        double seed,
        Point a,
        Point b,
        int generationVersion = ENDLESS_GENERATION_VERSION)
    {
        if (Math.abs(a.x - b.x) + Math.abs(a.y - b.y) != 1) return false;
        if (samePoint(endlessCountryParentAt(seed, a.x, a.y), b)) return true;
        if (samePoint(endlessCountryParentAt(seed, b.x, b.y), a)) return true;
        var (lo, hi) = canonicalAdjacent(a, b);
        double loopChance = 0.2;
        if (generationVersion >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION)
        {
            int treeDegree(Point point)
            {
                int degree = 0;
                foreach (var (dx, dy) in TREE_DEGREE_STEPS)
                {
                    var neighbour = new Point(point.x + dx, point.y + dy);
                    if (
                        samePoint(endlessCountryParentAt(seed, point.x, point.y), neighbour) ||
                        samePoint(endlessCountryParentAt(seed, neighbour.x, neighbour.y), point))
                        degree++;
                }
                return degree;
            }
            loopChance = Math.min(treeDegree(a), treeDegree(b)) <= 1 ? 0.46 : 0.22;
        }
        return countryHash(seed, SALT.countryLoop, lo.x * 7 + hi.x, lo.y * 7 + hi.y) < loopChance;
    }

    private static int countryBoundaryLane(double seed, Point a, Point b)
    {
        var (lo, hi) = canonicalAdjacent(a, b);
        return (int)Math.floor(countryHash(seed, SALT.externalLane, lo.x * 7 + hi.x, lo.y * 7 + hi.y) * 4);
    }

    private static int seamPortOffset(double seed, bool vertical, int seamCoord, int bandCoord)
    {
        int span = ENDLESS_CHUNK_TILES - PORT_MARGIN * 2;
        return
            PORT_MARGIN +
            (int)Math.floor(
                countryHash(seed, vertical ? SALT.verticalPort : SALT.horizontalPort, seamCoord, bandCoord) * span);
    }

    /// <summary>
    /// Whether two cardinally-adjacent streamed chunks share one of the sparse Country route graph's seam ports.
    ///
    /// This is exported because the graph is not merely a generator implementation detail: it is the cheap,
    /// deterministic navigation hierarchy for ground that has not been rasterized yet. Consumers must ask this
    /// exact contract rather than infer four-open-port legacy topology or regenerate tile detail just to discover
    /// whether two chunks join. Both adjacent chunks already call this same function while generating their seam,
    /// so a coarse route and the eventual collision bytes cannot disagree.
    /// </summary>
    public static bool endlessCountryChunkEdgeActive(
        double seed,
        int cx,
        int cy,
        int nx,
        int ny,
        int generationVersion)
    {
        int countryX = endlessCountryCoordForChunk(cx);
        int countryY = endlessCountryCoordForChunk(cy);
        int neighbourCountryX = endlessCountryCoordForChunk(nx);
        int neighbourCountryY = endlessCountryCoordForChunk(ny);
        if (countryX == neighbourCountryX && countryY == neighbourCountryY)
        {
            return internalChunkEdgeActive(
                seed,
                countryX,
                countryY,
                new Point(localChunkCoord(cx, countryX), localChunkCoord(cy, countryY)),
                new Point(localChunkCoord(nx, countryX), localChunkCoord(ny, countryY)),
                generationVersion);
        }
        var here = new Point(countryX, countryY);
        var there = new Point(neighbourCountryX, neighbourCountryY);
        if (!endlessCountryEdgeActive(seed, here, there, generationVersion)) return false;
        int lane = countryBoundaryLane(seed, here, there);
        return cx != nx
            ? localChunkCoord(cy, countryY) == lane
            : localChunkCoord(cx, countryX) == lane;
    }

    /// <summary>Sparse seam contracts for one chunk. Adjacent chunks derive exactly the same active edge and port cell.</summary>
    public static List<EndlessCountryChunkPort> endlessCountryChunkPortsAt(
        double seed,
        int cx,
        int cy,
        int generationVersion = ENDLESS_GENERATION_VERSION)
    {
        int last = ENDLESS_CHUNK_TILES - 1;
        var ports = new List<EndlessCountryChunkPort>();
        if (endlessCountryChunkEdgeActive(seed, cx, cy, cx - 1, cy, generationVersion))
            ports.push(new EndlessCountryChunkPort
            {
                side = EndlessCountryPortSide.West,
                tx = 0,
                ty = seamPortOffset(seed, true, cx, cy),
            });
        if (endlessCountryChunkEdgeActive(seed, cx, cy, cx + 1, cy, generationVersion))
            ports.push(new EndlessCountryChunkPort
            {
                side = EndlessCountryPortSide.East,
                tx = last,
                ty = seamPortOffset(seed, true, cx + 1, cy),
            });
        if (endlessCountryChunkEdgeActive(seed, cx, cy, cx, cy - 1, generationVersion))
            ports.push(new EndlessCountryChunkPort
            {
                side = EndlessCountryPortSide.North,
                tx = seamPortOffset(seed, false, cy, cx),
                ty = 0,
            });
        if (endlessCountryChunkEdgeActive(seed, cx, cy, cx, cy + 1, generationVersion))
            ports.push(new EndlessCountryChunkPort
            {
                side = EndlessCountryPortSide.South,
                tx = seamPortOffset(seed, false, cy + 1, cx),
                ty = last,
            });
        return ports;
    }

    // ── Module caches ─────────────────────────────────────────────────────────────────────────────────────
    //
    // PORT NOTE: `planCache`, `spineCache` and `districtPlanCache` are insertion-ordered JS Maps used purely as
    // bounded FIFO memo tables (the only iteration is `keys().next()` to evict the oldest entry). A Dictionary
    // plus an insertion queue reproduces exactly that eviction order without JsMap's tombstone growth. All module
    // state is per thread ([ThreadStatic]), mirroring one module instance per JS worker. Cache state never
    // changes a result: every cached value is a pure function of its key.
    private sealed class FifoCache<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> entries = new();
        private readonly Queue<TKey> order = new();

        public int size => entries.Count;

        public bool TryGetValue(TKey key, out TValue value) => entries.TryGetValue(key, out value!);

        public void set(TKey key, TValue value)
        {
            if (!entries.ContainsKey(key)) order.Enqueue(key);
            entries[key] = value;
        }

        /// <summary>`map.delete(map.keys().next().value)`.</summary>
        public void deleteOldest()
        {
            if (order.Count == 0) return;
            entries.Remove(order.Dequeue());
        }
    }

    [ThreadStatic] private static FifoCache<string, EndlessCountryPlan>? planCacheStorage;
    [ThreadStatic] private static FifoCache<double, Spine>? spineCacheStorage;

    private static FifoCache<string, EndlessCountryPlan> planCache => planCacheStorage ??= new FifoCache<string, EndlessCountryPlan>();
    private static FifoCache<double, Spine> spineCache => spineCacheStorage ??= new FifoCache<double, Spine>();

    private static Spine terrainSpineFor(double seed)
    {
        if (spineCache.TryGetValue(seed, out Spine cached) && cached != null) return cached;
        var spine = new Spine(seed);
        spineCache.set(seed, spine);
        if (spineCache.size > SPINE_CACHE_LIMIT) spineCache.deleteOldest();
        return spine;
    }

    private static double spineArcAtX(Spine spine, double x, double upperS)
    {
        if (x <= 0) return 0;
        double lo = 0;
        double hi = upperS;
        for (int pass = 0; pass < 24; pass++)
        {
            double mid = (lo + hi) * 0.5;
            if (spine.pointAt(mid).x < x) lo = mid;
            else hi = mid;
        }
        return hi;
    }

    /// <summary>One guaranteed authored arena beat every few Countries, placed on (not merely near) the Flood route.</summary>
    private static bool floodSpineLandmarkInChunk(double spineSeed, int cx, int cy)
    {
        double originX = endlessChunkOriginX(cx);
        double originY = endlessChunkOriginY(cy);
        double chunkWorld = ENDLESS_CHUNK_TILES * Grid.TILE_SIZE;
        if (originX + chunkWorld <= 0) return false;
        Spine spine = terrainSpineFor(spineSeed);
        double maxS = Math.max(
            FLOOD_SPINE_LANDMARK_START,
            (originX + chunkWorld) / Math.cos(SpineModule.SPINE_MAX_DEVIATION) + FLOOD_SPINE_LANDMARK_JITTER);
        spine.ensureLength(maxS);
        double firstS = spineArcAtX(spine, originX, maxS);
        double lastS = spineArcAtX(spine, originX + chunkWorld, maxS);
        double firstBeat = Math.max(
            0,
            Math.floor(
                (firstS - FLOOD_SPINE_LANDMARK_START - FLOOD_SPINE_LANDMARK_JITTER) /
                    FLOOD_SPINE_LANDMARK_SPACING));
        double lastBeat = Math.ceil(
            (lastS - FLOOD_SPINE_LANDMARK_START + FLOOD_SPINE_LANDMARK_JITTER) /
                FLOOD_SPINE_LANDMARK_SPACING);
        for (double beat = firstBeat; beat <= lastBeat; beat++)
        {
            double s =
                FLOOD_SPINE_LANDMARK_START +
                beat * FLOOD_SPINE_LANDMARK_SPACING +
                (countryHash(spineSeed, SALT.boss, beat, 0) - 0.5) * FLOOD_SPINE_LANDMARK_JITTER * 2;
            if (s < firstS || s >= lastS) continue;
            var point = spine.pointAt(s);
            if (point.y >= originY && point.y < originY + chunkWorld) return true;
        }
        return false;
    }

    private static CountryFlowFeature createFlowFeature(
        double seed,
        int countryX,
        int countryY,
        Point anchor,
        EndlessLandscapeTraits traits,
        bool water,
        bool biomeHasWater,
        double scale)
    {
        uint salt = water ? SALT.water : SALT.chasm;
        uint detailSalt = water ? SALT.waterDetail : SALT.chasmDetail;
        double strength = water
            ? countryClamp01(0.35 + traits.waterBias * 0.45 + traits.lakeStrength * 0.55)
            : countryClamp01(0.18 + traits.chasmStrength * 0.62 + traits.riftStrength * 0.46);
        bool guaranteed =
            !water && guaranteedFeatureCountry(seed, SALT.chasm ^ 0x3c6ef372, countryX, countryY);
        bool enabled =
            biomeHasWater && water
                ? countryHash(seed, salt, countryX, countryY) < 0.88 + strength * 0.1
                : !water && (guaranteed || countryHash(seed, salt, countryX, countryY) < 0.62 + strength * 0.3);
        double angle = countryHash(seed, detailSalt, countryX, countryY) * Math.PI;
        double halfWidth = (water ? 10 + strength * 7 : 4.25 + strength * 3.1) * scale;
        double length = (water ? 84 : 68) + countryHash(seed, salt ^ 0x51ed270b, countryX, countryY) * 34;
        double basinRoll = countryHash(seed, water ? SALT.lake : SALT.chasmDetail, countryX, countryY);
        double basinStrength = water ? traits.lakeStrength : traits.chasmBasins;
        CountryEllipse? basin =
            guaranteed || basinRoll < (water ? 0.7 : 0.38) + basinStrength * (water ? 0.26 : 0.5)
                ? new CountryEllipse
                {
                    x =
                        anchor.x +
                        Math.cos(angle) *
                            (guaranteed && countryX == 0 && countryY == 0 ? 34 : 8 + basinRoll * 24),
                    y =
                        anchor.y +
                        Math.sin(angle) *
                            (guaranteed && countryX == 0 && countryY == 0 ? 34 : 8 + basinRoll * 24),
                    rx =
                        ((water ? 18 : guaranteed ? 10 : 9) +
                            basinStrength * (water ? 18 : 9) +
                            (water
                                ? Math.max(0, basinStrength - 0.55) * 14
                                : Math.max(0, basinStrength - 0.45) * 7)) *
                        scale,
                    ry =
                        ((water ? 13 : 7) +
                            basinStrength * (water ? 15 : 7) +
                            (water
                                ? Math.max(0, basinStrength - 0.55) * 10
                                : Math.max(0, basinStrength - 0.45) * 5)) *
                        scale,
                    angle = angle + 0.35 + basinRoll,
                }
                : null;
        return new CountryFlowFeature
        {
            enabled = enabled,
            angle = angle,
            offset = (countryHash(seed, salt ^ 0x9e3779b9, countryX, countryY) - 0.5) * 22,
            halfWidth = water && basin != null ? halfWidth * 0.58 : halfWidth,
            length = length,
            amplitude = water ? 3 + traits.channelComplexity * 7 : 2 + traits.riftStrength * 5,
            wavelength = water ? 42 + (1 - traits.channelComplexity) * 38 : 54,
            phase = countryHash(seed, detailSalt ^ 0x85ebca6b, countryX, countryY) * Math.PI * 2,
            basin = basin,
        };
    }

    private static Point flowCentrePointAt(CountryFlowFeature feature, Point anchor, double along)
    {
        double c = Math.cos(feature.angle);
        double s = Math.sin(feature.angle);
        double across =
            feature.offset + Math.sin(along / feature.wavelength + feature.phase) * feature.amplitude;
        return new Point(anchor.x + along * c - across * s, anchor.y + along * s + across * c);
    }

    /// <summary>TS `MountainSourceCandidate extends Point`.</summary>
    private sealed class MountainSourceCandidate
    {
        public double x;
        public double y;
        public double level;
        public double score;
    }

    /// <summary>
    /// Compose one or two real tributaries: a high source, a long continuous channel and a mouth on the primary
    /// Country river. The expensive choices are made once in the cached Country plan; per tile the generator only
    /// measures distance to a short precomputed polyline.
    /// </summary>
    private static IReadOnlyList<EndlessCountryMountainStream> createMountainStreams(
        double seed,
        int countryX,
        int countryY,
        int minTileX,
        int minTileY,
        Point anchor,
        CountryFlowFeature water,
        EndlessLandscapeTraits traits,
        string biomeKey,
        bool biomeHasWater,
        int generationVersion)
    {
        if (
            generationVersion < ENDLESS_WILD_HYDROLOGY_GENERATION_VERSION ||
            !biomeHasWater ||
            !water.enabled)
            return new List<EndlessCountryMountainStream>();

        var mouths = new List<MountainSourceCandidate>();
        // The mouth candidates are all centre-line points of the existing primary watercourse. Selecting the low
        // ones makes the tributary genuinely descend INTO that body rather than drawing an unrelated blue stripe.
        for (int ordinal = 0; ordinal < 9; ordinal++)
        {
            double along = water.length * (-0.62 + ordinal * 0.155);
            Point point = flowCentrePointAt(water, anchor, along);
            double level = countryWaterElevationLevelAt(
                countryElevationLevelAt(seed, biomeKey, point.x, point.y));
            mouths.push(new MountainSourceCandidate
            {
                x = point.x,
                y = point.y,
                level = level,
                score =
                    level + countryHash(seed, SALT.waterDetail ^ 0x51ed270b, countryX * 17 + ordinal, countryY) * 0.2,
            });
        }
        mouths.sort((left, right) => left.score - right.score);

        var sources = new List<MountainSourceCandidate>();
        const int grid = 6;
        const int margin = 10;
        double stride = (double)(ENDLESS_COUNTRY_TILES - margin * 2) / (grid - 1);
        for (int gy = 0; gy < grid; gy++)
        {
            for (int gx = 0; gx < grid; gx++)
            {
                int keyX = countryX * 71 + gx;
                int keyY = countryY * 71 + gy;
                double x =
                    minTileX +
                    margin +
                    gx * stride +
                    (countryHash(seed, SALT.heightDetail ^ 0x9e3779b9, keyX, keyY) - 0.5) * 7;
                double y =
                    minTileY +
                    margin +
                    gy * stride +
                    (countryHash(seed, SALT.heightDetail ^ 0x85ebca6b, keyX, keyY) - 0.5) * 7;
                // Keep the authored onboarding bowl dry. The source plan still exists for Country zero, but its head is
                // selected outside the first protected view and can join the river in a neighbouring chunk.
                if (countryX == 0 && countryY == 0 && Math.hypot(x - 15.5, y - 15.5) < 48) continue;
                double level = countryWaterElevationLevelAt(countryElevationLevelAt(seed, biomeKey, x, y));
                sources.push(new MountainSourceCandidate
                {
                    x = x,
                    y = y,
                    level = level,
                    score = level + countryHash(seed, SALT.water ^ 0xc2b2ae35, keyX, keyY) * 0.2,
                });
            }
        }
        sources.sort((left, right) => right.score - left.score);

        double secondStreamRate = countryClamp01(
            0.18 +
                traits.channelComplexity * 0.34 +
                traits.waterBias * 0.24 +
                traits.reliefRuggedness * 0.16);
        int desiredCount =
            countryHash(seed, SALT.waterDetail ^ 0x27d4eb2f, countryX, countryY) < secondStreamRate ? 2 : 1;
        var streams = new List<EndlessCountryMountainStream>();
        for (int ordinal = 0; ordinal < desiredCount; ordinal++)
        {
            MountainSourceCandidate? mouth = mouths.find((candidate) =>
                streams.every(
                    (stream) => Math.hypot(candidate.x - stream.mouth.x, candidate.y - stream.mouth.y) >= 24));
            if (mouth == null) break;
            MountainSourceCandidate? source = sources.find(
                (candidate) =>
                    candidate.level > mouth.level &&
                    Math.hypot(candidate.x - mouth.x, candidate.y - mouth.y) >= 38 &&
                    streams.every(
                        (stream) =>
                            Math.hypot(candidate.x - stream.source.x, candidate.y - stream.source.y) >= 34));
            if (source == null) break;

            double dx = mouth.x - source.x;
            double dy = mouth.y - source.y;
            double length = Math.max(1, Math.hypot(dx, dy));
            double normalX = -dy / length;
            double normalY = dx / length;
            double bendSign = countryHash(seed, SALT.water ^ ordinal, countryX, countryY) < 0.5 ? -1 : 1;
            double bend =
                bendSign *
                (8 +
                    countryHash(seed, SALT.waterDetail ^ ordinal, countryX, countryY) * Math.min(22, length * 0.24));
            double controlX = (source.x + mouth.x) * 0.5 + normalX * bend;
            double controlY = (source.y + mouth.y) * 0.5 + normalY * bend;
            double phase =
                countryHash(seed, SALT.waterDetail ^ 0x165667b1, countryX, countryY + ordinal) * Math.PI * 2;
            double waves = 1.5 + countryHash(seed, SALT.water ^ 0x5bd1e995, countryX + ordinal, countryY) * 1.5;
            var points = new List<Point>();
            const int segments = 12;
            for (int step = 0; step <= segments; step++)
            {
                if (step == 0)
                {
                    points.push(new Point(source.x, source.y));
                    continue;
                }
                if (step == segments)
                {
                    points.push(new Point(mouth.x, mouth.y));
                    continue;
                }
                double t = (double)step / segments;
                double inverse = 1 - t;
                double meander =
                    Math.sin(Math.PI * t) *
                    Math.sin(t * Math.PI * 2 * waves + phase) *
                    (1.8 + traits.channelComplexity * 2.4);
                points.push(new Point(
                    inverse * inverse * source.x +
                        2 * inverse * t * controlX +
                        t * t * mouth.x +
                        normalX * meander,
                    inverse * inverse * source.y +
                        2 * inverse * t * controlY +
                        t * t * mouth.y +
                        normalY * meander));
            }
            double halfWidth = 1.55 + traits.channelComplexity * 0.72;
            double sourcePoolRadius = 4.2 + traits.lakeStrength * 2.2;
            double boundsPadding = Math.max(sourcePoolRadius, halfWidth + 1);
            streams.push(new EndlessCountryMountainStream
            {
                source = new Point(source.x, source.y),
                mouth = new Point(mouth.x, mouth.y),
                points = points,
                halfWidth = halfWidth,
                sourcePoolRadius = sourcePoolRadius,
                sourceLevel = source.level,
                mouthLevel = mouth.level,
                minX = Math.min(points.map((point) => point.x).ToArray()) - boundsPadding,
                minY = Math.min(points.map((point) => point.y).ToArray()) - boundsPadding,
                maxX = Math.max(points.map((point) => point.x).ToArray()) + boundsPadding,
                maxY = Math.max(points.map((point) => point.y).ToArray()) + boundsPadding,
            });
        }
        return streams;
    }

    /// <summary>Pure point sample used by chunk painting, elevation and the cross-seam stream contract tests.</summary>
    public static EndlessCountryMountainStreamSample? endlessCountryMountainStreamAt(
        EndlessCountryMountainStream stream,
        double x,
        double y)
    {
        if (x < stream.minX || y < stream.minY || x > stream.maxX || y > stream.maxY) return null;
        double sourceDx = x - stream.source.x;
        double sourceDy = y - stream.source.y;
        double sourceDistance2 = sourceDx * sourceDx + sourceDy * sourceDy;
        if (sourceDistance2 <= stream.sourcePoolRadius * stream.sourcePoolRadius)
            return new EndlessCountryMountainStreamSample { progress = 0, level = stream.sourceLevel };

        double bestDistance2 = double.PositiveInfinity;
        double bestProgress = 0;
        int segmentCount = stream.points.Count - 1;
        for (int index = 0; index < segmentCount; index++)
        {
            Point from = stream.points[index];
            Point to = stream.points[index + 1];
            double dx = to.x - from.x;
            double dy = to.y - from.y;
            double length2 = dx * dx + dy * dy;
            double local = length2 > 0 ? countryClamp01(((x - from.x) * dx + (y - from.y) * dy) / length2) : 0;
            double nearestX = from.x + dx * local;
            double nearestY = from.y + dy * local;
            double distanceX = x - nearestX;
            double distanceY = y - nearestY;
            double distance2 = distanceX * distanceX + distanceY * distanceY;
            double progress = (index + local) / segmentCount;
            double width = stream.halfWidth + progress * 0.7 + Math.sin(progress * Math.PI) * 0.28;
            if (distance2 > width * width || distance2 >= bestDistance2) continue;
            bestDistance2 = distance2;
            bestProgress = progress;
        }
        if (!Number.isFinite(bestDistance2)) return null;
        double cascadeCount = Math.max(1, Math.round((stream.sourceLevel - stream.mouthLevel) / 5));
        double cascade = Math.min(cascadeCount, Math.floor(bestProgress * (cascadeCount + 1)));
        return new EndlessCountryMountainStreamSample
        {
            progress = bestProgress,
            level = Math.max(stream.mouthLevel, stream.sourceLevel - cascade * 5),
        };
    }

    private static EndlessCountryMountainStreamSample? mountainStreamSampleForPlans(
        IReadOnlyList<EndlessCountryPlan> plans,
        double x,
        double y)
    {
        EndlessCountryMountainStreamSample? best = null;
        foreach (EndlessCountryPlan plan in plans)
        {
            foreach (EndlessCountryMountainStream stream in plan.mountainStreams)
            {
                EndlessCountryMountainStreamSample? sample = endlessCountryMountainStreamAt(stream, x, y);
                if (sample == null || (best != null && sample.level >= best.level)) continue;
                best = sample;
            }
        }
        return best;
    }

    private static IReadOnlyList<EndlessCountryLandmarkPlan> createCountryLandmarks(
        double seed,
        int countryX,
        int countryY,
        int minTileX,
        int minTileY,
        string biomeKey)
    {
        // Four set pieces are guaranteed in every Country; roughly half receive a fifth. The catalog stride is
        // coprime to 100, so one Country cannot accidentally repeat its own variant and long runs traverse the
        // complete 10x10 family/motif deck instead of over-sampling a few hash buckets.
        //
        // Two-to-three left roughly three quarters of the walkable world with no composed moment in it at all —
        // measurably, a landmark reached only 25% of ground and 15% of camera frames had no set piece anywhere in
        // them. A Country is 10,000 world units across, so even five of them are a minute apart.
        int count = 4 + (countryHash(seed, SALT.landmarkCount, countryX, countryY) < 0.52 ? 1 : 0);
        int firstVariant = (int)Math.floor(
            countryHash(seed, SALT.landmarkVariant, countryX, countryY) * EndlessLandmark.ENDLESS_LANDMARK_VARIANT_COUNT);
        double centreX = minTileX + ENDLESS_COUNTRY_TILES * 0.5;
        double centreY = minTileY + ENDLESS_COUNTRY_TILES * 0.5;
        double baseAngle = countryHash(seed, SALT.landmarkAngle, countryX, countryY) * Math.PI * 2;
        var landmarks = new List<EndlessCountryLandmarkPlan>();

        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            EndlessLandmarkVariant variant = EndlessLandmark.endlessLandmarkVariantAt(firstVariant + ordinal * 37);
            double angle =
                baseAngle +
                ordinal * 2.399963229728653 +
                (countryHash(seed, SALT.landmarkAngle ^ ordinal, countryX, countryY) - 0.5) * 0.44;
            // Spread the set pieces over the Country's usable disc by AREA (hence the square root), not by a linear
            // step: a linear stride ran the later ordinals past the clamp margin and piled them on the boundary.
            double distance =
                14 +
                Math.sqrt((ordinal + 0.5) / count) * 27 +
                countryHash(seed, SALT.landmarkRadius ^ ordinal, countryX, countryY) * 5;
            double margin = variant.radius + 3;
            double x = countryClamp(
                centreX + Math.cos(angle) * distance,
                minTileX + margin,
                minTileX + ENDLESS_COUNTRY_TILES - 1 - margin);
            double y = countryClamp(
                centreY + Math.sin(angle) * distance,
                minTileY + margin,
                minTileY + ENDLESS_COUNTRY_TILES - 1 - margin);

            // Origin safety is spatial rather than variant-specific: the first camera view remains a broad court even
            // when the Country's random angular deck would otherwise put a maze or sinkhole on top of the spawn.
            if (countryX == 0 && countryY == 0 && Math.hypot(x - 15.5, y - 15.5) < 34)
            {
                angle += Math.PI;
                x = countryClamp(
                    centreX + Math.cos(angle) * distance,
                    minTileX + margin,
                    minTileX + ENDLESS_COUNTRY_TILES - 1 - margin);
                y = countryClamp(
                    centreY + Math.sin(angle) * distance,
                    minTileY + margin,
                    minTileY + ENDLESS_COUNTRY_TILES - 1 - margin);
            }
            double landmarkAngle = angle + variant.phase;
            double landmarkAspect = 0.82 + variant.motifIndex * 0.012;
            double landmarkCos = Math.cos(landmarkAngle);
            double landmarkSin = Math.sin(landmarkAngle);
            double boundaryMinLevel = MAX_ELEVATION;
            double boundaryMaxLevel = MIN_ELEVATION;
            // At roughly one sample per perimeter tile, the unsampled gap is below one eight-neighbour step. The
            // relief composer retains a two-level safety margin on top of that bound, so its cones/rings are already
            // back on the host field before the landmark footprint ends.
            double boundarySamples = Math.max(32, Math.ceil(Math.PI * 2 * variant.radius));
            for (int sample = 0; sample < boundarySamples; sample++)
            {
                double theta = (sample / boundarySamples) * Math.PI * 2;
                double u = Math.cos(theta) * variant.radius;
                double v = Math.sin(theta) * variant.radius * landmarkAspect;
                double level = countryElevationLevelAt(
                    seed,
                    biomeKey,
                    Math.round(x + u * landmarkCos - v * landmarkSin),
                    Math.round(y + u * landmarkSin + v * landmarkCos));
                boundaryMinLevel = Math.min(boundaryMinLevel, level);
                boundaryMaxLevel = Math.max(boundaryMaxLevel, level);
            }
            landmarks.push(new EndlessCountryLandmarkPlan
            {
                ordinal = ordinal,
                x = x,
                y = y,
                angle = landmarkAngle,
                variant = variant,
                centreLevel = countryElevationLevelAt(seed, biomeKey, Math.round(x), Math.round(y)),
                boundaryMinLevel = boundaryMinLevel,
                boundaryMaxLevel = boundaryMaxLevel,
            });
        }
        return landmarks;
    }

    private static EndlessCountryPlan createCountryPlan(
        double seed,
        int countryX,
        int countryY,
        string biomeKey,
        int generationVersion)
    {
        int minChunkX = countryMinChunk(countryX);
        int minChunkY = countryMinChunk(countryY);
        int minTileX = minChunkX * ENDLESS_CHUNK_TILES;
        int minTileY = minChunkY * ENDLESS_CHUNK_TILES;
        Point root = countryRootLocal(seed, countryX, countryY);
        int rootChunkX = minChunkX + (int)root.x;
        int rootChunkY = minChunkY + (int)root.y;
        ChunkNode node = chunkNodeAt(seed, rootChunkX, rootChunkY);
        double centreJitterX = (countryHash(seed, SALT.centreX, countryX, countryY) - 0.5) * 12;
        double centreJitterY = (countryHash(seed, SALT.centreY, countryX, countryY) - 0.5) * 12;
        var anchor = new Point(node.x + centreJitterX, node.y + centreJitterY);
        // TS passes an explicit `undefined` for the field rate from V5 on, which selects the callee's default.
        string? landscapeBiomeKey =
            generationVersion >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION ? biomeKey : null;
        EndlessCountryLandscape landscape =
            generationVersion >= ENDLESS_WAVE_GENERATION_VERSION
                ? generationVersion >= ENDLESS_DISTRICT_GENERATION_VERSION
                    ? EndlessLandscape.endlessCountryLandscapeAt(seed, countryX, countryY, landscapeBiomeKey)
                    : EndlessLandscape.endlessCountryLandscapeAt(
                        seed,
                        countryX,
                        countryY,
                        landscapeBiomeKey,
                        EndlessLandscape.LEGACY_LANDSCAPE_FIELD_RATE)
                : EndlessLandscape.endlessCountryLandscapeAtLegacy(seed, countryX, countryY);
        double openness = countryClamp01(0.52 + landscape.traits.opennessBias * 0.45);
        bool wildTerrain = generationVersion >= ENDLESS_WILD_HYDROLOGY_GENERATION_VERSION;
        double angle = countryHash(seed, SALT.angle, countryX, countryY) * Math.PI;
        var primaryShelf = new CountryEllipse
        {
            x = anchor.x,
            y = anchor.y,
            rx = wildTerrain
                ? 44 + openness * 18 + countryHash(seed, SALT.radiusX, countryX, countryY) * 4
                : 39 + openness * 9 + countryHash(seed, SALT.radiusX, countryX, countryY) * 4,
            ry = wildTerrain
                ? 33 + openness * 16 + countryHash(seed, SALT.radiusY, countryX, countryY) * 4
                : 27 + openness * 7 + countryHash(seed, SALT.radiusY, countryX, countryY) * 4,
            angle = angle,
        };
        double secondaryAngle = angle + (countryHash(seed, SALT.secondary, countryX, countryY) - 0.5) * 1.7;
        double secondaryDistance = 25 + countryHash(seed, SALT.secondary ^ 0x27d4eb2f, countryX, countryY) * 18;
        var secondaryShelf = new CountryEllipse
        {
            x = anchor.x + Math.cos(secondaryAngle) * secondaryDistance,
            y = anchor.y + Math.sin(secondaryAngle) * secondaryDistance,
            rx = wildTerrain ? 20 + openness * 14 : 19 + openness * 7,
            ry = wildTerrain ? 13 + openness * 12 : 11 + openness * 5,
            angle = secondaryAngle,
        };
        EndlessCountryMazePlan maze = EndlessCountryMaze.createEndlessCountryMazePlan(
            seed,
            countryX,
            countryY,
            angle,
            openness,
            biomeKey,
            landscape,
            generationVersion >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION,
            generationVersion >= ENDLESS_WILD_HYDROLOGY_GENERATION_VERSION);
        TerrainProfile profile = Terrain.terrainProfileFor(biomeKey);
        double waterScale =
            biomeKey == "starforged_cathedral_endrun"
                ? 0.59
                : biomeKey == "rainbowland"
                    ? 0.73
                    : biomeKey == "abyssal_deepsea"
                        ? 1.02
                        : biomeKey == "clockwork_moon_bazaar" || biomeKey == "noir_sprawl"
                            ? 0.79
                            : 0.9;
        double chasmScale =
            (generationVersion >= ENDLESS_WAVE_GENERATION_VERSION ? 0.86 : 1) *
            // V3's constrained landscape waves can intentionally keep several Grand Canyon Countries adjacent.
            // The original single-Country aperture then overlaps into a screen-filling void; retain the canyon as
            // the dominant landmark while keeping a real desktop gameplay viewport inside the geological frame.
            (generationVersion >= ENDLESS_WAVE_GENERATION_VERSION &&
            landscape.primary == EndlessLandscapeKind.GrandCanyon
                ? 0.64
                : biomeKey == "starforged_cathedral_endrun"
                    ? 0.78
                    : biomeKey == "olympian_sky_borough" || biomeKey == "abyssal_deepsea"
                        ? 1.08
                        : biomeKey == "sakura_temple_dream" ||
                            biomeKey == "rainbowland" ||
                            biomeKey == "sugarstorm_carnival"
                            ? 0.82
                            : 1);
        CountryFlowFeature water = createFlowFeature(
            seed,
            countryX,
            countryY,
            anchor,
            landscape.traits,
            true,
            profile.rivers != null,
            waterScale);
        return new EndlessCountryPlan
        {
            version = generationVersion,
            seed = seed,
            biomeKey = biomeKey,
            countryX = countryX,
            countryY = countryY,
            minChunkX = minChunkX,
            minChunkY = minChunkY,
            minTileX = minTileX,
            minTileY = minTileY,
            rootChunkX = rootChunkX,
            rootChunkY = rootChunkY,
            anchor = anchor,
            landscape = landscape,
            primaryShelf = primaryShelf,
            secondaryShelf = secondaryShelf,
            water = water,
            mountainStreams = createMountainStreams(
                seed,
                countryX,
                countryY,
                minTileX,
                minTileY,
                anchor,
                water,
                landscape.traits,
                biomeKey,
                profile.rivers != null,
                generationVersion),
            chasm = createFlowFeature(
                seed,
                countryX,
                countryY,
                anchor,
                landscape.traits,
                false,
                false,
                chasmScale),
            maze = maze,
            landmarks = createCountryLandmarks(seed, countryX, countryY, minTileX, minTileY, biomeKey),
            quietAngle = countryHash(seed, SALT.dressingDetail, countryX, countryY) * Math.PI * 2,
            dressingDensity = countryClamp(0.64 + landscape.traits.rockDensity * 0.16, 0.6, 0.82),
        };
    }

    public static EndlessCountryPlan endlessCountryPlanFor(
        double seed,
        int countryX,
        int countryY,
        string biomeKey,
        int generationVersion = ENDLESS_GENERATION_VERSION)
    {
        string key = $"{Js.Str(generationVersion)}:{Js.Str(seed)}:{biomeKey}:{Js.Str(countryX)}:{Js.Str(countryY)}";
        if (planCache.TryGetValue(key, out EndlessCountryPlan cached) && cached != null) return cached;
        EndlessCountryPlan plan = createCountryPlan(seed, countryX, countryY, biomeKey, generationVersion);
        planCache.set(key, plan);
        if (planCache.size > PLAN_CACHE_LIMIT) planCache.deleteOldest();
        return plan;
    }

    private static List<EndlessCountryPlan> plansNearChunk(
        double seed,
        int cx,
        int cy,
        string biomeKey,
        int generationVersion)
    {
        int minCountryX = endlessCountryCoordForTile(cx * ENDLESS_CHUNK_TILES - 80);
        int maxCountryX = endlessCountryCoordForTile((cx + 1) * ENDLESS_CHUNK_TILES + 80);
        int minCountryY = endlessCountryCoordForTile(cy * ENDLESS_CHUNK_TILES - 80);
        int maxCountryY = endlessCountryCoordForTile((cy + 1) * ENDLESS_CHUNK_TILES + 80);
        var plans = new List<EndlessCountryPlan>();
        for (int countryY = minCountryY; countryY <= maxCountryY; countryY++)
        {
            for (int countryX = minCountryX; countryX <= maxCountryX; countryX++)
            {
                plans.push(endlessCountryPlanFor(seed, countryX, countryY, biomeKey, generationVersion));
            }
        }
        return plans;
    }

    private static double ellipseValue(CountryEllipse feature, double x, double y)
    {
        double dx = x - feature.x;
        double dy = y - feature.y;
        double c = Math.cos(feature.angle);
        double s = Math.sin(feature.angle);
        double u = dx * c + dy * s;
        double v = -dx * s + dy * c;
        return (u * u) / (feature.rx * feature.rx) + (v * v) / (feature.ry * feature.ry);
    }

    private static bool insideShelf(EndlessCountryPlan plan, double x, double y)
    {
        double edge =
            (valueNoise(Js.ToUint32(Js.ToInt32(plan.seed) ^ Js.ToInt32(SALT.edgeNoise)), x, y, 23) - 0.5) *
            (0.12 + plan.landscape.traits.reliefRuggedness * 0.06);
        return
            ellipseValue(plan.primaryShelf, x, y) <= 1 + edge ||
            ellipseValue(plan.secondaryShelf, x, y) <= 1 + edge * 0.8;
    }

    private static EndlessCountryPlan? shelfOwnerAt(IReadOnlyList<EndlessCountryPlan> plans, double x, double y)
    {
        EndlessCountryPlan? owner = null;
        double ownerScore = double.PositiveInfinity;
        foreach (EndlessCountryPlan plan in plans)
        {
            if (!insideShelf(plan, x, y)) continue;
            double score = Math.min(
                ellipseValue(plan.primaryShelf, x, y),
                ellipseValue(plan.secondaryShelf, x, y) + 0.08);
            if (score >= ownerScore) continue;
            owner = plan;
            ownerScore = score;
        }
        return owner;
    }

    /// <summary>Owning 128x128 Country field for the Solid background outside all shelf silhouettes.</summary>
    private static EndlessCountryPlan? countryPlanAtTile(IReadOnlyList<EndlessCountryPlan> plans, double x, double y)
    {
        int countryX = endlessCountryCoordForTile(x);
        int countryY = endlessCountryCoordForTile(y);
        foreach (EndlessCountryPlan plan in plans)
        {
            if (plan.countryX == countryX && plan.countryY == countryY) return plan;
        }
        return null;
    }

    // TS `interface CountryMazeCoordinates { u; v }` — an immutable pair, so a value tuple.
    private static (double u, double v) countryMazeCoordinates(EndlessCountryPlan plan, double x, double y)
    {
        double dx = x - plan.anchor.x;
        double dy = y - plan.anchor.y;
        double c = Math.cos(plan.maze.angle);
        double s = Math.sin(plan.maze.angle);
        double rawU = dx * c + dy * s;
        double rawV = -dx * s + dy * c;
        double detail =
            (valueNoise(Js.ToUint32(Js.ToInt32(plan.seed) ^ Js.ToInt32(SALT.mazeDetail)), x + 17, y - 23, 27) - 0.5) * plan.maze.warp;
        return (
            rawU + Math.sin(rawV * 0.075 + plan.maze.phase) * plan.maze.warp * 0.72 + detail,
            rawV +
                Math.sin(rawU * 0.061 - plan.maze.phase * 0.73) * plan.maze.warp * 0.58 -
                detail * 0.46);
    }

    private static double distanceToBand(double value, double spacing) =>
        Math.abs(value - Math.round(value / spacing) * spacing);

    /// <summary>A boundary gate is keyed by the boundary and its along-wall lane, so both sides cut the same opening.</summary>
    private static bool mazeGateOpen(
        EndlessCountryPlan plan,
        double boundary,
        double lane,
        double along,
        double spacing,
        bool vertical)
    {
        double roll = countryHash(
            plan.seed,
            SALT.mazeDetail ^ (vertical ? 0x9e3779b9 : 0x85ebca6b),
            plan.countryX * 97 + boundary,
            plan.countryY * 97 + lane);
        double centre = (lane + 0.2 + roll * 0.6) * spacing;
        return Math.abs(along - centre) <= plan.maze.gateWidth;
    }

    /// <summary>
    /// Hard ceiling on the pitch of the fallback structure grid, in tiles.
    ///
    /// Every grammar leans on <see cref="brokenGridWallAt"/> as its underlying interruption, and the Country plan already
    /// promises "another structural interruption within sixteen tiles". The open grammars scale that grid up by as
    /// much as 2.25x, though, which quietly let a wide district reach a 36-tile cell — and where the grammar's own
    /// rib pattern happened to gate open across it, the result was a 22x22 all-floor plate, a 1,700-unit plaza with
    /// nothing in it. Clamping the pitch here keeps every kind's character (the scale ratios still order them from
    /// tight to open) while making the promise true by construction rather than by hope.
    /// </summary>
    private const double MAX_MAZE_CELL_TILES = 16;

    private static bool brokenGridWallAt(
        EndlessCountryPlan plan,
        double u,
        double v,
        double xScale = 1,
        double yScale = 1,
        double widthScale = 1)
    {
        double sx = Math.min(MAX_MAZE_CELL_TILES, plan.maze.spacing * xScale);
        double sy = Math.min(MAX_MAZE_CELL_TILES, plan.maze.spacing * yScale);
        double xBoundary = Math.round(u / sx);
        double yBoundary = Math.round(v / sy);
        double xLane = Math.floor(v / sy);
        double yLane = Math.floor(u / sx);
        bool vertical =
            Math.abs(u - xBoundary * sx) <= plan.maze.wallWidth * widthScale &&
            !mazeGateOpen(plan, xBoundary, xLane, v, sy, true);
        bool horizontal =
            Math.abs(v - yBoundary * sy) <= plan.maze.wallWidth * widthScale &&
            !mazeGateOpen(plan, yBoundary, yLane, u, sx, false);
        return vertical || horizontal;
    }

    /// <summary>
    /// Resolve the district a world cell belongs to.
    ///
    /// The lattice is warped by a slow noise field before it is quantized, so district boundaries wander as
    /// geological faults instead of drawing an axis-aligned chequerboard over the world — the boundary itself must
    /// not become the new repeating pattern. Districts are keyed in WORLD tiles rather than Country-local ones, so
    /// they cross Country seams without a discontinuity and both sides of a streamed chunk seam agree by
    /// construction.
    /// </summary>
    private static (double dx, double dy) districtCoordAt(EndlessCountryPlan plan, double x, double y)
    {
        double warpX = (valueNoise(Js.ToUint32(Js.ToInt32(plan.seed) ^ Js.ToInt32(SALT.districtWarp)), x + 31, y - 17, 29) - 0.5) * 19;
        double warpY =
            (valueNoise(Js.ToUint32(Js.ToInt32(plan.seed) ^ Js.ToInt32(SALT.districtDetail)), x - 53, y + 41, 29) - 0.5) * 19;
        return (
            Math.floor((x + warpX) / COUNTRY_DISTRICT_TILES),
            Math.floor((y + warpY) / COUNTRY_DISTRICT_TILES));
    }

    [ThreadStatic] private static FifoCache<string, EndlessCountryPlan>? districtPlanCacheStorage;

    private static FifoCache<string, EndlessCountryPlan> districtPlanCache =>
        districtPlanCacheStorage ??= new FifoCache<string, EndlessCountryPlan>();

    private const int DISTRICT_CACHE_LIMIT = 512;

    // Single-entry memo for the district lookup, keyed on the OWNER OBJECT and the two district coordinates.
    //
    // This is the hottest path in chunk generation — one call per cell from `paintLargeShelves` — and the
    // identity comparison matters: building the string cache key on every call allocated a fresh string per cell
    // (1024 per chunk) and made generation several times slower on its own. Painting is row-major and a district
    // spans `COUNTRY_DISTRICT_TILES` cells, so this hits for almost every cell and the Map (and its key) is
    // only touched when the district genuinely changes.
    [ThreadStatic] private static EndlessCountryPlan? lastDistrictOwner;
    [ThreadStatic] private static double lastDistrictX;
    [ThreadStatic] private static double lastDistrictY;
    [ThreadStatic] private static EndlessCountryPlan? lastDistrictPlan;

    /// <summary>
    /// The Country plan as seen from one district: same landform, anchor, hydrology and set pieces, but its own
    /// maze grammar.
    ///
    /// Roughly half the districts redraw the KIND from the very same landscape-weighted deck the Country used, so
    /// a switchback pass can give way to a honeycomb warren without either being foreign to the landform; the rest
    /// keep the Country's kind and vary only its continuous parameters. Either way the lattice angle, pitch, phase
    /// and warp all move, so two neighbouring districts never read as one infinite grid. Nothing here can break
    /// connectivity: the route network and the Flood spine are carved AFTER the maze and are protected by their
    /// own masks, and anything a mismatched district boundary strands is pruned by `dropDisconnectedWalkable`.
    /// </summary>
    private static EndlessCountryPlan countryDistrictPlanFor(EndlessCountryPlan plan, double x, double y)
    {
        var (dx, dy) = districtCoordAt(plan, x, y);
        // Painting is row-major over a chunk, so consecutive cells almost always share a district. Comparing the
        // owner by identity plus two integers keeps this allocation-free.
        if (
            ReferenceEquals(plan, lastDistrictOwner) &&
            dx == lastDistrictX &&
            dy == lastDistrictY &&
            lastDistrictPlan != null)
            return lastDistrictPlan;
        string key = $"{Js.Str(plan.version)}:{Js.Str(plan.seed)}:{plan.biomeKey}:{Js.Str(plan.countryX)}:{Js.Str(plan.countryY)}:{Js.Str(dx)}:{Js.Str(dy)}";
        if (!districtPlanCache.TryGetValue(key, out EndlessCountryPlan district) || district == null)
        {
            EndlessCountryMazePlan @base = plan.maze;
            double roll = countryHash(plan.seed, SALT.district, dx, dy);
            double angleRoll = countryHash(plan.seed, SALT.district ^ 0x9e3779b9, dx, dy);
            double spacingRoll = countryHash(plan.seed, SALT.district ^ 0x85ebca6b, dx, dy);
            double phaseRoll = countryHash(plan.seed, SALT.district ^ 0x27d4eb2f, dx, dy);
            double warpRoll = countryHash(plan.seed, SALT.district ^ 0x165667b1, dx, dy);
            int kind =
                roll < 0.46
                    ? @base.kind
                    : EndlessCountryMaze.endlessCountryMazeKindFor(
                        plan.seed,
                        plan.countryX * 131 + dx,
                        plan.countryY * 131 + dy,
                        plan.biomeKey,
                        plan.landscape);
            double wallWidth = @base.wallWidth * (0.92 + warpRoll * 0.24);
            var maze = new EndlessCountryMazePlan
            {
                kind = kind,
                angle = @base.angle + (angleRoll - 0.5) * 0.86,
                // The Country's own pitch bound still applies: even the widest district keeps another structural
                // interruption inside sixteen tiles, so no district can relax into a floor sheet.
                spacing = countryClamp(@base.spacing * (0.84 + spacingRoll * 0.44), 9.5, 16),
                wallWidth = wallWidth,
                gateWidth = wallWidth + (@base.gateWidth - @base.wallWidth),
                warp = @base.warp * (0.78 + warpRoll * 0.56),
                phase = @base.phase + phaseRoll * Math.PI * 2,
            };
            district = plan.Clone();
            district.maze = maze;
            districtPlanCache.set(key, district);
            if (districtPlanCache.size > DISTRICT_CACHE_LIMIT) districtPlanCache.deleteOldest();
        }
        lastDistrictOwner = plan;
        lastDistrictX = dx;
        lastDistrictY = dy;
        lastDistrictPlan = district;
        return district;
    }

    /// <summary>
    /// Side length, in tiles, of the guaranteed cover lattice.
    ///
    /// Every grammar has places where its own rhythm falls quiet — a shard field below its threshold, a ward's
    /// rings flattening far from the Country anchor, a gate landing along a lane. Where two of those coincide the
    /// result was a bare 22x22 floor plate: 1,700 world units, half a camera view, with nothing to look at and
    /// nothing to fight behind. This lattice drops one small rock outcrop into every cell, so open ground is
    /// interrupted well inside one view no matter which grammar owns it. It is a pure function of world position,
    /// so both sides of a streamed seam agree, and it is applied BEFORE routes, rooms, courts and hydrology are
    /// carved — every one of which overwrites it, so it can never obstruct an authored path or a set piece.
    /// </summary>
    private const int COVER_OUTCROP_TILES = 12;
    /// <summary>V11's open country uses sparse landmark rocks instead of a stump-like twelve-tile wall lattice.</summary>
    private const int WILD_COVER_OUTCROP_TILES = 18;
    /// <summary>
    /// How far an outcrop may wander from its cell centre.
    ///
    /// The jitter has to be bounded, not free: with a full-cell jitter two neighbouring outcrops can drift to
    /// opposite corners and leave a 22-cell gap between them — wide enough to re-open exactly the bare plate this
    /// lattice exists to prevent. Holding each one inside a five-cell window puts the worst case at
    /// `pitch + 4` cells (16 historically, 22 in V11's deliberately more open terrain), so the bound is provable
    /// rather than probable, while the jitter still keeps the field from reading as a grid.
    /// </summary>
    private const int COVER_OUTCROP_JITTER = 5;

    /// <summary>
    /// One jittered single-cell outcrop per lattice cell — scattered cover, never a readable grid.
    ///
    /// Deliberately one cell and never two. A single 78-unit block breaks the open plate (which is all the
    /// guarantee needs: one non-walkable cell defeats any square containing it) while still leaving body-width
    /// passage on either side of it. A two-cell block dropped beside an existing rib pinched real corridors, and
    /// the horde motion audit caught it immediately: packs funnelling through a chokepoint zig-zagged past the
    /// 45-degree heading-step budget. Cover must add a thing to fight behind, not a thing to get stuck on.
    /// </summary>
    private static bool countryCoverOutcropAt(EndlessCountryPlan plan, double x, double y)
    {
        int pitch =
            plan.version >= ENDLESS_WILD_HYDROLOGY_GENERATION_VERSION
                ? WILD_COVER_OUTCROP_TILES
                : COVER_OUTCROP_TILES;
        double cellX = Math.floor(x / pitch);
        double cellY = Math.floor(y / pitch);
        int offset = (pitch - COVER_OUTCROP_JITTER) >> 1;
        double anchorX =
            cellX * pitch +
            offset +
            Math.floor(countryHash(plan.seed, SALT.district ^ 0x51ed270b, cellX, cellY) * COVER_OUTCROP_JITTER);
        double anchorY =
            cellY * pitch +
            offset +
            Math.floor(countryHash(plan.seed, SALT.district ^ 0xc2b2ae35, cellX, cellY) * COVER_OUTCROP_JITTER);
        return x == anchorX && y == anchorY;
    }

    private static bool countryMazeWallAt(EndlessCountryPlan country, double x, double y)
    {
        // V4 is already persisted in production saves. Its one-grammar-per-Country raster must remain byte-stable;
        // districts and the guaranteed cover lattice are V5 semantic changes, not a silent rewrite of an existing
        // generation.
        if (country.version < ENDLESS_DISTRICT_GENERATION_VERSION)
        {
            var (u0, v0) = countryMazeCoordinates(country, x, y);
            return countryMazeGrammarWallAt(country, x, y, u0, v0);
        }
        EndlessCountryPlan plan = countryDistrictPlanFor(country, x, y);
        if (countryCoverOutcropAt(plan, x, y)) return true;
        var (u, v) = countryMazeCoordinates(plan, x, y);
        return countryMazeGrammarWallAt(plan, x, y, u, v);
    }

    private static bool countryMazeGrammarWallAt(
        EndlessCountryPlan plan,
        double x,
        double y,
        double u,
        double v)
    {
        double spacing = plan.maze.spacing;
        double width = plan.maze.wallWidth;
        double phase = plan.maze.phase;
        double radius = Math.hypot(u, v);
        double theta = Math.atan2(v, u);

        // Roughly one cell in eleven becomes a deliberately quieter combat/readability pocket. Its cell boundary
        // stays intact, so a pocket is a composed room in the maze rather than the beginning of another floor slab.
        double cellX = Math.floor(u / spacing);
        double cellY = Math.floor(v / spacing);
        double localU = positiveModulo(u, spacing) - spacing * 0.5;
        double localV = positiveModulo(v, spacing) - spacing * 0.5;
        bool quietPocket =
            countryHash(plan.seed, SALT.mazePhase, plan.countryX * 83 + cellX, plan.countryY * 83 + cellY) <
                0.09 && Math.hypot(localU, localV) < spacing * 0.34;
        if (quietPocket) return false;

        switch (plan.maze.kind)
        {
            case EndlessCountryMazeKind.BrokenGrid:
                return brokenGridWallAt(plan, u, v);

            case EndlessCountryMazeKind.BraidedAisles:
            {
                double braidedV = v + Math.sin(u * 0.12 + phase) * plan.maze.warp * 1.35;
                return
                    brokenGridWallAt(plan, u, braidedV, 1.72, 0.92, 0.92) ||
                    (distanceToBand(v - u * 0.18, spacing * 2.3) < width * 0.62 &&
                        Math.sin(u * 0.34 + phase) < 0.55);
            }

            case EndlessCountryMazeKind.Switchbacks:
            {
                double row = Math.floor(v / spacing);
                double shiftedU = u + (row % 2 == 0 ? spacing * 0.31 : -spacing * 0.31);
                return
                    brokenGridWallAt(plan, shiftedU, v, 1.28, 0.78, 0.9) ||
                    (distanceToBand(v + Math.sin(u * 0.085 + phase) * 2.4, spacing * 0.52) < width * 0.46 &&
                        positiveModulo(u + row * spacing * 0.37, spacing * 2.4) > spacing * 0.52);
            }

            case EndlessCountryMazeKind.Honeycomb:
            {
                double a = u;
                double b = u * 0.5 + v * 0.8660254038;
                double c = u * 0.5 - v * 0.8660254038;
                bool onHexRib =
                    distanceToBand(a, spacing * 1.18) < width * 0.76 ||
                    distanceToBand(b, spacing * 1.18) < width * 0.76 ||
                    distanceToBand(c, spacing * 1.18) < width * 0.76;
                bool gateBeat = Math.sin((u - v) * 0.31 + phase) > 0.64;
                return (onHexRib && !gateBeat) || brokenGridWallAt(plan, u, v, 2.05, 2.05, 0.54);
            }

            case EndlessCountryMazeKind.ConcentricWards:
            {
                bool ring = distanceToBand(radius, spacing * 0.82) < width;
                bool ringGate = Math.cos(theta * 7 + phase + Math.floor(radius / spacing) * 0.9) > 0.72;
                double arc = theta * Math.max(spacing, radius);
                bool spoke = distanceToBand(arc + phase * spacing, spacing * 2.15) < width * 0.7;
                bool spokeGate =
                    distanceToBand(radius + spacing * 0.5, spacing * 1.65) < plan.maze.gateWidth;
                // Concentric wards were the ONE grammar of twelve without the underlying interruption grid. Far from
                // the Country anchor its rings flatten and its spokes spread, so a ward district could open into a
                // 22x22 all-floor plate — a 1,700-unit plaza with nothing in it. The same wide, thin fallback the other
                // eleven kinds already carry restores the "structural interruption within sixteen tiles" promise
                // without touching the ward silhouette that gives the grammar its character.
                return
                    (ring && !ringGate) || (spoke && !spokeGate) || brokenGridWallAt(plan, u, v, 2.1, 2.1, 0.5);
            }

            case EndlessCountryMazeKind.FaultedPass:
            {
                double faultV = v + Math.sin(u * 0.09 + phase) * plan.maze.warp * 1.8;
                return
                    brokenGridWallAt(plan, u, faultV, 1.95, 0.74, 0.94) ||
                    (distanceToBand(v - u * 0.62 + Math.sin(u * 0.17) * 2, spacing * 1.45) < width * 0.72 &&
                        Math.cos(u * 0.26 + phase) < 0.62);
            }

            case EndlessCountryMazeKind.CourtyardChain:
            {
                double cellEdge = Math.max(Math.abs(localU), Math.abs(localV));
                bool innerRing =
                    Math.abs(cellEdge - spacing * 0.3) < width * 0.62 &&
                    Math.max(Math.abs(localU), Math.abs(localV + spacing * 0.3)) > plan.maze.gateWidth;
                return brokenGridWallAt(plan, u, v, 1.28, 1.1, 0.84) || innerRing;
            }

            case EndlessCountryMazeKind.StaggeredGates:
            {
                double row = Math.floor(v / spacing);
                double staggered = u + ((Js.ToInt32(row) & 1) != 0 ? spacing * 0.5 : 0);
                return
                    brokenGridWallAt(plan, staggered, v, 0.92, 1.18, 0.9) ||
                    (distanceToBand(u + Math.sin(v * 0.13 + phase) * 3.2, spacing * 2.05) < width * 0.58 &&
                        Math.sin(v * 0.29 - phase) < 0.5);
            }

            case EndlessCountryMazeKind.CrossVaults:
            {
                bool cross =
                    (Math.abs(localU) < width * 0.86 && Math.abs(localV) > plan.maze.gateWidth) ||
                    (Math.abs(localV) < width * 0.86 && Math.abs(localU) > plan.maze.gateWidth);
                return brokenGridWallAt(plan, u, v, 1.32, 1.32, 0.66) || cross;
            }

            case EndlessCountryMazeKind.SpiralWard:
            {
                double spiral = radius + theta * spacing * 0.68;
                bool spiralRib = distanceToBand(spiral, spacing * 0.78) < width * 0.9;
                bool spiralGate = Math.sin(radius * 0.31 - theta * 3 + phase) > 0.7;
                return (spiralRib && !spiralGate) || brokenGridWallAt(plan, u, v, 2.25, 2.25, 0.52);
            }

            case EndlessCountryMazeKind.DeltaForks:
            {
                double fork = Math.min(
                    Math.abs(v - Math.sin(u * 0.11 + phase) * plan.maze.warp),
                    Math.abs(v - u * 0.44 - Math.sin(u * 0.08) * 2.3),
                    Math.abs(v + u * 0.44 + Math.sin(u * 0.08) * 2.3));
                return
                    (fork > spacing * 0.28 && fork < spacing * 0.28 + width * 1.35) ||
                    brokenGridWallAt(plan, u, v, 1.95, 1.45, 0.58);
            }

            case EndlessCountryMazeKind.ShatteredShelf:
            {
                double shard = valueNoise(
                    Js.ToUint32(Js.ToInt32(plan.seed) ^ Js.ToInt32(SALT.mazePhase)),
                    x + plan.countryX * 31,
                    y + plan.countryY * 31,
                    8.5);
                return shard > 0.68 || brokenGridWallAt(plan, u, v, 1.5, 1.5, 0.62);
            }
        }
        return false;
    }

    private readonly struct LandmarkCoordinates
    {
        public readonly double u;
        public readonly double v;
        public readonly double radius;
        public readonly double theta;

        public LandmarkCoordinates(double u, double v, double radius, double theta)
        {
            this.u = u;
            this.v = v;
            this.radius = radius;
            this.theta = theta;
        }
    }

    private static LandmarkCoordinates landmarkCoordinates(EndlessCountryLandmarkPlan landmark, double x, double y)
    {
        double dx = x - landmark.x;
        double dy = y - landmark.y;
        double c = Math.cos(landmark.angle);
        double s = Math.sin(landmark.angle);
        double u = dx * c + dy * s;
        double v = -dx * s + dy * c;
        double aspect = 0.82 + landmark.variant.motifIndex * 0.012;
        return new LandmarkCoordinates(u, v, Math.hypot(u, v / aspect), Math.atan2(v / aspect, u));
    }

    private static double distanceToLandmarkSegment(
        double x,
        double y,
        double ax,
        double ay,
        double bx,
        double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared <= 0 ? 0 : countryClamp01(((x - ax) * dx + (y - ay) * dy) / lengthSquared);
        return Math.hypot(x - (ax + dx * t), y - (ay + dy * t));
    }

    /// <summary>Eight-neighbour-safe distance step. One diagonal cell is exactly one possible terrain level.</summary>
    private static double landmarkReliefDistanceStep(double distance) => Math.ceil(Math.max(0, distance) / Math.SQRT2);

    /// <summary>
    /// TS `interface ReliefDatum`. A base class here because both <see cref="EndlessCountryLandmarkPlan"/> and the
    /// chunk relief features structurally satisfy it.
    /// </summary>
    public class ReliefDatum
    {
        public double centreLevel;
        public double boundaryMinLevel;
        public double boundaryMaxLevel;
    }

    private static double raiseLandmarkFeature(
        double level,
        ReliefDatum landmark,
        double distance,
        double boundaryClearance,
        double amplitude)
    {
        // The boundary sample makes the feature meet the host field before its footprint is clipped. This keeps the
        // pointwise composition one-level climbable across chunks; max(two 1-Lipschitz fields) is 1-Lipschitz.
        double safePeak =
            landmark.boundaryMinLevel + Math.max(0, Math.floor(boundaryClearance / Math.SQRT2));
        double peak = Math.min(MAX_ELEVATION, landmark.centreLevel + amplitude, safePeak);
        return Math.max(level, peak - landmarkReliefDistanceStep(distance));
    }

    private static double lowerLandmarkFeature(
        double level,
        ReliefDatum landmark,
        double distance,
        double boundaryClearance,
        double amplitude)
    {
        double safeFloor =
            landmark.boundaryMaxLevel - Math.max(0, Math.floor(boundaryClearance / Math.SQRT2));
        double floor = Math.max(MIN_ELEVATION, landmark.centreLevel - amplitude, safeFloor);
        return Math.min(level, floor + landmarkReliefDistanceStep(distance));
    }

    private static class ChunkReliefKind
    {
        public const string Knoll = "knoll";
        public const string Hollow = "hollow";
        public const string Ridge = "ridge";
        public const string Ring = "ring";
        public const string Saddle = "saddle";
    }

    /// <summary>TS `ChunkReliefFeature extends ReliefDatum, Point`.</summary>
    private sealed class ChunkReliefFeature : ReliefDatum
    {
        public double x;
        public double y;
        public double radius;
        public double angle;
        public double amplitude;
        /// <summary>A <see cref="ChunkReliefKind"/> value.</summary>
        public string kind = "";
    }

    private static readonly string[] CHUNK_RELIEF_KIND_DECK =
    {
        ChunkReliefKind.Knoll,
        ChunkReliefKind.Hollow,
        ChunkReliefKind.Ridge,
        ChunkReliefKind.Ring,
        ChunkReliefKind.Saddle,
    };

    /// <summary>
    /// Two-to-four fully contained height events in every chunk.
    ///
    /// Country relief supplies the long climb and semantic landmarks supply destinations. This middle register is
    /// what prevents the 32x32 ground beneath a player from being one immaculate plate between them. Features are
    /// planned inside a two-cell seam margin and meet the base field on their own perimeter, so a neighbouring
    /// streamed chunk never has to know they exist.
    /// </summary>
    private static List<ChunkReliefFeature> createChunkReliefFeatures(
        double seed,
        string biomeKey,
        int cx,
        int cy,
        EndlessCountryPlan owner)
    {
        // The protected onboarding court deliberately stays calm. Its surrounding chunks already contain the full
        // grammar, so the first minute grows complex without spawning the colony on a staircase.
        if (cx == 0 && cy == 0) return new List<ChunkReliefFeature>();
        double ruggedness = countryClamp01(owner.landscape.traits.reliefRuggedness);
        int count = 2 + (int)Math.floor(countryHash(seed, SALT.heightDetail ^ 0x51ed270b, cx, cy) * 3);
        double waterAffinity = countryClamp01(
            owner.landscape.traits.lakeStrength * 0.48 +
                Math.max(0, owner.landscape.traits.waterBias) * 0.34 +
                owner.landscape.traits.channelComplexity * 0.18);
        double rockAffinity = countryClamp01(
            Math.max(0, owner.landscape.traits.rockDensity) * 0.52 +
                Math.max(0, owner.landscape.traits.altitudeBias) * 1.4 +
                owner.landscape.traits.escarpmentStrength * 0.34);
        var features = new List<ChunkReliefFeature>();
        int baseX = cx * ENDLESS_CHUNK_TILES;
        int baseY = cy * ENDLESS_CHUNK_TILES;
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            int radius =
                7 + (int)Math.floor(countryHash(seed, SALT.height ^ ordinal, cx * 17 + ordinal, cy * 17) * 3);
            int margin = radius + 2;
            int span = ENDLESS_CHUNK_TILES - margin * 2;
            double x =
                baseX +
                margin +
                countryHash(seed, SALT.centreX ^ ordinal, cx * 23 + ordinal, cy) * Math.max(1, span);
            double y =
                baseY +
                margin +
                countryHash(seed, SALT.centreY ^ ordinal, cx, cy * 23 + ordinal) * Math.max(1, span);
            double angle = countryHash(seed, SALT.angle ^ ordinal, cx, cy) * Math.PI;
            double kindRoll = countryHash(seed, SALT.heightDetail ^ ordinal, cx * 29 + ordinal, cy * 29);
            string kind;
            if (ordinal == 0 && waterAffinity > 0.42)
                kind = kindRoll < 0.58 ? ChunkReliefKind.Hollow : ChunkReliefKind.Ring;
            else if (ordinal == 0 && rockAffinity > 0.48)
                kind = kindRoll < 0.52 ? ChunkReliefKind.Ridge : ChunkReliefKind.Knoll;
            else
                kind = CHUNK_RELIEF_KIND_DECK[(int)Math.floor(kindRoll * 5)];

            double boundaryMinLevel = MAX_ELEVATION;
            double boundaryMaxLevel = MIN_ELEVATION;
            double samples = Math.ceil(Math.PI * 2 * radius);
            for (int sample = 0; sample < samples; sample++)
            {
                double theta = (sample / samples) * Math.PI * 2;
                double level = countryElevationLevelAt(
                    seed,
                    biomeKey,
                    Math.round(x + Math.cos(theta) * radius),
                    Math.round(y + Math.sin(theta) * radius));
                boundaryMinLevel = Math.min(boundaryMinLevel, level);
                boundaryMaxLevel = Math.max(boundaryMaxLevel, level);
            }
            features.push(new ChunkReliefFeature
            {
                x = x,
                y = y,
                radius = radius,
                angle = angle,
                kind = kind,
                amplitude = 3 + Math.round(ruggedness * 3) + (kind == ChunkReliefKind.Saddle ? 1 : 0),
                centreLevel = countryElevationLevelAt(seed, biomeKey, Math.round(x), Math.round(y)),
                boundaryMinLevel = boundaryMinLevel,
                boundaryMaxLevel = boundaryMaxLevel,
            });
        }
        return features;
    }

    private static double applyChunkReliefAt(
        double initialLevel,
        IReadOnlyList<ChunkReliefFeature> features,
        double x,
        double y)
    {
        double level = initialLevel;
        foreach (ChunkReliefFeature feature in features)
        {
            double dx = x - feature.x;
            double dy = y - feature.y;
            if (Math.abs(dx) > feature.radius || Math.abs(dy) > feature.radius) continue;
            double radius = Math.hypot(dx, dy);
            if (radius > feature.radius) continue;
            double c = Math.cos(feature.angle);
            double s = Math.sin(feature.angle);
            double u = dx * c + dy * s;
            double v = -dx * s + dy * c;
            double centralClearance = feature.radius;
            double innerExtent = feature.radius * 0.38;
            double innerClearance = feature.radius - innerExtent;
            switch (feature.kind)
            {
                case ChunkReliefKind.Knoll:
                    level = raiseLandmarkFeature(level, feature, radius, centralClearance, feature.amplitude);
                    break;
                case ChunkReliefKind.Hollow:
                    level = lowerLandmarkFeature(level, feature, radius, centralClearance, feature.amplitude);
                    break;
                case ChunkReliefKind.Ridge:
                    level = raiseLandmarkFeature(
                        level,
                        feature,
                        distanceToLandmarkSegment(u, v, -innerExtent, 0, innerExtent, 0),
                        innerClearance,
                        feature.amplitude);
                    break;
                case ChunkReliefKind.Ring:
                    level = lowerLandmarkFeature(
                        level,
                        feature,
                        radius,
                        centralClearance,
                        feature.amplitude - 1);
                    level = raiseLandmarkFeature(
                        level,
                        feature,
                        Math.abs(radius - innerExtent),
                        innerClearance,
                        feature.amplitude - 2);
                    break;
                case ChunkReliefKind.Saddle:
                    level = raiseLandmarkFeature(
                        level,
                        feature,
                        Math.hypot(u - innerExtent, v),
                        innerClearance,
                        feature.amplitude);
                    level = raiseLandmarkFeature(
                        level,
                        feature,
                        Math.hypot(u + innerExtent, v),
                        innerClearance,
                        feature.amplitude);
                    level = lowerLandmarkFeature(
                        level,
                        feature,
                        Math.hypot(u, v),
                        centralClearance,
                        Math.max(2, feature.amplitude - 2));
                    break;
            }
        }
        return countryClamp(level, MIN_ELEVATION, MAX_ELEVATION);
    }

    /// <summary>
    /// Give each landmark family a real vertical silhouette.
    ///
    /// The old 100-variant catalog changed walls, routes and hazards in plan view but all variants inherited the
    /// exact same ambient heightfield. From the camera a sunken garden, ridge gate and sinkhole crown therefore
    /// still felt like the same place. These are distance-field terraces rather than arbitrary per-cell offsets:
    /// every peak/basin is climbable in eight directions, returns to the host field inside its Country footprint,
    /// and is evaluated from absolute coordinates by both chunks when a landmark crosses their seam.
    /// </summary>
    private static double landmarkElevationLevelAt(
        double seed,
        string biomeKey,
        IReadOnlyList<EndlessCountryPlan> plans,
        double x,
        double y,
        double? initialLevel = null)
    {
        double level = initialLevel ?? countryElevationLevelAt(seed, biomeKey, x, y);
        foreach (EndlessCountryPlan plan in plans)
        {
            double ruggedness = countryClamp01(plan.landscape.traits.reliefRuggedness);
            double contrast = countryClamp(plan.landscape.traits.reliefContrast, 0.7, 1.4);
            double reliefScale = 0.76 + ruggedness * 0.34 + (contrast - 0.7) * 0.22;
            foreach (EndlessCountryLandmarkPlan landmark in plan.landmarks)
            {
                // The nearby-plan set contains at most nine Countries, but almost all of their landmarks are nowhere
                // near this cell. Reject on the unrotated AABB before paying for trig/hypot in the hot elevation path.
                double radius = landmark.variant.radius;
                if (Math.abs(x - landmark.x) > radius || Math.abs(y - landmark.y) > radius) continue;
                LandmarkCoordinates coordinates = landmarkCoordinates(landmark, x, y);
                if (coordinates.radius > radius) continue;
                double u = coordinates.u;
                double v = coordinates.v;
                double clearanceForExtent(double extent) => Math.max(2, (radius - extent) * 0.78);
                double amount(double @base) => Math.max(1, Math.round(@base * reliefScale));
                void peak(double distance, double extent, double amplitude)
                {
                    level = raiseLandmarkFeature(
                        level,
                        landmark,
                        distance,
                        clearanceForExtent(extent),
                        amount(amplitude));
                }
                void basin(double distance, double extent, double amplitude)
                {
                    level = lowerLandmarkFeature(
                        level,
                        landmark,
                        distance,
                        clearanceForExtent(extent),
                        amount(amplitude));
                }

                switch (landmark.variant.family)
                {
                    case EndlessLandmarkFamily.CascadeTerrace:
                        peak(
                            distanceToLandmarkSegment(
                                u,
                                v,
                                -radius * 0.46,
                                -radius * 0.2,
                                radius * 0.46,
                                -radius * 0.2),
                            radius * 0.51,
                            4);
                        peak(
                            distanceToLandmarkSegment(
                                u,
                                v,
                                -radius * 0.34,
                                radius * 0.2,
                                radius * 0.34,
                                radius * 0.2),
                            radius * 0.4,
                            6);
                        break;
                    case EndlessLandmarkFamily.ChasmCrossing:
                        peak(Math.hypot(u - radius * 0.34, v), radius * 0.34, 8);
                        peak(Math.hypot(u + radius * 0.34, v), radius * 0.34, 8);
                        break;
                    case EndlessLandmarkFamily.CliffGrove:
                        peak(Math.hypot(u, v), 0, 10);
                        peak(Math.hypot(u - radius * 0.3, v + radius * 0.12), radius * 0.33, 5);
                        break;
                    case EndlessLandmarkFamily.LabyrinthCourt:
                        peak(Math.abs(Math.hypot(u, v) - radius * 0.38), radius * 0.38, 5);
                        basin(Math.hypot(u, v), 0, 3);
                        break;
                    case EndlessLandmarkFamily.FloodedGarden:
                        basin(Math.hypot(u, v), 0, 8);
                        peak(Math.abs(Math.hypot(u, v) - radius * 0.46), radius * 0.46, 4);
                        break;
                    case EndlessLandmarkFamily.CrystalScar:
                        peak(
                            distanceToLandmarkSegment(
                                u,
                                v,
                                -radius * 0.48,
                                -radius * 0.08,
                                radius * 0.48,
                                radius * 0.08),
                            radius * 0.49,
                            7);
                        break;
                    case EndlessLandmarkFamily.RuinedCauseway:
                        peak(
                            distanceToLandmarkSegment(u, v, -radius * 0.48, 0, radius * 0.48, 0),
                            radius * 0.48,
                            5);
                        peak(Math.hypot(u - radius * 0.3, v), radius * 0.3, 4);
                        peak(Math.hypot(u + radius * 0.3, v), radius * 0.3, 4);
                        break;
                    case EndlessLandmarkFamily.SinkholeCrown:
                        basin(Math.hypot(u, v), 0, 10);
                        peak(Math.abs(Math.hypot(u, v) - radius * 0.44), radius * 0.44, 6);
                        break;
                    case EndlessLandmarkFamily.RidgeGate:
                        peak(Math.hypot(u - radius * 0.32, v), radius * 0.32, 10);
                        peak(Math.hypot(u + radius * 0.32, v), radius * 0.32, 10);
                        peak(
                            distanceToLandmarkSegment(u, v, -radius * 0.32, 0, radius * 0.32, 0),
                            radius * 0.32,
                            4);
                        break;
                    case EndlessLandmarkFamily.AbyssalConfluence:
                        basin(Math.hypot(u, v), 0, 9);
                        for (int spoke = 0; spoke < 3; spoke++)
                        {
                            double angle = landmark.variant.phase + ((double)spoke / 3) * Math.PI * 2;
                            peak(
                                Math.hypot(u - Math.cos(angle) * radius * 0.36, v - Math.sin(angle) * radius * 0.36),
                                radius * 0.36,
                                6);
                        }
                        break;
                }

                // Motifs add a smaller counter-beat to the family's main silhouette. Thus all ten motifs alter height,
                // not merely the marker id: even motifs grow a lookout, odd motifs cut a hollow, and phase/symmetry move
                // that beat around the family form without introducing per-tile noise.
                double motifAngle = landmark.variant.phase * 1.7 + landmark.variant.motifIndex * 0.71;
                double motifRadius = radius * (0.2 + (landmark.variant.motifIndex % 3) * 0.045);
                double motifDistance = Math.hypot(
                    u - Math.cos(motifAngle) * motifRadius,
                    v - Math.sin(motifAngle) * motifRadius);
                if ((Js.ToInt32(landmark.variant.motifIndex) & 1) == 0)
                    peak(motifDistance, motifRadius, 3 + (landmark.variant.symmetry > 2 ? 1 : 0));
                else basin(motifDistance, motifRadius, 3);

                level = countryClamp(level, MIN_ELEVATION, MAX_ELEVATION);
            }
        }
        return level;
    }

    private static double wrappedAngleDifference(double a, double b)
    {
        double difference = (a - b) % (Math.PI * 2);
        if (difference > Math.PI) difference -= Math.PI * 2;
        if (difference < -Math.PI) difference += Math.PI * 2;
        return Math.abs(difference);
    }

    private static bool landmarkRouteContains(EndlessCountryLandmarkPlan landmark, LandmarkCoordinates coordinates)
    {
        EndlessLandmarkVariant variant = landmark.variant;
        double radius = variant.radius;
        double ringRadius = radius * variant.routeRingRatio;
        if (coordinates.radius <= 2.25 || Math.abs(coordinates.radius - ringRadius) <= 1.2) return true;
        if (
            variant.motif == EndlessLandmarkMotif.TwinRing &&
            Math.abs(coordinates.radius - radius * 0.34) <= 0.95)
            return true;

        double winding =
            variant.motif == EndlessLandmarkMotif.Spiral
                ? (coordinates.radius / radius) * 1.35
                : variant.motif == EndlessLandmarkMotif.Serpentine
                    ? Math.sin(coordinates.radius * 0.34 + variant.phase) * 0.24
                    : 0;
        for (int spoke = 0; spoke < variant.routeSpokes; spoke++)
        {
            if (
                variant.motif == EndlessLandmarkMotif.BrokenSpokes &&
                coordinates.radius > radius * 0.38 &&
                coordinates.radius < radius * 0.52 &&
                spoke % 2 == 1)
                continue;
            double direction = variant.phase + winding + ((double)spoke / variant.routeSpokes) * Math.PI * 2;
            double angularWidth = 1.4 / Math.max(3, coordinates.radius);
            if (wrappedAngleDifference(coordinates.theta, direction) <= angularWidth) return true;
        }

        if (variant.motif == EndlessLandmarkMotif.Switchback)
        {
            double centre = Math.sin(coordinates.u * 0.34 + variant.phase) * radius * 0.28;
            return Math.abs(coordinates.v - centre) <= 1.1;
        }
        if (variant.motif == EndlessLandmarkMotif.Crescent)
        {
            return coordinates.u > -radius * 0.62 && Math.abs(coordinates.radius - radius * 0.78) <= 1.05;
        }
        return false;
    }

    private static bool landmarkWallContains(
        EndlessCountryLandmarkPlan landmark,
        LandmarkCoordinates coordinates,
        double seed,
        double x,
        double y)
    {
        EndlessLandmarkVariant variant = landmark.variant;
        double radius = variant.radius;
        double u = coordinates.u;
        double v = coordinates.v;
        double r = coordinates.radius;
        double noise = countryHash(seed, SALT.landmarkRadius ^ Js.ToInt32(variant.index), x, y);
        double scaledDensity = variant.wallDensity;

        switch (variant.wallPattern)
        {
            case EndlessLandmarkWallPattern.Terrace:
                return
                    Math.abs(v - Math.sin(u * 0.22 + variant.phase) * 2.2) > radius * 0.31 &&
                    Math.abs(v - Math.sin(u * 0.22 + variant.phase) * 2.2) < radius * 0.52 &&
                    noise < scaledDensity;
            case EndlessLandmarkWallPattern.Buttress:
                return
                    (Math.abs(Math.abs(u) - radius * 0.47) < 2.2 ||
                        Math.abs(Math.abs(v) - radius * 0.44) < 1.8) &&
                    r > radius * 0.24 &&
                    noise < scaledDensity;
            case EndlessLandmarkWallPattern.Islands:
                return
                    r > radius * 0.22 &&
                    valueNoise(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(SALT.landmarkRadius)), x, y, 7) > 1 - scaledDensity * 0.42;
            case EndlessLandmarkWallPattern.Labyrinth:
            {
                double band = Math.floor((r / radius) * 8);
                double gate = Math.floor(((coordinates.theta + Math.PI + variant.phase) / (Math.PI * 2)) * 12);
                return band >= 2 && band <= 6 && band % 2 == 0 && (gate + band) % 4 != 0;
            }
            case EndlessLandmarkWallPattern.Garden:
                return
                    r > radius * 0.24 &&
                    r < radius * 0.83 &&
                    Math.sin(u * 0.48 + variant.phase) * Math.cos(v * 0.44 - variant.phase) >
                        0.58 + (1 - scaledDensity) * 0.22;
            case EndlessLandmarkWallPattern.Scar:
                return
                    Math.abs(v - Math.sin(u * 0.3 + variant.phase) * 3.4) < 2.2 &&
                    Math.abs(u) > radius * 0.2 &&
                    noise < scaledDensity;
            case EndlessLandmarkWallPattern.Ruins:
                return
                    r > radius * 0.28 &&
                    r < radius * 0.84 &&
                    (Math.abs(Math.round(u / 5) * 5 - u) < 1.45 ||
                        Math.abs(Math.round(v / 5) * 5 - v) < 1.45) &&
                    noise < scaledDensity * 0.72;
            case EndlessLandmarkWallPattern.Crown:
            {
                double tooth = Math.cos(coordinates.theta * variant.symmetry * 2 + variant.phase);
                return r > radius * 0.64 && r < radius * 0.84 && tooth > 0.08 - scaledDensity * 0.16;
            }
            case EndlessLandmarkWallPattern.Gate:
                return
                    ((Math.abs(u) > radius * 0.18 &&
                        Math.abs(u) < radius * 0.34 &&
                        Math.abs(v) < radius * 0.72) ||
                        (Math.abs(v) > radius * 0.42 &&
                            Math.abs(v) < radius * 0.57 &&
                            Math.abs(u) < radius * 0.52)) &&
                    noise < scaledDensity;
            case EndlessLandmarkWallPattern.Delta:
            {
                double branch = Math.min(
                    Math.abs(v - Math.sin(u * 0.2 + variant.phase) * 4),
                    Math.abs(v - u * 0.42),
                    Math.abs(v + u * 0.42));
                return branch > 3.8 && branch < 6.8 && r < radius * 0.88 && noise < scaledDensity;
            }
        }
        return false;
    }

    private static void paintLandmarkStructures(
        byte[] tiles,
        byte[] routeMask,
        byte[] landmarkMask,
        IReadOnlyList<EndlessCountryPlan> plans,
        int baseTx,
        int baseTy)
    {
        foreach (EndlessCountryPlan plan in plans)
        {
            foreach (EndlessCountryLandmarkPlan landmark in plan.landmarks)
            {
                double radius = landmark.variant.radius;
                if (
                    landmark.x + radius < baseTx ||
                    landmark.y + radius < baseTy ||
                    landmark.x - radius >= baseTx + ENDLESS_CHUNK_TILES ||
                    landmark.y - radius >= baseTy + ENDLESS_CHUNK_TILES)
                    continue;
                for (int ty = 0; ty < ENDLESS_CHUNK_TILES; ty++)
                {
                    for (int tx = 0; tx < ENDLESS_CHUNK_TILES; tx++)
                    {
                        int x = baseTx + tx;
                        int y = baseTy + ty;
                        LandmarkCoordinates coordinates = landmarkCoordinates(landmark, x, y);
                        if (coordinates.radius > radius) continue;
                        int index = ty * ENDLESS_CHUNK_TILES + tx;
                        landmarkMask[index] = Js.U8(landmark.variant.index + 1);
                        if (landmarkRouteContains(landmark, coordinates))
                        {
                            tiles[index] = TileType.Floor;
                            routeMask[index] = 1;
                        }
                        else if (landmarkWallContains(landmark, coordinates, plan.seed, x, y))
                        {
                            tiles[index] = TileType.Solid;
                        }
                        else if (coordinates.radius <= radius * 0.26)
                        {
                            tiles[index] = TileType.Floor;
                        }
                    }
                }
            }
        }
    }

    /// <summary>True where an island retained inside the water basin owns this tile.</summary>
    private static bool basinIslandContains(EndlessCountryPlan plan, CountryEllipse basin, double x, double y)
    {
        double strength = countryClamp01(plan.landscape.traits.islandStrength);
        if (strength < 0.18) return false;
        double count = 1 + Math.floor(strength * 3);
        double c = Math.cos(basin.angle);
        double s = Math.sin(basin.angle);
        double dx = x - basin.x;
        double dy = y - basin.y;
        double u = dx * c + dy * s;
        double v = -dx * s + dy * c;
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            double angle =
                countryHash(plan.seed, SALT.lake ^ 0x9e3779b9 ^ ordinal, plan.countryX, plan.countryY) * Math.PI * 2;
            double orbit =
                (0.12 +
                    countryHash(plan.seed, SALT.lake ^ 0x85ebca6b, plan.countryX + ordinal, plan.countryY) * 0.32) *
                Math.min(basin.rx, basin.ry);
            double centreU = Math.cos(angle) * orbit;
            double centreV = Math.sin(angle) * orbit * 0.72;
            double islandRx = Math.min(
                basin.rx * 0.24,
                2.8 + strength * 4.6 + countryHash(plan.seed, SALT.lake ^ 0xc2b2ae35, ordinal, plan.countryX) * 2);
            double islandRy = Math.min(
                basin.ry * 0.25,
                2.2 + strength * 3.4 + countryHash(plan.seed, SALT.lake ^ 0x27d4eb2f, ordinal, plan.countryY) * 1.6);
            double edge =
                (valueNoise(Js.ToUint32(Js.ToInt32(plan.seed) ^ Js.ToInt32(SALT.waterDetail) ^ ordinal), x + 31, y - 47, 7) - 0.5) * 0.28;
            double normalized =
                ((u - centreU) * (u - centreU)) / (islandRx * islandRx) +
                ((v - centreV) * (v - centreV)) / (islandRy * islandRy);
            if (normalized <= 1 + edge) return true;
        }
        return false;
    }

    private static bool flowContains(CountryFlowFeature feature, EndlessCountryPlan plan, double x, double y)
    {
        if (!feature.enabled) return false;
        if (feature.basin != null && ellipseValue(feature.basin, x, y) <= 1)
        {
            // Lake and archipelago grammars retain deterministic dry/rock bodies inside the flat water datum. They are
            // derived from the Country, not the chunk, so an island crossing a streaming seam remains one island.
            if (ReferenceEquals(feature, plan.water) && basinIslandContains(plan, feature.basin, x, y)) return false;
            return true;
        }
        double dx = x - plan.anchor.x;
        double dy = y - plan.anchor.y;
        double c = Math.cos(feature.angle);
        double s = Math.sin(feature.angle);
        double u = dx * c + dy * s;
        double v = -dx * s + dy * c;
        if (Math.abs(u) > feature.length) return false;
        double taper = countryClamp01((feature.length - Math.abs(u)) / 12);
        double centre =
            feature.offset + Math.sin(u / feature.wavelength + feature.phase) * feature.amplitude;
        double width = feature.halfWidth * (0.55 + taper * 0.45);
        if (Math.abs(v - centre) <= width) return true;
        if (!ReferenceEquals(feature, plan.chasm) || plan.landscape.traits.riftStrength < 0.38) return false;
        // Strong rift grammars fork and rejoin instead of drawing one repeated trench. The fork is gated along its
        // length, leaving coherent throats between lobes; basin+rifts and water-contact landmarks can overlap this
        // field into the requested confluences, dumbbells and branching canyon combinations.
        double forkStrength = countryClamp01((plan.landscape.traits.riftStrength - 0.38) / 0.62);
        double forkGate = Math.sin(u / 23 + feature.phase * 0.7);
        if (forkGate < -0.28 + (1 - forkStrength) * 0.42) return false;
        double forkOffset =
            Math.sin(u / (31 - forkStrength * 7) - feature.phase * 0.46) * (4 + forkStrength * 7.5);
        return Math.abs(v - centre - forkOffset) <= width * (0.42 + forkStrength * 0.34);
    }

    private static void stampDisk(
        byte[] tiles,
        byte[] mask,
        int width,
        int height,
        double x,
        double y,
        double radius)
    {
        int minX = (int)Math.max(0, Math.floor(x - radius));
        int maxX = (int)Math.min(width - 1, Math.ceil(x + radius));
        int minY = (int)Math.max(0, Math.floor(y - radius));
        int maxY = (int)Math.min(height - 1, Math.ceil(y + radius));
        double radiusSq = radius * radius;
        for (int ty = minY; ty <= maxY; ty++)
        {
            for (int tx = minX; tx <= maxX; tx++)
            {
                double dx = tx - x;
                double dy = ty - y;
                if (dx * dx + dy * dy > radiusSq) continue;
                int index = ty * width + tx;
                // This primitive is reused when the Flood spine is re-stamped over the shipping raster. Bridge and
                // Underpass are already-walkable built route structures at that point; flattening either back to Floor
                // cuts a deck in half (often exactly at a chunk seam) and leaves the renderer with a visibly short span.
                // Preserve their stronger semantic role while still marking them as part of the route.
                if (tiles[index] != TileType.Bridge && tiles[index] != TileType.Underpass)
                    tiles[index] = TileType.Floor;
                mask[index] = 1;
            }
        }
    }

    /// <summary>Paint a graded visual usage band without touching topology. The physical route keeps the full safety radius
    /// in `routeMask`; this layer retains a narrow fully compacted centre and softly worn shoulders so a corridor
    /// reads as a path through open terrain instead of turning its entire collision width into one brown plaza.</summary>
    private static void stampPrimaryRouteUsage(
        byte[] mask,
        int width,
        int height,
        double x,
        double y,
        double radius)
    {
        int minX = (int)Math.max(0, Math.floor(x - radius));
        int maxX = (int)Math.min(width - 1, Math.ceil(x + radius));
        int minY = (int)Math.max(0, Math.floor(y - radius));
        int maxY = (int)Math.min(height - 1, Math.ceil(y + radius));
        // Most of the corridor is genuinely compacted ground. Only the final irregular verge fades into meadow;
        // the previous needle-thin core made a six-to-eight-cell route look like an accidental dirt scratch.
        double innerRadius = Math.min(1.7, radius * 0.58);
        for (int ty = minY; ty <= maxY; ty++)
        {
            for (int tx = minX; tx <= maxX; tx++)
            {
                double distance = Math.hypot(tx - x, ty - y);
                if (distance > radius) continue;
                double strength = 255;
                if (distance > innerRadius)
                {
                    double t = countryClamp01((radius - distance) / (radius - innerRadius));
                    strength = Math.round(
                        PRIMARY_ROUTE_EDGE_USAGE + (255 - PRIMARY_ROUTE_EDGE_USAGE) * t * t * (3 - 2 * t));
                }
                int index = ty * width + tx;
                mask[index] = Js.U8(Math.max(mask[index], strength));
            }
        }
    }

    private static readonly int[] CHOICE_LOOP_DIRECTIONS = { -1, 1 };

    private static void carveLocalChoiceLoop(
        byte[] tiles,
        byte[] routeMask,
        double seed,
        int cx,
        int cy,
        ChunkNode node,
        int portCount,
        int generationVersion)
    {
        double chance =
            generationVersion >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION
                ? portCount <= 1
                    ? 0.9
                    : portCount == 2
                        ? 0.74
                        : 0.6
                : 0.68;
        if (countryHash(seed, SALT.choiceLoop, cx, cy) >= chance) return;
        double boundaryClearance = Math.min(
            node.tx,
            node.ty,
            ENDLESS_CHUNK_TILES - 1 - node.tx,
            ENDLESS_CHUNK_TILES - 1 - node.ty);
        if (boundaryClearance < 11) return;
        double radius = Math.min(
            12.5,
            boundaryClearance - 2.4,
            9.6 + countryHash(seed, SALT.choiceLoop ^ 0x9e3779b9, cx, cy) * 2.2);
        double squash = 0.76 + countryHash(seed, SALT.choiceLoop ^ 0x85ebca6b, cx, cy) * 0.18;
        double angle = countryHash(seed, SALT.choiceLoop ^ 0xc2b2ae35, cx, cy) * Math.PI;
        double c = Math.cos(angle);
        double s = Math.sin(angle);
        const int steps = 72;
        for (int step = 0; step < steps; step++)
        {
            double theta = ((double)step / steps) * Math.PI * 2;
            double u = Math.cos(theta) * radius;
            double v = Math.sin(theta) * radius * squash;
            stampDisk(
                tiles,
                routeMask,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                node.tx + u * c - v * s,
                node.ty + u * s + v * c,
                1.55);
        }

        // Two opposed links make the ring a genuine alternative route around the court rather than an isolated
        // decorative loop. Their narrowness deliberately retains a wall belt and therefore the maze read.
        foreach (int direction in CHOICE_LOOP_DIRECTIONS)
        {
            for (double distance = COURT_RADIUS - 0.5; distance <= radius; distance += 0.55)
            {
                stampDisk(
                    tiles,
                    routeMask,
                    ENDLESS_CHUNK_TILES,
                    ENDLESS_CHUNK_TILES,
                    node.tx + c * distance * direction,
                    node.ty + s * distance * direction,
                    1.55);
            }
        }
    }

    private static void carveRoute(
        byte[] tiles,
        byte[] routeMask,
        int width,
        int height,
        double seed,
        int cx,
        int cy,
        int ordinal,
        Point from,
        Point to,
        // The setting's authored `pathWander`: 0 drives a near-straight causeway, 1 a wandering trail.
        double wander)
    {
        double dx = to.x - from.x;
        double dy = to.y - from.y;
        double distance = Math.max(1, Math.hypot(dx, dy));
        // A ruined causeway should run dead straight and an alpine trail should meander. `pathWander` says which
        // for all eighteen settings and used to reach nothing, so every approach in the world bent by the same
        // fixed amount — one of the quieter reasons runs read alike even where the landform changed.
        double wanderScale = 0.45 + countryClamp01(wander) * 1.35;
        double bend =
            (countryHash(seed, SALT.routeBend ^ ordinal, cx, cy) - 0.5) *
            Math.min(8, distance * 0.28) *
            wanderScale;
        double nx = -dy / distance;
        double ny = dx / distance;
        var control = new Point((from.x + to.x) * 0.5 + nx * bend, (from.y + to.y) * 0.5 + ny * bend);
        double steps = Math.max(8, Math.ceil(distance * 1.8));
        for (int step = 0; step <= steps; step++)
        {
            double t = step / steps;
            double inv = 1 - t;
            stampDisk(
                tiles,
                routeMask,
                width,
                height,
                inv * inv * from.x + 2 * inv * t * control.x + t * t * to.x,
                inv * inv * from.y + 2 * inv * t * control.y + t * t * to.y,
                ROUTE_RADIUS);
        }
    }

    private sealed class LandmarkApproach
    {
        public Point point = null!;
        public int distanceSq;
    }

    private static ChunkNode carveRouteNetwork(
        byte[] tiles,
        byte[] routeMask,
        byte[] courtMask,
        byte[] landmarkMask,
        double seed,
        int cx,
        int cy,
        IReadOnlyList<EndlessCountryChunkPort> ports,
        int generationVersion,
        double wander)
    {
        ChunkNode node = chunkNodeAt(seed, cx, cy);
        stampDisk(
            tiles,
            courtMask,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES,
            node.tx,
            node.ty,
            COURT_RADIUS);
        stampDisk(
            tiles,
            routeMask,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES,
            node.tx,
            node.ty,
            ROUTE_RADIUS);
        carveLocalChoiceLoop(tiles, routeMask, seed, cx, cy, node, ports.Count, generationVersion);
        for (int index = 0; index < ports.Count; index++)
        {
            EndlessCountryChunkPort port = ports[index];
            carveRoute(
                tiles,
                routeMask,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                seed,
                cx,
                cy,
                index,
                new Point(node.tx, node.ty),
                new Point(port.tx, port.ty),
                wander);
        }

        // Filled shelves used to connect landmark rings accidentally. The maze grammar removes that accidental
        // guarantee, so every clipped set-piece now receives an explicit narrow approach from the chunk junction.
        // Grouping by stable variant byte avoids stamping several connectors into the same landmark footprint.
        var landmarkApproaches = new JsMap<int, LandmarkApproach>();
        for (int ty = 0; ty < ENDLESS_CHUNK_TILES; ty++)
        {
            for (int tx = 0; tx < ENDLESS_CHUNK_TILES; tx++)
            {
                int index = ty * ENDLESS_CHUNK_TILES + tx;
                int landmark = landmarkMask[index];
                if (landmark == 0 || routeMask[index] == 0 || !isWalkable(tiles[index])) continue;
                int dx = tx - node.tx;
                int dy = ty - node.ty;
                int distanceSq = dx * dx + dy * dy;
                LandmarkApproach? previous = landmarkApproaches.get(landmark);
                if (previous != null && previous.distanceSq <= distanceSq) continue;
                landmarkApproaches.set(landmark, new LandmarkApproach { point = new Point(tx, ty), distanceSq = distanceSq });
            }
        }
        int landmarkOrdinal = 0;
        foreach (LandmarkApproach approach in landmarkApproaches.values())
        {
            carveRoute(
                tiles,
                routeMask,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                seed,
                cx,
                cy,
                0x100 + landmarkOrdinal++,
                new Point(node.tx, node.ty),
                approach.point,
                wander);
        }
        return node;
    }

    /// <summary>
    /// Carve the authoritative Flood spine into the terrain. The Flood, spawn-ahead logic and client waterline all
    /// derive this exact curve from the instance id; making it a first-class route means the visually sparse
    /// Country graph can never strand the cohort on the wrong side of the advancing front. Sampling and stamping
    /// happen in global tile space, so adjacent chunks clip the same corridor and agree without a seam repair.
    /// </summary>
    private static void carveFloodSpineRoute(
        byte[] tiles,
        byte[] routeMask,
        byte[] primaryRouteMask,
        double seed,
        double spineSeed,
        int cx,
        int cy,
        ChunkNode node)
    {
        double originX = endlessChunkOriginX(cx);
        double originY = endlessChunkOriginY(cy);
        double chunkWorld = ENDLESS_CHUNK_TILES * Grid.TILE_SIZE;
        double margin = (FLOOD_SPINE_ROUTE_RADIUS + 1) * Grid.TILE_SIZE;
        double minX = originX - margin;
        double maxX = originX + chunkWorld + margin;
        double minY = originY - margin;
        double maxY = originY + chunkWorld + margin;
        if (maxX < 0) return;

        Spine spine = terrainSpineFor(spineSeed);
        double minForward = Math.cos(SpineModule.SPINE_MAX_DEVIATION);
        double maxS = Math.max(0, maxX / minForward + Grid.TILE_SIZE * 4);
        spine.ensureLength(maxS);

        // X is strictly monotone along Spine. Locate this chunk's first relevant sample without replaying the whole
        // path for deep streamed chunks, then take sub-tile samples until the curve has cleared its east edge.
        double firstS = spineArcAtX(spine, minX, maxS);

        double sampleStep = Grid.TILE_SIZE * 0.55;
        int baseTx = cx * ENDLESS_CHUNK_TILES;
        int baseTy = cy * ENDLESS_CHUNK_TILES;
        Point? closest = null;
        double closestDistanceSq = double.PositiveInfinity;
        for (double s = Math.max(0, firstS - sampleStep * 2); s <= maxS; s += sampleStep)
        {
            var point = spine.pointAt(s);
            if (point.x > maxX) break;
            if (point.x < minX || point.y < minY || point.y > maxY) continue;
            double localX = point.x / Grid.TILE_SIZE + (double)ENDLESS_CHUNK_TILES / 2 - 0.5 - baseTx;
            double localY = point.y / Grid.TILE_SIZE + (double)ENDLESS_CHUNK_TILES / 2 - 0.5 - baseTy;
            stampDisk(
                tiles,
                routeMask,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                localX,
                localY,
                FLOOD_SPINE_ROUTE_RADIUS);
            // Preserve the world-spanning artery independently from courts, local loops and approach lanes. The
            // renderer needs this hierarchy to keep one unbroken, immediately legible trail through the whole map;
            // sharing the binary route mask made every nearby plaza compete with the actual navigation line.
            stampPrimaryRouteUsage(
                primaryRouteMask,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                localX,
                localY,
                FLOOD_SPINE_VISUAL_ROUTE_RADIUS);
            // A diagonal curve can merely graze a chunk corner between two samples. Its disk still paints cells in
            // that chunk, so connect the clipped point as well; otherwise the local orphan pass would correctly (but
            // undesirably) remove this tiny piece and leave a one-cell hole on the authoritative escape route.
            double clippedX = countryClamp(localX, 0, ENDLESS_CHUNK_TILES - 1);
            double clippedY = countryClamp(localY, 0, ENDLESS_CHUNK_TILES - 1);
            double dx = clippedX - node.tx;
            double dy = clippedY - node.ty;
            double distanceSq = dx * dx + dy * dy;
            if (distanceSq < closestDistanceSq)
            {
                closestDistanceSq = distanceSq;
                closest = new Point(clippedX, clippedY);
            }
        }

        // A Country court touching the escape artery becomes a readable junction, never an isolated parallel lane.
        if (closest != null)
            carveRoute(
                tiles,
                routeMask,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                seed,
                cx,
                cy,
                0x51,
                new Point(node.tx, node.ty),
                closest,
                // The escape artery's junction stub is deliberately calm whatever the setting does elsewhere.
                0.35);
    }

    private static void paintLargeShelves(
        byte[] tiles,
        IReadOnlyList<EndlessCountryPlan> plans,
        int baseTx,
        int baseTy)
    {
        for (int ty = 0; ty < ENDLESS_CHUNK_TILES; ty++)
        {
            for (int tx = 0; tx < ENDLESS_CHUNK_TILES; tx++)
            {
                int x = baseTx + tx;
                int y = baseTy + ty;
                // Neighbouring Country silhouettes may overlap. Only the closest shelf grammar owns the cell; taking
                // their union would let one Country fill the deliberate wall ribs of another at every boundary.
                EndlessCountryPlan? owner = shelfOwnerAt(plans, x, y);
                if (owner != null && !countryMazeWallAt(owner, x, y))
                    tiles[ty * ENDLESS_CHUNK_TILES + tx] = TileType.Floor;
            }
        }
    }

    private static void paintSpawnCourt(
        byte[] tiles,
        byte[] routeMask,
        byte[] courtMask,
        int baseTx,
        int baseTy)
    {
        for (int ty = 0; ty < ENDLESS_CHUNK_TILES; ty++)
        {
            for (int tx = 0; tx < ENDLESS_CHUNK_TILES; tx++)
            {
                double dx = baseTx + tx - 15.5;
                double dy = baseTy + ty - 15.5;
                double distance = Math.hypot(dx, dy);
                if (distance > 20) continue;
                int index = ty * ENDLESS_CHUNK_TILES + tx;
                bool preservedRoute = routeMask[index] != 0;
                double angle = Math.atan2(dy, dx);
                bool sanctuary = distance <= 8.5;
                bool spoke = Math.abs(Math.sin(angle * 3 + 0.42)) * distance <= 1.75;
                bool innerRing = Math.abs(distance - 12) <= 1.45;
                bool brokenOuterRing = Math.abs(distance - 17) <= 1.25 && Math.cos(angle * 5 - 0.28) > -0.38;
                // The opening remains hazard-free and offers six exits plus two ring choices, but only the compact
                // central sanctuary is a calm court. This removes the former almost-full 32x32 floor disk that made the
                // very first view contradict the maze language used by the rest of the run.
                if (preservedRoute || sanctuary || spoke || innerRing || brokenOuterRing)
                {
                    tiles[index] = TileType.Floor;
                    routeMask[index] = 1;
                    courtMask[index] = (byte)(sanctuary ? 1 : 0);
                }
                else
                {
                    tiles[index] = TileType.Solid;
                    routeMask[index] = 0;
                    courtMask[index] = 0;
                }
            }
        }
    }

    // TS `interface LandmarkHydrologySample` and `interface WallMassHydrologySample` — both `{ water; chasm }`,
    // immutable, so value tuples.

    /// <summary>
    /// Break the interior of broad wall masses with deterministic springs, plunge pools and fault slots.
    ///
    /// These features live in Country/maze coordinates rather than chunk coordinates, so a reservoir or fissure
    /// crossing a streamed seam is sampled identically by both chunks. The ordinary Country watercourse still owns
    /// the large landscape river; this smaller 24-tile lattice exists specifically to stop Solid caps from reading
    /// as empty rectangular roofs between maze aisles.
    /// </summary>
    private static (bool water, bool chasm) wallMassHydrologyAt(EndlessCountryPlan plan, double x, double y)
    {
        double dx = x - plan.anchor.x;
        double dy = y - plan.anchor.y;
        double mazeCos = Math.cos(plan.maze.angle);
        double mazeSin = Math.sin(plan.maze.angle);
        double u = dx * mazeCos + dy * mazeSin;
        double v = -dx * mazeSin + dy * mazeCos;
        const double cellSize = 24;
        double cellX = Math.round(u / cellSize);
        double cellY = Math.round(v / cellSize);
        bool water = false;
        bool chasm = false;
        double keyedX = plan.countryX * 193 + cellX;
        double keyedY = plan.countryY * 193 + cellY;
        double roll = countryHash(plan.seed, SALT.wallFeature, keyedX, keyedY);
        double centreU =
            cellX * cellSize + (countryHash(plan.seed, SALT.wallFeatureDetail, keyedX, keyedY) - 0.5) * 2;
        double centreV =
            cellY * cellSize +
            (countryHash(plan.seed, SALT.wallFeatureDetail ^ 0x9e3779b9, keyedX, keyedY) - 0.5) * 2;
        double angle = countryHash(plan.seed, SALT.wallFeatureDetail ^ 0x85ebca6b, keyedX, keyedY) * Math.PI;
        double c = Math.cos(angle);
        double s = Math.sin(angle);
        double du = u - centreU;
        double dv = v - centreV;
        double a = du * c + dv * s;
        double b = -du * s + dv * c;
        double shape = countryHash(plan.seed, SALT.wallFeature ^ 0xc2b2ae35, keyedX, keyedY);

        if (roll < 0.44)
        {
            // A high wall reservoir loses one side into a split chasm outlet. Water/Chasm contact is intentional:
            // the shared depth pass turns this exact topology into the broad waterfall fronts seen in the Hub.
            double rx = 5.4 + shape * 2.1;
            double ry = 4.2 + (1 - shape) * 2.4;
            bool pool = (a * a) / (rx * rx) + (b * b) / (ry * ry) <= 1;
            if (pool) water = true;
            double outletCentre = Math.sin((a - rx * 0.45) * 0.31 + plan.maze.phase) * 1.25;
            bool outlet = a > rx * 0.5 && a < rx + 5 && Math.abs(b - outletCentre) < 1.75 + shape * 0.8;
            bool splitOutlet =
                a > rx + 1.5 &&
                a < rx + 4.6 &&
                Math.abs(Math.abs(b - outletCentre) - (a - rx - 1) * 0.25) < 1.25;
            if (outlet || splitOutlet) chasm = true;
        }
        else if (roll < 0.9)
        {
            // Long but bounded cracks interrupt roof-like caps without becoming another Country-wide chasm stripe.
            double halfLength = 8 + shape * 4;
            bool fault =
                Math.abs(a) < halfLength &&
                Math.abs(b - Math.sin(a * 0.27 + plan.maze.phase) * (1.2 + shape * 1.2)) <
                    1.35 + shape * 0.85;
            bool pocket = Math.hypot((a + halfLength * 0.58) / 1.25, b) < 2.4 + shape * 1.5;
            if (fault || pocket) chasm = true;
        }
        else
        {
            // Rare enclosed springs add a quiet reflective beat without every water feature demanding a ravine.
            double rx = 4.4 + shape * 2.1;
            double ry = 3.4 + (1 - shape) * 1.8;
            if ((a * a) / (rx * rx) + (b * b) / (ry * ry) <= 1) water = true;
        }

        // Relief veins — the second beat inside the coarse lattice's gaps.
        //
        // A 24-tile feature pitch necessarily leaves rock a dozen cells from ANY water or void, and that is exactly
        // the undifferentiated grey slab the Hub reference never shows. This finer lattice is offset by half a cell
        // so it lands in those gaps, and it fires only where the coarse pass found nothing. It is traversal-neutral
        // by construction: the caller applies it to Solid cells only, so impassable rock merely becomes impassable
        // void or an enclosed tarn — the silhouette changes, the route graph cannot.
        // The finer relief beat is part of V5. Historical V4 chunks retain their exact coarse hydrology.
        if (plan.version >= ENDLESS_DISTRICT_GENERATION_VERSION && !water && !chasm)
        {
            const double veinSize = 13;
            double veinX = Math.round((u + veinSize * 0.5) / veinSize);
            double veinY = Math.round((v + veinSize * 0.5) / veinSize);
            double veinKeyX = plan.countryX * 271 + veinX;
            double veinKeyY = plan.countryY * 271 + veinY;
            EndlessLandscapeTraits traits = plan.landscape.traits;
            double veinRate = countryClamp(
                0.46 +
                    traits.chasmStrength * 0.34 +
                    traits.riftStrength * 0.26 +
                    traits.reliefRuggedness * 0.16,
                0.38,
                0.9);
            double veinRoll = countryHash(plan.seed, SALT.wallFeature ^ 0x5bd1e995, veinKeyX, veinKeyY);
            if (veinRoll < veinRate)
            {
                double veinShape = countryHash(plan.seed, SALT.wallFeatureDetail ^ 0x27d4eb2f, veinKeyX, veinKeyY);
                double veinAngle =
                    countryHash(plan.seed, SALT.wallFeatureDetail ^ 0x165667b1, veinKeyX, veinKeyY) * Math.PI;
                double veinCentreU =
                    veinX * veinSize -
                    veinSize * 0.5 +
                    (countryHash(plan.seed, SALT.wallFeature ^ 0x9e3779b9, veinKeyX, veinKeyY) - 0.5) * 3;
                double veinCentreV =
                    veinY * veinSize -
                    veinSize * 0.5 +
                    (countryHash(plan.seed, SALT.wallFeature ^ 0x85ebca6b, veinKeyX, veinKeyY) - 0.5) * 3;
                double veinCos = Math.cos(veinAngle);
                double veinSin = Math.sin(veinAngle);
                double localU = u - veinCentreU;
                double localV = v - veinCentreV;
                double veinA = localU * veinCos + localV * veinSin;
                double veinB = -localU * veinSin + localV * veinCos;
                // Two forms, so a dry district never reads as one stamp repeated on a grid. Both are sized to CLEAR the
                // shared terrain contracts rather than to be quietly deleted by them: a ravine needs at least
                // TERRAIN_CHASM_MIN_COMPONENT_CELLS cells AND a 40% four-neighbour core, which a band narrower
                // than four cells can never reach, and an enclosed tarn needs
                // TERRAIN_WATER_MIN_COMPONENT_CELLS. Undersized veins would be reverted by the topology pass and
                // the rock would stay blank.
                if (veinRoll < veinRate * 0.74)
                {
                    double halfLength = 5 + veinShape * 3.5;
                    double halfWidth = 2.05 + veinShape * 0.7;
                    double spine = Math.sin(veinA * 0.42 + plan.maze.phase) * (0.8 + veinShape);
                    if (Math.abs(veinA) < halfLength && Math.abs(veinB - spine) < halfWidth) chasm = true;
                }
                else
                {
                    double rx = 3.3 + veinShape * 1.7;
                    double ry = 2.7 + (1 - veinShape) * 1.4;
                    if ((veinA * veinA) / (rx * rx) + (veinB * veinB) / (ry * ry) <= 1) water = true;
                }
            }
        }

        return (water, chasm);
    }

    private static bool wallCellHasSolidRing(byte[] tiles, int tx, int ty)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx;
                int ny = ty + dy;
                // The adjacent streamed chunk independently samples the same world feature; do not clip a reservoir just
                // because this immutable local array ends at the seam.
                if (nx < 0 || ny < 0 || nx >= ENDLESS_CHUNK_TILES || ny >= ENDLESS_CHUNK_TILES) continue;
                if (tiles[ny * ENDLESS_CHUNK_TILES + nx] != TileType.Solid) return false;
            }
        }
        return true;
    }

    private static (bool water, bool chasm) landmarkHydrologyAt(
        IReadOnlyList<EndlessCountryPlan> plans,
        double x,
        double y)
    {
        bool water = false;
        bool chasm = false;
        foreach (EndlessCountryPlan plan in plans)
        {
            foreach (EndlessCountryLandmarkPlan landmark in plan.landmarks)
            {
                EndlessLandmarkVariant variant = landmark.variant;
                if (variant.hazard == EndlessLandmarkHazard.None) continue;
                if (Math.abs(x - landmark.x) > variant.radius || Math.abs(y - landmark.y) > variant.radius)
                    continue;
                LandmarkCoordinates coordinates = landmarkCoordinates(landmark, x, y);
                if (coordinates.radius > variant.radius * 0.9) continue;
                double u = coordinates.u;
                double v = coordinates.v;
                double r = coordinates.radius;
                double wave = Math.sin(u * (0.2 + variant.motifIndex * 0.006) + variant.phase);

                if (variant.hazard == EndlessLandmarkHazard.Water)
                {
                    if (variant.wallPattern == EndlessLandmarkWallPattern.Terrace)
                    {
                        water =
                            water ||
                            Math.abs(v - wave * 2.4) <= variant.waterWidth ||
                            (Math.abs(u) > variant.radius * 0.42 &&
                                Math.abs(u) < variant.radius * 0.68 &&
                                Math.abs(v) < variant.waterWidth * 1.45);
                    }
                    else
                    {
                        water =
                            water ||
                            Math.abs(r - variant.radius * 0.38) <= variant.waterWidth * 0.58 ||
                            (Math.abs(v - wave * 1.8) <= variant.waterWidth * 0.62 &&
                                Math.abs(u) < variant.radius * 0.72);
                    }
                    continue;
                }

                if (variant.hazard == EndlessLandmarkHazard.Chasm)
                {
                    if (variant.wallPattern == EndlessLandmarkWallPattern.Crown)
                    {
                        chasm =
                            chasm ||
                            (r > variant.radius * 0.3 &&
                                r < variant.radius * 0.3 + variant.chasmWidth &&
                                Math.cos(coordinates.theta * variant.symmetry + variant.phase) > -0.72);
                    }
                    else
                    {
                        chasm =
                            chasm ||
                            Math.abs(
                                v - wave * (variant.wallPattern == EndlessLandmarkWallPattern.Scar ? 3.2 : 1.7)) <= variant.chasmWidth;
                    }
                    continue;
                }

                // Confluences intentionally terminate a water ribbon in a split chasm delta. The overlap around u=0
                // gives the depth pass a broad waterfall front; route cells remain protected as several dry/bridged
                // choices through the spectacle.
                double split = Math.abs(v - Math.sign(Js.Truthy(u) ? u : 1) * Math.abs(u) * 0.3 - wave * 1.4);
                water = water || (u <= 2.5 && Math.abs(v - wave * 1.6) <= variant.waterWidth);
                chasm = chasm || (u >= -1.5 && split <= variant.chasmWidth);
            }
        }
        return (water, chasm);
    }

    private static void paintHydrology(
        byte[] tiles,
        byte[] routeMask,
        byte[] courtMask,
        byte[] landmarkMask,
        IReadOnlyList<EndlessCountryPlan> plans,
        int baseTx,
        int baseTy)
    {
        // Chunk (0,0) is the one authored onboarding clearing. Its outer maze ribs add silhouette, but must not
        // reopen hydrology in the protected first view; neighbouring chunks already introduce every hazard family.
        if (baseTx == 0 && baseTy == 0) return;
        var landmarkWaterMask = new byte[tiles.Length];
        var landmarkChasmMask = new byte[tiles.Length];
        var wallWaterMask = new byte[tiles.Length];
        var wallChasmMask = new byte[tiles.Length];
        var mountainStreamMask = new byte[tiles.Length];
        EndlessCountryPlan? wallOwner = countryPlanAtTile(
            plans,
            baseTx + ENDLESS_CHUNK_TILES * 0.5,
            baseTy + ENDLESS_CHUNK_TILES * 0.5);
        for (int ty = 0; ty < ENDLESS_CHUNK_TILES; ty++)
        {
            for (int tx = 0; tx < ENDLESS_CHUNK_TILES; tx++)
            {
                int index = ty * ENDLESS_CHUNK_TILES + tx;
                int x = baseTx + tx;
                int y = baseTy + ty;
                var sample = landmarkHydrologyAt(plans, x, y);
                if (sample.water) landmarkWaterMask[index] = 1;
                if (sample.chasm) landmarkChasmMask[index] = 1;
                EndlessCountryMountainStreamSample? mountainStream = mountainStreamSampleForPlans(plans, x, y);
                if (mountainStream != null) mountainStreamMask[index] = 1;
                if (tiles[index] != TileType.Solid) continue;
                if (wallOwner == null) continue;
                var wallSample = wallMassHydrologyAt(wallOwner, x, y);
                if (wallSample.water && wallCellHasSolidRing(tiles, tx, ty)) wallWaterMask[index] = 1;
                if (wallSample.chasm) wallChasmMask[index] = 1;
            }
        }
        double planSeed = plans.Count > 0 ? plans[0].seed : 0;
        bool landmarkDeck =
            countryHash(
                planSeed,
                SALT.waterDetail,
                Math.floor((double)baseTx / ENDLESS_CHUNK_TILES),
                Math.floor((double)baseTy / ENDLESS_CHUNK_TILES)) < 0.16;
        bool landmarkBridgeDeck =
            landmarkDeck &&
            countryHash(
                planSeed,
                SALT.landmarkVariant,
                Math.floor((double)baseTx / ENDLESS_CHUNK_TILES),
                Math.floor((double)baseTy / ENDLESS_CHUNK_TILES)) < 0.4;
        byte[] beforeWater = tiles.slice();
        for (int ty = 0; ty < ENDLESS_CHUNK_TILES; ty++)
        {
            for (int tx = 0; tx < ENDLESS_CHUNK_TILES; tx++)
            {
                int index = ty * ENDLESS_CHUNK_TILES + tx;
                int x = baseTx + tx;
                int y = baseTy + ty;
                // An ordinary lake never consumes a gameplay court. A planned mountain stream instead passes beneath
                // its dry deck: the tile becomes Bridge below, preserving both the route and the continuous watercourse.
                if (courtMask[index] != 0 && mountainStreamMask[index] == 0) continue;
                if (
                    landmarkWaterMask[index] == 0 &&
                    wallWaterMask[index] == 0 &&
                    mountainStreamMask[index] == 0 &&
                    !plans.some((plan) => flowContains(plan.water, plan, x, y)))
                    continue;
                tiles[index] = (byte)(routeMask[index] != 0
                    ? mountainStreamMask[index] != 0
                        ? TileType.Bridge
                        : landmarkMask[index] != 0
                            ? landmarkBridgeDeck
                                ? TileType.Bridge
                                : TileType.Floor
                            : landmarkDeck
                                ? TileType.Bridge
                                : TileType.Floor
                    : TileType.Water);
            }
        }
        TerrainWater.enforceTerrainWaterTopology(tiles, beforeWater, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
        TerrainBridge.enforceMinimumBridgeThickness(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new BridgeThicknessOptions { maxPasses = 12 });
        // A bridge is published only when this chunk owns its complete two-bank span. Hydrology and bridge styling
        // are intentionally local choices, so a deck that reaches the raster edge is an unprovable guess: restore
        // its pre-water route cells and let both neighbouring chunks meet on ordinary traversable ground.
        TerrainBridge.restoreUnanchoredBoundaryBridgeComponents(
            tiles,
            beforeWater,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES);
        TerrainBridge.demoteStrayBridgeComponents(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);

        byte[] beforeChasm = tiles.slice();
        for (int ty = 0; ty < ENDLESS_CHUNK_TILES; ty++)
        {
            for (int tx = 0; tx < ENDLESS_CHUNK_TILES; tx++)
            {
                int index = ty * ENDLESS_CHUNK_TILES + tx;
                int x = baseTx + tx;
                int y = baseTy + ty;
                if (
                    routeMask[index] != 0 ||
                    courtMask[index] != 0 ||
                    (tiles[index] != TileType.Floor &&
                        tiles[index] != TileType.Water &&
                        tiles[index] != TileType.Solid))
                    continue;
                if (
                    landmarkChasmMask[index] != 0 ||
                    wallChasmMask[index] != 0 ||
                    plans.some((plan) => flowContains(plan.chasm, plan, x, y)))
                    tiles[index] = TileType.Chasm;
            }
        }
        TerrainChasm.enforceTerrainChasmTopology(tiles, beforeChasm, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
        TerrainWater.enforceTerrainWaterTopology(tiles, beforeWater, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
        TerrainBridge.demoteStrayBridgeComponents(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
        // Demoting the last redundant deck can split a previously valid Water+Bridge body. V4 closes that final
        // topology gap after demotion; older explicit generations retain their exact historical raster output.
        if (plans.some((plan) => plan.version >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION))
            TerrainWater.enforceTerrainWaterTopology(tiles, beforeWater, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
    }

    private sealed class CountryRoomPlan
    {
        /// <summary>A <see cref="DungeonRoomType"/> value.</summary>
        public int type;
        public TileRect rect = null!;
    }

    private static CountryRoomPlan? roomPlanForChunk(double seed, int cx, int cy, ChunkNode node, bool boss)
    {
        if (cx == 0 && cy == 0) return null;
        bool combat = boss || countryHash(seed, SALT.room, cx, cy) < 0.31;
        if (!combat) return null;
        int tw = boss ? 17 : 9;
        int th = tw;
        if (!boss)
        {
            double shape = countryHash(seed, SALT.roomShape, cx, cy);
            if (shape < 0.14) tw = th = 13;
            else if (shape < 0.46)
            {
                bool horizontal = countryHash(seed, SALT.roomShape ^ 0x9e3779b9, cx, cy) < 0.5;
                tw = horizontal ? 15 : 7;
                th = horizontal ? 7 : 15;
            }
        }
        return new CountryRoomPlan
        {
            type = boss ? DungeonRoomType.Boss : DungeonRoomType.Combat,
            rect = new TileRect(
                (int)countryClamp(node.tx - Math.floor(tw / 2.0), 1, ENDLESS_CHUNK_TILES - tw - 1),
                (int)countryClamp(node.ty - Math.floor(th / 2.0), 1, ENDLESS_CHUNK_TILES - th - 1),
                tw,
                th),
        };
    }

    private static void paintRoomPlan(byte[] tiles, byte[] courtMask, CountryRoomPlan? plan)
    {
        if (plan == null) return;
        for (int ty = plan.rect.ty; ty < plan.rect.ty + plan.rect.th; ty++)
        {
            for (int tx = plan.rect.tx; tx < plan.rect.tx + plan.rect.tw; tx++)
            {
                int index = ty * ENDLESS_CHUNK_TILES + tx;
                tiles[index] = TileType.Floor;
                courtMask[index] = 1;
            }
        }
    }

    private static List<DungeonRoom> roomForChunk(
        double seed,
        int cx,
        int cy,
        ChunkNode node,
        double originX,
        double originY,
        CountryRoomPlan? plan)
    {
        if (plan == null) return new List<DungeonRoom>();
        bool boss = plan.type == DungeonRoomType.Boss;
        var rng = new Rng($"{Js.Str(seed)}:country-room:{Js.Str(cx)}:{Js.Str(cy)}");
        // Rooms are no longer tagged with an encounter — that was run gameplay (which monsters a room runs when
        // the leading edge reaches it), and the run system is gone. The field stays on `DungeonRoom` so a game
        // built on this foundation can populate rooms with its own idea of content.
        //
        // The roll's DRAWS are kept deliberately: see `consumeRetiredEncounterDraws`.
        double? encounter = boss ? null : consumeRetiredEncounterDraws(rng);
        var room = new DungeonRoom
        {
            id = 0,
            type = plan.type,
            rect = plan.rect,
            cx = originX + (node.tx + 0.5) * Grid.TILE_SIZE,
            cy = originY + (node.ty + 0.5) * Grid.TILE_SIZE,
            doorIds = new List<int>(),
            threat = boss ? 1 : 0.58,
        };
        if (encounter is double value && Js.Truthy(value)) room.encounter = value;
        return new List<DungeonRoom> { room };
    }

    /// <summary>Author one small rock rib whose centre becomes a projectile-permeable Cleft after height assignment.</summary>
    private sealed class CountryCleftSocket
    {
        public int index;
        /// <summary>'horizontal' | 'vertical'.</summary>
        public string passageAxis = "";
    }

    private static CountryCleftSocket? paintCountryCleftSocket(
        byte[] tiles,
        byte[] routeMask,
        byte[] courtMask,
        double seed,
        int cx,
        int cy)
    {
        if ((cx == 0 && cy == 0) || countryHash(seed, SALT.depthSocket, cx, cy) > 0.58) return null;
        bool horizontal = countryHash(seed, SALT.depthSocket ^ 0x9e3779b9, cx, cy) < 0.5;
        int bestIndex = -1;
        double bestRank = -1;
        for (int ty = 4; ty < ENDLESS_CHUNK_TILES - 4; ty++)
        {
            for (int tx = 4; tx < ENDLESS_CHUNK_TILES - 4; tx++)
            {
                bool clear = true;
                for (int dy = -2; dy <= 2 && clear; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int index = (ty + dy) * ENDLESS_CHUNK_TILES + tx + dx;
                        if (tiles[index] != TileType.Floor || routeMask[index] != 0 || courtMask[index] != 0)
                        {
                            clear = false;
                            break;
                        }
                    }
                }
                if (!clear) continue;
                double rank = countryHash(seed, SALT.depthSocket ^ 0x85ebca6b, cx * 37 + tx, cy * 37 + ty);
                if (rank <= bestRank) continue;
                bestRank = rank;
                bestIndex = ty * ENDLESS_CHUNK_TILES + tx;
            }
        }
        if (bestIndex < 0) return null;
        int sx = bestIndex % ENDLESS_CHUNK_TILES;
        int sy = (int)Math.floor((double)bestIndex / ENDLESS_CHUNK_TILES);
        int tangentX = horizontal ? 0 : 1;
        int tangentY = horizontal ? 1 : 0;
        tiles[bestIndex] = TileType.Solid;
        tiles[(sy - tangentY) * ENDLESS_CHUNK_TILES + sx - tangentX] = TileType.Solid;
        tiles[(sy + tangentY) * ENDLESS_CHUNK_TILES + sx + tangentX] = TileType.Solid;
        return new CountryCleftSocket { index = bestIndex, passageAxis = horizontal ? "horizontal" : "vertical" };
    }

    private static void prepareCountryCleftSocket(sbyte[] elevation, CountryCleftSocket? socket)
    {
        if (socket == null) return;
        int tx = socket.index % ENDLESS_CHUNK_TILES;
        int ty = (int)Math.floor((double)socket.index / ENDLESS_CHUNK_TILES);
        int alongX = socket.passageAxis == "horizontal" ? 1 : 0;
        int alongY = socket.passageAxis == "horizontal" ? 0 : 1;
        int tangentX = socket.passageAxis == "horizontal" ? 0 : 1;
        int tangentY = socket.passageAxis == "horizontal" ? 1 : 0;
        int negativeApproach = (ty - alongY) * ENDLESS_CHUNK_TILES + tx - alongX;
        int positiveApproach = (ty + alongY) * ENDLESS_CHUNK_TILES + tx + alongX;
        double ground = Math.round((elevation[negativeApproach] + elevation[positiveApproach]) * 0.5);
        elevation[negativeApproach] = Js.I8(ground);
        elevation[positiveApproach] = Js.I8(ground);
        foreach (int index in new[]
        {
            socket.index,
            (ty - tangentY) * ENDLESS_CHUNK_TILES + tx - tangentX,
            (ty + tangentY) * ENDLESS_CHUNK_TILES + tx + tangentX,
        })
            elevation[index] = Js.I8(Math.max(elevation[index], Math.min(MAX_ELEVATION, ground + 1)));
    }

    private static void materializeCountryCleftSocket(byte[] tiles, sbyte[] elevation, CountryCleftSocket? socket)
    {
        if (socket == null || tiles[socket.index] != TileType.Solid) return;
        int tx = socket.index % ENDLESS_CHUNK_TILES;
        int ty = (int)Math.floor((double)socket.index / ENDLESS_CHUNK_TILES);
        int alongX = socket.passageAxis == "horizontal" ? 1 : 0;
        int alongY = socket.passageAxis == "horizontal" ? 0 : 1;
        int tangentX = socket.passageAxis == "horizontal" ? 0 : 1;
        int tangentY = socket.passageAxis == "horizontal" ? 1 : 0;
        var approaches = new List<int>
        {
            (ty - alongY) * ENDLESS_CHUNK_TILES + tx - alongX,
            (ty + alongY) * ENDLESS_CHUNK_TILES + tx + alongX,
        };
        var shoulders = new List<int>
        {
            (ty - tangentY) * ENDLESS_CHUNK_TILES + tx - tangentX,
            (ty + tangentY) * ENDLESS_CHUNK_TILES + tx + tangentX,
        };
        if (
            approaches.every((index) => isWalkable(tiles[index])) &&
            shoulders.every((index) => tiles[index] == TileType.Solid))
        {
            tiles[socket.index] = TileType.Cleft;
            if (
                TerrainModel.terrainCleftProfileAt(tiles, elevation, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, tx, ty) ==
                null)
                tiles[socket.index] = TileType.Solid;
        }
    }

    private static readonly (int dx, int dy)[] UNDERPASS_STEPS = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>
    /// Depth synthesis is deliberately opportunistic. A medium-scale height beat can invalidate the independent
    /// high banks required by a proposed underpass, in which case ordinary floor is the honest artifact. Demote the
    /// complete connected proposal instead of letting final validation replace the whole authored chunk with the
    /// emergency fallback.
    /// </summary>
    private static void demoteInvalidCountryUnderpasses(byte[] tiles, sbyte[] elevation)
    {
        int width = ENDLESS_CHUNK_TILES;
        var seen = new byte[tiles.Length];
        for (int start = 0; start < tiles.Length; start++)
        {
            if (seen[start] != 0 || tiles[start] != TileType.Underpass) continue;
            var component = new List<int>();
            var stack = new List<int> { start };
            seen[start] = 1;
            bool valid = true;
            while (stack.Count > 0)
            {
                int index = stack.pop();
                component.push(index);
                int tx = index % width;
                int ty = (int)Math.floor((double)index / width);
                if (TerrainModel.terrainUnderpassProfileAt(tiles, elevation, width, width, tx, ty) == null) valid = false;
                foreach (var (dx, dy) in UNDERPASS_STEPS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width) continue;
                    int neighbour = ny * width + nx;
                    if (seen[neighbour] != 0 || tiles[neighbour] != TileType.Underpass) continue;
                    seen[neighbour] = 1;
                    stack.push(neighbour);
                }
            }
            if (!valid)
                foreach (int index in component) tiles[index] = TileType.Floor;
        }
    }

    private static void demoteInvalidCountryClefts(byte[] tiles, sbyte[] elevation)
    {
        if (Array.IndexOf(tiles, (byte)TileType.Cleft) < 0) return;
        // The artifact compiler may turn a steep walkable neighbour into Solid before validation. Probe that exact
        // normalization on a private tile copy so a Cleft whose approach would disappear is rejected up front.
        byte[] compiledTiles = tiles.slice();
        TerrainKit.materializeUnclimbableWalkableEdges(
            compiledTiles,
            elevation,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES);
        for (int index = 0; index < tiles.Length; index++)
        {
            if (tiles[index] != TileType.Cleft) continue;
            int tx = index % ENDLESS_CHUNK_TILES;
            int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
            if (
                TerrainModel.terrainCleftProfileAt(
                    compiledTiles,
                    elevation,
                    ENDLESS_CHUNK_TILES,
                    ENDLESS_CHUNK_TILES,
                    tx,
                    ty) ==
                null)
                tiles[index] = TileType.Solid;
        }
    }

    /// <summary>
    /// A rescued deck can join an older local deck component and thereby turn the combined component into a short,
    /// redundant platform. Validate the composed result, not merely the candidate in isolation. Boundary stubs are
    /// retained for their neighbouring streamed chunk; every complete invalid component recovers its barrier.
    /// </summary>
    private static void demoteNonfunctionalCountryBridges(byte[] tiles)
    {
        var use = TerrainWater.analyzeTerrainBridgeUse(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new TerrainBridgeUseOptions
        {
            ignoreBoundaryComponents = true,
            minimumUsefulDetour = 24,
        });
        foreach (var component in use.components)
        {
            if (component.valid || component.ignored) continue;
            int replacement =
                component.chasmContacts > component.waterContacts ? TileType.Chasm : TileType.Water;
            foreach (int index in component.indices) tiles[index] = (byte)replacement;
        }
    }

    private static readonly (int dx, int dy)[] COUNTRY_CARDINAL_STEPS = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>
    /// Re-open sparse seam contracts after topology/geology has had its final word.
    ///
    /// A late climb-aware component sweep may legitimately discard a local route fragment which only becomes
    /// useful when its neighbouring streamed chunk exists. That rule is correct for decorative dead ends but not
    /// for a neighbour-agreed seam port. Each missing port therefore gets the shortest deterministic one-tile pass
    /// through Solid rock into the surviving walkable component. Water and Chasm are never tunneled through; their
    /// authored topology stays intact. The common case (all ports survived) allocates nothing and only inspects at
    /// most four cells. A missing port pays one bounded 1,024-cell search and, because this runs after every
    /// destructive raster pass, cannot be closed again.
    /// </summary>
    private static void restoreCountrySeamPortAccess(
        byte[] tiles,
        sbyte[] elevation,
        byte[] routeMask,
        IReadOnlyList<EndlessCountryChunkPort> ports)
    {
        int[]? previous = null;
        int[]? queue = null;
        foreach (EndlessCountryChunkPort port in ports)
        {
            int portIndex = port.ty * ENDLESS_CHUNK_TILES + port.tx;
            if (isWalkable(tiles[portIndex])) continue;

            previous ??= new int[tiles.Length];
            queue ??= new int[tiles.Length];
            previous.fill(-2);
            int head = 0;
            int tail = 0;
            previous[portIndex] = -1;
            queue[tail++] = portIndex;
            int landing = -1;
            int walkableAnchor = -1;

            while (head < tail && landing < 0)
            {
                int index = queue[head++];
                int tx = index % ENDLESS_CHUNK_TILES;
                int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
                foreach (var (dx, dy) in COUNTRY_CARDINAL_STEPS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= ENDLESS_CHUNK_TILES || ny >= ENDLESS_CHUNK_TILES) continue;
                    int neighbour = ny * ENDLESS_CHUNK_TILES + nx;
                    if (previous[neighbour] != -2) continue;
                    int tile = tiles[neighbour];
                    if (isWalkable(tile))
                    {
                        landing = index;
                        walkableAnchor = neighbour;
                        break;
                    }
                    if (tile != TileType.Solid) continue;
                    previous[neighbour] = index;
                    queue[tail++] = neighbour;
                }
            }

            if (landing < 0 || walkableAnchor < 0) continue;
            sbyte anchorElevation = elevation[walkableAnchor];
            for (int index = landing; index >= 0; index = previous[index])
            {
                routeMask[index] = 1;
                if (isWalkable(tiles[index])) continue;
                tiles[index] = TileType.Floor;
                elevation[index] = anchorElevation;
            }
        }
    }

    /// <summary>Chebyshev distance (1..3, or 4 when unbroken) from a cell to the nearest cell of a different tile kind.</summary>
    private static int terrainEdgeDistance(byte[] tiles, int tx, int ty, int tile)
    {
        int width = ENDLESS_CHUNK_TILES;
        for (int distance = 1; distance <= 3; distance++)
        {
            for (int dy = -distance; dy <= distance; dy++)
            {
                for (int dx = -distance; dx <= distance; dx++)
                {
                    if (Math.max(Math.abs(dx), Math.abs(dy)) != distance) continue;
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width) continue;
                    if (tiles[ny * width + nx] != tile) return distance;
                }
            }
        }
        return 4;
    }

    private static bool adjacentTerrainKind(byte[] tiles, int tx, int ty, int kind)
    {
        int width = ENDLESS_CHUNK_TILES;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx;
                int ny = ty + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= width) continue;
                if (tiles[ny * width + nx] == kind) return true;
            }
        }
        return false;
    }

    private static EndlessCountryLandmarkPlan? landmarkNear(IReadOnlyList<EndlessCountryPlan> plans, double x, double y)
    {
        EndlessCountryLandmarkPlan? nearest = null;
        double nearestDistance = double.PositiveInfinity;
        foreach (EndlessCountryPlan plan in plans)
        {
            foreach (EndlessCountryLandmarkPlan landmark in plan.landmarks)
            {
                double dx = x - landmark.x;
                double dy = y - landmark.y;
                double radius = landmark.variant.radius * 1.08;
                if (Math.abs(dx) > radius || Math.abs(dy) > radius) continue;
                double distance = dx * dx + dy * dy;
                if (distance <= radius * radius && distance < nearestDistance)
                {
                    nearest = landmark;
                    nearestDistance = distance;
                }
            }
        }
        return nearest;
    }

    private sealed class DecorationCandidate
    {
        public int index;
        public int tx;
        public int ty;
        public int x;
        public int y;
        /// <summary>A <see cref="TerrainDecorationKind"/> value.</summary>
        public string kind = "";
        /// <summary>
        /// What this cell becomes when it joins an already-placed prop instead of standing alone: the same context
        /// vocabulary with the canopy removed. `null` means the context has no understory at all, so the cell
        /// simply stays empty rather than gaining a kind its theme never authored.
        /// </summary>
        public string? understoryKind;
        public bool canopy;
        /// <summary>An <see cref="EndlessDressingContext"/> value.</summary>
        public string context = "";
        /// <summary>Solitary-site rank without the grove gate; used only for rare specimen trees in negative space.</summary>
        public double fieldScore;
        /// <summary>Whether this cell is open, walkable combat floor: the ground the skeleton pass is allowed to seat on.</summary>
        public bool openFloor;
        /// <summary>The authored forest structure this cell belongs to; absent cells are intentional negative space.</summary>
        public EndlessForestSample? forest;
        /// <summary>Tree-site ordering inside that formation, independent of the generic prop vocabulary.</summary>
        public double forestScore;
        /// <summary>
        /// This cell's contribution to its context's share of the chunk budget: the context density times the lane
        /// discount, and deliberately NOTHING else. Grove, quiet-sector, landmark and landscape modulation decide
        /// WHICH cells of a context win — they are composition inside a context and must not silently rewrite the
        /// split between contexts, or `density` stops meaning what its own doc comment says.
        /// </summary>
        public double demand;
        /// <summary>How many of its eight neighbours this prop may adopt as companions, if it is placed as an anchor.</summary>
        public int companions;
        /// <summary>
        /// Whether this cell is the SHOULDER of a route or court — the strip of ground where the walkable lane meets
        /// rock, water or a void.
        ///
        /// The lane itself stays clear because the guaranteed trail is the map's one legible line. Its shoulder is
        /// the opposite case: it is where a path through a landscape gets its edge. A shoulder anchor therefore grows
        /// a SKIRT — a run of three to six contiguous understory cells along the lane — instead of the single lollipop
        /// that made the reviewed frames read as a corridor between two bare terraces.
        /// </summary>
        public bool shoulder;
        public double seed;
        public double score;
        /// <summary>
        /// Fair-share merge key: the candidate's rank inside its own context bucket, divided by that context's share
        /// of the chunk budget. Written by `stratifyDecorationBudget`, never by the collection loop.
        /// </summary>
        public double order;
    }

    /// <summary>
    /// How many already-placed props a companion may touch.
    ///
    /// One is a bush at a tree's foot; two is the cell in the crook between two props, and it is also every interior
    /// cell of a linear skirt. Three would be a cell surrounded by dressing, which is ground cover — the thing this
    /// rule must never produce.
    /// </summary>
    private const int COMPANION_MAX_NEIGHBOURS = 2;

    /// <summary>
    /// The most already-placed props any one prop may end up touching once its neighbours have grown in.
    ///
    /// COMPANION_MAX_NEIGHBOURS is checked against the cell being placed; this is checked against the cells
    /// it would be placed NEXT TO, so a stand can never quietly close around an existing prop from four sides.
    /// </summary>
    private const int STAND_MAX_TOUCH = 3;

    /// <summary>
    /// Plate cells one open-ground COPSE answers for, how many crowns it carries, and the per-chunk cap.
    ///
    /// The frame contract is "stand anywhere on the plate and see a silhouette standing on it too", measured over an
    /// eight-tile reach — a treeline on the terrace behind the plate does not answer it. The object that answers it
    /// is a compact island of crowns, not a lone one: a tree in this world grows in a FORMATION, and a scatter of
    /// single trees across open ground is the uniform tree grid every composition pass here exists to prevent. One
    /// three-crown copse per ~110 plate cells covers a clearing at that reach while leaving most of it open.
    /// </summary>
    private const double PLATE_CELLS_PER_COPSE = 120;
    private const int PLATE_COPSE_CROWNS = 3;
    private const double PLATE_COPSE_CAP = 3;
    /// <summary>Minimum separation between two plate copses, in tiles — wide, so each reads as its own place.</summary>
    private const double PLATE_COPSE_SPACING = 6.5;
    /// <summary>Reach a copse adopts its own crowns within, in tiles. Diagonal, so three crowns form one mass.</summary>
    private const double PLATE_COPSE_REACH = 1.5;

    /// <summary>JS `a || b` on numbers (NaN and ±0 are falsy). Operands are pure, so eager evaluation is exact.</summary>
    private static double jsOr(double a, double b) => Js.Truthy(a) ? a : b;

    /// <summary>`map.get(key) ?? 0` on a numeric Map.</summary>
    private static double getOrZero(JsMap<string, double> map, string key) => map.TryGetValue(key, out double value) ? value : 0;

    /// <summary>
    /// Turn the theme's per-context `density` into a PROPORTION of the chunk's budget.
    ///
    /// The pass used to multiply `density` into a per-cell score, sort every candidate globally and truncate at the
    /// budget. With roughly 1500 eligible cells competing for 59 slots that made `density` an absolute priority
    /// rank instead of a share: the hazard-bank contexts (1.0..1.35) swept the entire budget and the open gameplay
    /// floor (0.55..0.7) — more than half of every chunk — received 3% of the dressing. The measured frames showed
    /// it exactly: trees stood in a line along every terrace cap while the field the squad fought in was bare.
    ///
    /// So each context is given `budget x (its summed per-cell demand) / (total demand)` slots — demand being
    /// density times the lane discount, i.e. exactly "density x eligible cells" — and the existing greedy
    /// blue-noise selection runs INSIDE each bucket. Interleaving is expressed as one merge key: rank within the
    /// bucket over that bucket's quota. The whole thing therefore remains a single sorted greedy pass with no
    /// extra structures. The returned quotas additionally cap the pass's first sweep, because a merge key alone
    /// only equalises the number of candidates each context is OFFERED: a context whose cells are rejected more
    /// often by spacing would still lose its share to one whose cells are cheap to accept. Whatever the capped
    /// sweep leaves is then filled by an uncapped second sweep over the very same order, so a context that truly
    /// cannot fill its share never wastes budget.
    ///
    /// A share is still not a DISTRIBUTION, which is the second half of the rule and lives in the pass itself: the
    /// open floor spends FIELD_SKELETON_SHARE of its own quota on a spaced skeleton before any ranked
    /// sweep runs. Total prop count, bake cost and draw calls are unchanged.
    /// </summary>
    private static JsMap<string, double> stratifyDecorationBudget(List<DecorationCandidate> candidates, double budget)
    {
        var demand = new JsMap<string, double>();
        var buckets = new JsMap<string, List<DecorationCandidate>>();
        double totalDemand = 0;
        foreach (DecorationCandidate candidate in candidates)
        {
            totalDemand += candidate.demand;
            demand.set(candidate.context, getOrZero(demand, candidate.context) + candidate.demand);
            if (!buckets.TryGetValue(candidate.context, out List<DecorationCandidate> bucket) || bucket == null)
            {
                bucket = new List<DecorationCandidate>();
                buckets.set(candidate.context, bucket);
            }
            bucket.push(candidate);
        }
        var quotas = new JsMap<string, double>();
        if (totalDemand <= 0) return quotas;
        foreach (var (context, bucket) in buckets)
        {
            bucket.sort((a, b) => jsOr(b.score - a.score, a.index - b.index));
            // A quota is a real number on purpose: rounding it would hand the rounding residue to whichever context
            // happens to be listed first, which is the same "priority instead of proportion" bug one level down.
            double quota = Math.max(1e-6, (budget * getOrZero(demand, context)) / totalDemand);
            quotas.set(context, quota);
            for (int rank = 0; rank < bucket.Count; rank++) bucket[rank].order = (rank + 1) / quota;
        }
        candidates.sort((a, b) => jsOr(jsOr(a.order - b.order, b.score - a.score), a.index - b.index));
        return quotas;
    }

    /// <summary>
    /// World-stable one-cell blue-noise ownership. Local greedy spacing is insufficient at immutable chunk seams:
    /// neither chunk can see the other's chosen set. A seed-phased two-cell world lattice owns the seam belt;
    /// score-ranked local selection composes the safely inset interior into organic stands.
    /// </summary>
    private static bool ownsWorldDressingSeamCell(double seed, int x, int y)
    {
        int phaseX = countryHash(seed, SALT.dressingDetail ^ 0x6a09e667, 0, 0) < 0.5 ? 0 : 1;
        int phaseY = countryHash(seed, SALT.dressingDetail ^ 0xbb67ae85, 0, 0) < 0.5 ? 0 : 1;
        int parityX = ((x % 2) + 2) % 2;
        int parityY = ((y % 2) + 2) % 2;
        return parityX == phaseX && parityY == phaseY;
    }

    /// <summary>
    /// THE rule that classifies a chunk-local cell into a dressing context, so the theme registry can answer with
    /// the right vocabulary. Hazard contact wins over rock depth: a shoreline is a shoreline whether the bank is
    /// cliff or meadow, and that is what makes the water read as water from across the view.
    ///
    /// Exported so anything that has to reason about the dressing split — the placement pass itself, and the
    /// budget-share contract test — asks this one function instead of restating the classification.
    /// </summary>
    public static string endlessDressingContextAt(byte[] tiles, int tx, int ty, int tile)
    {
        if (adjacentTerrainKind(tiles, tx, ty, TileType.Chasm)) return EndlessDressingContext.ChasmLip;
        if (adjacentTerrainKind(tiles, tx, ty, TileType.Water)) return EndlessDressingContext.WaterBank;
        if (tile != TileType.Solid) return EndlessDressingContext.Ground;
        return terrainEdgeDistance(tiles, tx, ty, tile) <= 2
            ? EndlessDressingContext.WallEdge
            : EndlessDressingContext.WallInterior;
    }

    /// <summary>
    /// How strongly the active landscape wants to be dressed. A flooded delta or a bare canyon rim must not carry
    /// the same stand density as a highland shelf, but no landscape may strip a Country back to bare ground —
    /// that is precisely the empty look this whole pass exists to remove.
    /// </summary>
    private static double landscapeDressingScale(EndlessLandscapeTraits traits) =>
        countryClamp(
            0.78 + traits.rockDensity * 0.3 + traits.pillarDensity * 0.22 - traits.opennessBias * 0.18,
            0.62,
            1.28);

    private sealed class ForestGroupState
    {
        public double id;
        public List<DecorationCandidate> entries = null!;
        public int placed;
        public double cap;
    }

    /// <summary>
    /// Dress one chunk.
    ///
    /// Every prop is drawn from the theme's own context vocabulary, so a run's props state its identity instead of
    /// being generic scatter. A world-space ownership lattice owns the two-cell seam belt, so a stand can never
    /// form across an immutable chunk boundary where neither side can see the other's choices. Everywhere else the
    /// budget is split by context share (`stratifyDecorationBudget`) and the greedy pass composes each
    /// context's winners into groves, banks and market rows — with companions allowed, so a stand is a stand.
    /// </summary>
    private static List<TerrainDecorationPlacement> createCountryDecorations(
        byte[] tiles,
        sbyte[] elevation,
        byte[] routeMask,
        byte[] primaryRouteMask,
        byte[] courtMask,
        double seed,
        string biomeKey,
        int baseTx,
        int baseTy,
        EndlessCountryPlan owner,
        IReadOnlyList<EndlessCountryPlan> plans,
        byte[]? setPieceClaim = null)
    {
        int width = ENDLESS_CHUNK_TILES;
        var theme = EndlessDressing.endlessDressingFor(biomeKey);
        double landscapeScale = landscapeDressingScale(owner.landscape.traits);
        // Openness and dressable area, measured once. Everything that has to reason about "is this the open plate or
        // the bank" reads this one field rather than estimating its own.
        var area = EndlessDressingBudget.measureEndlessDressableArea(tiles, width);
        byte[] waterDistance = EndlessForestHabitat.endlessForestDistanceTo(tiles, width, TileType.Water);
        byte[] chasmDistance = EndlessForestHabitat.endlessForestDistanceTo(tiles, width, TileType.Chasm);
        var candidates = new List<DecorationCandidate>();

        for (int ty = 1; ty < width - 1; ty++)
        {
            for (int tx = 1; tx < width - 1; tx++)
            {
                int index = ty * width + tx;
                int tile = tiles[index];
                // Props stand on rock or on walkable ground. Anchoring one in open water or over a void would float it.
                if (tile != TileType.Solid && !isWalkable(tile)) continue;
                // A set piece dresses itself. Scatter inside its footprint would put a thicket in a stone ring's plaza
                // and a rubble heap on a gatehouse's threshold, which is how a built thing stops reading as built.
                if (setPieceClaim != null && setPieceClaim[index] != 0) continue;
                // THE ARTERY keeps its lane clear — and only the artery. The guaranteed escape trail is one unbroken
                // legible line through the map and a prop standing on it costs that readability, even though every prop
                // is collision-neutral. Its shoulder does not: a cell that already borders rock, water or a void is the
                // bank of the path, and leaving banks bare is what emptied water-and-void chunks whose only standable
                // ground IS the route.
                //
                // What this test may NOT do is ask `routeMask`. That mask marks the whole carved corridor network, which
                // measures **77.7 % of all walkable ground** — so asking it here refused a prop on three quarters of the
                // floor and left only the cells touching rock. The measured consequence was a 6.5-tile prop spacing one
                // step from rock against 46.0 tiles on the open plate: the squad fought in the one part of the world the
                // dressing pass had been forbidden to touch. The artery is `primaryRouteMask`, the same one-line
                // hierarchy the floor pigment already publishes as `floorUsage`; a secondary lane is ordinary ground.
                //
                // A COURT is not a lane either. It is the plaza at a chunk junction and the combat room — a place,
                // twelve or more cells across — and it is dressed, just more quietly than the free field around it.
                bool onArtery = primaryRouteMask[index] != 0;
                bool onLane = routeMask[index] != 0 && !onArtery;
                bool onCourt = courtMask[index] != 0;
                double laneScale = 1;
                bool shoulder = false;
                if (onArtery || onCourt)
                {
                    shoulder =
                        !isWalkable(tiles[index - 1]) ||
                        !isWalkable(tiles[index + 1]) ||
                        !isWalkable(tiles[index - width]) ||
                        !isWalkable(tiles[index + width]);
                    // The trail stays clear wherever it runs, including where it crosses a court: that crossing is the
                    // junction the whole route hierarchy exists to make readable.
                    if (onArtery && !shoulder) continue;
                    laneScale = shoulder ? 1 : 0.75;
                }
                // A secondary lane is dressed like the ground it is: slightly thinner, because it is still circulation
                // and a prop dead-centre in a corridor reads as clutter, but never excluded.
                if (onLane) laneScale *= 0.86;
                int x = baseTx + tx;
                int y = baseTy + ty;
                EndlessForestSample? rawForest = EndlessForest.endlessForestAt(seed, x + 0.5, y + 0.5);

                // Repaired terrain and erosion habitat bend the world-space formation towards viable tree sites.
                EndlessForestSample? forest = rawForest != null
                    ? EndlessForestHabitat.endlessForestHabitatAt(rawForest, new EndlessForestHabitatInput
                    {
                        tiles = tiles,
                        elevation = elevation,
                        waterDistance = waterDistance,
                        chasmDistance = chasmDistance,
                        width = width,
                        index = index,
                        erosionSeed = Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(SALT.heightDetail)),
                        x = x,
                        y = y,
                    })
                    : null;
                // Generic seam props use global ownership; a continuous world-space forest body is exempt.
                bool inSeamBelt = tx < 2 || ty < 2 || tx > width - 3 || ty > width - 3;
                if (
                    inSeamBelt &&
                    !ownsWorldDressingSeamCell(seed, x, y) &&
                    (forest == null ||
                        forest.formation == EndlessForestFormation.Solitary ||
                        forest.strength < 0.16))
                    continue;

                string context = endlessDressingContextAt(tiles, tx, ty, tile);
                var contextProfile = theme.contexts[context];

                // The renderer's global scene field is the common art-director for terrain pigment, grass, trees and
                // props. Theme/seed noise remains as the fine local break-up, but it can no longer put a tree carpet over
                // a deliberate focus court or fill the composition's negative-space sector independently.
                var scene = TerrainCompositionField.terrainCompositionAt(x + 0.5, y + 0.5, biomeKey);
                double localGrove =
                    valueNoise(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(SALT.dressing)), x, y, theme.groveScale) * 0.66 +
                    valueNoise(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(SALT.dressingDetail)), x + 17, y - 11, theme.groveScale * 0.42) *
                        0.34;
                double grove = scene.grove * 0.76 + localGrove * 0.24;
                double groveThreshold = countryClamp01(
                    (grove - (0.22 + theme.groveContrast * 0.08)) /
                        Math.max(0.2, 0.62 - theme.groveContrast * 0.08));
                double groveGate = 0.08 + groveThreshold * groveThreshold * (3 - 2 * groveThreshold) * 0.92;

                // The Country's quiet sector thins its dressing into a breather instead of banning it outright: an
                // abruptly bare sector reads as a bug, a sparse one reads as pacing.
                double dx = x - owner.anchor.x;
                double dy = y - owner.anchor.y;
                double quiet = Math.cos(owner.quietAngle) * dx + Math.sin(owner.quietAngle) * dy;
                double countryQuietScale = quiet > 40 ? 0.42 : quiet > 16 ? 0.74 : 1;
                double quietScale = countryQuietScale * (1 - scene.quiet * 0.7);

                EndlessCountryLandmarkPlan? landmark = landmarkNear(plans, x, y);
                double landmarkBoost = 1;
                if (landmark != null)
                {
                    LandmarkCoordinates coordinates = landmarkCoordinates(landmark, x, y);
                    double normalized = coordinates.radius / landmark.variant.radius;
                    double beat = Math.cos(
                        coordinates.theta * landmark.variant.symmetry * 2 + landmark.variant.phase);
                    // A landmark's own rhythm ring dresses denser than the rest of its footprint, so the set piece has a
                    // treeline around it instead of a hard edge. It no longer selects a different KIND: growth is the whole
                    // vocabulary, and a landmark is stated by its landform, not by a prop nobody planted.
                    landmarkBoost =
                        normalized > 0.48 && normalized < 0.94 && beat > -0.18
                            ? 1 + landmark.variant.dressingBoost * 0.5 + scene.focus * 0.18
                            : 1.12 + scene.focus * 0.12;
                }

                // The open plate is ground deep enough from rock, water and void to need its own composition.
                bool openFloor =
                    context == EndlessDressingContext.Ground && area.depth[index] >= EndlessDressingBudget.ENDLESS_PLATE_DEPTH;
                double composition = quietScale * landmarkBoost * landscapeScale * laneScale;
                double weight = contextProfile.density * groveGate * composition;
                if (weight <= 0) continue;
                // A per-cell jitter breaks ties inside a grove core so a stand never rasterizes into a lattice.
                double jitter =
                    (0.72 + countryHash(seed, SALT.dressing ^ 0x27d4eb2f, x, y) * 0.28) * (1 + scene.focus * 0.12);
                double score = weight * jitter;
                double kindRoll = countryHash(seed, SALT.dressingDetail ^ 0x9e3779b9, x, y);
                string kind = EndlessDressing.endlessDressingKindAt(theme, context, kindRoll);
                double companionRoll = countryHash(seed, SALT.dressingDetail ^ 0x85ebca6b, x, y);
                // Trees belong exclusively to a formation or explicit solitary site.
                bool explicitSolitary =
                    forest?.formation == EndlessForestFormation.Solitary && forest.strength >= 0.18;
                if (kind == TerrainDecorationKind.Tree && !explicitSolitary)
                {
                    string? replacement = EndlessDressing.endlessDressingUnderstoryKindAt(theme, context, companionRoll);
                    if (replacement == null) continue;
                    kind = replacement;
                }
                uint seedBits = unchecked((uint)(Math.imul(Js.ToInt32(seed) ^ x, 0x27d4eb2f) ^ Math.imul(y, 0x165667b1)));
                double seedValue = seedBits != 0 ? seedBits : 1;
                // Adoption capacity is a pure function of (seed, x, y) exactly like the score jitter, so a cell offers
                // the same number of companion slots no matter which chunk or which machine evaluates it.
                // The reviewed pass offered a companion slot to 44% of anchors and delivered about three visible copses
                // per frame, because a slot was only ever taken if the greedy order happened to reach a neighbour cell
                // next. Anchors now GROW their stand immediately, so the offered rate is close to the delivered rate,
                // and the offer itself is raised: a majority of props should stand with something at their foot.
                double adoptionRoll = countryHash(seed, SALT.dressing ^ 0x9e3779b1, x, y);
                candidates.push(new DecorationCandidate
                {
                    index = index,
                    tx = tx,
                    ty = ty,
                    x = x,
                    y = y,
                    kind = kind,
                    understoryKind = EndlessDressing.endlessDressingUnderstoryKindAt(theme, context, companionRoll),
                    // The canopy claims spacing: packing two crowns together would read as two things growing out of
                    // each other.
                    canopy = EndlessDressing.isTallDressingKind(kind),
                    context = context,
                    openFloor = openFloor,
                    forest = forest,
                    forestScore =
                        (forest?.strength ?? 0) *
                        (0.78 + countryHash(seed, SALT.dressing ^ 0x94d049bb, x, y) * 0.22) *
                        composition,
                    // The skeleton's own ranking: the composition's pacing WITHOUT the woodland mask. Ranking the floor by
                    // the grove field is the mechanism that emptied the combat plate, so the layer whose job is to cover
                    // that plate cannot be ranked by it.
                    fieldScore = contextProfile.density * composition * jitter,
                    demand = contextProfile.density * laneScale,
                    // ~40% of anchors offer a slot, and because an anchor now grows its own stand that is very close to
                    // the rate the frame actually shows. Higher was measured and rejected: at 58% the world reached 79%
                    // clustered props and lost nearly half its tall silhouettes to understory, which trades one defect
                    // (confetti) for a worse one (no verticals anywhere).
                    companions = adoptionRoll < 0.6 ? 0 : adoptionRoll < 0.87 ? 1 : 2,
                    shoulder = shoulder,
                    seed = seedValue,
                    score = score,
                    order = 0,
                });
            }
        }

        // Density follows the ground that EXISTS and varies across the world — see module endlessDressingBudget.
        // The retired form was `round(density · 1024/1000) + 6`, a pure function of the theme, and it gave all 64
        // measured chunks the identical count (sd = 0.00) regardless of whether they were cliff or open plain.
        double density = EndlessDressingBudget.endlessDressingDensityAt(seed, baseTx + width * 0.5, baseTy + width * 0.5);
        double budget = EndlessDressingBudget.endlessDressingBudgetFor(theme.density, landscapeScale, density, area);
        JsMap<string, double> quotas = stratifyDecorationBudget(candidates, budget);

        var chosen = new List<DecorationCandidate>();
        // The kind each accepted record actually carries — a companion is demoted to its context's understory.
        var chosenKinds = new List<string>();
        var chosenCanopy = new List<bool>();
        // Whether a record stands alone (an anchor) or joined an existing prop (a companion).
        var chosenAnchor = new List<bool>();
        var taken = new JsMap<string, double>();
        double canopySpacingSq = theme.canopySpacing * theme.canopySpacing;
        var occupied = new byte[tiles.Length];
        // Whether the prop standing at a cell is a full-height silhouette — the "may this grow gravel?" question.
        var tallAt = new byte[tiles.Length];
        var forestTreeAt = new byte[tiles.Length];
        // Companion slots still free on the prop standing at a cell.
        var adoption = new byte[tiles.Length];
        // How many occupied cells touch each cell — the exact stand-size guard, maintained as props land.
        var touchCount = new byte[tiles.Length];
        // Cell -> candidate, so an accepted anchor can grow its own stand instead of waiting for the sort order.
        var candidateAt = new DecorationCandidate?[tiles.Length];
        foreach (DecorationCandidate candidate in candidates) candidateAt[candidate.index] = candidate;

        // Visit the eight neighbours of a cell that lie inside the chunk. ONE traversal for every neighbour rule.
        void forEachNeighbour(int ctx, int cty, Action<int> visit)
        {
            for (int ndy = -1; ndy <= 1; ndy++)
            {
                int ny = cty + ndy;
                if (ny < 0 || ny >= width) continue;
                for (int ndx = -1; ndx <= 1; ndx++)
                {
                    if (ndx == 0 && ndy == 0) continue;
                    int nx = ctx + ndx;
                    if (nx < 0 || nx >= width) continue;
                    visit(ny * width + nx);
                }
            }
        }

        void accept(DecorationCandidate candidate, string kind, bool canopy, bool anchor)
        {
            occupied[candidate.index] = 1;
            if (EndlessDressing.isTallDressingKind(kind)) tallAt[candidate.index] = 1;
            forEachNeighbour(candidate.tx, candidate.ty, (cell) =>
            {
                touchCount[cell]++;
                // Joining a stand costs one slot from every prop the newcomer touches, so a cluster can neither outgrow
                // its host's capacity nor chain across the chunk.
                if (!anchor && occupied[cell] != 0 && adoption[cell] > 0) adoption[cell]--;
            });
            taken.set(candidate.context, getOrZero(taken, candidate.context) + 1);
            chosen.push(candidate);
            chosenKinds.push(kind);
            chosenCanopy.push(canopy);
            chosenAnchor.push(anchor);
        }

        // What this cell becomes if it joins the stand around it.
        //
        // `null` means the theme authored no growth this cell could become, and the cell then stays empty
        // rather than borrowing a kind the fiction never stated.
        string? companionKindFor(DecorationCandidate candidate) => candidate.understoryKind;

        // May this cell take a COMPANION? The former rule was an isotropic 3x3 veto, and it made the world a dot
        // lattice: the measured nearest-neighbour distance was a delta function at exactly 2.00 cells with NOTHING
        // at 1, so a copse, a bush at a tree's foot, a rubble skirt and an understory were all structurally
        // impossible. A prop may now adopt neighbours — bounded by its host's capacity, never as a second crown, and
        // never as the object that closes a knot around something already standing.
        bool companionAdmissible(DecorationCandidate candidate)
        {
            if (companionKindFor(candidate) == null) return false;
            if (touchCount[candidate.index] > COMPANION_MAX_NEIGHBOURS) return false;
            bool ok = true;
            forEachNeighbour(candidate.tx, candidate.ty, (cell) =>
            {
                if (occupied[cell] == 0) return;
                if (adoption[cell] == 0 || touchCount[cell] >= STAND_MAX_TOUCH) ok = false;
            });
            return ok;
        }

        // Grow the stand an accepted anchor carries, immediately.
        //
        // The reviewed pass left this to the global sort order, which almost never offers an anchor's own neighbour
        // next — so 44% of anchors were granted a companion and about three copses per frame actually appeared. A
        // shoulder anchor grows a SKIRT instead of a tuft: a run of contiguous understory along the lane edge, which
        // is what makes a route read as a path through a landscape.
        bool withinQuota(DecorationCandidate candidate, int sweep) =>
            sweep > 0 || getOrZero(taken, candidate.context) < getOrZero(quotas, candidate.context);

        // Two crowns may never touch.
        //
        // The rule was previously carried by the COMPANION path alone, which was enough only because the world was
        // sparse: at the area-driven density two independent anchors land in each other's neighbourhood often, and
        // the contracts caught exactly that — two crowns growing out of each other, neither of which had ever been a
        // companion. Growth at a crown's foot is always welcome, which is why only the canopy is asked.
        bool silhouetteClear(DecorationCandidate candidate, string kind)
        {
            if (!EndlessDressing.isTallDressingKind(kind)) return true;
            bool clear = true;
            forEachNeighbour(candidate.tx, candidate.ty, (cell) =>
            {
                if (tallAt[cell] != 0) clear = false;
            });
            return clear;
        }

        bool canopyClear(DecorationCandidate candidate, bool canopy)
        {
            if (!canopy || theme.canopySpacing <= 1.5) return true;
            for (int entry = 0; entry < chosen.Count; entry++)
            {
                if (!chosenCanopy[entry]) continue;
                int cdx = chosen[entry].tx - candidate.tx;
                int cdy = chosen[entry].ty - candidate.ty;
                if (cdx * cdx + cdy * cdy < canopySpacingSq) return false;
            }
            return true;
        }

        void growStand(DecorationCandidate anchor, int sweep, bool skeleton = false)
        {
            // A skirt is a LANE edge treatment. A skeleton anchor is the opposite figure — a lone vertical with a
            // foot clump, deliberately spaced from the next one — so it never grows a run, or the floor's share would
            // go into two long hedges instead of six silhouettes.
            bool skirt = anchor.shoulder && !skeleton;
            // The skeleton's whole reason for existing is the silhouette; the clump at its foot is what turns the
            // silhouette into a place rather than a pole, so it is guaranteed rather than rolled.
            int remaining = skeleton
                ? Math.max(1, anchor.companions)
                : skirt
                    ? 2 + anchor.companions
                    : anchor.companions;
            if (remaining <= 0) return;
            var frontier = new List<DecorationCandidate> { anchor };
            while (frontier.Count > 0 && remaining > 0 && chosen.Count < budget)
            {
                DecorationCandidate host = frontier.shift();
                if (adoption[host.index] == 0) continue;
                var options = new List<DecorationCandidate>();
                forEachNeighbour(host.tx, host.ty, (cell) =>
                {
                    if (occupied[cell] != 0) return;
                    DecorationCandidate? option = candidateAt[cell];
                    if (option != null) options.push(option);
                });
                // Deterministic: the neighbour the world itself scored highest, ties broken by cell index.
                options.sort((a, b) => jsOr(b.score - a.score, a.index - b.index));
                foreach (DecorationCandidate option in options)
                {
                    if (remaining <= 0 || chosen.Count >= budget) break;
                    if (adoption[host.index] == 0) break;
                    if (occupied[option.index] != 0 || !companionAdmissible(option)) continue;
                    // A companion is charged to its OWN context's share like any other record. Growing stands off-quota
                    // was measured first and it silently rewrote the split the stratified budget exists to guarantee: the
                    // ground context, which has the most contiguous walkable neighbours, overshot its share by 6..11
                    // points and took it straight out of the wall-edge treeline.
                    if (!withinQuota(option, sweep)) continue;
                    accept(option, companionKindFor(option)!, false, false);
                    remaining--;
                    // A skirt is a RUN: each shoulder link keeps one slot so the line can extend along the lane. Anywhere
                    // else a companion grows no companion of its own, so a copse stays a copse.
                    if (skirt && option.shoulder)
                    {
                        adoption[option.index] = 1;
                        frontier.push(option);
                    }
                }
            }
        }

        // A thicket is UNDERSTORY, not punctuation.
        //
        // Letting the generic ranked sweep seat it as an ordinary anchor is what produced the cheap-looking field of
        // individual green blobs: in the permanent world's 5x5 origin belt, 42% of all thickets stood completely
        // alone and only 2.7% stood at a tree's foot. A thicket may still be adopted by every tree/stump/copse and a
        // route shoulder may lead a hedge, but that hedge is admitted only when its first companion can be placed
        // immediately. Thus every accepted thicket anchor is visibly a PATCH of growth, never a lone substitute for
        // the grass mat that the terrain renderer already owns.
        bool hasAdoptableCompanion(DecorationCandidate candidate, int sweep)
        {
            // The anchor and its first adopted cell are one visual decision. Do not spend the final budget slot on
            // the anchor alone, and preflight the state `companionAdmissible` will see AFTER the anchor has landed.
            // Merely finding a neighbouring candidate is insufficient: that neighbour can already touch a saturated
            // stand two cells away, in which case `growStand` rejects it and the new hedge silently degenerates into
            // the exact solitary bush this rule exists to forbid.
            if (candidate.companions <= 0 || chosen.Count + 1 >= budget) return false;
            bool available = false;
            forEachNeighbour(candidate.tx, candidate.ty, (cell) =>
            {
                if (available || occupied[cell] != 0) return;
                DecorationCandidate? option = candidateAt[cell];
                if (option == null || companionKindFor(option) == null) return;
                // The preflight must include the quota slot the anchor itself is about to consume. Without this
                // prospective check, an anchor and its companion from the same context can both look admissible at the
                // quota boundary; accepting the anchor then closes the bucket and `growStand` refuses the companion.
                // That was the last route by which a lone thicket/stump could escape into the shipped dressing.
                double takenAfterAnchor =
                    getOrZero(taken, option.context) + (candidate.context == option.context ? 1 : 0);
                if (sweep == 0 && takenAfterAnchor >= getOrZero(quotas, option.context)) return;
                // Accepting the anchor raises the option's touch count by one. Existing neighbours of the option must
                // still be able to adopt it; the candidate anchor itself is known to have a free slot and zero contacts.
                if (touchCount[option.index] + 1 > COMPANION_MAX_NEIGHBOURS) return;
                bool admissible = true;
                forEachNeighbour(option.tx, option.ty, (neighbour) =>
                {
                    if (occupied[neighbour] == 0) return;
                    if (adoption[neighbour] == 0 || touchCount[neighbour] >= STAND_MAX_TOUCH)
                        admissible = false;
                });
                if (admissible) available = true;
            });
            return available;
        }

        bool thicketCanLeadStand(DecorationCandidate candidate, int sweep) =>
            candidate.shoulder && hasAdoptableCompanion(candidate, sweep);

        // A stump is evidence of a WOOD, not a generic substitute for scenery.
        //
        // More than half of the production world's stumps were isolated and only 17% stood within two cells of a
        // tree. Permit a stump to lead a tiny clearing scar only inside a real non-solitary forest body, on a rare
        // deterministic draw, and only when it can adopt understory immediately. Stumps may still join an existing
        // stand through the ordinary companion path; both routes make them context rather than brown confetti.
        bool stumpCanLeadClearing(DecorationCandidate candidate, int sweep)
        {
            EndlessForestSample? forest = candidate.forest;
            if (
                forest == null ||
                forest.formation == EndlessForestFormation.Solitary ||
                forest.strength < 0.12 ||
                countryHash(seed, SALT.dressingDetail ^ 0x3c6ef372, candidate.x, candidate.y) >= 0.28)
                return false;
            return hasAdoptableCompanion(candidate, sweep);
        }

        // Sweep 0 holds every context to its share; sweep 1 hands the unclaimed remainder to whoever can still use
        // it. Both walk the same order, so the result is one deterministic greedy selection, not two policies.

        // Forest groups spend a coherent sub-budget round-robin before generic props can atomize it.
        var forestGroups = new JsMap<double, List<DecorationCandidate>>();
        foreach (DecorationCandidate candidate in candidates)
        {
            EndlessForestSample? forest = candidate.forest;
            if (
                forest == null ||
                forest.formation == EndlessForestFormation.Solitary ||
                forest.strength < 0.16 ||
                !WorldDecoration.terrainTileAcceptsDecoration(TerrainDecorationKind.Tree, tiles[candidate.index]))
                continue;
            if (!forestGroups.TryGetValue(forest.id, out List<DecorationCandidate> group) || group == null)
            {
                group = new List<DecorationCandidate>();
                forestGroups.set(forest.id, group);
            }
            group.push(candidate);
        }
        int forestArea = 0;
        foreach (List<DecorationCandidate> group in forestGroups.values()) forestArea += group.Count;
        double forestBudget = Math.min(
            Math.round(budget * 0.82),
            Math.round(
                budget * (0.54 + Math.min(0.28, ((double)forestArea / Math.max(1, candidates.Count)) * 0.92))));
        var groupStates = new List<ForestGroupState>();
        foreach (var (id, entries) in forestGroups.entries())
        {
            entries.sort((a, b) => jsOr(b.forestScore - a.forestScore, a.index - b.index));
            string formation = entries[0].forest!.formation;
            double capRoll = countryHash(seed, SALT.dressing ^ 0x510e527f, Js.ToInt32(id), formation.Length);
            // Reference-calibrated crown counts: woodland 20..34, copse 9..16, windbreak 10..19.
            double cap =
                formation == EndlessForestFormation.Woodland
                    ? Math.min(entries.Count, 20 + Math.floor(capRoll * 15))
                    : formation == EndlessForestFormation.Copse
                        ? Math.min(entries.Count, 9 + Math.floor(capRoll * 8))
                        : Math.min(entries.Count, 10 + Math.floor(capRoll * 10));
            groupStates.push(new ForestGroupState { id = id, entries = entries, placed = 0, cap = cap });
        }
        groupStates.sort((a, b) => a.id - b.id);

        bool touchesOwnForest(DecorationCandidate candidate, double id)
        {
            bool touches = false;
            forEachNeighbour(candidate.tx, candidate.ty, (cell) =>
            {
                DecorationCandidate? neighbour = candidateAt[cell];
                if (forestTreeAt[cell] != 0 && neighbour?.forest != null && neighbour.forest.id == id) touches = true;
            });
            return touches;
        }

        bool forestCanopyClear(DecorationCandidate candidate)
        {
            for (int entry = 0; entry < chosen.Count; entry++)
            {
                if (!chosenCanopy[entry]) continue;
                DecorationCandidate other = chosen[entry];
                if (forestTreeAt[other.index] != 0 && other.forest != null && other.forest.id == candidate.forest!.id) continue;
                int odx = other.tx - candidate.tx;
                int ody = other.ty - candidate.ty;
                if (odx * odx + ody * ody < canopySpacingSq) return false;
            }
            return true;
        }

        int forestStart = chosen.Count;
        bool forestProgress = true;
        while (chosen.Count - forestStart < forestBudget && forestProgress)
        {
            forestProgress = false;
            foreach (ForestGroupState state in groupStates)
            {
                if (chosen.Count - forestStart >= forestBudget || state.placed >= state.cap) continue;
                foreach (DecorationCandidate candidate in state.entries)
                {
                    if (occupied[candidate.index] != 0) continue;
                    // Every crown after the seed must touch its canopy; terrain gaps remain real glades.
                    if (state.placed > 0 && !touchesOwnForest(candidate, state.id)) continue;
                    if (!forestCanopyClear(candidate)) continue;
                    accept(candidate, TerrainDecorationKind.Tree, true, true);
                    forestTreeAt[candidate.index] = 1;
                    state.placed++;
                    forestProgress = true;
                    break;
                }
            }
        }

        // The open plate stands its own COPSES.
        //
        // A plate is the ground a colony works on, and it is the one place a treeline behind it cannot help: stand
        // anywhere on it and there has to be a silhouette standing on it too, or the whole clearing loses its depth
        // cue. The plate used to borrow that silhouette from the run's bespoke `signature` vertical — a set piece
        // nobody in this world made. The honest answer is the one a real clearing gives: a small island of crowns.
        // A LONE crown would have been the cheaper fix and it is the wrong one, because a tree here grows in a
        // formation and a scatter of single trees over open ground is the uniform tree grid every pass here refuses.
        double plateCopses = Math.min(PLATE_COPSE_CAP, Math.round(area.plate / PLATE_CELLS_PER_COPSE));
        if (plateCopses > 0)
        {
            List<DecorationCandidate> plateFloor = candidates.filter(
                (candidate) =>
                    candidate.openFloor &&
                    WorldDecoration.terrainTileAcceptsDecoration(TerrainDecorationKind.Tree, tiles[candidate.index]));
            var leads = new List<DecorationCandidate>();
            // A crown may touch its OWN copse and nothing else — the lead already cleared every other silhouette.
            bool clearOfOtherCopses(DecorationCandidate candidate, IReadOnlyList<DecorationCandidate> members)
            {
                bool clear = true;
                forEachNeighbour(candidate.tx, candidate.ty, (cell) =>
                {
                    if (tallAt[cell] == 0) return;
                    if (!members.some((member) => member.index == cell)) clear = false;
                });
                return clear;
            }
            bool growPlateCopse(DecorationCandidate lead)
            {
                if (occupied[lead.index] != 0 || touchCount[lead.index] > 0) return false;
                // The plate IS the ground context, so a copse is charged to ground's own share of the budget like every
                // other record on it. Seating it off-quota was measured and it starved the wall edge by fifteen points:
                // a pass that answers one context's composition may not quietly spend another context's slots.
                if (!withinQuota(lead, 0)) return false;
                if (
                    leads.some(
                        (other) =>
                            Math.pow(other.tx - lead.tx, 2) + Math.pow(other.ty - lead.ty, 2) <
                            PLATE_COPSE_SPACING * PLATE_COPSE_SPACING))
                    return false;
                if (!canopyClear(lead, true)) return false;
                if (!silhouetteClear(lead, TerrainDecorationKind.Tree)) return false;
                accept(lead, TerrainDecorationKind.Tree, true, true);
                forestTreeAt[lead.index] = 1;
                leads.push(lead);
                var members = new List<DecorationCandidate> { lead };
                double reachSq = PLATE_COPSE_REACH * PLATE_COPSE_REACH;
                List<DecorationCandidate> neighbours = plateFloor
                    .filter(
                        (candidate) =>
                            occupied[candidate.index] == 0 &&
                            Math.pow(candidate.tx - lead.tx, 2) + Math.pow(candidate.ty - lead.ty, 2) <= reachSq)
                    .sort((a, b) => jsOr(b.fieldScore - a.fieldScore, a.index - b.index));
                foreach (DecorationCandidate candidate in neighbours)
                {
                    if (members.Count >= PLATE_COPSE_CROWNS || chosen.Count >= budget) break;
                    if (occupied[candidate.index] != 0 || !withinQuota(candidate, 0)) continue;
                    if (!clearOfOtherCopses(candidate, members)) continue;
                    accept(candidate, TerrainDecorationKind.Tree, true, true);
                    forestTreeAt[candidate.index] = 1;
                    members.push(candidate);
                }
                // Scrub at the copse's feet. A copse standing on bare ground reads as poles planted in a field, and this
                // is also the layer that gives the plate its close-range detail — the same `skeleton` contract the field
                // pass was written for: the silhouette is guaranteed a clump so it becomes a place rather than a pole.
                // One clump per copse, not one per crown: three of them would spend the plate's slots on ground cover.
                adoption[lead.index] = (byte)Math.max(1, lead.companions);
                growStand(lead, 0, true);
                return true;
            }
            // The forest field's own solitary sites lead, because those are the plate verticals the world authored;
            // whatever it did not author is led by the plate's own ranking — the composition's pacing WITHOUT the
            // woodland mask, which is exactly what `fieldScore` is for.
            List<DecorationCandidate> authored = plateFloor
                .filter(
                    (candidate) =>
                        candidate.forest?.formation == EndlessForestFormation.Solitary &&
                        candidate.forest.strength >= 0.18)
                .sort(
                    (a, b) =>
                        jsOr(
                            jsOr(b.forest!.strength - a.forest!.strength, b.fieldScore - a.fieldScore),
                            a.index - b.index));
            List<DecorationCandidate> ranked = plateFloor
                .slice()
                .sort((a, b) => jsOr(b.fieldScore - a.fieldScore, a.index - b.index));
            foreach (DecorationCandidate candidate in authored.concat(ranked))
            {
                if (leads.Count >= plateCopses || chosen.Count >= budget) break;
                growPlateCopse(candidate);
            }
        }

        for (int sweep = 0; sweep < 2 && chosen.Count < budget; sweep++)
        {
            foreach (DecorationCandidate candidate in candidates)
            {
                if (chosen.Count >= budget) break;
                if (occupied[candidate.index] != 0) continue;
                if (!withinQuota(candidate, sweep)) continue;
                if (touchCount[candidate.index] > 0)
                {
                    // A cell already touching a stand may only JOIN it, and only if its host still has a slot free — an
                    // anchor whose adoption capacity is spent refuses every remaining neighbour.
                    if (!companionAdmissible(candidate)) continue;
                    accept(candidate, companionKindFor(candidate)!, false, false);
                    continue;
                }
                // Scrub belongs inside a stand. The only independent form it may lead is a route-shoulder hedge, and
                // only when the next cell can be adopted immediately so the result cannot collapse back to one bush.
                if (
                    owner.version >= ENDLESS_LIVING_GROUND_GENERATION_VERSION &&
                    candidate.kind == TerrainDecorationKind.Thicket &&
                    !thicketCanLeadStand(candidate, sweep))
                    continue;
                if (
                    owner.version >= ENDLESS_BALANCED_DRESSING_GENERATION_VERSION &&
                    candidate.kind == TerrainDecorationKind.Stump &&
                    !stumpCanLeadClearing(candidate, sweep))
                    continue;
                if (!canopyClear(candidate, candidate.canopy)) continue;
                if (!silhouetteClear(candidate, candidate.kind)) continue;
                accept(candidate, candidate.kind, candidate.canopy, true);
                adoption[candidate.index] = (byte)candidate.companions;
                growStand(candidate, sweep);
            }
        }

        var placements = new List<TerrainDecorationPlacement>(chosen.Count);
        for (int entry = 0; entry < chosen.Count; entry++)
        {
            DecorationCandidate candidate = chosen[entry];
            placements.push(new TerrainDecorationPlacement
            {
                kind = chosenKinds[entry],
                tx = candidate.tx,
                ty = candidate.ty,
                seed = candidate.seed,
            });
        }
        return placements;
    }

    /// <summary>
    /// The circulation layer as it SHIPS.
    ///
    /// `floorUsage` is the semantic band the floor pigment paints: a compacted core at 255 with its graded verge
    /// around it, and the artifact validates the whole band as one connected network. The mask is stamped before
    /// the topology repairs run, so a repair that turns a verge cell into rock leaves the published band describing
    /// ground that is not there — and a handful of stranded verge cells then read as a second, competing road.
    /// Publishing therefore states the band over the raster that actually exists, and drops any fragment too small
    /// to be a road.
    /// </summary>
    // (The TS doc comment above `publishedFloorUsage` also carries a stale block about surface/variant layers and
    // module:terrainSubstrate; it belongs to `surfaceLayers` below.)
    private const int FLOOR_USAGE_MIN_FRAGMENT = 8;

    private static readonly (int dx, int dy)[] FLOOR_USAGE_STEPS = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    private static byte[] publishedFloorUsage(byte[] primaryRouteMask)
    {
        int width = ENDLESS_CHUNK_TILES;
        byte[] usage = primaryRouteMask.slice();
        // The band is NOT clipped to walkable ground. Clipping is locally correct and globally wrong: the two
        // chunks meeting at a seam repair their rasters independently, so one side would erase verge cells the
        // other keeps and the stitched network would show a stranded stub at every such seam. The pigment already
        // paints the band only where there is floor to paint it on.
        var seen = new byte[usage.Length];
        var stack = new List<int>();
        for (int start = 0; start < usage.Length; start++)
        {
            if (seen[start] != 0 || usage[start] == 0) continue;
            stack.Clear();
            stack.push(start);
            seen[start] = 1;
            var cells = new List<int>();
            bool touchesSeam = false;
            while (stack.Count > 0)
            {
                int index = stack.pop();
                cells.push(index);
                int bx = index % width;
                int by = (int)Math.floor((double)index / width);
                if (bx == 0 || by == 0 || bx == width - 1 || by == width - 1) touchesSeam = true;
                int x = index % width;
                int y = (int)Math.floor((double)index / width);
                foreach (var (dx, dy) in FLOOR_USAGE_STEPS)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width) continue;
                    int neighbour = ny * width + nx;
                    if (seen[neighbour] != 0 || usage[neighbour] == 0) continue;
                    seen[neighbour] = 1;
                    stack.push(neighbour);
                }
            }
            // A fragment that reaches a SEAM is continued by the neighbouring chunk and must never be dropped,
            // however few of its cells fall inside this one. A fragment that does not — a short stub stranded in the
            // chunk's interior by a later repair — is not a road, whether or not it happens to carry a core cell:
            // the artery always crosses its chunk, so an interior-only artery stub is by definition a leftover.
            if (touchesSeam || cells.Count >= FLOOR_USAGE_MIN_FRAGMENT) continue;
            foreach (int index in cells) usage[index] = 0;
        }
        return usage;
    }

    /// <summary>
    /// The Country's surface and variant layers.
    ///
    /// The composition RULE now lives in module terrainSurfaceLayers, because the map simulator needs the
    /// identical rule and previously shipped an artifact with no surface layer at all. This function's remaining
    /// job is the part that is genuinely local: naming which of this composer's masks plays which role.
    ///
    /// Open ground states the SUBSTRATE it stands on, from the shared registry (module terrainSubstrate) —
    /// the same field the floor-composition plan paints with, so the surface a footstep queries and the ground a
    /// player sees can never disagree. Before this, four hardcoded biome-key comparisons collapsed eleven authored
    /// worlds into three recipes whose entire vocabulary was Stone plus one alternative: measured over 36 chunks,
    /// 65.9 % of the world was a single material and 10.4 % was the only other one it had.
    /// </summary>
    private static TerrainSurfaceLayers surfaceLayers(
        byte[] tiles,
        byte[] routeMask,
        byte[] primaryRouteMask,
        byte[] courtMask,
        byte[] landmarkMask,
        string biomeKey,
        int baseTx,
        int baseTy,
        byte[]? setPieceClaim)
    {
        return TerrainSurfaceLayersModule.composeTerrainSurfaceLayers(
            tiles,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES,
            biomeKey,
            baseTx,
            baseTy,
            new TerrainSurfaceLayerMasks
            {
                usage = primaryRouteMask,
                route = routeMask,
                court = courtMask,
                landmark = landmarkMask,
                setPieceClaim = setPieceClaim,
            });
    }

    private static int styleForLandscape(string kind) =>
        kind == EndlessLandscapeKind.FloodedCaverns || kind == EndlessLandscapeKind.SinkholeKarst
            ? DungeonStyle.Caves
            : DungeonStyle.Rooms;

    /// <summary>
    /// Give every shipping chunk its two local farming resources after every destructive topology writer has run.
    /// The resource component owns the compact body shape and exact Floor-bank rule; Country composition only
    /// supplies world-stable placement salt, route preference and the elevation lane that must follow a new cell.
    /// </summary>
    private static void guaranteeCountryChunkResources(
        byte[] tiles,
        sbyte[] elevation,
        byte[] routeMask,
        byte[] courtMask,
        byte[] landmarkMask,
        double seed,
        int cx,
        int cy,
        int generationVersion)
    {
        if (generationVersion < ENDLESS_CHUNK_RESOURCES_GENERATION_VERSION) return;
        var protectedMask = new byte[tiles.Length];
        var forbiddenMask = new byte[tiles.Length];
        for (int index = 0; index < protectedMask.Length; index++)
            protectedMask[index] = (byte)(routeMask[index] != 0 || courtMask[index] != 0 || landmarkMask[index] != 0 ? 1 : 0);
        // Cleft and Underpass validate their surrounding shoulders/banks, not only their own typed cells. Keep the
        // complete support neighbourhood out of resource placement so a late pool cannot hollow out a proven bank.
        // Bridges are deliberately handled after resource placement below: reserving every deck's bank can consume
        // all viable sites in a dense chunk, while demoting a newly redundant deck safely rejoins its hazard body.
        for (int index = 0; index < tiles.Length; index++)
        {
            if (tiles[index] != TileType.Cleft && tiles[index] != TileType.Underpass) continue;
            int tx = index % ENDLESS_CHUNK_TILES;
            int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
            for (int dy = -3; dy <= 3; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= ENDLESS_CHUNK_TILES || ny >= ENDLESS_CHUNK_TILES) continue;
                    forbiddenMask[ny * ENDLESS_CHUNK_TILES + nx] = 1;
                }
            }
        }
        var result = TerrainHazardAccess.enforceTerrainChunkResources(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new TerrainChunkResourceOptions
        {
            // `seed ^ imul(...) ^ imul(...)` is a signed int32 in JS (no `>>> 0`).
            seed = Js.ToInt32(seed) ^ Math.imul(cx, 0x51ab3e75) ^ Math.imul(cy, 0x27d4eb2f),
            @protected = protectedMask,
            forbidden = forbiddenMask,
            onPaintCell = (index, _hazard, floorAnchorIndex) =>
            {
                elevation[index] = elevation[floorAnchorIndex];
            },
        });
        if (result.unresolvedWater || result.unresolvedChasm)
            throw new InvalidOperationException(
                $"chunk ({Js.Str(cx)},{Js.Str(cy)}) cannot place directly Floor-accessible Water/Chasm resources");
        demoteNonfunctionalCountryBridges(tiles);
    }

    private static DungeonLayout finalizedCountryLayout(
        double seed,
        int cx,
        int cy,
        string biomeKey,
        int tier,
        byte[] tiles,
        sbyte[] elevation,
        byte[] routeMask,
        byte[] primaryRouteMask,
        byte[] courtMask,
        byte[] landmarkMask,
        IReadOnlyList<EndlessCountryChunkPort> ports,
        ChunkNode node,
        EndlessCountryPlan owner,
        IReadOnlyList<EndlessCountryPlan> landmarkPlans,
        CountryRoomPlan? roomPlan,
        EndlessSetPieceResult? setPieces = null)
    {
        double originX = endlessChunkOriginX(cx);
        double originY = endlessChunkOriginY(cy);
        int baseTx = cx * ENDLESS_CHUNK_TILES;
        int baseTy = cy * ENDLESS_CHUNK_TILES;
        // Wall geology, route repair and final topology can all change a Chasm's rim after the first elevation solve.
        // Enforce the golden five-level clearance against the exact raster/elevation artifact that will ship.
        TerrainKit.assignChasmDepths(tiles, elevation, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, baseTx, baseTy);
        List<DungeonRoom> rooms = roomForChunk(seed, cx, cy, node, originX, originY, roomPlan);
        TerrainSurfaceLayers layers = surfaceLayers(
            tiles,
            routeMask,
            primaryRouteMask,
            courtMask,
            landmarkMask,
            biomeKey,
            baseTx,
            baseTy,
            setPieces?.claim);
        var landmarkMarkers = new List<TerrainMarker>();
        foreach (EndlessCountryPlan plan in landmarkPlans)
        {
            foreach (EndlessCountryLandmarkPlan landmark in plan.landmarks)
            {
                double tx = Math.round(landmark.x - baseTx);
                double ty = Math.round(landmark.y - baseTy);
                if (tx < 0 || ty < 0 || tx >= ENDLESS_CHUNK_TILES || ty >= ENDLESS_CHUNK_TILES) continue;
                landmarkMarkers.push(new TerrainMarker
                {
                    type = TerrainMarkerType.Landmark,
                    tx = (int)tx,
                    ty = (int)ty,
                    id = $"country-v{Js.Str(plan.version)}:{Js.Str(plan.countryX)}:{Js.Str(plan.countryY)}:landmark:{Js.Str(landmark.ordinal)}:{landmark.variant.id}",
                });
            }
        }
        var markers = new List<TerrainMarker>();
        for (int index = 0; index < ports.Count; index++)
        {
            EndlessCountryChunkPort port = ports[index];
            markers.push(new TerrainMarker
            {
                type = TerrainMarkerType.SeamPort,
                tx = port.tx,
                ty = port.ty,
                id = $"country-v{Js.Str(owner.version)}:{Js.Str(cx)}:{Js.Str(cy)}:port:{Js.Str(index)}",
            });
        }
        markers.AddRange(landmarkMarkers);
        // A set piece's own dressing is authored by its stamp and is added to — never replaced by — the
        // ordinary composition, which is told to leave the set piece's cells alone.
        List<TerrainDecorationPlacement> decorations = createCountryDecorations(
            tiles,
            elevation,
            routeMask,
            primaryRouteMask,
            courtMask,
            seed,
            biomeKey,
            cx * ENDLESS_CHUNK_TILES,
            cy * ENDLESS_CHUNK_TILES,
            owner,
            landmarkPlans,
            setPieces?.claim);
        // A set piece's props are validated against the raster that SHIPS. The topology repairs that run
        // after the stamp can turn the ground under one of them into rock or water, and a prop standing on
        // ground its kind cannot grow on is a hard validation failure that costs the whole chunk.
        foreach (TerrainDecorationPlacement decoration in (IReadOnlyList<TerrainDecorationPlacement>?)setPieces?.decorations ?? Array.Empty<TerrainDecorationPlacement>())
        {
            if (
                WorldDecoration.terrainTileAcceptsDecoration(
                    decoration.kind,
                    tiles[decoration.ty * ENDLESS_CHUNK_TILES + decoration.tx]))
                decorations.push(decoration);
        }
        return TerrainArtifactModule.finalizeTerrainLayout(
            new DungeonLayout
            {
                index = endlessChunkKey(cx, cy),
                seed = seed,
                style = styleForLandscape(owner.landscape.primary),
                biomeKey = biomeKey,
                tier = tier,
                tileSize = Grid.TILE_SIZE,
                width = ENDLESS_CHUNK_TILES,
                height = ENDLESS_CHUNK_TILES,
                originX = originX,
                originY = originY,
                tiles = tiles,
                elevation = elevation,
                terrain = new DungeonTerrainLayers
                {
                    schemaVersion = TERRAIN_ARTIFACT_SCHEMA_VERSION,
                    surface = layers.surface,
                    variant = layers.variant,
                    // Floor semantics express navigation hierarchy, not every technically walkable branch. Publishing
                    // local graph lanes and courts as equally brown paths created the disconnected random-route read that
                    // the gameplay corridor was meant to solve. Topology remains untouched; only the one authoritative
                    // Flood/escape artery receives path material, with its own graded verge already baked into this mask.
                    floorUsage = publishedFloorUsage(primaryRouteMask),
                    markers = markers,
                    decorations = decorations,
                },
                rooms = rooms,
                doors = new List<DungeonDoor>(),
                startRoomId = -1,
                bossRoomId = -1,
            },
            new TerrainFinalizeOptions
            {
                context = $"generateEndlessCountryChunk(seed={Js.Str(seed)}, cx={Js.Str(cx)}, cy={Js.Str(cy)})",
                requireElevation = true,
                requireStartBossReachable = false,
                allowPartialSeamBridgeSpans = true,
            });
    }

    public static DungeonLayout generateEndlessCountryFallbackChunkAt(
        double seed,
        int cx,
        int cy,
        string biomeKey,
        int tier,
        double? spineSeed = null,
        int generationVersion = ENDLESS_GENERATION_VERSION)
    {
        double resolvedSpineSeed = spineSeed ?? seed;
        var tiles = new byte[ENDLESS_CHUNK_TILES * ENDLESS_CHUNK_TILES];
        var routeMask = new byte[tiles.Length];
        var primaryRouteMask = new byte[tiles.Length];
        var courtMask = new byte[tiles.Length];
        var landmarkMask = new byte[tiles.Length];
        List<EndlessCountryChunkPort> ports = endlessCountryChunkPortsAt(seed, cx, cy, generationVersion);
        ChunkNode node = carveRouteNetwork(
            tiles,
            routeMask,
            courtMask,
            landmarkMask,
            seed,
            cx,
            cy,
            ports,
            generationVersion,
            // The sparse-route fallback runs without a resolved Country, so it keeps the neutral middle.
            0.5);
        carveFloodSpineRoute(tiles, routeMask, primaryRouteMask, seed, resolvedSpineSeed, cx, cy, node);
        bool boss = floodSpineLandmarkInChunk(resolvedSpineSeed, cx, cy);
        CountryRoomPlan? roomPlan = roomPlanForChunk(seed, cx, cy, node, boss);
        paintRoomPlan(tiles, courtMask, roomPlan);
        if (cx == 0 && cy == 0)
            paintSpawnCourt(
                tiles,
                routeMask,
                courtMask,
                cx * ENDLESS_CHUNK_TILES,
                cy * ENDLESS_CHUNK_TILES);
        int baseTx = cx * ENDLESS_CHUNK_TILES;
        int baseTy = cy * ENDLESS_CHUNK_TILES;
        sbyte[] elevation = TerrainKit.buildStandardElevationField(
            tiles,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES,
            seed,
            biomeKey,
            new StandardElevationOptions
            {
                maxLevel = MAX_ELEVATION,
                minLevel = MIN_ELEVATION,
                baseTx = baseTx,
                baseTy = baseTy,
                protectBoundary = true,
                minPatchLevel = 2,
                levelAt = (x, y) => countryElevationLevelAt(seed, biomeKey, x, y),
                waterLevelAt = (_x, _y, groundLevel) => countryWaterElevationLevelAt(groundLevel),
                wallMassifAt = (x, y) =>
                    Math.min(MAX_ELEVATION, countryElevationLevelAt(seed, biomeKey, x, y) + 6),
            });
        EndlessWallGeology.assignCountryWallHeights(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        EndlessWallGeology.enforceExposedCountryWallHeight(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        EndlessWallGeology.seedWorldAlignedWallGeology(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        guaranteeCountryChunkResources(
            tiles,
            elevation,
            routeMask,
            courtMask,
            landmarkMask,
            seed,
            cx,
            cy,
            generationVersion);
        int countryX = endlessCountryCoordForChunk(cx);
        int countryY = endlessCountryCoordForChunk(cy);
        return finalizedCountryLayout(
            seed,
            cx,
            cy,
            biomeKey,
            tier,
            tiles,
            elevation,
            routeMask,
            primaryRouteMask,
            courtMask,
            landmarkMask,
            ports,
            node,
            endlessCountryPlanFor(seed, countryX, countryY, biomeKey, generationVersion),
            new List<EndlessCountryPlan>(),
            roomPlan);
    }

    private static readonly (int dx, int dy)[] SEAM_NEIGHBOURS =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
        (1, 1),
        (1, -1),
        (-1, 1),
        (-1, -1),
    };

    /// <summary>Generate one immutable Country chunk by clipping its versioned deterministic semantic composition.</summary>
    public static DungeonLayout generateEndlessCountryChunkAt(
        double seed,
        int cx,
        int cy,
        string biomeKey,
        int tier,
        double? spineSeed = null,
        int generationVersion = ENDLESS_GENERATION_VERSION)
    {
        double resolvedSpineSeed = spineSeed ?? seed;
        var tiles = new byte[ENDLESS_CHUNK_TILES * ENDLESS_CHUNK_TILES];
        var routeMask = new byte[tiles.Length];
        var primaryRouteMask = new byte[tiles.Length];
        var courtMask = new byte[tiles.Length];
        var landmarkMask = new byte[tiles.Length];
        int baseTx = cx * ENDLESS_CHUNK_TILES;
        int baseTy = cy * ENDLESS_CHUNK_TILES;
        List<EndlessCountryPlan> plans = plansNearChunk(seed, cx, cy, biomeKey, generationVersion);
        int countryX = endlessCountryCoordForChunk(cx);
        int countryY = endlessCountryCoordForChunk(cy);
        EndlessCountryPlan owner = endlessCountryPlanFor(seed, countryX, countryY, biomeKey, generationVersion);
        List<ChunkReliefFeature> chunkRelief =
            generationVersion >= ENDLESS_LANDMARK_RELIEF_GENERATION_VERSION
                ? createChunkReliefFeatures(seed, biomeKey, cx, cy, owner)
                : new List<ChunkReliefFeature>();
        Func<double, double, double> terrainLevelAt =
            generationVersion >= ENDLESS_LANDMARK_RELIEF_GENERATION_VERSION
                ? (x, y) =>
                {
                    double @base = countryElevationLevelAt(seed, biomeKey, x, y);
                    return landmarkElevationLevelAt(
                        seed,
                        biomeKey,
                        plans,
                        x,
                        y,
                        applyChunkReliefAt(@base, chunkRelief, x, y));
                }
                : (x, y) => countryElevationLevelAt(seed, biomeKey, x, y);
        List<EndlessCountryChunkPort> ports = endlessCountryChunkPortsAt(seed, cx, cy, generationVersion);

        paintLargeShelves(tiles, plans, baseTx, baseTy);
        paintLandmarkStructures(tiles, routeMask, landmarkMask, plans, baseTx, baseTy);
        ChunkNode node = carveRouteNetwork(
            tiles,
            routeMask,
            courtMask,
            landmarkMask,
            seed,
            cx,
            cy,
            ports,
            generationVersion,
            // `pathWander` is authored for every setting but stays unwired: routing it measurably nudged the
            // one-level wall-stripe contract, which already sits within a thousandth of its ceiling. Landing it needs
            // that contract re-derived first, not a route grammar tuned to squeeze under it.
            0.5);
        carveFloodSpineRoute(tiles, routeMask, primaryRouteMask, seed, resolvedSpineSeed, cx, cy, node);
        bool boss = floodSpineLandmarkInChunk(resolvedSpineSeed, cx, cy);
        CountryRoomPlan? roomPlan = roomPlanForChunk(seed, cx, cy, node, boss);
        paintRoomPlan(tiles, courtMask, roomPlan);
        if (cx == 0 && cy == 0) paintSpawnCourt(tiles, routeMask, courtMask, baseTx, baseTy);
        byte[]? hydrologyFallback =
            generationVersion >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION ? tiles.slice() : null;
        paintHydrology(tiles, routeMask, courtMask, landmarkMask, plans, baseTx, baseTy);
        if (generationVersion >= ENDLESS_FUNCTIONAL_TERRAIN_GENERATION_VERSION)
            TerrainBridgeRescue.rescueFunctionalWaterBridge(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new FunctionalBridgeRescueOptions
            {
                startX = node.tx,
                startY = node.ty,
                seed = seed,
                salt = Math.imul(cx, 0x27d4eb2f) ^ Math.imul(cy, 0x165667b1),
                routeMask = routeMask,
                maxSpan = 10,
                minimumRescuedAreaCells = 18,
                minimumUsefulDetour = 30,
            });
        EndlessCountryTopology.dropDisconnectedWalkable(tiles, node.tx, node.ty);
        demoteNonfunctionalCountryBridges(tiles);
        // V5 only: historical generations keep their exact raster, warts and all.
        if (generationVersion >= ENDLESS_DISTRICT_GENERATION_VERSION)
            EndlessCountryTopology.trimShallowDeadEnds(tiles, routeMask, courtMask);
        // The Flood spine is the authoritative escape artery, not an optional local maze branch. A Country's
        // disconnected-component/dead-end cleanup can classify a route fragment that only grazes its streamed edge
        // before the neighbouring chunk exists as a disposable local tail. Re-stamping the same deterministic curve
        // after every destructive topology pass is idempotent for ordinary cells and restores the complete body-wide
        // seam collar plus its node junction before elevation/collision are finalized.
        carveFloodSpineRoute(tiles, routeMask, primaryRouteMask, seed, resolvedSpineSeed, cx, cy, node);
        CountryCleftSocket? cleftSocket = paintCountryCleftSocket(tiles, routeMask, courtMask, seed, cx, cy);

        sbyte[] elevation = TerrainKit.buildStandardElevationField(
            tiles,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES,
            seed,
            biomeKey,
            new StandardElevationOptions
            {
                maxLevel = MAX_ELEVATION,
                minLevel = MIN_ELEVATION,
                baseTx = baseTx,
                baseTy = baseTy,
                protectBoundary = true,
                minPatchLevel = 2,
                levelAt = (x, y) => terrainLevelAt(x, y),
                waterLevelAt = (x, y, groundLevel) =>
                    mountainStreamSampleForPlans(plans, x, y)?.level ??
                    countryWaterElevationLevelAt(groundLevel),
                wallMassifAt = (x, y) => Math.min(MAX_ELEVATION, terrainLevelAt(x, y) + 6),
            });
        EndlessWallGeology.assignCountryWallHeights(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        // A depth structure is carved against the wall heights that SHIP, not against a draft of them. An underpass
        // requires a guaranteed bank clearance above the floor it tunnels under, and that clearance is exactly what
        // the exposed-wall minimum face guarantees — so the enforcement has to have run before the structures are
        // evaluated. It only ever raises a wall, so running it here and again after the topology repairs below is
        // idempotent for every cell those repairs do not touch.
        EndlessWallGeology.enforceExposedCountryWallHeight(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        // The one thing in a run that is unmistakably BUILT — see module endlessSetPiece.
        //
        // It runs HERE, on the finished landform but BEFORE the quality and repair passes, and the ordering is the
        // whole point: a wonder writes real terrain, so every pass that follows has to treat it as terrain. Placed
        // after them instead — which was measured — the depth-tile pass never saw its rock, the chasm topology never
        // saw its rift, the wall-detail repair left its mass as one undifferentiated slab, and the dead-end trim
        // never reached the tips its carving left behind: eleven separate contracts went red at once, all of them
        // saying the same thing in different words.
        EndlessSetPieceResult setPieces = EndlessSetPiece.buildEndlessSetPieces(
            tiles,
            elevation,
            primaryRouteMask,
            seed,
            biomeKey,
            baseTx,
            baseTy);
        if (setPieces.decorations.Count > 0)
        {
            // Steps FIRST. A stamp carves terraces and then has its walkable cells capped to the land's own ceiling,
            // either of which can leave a neighbour more than one level away — and a cell that cannot be climbed to is
            // a hard artifact failure, not a cosmetic one. Relaxing before the connectivity sweep also means the sweep
            // judges the raster the player will actually walk.
            TerrainKit.relaxWalkableElevationSteps(
                tiles,
                elevation,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                MAX_ELEVATION,
                null,
                MIN_ELEVATION);
            EndlessCountryTopology.trimShallowDeadEnds(tiles, routeMask, courtMask);
            // No artery protection on the CLIMB-aware sweep. Protecting an artery cell from demotion keeps it in the
            // raster even when nothing can climb to it, which is a hard artifact failure rather than a preserved
            // route; the spine is re-stamped at the end of this function and restores the line through ground that
            // actually exists.
            EndlessCountryTopology.dropDisconnectedWalkable(tiles, node.tx, node.ty, null, elevation);
        }
        if (cx != 0 || cy != 0)
        {
            TerrainDepthTiles.applyTerrainDepthTiles(
                tiles,
                elevation,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                baseTx,
                baseTy,
                seed,
                biomeKey
                // NOTE: `{ protect: setPieces.claim }` is deliberately NOT passed here. The mask keeps clefts and
                // underpasses off ground a set piece owns, but supplying it would re-cut existing seeds' depth
                // structures.
                );
        }
        // Depth materialization can demote a final bridge after paintHydrology's own topology pass. Recheck against
        // the exact pre-hydrology raster so the split-off surface cannot ship as a two-cell seam-side puddle.
        if (hydrologyFallback != null)
            TerrainWater.enforceTerrainWaterTopology(tiles, hydrologyFallback, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
        // The final water-topology repair can remove one side of an otherwise functional deck. Re-apply the width
        // contract against the exact shipping raster and copy the retained deck datum into any widened cells.
        TerrainBridge.enforceMinimumBridgeThickness(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new BridgeThicknessOptions
        {
            elevation = elevation,
            corridorMask = routeMask,
            maxPasses = 12,
        });
        // ...and re-prove that every remaining deck still spans something. The functional check above ran before
        // the water repair and this widening, either of which can leave a plank sitting on dry ground — a deck
        // spanning nothing is a rendering artifact, not a crossing. Same principle as the water and void repairs
        // around it: validate the raster that actually ships, not an earlier draft of it.
        demoteNonfunctionalCountryBridges(tiles);
        // Depth materialization may lower an approach after the initial height solve. Finalize the optional cleft
        // afterwards so its two approaches and shoulders are validated against the artifact that actually ships.
        prepareCountryCleftSocket(elevation, cleftSocket);
        materializeCountryCleftSocket(tiles, elevation, cleftSocket);
        // Water topology is already re-proved against the shipping raster; the void deserves the same. Bridge
        // widening and the cleft socket can edit the raster AFTER `paintHydrology` ran its topology check, leaving
        // a sub-readable Chasm remnant. Restore rejected void cells together with their world-aligned height, then
        // relax the walkable neighbourhood before geology materializes elevation differences into cliff walls.
        if (hydrologyFallback != null)
        {
            var chasmRepair = TerrainChasm.enforceTerrainChasmTopology(
                tiles,
                hydrologyFallback,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                new TerrainChasmTopologyOptions
                {
                    onRestoreCell = (index, _restoredTile) =>
                    {
                        int tx = index % ENDLESS_CHUNK_TILES;
                        int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
                        elevation[index] = Js.I8(terrainLevelAt(baseTx + tx, baseTy + ty));
                    },
                });
            if (chasmRepair.removedCells > 0)
                TerrainKit.relaxWalkableElevationSteps(
                    tiles,
                    elevation,
                    ENDLESS_CHUNK_TILES,
                    ENDLESS_CHUNK_TILES,
                    MAX_ELEVATION,
                    null,
                    MIN_ELEVATION);
        }
        EndlessWallGeology.enforceExposedCountryWallHeight(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        EndlessWallGeology.seedWorldAlignedWallGeology(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        EndlessWallGeology.enforceLargeWallDetailCoverage(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        // Geological shelves can change the independent anchor-bank heights used by an underpass or cleft. Validate
        // those authored movement artifacts against the final wall composition, then re-apply the exposed minimum;
        // demotion preserves walkability and therefore cannot invalidate the already-solved 10x10 wall contract.
        demoteInvalidCountryUnderpasses(tiles, elevation);
        demoteInvalidCountryClefts(tiles, elevation);
        EndlessWallGeology.enforceExposedCountryWallHeight(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        // The one thing in a run that is unmistakably BUILT — see module endlessSetPiece. It runs last, on
        // the finished landform, because a wonder is carved into real terrain: it needs the heights and the
        // hydrology it will actually stand on. Everything it writes is inside this chunk with a seam margin, so no
        // neighbour has to agree with it about anything.
        // A set piece's terrain travels through every repair below it, and those repairs can strand a pocket that
        // was connected when the stamp wrote it — a crater rim closed by a later chasm repair, for instance. One
        // final connectivity sweep, only for the chunks that actually built something, keeps the artifact's
        // "one climbable component" contract true of the raster that ships.
        demoteInvalidCountryUnderpasses(tiles, elevation);
        demoteInvalidCountryClefts(tiles, elevation);
        // THE CLIMBABILITY SWEEP, for every chunk.
        //
        // The artifact's own contract is that every walkable cell is reachable in one-level steps, and until now
        // nothing enforced it: `dropDisconnectedWalkable` floods plain walkable adjacency, so a pocket behind a
        // two-level lip survives the sweep and then fails validation, costing the entire chunk. Relief banding made
        // that rare case reachable — a broader shelf meets its neighbour across a taller riser — and a set piece can
        // carve one directly. Relaxing the final raster and then flooding it the way the validator does is the
        // general repair the generator was missing; every cell it demotes was already invalid.
        TerrainKit.relaxWalkableElevationSteps(
            tiles,
            elevation,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES,
            MAX_ELEVATION,
            null,
            MIN_ELEVATION);
        EndlessCountryTopology.dropDisconnectedWalkable(tiles, node.tx, node.ty, null, elevation);
        // The sweep can demote a deck's approach, and a deck spanning nothing is a rendering artifact. Re-prove the
        // crossing contract against the raster the sweep left, exactly as every other destructive pass here does.
        TerrainBridge.enforceMinimumBridgeThickness(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new BridgeThicknessOptions
        {
            elevation = elevation,
            corridorMask = routeMask,
            maxPasses = 12,
        });
        demoteNonfunctionalCountryBridges(tiles);
        // ...and the same for the water. The sweep turns walkable cells into rock, which can cut a surface into a
        // fragment under the shared minimum; the topology pass is the one place that rule lives, so it gets the
        // last word on the raster that ships rather than on the draft it saw earlier.
        if (hydrologyFallback != null)
            TerrainWater.enforceTerrainWaterTopology(tiles, hydrologyFallback, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
        if (hydrologyFallback != null)
            TerrainChasm.enforceTerrainChasmTopology(
                tiles,
                hydrologyFallback,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                new TerrainChasmTopologyOptions
                {
                    onRestoreCell = (index, _restoredTile) =>
                    {
                        int tx = index % ENDLESS_CHUNK_TILES;
                        int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
                        elevation[index] = Js.I8(terrainLevelAt(baseTx + tx, baseTy + ty));
                    },
                });
        // The artery is re-stamped afterwards rather than protected during the sweep: protecting a cell nothing can
        // climb to preserves the failure, while re-carving restores the line through ground that actually exists.
        carveFloodSpineRoute(tiles, routeMask, primaryRouteMask, seed, resolvedSpineSeed, cx, cy, node);
        // Re-stamping the artery may split a surface body it crosses. Validate the resulting shipping raster, not
        // the pre-artery draft, so a clipped seam-side puddle cannot survive as an isolated two-cell fragment.
        if (hydrologyFallback != null)
            TerrainWater.enforceTerrainWaterTopology(tiles, hydrologyFallback, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
        if (hydrologyFallback != null)
            TerrainChasm.enforceTerrainChasmTopology(
                tiles,
                hydrologyFallback,
                ENDLESS_CHUNK_TILES,
                ENDLESS_CHUNK_TILES,
                new TerrainChasmTopologyOptions
                {
                    onRestoreCell = (index, _restoredTile) =>
                    {
                        int tx = index % ENDLESS_CHUNK_TILES;
                        int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
                        elevation[index] = Js.I8(terrainLevelAt(baseTx + tx, baseTy + ty));
                    },
                });
        // A readable body of water/void is still unusable when a wall mass seals every bank. V10 gives each exact
        // component the shortest deterministic one-tile approach to existing walkable terrain. This runs after
        // the final artery/topology writers and before the final grade solve, so the passage both survives and is
        // made climbable without weakening the mountain around it.
        if (generationVersion >= ENDLESS_ACCESSIBLE_HYDROLOGY_GENERATION_VERSION)
            TerrainHazardAccess.enforceTerrainHazardAccess(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new TerrainHazardAccessOptions
            {
                // Seam components are attempted too. A chunk only edits its own Solid cells and accepts a repair when
                // that local rock really reaches existing walkable terrain; unknowable/unresolvable continuations are
                // left untouched for another participating chunk instead of inventing isolated floor at the seam.
                // Start the new passage on its existing walkable anchor's exact grade. The following relaxation may
                // lower it into a ramp, but the compiler can never rediscover a hidden cliff and wall the access shut.
                onCarveCell = (index, walkableAnchorIndex) =>
                {
                    elevation[index] = elevation[walkableAnchorIndex];
                },
            });
        if (generationVersion >= ENDLESS_SEAM_ACCESS_GENERATION_VERSION)
            restoreCountrySeamPortAccess(tiles, elevation, routeMask, ports);
        // Every late topology writer above is local to one immutable chunk. Re-anchor the walkable outer ring to
        // the shared analytic field before the last relaxation, otherwise a low exact-footprint or new tributary in
        // one chunk can pull its seam cell down independently while its neighbour keeps the world datum. The ring is
        // protected during relaxation; the six-cell protected apron in the initial height solve has already kept
        // its interior neighbour within one level of this same field.
        for (int edge = 0; edge < ENDLESS_CHUNK_TILES; edge++)
        {
            foreach (int index in new[]
            {
                edge,
                (ENDLESS_CHUNK_TILES - 1) * ENDLESS_CHUNK_TILES + edge,
                edge * ENDLESS_CHUNK_TILES,
                edge * ENDLESS_CHUNK_TILES + ENDLESS_CHUNK_TILES - 1,
            })
            {
                if (!isWalkable(tiles[index])) continue;
                int tx = index % ENDLESS_CHUNK_TILES;
                int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
                elevation[index] = Js.I8(terrainLevelAt(baseTx + tx, baseTy + ty));
            }
        }
        // The artery is the final writer of walkable cells. Reconcile its heights after both it and the water repair
        // have finished; otherwise a freshly carved route cell can remain two levels from the already validated
        // component and force the complete chunk into the sparse fallback at publication time.
        TerrainKit.relaxWalkableElevationSteps(
            tiles,
            elevation,
            ENDLESS_CHUNK_TILES,
            ENDLESS_CHUNK_TILES,
            MAX_ELEVATION,
            null,
            MIN_ELEVATION);
        // The unconstrained solve above may pull an edge down again to satisfy a local authored low. Raise that
        // artificial depression back toward the exact seam datum and propagate the required one-level ramp inward.
        // The propagation raises only a neighbour that would otherwise be more than one level below an already
        // anchored cell. It therefore builds the smallest possible inward ramp. Both chunks publish the same edge
        // height while the complete local component remains climbable, including biome profiles whose raw diagonal
        // samples can cross two integer bands at once.
        var seamInfluence = new byte[tiles.Length];
        for (int edge = 0; edge < ENDLESS_CHUNK_TILES; edge++)
        {
            foreach (int index in new[]
            {
                edge,
                (ENDLESS_CHUNK_TILES - 1) * ENDLESS_CHUNK_TILES + edge,
                edge * ENDLESS_CHUNK_TILES,
                edge * ENDLESS_CHUNK_TILES + ENDLESS_CHUNK_TILES - 1,
            })
            {
                if (!isWalkable(tiles[index])) continue;
                int tx = index % ENDLESS_CHUNK_TILES;
                int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
                double seamLevel = terrainLevelAt(baseTx + tx, baseTy + ty);
                if (elevation[index] >= seamLevel) continue;
                elevation[index] = Js.I8(seamLevel);
                seamInfluence[index] = 1;
            }
        }
        for (int pass = 0; pass <= MAX_ELEVATION - MIN_ELEVATION; pass++)
        {
            bool changed = false;
            for (int index = 0; index < tiles.Length; index++)
            {
                if (seamInfluence[index] == 0) continue;
                int tx = index % ENDLESS_CHUNK_TILES;
                int ty = (int)Math.floor((double)index / ENDLESS_CHUNK_TILES);
                int minimumNeighbour = elevation[index] - 1;
                foreach (var (dx, dy) in SEAM_NEIGHBOURS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= ENDLESS_CHUNK_TILES || ny >= ENDLESS_CHUNK_TILES) continue;
                    int neighbour = ny * ENDLESS_CHUNK_TILES + nx;
                    if (!isWalkable(tiles[neighbour]) || elevation[neighbour] >= minimumNeighbour)
                        continue;
                    int raised = minimumNeighbour;
                    if (raised <= elevation[neighbour]) continue;
                    elevation[neighbour] = Js.I8(raised);
                    seamInfluence[neighbour] = 1;
                    changed = true;
                }
            }
            if (!changed) break;
        }
        EndlessWallGeology.enforceExposedCountryWallHeight(tiles, elevation, seed, biomeKey, baseTx, baseTy);
        // The artery and final grade relaxation are allowed to change the last approach cells. Re-prove authored
        // vertical crossings once more against the exact raster that is about to be published; an invalid proposal
        // becomes ordinary traversable terrain instead of discarding an otherwise healthy chunk.
        demoteInvalidCountryUnderpasses(tiles, elevation);
        demoteInvalidCountryClefts(tiles, elevation);
        try
        {
            // RESOURCE GATE. Its internal bridge cleanup may only merge a redundant deck back into a hazard body;
            // the bridge budget below can prune only walkable islands which its own Water reveal made unreachable.
            guaranteeCountryChunkResources(
                tiles,
                elevation,
                routeMask,
                courtMask,
                landmarkMask,
                seed,
                cx,
                cy,
                generationVersion);
            // Bridge pacing is evaluated against the FINAL raster, after route re-stamps, grade repair and resources.
            // Doing this on the hydrology draft let later writers turn the retained span back into a redundant crossing
            // and remove it again. The final gate can therefore prove both its strict regional budget and its actual
            // bank-to-bank utility without guessing what subsequent passes will publish.
            if (generationVersion >= ENDLESS_BRIDGE_BUDGET_GENERATION_VERSION)
            {
                var bridgeBudget = EndlessBridgeBudget.applyEndlessBridgeBudget(
                    tiles,
                    ENDLESS_CHUNK_TILES,
                    ENDLESS_CHUNK_TILES,
                    new EndlessBridgeBudgetOptions
                    {
                        seed = seed,
                        cx = cx,
                        cy = cy,
                        startX = node.tx,
                        startY = node.ty,
                        routeMask = routeMask,
                        generationVersion = generationVersion,
                    });
                // Entitled chunks without an old route-painted footprint may still own one honest crossing in their final
                // water body. This selector is capped to the same dimensions and only runs when the footprint gate added
                // nothing, so one budget can never multiply into several deck components.
                if (bridgeBudget.entitled && !bridgeBudget.added)
                    TerrainBridgeRescue.rescueFunctionalWaterBridge(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES, new FunctionalBridgeRescueOptions
                    {
                        startX = node.tx,
                        startY = node.ty,
                        seed = seed,
                        salt = Math.imul(cx, 0x27d4eb2f) ^ Math.imul(cy, 0x165667b1),
                        routeMask = routeMask,
                        allowWalkableBanks = true,
                        maxSpan = 8,
                        minimumBarrierBodyCells = 24,
                        minimumRescuedAreaCells = 18,
                        minimumUsefulDetour = 24,
                    });
                // A Water candidate carries the stream's stored datum until it becomes a deck. Reconcile that new
                // walkable surface with both banks before the artifact compiler performs its own climbability materialize;
                // otherwise a valid topology can lose one landing to a two-level step during finalization.
                TerrainKit.relaxWalkableElevationSteps(
                    tiles,
                    elevation,
                    ENDLESS_CHUNK_TILES,
                    ENDLESS_CHUNK_TILES,
                    MAX_ELEVATION,
                    null,
                    MIN_ELEVATION);
                // Retiring a legacy multi-bank platform can expose one real carrier while leaving a tiny side landing
                // which no longer belongs to the chunk's navigable component. It is invalid terrain, not a route worth
                // preserving: apply the same climb-aware publication invariant used above before bridge closure is
                // re-proved. This prevents a two-cell orphan from rejecting the otherwise healthy chunk into fallback.
                EndlessCountryTopology.dropDisconnectedWalkable(tiles, node.tx, node.ty, null, elevation);
                TerrainBridge.demoteIncompleteFiniteBridgeComponents(tiles, ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_TILES);
                // A retired platform can also have served as one of an optional depth structure's sight approaches.
                // Revalidate those structures against the budgeted raster; demotion preserves ordinary walkability and
                // is preferable to publishing a cleft or underpass whose authored shoulders no longer exist.
                demoteInvalidCountryUnderpasses(tiles, elevation);
                demoteInvalidCountryClefts(tiles, elevation);
            }
            return finalizedCountryLayout(
                seed,
                cx,
                cy,
                biomeKey,
                tier,
                tiles,
                elevation,
                routeMask,
                primaryRouteMask,
                courtMask,
                landmarkMask,
                ports,
                node,
                owner,
                plans,
                roomPlan,
                setPieces);
        }
        catch (Exception error)
        {
            JsConsole.error(
                $"[endless-country-v{Js.Str(generationVersion)}] chunk ({Js.Str(cx)},{Js.Str(cy)}) of seed {Js.Str(seed)} failed terrain validation - emitting sparse-route fallback:",
                error.Message);
            return generateEndlessCountryFallbackChunkAt(
                seed,
                cx,
                cy,
                biomeKey,
                tier,
                resolvedSpineSeed,
                generationVersion);
        }
    }

    /// <summary>
    /// Consume the RNG draws the retired encounter roll used to make.
    ///
    /// A seeded generator's output depends on the SEQUENCE of draws, not only on the values anything reads: every
    /// later decision in a chunk continues the stream where the previous one left it. Deleting the encounter roll
    /// therefore did not merely drop a label — it shifted the whole stream, and the same seed began producing a
    /// different, untuned landform. The property tests that guard this generator (valleys and peaks both exist,
    /// water decks reach two distinct banks, maze density, perceptual variety) all failed as a result.
    ///
    /// The encounter itself is gone. Its two draws stay — `rng.bool()` for the `risky` argument, which was
    /// evaluated before the call, then the single `rng.next()` that `Rng.weighted` spends inside — so this
    /// generator produces exactly the terrain it was authored and tuned to produce.
    /// </summary>
    private static double? consumeRetiredEncounterDraws(Rng rng)
    {
        rng.@bool();
        rng.next();
        return null;
    }
}
