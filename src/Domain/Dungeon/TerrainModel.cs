// Port of packages/shared/src/domain/dungeon/terrainModel.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// ── Shape of the materialized terrain ─────────────────────────────────────────────────────────────────
//
// Porting notes (C# representation of the TS unions):
// * `TileType | 'outside'` (TerrainContact.type, TerrainEdge.contactType) is an `int`; 'outside' is the sentinel
//   TerrainModel.TERRAIN_CONTACT_OUTSIDE (-1). Every TileType predicate/comparison answers false for it, exactly
//   like the string 'outside' in JS.
// * `TerrainBridgeSpan | null` (TerrainCell.span) is `int?` holding TileType.Water/Chasm/Floor.
// * String-literal unions are `string`; their literals are available as constants in the static classes below.
// * `Record<TerrainEdgeDirection, T>` is TerrainDirectionRecord<T> (fields n/e/s/w plus a string indexer).

/// <summary>Edge-local movement transition kinds.</summary>
public static class TransitionKind
{
    public const string Level = "level";
    public const string StepUp = "step-up";
    public const string StepDown = "step-down";
    public const string Blocked = "blocked";
}

/// <summary>Cardinal edge keys of a terrain cell (canonical domain definition).</summary>
public static class TerrainEdgeDirection
{
    public const string North = "n";
    public const string East = "e";
    public const string South = "s";
    public const string West = "w";
}

/// <summary>One entry of <see cref="TerrainModel.TerrainDirections"/> (`(typeof TerrainDirections)[number]`).</summary>
public sealed class TerrainDirection
{
    /// <summary>A <see cref="TerrainEdgeDirection"/> key.</summary>
    public readonly string key;
    public readonly string label;
    public readonly int dx;
    public readonly int dy;

    public TerrainDirection(string key, string label, int dx, int dy)
    {
        this.key = key;
        this.label = label;
        this.dx = dx;
        this.dy = dy;
    }
}

/// <summary>`TerrainPassageAxis = 'horizontal' | 'vertical'`.</summary>
public static class TerrainPassageAxis
{
    public const string Horizontal = "horizontal";
    public const string Vertical = "vertical";
}

public sealed class TerrainCleftProfile
{
    /// <summary>Direction sight/projectiles travel through the split wall (<see cref="TerrainPassageAxis"/>).</summary>
    public string passageAxis;
    public double groundZ;
    public double wallTopZ;
}

public sealed class TerrainUnderpassProfile
{
    /// <summary><see cref="TerrainPassageAxis"/>.</summary>
    public string passageAxis;
    public int span;
    /// <summary>Zero-based cell position from the negative anchor bank along the overhead bridge travel axis.</summary>
    public int spanIndex;
    public double deckBottom;
    public double deckTop;
    public double negativeSupportTop;
    public double positiveSupportTop;
}

public sealed class TerrainOverheadVolume
{
    public double bottom;
    public double top;
    /// <summary>Direction actors traverse through the covered passage (<see cref="TerrainPassageAxis"/>).</summary>
    public string passageAxis;
    /// <summary>Complete bridge length and this cell's stable position within it (used by continuous suspension curves).</summary>
    public int span;
    public int spanIndex;
    public double negativeSupportTop;
    public double positiveSupportTop;
}

/// <summary>`TerrainEdgeRelation` literals.</summary>
public static class TerrainEdgeRelation
{
    public const string Outer = "outer";
    public const string WallFace = "wall-face";
    public const string WallContact = "wall-contact";
    public const string WaterEdge = "water-edge";
    public const string ChasmEdge = "chasm-edge";
    public const string DeckEdge = "deck-edge";
    public const string Drop = "drop";
    public const string Rise = "rise";
    public const string Step = "step";
    public const string Level = "level";
}

/// <summary>`TerrainEdgeMaterial` literals.</summary>
public static class TerrainEdgeMaterial
{
    public const string Rock = "rock";
    public const string Bank = "bank";
    public const string Deck = "deck";
    public const string Earth = "earth";
    public const string Abyss = "abyss";
    public const string None = "none";
}

/// <summary>`TerrainFaceSegment['role']` literals (anonymous union in the original).</summary>
public static class TerrainFaceSegmentRole
{
    public const string Surface = "surface";
    public const string Chasm = "chasm";
}

public sealed class TerrainFaceSegment
{
    public double fromZ;
    public double toZ;
    public double drop;
    /// <summary><see cref="TerrainEdgeMaterial"/>.</summary>
    public string material;
    /// <summary><see cref="TerrainFaceSegmentRole"/>.</summary>
    public string role;
}

/// <summary>`TerrainMovementRule['reason']` literals (anonymous union in the original).</summary>
public static class TerrainMovementReason
{
    public const string Walkable = "walkable";
    public const string Outside = "outside";
    public const string OriginBlocked = "origin-blocked";
    public const string TargetBlocked = "target-blocked";
    public const string Solid = "solid";
    public const string Cleft = "cleft";
    public const string Water = "water";
    public const string Chasm = "chasm";
    public const string HeightStep = "height-step";
}

public sealed class TerrainMovementRule
{
    public bool passable;
    /// <summary><see cref="TerrainMovementReason"/>.</summary>
    public string reason;
    public double? delta;
    public double maxStep;
}

/// <summary>`TerrainHeightModel['role']` literals (anonymous union in the original).</summary>
public static class TerrainHeightRole
{
    public const string SolidVolume = "solid-volume";
    public const string FluidSurface = "fluid-surface";
    public const string DeckSurface = "deck-surface";
    public const string ChasmVoidDatum = "chasm-void-datum";
    public const string PerforatedWall = "perforated-wall";
    public const string Underpass = "underpass";
    public const string WalkSurface = "walk-surface";
}

/// <summary>`TerrainHeightModel['solidVolume']` shape: `{ bottom; top }`.</summary>
public sealed class TerrainSolidVolume
{
    public double bottom;
    public double top;
}

/// <summary>`TerrainHeightModel['fluidVolume']` shape: `{ surface; bottom }`.</summary>
public sealed class TerrainFluidVolume
{
    public double surface;
    public double bottom;
}

public sealed class TerrainHeightModel
{
    /// <summary><see cref="TerrainHeightRole"/>.</summary>
    public string role;
    public double elevation;
    public double topZ;
    public double baseZ;
    public double? walkZ;
    public double? waterLevel;
    public TerrainSolidVolume? solidVolume;
    public TerrainFluidVolume? fluidVolume;
    public TerrainCleftProfile? cleft;
    public TerrainOverheadVolume? overheadVolume;
}

public sealed class TerrainContact
{
    /// <summary>TileType, or <see cref="TerrainModel.TERRAIN_CONTACT_OUTSIDE"/> for 'outside'.</summary>
    public int type;
    public bool walkable;
    public bool solid;
    public double? elevation;
    public double? surfaceZ;
    public double? waterLevel;
}

public sealed class TerrainEdge
{
    /// <summary><see cref="TerrainEdgeDirection"/>.</summary>
    public string direction;
    public string label;
    /// <summary>TileType, or <see cref="TerrainModel.TERRAIN_CONTACT_OUTSIDE"/> for 'outside'.</summary>
    public int contactType;
    public double? contactElevation;
    public double? contactSurfaceZ;
    /// <summary><see cref="TerrainEdgeRelation"/>.</summary>
    public string relation;
    /// <summary><see cref="TerrainEdgeMaterial"/>.</summary>
    public string material;
    /// <summary>`readonly TerrainFaceSegment[]`; a List because materialization reuses and refills it in place.</summary>
    public List<TerrainFaceSegment> faceSegments;
    public double fromZ;
    public double toZ;
    public double delta;
    public double drop;
    public bool visibleFace;
    public bool passable;
    /// <summary><see cref="TransitionKind"/>.</summary>
    public string transition;
    /// <summary><see cref="TerrainMovementReason"/>.</summary>
    public string blockReason;
    public double? walkDelta;
    public double maxStep;
}

/// <summary>
/// `Record&lt;TerrainEdgeDirection, T&gt;`. Keys are 'n'/'e'/'s'/'w'; <see cref="values"/> mirrors
/// `Object.values(record)`, whose order is the insertion order materialization always uses (n, e, s, w).
/// </summary>
public sealed class TerrainDirectionRecord<T> where T : class
{
    public T? n;
    public T? e;
    public T? s;
    public T? w;

    public T? this[string direction]
    {
        get => direction switch
        {
            "n" => n,
            "e" => e,
            "s" => s,
            "w" => w,
            _ => null,
        };
        set
        {
            switch (direction)
            {
                case "n":
                    n = value;
                    break;
                case "e":
                    e = value;
                    break;
                case "s":
                    s = value;
                    break;
                case "w":
                    w = value;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown terrain edge direction '{direction}'");
            }
        }
    }

    /// <summary>`Object.values(record)` (absent keys are skipped).</summary>
    public List<T> values()
    {
        var result = new List<T>(4);
        if (n != null) result.Add(n);
        if (e != null) result.Add(e);
        if (s != null) result.Add(s);
        if (w != null) result.Add(w);
        return result;
    }

    // PORT ADDITIONS (allocation): `Object.values(record).filter(p).length` / `.some(p)` / `.every(p)` without the
    // two arrays per call — same order (n, e, s, w), same skipping of absent keys, same short-circuiting.

    /// <summary>`Object.values(record).filter(predicate).length`.</summary>
    public int countValues(Func<T, bool> predicate)
    {
        int count = 0;
        if (n != null && predicate(n)) count++;
        if (e != null && predicate(e)) count++;
        if (s != null && predicate(s)) count++;
        if (w != null && predicate(w)) count++;
        return count;
    }

    /// <summary>`Object.values(record).some(predicate)`.</summary>
    public bool someValue(Func<T, bool> predicate) =>
        (n != null && predicate(n)) ||
        (e != null && predicate(e)) ||
        (s != null && predicate(s)) ||
        (w != null && predicate(w));

    /// <summary>`Object.values(record).every(predicate)`.</summary>
    public bool everyValue(Func<T, bool> predicate) =>
        (n == null || predicate(n)) &&
        (e == null || predicate(e)) &&
        (s == null || predicate(s)) &&
        (w == null || predicate(w));
}

public sealed class TerrainCell
{
    public int id;
    public int x;
    public int y;
    /// <summary>TileType.</summary>
    public int type;
    public double elevation;
    public double? waterLevel;
    /// <summary>TerrainBridgeSpan (TileType.Water/Chasm/Floor) or null.</summary>
    public int? span;
    public double surfaceZ;
    public double baseZ;
    public double? walkZ;
    public TerrainHeightModel height;
    public bool walkable;
    public bool solid;
    public bool blocksSight;
    public TerrainDirectionRecord<TerrainContact> contacts;
    public TerrainDirectionRecord<TerrainEdge> edges;

