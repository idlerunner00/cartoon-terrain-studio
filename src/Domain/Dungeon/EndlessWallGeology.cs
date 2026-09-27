// Port of packages/shared/src/domain/dungeon/endlessWallGeology.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.EndlessCoordinates;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * **Wall geology** — what the ROCK of an endless Country looks like, and how it meets the ground.
 *
 * Everything here edits the elevation of `Solid` cells only. It is separated from the Country composer for the
 * ordinary reason: the composer decides where rock IS, this decides what that rock reads as, and the two
 * change for different reasons. The four passes run in order and each depends on the one before it:
 *
 *  1. assignCountryWallHeights seats every wall on its own six-terrace ladder,
 *  2. enforceExposedCountryWallHeight guarantees the face a wall shows to the floor beside it,
 *  3. seedWorldAlignedWallGeology breaks a mass into world-aligned shelves,
 *  4. enforceLargeWallDetailCoverage repairs any window that came out as one undifferentiated slab.
 *
 * Imports renamed in the original (`ENDLESS_COUNTRY_SALT as SALT`, `countryClamp as clamp`,
 * `countryClamp01 as clamp01`, `countryHash as hash`) are called by their original, qualified names.
 */

public static partial class EndlessWallGeology
{
    /// <summary>
    /// How far above its own ground sample a wall is seated, in levels. Two is one full wall-ladder step: enough
    /// that rock always reads as rock from the floor beside it, small enough that the thirteen-level ladder still
    /// has room for the six authored wall terraces above it.
    /// </summary>
    private const int COUNTRY_WALL_GROUND_SEAT = 2;

