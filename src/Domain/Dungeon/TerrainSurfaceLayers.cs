// Port of packages/shared/src/domain/dungeon/terrainSurfaceLayers.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * **The one composer of an artifact's surface, variant and circulation layers.**
 *
 * ## Why this module exists
 *
 * These three layers are what turn a tile raster into a *place*: `surface` states the material a cell is made
 * of (which is what puts grass, sward, scree, sand and brass on the ground and what a footstep queries),
 * `variant` states what authored thing owns a cell (route, court, set piece, landmark), and `floorUsage`
 * states how heavily circulation has worn it — a compacted core with a softly worn verge, which is the
 * difference between a trail through a meadow and a brown stripe painted over one.
 *
 * The streamed Country composer built all three. The map simulator built **none** of them: it emitted an
 * artifact whose `surface` was `Auto` for every tile, whose `variant` was zero everywhere, and whose
 * `floorUsage` was a bare 0/255 stencil with no verge. Measured on a 128x96 `highland_pass` simulation before
 * this module existed:
 *
 * | layer        | streamed world           | simulated map          |
 * | ------------ | ------------------------ | ---------------------- |
 * | `surface`    | 19.5 % grass, 5 materials | **100 % `Auto`**       |
 * | `variant`    | routes, courts, landmarks | **all zero**           |
 * | `floorUsage` | graded 0..255 band        | **binary 0/255 only**  |
 *
 * So every ground material the substrate registry authors — and therefore every grass patch — was invisible
 * on the simulated map, and its roads had no shoulders. Not because the map simulator disagreed about the
 * rule, but because it never ran it. That is precisely the failure mode "one rule, one place" exists to
 * prevent, so the rule moved here and both producers now call it.
 */

/// <summary>
/// The authored masks a composer may hand in. All are optional: a producer states what it actually knows,
/// and a layer nobody claims simply stays at its neutral value.
/// </summary>
public sealed class TerrainSurfaceLayerMasks
{
    /// <summary>Graded 0..255 circulation band. Only its core is treated as worn ground.</summary>
    public byte[]? usage;
    /// <summary>Binary corridor network — broader than the artery, so it marks variant but never repaints material.</summary>
    public byte[]? route;
    /// <summary>Open authored courts and plazas.</summary>
    public byte[]? court;
    /// <summary>Per-cell landmark id, 1-based; 0 where no landmark owns the cell.</summary>
    public byte[]? landmark;
    /// <summary>Cells a built set piece owns.</summary>
    public byte[]? setPieceClaim;
    /// <summary>
    /// Usage at or above which open ground states its **worn** facies rather than its ordinary one.
    ///
    /// Defaults to 1 — the whole graded band, verge included — which is what the streamed world has always
    /// shipped. It is a parameter rather than a constant because the two readings are both defensible (a verge
    /// is thinned ground, not untouched ground) and because changing it silently would repaint every road in
    /// the world; a producer that wants a narrower material core states so.
    /// </summary>
    public double? wornAtUsage;
}

public sealed class TerrainSurfaceLayers
{
    public byte[] surface = null!;
    public byte[] variant = null!;
}

public static class TerrainSurfaceLayersModule
{
    /// <summary>
    /// Usage value at the outermost worn cell of a route band.
    ///
    /// Not zero: a trail's edge is thinned meadow, not untouched ground, and a band that fell straight to zero
    /// read as a cut-out rather than as wear.
    /// </summary>
    public const int PRIMARY_ROUTE_EDGE_USAGE = 42;

    /// <summary>Variant id for a cell a built set piece owns — "the world composed this" vs "a stamp built this".</summary>
    public const int TERRAIN_VARIANT_SET_PIECE = 3;
    public const int TERRAIN_VARIANT_ROUTE = 1;
    public const int TERRAIN_VARIANT_COURT = 2;
    /// <summary>Landmark variants occupy `4 + (id − 1) mod LANDMARK_VARIANT_SLOTS`.</summary>
    public const int TERRAIN_VARIANT_LANDMARK_BASE = 4;
    public const int TERRAIN_VARIANT_LANDMARK_SLOTS = 12;
    /// <summary>
    /// Endless chunk width and the maximum ground-patch coverage cell. Kept numeric here to avoid coupling the
    /// generic surface composer to the Endless layout module. Larger simulated maps receive one pair per block.
    /// </summary>
    public const int REQUIRED_GROUND_PATCH_SPAN = 32;
    /// <summary>Small enough to preserve biome identity, large enough to read as an area instead of isolated pixels.</summary>
    public const int REQUIRED_GROUND_PATCH_CELLS = 12;

