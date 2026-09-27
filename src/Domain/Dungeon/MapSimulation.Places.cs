// Port of packages/shared/src/domain/dungeon/mapSimulation.ts (wonders, roads, theming, flora,
// landmarks) — keep in lockstep with the original. See MapSimulation.cs for the file split.
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
    /// <summary>Proximity masks the wonder site search reads (TS `{ water; rift; solid }`).</summary>
    private sealed class WonderProximity
    {
        public byte[] water = Array.Empty<byte>();
        public byte[] rift = Array.Empty<byte>();
        public byte[] solid = Array.Empty<byte>();
    }

    private sealed class ThemeAnchor
    {
        public double x;
        public double y;
        public double reach;
    }

    private sealed partial class MapComposer
    {
        // ---------------------------------------------------------------------------------------------------
        // Wonders

        /// <summary>
        /// Raise the map's monuments.
        ///
        /// This is the beat that decides whether a composed world reads as a place or as terrain. Everything else
        /// the composer does is statistical — a landform, a water budget, an ecology — and statistics never produce
        /// something that looks *built*. A wonder does, because it is authored: the catalog says what it is, the
        /// shared form machine says how it is carved, and the site search says where the map is willing to carry it.
        /// </summary>
        private IEnumerable<MapSimulationBuildStep> composeWonders()
        {
            Rng rng = stream("wonders");
            double exactWanted = ((double)count / 4096) * roll.wonderDensity * rng.range(0.8, 1.3);
            double wholeWonders = Math.floor(exactWanted);
            // Stochastic rounding keeps small maps authored over a seed set without imposing a fake per-map minimum.
            double wanted = Math.min(
                9,
                wholeWonders + (rng.fork("count").@bool(exactWanted - wholeWonders) ? 1 : 0));
            var placed = new List<PlacedMapWonder>();
            var decorations = new List<TerrainDecorationPlacement>();
            IReadOnlyList<MapWonderDef> pool = MapSetPieces.mapWondersForChaos(chaos).filter((def) => !excludedWonders.Contains(def.key) && wonderFitsDials(def));
            if (wanted > 0 && pool.Count > 0)
            {
                var affinity = new HashSet<string>(archetype.wonderAffinity);
                short[] open = openDistanceField();
                List<TerrainCrossing> spans = crossingCandidates()
                    .filter((candidate) => candidate.length >= 3)
                    .sort((a, b) => b.length - a.length);
                WonderCanvas canvas = wonderCanvas();
                var used = new HashSet<string>();
                // Proximity is answered from precomputed masks, not by scanning a neighbourhood per candidate cell:
                // the site search visits every cell once per site kind per wonder, and a 7x7 probe inside that loop is
                // a hundred million operations on a continent.
                var near = new WonderProximity
                {
                    water = dilateTile(TileType.Water, 3),
                    rift = dilateTile(TileType.Chasm, 3),
                    solid = dilateTile(TileType.Solid, 2),
                };

                for (int i = 0; i < wanted; i++)
                {
                    List<double> weights = pool.map(
                        (def) => def.weight * (affinity.Contains(def.key) ? 2.6 : 1) * (used.Contains(def.key) ? 0.12 : 1));
                    MapWonderDef def = rng.weighted(pool, weights);
                    WonderSite? site = findWonderSite(def, open, near, spans, placed, rng);
                    if (site == null) continue;
                    WonderStamp stamp = MapSetPieceStamps.stampMapWonder(canvas, def, site, rng.fork($"wonder:{def.key}:{i}"));
                    if (!stamp.built) continue;
                    used.Add(def.key);
                    decorations.AddRange(stamp.decorations);
                    placed.Add(new PlacedMapWonder
                    {
                        key = def.key,
                        name = def.name,
                        form = def.form,
                        tx = site.tx,
                        ty = site.ty,
                        radius = site.radius,
                    });
                    yield return Step($"Raising {def.name}", 2);
                }
            }

            // A stamp may have walled a pocket off or dropped a deck's span out from under it; the world is repaired
            // once, after all of them, so one wonder never has to know what another did.
            repairBridges();
            dissolveStrandedLand();
            resolveWaterElevation();
            TerrainKit.assignChasmDepths(tiles, elevation, width, height);
            for (int i = 0; i < count; i++)
            {
                if (isWalkable(tiles[i])) ground[i] = Js.U8(elevation[i]);
            }
            wonders.AddRange(placed);
            this.decorations.AddRange(decorations);
            PlacedMapWonder? anchor = placed.Count > 0 ? placed[0] : null;
            recordStage(
                MapSimulationLayer.Wonders,
                sweepPlan(
                    MapRevealSweep.Radial,
                    anchor != null ? anchor.tx : heartTx,
                    anchor != null ? anchor.ty : heartTy,
                    1,
                    0,
                    0.45),
                decorations,
                placed);
            yield return Step("Settling the monuments", 2);
        }

        /// <summary>
        /// STUDIO: a wonder made of something a dial turned to zero stays out: no pools or drowned craters without
        /// water, no sinkholes without chasms, and on a flat world only the wonders built at ground level.
        /// </summary>
        private bool wonderFitsDials(MapWonderDef def)
        {
            if (waterScale <= 0 && (def.core == MapWonderCore.Water || def.form == MapWonderForm.Springs)) return false;
            if (riftScale <= 0 && (def.core == MapWonderCore.Rift || def.form == MapWonderForm.Sinkhole)) return false;
            return !flatWorld || FLAT_WONDER_FORMS.Contains(def.form);
        }

        /// <summary>The guarded view of the working world the stamp machine writes through.</summary>
        private WonderCanvas wonderCanvas()
        {
            return new WonderCanvas
            {
                width = width,
                height = height,
                tiles = tiles,
                elevation = elevation,
                ground = ground,
                protect = protect,
                claim = claim,
                maxLevel = SIM_MAX_LEVEL,
                seed = seedNum,
                canWrite = (tx, ty) =>
                {
                    if (!isInset(tx, ty, RIM_CELLS + 1)) return false;
                    return !isHeartward(tileIndex(width, tx, ty));
                },
            };
        }

        /// <summary>Where the map is willing to carry this monument, best site kind first.</summary>
        private WonderSite? findWonderSite(
            MapWonderDef def,
            short[] open,
            WonderProximity near,
            IReadOnlyList<TerrainCrossing> spans,
            IReadOnlyList<PlacedMapWonder> placed,
            Rng rng)
        {
            int minSpan = Math.min(width, height);
            double radius = Math.max(3, Math.round(minSpan * rng.range(def.radius.Item1, def.radius.Item2)));
            uint salt = Js.ToUint32(seedInt ^ 0x6b1f2a35);

            foreach (string kind in def.sites)
            {
                if (kind == MapWonderSite.Span)
                {
                    foreach (TerrainCrossing candidate in spans)
                    {
                        double mid = candidate.start + candidate.length * 0.5;
                        int tx = (int)Math.round(candidate.axis == 'x' ? mid : candidate.lane);
                        int ty = (int)Math.round(candidate.axis == 'x' ? candidate.lane : mid);
                        if (!isInset(tx, ty, 3)) continue;
                        if (overlapsPlaced(tx, ty, radius, placed)) continue;
                        return new WonderSite
                        {
                            tx = tx,
                            ty = ty,
                            radius = radius,
                            dx = candidate.axis == 'x' ? 1 : 0,
                            dy = candidate.axis == 'x' ? 0 : 1,
                            span = Math.ceil(candidate.length * 0.5) + 2,
                        };
                    }
                    continue;
                }

                List<int> anchors = topCells((tx, ty, index) =>
                {
                    if (tiles[index] != TileType.Floor) return -1;
                    if (!isInset(tx, ty, radius + 2)) return -1;
                    if (claim[index] != 0 || isHeartward(index)) return -1;
                    double clearance = open[index];
                    double high = elevation[index] / (double)SIM_MAX_LEVEL;
                    double score;
                    switch (kind)
                    {
                        case MapWonderSite.Plain:
                            if (clearance < radius * 0.72) return -1;
                            score = clearance * 0.09;
                            break;
                        case MapWonderSite.High:
                            if (clearance < radius * 0.42) return -1;
                            score = high * 2.4 + clearance * 0.035;
                            break;
                        case MapWonderSite.Low:
                            if (clearance < radius * 0.42) return -1;
                            score = (1 - high) * 2.4 + clearance * 0.035;
                            break;
                        case MapWonderSite.Shore:
                            if (near.water[index] == 0) return -1;
                            score = 1.6 + clearance * 0.05;
                            break;
                        case MapWonderSite.RiftEdge:
                            if (near.rift[index] == 0) return -1;
                            score = 1.6 + clearance * 0.05;
                            break;
                        case MapWonderSite.Wallfoot:
                            if (near.solid[index] == 0) return -1;
                            score = 1.3 + clearance * 0.06;
                            break;
                        default:
                            return -1;
                    }
                    return score + latticeHash(salt, tx, ty) * 0.7;
                }, 48);

                foreach (int index in anchors)
                {
                    int tx = index % width;
                    int ty = index / width;
                    if (overlapsPlaced(tx, ty, radius, placed)) continue;
                    double angle = latticeHash((int)salt ^ 0x27d4, tx, ty) * Math.PI * 2;
                    return new WonderSite { tx = tx, ty = ty, radius = radius, dx = Math.cos(angle), dy = Math.sin(angle), span = radius };
                }
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------
        // Roads

        /// <summary>
        /// Lay the circulation network: plazas at the places that matter, avenues between them.
        ///
        /// Roads do real structural work here: an avenue levels its own lane one step at a time, so it climbs the
        /// terraces the relief pass built and the escarpments answer with cuttings on either side — exactly the way a
        /// mountain road reads from above.
        /// </summary>
        private void composeRoads()
        {
            Rng rng = stream("roads");
            int plazaRadius = (int)Math.max(2, Math.round(Math.min(width, height) * 0.035));
            var nodes = new List<RoadNode>
            {
                new RoadNode { tx = heartTx, ty = heartTy, plaza = plazaRadius + 1, kind = "heart" },
            };
            foreach (PlacedMapWonder wonder in wonders)
            {
                nodes.Add(new RoadNode { tx = wonder.tx, ty = wonder.ty, plaza = 0, kind = "wonder" });
            }
            if (roll.roadDensity > 0.05) foreach (RoadNode gate in pickGates(rng)) nodes.Add(gate);

            if (roll.roadDensity <= 0.05 || nodes.Count < 2)
            {
                recordStage(MapSimulationLayer.Roads, sweepPlan(MapRevealSweep.Radial, heartTx, heartTy, 1, 0, 0.25));
                return;
            }

            foreach (RoadNode node in nodes) if (node.plaza > 0) stampPlaza(node);

            // A minimum spanning tree is the honest skeleton: every place reachable, no avenue that exists only
            // because the roll said so. The extra edges on top are what turn a tree into a network you can loop.
            List<(int, int)> edges = MapSimulationRoads.minimumSpanningEdges(nodes);
            int extras = clampInt(nodes.Count * (roll.roadDensity * 0.12 + chaos * 0.22), 0, 6);
            foreach ((int, int) extra in MapSimulationRoads.extraLoopEdges(nodes, edges, extras)) edges.Add(extra);

            int laneHalf = roll.roadDensity > 2.2 ? 1 : 0;
            var terrain = new RoadTerrain
            {
                width = width,
                height = height,
                tiles = tiles,
                elevation = elevation,
                road = road,
                claim = claim,
                border = RIM_CELLS,
            };
            foreach (var (a, b) in edges)
            {
                int from = tileIndex(width, nodes[a].tx, nodes[a].ty);
                int to = tileIndex(width, nodes[b].tx, nodes[b].ty);
                uint[]? path = MapSimulationRoads.routeRoad(terrain, from, to);
                if (path != null) paintRoad(path, laneHalf);
            }

            // The lane is level; the ground beside it is not. Letting the terrain contract answer that difference is
            // what gives an avenue its cutting instead of an invisible ledge.
            TerrainKit.materializeUnclimbableWalkableEdges(tiles, elevation, width, height, new MaterializeUnclimbableEdgesOptions
            {
                protect = (_tx, _ty, index) => protect[index] == 1 || isHeartward(index),
            });
            repairBridges();
            dissolveStrandedLand();
            resolveWaterElevation();
            TerrainKit.assignChasmDepths(tiles, elevation, width, height);
            for (int i = 0; i < count; i++)
            {
                if (isWalkable(tiles[i])) ground[i] = Js.U8(elevation[i]);
            }
            recordStage(MapSimulationLayer.Roads, sweepPlan(MapRevealSweep.Radial, heartTx, heartTy, 1, 0, 0.25));
        }

        private static readonly (int, int)[] GATE_SIDES =
        {
            (0, -1),
            (1, 0),
            (0, 1),
            (-1, 0),
        };

        /// <summary>Walkable ground close to each open side of the frame — the roads have to come from somewhere.</summary>
        private List<RoadNode> pickGates(Rng rng)
        {
            var @out = new List<RoadNode>();
            int wanted = clampInt(1 + roll.roadDensity * 0.9 + chaos, 1, 4);
            List<(int, int)> sides = rng.shuffle(GATE_SIDES);
            foreach (var (dx, dy) in sides)
            {
                if (@out.Count >= wanted) break;
                double cx = Math.round((width - 1) * 0.5 + dx * (width - 1) * 0.42);
                double cy = Math.round((height - 1) * 0.5 + dy * (height - 1) * 0.42);
                int best = -1;
                double bestDistance = double.PositiveInfinity;
                for (int ty = RIM_CELLS + 1; ty < height - RIM_CELLS - 1; ty++)
                {
                    for (int tx = RIM_CELLS + 1; tx < width - RIM_CELLS - 1; tx++)
                    {
                        int index = tileIndex(width, tx, ty);
                        if (tiles[index] != TileType.Floor) continue;
                        double ddx = tx - cx;
                        double ddy = ty - cy;
                        double distance = ddx * ddx + ddy * ddy;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = index;
                        }
                    }
                }
                if (best < 0) continue;
                @out.Add(new RoadNode { tx = best % width, ty = best / width, plaza = 0, kind = "gate" });
            }
            return @out;
        }

        /// <summary>A level disc of open ground: the one shape that reads as "people gather here".</summary>
        private void stampPlaza(RoadNode node)
        {
            sbyte level = elevation[tileIndex(width, node.tx, node.ty)];
            int radius = node.plaza;
            for (int dy = -radius - 1; dy <= radius + 1; dy++)
            {
                for (int dx = -radius - 1; dx <= radius + 1; dx++)
                {
                    double wobble = (latticeHash(seedInt ^ 0x5f2c, node.tx + dx, node.ty + dy) - 0.5) * 0.9;
                    if (dx * dx + dy * dy > (radius + wobble) * (radius + wobble)) continue;
                    int tx = node.tx + dx;
                    int ty = node.ty + dy;
                    if (!isInset(tx, ty, RIM_CELLS + 1)) continue;
                    int index = tileIndex(width, tx, ty);
                    int tile = tiles[index];
                    if (tile == TileType.Bridge) continue;
                    tiles[index] = TileType.Floor;
                    elevation[index] = level;
                    road[index] = 1;
                    protect[index] = 1;
                }
            }
        }

        /// <summary>
        /// Paint an avenue along a routed path.
        ///
        /// The lane's level moves at most one step per cell whatever the ground under it does — that single rule is
        /// both what makes the road legal and what makes it read as engineered, because the surrounding land keeps
        /// its own shape and the difference becomes a cutting or an embankment.
        /// </summary>
        private void paintRoad(uint[] path, int extraHalf)
        {
            if (path.Length == 0) return;
            int level = elevation[path[0]];
            for (int i = 0; i < path.Length; i++)
            {
                int index = (int)path[i];
                int tx = index % width;
                int ty = index / width;
                int target = ground[index];
                if (level < target) level++;
                else if (level > target) level--;
                int previous = (int)path[Math.max(0, i - 1)];
                int px = index % width == previous % width ? 1 : 0;
                int py = px == 1 ? 0 : 1;
                int half = 1 + extraHalf;
                for (int w = -half; w <= half; w++)
                {
                    int cx = tx + px * w;
                    int cy = ty + py * w;
                    if (!isInset(cx, cy, RIM_CELLS + 1)) continue;
                    int cell = tileIndex(width, cx, cy);
                    int tile = tiles[cell];
                    if (tile == TileType.Water || tile == TileType.Chasm) continue;
                    if (tile != TileType.Bridge) tiles[cell] = TileType.Floor;
                    elevation[cell] = Js.I8(TerrainKit.clampElevationLevel(level, SIM_MAX_LEVEL));
                    road[cell] = 1;
                    protect[cell] = 1;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------
        // Theming

        /// <summary>
        /// Partition the map into the recipe's theme regions: weighted nearest anchor in domain-warped space, so
        /// borders are organic bands rather than Voronoi seams. Region 0 inherits the artifact's base biome, which
        /// keeps the layer compact and lets single-theme maps stay entirely implicit.
        /// </summary>
        private void composeTheming()
        {
            int regions = themeKeys.Count;
            if (regions <= 1)
            {
                recordStage(MapSimulationLayer.Theming, sweepPlan(MapRevealSweep.Linear, 0, 0, 1, 0, 0.3));
                return;
            }
            var anchors = new List<ThemeAnchor>();
            Rng rng = stream("themes");
            for (int i = 0; i < regions; i++)
            {
                double angle = ((i + rng.next() * 0.6) / regions) * Math.PI * 2;
                double radius = rng.range(0.28, 0.46);
                double x = width * (0.5 + Math.cos(angle) * radius);
                double y = height * (0.5 + Math.sin(angle) * radius);
                anchors.Add(new ThemeAnchor { x = x, y = y, reach = rng.range(0.86, 1.18) });
            }
            uint warpSeed = Js.ToUint32(seedInt ^ 0x6c8e9cf5);
            double warpScale = Math.max(9, Math.min(width, height) * 0.22);
            double warpAmount = Math.max(3, Math.min(width, height) * 0.07);
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    double wx = tx + (valueNoise(warpSeed, tx, ty, warpScale) - 0.5) * 2 * warpAmount;
                    double wy =
                        ty + (valueNoise((int)warpSeed ^ 0x2f6e2b1d, tx, ty, warpScale) - 0.5) * 2 * warpAmount;
                    int best = 0;
                    double bestScore = double.PositiveInfinity;
                    for (int i = 0; i < anchors.Count; i++)
                    {
                        ThemeAnchor anchor = anchors[i];
                        double score = Math.hypot(wx - anchor.x, wy - anchor.y) / anchor.reach;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = i;
                        }
                    }
                    themeIndex[tileIndex(width, tx, ty)] = best == 0 ? (byte)TERRAIN_THEME_INHERIT : Js.U8(best);
                }
            }
            recordStage(
                MapSimulationLayer.Theming,
                sweepPlan(MapRevealSweep.Radial, Math.round(anchors[0].x), Math.round(anchors[0].y), 1, 0, 0.5));
        }

        // ---------------------------------------------------------------------------------------------------
        // Flora

        /// <summary>
        /// Seed the world's ecology.
        ///
        /// Density follows a continuous grove field so canopy clusters and clearings stay open, and every real edge
        /// in the world recruits the growth that belongs to it: reeds on a shoreline, deadfall at a cliff foot,
        /// crystal along a rift lip, a tree line down an avenue. Roads, plazas and wonder floors stay clear — the
        /// quiet ground is what makes the busy ground read.
        /// </summary>
        private void composeFlora()
        {
            int floor = 0;
            for (int i = 0; i < count; i++) if (tiles[i] == TileType.Floor) floor++;
            double target = Math.round(floor * roll.floraDensity);
            uint ecologySeed = Js.ToUint32(seedInt ^ 0x27d4eb2f);
            double groveScale = Math.max(6, Math.min(width, height) * 0.16);

            var candidates = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != TileType.Floor) continue;
                if (road[i] != 0 || claim[i] != 0) continue;
                candidates.Add(i);
            }
            List<int> chosen = selectByField(candidates, target, 0x51ab3e75, (tx, ty, _index) =>
            {
                double grove = valueNoise(ecologySeed, tx, ty, groveScale);
                double detail = valueNoise((int)ecologySeed ^ unchecked((int)0x85ebca6b), tx, ty, 5);
                // A verge is a real edge too: growth crowds an avenue instead of ignoring it.
                double verge = hasMaskWithin(road, tx, ty, 2) ? 0.55 : 0;
                return grove * grove * 2.2 + detail * 0.5 + edgeAffinity(tx, ty) * 0.85 + verge;
            });

            Rng rng = stream("flora");
            var flora = new List<TerrainDecorationPlacement>();
            foreach (int index in chosen)
            {
                int tx = index % width;
                int ty = index / width;
                flora.Add(new TerrainDecorationPlacement
                {
                    kind = floraKindAt(tx, ty, index, rng, ecologySeed, groveScale),
                    tx = tx,
                    ty = ty,
                    seed = placementSeed(seedNum, tx, ty, 0x7f3a),
                    themeKey = themeKeyAt(index),
                });
            }
            sortPlacementsForReveal(flora, (d) => d.tx, (d) => d.ty);
            decorations.AddRange(flora);
            recordStage(
                MapSimulationLayer.Flora,
                sweepPlan(MapRevealSweep.Radial, heartTx, heartTy, 1, 0, 0.85),
                flora);
        }

        private static readonly string[] UNDERGROWTH_KINDS = { TerrainDecorationKind.Thicket, TerrainDecorationKind.Stump };
        private static readonly double[] UNDERGROWTH_WEIGHTS = { 1.5, 1 };

        /// <summary>Which growth belongs on a cell — the edge it stands on decides before the dice do.</summary>
        private string floraKindAt(
            int tx,
            int ty,
            int index,
            Rng rng,
            double ecologySeed,
            double groveScale)
        {
            bool nearRift = neighbourHas(tx, ty, TileType.Chasm);
            bool nearWater = neighbourHas(tx, ty, TileType.Water);
            bool onVerge = hasMaskWithin(road, tx, ty, 2);
            if (nearRift && rng.@bool(0.62)) return TerrainDecorationKind.Thicket;
            if (nearWater && rng.@bool(0.66)) return TerrainDecorationKind.Thicket;
            // A cliff foot collects what falls off the cliff.
            if (isCliffFoot(tx, ty) && rng.@bool(0.55)) return TerrainDecorationKind.Stump;
            // Avenue verges are planted, not overgrown.
            if (onVerge && rng.@bool(0.6)) return TerrainDecorationKind.Tree;
            double grove = valueNoise(ecologySeed, tx, ty, groveScale);
            if (rng.next() < roll.canopyShare * (0.55 + grove)) return TerrainDecorationKind.Tree;
            // High ground is above the comfortable treeline: scrub, and the stumps of what was cut for it.
            double high = elevation[index] / (double)SIM_MAX_LEVEL;
            if (high > 0.66 && rng.@bool(0.45)) return TerrainDecorationKind.Stump;
            return rng.weighted(UNDERGROWTH_KINDS, UNDERGROWTH_WEIGHTS);
        }

        /// <summary>True when a walkable cell stands directly under rock that rises above it.</summary>
        private bool isCliffFoot(int tx, int ty)
        {
            int here = elevation[tileIndex(width, tx, ty)];
            foreach (var (dx, dy) in NEIGHBOURS4)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int index = tileIndex(width, nx, ny);
                if (tiles[index] != TileType.Solid) continue;
                if (elevation[index] >= here + 2) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------------
        // Landmarks

        /// <summary>
        /// The composition's punctuation: one solitary crown on each of its significant points.
        ///
        /// This used to raise monuments — a standing stone, a ruined arch, a wayshrine. Nobody in this world built
        /// any of them, so the beat now plants what a landscape actually marks its high places with: a single tree,
        /// spaced far enough from the next that it reads as deliberate rather than as forest.
        /// </summary>
        private void composeLandmarks()
        {
            Rng rng = stream("landmarks");
            int wanted = clampInt(
                ((double)count / 4096) * roll.landmarkDensity * rng.range(0.8, 1.2),
                0,
                24);
            var occupied = new byte[count];
            foreach (TerrainDecorationPlacement decoration in decorations)
                occupied[tileIndex(width, decoration.tx, decoration.ty)] = 1;

            uint salt = Js.ToUint32(seedInt ^ 0x133111eb);
            List<int> anchors = topCells((tx, ty, index) =>
            {
                if (tiles[index] != TileType.Floor || occupied[index] != 0) return -1;
                if (claim[index] != 0 || road[index] != 0) return -1;
                if (!isInset(tx, ty, 4)) return -1;
                if (!hasLevelPlaza(tx, ty, 1)) return -1;
                double high = elevation[index] / (double)SIM_MAX_LEVEL;
                return high * 1.1 + edgeAffinity(tx, ty) * 0.5 + valueNoise(salt, tx, ty, 31);
            }, wanted * 8);

            var landmarks = new List<TerrainDecorationPlacement>();
            double spacing = Math.max(5, Math.round(Math.min(width, height) * 0.12));
            foreach (int index in anchors)
            {
                if (landmarks.Count >= wanted) break;
                int tx = index % width;
                int ty = index / width;
                if (
                    landmarks.some(
                        (placed) => Math.abs(placed.tx - tx) < spacing && Math.abs(placed.ty - ty) < spacing)
                )
                    continue;
                landmarks.Add(new TerrainDecorationPlacement
                {
                    kind = TerrainDecorationKind.Tree,
                    tx = tx,
                    ty = ty,
                    seed = placementSeed(seedNum, tx, ty, 0x11c3),
                    themeKey = themeKeyAt(index),
                });
            }
            sortPlacementsForReveal(landmarks, (d) => d.tx, (d) => d.ty);
            decorations.AddRange(landmarks);
            recordStage(
                MapSimulationLayer.Landmarks,
                sweepPlan(MapRevealSweep.Scatter, heartTx, heartTy, 1, 0, 0.1),
                landmarks);
        }
    }
}
