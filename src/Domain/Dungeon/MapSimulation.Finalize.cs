// Port of packages/shared/src/domain/dungeon/mapSimulation.ts (finalize, repairs, shared composition helpers) —
// keep in lockstep with the original. See MapSimulation.cs for the file split.
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
    private sealed class ScoredCell
    {
        public int index;
        public double score;
    }

    private sealed partial class MapComposer
    {
        // ---------------------------------------------------------------------------------------------------
        // Finalize

        /// <summary>
        /// Growth cannot outlive the ground it stood on.
        ///
        /// Every beat places its dressing on ground that was legal at the time, but settling the bridges and the
        /// terrain contract and the validation repair loop below still carve terrain afterwards. This asks the shared habitat rule
        /// once more against the final tiles and takes the recorded build stages with it, so a replay of the build
        /// lands on exactly the world that was exported instead of planting a tree in a freshly opened rift.
        /// </summary>
        private bool pruneDecorationsAgainstTerrain()
        {
            // Identity set, as the original's `Set<TerrainDecorationPlacement>`.
            var dropped = new HashSet<TerrainDecorationPlacement>(ReferenceEqualityComparer.Instance);
            foreach (TerrainDecorationPlacement decoration in decorations)
            {
                int index = tileIndex(width, decoration.tx, decoration.ty);
                if (!WorldDecoration.terrainTileAcceptsDecoration(decoration.kind, tiles[index]))
                    dropped.Add(decoration);
            }
            if (dropped.Count == 0) return false;
            List<TerrainDecorationPlacement> kept = decorations.filter((decoration) => !dropped.Contains(decoration));
            decorations.Clear();
            decorations.AddRange(kept);
            for (int i = 0; i < stages.Count; i++)
            {
                MapSimulationStage stage = stages[i];
                if (stage.decorations == null || !stage.decorations.some((decoration) => dropped.Contains(decoration))) continue;
                MapSimulationStage next = stage.Clone();
                next.decorations = stage.decorations.filter((decoration) => !dropped.Contains(decoration));
                stages[i] = next;
            }
            return true;
        }

        /// <summary>
        /// Cut the world's clefts and hang its suspension bridges.
        ///
        /// This is the very same promotion pass streamed Endless terrain uses — a composed map has no business
        /// growing a second implementation of the two depth-tile contracts. It runs *after* the terrain contract has
        /// settled because both roles are proven against final elevations: a bank raised or a shelf terraced later
        /// would silently turn a legal deck into a `underpass_structure` error.
        ///
        /// Neither promotion changes reachability. Underpass replaces Floor and stays walkable, Cleft replaces Solid
        /// and stays body-blocking, so the world's connectivity is exactly what the passes before it composed.
        /// </summary>
        private void composeDepthTiles()
        {
            // Endless promotes per streamed 64-cell chunk; one composed map is a single sweep over the whole world and
            // would otherwise hang a bridge over every corridor of a dense grid. Scaling the biome's own density keeps
            // both roles inside the shared ≤2% footprint budget and widens their spacing, without touching a rule.
            string? baseTheme = themeKeys.Count > 0 ? themeKeys[0] : null;
            TerrainDepthTileProfile biome = TerrainDepthTiles.terrainDepthTileProfileFor(baseTheme);
            TerrainDepthTiles.applyTerrainDepthTiles(
                tiles,
                elevation,
                width,
                height,
                0,
                0,
                seedNum,
                baseTheme,
                new TerrainDepthTileOptions
                {
                    profile = new TerrainDepthTileProfile
                    {
                        clefts = biome.clefts * MAP_DEPTH_TILE_DENSITY,
                        underpasses = biome.underpasses * MAP_DEPTH_TILE_DENSITY,
                    },
                    maxFeatureShare = TerrainDepthTiles.TERRAIN_DEPTH_TILE_BUDGET_SHARE,
                });
        }

        /// <summary>
        /// Write the artifact's surface and variant layers.
        ///
        /// Composed once **per theme in the palette** and merged through `themeIndex`, rather than once for
        /// `themeKeys[0]`. A blended map is several countries sharing a raster, and each of them owns its own
        /// substrate recipe: composing the whole map from the first theme's registry would give a sakura valley the
        /// ground of the highland pass it happens to border. The palette is one or a handful of entries, so the
        /// repeat costs a few passes over the raster and nothing else.
        /// </summary>
        private void composeSurfaceLayers(TerrainArtifact artifact)
        {
            byte[]? usage = artifact.floorUsage;
            var surface = new byte[count];
            var variant = new byte[count];
            for (int theme = 0; theme < themeKeys.Count; theme++)
            {
                TerrainSurfaceLayers layers = TerrainSurfaceLayersModule.composeTerrainSurfaceLayers(
                    tiles,
                    width,
                    height,
                    themeKeys[theme],
                    0,
                    0,
                    new TerrainSurfaceLayerMasks { usage = usage, route = road, setPieceClaim = claim });
                for (int index = 0; index < count; index++)
                {
                    // `TERRAIN_THEME_INHERIT` is how a cell says "the layout's own biome", which is palette entry 0 —
                    // the same resolution `terrainLayoutThemeKeyAt` performs. Comparing the raw byte to the loop index
                    // instead matches nothing on an unblended map, because every cell of one is the inherit sentinel.
                    int cell = themeIndex[index];
                    int resolved = cell == TERRAIN_THEME_INHERIT ? 0 : cell;
                    if (resolved != theme) continue;
                    surface[index] = layers.surface[index];
                    variant[index] = layers.variant[index];
                }
            }
            artifact.surface = surface;
            artifact.variant = variant;
        }

        private List<TerrainDecorationPlacement> cloneDecorations() => decorations.map((decoration) => decoration.Clone());

        /// <summary>Assemble the artifact, then compile it and repair anything the composition left illegal.</summary>
        private SimulatedMap finalize()
        {
            // Last word on the terrain contract, after every beat has had its say. Bridges are settled here because
            // they are the one structure five different passes can each narrow by one cell.
            enforceDialZeros();
            settleTerrainContract();
            composeDepthTiles();
            pruneDecorationsAgainstTerrain();
            TerrainArtifact artifact = TerrainEditor.createTerrainArtifact(new CreateTerrainArtifactOptions
            {
                width = width,
                height = height,
                biomeKey = themeKeys.Count > 0 ? themeKeys[0] : null,
                seed = seedNum,
                withDefaultMarkers = false,
            });
            artifact.schemaVersion = TERRAIN_ARTIFACT_SCHEMA_VERSION;
            artifact.tier = archetype.tier;
            artifact.themePalette = new List<string>(themeKeys);
            artifact.themeIndex = (byte[])themeIndex.Clone();
            // THE GROUND THE MAP IS MADE OF.
            //
            // A simulated map used to ship `surface = Auto` for every tile and a bare 0/255 road stencil, so none of
            // the substrate registry's materials — and therefore none of its grass — ever appeared on it, and its
            // roads had no worn verge. Both layers come from the same composer the streamed world uses; see
            // TerrainSurfaceLayers.
            artifact.floorUsage = TerrainSurfaceLayersModule.gradeRouteUsage(road, width, height);
            artifact.decorations = cloneDecorations();

            TerrainValidationResult? validation = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Array.Copy(tiles, artifact.baseTiles, count);
                Array.Copy(elevation, artifact.elevation!, count);
                // Material follows the raster, so it is recomposed on every attempt: a repair that turns a floor cell
                // into rock must also stop calling that cell sward, or the artifact ships a surface layer describing
                // ground the tile layer no longer has.
                composeSurfaceLayers(artifact);
                // A repair from the previous attempt may have carved ground out from under a prop.
                if (pruneDecorationsAgainstTerrain())
                    artifact.decorations = cloneDecorations();
                TerrainCompileResult compiled = TerrainArtifactModule.compileTerrainArtifactToDungeonLayout(
                    artifact,
                    TerrainArtifactModule.TERRAIN_EDITOR_VALIDATION_OPTIONS);
                validation = compiled.validation;
                if (compiled.validation.ok) break;
                if (!repairFrom(compiled.validation)) break;
            }
            Array.Copy(tiles, artifact.baseTiles, count);
            Array.Copy(elevation, artifact.elevation!, count);
            composeSurfaceLayers(artifact);

            // The final layer snapshot must be exactly what the artifact holds, so playback lands on the real world.
            // The repair pass above runs after the last layer was recorded, so anything it corrected belongs to that
            // layer too — otherwise those cells exist in the artifact but in no layer, and a replay of the build ends
            // on a world that is subtly not the one that was exported.
            MapSimulationStage? last = stages.Count > 0 ? stages[stages.Count - 1] : null;
            if (last != null)
            {
                // `new Set(last.cells)` then `cells.add(i)`: insertion order, first occurrence wins.
                var member = new byte[count];
                var cells = new List<uint>(last.cells.Length);
                foreach (uint cell in last.cells)
                {
                    if (member[cell] != 0) continue;
                    member[cell] = 1;
                    cells.Add(cell);
                }
                for (int i = 0; i < count; i++)
                {
                    if (
                        tiles[i] != previousTiles[i] ||
                        elevation[i] != previousElevation[i]
                    )
                    {
                        if (member[i] != 0) continue;
                        member[i] = 1;
                        cells.Add((uint)i);
                    }
                }
                MapSimulationStage next = last.Clone();
                next.cells = cells.ToArray();
                next.tiles = (byte[])tiles.Clone();
                next.elevation = (sbyte[])elevation.Clone();
                stages[stages.Count - 1] = next;
            }

            applyBeatTiming();
            SimulatedMapMetrics metrics = this.metrics();
            string baseName = themeKeys.Count > 0 ? themeKeys[0] : "world";
            return new SimulatedMap
            {
                recipe = new SimulatedMapRecipe
                {
                    seedId = seedId,
                    archetype = archetype,
                    motif = motifApplied,
                    themeKeys = new List<string>(themeKeys),
                    width = width,
                    height = height,
                    // A world that got explicit bones is named after them: "Noir Sprawl · Sunken Caldera" says far more
                    // about what you are looking at than the archetype alone ever could.
                    name = $"{titleCase(baseName)} · {motifApplied?.name ?? archetype.name}",
                    heartTx = heartTx,
                    heartTy = heartTy,
                    chaos = chaos,
                    frame = roll.frame,
                    wonders = new List<PlacedMapWonder>(wonders),
                    runtimeSeconds = MAP_SIMULATION_RUNTIME_SECONDS,
                },
                artifact = artifact,
                validation = validation ?? new TerrainValidationResult { ok = true, issues = new List<TerrainValidationIssue>() },
                stages = stages,
                baseTiles = baseTiles,
                baseElevation = baseElevation,
                metrics = metrics,
            };
        }

        /// <summary>
        /// Spend the cinematic's authored runtime on the layers in proportion to what each one actually did.
        ///
        /// A fixed per-layer beat is the reason build animations feel cheap: the theming beat of a single-theme map
        /// changes nothing at all and still held the screen for a second and a half, while a twelve-thousand-cell
        /// bedrock pass got barely more. Work is raised to a fractional power so a huge layer does not swallow the
        /// film, multiplied by the layer's authored dramatic share, and a layer that did nothing gets exactly zero —
        /// playback skips it outright instead of pretending.
        /// </summary>
        private void applyBeatTiming()
        {
            List<double> work = stages.map(
                (stage) =>
                    stage.cells.Length +
                    ((stage.decorations?.Count ?? 0) +
                        (stage.wonders?.Count ?? 0) * 40) *
                        PLACEMENT_WORK_WEIGHT);
            var active = new List<int>();
            for (int i = 0; i < work.Count; i++) if (work[i] > 0) active.Add(i);
            if (active.Count == 0) return;
            List<double> shaped = active.map(
                (i) => Math.pow(work[i], 0.55) * STAGE_COPY[stages[i].layer].share);
            double totalShaped = shaped.reduce((sum, value) => sum + value, 0.0);
            if (!Js.Truthy(totalShaped)) totalShaped = 1;
            double spare = Math.max(0, MAP_SIMULATION_RUNTIME_SECONDS - active.Count * MIN_BEAT_SECONDS);
            var seconds = new double[stages.Count];
            for (int k = 0; k < active.Count; k++)
            {
                seconds[active[k]] = MIN_BEAT_SECONDS + (spare * shaped[k]) / totalShaped;
            }
            for (int i = 0; i < stages.Count; i++)
            {
                MapSimulationStage next = stages[i].Clone();
                next.beatSeconds = seconds[i];
                stages[i] = next;
            }
        }

        /// <summary>Targeted repairs for the validation codes a composition can realistically produce.</summary>
        private bool repairFrom(TerrainValidationResult validation)
        {
            bool repaired = false;
            foreach (TerrainValidationIssue issue in validation.issues)
            {
                if (issue.severity != "error") continue;
                int? index =
                    issue.index ??
                    (issue.tx != null && issue.ty != null
                        ? tileIndex(width, issue.tx.Value, issue.ty.Value)
                        : null);
                if (index == null || index.Value < 0 || index.Value >= count) continue;
                switch (issue.code)
                {
                    case "bridge_stray":
                    case "bridge_span":
                        // A span verdict is about the whole deck, not one cell of it: demoting a single cell just splits
                        // the component and re-reports next attempt. Take the crossing back to ground in one move.
                        demoteBridgeComponentAt(index.Value);
                        repaired = true;
                        break;
                    case "bridge_width":
                        tiles[index.Value] = TileType.Floor;
                        repaired = true;
                        break;
                    case "cleft_structure":
                    case "overhead_beam":
                        tiles[index.Value] = TileType.Solid;
                        repaired = true;
                        break;
                    case "underpass_structure":
                        // An Underpass is a promoted Floor cell, so its repair is a demotion back to Floor. Walling it off
                        // instead would take a legal walkable passage out of the world and can disconnect a whole region.
                        tiles[index.Value] = TileType.Floor;
                        repaired = true;
                        break;
                    case "height_step":
                    case "water_stored_elevation":
                    case "chasm_stored_depth":
                        repaired = true;
                        break;
                    default:
                        break;
                }
            }
            if (repaired) settleTerrainContract();
            return repaired;
        }

        /// <summary>Take the whole deck that owns this cell back to ordinary ground.</summary>
        private void demoteBridgeComponentAt(int index)
        {
            if (tiles[index] != TileType.Bridge) return;
            var stack = new List<int> { index };
            tiles[index] = TileType.Floor;
            while (stack.Count > 0)
            {
                int at = stack.pop();
                int tx = at % width;
                int ty = at / width;
                foreach (var (dx, dy) in NEIGHBOURS4)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int ni = tileIndex(width, nx, ny);
                    if (tiles[ni] != TileType.Bridge) continue;
                    tiles[ni] = TileType.Floor;
                    stack.Add(ni);
                }
            }
        }

        /// <summary>
        /// Bring the whole world back onto the terrain contract without flattening it.
        ///
        /// The obvious repair — relax every walkable step to one level — is exactly wrong here: it would erase the
        /// terraces the relief pass exists to build. Materializing the illegal edges as rock keeps the shelves and
        /// turns their faces into what they physically are, and the protected set (ramps, avenues, plazas, decks)
        /// guarantees the routes through them survive.
        /// </summary>
        private void settleTerrainContract()
        {
            for (int round = 0; round < 2; round++)
            {
                repairBridges();
                TerrainKit.materializeUnclimbableWalkableEdges(tiles, elevation, width, height, new MaterializeUnclimbableEdgesOptions
                {
                    protect = (_tx, _ty, index) =>
                        protect[index] == 1 || isHeartward(index) || tiles[index] == TileType.Bridge,
                });
            }
            repairBridges();
            dissolveStrandedLand();
            resolveWaterElevation();
            // Repairs and road materialization run after the original rift pass and can expose a new diagonal
            // Water/Chasm contact. Give the final shipping raster the same one-cell physical bank contract.
            separateRiftFromWater();
            TerrainKit.assignChasmDepths(tiles, elevation, width, height);
            TerrainRules.normalizeTerrainStoredElevationInPlace(tiles, elevation, width, height);
        }

        private SimulatedMapMetrics metrics()
        {
            int walkable = 0;
            int water = 0;
            int chasm = 0;
            int bridge = 0;
            int roadCells = 0;
            var levels = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int tile = tiles[i];
                if (tile == TileType.Water) water++;
                else if (tile == TileType.Chasm) chasm++;
                else if (tile == TileType.Bridge) bridge++;
                if (isWalkable(tile))
                {
                    walkable++;
                    levels.Add(elevation[i]);
                    if (road[i] != 0) roadCells++;
                }
            }
            return new SimulatedMapMetrics
            {
                cells = count,
                walkableCells = walkable,
                waterCells = water,
                chasmCells = chasm,
                bridgeCells = bridge,
                decorations = decorations.Count,
                themeRegions = themeRegionCount,
                floraPerWalkable = walkable > 0 ? (double)decorations.Count / walkable : 0,
                wonders = wonders.Count,
                roadCells = roadCells,
                terraceLevels = levels.Count,
            };
        }

        // ---------------------------------------------------------------------------------------------------
        // Shared composition helpers

        /// <summary>Record the layer that just finished: its snapshots, and exactly which cells it changed.</summary>
        private void recordStage(
            string layer,
            MapRevealSweepPlan sweep,
            IReadOnlyList<TerrainDecorationPlacement>? decorations = null,
            IReadOnlyList<PlacedMapWonder>? wonders = null)
        {
            var changed = new List<uint>();
            bool tilesChanged = false;
            bool elevationChanged = false;
            bool themeChanged = false;
            for (int i = 0; i < count; i++)
            {
                bool tileDiff = tiles[i] != previousTiles[i];
                bool elevationDiff = elevation[i] != previousElevation[i];
                bool themeDiff = themeIndex[i] != previousThemeIndex[i];
                if (tileDiff) tilesChanged = true;
                if (elevationDiff) elevationChanged = true;
                if (themeDiff) themeChanged = true;
                if (tileDiff || elevationDiff || themeDiff) changed.Add((uint)i);
            }
            StageCopy copy = STAGE_COPY[layer];
            stages.Add(new MapSimulationStage
            {
                layer = layer,
                title = copy.title,
                detail = copy.detail,
                // Rewritten from the layer's real work once the whole build is known; see `applyBeatTiming`.
                beatSeconds = 0,
                sweep = sweep,
                cells = changed.ToArray(),
                tiles = tilesChanged ? (byte[])tiles.Clone() : null,
                elevation = elevationChanged ? (sbyte[])elevation.Clone() : null,
                themeIndex = themeChanged ? (byte[])themeIndex.Clone() : null,
                decorations = decorations,
                wonders = wonders,
            });
            previousTiles = (byte[])tiles.Clone();
            previousElevation = (sbyte[])elevation.Clone();
            previousThemeIndex = (byte[])themeIndex.Clone();
        }

        /// <summary>
        /// STUDIO: turn every cell of one tile kind (water, chasm, rock) back into ground at the level of the land
        /// around it, spreading inward from its banks.
        /// </summary>
        private void closeHazard(int tile)
        {
            var pending = new byte[count];
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != tile) continue;
                pending[i] = 1;
                any = true;
            }
            if (any) levelIntoLand(pending);
        }

        /// <summary>
        /// STUDIO: the marked cells become ground at the level of the land next to them, spreading inward from the
        /// walkable cells around them. Cells no land reaches stand at the flat level (a map that is all water), or
        /// stay as they are when `strandedStay` is set.
        /// </summary>
        private void levelIntoLand(byte[] pending, bool strandedStay = false)
        {
            var frontier = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (pending[i] != 0 || !isWalkable(tiles[i])) continue;
                if (neighbourPending(i)) frontier.Add(i);
            }
            bool neighbourPending(int index)
            {
                int tx = index % width;
                int ty = index / width;
                foreach (var (dx, dy) in NEIGHBOURS4)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (inBounds(width, height, nx, ny) && pending[tileIndex(width, nx, ny)] != 0) return true;
                }
                return false;
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
                        tiles[ni] = TileType.Floor;
                        elevation[ni] = elevation[index];
                        ground[ni] = Js.U8(elevation[index]);
                        next.Add(ni);
                    }
                }
                frontier = next;
            }
            if (strandedStay) return;
            for (int i = 0; i < count; i++)
            {
                if (pending[i] == 0) continue;
                tiles[i] = TileType.Floor;
                elevation[i] = FLAT_GROUND_LEVEL;
                ground[i] = FLAT_GROUND_LEVEL;
            }
        }

        /// <summary>
        /// STUDIO: the dials' promise, made good on the finished raster. A dial at zero means none of its feature —
        /// whatever a wonder stamp, a motif or a repair did after the pass that owns it. On a flat world every
        /// walkable cell also stands on the one flat level.
        /// </summary>
        private void enforceDialZeros()
        {
            if (waterScale <= 0) closeHazard(TileType.Water);
            if (riftScale <= 0) closeHazard(TileType.Chasm);
            if (!flatWorld) return;
            closeHazard(TileType.Cleft);
            closeHazard(TileType.Solid);
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] == TileType.Underpass) tiles[i] = TileType.Floor;
                if (isWalkable(tiles[i])) elevation[i] = FLAT_GROUND_LEVEL;
            }
        }

        /// <summary>Every wall, shore and rim gets a height derived from the ground it stands on.</summary>
        private void resolveBlockedElevation()
        {
            TerrainKit.assignBlockedElevationFromNeighbours(tiles, elevation, width, height, new SolidElevationOptions
            {
                maxLevel = SIM_MAX_LEVEL,
                wallStep = 1,
                neighbourhood = 8,
                includeWaterAsNeighbour = true,
                preserveWater = true,
                fallback = (tx, ty) =>
                    TerrainKit.clampElevationLevel(ground[tileIndex(width, tx, ty)] + 2, SIM_MAX_LEVEL),
            });
            raiseWallMasses();
        }

        /// <summary>
        /// Give the rock its height.
        ///
        /// A wall one level above the floor it stands on is a kerb, not a cliff — and a whole world built of kerbs
        /// reads flat from the fixed camera however carefully its ground was terraced. This lifts every blocked cell
        /// to its neighbouring ground plus a *massif* height sampled from a smooth field, so one map carries low
        /// banks along its water and towering faces around its uplands instead of one uniform step everywhere.
        ///
        /// It is free in legality terms: the terrain contract constrains walkable-to-walkable climbs, never how tall
        /// a wall is. Height is clamped so a mass standing on high ground simply tops out at the ceiling rather than
        /// flattening the shelf below it.
        /// </summary>
        private void raiseWallMasses()
        {
            uint massifSeed = Js.ToUint32(seedInt ^ 0x51ab3e75);
            double massifScale = Math.max(7, Math.min(width, height) * WALL_MASSIF_SCALE);
            double detailScale = Math.max(3, massifScale * 0.31);
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    int tile = tiles[index];
                    if (tile != TileType.Solid && tile != TileType.Cleft) continue;
                    // Stand on the highest ground this mass touches, so a face never sinks below the shelf beside it.
                    int groundLevel = -1;
                    foreach (var (dx, dy) in NEIGHBOURS8)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        int neighbour = tiles[ni];
                        if (!isWalkable(neighbour) && neighbour != TileType.Water) continue;
                        groundLevel = Math.max(groundLevel, elevation[ni]);
                    }
                    if (groundLevel < 0) continue;
                    // Two octaves of massif field: which range this rock belongs to, and how broken its crest is.
                    double massif = valueNoise(massifSeed, tx, ty, massifScale);
                    double detail = valueNoise((int)massifSeed ^ unchecked((int)0x9e3779b9), tx, ty, detailScale);
                    double rise = roll.wallRelief * (0.45 + massif * 1.15) + detail * 1.4;
                    elevation[index] = Js.I8(TerrainKit.clampElevationLevel(
                        groundLevel + Math.max(1, Math.round(rise)),
                        SIM_MAX_LEVEL));
                }
            }
        }

        /// <summary>A basin's stored datum is one step below the driest shore it touches.</summary>
        private void resolveWaterElevation()
        {
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    if (tiles[index] != TileType.Water) continue;
                    int shore = -1;
                    foreach (var (dx, dy) in NEIGHBOURS8)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (!isWalkable(tiles[ni])) continue;
                        shore = Math.max(shore, elevation[ni]);
                    }
                    int groundLevel = shore >= 0 ? shore : ground[index];
                    elevation[index] = Js.I8(TerrainKit.standardWaterStoredLevelAt(groundLevel, SIM_MAX_LEVEL));
                }
            }
            resolveBlockedElevation();
        }

        /// <summary>A deck sits level with the shore it leaves, so the crossing is one legal step at each landing.</summary>
        private void resolveBridgeElevation()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                for (int ty = 0; ty < height; ty++)
                {
                    for (int tx = 0; tx < width; tx++)
                    {
                        int index = tileIndex(width, tx, ty);
                        if (tiles[index] != TileType.Bridge) continue;
                        int level = -1;
                        foreach (var (dx, dy) in NEIGHBOURS4)
                        {
                            int nx = tx + dx;
                            int ny = ty + dy;
                            if (!inBounds(width, height, nx, ny)) continue;
                            int ni = tileIndex(width, nx, ny);
                            int tile = tiles[ni];
                            if (tile != TileType.Floor && tile != TileType.Bridge) continue;
                            level = Math.max(level, elevation[ni]);
                        }
                        if (level >= 0) elevation[index] = Js.I8(TerrainKit.clampElevationLevel(level, SIM_MAX_LEVEL));
                    }
                }
            }
            // Only the decks and their landings are relaxed; relaxing the whole field here would erase the terraces.
            TerrainKit.materializeUnclimbableWalkableEdges(tiles, elevation, width, height, new MaterializeUnclimbableEdgesOptions
            {
                protect = (_tx, _ty, index) =>
                    protect[index] == 1 || isHeartward(index) || tiles[index] == TileType.Bridge,
            });
            repairBridges();
            dissolveStrandedLand();
            resolveWaterElevation();
            TerrainKit.assignChasmDepths(tiles, elevation, width, height);
            TerrainRules.normalizeTerrainStoredElevationInPlace(tiles, elevation, width, height);
        }

        /// <summary>
        /// Deterministically pick `target` cells from `candidates`, preferring high field scores. A stable hash is
        /// mixed into the score so the selection is dithered rather than a hard iso-line through the field.
        /// </summary>
        private List<int> selectByField(
            List<int> candidates,
            double target,
            int salt,
            Func<int, int, int, double> field)
        {
            if (target <= 0 || candidates.Count == 0) return new List<int>();
            if (target >= candidates.Count) return new List<int>(candidates);
            uint hashSeed = Js.ToUint32(seedInt ^ salt);
            var scored = new List<ScoredCell>(candidates.Count);
            foreach (int index in candidates)
            {
                int tx = index % width;
                int ty = index / width;
                scored.Add(new ScoredCell
                {
                    index = index,
                    score = field(tx, ty, index) + latticeHash(hashSeed, tx, ty) * 0.35,
                });
            }
            scored.sort((a, b) =>
            {
                double d = b.score - a.score;
                return Js.Truthy(d) ? d : a.index - b.index;
            });
            // `slice(0, target)`: ToIntegerOrInfinity truncates a fractional end.
            int take = (int)Math.trunc(target);
            var result = new List<int>(take);
            for (int i = 0; i < take; i++) result.Add(scored[i].index);
            return result;
        }

        /// <summary>
        /// The best `limit` cells by a scoring function, negative scores rejected.
        ///
        /// Bounded selection, not a full sort: the site searches call this dozens of times per build, and sorting a
        /// quarter of a million scored cells each time was the composition's worst super-linear term.
        /// </summary>
        private List<int> topCells(Func<int, int, int, double> score, double limit)
        {
            double bound = Math.max(0, Math.round(limit));
            if (bound == 0) return new List<int>();
            var bestIndex = new List<int>();
            var bestScore = new List<double>();
            double worst = double.PositiveInfinity;
            for (int index = 0; index < count; index++)
            {
                int tx = index % width;
                int ty = index / width;
                double value = score(tx, ty, index);
                if (value < 0) continue;
                if (bestIndex.Count >= bound && value <= worst) continue;
                // Insertion into a short, descending list. `bound` is tens of entries, so this stays cheaper than a
                // heap and keeps the result deterministic on ties.
                int at = bestIndex.Count;
                while (at > 0 && bestScore[at - 1] < value) at--;
                bestIndex.Insert(at, index);
                bestScore.Insert(at, value);
                if (bestIndex.Count > bound)
                {
                    bestIndex.RemoveAt(bestIndex.Count - 1);
                    bestScore.RemoveAt(bestScore.Count - 1);
                }
                worst = bestScore[bestScore.Count - 1];
            }
            return bestIndex;
        }

        private List<List<int>> walkableComponents()
        {
            var seen = new byte[count];
            var @out = new List<List<int>>();
            var stack = new List<int>();
            for (int start = 0; start < count; start++)
            {
                if (seen[start] != 0 || !isWalkable(tiles[start])) continue;
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
                @out.Add(cells);
            }
            return @out;
        }

        /// <summary>Principal axis of a tile family — the direction a flow sweep should follow.</summary>
        private SweepAxis principalAxis(int tile)
        {
            int n = 0;
            double sumX = 0;
            double sumY = 0;
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != tile) continue;
                n++;
                sumX += i % width;
                sumY += i / width;
            }
            if (n == 0)
            {
                return new SweepAxis(heartTx, heartTy, 1, 0);
            }
            double meanX = sumX / n;
            double meanY = sumY / n;
            double sxx = 0;
            double syy = 0;
            double sxy = 0;
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] != tile) continue;
                double ddx = (i % width) - meanX;
                double ddy = (i / width) - meanY;
                sxx += ddx * ddx;
                syy += ddy * ddy;
                sxy += ddx * ddy;
            }
            // Dominant eigenvector of the 2x2 covariance matrix.
            double theta = 0.5 * Math.atan2(2 * sxy, sxx - syy);
            double dx = Math.cos(theta);
            double dy = Math.sin(theta);
            int extent = Math.max(width, height);
            return new SweepAxis(
                Math.round(meanX - dx * extent),
                Math.round(meanY - dy * extent),
                dx,
                dy);
        }

        private int interiorCellCount()
        {
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                if (tiles[i] == TileType.Floor) n++;
            }
            return n;
        }

        private bool isInset(int tx, int ty, double margin)
        {
            return tx >= margin && ty >= margin && tx < width - margin && ty < height - margin;
        }

        /// <summary>True when a square of walkable cells at one single level surrounds the anchor — buildable ground.</summary>
        private bool hasLevelPlaza(int tx, int ty, int radius)
        {
            sbyte level = elevation[tileIndex(width, tx, ty)];
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) return false;
                    int index = tileIndex(width, nx, ny);
                    if (tiles[index] != TileType.Floor) return false;
                    if (elevation[index] != level) return false;
                }
            }
            return true;
        }

        /// <summary>Proximity to a real edge (wall foot, shoreline, rift lip) — where undergrowth and relics belong.</summary>
        private double edgeAffinity(int tx, int ty)
        {
            double score = 0;
            foreach (var (dx, dy) in NEIGHBOURS8)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int tile = tiles[tileIndex(width, nx, ny)];
                if (tile == TileType.Solid) score += 0.16;
                else if (tile == TileType.Water) score += 0.2;
                else if (tile == TileType.Chasm) score += 0.14;
            }
            return Math.min(1, score);
        }

        private bool neighbourHas(int tx, int ty, int tile)
        {
            foreach (var (dx, dy) in NEIGHBOURS8)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                if (tiles[tileIndex(width, nx, ny)] == tile) return true;
            }
            return false;
        }

        /// <summary>"Is there one of these within `radius`?" as a field — a separable dilation, computed once.</summary>
        private byte[] dilateTile(int tile, int radius)
        {
            var rows = new byte[count];
            var @out = new byte[count];
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int index = tileIndex(width, tx, ty);
                    if (tiles[index] != tile) continue;
                    int from = Math.max(0, tx - radius);
                    int to = Math.min(width - 1, tx + radius);
                    for (int x = from; x <= to; x++) rows[tileIndex(width, x, ty)] = 1;
                }
            }
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    if (rows[tileIndex(width, tx, ty)] == 0) continue;
                    int from = Math.max(0, ty - radius);
                    int to = Math.min(height - 1, ty + radius);
                    for (int y = from; y <= to; y++) @out[tileIndex(width, tx, y)] = 1;
                }
            }
            return @out;
        }

        private bool hasMaskWithin(byte[] mask, int tx, int ty, int radius)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    if (mask[tileIndex(width, nx, ny)] != 0) return true;
                }
            }
            return false;
        }

        /// <summary>A hazard pass may claim a cell only if nothing deliberate was built there.</summary>
        private bool hazardCanClaim(int index)
        {
            return protect[index] == 0 && claim[index] == 0 && !isHeartward(index);
        }

        /// <summary>
        /// The heart's plaza is off limits to every hazard pass. The composition always needs one guaranteed patch of
        /// standing ground: it anchors connectivity repair, the camera and the reveal, and a map whose heart drowned
        /// in its own river has no anchor left to rebuild from.
        /// </summary>
        private bool isHeartward(int index)
        {
            int tx = index % width;
            int ty = index / width;
            return Math.abs(tx - heartTx) <= HEART_PLAZA && Math.abs(ty - heartTy) <= HEART_PLAZA;
        }

        /// <summary>
        /// Re-anchor the heart on the biggest walkable body the world currently has.
        ///
        /// This runs before every connectivity trim, and it must re-anchor unconditionally rather than only when the
        /// heart's own cell stopped being walkable. When the terrace pass splits the world along a cliff, the heart
        /// can end up perfectly walkable inside a small pocket — and a trim anchored there deletes every other body
        /// on the map. That failure mode is silent, catastrophic and produced a 5%-walkable world; anchoring on the
        /// largest component makes it impossible by construction.
        /// </summary>
        private void refreshHeart()
        {
            heartFromLargestComponent();
        }

        /// <summary>The explicit theme a placement carries, or null when it simply inherits the base biome.</summary>
        private string? themeKeyAt(int index)
        {
            int value = themeIndex[index];
            if (value == TERRAIN_THEME_INHERIT) return null;
            string? key = value < themeKeys.Count ? themeKeys[value] : null;
            return !string.IsNullOrEmpty(key) ? key : null;
        }

        /// <summary>
        /// Order placements so they appear as a spreading wave from the heart rather than in raster order, with a
        /// hash-driven wobble so the wave never looks like an expanding ring.
        /// </summary>
        private void sortPlacementsForReveal<T>(List<T> placements, Func<T, int> txOf, Func<T, int> tyOf)
        {
            double seed = seedNum;
            placements.sort((a, b) =>
            {
                int atx = txOf(a), aty = tyOf(a), btx = txOf(b), bty = tyOf(b);
                double da =
                    Math.hypot(atx - heartTx, aty - heartTy) +
                    latticeHash(seed, atx, aty) * 9 -
                    4.5;
                double db =
                    Math.hypot(btx - heartTx, bty - heartTy) +
                    latticeHash(seed, btx, bty) * 9 -
                    4.5;
                double d = da - db;
                if (Js.Truthy(d)) return d;
                if (aty != bty) return aty - bty;
                return atx - btx;
            });
        }
    }
}