    private static int stampRequiredGroundPatch(
        byte[] surface,
        byte[] tiles,
        byte[] used,
        int width,
        int blockLeft,
        int blockTop,
        int blockRight,
        int blockBottom,
        int baseTx,
        int baseTy,
        int salt,
        double targetX,
        double targetY,
        int material)
    {
        var bestIndices = new int[REQUIRED_GROUND_PATCH_CELLS].fill(-1);
        var bestScores = new double[REQUIRED_GROUND_PATCH_CELLS].fill(double.PositiveInfinity);
        int candidates = 0;
        for (int ty = blockTop; ty < blockBottom; ty++)
        {
            for (int tx = blockLeft; tx < blockRight; tx++)
            {
                int index = ty * width + tx;
                // Out of range reads undefined in JS: `undefined !== Floor` skips the cell.
                if ((uint)index >= (uint)tiles.Length || tiles[index] != TileType.Floor || used[index] != 0) continue;
                candidates++;
                double dx = tx + 0.5 - targetX;
                double dy = ty + 0.5 - targetY;
                // A little coordinate-stable jitter keeps the compact patch edge organic while distance keeps it a patch.
                double score = dx * dx + dy * dy + TerrainRules.terrainHash(baseTx + tx, baseTy + ty, salt) * 2.25;
                if (score >= bestScores[REQUIRED_GROUND_PATCH_CELLS - 1]) continue;
                int slot = REQUIRED_GROUND_PATCH_CELLS - 1;
                while (slot > 0 && score < bestScores[slot - 1])
                {
                    bestScores[slot] = bestScores[slot - 1];
                    bestIndices[slot] = bestIndices[slot - 1];
                    slot--;
                }
                bestScores[slot] = score;
                bestIndices[slot] = index;
            }
        }
        int count = Math.min(REQUIRED_GROUND_PATCH_CELLS, candidates);
        for (int slot = 0; slot < count; slot++)
        {
            int index = bestIndices[slot];
            if (index < 0) continue;
            surface[index] = (byte)material;
            used[index] = 1;
        }
        return count;
    }

    /// <summary>Guarantee one compact grass patch and one compact exposed-soil patch in every 32x32 generated block.</summary>
    private static void enforceRequiredGroundPatches(
        byte[] surface,
        byte[] tiles,
        int width,
        int height,
        string biomeKey,
        int baseTx,
        int baseTy,
        byte[]? usage)
    {
        var used = new byte[tiles.Length];
        // A guaranteed patch has to survive the render contract. The route core and verge deliberately clear turf,
        // so choosing their cells here fulfilled the artifact statistic while producing no visible patch at all.
        if (usage != null)
            for (int index = 0; index < usage.Length; index++)
                // A typed-array store past the end is a no-op in JS.
                if (usage[index] != 0 && index < used.Length) used[index] = 1;
        int biomeSalt = TerrainCompositionField.terrainEcologyBiomeSalt(biomeKey);
        for (int blockTop = 0; blockTop < height; blockTop += REQUIRED_GROUND_PATCH_SPAN)
        {
            for (int blockLeft = 0; blockLeft < width; blockLeft += REQUIRED_GROUND_PATCH_SPAN)
            {
                int blockRight = Math.min(width, blockLeft + REQUIRED_GROUND_PATCH_SPAN);
                int blockBottom = Math.min(height, blockTop + REQUIRED_GROUND_PATCH_SPAN);
                int blockX = (int)Math.floor((double)(baseTx + blockLeft) / REQUIRED_GROUND_PATCH_SPAN);
                int blockY = (int)Math.floor((double)(baseTy + blockTop) / REQUIRED_GROUND_PATCH_SPAN);
                double grassRoll = TerrainRules.terrainHash(blockX, blockY, biomeSalt ^ 0x51a7c3d);
                double dirtRoll = TerrainRules.terrainHash(blockX, blockY, biomeSalt ^ 0x2d19b7f);
                int spanX = blockRight - blockLeft;
                int spanY = blockBottom - blockTop;
                double grassX = blockLeft + spanX * (0.2 + grassRoll * 0.3);
                double grassY = blockTop + spanY * (0.2 + dirtRoll * 0.3);
                double dirtX = blockRight - spanX * (0.2 + grassRoll * 0.3);
                double dirtY = blockBottom - spanY * (0.2 + dirtRoll * 0.3);
                stampRequiredGroundPatch(
                    surface,
                    tiles,
                    used,
                    width,
                    blockLeft,
                    blockTop,
                    blockRight,
                    blockBottom,
                    baseTx,
                    baseTy,
                    biomeSalt ^ 0x6e624eb7,
                    grassX,
                    grassY,
                    TerrainSurface.Grass);
                stampRequiredGroundPatch(
                    surface,
                    tiles,
                    used,
                    width,
                    blockLeft,
                    blockTop,
                    blockRight,
                    blockBottom,
                    baseTx,
                    baseTy,
                    biomeSalt ^ 0x13c8a91,
                    dirtX,
                    dirtY,
                    TerrainSurface.Floor);
            }
        }
    }

