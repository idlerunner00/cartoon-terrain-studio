// Port of packages/shared/src/domain/dungeon/terrainVisualContour.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Porting notes:
// * Corner keys (`'nw' | 'ne' | 'se' | 'sw'`) and edge directions (TerrainEdgeDirection) are strings, as in TS.
// * World cell/corner coordinates are `double`: they only enter the hashes through `x | 0` (Js.ToInt32 of the
//   double product) and the macro field divides them, so an `int` caller converts implicitly and exactly.
// * Mid-edge chip directions (`0 | 1 | 2 | 3`) are `int`.

/// <summary>
/// Cosmetic contour information for one authoritative gameplay cell.
///
/// Values are fractions of a cell edge. A non-zero corner value means that the two compatible exposed edges
/// meeting there should end early and be joined by one compact quarter-round contour. The gameplay cell, height,
/// collision and navigation footprint remain unchanged.
/// </summary>
public sealed class TerrainVisualContourCorners
{
    public double nw;
    public double ne;
    public double se;
    public double sw;
    /// <summary>Optional shallow mid-edge chips, expressed as a fraction of tile size and consumed as cap Y drop.</summary>
    public double? n;
    public double? e;
    public double? s;
    public double? w;
}

/// <summary>`TerrainEdgeProfile = 'calm' | 'weathered' | 'notched' | 'fractured'`.</summary>
public static class TerrainEdgeProfile
{
    public const string Calm = "calm";
    public const string Weathered = "weathered";
    public const string Notched = "notched";
    public const string Fractured = "fractured";
}

public static class TerrainVisualContour
{
    /// <summary>Immutable value (never mutated after construction), so a struct is equivalent to the TS object.</summary>
    private readonly struct TerrainVisualBoundary
    {
        public readonly bool active;
        public readonly double family;
        public readonly double datum;

        public TerrainVisualBoundary(bool active, double family, double datum)
        {
            this.active = active;
            this.family = family;
            this.datum = datum;
        }
    }

    // Break-the-staircase span. The earlier uniform 0.31-0.37 band killed the saw-tooth chatter but let long
    // terrace rims repeat one radius, so coastlines still read as a metronome staircase. The authored range is
    // widened again (0.28-0.44), while the variation lives on a SMOOTH four-cell macro field instead of
    // independent per-corner jitter: neighbouring cuts inherit one geological gesture (no alternating
    // wide/narrow teeth, no isolated near-half-cell bite like the abandoned 0.27-0.47 experiment). The field is
    // a pure function of absolute world corner coordinates, so it is deterministic and chunk-seam stable.
    private const double CONTOUR_MIN = 0.28;
    private const double CONTOUR_MAX = 0.44;
    private const double CONTINUOUS_RUN_RADIUS = 0.315;
    private const double BROKEN_RUN_RADIUS = 0.35;
    private const double STAIR_STEP_RADIUS = 0.395;
    private const double CONTOUR_MACRO_SCALE = 4;
    private const double CONTOUR_MACRO_VARIATION = 0.1;
    private const double DATUM_EPSILON = 0.26;

    private static readonly TerrainVisualBoundary EMPTY_BOUNDARY = new(false, 0, 0);

    /// <summary>
    /// Fluitown comic look (not in the original): the
    /// coherent policy's outside turns and thin masses round as generously as a stair transition, so terraces read as
    /// weathered rock instead of stacked blocks and a one-cell pillar becomes a rock needle. Set before the first bake
    /// with the organic terrain form (TerrainSceneRenderer.Initialize, TerrainView.ApplyForm); off, the ported radii stay.
    /// </summary>
    public static volatile bool OrganicCorners;

