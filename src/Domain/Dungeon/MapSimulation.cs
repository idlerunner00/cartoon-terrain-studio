// Port of packages/shared/src/domain/dungeon/mapSimulation.ts — keep in lockstep with the original.
// The composer is split over MapSimulation.cs (module surface, roll, pump, bedrock), MapSimulation.Terrain.cs
// (relief, hydrology, rifts, crossings), MapSimulation.Places.cs (wonders, roads, theming, flora,
// landmarks) and MapSimulation.Finalize.cs (finalize, repairs, shared helpers); the method order inside each
// file follows the original.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * **Simulate Map** — compose one complete, high-quality random world as a TerrainArtifact, together with the
 * ordered *layer plan* that lets a client watch it being built.
 *
 * The quality bar is the authored Tutorial map and the Hub: a simulated map is a composed *place* with a
 * readable silhouette, a terraced landform, a hydrology, crossings, wonders, a circulation network, theme
 * regions and a real flora budget — not noise in a rectangle. It reaches that bar by reusing the
 * systems that already produce shipping worlds rather than inventing a second terrain generator:
 *
 *  - **bedrock** is the endless landform (generateEndlessChunkAt) — macro DNA, country grammar and all the
 *    tuning behind the ten runs — cropped into the map frame,
 *  - **relief** bands that landform into walkable shelves and lets the shared terrain contract materialize a
 *    rock cliff at every band edge, which is what turns a heightmap into a landform you can read at a glance,
 *  - **hydrology / rifts / crossings** run through the shared terrain kit's repair chain, the exact same one
 *    every authored finite world (arena, lobby, raids) is finalized with,
 *  - **wonders** are stamped from MAP_WONDERS by the one shared form machine (stampMapWonder) — identity is
 *    data, the carving is a platform,
 *  - **roads / theming / flora / landmarks** are composition passes over that terrain, written
 *    into the artifact's own authored layers, so what the editor exports is what the game renders.
 *
 * Composition identity is data: MAP_SIMULATION_ARCHETYPES and MAP_WONDERS. Adding a new kind of world, or a
 * new kind of monument, is a registry entry — never a branch in here.
 *
 * The build is **step-sliced**: beginMapSimulation returns a pump the caller drives with a per-frame time
 * budget, so generating a 320x240 world never blocks a frame. It is otherwise completely pure — same options
 * in, byte-identical world out, on server and client alike (no Math.random).
 *
 * PORT NOTE (threading): all composition state lives in one MapComposer instance per build; the module holds
 * only immutable tables, so builds may run concurrently on background threads.
 */
public static partial class MapSimulation
{
    /// <summary>Chunk quantum of the endless landform the bedrock pass samples.</summary>
    private const int BEDROCK_CHUNK = 32;
    /// <summary>
    /// How many endless chunks one composition may survey.
    ///
    /// Generating an endless chunk runs the whole country/macro/landscape/maze pipeline — in the game that work
    /// happens in a worker, and it is by far the most expensive thing this composer does (a 320x240 map tiled at
    /// 1:1 spends sixteen seconds here and nowhere else). Above this budget the crop is sampled at a coarser
    /// lattice and scaled up, which costs the landform none of its character: a composed map wants BIGGER
    /// landforms than an endless run's, not more of them.
    /// </summary>
    private const int MAX_BEDROCK_CHUNKS = 12;
    /// <summary>Deck thickness of an authored crossing — the terrain contract's minimum readable deck.</summary>
    private const int DECK_THICKNESS = 2;
    /// <summary>Flat datum the bedrock silhouette is drawn at before the relief pass raises the land.</summary>
    private const int BEDROCK_FLAT_LEVEL = 2;
    /// <summary>Ceiling for every composed elevation field. Walls and summits are clamped into this band.</summary>
    private const int SIM_MAX_LEVEL = MAX_ELEVATION;
    /// <summary>Widest border ring that always stays solid rim, whatever the frame does.</summary>
    private const int RIM_CELLS = 1;
    /// <summary>Open clearance (cells) a plain needs before a free-standing outcrop may be set into it.</summary>
    private const int OUTCROP_MIN_CLEARANCE = 4;
    /// <summary>How often a world is built on an explicit macro layout rather than pure landform, at chaos 0.</summary>
    private const double MOTIF_BASE_CHANCE = 0.45;
    /// <summary>Additional motif chance at chaos 1 — a wild dial should mostly produce worlds with stated bones.</summary>
    private const double MOTIF_CHAOS_CHANCE = 0.42;
    /// <summary>Share of the pre-motif walkable world a stamp must leave behind, or its work is reverted wholesale.</summary>
    private const double MOTIF_MIN_SURVIVING_SHARE = 0.34;
    /// <summary>Absolute circulation floor: a motif may not turn a naturally tight map into a mostly blocked diorama.</summary>
    private const double MOTIF_MIN_WALKABLE_SHARE = 0.16;
    /// <summary>Massif-field wavelength as a share of the map's short axis — how broad one range of rock reads.</summary>
    private const double WALL_MASSIF_SCALE = 0.21;
    /// <summary>Half-extent (cells) of the protected plaza around the map's heart.</summary>
    private const int HEART_PLAZA = 3;
    /// <summary>
    /// Scale applied to a theme's depth-tile density when the promotion sweeps a whole composed map at once rather
    /// than a streamed 64-cell chunk. Holds Cleft + Underpass inside the shared ≤2% combined footprint budget even
    /// on the densest grid compositions, and widens the spacing field so bridges read as landmarks, not as fencing.
    /// </summary>
    private const double MAP_DEPTH_TILE_DENSITY = 0.3;
    /// <summary>
    /// STUDIO: the level a flat world (Mountains dial at zero) stands on — the base shelf of the terrace pass, so the
    /// flattest banded world and a dial-zero world meet at the same height.
    /// </summary>
    private const int FLAT_GROUND_LEVEL = 1;
    /// <summary>STUDIO: at the Mountains dial's top the rock grows by this share of its natural mass.</summary>
    private const double ROCK_GROWTH_AT_FULL_DIAL = 0.45;
    /// <summary>STUDIO: how far (cells) rock may grow out of a mass, and the open half-width a passage keeps.</summary>
    private const int ROCK_GROWTH_REACH = 3;
    private const int ROCK_GROWTH_KEEP_OPEN = 4;
    /// <summary>
    /// STUDIO: the wonder forms built at ground level. A flat world (Mountains at zero) raises only these; every other
    /// form is carved from rock or height.
    /// </summary>
    private static readonly HashSet<string> FLAT_WONDER_FORMS = new()
    {
        MapWonderForm.Causeway,
        MapWonderForm.Springs,
        MapWonderForm.SacredGrove,
    };
    /// <summary>
    /// Authored runtime of the whole build cinematic at 1x, in seconds.
    ///
    /// The composer distributes this across the layers in proportion to what each one actually does, so the film
    /// is the same length whatever the map, and no layer can ever hold the screen while doing nothing.
    /// </summary>
    public const double MAP_SIMULATION_RUNTIME_SECONDS = 17;
    /// <summary>Floor for a layer that did *something*, so a two-cell beat still registers as a beat.</summary>
    private const double MIN_BEAT_SECONDS = 0.55;
    /// <summary>A placement is visually heavier than a terrain cell: it is a whole new actor, not a re-bake.</summary>
    private const double PLACEMENT_WORK_WEIGHT = 3;

    private sealed class StageCopy
    {
        public readonly string title;
        public readonly string detail;
        public readonly double share;

        public StageCopy(string title, string detail, double share)
        {
            this.title = title;
            this.detail = detail;
            this.share = share;
        }
    }

