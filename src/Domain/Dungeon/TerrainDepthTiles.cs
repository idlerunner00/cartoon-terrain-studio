// Port of packages/shared/src/domain/dungeon/terrainDepthTiles.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public sealed class TerrainDepthTileProfile
{
    /// <summary>Likelihood that a one-cell-thick wall receives one projectile-width Cleft.</summary>
    public double clefts;
    /// <summary>Likelihood that a two-bank corridor receives one +4-clear suspension bridge deck.</summary>
    public double underpasses;
}

public sealed class TerrainDepthTileOptions
{
    /// <summary>
    /// Density override for callers that are not a streamed Endless chunk. Streamed terrain uses the biome's own
    /// profile (omit this) so independently generated seams stay byte-identical. The topology rules are never
    /// part of this — only how often they are reached for.
    /// </summary>
    public TerrainDepthTileProfile? profile;
    /// <summary>
    /// Combined Cleft + Underpass share of the grid this sweep may consume. Unbounded when omitted, which is
    /// what streamed Endless generation needs; see <see cref="TerrainDepthTiles.TERRAIN_DEPTH_TILE_BUDGET_SHARE"/>.
    /// </summary>
    public double? maxFeatureShare;
    /// <summary>
    /// Ground a BUILT set piece owns, as a 1/0 mask — typically the wonder claim.
    ///
    /// A depth structure is a LANDFORM: a cleft splits rock, an underpass tunnels beneath a natural bank. Neither
    /// may be bored through a set piece: an underpass bank must guarantee UNDERPASS_MIN_BANK_CLEARANCE, and a
    /// structure that adopted stamped ground as its bank would be demoted by the late validation pass — taking the
    /// chunk with it into a rock-slab fallback.
    /// </summary>
    public byte[]? protect;
}

public sealed class TerrainDepthTileResult
{
    public int cleftCells;
    public int cleftFeatures;
    public int underpassCells;
    public int underpassFeatures;
}

public static class TerrainDepthTiles
{
    private const int CLEFT_BORDER_GUARD = 3;
    private const int CLEFT_ANCHOR_RADIUS = 5;
    private const int UNDERPASS_BORDER_GUARD = 3;
    private const int UNDERPASS_MIN_ANCHOR_RADIUS = 2;
    private const int UNDERPASS_MAX_ANCHOR_RADIUS = 5;
    private const int CLEFT_FIELD_SALT = 0x6e624eb7;
    private const int UNDERPASS_FIELD_SALT = 0x51d7348d;

    private static readonly TerrainDepthTileProfile NONE = new() { clefts = 0, underpasses = 0 };

    /// <summary>Only ever looked up by key, never iterated.</summary>
    private static readonly Dictionary<string, TerrainDepthTileProfile> TERRAIN_DEPTH_TILE_PROFILES = new()
    {
        ["highland_pass"] = new() { clefts = 0.78, underpasses = 1 },
        ["noir_sprawl"] = new() { clefts = 0.72, underpasses = 0.86 },
        ["olympian_sky_borough"] = new() { clefts = 0.64, underpasses = 0.96 },
        ["sakura_temple_dream"] = new() { clefts = 0.68, underpasses = 0.92 },
        ["abyssal_deepsea"] = new() { clefts = 0.7, underpasses = 0.64 },
        ["rainbowland"] = new() { clefts = 0.58, underpasses = 0.78 },
        ["clockwork_moon_bazaar"] = new() { clefts = 0.62, underpasses = 0.92 },
        ["sugarstorm_carnival"] = new() { clefts = 0.54, underpasses = 0.84 },
        ["prismglass_archive"] = new() { clefts = 0.82, underpasses = 0.94 },
        ["starforged_cathedral_endrun"] = new() { clefts = 0.86, underpasses = 1 },
        ["mountain"] = new() { clefts = 0.8, underpasses = 0.7 },
        ["verdant"] = new() { clefts = 0.72, underpasses = 0.78 },
        ["frogmire"] = new() { clefts = 0.74, underpasses = 0.66 },
        ["paradisebower"] = new() { clefts = 0.7, underpasses = 0.82 },
        ["raid_verdant"] = new() { clefts = 0, underpasses = 0 },
        ["raid_holdthefort"] = new() { clefts = 0.68, underpasses = 0.94 },
        ["raid_tidecage"] = new() { clefts = 0.66, underpasses = 0.72 },
        ["raid_moonroot"] = new() { clefts = 0.74, underpasses = 0.84 },
        ["raid_crownbower"] = new() { clefts = 0.72, underpasses = 0.92 },
    };