    private static readonly (int, int)[] NEIGHBOURS4 =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };

    public static void assignCountryWallHeights(
        byte[] tiles,
        sbyte[] elevation,
        double seed,
        string biomeKey,
        int baseTx,
        int baseTy)
    {
        int width = ENDLESS_CHUNK_TILES;
        double ridgeAngle =
            EndlessCountryField.countryHash(seed, Js.ToInt32(ENDLESS_COUNTRY_SALT.wall) ^ unchecked((int)0x9e3779b9), 0, 0) * Math.PI;
        double ridgeCos = Math.cos(ridgeAngle);
        double ridgeSin = Math.sin(ridgeAngle);
        double ridgePhase =
            EndlessCountryField.countryHash(seed, Js.ToInt32(ENDLESS_COUNTRY_SALT.wall) ^ unchecked((int)0x85ebca6b), 0, 0) * Math.PI * 2;
        for (int ty = 0; ty < width; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int index = ty * width + tx;
                if (tiles[index] != TileType.Solid) continue;
                int worldX = baseTx + tx;
                int worldY = baseTy + ty;
                double ground = Math.max(
                    MIN_ELEVATION,
                    Math.floor((double)EndlessCountryField.countryElevationLevelAt(seed, biomeKey, worldX, worldY) / 2) * 2);
                double massif = valueNoise(Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.wall)), worldX, worldY, 46);
                double terrace = valueNoise(
                    Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.wallDetail)),
                    worldX + 23,
                    worldY - 31,
                    8.5);
                double ridge =
                    0.5 +
                    Math.sin(
                        (worldX * ridgeCos + worldY * ridgeSin) / 8.5 + ridgePhase + (massif - 0.5) * 2.2) *
                    0.5;
                double wallField = EndlessCountryField.countryClamp01(massif * 0.34 + terrace * 0.5 + ridge * 0.16);
                // Six two-level wall-only terraces replace the former four 20-tile bands. Their 8.5/46-tile blend
                // produces real but composed mesas, steps and crowns inside a wall mass; walkable terrain never samples
                // this field and therefore keeps its calmer combat-readable height profile.
                double wallTerrace = Math.min(5, Math.floor(wallField * 6));
                double regionalOffset = ground >= 8 ? 2 : 0;
                // The wall ladder is deliberately independent from the walkable ladder. This prevents low/high Country
                // extremes from clamping several wall terraces into one enormous level-0 or level-12 roof.
                //
                // Independent is not the same as UNRELATED, though: a cliff still stands on the land it rises out of. A
                // purely independent ladder let a wall land level with — or below — the ground beside it, and once the
                // walkable relief was banded into real shelves that stopped being rare: the measured mean face on run 6
                // fell to 0.74 levels, which renders as a grey slab flush with the floor rather than as rock. The seat
                // below is taken from the wall's OWN continuous ground sample, so it is smooth across neighbouring wall
                // cells and cannot introduce the one-level cap dither the vertical-profile contract bounds — a
                // neighbour-scan lift, measured, did exactly that.
                elevation[index] = Js.I8(EndlessCountryField.countryClamp(
                    ground + COUNTRY_WALL_GROUND_SEAT + wallTerrace * 2 + regionalOffset,
                    MIN_ELEVATION,
                    MAX_ELEVATION));
            }
        }
    }

    /*
     * A high stored walkable shelf used to be able to meet a low wall terrace and visually consume most of the
     * Endless shell. Raise only exposed Solid caps to their highest adjacent walkable datum. The 6.4-level Endless
     * shell then remains fully visible at the gameplay edge while interior wall terraces retain their own much
     * broader height ladder.
     */

    /// <summary>
    /// The smallest height, in levels, a wall may stand above the walkable ground it touches.
    ///
    /// The owner's authored target for this face is five levels; the thirteen-level ladder cannot fund five while
    /// also funding varied wall crowns and ground that climbs to real highlands, and that gap is a known open item.
    /// One level is what the thirteen-step ladder can guarantee everywhere without compressing the wall crowns
    /// into a single dominant band, and it is the difference between a cliff and a floor tile of a different colour.
    ///
    /// The lift lands ON the wall's own even ladder rather than at the exact deficit. An exact lift leaves a raised
    /// wall one level off the unraised wall beside it, and a one-level difference between two neighbouring caps is
    /// the "wall stripe" fizz the vertical-profile contract bounds at 8 %: rock may be flat or step, never dither.
    /// </summary>
    private const int COUNTRY_WALL_MIN_FACE = 0;

    public static void enforceExposedCountryWallHeight(
        byte[] tiles,
        sbyte[] elevation,
        double seed,
        string biomeKey,
        int baseTx,
        int baseTy)
    {
        int width = ENDLESS_CHUNK_TILES;
        for (int ty = 0; ty < width; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int index = ty * width + tx;
                if (tiles[index] != TileType.Solid) continue;
                double adjacentWalkable = double.NegativeInfinity;
                foreach (var (dx, dy) in NEIGHBOURS4)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width)
                    {
                        // The neighbour chunk is intentionally unavailable here. Its walkable datum comes from the same
                        // continuous global level function, so using that value prevents a low local cap from turning a
                        // streamed seam into a two-level wall.
                        adjacentWalkable = Math.max(
                            adjacentWalkable,
                            EndlessCountryField.countryElevationLevelAt(seed, biomeKey, baseTx + nx, baseTy + ny));
                        continue;
                    }
                    int neighbour = ny * width + nx;
                    if (!isWalkable(tiles[neighbour])) continue;
                    adjacentWalkable = Math.max(adjacentWalkable, elevation[neighbour]);
                }
                // A wall must READ as a wall from the floor beside it. Lifting it only to `ceil(ground / 2) * 2` left a
                // face of zero or one level wherever the ground happened to sit on an even step — and once the walkable
                // relief was banded into real shelves, "wherever" became most of the world: the measured mean face fell
                // to 0.74 levels on run 6, which is a grey slab flush with the floor, not a cliff. The minimum face is
                // stated in levels and then snapped UP onto the wall ladder, so the rock keeps its own two-level rhythm.
                if (Number.isFinite(adjacentWalkable))
                    elevation[index] = Js.I8(Math.max(
                        elevation[index],
                        Math.min(MAX_ELEVATION, Math.ceil((adjacentWalkable + COUNTRY_WALL_MIN_FACE) / 2) * 2)));
            }
        }
    }

    /// <summary>
    /// A wall mass is large enough to need authored-looking internal composition once at least eighty cells of a
    /// 10x10 view are non-traversable structure. Water and chasm are details by definition; Solid caps count as
    /// detailed when their final rendered summit leaves the locally dominant terrace. Half the footprint is a hard
    /// quality floor, not an average: a single blank roof plate must fail even when the surrounding Country is rich.
    /// Keeping these values exported gives tests and the production audit one exact interpretation of "large wall".
    /// </summary>
    public const int ENDLESS_WALL_DETAIL_WINDOW = 10;
    public const int ENDLESS_WALL_DETAIL_MIN_STRUCTURE_CELLS = 80;
    /// <summary>Chunk-local headroom protecting the published target when a sliding window crosses a stream seam.</summary>
    private const double ENDLESS_WALL_DETAIL_REPAIR_SHARE = 0.72;

    private static double exposedCountryWallMinimumLevel(
        byte[] tiles,
        sbyte[] elevation,
        double seed,
        string biomeKey,
        int baseTx,
        int baseTy,
        int tx,
        int ty)
    {
        int width = ENDLESS_CHUNK_TILES;
        double adjacentWalkable = double.NegativeInfinity;
        foreach (var (dx, dy) in NEIGHBOURS4)
        {
            int nx = tx + dx;
            int ny = ty + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= width)
            {
                adjacentWalkable = Math.max(
                    adjacentWalkable,
                    EndlessCountryField.countryElevationLevelAt(seed, biomeKey, baseTx + nx, baseTy + ny));
                continue;
            }
            int neighbour = ny * width + nx;
            if (isWalkable(tiles[neighbour]))
                adjacentWalkable = Math.max(adjacentWalkable, elevation[neighbour]);
        }
        return !Number.isFinite(adjacentWalkable)
            ? MIN_ELEVATION
            : Math.min(MAX_ELEVATION, Math.ceil(adjacentWalkable / 2) * 2);
    }

    private sealed class WallDetailCandidate
    {
        public int index;
        public int tx;
        public int ty;
        public double minimumLevel;
        public double score;
    }

    private const int RENDERED_WALL_SURFACE_KEY_OFFSET = 2_048;
    private const int RENDERED_WALL_SURFACE_KEY_COUNT = 8_192;

    /// <summary>
    /// The raw (unwrapped) key. Low wall levels produce negative keys; stored into the Uint16 cache they wrap,
    /// and read against the 8192-slot histogram they fall outside it — both exactly as in the original.
    /// </summary>
    private static double renderedCountryWallSurfaceKey(int baseTx, int baseTy, int tx, int ty, int level)
    {
        return
            RENDERED_WALL_SURFACE_KEY_OFFSET +
            Math.round((level + TerrainRules.endlessWallRiseAt(baseTx + tx, baseTy + ty, level)) * 100);
    }

    /// <summary>
    /// Lay a seam-stable geological rhythm under the sliding-window repair. Three broad strata use disjoint height
    /// families in a 30/30/40 composition. No family can therefore dominate more than forty cells of a 10x10
    /// window, even if movement space removes the other twenty allowed cells. A slow cross-axis warp and broad
    /// level patches keep those strata geological rather than ruler-straight. Where a very high gameplay shelf
    /// leaves no valid member in one family, the exposed minimum wins and the overlapping-window repair
    /// redistributes the remaining cap instead of weakening its wall face.
    /// </summary>
    public static void seedWorldAlignedWallGeology(
        byte[] tiles,
        sbyte[] elevation,
        double seed,
        string biomeKey,
        int baseTx,
        int baseTy)
    {
        int width = ENDLESS_CHUNK_TILES;
        int[][] levelOffsets =
        {
            new[] { 2, 8, 14 },
            new[] { 4, 10 },
            new[] { 6, 12 },
        };
        for (int ty = 0; ty < width; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int index = ty * width + tx;
                if (tiles[index] != TileType.Solid) continue;
                int worldX = baseTx + tx;
                int worldY = baseTy + ty;
                int secondary = worldY;
                int family = (int)TerrainRules.endlessWallCrownBandAt(worldX, worldY);

                double minimumLevel = exposedCountryWallMinimumLevel(
                    tiles,
                    elevation,
                    seed,
                    biomeKey,
                    baseTx,
                    baseTy,
                    tx,
                    ty);
                double ground = EndlessCountryField.countryElevationLevelAt(seed, biomeKey, worldX, worldY);
                // `[...new Set(offsets.map(...).filter(...))]`: first occurrence wins, order kept.
                var availableSet = new JsSet<double>();
                foreach (int offset in levelOffsets[family])
                {
                    double level = EndlessCountryField.countryClamp(ground + offset, MIN_ELEVATION, MAX_ELEVATION);
                    if (level >= minimumLevel) availableSet.add(level);
                }
                List<double> availableLevels = availableSet.ToList();
                if (availableLevels.Count == 0)
                {
                    elevation[index] = MAX_ELEVATION;
                    continue;
                }
                double geologicalField = valueNoise(
                    Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.wallDetail)),
                    31,
                    secondary - 43,
                    17);
                // The strata field spans the historical range, NOT the lengthened ladder. Scaling it by the full range
                // made every wall aim at the middle of a much taller world and pushed the mean exposed face to ten
                // levels — a canyon, not a cliff. The extra rungs belong to the floor-relative minimum, which lifts a
                // wall exactly as far as the ground beside it demands; this pass only decides which stratum it lands on.
                double desiredLevel = EndlessCountryField.countryClamp(
                    ground + 2 + Math.round(geologicalField * 7) * 2,
                    minimumLevel,
                    MAX_ELEVATION);
                // `reduce` without an initial value: the first level seeds the accumulator.
                double best = availableLevels[0];
                for (int i = 1; i < availableLevels.Count; i++)
                {
                    double level = availableLevels[i];
                    best = Math.abs(level - desiredLevel) < Math.abs(best - desiredLevel) ? level : best;
                }
                elevation[index] = Js.I8(best);
            }
        }
    }

    /// <summary>
    /// Repair the exact failure visible in broad Endless wall roofs: every sufficiently wall-heavy 10x10 window
    /// must devote at least half of its structural footprint to hydrology, voids or a secondary geological level.
    ///
    /// The base wall field and mass hydrology still create the composition. This pass is a deterministic safety
    /// net for unlucky low-frequency plateaus. Candidates are ranked in world-aligned 3x3 clusters, so a repair
    /// produces ledges, bowls and stepped shelves rather than checkerboard height noise. Several overlapping
    /// passes close the sliding-window loophole while preserving chunk-order independence.
    /// </summary>
    public static void enforceLargeWallDetailCoverage(
        byte[] tiles,
        sbyte[] elevation,
        double seed,
        string biomeKey,
        int baseTx,
        int baseTy)
    {
        int width = ENDLESS_CHUNK_TILES;
        int window = ENDLESS_WALL_DETAIL_WINDOW;
        var repaired = new byte[tiles.Length];
        int levelCount = ELEVATION_LEVELS;
        bool forward = EndlessCountryField.countryHash(seed, Js.ToInt32(ENDLESS_COUNTRY_SALT.wallFeatureDetail), baseTx, baseTy) < 0.5;
        // The contract concerns the summit the player sees, not merely the stored base elevation. Cache that
        // rendered surface once: recalculating the mountain/crown fields in every overlapping 10x10 window made
        // this safety pass needlessly expensive during chunk generation.
        var renderedSurfaceKeys = new ushort[tiles.Length];
        for (int ty = 0; ty < width; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int index = ty * width + tx;
                if (tiles[index] != TileType.Solid) continue;
                renderedSurfaceKeys[index] = Js.U16(renderedCountryWallSurfaceKey(
                    baseTx,
                    baseTy,
                    tx,
                    ty,
                    elevation[index]));
            }
        }
        var histogram = new byte[RENDERED_WALL_SURFACE_KEY_COUNT];
        // `histogram[key]` for a key outside the Uint8Array is `undefined` in JS: it is never `=== 0`, arithmetic on
        // it is NaN, and a store to it is dropped.
        double histogramAt(double key) => key >= 0 && key < histogram.Length ? histogram[(int)key] : double.NaN;
        // A window can initially touch at most 100 keys and every repair adds at most one. Reuse one sparse reset
        // list rather than allocating/clearing an 8K histogram for every sliding window.
        var touchedSurfaceKeys = new ushort[window * window * 2];
        int touchedSurfaceKeyCount = 0;

        for (int pass = 0; pass < 5; pass++)
        {
            int repairs = 0;
            for (int oyStep = 0; oyStep <= width - window; oyStep++)
            {
                int oy = forward != (pass % 2 == 1) ? oyStep : width - window - oyStep;
                for (int oxStep = 0; oxStep <= width - window; oxStep++)
                {
                    int ox = forward != (pass % 2 == 1) ? oxStep : width - window - oxStep;
                    for (int keyIndex = 0; keyIndex < touchedSurfaceKeyCount; keyIndex++)
                    {
                        histogram[touchedSurfaceKeys[keyIndex]] = 0;
                    }
                    touchedSurfaceKeyCount = 0;
                    int structureCells = 0;
                    for (int dy = 0; dy < window; dy++)
                    {
                        for (int dx = 0; dx < window; dx++)
                        {
                            int index = (oy + dy) * width + ox + dx;
                            int tile = tiles[index];
                            if (tile == TileType.Water || tile == TileType.Chasm)
                            {
                                structureCells++;
                            }
                            else if (tile == TileType.Solid)
                            {
                                structureCells++;
                                int surfaceKey = renderedSurfaceKeys[index];
                                if (surfaceKey < histogram.Length)
                                {
                                    if (histogram[surfaceKey] == 0)
                                        touchedSurfaceKeys[touchedSurfaceKeyCount++] = (ushort)surfaceKey;
                                    histogram[surfaceKey] = Js.U8(histogram[surfaceKey] + 1);
                                }
                            }
                        }
                    }
                    if (structureCells < ENDLESS_WALL_DETAIL_MIN_STRUCTURE_CELLS) continue;

                    int dominantSurfaceKey = 0;
                    int dominantCells = 0;
                    for (int keyIndex = 0; keyIndex < touchedSurfaceKeyCount; keyIndex++)
                    {
                        int surfaceKey = touchedSurfaceKeys[keyIndex];
                        int cells = histogram[surfaceKey];
                        if (cells > dominantCells)
                        {
                            dominantSurfaceKey = surfaceKey;
                            dominantCells = cells;
                        }
                    }
                    int detailCells = structureCells - dominantCells;
                    double requiredDetails = Math.ceil(structureCells * ENDLESS_WALL_DETAIL_REPAIR_SHARE);
                    double needed = requiredDetails - detailCells;
                    if (needed <= 0) continue;

                    var candidates = new List<WallDetailCandidate>();
                    for (int dy = 0; dy < window; dy++)
                    {
                        for (int dx = 0; dx < window; dx++)
                        {
                            int tx = ox + dx;
                            int ty = oy + dy;
                            int index = ty * width + tx;
                            if (
                                tiles[index] != TileType.Solid ||
                                renderedSurfaceKeys[index] != dominantSurfaceKey ||
                                repaired[index] != 0)
                                continue;
                            double minimumLevel = exposedCountryWallMinimumLevel(
                                tiles,
                                elevation,
                                seed,
                                biomeKey,
                                baseTx,
                                baseTy,
                                tx,
                                ty);
                            if (minimumLevel >= MAX_ELEVATION && elevation[index] == MAX_ELEVATION) continue;
                            int worldX = baseTx + tx;
                            int worldY = baseTy + ty;
                            double clusterX = Math.floor((double)worldX / 3);
                            double clusterY = Math.floor((double)worldY / 3);
                            double clusterRank = EndlessCountryField.countryHash(seed, Js.ToInt32(ENDLESS_COUNTRY_SALT.wallFeatureDetail) ^ pass, clusterX, clusterY);
                            double cellRank = EndlessCountryField.countryHash(seed, Js.ToInt32(ENDLESS_COUNTRY_SALT.wallDetail) ^ pass, worldX, worldY);
                            // Coherent 3x3 geological clusters win first. The tiny cell rank only decides how a partial
                            // cluster ends and cannot turn the result into per-tile static.
                            double score = clusterRank + cellRank * 0.035;
                            candidates.push(new WallDetailCandidate { index = index, tx = tx, ty = ty, minimumLevel = minimumLevel, score = score });
                        }
                    }
                    candidates.sort((left, right) => left.score - right.score);

                    foreach (WallDetailCandidate candidate in candidates)
                    {
                        if (needed <= 0) break;
                        int worldX = baseTx + candidate.tx;
                        int worldY = baseTy + candidate.ty;
                        double geologicalField = valueNoise(
                            Js.ToUint32(Js.ToInt32(seed) ^ Js.ToInt32(ENDLESS_COUNTRY_SALT.wallFeatureDetail)),
                            worldX - 17,
                            worldY + 29,
                            6.5);
                        double desiredLevel = Math.min(
                            MAX_ELEVATION,
                            MIN_ELEVATION + Math.floor(geologicalField * levelCount));
                        var alternatives = new List<int>();
                        // Four-level crown families keep the repair vocabulary bounded across the 51-level signed world.
                        // The walkable ground owns the fine one-level gradient; rock roofs need broad strata, not every
                        // possible integer as a candidate in every overlapping 10x10 window.
                        // (minimumLevel is always an integer: MIN_ELEVATION or `min(MAX, ceil(x / 2) * 2)`.)
                        for (int level = (int)candidate.minimumLevel; level <= MAX_ELEVATION; level += 4)
                        {
                            if (
                                renderedCountryWallSurfaceKey(baseTx, baseTy, candidate.tx, candidate.ty, level) !=
                                dominantSurfaceKey)
                                alternatives.push(level);
                        }
                        if (alternatives.Count == 0) continue;
                        // A level whose raw key falls outside the histogram compares as NaN (JS: undefined - n), i.e.
                        // "equal" to everything. Keys rise strictly with level, so such levels form a prefix/suffix of
                        // this ascending list, and every stable sort (V8's TimSort included) keeps that prefix first.
                        alternatives.sort((left, right) =>
                        {
                            double leftSurfaceKey = renderedCountryWallSurfaceKey(
                                baseTx,
                                baseTy,
                                candidate.tx,
                                candidate.ty,
                                left);
                            double rightSurfaceKey = renderedCountryWallSurfaceKey(
                                baseTx,
                                baseTy,
                                candidate.tx,
                                candidate.ty,
                                right);
                            double population = histogramAt(leftSurfaceKey) - histogramAt(rightSurfaceKey);
                            if (population != 0) return population;
                            double distance = Math.abs(left - desiredLevel) - Math.abs(right - desiredLevel);
                            if (distance != 0) return distance;
                            bool preferHigh = EndlessCountryField.countryHash(seed, Js.ToInt32(ENDLESS_COUNTRY_SALT.wallFeature), worldX, worldY) < 0.5;
                            return preferHigh ? right - left : left - right;
                        });
                        int targetLevel = alternatives[0];
                        int previousSurfaceKey = renderedSurfaceKeys[candidate.index];
                        double targetSurfaceKey = renderedCountryWallSurfaceKey(
                            baseTx,
                            baseTy,
                            candidate.tx,
                            candidate.ty,
                            targetLevel);
                        elevation[candidate.index] = (sbyte)targetLevel;
                        renderedSurfaceKeys[candidate.index] = Js.U16(targetSurfaceKey);
                        if (previousSurfaceKey < histogram.Length)
                            histogram[previousSurfaceKey] = Js.U8(histogram[previousSurfaceKey] - 1);
                        if (histogramAt(targetSurfaceKey) == 0)
                            touchedSurfaceKeys[touchedSurfaceKeyCount++] = (ushort)targetSurfaceKey;
                        if (targetSurfaceKey >= 0 && targetSurfaceKey < histogram.Length)
                            histogram[(int)targetSurfaceKey] = Js.U8(histogram[(int)targetSurfaceKey] + 1);
                        repaired[candidate.index] = 1;
                        needed--;
                        repairs++;
                    }
                }
            }
            if (repairs == 0) break;
        }
    }
}
