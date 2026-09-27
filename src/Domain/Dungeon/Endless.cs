// Port of packages/shared/src/domain/dungeon/endless.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.EndlessCoordinates;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>
/// Endless dungeon generation for the streamed Standard Run.
///
/// Generation V4 composes the infinite world in 4×4-chunk "Countries". A biome-aware WFC/Markov layer selects
/// compatible landscape sequences above those Countries; each then owns one dominant grammar, an optional
/// restrained support grammar, broad elevation shelves, causal water/chasm systems, semantic dressing and
/// deliberate quiet space. Chunk generation only rasterizes the relevant slice, so
/// `(seed, cx, cy, generationVersion)` remains deterministic on server and client.
///
/// Connectivity is sparse and world-authored: a deterministic Country tree plus leaf-rescue loops selects the
/// seams that actually open, while both sides derive identical port positions from shared seam identity.
/// The authoritative Flood spine is then painted from the exact runtime spine seed, guaranteeing a broad,
/// climbable escape body without restoring the old four-ports-per-chunk chessboard. Boss courts and other
/// set pieces are anchored to that spine at world-scale cadence.
///
/// The finalized terrain contract is unchanged: tiles are collision truth, elevation carries the 0..12
/// walkable shelf and wall-cap ladder, depth tiles author waterfalls/chasm crossings, and finalization
/// materializes unsafe steps as Solid. `generationVersion: 1` retains the original chunk generator and version
/// 2 retains independent Country selection for old explicit descriptors; newly parsed runs use
/// ENDLESS_GENERATION_VERSION. The server audit re-proves seam, traversal, landscape, vertical-profile
/// and generation-time contracts over real collision regions.
///
/// The TS module re-exports ENDLESS_CHUNK_TILES, ENDLESS_CHUNK_WORLD, endlessChunkOriginX/Y,
/// endlessChunkCoordX/Y, ENDLESS_GRID_ORIGIN and endlessChunkKey from endlessCoordinates.ts; in C# they are
/// referenced from <see cref="EndlessCoordinates"/> directly (no forwarding members, to avoid ambiguous
/// `using static` imports).
/// </summary>
public static class Endless
{
    /// <summary>
    /// The archetype that gives a chunk its character (drawn deterministically per chunk). Each is built on the
    /// same seam-port + braided-maze backbone, so connectivity and "no straight lane" hold for every kind.
    /// </summary>
    private static class ChunkKind
    {
        /// <summary>A tight braided warren — winding corridors between many small junction cells. The dominant beat.</summary>
        public const int Warren = 0;
        /// <summary>A braided maze whose cells widen into encounter rooms (each stamped with an an encounter id).</summary>
        public const int Rooms = 1;
        /// <summary>A braided maze of larger cover halls, each studded with a solid pillar.</summary>
        public const int Halls = 2;
        /// <summary>An organic cellular-automata cavern, its four seam ports trenched into the body.</summary>
        public const int Cavern = 3;
        /// <summary>A rare large open boss arena (a DungeonRoomType.Boss marker the spawn system seeks).</summary>
        public const int Arena = 4;
        /// <summary>The open spawn clearing — only ever chunk (0,0), so the cohort spawns on clean, safe ground.</summary>
        public const int Clearing = 5;
        /// <summary>A calm organic glade — the pacing breather between maze districts (no encounter rooms).</summary>
        public const int Glade = 6;
        public const int Courtyard = 7;
        public const int Ruins = 8;
    }

    /// <summary>Draw order of the per-chunk archetype deck; weights are computed per chunk in <see cref="chunkKindFor"/>.</summary>
    private static readonly int[] KIND_DECK =
    {
        ChunkKind.Warren,
        ChunkKind.Rooms,
        ChunkKind.Halls,
        ChunkKind.Cavern,
        ChunkKind.Arena,
        ChunkKind.Glade,
        ChunkKind.Courtyard,
        ChunkKind.Ruins,
    };
    /// <summary>
    /// Reused per-draw weight buffer. The TS relies on chunk generation being single-threaded; C# chunk
    /// generation may run on streaming workers, so the scratch is per thread (every draw overwrites all slots).
    /// </summary>
    [ThreadStatic] private static double[]? kindWeightsScratch;

    /// <summary>Field salts for the world-character noises (independent of the elevation octaves' salts).</summary>
    private const int STRUCTURE_SALT = 0x51ab3e75;
    private const int STRUCTURE_DETAIL_SALT = 0x6c8e9cf5;
    private const int STRUCTURE_OPENNESS_SALT = 0x27d4eb2f;
    private const int STRUCTURE_RIDGE_SALT = 0x4cf5ad43;
    private const int FIELD_WARP_X_SALT = unchecked((int)0x94d049bb);
    private const int FIELD_WARP_Y_SALT = 0x133111eb;
    private const int SEAM_ROW_SALT = 0x7a2fb1c9;
    private const int SEAM_COL_SALT = 0x3d9f42a7;
    private const int SEAM_DETAIL_SALT = 0x5bd1e995;