    /// <summary>Shallow copy, the equivalent of `{ ...cell }`.</summary>
    public TerrainCell Clone() => (TerrainCell)MemberwiseClone();
}

public sealed class MaterializedTerrain
{
    public int width;
    public int height;
    public TerrainCell[] cells;
    public byte[] tiles;
    public sbyte[]? elevation;
    /// <summary>Optional semantic ground-material layer copied from the generated/authored terrain artifact.</summary>
    public byte[]? surface;
    /// <summary>Optional semantic circulation layer copied from the authored/generated terrain artifact.</summary>
    public byte[]? floorUsage;
    /// <summary>Optional region-local authored decoration placements consumed by render planning.</summary>
    public List<TerrainDecorationPlacement>? decorations;
}

public sealed class TerrainModelOptions
{
    public double? maxStep;
    /// <summary>(tx, ty, storedElevation) → wall rise.</summary>
    public Func<int, int, double, double>? solidWallRiseAt;
    /// <summary>(tx, ty, tile) → water surface level.</summary>
    public Func<int, int, int, double>? waterLevelAt;
    /// <summary>
    /// Pre-classified terrain below Bridge cells, resolved from the complete owning map/chunk.
    ///
    /// Render bakes intentionally materialize a small clipped window. A broad deck can fill that complete
    /// window, leaving no exposed Water/Chasm seed for the local classifier and making the deck appear to span
    /// ordinary Floor. Supplying the owning layout's classification keeps materialization invariant under those
    /// presentation-only clips. Non-Bridge entries use TERRAIN_BRIDGE_SPAN_NONE.
    /// </summary>
    public byte[]? bridgeSpans;
    /// <summary>
    /// Water datums already resolved over the complete owning layout/chunk, parallel to the input tiles.
    /// Presentation bakes are clipped windows and must not invent a new hydrology merely because a broad Bridge
    /// hides every exposed source outside their border. Non-carriers use NaN.
    /// (`ArrayLike&lt;number&gt;`; every producer hands a Float32Array, hence `float[]`.)
    /// </summary>
    public float[]? resolvedWaterLevels;
}

public static partial class TerrainModel
{
    /// <summary>C# stand-in for the `'outside'` member of `TileType | 'outside'`.</summary>
    public const int TERRAIN_CONTACT_OUTSIDE = -1;

    public static class TERRAIN_PHYSICS
    {
        public const double maxStep = 1;
    }

    // ── The world's vertical domain ──────────────────────────────────────────────────────────────────
    //
    // Ground, walls and water occupy the complete signed −25..+25 band. Chasm goes deeper than them, down to
    // TERRAIN_MIN_ELEVATION (−30), so a watercourse arriving at a rim always has somewhere to fall.
    //
    // These bounds ARE the domain, and every authoring clamp, editor range, renderer height band and picking
    // ray must be derived from them rather than restated — a second copy of "how high can the world be" is how
    // the generator ended up showing a range the model had already outgrown.
    //
    // The elevation layer is a SIGNED byte layer (TerrainElevationLayer) precisely so this domain needs no bias
    // arithmetic: a stored level means the same thing on both sides of zero. Chasm remains the single exception
    // and stores its depth as a positive magnitude — see chasmDepthFromStored.
    public const int CHASM_MIN_DEPTH = 5;
    /// <summary>
    /// Chasm reaches FIVE levels past the mirror of the ground ceiling. That asymmetry is the point: a canyon is
    /// allowed to be deeper than anything can stand on or float at, so a watercourse arriving at its rim always
    /// has somewhere to fall to. See WATER_MIN_STORED_LEVEL.
    /// </summary>
    public const int CHASM_MAX_DEPTH = 30;
    public const int CHASM_DEFAULT_DEPTH = 6;
    /// <summary>Deepest authored datum in the world. Chasm alone reaches it.</summary>
    public const int TERRAIN_MIN_ELEVATION = -CHASM_MAX_DEPTH;
    /// <summary>Lowest walkable ground/wall datum; Chasm alone may descend below it.</summary>
    public const int TERRAIN_MIN_GROUND_ELEVATION = MIN_ELEVATION;
    /// <summary>Highest authored datum in the world. Re-exported so the domain reads as one pair, not two imports.</summary>
    public const int TERRAIN_MAX_ELEVATION = MAX_ELEVATION;
    /// <summary>
    /// Visual shaft-wall depth below every authored Chasm throat before it meets that cell's real deep floor.
    ///
    /// Four additional levels leave enough wall for a clear depth gradient while keeping the deliberately visible
    /// bottom plane inside the normal orthographic composition. Movement continues to use `walkZ` and is unaffected.
    /// </summary>
    public const int CHASM_VOID_WALL_EXTENSION = 4;
    /// <summary>
    /// Deepest possible Chasm floor datum. Materialization resolves the complete Chasm topology into nested floor
    /// terraces descending from CHASM_ABYSS_SURFACE_MAX_Z: the rim remains high, broad interior plateaus descend
    /// towards the medial core, and only a genuinely wide basin reaches this datum. Movement still uses `walkZ`
    /// and never treats any deep floor as standable terrain.
    ///
    /// It sits BELOW the authored domain floor on purpose — this is the visual shaft backing under the deepest
    /// authorable throat, not a datum anything can be placed at.
    /// </summary>
    public const int CHASM_ABYSS_SURFACE_Z = -(CHASM_MAX_DEPTH + CHASM_VOID_WALL_EXTENSION);
    /// <summary>Highest possible materialized Chasm floor terrace.</summary>
    public const int CHASM_ABYSS_SURFACE_MAX_Z = -(CHASM_MIN_DEPTH + CHASM_VOID_WALL_EXTENSION);

    /// <summary>
    /// Water stores the datum of its authored terrace in the shared elevation byte, and that datum is SYMMETRIC
    /// around zero: a tarn can sit on a +25 summit shelf and a flooded basin floor can lie at −25. The horizontal
    /// fluid surface sits a small, constant amount below the datum, leaving a readable bank lip while still
    /// allowing one connected watercourse to own several physical levels.
    ///
    /// Water deliberately stops short of the domain floor that Chasm reaches (CHASM_MAX_DEPTH). A body of water
    /// is a surface something rests ON; a chasm is an absence. Keeping the canyon strictly deeper than any water
    /// table is what guarantees that a watercourse meeting a rim always has somewhere to FALL — see
    /// createTerrainWaterfalls. If the two floors were equal, the deepest rivers would simply end at a wall.
    ///
    /// Both bounds are derived from the ground ceiling, never restated: water is not allowed to hold its own
    /// opinion about how high the world goes.
    /// </summary>
    public const int WATER_MIN_STORED_LEVEL = -TERRAIN_MAX_ELEVATION;
    public const int WATER_MAX_STORED_LEVEL = TERRAIN_MAX_ELEVATION;
    public const double WATER_SURFACE_DATUM_DROP = 0.62;
    /// <summary>Guaranteed wall rise before optional skyline variation. Shared by high-structure eligibility and rendering.</summary>
    public const double TERRAIN_STANDARD_WALL_BASE_RISE = 2.4;
    /// <summary>The finished Underpass deck must clear its passage floor by at least this many terrain levels.</summary>
    public const int UNDERPASS_MIN_SUPPORT_CLEARANCE = 4;
    /// <summary>Minimum natural bank height that can carry a suspension anchor tower without reading as a freestanding gate.</summary>
    public const double UNDERPASS_MIN_BANK_CLEARANCE = 2.8;
    /// <summary>Maximum extra height supplied by the suspension bridge's anchored end towers above the natural wall bank.</summary>
    public const double UNDERPASS_MAX_ANCHOR_RISE = UNDERPASS_MIN_SUPPORT_CLEARANCE - UNDERPASS_MIN_BANK_CLEARANCE;
    /// <summary>Smallest useful overhead bridge: one-cell lintels read as door frames rather than traversable bridges.</summary>
    public const int UNDERPASS_MIN_SPAN = 2;
    /// <summary>
    /// Longest supported overhead bridge. Standard Endless corridors are five cells wide; ten cells let a deck
    /// reach the next genuinely rooted mountain shoulder instead of accepting a nearer pillar or disappearing.
    /// The span remains below room scale, and generator and validator deliberately share this one bound.
    /// </summary>
    public const int UNDERPASS_MAX_SPAN = 10;
    /// <summary>Physical thickness of the flat bridge slab spanning an Underpass.</summary>
    public const double UNDERPASS_DECK_THICKNESS = 0.62;
    /// <summary>A Cleft needs enough vertical wall mass for its negative opening to remain legible at gameplay distance.</summary>
    public const double CLEFT_MIN_WALL_CLEARANCE = 2.8;
    public const double TERRAIN_WATERFALL_MIN_DROP = 0.72;

    public static double chasmDepthFromStored(double storedDepth)
    {
        if (!Number.isFinite(storedDepth)) return CHASM_DEFAULT_DEPTH;
        double depth = Math.round(storedDepth);
        return depth >= CHASM_MIN_DEPTH && depth <= CHASM_MAX_DEPTH ? depth : CHASM_DEFAULT_DEPTH;
    }

    /// <summary>Never written (TS `as const`). An array, not an IReadOnlyList: `foreach` over the interface boxes an
    /// enumerator per loop, and the bake runs these loops per cell.</summary>
    public static readonly TerrainDirection[] TerrainDirections = new[]
    {
        new TerrainDirection(TerrainEdgeDirection.North, "N", 0, -1),
        new TerrainDirection(TerrainEdgeDirection.East, "E", 1, 0),
        new TerrainDirection(TerrainEdgeDirection.South, "S", 0, 1),
        new TerrainDirection(TerrainEdgeDirection.West, "W", -1, 0),
    };

