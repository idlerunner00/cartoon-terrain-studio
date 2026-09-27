// Port of packages/shared/src/domain/dungeon/terrainKit.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public class TileAnchor
{
    public int tx;
    public int ty;

    public TileAnchor() { }
}

public sealed class OrganicZone : TileAnchor
{
    public double rx;
    public double ry;
}

public class BrushOptions
{
    public int? minBorder;
    /// <summary>(tx, ty, current TileType) → may this brush write the tile?</summary>
    public Func<int, int, int, bool>? canWrite;
    public byte[]? mask;
}

public sealed class OrganicLineOptions : BrushOptions
{
    public bool? ease;
    public double? wobbleScale;
    public double? stepsPerTile;
}

/// <summary>`BrushOptions &amp; { pad?: number; wobble?: number }` (carveOrganicBlob).</summary>
public sealed class OrganicBlobOptions : BrushOptions
{
    public double? pad;
    public double? wobble;
}

public sealed class WalkableFootprintOptions
{
    /// <summary>Minimum readable passage footprint. Odd values keep the local tile visually centred.</summary>
    public int? size;
    public int? minBorder;
    public bool? includeBridge;
}

public sealed class SolidElevationOptions
{
    public double maxLevel;
    public double? minLevel;
    public Func<int, int, double>? fallback;
    public double? wallStep;
    public bool? includeWaterAsNeighbour;
    public bool? preserveWater;
    /// <summary>(tx, ty, tile) → stored water level.</summary>
    public Func<int, int, int, double>? waterLevel;
    /// <summary>4 | 8.</summary>
    public int? neighbourhood;
    public double? minimumBlockedLevel;
}

public sealed class ElevationFootprintOptions
{
    public double? minLevel;
    public double? maxLevel;
    public int? patchSize;
    public int? maxPasses;
    public bool? protectBoundary;
    /// <summary>
    /// With protectBoundary: width (tiles) of the outer band this pass must NEVER modify — components touching the
    /// band are left as-is and stamped patches must lie fully inside it. Streamed endless chunks pass a band wider
    /// than any relax cascade a footprint edit can start, so the chunk's outermost ring provably keeps its pure
    /// global-noise level and two neighbours always agree across their seam (the cross-chunk "no invisible wall"
    /// guarantee). Defaults to 1 (the classic edge-ring semantics).
    /// </summary>
    public int? boundaryMargin;
}

public sealed class StandardElevationOptions
{
    public double? maxLevel;
    public double? minLevel;
    public int? baseTx;
    public int? baseTy;
    public double? minPatchLevel;
    public bool? protectBoundary;
    /// <summary>
    /// Walkable ground level at a GLOBAL tile coordinate. Overrides the default biome-profile noise level so a
    /// generator can modulate relief per position (e.g. the endless run's depth/belt fields). MUST be a pure
    /// function of the global tile (identical on both sides of every chunk seam) and keep the shared ≤1-step
    /// smoothness between neighbouring tiles — the seam contract and the relax pass both rely on it.
    /// </summary>
    public Func<int, int, double>? levelAt;
    /// <summary>
    /// Stored Water terrace at a GLOBAL tile coordinate. Receives the already sampled local ground level so an
    /// Endless generator can pool water on a coarser hydraulic ladder without re-running its expensive relief
    /// field. Like levelAt, it must be a pure seam-stable function.
    /// </summary>
    public Func<int, int, double, double>? waterLevelAt;
    /// <summary>
    /// Summit level of a WALL MASS at a GLOBAL tile — the height the deep interior of a thick Solid mass towers to
    /// (rock cliffs). Only interior wall tiles with no walkable neighbour read it; the mass edges
    /// keep their neighbour-relative height, so masses rise from their ground toward this summit. Optional — when
    /// absent, interior walls fall back to one step above their ground noise (the pre-expansion behaviour). MUST be
    /// a pure function of the global tile (seam-identical); it needs no ≤1-step contract (walls are visual volume).
    /// </summary>
    public Func<int, int, double>? wallMassifAt;
}

public sealed class EndlessChunkProgression
{
    public double depthChunks;
    public double openness;
    public double density;
    public double elevation;
    public double hazards;
    public double walls;
    public double arenas;
}