    /// <summary>
    /// Resolve the authored-procedural corner cuts for one cell. Absolute cell coordinates only affect the small
    /// radius variation; topology comes exclusively from the guard-bordered shared terrain model. Supplying `out`
    /// keeps worker hot paths allocation-free.
    /// </summary>
    public static TerrainVisualContourCorners terrainVisualContourCorners(
        MaterializedTerrain terrain,
        TerrainCell cell,
        double worldCellX,
        double worldCellY,
        TerrainVisualContourCorners? @out = null)
    {
        @out ??= new TerrainVisualContourCorners { nw = 0, ne = 0, se = 0, sw = 0 };
        @out.nw = 0;
        @out.ne = 0;
        @out.se = 0;
        @out.sw = 0;
        @out.n = 0;
        @out.e = 0;
        @out.s = 0;
        @out.w = 0;

        // An Underpass is a continuous overhead volume authored across several cells and retains its structural
        // outline. A Bridge is ordinary walkable terrain topology at its deck plane: excluding it here forced every
        // renderer below to invent a second rectangular footprint for planks, soffit and span backing. Bridge runs
        // therefore use the same exposed-edge contour contract as Floor/Solid; internal deck joins remain inactive
        // boundaries, so a straight multi-cell crossing still stays straight along its load-bearing run.
        if (cell.type == TileType.Underpass) return @out;

        TerrainVisualBoundary n = visualBoundaryAt(terrain, cell, "n");
        TerrainVisualBoundary e = visualBoundaryAt(terrain, cell, "e");
        TerrainVisualBoundary s = visualBoundaryAt(terrain, cell, "s");
        TerrainVisualBoundary w = visualBoundaryAt(terrain, cell, "w");

        @out.nw = cornerRadius(terrain, cell, worldCellX, worldCellY, "nw", n, w);
        @out.ne = cornerRadius(terrain, cell, worldCellX + 1, worldCellY, "ne", n, e);
        @out.se = cornerRadius(terrain, cell, worldCellX + 1, worldCellY + 1, "se", s, e);
        @out.sw = cornerRadius(terrain, cell, worldCellX, worldCellY + 1, "sw", s, w);
        // A cap chip is a roughly two-to-three pixel downward nick at the existing edge midpoint, not an outward
        // footprint
        // change. At the production tile scale the former sub-pixel range disappeared after antialiasing and left
        // every long geological run ruler-clean despite the curved corners.
        // It therefore enriches the silhouette/material break while the authoritative walk plane and wall closure
        // stay exact. Solid geological caps alone receive them; constructed decks and walkable terraces remain calm.
        if (cell.type == TileType.Solid)
        {
            @out.n = edgeChip(worldCellX, worldCellY, 0, n);
            @out.e = edgeChip(worldCellX, worldCellY, 1, e);
            @out.s = edgeChip(worldCellX, worldCellY, 2, s);
            @out.w = edgeChip(worldCellX, worldCellY, 3, w);
        }
        return @out;
    }

    /// <summary>
    /// Live-render contour policy.
    ///
    /// The legacy radius policy is intentionally retained above as the byte-parity reference for baked payload
    /// tests. Live terrain uses this topology-aware radius policy with the same shared organic arc primitive: a
    /// one-cell diagonal staircase consumes almost the complete half edge so neighbouring cuts meet as one rounded
    /// run, while a genuine long-run outside corner receives only a restrained cut. Isolated pillars never become
    /// blobs.
    /// </summary>
    public static TerrainVisualContourCorners terrainCoherentContourCorners(
        MaterializedTerrain terrain,
        TerrainCell cell,
        double worldCellX,
        double worldCellY,
        TerrainVisualContourCorners? @out = null)
    {
        @out ??= new TerrainVisualContourCorners { nw = 0, ne = 0, se = 0, sw = 0 };
        @out.nw = 0;
        @out.ne = 0;
        @out.se = 0;
        @out.sw = 0;
        @out.n = 0;
        @out.e = 0;
        @out.s = 0;
        @out.w = 0;
        if (cell.type == TileType.Underpass) return @out;

        TerrainVisualBoundary n = visualBoundaryAt(terrain, cell, "n");
        TerrainVisualBoundary e = visualBoundaryAt(terrain, cell, "e");
        TerrainVisualBoundary s = visualBoundaryAt(terrain, cell, "s");
        TerrainVisualBoundary w = visualBoundaryAt(terrain, cell, "w");
        int activeEdges = (n.active ? 1 : 0) + (e.active ? 1 : 0) + (s.active ? 1 : 0) + (w.active ? 1 : 0);

        @out.nw = coherentCornerRadius(terrain, cell, worldCellX, worldCellY, "nw", n, w, activeEdges);
        @out.ne = coherentCornerRadius(terrain, cell, worldCellX + 1, worldCellY, "ne", n, e, activeEdges);
        @out.se = coherentCornerRadius(
            terrain,
            cell,
            worldCellX + 1,
            worldCellY + 1,
            "se",
            s,
            e,
            activeEdges);
        @out.sw = coherentCornerRadius(terrain, cell, worldCellX, worldCellY + 1, "sw", s, w, activeEdges);
        return @out;
    }