    /// <summary>
    /// Compose the surface and variant layers for a tile raster.
    ///
    /// `baseTx`/`baseTy` are the raster's origin in **world tiles**, which is what makes the substrate field
    /// continuous across a streaming seam: the facies is a pure function of world position, so two chunks that
    /// meet agree about the material at the column they share without consulting each other.
    /// </summary>
    public static TerrainSurfaceLayers composeTerrainSurfaceLayers(
        byte[] tiles,
        int width,
        int height,
        string biomeKey,
        int baseTx,
        int baseTy,
        TerrainSurfaceLayerMasks? masks = null)
    {
        masks ??= new TerrainSurfaceLayerMasks();
        var surface = new byte[tiles.Length];
        var variant = new byte[tiles.Length];
        TerrainSubstrateRecipe substrate = TerrainSubstrate.terrainSubstrateFor(biomeKey);
        double wornAtUsage = masks.wornAtUsage ?? 1;
        byte[]? usageMask = masks.usage;
        byte[]? setPieceClaim = masks.setPieceClaim;
        byte[]? route = masks.route;
        byte[]? court = masks.court;
        byte[]? landmark = masks.landmark;

        for (int index = 0; index < tiles.Length; index++)
        {
            int tile = tiles[index];
            if (tile == TileType.Water) surface[index] = TerrainSurface.Water;
            else if (tile == TileType.Bridge) surface[index] = TerrainSurface.Bridge;
            else if (tile == TileType.Chasm) surface[index] = TerrainSurface.Chasm;
            else if (tile == TileType.Cleft) surface[index] = TerrainSurface.Cleft;
            else if (tile == TileType.Underpass) surface[index] = TerrainSurface.Underpass;
            else if (tile == TileType.Solid) surface[index] = (byte)substrate.rockSurface;
            else
            {
                // Circulation does not replace the ground, it WEARS it: the facies states its own worn form, so a
                // trail reads as a trail through this particular country rather than as one stone ribbon laid over
                // every world.
                TerrainSubstrateFacies facies = TerrainSubstrate.terrainSubstrateFaciesAt(
                    substrate,
                    baseTx + (index % width) + 0.5,
                    baseTy + Math.floor((double)index / width) + 0.5);
                // ONLY the graded artery band is worn. The full corridor network (`route`) is most of the walkable
                // world, and calling all of it a trail repaints the world as one continuous path.
                // `masks.usage?.[index] ?? 0`: a missing mask or an out-of-range read both yield 0.
                double usageAt = usageMask != null && (uint)index < (uint)usageMask.Length ? usageMask[index] : 0;
                bool worn = usageAt >= wornAtUsage;
                surface[index] = (byte)(worn ? facies.worn : facies.surface);
            }

            // `masks.x?.[index]` is falsy for a missing mask and for an out-of-range read alike.
            if (setPieceClaim != null && (uint)index < (uint)setPieceClaim.Length && setPieceClaim[index] != 0)
                variant[index] = TERRAIN_VARIANT_SET_PIECE;
            else if (route != null && (uint)index < (uint)route.Length && route[index] != 0)
                variant[index] = TERRAIN_VARIANT_ROUTE;
            else if (court != null && (uint)index < (uint)court.Length && court[index] != 0)
                variant[index] = TERRAIN_VARIANT_COURT;
            else if (landmark != null && (uint)index < (uint)landmark.Length && landmark[index] != 0)
                variant[index] = (byte)(
                    TERRAIN_VARIANT_LANDMARK_BASE +
                    ((landmark[index] - 1) % TERRAIN_VARIANT_LANDMARK_SLOTS));
        }
        enforceRequiredGroundPatches(
            surface,
            tiles,
            width,
            height,
            biomeKey,
            baseTx,
            baseTy,
            masks.usage);
        return new TerrainSurfaceLayers { surface = surface, variant = variant };
    }

