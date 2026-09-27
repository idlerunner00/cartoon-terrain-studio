// Port of packages/shared/src/domain/dungeon/mapSimulation.ts (relief, hydrology, rifts, crossings) — keep in
// lockstep with the original. See MapSimulation.cs for the file split.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public static partial class MapSimulation
{
    private sealed class RampSite
    {
        public int tx;
        public int ty;
        public int dx;
        public int dy;
        public int b;
    }

    private sealed class RampOrder
    {
        public int at;
        public double score;
    }

    private sealed partial class MapComposer
    {
        // ---------------------------------------------------------------------------------------------------
        // Relief

        /// <summary>
        /// Band the landform into walkable shelves and let the terrain contract draw the cliffs.
        ///
        /// A continuous height field is invisible from above: nothing in it draws a line, so however carefully it is
        /// tuned it reads as noise. Quantizing the same field into shelves and materializing a rock face at every
        /// band edge is what gives the map its silhouette — escarpments, benches, a summit you can see is a summit —
        /// and it costs nothing in legality, because the ramps cut through those faces are the only walkable way
        /// between two bands and every one of them climbs exactly one level per cell.
        ///
        /// `terraceBands &lt; 2` keeps the old continuous slope for archetypes that genuinely want rolling ground.
        /// </summary>
        private void composeRelief()
        {
            double halfSpan = Math.max(1, Math.min(width, height) * 0.5);
            uint grainSeed = Js.ToUint32(seedInt ^ 0x1d7c9b31);
            double grainScale = Math.max(6, Math.min(width, height) * 0.17);
            double detailScale = Math.max(3, grainScale * 0.34);
            // STUDIO: below its centre the Mountains dial flattens the height field toward one level (at zero the map
            // is a single flat floor); the macro bias already carries the dial.
            double amplitude = Math.min(1, reliefScale);

            // A Float32Array in the original: the stored field rounds to single precision, `lo`/`hi` do not.
            var field = new float[count];
            double lo = double.PositiveInfinity;
            double hi = double.NegativeInfinity;
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    double nx = (tx - (width - 1) * 0.5) / halfSpan;
                    double ny = (ty - (height - 1) * 0.5) / halfSpan;
                    double radial = Scalar.clamp01(1 - Math.hypot(nx, ny));
                    double macro = roll.reliefBias * Scalar.smoothstep(radial);
                    // Two octaves: the coarse one decides which shelf a district lands on, the fine one makes the shelf
                    // edge ragged instead of a drawn contour.
                    double grain =
                        (valueNoise(grainSeed, tx, ty, grainScale) - 0.5) * 2 +
                        (valueNoise((int)grainSeed ^ unchecked((int)0x9e3779b9), tx, ty, detailScale) - 0.5) * 0.7;
                    double value =
                        ground[index] * amplitude + FLAT_GROUND_LEVEL * (1 - amplitude) + macro +
                        grain * roll.terraceNoise * 1.6 * amplitude;
                    field[index] = (float)value;
                    if (!isWalkable(tiles[index])) continue;
                    if (value < lo) lo = value;
                    if (value > hi) hi = value;
                }
            }
            if (!Number.isFinite(lo) || !Number.isFinite(hi))
            {
                lo = 0;
                hi = 1;
            }

            int bands = roll.terraceBands;
            if (bands >= 2)
            {
                double range = Math.max(1e-3, hi - lo);
                byte[] beforeTiles = (byte[])tiles.Clone();
                int walkableBefore = 0;
                for (int i = 0; i < count; i++) if (isWalkable(tiles[i])) walkableBefore++;

                // Banding costs walkable ground: every shelf edge becomes a rock face. Usually that is the whole point,
                // but a noisy field on a tight crop can turn the map into a staircase with no landings. The pass is
                // therefore *measured*: if the shelves ate the world, retry with fewer, gentler ones rather than ship a
                // world you can barely walk across.
                var band = new byte[count];
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int useBands = Math.max(2, bands - attempt * 2);
                    // Keep the top shelf one level below the ceiling: walls stand on top of the highest ground.
                    double step = Math.max(
                        1,
                        Math.min(
                            attempt >= 2 ? 1 : roll.terraceStep,
                            Math.floor((SIM_MAX_LEVEL - 2) / (double)(useBands - 1))));
                    const double baseLevel = 1;
                    Array.Copy(beforeTiles, tiles, count);
                    Array.Clear(protect);
                    for (int i = 0; i < count; i++)
                    {
                        double t = Scalar.clamp01((field[i] - lo) / range);
                        band[i] = Js.U8(Math.min(useBands - 1, Math.floor(t * useBands)));
                    }
                    // A shelf is a region, not a fringe. Smoothing the band ids before they become geometry is what turns
                    // a fractal contour (kilometres of cliff, no landings) into a handful of readable benches.
                    smoothBands(band, attempt == 0 ? 2 : 3);
                    for (int i = 0; i < count; i++)
                    {
                        elevation[i] = Js.I8(TerrainKit.clampElevationLevel(baseLevel + band[i] * step, SIM_MAX_LEVEL));
                    }
                    if (step > 1) cutTerraceRamps(band, baseLevel, step, useBands);
                    TerrainKit.materializeUnclimbableWalkableEdges(tiles, elevation, width, height, new MaterializeUnclimbableEdgesOptions
                    {
                        protect = (_tx, _ty, index) => protect[index] == 1 || isHeartward(index),
                    });
                    rescueStrandedTerraces();
                    dissolveStrandedLand();
                    int walkableNow = 0;
                    for (int i = 0; i < count; i++) if (isWalkable(tiles[i])) walkableNow++;
                    if (walkableNow >= walkableBefore * 0.62) break;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    elevation[i] = Js.I8(TerrainKit.clampElevationLevel(field[i], SIM_MAX_LEVEL));
                }
                TerrainKit.relaxWalkableElevationSteps(tiles, elevation, width, height, SIM_MAX_LEVEL);
            }

            applyLandformMotif();
            if (reliefScale > 1) growRock();
            resolveBlockedElevation();
            for (int i = 0; i < count; i++) ground[i] = Js.U8(elevation[i]);
            recordStage(MapSimulationLayer.Relief, sweepPlan(MapRevealSweep.Radial, heartTx, heartTy, 0, 1, 0.4));
        }

        /// <summary>
        /// Stamp the rolled landform motif over the terraced relief — the world's macro bones.
        ///
        /// The motif is applied *after* banding on purpose: terracing supplies texture, the motif supplies structure,
        /// and structure has to win where the two disagree or the caldera turns back into rolling hills. Afterwards
        /// the same legality chain that finishes a terrace pass runs over the result, so a stamp is free to be bold
        /// with elevation without ever shipping an illegal climb or an unreachable pocket.
        ///
        /// The whole pass is *measured*: a stamp that ate the walkable world is reverted outright. A motif is worth
        /// having only while there is still a world left to walk through it.
        /// </summary>
        private void applyLandformMotif()
        {
            MapLandformMotif? motif = roll.motif;
            if (motif == null) return;
            // STUDIO: a motif is landform: rock and height. A flat world has none, and below its centre the Mountains
            // dial lowers the motif's steps (and wears its rock away, below), so the layout keeps its lines.
            if (flatWorld) return;
            if (reliefScale < 1)
            {
                motif = new MapLandformMotif
                {
                    key = motif.key,
                    name = motif.name,
                    summary = motif.summary,
                    form = motif.form,
                    extent = motif.extent,
                    step = motif.step * reliefScale,
                    weight = motif.weight,
                };
            }
            byte[] beforeTiles = (byte[])tiles.Clone();
            sbyte[] beforeElevation = (sbyte[])elevation.Clone();
            int walkableBefore = 0;
            for (int i = 0; i < count; i++)
                if (isWalkable(tiles[i])) walkableBefore++;

            var canvas = new MotifCanvas
            {
                width = width,
                height = height,
                tiles = tiles,
                elevation = elevation,
                maxLevel = SIM_MAX_LEVEL,
                seed = seedNum,
                heartTx = heartTx,
                heartTy = heartTy,
                chaos = chaos,
                canWrite = (tx, ty) =>
                    isInset(tx, ty, RIM_CELLS) && !isHeartward(tileIndex(width, tx, ty)),
            };
            bool stamped = MapSimulationMotifs.stampLandformMotif(canvas, motif, stream($"motif:{motif.key}"));
            if (!stamped)
            {
                Array.Copy(beforeTiles, tiles, count);
                Array.Copy(beforeElevation, elevation, count);
                return;
            }
            if (riftScale < 1) thinMotifRifts(beforeTiles);
            if (reliefScale < 1) thinMotifRock(beforeTiles);

            TerrainKit.materializeUnclimbableWalkableEdges(tiles, elevation, width, height, new MaterializeUnclimbableEdgesOptions
            {
                protect = (_tx, _ty, index) => protect[index] == 1 || isHeartward(index),
            });
            rescueStrandedTerraces();
            dissolveStrandedLand();

            int walkableAfter = 0;
            for (int i = 0; i < count; i++) if (isWalkable(tiles[i])) walkableAfter++;
            if (
                walkableAfter < walkableBefore * MOTIF_MIN_SURVIVING_SHARE ||
                walkableAfter < count * MOTIF_MIN_WALKABLE_SHARE ||
                walkableAfter < 64
            )
            {
                Array.Copy(beforeTiles, tiles, count);
                Array.Copy(beforeElevation, elevation, count);
                motifApplied = null;
                return;
            }
            motifApplied = roll.motif;
        }

        /// <summary>
        /// STUDIO: the ravines a motif tears (plate seams, lattice canyons) belong to the Chasms dial. Below its centre
        /// they close in stretches, chosen by a coarse noise so the canyons keep long runs, and at zero none is left.
        /// A closed stretch becomes ground at the level beside it.
        /// </summary>
        private void thinMotifRifts(byte[] beforeTiles)
        {
            uint noiseSeed = Js.ToUint32(seedInt ^ 0x6a09e667);
            var torn = new List<ScoredCell>();
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != TileType.Chasm || beforeTiles[i] == TileType.Chasm) continue;
                int tx = i % width;
                int ty = i / width;
                torn.Add(new ScoredCell { index = i, score = valueNoise(noiseSeed, tx, ty, 9) + latticeHash(noiseSeed, tx, ty) * 0.05 });
            }
            torn.sort((a, b) =>
            {
                double d = b.score - a.score;
                return d != 0 ? Math.sign(d) : a.index - b.index;
            });
            var closed = new byte[count];
            for (int k = clampInt(torn.Count * riftScale, 0, torn.Count); k < torn.Count; k++) closed[torn[k].index] = 1;
            levelIntoLand(closed, strandedStay: true);
        }

        /// <summary>
        /// STUDIO: the rock a motif raises (rims, ring walls, blocks) belongs to the Mountains dial as well. Below its
        /// centre it wears away from the edges of each mass inward, down to the ground beside it, so the rock of a
        /// lower setting is always part of the rock of a higher one.
        /// </summary>
        private void thinMotifRock(byte[] beforeTiles)
        {
            short[] depth = chebyshevDistance((i) => tiles[i] == TileType.Solid);
            var raised = new List<ScoredCell>();
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != TileType.Solid || beforeTiles[i] == TileType.Solid) continue;
                raised.Add(new ScoredCell { index = i, score = depth[i] + rockWobble(i % width, i / width) });
            }
            raised.sort((a, b) =>
            {
                double d = b.score - a.score;
                return d != 0 ? Math.sign(d) : a.index - b.index;
            });
            var worn = new byte[count];
            for (int k = clampInt(raised.Count * reliefScale, 0, raised.Count); k < raised.Count; k++) worn[raised[k].index] = 1;
            levelIntoLand(worn, strandedStay: true);
        }

        /// <summary>
        /// Majority-smooth the band ids so shelves become compact regions.
        ///
        /// This is the difference between terraces and corduroy. A raw quantized noise field has an enormous band
        /// perimeter, and since every metre of that perimeter becomes a cliff, the map ends up as more wall than
        /// floor. Two or three majority passes collapse the fringe without touching the macro shape.
        /// </summary>
        private void smoothBands(byte[] band, int passes)
        {
            var next = new byte[count];
            var tally = new int[16];
            for (int pass = 0; pass < passes; pass++)
            {
                Array.Copy(band, next, count);
                for (int ty = 1; ty < height - 1; ty++)
                {
                    for (int tx = 1; tx < width - 1; tx++)
                    {
                        int index = tileIndex(width, tx, ty);
                        if (!isWalkable(tiles[index])) continue;
                        Array.Clear(tally);
                        foreach (var (dx, dy) in NEIGHBOURS8)
                        {
                            tally[band[tileIndex(width, tx + dx, ty + dy)]] += 1;
                        }
                        int current = band[index];
                        // Ties keep the current band, so smoothing can never oscillate between two passes.
                        int best = current;
                        int bestCount = tally[current] + 1;
                        for (int value = 0; value < tally.Length; value++)
                        {
                            if (tally[value] > bestCount)
                            {
                                bestCount = tally[value];
                                best = value;
                            }
                        }
                        next[index] = (byte)best;
                    }
                }
                Array.Copy(next, band, count);
            }
        }

        /// <summary>
        /// Cut the ramps that make a banded landform walkable.
        ///
        /// Ramps are placed per boundary *component*, not per band: two shelves can meet along several separate
        /// escarpments, and punching only the longest of them leaves whole benches stranded for the connectivity
        /// pass to dissolve — which is how a terraced map quietly turns back into a plain.
        /// </summary>
        private void cutTerraceRamps(byte[] band, double baseLevel, double step, int bands)
        {
            // Boundary sites: a walkable cell whose cardinal neighbour sits exactly one band higher.
            var siteOf = new int[count].fill(-1);
            var sites = new List<RampSite>();
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    if (!isWalkable(tiles[index])) continue;
                    int b = band[index];
                    if (b >= bands - 1) continue;
                    foreach (var (dx, dy) in NEIGHBOURS4)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (!isWalkable(tiles[ni]) || band[ni] != b + 1) continue;
                        siteOf[index] = sites.Count;
                        sites.Add(new RampSite { tx = tx, ty = ty, dx = dx, dy = dy, b = b });
                        break;
                    }
                }
            }
            if (sites.Count == 0) return;

            // Label boundary components by walking the site cells 8-connected within one band.
            var label = new int[sites.Count].fill(-1);
            var components = new List<List<int>>();
            var stack = new List<int>();
            for (int start = 0; start < sites.Count; start++)
            {
                if (label[start] != -1) continue;
                int id = components.Count;
                var cells = new List<int>();
                stack.Clear();
                stack.Add(start);
                label[start] = id;
                while (stack.Count > 0)
                {
                    int at = stack.pop();
                    cells.Add(at);
                    RampSite site = sites[at];
                    foreach (var (dx, dy) in NEIGHBOURS8)
                    {
                        int nx = site.tx + dx;
                        int ny = site.ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int other = siteOf[tileIndex(width, nx, ny)];
                        if (other < 0 || label[other] != -1) continue;
                        if (sites[other].b != site.b) continue;
                        label[other] = id;
                        stack.Add(other);
                    }
                }
                components.Add(cells);
            }

            double spacing = Math.max(7, Math.round(Math.min(width, height) * 0.14));
            Rng rng = stream("ramps");
            foreach (List<int> cells in components)
            {
                // Short escarpments still get one way up: a three-cell step nobody can climb is just a wall.
                double wanted = Math.max(2, Math.round(cells.Count / spacing));
                List<RampOrder> ordered = cells
                    .map((at) => new RampOrder
                    {
                        at = at,
                        score = latticeHash(seedInt ^ 0x3ab1, sites[at].tx, sites[at].ty),
                    })
                    .sort((a, b) =>
                    {
                        double d = a.score - b.score;
                        return Js.Truthy(d) ? d : a.at - b.at;
                    });
                var placed = new List<CellRef>();
                foreach (RampOrder entry in ordered)
                {
                    if (placed.Count >= wanted) break;
                    RampSite site = sites[entry.at];
                    if (
                        placed.some(
                            (p) =>
                                Math.abs(p.tx - site.tx) < spacing * 0.6 && Math.abs(p.ty - site.ty) < spacing * 0.6)
                    )
                        continue;
                    carveRamp(site.tx, site.ty, site.dx, site.dy, baseLevel + site.b * step, step, rng);
                    placed.Add(new CellRef(site.tx, site.ty));
                }
            }
        }

        /// <summary>One cutting through an escarpment: three cells wide, climbing exactly one level per cell.</summary>
        private void carveRamp(
            int tx,
            int ty,
            int dx,
            int dy,
            double lowLevel,
            double step,
            Rng rng)
        {
            int px = dy;
            int py = dx;
            int half = rng.@bool(0.35) ? 2 : 1;
            for (int k = 0; k <= step + 1; k++)
            {
                double level = TerrainKit.clampElevationLevel(lowLevel + Math.min(k, step), SIM_MAX_LEVEL);
                for (int w = -half; w <= half; w++)
                {
                    int cx = tx + dx * k + px * w;
                    int cy = ty + dy * k + py * w;
                    if (!inBounds(width, height, cx, cy)) continue;
                    if (cx < RIM_CELLS || cy < RIM_CELLS || cx >= width - RIM_CELLS || cy >= height - RIM_CELLS)
                        continue;
                    int index = tileIndex(width, cx, cy);
                    int tile = tiles[index];
                    if (tile == TileType.Water || tile == TileType.Chasm) continue;
                    tiles[index] = TileType.Floor;
                    elevation[index] = Js.I8(level);
                    protect[index] = 1;
                }
            }
        }

        /// <summary>
        /// After the cliffs land, any shelf a ramp missed is an island. Small ones are dissolved by the connectivity
        /// pass; a large one is worth a staircase, because losing a whole bench costs the map its best silhouette.
        /// </summary>
        private void rescueStrandedTerraces()
        {
            refreshHeart();
            byte[] reach = floodFillWalkable(tiles, width, height, heartTx, heartTy);
            int walkable = 0;
            for (int i = 0; i < count; i++) if (isWalkable(tiles[i])) walkable++;
            if (walkable == 0) return;
            double worthSaving = Math.max(24, Math.round(walkable * 0.02));

            var seen = new byte[count];
            var stack = new List<int>();
            for (int start = 0; start < count; start++)
            {
                if (seen[start] != 0 || reach[start] != 0 || !isWalkable(tiles[start])) continue;
                stack.Clear();
                stack.Add(start);
                seen[start] = 1;
                var cells = new List<int>();
                while (stack.Count > 0)
                {
                    int index = stack.pop();
                    cells.Add(index);
                    int tx = index % width;
                    int ty = index / width;
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
                if (cells.Count < worthSaving) continue;
                cutStairToReachable(cells, reach);
            }
        }

        /// <summary>Drive a staircase from a stranded shelf to the nearest cell of the main body, one level per cell.</summary>
        private bool cutStairToReachable(IReadOnlyList<int> cells, byte[] reach)
        {
            int bestFrom = -1;
            int bestTo = -1;
            double bestCost = double.PositiveInfinity;
            // Search from a sample of the island's cells: the nearest main-body cell to any of them is good enough,
            // and scanning every cell of a large shelf against the whole map is not.
            int stride = (int)Math.max(1, Math.floor(cells.Count / 48.0));
            for (int i = 0; i < cells.Count; i += stride)
            {
                int from = cells[i];
                int fx = from % width;
                int fy = from / width;
                for (int radius = 2; radius <= 14; radius++)
                {
                    bool found = false;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            if (Math.max(Math.abs(dx), Math.abs(dy)) != radius) continue;
                            int nx = fx + dx;
                            int ny = fy + dy;
                            if (!inBounds(width, height, nx, ny)) continue;
                            int to = tileIndex(width, nx, ny);
                            if (reach[to] == 0 || !isWalkable(tiles[to])) continue;
                            int drop = Math.abs(elevation[to] - elevation[from]);
                            double cost = radius + drop * 0.5;
                            if (cost < bestCost && drop <= radius)
                            {
                                bestCost = cost;
                                bestFrom = from;
                                bestTo = to;
                            }
                            found = true;
                        }
                    }
                    if (found) break;
                }
            }
            if (bestFrom < 0 || bestTo < 0) return false;
            return cutStair(bestFrom, bestTo);
        }

        /// <summary>
        /// Carve a walkable stair between two cells: a straight cut whose level moves at most one step per cell.
        ///
        /// This is the single primitive behind every deliberate climb in the composition — terrace rescues and the
        /// road network's cliff crossings both go through it, so a stair looks and behaves the same wherever the map
        /// needed one.
        /// </summary>
        private bool cutStair(int from, int to, int halfWidth = 1)
        {
            int ax = from % width;
            int ay = from / width;
            int bx = to % width;
            int by = to / width;
            int span = Math.max(Math.abs(bx - ax), Math.abs(by - ay));
            if (span == 0) return true;
            int startLevel = elevation[from];
            int endLevel = elevation[to];
            if (Math.abs(endLevel - startLevel) > span) return false;
            int direction = (int)Math.sign(endLevel - startLevel);
            int level = startLevel;
            for (int s = 0; s <= span; s++)
            {
                double t = (double)s / span;
                int cx = (int)Math.round(ax + (bx - ax) * t);
                int cy = (int)Math.round(ay + (by - ay) * t);
                // Move the level toward the target only while there is still distance left to spend on it.
                int remaining = span - s;
                if (level != endLevel && Math.abs(endLevel - level) >= remaining) level += direction;
                else if (level != endLevel && s > 0) level += direction;
                for (int w = -halfWidth; w <= halfWidth; w++)
                {
                    int px = cx + (Math.abs(bx - ax) >= Math.abs(by - ay) ? 0 : w);
                    int py = cy + (Math.abs(bx - ax) >= Math.abs(by - ay) ? w : 0);
                    if (!inBounds(width, height, px, py)) continue;
                    if (px < RIM_CELLS || py < RIM_CELLS || px >= width - RIM_CELLS || py >= height - RIM_CELLS)
                        continue;
                    int index = tileIndex(width, px, py);
                    int tile = tiles[index];
                    if (tile == TileType.Water || tile == TileType.Chasm || tile == TileType.Bridge)
                        continue;
                    tiles[index] = TileType.Floor;
                    elevation[index] = Js.I8(TerrainKit.clampElevationLevel(level, SIM_MAX_LEVEL));
                    protect[index] = 1;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------------------------------------------
        // Hydrology

        /// <summary>
        /// Give the map a real hydrology: watercourses that run downhill from the high ground, then standing water
        /// pooling in whatever low country is left, up to the archetype's budget. The landform's own water is the
        /// seed set, so a theme that is naturally wet stays wet.
        /// </summary>
        private void composeHydrology()
        {
            double target = Math.round(interiorCellCount() * roll.waterBudget);
            if (target > 0)
            {
                int courses = clampInt(1 + roll.waterBudget * 9, 1, 4);
                for (int i = 0; i < courses; i++) carveWatercourse(i);
            }

            // Standing water settles in the lowest ground. The landform's own basins get a head start so a wet theme
            // keeps its shape, and the dither in `selectByField` stops the result reading as a hard contour line.
            var candidates = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != TileType.Floor) continue;
                if (!hazardCanClaim(i)) continue;
                candidates.Add(i);
            }
            int already = 0;
            for (int i = 0; i < count; i++) if (tiles[i] == TileType.Water) already++;
            double remaining = Math.max(0, target - already);
            uint basinSeed = Js.ToUint32(seedInt ^ 0x3d9f42a7);
            double basinScale = Math.max(7, Math.min(width, height) * 0.19);
            List<int> wet = selectByField(candidates, remaining, 0x3d9f42a7, (tx, ty, index) =>
            {
                double low = 1 - elevation[index] / (double)SIM_MAX_LEVEL;
                double basin = valueNoise(basinSeed, tx, ty, basinScale);
                double inherited = landform[index] == TileType.Water ? 0.45 : 0;
                return low * 2.2 + basin * 1.1 + inherited;
            });
            foreach (int index in wet) tiles[index] = TileType.Water;
            // STUDIO: the Water dial at zero means no water at all, whatever the landform or a motif left.
            if (waterScale <= 0) closeHazard(TileType.Water);

            consolidate(TileType.Water);
            keepWaterOffTheRampart();
            dissolveWaterSpecks();
            resolveWaterElevation();
            waterAxis = principalAxis(TileType.Water);
            recordStage(
                MapSimulationLayer.Hydrology,
                sweepPlan(MapRevealSweep.Flow, waterAxis.ax, waterAxis.ay, waterAxis.dx, waterAxis.dy, 0.3));
        }

        /// <summary>
        /// Run one watercourse downhill from a high shoulder. Steepest descent with a hash-driven meander gives a
        /// course that reads as carved by the relief rather than drawn over it.
        /// </summary>
        private void carveWatercourse(int ordinal)
        {
            Rng rng = stream($"river:{ordinal}");
            List<int> sources = topCells((tx, ty, index) =>
            {
                if (tiles[index] != TileType.Floor) return -1;
                if (!isInset(tx, ty, 3)) return -1;
                return (
                    elevation[index] / (double)SIM_MAX_LEVEL + latticeHash(seedInt ^ ordinal, tx, ty) * 0.5
                );
            }, 48);
            if (sources.Count == 0) return;
            int start = sources[(int)rng.@int(0, Math.min(sources.Count, 12) - 1)];

            int tx = start % width;
            int ty = start / width;
            int maxSteps = width + height;
            double bias = rng.range(0, Math.PI * 2);
            for (int step = 0; step < maxSteps; step++)
            {
                int radius = 1 + (rng.next() < 0.32 ? 1 : 0);
                bool poured = false;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dy * dy > radius * radius + 0.4) continue;
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        if (!isInset(nx, ny, RIM_CELLS)) continue;
                        int index = tileIndex(width, nx, ny);
                        if (tiles[index] != TileType.Floor || !hazardCanClaim(index)) continue;
                        tiles[index] = TileType.Water;
                        poured = true;
                    }
                }
                // Steepest legal descent, nudged by a slow meander so the course never runs perfectly straight.
                double bestScore = double.PositiveInfinity;
                int bestX = tx;
                int bestY = ty;
                foreach (var (dx, dy) in NEIGHBOURS8)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    if (!isInset(nx, ny, RIM_CELLS)) continue;
                    int index = tileIndex(width, nx, ny);
                    int tile = tiles[index];
                    if (tile == TileType.Chasm) continue;
                    double meander = Math.cos(bias + step * 0.19) * dx + Math.sin(bias + step * 0.19) * dy;
                    double score = elevation[index] - meander * 0.55 + latticeHash(seedNum, nx, ny) * 0.4;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestX = nx;
                        bestY = ny;
                    }
                }
                if (!poured && bestX == tx && bestY == ty) break;
                if (bestX == tx && bestY == ty) break;
                tx = bestX;
                ty = bestY;
                // The course ends where it meets standing water or leaves the composed interior.
                if (tiles[tileIndex(width, tx, ty)] == TileType.Water && step > 4) break;
            }
        }

        /// <summary>
        /// Turn a dithered field selection into readable bodies: close the pinholes, shave the isolated fringe, then
        /// drop whatever is still too small to read. Without this the map gets speckle where it should get lakes —
        /// and no straight, deck-thick line ever crosses a porous body, so the crossings pass would starve too.
        /// </summary>
        private void consolidate(int tile, int passes = 2)
        {
            var next = new byte[count];
            for (int pass = 0; pass < passes; pass++)
            {
                Array.Copy(tiles, next, count);
                for (int ty = RIM_CELLS; ty < height - RIM_CELLS; ty++)
                {
                    for (int tx = RIM_CELLS; tx < width - RIM_CELLS; tx++)
                    {
                        int index = tileIndex(width, tx, ty);
                        int current = tiles[index];
                        if (current != TileType.Floor && current != tile) continue;
                        if (!hazardCanClaim(index)) continue;
                        int near = 0;
                        int cardinal = 0;
                        foreach (var (dx, dy) in NEIGHBOURS8)
                        {
                            int nx = tx + dx;
                            int ny = ty + dy;
                            if (!inBounds(width, height, nx, ny)) continue;
                            if (tiles[tileIndex(width, nx, ny)] != tile) continue;
                            near++;
                            if (dx == 0 || dy == 0) cardinal++;
                        }
                        if (current == TileType.Floor && near >= 5) next[index] = (byte)tile;
                        else if (current == tile && cardinal <= 1 && near <= 2) next[index] = TileType.Floor;
                    }
                }
                Array.Copy(next, tiles, count);
            }
        }

        /// <summary>
        /// Give the frame a shoreline: standing water never climbs the rampart.
        ///
        /// A rock frame stands several levels above the interior. Water pressed against that wall reads to the
        /// renderer as a body at a cliff edge, and it answers with a full cascade curtain — a tall sheet that rises
        /// off the map's northern rim and hangs over the backdrop. Lakes do not lap at the top of a rampart anyway;
        /// a one-cell bank is both what the landscape wants and what keeps the frame quiet. A water frame (an island's
        /// own sea) is exempt: there is no wall there for anything to fall over.
        /// </summary>
        private void keepWaterOffTheRampart()
        {
            if (roll.frame == MapFrameKind.Island || roll.frame == MapFrameKind.Open) return;
            double band = Math.max(2, Math.round(Math.min(width, height) * roll.frameDepth) + 2);
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    if (tiles[index] != TileType.Water) continue;
                    int edge = Math.min(Math.min(tx, ty), Math.min(width - 1 - tx, height - 1 - ty));
                    if (edge <= band) tiles[index] = TileType.Floor;
                }
            }
        }

        /// <summary>
        /// Put a bank between every rift and every water body.
        ///
        /// The consolidation pass can grow a rift into contact with a lake even though no such candidate was picked,
        /// and a water cell touching a chasm lip is what the renderer builds a full cascade curtain for. One shared
        /// edge is enough to hang a waterfall over the void at the map's rim. The bank is one cell of ordinary
        /// ground — cheap, physically right, and it removes the whole class of accidental set piece.
        /// </summary>
        private void separateRiftFromWater()
        {
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    if (tiles[index] != TileType.Chasm) continue;
                    if (!neighbourHas(tx, ty, TileType.Water)) continue;
                    // STUDIO: a flat world has no rock to make a bank of, even at the rim.
                    tiles[index] = isInset(tx, ty, RIM_CELLS + 1) || flatWorld ? (byte)TileType.Floor : (byte)TileType.Solid;
                }
            }
        }

        /// <summary>A single wet cell is noise, not water. Dissolve anything too small to read as a body.</summary>
        private void dissolveWaterSpecks()
        {
            var seen = new byte[count];
            var stack = new List<int>();
            for (int start = 0; start < count; start++)
            {
                if (seen[start] != 0 || tiles[start] != TileType.Water) continue;
                stack.Clear();
                stack.Add(start);
                seen[start] = 1;
                var cells = new List<int>();
                while (stack.Count > 0)
                {
                    int index = stack.pop();
                    cells.Add(index);
                    int tx = index % width;
                    int ty = index / width;
                    foreach (var (dx, dy) in NEIGHBOURS4)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (seen[ni] != 0 || tiles[ni] != TileType.Water) continue;
                        seen[ni] = 1;
                        stack.Add(ni);
                    }
                }
                if (cells.Count < 4) foreach (int index in cells) tiles[index] = TileType.Floor;
            }
        }

        // ---------------------------------------------------------------------------------------------------
        // Rifts

        /// <summary>Reopen the landform's ravines up to the archetype's budget and give them their storage depth.</summary>
        private void composeRifts()
        {
            int interior = interiorCellCount();
            double target = Math.round(interior * roll.riftBudget);
            // STUDIO: the Chasms dial at zero means no chasm at all — a motif's seams close up as well.
            if (riftScale <= 0) closeHazard(TileType.Chasm);
            var candidates = new List<int>();
            var open = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != TileType.Floor && tiles[i] != TileType.Solid) continue;
                int tx = i % width;
                int ty = i / width;
                if (!hazardCanClaim(i) || !isInset(tx, ty, RIM_CELLS + 1)) continue;
                // A rift never opens straight into standing water. Physically the two want a bank between them, and
                // visually a water body pouring over a ravine lip is the renderer's most delicate construction — a
                // composition has no business manufacturing one at every lakeside by accident.
                if (neighbourHas(tx, ty, TileType.Water)) continue;
                if (landform[i] == TileType.Chasm) candidates.Add(i);
                else if (tiles[i] == TileType.Floor) open.Add(i);
            }
            // STUDIO: up to the dial's centre the rift reopens the landform's own ravines, as in the original. What the
            // dial asks for beyond that takes the landform's remaining ravines first, then follows long winding lines of
            // a ridged noise field across open ground — the original stopped at the landform's ravines, so the dial
            // could not add rift above its centre on most maps, and none at all where the landform had none.
            double baseTarget = Math.round(interior * roll.riftBase);
            List<int> torn = selectByField(candidates, Math.min(target, baseTarget), 0x4cf5ad43, (tx, ty, _index) =>
                valueNoise(seedInt ^ 0x4cf5ad43, tx, ty, 29));
            foreach (int index in torn) tiles[index] = TileType.Chasm;
            double more = target - baseTarget;
            if (more > 0)
            {
                uint ravineSeed = Js.ToUint32(seedInt ^ 0x3c6ef372);
                double ravineScale = Math.max(9, Math.min(width, height) * 0.3);
                var rest = candidates.filter((index) => tiles[index] != TileType.Chasm);
                rest.AddRange(open);
                List<int> extra = selectByField(rest, more, 0x3c6ef372, (tx, ty, index) =>
                    landform[index] == TileType.Chasm
                        ? 10 + valueNoise(seedInt ^ 0x4cf5ad43, tx, ty, 29)
                        : 1 - Math.abs(valueNoise(ravineSeed, tx, ty, ravineScale) * 2 - 1) * 2.5);
                foreach (int index in extra) tiles[index] = TileType.Chasm;
            }
            consolidate(TileType.Chasm);
            separateRiftFromWater();
            resolveBlockedElevation();
            TerrainKit.assignChasmDepths(tiles, elevation, width, height);
            riftAxis = principalAxis(TileType.Chasm);
            recordStage(
                MapSimulationLayer.Rifts,
                sweepPlan(MapRevealSweep.Flow, riftAxis.ax, riftAxis.ay, riftAxis.dx, riftAxis.dy, 0.75));
        }

        // ---------------------------------------------------------------------------------------------------
        // Crossings

        /// <summary>
        /// Stitch the world back together with real decks.
        ///
        /// Every crossing is an axis-aligned band that starts on walkable land, spans nothing but water or rift, and
        /// lands on walkable land again — the exact shape the bridge contract asks for (a deck touches its span,
        /// reaches two distinct banks, and is never thinner than two cells). Crossings are then chosen like a
        /// minimum spanning forest over the walkable components: the cheapest span that merges two separate bodies
        /// wins, so the map ends up connected with as few, and as deliberate, bridges as possible.
        /// </summary>
        private void composeCrossings()
        {
            List<TerrainCrossing> candidates = crossingCandidates();
            var owner = new int[count].fill(-1);
            List<List<int>> components = walkableComponents();
            for (int id = 0; id < components.Count; id++)
            {
                foreach (int index in components[id]) owner[index] = id;
            }
            var parent = new int[components.Count];
            for (int id = 0; id < parent.Length; id++) parent[id] = id;
            int find(int id)
            {
                int root = id;
                while (parent[root] != root) root = parent[root];
                while (parent[id] != root)
                {
                    int next = parent[id];
                    parent[id] = root;
                    id = next;
                }
                return root;
            }

            candidates.sort((a, b) =>
            {
                int d = a.length - b.length;
                return d != 0 ? d : a.start - b.start;
            });
            int builtCount = 0;
            foreach (TerrainCrossing candidate in candidates)
            {
                int left = owner[candidate.bankA];
                int right = owner[candidate.bankB];
                if (left < 0 || right < 0) continue;
                int rootA = find(left);
                int rootB = find(right);
                // Merging spans are always worth building. A small number of extra spans are kept for character, so a
                // wide river gets more than the single mathematically necessary crossing.
                bool merges = rootA != rootB;
                if (!merges && (builtCount == 0 || candidate.length > 9 || builtCount > candidates.Count * 0.12))
                    continue;
                if (!spanIsStillOpen(candidate)) continue;
                paintCrossing(candidate);
                if (merges) parent[rootA] = rootB;
                builtCount++;
            }

            // Order matters: a deck that turns out to span nothing becomes ordinary floor, and only THEN is the world
            // trimmed to one body — otherwise a demoted deck can be left stranded in the middle of a lake.
            repairBridges();
            dissolveStrandedLand();
            resolveBridgeElevation();
            recordStage(MapSimulationLayer.Crossings, sweepPlan(MapRevealSweep.Scatter, heartTx, heartTy, 1, 0, 0.2));
        }

        private static readonly char[] CROSSING_AXES = { 'x', 'y' };

        /// <summary>
        /// Every straight, deck-thick span across water or rift with walkable land on both ends. Scanning rows and
        /// columns for bands that are uniformly spannable is what makes the result legal by construction.
        /// </summary>
        private List<TerrainCrossing> crossingCandidates()
        {
            var @out = new List<TerrainCrossing>();
            double maxLength = Math.max(6, Math.round(Math.min(width, height) * 0.4));
            foreach (char axis in CROSSING_AXES)
            {
                int across = axis == 'x' ? height : width;
                int along = axis == 'x' ? width : height;
                for (int lane = RIM_CELLS; lane + DECK_THICKNESS <= across - RIM_CELLS; lane++)
                {
                    int runStart = -1;
                    for (int step = RIM_CELLS; step < along - RIM_CELLS; step++)
                    {
                        int state = laneState(axis, lane, step);
                        if (state == LANE_SPAN)
                        {
                            if (runStart < 0) runStart = step;
                            continue;
                        }
                        if (runStart >= 0 && state == LANE_LAND)
                        {
                            int length = step - runStart;
                            int before = runStart - 1;
                            if (length <= maxLength && laneState(axis, lane, before) == LANE_LAND)
                            {
                                int bankA = laneIndex(axis, lane, before);
                                int bankB = laneIndex(axis, lane, step);
                                int drop = Math.abs(elevation[bankA] - elevation[bankB]);
                                if (drop <= 2)
                                    @out.Add(new TerrainCrossing
                                    {
                                        axis = axis,
                                        lane = lane,
                                        start = runStart,
                                        length = length,
                                        bankA = bankA,
                                        bankB = bankB,
                                    });
                            }
                        }
                        runStart = -1;
                    }
                }
            }
            return @out;
        }

        /// <summary>What a deck-thick slice of a lane is: solid land to stand on, a spannable gap, or unusable.</summary>
        private int laneState(char axis, int lane, int step)
        {
            int along = axis == 'x' ? width : height;
            if (step < 0 || step >= along) return LANE_BLOCKED;
            int land = 0;
            int span = 0;
            for (int k = 0; k < DECK_THICKNESS; k++)
            {
                int index = laneIndex(axis, lane + k, step);
                if (index < 0) return LANE_BLOCKED;
                int tile = tiles[index];
                if (tile == TileType.Floor || tile == TileType.Bridge) land++;
                else if (tile == TileType.Water || tile == TileType.Chasm) span++;
                else return LANE_BLOCKED;
            }
            if (land == DECK_THICKNESS) return LANE_LAND;
            if (span == DECK_THICKNESS) return LANE_SPAN;
            return LANE_BLOCKED;
        }

        private int laneIndex(char axis, int lane, int step)
        {
            int tx = axis == 'x' ? step : lane;
            int ty = axis == 'x' ? lane : step;
            if (!inBounds(width, height, tx, ty)) return -1;
            return tileIndex(width, tx, ty);
        }

        /// <summary>An earlier crossing may have consumed part of this span; only build over untouched water or rift.</summary>
        private bool spanIsStillOpen(TerrainCrossing crossing)
        {
            for (int step = crossing.start; step < crossing.start + crossing.length; step++)
            {
                if (laneState(crossing.axis, crossing.lane, step) != LANE_SPAN) return false;
            }
            return (
                laneState(crossing.axis, crossing.lane, crossing.start - 1) == LANE_LAND &&
                laneState(crossing.axis, crossing.lane, crossing.start + crossing.length) == LANE_LAND
            );
        }

        private void paintCrossing(TerrainCrossing crossing)
        {
            for (int step = crossing.start; step < crossing.start + crossing.length; step++)
            {
                for (int k = 0; k < DECK_THICKNESS; k++)
                {
                    int index = laneIndex(crossing.axis, crossing.lane + k, step);
                    if (index >= 0)
                    {
                        tiles[index] = TileType.Bridge;
                        protect[index] = 1;
                    }
                }
            }
        }

        /// <summary>Land the crossings could not reach is dead space; return it to rock so the map stays one place.</summary>
        private void dissolveStrandedLand()
        {
            refreshHeart();
            // STUDIO: a flat world has no rock; stranded land stays ground (see finishBedrock).
            if (flatWorld) return;
            byte[] reach = floodFillWalkable(tiles, width, height, heartTx, heartTy);
            // STUDIO: only pockets turn to rock. In the original every piece the crossings missed became rock, and
            // which piece that was flipped with the smallest change (a bridge one cell shorter, a wonder over a
            // landing): thousands of cells of rock came and went, whole massifs that no dial asked for. A large piece
            // stays ground (the map check lists it as a hint); the size is the one a stranded terrace is saved at.
            int walkable = 0;
            for (int i = 0; i < count; i++) if (isWalkable(tiles[i])) walkable++;
            double pocket = Math.max(24, Math.round(walkable * 0.02));
            var seen = new byte[count];
            var stack = new List<int>();
            var cells = new List<int>();
            for (int start = 0; start < count; start++)
            {
                if (seen[start] != 0 || reach[start] != 0 || !isWalkable(tiles[start])) continue;
                stack.Clear();
                cells.Clear();
                stack.Add(start);
                seen[start] = 1;
                while (stack.Count > 0)
                {
                    int index = stack.pop();
                    cells.Add(index);
                    int tx = index % width;
                    int ty = index / width;
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
                if (cells.Count < pocket) foreach (int index in cells) tiles[index] = TileType.Solid;
            }
        }

        /// <summary>
        /// Bring every deck back onto the bridge contract: no stray span, no deck thinner than two cells.
        ///
        /// Composition narrows decks in ways no single pass can foresee — a wonder stamps over one lane of a
        /// crossing, an avenue levels a landing, a cliff materializes against a bank — so the repair runs after each
        /// of those beats rather than being left to the final validation loop, and it is the SHARED widener, not a
        /// private reimplementation of the same rule.
        /// </summary>
        private void repairBridges()
        {
            // Demotion only, never widening. Widening a thin deck writes a new Bridge cell, which can turn the next
            // component into a stray, which demotes it, which thins its neighbour — the repair chases its own tail
            // and never lands. Demotion is monotone: every pass removes deck cells or the world is already legal.
            for (int pass = 0; pass < 12; pass++)
            {
                TerrainBridge.demoteStrayBridgeComponents(tiles, width, height, TileType.Floor);
                TerrainBridge.demoteIncompleteFiniteBridgeComponents(tiles, width, height, TileType.Floor);
                List<BridgeWidthIssue> thin = TerrainBridge.findBridgeWidthIssues(tiles, width, height, 2);
                if (thin.Count == 0) return;
                foreach (BridgeWidthIssue issue in thin) tiles[issue.index] = TileType.Floor;
            }
        }
    }
}