    public static TerrainDepthTileProfile terrainDepthTileProfileFor(string? biomeKey)
    {
        if (string.IsNullOrEmpty(biomeKey)) return NONE;
        return TERRAIN_DEPTH_TILE_PROFILES.TryGetValue(biomeKey, out var profile) && profile != null ? profile : NONE;
    }

    /// <summary>
    /// Combined Cleft + Underpass share of a grid a bounded caller may consume. Two percent is the budget the
    /// Endless audit measures these roles against; making it an explicit bound rather than an emergent property of
    /// the probability field is what lets a single sweep over a whole composed map stay inside it even when the
    /// composition offers an unusually dense field of eligible sites (a city grid of parallel corridors).
    ///
    /// Deliberately **not** the default: streamed Endless chunks must keep producing byte-identical terrain, and a
    /// bound applied there would silently re-cut existing seams. Bounded callers opt in via `maxFeatureShare`.
    /// </summary>
    public const double TERRAIN_DEPTH_TILE_BUDGET_SHARE = 0.02;
    /// <summary>Portion of the budget the deck role may claim before the finer Cleft gets the remainder.</summary>
    private const double UNDERPASS_BUDGET_SHARE = 0.8;

    /// <summary>
    /// Cells no depth structure may anchor on, dilated so a structure cannot reach INTO protected ground either.
    ///
    /// The radius covers the furthest cell a candidate can write or read from its anchor: half of the widest
    /// passage plus its bank and its two approaches.
    /// </summary>
    private const int DEPTH_PROTECT_CLEARANCE = 4;

    private static byte[]? dilateProtectMask(
        byte[] protect,
        int width,
        int height)
    {
        bool any = false;
        for (int index = 0; index < protect.Length; index++)
            if (protect[index] != 0)
            {
                any = true;
                break;
            }
        if (!any) return null;
        var blocked = new byte[width * height];
        for (int ty = 0; ty < height; ty++)
            for (int tx = 0; tx < width; tx++)
            {
                int protectIndex = ty * width + tx;
                // Past the end of a short mask JS reads `undefined`, and `undefined === 0` is false: the cell dilates.
                if ((uint)protectIndex < (uint)protect.Length && protect[protectIndex] == 0) continue;
                for (int dy = -DEPTH_PROTECT_CLEARANCE; dy <= DEPTH_PROTECT_CLEARANCE; dy++)
                    for (int dx = -DEPTH_PROTECT_CLEARANCE; dx <= DEPTH_PROTECT_CLEARANCE; dx++)
                    {
                        int x = tx + dx;
                        int y = ty + dy;
                        if (x < 0 || y < 0 || x >= width || y >= height) continue;
                        blocked[y * width + x] = 1;
                    }
            }
        return blocked;
    }

    private static bool inBounds(int width, int height, int tx, int ty) => tx >= 0 && ty >= 0 && tx < width && ty < height;

    private static bool isPlainFloorAt(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int tx,
        int ty,
        int level)
    {
        if (!inBounds(width, height, tx, ty)) return false;
        int index = ty * width + tx;
        return tiles[index] == TileType.Floor && elevation[index] == level;
    }

    private sealed class UnderpassCandidate
    {
        /// <summary>'horizontal' | 'vertical'</summary>
        public string passageAxis = "";
        public List<int> indices = null!;
        public int anchorX;
        public int anchorY;
    }

    private static double guaranteedWallClearance(
        sbyte[] elevation,
        int supportIndex,
        double floor) =>
        elevation[supportIndex] + TerrainModel.TERRAIN_STANDARD_WALL_BASE_RISE - floor;