    private const double FACE_EPSILON = 0.08;
    /// <summary>
    /// How deep (levels) a fluid basin floor sits below its water surface — exported so renderers sink real
    /// basin geometry to exactly the modelled depth (one height model, shared kernel).
    /// </summary>
    public const double WATER_BASIN_DEPTH = 0.88;
    /// <summary>Visual deck lift (levels) of a bridge above its walk elevation — exported for the same reason.</summary>
    public const double BRIDGE_DECK_LIFT = 0.18;
    /// <summary>Complete top-to-soffit thickness. Every deck renderer consumes this one physical cross-section.</summary>
    public const double BRIDGE_DECK_THICKNESS = 0.14;
    /// <summary>
    /// Required empty space between the underside of a Bridge deck and its hidden Water span. The normal Water
    /// datum inset already provides this exact gap when Water and deck share one stored level:
    /// `deck underside = elevation + lift - thickness`, `Water = elevation - WATER_SURFACE_DATUM_DROP`.
    /// </summary>
    public const double BRIDGE_MIN_WATER_CLEARANCE = WATER_SURFACE_DATUM_DROP + BRIDGE_DECK_LIFT - BRIDGE_DECK_THICKNESS;
    private const double BRIDGE_WATER_EPSILON = 0.001;

    private static double storedElevationAt(sbyte[]? elevation, int width, int height, int tx, int ty)
    {
        if (elevation == null || !inBounds(width, height, tx, ty)) return 0;
        int index = tileIndex(width, tx, ty);
        // `elevation[i] ?? 0`: an index past a short layer reads undefined → 0.
        return (uint)index < (uint)elevation.Length ? elevation[index] : 0;
    }

    private static string terrainPassageAxisAt(byte[] tiles, int width, int height, int tx, int ty, int componentTile)
    {
        int approach(int dx, int dy)
        {
            int nx = tx + dx;
            int ny = ty + dy;
            if (!inBounds(width, height, nx, ny)) return 0;
            int tile = tiles[tileIndex(width, nx, ny)];
            return tile != componentTile && isWalkable(tile) ? 1 : 0;
        }
        int horizontal = approach(-1, 0) + approach(1, 0);
        int vertical = approach(0, -1) + approach(0, 1);
        if (horizontal != vertical) return horizontal > vertical ? TerrainPassageAxis.Horizontal : TerrainPassageAxis.Vertical;

        int component(int dx, int dy)
        {
            int nx = tx + dx;
            int ny = ty + dy;
            return inBounds(width, height, nx, ny) && tiles[tileIndex(width, nx, ny)] == componentTile ? 1 : 0;
        }
        int northSouthSpan = component(0, -1) + component(0, 1);
        int eastWestSpan = component(-1, 0) + component(1, 0);
        return northSouthSpan >= eastWestSpan ? TerrainPassageAxis.Horizontal : TerrainPassageAxis.Vertical;
    }

    /// <summary>Direction sight/projectiles traverse through a body-blocking Cleft.</summary>
    public static string terrainCleftAxisAt(byte[] tiles, int width, int height, int tx, int ty) =>
        terrainPassageAxisAt(tiles, width, height, tx, ty, TileType.Cleft);

    /// <summary>Direction actors traverse below an Underpass bridge deck.</summary>
    public static string terrainUnderpassAxisAt(byte[] tiles, int width, int height, int tx, int ty) =>
        terrainPassageAxisAt(tiles, width, height, tx, ty, TileType.Underpass);

    // type WallRiseAt = (tx, ty, storedElevation) => number  →  Func<int, int, double, double>

    private static double wallTopAt(
        sbyte[]? elevation,
        int width,
        int height,
        int tx,
        int ty,
        Func<int, int, double, double>? wallRiseAt = null)
    {
        double stored = storedElevationAt(elevation, width, height, tx, ty);
        return stored + (wallRiseAt?.Invoke(tx, ty, stored) ?? TERRAIN_STANDARD_WALL_BASE_RISE);
    }

    /// <summary>
    /// Whether one terminal Underpass support belongs to a real rock shoulder rather than to a freestanding pillar.
    ///
    /// A single high Solid cell used to satisfy the structural profile. The suspension portal then hid most of that
    /// lone column and the deck read as ending in open air. A truthful mountain bank has depth away from the span and
    /// breadth along it: the support must be part of a 2x2 Solid root extending away from the bridge. The asymmetric
    /// lateral choice permits organic cliff edges without weakening the no-floating-end guarantee.
    /// </summary>
    public static bool terrainUnderpassBankIsAnchored(
        byte[] tiles,
        int width,
        int height,
        int supportX,
        int supportY,
        int outwardX,
        int outwardY,
        int tangentX,
        int tangentY)
    {
        bool solidAt(int tx, int ty) => inBounds(width, height, tx, ty) && tiles[tileIndex(width, tx, ty)] == TileType.Solid;
        if (!solidAt(supportX, supportY) || !solidAt(supportX + outwardX, supportY + outwardY))
            return false;
        // `([-1, 1] as const).some(...)`
        for (int side = -1; side <= 1; side += 2)
        {
            if (
                solidAt(supportX + tangentX * side, supportY + tangentY * side) &&
                solidAt(supportX + outwardX + tangentX * side, supportY + outwardY + tangentY * side))
                return true;
        }
        return false;
    }

    /// <summary>Validate and describe a one-cell-thick wall split with two same-level open sight approaches.</summary>
    public static TerrainCleftProfile? terrainCleftProfileAt(
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        int tx,
        int ty,
        Func<int, int, double, double>? wallRiseAt = null)
    {
        if (!inBounds(width, height, tx, ty) || tiles[tileIndex(width, tx, ty)] != TileType.Cleft)
            return null;
        string passageAxis = terrainCleftAxisAt(tiles, width, height, tx, ty);
        int alongX = passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        int alongY = passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        int tangentX = passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        int tangentY = passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        var approachLevels = new List<double>(2);
        for (int side = -1; side <= 1; side += 2)
        {
            int ax = tx + alongX * side;
            int ay = ty + alongY * side;
            int sx = tx + tangentX * side;
            int sy = ty + tangentY * side;
            if (!inBounds(width, height, ax, ay) || !inBounds(width, height, sx, sy)) return null;
            int approachTile = tiles[tileIndex(width, ax, ay)];
            if (!isWalkable(approachTile) || tiles[tileIndex(width, sx, sy)] != TileType.Solid) return null;
            approachLevels.push(storedElevationAt(elevation, width, height, ax, ay));
        }
        if (approachLevels[0] != approachLevels[1]) return null;
        double groundZ = approachLevels[0];
        double wallTopZ = wallTopAt(elevation, width, height, tx, ty, wallRiseAt);
        if (wallTopZ - groundZ < CLEFT_MIN_WALL_CLEARANCE) return null;
        return new TerrainCleftProfile { passageAxis = passageAxis, groundZ = groundZ, wallTopZ = wallTopZ };
    }

    /// <summary>
    /// Describe the complete two-level contract of an Underpass. Both terminal Solid banks must independently carry
    /// at least +2.8 of geological height. A bounded suspension-anchor tower may supply the remaining rise, but the
    /// resulting deck always clears the passage floor by at least +4.
    /// </summary>
    public static TerrainUnderpassProfile? terrainUnderpassProfileAt(
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        int tx,
        int ty,
        Func<int, int, double, double>? wallRiseAt = null)
    {
        if (!inBounds(width, height, tx, ty) || tiles[tileIndex(width, tx, ty)] != TileType.Underpass)
            return null;
        string passageAxis = terrainUnderpassAxisAt(tiles, width, height, tx, ty);
        int crossX = passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        int crossY = passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        double passageZ = storedElevationAt(elevation, width, height, tx, ty);
        int negative = 0;
        int positive = 0;
        bool sameSpan(int offset)
        {
            int x = tx + crossX * offset;
            int y = ty + crossY * offset;
            return inBounds(width, height, x, y) &&
                tiles[tileIndex(width, x, y)] == TileType.Underpass &&
                storedElevationAt(elevation, width, height, x, y) == passageZ;
        }
        while (sameSpan(-(negative + 1))) negative++;
        while (sameSpan(positive + 1)) positive++;
        int span = negative + 1 + positive;
        if (span < UNDERPASS_MIN_SPAN || span > UNDERPASS_MAX_SPAN) return null;
        int alongX = passageAxis == TerrainPassageAxis.Horizontal ? 1 : 0;
        int alongY = passageAxis == TerrainPassageAxis.Horizontal ? 0 : 1;
        for (int offset = -negative; offset <= positive; offset++)
        {
            int x = tx + crossX * offset;
            int y = ty + crossY * offset;
            for (int side = -1; side <= 1; side += 2)
            {
                int approachX = x + alongX * side;
                int approachY = y + alongY * side;
                if (!inBounds(width, height, approachX, approachY)) return null;
                int approachTile = tiles[tileIndex(width, approachX, approachY)];
                if (
                    !isWalkable(approachTile) ||
                    approachTile == TileType.Underpass ||
                    storedElevationAt(elevation, width, height, approachX, approachY) != passageZ)
                    return null;
            }
        }
        int negativeX = tx - crossX * (negative + 1);
        int negativeY = ty - crossY * (negative + 1);
        int positiveX = tx + crossX * (positive + 1);
        int positiveY = ty + crossY * (positive + 1);
        if (
            !inBounds(width, height, negativeX, negativeY) ||
            !inBounds(width, height, positiveX, positiveY) ||
            tiles[tileIndex(width, negativeX, negativeY)] != TileType.Solid ||
            tiles[tileIndex(width, positiveX, positiveY)] != TileType.Solid)
            return null;
        if (
            !terrainUnderpassBankIsAnchored(
                tiles,
                width,
                height,
                negativeX,
                negativeY,
                -crossX,
                -crossY,
                alongX,
                alongY) ||
            !terrainUnderpassBankIsAnchored(
                tiles,
                width,
                height,
                positiveX,
                positiveY,
                crossX,
                crossY,
                alongX,
                alongY))
            return null;
        double negativeSupportTop = wallTopAt(elevation, width, height, negativeX, negativeY, wallRiseAt);
        double positiveSupportTop = wallTopAt(elevation, width, height, positiveX, positiveY, wallRiseAt);
        double naturalDeckTop = Math.min(negativeSupportTop, positiveSupportTop);
        if (
            negativeSupportTop - passageZ < UNDERPASS_MIN_BANK_CLEARANCE ||
            positiveSupportTop - passageZ < UNDERPASS_MIN_BANK_CLEARANCE ||
            passageZ + UNDERPASS_MIN_SUPPORT_CLEARANCE - naturalDeckTop > UNDERPASS_MAX_ANCHOR_RISE + 1e-6)
            return null;
        double deckTop = Math.max(passageZ + UNDERPASS_MIN_SUPPORT_CLEARANCE, naturalDeckTop);
        return new TerrainUnderpassProfile
        {
            passageAxis = passageAxis,
            span = span,
            spanIndex = negative,
            deckBottom = deckTop - UNDERPASS_DECK_THICKNESS,
            deckTop = deckTop,
            negativeSupportTop = negativeSupportTop,
            positiveSupportTop = positiveSupportTop,
        };
    }