    /// <summary>Only ever looked up by key, never iterated.</summary>
    private static readonly Dictionary<string, StageCopy> STAGE_COPY = new()
    {
        [MapSimulationLayer.Bedrock] = new StageCopy("Bedrock", "The landmass takes its outline", 1.15),
        [MapSimulationLayer.Relief] = new StageCopy("Relief", "Shelves and escarpments rise out of the plate", 1.1),
        [MapSimulationLayer.Hydrology] = new StageCopy("Hydrology", "Water finds the low country", 1),
        [MapSimulationLayer.Rifts] = new StageCopy("Rifts", "The ground tears open", 1),
        [MapSimulationLayer.Crossings] = new StageCopy("Crossings", "Spans stitch the world back together", 1.35),
        [MapSimulationLayer.Wonders] = new StageCopy("Wonders", "Great landforms take their place", 1.7),
        [MapSimulationLayer.Roads] = new StageCopy("Roads", "Roads and trails find their line", 1.35),
        [MapSimulationLayer.Theming] = new StageCopy("Theming", "Regions claim their identity", 0.9),
        [MapSimulationLayer.Flora] = new StageCopy("Flora", "Groves and undergrowth take root", 1.25),
        [MapSimulationLayer.Landmarks] = new StageCopy("Landmarks", "Solitary crowns mark the map", 1.1),
    };

    /// <summary>
    /// One map's composition roll: the archetype's data after bounded per-map variation.
    ///
    /// The registry states an archetype's *identity*; this states one particular world of that identity. Every
    /// scalar moves inside a band whose WIDTH is the chaos dial, the frame is drawn from the archetype's set, and
    /// the endless crop is taken from a rolled origin and orientation — so two Highland Basins share a character
    /// without sharing a coastline, a water budget, or the corner of the endless stream they were cut from.
    /// </summary>
    private sealed class CompositionRoll
    {
        public string frame = MapFrameKind.Open;
        public double frameDepth;
        public double frameNoise;
        public double openness;
        public double reliefBias;
        public int terraceBands;
        public int terraceStep;
        public double terraceNoise;
        /// <summary>Average levels a wall stands above the ground it borders — the world's depth.</summary>
        public double wallRelief;
        /// <summary>The macro layout stamped over the relief, or null for a world that is pure landform.</summary>
        public MapLandformMotif? motif;
        public double waterBudget;
        public double riftBudget;
        /// <summary>
        /// STUDIO: the water and rift budgets with their dials at 1. The bedrock pass sizes its open ground from these,
        /// so turning a hazard dial adds or removes that hazard instead of re-cutting the whole landform.
        /// </summary>
        public double waterBase;
        public double riftBase;
        public double floraDensity;
        public double canopyShare;
        public double landmarkDensity;
        public double wonderDensity;
        public double roadDensity;
        public int tier;
        /// <summary>Chunk-space origin the bedrock crop is taken from.</summary>
        public int chunkX;
        public int chunkY;
        /// <summary>Quarter-turns applied to the crop, then an optional mirror.</summary>
        public int turns;
        public bool mirror;
    }

    /// <summary>One straight, deck-thick span across water or rift, with walkable land at both ends.</summary>
    private sealed class TerrainCrossing
    {
        /// <summary>'x' | 'y'.</summary>
        public char axis;
        /// <summary>Lane coordinate of the deck's first row/column.</summary>
        public int lane;
        /// <summary>First spanning step along the lane.</summary>
        public int start;
        public int length;
        public int bankA;
        public int bankB;
    }

    /// <summary>`laneState` results: 'land' | 'span' | 'blocked'.</summary>
    private const int LANE_LAND = 0;
    private const int LANE_SPAN = 1;
    private const int LANE_BLOCKED = 2;

    private static int clampInt(double value, double lo, double hi)
    {
        double v = Math.round(value);
        return (int)(v < lo ? lo : v > hi ? hi : v);
    }

    /// <summary>
    /// Themes the map generator offers — **read from the theme registry**, never restated.
    ///
    /// The ancestor project derived this list from its run rotation, each run owning a theme. When runs were
    /// removed the list was briefly hand-written, and every hand-written key but one named a theme that exists
    /// in no registry: those worlds silently degraded to the neutral landform, so five of six "themes" the
    /// generator offered rendered as the same nothing. Deriving it from ENDLESS_DRESSING_THEMES is the only
    /// version that cannot drift — a theme is offered exactly when its content exists (CLAUDE.md §4/§6).
    /// </summary>
    public static IReadOnlyList<string> generatorThemeKeys()
    {
        var keys = new List<string>();
        foreach (string key in EndlessDressing.ENDLESS_DRESSING_THEMES.keys())
            if (!EndlessDressing.GENERATOR_ONLY_DRESSING_THEMES.has(key)) keys.Add(key);
        return keys;
    }

    /// <summary>A bias input is a 0..2 multiplier; anything else means "leave the archetype alone".</summary>
    private static double bias(double? value)
    {
        return value.HasValue && Number.isFinite(value.Value) ? Math.max(0, Math.min(2, value.Value)) : 1;
    }

    /// <summary>
    /// STUDIO: how many terrace bands the Mountains dial keeps. Below 1 the bands fade out with the dial (0 is one flat
    /// shelf); above 1 it is the original's curve (1 at 1, 1.45 at 2).
    /// </summary>
    private static double terraceBandScale(double relief) => relief < 1 ? relief : 0.55 + relief * 0.45;

    /// <summary>Compose the whole world in one call. Tests and headless tools use this; interactive callers slice it.</summary>
    public static SimulatedMap simulateMap(MapSimulationOptions options)
    {
        MapSimulationBuild build = beginMapSimulation(options);
        while (!build.step(1)) { }
        SimulatedMap? result = build.result;
        if (result == null) throw new InvalidOperationException("Map simulation produced no result");
        return result;
    }

    public static MapSimulationBuild beginMapSimulation(MapSimulationOptions options)
    {
        var composer = new MapComposer(options);
        return composer.build();
    }