public sealed class MaterializeUnclimbableEdgesOptions
{
    public double? maxClimb;
    /// <summary>Return true for semantic anchors that must stay walkable; the opposite side becomes the wall instead.</summary>
    public Func<int, int, int, bool>? protect;
}

public static partial class TerrainKit
{
    // `export { TERRAIN_ENDLESS_WALL_BASE_RISE, TERRAIN_STANDARD_WALL_BASE_RISE, TERRAIN_STANDARD_WALL_EXTRA_RISE,
    // type StandardWallRiseOptions, endlessWallRiseAt, standardWallRiseAt, terrainHash } from './terrainRules.js';`
    //
    // Re-exports live with their owner (TerrainRules / TerrainModel). Only terrainHash is forwarded, because the
    // legacy generators import it through the kit; forwarding the rest would only make files that `using static`
    // both module classes ambiguous.
    public static double terrainHash(double tx, double ty, double salt = 0) => TerrainRules.terrainHash(tx, ty, salt);

    public static double smoothstep01(double t) => Scalar.smoothstep(t);

    public static double clampElevationLevel(double level, double maxLevel, double minLevel = 0) =>
        level < minLevel ? minLevel : level > maxLevel ? maxLevel : Js.ToInt32(level);

    /// <summary>Shared authored/generator rule: a basin surface datum sits one logical step below its local dry ground.</summary>
    public static double standardWaterStoredLevelAt(
        double groundLevel,
        double maxLevel = MAX_ELEVATION,
        double minLevel = MIN_ELEVATION) =>
        clampElevationLevel(groundLevel - 1, maxLevel, minLevel);

    private static bool canTouch(byte[] tiles, int width, int height, int tx, int ty, BrushOptions? options)
    {
        int border = options?.minBorder ?? 1;
        if (tx < border || ty < border || tx >= width - border || ty >= height - border) return false;
        int current = getTile(tiles, width, height, tx, ty);
        return options?.canWrite != null ? options.canWrite(tx, ty, current) : true;
    }