    public static bool terrainTileCarriesWater(int tile, int? bridgeSpan = null) =>
        tile == TileType.Water || (tile == TileType.Bridge && bridgeSpan == TileType.Water);

    /// <summary>
    /// The physical liquid carrier of a materialized cell. Consumers must use this instead of treating every
    /// Bridge as fluid (which leaks Water into Floor/Chasm spans) or only accepting raw Water (which cuts the same
    /// body at its covered cells). The safe-clearance guard is deliberately separate: topology answers what the
    /// authored cell carries; validation answers whether a malformed deck can display it safely.
    /// </summary>
    public static bool terrainCellCarriesWater(TerrainCell? cell) =>
        cell != null && terrainTileCarriesWater(cell.type, cell.span);

    /// <summary>
    /// A Chasm-crossing Bridge suspends its deck above the same physical deep floor as every exposed Chasm cell.
    /// Keeping this topology predicate in the shared model prevents render-plan ecology, floor relief and geometry
    /// emission from disagreeing about whether the under-deck tile belongs to the materialized abyss floor.
    /// </summary>
    public static bool terrainCellCarriesChasmFloor(TerrainCell cell) =>
        cell.type == TileType.Chasm || (cell.type == TileType.Bridge && cell.span == TileType.Chasm);

    /// <summary>
    /// How far inward the basin field looks for a rim. It is the same reach terrainChasmBasinFloorZAt searches,
    /// and the two must agree: the ladder below is written to arrive exactly at the deepest floor at this inset,
    /// so the "no rim in reach" case is the natural end of the descent rather than a separate answer.
    /// </summary>
    public const int CHASM_BASIN_REACH_CELLS = 8;

    /// <summary>Cells of inset one deep-floor terrace spans before the next one starts.</summary>
    private const int CHASM_BASIN_BAND_CELLS = 3;

    /// <summary>
    /// How many terraces BELOW the standard void wall a basin floor sinks at a given inset from the visible rim:
    /// one per CHASM_BASIN_BAND_CELLS cells travelled inward, and never more than the reach can carry.
    ///
    /// A relative drop, not an absolute datum — the caller hangs it off the local throat.
    ///
    /// Two things this deliberately does NOT do. It does not jump to the deepest possible floor once the search
    /// runs out — that was a hidden cliff, invisible only while the abyss datum happened to sit one level under
    /// the last band, and a nineteen-level wall between neighbouring cells the moment the domain deepened to
    /// −25. And it does not stretch itself to reach that datum either: this ladder is a shared basin field with
    /// no idea how deep the local throat is, so anchoring it to the world's floor would sink a shallow crack to
    /// canyon depth. The genuinely deep backing is per-cell and already exact — a −25 throat gets its −29 shaft
    /// from `baseZ = surfaceZ − CHASM_VOID_WALL_EXTENSION`, without this function's help.
    /// </summary>
    private static double terrainChasmFloorDropForInset(int inset)
    {
        if (inset <= 0) return 0;
        int reached = Math.min(inset, CHASM_BASIN_REACH_CELLS);
        return Math.ceil((double)reached / CHASM_BASIN_BAND_CELLS);
    }