    // Braid tuning for the global node-graph maze: the base probability a non-tree (Gabriel) edge is looped in,
    // and the extra probability at full local openness. Higher braid = more loops = MORE labyrinth (winding routes
    // with choices) without opening the ground up — the lattice pitch (wall spacing) stays per-run so walls read as
    // clean contiguous rock rather than fragmenting into noise. Constant over every run; regional density comes from
    // the global maze-openness field, so seams always agree. See EndlessMaze.carveEndlessMaze.
    private const int CHASM_FIELD_SALT = 0x6ea3c8d1;
    private const int CHASM_DETAIL_SALT = 0x18bf54a7;
    private const int CHASM_BASIN_SALT = 0x2f6e2b1d;
    private const int BASIN_WALL_VEIN_SALT = 0x75a4f319;
    private const int BRIDGE_LANDMARK_PORT_SALT = 0x1f83d9ab;
    /// <summary>Chasm is a dramatic aperture, never the majority ground of a streamed gameplay chunk.</summary>
    private const double CHASM_MAX_CHUNK_FRACTION = 0.145;
    private static readonly (int dx, int dy)[] CHASM_CARDINAL_DIRS =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };
    /// <summary>`[[0, 0] as const, ...CHASM_CARDINAL_DIRS]` — the centre cell, then the four cardinals.</summary>
    private static readonly (int dx, int dy)[] CENTRE_AND_CHASM_CARDINAL_DIRS =
    {
        (0, 0),
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };

    /// <summary>The global maze tuning for a run — the per-run lattice pitch + openness bias from its macro DNA.</summary>
    private static MazeTuning mazeTuningFor(EndlessWorldProfile profile)
    {
        return new MazeTuning
        {
            pitch = profile.macro.latticePitch,
            opennessBias = profile.macro.opennessBias,
            braidBase = profile.mazeBraidBase,
            braidRange = profile.mazeBraidRange,
        };
    }

    private static double clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static void applyEndlessChasms(
        byte[] tiles,
        // Terrain kinds before the river pass. Chasm eligibility is derived from this landform source so a river
        // painted over open ground cannot hide a native rift; the current `tiles` still owns the final Water source
        // cells, bridges and navigation contract.
        byte[] preRiverTiles,
        int w,
        int h,
        int cx,
        int cy,
        byte[] corridorMask,
        IReadOnlyList<DungeonRoom> rooms,
        IReadOnlyList<ChunkPort> ports,
        double seedNum,
        EndlessWorldProfile profile,
        EndlessLandscapeSample landscape,
        byte[]? beforeChasmTiles = null)
    {
        var dna = profile.macro;
        int baseGtx = cx * w;
        int baseGty = cy * h;
        var before = beforeChasmTiles ?? tiles.slice();
        var candidate = new byte[tiles.Length];
        var chasmScore = new float[tiles.Length];
        var protectedMask = new byte[tiles.Length];
        bool protectedByRoom(int tx, int ty)
        {
            foreach (var room in rooms)
            {
                var r = room.rect;
                if (tx >= r.tx - 2 && tx < r.tx + r.tw + 2 && ty >= r.ty - 2 && ty < r.ty + r.th + 2)
                    return true;
            }
            return false;
        }

        // corridorMask is deliberately broad: in maze archetypes it can cover every carved floor cell. Treating
        // that complete mask as sacred would make Floor categorically immune again and pay for ravines only with
        // mountain/water space. Instead, derive the actual navigation contract: the shortest WALKABLE route tree
        // joining the four deterministic seam ports. A one-cell collar keeps that tree comfortably three tiles wide,
        // while loops, plazas and meadow shoulders remain genuine chasm candidates.
        void protectRoute(ChunkPort start, ChunkPort target)
        {
            int startIdx = start.ty * w + start.tx;
            int targetIdx = target.ty * w + target.tx;
            if (!isWalkable(before[startIdx]) || !isWalkable(before[targetIdx]))
                return;
            var previous = new int[tiles.Length].fill(-1);
            var queue = new int[tiles.Length];
            int head = 0;
            int tail = 0;
            queue[tail++] = startIdx;
            previous[startIdx] = startIdx;
            while (head < tail && previous[targetIdx] < 0)
            {
                int at = queue[head++];
                int ax = at % w;
                int ay = at / w; // Math.floor(at / w) of a non-negative index
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = ax + dx;
                    int ny = ay + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (previous[ni] >= 0 || !isWalkable(before[ni])) continue;
                    previous[ni] = at;
                    queue[tail++] = ni;
                }
            }
            if (previous[targetIdx] < 0) return;
            for (int at = targetIdx; ; at = previous[at])
            {
                int ax = at % w;
                int ay = at / w;
                foreach (var (dx, dy) in CENTRE_AND_CHASM_CARDINAL_DIRS)
                {
                    int nx = ax + dx;
                    int ny = ay + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (isWalkable(before[ni])) protectedMask[ni] = 1;
                }
                if (at == startIdx) break;
            }
        }
        var routeRoot = ports.Count > 0 ? ports[0] : null;
        if (routeRoot != null)
        {
            foreach (var port in ports) protectRoute(routeRoot, port);
        }
        // A bridge is valid only while at least one cardinal bank cell remains Water. Preserve the immediate deck
        // halo from both the land carve and the later water-gradient/coupling passes; Chasm may frame the crossing one
        // cell farther out, but can never turn a valid deck into a dry stray during final validation.
        for (int ty = 0; ty < h; ty++)
        {
            for (int tx = 0; tx < w; tx++)
            {
                int idx = ty * w + tx;
                if (before[idx] != TileType.Bridge) continue;
                protectedMask[idx] = 1;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h) protectedMask[ny * w + nx] = 1;
                }
            }
        }

        for (int ty = 0; ty < h; ty++)
        {
            for (int tx = 0; tx < w; tx++)
            {
                int idx = ty * w + tx;
                int tile = tiles[idx];
                if (tile == TileType.Bridge || protectedMask[idx] != 0 || protectedByRoom(tx, ty)) continue;
                int gtx = baseGtx + tx;
                int gty = baseGty + ty;
                var macro = EndlessMacro.endlessMacroSample(seedNum, gtx, gty, dna);
                double broad = valueNoise((uint)(Js.ToInt32(seedNum) ^ CHASM_FIELD_SALT), gtx, gty, 19);
                double detail = valueNoise((uint)(Js.ToInt32(seedNum) ^ CHASM_DETAIL_SALT), gtx, gty, 7);
                double basin = valueNoise((uint)(Js.ToInt32(seedNum) ^ CHASM_BASIN_SALT), gtx, gty, 41);
                // Tighten the smooth ridge core instead of lowering its amplitude. The former linear ridge admitted a
                // broad shoulder on both sides of every contour and could let one macro locus occupy a quarter of a
                // gameplay camera. A power curve keeps the same deterministic, seam-continuous centre line while
                // turning Chasm back into a narrow landmark rather than a regional ground cover.
                double rift = Math.pow(1 - Math.abs(broad - 0.5) * 2, 2.85);
                double basinCore = TerrainKit.smoothstep01((basin - 0.57) / 0.24);
                double score =
                    rift * 0.58 +
                    detail * 0.2 +
                    (1 - macro.openness) * 0.1 +
                    macro.rock * 0.12 +
                    basinCore * profile.chasmBasinStrength +
                    profile.chasmScoreBias +
                    landscape.traits.chasmStrength * 0.075 +
                    basinCore * landscape.traits.chasmBasins * 0.09 +
                    rift * landscape.traits.riftStrength * 0.08;
                if (macro.style == "chasm_rift") score += macro.interior * 0.24;
                else if (macro.style == "gorge_network" || macro.style == "sinkhole_cluster")
                    score += macro.interior * 0.12;
                else if (
                    macro.style == "canyon_web" ||
                    macro.style == "crater_field" ||
                    macro.style == "moat_island"
                )
                    score += macro.interior * 0.07;
                bool touchesWater = tile == TileType.Water;
                if (!touchesWater)
                {
                    foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && before[ny * w + nx] == TileType.Water)
                        {
                            touchesWater = true;
                            break;
                        }
                    }
                }
                if (touchesWater)
                    score += profile.chasmWaterAffinity + landscape.traits.waterChasmAffinity * 0.09;
                chasmScore[idx] = (float)score;
                // Chasms are a native third landform: broad rifts deliberately cut open ground as well as rock and
                // water. Open floor deliberately carries the lower land threshold: the protected route skeleton owns
                // navigation safety, so the new landform is paid for by real traversable country rather than by quietly
                // replacing the mountain silhouette. Water asks for the strongest field core, leaving a source lip beside
                // the converted cells from which the renderer can emit a physical waterfall.
                // Rivers are a surface treatment over the underlying landform, not a veto over it. If this cell was
                // walkable/rock before the river pass, use that source kind for the rift threshold even when it is Water
                // now. The candidate is then allowed to cut the river into a real Water→Chasm lip; cells outside the rift
                // remain Water sources and the waterfall pass below keeps the contact spatially coherent.
                int sourceTile = preRiverTiles[idx];
                int thresholdTile = tile == TileType.Water ? sourceTile : tile;
                double threshold =
                    tile == TileType.Water && sourceTile == TileType.Floor
                        ? 0.71
                        : thresholdTile == TileType.Floor
                            ? 0.75
                            : thresholdTile == TileType.Water
                                ? 1
                                : thresholdTile == TileType.Solid
                                    ? 0.78
                                    : 1;
                if (score >= threshold) candidate[idx] = 1;
            }
        }

        // Suppress isolated one-cell pinholes. The field's contour bands remain broad and cross-seam stable; only
        // local speckle is removed, and the protected seam-port trails above already own all navigation contracts.
        for (int ty = 0; ty < h; ty++)
        {
            for (int tx = 0; tx < w; tx++)
            {
                int idx = ty * w + tx;
                if (candidate[idx] == 0) continue;
                int neighbors = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && candidate[ny * w + nx] != 0) neighbors++;
                    }
                }
                if (neighbors < 3) candidate[idx] = 0;
            }
        }

        // A 5..8-level drop needs several ground cells of plan-view thickness before the oblique gameplay camera can
        // see its remote bottom; a one-to-three-cell contour reads only as a dark facade even when its height/material
        // contract is correct. Grow coherent LAND contours by two field-qualified collars (never into protected
        // routes, rooms, bridges or Water). This gives the natural core enough plan-view aperture for the deep-void
        // surface while broad rifts gain only a narrow irregular perimeter. Navigation stays owned by the protected
        // route tree and final repair.
        for (int collar = 0; collar < 2; collar++)
        {
            var landContour = candidate.slice();
            for (int ty = 1; ty < h - 1; ty++)
            {
                for (int tx = 1; tx < w - 1; tx++)
                {
                    int idx = ty * w + tx;
                    if (
                        landContour[idx] != 0 ||
                        protectedMask[idx] != 0 ||
                        protectedByRoom(tx, ty) ||
                        tiles[idx] == TileType.Water ||
                        tiles[idx] == TileType.Bridge ||
                        chasmScore[idx] < 0.7
                    )
                        continue;
                    int contourNeighbors = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (landContour[(ty + dy) * w + tx + dx] != 0) contourNeighbors++;
                        }
                    }
                    if (contourNeighbors >= 3) candidate[idx] = 1;
                }
            }
        }

        // Let a coherent land rift bite one step into a river/lake, but only down the local score gradient. At least
        // one lower-scored water neighbour therefore remains a real source cell beside every converted water cell:
        // a guaranteed Water→Chasm lip instead of either a dry near miss or a whole pond silently disappearing.
        // Freeze the land contour first: water selected in this pass must not recursively seed another water cell.
        var landCandidate = candidate.slice();
        for (int ty = 0; ty < h; ty++)
        {
            for (int tx = 0; tx < w; tx++)
            {
                int idx = ty * w + tx;
                if (
                    before[idx] != TileType.Water ||
                    protectedMask[idx] != 0 ||
                    protectedByRoom(tx, ty) ||
                    chasmScore[idx] < 0.7
                )
                    continue;
                bool touchesRift = false;
                bool keepsWaterSource = false;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (landCandidate[ni] != 0) touchesRift = true;
                    if (before[ni] == TileType.Water && chasmScore[ni] < (double)chasmScore[idx] - 0.025)
                    {
                        keepsWaterSource = true;
                    }
                }
                if (touchesRift && keepsWaterSource) candidate[idx] = 1;
            }
        }

        // Do not manufacture one-cell connector mouths between unrelated Water and Chasm fields. Natural rifts may
        // still bite the river in the coherent pass above, but a waterfall is never purchased with a wall-only crack.
        // Components that are genuinely present in the field but still miss the shared aperture contract receive a
        // lower-score collar of their own. Already broad components do not grow, so a strong run cannot become a
        // regional void merely to rescue a sparse run's otherwise valid landmark.
        for (int pass = 0; pass < 2; pass++)
        {
            var topologyTiles = new byte[candidate.Length].fill((byte)TileType.Solid);
            for (int index = 0; index < candidate.Length; index++)
            {
                if (candidate[index] != 0) topologyTiles[index] = TileType.Chasm;
            }
            var issueMask = TerrainChasm.analyzeTerrainChasmTopology(topologyTiles, w, h).issueMask;
            if (!issueMask.some(value => value != 0)) break;
            var contour = candidate.slice();
            for (int ty = 1; ty < h - 1; ty++)
            {
                for (int tx = 1; tx < w - 1; tx++)
                {
                    int idx = ty * w + tx;
                    if (
                        contour[idx] != 0 ||
                        protectedMask[idx] != 0 ||
                        protectedByRoom(tx, ty) ||
                        tiles[idx] == TileType.Water ||
                        tiles[idx] == TileType.Bridge ||
                        chasmScore[idx] < 0.66
                    )
                        continue;
                    bool touchesInvalidContour = false;
                    for (int dy = -1; dy <= 1 && !touchesInvalidContour; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int ni = (ty + dy) * w + tx + dx;
                            if (contour[ni] != 0 && issueMask[ni] != 0)
                            {
                                touchesInvalidContour = true;
                                break;
                            }
                        }
                    }
                    if (touchesInvalidContour) candidate[idx] = 1;
                }
            }
        }

        bool hasWaterContact()
        {
            for (int ty = 0; ty < h; ty++)
            {
                for (int tx = 0; tx < w; tx++)
                {
                    int index = ty * w + tx;
                    if (candidate[index] == 0) continue;
                    foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && before[ny * w + nx] == TileType.Water)
                            return true;
                    }
                }
            }
            return false;
        }

        // When independent river/rift fields narrowly miss, couple at most one pair with a FIVE-cell-wide gorge
        // mouth. The former one-cell path was responsible for many wall-only screenshots. The complete diamond
        // collar must be free of route/room/deck protection or the coupling is skipped; the Water endpoint itself
        // remains untouched and becomes the physical fall source.
        if (!hasWaterContact())
        {
            byte[]? couplingPatch(int sx, int sy, int wx, int wy, bool xFirst)
            {
                var centres = new List<(int, int)> { (sx, sy) };
                int tx = sx;
                int ty = sy;
                while (Math.abs(tx - wx) + Math.abs(ty - wy) > 1)
                {
                    if ((xFirst && tx != wx) || ty == wy) tx += (int)Math.sign(wx - tx);
                    else ty += (int)Math.sign(wy - ty);
                    centres.push((tx, ty));
                }
                var patch = new byte[candidate.Length];
                foreach (var (cx0, cy0) in centres)
                {
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            if (Math.abs(dx) + Math.abs(dy) > 2) continue;
                            int nx = cx0 + dx;
                            int ny = cy0 + dy;
                            if (nx <= 0 || ny <= 0 || nx >= w - 1 || ny >= h - 1) return null;
                            int index = ny * w + nx;
                            if (before[index] == TileType.Water) continue;
                            if (
                                before[index] == TileType.Bridge ||
                                protectedMask[index] != 0 ||
                                protectedByRoom(nx, ny)
                            )
                                return null;
                            patch[index] = 1;
                        }
                    }
                }
                return patch;
            }

            bool coupled = false;
            for (int wy = 2; wy < h - 2 && !coupled; wy++)
            {
                for (int wx = 2; wx < w - 2 && !coupled; wx++)
                {
                    if (before[wy * w + wx] != TileType.Water) continue;
                    double couplingRadius = 3 + Math.round(landscape.traits.waterfallAffinity * 5);
                    for (int radius = 1; radius <= couplingRadius && !coupled; radius++)
                    {
                        for (int dy = -radius; dy <= radius && !coupled; dy++)
                        {
                            int dxAbs = radius - Math.abs(dy);
                            // `for (const dx of dxAbs === 0 ? [0] : [-dxAbs, dxAbs])`
                            int dxCount = dxAbs == 0 ? 1 : 2;
                            for (int dxSlot = 0; dxSlot < dxCount; dxSlot++)
                            {
                                int dx = dxAbs == 0 ? 0 : dxSlot == 0 ? -dxAbs : dxAbs;
                                int rx = wx + dx;
                                int ry = wy + dy;
                                if (rx < 0 || ry < 0 || rx >= w || ry >= h || candidate[ry * w + rx] == 0) continue;
                                var patch =
                                    couplingPatch(rx, ry, wx, wy, true) ?? couplingPatch(rx, ry, wx, wy, false);
                                if (patch == null) continue;
                                for (int index = 0; index < patch.Length; index++)
                                {
                                    if (patch[index] != 0) candidate[index] = 1;
                                }
                                coupled = true;
                                break;
                            }
                        }
                    }
                }
            }
        }

        // Turn every accepted river/rift meeting into a legible CASCADE LIP, not a one-cell accidental slit. Freeze
        // the contact set first, then extend its land-side Chasm shoulder along the shoreline. Each added cell must
        // retain a Water source directly across the same normal and stay outside protected navigation/rooms/decks.
        // The already-accepted contact is the geological anchor, so its short lip may cross a weak fringe of the
        // score field. The result is a coherent 7..11-cell waterfall sheet; it cannot recurse along itself, cross a
        // route, or manufacture a detached component (the topology gate below removes any obstructed fragment).
        var waterfallContacts = new List<(int tx, int ty, int dx, int dy)>();
        var waterfallSourceExtensions = new byte[candidate.Length];
        for (int ty = 1; ty < h - 1; ty++)
        {
            for (int tx = 1; tx < w - 1; tx++)
            {
                int index = ty * w + tx;
                if (candidate[index] == 0) continue;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    if (before[(ty + dy) * w + tx + dx] == TileType.Water)
                        waterfallContacts.push((tx, ty, dx, dy));
                }
            }
        }
        int waterfallHalfWidth = 3 + (int)Math.round(clamp01(landscape.traits.waterfallAffinity) * 2);
        foreach (var (tx, ty, waterDx, waterDy) in waterfallContacts)
        {
            int tangentX = -waterDy;
            int tangentY = waterDx;
            for (int offset = -waterfallHalfWidth; offset <= waterfallHalfWidth; offset++)
            {
                int nx = tx + tangentX * offset;
                int ny = ty + tangentY * offset;
                int wx = nx + waterDx;
                int wy = ny + waterDy;
                if (nx <= 0 || ny <= 0 || nx >= w - 1 || ny >= h - 1) continue;
                int index = ny * w + nx;
                int waterIndex = wy * w + wx;
                bool sourceAlreadyWater = before[waterIndex] == TileType.Water;
                int sourceAX = wx - tangentX;
                int sourceAY = wy - tangentY;
                int sourceBX = wx + tangentX;
                int sourceBY = wy + tangentY;
                bool touchesOriginalBankWater =
                    before[sourceAY * w + sourceAX] == TileType.Water ||
                    before[sourceBY * w + sourceBX] == TileType.Water;
                // A jagged river bank can leave a one-cell Floor notch in an otherwise broad source edge. Fill only that
                // notch and never overwrite a competing Chasm candidate (doing so would shorten sparse cascades). Both
                // tangential indices stay in-bounds because nx/ny have the one-cell border checked above.
                bool sourceCanExtend =
                    !sourceAlreadyWater &&
                    before[waterIndex] == TileType.Floor &&
                    candidate[waterIndex] == 0 &&
                    protectedMask[waterIndex] == 0 &&
                    !protectedByRoom(wx, wy) &&
                    touchesOriginalBankWater;
                if (
                    (!sourceAlreadyWater && !sourceCanExtend) ||
                    before[index] == TileType.Water ||
                    before[index] == TileType.Bridge ||
                    protectedMask[index] != 0 ||
                    protectedByRoom(nx, ny)
                )
                    continue;
                if (sourceCanExtend) waterfallSourceExtensions[waterIndex] = 1;
                candidate[index] = 1;
                // The chunk-level Chasm cap keeps the strongest scores. Preserve the cascade lip as part of that core so
                // sorting cannot randomly shave the outer cells back into the narrow waterfall this pass just repaired.
                chasmScore[index] = (float)Math.max(
                    chasmScore[index],
                    0.82 + clamp01(landscape.traits.waterfallAffinity) * 0.08);
            }
        }
        for (int index = 0; index < waterfallSourceExtensions.Length; index++)
        {
            if (waterfallSourceExtensions[index] != 0) tiles[index] = TileType.Water;
        }
        // A broad field crest can otherwise cover most of a small streamed chunk, especially when several macro
        // accents overlap. Keep the strongest coherent core only; the topology gate below then peels any fragments.
        // This local cap composes across arbitrary camera windows and guarantees that "dramatic" never means an
        // unreadable all-void screen.
        double landscapeChasmFraction =
            landscape.traits.riftStrength * 0.075 + landscape.traits.chasmBasins * 0.035;
        int maxChasmCells = (int)Math.floor(
            candidate.Length * Math.min(0.24, CHASM_MAX_CHUNK_FRACTION + landscapeChasmFraction));
        int candidateCount = 0;
        foreach (byte value in candidate) candidateCount += value != 0 ? 1 : 0;
        if (candidateCount > maxChasmCells)
        {
            // Retain indices instead of duplicating score values. Reading the immutable score lane in the comparator
            // lets equal scores use their stable cell index as the final key, so the authored cap remains exact and
            // deterministic on every engine rather than occasionally retaining every tie at the cutoff.
            var scoredCandidates = new List<int>();
            for (int index = 0; index < candidate.Length; index++)
            {
                if (candidate[index] != 0) scoredCandidates.push(index);
            }
            scoredCandidates.sort((a, b) =>
            {
                // `chasmScore[b]! - chasmScore[a]! || a - b` (Float32Array reads are doubles in JS)
                double byScore = (double)chasmScore[b] - chasmScore[a];
                return Js.Truthy(byScore) ? byScore : a - b;
            });
            for (int rank = maxChasmCells; rank < scoredCandidates.Count; rank++)
            {
                candidate[scoredCandidates[rank]] = 0;
            }
        }

        for (int i = 0; i < tiles.Length; i++)
        {
            if (candidate[i] != 0) tiles[i] = TileType.Chasm;
        }

        // A rift may close a decorative floor loop that was not part of the protected skeleton. Re-open the
        // shortest route through cells that were WALKABLE before this pass, widening it by one tile where possible.
        // This keeps all open ground connected without turning an orphan into more wall and leaves a natural stone
        // causeway through the ravine instead of an invisible navigation repair.
        int seed = new Func<int>(() =>
        {
            for (int i = 0; i < tiles.Length; i++)
                if (corridorMask[i] != 0 && isWalkable(tiles[i])) return i;
            for (int i = 0; i < tiles.Length; i++) if (isWalkable(tiles[i])) return i;
            return -1;
        })();
        for (int repair = 0; seed >= 0 && repair < 12; repair++)
        {
            var reachable = floodFillWalkable(tiles, w, h, seed % w, seed / w);
            int target = -1;
            for (int i = 0; i < tiles.Length; i++)
            {
                if (isWalkable(tiles[i]) && reachable[i] == 0)
                {
                    target = i;
                    break;
                }
            }
            if (target < 0) break;
            var previous = new int[tiles.Length].fill(-1);
            var seen = new byte[tiles.Length];
            var queue = new int[tiles.Length];
            int head = 0;
            int tail = 0;
            queue[tail++] = target;
            seen[target] = 1;
            int landed = -1;
            while (head < tail && landed < 0)
            {
                int at = queue[head++];
                int ax = at % w;
                int ay = at / w;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = ax + dx;
                    int ny = ay + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (seen[ni] != 0 || !isWalkable(before[ni])) continue;
                    seen[ni] = 1;
                    previous[ni] = at;
                    if (reachable[ni] != 0)
                    {
                        landed = ni;
                        break;
                    }
                    queue[tail++] = ni;
                }
            }
            if (landed < 0) break;
            for (int at = landed; at >= 0; at = previous[at])
            {
                int ax = at % w;
                int ay = at / w;
                foreach (var (dx, dy) in CENTRE_AND_CHASM_CARDINAL_DIRS)
                {
                    int nx = ax + dx;
                    int ny = ay + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (tiles[ni] == TileType.Chasm && isWalkable(before[ni]))
                    {
                        tiles[ni] = before[ni];
                    }
                }
                if (at == target) break;
            }
        }

        // Connectivity repair can cut an otherwise broad rift into isolated wall-like remnants. This is the final
        // topology gate: peel tendrils, reject every component below the shared size/core contract, and restore the
        // exact pre-Chasm Water/Floor/Solid byte so server collision and client regeneration stay identical.
        TerrainChasm.enforceTerrainChasmTopology(tiles, before, w, h);
    }

    private static double rotatedFieldX(double cx, double cy) => cx * 0.83 + cy * 0.37;

    private static double rotatedFieldY(double cx, double cy) => cy * 0.91 - cx * 0.29;

    /// <summary>
    /// Low-frequency world-character field sampled in rotated, domain-warped chunk space. This deliberately breaks
    /// the direct `(cx,cy)` value-noise grid that tends to read as long straight or diagonal chunk bands while staying
    /// pure and seam-stable for server/client regeneration.
    /// </summary>
    private static double warpedChunkField(
        double seedNum,
        int salt,
        double cx,
        double cy,
        double cell,
        double? warpCell = null,
        double warpAmp = 1.8)
    {
        double warpCellValue = warpCell ?? cell * 1.65;
        double x = rotatedFieldX(cx, cy);
        double y = rotatedFieldY(cx, cy);
        double wx =
            (valueNoise((uint)(Js.ToInt32(seedNum) ^ salt ^ FIELD_WARP_X_SALT), x + 31.7, y - 17.3, warpCellValue) - 0.5) *
            warpAmp;
        double wy =
            (valueNoise((uint)(Js.ToInt32(seedNum) ^ salt ^ FIELD_WARP_Y_SALT), x - 23.1, y + 29.9, warpCellValue) - 0.5) *
            warpAmp;
        return valueNoise((uint)(Js.ToInt32(seedNum) ^ salt), x + wx, y + wy, cell);
    }

    private static double seamPortT(double seedNum, double a, double b, int salt)
    {
        double macro = warpedChunkField(seedNum, salt, a, b, 2.8, 5.9, 1.55);
        double detail = warpedChunkField(
            seedNum,
            salt ^ SEAM_DETAIL_SALT,
            a * 1.55 + 11,
            b * 1.55 - 7,
            1.45,
            3.2,
            0.85);
        double jitter = seamHash(seedNum, a, b, salt);
        return clamp01(macro * 0.66 + detail * 0.24 + jitter * 0.1);
    }

    /// <summary>
    /// Draw a chunk's archetype. Predominantly maze, rare arenas (owner's call) — but the weights breathe with
    /// ONE low-frequency, seed-keyed **structure field** over the chunk lattice, so archetypes cluster into
    /// readable DISTRICTS (a cavern field, a hall quarter, warren belts) instead of an i.i.d. shuffle, and with
    /// the shared depth curve, which eases the set-piece beats (boss arenas, calm glades) in past the opening
    /// rings. Deterministic: the fields read only `(seed, chunk coord)` and the draw burns exactly one
    /// `rng.weighted`, so server and client always agree.
    /// </summary>
    private static int chunkKindFor(
        Rng rng,
        double seedNum,
        int cx,
        int cy,
        EndlessWorldProfile profile,
        EndlessSectionSample section)
    {
        double macro = warpedChunkField(seedNum, STRUCTURE_SALT, cx, cy, 5.1, 8.2, 2.4);
        double detail = warpedChunkField(
            seedNum,
            STRUCTURE_DETAIL_SALT,
            cx + 19,
            cy - 31,
            2.05,
            4.4,
            1.35);
        double ridge =
            Math.abs(
                warpedChunkField(seedNum, STRUCTURE_RIDGE_SALT, cx - 7, cy + 13, 6.4, 9.5, 2.2) - 0.5) *
            2;
        double structure = clamp01(macro * 0.64 + detail * 0.25 + (1 - ridge) * 0.11);
        double organic = TerrainKit.smoothstep01((structure - 0.55) / 0.2); // cavern/glade districts (high field)
        double roomy = TerrainKit.smoothstep01((0.44 - structure) / 0.2); // hall/room districts (low field)
        double openness = TerrainKit.smoothstep01(
            (warpedChunkField(seedNum, STRUCTURE_OPENNESS_SALT, cx - 11, cy + 23, 6.2, 9, 2.1) - 0.44) /
            0.24);
        var prog = TerrainKit.endlessProgressionForChunk(cx, cy);
        var w = kindWeightsScratch ??= new double[KIND_DECK.Length];
        var bias = profile.archetypes;
        var local = section.archetypes;
        w[0] = 30 * (1 - 0.62 * organic) * (1 - 0.2 * openness) * bias.warren * local.warren;
        w[1] =
            27 *
            (1 - 0.48 * organic) *
            (1 + 0.7 * roomy) *
            (1 - 0.18 * openness) *
            bias.rooms *
            local.rooms;
        w[2] =
            17 * (1 - 0.42 * organic) * (1 + 0.95 * roomy + 0.35 * openness) * bias.halls * local.halls;
        w[3] = 12 * (1 + 2.9 * organic) * (1 - 0.12 * roomy) * bias.cavern * local.cavern;
        w[4] = 7 * (0.35 + 0.9 * prog.arenas) * (0.65 + 0.75 * openness) * bias.arena * local.arena;
        w[5] =
            6 *
            (0.55 + 1.0 * openness + 0.35 * organic) *
            TerrainKit.smoothstep01((prog.depthChunks - 1.1) / 0.8) *
            bias.glade *
            local.glade;
        w[6] =
            3 *
            (0.55 + 0.8 * roomy + 0.35 * openness) *
            TerrainKit.smoothstep01((prog.depthChunks - 0.75) / 0.7) *
            bias.courtyard *
            local.courtyard;
        w[7] =
            4 *
            (0.7 + 0.85 * organic + 0.4 * (1 - openness)) *
            TerrainKit.smoothstep01((prog.depthChunks - 0.55) / 0.65) *
            bias.ruins *
            local.ruins;
        return rng.weighted(KIND_DECK, w);
    }

    /// <summary>Tiles kept off each chunk corner so a jittered seam port (and its 3-wide mouth) never clips the edge.</summary>
    private const int PORT_MARGIN = 5;

    /// <summary>Deterministic [0,1) hash of a seam identity — both chunks sharing that seam compute the SAME value.</summary>
    private static double seamHash(double seed, double a, double b, int salt)
    {
        int h =
            Js.ToInt32(seed) ^
            Math.imul(Js.ToInt32(a), 0x27d4eb2f) ^
            Math.imul(Js.ToInt32(b), unchecked((int)0x9e3779b9)) ^
            Math.imul(salt, unchecked((int)0x85ebca6b));
        h = Math.imul(h ^ (int)((uint)h >> 15), 0x2c1b3c6d);
        h ^= (int)((uint)h >> 13);
        return (uint)h / 4294967296.0;
    }

    /// <summary>
    /// Row (tile Y) of the crossing on the VERTICAL seam left of chunk column `vx`, in chunk row `cy`. The chunk
    /// to the seam's right reads this as its WEST port; the chunk to its left reads the same value as its EAST
    /// port — so the two always meet, but the crossing is jittered along the edge (no aligned straight lane).
    /// </summary>
    public static int seamRow(double seed, double vx, double cy)
    {
        const int span = ENDLESS_CHUNK_TILES - 1 - PORT_MARGIN * 2;
        return PORT_MARGIN + (int)Math.floor(seamPortT(seed, vx, cy, SEAM_ROW_SALT) * (span + 1));
    }

    /// <summary>
    /// Column (tile X) of the crossing on the HORIZONTAL seam above chunk row `hy`, in chunk column `cx`. The
    /// chunk below reads this as its NORTH port; the chunk above reads the same value as its SOUTH port.
    /// </summary>
    public static int seamCol(double seed, double cx, double hy)
    {
        const int span = ENDLESS_CHUNK_TILES - 1 - PORT_MARGIN * 2;
        return PORT_MARGIN + (int)Math.floor(seamPortT(seed, cx, hy, SEAM_COL_SALT) * (span + 1));
    }

    /// <summary>Deterministic int salt for a chunk's decorative noise (blob wobbles etc.) — pure `(seed,cx,cy,slot)`.</summary>
    private static int chunkSalt(double seed, int cx, int cy, int slot)
    {
        return
            Js.ToInt32(seed) ^
            Math.imul(cx, 0x27d4eb2f) ^
            Math.imul(cy, 0x165667b1) ^
            Math.imul(slot, unchecked((int)0x9e3779b9));
    }

    /// <summary>`Math.max(min, Math.min(max, Math.round(value)))` — always an integral tile coordinate here.</summary>
    private static int clampInt(double value, int min, int max)
    {
        return (int)Math.max(min, Math.min(max, Math.round(value)));
    }

    /// <summary>
    /// Short side pockets carved off maze corridors — readable nooks (an ambush corner, a loot cubby, a moment
    /// of cover) that give the braided labyrinth spatial texture WITHOUT real dead-end traps: one to two tiles
    /// deep, carved strictly INTO solid rock (the far cap must be rock too, so a pocket never merges corridors
    /// or widens the backbone), and never inside the seam-port margin. Pocket floor stays OFF `corridorMask`,
    /// so the river pass never decks a bridge across one.
    /// </summary>
    private static void carveCorridorAlcoves(
        Rng rng,
        byte[] tiles,
        int w,
        int h,
        byte[] corridorMask,
        double density)
    {
        double targetMin = 1 + Math.round(clamp01(density) * 2);
        double targetMax = targetMin + 2 + Math.round(clamp01(density) * 2);
        double target = rng.@int(targetMin, targetMax);
        int placed = 0;
        for (int attempts = 0; attempts < 48 && placed < target; attempts++)
        {
            int tx = (int)rng.@int(PORT_MARGIN + 1, w - PORT_MARGIN - 2);
            int ty = (int)rng.@int(PORT_MARGIN + 1, h - PORT_MARGIN - 2);
            int idx = ty * w + tx;
            if (corridorMask[idx] == 0 || tiles[idx] != TileType.Floor) continue;
            int dir = (int)rng.@int(0, 3);
            int dx = dir == 0 ? 1 : dir == 1 ? -1 : 0;
            int dy = dir == 2 ? 1 : dir == 3 ? -1 : 0;
            int depth = rng.@bool(0.2 + clamp01(density) * 0.42) ? 2 : 1;
            bool ok = true;
            for (int s = 1; s <= depth + 1; s++)
            {
                int nx = tx + dx * s;
                int ny = ty + dy * s;
                if (
                    nx <= 1 ||
                    ny <= 1 ||
                    nx >= w - 2 ||
                    ny >= h - 2 ||
                    tiles[ny * w + nx] != TileType.Solid
                )
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;
            for (int s = 1; s <= depth; s++) tiles[(ty + dy * s) * w + (tx + dx * s)] = TileType.Floor;
            placed++;
        }
    }

    /// <summary>A seam port: the edge tile it crosses at, and whether its inward stub carves horizontally first.</summary>
    private sealed class ChunkPort
    {
        public int tx;
        public int ty;
        /// <summary>WEST/EAST ports head inward horizontally first; NORTH/SOUTH vertically first (so the L bends inward).</summary>
        public bool horiz;
    }

    /// <summary>The four jittered seam ports of chunk (cx,cy) — each shared with the neighbour across that seam.</summary>
    private static List<ChunkPort> chunkPorts(double seed, int cx, int cy)
    {
        const int last = ENDLESS_CHUNK_TILES - 1;
        return new List<ChunkPort>
        {
            new ChunkPort { tx = 0, ty = seamRow(seed, cx, cy), horiz = true }, // WEST  (seam left of this column)
            new ChunkPort { tx = last, ty = seamRow(seed, cx + 1, cy), horiz = true }, // EAST (seam left of the next column)
            new ChunkPort { tx = seamCol(seed, cx, cy), ty = 0, horiz = false }, // NORTH (seam above this row)
            new ChunkPort { tx = seamCol(seed, cx, cy + 1), ty = last, horiz = false }, // SOUTH (seam above the next row)
        };
    }

    /// <summary>
    /// Tiles kept off each chunk edge that a raised rock massif must never touch, so the seam band + port stubs
    /// stay clear for neighbour joining (the basin carve only ADDS floor, so it is bounded by a mere 1-tile ring).
    /// </summary>
    private const int MACRO_MASSIF_BORDER = 3;

    private static bool protectedByRoom(
        IReadOnlyList<DungeonRoom> rooms,
        int tx,
        int ty,
        int margin = 1)
    {
        foreach (var room in rooms)
        {
            var rect = room.rect;
            if (
                tx >= rect.tx - margin &&
                tx < rect.tx + rect.tw + margin &&
                ty >= rect.ty - margin &&
                ty < rect.ty + rect.th + margin
            )
                return true;
        }
        return false;
    }

    /// <summary>
    /// Apply the global macro-landform layer to a freshly-carved chunk (after its connective skeleton exists,
    /// before rivers). Driven purely by the shared `(seed, globalTile)` fields in endlessMacroSample, so
    /// the same landform continues seamlessly across every chunk seam. Two edits, both connectivity-safe by
    /// construction — the braided-maze skeleton (owner's chosen backbone) is never touched:
    ///  1. **Basin / plaza carve** — where macro openness is high, open walls to floor (Solid → Floor). Opening
    ///     ground can only MERGE walkable regions, never sever one, so a big open landform grows across the seam.
    ///  2. **Rock massif raise** — where macro rock is high, fill open floor back to solid (Floor → Solid), but
    ///     ONLY off the corridor skeleton and off the edge band; the caller's reachability repair then drops any
    ///     floor a massif orphaned. Identical in spirit to the existing mountain-outcrop pass, at landform scale.
    /// Eased in with radial depth so the spawn chunk + opening ring stay calm.
    /// </summary>
    private static void applyEndlessMacroLandforms(
        byte[] tiles,
        int w,
        int h,
        int cx,
        int cy,
        byte[] corridorMask,
        IReadOnlyList<DungeonRoom> rooms,
        double seedNum,
        EndlessWorldProfile profile,
        EndlessLandscapeSample landscape)
    {
        var dna = profile.macro;
        double ease = EndlessMacro.endlessMacroEaseAt(Math.hypot(cx, cy));
        if (ease <= 0.001) return;
        int baseGtx = cx * w;
        int baseGty = cy * h;
        // High thresholds on purpose: only STRONG anchor-driven openness/rock (real basins, real massifs) should punch
        // through the maze. The smooth base field alone (~0.5 in plain labyrinth country) must NOT scatter holes into
        // walls and rock into corridors, or the maze dissolves into blobby noise instead of reading as a clean
        // labyrinth. So landforms stay bold and localised while the residual maze keeps its clean corridors/walls.
        double carveThresh = clamp01(1 - 0.44 * ease - landscape.traits.opennessBias * 0.13 * ease);
        double raiseThresh = clamp01(1 - 0.42 * ease - landscape.traits.rockDensity * 0.13 * ease);
        // Basin/plaza carve — Solid → Floor. Runs over the WHOLE grid INCLUDING the chunk's border ring: the openness
        // field is global & continuous, so two chunks meeting at a seam open the same landform on both sides and a
        // basin spans the seam seamlessly. (If the border ring were left solid, a fully-open region would expose the
        // 32-tile chunk boundaries as a hard black grid — the very artifact we are removing.) Opening ground can only
        // merge walkable regions, so the seam ports / connectivity are never harmed; the border stays pure global
        // elevation (≤1-step across the seam), so no invisible wall appears.
        for (int ty = 0; ty < h; ty++)
        {
            for (int tx = 0; tx < w; tx++)
            {
                int idx = ty * w + tx;
                if (tiles[idx] != TileType.Solid) continue;
                var macro = EndlessMacro.endlessMacroSample(seedNum, baseGtx + tx, baseGty + ty, dna);
                double affinity = EndlessLandscape.endlessLandscapeMacroAffinity(landscape, macro.style);
                double openness = macro.openness + affinity * macro.interior * 0.09 * ease;
                if (openness > carveThresh)
                {
                    double strength = clamp01((openness - carveThresh) / Math.max(0.01, 1 - carveThresh));
                    // Even the strongest basin retains a coherent minority of its original walls. The global ridge field
                    // makes these long islands/veins rather than per-tile noise; because the base maze remains underneath,
                    // retaining rock can never sever the guaranteed corridor skeleton.
                    double vein =
                        1 -
                        Math.abs(
                            valueNoise((uint)(Js.ToInt32(seedNum) ^ BASIN_WALL_VEIN_SALT), baseGtx + tx, baseGty + ty, 17) -
                            0.5) *
                        2;
                    double retention = profile.basinWallRetention * (1 - strength * 0.42);
                    if (vein > 1 - retention) continue;
                    tiles[idx] = TileType.Floor;
                }
            }
        }

        // Rock massif raise — Floor → Solid, but ONLY off the corridor skeleton and off the edge band, so the
        // connective backbone + seam joining survive; the caller's reachability repair drops any floor a massif
        // orphaned. The border stays solid where a massif abuts it, which simply continues into the neighbour's solid
        // border (ranges span seams for free — only OPEN basins needed the border-inclusive carve above).
        for (int ty = MACRO_MASSIF_BORDER; ty < h - MACRO_MASSIF_BORDER; ty++)
        {
            for (int tx = MACRO_MASSIF_BORDER; tx < w - MACRO_MASSIF_BORDER; tx++)
            {
                int idx = ty * w + tx;
                if (tiles[idx] != TileType.Floor || corridorMask[idx] != 0 || protectedByRoom(rooms, tx, ty))
                    continue; // never wall off the backbone or authored chambers
                var macro = EndlessMacro.endlessMacroSample(seedNum, baseGtx + tx, baseGty + ty, dna);
                double affinity = EndlessLandscape.endlessLandscapeMacroAffinity(landscape, macro.style);
                if (macro.rock + affinity * macro.interior * 0.09 * ease > raiseThresh)
                {
                    tiles[idx] = TileType.Solid;
                }
            }
        }

        // Tight-pass and column settings add coherent rock texture only away from the guaranteed route skeleton.
        // The route itself is five tiles wide in the base generator (and receives a three-tile collar in the Chasm
        // pass), so even the densest alpine/ridge blend cannot collapse into a one-cell traversal slit.
        double confinement = Math.max(0, landscape.traits.pathConfinement);
        double pillars = Math.max(0, landscape.traits.pillarDensity);
        if (confinement > 0.05 || pillars > 0.05)
        {
            for (int ty = MACRO_MASSIF_BORDER; ty < h - MACRO_MASSIF_BORDER; ty++)
            {
                for (int tx = MACRO_MASSIF_BORDER; tx < w - MACRO_MASSIF_BORDER; tx++)
                {
                    int idx = ty * w + tx;
                    if (tiles[idx] != TileType.Floor || corridorMask[idx] != 0 || protectedByRoom(rooms, tx, ty))
                        continue;
                    int gtx = baseGtx + tx;
                    int gty = baseGty + ty;
                    double wallCountry =
                        valueNoise((uint)(Js.ToInt32(seedNum) ^ STRUCTURE_RIDGE_SALT), gtx, gty, 21) * 0.68 +
                        valueNoise((uint)(Js.ToInt32(seedNum) ^ BASIN_WALL_VEIN_SALT), gtx + 9.5, gty - 13.5, 8) * 0.32;
                    double columnCountry =
                        valueNoise((uint)(Js.ToInt32(seedNum) ^ CHASM_DETAIL_SALT), gtx - 27.25, gty + 5.75, 7) * 0.72 +
                        valueNoise((uint)(Js.ToInt32(seedNum) ^ CHASM_BASIN_SALT), gtx, gty, 25) * 0.28;
                    if (wallCountry > 0.86 - confinement * 0.15 || columnCountry > 0.93 - pillars * 0.12)
                    {
                        tiles[idx] = TileType.Solid;
                    }
                }
            }
        }
    }

    /// <summary>Mark the shortest existing walkable route tree from the first port to every other reachable port.</summary>
    private static byte[] endlessPortRouteProtectionMask(
        byte[] tiles,
        int w,
        int h,
        IReadOnlyList<ChunkPort> ports,
        int collar,
        Action<int>? onProtect = null)
    {
        var protectedMask = new byte[tiles.Length];
        var root = ports.Count > 0 ? ports[0] : null;
        if (root == null) return protectedMask;
        int rootIndex = root.ty * w + root.tx;
        if (!isWalkable(tiles[rootIndex])) return protectedMask;

        // One BFS owns the shortest-path tree for all ports; tracing four targets must not pay four full searches.
        var previous = new int[tiles.Length].fill(-1);
        var queue = new int[tiles.Length];
        int head = 0;
        int tail = 0;
        queue[tail++] = rootIndex;
        previous[rootIndex] = rootIndex;
        while (head < tail)
        {
            int index = queue[head++];
            int tx = index % w;
            int ty = index / w;
            foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx;
                if (previous[ni] >= 0 || !isWalkable(tiles[ni])) continue;
                previous[ni] = index;
                queue[tail++] = ni;
            }
        }
        foreach (var target in ports)
        {
            int targetIndex = target.ty * w + target.tx;
            if (previous[targetIndex] < 0) continue;
            for (int index = targetIndex; ; index = previous[index])
            {
                int tx = index % w;
                int ty = index / w;
                for (int dy = -collar; dy <= collar; dy++)
                {
                    for (int dx = -collar; dx <= collar; dx++)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        protectedMask[ni] = 1;
                        onProtect?.Invoke(ni);
                    }
                }
                if (index == rootIndex) break;
            }
        }
        return protectedMask;
    }

    /// <summary>
    /// Turn strong alpine/ridge recipes into actual narrow-pass country. The global graph initially contains many
    /// useful loops; this pass keeps a shortest route tree between all four agreed ports, protects a full 3x3 collar
    /// around every route cell (including bend diagonals), then closes only a coherent share of optional loops.
    /// Rooms and the two-tile seam band remain untouched. Removed optional cells are also removed from corridorMask,
    /// so the later river reconnection cannot accidentally reopen them as mandatory paths.
    /// </summary>
    private static void applyLandscapePathConfinement(
        byte[] tiles,
        int w,
        int h,
        int cx,
        int cy,
        byte[] corridorMask,
        IReadOnlyList<DungeonRoom> rooms,
        IReadOnlyList<ChunkPort> ports,
        double seedNum,
        EndlessLandscapeSample landscape)
    {
        // Dense hand-authored countries need the same route-tree treatment even when their fantasy is not literally
        // a narrow pass. `openGroundCap` turns the target density into a bounded confinement pressure; genuinely open
        // moors/deltas remain untouched while terraces, ruins and stone country shed optional maze loops coherently.
        // Broad lake/delta countries pay their composition with Water in the hydrology stage; closing their future
        // lake bed here would turn an authored basin into scattered puddles. Literal pass confinement still wins.
        double densityConfinement =
            clamp01((0.68 - landscape.traits.openGroundCap) / 0.26) *
            (1 - clamp01(landscape.traits.lakeStrength) * 0.85);
        double confinement = Math.max(landscape.traits.pathConfinement, densityConfinement);
        if (confinement <= 0.18) return;
        var protectedMask = endlessPortRouteProtectionMask(tiles, w, h, ports, 1, index =>
        {
            // A "protected" collar is not enough here: if a pre-existing route brushes a rock face, retaining only its
            // open neighbours can still leave a narrow throat. Carve the complete square collar so alpine routes own a
            // real three-tile minimum width, including diagonals at bends.
            tiles[index] = TileType.Floor;
            corridorMask[index] = 1;
        });

        double strength = clamp01((confinement - 0.12) / 0.88);
        int baseGtx = cx * w;
        int baseGty = cy * h;
        for (int ty = 2; ty < h - 2; ty++)
        {
            for (int tx = 2; tx < w - 2; tx++)
            {
                int index = ty * w + tx;
                if (
                    tiles[index] != TileType.Floor ||
                    protectedMask[index] != 0 ||
                    protectedByRoom(rooms, tx, ty, 2)
                )
                    continue;
                int gtx = baseGtx + tx;
                int gty = baseGty + ty;
                double closure =
                    valueNoise((uint)(Js.ToInt32(seedNum) ^ STRUCTURE_RIDGE_SALT), gtx, gty, 18) * 0.7 +
                    valueNoise((uint)(Js.ToInt32(seedNum) ^ STRUCTURE_DETAIL_SALT), gtx + 7.5, gty - 11.5, 7) * 0.3;
                if (closure <= 0.81 - strength * 0.43) continue;
                tiles[index] = TileType.Solid;
                corridorMask[index] = 0;
            }
        }
    }

    /// <summary>
    /// Apply the cheap, globally continuous skyline profile to EVERY Solid cell, including the spawn clearing and
    /// the deterministic fallback. The opening remains topologically calm (no water/massif carve at the origin),
    /// but its framing walls still show the run's vertical identity; suppressing this layer until chunk three was
    /// why the complete first gameplay view read as one equal-height enclosure.
    ///
    /// `buildStandardElevationField` has already established the collision-safe floor and minimum wall bases. This
    /// pass touches blocked cells exclusively, so it cannot change reachability or introduce a hidden walkable step.
    /// At the absolute ceiling it may lower a wall's stored base by several bands while its standard wall rise
    /// remains above adjacent ground. That preserves walkable summit plateaus but prevents a whole high country from
    /// becoming one repeated MAX slab. The shared global fields also mean a fallback and its ordinary neighbours
    /// agree on the same skyline rather than exposing the degraded chunk as a flat rectangular slab.
    /// </summary>
    private static double endlessStoredWallTerraceLevelAt(
        double seed,
        int gtx,
        int gty,
        MacroDna dna,
        double ground)
    {
        double shelf = EndlessMacro.endlessWallTerraceLevelAt(seed, gtx, gty, dna, ground, MAX_ELEVATION);
        if (shelf <= 0) return shelf;

        // The standard terrain model adds a broad 0/1-level skyline lift to Solid surfaces. Compensate that lift in
        // the stored base whenever the lower integer lands on the exact same visual shelf. Without this inverse step,
        // every coherent geological terrace would be re-sliced into four-tile, one-level contour-paper patches during
        // materialization. Physics still sees a Solid tile, and its final visible top remains shelf + base wall rise.
        double baseRise = TerrainRules.TERRAIN_ENDLESS_WALL_BASE_RISE;
        double compensated = shelf - 1;
        double compensatedSurface = compensated + TerrainRules.endlessWallRiseAt(gtx, gty, compensated);
        return Math.abs(compensatedSurface - (shelf + baseRise)) < 0.001 ? compensated : shelf;
    }

    private static void applyEndlessWallHeightProfile(
        byte[] tiles,
        sbyte[] elevation,
        int width,
        int height,
        int baseTx,
        int baseTy,
        double seed,
        MacroDna dna,
        Func<int, int, double> groundLevelAt)
    {
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int idx = ty * width + tx;
                if (tiles[idx] != TileType.Solid) continue;
                int gtx = baseTx + tx;
                int gty = baseTy + ty;
                double ground = groundLevelAt(gtx, gty);
                elevation[idx] = Js.I8(endlessStoredWallTerraceLevelAt(seed, gtx, gty, dna, ground));
            }
        }
    }

    /// <summary>
    /// Generate endless chunk (cx,cy) for a run. Pure &amp; reproducible from `(seedNum, cx, cy)` — the server and
    /// client regenerate byte-identical geometry. `biomeKey`/`tier` are stamped for theming/scaling.
    /// </summary>
    public static DungeonLayout generateEndlessChunkAt(
        double seedNum,
        int cx,
        int cy,
        string biomeKey,
        int tier,
        int generationVersion = EndlessCountry.ENDLESS_GENERATION_VERSION,
        double? spineSeed = null)
    {
        spineSeed ??= seedNum;
        if (generationVersion >= EndlessCountry.ENDLESS_COUNTRY_GENERATION_VERSION)
            return EndlessCountry.generateEndlessCountryChunkAt(
                seedNum,
                cx,
                cy,
                biomeKey,
                tier,
                spineSeed.Value,
                generationVersion);
        const int w = ENDLESS_CHUNK_TILES;
        const int h = ENDLESS_CHUNK_TILES;
        const double ts = TILE_SIZE;
        double originX = endlessChunkOriginX(cx);
        double originY = endlessChunkOriginY(cy);
        var tiles = new byte[w * h]; // all Solid
        // The connective "skeleton": every corridor/port-stub tile is flagged here as it is carved, so the river
        // pass can bridge water across the maze (the generalization of the old fixed "+" cross — see terrain.ts).
        var corridorMask = new byte[w * h];
        var rng = new Rng($"{Js.Str(seedNum)}:{Js.Str(cx)}:{Js.Str(cy)}");
        const int cxTile = w >> 1;
        const int cyTile = h >> 1;
        bool isSpawn = cx == 0 && cy == 0;
        // The run's macro-landform World-DNA (its structural signature) — read from the run theme key, mirrors the
        // per-run terrain/river profile. Drives the cross-chunk basins/ranges, the ground relief and the maze pitch.
        var worldProfile = EndlessWorld.endlessWorldProfileFor(seedNum, biomeKey);
        var section = EndlessWorld.endlessSectionSampleAt(seedNum, cx, cy, worldProfile.cadence);
        var landscape = EndlessLandscape.endlessLandscapeSampleAt(seedNum, cx, cy);
        var dna = worldProfile.macro;
        var terrainProfile = Terrain.terrainProfileFor(biomeKey);
        var verticalField = EndlessVertical.createEndlessVerticalField(new EndlessVerticalFieldOptions
        {
            seed = seedNum,
            baseTx = cx * w,
            baseTy = cy * h,
            width = w,
            height = h,
            landscapeCellSize = ENDLESS_CHUNK_TILES,
            profile = terrainProfile.elevation,
            dna = dna,
            maxLevel = MAX_ELEVATION,
        });

        // Radial distance from the spawn (chunk 0,0), used only as a gen-time proxy "depth" to gate which
        // encounter beats can appear this far out; the *difficulty* of a fired beat is applied by the server
        // from the cohort's live progress, so this stays direction-agnostic.
        double radialDepth = Math.hypot(cx, cy) * ENDLESS_CHUNK_WORLD;

        var rooms = new List<DungeonRoom>();
        void mkRoom(int type, TileRect rect, double threat, double? encounter = null)
        {
            rooms.push(new DungeonRoom
            {
                id = rooms.Count,
                type = type,
                rect = rect,
                cx = originX + (rect.tx + rect.tw / 2.0) * ts,
                cy = originY + (rect.ty + rect.th / 2.0) * ts,
                doorIds = new List<int>(),
                threat = threat,
                encounter = encounter,
            });
        }

        // The four jittered seam ports. The spawn chunk is an open clearing; everything else draws an archetype
        // from the district-biased deck.
        var ports = chunkPorts(seedNum, cx, cy);
        int kind = isSpawn
            ? ChunkKind.Clearing
            : chunkKindFor(rng, seedNum, cx, cy, worldProfile, section);

        // Carve a port's inward stub to a target tile, recording it on the corridor skeleton (so it bridges rivers).
        void linkPort(ChunkPort p, int tx, int ty)
        {
            double wanderChance = Math.min(
                0.96,
                0.2 + section.traits.pathWander * 0.48 + landscape.traits.pathWander * 0.3);
            if (!rng.@bool(wanderChance))
            {
                carveCorridor(tiles, w, h, p.tx, p.ty, tx, ty, Bsp.CORRIDOR_THICKNESS, p.horiz, corridorMask);
                return;
            }
            int dirX = p.tx == 0 ? 1 : p.tx == w - 1 ? -1 : 0;
            int dirY = p.ty == 0 ? 1 : p.ty == h - 1 ? -1 : 0;
            int anchorX = clampInt(
                p.tx + dirX * rng.@int(5, 8) + (dirY != 0 ? rng.@int(-2, 2) : 0),
                2,
                w - 3);
            int anchorY = clampInt(
                p.ty + dirY * rng.@int(5, 8) + (dirX != 0 ? rng.@int(-2, 2) : 0),
                2,
                h - 3);
            int midX = clampInt((anchorX + tx) / 2.0 + rng.@int(-3, 3), 3, w - 4);
            int midY = clampInt((anchorY + ty) / 2.0 + rng.@int(-3, 3), 3, h - 4);
            carveCorridor(
                tiles,
                w,
                h,
                p.tx,
                p.ty,
                anchorX,
                anchorY,
                Bsp.CORRIDOR_THICKNESS,
                p.horiz,
                corridorMask);
            carveCorridor(
                tiles,
                w,
                h,
                anchorX,
                anchorY,
                midX,
                midY,
                Bsp.CORRIDOR_THICKNESS,
                rng.@bool(),
                corridorMask);
            carveCorridor(tiles, w, h, midX, midY, tx, ty, Bsp.CORRIDOR_THICKNESS, rng.@bool(), corridorMask);
        }

        if (kind == ChunkKind.Clearing || kind == ChunkKind.Arena)
        {
            // One big open room. The arena spans almost the whole chunk and carries a boss marker; the spawn
            // clearing is smaller (so the cohort still leaves through a chosen, jittered port into the maze beyond).
            if (kind == ChunkKind.Arena)
            {
                // The rare boss set piece is an ORGANIC bowl (a wobbled blob, not a stamped rectangle), so every
                // arena's rim reads differently while the fight space at the centre stays guaranteed open.
                const int m = 2;
                TerrainKit.carveOrganicBlob(
                    tiles,
                    w,
                    h,
                    new OrganicZone { tx = cxTile, ty = cyTile, rx = (w >> 1) - m - 1, ry = (h >> 1) - m - 1 },
                    chunkSalt(seedNum, cx, cy, 5),
                    new OrganicBlobOptions { minBorder = m });
                mkRoom(DungeonRoomType.Boss, new TileRect(cxTile - 5, cyTile - 5, 10, 10), 1);
            }
            else
            {
                const int m = 6;
                carveRect(tiles, w, h, new TileRect(m, m, w - m * 2, h - m * 2));
            }
            foreach (var p in ports) linkPort(p, cxTile, cyTile); // wire each port into the open centre
        }
        else if (kind == ChunkKind.Glade)
        {
            // A calm organic glade — the run's breathing beat between maze districts: one open meadow pocket, NO
            // encounter rooms (ambient spawn pressure still applies), all four ports feeding it. Smaller than a
            // boss arena so it reads as a clearing in the rock, not a set piece.
            TerrainKit.carveOrganicBlob(
                tiles,
                w,
                h,
                new OrganicZone { tx = cxTile, ty = cyTile, rx = (int)rng.@int(8, 10), ry = (int)rng.@int(7, 9) },
                chunkSalt(seedNum, cx, cy, 7),
                new OrganicBlobOptions { minBorder = 3 });
            foreach (var p in ports) linkPort(p, cxTile, cyTile);
        }
        else if (kind == ChunkKind.Cavern)
        {
            // An organic cellular-automata cavern; trench each seam port into its body so the chunk stays linked.
            var cave = Cave.generateCaveGrid(rng, w, h, tier);
            int sx = 0;
            int sy = 0;
            int n = 0;
            for (int ty = 1; ty < h - 1; ty++)
            {
                for (int tx = 1; tx < w - 1; tx++)
                {
                    if (cave.tiles[ty * w + tx] != TileType.Floor) continue;
                    tiles[ty * w + tx] = TileType.Floor;
                    sx += tx;
                    sy += ty;
                    n++;
                }
            }
            int cnx = n > 0 ? (int)Math.round((double)sx / n) : cxTile;
            int cny = n > 0 ? (int)Math.round((double)sy / n) : cyTile;
            foreach (var p in ports) linkPort(p, cnx, cny);
        }
        else
        {
            // Maze archetypes (Warren / Rooms / Halls): the shared GLOBAL node-graph labyrinth (see endlessMaze.ts) —
            // an irregular jittered-lattice of sites wired by a Relative-Neighbourhood + braided-Gabriel graph and
            // carved as organic curved corridors that flow across the seams. The three kinds share ONE graph (its
            // topology is a pure global function, so junctions never form a raster and the pattern never resets at a
            // seam); they differ only in DECORATION. Regional density/room-size come from the global maze-openness
            // field, so a chunk's kind never changes the seam geometry.
            var sites = EndlessMaze.carveEndlessMaze(
                tiles,
                w,
                h,
                cx * w,
                cy * h,
                seedNum,
                mazeTuningFor(worldProfile),
                corridorMask);

            // Wire each seam port into its nearest graph site, so every port folds into the maze body. (The dry
            // connectivity closure below then guarantees all four ports end up on one connected component.)
            foreach (var p in ports)
            {
                MazeSite? nearest = null;
                double nearestD = double.PositiveInfinity;
                foreach (var s in sites)
                {
                    double slx = s.gx - cx * w;
                    double sly = s.gy - cy * h;
                    double d = Math.pow(slx - p.tx, 2) + Math.pow(sly - p.ty, 2);
                    if (d < nearestD)
                    {
                        nearestD = d;
                        nearest = s;
                    }
                }
                int tx = nearest != null ? clampInt(nearest.gx - cx * w, 2, w - 3) : cxTile;
                int ty = nearest != null ? clampInt(nearest.gy - cy * h, 2, h - 3) : cyTile;
                linkPort(p, tx, ty);
            }

            // Decorate (never blocking a corridor): Rooms stamp encounter markers on a share of junctions; Halls raise
            // cover pillars beside interior junctions (off the seam band, off the corridor skeleton).
            if (kind == ChunkKind.Rooms)
            {
                int authoredRooms = 0;
                double roomLimit = 2 + Math.round(section.traits.roomDensity * 3);
                foreach (var s in sites)
                {
                    if (authoredRooms >= roomLimit) break;
                    int lx = (int)Math.round(s.gx - cx * w);
                    int ly = (int)Math.round(s.gy - cy * h);
                    if (lx < 5 || ly < 5 || lx >= w - 5 || ly >= h - 5) continue;
                    if (authoredRooms > 0 && !rng.@bool(0.18 + section.traits.roomDensity * 0.52)) continue;
                    double roomRadiusMax = Math.max(3, Math.min(4, Math.round(2.3 + section.traits.roomScale)));
                    int rx = (int)rng.@int(2, roomRadiusMax);
                    int ry = (int)rng.@int(2, roomRadiusMax);
                    // A Rooms chunk now contains real spatial chambers rather than 3x3 metadata laid over the same warren
                    // corridor. The organic wall is compact enough to retain maze pressure but large enough to stage an
                    // encounter, and the room record protects it from later massif/water/chasm passes.
                    TerrainKit.carveOrganicBlob(
                        tiles,
                        w,
                        h,
                        new OrganicZone { tx = lx, ty = ly, rx = rx, ry = ry },
                        chunkSalt(seedNum, cx, cy, 30 + authoredRooms),
                        new OrganicBlobOptions { minBorder = 3, wobble = 0.2 });
                    mkRoom(
                        DungeonRoomType.Combat,
                        new TileRect(lx - rx, ly - ry, rx * 2 + 1, ry * 2 + 1),
                        0.55,
                        consumeRetiredEncounterDraws(rng));
                    authoredRooms++;
                }
            }
            else if (kind == ChunkKind.Halls)
            {
                int authoredHalls = 0;
                double hallLimit = 1 + Math.round(section.traits.roomDensity * 3);
                foreach (var s in sites)
                {
                    if (authoredHalls >= hallLimit) break;
                    int lx = (int)Math.round(s.gx - cx * w);
                    int ly = (int)Math.round(s.gy - cy * h);
                    if (lx < 6 || ly < 6 || lx >= w - 6 || ly >= h - 6) continue;
                    if (authoredHalls > 0 && !rng.@bool(0.14 + section.traits.roomDensity * 0.42)) continue;
                    bool horizontal = rng.@bool();
                    double longMax = Math.max(5, Math.min(7, Math.round(4 + section.traits.roomScale * 1.7)));
                    int rx = horizontal ? (int)rng.@int(4, longMax) : 2;
                    int ry = horizontal ? 2 : (int)rng.@int(4, longMax);
                    TerrainKit.carveOrganicBlob(
                        tiles,
                        w,
                        h,
                        new OrganicZone { tx = lx, ty = ly, rx = rx, ry = ry },
                        chunkSalt(seedNum, cx, cy, 40 + authoredHalls),
                        new OrganicBlobOptions { minBorder = 3, wobble = 0.12 });
                    // Twin off-axis pillars create line-of-sight/cover play while the original graph corridor through the
                    // centre remains sacred. Their separation and hall orientation vary per chunk.
                    foreach (int sign in new[] { -1, 1 })
                    {
                        int px = lx + (horizontal ? sign * Math.max(2, rx - 1) : sign * (rng.@bool() ? 1 : 2));
                        int py = ly + (horizontal ? sign * (rng.@bool() ? 1 : 2) : sign * Math.max(2, ry - 1));
                        int idx = py * w + px;
                        if (corridorMask[idx] == 0 && tiles[idx] == TileType.Floor) tiles[idx] = TileType.Solid;
                    }
                    mkRoom(
                        DungeonRoomType.Combat,
                        new TileRect(lx - rx, ly - ry, rx * 2 + 1, ry * 2 + 1),
                        0.62,
                        consumeRetiredEncounterDraws(rng));
                    authoredHalls++;
                }
            }
            else if (kind == ChunkKind.Courtyard)
            {
                // A broad non-boss combat court with a broken central monument. The maze still runs underneath and every
                // raised cover cell stays off its corridor mask, so the court is a landmark without becoming a gate.
                var anchor = sites.Count > 0 ? sites[0] : null;
                double bestDistance = Number.POSITIVE_INFINITY;
                foreach (var site in sites)
                {
                    // (`lx`/`ly` in the TS loop body; renamed because C# forbids shadowing the court's lx/ly below.)
                    double siteLx = site.gx - cx * w;
                    double siteLy = site.gy - cy * h;
                    if (siteLx < 7 || siteLy < 7 || siteLx >= w - 7 || siteLy >= h - 7) continue;
                    double distance = Math.pow(siteLx - cxTile, 2) + Math.pow(siteLy - cyTile, 2);
                    if (distance < bestDistance)
                    {
                        anchor = site;
                        bestDistance = distance;
                    }
                }
                int lx = anchor != null ? clampInt(anchor.gx - cx * w, 8, w - 9) : cxTile;
                int ly = anchor != null ? clampInt(anchor.gy - cy * h, 8, h - 9) : cyTile;
                int rx = clampInt(4.5 + section.traits.roomScale * 1.7, 5, 7);
                int ry = clampInt(4 + section.traits.roomScale * 1.45, 5, 7);
                TerrainKit.carveOrganicBlob(tiles, w, h, new OrganicZone { tx = lx, ty = ly, rx = rx, ry = ry }, chunkSalt(seedNum, cx, cy, 50), new OrganicBlobOptions
                {
                    minBorder = 3,
                    wobble = 0.1,
                });
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (Math.abs(dx) + Math.abs(dy) > 3) continue;
                        int idx = (ly + dy) * w + lx + dx;
                        if (corridorMask[idx] != 0 || tiles[idx] != TileType.Floor) continue;
                        if (Math.abs(dx) + Math.abs(dy) <= 1 || rng.@bool(section.traits.coverDensity * 0.48))
                            tiles[idx] = TileType.Solid;
                    }
                }
                mkRoom(
                    DungeonRoomType.Combat,
                    new TileRect(lx - rx, ly - ry, rx * 2 + 1, ry * 2 + 1),
                    0.68,
                    consumeRetiredEncounterDraws(rng));
            }
            else if (kind == ChunkKind.Ruins)
            {
                // Several overlapping courts form an irregular ruin quarter. Rubble is placed only beside, never on, the
                // global graph so this section gains cover-rich micro-paths without compromising its seamless backbone.
                int authoredRuins = 0;
                double ruinLimit = 2 + Math.round(section.traits.roomDensity * 2);
                foreach (var site in sites)
                {
                    if (authoredRuins >= ruinLimit) break;
                    int lx = (int)Math.round(site.gx - cx * w);
                    int ly = (int)Math.round(site.gy - cy * h);
                    if (lx < 5 || ly < 5 || lx >= w - 5 || ly >= h - 5) continue;
                    if (authoredRuins > 0 && !rng.@bool(0.34 + section.traits.roomDensity * 0.36)) continue;
                    int rx = (int)rng.@int(2, Math.max(3, Math.min(4, Math.round(section.traits.roomScale * 3))));
                    int ry = (int)rng.@int(2, Math.max(3, Math.min(4, Math.round(section.traits.roomScale * 3))));
                    TerrainKit.carveOrganicBlob(
                        tiles,
                        w,
                        h,
                        new OrganicZone { tx = lx, ty = ly, rx = rx, ry = ry },
                        chunkSalt(seedNum, cx, cy, 60 + authoredRuins),
                        new OrganicBlobOptions { minBorder = 3, wobble = 0.28 });
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        int px = clampInt(lx + rng.@int(-rx, rx), 3, w - 4);
                        int py = clampInt(ly + rng.@int(-ry, ry), 3, h - 4);
                        int idx = py * w + px;
                        if (
                            corridorMask[idx] == 0 &&
                            tiles[idx] == TileType.Floor &&
                            rng.@bool(section.traits.coverDensity)
                        )
                            tiles[idx] = TileType.Solid;
                    }
                    mkRoom(
                        DungeonRoomType.Combat,
                        new TileRect(lx - rx, ly - ry, rx * 2 + 1, ry * 2 + 1),
                        0.58,
                        consumeRetiredEncounterDraws(rng));
                    authoredRuins++;
                }
            }
        }
        // Whether this maze slice split into more than one walkable component at the chunk clip (a graph edge whose
        // only route runs through a halo site outside the chunk), unify it into ONE body — so all four ports survive
        // the reachability repair and no ground is dropped to a rectangular fallback. Maze kinds only (the organic
        // kinds are single blobs already, kept on the old drop-orphans behaviour).
        if (
            kind == ChunkKind.Warren ||
            kind == ChunkKind.Rooms ||
            kind == ChunkKind.Halls ||
            kind == ChunkKind.Courtyard ||
            kind == ChunkKind.Ruins
        )
        {
            unifyChunkBody(tiles, w, h, corridorMask);
        }

        // Spatial texture for the tight maze beats: short side pockets off the corridors (cover corners, loot
        // nooks) — "light dead ends" that never trap (1-2 tiles deep) and never touch the seam-port margin.
        if (kind == ChunkKind.Warren || kind == ChunkKind.Rooms || kind == ChunkKind.Ruins)
        {
            carveCorridorAlcoves(rng, tiles, w, h, corridorMask, section.traits.alcoveDensity);
        }

        // The universal repair / bridge seed: the WEST port's first inward tile is ALWAYS a carved corridor (it is
        // on every archetype's west stub), so it is Floor before rivers and walkable (Floor or a bridge) after —
        // and it is connected to the whole chunk. Both the dry repair below and the river repair seed from it.
        const int seedTx = 1;
        int seedTy = ports[0].ty;

        // Connectivity repair: drop any floor not reachable from the connected body (no orphan pockets / traps).
        repairFrom(tiles, w, h, seedTx, seedTy);

        // Macro-landform layer: cross-chunk open basins/arenas and rock ranges from the shared global fields, so the
        // world stops reading as a uniform per-chunk maze. Skipped on the spawn chunk (kept a clean clearing); the
        // basin carve only merges, the massif raise stays off the skeleton, then re-repair drops any orphaned floor.
        if (!isSpawn)
        {
            applyEndlessMacroLandforms(
                tiles,
                w,
                h,
                cx,
                cy,
                corridorMask,
                rooms,
                seedNum,
                worldProfile,
                landscape);
            repairFrom(tiles, w, h, seedTx, seedTy);
            applyLandscapePathConfinement(
                tiles,
                w,
                h,
                cx,
                cy,
                corridorMask,
                rooms,
                ports,
                seedNum,
                landscape);
            repairFrom(tiles, w, h, seedTx, seedTy);

            // Align topology with the height composition. Outside the guaranteed graph/routes, selected high-side
            // contours become solid terrace rims; route collars remain broad, deliberate ramps through those cliffs.
            var escarpmentRouteMask = endlessPortRouteProtectionMask(tiles, w, h, ports, 1);
            EndlessVertical.applyEndlessEscarpments(tiles, w, h, new ApplyEndlessEscarpmentOptions
            {
                seed = seedNum,
                baseTx = cx * w,
                baseTy = cy * h,
                routeMask = escarpmentRouteMask,
                rooms = rooms,
                field = verticalField,
            });
            repairFrom(tiles, w, h, seedTx, seedTy);
        }

        // Biome landform: a mountain biome cuts rivers (bridged across the maze corridors) and raises rock
        // outcrops; riverless biomes are a no-op. The spawn chunk stays a clean clearing (never spawn against water).
        // Preserve the native landform contract before horizontal water is painted. Chasms are evaluated from this
        // snapshot after bridges/navigation have been finalized, so a river can create a waterfall at a rift but can
        // never erase the rift merely because this generator happens to run its water pass first.
        var preRiverTiles = tiles.slice();
        if (terrainProfile.rivers != null && !isSpawn)
        {
            Terrain.applyMountainTerrain(
                tiles,
                w,
                h,
                cx * w,
                cy * h,
                corridorMask,
                seedTx,
                seedTy,
                rooms,
                seedNum,
                terrainProfile.rivers,
                new EndlessTerrainVariation
                {
                    waterBias = landscape.traits.waterBias,
                    lakeStrength = landscape.traits.lakeStrength,
                    channelComplexity = landscape.traits.channelComplexity,
                    islandStrength = landscape.traits.islandStrength,
                });
            // Re-unify the chunk across the river. The shared mountain pass bridges water on the corridor skeleton,
            // but its spine-trim / minimum-thickness passes (tuned for broad authored layouts) can drop a crossing on
            // the maze's thin corridors — severing the chunk so the reachability repair below then solidifies the
            // whole far side (a near-solid "fake wall" chunk with dead seam ports). This re-carves any corridor the
            // repair solidified and lays a MINIMAL thin deck across each remaining severance, restoring the maze as
            // one connected body without undoing the pass's deliberate broad-river / minority-bridge shaping. The
            // four seam ports sit on this skeleton, so they all stay reachable — no walled-off chunk, no dead seam.
            // Re-open the four seam ports FIRST (so they are walkable stubs the reconnection can fold into the body),
            // then re-unify — guaranteeing every port ends up on the one connected component.
            restoreSeamPorts(tiles, w, h, ports);
            reconnectChunkBody(tiles, w, h, corridorMask);
            // Keep only crossings connectivity actually needs. Water remains a real route barrier, but its authored
            // channel no longer expands through rock into a lake-sized horizontal plane.
            // The neighbour chunk is not available yet. Preserve all edge candidates through Chasm composition; the
            // final sweep below can distinguish the actual shared ports without perturbing Water/Chasm placement.
            thinBridgeDecks(tiles, w, h, seedTx, seedTy, ports, true);
            restoreSeamPorts(tiles, w, h, ports); // re-guarantee the four port crossings after thinning
        }

        // Final connectivity guarantee. The river pass's own repair only re-solidifies orphan *floor*; a river can
        // still strand a *bridge* deck (an island the water cut off on both banks). Re-flood the WALKABLE graph
        // (floor + bridge) from the connected west-port seed and solidify anything it cannot reach, so every chunk
        // is exactly one navigable body — no trapped pocket, no stray deck — and every seam port (wired to the
        // connected centre before the river) survives, so neighbours always join across the seam.
        if (!isSpawn)
        {
            repairWalkableFrom(tiles, w, h, seedTx, seedTy);
            // A broad lake may exceed the ordinary reconnection budget. Seam ports are world connectivity contracts,
            // so restore only a disconnected port along the already-connected maze skeleton before Chasms are placed;
            // the rift pass can then protect that route like every other real navigation path.
            ensureSeamPortsConnected(tiles, w, h, corridorMask, ports);
            limitExcessiveOpenCountry(
                tiles,
                w,
                h,
                rooms,
                seedNum,
                cx,
                cy,
                ports,
                verticalField,
                Math.min(
                    0.82,
                    landscape.traits.openGroundCap +
                    (kind == ChunkKind.Arena ? 0.18 : kind == ChunkKind.Glade ? 0.12 : 0)));
            repairWalkableFrom(tiles, w, h, seedTx, seedTy);
        }

        // The world-space rift field is a third native landform over open floor, rock and water. A padded corridor
        // skeleton plus room courts stay protected; the pass itself reconnects any decorative floor loop it cuts.
        byte[]? preChasmTiles = null;
        if (!isSpawn)
        {
            preChasmTiles = tiles.slice();
            applyEndlessChasms(
                tiles,
                preRiverTiles,
                w,
                h,
                cx,
                cy,
                corridorMask,
                rooms,
                ports,
                seedNum,
                worldProfile,
                landscape,
                preChasmTiles);
            // The topology gate can restore a rejected rift cell to its exact pre-Chasm Floor/Bridge byte. In the rare
            // case where the earlier rift reconnection had already removed the only route to that byte, restoring it
            // would reintroduce a one-cell walkable island. Chasm shape is final at this point, so a last walkable-only
            // repair safely solidifies such islands without cutting, shrinking, or otherwise changing any accepted rift.
            repairWalkableFrom(tiles, w, h, seedTx, seedTy);
        }

        // Extremely dense intersections of massif + lake + rift must remain playable country, not a technically
        // connected one-tile thread. This guard only activates below 16% walkable area and expands into interior rock;
        // it never erases Water/Chasm, touches a seam, or makes ordinary chunks more open.
        if (!isSpawn) ensureMinimumWalkableCountry(tiles, w, h, Js.ToInt32(seedNum) ^ chunkSalt(seedNum, cx, cy, 91));

        // Thicken truly LAST. Connectivity and Chasm topology can legitimately remove an unreachable bank or restore
        // a pre-rift deck byte after the first river normalization; doing the width pass before those operations left
        // a rare deterministic one-cell bridge fringe that failed final validation and forced the whole chunk into
        // its safe fallback. Every widening cell is cardinally attached to an already-connected deck, while the Chasm
        // pass protects a one-cell bridge halo, so this cannot create an island or erase an accepted rift.
        if (terrainProfile.rivers != null && !isSpawn)
        {
            // Chasm restoration and seam repair can change whether a previously-kept crossing is still required. Run
            // the necessity sweep again against the final landform before thickening the surviving landmark decks.
            var beforeNecessitySweep = tiles.slice();
            thinBridgeDecks(tiles, w, h, seedTx, seedTy, ports);
            TerrainBridge.enforceMinimumBridgeThickness(tiles, w, h, new BridgeThicknessOptions { maxPasses = 16 });
            // A rare deck framed tightly by Chasm cannot widen after thinning. Keep the already-valid pre-sweep deck in
            // that case; necessity pruning is a quality optimization and may never trigger a whole-chunk fallback.
            if (TerrainBridge.findBridgeWidthIssues(tiles, w, h, 2, 1).Count > 0)
            {
                tiles.set(beforeNecessitySweep);
                TerrainBridge.enforceMinimumBridgeThickness(tiles, w, h, new BridgeThicknessOptions { maxPasses = 16 });
            }
            // A Chasm can consume the last water cells beside an otherwise useful causeway. The deck remains part of
            // the connected walkable country, but it is no longer a bridge semantically. Normalize that rare component
            // to ordinary floor after widening so the final artifact keeps the authored country instead of rejecting
            // the whole chunk and substituting the safe clearing.
            TerrainBridge.demoteStrayBridgeComponents(tiles, w, h);
            // Final hard gate after every water/bridge/chasm mutation. Closed puddles below ten cells disappear; a
            // bridge whose complete hydrological body fell below twenty cells restores that body to its pre-river land.
            // Boundary fragments were completed inward by the hydrology pass, so the same hard minimum applies here.
            TerrainWater.enforceTerrainWaterTopology(tiles, preRiverTiles, w, h);
            normalizeBridgeLandAnchors(tiles, preRiverTiles, w, h);
            TerrainBridge.demoteStrayBridgeComponents(tiles, w, h);
            // Topology/anchor normalization may remove the last local deck leading to a seam whose opposite bank lives
            // in the neighbouring chunk. Reconnect only that deterministic contract along the original skeleton, using
            // a narrow stone causeway at this final stage so no new sub-20-cell bridged pond can be manufactured.
            ensureSeamPortsConnected(tiles, w, h, corridorMask, ports, true);
            TerrainWater.enforceTerrainWaterTopology(tiles, preRiverTiles, w, h);
            TerrainBridge.enforceMinimumBridgeThickness(tiles, w, h, new BridgeThicknessOptions { maxPasses = 16 });
            demoteUnthickenableBridgeComponents(tiles, w, h);
            normalizeBridgeLandAnchors(tiles, preRiverTiles, w, h);
            TerrainBridge.demoteStrayBridgeComponents(tiles, w, h);
            // Demoting an unthickenable deck to a stone causeway removes those deck cells from its hydrological body.
            // Re-run the hard minimum after that final semantic conversion so it cannot leave two sub-ten puddles (or
            // a remaining bridge over fewer than twenty surface cells) on either side of the former crossing.
            curateLandmarkBridgeDecks(
                tiles,
                w,
                h,
                ports,
                seedNum,
                cx,
                cy,
                verticalField.bridgeLandmarkDensityAt(cx * w + w / 2, cy * h + h / 2));
            demoteUnthickenableBridgeComponents(tiles, w, h);
            TerrainWater.enforceTerrainWaterTopology(tiles, preRiverTiles, w, h);
            TerrainBridge.demoteStrayBridgeComponents(tiles, w, h);
            repairWalkableFrom(tiles, w, h, seedTx, seedTy);
        }

        // Nothing may mutate a seam contract after this point. Water/Chasm normalization and the last reachability
        // repair can remove a local bank that existed during the earlier port pass; rebuild only the affected
        // original skeleton route as stone and leave it connected. This is also the hard guard for dry runs.
        if (!isSpawn)
        {
            ensureSeamPortsConnected(tiles, w, h, corridorMask, ports, true);
            if (terrainProfile.rivers != null)
            {
                TerrainWater.enforceTerrainWaterTopology(tiles, preRiverTiles, w, h);
                TerrainBridge.demoteStrayBridgeComponents(tiles, w, h);
            }
            // Reopening an agreed seam is allowed to notch a Chasm, but that notch may leave a remnant below the
            // shared aperture/core contract. Re-run the same topology gate against the exact pre-Chasm terrain so the
            // landmark is either still broad enough or removed coherently; never leave a wall-like sliver beside a port.
            if (preChasmTiles != null) TerrainChasm.enforceTerrainChasmTopology(tiles, preChasmTiles, w, h);
        }

        // Elevation: the walkable ground follows the macro relief (broad plateaus & valleys spanning chunks — a pure
        // smooth field, ≤1-step-safe so the seam contract holds), and thick rock masses tower to real ranges/canyon
        // walls via the macro rock summit (walls are visual volume, no ≤1-step contract).
        var elevation = TerrainKit.buildStandardElevationField(tiles, w, h, seedNum, biomeKey, new StandardElevationOptions
        {
            maxLevel = MAX_ELEVATION,
            baseTx = cx * w,
            baseTy = cy * h,
            protectBoundary = true,
            levelAt = verticalField.levelAt,
            wallMassifAt = (gtx, gty) =>
                endlessStoredWallTerraceLevelAt(seedNum, gtx, gty, dna, verticalField.levelAt(gtx, gty)),
        });

        // Give edge walls the same complete, theme-authored height composition as massif interiors. Topology stays
        // calm at the origin; vertical identity no longer waits until the player has crossed three chunks.
        applyEndlessWallHeightProfile(
            tiles,
            elevation,
            w,
            h,
            cx * w,
            cy * h,
            seedNum,
            dna,
            verticalField.levelAt);

        // Spatial depth roles are selected only after the authoritative heightfield is final. Cleft therefore owns
        // a proven one-cell wall section, while Underpass owns a corridor carried by independent +4 wall banks.
        // Each preserves the movement role it replaces (Cleft stays blocked, Underpass stays walkable) and both stay
        // outside the streamed boundary ring, so reachability and seam contracts cannot change after topology proof.
        if (!isSpawn) TerrainDepthTiles.applyTerrainDepthTiles(tiles, elevation, w, h, cx * w, cy * h, seedNum, biomeKey);

        try
        {
            return TerrainArtifactModule.finalizeTerrainLayout(
                new DungeonLayout
                {
                    index = endlessChunkKey(cx, cy),
                    seed = seedNum,
                    style = kind == ChunkKind.Cavern ? DungeonStyle.Caves : DungeonStyle.Rooms,
                    biomeKey = biomeKey,
                    tier = tier,
                    tileSize = ts,
                    width = w,
                    height = h,
                    originX = originX,
                    originY = originY,
                    tiles = tiles,
                    // Finalized elevation: walkable terraces are ramps; unwalkable wall blocks stay Solid.
                    elevation = elevation,
                    rooms = rooms,
                    doors = new List<DungeonDoor>(), // endless flow has no gated doors
                    startRoomId = -1,
                    bossRoomId = -1,
                },
                new TerrainFinalizeOptions
                {
                    context = $"generateEndlessChunk(seed={Js.Str(seedNum)}, cx={Js.Str(cx)}, cy={Js.Str(cy)})",
                    requireElevation = true,
                    requireStartBossReachable = false,
                    allowPartialSeamBridgeSpans = true,
                });
        }
        catch (Exception error)
        {
            // Fail LOUD (a validation failure here is a generator defect for this exact seed — report it), but
            // degrade SAFE: generation is pure, so the same seed fails identically on the server and every client,
            // and all of them replace it with the SAME always-valid fallback clearing. One chunk loses its variety;
            // no tick dies, no render crashes, the infinite world stays connected through the ports.
            JsConsole.error(
                $"[endless] chunk ({Js.Str(cx)},{Js.Str(cy)}) of seed {Js.Str(seedNum)} failed terrain validation - emitting safe fallback chunk:",
                error.Message);
            return endlessSafeFallbackChunkAt(seedNum, cx, cy, biomeKey, tier);
        }
    }

    /// <summary>
    /// Deterministic last-resort chunk for a `(seed,cx,cy)` whose generated layout failed shared validation: an
    /// open clearing with the SAME four seam ports (so all neighbours still join), flat ground, no water — valid
    /// under the standard terrain contract by construction. Still finalized through the shared gate, so even the
    /// degraded path carries the full artifact schema. Exported for the contract tests / world tools.
    /// </summary>
    public static DungeonLayout endlessSafeFallbackChunkAt(
        double seedNum,
        int cx,
        int cy,
        string biomeKey,
        int tier)
    {
        var ports = chunkPorts(seedNum, cx, cy);
        const int w = ENDLESS_CHUNK_TILES;
        const int h = ENDLESS_CHUNK_TILES;
        const int m = 4;
        var tiles = new byte[w * h]; // all Solid
        carveRect(tiles, w, h, new TileRect(m, m, w - m * 2, h - m * 2));
        foreach (var p in ports)
        {
            carveCorridor(tiles, w, h, p.tx, p.ty, w >> 1, h >> 1, Bsp.CORRIDOR_THICKNESS, p.horiz);
        }
        // The SAME standard elevation field as an ordinary chunk (incl. the macro relief + seam-band guarantee), so
        // the fallback's border levels agree with every ordinary neighbour — a flat or plain-noise field would itself
        // put an unclimbable step on the seam. No water in the clearing ⇒ nothing else for validation to reject.
        var dna = EndlessWorld.endlessWorldProfileFor(seedNum, biomeKey).macro;
        var elevProfile = Terrain.terrainProfileFor(biomeKey).elevation;
        var verticalField = EndlessVertical.createEndlessVerticalField(new EndlessVerticalFieldOptions
        {
            seed = seedNum,
            baseTx = cx * w,
            baseTy = cy * h,
            width = w,
            height = h,
            landscapeCellSize = ENDLESS_CHUNK_TILES,
            profile = elevProfile,
            dna = dna,
            maxLevel = MAX_ELEVATION,
        });
        var elevation = TerrainKit.buildStandardElevationField(tiles, w, h, seedNum, biomeKey, new StandardElevationOptions
        {
            maxLevel = MAX_ELEVATION,
            baseTx = cx * w,
            baseTy = cy * h,
            protectBoundary = true,
            levelAt = verticalField.levelAt,
            wallMassifAt = (gtx, gty) =>
                endlessStoredWallTerraceLevelAt(seedNum, gtx, gty, dna, verticalField.levelAt(gtx, gty)),
        });
        applyEndlessWallHeightProfile(
            tiles,
            elevation,
            w,
            h,
            cx * w,
            cy * h,
            seedNum,
            dna,
            verticalField.levelAt);
        return TerrainArtifactModule.finalizeTerrainLayout(
            new DungeonLayout
            {
                index = endlessChunkKey(cx, cy),
                seed = seedNum,
                style = DungeonStyle.Rooms,
                biomeKey = biomeKey,
                tier = tier,
                tileSize = TILE_SIZE,
                width = w,
                height = h,
                originX = endlessChunkOriginX(cx),
                originY = endlessChunkOriginY(cy),
                tiles = tiles,
                elevation = elevation,
                rooms = new List<DungeonRoom>(),
                doors = new List<DungeonDoor>(),
                startRoomId = -1,
                bossRoomId = -1,
            },
            new TerrainFinalizeOptions
            {
                context = $"endlessFallbackChunk(seed={Js.Str(seedNum)}, cx={Js.Str(cx)}, cy={Js.Str(cy)})",
                requireElevation = true,
                requireStartBossReachable = false,
                allowPartialSeamBridgeSpans = true,
            });
    }

    /// <summary>
    /// Unify the freshly-carved maze into ONE connected Floor body by DIGGING solid, without dropping any ground.
    ///
    /// The global node-graph is connected over the whole plane, but a single chunk sees only a halo-limited window:
    /// two in-chunk sites whose only graph route runs through a site OUTSIDE the halo appear as two separate stubs,
    /// so the clipped slice can split into several Floor components. Left alone, the later reachability repair would
    /// SOLIDIFY every component but the seed's — throwing away real maze and, worse, killing any seam port stranded
    /// on a dropped component. Instead we connect them: label 4-connected Floor components, then repeatedly BFS from
    /// the largest one THROUGH interior solid to the nearest other component and carve that shortest rock path to
    /// Floor (recording it on `corridorMask`, so the massif-raise never re-walls it and the river pass can bridge
    /// it). This mirrors <see cref="reconnectChunkBody"/> (which decks WATER gaps with bridges) but digs SOLID gaps —
    /// unbounded, because a 32-tile chunk's interior is one contiguous rock body, so every component is reachable
    /// and the loop always converges to a single body. Deterministic (no Rng), and the dug tunnels stay interior so
    /// they never fabricate an unmatched seam crossing.
    /// </summary>
    private static void unifyChunkBody(byte[] tiles, int w, int h, byte[] corridorMask)
    {
        var comp = new int[tiles.Length];
        for (int pass = 0; pass < 64; pass++)
        {
            comp.fill(-1);
            int nComp = 0;
            int bodyId = -1;
            int bodySize = -1;
            var stack = new List<int>();
            for (int s = 0; s < tiles.Length; s++)
            {
                if (comp[s] >= 0 || tiles[s] != TileType.Floor) continue;
                int id = nComp++;
                int size = 0;
                comp[s] = id;
                stack.push(s);
                while (stack.Count > 0)
                {
                    int idx = stack.pop();
                    size++;
                    int tx = idx % w;
                    int ty = idx / w; // (idx / w) | 0
                    // `[[tx + 1, ty], [tx - 1, ty], [tx, ty + 1], [tx, ty - 1]]` — the same order as CHASM_CARDINAL_DIRS.
                    foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (comp[ni] >= 0 || tiles[ni] != TileType.Floor) continue;
                        comp[ni] = id;
                        stack.push(ni);
                    }
                }
                if (size > bodySize)
                {
                    bodySize = size;
                    bodyId = id;
                }
            }
            if (nComp <= 1) return;

            // Multi-source BFS from the largest body, stepping only through INTERIOR Solid, until it touches another
            // component's Floor — the shortest rock crossing. Dig that solid path to Floor; re-label next pass.
            var dist = new short[tiles.Length].fill((short)-1);
            var prev = new int[tiles.Length].fill(-1);
            var queue = new List<int>();
            for (int i = 0; i < tiles.Length; i++)
            {
                if (comp[i] == bodyId)
                {
                    dist[i] = 0;
                    queue.push(i);
                }
            }
            int head = 0;
            int landed = -1;
            while (head < queue.Count && landed < 0)
            {
                int idx = queue[head++];
                int d = dist[idx];
                int tx = idx % w;
                int ty = idx / w;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (dist[ni] >= 0) continue;
                    int tile = tiles[ni];
                    if (tile == TileType.Floor)
                    {
                        if (comp[ni] != bodyId && d > 0)
                        {
                            prev[ni] = idx; // reached another component across the rock just traversed
                            landed = ni;
                            break;
                        }
                        continue; // body floor (d===0 frontier) — path only across solid, not back through dry body
                    }
                    if (tile != TileType.Solid || nx == 0 || ny == 0 || nx == w - 1 || ny == h - 1)
                        continue; // interior rock only
                    dist[ni] = Js.I16(d + 1);
                    prev[ni] = idx;
                    queue.push(ni);
                }
            }
            if (landed < 0) return; // no interior rock path (degenerate) — leave the rest to the reachability repair
            for (int cur = prev[landed]; cur >= 0 && tiles[cur] == TileType.Solid; cur = prev[cur])
            {
                tiles[cur] = TileType.Floor; // dig the rock gap into a corridor
                corridorMask[cur] = 1;
            }
        }
    }

    /// <summary>Solidify any floor tile not reachable from the connected seed tile (drops orphan pockets / dead-end traps).</summary>
    private static void repairFrom(byte[] tiles, int w, int h, int seedTx, int seedTy)
    {
        var mask = floodFill(tiles, w, h, seedTx, seedTy);
        if (countMask(mask) == 0) return;
        for (int i = 0; i < tiles.Length; i++)
        {
            if (tiles[i] == TileType.Floor && mask[i] == 0) tiles[i] = TileType.Solid;
        }
    }

    /// <summary>
    /// Solidify any WALKABLE tile (floor or bridge) not reachable from the connected seed, across bridges — the
    /// post-river guarantee that the chunk's walkable ground (and every seam port wired to it) is one component.
    /// </summary>
    private static void repairWalkableFrom(byte[] tiles, int w, int h, int seedTx, int seedTy)
    {
        if (!isWalkable(getTileSafe(tiles, w, h, seedTx, seedTy)))
        {
            seedTx = -1;
            seedTy = -1;
            for (int ty = 0; ty < h && seedTx < 0; ty++)
            {
                for (int tx = 0; tx < w; tx++)
                {
                    if (!isWalkable(getTileSafe(tiles, w, h, tx, ty))) continue;
                    seedTx = tx;
                    seedTy = ty;
                    break;
                }
            }
        }
        if (seedTx < 0 || seedTy < 0) return;
        var mask = floodFillWalkable(tiles, w, h, seedTx, seedTy);
        if (countMask(mask) == 0) return;
        for (int i = 0; i < tiles.Length; i++)
        {
            int t = tiles[i];
            if ((t == TileType.Floor || t == TileType.Bridge) && mask[i] == 0) tiles[i] = TileType.Solid;
        }
    }

    /// <summary>
    /// Keep a pathological massif/lake/rift intersection from collapsing into a visually unreadable thread.
    /// Expansion is connected, interior-only and Solid-only: water/chasm set-pieces and every seam byte stay intact.
    /// </summary>
    private static void ensureMinimumWalkableCountry(byte[] tiles, int w, int h, double salt)
    {
        double target = Math.ceil(tiles.Length * 0.16);
        int walkable = 0;
        foreach (byte tile in tiles) if (isWalkable(tile)) walkable++;
        if (walkable >= target) return;

        var mark = new byte[tiles.Length];
        for (int pass = 0; pass < 16 && walkable < target; pass++)
        {
            var candidates = new List<(int idx, double order)>();
            for (int ty = 2; ty < h - 2; ty++)
            {
                for (int tx = 2; tx < w - 2; tx++)
                {
                    int idx = ty * w + tx;
                    if (tiles[idx] != TileType.Solid || mark[idx] != 0) continue;
                    bool touchesCountry = false;
                    foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                    {
                        if (isWalkable(tiles[(ty + dy) * w + tx + dx]))
                        {
                            touchesCountry = true;
                            break;
                        }
                    }
                    if (!touchesCountry) continue;
                    candidates.push((idx, latticeHash(salt, tx, ty)));
                }
            }
            if (candidates.Count == 0) return;
            candidates.sort((a, b) =>
            {
                // `a.order - b.order || a.idx - b.idx`
                double byOrder = a.order - b.order;
                return Js.Truthy(byOrder) ? byOrder : a.idx - b.idx;
            });
            int take = (int)Math.min(target - walkable, candidates.Count);
            for (int i = 0; i < take; i++)
            {
                int idx = candidates[i].idx;
                tiles[idx] = TileType.Floor;
                mark[idx] = 1;
                walkable++;
            }
        }
    }

    /// <summary>
    /// Prevent several adjacent macro basins from merging into a featureless open sheet. Only decorative interior
    /// Floor outside the shortest guaranteed port-route tree and authored rooms is converted back to coherent rock
    /// cover; rare arenas keep the loosest cap, while their protected combat court remains untouched.
    /// </summary>
    private static void limitExcessiveOpenCountry(
        byte[] tiles,
        int w,
        int h,
        IReadOnlyList<DungeonRoom> rooms,
        double seed,
        int cx,
        int cy,
        IReadOnlyList<ChunkPort> ports,
        EndlessVerticalField verticalField,
        double maxFraction)
    {
        int walkable = 0;
        foreach (byte tile in tiles) if (isWalkable(tile)) walkable++;
        int target = (int)Math.floor(tiles.Length * maxFraction);
        if (walkable <= target) return;

        var protectedMask = endlessPortRouteProtectionMask(tiles, w, h, ports, 1);
        var candidates = new List<(int idx, double cover)>();
        for (int ty = 3; ty < h - 3; ty++)
        {
            for (int tx = 3; tx < w - 3; tx++)
            {
                int idx = ty * w + tx;
                if (tiles[idx] != TileType.Floor || protectedMask[idx] != 0 || protectedByRoom(rooms, tx, ty, 2))
                    continue;
                int solidNeighbours = 0;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    if (tiles[(ty + dy) * w + tx + dx] == TileType.Solid) solidNeighbours++;
                }
                double broad = valueNoise(
                    (uint)(Js.ToInt32(seed) ^ BASIN_WALL_VEIN_SALT ^ 0x7f4a7c15),
                    cx * w + tx,
                    cy * h + ty,
                    7);
                int gtx = cx * w + tx;
                int gty = cy * h + ty;
                // Prefer coherent high/terraced shoulders over arbitrary floor speckle. This makes the density guard an
                // extension of the same vertical composition rather than an unrelated per-chunk erosion pass.
                double verticalCover =
                    verticalField.normalizedHeightAt(gtx, gty) * 0.18 +
                    verticalField.escarpmentStrengthAt(gtx, gty) * 0.1;
                // Preserve the existing exposed face of a massif instead of repeatedly pushing that face outward and
                // burying the height variation players could see. The slow cover field still selects coherent patches;
                // a small contact penalty lets new shoulders meet old rock occasionally without consuming every cliff lip.
                candidates.push((idx, broad - solidNeighbours * 0.06 + verticalCover));
            }
        }
        candidates.sort((a, b) =>
        {
            // `b.cover - a.cover || a.idx - b.idx`
            double byCover = b.cover - a.cover;
            return Js.Truthy(byCover) ? byCover : a.idx - b.idx;
        });
        int remove = Math.min(walkable - target, candidates.Count);
        for (int i = 0; i < remove; i++) tiles[candidates[i].idx] = TileType.Solid;
    }

    private static int getTileSafe(byte[] tiles, int w, int h, int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= w || ty >= h) return TileType.Solid;
        return tiles[ty * w + tx];
    }

    // Restore walkability on the whole connective skeleton after the river pass. The skeleton (carved corridors +
    // port stubs, flagged in `corridorMask`) was one connected Floor body before the river; re-walking it keeps
    // the maze a single navigable component across the water, so the four seam ports — which all sit on the
    // skeleton — stay mutually reachable and the chunk never collapses into a near-solid "fake wall".
    //
    // The shared mountain pass can leave a skeleton tile in either of two damaged states: still flooded
    // (TileType.Water, where its bridge deck was trimmed away) or already re-solidified
    // (TileType.Solid, where its own reachability repair dropped a severed branch). Both are restored: a
    // flooded tile that still borders open water (or another deck) becomes a TileType.Bridge — a genuine
    // crossing — a lone one-tile pinch becomes plain TileType.Floor (a ford, never a deck that spans
    // nothing), and a re-solidified corridor tile is re-carved to TileType.Floor.

    /// <summary>
    /// Furthest a thin reconnection deck may reach across open water (tiles) to re-link a severed region — wide
    /// enough to span any river a chunk-sized crossing can present with a single thin deck, so no maze branch or
    /// seam port is ever left stranded; a still-wider separation is a genuine lake and stays the barrier it is.
    /// </summary>
    private const int RECONNECT_MAX_GAP = 12;

    // Restore the chunk to ONE connected walkable body after the shared river pass, without undoing its
    // deliberate broad-river / minority-bridge shaping:
    //  1. Re-carve every corridor-skeleton tile the river repair severed and solidified back to Floor (the maze's
    //     backbone is always walkable ground).
    //  2. Bridge only the MINIMAL water gap re-linking each still-separated walkable region to the largest body —
    //     a thin deck along the shortest crossing, so genuine crossings are restored but rivers stay broad.
    // Anything still unreachable past RECONNECT_MAX_GAP is left for the caller's reachability repair to
    // drop (a far lake island, not a maze branch). Pure & deterministic.

    /// <summary>
    /// Demote every REDUNDANT bridge deck back to open water. A deck is redundant when removing it (turning its
    /// tiles to Water) leaves every OTHER walkable tile still reachable from the seed — its two banks are linked by
    /// some other route anyway. Keeping only the non-redundant crossings turns a river that the dense maze skeleton
    /// would otherwise pave wall-to-wall with bridges into broad open water spanned by a few genuine causeways
    /// (owner's call: large water is a barrier you route around, not a paved plaza). At a chunk border only a deck
    /// containing one of the four neighbour-agreed seam ports is protected; decorative edge decks can otherwise end
    /// at one bank when the neighbour is generated independently. Deterministic tile-order sweep, no Rng.
    /// </summary>
    private static void thinBridgeDecks(
        byte[] tiles,
        int w,
        int h,
        int seedTx,
        int seedTy,
        IReadOnlyList<ChunkPort> ports,
        bool protectAllBorder = false)
    {
        int sTx = seedTx;
        int sTy = seedTy;
        if (!isWalkable(getTileSafe(tiles, w, h, sTx, sTy)))
        {
            sTx = -1;
            sTy = -1;
            for (int ty = 0; ty < h && sTx < 0; ty++)
            {
                for (int tx = 0; tx < w; tx++)
                {
                    if (isWalkable(getTileSafe(tiles, w, h, tx, ty)))
                    {
                        sTx = tx;
                        sTy = ty;
                        break;
                    }
                }
            }
        }
        if (sTx < 0) return;

        int baseReached = countMask(floodFillWalkable(tiles, w, h, sTx, sTy));
        var bridgeSpans = TerrainBridgeSpanModule.classifyTerrainBridgeSpans(tiles, w, h);
        var seen = new byte[tiles.Length];
        for (int s = 0; s < tiles.Length; s++)
        {
            if (seen[s] != 0 || tiles[s] != TileType.Bridge) continue;
            // Collect this connected bridge deck.
            var deck = new List<int>();
            var stack = new List<int> { s };
            seen[s] = 1;
            bool touchesBorder = false;
            bool touchesRequiredPort = false;
            while (stack.Count > 0)
            {
                int idx = stack.pop();
                deck.push(idx);
                int tx = idx % w;
                int ty = idx / w; // (idx / w) | 0
                if (tx == 0 || ty == 0 || tx == w - 1 || ty == h - 1) touchesBorder = true;
                if (ports.some(port => port.tx == tx && port.ty == ty)) touchesRequiredPort = true;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (seen[ni] != 0 || tiles[ni] != TileType.Bridge) continue;
                    seen[ni] = 1;
                    stack.push(ni);
                }
            }
            if (touchesBorder && (protectAllBorder || touchesRequiredPort)) continue;
            foreach (int idx in deck)
            {
                // Reveal the inferred barrier rather than flooding a genuine Chasm crossing with invented Water.
                tiles[idx] = bridgeSpans[idx] == TileType.Chasm ? (byte)TileType.Chasm : (byte)TileType.Water;
            }
            int reached = countMask(floodFillWalkable(tiles, w, h, sTx, sTy));
            if (reached == baseReached - deck.Count)
            {
                baseReached = reached; // redundant: banks stay linked → leave as open water
            }
            else
            {
                foreach (int idx in deck) tiles[idx] = TileType.Bridge; // a needed causeway → restore it
            }
        }
    }

    /// <summary>
    /// Final semantic guard for decks after Water/Chasm normalization. A strong lake can drown an old rock bank and
    /// leave a formerly valid interior deck with only one dry anchor. First restore its exact pre-river Floor
    /// approach when one is still available; if an interior component still cannot span two banks, turn it into a
    /// plain stone causeway. Edge decks are valid partial spans whose missing bank belongs to the neighbour chunk.
    /// </summary>
    private static void normalizeBridgeLandAnchors(
        byte[] tiles,
        byte[] preRiverTiles,
        int w,
        int h)
    {
        var seen = new byte[tiles.Length];
        for (int start = 0; start < tiles.Length; start++)
        {
            if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
            var stack = new List<int> { start };
            var deck = new List<int>();
            var anchors = new JsSet<int>();
            var approachCandidates = new JsSet<int>();
            seen[start] = 1;
            bool touchesEdge = false;
            while (stack.Count > 0)
            {
                int index = stack.pop();
                deck.push(index);
                int tx = index % w;
                int ty = index / w;
                touchesEdge = touchesEdge || tx == 0 || ty == 0 || tx == w - 1 || ty == h - 1;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (tiles[ni] == TileType.Floor) anchors.add(ni);
                    else if (tiles[ni] == TileType.Bridge && seen[ni] == 0)
                    {
                        seen[ni] = 1;
                        stack.push(ni);
                    }
                    else if (tiles[ni] == TileType.Solid && preRiverTiles[ni] == TileType.Floor)
                    {
                        approachCandidates.add(ni);
                    }
                }
            }
            foreach (int index in approachCandidates.ToList().sort((a, b) => a - b))
            {
                if (anchors.size >= 2) break;
                tiles[index] = TileType.Floor;
                anchors.add(index);
            }
            if (anchors.size >= 2 || touchesEdge) continue;
            foreach (int index in deck) tiles[index] = TileType.Floor;
        }
    }

    /// <summary>Replace a rare final deck that cannot acquire a complete two-tile width with a walkable stone causeway.</summary>
    private static void demoteUnthickenableBridgeComponents(byte[] tiles, int w, int h)
    {
        var issues = TerrainBridge.findBridgeWidthIssues(tiles, w, h, 2);
        if (issues.Count == 0) return;
        var seen = new byte[tiles.Length];
        foreach (var issue in issues)
        {
            if (seen[issue.index] != 0 || tiles[issue.index] != TileType.Bridge) continue;
            var stack = new List<int> { issue.index };
            seen[issue.index] = 1;
            while (stack.Count > 0)
            {
                int index = stack.pop();
                tiles[index] = TileType.Floor;
                int tx = index % w;
                int ty = index / w;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (seen[ni] != 0 || tiles[ni] != TileType.Bridge) continue;
                    seen[ni] = 1;
                    stack.push(ni);
                }
            }
        }
    }

    private sealed class BridgeLandmarkComponent
    {
        public List<int> indices;
        public bool touchesRequiredPort;
        public int spanEdges;
        public int order;
    }

    /// <summary>
    /// Keep explicit decks as sparse landmarks. The maze/hydrology repair may require many crossings, but rendering
    /// every required route as timber turns a river district into an orange road mesh. Non-landmark components are
    /// therefore retained as equally-walkable stone causeways (Floor); collision/connectivity are unchanged. Shared
    /// seam-port decks are always preserved so two independently generated chunks never disagree on a visible deck
    /// continuation. The following water-topology pass owns any small pools split by a new causeway.
    /// </summary>
    private static int curateLandmarkBridgeDecks(
        byte[] tiles,
        int w,
        int h,
        IReadOnlyList<ChunkPort> ports,
        double seed,
        int cx,
        int cy,
        double density)
    {
        bool portIsLandmark(ChunkPort port)
        {
            double threshold = 0.12 + clamp01(density) * 0.22;
            if (port.horiz)
            {
                int seamX = port.tx == 0 ? cx : cx + 1;
                return seamHash(seed, seamX, cy, BRIDGE_LANDMARK_PORT_SALT) < threshold;
            }
            int seamY = port.ty == 0 ? cy : cy + 1;
            return seamHash(seed, cx, seamY, BRIDGE_LANDMARK_PORT_SALT ^ unchecked((int)0x85ebca6b)) < threshold;
        }
        var landmarkPortIndices = new JsSet<int>(
            ports.filter(port => portIsLandmark(port)).map(port => port.ty * w + port.tx));
        var seen = new byte[tiles.Length];
        var components = new List<BridgeLandmarkComponent>();
        for (int start = 0; start < tiles.Length; start++)
        {
            if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
            var indices = new List<int>();
            var stack = new List<int> { start };
            seen[start] = 1;
            bool touchesRequiredPort = false;
            int spanEdges = 0;
            while (stack.Count > 0)
            {
                int index = stack.pop();
                indices.push(index);
                int tx = index % w;
                int ty = index / w;
                if (landmarkPortIndices.has(index)) touchesRequiredPort = true;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int neighbour = ny * w + nx;
                    if (tiles[neighbour] == TileType.Water || tiles[neighbour] == TileType.Chasm) spanEdges++;
                    if (tiles[neighbour] != TileType.Bridge || seen[neighbour] != 0) continue;
                    seen[neighbour] = 1;
                    stack.push(neighbour);
                }
            }
            components.push(new BridgeLandmarkComponent
            {
                indices = indices,
                touchesRequiredPort = touchesRequiredPort,
                spanEdges = spanEdges,
                order = start,
            });
        }
        double landmarkLimit = 1 + Math.round(clamp01(density) * 2);
        double cellBudget = Math.floor(tiles.Length * (0.012 + clamp01(density) * 0.02));
        double maximumLandmarkSpan = 40 + Math.round(clamp01(density) * 24);
        var eligible = components.filter(
            component => component.indices.Count <= maximumLandmarkSpan && component.spanEdges >= 2);
        var ranked = eligible
            .filter(component => !component.touchesRequiredPort)
            .sort((a, b) =>
            {
                // `b.spanEdges - a.spanEdges || |a.len - 12| - |b.len - 12| || a.order - b.order`
                int bySpan = b.spanEdges - a.spanEdges;
                if (bySpan != 0) return bySpan;
                int byLength = Math.abs(a.indices.Count - 12) - Math.abs(b.indices.Count - 12);
                if (byLength != 0) return byLength;
                return a.order - b.order;
            });
        var keep = new JsSet<BridgeLandmarkComponent>(
            eligible.filter(component => component.touchesRequiredPort));
        int keptCells = keep.ToList().reduce((int sum, BridgeLandmarkComponent component) => sum + component.indices.Count, 0);
        int optionalKept = 0;
        foreach (var component in ranked)
        {
            if (optionalKept >= landmarkLimit) break;
            if (keptCells > 0 && keptCells + component.indices.Count > cellBudget) continue;
            keep.add(component);
            keptCells += component.indices.Count;
            optionalKept++;
        }
        // A wholly interior river still deserves one explicit crossing even when its best component is larger than
        // the soft cell budget. This is a visual rhythm cap, never a rule that eliminates bridges categorically.
        if (keep.size == 0 && ranked.Count > 0) keep.add(ranked[0]);

        int demoted = 0;
        foreach (var component in components)
        {
            if (keep.has(component)) continue;
            foreach (int index in component.indices)
            {
                tiles[index] = TileType.Floor;
                demoted++;
            }
        }
        // A kept component may join two seam ports locally. The unselected seam still resolves identically on both
        // neighbouring chunks; turn its small local deck collar into stone without discarding the selected landmark.
        foreach (var port in ports)
        {
            if (portIsLandmark(port)) continue;
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int tx = port.tx + dx;
                    int ty = port.ty + dy;
                    if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                    int index = ty * w + tx;
                    if (tiles[index] != TileType.Bridge) continue;
                    tiles[index] = TileType.Floor;
                    demoted++;
                }
            }
        }
        return demoted;
    }

    private static void reconnectChunkBody(byte[] tiles, int w, int h, byte[] corridorMask)
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            if (corridorMask[i] != 0 && tiles[i] == TileType.Solid) tiles[i] = TileType.Floor;
        }

        var comp = new int[tiles.Length];
        // Iteratively merge: label walkable components, then thread the shortest water gap from the largest body to
        // any other component and deck it. Each pass joins ≥1 component, so it converges well inside the bound.
        for (int pass = 0; pass < 64; pass++)
        {
            comp.fill(-1);
            int nComp = 0;
            int bodyId = -1;
            int bodySize = -1;
            var stack = new List<int>();
            for (int s = 0; s < tiles.Length; s++)
            {
                if (comp[s] >= 0 || !isWalkable(tiles[s])) continue;
                int id = nComp++;
                int size = 0;
                comp[s] = id;
                stack.push(s);
                while (stack.Count > 0)
                {
                    int idx = stack.pop();
                    size++;
                    int tx = idx % w;
                    int ty = idx / w; // (idx / w) | 0
                    foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (comp[ni] >= 0 || !isWalkable(tiles[ni])) continue;
                        comp[ni] = id;
                        stack.push(ni);
                    }
                }
                if (size > bodySize)
                {
                    bodySize = size;
                    bodyId = id;
                }
            }
            if (nComp <= 1) return;

            // Multi-source BFS from the body, stepping through Water only (each step a gap tile), until it touches a
            // foreign component's walkable tile — the shortest crossing. Deck that water path; re-label next pass.
            var dist = new short[tiles.Length].fill((short)-1);
            var prev = new int[tiles.Length].fill(-1);
            var queue = new List<int>();
            for (int i = 0; i < tiles.Length; i++)
            {
                if (comp[i] == bodyId)
                {
                    dist[i] = 0;
                    queue.push(i);
                }
            }
            int head = 0;
            int landed = -1;
            while (head < queue.Count && landed < 0)
            {
                int idx = queue[head++];
                int d = dist[idx];
                int tx = idx % w;
                int ty = idx / w;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (dist[ni] >= 0) continue;
                    int tile = tiles[ni];
                    if (isWalkable(tile))
                    {
                        if (comp[ni] != bodyId && d > 0)
                        {
                            prev[ni] = idx; // reached another component across the water just traversed
                            landed = ni;
                            break;
                        }
                        continue; // body tile (d===0 frontier) — don't path through dry body, only across water
                    }
                    if (tile != TileType.Water || d >= RECONNECT_MAX_GAP) continue;
                    dist[ni] = Js.I16(d + 1);
                    prev[ni] = idx;
                    queue.push(ni);
                }
            }
            if (landed < 0) break; // every remaining component is a far lake island — leave to the reachability repair
            for (int cur = prev[landed]; cur >= 0 && tiles[cur] == TileType.Water; cur = prev[cur])
            {
                tiles[cur] = TileType.Bridge; // deck only the water gap; the two banks are already walkable land
            }
        }
    }

    private static void restoreSeamPorts(
        byte[] tiles,
        int w,
        int h,
        IReadOnlyList<ChunkPort> ports,
        bool stoneCauseways = false)
    {
        void restore(int tx, int ty)
        {
            int idx = ty * w + tx;
            if (tiles[idx] == TileType.Water)
                tiles[idx] = stoneCauseways ? (byte)TileType.Floor : (byte)TileType.Bridge;
            // Ports are a stronger contract than every local landform. Chasm/Cleft can be introduced after the first
            // route repair just like rock, so treating only Solid as restorable lets a final rift normalization close a
            // neighbour-agreed world seam. Keep Water's bridge semantics above; every other blocking surface becomes
            // the same dry stone stub on both independently generated sides.
            else if (!isWalkable(tiles[idx])) tiles[idx] = TileType.Floor;
        }
        foreach (var p in ports)
        {
            restore(p.tx, p.ty);
            if (p.tx == 0) restore(1, p.ty);
            else if (p.tx == w - 1) restore(w - 2, p.ty);
            else if (p.ty == 0) restore(p.tx, 1);
            else if (p.ty == h - 1) restore(p.tx, h - 2);
        }
    }

    /// <summary>
    /// Reconnect only seam ports that a lake wider than RECONNECT_MAX_GAP left outside the main walkable
    /// body. The original corridor mask is connected by construction, so a BFS over walkable ground plus that mask
    /// finds the least invasive route back; only the selected route is restored (Water -> Bridge, rock/rift -> Floor).
    /// </summary>
    private static void ensureSeamPortsConnected(
        byte[] tiles,
        int w,
        int h,
        byte[] corridorMask,
        IReadOnlyList<ChunkPort> ports,
        bool stoneCauseways = false)
    {
        var root = ports.Count > 0 ? ports[0] : null;
        if (root == null) return;
        restoreSeamPorts(tiles, w, h, ports, stoneCauseways);
        int rootIdx = root.ty * w + root.tx;

        // `for (const port of ports.slice(1))`
        for (int portIndex = 1; portIndex < ports.Count; portIndex++)
        {
            var port = ports[portIndex];
            var connected = floodFillWalkable(tiles, w, h, root.tx, root.ty);
            int target = port.ty * w + port.tx;
            if (connected[target] != 0) continue;

            var previous = new int[tiles.Length].fill(-1);
            var queue = new int[tiles.Length];
            int head = 0;
            int tail = 0;
            for (int i = 0; i < connected.Length; i++)
            {
                if (connected[i] == 0) continue;
                previous[i] = i;
                queue[tail++] = i;
            }
            while (head < tail && previous[target] < 0)
            {
                int at = queue[head++];
                int tx = at % w;
                int ty = at / w;
                foreach (var (dx, dy) in CHASM_CARDINAL_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (previous[ni] >= 0) continue;
                    if (tiles[ni] == TileType.Chasm) continue;
                    if (!isWalkable(tiles[ni]) && corridorMask[ni] == 0 && ni != target) continue;
                    previous[ni] = at;
                    queue[tail++] = ni;
                }
            }
            if (previous[target] < 0) continue;
            for (int at = target; previous[at] != at; at = previous[at])
            {
                if (tiles[at] == TileType.Water)
                    tiles[at] = stoneCauseways ? (byte)TileType.Floor : (byte)TileType.Bridge;
                else if (!isWalkable(tiles[at])) tiles[at] = TileType.Floor;
            }
            // The root must remain part of the rebuilt body even if a malformed mask ever omitted a port stub.
            if (!isWalkable(tiles[rootIdx])) tiles[rootIdx] = TileType.Floor;
            connected = floodFillWalkable(tiles, w, h, root.tx, root.ty);
            if (connected[target] == 0) break;
        }
    }

    /// <summary>
    /// Consume the RNG draws the retired encounter roll used to make.
    ///
    /// A seeded generator's output depends on the SEQUENCE of draws, not only on the values anything reads: every
    /// later decision in a chunk continues the stream where the previous one left it. Deleting the encounter roll
    /// therefore did not merely drop a label — it shifted the whole stream, and the same seed began producing a
    /// different, untuned landform. The property tests that guard this generator (valleys and peaks both exist,
    /// water decks reach two distinct banks, maze density, perceptual variety) all failed as a result.
    ///
    /// The encounter itself is gone. Its two draws stay — `rng.bool()` for the `risky` argument, which was
    /// evaluated before the call, then the single `rng.next()` that `Rng.weighted` spends inside — so this
    /// generator produces exactly the terrain it was authored and tuned to produce.
    /// </summary>
    private static double? consumeRetiredEncounterDraws(Rng rng)
    {
        rng.@bool();
        rng.next();
        return null;
    }
}