    /// <summary>
    /// Byte-parity legacy chip retained for baked payload comparisons; live coherent terrain uses the richer
    /// profile vocabulary below.
    /// </summary>
    private static double edgeChip(
        double worldCellX,
        double worldCellY,
        int direction,
        TerrainVisualBoundary boundary)
    {
        if (!boundary.active) return 0;
        double chance = contourHash01(worldCellX * 5 + direction * 17, worldCellY * 5 - direction * 11);
        if (chance >= 0.34) return 0;
        return 0.015 + contourHash01(worldCellX * 7 + direction, worldCellY * 7 - direction) * 0.017;
    }

    private static double edgeProfileSample(double worldCellX, double worldCellY, int direction) =>
        contourHash01(worldCellX * 5 + direction * 17, worldCellY * 5 - direction * 11);

    /// <summary>Deterministic named edge vocabulary for audits and tooling.</summary>
    /// <param name="direction">0 | 1 | 2 | 3 (n, e, s, w).</param>
    /// <returns>A <see cref="TerrainEdgeProfile"/>.</returns>
    public static string terrainEdgeProfileKindAt(
        double worldCellX,
        double worldCellY,
        int direction,
        bool floor)
    {
        double sample = edgeProfileSample(worldCellX, worldCellY, direction);
        if (floor)
            return sample < 0.78 ? TerrainEdgeProfile.Calm : sample < 0.95 ? TerrainEdgeProfile.Weathered : TerrainEdgeProfile.Notched;
        return sample < 0.5
            ? TerrainEdgeProfile.Calm
            : sample < 0.82
                ? TerrainEdgeProfile.Weathered
                : sample < 0.96
                    ? TerrainEdgeProfile.Notched
                    : TerrainEdgeProfile.Fractured;
    }

    private static double cornerRadius(
        MaterializedTerrain terrain,
        TerrainCell cell,
        double worldCornerX,
        double worldCornerY,
        string corner,
        TerrainVisualBoundary a,
        TerrainVisualBoundary b)
    {
        if (
            !compatibleBoundaries(a, b) ||
            !receiverFamilyContinuesThroughDiagonal(terrain, cell, corner, a, b, DATUM_EPSILON))
            return 0;

        bool continuationA = boundaryContinuesFromCorner(terrain, cell, corner, true, a);
        bool continuationB = boundaryContinuesFromCorner(terrain, cell, corner, false, b);
        return terrainVisualCornerRadiusAt(worldCornerX, worldCornerY, continuationA, continuationB);
    }