    private static UnderpassCandidate? underpassCandidateAt(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int tx,
        int ty,
        string passageAxis)
    {
        int anchorIndex = ty * width + tx;
        if (tiles[anchorIndex] != TileType.Floor) return null;
        int level = elevation[anchorIndex];
        int crossX = passageAxis == "horizontal" ? 0 : 1;
        int crossY = passageAxis == "horizontal" ? 1 : 0;
        int alongX = passageAxis == "horizontal" ? 1 : 0;
        int alongY = passageAxis == "horizontal" ? 0 : 1;
        int negativeSupport = -1;
        int positiveSupport = -1;
        for (int distance = 1; distance <= TerrainModel.UNDERPASS_MAX_SPAN; distance++)
        {
            int x = tx - crossX * distance;
            int y = ty - crossY * distance;
            if (!inBounds(width, height, x, y)) return null;
            int index = y * width + x;
            if (tiles[index] == TileType.Solid)
            {
                if (
                    !TerrainModel.terrainUnderpassBankIsAnchored(
                        tiles,
                        width,
                        height,
                        x,
                        y,
                        -crossX,
                        -crossY,
                        alongX,
                        alongY)
                )
                    return null;
                if (guaranteedWallClearance(elevation, index, level) < TerrainModel.UNDERPASS_MIN_BANK_CLEARANCE)
                    return null;
                negativeSupport = distance;
                break;
            }
            if (tiles[index] != TileType.Floor || elevation[index] != level) return null;
        }
        for (int distance = 1; distance <= TerrainModel.UNDERPASS_MAX_SPAN; distance++)
        {
            int x = tx + crossX * distance;
            int y = ty + crossY * distance;
            if (!inBounds(width, height, x, y)) return null;
            int index = y * width + x;
            if (tiles[index] == TileType.Solid)
            {
                if (
                    !TerrainModel.terrainUnderpassBankIsAnchored(tiles, width, height, x, y, crossX, crossY, alongX, alongY)
                )
                    return null;
                if (guaranteedWallClearance(elevation, index, level) < TerrainModel.UNDERPASS_MIN_BANK_CLEARANCE)
                    return null;
                positiveSupport = distance;
                break;
            }
            if (tiles[index] != TileType.Floor || elevation[index] != level) return null;
        }
        if (negativeSupport < 1 || positiveSupport < 1) return null;
        int span = negativeSupport + positiveSupport - 1;
        if (span < 2 || span > TerrainModel.UNDERPASS_MAX_SPAN) return null;
        if (Math.floor((positiveSupport - negativeSupport) * 0.5) != 0) return null;
        var indices = new List<int>();
        for (int offset = -negativeSupport + 1; offset <= positiveSupport - 1; offset++)
        {
            int x = tx + crossX * offset;
            int y = ty + crossY * offset;
            int index = y * width + x;
            if (tiles[index] != TileType.Floor || elevation[index] != level) return null;
            // `for (const approach of [-1, 1] as const)`
            for (int approach = -1; approach <= 1; approach += 2)
            {
                if (
                    !isPlainFloorAt(
                        tiles,
                        elevation,
                        width,
                        height,
                        x + alongX * approach,
                        y + alongY * approach,
                        level)
                )
                    return null;
            }
            indices.push(index);
        }
        return new UnderpassCandidate { passageAxis = passageAxis, indices = indices, anchorX = tx, anchorY = ty };
    }

    private static double underpassRank(
        double seed,
        int gtx,
        int gty,
        string axis)
    {
        // `(seed ^ UNDERPASS_FIELD_SALT ^ axisBit) >>> 0`; terrainHash reads its salt through `| 0`, so passing the
        // int bit pattern is identical whatever numeric type the callee declares.
        uint salt = (uint)(Js.ToInt32(seed) ^ UNDERPASS_FIELD_SALT ^ (axis == "horizontal" ? 0 : 1));
        return TerrainRules.terrainHash(
            gtx,
            gty,
            unchecked((int)salt));
    }