    /// <summary>
    /// Resolve one Chasm carrier into a coherent basin terrace. Cardinal distance from the visible rim is the
    /// topology field, so depth always increases towards the medial core instead of following authored per-cell
    /// noise. The rim owns one shallow band; subsequent bands are three cells wide. Typical pits therefore expose
    /// only two internal height edges, while very broad basins can still reach the authored abyss datum.
    ///
    /// A Manhattan ring is intentional here. Chasm walls exist on cardinal tile edges, so a merely diagonal Floor
    /// contact must not pull an otherwise interior Chasm cell back up and create a bright staircase at every corner.
    /// </summary>
    private static double terrainChasmBasinFloorZAt(MaterializedTerrain terrain, TerrainCell cell)
    {
        int inset = CHASM_BASIN_REACH_CELLS;
        for (int radius = 1; radius <= CHASM_BASIN_REACH_CELLS; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.abs(dx) + Math.abs(dy) != radius) continue;
                    TerrainCell? candidate = terrainCellAt(terrain, cell.x + dx, cell.y + dy);
                    if (candidate != null && terrainCellCarriesChasmFloor(candidate)) continue;
                    inset = radius - 1;
                    goto search_done; // `break search`
                }
            }
        }
    search_done:
        // The floor hangs off THIS shaft's throat, never off a world-wide datum. Anchoring it globally is what
        // put a −9 floor under a −30 opening: an inverted volume with nothing to close it, which the camera then
        // looked straight through to the backdrop, and which left a falling watercourse landing above its own rim.
        //
        // The DEEPEST throat in reach anchors it, not this cell's own and not the shallowest. That is what keeps a
        // basin of staggered throats reading as one coherent bottom instead of a staircase — the property the old
        // global datum delivered by accident, here delivered on purpose and at whatever depth the basin was cut to.
        double throatZ = chasmThroatNear(terrain, cell, "deepest") ?? -CHASM_MIN_DEPTH;
        return Math.max(
            CHASM_ABYSS_SURFACE_Z,
            throatZ - CHASM_VOID_WALL_EXTENSION - terrainChasmFloorDropForInset(inset));
    }

    /// <summary>
    /// A real Chasm throat within reach, or `null` when there is none.
    ///
    /// The two callers want opposite ends of the same scan, and both are load-bearing. A CROSSING wants the
    /// shallowest: a deck may never cut deeper than the opening it spans. A basin FLOOR wants the deepest, so a
    /// cluster of staggered throats resolves to one shared bottom rather than a private terrace per cell.
    ///
    /// Kept separate from terrainChasmThroatZAt so the basin floor can consult it without recursing back through
    /// the throat resolver, which itself falls back to the floor.
    /// </summary>
    /// <param name="end">'shallowest' | 'deepest'.</param>
    private static double? chasmThroatNear(MaterializedTerrain terrain, TerrainCell cell, string end)
    {
        // `const pick = end === 'deepest' ? Math.min : Math.max;`
        bool deepest = end == "deepest";
        double? throat = cell.type == TileType.Chasm ? cell.surfaceZ : null;
        // A crossing stops at the first ring that answers; a floor keeps widening so the whole basin votes.
        if (throat != null && end == "shallowest") return throat;
        for (int radius = 1; radius <= CHASM_BASIN_REACH_CELLS && (end == "deepest" || throat == null); radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.abs(dx) + Math.abs(dy) != radius) continue;
                    TerrainCell? candidate = terrainCellAt(terrain, cell.x + dx, cell.y + dy);
                    if (candidate == null || candidate.type != TileType.Chasm) continue;
                    throat = throat == null
                        ? candidate.surfaceZ
                        : deepest
                            ? Math.min(throat.Value, candidate.surfaceZ)
                            : Math.max(throat.Value, candidate.surfaceZ);
                }
            }
        }
        return throat;
    }

    /// <summary>
    /// Resolve the physical deep-floor terrace below a Chasm carrier. Real Chasm cells own the materialized value;
    /// a suspended Bridge evaluates the same basin field at its own position, so a deck cannot flatten or displace
    /// the medial depth progression underneath it.
    /// </summary>
    public static double? terrainChasmFloorZAt(MaterializedTerrain terrain, TerrainCell cell)
    {
        if (cell.type == TileType.Chasm) return cell.baseZ;
        if (!terrainCellCarriesChasmFloor(cell)) return null;
        return terrainChasmBasinFloorZAt(terrain, cell);
    }

    /// <summary>
    /// Resolve the THROAT of the shaft a Chasm carrier opens — the datum where the opaque geological backing ends
    /// and the open void begins. Together with terrainChasmFloorZAt it is the complete vertical contract of a
    /// Chasm opening, and it exists because `cell.surfaceZ`/`cell.baseZ` describe the DECK of a Chasm-crossing
    /// Bridge, not the shaft below it.
    ///
    /// Reading the deck datums instead was one defect with several faces: a pond ending at a Bridge-crossed shaft
    /// got no geological drop wall and no falling sheet (its "open side"), and the shaft itself was closed only
    /// across the few pixels of the deck fascia — so the camera looked straight through the world to the backdrop.
    ///
    /// A real Chasm returns exactly its own materialized datum, so no existing topology changes. A Bridge mirrors
    /// the shaft it crosses: the shallowest throat among the Chasm carriers of that channel, which keeps a crossing
    /// continuous with the opening on either side of it. A deck with no exposed Chasm anywhere in reach falls back
    /// to the standard CHASM_VOID_WALL_EXTENSION above its own floor terrace — the same relation materialization
    /// gives an authored Chasm cell.
    /// </summary>
    public static double? terrainChasmThroatZAt(MaterializedTerrain terrain, TerrainCell cell)
    {
        if (cell.type == TileType.Chasm) return cell.surfaceZ;
        if (!terrainCellCarriesChasmFloor(cell)) return null;
        double? throat = chasmThroatNear(terrain, cell, "shallowest");
        if (throat != null) return throat;
        double floorZ = terrainChasmBasinFloorZAt(terrain, cell);
        return floorZ + CHASM_VOID_WALL_EXTENSION;
    }

    /// <summary>
    /// A Bridge's open flanks may border either visible Water or the continuation of that channel into a Chasm.
    /// Bridge neighbours are deliberately excluded: they describe deck continuity, not the crossed span.
    /// </summary>
    /// <param name="contactType">TileType or TERRAIN_CONTACT_OUTSIDE.</param>
    public static bool terrainBridgeOpenSpanContact(int contactType) =>
        contactType == TileType.Water || contactType == TileType.Chasm;

    /// <summary>
    /// Resolve the direction travelled across a Bridge from materialized cardinal topology.
    ///
    /// Deck continuity is the primary signal. This is important for the end of a long bridge: a Chasm in front of
    /// the last deck cell must not rotate that one cell ninety degrees. A single isolated cell has no continuity
    /// signal, so its Water/Chasm flanks select the perpendicular travel axis. Completely symmetric junctions use
    /// one deterministic fallback and therefore cannot change with renderer bake clipping.
    /// </summary>
    public static string terrainBridgeTravelAxis(TerrainCell cell)
    {
        int horizontalDeck =
            (cell.edges.e!.contactType == TileType.Bridge ? 1 : 0) +
            (cell.edges.w!.contactType == TileType.Bridge ? 1 : 0);
        int verticalDeck =
            (cell.edges.n!.contactType == TileType.Bridge ? 1 : 0) +
            (cell.edges.s!.contactType == TileType.Bridge ? 1 : 0);
        int horizontalSpan =
            (terrainBridgeOpenSpanContact(cell.edges.e.contactType) ? 1 : 0) +
            (terrainBridgeOpenSpanContact(cell.edges.w.contactType) ? 1 : 0);
        int verticalSpan =
            (terrainBridgeOpenSpanContact(cell.edges.n.contactType) ? 1 : 0) +
            (terrainBridgeOpenSpanContact(cell.edges.s.contactType) ? 1 : 0);

        // A production deck is two cells WIDE. Its cells therefore participate in both deck axes: one neighbour is
        // the other half of the width while up to two neighbours continue the actual crossing. Treating any such
        // cell as a two-dimensional platform forced every horizontal crossing onto the vertical plank lattice. The
        // longer continuation wins; at its two end cells the open Water/Chasm flank breaks the one-to-one tie. Only
        // a genuinely symmetric junction reaches the world-stable fallback below.
        if (horizontalDeck != verticalDeck)
            return horizontalDeck > verticalDeck ? TerrainPassageAxis.Horizontal : TerrainPassageAxis.Vertical;
        if (horizontalDeck > 0 && horizontalSpan != verticalSpan)
            return horizontalSpan > verticalSpan ? TerrainPassageAxis.Vertical : TerrainPassageAxis.Horizontal;
        if (horizontalDeck > 0) return TerrainPassageAxis.Vertical;
        if (horizontalSpan != verticalSpan)
        {
            return horizontalSpan > verticalSpan ? TerrainPassageAxis.Vertical : TerrainPassageAxis.Horizontal;
        }
        return TerrainPassageAxis.Vertical;
    }

    /// <summary>
    /// A hidden under-Bridge Water basin must stay open where its fluid path enters Chasm. The thin deck fascia
    /// remains above it; below the Water line, waterfall shoulders own only the area outside the falling sheet.
    /// </summary>
    /// <param name="direction">A TerrainEdgeDirection key.</param>
    public static bool terrainBridgeWaterOpensIntoChasm(TerrainCell cell, string direction) =>
        cell.type == TileType.Bridge &&
        cell.span == TileType.Water &&
        cell.waterLevel != null &&
        cell.edges[direction]!.contactType == TileType.Chasm;

    public static double waterSurfaceLevelFromStored(double storedLevel)
    {
        double finite = Number.isFinite(storedLevel) ? Math.round(storedLevel) : WATER_MIN_STORED_LEVEL;
        double datum = Math.max(WATER_MIN_STORED_LEVEL, Math.min(WATER_MAX_STORED_LEVEL, finite));
        return datum - WATER_SURFACE_DATUM_DROP;
    }

    /// <summary>Highest physically safe Water surface below a Bridge with this stored walk/deck elevation.</summary>
    public static double bridgeWaterSurfaceCeilingFromStored(double storedDeckLevel)
    {
        double finite = Number.isFinite(storedDeckLevel) ? Math.round(storedDeckLevel) : WATER_MIN_STORED_LEVEL;
        return finite + BRIDGE_DECK_LIFT - BRIDGE_DECK_THICKNESS - BRIDGE_MIN_WATER_CLEARANCE;
    }

    /// <summary>
    /// Resolve the one physical Water datum carried by every Water and Water-span Bridge cell.
    ///
    /// Exposed Water owns authored terraces. Covered cells inherit the nearest exposed owner through their complete
    /// Bridge component, capped only by the local deck clearance. This preserves legitimate opposing terraces while
    /// making every safe Water-to-Bridge boundary numerically identical. Callers rendering clipped windows should
    /// supply the result resolved over the complete owning layout rather than re-solving an arbitrary crop.
    /// </summary>
    /// <remarks>Assumes `tiles.Length == width * height` (the JS would grow its plain result array otherwise).</remarks>
    public static double[] deriveTerrainWaterLevels(
        byte[] tiles,
        byte[] bridgeSpans,
        sbyte[]? elevation,
        int width,
        int height,
        TerrainModelOptions options,
        double[]? reuse = null)
    {
        int count = width * height;
        double[] levels = reuse != null && reuse.Length == count ? reuse : new double[count];
        for (int index = 0; index < count; index++) levels[index] = double.NaN;

        // A Water tile owns its physical terrace. Adjacent Water at another stored datum is intentionally not
        // flattened into the same component: that edge is the authoritative start of a cascade.
        for (int index = 0; index < tiles.Length; index++)
        {
            if (tiles[index] != TileType.Water) continue;
            int x = index % width;
            int y = index / width;
            double? @override = options.waterLevelAt?.Invoke(x, y, TileType.Water);
            levels[index] = @override != null && Number.isFinite(@override.Value)
                ? @override.Value
                : waterSurfaceLevelFromStored(storedElevationAt(elevation, width, height, x, y));
        }

        // Seed a deterministic nearest-owner field at every real-Water/covered-Water contact. A Water terrace at the
        // far side of a long deck does not pull the near side down: graph distance owns the hand-off, and the lower
        // source index resolves the exact medial tie. All real sources enter the FIFO in ascending cell order, so
        // every breadth layer retains that order and each covered cell needs to be visited exactly once.
        var owner = new int[count];
        owner.fill(-1);
        var distance = new int[count];
        distance.fill(0x3fffffff);
        var queue = new int[Math.max(1, count)];
        int head = 0;
        int tail = 0;
        for (int index = 0; index < tiles.Length; index++)
        {
            if (tiles[index] != TileType.Water || !Number.isFinite(levels[index])) continue;
            distance[index] = 0;
            owner[index] = index;
            queue[tail++] = index;
        }
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            for (int k = 0; k < TerrainDirections.Length; k++)
            {
                TerrainDirection d = TerrainDirections[k];
                int nx = x + d.dx;
                int ny = y + d.dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int ni = tileIndex(width, nx, ny);
                if (tiles[ni] != TileType.Bridge || bridgeSpans[ni] != TileType.Water) continue;
                int nextDistance = distance[index] + 1;
                int source = owner[index];
                if (distance[ni] != 0x3fffffff) continue;
                distance[ni] = nextDistance;
                owner[ni] = source;
                queue[tail++] = ni;
            }
        }

        for (int index = 0; index < tiles.Length; index++)
        {
            if (tiles[index] != TileType.Bridge || bridgeSpans[index] != TileType.Water) continue;
            int x = index % width;
            int y = index / width;
            double level = bridgeWaterSurfaceCeilingFromStored(storedElevationAt(elevation, width, height, x, y));
            int source = owner[index];
            if (source >= 0 && Number.isFinite(levels[source])) level = Math.min(level, levels[source]);
            double? @override = options.waterLevelAt?.Invoke(x, y, TileType.Bridge);
            if (@override != null && Number.isFinite(@override.Value)) level = Math.min(level, @override.Value);
            levels[index] = level;
        }

        float[]? resolved = options.resolvedWaterLevels;
        if (resolved != null && resolved.Length >= count)
        {
            for (int index = 0; index < count; index++)
            {
                double supplied = resolved[index];
                if (!Number.isFinite(supplied)) continue;
                if (tiles[index] == TileType.Water)
                {
                    levels[index] = supplied;
                    continue;
                }
                if (tiles[index] != TileType.Bridge || bridgeSpans[index] != TileType.Water) continue;
                int x = index % width;
                int y = index / width;
                levels[index] = Math.min(
                    supplied,
                    bridgeWaterSurfaceCeilingFromStored(storedElevationAt(elevation, width, height, x, y)));
            }
        }

        // A spline-rendered Water cell reads owners up to two cells away. Constrain that complete support apron to
        // the lowest nearby dry surface, not merely the one boundary cell: otherwise an elevated interior owner can
        // pull the shared shoreline back above its Floor/rock crest even though the boundary owner's own datum is
        // legal. This is a physical hydrology rule, not a renderer patch. The materialized waterLevel consumed by
        // collision, analysis and every renderer can therefore never form the floating square/curved sheet produced
        // by a high Water terrace beside low dry terrain. Chasm is deliberately excluded (it is a waterfall outlet),
        // as are Bridge carriers whose separate deck-clearance contract remains authoritative.
        // Reuse the completed Bridge-distance lane for fixed-point bank ceilings. Keeping the physical ceiling
        // separate from the Water owner's (possibly lower) datum prevents a low side-channel from flattening an
        // unrelated high interior through this visual support pass, without allocating another frame-sized array.
        const double bankCeilingScale = 16_384;
        const int noBankCeiling = 0x3fffffff;
        distance.fill(noBankCeiling);
        for (int index = 0; index < count; index++)
        {
            if (tiles[index] != TileType.Water || !Number.isFinite(levels[index])) continue;
            int x = index % width;
            int y = index / width;
            double bankCeiling = double.PositiveInfinity;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    int nx = x + ox;
                    int ny = y + oy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int ni = tileIndex(width, nx, ny);
                    int neighbor = tiles[ni];
                    if (neighbor == TileType.Water || neighbor == TileType.Chasm || neighbor == TileType.Bridge)
                        continue;
                    double stored = storedElevationAt(elevation, width, height, nx, ny);
                    bankCeiling = Math.min(bankCeiling, surfaceZFor(neighbor, stored, null, options, nx, ny));
                }
            }
            if (!Number.isFinite(bankCeiling)) continue;
            double encodedBankCeiling = Math.floor(bankCeiling * bankCeilingScale);
            // Int32Array store (ToInt32); the level below keeps using the unwrapped double, as in the original.
            distance[index] = Js.ToInt32(encodedBankCeiling);
            levels[index] = Math.min(levels[index], encodedBankCeiling / bankCeilingScale);
        }
        // The cubic support at a shoreline also includes the next Water owner inward. Propagate the physical bank
        // ceiling through exactly that one connected apron, rather than scanning a square radius which could jump a
        // Solid divider and incorrectly couple two unrelated basins.
        for (int index = 0; index < count; index++)
        {
            if (tiles[index] != TileType.Water || distance[index] != noBankCeiling) continue;
            int x = index % width;
            int y = index / width;
            int apronCeiling = noBankCeiling;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    int nx = x + ox;
                    int ny = y + oy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int ni = tileIndex(width, nx, ny);
                    if (tiles[ni] != TileType.Water || distance[ni] == noBankCeiling) continue;
                    apronCeiling = Math.min(apronCeiling, distance[ni]);
                }
            }
            if (apronCeiling != noBankCeiling)
                levels[index] = Math.min(levels[index], apronCeiling / bankCeilingScale);
        }

        return levels;
    }

    private static double surfaceZFor(
        int tile,
        double elevation,
        double? waterLevel,
        TerrainModelOptions options,
        int tx,
        int ty)
    {
        if (tile == TileType.Solid || tile == TileType.Cleft)
            return elevation + (options.solidWallRiseAt?.Invoke(tx, ty, elevation) ?? TERRAIN_STANDARD_WALL_BASE_RISE);
        if (tile == TileType.Chasm) return -chasmDepthFromStored(elevation);
        if (tile == TileType.Water) return waterLevel ?? waterSurfaceLevelFromStored(elevation);
        if (tile == TileType.Bridge) return elevation + BRIDGE_DECK_LIFT;
        return elevation;
    }

    private static double baseZFor(int tile, double elevation, double surfaceZ)
    {
        if (tile == TileType.Solid || tile == TileType.Cleft) return elevation;
        if (tile == TileType.Chasm) return surfaceZ - CHASM_VOID_WALL_EXTENSION;
        if (tile == TileType.Water) return surfaceZ - WATER_BASIN_DEPTH;
        if (tile == TileType.Bridge) return surfaceZ - BRIDGE_DECK_THICKNESS;
        return elevation - 0.12;
    }

    public static bool terrainTileCanOccupy(int tile) => isWalkable(tile);

    public static bool canOccupyTerrainCell(TerrainCell? cell) =>
        cell != null && terrainTileCanOccupy(cell.type) && cell.walkable && !cell.solid;

    public static double? walkHeightForTerrainCell(TerrainCell? cell)
    {
        if (!canOccupyTerrainCell(cell)) return null;
        return cell!.walkZ != null && Number.isFinite(cell.walkZ.Value) ? cell.walkZ : cell.elevation;
    }

    /// <summary>
    /// The surface a Bridge deck spans — the thing a Flui would fall onto, and the thing the world around the deck
    /// must close down to. `null` for every cell that is not a Bridge.
    ///
    /// Water answers with its hidden sheet, Chasm with the standard void wall below the deck underside (renderers
    /// refine that to the shaft's real floor through terrainChasmFloorZAt, which needs the grid this rule
    /// deliberately does not), and Floor with the highest surface a deck may sit above at all.
    /// </summary>
    public static double? terrainBridgeSpanSurfaceZ(TerrainCell cell)
    {
        if (cell.type != TileType.Bridge) return null;
        if (cell.span == TileType.Chasm) return cell.baseZ - CHASM_VOID_WALL_EXTENSION;
        return cell.waterLevel ?? bridgeWaterSurfaceCeilingFromStored(cell.elevation);
    }

    /// <summary>
    /// The datum a NEIGHBOURING cell borders across an edge, which is not always that cell's own surface.
    ///
    /// A Bridge is the whole reason this exists. Its deck is a thin suspended slab, not a terrain volume, and
    /// BRIDGE_DECK_LIFT puts that slab slightly ABOVE its landing — so a neighbour reading `surfaceZ` measured a
    /// rise, concluded it owed no wall, and left the metres of open span below the deck unclosed on every side.
    /// The camera then looked under the timber, through the world, and onto the backdrop: the rectangular grey
    /// patch beside every crossing. What a neighbour actually borders is the span.
    /// </summary>
    public static double terrainEdgeContactZ(TerrainCell cell) => terrainBridgeSpanSurfaceZ(cell) ?? cell.surfaceZ;

    /// <summary>Vertical air gap between a Bridge deck's underside and its modelled Water span.</summary>
    public static double? bridgeWaterClearanceForTerrainCell(TerrainCell? cell)
    {
        if (cell == null || cell.type != TileType.Bridge || cell.span != TileType.Water || cell.waterLevel == null)
            return null;
        return cell.baseZ - cell.waterLevel.Value;
    }

    /// <summary>Shared renderer/audit guard: Bridge Water must remain completely below the timber volume.</summary>
    public static bool bridgeWaterIsSafelyBelowDeck(TerrainCell? cell)
    {
        double? clearance = bridgeWaterClearanceForTerrainCell(cell);
        return clearance != null &&
            Number.isFinite(clearance.Value) &&
            clearance.Value + BRIDGE_WATER_EPSILON >= BRIDGE_MIN_WATER_CLEARANCE;
    }

    /// <summary>Lowest hidden Water datum of safe Bridge carriers touching one absolute lattice corner.</summary>
    private static double bridgeWaterLevelLimitAtCorner(MaterializedTerrain terrain, int cornerX, int cornerY)
    {
        double limit = double.PositiveInfinity;
        for (int oy = -1; oy <= 0; oy++)
        {
            for (int ox = -1; ox <= 0; ox++)
            {
                TerrainCell? owner = terrainCellAt(terrain, cornerX + ox, cornerY + oy);
                if (
                    owner == null ||
                    owner.type != TileType.Bridge ||
                    owner.span != TileType.Water ||
                    owner.waterLevel == null ||
                    !bridgeWaterIsSafelyBelowDeck(owner))
                    continue;
                limit = Math.min(limit, owner.waterLevel.Value);
            }
        }
        return limit;
    }

    /// <summary>
    /// Apply the one seam-stable height constraint for liquid carried below Bridge cells.
    ///
    /// Hidden Water is sourced from the nearest exposed terrace and is therefore allowed to change level across a
    /// broad, stepped boardwalk. Smoothing those samples and then clamping each rendered cell to ITS OWN deck made
    /// the two owners of a shared edge publish different heights: the lower deck clamped the edge while its higher
    /// neighbour did not. The resulting open triangles exposed the scene backdrop below exactly the Bridge runs
    /// that are meant to conceal the transition.
    ///
    /// The constraint is instead resolved at the four shared lattice corners. A corner takes the lowest physical
    /// Water datum of every safe Water-span Bridge touching it; bilinear weights carry that common answer through
    /// a cell. All four corners of a Bridge cell are constrained, while an adjacent exposed Water cell fades the
    /// constraint out over one cell. Thus both owners are byte-identical on their edge, every deck remains dry, and
    /// distant low terraces still cannot flatten an entire Bridge component.
    /// </summary>
    public static double constrainTerrainWaterLevelBelowBridges(
        MaterializedTerrain terrain,
        double gridX,
        double gridY,
        double candidateLevel)
    {
        double cellX = Math.floor(gridX);
        double cellY = Math.floor(gridY);
        double u = Math.max(0, Math.min(1, gridX - cellX));
        double v = Math.max(0, Math.min(1, gridY - cellY));
        // Tile coordinates for the lattice lookups (an out-of-range/NaN coordinate misses the grid either way).
        int ix = (int)cellX;
        int iy = (int)cellY;
        double nw = bridgeWaterLevelLimitAtCorner(terrain, ix, iy);
        double ne = bridgeWaterLevelLimitAtCorner(terrain, ix + 1, iy);
        double se = bridgeWaterLevelLimitAtCorner(terrain, ix + 1, iy + 1);
        double sw = bridgeWaterLevelLimitAtCorner(terrain, ix, iy + 1);
        double influence = 0;
        double weightedLimit = 0;
        double nwWeight = (1 - u) * (1 - v);
        if (Number.isFinite(nw))
        {
            influence += nwWeight;
            weightedLimit += nw * nwWeight;
        }
        double neWeight = u * (1 - v);
        if (Number.isFinite(ne))
        {
            influence += neWeight;
            weightedLimit += ne * neWeight;
        }
        double seWeight = u * v;
        if (Number.isFinite(se))
        {
            influence += seWeight;
            weightedLimit += se * seWeight;
        }
        double swWeight = (1 - u) * v;
        if (Number.isFinite(sw))
        {
            influence += swWeight;
            weightedLimit += sw * swWeight;
        }
        if (influence <= 0.000001) return candidateLevel;
        double limit = weightedLimit / influence;
        double constrained = Math.min(candidateLevel, limit);
        return candidateLevel + (constrained - candidateLevel) * Math.min(1, influence);
    }

    private static TerrainHeightModel heightModelFor(
        int tile,
        double elevation,
        double? waterLevel,
        int? span,
        double surfaceZ,
        double baseZ,
        TerrainCleftProfile? cleft,
        TerrainUnderpassProfile? underpass,
        TerrainHeightModel? reuse = null)
    {
        bool walkable = terrainTileCanOccupy(tile);
        double fluidSurface = waterLevel ?? surfaceZ;
        TerrainHeightModel model = reuse ?? new TerrainHeightModel();
        model.role =
            tile == TileType.Solid
                ? TerrainHeightRole.SolidVolume
                : tile == TileType.Cleft
                    ? TerrainHeightRole.PerforatedWall
                    : tile == TileType.Chasm
                        ? TerrainHeightRole.ChasmVoidDatum
                        : tile == TileType.Water
                            ? TerrainHeightRole.FluidSurface
                            : tile == TileType.Bridge
                                ? TerrainHeightRole.DeckSurface
                                : tile == TileType.Underpass
                                    ? TerrainHeightRole.Underpass
                                    : TerrainHeightRole.WalkSurface;
        model.elevation = elevation;
        model.baseZ = baseZ;
        model.walkZ = walkable ? elevation : null;
        model.waterLevel = waterLevel;
        if (tile == TileType.Solid || tile == TileType.Cleft)
        {
            TerrainSolidVolume volume = model.solidVolume ?? new TerrainSolidVolume { bottom = 0, top = 0 };
            volume.bottom = baseZ;
            volume.top = surfaceZ;
            model.solidVolume = volume;
        }
        else
        {
            model.solidVolume = null;
        }
        if (tile == TileType.Water || span == TileType.Water)
        {
            TerrainFluidVolume volume = model.fluidVolume ?? new TerrainFluidVolume { surface = 0, bottom = 0 };
            volume.surface = fluidSurface;
            volume.bottom = fluidSurface - WATER_BASIN_DEPTH;
            model.fluidVolume = volume;
        }
        else
        {
            model.fluidVolume = null;
        }
        model.cleft = cleft;
        if (tile == TileType.Underpass && underpass != null)
        {
            TerrainOverheadVolume volume = model.overheadVolume ?? new TerrainOverheadVolume
            {
                bottom = 0,
                top = 0,
                passageAxis = TerrainPassageAxis.Horizontal,
                span = 0,
                spanIndex = 0,
                negativeSupportTop = 0,
                positiveSupportTop = 0,
            };
            volume.bottom = underpass.deckBottom;
            volume.top = underpass.deckTop;
            volume.passageAxis = underpass.passageAxis;
            volume.span = underpass.span;
            volume.spanIndex = underpass.spanIndex;
            volume.negativeSupportTop = underpass.negativeSupportTop;
            volume.positiveSupportTop = underpass.positiveSupportTop;
            model.overheadVolume = volume;
            model.topZ = volume.top;
        }
        else
        {
            model.overheadVolume = null;
            model.topZ = surfaceZ;
        }
        return model;
    }

    private static TerrainCell materializeCell(
        byte[] tiles,
        byte[] bridgeSpans,
        sbyte[]? elevation,
        double[] waterLevels,
        int width,
        int height,
        int index,
        TerrainModelOptions options,
        TerrainCell? reuse = null)
    {
        int x = index % width;
        int y = index / width;
        int type = tiles[index];
        double stored = storedElevationAt(elevation, width, height, x, y);
        Func<int, int, double, double>? wallRiseAt = options.solidWallRiseAt;
        TerrainCleftProfile? cleft =
            type == TileType.Cleft
                ? terrainCleftProfileAt(tiles, elevation, width, height, x, y, wallRiseAt)
                : null;
        TerrainUnderpassProfile? underpass =
            type == TileType.Underpass
                ? terrainUnderpassProfileAt(tiles, elevation, width, height, x, y, wallRiseAt)
                : null;
        int encodedSpan = bridgeSpans[index];
        int? span =
            type == TileType.Bridge && encodedSpan != TerrainBridgeSpanModule.TERRAIN_BRIDGE_SPAN_NONE
                ? encodedSpan
                : null;
        double? waterLevel =
            terrainTileCarriesWater(type, span) && Number.isFinite(waterLevels[index])
                ? waterLevels[index]
                : null;
        double surfaceZ = surfaceZFor(type, stored, waterLevel, options, x, y);
        double baseZ = baseZFor(type, stored, surfaceZ);
        TerrainCell cell = reuse ?? new TerrainCell();
        cell.id = index;
        cell.x = x;
        cell.y = y;
        cell.type = type;
        cell.elevation = stored;
        cell.waterLevel = waterLevel;
        cell.span = span;
        cell.surfaceZ = surfaceZ;
        cell.baseZ = baseZ;
        cell.height = heightModelFor(
            type,
            stored,
            waterLevel,
            span,
            surfaceZ,
            baseZ,
            cleft,
            underpass,
            reuse?.height);
        cell.walkZ = cell.height.walkZ;
        cell.walkable = isWalkable(type);
        cell.solid = blocksMovement(type);
        cell.blocksSight = blocksSight(type);
        cell.contacts ??= new TerrainDirectionRecord<TerrainContact>();
        cell.edges ??= new TerrainDirectionRecord<TerrainEdge>();
        return cell;
    }

    private static string blockedReasonFor(TerrainCell? cell, string fallback)
    {
        if (cell == null) return TerrainMovementReason.Outside;
        if (cell.type == TileType.Solid) return TerrainMovementReason.Solid;
        if (cell.type == TileType.Cleft) return TerrainMovementReason.Cleft;
        if (cell.type == TileType.Water) return TerrainMovementReason.Water;
        if (cell.type == TileType.Chasm) return TerrainMovementReason.Chasm;
        return fallback;
    }

    public static TerrainMovementRule terrainMovementRuleFor(
        TerrainCell? from,
        TerrainCell? to,
        double maxStep = TERRAIN_PHYSICS.maxStep,
        TerrainMovementRule? reuse = null)
    {
        TerrainMovementRule result = reuse ?? new TerrainMovementRule();
        result.maxStep = maxStep;
        if (to == null)
        {
            result.passable = false;
            result.reason = TerrainMovementReason.Outside;
            result.delta = null;
            return result;
        }
        if (!canOccupyTerrainCell(from))
        {
            result.passable = false;
            result.reason = blockedReasonFor(from, TerrainMovementReason.OriginBlocked);
            result.delta = null;
            return result;
        }
        if (!canOccupyTerrainCell(to))
        {
            result.passable = false;
            result.reason = blockedReasonFor(to, TerrainMovementReason.TargetBlocked);
            result.delta = null;
            return result;
        }
        double? fromZ = walkHeightForTerrainCell(from);
        double? toZ = walkHeightForTerrainCell(to);
        double delta = Math.abs((toZ ?? 0) - (fromZ ?? 0));
        result.delta = delta;
        if (delta > maxStep)
        {
            result.passable = false;
            result.reason = TerrainMovementReason.HeightStep;
        }
        else
        {
            result.passable = true;
            result.reason = TerrainMovementReason.Walkable;
        }
        return result;
    }

    private static bool hasFluid(TerrainCell cell) => cell.type == TileType.Water || cell.span == TileType.Water;

    private static string edgeRelation(TerrainCell cell, TerrainCell? neighbor, double delta, double maxStep)
    {
        if (neighbor == null) return TerrainEdgeRelation.Outer;
        if (cell.type == TileType.Chasm || neighbor.type == TileType.Chasm) return TerrainEdgeRelation.ChasmEdge;
        if (cell.type == TileType.Solid && neighbor.type != TileType.Solid) return TerrainEdgeRelation.WallFace;
        if (neighbor.type == TileType.Solid && cell.type != TileType.Solid) return TerrainEdgeRelation.WallContact;
        if (hasFluid(cell) || hasFluid(neighbor)) return TerrainEdgeRelation.WaterEdge;
        if (cell.type == TileType.Bridge || neighbor.type == TileType.Bridge) return TerrainEdgeRelation.DeckEdge;
        if (delta > maxStep + FACE_EPSILON) return TerrainEdgeRelation.Drop;
        if (delta < -maxStep - FACE_EPSILON) return TerrainEdgeRelation.Rise;
        if (Math.abs(delta) > FACE_EPSILON) return TerrainEdgeRelation.Step;
        return TerrainEdgeRelation.Level;
    }

    private static string surfaceEdgeMaterial(TerrainCell cell, string relation)
    {
        if (cell.type == TileType.Chasm) return TerrainEdgeMaterial.Abyss;
        if (relation == TerrainEdgeRelation.WallFace || cell.type == TileType.Solid) return TerrainEdgeMaterial.Rock;
        // A Bridge carries Water, but the visible volume at its walk surface is still the timber deck. Treating its
        // crest as a bank lets renderers bevel and extrude the complete deck-to-neighbour drop as terrain.
        if (cell.type == TileType.Bridge) return TerrainEdgeMaterial.Deck;
        // A dry cell bordering Water still owns an ordinary terrain face. Classifying both sides of the contact as
        // `bank` recoloured the Floor face toward cyan and made a second, visibly foreign "water edge" sit in front
        // of the normal floor cliff. Only the liquid owner uses the bank material; Floor keeps its earth language
        // (Solid already returned rock above), so both renderers can close the shore with the canonical dry edge.
        if (hasFluid(cell)) return TerrainEdgeMaterial.Bank;
        if (relation == TerrainEdgeRelation.WaterEdge) return TerrainEdgeMaterial.Earth;
        // Only the deck itself is timber (returned above). Reaching here means the NEIGHBOUR is the Bridge and this
        // cell is ordinary ground closing down to the span below it — its own earth, exactly like the `bank` clause.
        if (relation == TerrainEdgeRelation.DeckEdge) return TerrainEdgeMaterial.Earth;
        if (
            relation == TerrainEdgeRelation.ChasmEdge ||
            relation == TerrainEdgeRelation.Drop ||
            relation == TerrainEdgeRelation.Rise ||
            relation == TerrainEdgeRelation.Step)
            return TerrainEdgeMaterial.Earth;
        return TerrainEdgeMaterial.None;
    }

    private static List<TerrainFaceSegment> deriveFaceSegments(
        TerrainCell cell,
        TerrainCell? neighbor,
        string relation,
        double fromZ,
        double toZ,
        bool visibleFace,
        List<TerrainFaceSegment>? reuse = null)
    {
        List<TerrainFaceSegment> previous = reuse ?? new List<TerrainFaceSegment>();
        List<TerrainFaceSegment> segments = previous;
        TerrainFaceSegment? first = previous.Count > 0 ? previous[0] : null;
        TerrainFaceSegment? second = previous.Count > 1 ? previous[1] : null;
        segments.Clear();
        if (!visibleFace) return segments;

        // A Bridge is a thin suspended deck, never a terrain column. At an ordinary open flank its timber face ends
        // at the soffit and the carrier owns the world below. Two adjacent deck treads are the one exception: the
        // higher cell owns the complete riser down to the lower tread. Without that physical stair face a one-level
        // walkable transition reads as two disconnected floating slabs and the underlying carrier shows as a broad
        // rectangular hole between them.
        if (cell.type == TileType.Bridge)
        {
            double deckFaceBottomZ = neighbor != null && neighbor.type == TileType.Bridge ? toZ : Math.max(cell.baseZ, toZ);
            if (fromZ <= deckFaceBottomZ + FACE_EPSILON) return segments;
            TerrainFaceSegment segment = first ?? new TerrainFaceSegment();
            segment.fromZ = fromZ;
            segment.toZ = deckFaceBottomZ;
            segment.drop = fromZ - deckFaceBottomZ;
            segment.material = TerrainEdgeMaterial.Deck;
            segment.role = TerrainFaceSegmentRole.Surface;
            segments.push(segment);
            return segments;
        }

        if (fromZ <= toZ + FACE_EPSILON) return segments;
        string surfaceMaterial = surfaceEdgeMaterial(cell, relation);
        // A Chasm-crossing Bridge is the same shaft as the open Chasm beside it, so the land closing down to it owes
        // the same abyss split. Reading the raw tile type answered "Bridge" and painted the whole drop as earth.
        bool touchesChasm =
            cell.type == TileType.Chasm ||
            (neighbor != null && neighbor.type == TileType.Chasm) ||
            (neighbor != null && terrainCellCarriesChasmFloor(neighbor));
        if (!touchesChasm)
        {
            TerrainFaceSegment segment = first ?? new TerrainFaceSegment();
            segment.fromZ = fromZ;
            segment.toZ = toZ;
            segment.drop = fromZ - toZ;
            segment.material = surfaceMaterial;
            segment.role = TerrainFaceSegmentRole.Surface;
            segments.push(segment);
            return segments;
        }

        // Chasm-to-Chasm drops are shaft geology throughout. For every ordinary owner, however, the local physical
        // base is the material boundary: a Solid keeps its complete wall from surfaceZ to its floor/base datum, a
        // Floor keeps its thin fascia, and Water keeps its basin bank. Only the volume BELOW that local datum is the
        // Chasm shaft. A global zero split started Chasm pigment too early on low terrain and too late on high terrain.
        if (cell.type == TileType.Chasm)
        {
            TerrainFaceSegment segment = first ?? new TerrainFaceSegment();
            segment.fromZ = fromZ;
            segment.toZ = toZ;
            segment.drop = fromZ - toZ;
            segment.material = TerrainEdgeMaterial.Abyss;
            segment.role = TerrainFaceSegmentRole.Chasm;
            segments.push(segment);
            return segments;
        }

        double splitZ = cell.baseZ;
        if (fromZ > splitZ)
        {
            double upperTo = Math.max(toZ, splitZ);
            if (fromZ > upperTo + FACE_EPSILON)
            {
                TerrainFaceSegment segment = first ?? new TerrainFaceSegment();
                segment.fromZ = fromZ;
                segment.toZ = upperTo;
                segment.drop = fromZ - upperTo;
                segment.material = surfaceMaterial == TerrainEdgeMaterial.Abyss ? TerrainEdgeMaterial.Earth : surfaceMaterial;
                segment.role = TerrainFaceSegmentRole.Surface;
                segments.push(segment);
            }
        }
        if (toZ < splitZ)
        {
            double lowerFrom = Math.min(fromZ, splitZ);
            if (lowerFrom > toZ + FACE_EPSILON)
            {
                TerrainFaceSegment segment =
                    segments.Count == 0
                        ? (first ?? new TerrainFaceSegment())
                        : (second ?? new TerrainFaceSegment());
                segment.fromZ = lowerFrom;
                segment.toZ = toZ;
                segment.drop = lowerFrom - toZ;
                segment.material = TerrainEdgeMaterial.Abyss;
                segment.role = TerrainFaceSegmentRole.Chasm;
                segments.push(segment);
            }
        }
        return segments;
    }

    private static string transitionFor(TerrainCell cell, TerrainCell? neighbor, TerrainMovementRule movement)
    {
        if (!movement.passable || neighbor == null) return TransitionKind.Blocked;
        double? fromZ = walkHeightForTerrainCell(cell);
        double? toZ = walkHeightForTerrainCell(neighbor);
        if (fromZ == null || !Number.isFinite(fromZ.Value) || toZ == null || !Number.isFinite(toZ.Value))
            return TransitionKind.Blocked;
        if ((toZ ?? 0) > (fromZ ?? 0)) return TransitionKind.StepUp;
        if ((toZ ?? 0) < (fromZ ?? 0)) return TransitionKind.StepDown;
        return TransitionKind.Level;
    }

    // `const EDGE_MOVEMENT_SCRATCH: TerrainMovementRule = {...}` — one module-level scratch per JS realm. The C#
    // generator runs on several worker threads, so each thread owns its scratch (the value never escapes deriveEdge).
    [ThreadStatic]
    private static TerrainMovementRule? edgeMovementScratch;

    private static TerrainMovementRule EDGE_MOVEMENT_SCRATCH =>
        edgeMovementScratch ??= new TerrainMovementRule
        {
            passable = false,
            reason = TerrainMovementReason.Outside,
            delta = null,
            maxStep = TERRAIN_PHYSICS.maxStep,
        };

    private static TerrainEdge deriveEdge(
        TerrainCell cell,
        TerrainCell? neighbor,
        TerrainDirection direction,
        double maxStep,
        TerrainEdge? reuse = null)
    {
        // A Bridge reads its neighbours' real surfaces (deck continuity and its own landing), but everything else
        // reads a Bridge as the span it crosses — see terrainEdgeContactZ.
        double toZ = neighbor != null
            ? cell.type == TileType.Bridge
                ? neighbor.surfaceZ
                : terrainEdgeContactZ(neighbor)
            : cell.surfaceZ - 1.2;
        double delta = cell.surfaceZ - toZ;
        string relation = edgeRelation(cell, neighbor, delta, maxStep);
        TerrainMovementRule movement = terrainMovementRuleFor(cell, neighbor, maxStep, EDGE_MOVEMENT_SCRATCH);
        string transition = transitionFor(cell, neighbor, movement);
        bool visibleFace =
            relation == TerrainEdgeRelation.Outer ||
            relation == TerrainEdgeRelation.WallFace ||
            delta > FACE_EPSILON ||
            (cell.type == TileType.Bridge && cell.span == TileType.Water) ||
            // A Chasm-crossing deck presents its DECK datum to this edge, so the drop measures as nothing and the
            // land beside it would author no face at all — while the shaft it spans is metres deep. Presence here is
            // a purely visual decision; the movement relation above still sees the walkable deck, so a crossing stays
            // as walkable as it ever was.
            (neighbor != null &&
                terrainCellCarriesChasmFloor(neighbor) &&
                !terrainCellCarriesChasmFloor(cell));
        List<TerrainFaceSegment> faceSegments = deriveFaceSegments(
            cell,
            neighbor,
            relation,
            cell.surfaceZ,
            toZ,
            visibleFace,
            reuse?.faceSegments);

        TerrainEdge edge = reuse ?? new TerrainEdge();
        edge.direction = direction.key;
        edge.label = direction.label;
        edge.contactType = neighbor?.type ?? TERRAIN_CONTACT_OUTSIDE;
        edge.contactElevation = neighbor?.elevation;
        edge.contactSurfaceZ = neighbor?.surfaceZ;
        edge.relation = relation;
        // Legacy consumers use the crest material. The authoritative vertical contract is faceSegments.
        edge.material = (faceSegments.Count > 0 ? faceSegments[0] : null)?.material ?? surfaceEdgeMaterial(cell, relation);
        edge.faceSegments = faceSegments;
        edge.fromZ = cell.surfaceZ;
        edge.toZ = toZ;
        edge.delta = delta;
        edge.drop = Math.max(0, delta);
        edge.visibleFace = visibleFace;
        edge.passable = movement.passable;
        edge.transition = transition;
        edge.blockReason = movement.reason;
        edge.walkDelta = movement.delta;
        edge.maxStep = movement.maxStep;
        return edge;
    }

    private static readonly ConditionalWeakTable<MaterializedTerrain, double[]> TERRAIN_WATER_LEVEL_SCRATCH = new();

    public static MaterializedTerrain materializeTerrainGrid(
        byte[] tiles,
        int width,
        int height,
        sbyte[]? elevation = null,
        TerrainModelOptions? options = null,
        MaterializedTerrain? reuse = null)
    {
        options ??= new TerrainModelOptions();
        bool canReuse = reuse != null && reuse.width == width && reuse.height == height;
        byte[] bridgeSpans = TerrainBridgeSpanModule.classifyTerrainBridgeSpans(tiles, width, height);
        byte[]? suppliedBridgeSpans = options.bridgeSpans;
        if (suppliedBridgeSpans != null && suppliedBridgeSpans.Length >= width * height)
        {
            for (int index = 0; index < width * height; index++)
            {
                if (tiles[index] != TileType.Bridge) continue;
                int supplied = suppliedBridgeSpans[index];
                if (supplied == TileType.Water || supplied == TileType.Chasm || supplied == TileType.Floor)
                    bridgeSpans[index] = (byte)supplied;
            }
        }
        double[]? waterLevelScratch = null;
        if (canReuse && reuse != null) TERRAIN_WATER_LEVEL_SCRATCH.TryGetValue(reuse, out waterLevelScratch);
        double[] waterLevels = deriveTerrainWaterLevels(
            tiles,
            bridgeSpans,
            elevation,
            width,
            height,
            options,
            waterLevelScratch);
        int count = width * height;
        TerrainCell[] cells = canReuse && reuse != null ? reuse.cells : new TerrainCell[count];
        for (int index = 0; index < count; index++)
        {
            cells[index] = materializeCell(
                tiles,
                bridgeSpans,
                elevation,
                waterLevels,
                width,
                height,
                index,
                options,
                canReuse ? cells[index] : null);
        }
        MaterializedTerrain terrain = reuse ?? new MaterializedTerrain
        {
            width = width,
            height = height,
            cells = cells,
            tiles = tiles,
            elevation = elevation,
        };
        terrain.width = width;
        terrain.height = height;
        terrain.cells = cells;
        terrain.tiles = tiles;
        terrain.elevation = elevation;
        // BaseZ is the physical Chasm floor, so resolve it before deriving any edge/contact that can consume it.
        // The temporary per-cell value from `baseZFor` remains useful while a cell is constructed, but never escapes
        // materialization: connected topology, including suspended Chasm Bridge carriers, owns the final terraces.
        foreach (TerrainCell cell in cells)
        {
            if (cell.type != TileType.Chasm) continue;
            cell.baseZ = terrainChasmBasinFloorZAt(terrain, cell);
            cell.height.baseZ = cell.baseZ;
        }
        double maxStep = options.maxStep ?? TERRAIN_PHYSICS.maxStep;
        foreach (TerrainCell cell in cells)
        {
            for (int k = 0; k < TerrainDirections.Length; k++)
            {
                TerrainDirection direction = TerrainDirections[k];
                int nx = cell.x + direction.dx;
                int ny = cell.y + direction.dy;
                TerrainCell? neighbor = inBounds(width, height, nx, ny) ? cells[tileIndex(width, nx, ny)] : null;
                TerrainContact contact = cell.contacts[direction.key] ?? new TerrainContact();
                contact.type = neighbor?.type ?? TERRAIN_CONTACT_OUTSIDE;
                contact.walkable = neighbor != null && neighbor.walkable;
                contact.solid = neighbor != null && neighbor.solid;
                contact.elevation = neighbor?.elevation;
                contact.surfaceZ = neighbor?.surfaceZ;
                contact.waterLevel = neighbor?.waterLevel;
                cell.contacts[direction.key] = contact;
                cell.edges[direction.key] = deriveEdge(
                    cell,
                    neighbor,
                    direction,
                    maxStep,
                    cell.edges[direction.key]);
            }
        }
        TERRAIN_WATER_LEVEL_SCRATCH.AddOrUpdate(terrain, waterLevels);
        return terrain;
    }

    public static TerrainCell? terrainCellAt(MaterializedTerrain terrain, int tx, int ty)
    {
        if (!inBounds(terrain.width, terrain.height, tx, ty)) return null;
        int index = tileIndex(terrain.width, tx, ty);
        // `terrain.cells[i] ?? null`
        return (uint)index < (uint)terrain.cells.Length ? terrain.cells[index] : null;
    }
}