    /// <summary>Shared legacy Floor radius policy for renderers that own a compatible derived contour.</summary>
    public static double terrainVisualCornerRadiusAt(
        double worldCornerX,
        double worldCornerY,
        bool continuationA,
        bool continuationB)
    {
        double @base =
            continuationA && continuationB
                ? CONTINUOUS_RUN_RADIUS
                : continuationA || continuationB
                    ? BROKEN_RUN_RADIUS
                    : STAIR_STEP_RADIUS;
        // A smooth macro sweep carries the visible radius variance (several corners share one gesture); the tiny
        // per-corner term only breaks exact ties so two distant staircases never repeat bit-identically. Both
        // depend exclusively on absolute world corner coordinates: deterministic and identical across chunk seams.
        double variation =
            (contourMacroField(worldCornerX, worldCornerY) - 0.5) * CONTOUR_MACRO_VARIATION +
            (contourHash01(worldCornerX, worldCornerY) - 0.5) * 0.006;
        return clamp(@base + variation, CONTOUR_MIN, CONTOUR_MAX);
    }

    private static double coherentCornerRadius(
        MaterializedTerrain terrain,
        TerrainCell cell,
        double worldCornerX,
        double worldCornerY,
        string corner,
        TerrainVisualBoundary a,
        TerrainVisualBoundary b,
        int activeEdges)
    {
        if (
            !coherentCompatibleBoundaries(a, b) ||
            !receiverFamilyContinuesThroughDiagonal(terrain, cell, corner, a, b, 1.25))
            return 0;

        // Three or four exposed sides describe an isolated/thin mass, not a coastline stair. A small planar bevel
        // preserves its scale and silhouette instead of turning the whole cell into a round pebble.
        double organicMacro = contourMacroField(worldCornerX, worldCornerY) - 0.5;
        double organicCharacter = coherentCornerCharacter(worldCornerX, worldCornerY);
        if (activeEdges >= 3)
        {
            if (OrganicCorners) return clamp(0.39 + organicMacro * 0.1 + organicCharacter * 2, 0.33, 0.45);
            return clamp(
                0.095 + contourMacroField(worldCornerX, worldCornerY) * 0.025 +
                    coherentCornerCharacter(worldCornerX, worldCornerY),
                0.095,
                0.12);
        }

        bool continuationA = boundaryContinuesFromCorner(terrain, cell, corner, true, a);
        bool continuationB = boundaryContinuesFromCorner(terrain, cell, corner, false, b);
        double macro = contourMacroField(worldCornerX, worldCornerY) - 0.5;
        if (!continuationA && !continuationB)
        {
            // Consecutive one-cell steps meet at r=0.5. Leave a sub-pixel safety land so independently triangulated
            // neighbours cannot invert or overlap under Float32 quantisation.
            return clamp(
                0.49 + macro * 0.012 + coherentCornerCharacter(worldCornerX, worldCornerY) * 0.35,
                0.478,
                0.496);
        }
        if (continuationA && continuationB)
        {
            if (OrganicCorners) return clamp(0.37 + organicMacro * 0.14 + organicCharacter * 2, 0.29, 0.46);
            // A real large-scale outside turn needs only a compact authored break.
            return clamp(
                0.13 + macro * 0.035 + coherentCornerCharacter(worldCornerX, worldCornerY),
                0.112,
                0.148);
        }
        // Transition between a straight run and a diagonal run. Macro-coherent variance avoids a mechanical repeat
        // without allowing neighbouring corners to jitter independently.
        if (OrganicCorners) return clamp(0.43 + organicMacro * 0.06 + organicCharacter, 0.39, 0.47);
        return clamp(
            0.305 + macro * 0.04 + coherentCornerCharacter(worldCornerX, worldCornerY),
            0.285,
            0.325);
    }

    /// <summary>
    /// Three stable corner characters: softly eroded, neutral and freshly broken. The macro field still owns
    /// continuity; this tiny absolute-corner offset merely stops identical topology from repeating one radius.
    /// </summary>
    private static double coherentCornerCharacter(double worldCornerX, double worldCornerY)
    {
        double sample = contourHash01(worldCornerX * 11 + 0x2d, worldCornerY * 13 - 0x31);
        return sample < 0.34 ? -0.006 : sample < 0.8 ? 0 : 0.006;
    }