    private static (int cells, int features) applyUnderpasses(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int baseGtx,
        int baseGty,
        double seed,
        double coverage,
        double cellBudget,
        byte[]? blocked)
    {
        if (coverage <= 0 || cellBudget <= 0) return (0, 0);
        var candidates = new List<UnderpassCandidate>();
        for (int ty = UNDERPASS_BORDER_GUARD; ty < height - UNDERPASS_BORDER_GUARD; ty++)
        {
            for (int tx = UNDERPASS_BORDER_GUARD; tx < width - UNDERPASS_BORDER_GUARD; tx++)
            {
                if (blocked != null && blocked[ty * width + tx] != 0) continue;
                // `for (const axis of ['horizontal', 'vertical'] as const)`
                for (int axisIndex = 0; axisIndex < 2; axisIndex++)
                {
                    string axis = axisIndex == 0 ? "horizontal" : "vertical";
                    UnderpassCandidate? candidate = underpassCandidateAt(tiles, elevation, width, height, tx, ty, axis);
                    // The anchor may be closer to an edge when an asymmetric span extends inward. Guard the cells that are
                    // actually rewritten instead of over-constraining the anchor; this preserves small authored layouts
                    // while keeping every Endless seam byte and its two-cell render halo untouched.
                    if (
                        candidate != null &&
                        candidate.indices.every((index) =>
                        {
                            int x = index % width;
                            int y = (int)Math.floor((double)index / width);
                            return Math.min(x, y, width - 1 - x, height - 1 - y) >= UNDERPASS_BORDER_GUARD;
                        })
                    )
                        candidates.push(candidate);
                }
            }
        }
        candidates.sort(
            (a, b) =>
                underpassRank(seed, baseGtx + b.anchorX, baseGty + b.anchorY, b.passageAxis) -
                underpassRank(seed, baseGtx + a.anchorX, baseGty + a.anchorY, a.passageAxis));
        int cells = 0;
        int features = 0;
        // Math.round of a value in [2, 5] (coverage is clamped to [0, 1] first).
        int anchorRadius = (int)Math.round(
            UNDERPASS_MAX_ANCHOR_RADIUS -
            Math.max(0, Math.min(1, coverage)) *
            (UNDERPASS_MAX_ANCHOR_RADIUS - UNDERPASS_MIN_ANCHOR_RADIUS));
        foreach (UnderpassCandidate candidate in candidates)
        {
            UnderpassCandidate? live = underpassCandidateAt(
                tiles,
                elevation,
                width,
                height,
                candidate.anchorX,
                candidate.anchorY,
                candidate.passageAxis);
            if (
                live == null ||
                live.indices.Count != candidate.indices.Count ||
                live.indices.some((index, offset) => index != candidate.indices[offset])
            )
                continue;
            int gtx = baseGtx + candidate.anchorX;
            int gty = baseGty + candidate.anchorY;
            double rank = underpassRank(seed, gtx, gty, candidate.passageAxis);
            if (rank < 0.78 - coverage * 0.24) continue;
            int alongX = candidate.passageAxis == "horizontal" ? 1 : 0;
            int alongY = candidate.passageAxis == "horizontal" ? 0 : 1;
            bool localMaximum = true;
            for (int offset = -anchorRadius; offset <= anchorRadius; offset++)
            {
                if (
                    offset != 0 &&
                    underpassRank(seed, gtx + alongX * offset, gty + alongY * offset, candidate.passageAxis) >
                    rank
                )
                {
                    localMaximum = false;
                    break;
                }
            }
            if (!localMaximum) continue;
            // Candidates are rank-sorted, so spending a bounded budget strongest-first stays deterministic.
            if (cells + candidate.indices.Count > cellBudget) continue;
            foreach (int index in candidate.indices)
            {
                tiles[index] = TileType.Underpass;
                cells++;
            }
            features++;
        }
        return (cells, features);
    }

    private sealed class CleftCandidate
    {
        /// <summary>'horizontal' | 'vertical'</summary>
        public string passageAxis = "";
        public int index;
        public int tx;
        public int ty;
    }