    public static void paintFloorBrush(
        byte[] tiles,
        int width,
        int height,
        double cx,
        double cy,
        double radius,
        BrushOptions? options = null)
    {
        int r = (int)Math.max(1, Math.ceil(radius));
        double rr = (radius + 0.35) * (radius + 0.35);
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > rr) continue;
                int tx = (int)Math.round(cx + dx);
                int ty = (int)Math.round(cy + dy);
                if (!canTouch(tiles, width, height, tx, ty, options)) continue;
                int idx = tileIndex(width, tx, ty);
                tiles[idx] = TileType.Floor;
                if (options?.mask != null) options.mask[idx] = 1;
            }
        }
    }

    public static void carveOrganicBlob(
        byte[] tiles,
        int width,
        int height,
        OrganicZone zone,
        double salt,
        OrganicBlobOptions? options = null)
    {
        double rx = zone.rx + (options?.pad ?? 0);
        double ry = zone.ry + (options?.pad ?? 0);
        int border = options?.minBorder ?? 1;
        int x0 = (int)Math.max(border, Math.floor(zone.tx - rx - 2));
        int x1 = (int)Math.min(width - border - 1, Math.ceil(zone.tx + rx + 2));
        int y0 = (int)Math.max(border, Math.floor(zone.ty - ry - 2));
        int y1 = (int)Math.min(height - border - 1, Math.ceil(zone.ty + ry + 2));
        double wobbleAmp = options?.wobble ?? 0.32;
        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                double nx = (tx + 0.5 - zone.tx) / Math.max(1, rx);
                double ny = (ty + 0.5 - zone.ty) / Math.max(1, ry);
                double wobble = (terrainHash(tx, ty, salt) - 0.5) * wobbleAmp;
                if (nx * nx + ny * ny > 1 + wobble) continue;
                if (!canTouch(tiles, width, height, tx, ty, options)) continue;
                int idx = tileIndex(width, tx, ty);
                tiles[idx] = TileType.Floor;
                if (options?.mask != null) options.mask[idx] = 1;
            }
        }
    }

    public static void carveOrganicLine(
        byte[] tiles,
        int width,
        int height,
        double ax,
        double ay,
        double bx,
        double by,
        double brushWidth,
        double salt,
        OrganicLineOptions? options = null)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double len = Math.hypot(dx, dy);
        if (!Js.Truthy(len)) len = 1; // `Math.hypot(dx, dy) || 1`
        double steps = Math.ceil(len * (options?.stepsPerTile ?? 2.3));
        double nx = -dy / len;
        double ny = dx / len;
        double waveAmp = Math.min(5, Math.max(1.3, brushWidth * (options?.wobbleScale ?? 0.5)));
        double phase = terrainHash(ax + bx, ay + by, salt) * Math.PI * 2;
        for (int s = 0; s <= steps; s++)
        {
            double t = s / steps;
            double tt = options?.ease == false ? t : t * t * (3 - 2 * t);
            double wobble =
                Math.sin(t * Math.PI * 2 + phase) * waveAmp +
                Math.sin(t * Math.PI * 5 + phase * 0.67) * waveAmp * 0.32;
            double breathe =
                brushWidth +
                Math.floor(terrainHash(Math.round(ax + dx * tt), Math.round(ay + dy * tt), salt + 11) * 2);
            paintFloorBrush(
                tiles,
                width,
                height,
                ax + dx * tt + nx * wobble,
                ay + dy * tt + ny * wobble,
                breathe,
                options);
        }
    }

    private static bool isWalkableFootprint(int tile, bool includeBridge) =>
        tile == TileType.Floor || (includeBridge && tile == TileType.Bridge);

    private static bool hasMinimumWalkableFootprintAtWithRules(
        byte[] tiles,
        int width,
        int height,
        int tx,
        int ty,
        int size,
        int border,
        bool includeBridge)
    {
        if (!isWalkableFootprint(getTile(tiles, width, height, tx, ty), includeBridge)) return false;
        if (width - border * 2 < size || height - border * 2 < size) return false;

        for (int top = ty - size + 1; top <= ty; top++)
        {
            if (top < border || top + size > height - border) continue;
            for (int left = tx - size + 1; left <= tx; left++)
            {
                if (left < border || left + size > width - border) continue;
                bool ok = true;
                for (int y = top; y < top + size && ok; y++)
                {
                    for (int x = left; x < left + size; x++)
                    {
                        if (!isWalkableFootprint(tiles[tileIndex(width, x, y)], includeBridge))
                        {
                            ok = false;
                            break;
                        }
                    }
                }
                if (ok) return true;
            }
        }
        return false;
    }

    public static bool hasMinimumWalkableFootprintAt(
        byte[] tiles,
        int width,
        int height,
        int tx,
        int ty,
        WalkableFootprintOptions? options = null) =>
        hasMinimumWalkableFootprintAtWithRules(
            tiles,
            width,
            height,
            tx,
            ty,
            Math.max(3, options?.size ?? 5),
            options?.minBorder ?? 0,
            options?.includeBridge ?? true);

    /// <summary>
    /// Lower walkable ground until no standable neighbour is more than one level away.
    ///
    /// ## The neighbourhood is EIGHT, and that is the whole correctness of this pass
    ///
    /// It used to relax across the four cardinal edges only, while every consumer of the result reads the
    /// neighbourhood as eight: `navigation.ts` routes 8-connected with a no-corner-cut rule, `terrainMovementRuleFor`
    /// applies `maxStep` to whichever pair it is handed, and `elevation.ts` states the invariant as "any two
    /// neighbouring tiles (4- **AND** 8-connected) within ONE level". A diagonal pair was therefore never examined
    /// by the only pass that could have fixed it, and the contract held only for as long as the underlying field
    /// was too gentle to produce a diagonal breach on its own.
    ///
    /// Measured on the shipping raster before this change: a 5x5-chunk `highland_pass` region carried **116**
    /// walkable diagonal pairs above one level, the worst of them **four** levels apart — two floor tiles touching
    /// at a corner with a four-level cliff between them, which navigation reads as an impassable diagonal and the
    /// renderer draws as a hole in the ground. Every one of them was diagonal; the cardinal edges were already
    /// clean, exactly as a four-neighbour relaxation guarantees.
    ///
    /// Relaxing the full ring is also the precondition for any richer relief: a steeper landform produces diagonal
    /// breaches at a far higher rate than cardinal ones, because the diagonal spans 1.41 tiles of ground for the
    /// same one-level budget. Raising the mountains without this fix would have multiplied the defect rather than
    /// revealed it.
    ///
    /// The pass only ever LOWERS ground, so it cannot introduce a breach elsewhere, and it terminates: each sweep
    /// either changes nothing or strictly decreases a bounded integer field.
    /// </summary>
    /// <param name="passes">Defaults to `maxLevel * 3 + 3` when null (JS default parameter).</param>
    public static void relaxWalkableElevationSteps(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        double maxLevel,
        double? passes = null,
        double minLevel = 0,
        Func<int, int, int, bool>? protect = null)
    {
        double passCount = passes ?? maxLevel * 3 + 3;
        for (int pass = 0; pass < passCount; pass++)
        {
            bool changed = false;
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int idx = tileIndex(width, tx, ty);
                    if (!isWalkable(tiles[idx])) continue;
                    if (protect != null && protect(tx, ty, idx)) continue;
                    double level = elevation[idx];
                    foreach (var (dx, dy) in WALKABLE_STEP_NEIGHBOURS)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (!isWalkable(tiles[ni])) continue;
                        double other = elevation[ni];
                        if (level > other + 1)
                        {
                            level = other + 1;
                            changed = true;
                        }
                    }
                    elevation[idx] = Js.I8(clampElevationLevel(level, maxLevel, minLevel));
                }
            }
            if (!changed) break;
        }
    }

    /// <summary>The eight-neighbour ring the walkable step contract is stated over. Cardinals first, then diagonals.</summary>
    private static readonly (int dx, int dy)[] WALKABLE_STEP_NEIGHBOURS =
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

    // Inline `[dx, dy]` literals of the original, hoisted so the hot loops do not allocate.
    private static readonly (int dx, int dy)[] FORWARD_NEIGHBOURS = { (1, 0), (0, 1) };
    private static readonly (int dx, int dy)[] CARDINAL_NEIGHBOURS = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly (int dx, int dy)[] EIGHT_NEIGHBOURS =
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

    private static int preferredCliffWallTarget(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int a,
        int b,
        Func<int, int, int, bool>? protect = null)
    {
        // `elevation[i] ?? 0`
        double ah = (uint)a < (uint)elevation.Length ? elevation[a] : 0;
        double bh = (uint)b < (uint)elevation.Length ? elevation[b] : 0;
        int high = ah >= bh ? a : b;
        int low = high == a ? b : a;
        for (int k = 0; k < 2; k++)
        {
            int idx = k == 0 ? high : low; // `for (const idx of [high, low])`
            int tx = idx % width;
            int ty = idx / width;
            if (tiles[idx] == TileType.Bridge || (protect != null && protect(tx, ty, idx))) continue;
            return idx;
        }
        for (int k = 0; k < 2; k++)
        {
            int idx = k == 0 ? high : low;
            int tx = idx % width;
            int ty = idx / width;
            if (!(protect != null && protect(tx, ty, idx))) return idx;
        }
        return -1;
    }

    /// <summary>
    /// Standard terrain contract: a walkable-to-walkable edge may be a ramp (height delta &lt;= maxClimb), never an
    /// invisible wall. Any steeper edge is materialized as ordinary Solid terrain, preferring the high-side tile so
    /// renderers show the same wall/rock language they already use everywhere else.
    /// </summary>
    public static List<int> materializeUnclimbableWalkableEdges(
        byte[] tiles,
        sbyte[]? elevation,
        int width,
        int height,
        MaterializeUnclimbableEdgesOptions? options = null)
    {
        if (elevation == null) return new List<int>();
        double maxClimb = options?.maxClimb ?? TerrainRules.TERRAIN_STANDARD_MAX_CLIMB;
        var marked = new byte[tiles.Length];
        var @out = new List<int>();

        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int idx = tileIndex(width, tx, ty);
                if (!isWalkable(tiles[idx])) continue;
                foreach (var (dx, dy) in FORWARD_NEIGHBOURS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int ni = tileIndex(width, nx, ny);
                    if (!isWalkable(tiles[ni])) continue;
                    var connection = TerrainRules.terrainConnectionFromCells(
                        tiles[idx],
                        tiles[ni],
                        (uint)idx < (uint)elevation.Length ? elevation[idx] : 0,
                        (uint)ni < (uint)elevation.Length ? elevation[ni] : 0,
                        new TerrainConnectionOptions { maxClimb = maxClimb });
                    if (connection.kind != "cliff") continue;
                    int target = preferredCliffWallTarget(tiles, elevation, width, idx, ni, options?.protect);
                    if (target < 0 || marked[target] != 0) continue;
                    marked[target] = 1;
                    @out.push(target);
                }
            }
        }

        foreach (int idx in @out) tiles[idx] = TileType.Solid;
        if (@out.Count > 0) TerrainRules.invalidateTerrainRulesMaterialization(tiles);
        return @out;
    }

    public static void assignBlockedElevationFromNeighbours(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        SolidElevationOptions options)
    {
        double wallStep = options.wallStep ?? 1;
        double minLevel = options.minLevel ?? 0;
        (int dx, int dy)[] dirs = options.neighbourhood == 8 ? EIGHT_NEIGHBOURS : CARDINAL_NEIGHBOURS;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int idx = tileIndex(width, tx, ty);
                    int tile = tiles[idx];
                    if (tile == TileType.Chasm) continue;
                    if (tile == TileType.Water)
                    {
                        if (options.waterLevel != null)
                            elevation[idx] = Js.I8(clampElevationLevel(
                                options.waterLevel(tx, ty, tile),
                                options.maxLevel,
                                minLevel));
                        else if (!(options.preserveWater ?? false)) elevation[idx] = 0;
                        continue;
                    }
                    if (isWalkable(tile)) continue;
                    double near = double.NegativeInfinity;
                    foreach (var (dx, dy) in dirs)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        int nt = tiles[ni];
                        if (isWalkable(nt) || ((options.includeWaterAsNeighbour ?? false) && nt == TileType.Water))
                        {
                            near = Math.max(near, elevation[ni]);
                        }
                    }
                    if (Number.isFinite(near))
                    {
                        elevation[idx] = Js.I8(clampElevationLevel(
                            Math.max(options.minimumBlockedLevel ?? minLevel, near + wallStep),
                            options.maxLevel,
                            minLevel));
                    }
                    else if (pass == 2 && options.fallback != null)
                        elevation[idx] = Js.I8(clampElevationLevel(
                            options.fallback(tx, ty),
                            options.maxLevel,
                            minLevel));
                }
            }
        }
    }

    /// <summary>
    /// Assign each ravine's positive storage depth after ordinary terrain relaxation.
    ///
    /// A Chasm surface is `-depth`; therefore being five levels below every non-Chasm rim neighbour means
    /// `depth >= 5 - neighbourLevel`. Raster-edge Chasms conservatively take the full 30-level depth because the
    /// independently generated adjacent chunk may legally expose ground at -25. This makes the rule seam-safe
    /// without a neighbour-chunk dependency or a second generation pass.
    /// </summary>
    public static void assignChasmDepths(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int baseTx = 0,
        int baseTy = 0)
    {
        int count = Math.min(width * height, Math.min(tiles.Length, elevation.Length));
        for (int index = 0; index < count; index++)
        {
            if (tiles[index] != TileType.Chasm) continue;
            int tx = index % width;
            int ty = index / width;
            double requiredDepth = TerrainModel.CHASM_MIN_DEPTH;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny))
                    {
                        requiredDepth = TerrainModel.CHASM_MAX_DEPTH;
                        continue;
                    }
                    int neighbour = tileIndex(width, nx, ny);
                    // A connected Chasm is one ravine body, not its own rim; requiring both adjacent void cells to be five
                    // below one another would be mathematically impossible.
                    if (tiles[neighbour] == TileType.Chasm) continue;
                    requiredDepth = Math.max(requiredDepth, TerrainModel.CHASM_MIN_DEPTH - elevation[neighbour]);
                }
            }
            double organicDepth = TerrainRules.standardChasmDepthAt(
                baseTx + (index % width),
                baseTy + index / width);
            elevation[index] = Js.I8(Math.max(
                TerrainModel.CHASM_MIN_DEPTH,
                Math.min(TerrainModel.CHASM_MAX_DEPTH, Math.max(organicDepth, requiredDepth))));
        }
    }

    private static bool isFootprintTile(int tile) => isWalkable(tile) && tile != TileType.Bridge;

    private static bool hasExactLandPatch(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        double level,
        IReadOnlyList<int> cells,
        int patchSize)
    {
        int half = patchSize / 2; // Math.floor(patchSize / 2), patchSize >= 3
        for (int c = 0; c < cells.Count; c++)
        {
            int idx = cells[c];
            int cx = idx % width;
            int cy = idx / width;
            if (cx < half || cy < half || cx >= width - half || cy >= height - half) continue;
            bool ok = true;
            for (int dy = -half; dy <= half && ok; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    int ni = tileIndex(width, cx + dx, cy + dy);
                    if (!isFootprintTile(tiles[ni]) || elevation[ni] != level)
                    {
                        ok = false;
                        break;
                    }
                }
            }
            if (ok) return true;
        }
        return false;
    }

    private static int bestFootprintCenter(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        double level,
        IReadOnlyList<int> cells,
        int patchSize,
        int margin)
    {
        int half = patchSize / 2; // Math.floor(patchSize / 2), patchSize >= 3
        int lo = Math.max(half, margin + half);
        int best = -1;
        double bestScore = double.NegativeInfinity;
        for (int c = 0; c < cells.Count; c++)
        {
            int cell = cells[c];
            int sx = cell % width;
            int sy = cell / width;
            for (int cy = sy - half; cy <= sy + half; cy++)
            {
                for (int cx = sx - half; cx <= sx + half; cx++)
                {
                    if (cx < lo || cy < lo || cx >= width - lo || cy >= height - lo) continue;
                    int score = 0;
                    bool ok = true;
                    for (int dy = -half; dy <= half && ok; dy++)
                    {
                        for (int dx = -half; dx <= half; dx++)
                        {
                            int ni = tileIndex(width, cx + dx, cy + dy);
                            int tile = tiles[ni];
                            if (!isFootprintTile(tile))
                            {
                                ok = false;
                                break;
                            }
                            if (elevation[ni] == level) score += 2;
                            else if (Math.abs(elevation[ni] - level) <= TerrainRules.TERRAIN_STANDARD_MAX_CLIMB) score += 1;
                        }
                    }
                    if (ok && score > bestScore)
                    {
                        bestScore = score;
                        best = tileIndex(width, cx, cy);
                    }
                }
            }
        }
        return best;
    }

    private static void lowerFootprintFragment(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        double level,
        IReadOnlyList<int> cells,
        double minLevel,
        double maxLevel)
    {
        for (int c = 0; c < cells.Count; c++)
        {
            int idx = cells[c];
            int tx = idx % width;
            int ty = idx / width;
            double target = level > minLevel ? level - 1 : minLevel;
            foreach (var (dx, dy) in CARDINAL_NEIGHBOURS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int ni = tileIndex(width, nx, ny);
                if (!isFootprintTile(tiles[ni]) || elevation[ni] == level) continue;
                double nl = elevation[ni];
                target = Math.max(target, nl > level ? level - 1 : nl);
            }
            elevation[idx] = Js.I8(clampElevationLevel(target, maxLevel, minLevel));
        }
    }

    public static void enforceExactLevelFootprints(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        ElevationFootprintOptions? options = null)
    {
        int patchSize = options?.patchSize ?? 3;
        double minLevel = options?.minLevel ?? 2;
        double maxLevel = options?.maxLevel ?? MAX_ELEVATION;
        int maxPasses = options?.maxPasses ?? 4;
        bool protectBoundary = options?.protectBoundary ?? false;
        int margin = protectBoundary ? Math.max(1, options?.boundaryMargin ?? 1) : 0;
        if (patchSize < 3 || patchSize % 2 == 0) return;

        var seen = new byte[width * height];
        var queue = new int[width * height];
        var cells = new List<int>();

        for (int pass = 0; pass < maxPasses; pass++)
        {
            seen.fill((byte)0);
            bool changed = false;
            for (int sy = 0; sy < height; sy++)
            {
                for (int sx = 0; sx < width; sx++)
                {
                    int start = tileIndex(width, sx, sy);
                    double level = elevation[start];
                    if (
                        seen[start] != 0 ||
                        level < minLevel ||
                        level > maxLevel ||
                        !isFootprintTile(tiles[start]))
                        continue;

                    int head = 0;
                    int tail = 0;
                    bool touchesBoundary = false;
                    cells.Clear();
                    seen[start] = 1;
                    queue[tail++] = start;
                    while (head < tail)
                    {
                        int idx = queue[head++];
                        int tx = idx % width;
                        int ty = idx / width;
                        cells.push(idx);
                        if (tx < margin || ty < margin || tx >= width - margin || ty >= height - margin)
                            touchesBoundary = true;
                        foreach (var (dx, dy) in CARDINAL_NEIGHBOURS)
                        {
                            int nx = tx + dx;
                            int ny = ty + dy;
                            if (!inBounds(width, height, nx, ny)) continue;
                            int ni = tileIndex(width, nx, ny);
                            if (seen[ni] != 0 || elevation[ni] != level || !isFootprintTile(tiles[ni]))
                                continue;
                            seen[ni] = 1;
                            queue[tail++] = ni;
                        }
                    }

                    if (protectBoundary && touchesBoundary) continue;
                    if (hasExactLandPatch(tiles, elevation, width, height, level, cells, patchSize)) continue;

                    int center = bestFootprintCenter(
                        tiles,
                        elevation,
                        width,
                        height,
                        level,
                        cells,
                        patchSize,
                        margin);
                    if (center >= 0)
                    {
                        int cx = center % width;
                        int cy = center / width;
                        int half = patchSize / 2;
                        for (int dy = -half; dy <= half; dy++)
                        {
                            for (int dx = -half; dx <= half; dx++)
                            {
                                elevation[tileIndex(width, cx + dx, cy + dy)] = Js.I8(clampElevationLevel(
                                    level,
                                    maxLevel,
                                    minLevel));
                            }
                        }
                    }
                    else
                    {
                        lowerFootprintFragment(tiles, elevation, width, height, level, cells, minLevel, maxLevel);
                    }
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    /// <summary>
    /// With StandardElevationOptions.protectBoundary: the footprint pass may never edit this outer band.
    /// The bound is the worst relax cascade a footprint edit can start (`maxPasses` single-level lowerings = 4
    /// tiles of reach) plus a safety ring — so a streamed chunk's outermost ring provably keeps its pure
    /// global-noise level, and the two chunks meeting at any seam always agree within one climbable step.
    /// </summary>
    private const int STANDARD_ELEVATION_SEAM_BAND = 6;

    public static sbyte[] buildStandardElevationField(
        byte[] tiles,
        int width,
        int height,
        double seed,
        string biomeKey,
        StandardElevationOptions? options = null)
    {
        double maxLevel = options?.maxLevel ?? MAX_ELEVATION;
        double minLevel = options?.minLevel ?? MIN_ELEVATION;
        int baseTx = options?.baseTx ?? 0;
        int baseTy = options?.baseTy ?? 0;
        ElevationProfile profile = Terrain.elevationProfileFor(biomeKey);
        Func<int, int, double> levelAt =
            options?.levelAt ??
            ((gtx, gty) => elevationLevelAt(seed, gtx, gty, profile));
        var elevation = new sbyte[width * height];

        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int idx = tileIndex(width, tx, ty);
                int tile = tiles[idx];
                if (tile == TileType.Water)
                {
                    double groundLevel = levelAt(baseTx + tx, baseTy + ty);
                    elevation[idx] = Js.I8(clampElevationLevel(
                        options?.waterLevelAt?.Invoke(baseTx + tx, baseTy + ty, groundLevel) ??
                            standardWaterStoredLevelAt(groundLevel, maxLevel, minLevel),
                        maxLevel,
                        minLevel));
                }
                else if (isWalkable(tile))
                    elevation[idx] = Js.I8(clampElevationLevel(levelAt(baseTx + tx, baseTy + ty), maxLevel, minLevel));
                else elevation[idx] = Js.I8(maxLevel);
            }
        }

        double relaxPasses = (maxLevel - minLevel) * 3 + 3;
        relaxWalkableElevationSteps(tiles, elevation, width, height, maxLevel, relaxPasses, minLevel);
        enforceExactLevelFootprints(tiles, elevation, width, height, new ElevationFootprintOptions
        {
            minLevel = options?.minPatchLevel ?? 2,
            maxLevel = maxLevel,
            protectBoundary = options?.protectBoundary ?? false,
            boundaryMargin = STANDARD_ELEVATION_SEAM_BAND,
        });
        Func<int, int, int, bool>? protectSeamBand = options?.protectBoundary == true
            ? (tx, ty, _) =>
                tx < STANDARD_ELEVATION_SEAM_BAND ||
                ty < STANDARD_ELEVATION_SEAM_BAND ||
                tx >= width - STANDARD_ELEVATION_SEAM_BAND ||
                ty >= height - STANDARD_ELEVATION_SEAM_BAND
            : null;
        relaxWalkableElevationSteps(
            tiles,
            elevation,
            width,
            height,
            maxLevel,
            relaxPasses,
            minLevel,
            protectSeamBand);
        Func<int, int, double>? wallMassifAt = options?.wallMassifAt;
        assignBlockedElevationFromNeighbours(tiles, elevation, width, height, new SolidElevationOptions
        {
            maxLevel = maxLevel,
            minLevel = minLevel,
            wallStep = 1,
            includeWaterAsNeighbour = true,
            // A deep-interior wall towers to the greater of one step above its ground noise and its mass's summit
            // height — so thick rock masses rise to real cliffs while thin walls stay a step tall.
            fallback = (tx, ty) =>
            {
                double @base = levelAt(baseTx + tx, baseTy + ty) + 1;
                double massif = wallMassifAt != null ? wallMassifAt(baseTx + tx, baseTy + ty) : 0;
                return clampElevationLevel(massif > @base ? massif : @base, maxLevel, minLevel);
            },
            preserveWater = true,
        });
        assignChasmDepths(tiles, elevation, width, height, baseTx, baseTy);
        return elevation;
    }

    /// <summary>
    /// Allocation-free scalar of the endless depth curve: how much of the biome's relief has eased in at a
    /// radial depth (in chunks). The opening ring spawns calm and flat; full terraces arrive a few chunks out.
    /// Kept as its own export so per-tile hot paths (the endless elevation field) read the SAME curve the
    /// chunk-level endlessProgressionForPoint reports, without allocating its result object.
    /// </summary>
    public static double endlessReliefEaseAt(double depthChunks) => smoothstep01((depthChunks - 0.85) / 3.1);

    /// <summary>
    /// Depth curve for endless terrain. The first ring stays open and calm, then the world ramps through
    /// readable height, hazards, denser cave-fields and finally harder walls/arenas.
    /// </summary>
    public static EndlessChunkProgression endlessProgressionForPoint(double gtx, double gty, double chunkTiles)
    {
        double depthChunks = Math.hypot(gtx / chunkTiles, gty / chunkTiles);
        double elevation = endlessReliefEaseAt(depthChunks);
        double hazards = smoothstep01((depthChunks - 1.45) / 3.0);
        double density = smoothstep01((depthChunks - 1.15) / 5.0);
        double walls = smoothstep01((depthChunks - 1.65) / 4.2);
        return new EndlessChunkProgression
        {
            depthChunks = depthChunks,
            openness = 1 - density,
            density = density,
            elevation = elevation,
            hazards = hazards,
            walls = walls,
            arenas = smoothstep01((depthChunks - 2.4) / 3.2),
        };
    }

    public static EndlessChunkProgression endlessProgressionForChunk(double cx, double cy) =>
        endlessProgressionForPoint(cx + 0.5, cy + 0.5, 1);
}