    /// <summary>
    /// A rounded high corner removes real area from its authoritative square cap. The three receiver quadrants
    /// around that cut must therefore agree on who fills the removed footprint. Looking only at the two cardinal
    /// edges let point-touching ponds, dry terraces beside diagonal Water, and Chasm/dry junctions borrow one
    /// another's fill. That is the black/purple triangular card seen at mixed four-cell corners. Mixed receiver
    /// families keep the high cap square; compatible Water, Chasm and dry geology may round only when the diagonal
    /// carrier completes the same family.
    /// </summary>
    private static bool receiverFamilyContinuesThroughDiagonal(
        MaterializedTerrain terrain,
        TerrainCell cell,
        string corner,
        TerrainVisualBoundary a,
        TerrainVisualBoundary b,
        double datumTolerance)
    {
        if (!a.active || !b.active) return false;
        int dx = corner == "ne" || corner == "se" ? 1 : -1;
        int dy = corner == "se" || corner == "sw" ? 1 : -1;
        TerrainCell? diagonal = terrainCellAt(terrain, cell.x + dx, cell.y + dy);

        if (a.family == 2 && b.family == 2) return terrainCellCarriesWater(diagonal);
        if (a.family == 1 && b.family == 1)
            return diagonal != null && terrainCellCarriesChasmFloor(diagonal);
        if (a.family >= 10 && b.family >= 10)
        {
            return
                diagonal != null &&
                !terrainCellCarriesWater(diagonal) &&
                !terrainCellCarriesChasmFloor(diagonal) &&
                Math.abs(diagonal.surfaceZ - a.datum) <= datumTolerance &&
                Math.abs(diagonal.surfaceZ - b.datum) <= datumTolerance;
        }
        // Outside/legacy semantic families retain their existing bounded corner policy. They do not publish a
        // competing horizontal receiver inside the loaded terrain and therefore cannot select the wrong fill.
        return true;
    }

    private static bool coherentCompatibleBoundaries(TerrainVisualBoundary a, TerrainVisualBoundary b)
    {
        if (!a.active || !b.active) return false;
        // Ordinary geological drops may meet two neighbouring lower terraces at different datums. The wall builder
        // already owns separate foot heights for both endpoints, so rejecting that turn only resurrects a square
        // raster step. Semantic boundaries (Water, Chasm, outside) still require an exact family match.
        bool ordinaryGeology = a.family >= 10 && b.family >= 10;
        return ordinaryGeology
            ? Math.abs(a.datum - b.datum) <= 1.25
            : a.family == b.family && Math.abs(a.datum - b.datum) <= DATUM_EPSILON;
    }

    private static bool compatibleBoundaries(TerrainVisualBoundary a, TerrainVisualBoundary b)
    {
        // A rounded corner is a transfer of real cap area to one horizontal receiver. Two Water edges at different
        // datums do not have such a receiver: their visual spline is sloped and translucent, so assigning the cut to
        // it exposes the deep fail-closed foundation as a black triangle. Keep the authoritative dry cap square at
        // that hydraulic step. Equal-datum Water still rounds normally and owns one unambiguous horizontal fill.
        return
            a.active && b.active && a.family == b.family && Math.abs(a.datum - b.datum) <= DATUM_EPSILON;
    }

    private static bool boundaryContinuesFromCorner(
        MaterializedTerrain terrain,
        TerrainCell cell,
        string corner,
        bool firstEdge,
        TerrainVisualBoundary reference)
    {
        int dx = 0;
        int dy = 0;
        string direction;
        if (corner == "nw")
        {
            if (firstEdge)
            {
                dx = 1;
                direction = "n";
            }
            else
            {
                dy = 1;
                direction = "w";
            }
        }
        else if (corner == "ne")
        {
            if (firstEdge)
            {
                dx = -1;
                direction = "n";
            }
            else
            {
                dy = 1;
                direction = "e";
            }
        }
        else if (corner == "se")
        {
            if (firstEdge)
            {
                dx = -1;
                direction = "s";
            }
            else
            {
                dy = -1;
                direction = "e";
            }
        }
        else if (firstEdge)
        {
            dx = 1;
            direction = "s";
        }
        else
        {
            dy = -1;
            direction = "w";
        }
        TerrainCell? next = terrainCellAt(terrain, cell.x + dx, cell.y + dy);
        if (next == null || next.type != cell.type || Math.abs(next.surfaceZ - cell.surfaceZ) > DATUM_EPSILON)
            return false;
        return compatibleBoundaries(reference, visualBoundaryAt(terrain, next, direction));
    }