    private static CleftCandidate? cleftCandidateAt(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int tx,
        int ty)
    {
        int index = ty * width + tx;
        if (tiles[index] != TileType.Solid) return null;
        string? match = null;
        // `for (const passageAxis of ['horizontal', 'vertical'] as const)`
        for (int axisIndex = 0; axisIndex < 2; axisIndex++)
        {
            string passageAxis = axisIndex == 0 ? "horizontal" : "vertical";
            int alongX = passageAxis == "horizontal" ? 1 : 0;
            int alongY = passageAxis == "horizontal" ? 0 : 1;
            int tangentX = passageAxis == "horizontal" ? 0 : 1;
            int tangentY = passageAxis == "horizontal" ? 1 : 0;
            int negativeApproach = (ty - alongY) * width + tx - alongX;
            int positiveApproach = (ty + alongY) * width + tx + alongX;
            int negativeShoulder = (ty - tangentY) * width + tx - tangentX;
            int positiveShoulder = (ty + tangentY) * width + tx + tangentX;
            if (
                !inBounds(width, height, tx - alongX, ty - alongY) ||
                !inBounds(width, height, tx + alongX, ty + alongY) ||
                !inBounds(width, height, tx - tangentX, ty - tangentY) ||
                !inBounds(width, height, tx + tangentX, ty + tangentY) ||
                tiles[negativeApproach] != TileType.Floor ||
                tiles[positiveApproach] != TileType.Floor ||
                tiles[negativeShoulder] != TileType.Solid ||
                tiles[positiveShoulder] != TileType.Solid ||
                elevation[negativeApproach] != elevation[positiveApproach]
            )
                continue;
            int ground = elevation[negativeApproach];
            if (
                elevation[index] + TerrainModel.TERRAIN_STANDARD_WALL_BASE_RISE - ground < TerrainModel.CLEFT_MIN_WALL_CLEARANCE ||
                elevation[negativeShoulder] + TerrainModel.TERRAIN_STANDARD_WALL_BASE_RISE - ground <
                TerrainModel.CLEFT_MIN_WALL_CLEARANCE ||
                elevation[positiveShoulder] + TerrainModel.TERRAIN_STANDARD_WALL_BASE_RISE - ground <
                TerrainModel.CLEFT_MIN_WALL_CLEARANCE
            )
                continue;
            if (match != null) return null;
            match = passageAxis;
        }
        return match != null ? new CleftCandidate { passageAxis = match, index = index, tx = tx, ty = ty } : null;
    }

    private static double cleftRank(double seed, int gtx, int gty, string axis)
    {
        // `(seed ^ CLEFT_FIELD_SALT ^ axisBit) >>> 0`, read by terrainHash through `| 0` (see underpassRank).
        uint salt = (uint)(Js.ToInt32(seed) ^ CLEFT_FIELD_SALT ^ (axis == "horizontal" ? 0 : 1));
        return TerrainRules.terrainHash(gtx, gty, unchecked((int)salt));
    }