    private static readonly (int, int)[] NEIGHBOURS4 =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };

    private static readonly (int, int)[] NEIGHBOURS8 =
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

    /// <summary>The dry silhouette of a landform tile: what the bedrock beat draws before hazards return.</summary>
    private static int dryTileFor(int tile)
    {
        switch (tile)
        {
            case TileType.Water:
            case TileType.Bridge:
                return TileType.Floor;
            case TileType.Chasm:
            case TileType.Cleft:
            case TileType.Underpass:
                return TileType.Solid;
            default:
                return tile;
        }
    }

    /// <summary>True when a new monument of `radius` would sit on top of one that is already there.</summary>
    private static bool overlapsPlaced(int tx, int ty, double radius, IReadOnlyList<PlacedMapWonder> placed)
    {
        foreach (PlacedMapWonder wonder in placed)
        {
            if (Math.hypot(wonder.tx - tx, wonder.ty - ty) < (wonder.radius + radius) * 1.15) return true;
        }
        return false;
    }

    /// <summary>Stable per-cell variation seed, matching the terrain editor's own placement-seed contract (a signed int).</summary>
    private static double placementSeed(double seed, int tx, int ty, int salt)
    {
        int value = Js.ToInt32(Math.round(latticeHash(Js.ToUint32(Js.ToInt32(seed) ^ salt), tx, ty) * 0xffffffff));
        return value != 0 ? value : 1;
    }

    private static readonly Regex TITLE_SPLIT = new("[_\\s]+", RegexOptions.CultureInvariant);

    private static string titleCase(string key)
    {
        var parts = new List<string>();
        foreach (string part in TITLE_SPLIT.Split(key))
        {
            if (part.Length == 0) continue;
            parts.Add(char.ToUpperInvariant(part[0]) + part.Substring(1));
        }
        return string.Join(" ", parts);
    }

    /// <summary>The closest cell already reachable from the heart (TS `{ tx, ty } | undefined`).</summary>
    private readonly struct CellRef
    {
        public readonly int tx;
        public readonly int ty;

        public CellRef(int tx, int ty)
        {
            this.tx = tx;
            this.ty = ty;
        }
    }

    /// <summary>A principal axis: sweep anchor plus unit direction.</summary>
    private readonly struct SweepAxis
    {
        public readonly double ax;
        public readonly double ay;
        public readonly double dx;
        public readonly double dy;

        public SweepAxis(double ax, double ay, double dx, double dy)
        {
            this.ax = ax;
            this.ay = ay;
            this.dx = dx;
            this.dy = dy;
        }
    }

    private static MapRevealSweepPlan sweepPlan(string kind, double ax, double ay, double dx, double dy, double jitter) =>
        new() { kind = kind, ax = ax, ay = ay, dx = dx, dy = dy, jitter = jitter };

    /// <summary>
    /// The composition state machine. Every pass mutates the working grids and then records the layer snapshot the
    /// animation replays; nothing here reaches outside the module.
    /// </summary>
    private sealed partial class MapComposer
    {
        private readonly Rng rng;
        private readonly double seedNum;
        /// <summary>`seedNum` as the int32 every `seedNum ^ salt` expression reads (ECMAScript ToInt32).</summary>
        private readonly int seedInt;
        private readonly string seedId;
        private readonly MapSimulationArchetype archetype;
        private readonly double chaos;
        /// <summary>STUDIO: the Mountains, Water and Chasms dials (0..2). Zero promises none of that feature.</summary>
        private readonly double reliefScale;
        private readonly double waterScale;
        private readonly double riftScale;
        private readonly HashSet<string> excludedWonders;
        /// <summary>The motif that actually survived its measured retry — what the recipe reports.</summary>
        private MapLandformMotif? motifApplied;
        private readonly CompositionRoll roll;
        private readonly List<string> themeKeys;
        private readonly int width;
        private readonly int height;
        private readonly int count;
        /// <summary>How many map cells one endless landform cell covers. See <see cref="MAX_BEDROCK_CHUNKS"/>.</summary>
        private readonly int bedrockScale;

        /// <summary>Working tile/elevation grids — the world as far as it has been composed.</summary>
        private readonly byte[] tiles;
        private readonly sbyte[] elevation;
        /// <summary>Dry ground level per cell, independent of what the cell currently is. Hazard elevation derives from it.</summary>
        private readonly byte[] ground;
        private readonly byte[] themeIndex;

        /// <summary>The full-detail bedrock crop, kept so later passes can reintroduce the landform's own hazards.</summary>
        private readonly byte[] landform;
        private readonly sbyte[] landformElevation;

        /// <summary>Cells later passes must never wall off, flood or tear open: ramps, roads, plazas, wonder floors.</summary>
        private readonly byte[] protect;
        /// <summary>Cells a wonder owns. Dressing passes leave them to the wonder's own recipe.</summary>
        private readonly byte[] claim;
        /// <summary>Cells belonging to the circulation network.</summary>
        private readonly byte[] road;

        private readonly List<MapSimulationStage> stages = new();
        private byte[] previousTiles;
        private sbyte[] previousElevation;
        private byte[] previousThemeIndex;

        private readonly List<TerrainDecorationPlacement> decorations = new();
        private readonly List<PlacedMapWonder> wonders = new();

        private byte[] baseTiles = null!;
        private sbyte[] baseElevation = null!;
        private int heartTx = 0;
        private int heartTy = 0;
        private int themeRegionCount = 1;
        /// <summary>Long axis of the composed hydrology — the flow sweep follows it.</summary>
        private SweepAxis waterAxis = new(0, 0, 1, 0);
        private SweepAxis riftAxis = new(0, 0, 0, 1);

        /// <summary>What the generator's `return this.finalize()` hands back.</summary>
        private SimulatedMap? finalResult;

        public MapComposer(MapSimulationOptions options)
        {
            width = clampInt(options.width ?? 96, 24, 384);
            height = clampInt(options.height ?? 72, 24, 256);
            count = width * height;
            bedrockScale = clampInt(
                Math.ceil(Math.sqrt((double)count / (BEDROCK_CHUNK * BEDROCK_CHUNK * MAX_BEDROCK_CHUNKS))),
                1,
                4);
            seedId = options.seed.ToString();
            seedNum = RngModule.hashSeed(seedId);
            seedInt = Js.ToInt32(seedNum);
            rng = new Rng($"mapsim:{seedId}");
            chaos = Scalar.clamp01(options.chaos ?? 0.4);
            reliefScale = bias(options.reliefBias);
            waterScale = bias(options.waterBias);
            riftScale = bias(options.riftBias);
            excludedWonders = new HashSet<string>(options.excludedWonders ?? Array.Empty<string>());

            IReadOnlyList<MapSimulationArchetype> archetypes = MapSimulationArchetypes.MAP_SIMULATION_ARCHETYPES;
            MapSimulationArchetype? pinned = !string.IsNullOrEmpty(options.archetypeKey)
                ? archetypes.find((entry) => entry.key == options.archetypeKey)
                : null;
            // STUDIO: the roll is drawn even when the archetype is pinned, so pinning one keeps the seed's theme.
            MapSimulationArchetype rolled = rng.weighted(
                archetypes,
                archetypes.map((entry) => entry.weight));
            archetype = pinned ?? rolled;

            List<string> candidates = (options.themeKeys ?? generatorThemeKeys()).filter(
                (key) => key != null && key.Length > 0);
            List<string> pool = candidates.Count > 0 ? candidates : new List<string> { "highland_pass" };
            // A chosen theme is a promise: the whole world wears it, edge to edge. Region blending is a separate,
            // explicitly requested composition — it must never turn "I picked Noir Sprawl" into "mostly Noir Sprawl".
            bool blend = options.blendThemes == true;
            if (!string.IsNullOrEmpty(options.themeKey))
            {
                if (blend)
                {
                    themeKeys = new List<string> { options.themeKey };
                    themeKeys.AddRange(rng.shuffle(pool.filter((key) => key != options.themeKey)));
                }
                else
                {
                    themeKeys = new List<string> { options.themeKey };
                }
            }
            else if (blend)
            {
                themeKeys = rng.shuffle(pool);
            }
            else
            {
                themeKeys = new List<string> { rng.pick(pool) };
            }
            if (blend)
            {
                var (minRegions, maxRegions) = archetype.themeRegions;
                double wanted = Math.max(
                    2,
                    Math.min(themeKeys.Count, rng.@int(minRegions, Math.max(minRegions, maxRegions))));
                int keep = (int)Math.min(themeKeys.Count, wanted);
                if (keep < themeKeys.Count) themeKeys.RemoveRange(keep, themeKeys.Count - keep);
            }
            themeRegionCount = themeKeys.Count;
            roll = rollComposition(options);

            tiles = new byte[count];
            elevation = new sbyte[count];
            ground = new byte[count];
            themeIndex = new byte[count].fill((byte)TERRAIN_THEME_INHERIT);
            landform = new byte[count];
            landformElevation = new sbyte[count];
            protect = new byte[count];
            claim = new byte[count];
            road = new byte[count];
            previousTiles = new byte[count];
            previousElevation = new sbyte[count];
            previousThemeIndex = new byte[count].fill((byte)TERRAIN_THEME_INHERIT);
        }

        /// <summary>
        /// Vary the archetype into one concrete world.
        ///
        /// The band WIDTH is the chaos dial: at 0 every scalar sits close to the archetype's authored value and the
        /// silhouette is its first choice, so the same archetype composes recognisably the same kind of world; at 1
        /// every band opens to its full width, the frame can be anything and sides drop out of it. The crop origin
        /// matters as much as any of them — sampling the endless stream from a rolled chunk avoids reusing the same
        /// corner of every generated world (chunk (0,0) in particular carries the endless spawn court, which would
        /// otherwise turn up in the top-left of every single simulated map).
        /// </summary>
        private CompositionRoll rollComposition(MapSimulationOptions options)
        {
            MapSimulationArchetype a = archetype;
            // STUDIO: the roll reads streams of its own, and every draw below happens whatever the dials, the pinned
            // frame or the pinned layout say. A dial therefore changes exactly the value it names: in the original a
            // dial at zero skipped its draw, every later value shifted, and the map was cut from another corner of
            // the endless landform — "Mountains 0" came back as a different, often rockier world.
            Rng rng = stream("composition");
            Rng crop = stream("crop");
            // 0.22 at a calm dial, 1.55 at a wild one. Never 0: two maps of one archetype must never be the same map.
            double spread = 0.22 + chaos * 1.33;
            // The multiplier is clamped even at a wild dial: a ten-fold swing in a *density* is not variety, it is a
            // map that sometimes forgets to grow anything. Chaos widens the band, it does not remove the floor.
            double factor(double band) => Math.max(
                0.45,
                Math.min(1.85, rng.range(1 - band * spread, 1 + band * spread)));
            double vary(double value, double band, double lo, double hi)
            {
                double f = factor(band);
                // A dial turned to zero means zero. Only a value the archetype actually asked for gets a floor.
                if (value <= 0) return 0;
                return Math.min(hi, Math.max(lo, value * f));
            }
            // STUDIO: a dialled value. Below 1 the floor shrinks with the dial, so its low end keeps thinning out
            // instead of stopping at the archetype's floor; above 1 `extra` adds an absolute share, so the dial also
            // grows the feature in a landscape whose own value is zero (a delta has no rifts of its own), and the cap
            // rises by half the dial's excess, so a landscape already at its cap still gets more.
            double dial(double value, double scale, double extra, double f, double lo, double hi)
            {
                double v = value * scale + Math.max(0, scale - 1) * extra;
                if (v <= 0) return 0;
                double cap = hi * (1 + Math.max(0, scale - 1) * 0.5);
                return Math.min(cap, Math.max(lo * Math.min(1, scale), v * f));
            }

            IReadOnlyList<string> frames = a.frames.Count > 0 ? a.frames : new[] { MapFrameKind.Open };
            bool wildFrame = rng.@bool(Math.max(0, (chaos - 0.7) * 1.4));
            string anyFrame = rng.pick(MapSimulationArchetypes.MAP_FRAME_KINDS);
            string ownFrame = rng.pick(frames);
            string frame =
                options.frameKind ??
                // Above a wild dial the silhouette is allowed to leave the archetype's own set entirely.
                (chaos > 0.7 && wildFrame
                    ? anyFrame
                    : chaos < 0.08
                        ? frames[0]
                        : ownFrame);
            // STUDIO: a sea or a rift around the map is water or chasm: at zero the Water or Chasms dial takes it away.
            if ((frame == MapFrameKind.Island && waterScale <= 0) || (frame == MapFrameKind.Shelf && riftScale <= 0))
                frame = MapFrameKind.Open;

            int bands = clampInt(
                a.terraceBands *
                    rng.range(1 - 0.35 * spread, 1 + 0.45 * spread) *
                    terraceBandScale(reliefScale),
                0,
                8);
            int step = clampInt(a.terraceStep * (0.6 + reliefScale * 0.55), 1, 3);
            // Object-literal properties evaluate in source order; each rng draw below happens in that order.
            var result = new CompositionRoll();
            result.frame = frame;
            result.frameDepth = vary(a.frameDepth, 1.6, 0.02, 0.16);
            result.frameNoise = vary(a.frameNoise, 1.2, 0.15, 1);
            result.openness = vary(a.openness, 0.9, 0.22, 0.66);
            // Relief bias flips sign occasionally: the same archetype can crown or sink its centre.
            double reliefRange = rng.range(0.5, 1.35);
            result.reliefBias =
                a.reliefBias *
                reliefRange *
                reliefScale *
                (rng.@bool(0.06 + chaos * 0.14) ? -1 : 1);
            result.terraceBands = bands;
            result.terraceStep = step;
            result.terraceNoise = vary(a.terraceNoise, 1.1, 0.08, 1.1);
            // Depth varies hard: some worlds are gentle downland, others are all cliff. Both are wanted, and the
            // spread between them is a large part of what makes two maps feel like different places at a glance.
            result.wallRelief = dial(a.wallRelief, reliefScale, 0, factor(1.5), 0.9, 7);
            // Most worlds are landform; a substantial minority are built on explicit bones. The chaos dial decides
            // how often the world is allowed to be a caldera, a cut grid or a giant stair rather than country.
            bool motifRoll = rng.@bool(MOTIF_BASE_CHANCE + chaos * MOTIF_CHAOS_CHANCE);
            MapLandformMotif rolledMotif = rng.weighted(
                MapSimulationMotifs.MAP_LANDFORM_MOTIFS,
                MapSimulationMotifs.MAP_LANDFORM_MOTIFS.map((entry) => entry.weight));
            result.motif =
                options.motifKey != null
                    ? MapSimulationMotifs.MAP_LANDFORM_MOTIFS.find((entry) => entry.key == options.motifKey)
                    : motifRoll
                        ? rolledMotif
                        : null;
            double waterFactor = factor(1.5);
            result.waterBudget = dial(a.waterBudget, waterScale, 0.1, waterFactor, 0, 0.5);
            result.waterBase = dial(a.waterBudget, 1, 0, waterFactor, 0, 0.5);
            double riftFactor = factor(1.7);
            result.riftBudget = dial(a.riftBudget, riftScale, 0.05, riftFactor, 0, 0.3);
            result.riftBase = dial(a.riftBudget, 1, 0, riftFactor, 0, 0.3);
            result.floraDensity = dial(a.floraDensity, bias(options.floraBias), 0, factor(1.2), 0.065, 0.3);
            result.canopyShare = vary(a.canopyShare, 1, 0.1, 0.94);
            result.landmarkDensity = dial(a.landmarkDensity, bias(options.landmarkBias), 0, factor(1.9), 0, 4.5);
            result.wonderDensity = dial(a.wonderDensity, bias(options.wonderBias), 0, factor(1.3), 0, 4);
            result.roadDensity = dial(a.roadDensity, bias(options.roadBias), 0, factor(1.2), 0, 4);
            // STUDIO: where the landform is cut from is its own stream: no dial, not even Surprises, moves the crop.
            result.tier = clampInt(a.tier + crop.@int(-1, 1), 1, 5);
            result.chunkX = (int)crop.@int(-4096, 4096);
            result.chunkY = (int)crop.@int(-4096, 4096);
            result.turns = (int)crop.@int(0, 3);
            result.mirror = crop.@bool();
            return result;
        }

        /// <summary>
        /// STUDIO: an independent random stream per pass, derived from the seed and the pass's name.
        ///
        /// The original forked every pass from one shared generator, so a pass that ran a different number of times
        /// (a river more, a ramp less) shifted every pass after it: moving one dial re-rolled roads, wonders and
        /// flora everywhere on the map. Named streams keep each pass's dice its own.
        /// </summary>
        private Rng stream(string label) => new Rng($"mapsim:{seedId}:{label}");

        public MapSimulationBuild build()
        {
            int span = BEDROCK_CHUNK * bedrockScale;
            int chunksX = (int)Math.ceil((double)width / span);
            int chunksY = (int)Math.ceil((double)height / span);
            // Progress is modelled, never measured: the composition is a generator, so the only honest readout is a
            // running weight against an estimate. The estimate is deliberately a little generous — a bar that reaches
            // 100% and then waits reads far worse than one that arrives a touch early.
            double totalWeight = chunksX * chunksY * 3 + 46;
            IEnumerator<MapSimulationBuildStep> runner = passes(chunksX, chunksY).GetEnumerator();
            return new MapSimulationBuild(runner, totalWeight, () => finalResult);
        }

        private static MapSimulationBuildStep Step(string label, double weight) => new(label, weight);

        /// <summary>The composition itself. Each `yield` is one interruptible slice of work.</summary>
        private IEnumerable<MapSimulationBuildStep> passes(int chunksX, int chunksY)
        {
            for (int cy = 0; cy < chunksY; cy++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    blitBedrockChunk(cx, cy);
                    yield return Step("Surveying bedrock", 3);
                }
            }

            prepareBedrock();
            yield return Step("Framing the map", 2);
            foreach (MapSimulationBuildStep slice in openDeadCountry()) yield return slice;
            finishBedrock();
            yield return Step("Texturing open ground", 2);
            composeRelief();
            yield return Step("Banding the relief", 4);
            composeHydrology();
            yield return Step("Routing water", 3);
            composeRifts();
            yield return Step("Opening rifts", 2);
            composeCrossings();
            yield return Step("Building crossings", 2);
            foreach (MapSimulationBuildStep slice in composeWonders()) yield return slice;
            composeRoads();
            yield return Step("Laying avenues", 3);
            composeTheming();
            yield return Step("Assigning regions", 2);
            composeFlora();
            yield return Step("Seeding flora", 3);
            composeLandmarks();
            yield return Step("Raising landmarks", 2);
            finalResult = finalize();
        }

        // ---------------------------------------------------------------------------------------------------
        // Bedrock

        /// <summary>Sample one endless chunk of the archetype's landform and blit its overlap into the map frame.</summary>
        private void blitBedrockChunk(int cx, int cy)
        {
            DungeonLayout layout = Endless.generateEndlessChunkAt(
                seedNum,
                roll.chunkX + cx,
                roll.chunkY + cy,
                themeKeys.Count > 0 ? themeKeys[0] : "highland_pass",
                roll.tier,
                EndlessCountry.ENDLESS_GENERATION_VERSION,
                RngModule.hashSeed($"{seedId}:spine"));
            int scale = bedrockScale;
            int ox = cx * BEDROCK_CHUNK * scale;
            int oy = cy * BEDROCK_CHUNK * scale;
            sbyte[]? layoutElevation = layout.elevation;
            for (int y = 0; y < layout.height; y++)
            {
                int src = y * layout.width;
                for (int x = 0; x < layout.width; x++, src++)
                {
                    byte tile = layout.tiles[src];
                    sbyte level = layoutElevation != null && (uint)src < (uint)layoutElevation.Length ? layoutElevation[src] : (sbyte)0;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int dst = orientedIndex(ox + x * scale + sx, oy + y * scale + sy);
                            if (dst < 0) continue;
                            landform[dst] = tile;
                            landformElevation[dst] = level;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Map a crop coordinate through the roll's quarter-turn and mirror. Reorienting the sample is free variety:
        /// the same landform read from a different corner and a different facing composes a different place.
        /// </summary>
        private int orientedIndex(int x, int y)
        {
            int tx = x;
            int ty = y;
            // Turns operate on the map rectangle; odd turns swap the axes, so fold the longer span back in bounds.
            for (int turn = 0; turn < roll.turns; turn++)
            {
                int nx = height - 1 - ty;
                ty = tx;
                tx = nx;
            }
            if (roll.mirror) tx = width - 1 - tx;
            if (tx < 0 || ty < 0 || tx >= width || ty >= height) return -1;
            return ty * width + tx;
        }

        /// <summary>
        /// Turn the landform into a bounded *place*: dry it out to a pure land/rock silhouette, close it with the
        /// archetype's frame, and guarantee one connected walkable body. This is the first thing the viewer sees.
        /// </summary>
        private void prepareBedrock()
        {
            // Ground level per cell, independent of the hazards that will be reintroduced later. Water in the
            // landform stores a datum one step below its shore, so lift it back to a dry level.
            for (int i = 0; i < count; i++)
            {
                int tile = landform[i];
                int stored = landformElevation[i];
                ground[i] = Js.U8(TerrainKit.clampElevationLevel(
                    tile == TileType.Water ? stored + 1 : tile == TileType.Chasm ? 0 : stored,
                    SIM_MAX_LEVEL));
                tiles[i] = (byte)dryTileFor(tile);
            }
            fillGroundHoles();
            applyFrame();
            sealRim();
            heartFromLargestComponent();
        }

        /// <summary>Second half of the bedrock beat: texture the plains, keep one body, and publish the silhouette.</summary>
        private void finishBedrock()
        {
            if (reliefScale < 1) erodeRock();
            textureOpenGround();

            // One connected body. The heart is the walkable cell closest to the centre of the largest component.
            heartFromLargestComponent();
            // STUDIO: a flat world has no rock to return stranded land to; it stays ground (the map check calls it
            // out as a hint) until the crossings pass bridges it.
            if (!flatWorld)
            {
                byte[] reach = floodFillWalkable(tiles, width, height, heartTx, heartTy);
                for (int i = 0; i < count; i++)
                {
                    if (isWalkable(tiles[i]) && reach[i] == 0) tiles[i] = TileType.Solid;
                }
            }

            // The bedrock beat reads as a silhouette drawn flat on the sheet — relief is the next beat's payoff.
            Array.Fill(elevation, (sbyte)BEDROCK_FLAT_LEVEL);
            for (int i = 0; i < count; i++)
            {
                if (!isWalkable(tiles[i])) elevation[i] = BEDROCK_FLAT_LEVEL + 1;
            }
            baseTiles = new byte[count].fill((byte)TileType.Solid);
            baseElevation = new sbyte[count].fill((sbyte)BEDROCK_FLAT_LEVEL);
            previousTiles = (byte[])baseTiles.Clone();
            previousElevation = (sbyte[])baseElevation.Clone();
            recordStage(MapSimulationLayer.Bedrock, sweepPlan(MapRevealSweep.Radial, heartTx, heartTy, 1, 0, 0.55));
        }

        /// <summary>STUDIO: the Mountains dial at zero — no rock, no height, one flat floor.</summary>
        private bool flatWorld => reliefScale <= 0;

        /// <summary>
        /// STUDIO: below its centre the Mountains dial takes rock away, not only height.
        ///
        /// In the original the dial only scaled heights: at zero the landform kept all of its rock (four tenths of a
        /// typical map), and the clearings cut into it kept the rock's height, so a "no mountains" map still read as
        /// mountains. Here the masses erode from their edges inward — thin walls go first, the cores of the ranges
        /// last — and at zero none is left. Every rock cell is ranked by one fixed score, so the rock of a lower
        /// setting is always part of the rock of a higher one: the dial moves the map monotonically and never
        /// re-rolls it. (Above its centre the dial grows the rock after the relief pass: <see cref="growRock"/>.)
        /// </summary>
        private void erodeRock()
        {
            var rock = new List<int>();
            for (int i = 0; i < count; i++) if (tiles[i] == TileType.Solid) rock.Add(i);
            if (rock.Count == 0) return;
            // Depth into the rock: how many cells a rock cell lies behind the nearest open ground.
            short[] depth = chebyshevDistance((i) => tiles[i] == TileType.Solid);
            var ranked = rock.map((i) => new ScoredCell { index = i, score = depth[i] + rockWobble(i % width, i / width) });
            ranked.sort((a, b) =>
            {
                double d = b.score - a.score;
                return d != 0 ? Math.sign(d) : a.index - b.index;
            });
            int keep = clampInt(rock.Count * reliefScale, 0, rock.Count);
            var opened = new List<int>();
            for (int k = keep; k < ranked.Count; k++)
            {
                tiles[ranked[k].index] = TileType.Floor;
                opened.Add(ranked[k].index);
            }
            groundFromOpenNeighbours(opened);
        }

        /// <summary>
        /// STUDIO: above its centre the Mountains dial grows the rock out into open ground. It runs on the finished
        /// relief, so a layout motif's rock grows as well as the landform's, and it takes open ground next to rock only
        /// where the ground around it is wide: a passage never closes, and ramps and the heart stay clear.
        /// </summary>
        private void growRock()
        {
            int rock = 0;
            for (int i = 0; i < count; i++) if (tiles[i] == TileType.Solid) rock++;
            if (rock == 0) return;
            short[] open = openDistanceField();
            short[] fromRock = chebyshevDistance((i) => tiles[i] != TileType.Solid);
            var wide = new byte[count];
            for (int i = 0; i < count; i++) if (isWalkable(tiles[i]) && open[i] >= ROCK_GROWTH_KEEP_OPEN) wide[i] = 1;
            var candidates = new List<ScoredCell>();
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != TileType.Floor || protect[i] != 0 || isHeartward(i)) continue;
                int d = fromRock[i];
                if (d < 1 || d > ROCK_GROWTH_REACH) continue;
                int tx = i % width;
                int ty = i / width;
                if (!hasMaskWithin(wide, tx, ty, ROCK_GROWTH_REACH)) continue;
                candidates.Add(new ScoredCell { index = i, score = -d + rockWobble(tx, ty) });
            }
            candidates.sort((a, b) =>
            {
                double d = b.score - a.score;
                return d != 0 ? Math.sign(d) : a.index - b.index;
            });
            int grow = clampInt(rock * (reliefScale - 1) * ROCK_GROWTH_AT_FULL_DIAL, 0, candidates.Count);
            for (int k = 0; k < grow; k++) tiles[candidates[k].index] = TileType.Solid;
        }

        /// <summary>STUDIO: the noise that keeps eroded and grown rock edges organic instead of following the distance field.</summary>
        private double rockWobble(int tx, int ty)
        {
            uint noiseSeed = Js.ToUint32(seedInt ^ 0x2b7e1516);
            return (valueNoise(noiseSeed, tx, ty, 7) - 0.5) * 3.2 + latticeHash(noiseSeed, tx, ty) * 0.05;
        }

        /// <summary>
        /// STUDIO: Chebyshev distance of every cell inside `inside` to the nearest cell outside it (0 outside). The map
        /// border is not an edge: a mass cut by the border counts as deep.
        /// </summary>
        private short[] chebyshevDistance(Func<int, bool> inside)
        {
            var field = new short[count];
            int far = width + height;
            for (int i = 0; i < count; i++) field[i] = (short)(inside(i) ? far : 0);
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    if (field[index] == 0) continue;
                    int best = field[index];
                    if (tx > 0) best = Math.min(best, field[index - 1] + 1);
                    if (ty > 0) best = Math.min(best, field[index - width] + 1);
                    if (tx > 0 && ty > 0) best = Math.min(best, field[index - width - 1] + 1);
                    if (tx < width - 1 && ty > 0) best = Math.min(best, field[index - width + 1] + 1);
                    field[index] = (short)best;
                }
            }
            for (int ty = height - 1; ty >= 0; ty--)
            {
                for (int tx = width - 1; tx >= 0; tx--)
                {
                    int index = tileIndex(width, tx, ty);
                    if (field[index] == 0) continue;
                    int best = field[index];
                    if (tx < width - 1) best = Math.min(best, field[index + 1] + 1);
                    if (ty < height - 1) best = Math.min(best, field[index + width] + 1);
                    if (tx < width - 1 && ty < height - 1) best = Math.min(best, field[index + width + 1] + 1);
                    if (tx > 0 && ty < height - 1) best = Math.min(best, field[index + width - 1] + 1);
                    field[index] = (short)best;
                }
            }
            return field;
        }

        /// <summary>
        /// STUDIO: ground that was rock takes the level of the open ground next to it, spreading inward — the rock is
        /// taken away down to the land around it, instead of leaving a shelf at the height of its crest.
        /// </summary>
        private void groundFromOpenNeighbours(List<int> opened)
        {
            var pending = new byte[count];
            foreach (int index in opened) pending[index] = 1;
            var frontier = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (pending[i] != 0 || !isWalkable(tiles[i])) continue;
                int tx = i % width;
                int ty = i / width;
                foreach (var (dx, dy) in NEIGHBOURS4)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    if (pending[tileIndex(width, nx, ny)] == 0) continue;
                    frontier.Add(i);
                    break;
                }
            }
            while (frontier.Count > 0)
            {
                var next = new List<int>();
                foreach (int index in frontier)
                {
                    int tx = index % width;
                    int ty = index / width;
                    foreach (var (dx, dy) in NEIGHBOURS4)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (pending[ni] == 0) continue;
                        pending[ni] = 0;
                        ground[ni] = ground[index];
                        next.Add(ni);
                    }
                }
                frontier = next;
            }
        }

        /// <summary>
        /// A crop of an endless stream can land on solid country: whole quarters of the frame with nothing in them.
        /// A *map* may not have a dead half. This pass measures walkable coverage on a coarse grid, then opens the
        /// emptiest districts with organic clearings and links each one back toward the heart — the same blob-and-path
        /// grammar every authored finite world is carved with, applied only where the crop came up empty.
        ///
        /// Reachability is refreshed on a schedule rather than after every clearing: a full flood fill per attempt was
        /// the composition's single most expensive line, and a clearing carved two cells from the last one does not
        /// need a fresh answer to be linked correctly.
        /// </summary>
        private IEnumerable<MapSimulationBuildStep> openDeadCountry()
        {
            int block = (int)Math.max(7, Math.round(Math.min(width, height) / 6.0));
            int cols = (int)Math.ceil((double)width / block);
            int rows = (int)Math.ceil((double)height / block);
            int inset = (int)Math.max(2, Math.round(Math.min(width, height) * roll.frameDepth) + 1);
            Rng rng = stream("clearings");
            var density = new float[cols * rows];
            int attempts = Math.min(64, cols * rows * 2);
            // This pass runs on the DRY silhouette; hydrology and rifts will each claim their budget of that floor
            // afterwards. Aim high enough that what survives them still meets the archetype's openness.
            // STUDIO: sized from the budgets at their dials' centre, so the Water and Chasms dials never re-cut the land.
            double hazardShare = Scalar.clamp01(roll.waterBase + roll.riftBase);
            double dryTarget = Math.min(0.92, roll.openness / Math.max(0.25, 1 - hazardShare));
            byte[] reach = floodFillWalkable(tiles, width, height, heartTx, heartTy);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                Array.Clear(density);
                int interior = 0;
                int walkable = 0;
                for (int ty = inset; ty < height - inset; ty++)
                {
                    for (int tx = inset; tx < width - inset; tx++)
                    {
                        interior++;
                        if (!isWalkable(tiles[tileIndex(width, tx, ty)])) continue;
                        walkable++;
                        density[(ty / block) * cols + (tx / block)] += 1;
                    }
                }
                if (interior == 0) yield break;

                // Openness is a LOCAL contract, not an average: a map whose western half is a lake district and whose
                // east is unbroken rock passes any global test while still being half dead. Every district inside the
                // frame must therefore carry its own share of walkable ground, and the emptiest one is opened next.
                double districtFloor = block * block * dryTarget * 0.45;
                int worst = -1;
                double worstScore = double.PositiveInfinity;
                for (int by = 0; by < rows; by++)
                {
                    for (int bx = 0; bx < cols; bx++)
                    {
                        double cx = bx * block + block * 0.5;
                        double cy = by * block + block * 0.5;
                        if (cx < inset || cy < inset || cx > width - inset || cy > height - inset) continue;
                        double count = density[by * cols + bx];
                        if (count >= districtFloor) continue;
                        double score = count + latticeHash(seedNum, bx, by) * block;
                        if (score < worstScore)
                        {
                            worstScore = score;
                            worst = by * cols + bx;
                        }
                    }
                }
                if (worst < 0 && (double)walkable / interior >= dryTarget) yield break;
                if (worst < 0)
                {
                    // Every district carries its share but the map as a whole is still tight: widen the thinnest one.
                    for (int by = 0; by < rows; by++)
                    {
                        for (int bx = 0; bx < cols; bx++)
                        {
                            double cx = bx * block + block * 0.5;
                            double cy = by * block + block * 0.5;
                            if (cx < inset || cy < inset || cx > width - inset || cy > height - inset) continue;
                            double score = density[by * cols + bx] + latticeHash(seedNum, bx, by) * block;
                            if (score < worstScore)
                            {
                                worstScore = score;
                                worst = by * cols + bx;
                            }
                        }
                    }
                }
                if (worst < 0) yield break;

                {
                    int bx = worst % cols;
                    int by = worst / cols;
                    int cx = clampInt(
                        bx * block + block * 0.5 + rng.range(-1, 1) * block * 0.2,
                        inset,
                        width - 1 - inset);
                    int cy = clampInt(
                        by * block + block * 0.5 + rng.range(-1, 1) * block * 0.2,
                        inset,
                        height - 1 - inset);
                    double rx = block * rng.range(0.45, 0.8);
                    double ry = block * rng.range(0.45, 0.8);
                    TerrainKit.carveOrganicBlob(
                        tiles,
                        width,
                        height,
                        new OrganicZone { tx = cx, ty = cy, rx = rx, ry = ry },
                        seedInt ^ (attempt * 0x9e37),
                        new OrganicBlobOptions { minBorder = inset, wobble = 0.55 });
                    // The clearing must join the body the map is anchored on, not just any neighbouring pocket — an
                    // unreachable clearing is dissolved again by the connectivity pass and the district stays dead.
                    if (attempt % 3 == 0)
                        reach = floodFillWalkable(tiles, width, height, heartTx, heartTy);
                    CellRef? link = nearestReachable(cx, cy, reach);
                    if (link.HasValue)
                    {
                        TerrainKit.carveOrganicLine(
                            tiles,
                            width,
                            height,
                            cx,
                            cy,
                            link.Value.tx,
                            link.Value.ty,
                            rng.@int(2, 3),
                            seedInt ^ (attempt * 0x27d4),
                            new OrganicLineOptions { minBorder = inset, wobbleScale = 0.45 });
                    }
                }
                yield return Step("Opening dead country", 1);
            }
        }

        /// <summary>
        /// Break up wide-open ground with free-standing outcrops.
        ///
        /// A carved clearing is honest space but not yet a *place*: the eye needs something to measure it against.
        /// Small rock masses set well away from the walls give the plain scale, cover and silhouette — the same role
        /// the arena's bastion ring plays — without ever pinching a route, because every outcrop stays smaller than
        /// the open distance around it.
        /// </summary>
        private void textureOpenGround()
        {
            short[] open = openDistanceField();
            var candidates = new List<int>();
            for (int index = 0; index < count; index++)
            {
                if (tiles[index] != TileType.Floor || isHeartward(index)) continue;
                if (open[index] < OUTCROP_MIN_CLEARANCE) continue;
                candidates.Add(index);
            }
            if (candidates.Count == 0) return;
            // STUDIO: outcrops are rock, so the Mountains dial scales them (none on a flat world).
            double target = Math.round(candidates.Count / 26.0 * reliefScale);
            if (target <= 0) return;
            uint outcropSeed = Js.ToUint32(seedInt ^ 0x7a2fb1c9);
            List<int> anchors = selectByField(candidates, target, 0x7a2fb1c9, (tx, ty, index) =>
            {
                return valueNoise(outcropSeed, tx, ty, 11) + open[index] * 0.06;
            });
            Rng rng = stream("outcrops");
            foreach (int index in anchors)
            {
                int tx = index % width;
                int ty = index / width;
                // Never grow wider than the clearance around the anchor, so an outcrop can not become a wall.
                double radius = Math.min(open[index] - 1.6, rng.range(0.9, 2.3));
                if (radius < 0.7) continue;
                int r = (int)Math.ceil(radius);
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        double wobble = (latticeHash(outcropSeed, tx + dx, ty + dy) - 0.5) * 0.9;
                        if (dx * dx + dy * dy > (radius + wobble) * (radius + wobble)) continue;
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int cell = tileIndex(width, nx, ny);
                        if (tiles[cell] != TileType.Floor || isHeartward(cell)) continue;
                        tiles[cell] = TileType.Solid;
                    }
                }
            }
        }

        /// <summary>Chebyshev distance from every walkable cell to the nearest thing that is not walkable.</summary>
        private short[] openDistanceField()
        {
            var field = new short[count];
            int far = width + height;
            for (int i = 0; i < count; i++)
            {
                field[i] = (short)(isWalkable(tiles[i]) ? far : 0);
            }
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    if (field[index] == 0) continue;
                    int best = far;
                    if (tx > 0) best = Math.min(best, field[index - 1] + 1);
                    if (ty > 0) best = Math.min(best, field[index - width] + 1);
                    if (tx > 0 && ty > 0) best = Math.min(best, field[index - width - 1] + 1);
                    if (tx < width - 1 && ty > 0) best = Math.min(best, field[index - width + 1] + 1);
                    field[index] = (short)Math.min(field[index], best);
                }
            }
            for (int ty = height - 1; ty >= 0; ty--)
            {
                for (int tx = width - 1; tx >= 0; tx--)
                {
                    int index = tileIndex(width, tx, ty);
                    if (field[index] == 0) continue;
                    int best = field[index];
                    if (tx < width - 1) best = Math.min(best, field[index + 1] + 1);
                    if (ty < height - 1) best = Math.min(best, field[index + width] + 1);
                    if (tx < width - 1 && ty < height - 1) best = Math.min(best, field[index + width + 1] + 1);
                    if (tx > 0 && ty < height - 1) best = Math.min(best, field[index + width - 1] + 1);
                    field[index] = (short)best;
                }
            }
            return field;
        }

        /// <summary>The closest cell already reachable from the heart, searched in expanding rings.</summary>
        private CellRef? nearestReachable(int tx, int ty, byte[] reach)
        {
            int maxRadius = Math.max(width, height);
            for (int radius = 2; radius <= maxRadius; radius += 2)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.max(Math.abs(dx), Math.abs(dy)) != radius) continue;
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        if (reach[tileIndex(width, nx, ny)] != 0) return new CellRef(nx, ny);
                    }
                }
            }
            return null;
        }

        /// <summary>Chasm cells have no ground of their own; grow one in from their neighbours so relief stays continuous.</summary>
        private void fillGroundHoles()
        {
            var pending = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (landform[i] == TileType.Chasm) pending.Add(i);
            }
            for (int pass = 0; pass < 6 && pending.Count > 0; pass++)
            {
                int write = 0;
                // `for (const index of pending)` while compacting the same array: writes only land at or behind the
                // cursor, so an index loop over the live list reads exactly what the iterator read.
                for (int k = 0; k < pending.Count; k++)
                {
                    int index = pending[k];
                    int tx = index % width;
                    int ty = index / width;
                    int best = -1;
                    foreach (var (dx, dy) in NEIGHBOURS4)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (landform[ni] == TileType.Chasm) continue;
                        best = Math.max(best, ground[ni]);
                    }
                    if (best >= 0) ground[index] = (byte)best;
                    else pending[write++] = index;
                }
                pending.RemoveRange(write, pending.Count - write);
            }
        }

        /// <summary>
        /// Close the map with the rolled silhouette: coast, rampart, rim, shattered edge — or nothing at all.
        ///
        /// The frame is not applied evenly. Each of the four sides gets its own depth, and one or two of them may be
        /// dropped entirely, so a map can be walled to the north, open to the east and drowned to the south. A
        /// composition that is always boxed in on all four sides reads as the same map every time, whatever grew
        /// inside it, and that sameness was the single biggest tell that these worlds were generated. How often a
        /// side drops out is itself on the chaos dial.
        /// </summary>
        private void applyFrame()
        {
            if (roll.frame == MapFrameKind.Open) return;
            Rng rng = stream("frame");
            int minAxis = Math.min(width, height);
            bool hazardFrame = roll.frame == MapFrameKind.Island || roll.frame == MapFrameKind.Shelf;
            // A frame is a border, not a biome. The absolute cap matters more than the ratio: on a small map a
            // "0.16 of the short axis, times a 1.5 side roll, plus a 1.5x wobble" chasm frame eats the whole world,
            // and a shelf map with no shelf left on it is the worst thing this composer can ship.
            double @base = Math.max(
                2,
                Math.min(minAxis * roll.frameDepth, minAxis * (hazardFrame ? 0.085 : 0.11)));
            uint noiseSeed = Js.ToUint32(seedInt ^ 0x51ab3e75);
            byte frameTile =
                roll.frame == MapFrameKind.Island
                    ? (byte)TileType.Water
                    : roll.frame == MapFrameKind.Shelf
                        ? (byte)TileType.Chasm
                        : (byte)TileType.Solid;
            // Per-side depth, with a real chance of a side simply not being framed at all.
            double dropChance = 0.1 + chaos * 0.3;
            var sides = new double[4];
            for (int side = 0; side < 4; side++) sides[side] = rng.@bool(dropChance) ? 0 : @base * rng.range(0.4, 1.2);
            // STUDIO: a sea or rift frame is water or chasm, so below its dial's centre it narrows with that dial.
            double scale =
                roll.frame == MapFrameKind.Island
                    ? Math.min(1, waterScale)
                    : roll.frame == MapFrameKind.Shelf
                        ? Math.min(1, riftScale)
                        : 1;
            for (int side = 0; side < 4; side++) sides[side] *= scale;
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    double wobble = (valueNoise(noiseSeed, tx, ty, 17) - 0.5) * roll.frameNoise * @base * 0.85 * scale;
                    double reach =
                        Math.max(
                            sides[0] - ty,
                            sides[1] - (width - 1 - tx),
                            sides[2] - (height - 1 - ty),
                            sides[3] - tx) + wobble;
                    if (reach > 0) tiles[index] = frameTile;
                }
            }
        }

        /// <summary>
        /// Seal the outermost ring with whatever the frame is made of.
        ///
        /// The ring exists so the composed world has a defined last cell rather than a ragged half-cell against the
        /// void; it is emphatically NOT a mandatory wall. An island ends in its own sea, a shelf in its own rift, and
        /// an open frame just keeps the landform it grew — only rock frames end in rock.
        /// </summary>
        private void sealRim()
        {
            if (roll.frame == MapFrameKind.Open) return;
            byte rim =
                roll.frame == MapFrameKind.Island
                    ? (byte)TileType.Water
                    : roll.frame == MapFrameKind.Shelf
                        ? (byte)TileType.Chasm
                        : (byte)TileType.Solid;
            for (int ring = 0; ring < RIM_CELLS; ring++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    tiles[tileIndex(width, tx, ring)] = rim;
                    tiles[tileIndex(width, tx, height - 1 - ring)] = rim;
                }
                for (int ty = 0; ty < height; ty++)
                {
                    tiles[tileIndex(width, ring, ty)] = rim;
                    tiles[tileIndex(width, width - 1 - ring, ty)] = rim;
                }
            }
        }

        /// <summary>Pick the map's heart: the walkable cell nearest the centre of the biggest walkable component.</summary>
        private void heartFromLargestComponent()
        {
            var seen = new byte[count];
            int bestSize = -1;
            double bestSumX = 0;
            double bestSumY = 0;
            List<int> bestCells = new();
            var stack = new List<int>();
            for (int start = 0; start < count; start++)
            {
                if (seen[start] != 0 || !isWalkable(tiles[start])) continue;
                stack.Clear();
                stack.Add(start);
                seen[start] = 1;
                var cells = new List<int>();
                double sumX = 0;
                double sumY = 0;
                while (stack.Count > 0)
                {
                    int index = stack.pop();
                    cells.Add(index);
                    int tx = index % width;
                    int ty = index / width;
                    sumX += tx;
                    sumY += ty;
                    foreach (var (dx, dy) in NEIGHBOURS4)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (seen[ni] != 0 || !isWalkable(tiles[ni])) continue;
                        seen[ni] = 1;
                        stack.Add(ni);
                    }
                }
                if (cells.Count > bestSize)
                {
                    bestSize = cells.Count;
                    bestSumX = sumX;
                    bestSumY = sumY;
                    bestCells = cells;
                }
            }
            if (bestSize <= 0)
            {
                // Degenerate landform (an all-rock roll). Open a plaza so the map always has a heart.
                int cx = width >> 1;
                int cy = height >> 1;
                for (int ty = cy - 4; ty <= cy + 4; ty++)
                {
                    for (int tx = cx - 4; tx <= cx + 4; tx++)
                    {
                        if (!inBounds(width, height, tx, ty)) continue;
                        tiles[tileIndex(width, tx, ty)] = TileType.Floor;
                    }
                }
                heartTx = cx;
                heartTy = cy;
                return;
            }
            double centroidX = bestSumX / bestSize;
            double centroidY = bestSumY / bestSize;
            int bestCell = bestCells[0];
            double bestDistance = double.PositiveInfinity;
            foreach (int index in bestCells)
            {
                int tx = index % width;
                int ty = index / width;
                // `(tx - centroidX) ** 2`: exponent 2 is an exact square in every engine.
                double ddx = tx - centroidX;
                double ddy = ty - centroidY;
                double distance = ddx * ddx + ddy * ddy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCell = index;
                }
            }
            heartTx = bestCell % width;
            heartTy = bestCell / width;
        }
    }
}