    /// <param name="direction">A <see cref="TerrainEdgeDirection"/>.</param>
    private static TerrainVisualBoundary visualBoundaryAt(
        MaterializedTerrain terrain,
        TerrainCell cell,
        string direction)
    {
        TerrainEdge edge = cell.edges[direction]!;
        TerrainCell? neighbor = terrainCellAt(
            terrain,
            cell.x + (direction == "e" ? 1 : direction == "w" ? -1 : 0),
            cell.y + (direction == "s" ? 1 : direction == "n" ? -1 : 0));

        if (cell.type == TileType.Water)
        {
            double level = cell.waterLevel ?? cell.surfaceZ;
            // A Water-to-Water height change is still one hydraulic footprint. The renderer tessellates its Y field;
            // treating the stored datum jump as an exposed XZ boundary made both owners round away the shared corner,
            // leaving the deep fail-closed foundation visible as a black triangle beside an otherwise continuous flow.
            bool continuous =
                neighbor != null && terrainCellCarriesWater(neighbor) && neighbor.waterLevel != null;
            if (continuous) return EMPTY_BOUNDARY;
            return new TerrainVisualBoundary(true, 4, level);
        }

        if (!edge.visibleFace || edge.drop <= 0.08) return EMPTY_BOUNDARY;
        if (edge.contactType == TileType.Chasm) return new TerrainVisualBoundary(true, 1, edge.toZ);
        if (edge.contactType == TileType.Water) return new TerrainVisualBoundary(true, 2, edge.toZ);
        if (edge.contactType == TERRAIN_CONTACT_OUTSIDE) return new TerrainVisualBoundary(true, 3, edge.toZ);
        // Quantising only the comparison key joins tiny model epsilon differences without changing real heights.
        return new TerrainVisualBoundary(true, 10 + Math.round(edge.toZ * 4), edge.toZ);
    }

    private static double contourHash01(double x, double y)
    {
        int n = Math.imul(Js.ToInt32(x), 0x1f123bb5) ^ Math.imul(Js.ToInt32(y), 0x5f356495) ^ 0x4d3a2f19;
        n = Math.imul(n ^ (int)((uint)n >> 16), 0x45d9f3b);
        n = Math.imul(n ^ (int)((uint)n >> 16), 0x45d9f3b);
        return (uint)(n ^ (int)((uint)n >> 16)) / 4294967296.0;
    }

    /// <summary>Bilinear four-cell value noise over world corners: one smooth geological gesture per few cells.</summary>
    private static double contourMacroField(double x, double y)
    {
        double sx = x / CONTOUR_MACRO_SCALE;
        double sy = y / CONTOUR_MACRO_SCALE;
        double ix = Math.floor(sx);
        double iy = Math.floor(sy);
        double fx = smooth01(sx - ix);
        double fy = smooth01(sy - iy);
        double north = mixValue(contourHash01(ix, iy), contourHash01(ix + 1, iy), fx);
        double south = mixValue(contourHash01(ix, iy + 1), contourHash01(ix + 1, iy + 1), fx);
        return mixValue(north, south, fy);
    }

    private static double smooth01(double value) => value * value * (3 - 2 * value);

    private static double mixValue(double a, double b, double t) => a + (b - a) * t;

    private static double clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
}