    private static (int cells, int features) applyClefts(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int baseGtx,
        int baseGty,
        double seed,
        double coverage,
        double cellBudget,
        byte[]? blocked)
    {
        if (coverage <= 0 || cellBudget <= 0) return (0, 0);
        var candidates = new List<CleftCandidate>();
        for (int ty = CLEFT_BORDER_GUARD; ty < height - CLEFT_BORDER_GUARD; ty++)
        {
            for (int tx = CLEFT_BORDER_GUARD; tx < width - CLEFT_BORDER_GUARD; tx++)
            {
                if (blocked != null && blocked[ty * width + tx] != 0) continue;
                CleftCandidate? candidate = cleftCandidateAt(tiles, elevation, width, height, tx, ty);
                if (candidate != null) candidates.push(candidate);
            }
        }
        candidates.sort(
            (a, b) =>
                cleftRank(seed, baseGtx + b.tx, baseGty + b.ty, b.passageAxis) -
                cleftRank(seed, baseGtx + a.tx, baseGty + a.ty, a.passageAxis));
        int cells = 0;
        int features = 0;
        foreach (CleftCandidate candidate in candidates)
        {
            CleftCandidate? live = cleftCandidateAt(tiles, elevation, width, height, candidate.tx, candidate.ty);
            if (live == null || live.passageAxis != candidate.passageAxis) continue;
            int gtx = baseGtx + candidate.tx;
            int gty = baseGty + candidate.ty;
            double rank = cleftRank(seed, gtx, gty, candidate.passageAxis);
            if (rank < 0.92 - coverage * 0.1) continue;
            // Candidates are already sorted strongest-first. Compare spacing only against accepted Clefts, not against
            // arbitrary hash samples on cells that could never carry the feature. The old all-cell local-maximum test
            // regularly rejected every one of 30+ structurally valid candidates in a region after wall terracing.
            bool nearCleft = false;
            for (int dy = -CLEFT_ANCHOR_RADIUS; dy <= CLEFT_ANCHOR_RADIUS && !nearCleft; dy++)
            {
                for (int dx = -CLEFT_ANCHOR_RADIUS; dx <= CLEFT_ANCHOR_RADIUS; dx++)
                {
                    int nx = candidate.tx + dx;
                    int ny = candidate.ty + dy;
                    if (
                        inBounds(width, height, nx, ny) &&
                        Math.abs(dx) + Math.abs(dy) <= CLEFT_ANCHOR_RADIUS &&
                        tiles[ny * width + nx] == TileType.Cleft
                    )
                    {
                        nearCleft = true;
                        break;
                    }
                }
            }
            if (nearCleft) continue;
            bool nearUnderpass = false;
            for (int dy = -2; dy <= 2 && !nearUnderpass; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    // In range: candidates sit at least CLEFT_BORDER_GUARD (3) cells inside the grid.
                    if (tiles[(candidate.ty + dy) * width + candidate.tx + dx] == TileType.Underpass)
                    {
                        nearUnderpass = true;
                        break;
                    }
                }
            }
            if (nearUnderpass) continue;
            if (cells + 1 > cellBudget) continue;
            tiles[candidate.index] = TileType.Cleft;
            cells++;
            features++;
        }
        return (cells, features);
    }

    /// <summary>
    /// Underpass replaces Floor and remains walkable; Cleft replaces Solid and remains body-blocking. Only the
    /// intended vertical/sight layer changes. Guard rings keep independently generated seams byte-identical.
    /// </summary>
    public static TerrainDepthTileResult applyTerrainDepthTiles(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int baseGtx,
        int baseGty,
        double seed,
        string? biomeKey,
        TerrainDepthTileOptions? options = null)
    {
        options ??= new TerrainDepthTileOptions();
        if (
            width <= 0 ||
            height <= 0 ||
            tiles.Length < width * height ||
            elevation.Length < width * height
        )
            return new TerrainDepthTileResult { cleftCells = 0, cleftFeatures = 0, underpassCells = 0, underpassFeatures = 0 };
        TerrainDepthTileProfile profile = options.profile ?? terrainDepthTileProfileFor(biomeKey);
        double budget =
            options.maxFeatureShare == null
                ? double.PositiveInfinity
                : Math.floor((double)width * height * Math.max(0, options.maxFeatureShare.Value));
        byte[]? blocked = options.protect != null ? dilateProtectMask(options.protect, width, height) : null;
        // Bridges are the loud role and eat cells in span-sized bites, so they get the bulk of the budget while a
        // reserved slice keeps the finer Cleft from being starved out on a bridge-heavy composition.
        var underpasses = applyUnderpasses(
            tiles,
            elevation,
            width,
            height,
            baseGtx,
            baseGty,
            seed,
            profile.underpasses,
            Math.floor(budget * UNDERPASS_BUDGET_SHARE),
            blocked);
        var clefts = applyClefts(
            tiles,
            elevation,
            width,
            height,
            baseGtx,
            baseGty,
            seed,
            profile.clefts,
            budget - underpasses.cells,
            blocked);
        return new TerrainDepthTileResult
        {
            cleftCells = clefts.cells,
            cleftFeatures = clefts.features,
            underpassCells = underpasses.cells,
            underpassFeatures = underpasses.features,
        };
    }
}