    /// <summary>
    /// Grade a binary road stencil into a worn band with a compacted core and a soft verge.
    ///
    /// The streamed composer produces its band while it carves, one stamped disk at a time, because it knows the
    /// route's centre line and its radius. A producer that only has a finished stencil — the map simulator's
    /// `road` raster is exactly that — cannot stamp, so it measures instead: the distance from each road cell to
    /// the nearest non-road cell **is** the core-to-verge coordinate, and running the same ramp over it yields the
    /// same band. One rule, reached two ways, rather than two rules.
    ///
    /// `verge` is how far the wear reaches beyond the stencil, in tiles.
    /// </summary>
    public static byte[] gradeRouteUsage(
        byte[] road,
        int width,
        int height,
        double verge = 2)
    {
        var usage = new byte[road.Length];
        // Chebyshev distance transform outward from the road, bounded by the verge — two sweeps, no queue.
        const int LARGE = 0x3fff;
        var distance = new int[road.Length].fill(LARGE);
        for (int i = 0; i < road.Length; i++) if (road[i] != 0) distance[i] = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int best = distance[i];
                if (x > 0) best = Math.min(best, distance[i - 1] + 1);
                if (y > 0) best = Math.min(best, distance[i - width] + 1);
                if (x > 0 && y > 0) best = Math.min(best, distance[i - width - 1] + 1);
                if (x < width - 1 && y > 0) best = Math.min(best, distance[i - width + 1] + 1);
                distance[i] = best;
            }
        for (int y = height - 1; y >= 0; y--)
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x;
                int best = distance[i];
                if (x < width - 1) best = Math.min(best, distance[i + 1] + 1);
                if (y < height - 1) best = Math.min(best, distance[i + width] + 1);
                if (x < width - 1 && y < height - 1) best = Math.min(best, distance[i + width + 1] + 1);
                if (x > 0 && y < height - 1) best = Math.min(best, distance[i + width - 1] + 1);
                distance[i] = best;
            }

        // Inward distance: how deep inside the stencil a road cell sits. Its core is the part more than one cell
        // from open ground, so a one-tile track is all verge and a six-tile route has a genuine compacted middle.
        var inward = new int[road.Length].fill(LARGE);
        for (int i = 0; i < road.Length; i++) if (road[i] == 0) inward[i] = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int best = inward[i];
                if (x > 0) best = Math.min(best, inward[i - 1] + 1);
                if (y > 0) best = Math.min(best, inward[i - width] + 1);
                if (x > 0 && y > 0) best = Math.min(best, inward[i - width - 1] + 1);
                if (x < width - 1 && y > 0) best = Math.min(best, inward[i - width + 1] + 1);
                inward[i] = best;
            }
        for (int y = height - 1; y >= 0; y--)
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x;
                int best = inward[i];
                if (x < width - 1) best = Math.min(best, inward[i + 1] + 1);
                if (y < height - 1) best = Math.min(best, inward[i + width] + 1);
                if (x < width - 1 && y < height - 1) best = Math.min(best, inward[i + width + 1] + 1);
                if (x > 0 && y < height - 1) best = Math.min(best, inward[i + width - 1] + 1);
                inward[i] = best;
            }

        for (int i = 0; i < road.Length; i++)
        {
            if (road[i] != 0)
            {
                // Inside the stencil: fully compacted from the second ring in, ramping up from the edge.
                int depth = Math.min(inward[i], 2);
                double t = Scalar.clamp01(depth / 2.0);
                usage[i] = Js.U8(Math.round(
                    PRIMARY_ROUTE_EDGE_USAGE + (255 - PRIMARY_ROUTE_EDGE_USAGE) * t * t * (3 - 2 * t)));
                continue;
            }
            int d = distance[i];
            if (d > verge) continue;
            // Outside: the verge fades from the edge value to nothing.
            double t2 = Scalar.clamp01(1 - (d - 1) / Math.max(1, verge));
            usage[i] = Js.U8(Math.round(PRIMARY_ROUTE_EDGE_USAGE * t2 * t2 * (3 - 2 * t2)));
        }
        return usage;
    }
}
