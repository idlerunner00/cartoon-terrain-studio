// Fluitown extension — NOT a port of the original. The comic look's organic terrain form. Only reached with
// TerrainOrganicForm.Enabled (set before the first bake; the Style drawer's "organic rock" switch); without it the
// ported block shapes stay.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Fluitown.Domain;

namespace Fluitown.Render;

/// <summary>
/// Turns the terrain's raster-aligned terraces into organic rock: every baked tile is displaced by one smooth,
/// world-deterministic horizontal field before it leaves the bake worker.
///
/// The original compiles each 2.5 m cell into straight cliff runs along the cell raster, vertical walls and square
/// caps — seen from the Flui's world that reads as stacked blocks. The field bends those outlines (a 6 m gesture and a
/// 3.4 m detail), rounds every inside corner into a hollow and domes the rock tops, fading to zero on open ground and
/// pinned at zero on structures (bridges, underpasses, clefts), whose placement stays on the
/// raster. Its horizontal part does not depend on height: walls stay vertical and every vertical edge moves as a whole.
///
/// Watertight by construction:
/// * The field is a pure function of the absolute compile-space position, so coincident vertices — across faces,
///   lanes, cells and tiles — move identically.
/// * Its horizontal part is one 2D warp for every height, with a gradient below one: it is injective, floors and caps
///   stay flat at their heights, nothing folds, and a vertical edge is displaced exactly (its chord is its image).
/// * A straight edge maps to the chord between its warped ends, whereas a vertex lying on that edge (a T-junction —
///   the compiler joins lattices of different density everywhere) follows the curved field. Each such vertex is found
///   before the displacement and put back onto its host's displaced chord afterwards (<see cref="Mesh.FindTJunctions"/>,
///   <see cref="Mesh.SnapTJunctions"/>): watertight without an extra face.
/// * Refinement (<see cref="Mesh.Refine"/>) splits an edge by a rule that depends on that edge alone (its length and
///   the field at its ends), so the two faces sharing it split it alike. Across a tile seam the neighbour's vertices
///   are unknown: edges lying in a seam plane are split at canonical world positions in both tiles (SEAM_STEP_PX).
///
/// Normals follow the displacement exactly (inverse transpose of its Jacobian). Wind decoration is only carried
/// along (it keeps its shape). The vegetation lane's feet move with the ground they stand on.
/// </summary>
public static partial class TerrainOrganicForm
{
    /// <summary>Set before the first bake (comic look only); bakes run on worker threads.</summary>
    public static volatile bool Enabled;

    // ── Field (compile px, 25 px = 1 m; one cell = 62.5 px, one level = 15 px) ──
    /// <summary>Outline gesture: bends straight cliff runs and coastlines.</summary>
    internal const double OUTLINE_AMPLITUDE = 6;
    internal const double OUTLINE_WAVELENGTH = 150;
    /// <summary>Outline detail: a shorter bend on top of the gesture, so no run of cliff is a clean arc.</summary>
    internal const double DETAIL_AMPLITUDE = 3;
    internal const double DETAIL_WAVELENGTH = 84;
    // Share the existing 14 px displacement budget across three scales. Broad shoulders connect neighbouring
    // terraces; the smaller terms keep their hand-shaped edges. The pin-mask gradient bound does not increase.
    internal const double MASSIF_AMPLITUDE = 5, MASSIF_WAVELENGTH = 650;
    /// <summary>Inside-corner fillet: how far the corner moves into the low cell, and the bump's reach in cells.</summary>
    internal const double FILLET_PX = 14;
    internal const double FILLET_RADIUS = 1.0;
    /// <summary>Rock cap dome: lift at the middle of a rock cell (its rim rises by half, an outer corner by a quarter).</summary>
    internal const double DOME_PX = 12;
    /// <summary>Longest edge of a doming rock cap after refinement.</summary>
    internal const double DOME_EDGE_PX = 44;
    /// <summary>Longest edge of an ink/decal strip after refinement: keeps it on its warped crest.</summary>
    internal const double OVERLAY_EDGE_PX = 44;
    /// <summary>
    /// Canonical split spacing on tile seams: a horizontal edge lying in a seam plane is split where its along-seam
    /// coordinate crosses a multiple of SEAM_STEP_PX, in both tiles alike (4 px bounds the subpixel chord error at
    /// relief and cliff joins). Other edges need no split: the horizontal field does not depend on height, so a vertical edge moves
    /// as a whole, and the neighbour's faces meet a seam wall only along its horizontal edges.
    /// </summary>
    internal const double SEAM_STEP_PX = 4;
    /// <summary>A vertex closer than this to another face's edge is a T-junction on it.</summary>
    internal const double T_JUNCTION_EPS = 0.05;

    /// <summary>Diagnostics: count folded faces (costs a copy of every lane's positions).</summary>
    public static volatile bool Diagnostics;

    /// <summary>Diagnostics: receives every input of <see cref="apply"/> before it is changed.</summary>
    public static Action<TerrainGeometryPayload, TerrainBakeFrame, MaterializedTerrain>? InputProbe;

    /// <summary>Diagnostics summed over every <see cref="apply"/> of the process.</summary>
    public static readonly OrganicStats Totals = new();

    public sealed class OrganicStats
    {
        public int tJunctions;
        public int refineTriangles, seamTriangles, boulderTriangles;
        public int addedVertices;
        public int addedTriangles;
        public int flippedTriangles;
        public double milliseconds, refineMs, repairMs, warpMs;
        public double flippedArea;
        public int tiles;
        /// <summary>Bakes whose tile was left unchanged because the pass failed.</summary>
        public int failures;

        internal void AddTo(OrganicStats total)
        {
            lock (total)
            {
                total.tJunctions += tJunctions;
                total.refineTriangles += refineTriangles;
                total.seamTriangles += seamTriangles;
                total.boulderTriangles += boulderTriangles;
                total.addedVertices += addedVertices;
                total.addedTriangles += addedTriangles;
                total.flippedTriangles += flippedTriangles;
                total.milliseconds += milliseconds;
                total.refineMs += refineMs;
                total.repairMs += repairMs;
                total.warpMs += warpMs;
                total.flippedArea += flippedArea;
                total.tiles++;
            }
        }
    }

    /// <summary>
    /// Displaces one merged tile payload in place (its lanes' arrays are replaced where refinement grows them; the
    /// replaced stores go back to <paramref name="pool"/>). The payload must be the bake's fresh merged result — never a
    /// cached layer.
    /// </summary>
    public static void apply(
        TerrainGeometryPayload geometry,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainTransferBufferPool? pool)
    {
        InputProbe?.Invoke(geometry, frame, terrain);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var stats = new OrganicStats();
        var commits = new List<Action>();
        var meshes = new List<Mesh>();
        try
        {
            var field = new Field(frame, terrain);
            if (!field.any) return;
            // Every lane is computed into its own stores first; the payload changes only once all lanes succeeded.
            if (geometry.surface is { } surface)
            {
                var mesh = new Mesh(field, surface.position, surface.index);
                meshes.Add(mesh);
                int normal = mesh.AddChannel(surface.normal, 3, ChannelKind.Normal);
                mesh.AddChannel(surface.color, 3, ChannelKind.Linear);
                int kind = mesh.AddChannel(surface.surface, 2, ChannelKind.FirstDiscrete);
                mesh.AddChannel(surface.emissive, 1, ChannelKind.Linear);
                mesh.AddChannel(surface.ground, 4, ChannelKind.Linear);
                // Wind decoration (negative strength) keeps its shape: carried, never split.
                mesh.MarkRigid(v => mesh.Get(kind, v, 1) < 0);
                // Doming rock caps get inner vertices (with only its rim lifted, a cap's fan triangles band into dark
                // wedges under the light ramp).
                mesh.Refine(DOME_EDGE_PX, stats, v => IsRockCap(mesh.Get(kind, v, 0)), field.DomeActive);
                mesh.SplitSeamEdges(stats);
                mesh.FindTJunctions(stats);
                // Mountain form (TerrainOrganicForm.Cliffs.cs): wall heights above their foot, for the rock shader.
                MarkWallHeights(mesh, field, kind, 5);
                mesh.Warp(normal, stats);
                mesh.SnapTJunctions(stats);
                AddCliffRocks(mesh, field, frame, terrain, kind, stats);
                commits.Add(() =>
                {
                    var old = new Array[] { surface.position, surface.normal, surface.color, surface.surface, surface.emissive, surface.ground, surface.index };
                    surface.position = mesh.Take(0, pool);
                    surface.normal = mesh.Take(1, pool);
                    surface.color = mesh.Take(2, pool);
                    surface.surface = mesh.Take(3, pool);
                    surface.emissive = mesh.Take(4, pool);
                    surface.ground = mesh.Take(5, pool);
                    surface.index = mesh.TakeIndex(pool);
                    surface.bounds = TerrainGeometryCompilerModule.boundsOfPositions(surface.position);
                    Release(pool, old, surface.position, surface.normal, surface.color, surface.surface, surface.emissive, surface.ground, surface.index);
                });
            }
            if (geometry.water is { } water)
            {
                var mesh = new Mesh(field, water.position, water.index);
                meshes.Add(mesh);
                int normal = mesh.AddChannel(water.normal, 3, ChannelKind.Normal);
                mesh.AddChannel(water.color, 3, ChannelKind.Linear);
                mesh.AddChannel(water.water, 4, ChannelKind.Linear);
                mesh.AddChannel(water.fold, 2, ChannelKind.Linear);
                mesh.AddChannel(water.reflection, 3, ChannelKind.Linear);
                // No mountain splits: the water neither domes nor leans (its geometry stays as without the form).
                mesh.SplitSeamEdges(stats, mountain: false);
                mesh.FindTJunctions(stats);
                // Water stays planar: rock domes never lift a sheet (it would bulge where a rock cap meets its level).
                mesh.Warp(normal, stats, domes: false);
                mesh.SnapTJunctions(stats);
                commits.Add(() =>
                {
                    var old = new Array[] { water.position, water.normal, water.color, water.water, water.fold, water.reflection, water.index };
                    water.position = mesh.Take(0, pool);
                    water.normal = mesh.Take(1, pool);
                    water.color = mesh.Take(2, pool);
                    water.water = mesh.Take(3, pool);
                    water.fold = mesh.Take(4, pool);
                    water.reflection = mesh.Take(5, pool);
                    water.index = mesh.TakeIndex(pool);
                    water.bounds = TerrainGeometryCompilerModule.boundsOfPositions(water.position);
                    Release(pool, old, water.position, water.normal, water.color, water.water, water.fold, water.reflection, water.index);
                });
            }
            if (geometry.mist is { } mist)
            {
                // Chasm mist cards: carried along, unchanged in shape.
                var mesh = new Mesh(field, mist.position, mist.index);
                meshes.Add(mesh);
                mesh.Warp(-1, stats);
                commits.Add(() =>
                {
                    var old = new Array[] { mist.position };
                    mist.position = mesh.Take(0, pool);
                    mist.bounds = TerrainGeometryCompilerModule.boundsOfPositions(mist.position);
                    Release(pool, old, mist.position);
                });
            }
            if (geometry.overlay is { } overlay)
            {
                var mesh = new Mesh(field, overlay.position, overlay.index);
                meshes.Add(mesh);
                mesh.AddChannel(overlay.color, 4, ChannelKind.Linear);
                mesh.Refine(OVERLAY_EDGE_PX, stats);
                mesh.Warp(-1, stats);
                commits.Add(() =>
                {
                    var old = new Array[] { overlay.position, overlay.color, overlay.index };
                    overlay.position = mesh.Take(0, pool);
                    overlay.color = mesh.Take(1, pool);
                    overlay.index = mesh.TakeIndex(pool);
                    overlay.bounds = TerrainGeometryCompilerModule.boundsOfPositions(overlay.position);
                    Release(pool, old, overlay.position, overlay.color, overlay.index);
                });
            }
            if (geometry.actorWall is { } caster)
            {
                // Shadow casters keep their panels: a shadow follows the bent outline at the panels' corners.
                var mesh = new Mesh(field, caster.position, caster.index);
                meshes.Add(mesh);
                mesh.Warp(-1, stats);
                commits.Add(() =>
                {
                    var old = new Array[] { caster.position, caster.index };
                    caster.position = mesh.Take(0, pool);
                    caster.index = mesh.TakeIndex(pool);
                    caster.bounds = TerrainGeometryCompilerModule.boundsOfPositions(caster.position);
                    Release(pool, old, caster.position, caster.index);
                });
            }
            if (geometry.vegetation is { } vegetation && vegetation.records.Length > 0)
            {
                // The merged lane may be the dressing layer's own object: warp a copy (with the mountain form's plants on
                // the rock appended, TerrainOrganicForm.Cliffs.cs).
                float[]? cliffPlants = CliffPlants(frame, terrain, vegetation);
                float[] records = new float[vegetation.records.Length + (cliffPlants?.Length ?? 0)];
                Array.Copy(vegetation.records, records, vegetation.records.Length);
                if (cliffPlants != null) Array.Copy(cliffPlants, 0, records, vegetation.records.Length, cliffPlants.Length);
                for (int r = 0; r + FluitownVegetation.Stride <= records.Length; r += FluitownVegetation.Stride)
                {
                    field.Sample(records[r + 1], records[r + 2], records[r + 3], out double wx, out double wy, out double wz, Span<double>.Empty);
                    records[r + 1] = (float)(records[r + 1] + wx);
                    records[r + 2] = (float)(records[r + 2] + wy);
                    records[r + 3] = (float)(records[r + 3] + wz);
                }
                commits.Add(() => geometry.vegetation = new TerrainVegetationLane { records = records });
            }
        }
        catch (Exception error)
        {
            // Never lose a tile to the stylisation: the ported geometry is drawn instead.
            lock (Totals) Totals.failures++;
            if (Interlocked.Exchange(ref reportedFailure, 1) == 0)
                Console.Error.WriteLine($"TerrainOrganicForm: tile left unchanged after {error}");
            foreach (Mesh mesh in meshes) mesh.ReturnScratch();
            return;
        }
        // Commits copy every edited channel into exact stores; the scratch goes back to its pool afterwards.
        foreach (Action commit in commits) commit();
        foreach (Mesh mesh in meshes) mesh.ReturnScratch();
        stats.milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        stats.AddTo(Totals);
    }

    private static int reportedFailure;

    /// <summary>SURF rockCap (1): the caps that dome.</summary>
    private static bool IsRockCap(float kind) => kind > 0.5f && kind < 1.5f;

    /// <summary>Returns the lane's former stores to the pool unless a store is still the lane's own (warped in place).</summary>
    private static void Release(TerrainTransferBufferPool? pool, Array[] old, params Array[] current)
    {
        if (pool == null) return;
        foreach (Array array in old)
            if (Array.IndexOf(current, array) < 0) pool.release(array);
    }

    // ───────────────────────────────────────── field ─────────────────────────────────────────

    /// <summary>
    /// The displacement, a pure function of the compile-space position (x, y, z):
    /// * <b>Wobble</b> (horizontal): relief mask × (outline gesture + detail), the same for every height. The relief
    ///   mask lives on the frame's cell corners — 1 where terrain of different height or kind meets, ½ one corner
    ///   further, 0 on open ground — interpolated with smoothstep weights (C¹ across cells).
    /// * <b>Fillets</b> (horizontal): every inside corner of a cliff, shore or chasm rim — one low cell under three high
    ///   ones — is pushed diagonally into the low cell by a smooth bump, so the two walls meeting there bend into one
    ///   rounded hollow. (Outside corners are rounded by the compiler itself, TerrainVisualContour.OrganicCorners.)
    /// * <b>Domes</b> (vertical): a rock cap (Solid) rises towards its middle. A half-cell lattice carries, for the
    ///   height being asked about, the share of its cells with rock caps supporting that height: 1 at a rock
    ///   cell's centre, ½ on its rim, ¼ at an outer corner. The lift continues above the cap so higher bevels and
    ///   adjoining terraces cannot fall through its crest; the lower floor at the wall's foot stays in place.
    /// Every part is 0 at the corners of pinned cells (bridges, underpasses, clefts) and ½ one
    /// corner further, so structures placed on the raster keep their ground.
    /// </summary>
    public sealed partial class Field
    {
        private readonly double originX, originY, tileSize;
        private readonly int i0, j0, cellsW, cellsH, cornersW, cornersH;
        private readonly float[] wobble;
        private readonly float[] filletX, filletZ;
        /// <summary>Per cell: a fillet bump reaches into it.</summary>
        private readonly bool[] filletNear;
        /// <summary>Per cell: its rock cap height in px (NaN: not a rock cap that domes).</summary>
        private readonly double[] domeY;
        /// <summary>Per cell: some part of the field can move a point inside it.</summary>
        private readonly bool[] moves;
        /// <summary>Per cell: a doming rock cap lies within one cell.</summary>
        private readonly bool[] domeNear;
        private readonly bool anyDome;
        public readonly bool any;

        private const double OUTLINE_ANGLE = 0.541; // ≈ 31°: no lattice axis of the noise runs along the cell raster
        private const double DETAIL_ANGLE = -0.401; // ≈ −23°
        private static readonly double OutlineCos = System.Math.Cos(OUTLINE_ANGLE), OutlineSin = System.Math.Sin(OUTLINE_ANGLE);
        private static readonly double DetailCos = System.Math.Cos(DETAIL_ANGLE), DetailSin = System.Math.Sin(DETAIL_ANGLE);
        private const double LOW_KIND = -1000; // water and chasm cells are "low" at every corner they share

        internal Field(TerrainBakeFrame frame, MaterializedTerrain terrain)
        {
            originX = frame.originX;
            originY = frame.originY;
            tileSize = frame.tileSize;
            i0 = frame.i0;
            j0 = frame.j0;
            int own = TerrainTileLattice.TILE_BORDER, cells = TerrainTileLattice.TILE_CELLS;
            seamX = new[] { originX + (i0 + own) * tileSize, originX + (i0 + own + cells) * tileSize };
            seamZ = new[] { originY + (j0 + own) * tileSize, originY + (j0 + own + cells) * tileSize };
            int w = cellsW = terrain.width, h = cellsH = terrain.height;
            cornersW = w + 1;
            cornersH = h + 1;
            var pinnedCell = new bool[w * h];
            var height = new double[w * h];
            for (int i = 0; i < w * h; i++)
            {
                TerrainCell? cell = i < terrain.cells.Length ? terrain.cells[i] : null;
                if (cell == null) { pinnedCell[i] = true; height[i] = double.NaN; continue; }
                int type = cell.type;
                if (type == TileType.Bridge || type == TileType.Underpass || type == TileType.Cleft) pinnedCell[i] = true;
                height[i] = type == TileType.Water || type == TileType.Chasm ? LOW_KIND + cell.surfaceZ : cell.surfaceZ;
            }

            // Corner classes: relief (terrain of different height/kind meets), pinned (touches a pinned cell), and the
            // inside corners (exactly one cell lower than the three others by at least half a level).
            int corners = cornersW * cornersH;
            var relief = new bool[corners];
            var pinned = new bool[corners];
            var insideToward = new int[corners];
            Array.Fill(insideToward, -1);
            Span<double> around = stackalloc double[4];
            for (int cy = 0; cy < cornersH; cy++)
                for (int cx = 0; cx < cornersW; cx++)
                {
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    bool pin = false, complete = true;
                    for (int k = 0; k < 4; k++)
                    {
                        int x = cx - 1 + (k & 1), y = cy - 1 + (k >> 1);
                        around[k] = double.NaN;
                        if (x < 0 || y < 0 || x >= w || y >= h) { complete = false; continue; }
                        int index = y * w + x;
                        if (pinnedCell[index]) pin = true;
                        around[k] = height[index];
                        if (double.IsNaN(height[index])) { complete = false; continue; }
                        minZ = System.Math.Min(minZ, height[index]);
                        maxZ = System.Math.Max(maxZ, height[index]);
                    }
                    int corner = cy * cornersW + cx;
                    pinned[corner] = pin;
                    relief[corner] = maxZ - minZ >= 0.5;
                    if (!complete || pin) continue;
                    int low = -1, lows = 0;
                    for (int k = 0; k < 4; k++)
                        if (around[k] < minZ + 0.5) { low = k; lows++; }
                    if (lows == 1 && maxZ - minZ >= 0.5) insideToward[corner] = low;
                }

            // Inside corners pushing into the same low cell share the push: a one-cell pond or notch rounds, it does not
            // shrink (four corners converging on one cell squeezed it to a third).
            var pushers = new int[w * h];
            for (int cy = 0; cy < cornersH; cy++)
                for (int cx = 0; cx < cornersW; cx++)
                {
                    int toward = insideToward[cy * cornersW + cx];
                    if (toward < 0) continue;
                    int lx = cx - 1 + (toward & 1), ly = cy - 1 + (toward >> 1);
                    pushers[ly * w + lx]++;
                }

            wobble = new float[corners];
            filletX = new float[corners];
            filletZ = new float[corners];
            for (int cy = 0; cy < cornersH; cy++)
                for (int cx = 0; cx < cornersW; cx++)
                {
                    int corner = cy * cornersW + cx;
                    double m = relief[corner] ? 1 : 0;
                    double p = pinned[corner] ? 0 : 1;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = cx + dx, y = cy + dy;
                            if (x < 0 || y < 0 || x >= cornersW || y >= cornersH) continue;
                            int n = y * cornersW + x;
                            if (relief[n]) m = System.Math.Max(m, 0.5);
                            if (pinned[n]) p = System.Math.Min(p, 0.5);
                        }
                    wobble[corner] = (float)(m * p);
                    if (wobble[corner] > 0) any = true;
                    int toward = insideToward[corner];
                    if (toward < 0 || p <= 0) continue;
                    // Towards the low cell's centre: (−½ or +½, −½ or +½) from the corner.
                    int lowX = cx - 1 + (toward & 1), lowY = cy - 1 + (toward >> 1);
                    double d = FILLET_PX * p * System.Math.Sqrt(0.5) / System.Math.Max(1, pushers[lowY * w + lowX]);
                    filletX[corner] = (float)((toward & 1) == 1 ? d : -d);
                    filletZ[corner] = (float)((toward >> 1) == 1 ? d : -d);
                    any = true;
                }
            // A fillet reaches FILLET_RADIUS cells: flag every cell whose points can feel it.
            filletNear = new bool[w * h];
            int reach = (int)System.Math.Ceiling(FILLET_RADIUS);
            for (int cy = 0; cy < cornersH; cy++)
                for (int cx = 0; cx < cornersW; cx++)
                {
                    int corner = cy * cornersW + cx;
                    if (filletX[corner] == 0 && filletZ[corner] == 0) continue;
                    for (int y = cy - reach; y < cy + reach; y++)
                        for (int x = cx - reach; x < cx + reach; x++)
                            if (x >= 0 && y >= 0 && x < w && y < h) filletNear[y * w + x] = true;
                }

            // Domes: rock caps away from pinned cells.
            domeY = new double[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int index = y * w + x;
                    domeY[index] = double.NaN;
                    TerrainCell? cell = index < terrain.cells.Length ? terrain.cells[index] : null;
                    if (cell == null || cell.type != TileType.Solid || pinnedCell[index]) continue;
                    bool nearPin = false;
                    for (int dy = -1; dy <= 1 && !nearPin; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && pinnedCell[ny * w + nx]) { nearPin = true; break; }
                        }
                    if (nearPin) continue;
                    domeY[index] = cell.surfaceZ * TerrainProjection.TERRAIN_ELEVATION_STEP_PX;
                    anyDome = true;
                }
            any |= anyDome;
            // Mountain form (TerrainOrganicForm.Mountain.cs): walls lean back into their massif.
            BuildLean(terrain, height, pinned);
            any |= anyLean;

            domeNear = new bool[w * h];
            moves = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int index = y * w + x;
                    for (int dy = -1; dy <= 1 && !domeNear[index]; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && !double.IsNaN(domeY[ny * w + nx])) { domeNear[index] = true; break; }
                        }
                    int c = y * cornersW + x;
                    moves[index] = domeNear[index] || filletNear[index] || LeanNear(index) ||
                        wobble[c] + wobble[c + 1] + wobble[c + cornersW] + wobble[c + cornersW + 1] > 0;
                }
        }

        /// <summary>Whether the field can move any point of the compile-space rectangle (a cell margin included).</summary>
        public bool MovesIn(double minX, double minZ, double maxX, double maxZ)
        {
            int x0 = System.Math.Max(0, (int)System.Math.Floor(CellX(minX)) - 1), x1 = System.Math.Min(cellsW - 1, (int)System.Math.Floor(CellX(maxX)) + 1);
            int z0 = System.Math.Max(0, (int)System.Math.Floor(CellZ(minZ)) - 1), z1 = System.Math.Min(cellsH - 1, (int)System.Math.Floor(CellZ(maxZ)) + 1);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    if (moves[z * cellsW + x]) return true;
            return false;
        }

        /// <summary>The tile's own-region borders: where neighbouring tiles meet it.</summary>
        public readonly double[] seamX, seamZ;

        private double CellX(double x) => (x - originX) / tileSize - i0;
        private double CellZ(double z) => (z - originY) / tileSize - j0;

        /// <summary>Whether the horizontal field can move points around (x, z) (refinement only splits where it can).</summary>
        public bool Active(double x, double z)
        {
            int cx = (int)System.Math.Floor(CellX(x)), cz = (int)System.Math.Floor(CellZ(z));
            if (cx < 0 || cz < 0 || cx >= cellsW || cz >= cellsH) return false;
            int c = cz * cornersW + cx;
            return wobble[c] + wobble[c + 1] + wobble[c + cornersW] + wobble[c + cornersW + 1] > 0 ||
                filletNear[cz * cellsW + cx];
        }

        /// <summary>Whether (x, z) lies on or next to a rock cap that domes.</summary>
        public bool DomeActive(double x, double z)
        {
            if (!anyDome) return false;
            int cx = (int)System.Math.Floor(CellX(x)), cz = (int)System.Math.Floor(CellZ(z));
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x1 = cx + dx, z1 = cz + dz;
                    if (x1 >= 0 && z1 >= 0 && x1 < cellsW && z1 < cellsH && !double.IsNaN(domeY[z1 * cellsW + x1])) return true;
                }
            return false;
        }

        /// <summary>
        /// The displacement (wx, wy, wz) at a compile-space point and, if <paramref name="j"/> has 8 slots, its
        /// Jacobian: j = (∂wx/∂x, ∂wx/∂y, ∂wx/∂z, ∂wz/∂x, ∂wz/∂y, ∂wz/∂z, ∂wy/∂x, ∂wy/∂z); ∂wy/∂y is 0 within
        /// each supported height band. Returns false where nothing moves.
        /// </summary>
        public bool Sample(double x, double y, double z, out double wx, out double wy, out double wz, Span<double> j,
            bool domes = true)
        {
            wx = wy = wz = 0;
            bool jac = j.Length >= 8;
            if (jac) j.Clear();
            double gx = CellX(x), gz = CellZ(z);
            double fx = System.Math.Floor(gx), fz = System.Math.Floor(gz);
            int cx = (int)fx, cz = (int)fz;
            if (cx < 0 || cz < 0 || cx >= cellsW || cz >= cellsH || !moves[cz * cellsW + cx]) return false;
            bool moved = false;

            // Wobble: relief mask × shape.
            int c = cz * cornersW + cx;
            double m00 = wobble[c], m10 = wobble[c + 1], m01 = wobble[c + cornersW], m11 = wobble[c + cornersW + 1];
            if (m00 + m10 + m01 + m11 > 0)
            {
                double tx = gx - fx, tz = gz - fz;
                double sx = tx * tx * (3 - 2 * tx), sz = tz * tz * (3 - 2 * tz);
                double top = m00 + (m10 - m00) * sx, bottom = m01 + (m11 - m01) * sx;
                double m = top + (bottom - top) * sz;
                if (m > 0)
                {
                    Span<double> sj = stackalloc double[6];
                    Shape(x, y, z, out double shx, out double shz, jac ? sj : Span<double>.Empty);
                    wx += m * shx;
                    wz += m * shz;
                    if (jac)
                    {
                        double dsx = 6 * tx * (1 - tx) / tileSize, dsz = 6 * tz * (1 - tz) / tileSize;
                        double mdx = ((m10 - m00) + ((m11 - m01) - (m10 - m00)) * sz) * dsx;
                        double mdz = (bottom - top) * dsz;
                        j[0] += m * sj[0] + mdx * shx;
                        j[1] += m * sj[1];
                        j[2] += m * sj[2] + mdz * shx;
                        j[3] += m * sj[3] + mdx * shz;
                        j[4] += m * sj[4];
                        j[5] += m * sj[5] + mdz * shz;
                    }
                    moved = true;
                }
            }

            // Fillets: smooth bumps (1 − r²)² around the inside corners within reach.
            if (filletNear[cz * cellsW + cx])
            {
                int reach = (int)System.Math.Ceiling(FILLET_RADIUS);
                for (int ky = cz - reach + 1; ky <= cz + reach; ky++)
                    for (int kx = cx - reach + 1; kx <= cx + reach; kx++)
                    {
                        if (kx < 0 || ky < 0 || kx >= cornersW || ky >= cornersH) continue;
                        int k = ky * cornersW + kx;
                        double vx = filletX[k], vz = filletZ[k];
                        if (vx == 0 && vz == 0) continue;
                        double rx = (gx - kx) / FILLET_RADIUS, rz = (gz - ky) / FILLET_RADIUS;
                        double r2 = rx * rx + rz * rz;
                        if (r2 >= 1) continue;
                        double q = 1 - r2;
                        wx += vx * q * q;
                        wz += vz * q * q;
                        if (jac)
                        {
                            // ∂φ/∂x = −4 q r_x / (R · tileSize)
                            double scale = -4 * q / (FILLET_RADIUS * tileSize);
                            double px = scale * rx, pz = scale * rz;
                            j[0] += vx * px;
                            j[2] += vx * pz;
                            j[3] += vz * px;
                            j[5] += vz * pz;
                        }
                        moved = true;
                    }
            }

            // Domes: half-cell lattice of rock-cap shares at this height.
            if (domes && domeNear[cz * cellsW + cx] && DomeAt(gx, gz, y, out double dome, out double ddx, out double ddz))
            {
                // Mountain form: the crest rises and dips along the massif (TerrainOrganicForm.Mountain.cs).
                double crest = CrestScale(x, z, out double cdx, out double cdz);
                wy += dome * crest;
                if (jac)
                {
                    j[6] += ddx * crest + dome * cdx;
                    j[7] += ddz * crest + dome * cdz;
                }
                moved = true;
            }

            // Lean: walls tilt back into their massif. Linear in height, so a vertical edge maps to a straight edge;
            // evaluated at the height after the dome, so a point the dome lifts along a wall edge stays on it.
            if (LeanNear(cz * cellsW + cx) && LeanAt(cx, cz, gx - fx, gz - fz, y + wy, ref wx, ref wz, j)) moved = true;
            return moved;
        }

        /// <summary>Dome lift at (gx, gz) in cell units for points at height <paramref name="y"/>, and its gradient per px.</summary>
        private bool DomeAt(double gx, double gz, double y, out double lift, out double dx, out double dz)
        {
            lift = dx = dz = 0;
            double hx = gx * 2, hz = gz * 2;
            double fx = System.Math.Floor(hx), fz = System.Math.Floor(hz);
            int nx = (int)fx, nz = (int)fz;
            double v00 = Share(nx, nz, y), v10 = Share(nx + 1, nz, y), v01 = Share(nx, nz + 1, y), v11 = Share(nx + 1, nz + 1, y);
            if (v00 + v10 + v01 + v11 <= 0) return false;
            double tx = hx - fx, tz = hz - fz;
            double sx = tx * tx * (3 - 2 * tx), sz = tz * tz * (3 - 2 * tz);
            double top = v00 + (v10 - v00) * sx, bottom = v01 + (v11 - v01) * sx;
            lift = DOME_PX * (top + (bottom - top) * sz);
            double dsx = 6 * tx * (1 - tx) * 2 / tileSize, dsz = 6 * tz * (1 - tz) * 2 / tileSize;
            dx = DOME_PX * ((v10 - v00) + ((v11 - v01) - (v10 - v00)) * sz) * dsx;
            dz = DOME_PX * (bottom - top) * dsz;
            return true;
        }

        /// <summary>Share of nearby rock caps supporting this height. The crest lift continues above its datum so
        /// bevels and adjoining caps cannot drop back down through a lifted rim.</summary>
        private double Share(int nx, int nz, double y)
        {
            int xa = (nx & 1) == 1 ? (nx - 1) >> 1 : (nx >> 1) - 1, xb = (nx & 1) == 1 ? xa : xa + 1;
            int za = (nz & 1) == 1 ? (nz - 1) >> 1 : (nz >> 1) - 1, zb = (nz & 1) == 1 ? za : za + 1;
            int hits = 0, count = 0;
            for (int z = za; z <= zb; z++)
                for (int x = xa; x <= xb; x++)
                {
                    count++;
                    if (x < 0 || z < 0 || x >= cellsW || z >= cellsH) continue;
                    double cap = domeY[z * cellsW + x];
                    if (!double.IsNaN(cap) && y > cap - 0.5) hits++;
                }
            return hits == 0 ? 0 : (double)hits / count;
        }

        /// <summary>Outline gesture + detail before the mask; <paramref name="j"/> (if not empty) receives its gradient.</summary>
        private static void Shape(double x, double y, double z, out double sx, out double sz, Span<double> j)
        {
            // Three scales in absolute world coordinates: massif, shoulder and edge. No tile-dependent seeds.
            double ou = (x * OutlineCos - z * OutlineSin) / OUTLINE_WAVELENGTH;
            double ov = (x * OutlineSin + z * OutlineCos) / OUTLINE_WAVELENGTH;
            Noise2Pair(ou, ov, 0x3a1d_07e5u, out double ax, out double axu, out double axv, out double az, out double azu, out double azv);
            double du = (x * DetailCos - z * DetailSin) / DETAIL_WAVELENGTH;
            double dv = (x * DetailSin + z * DetailCos) / DETAIL_WAVELENGTH;
            Noise2Pair(du, dv, 0x5e27_c3a9u, out double bx, out double bxu, out double bxv, out double bz, out double bzu, out double bzv);
            double mu = (x * OutlineCos - z * OutlineSin) / MASSIF_WAVELENGTH;
            double mv = (x * OutlineSin + z * OutlineCos) / MASSIF_WAVELENGTH;
            Noise2Pair(mu, mv, 0x71bd_42c3u, out double mx, out double mxu, out double mxv, out double mz, out double mzu, out double mzv);
            sx = OUTLINE_AMPLITUDE * ax + DETAIL_AMPLITUDE * bx + MASSIF_AMPLITUDE * mx;
            sz = OUTLINE_AMPLITUDE * az + DETAIL_AMPLITUDE * bz + MASSIF_AMPLITUDE * mz;
            if (j.Length < 6) return;
            const double oa = OUTLINE_AMPLITUDE / OUTLINE_WAVELENGTH, da = DETAIL_AMPLITUDE / DETAIL_WAVELENGTH;
            // ∂u/∂x = cos/λ, ∂v/∂x = sin/λ, ∂u/∂z = −sin/λ, ∂v/∂z = cos/λ.
            j[0] = oa * (axu * OutlineCos + axv * OutlineSin) + da * (bxu * DetailCos + bxv * DetailSin);
            j[1] = 0;
            j[2] = oa * (-axu * OutlineSin + axv * OutlineCos) + da * (-bxu * DetailSin + bxv * DetailCos);
            j[3] = oa * (azu * OutlineCos + azv * OutlineSin) + da * (bzu * DetailCos + bzv * DetailSin);
            j[4] = 0;
            j[5] = oa * (-azu * OutlineSin + azv * OutlineCos) + da * (-bzu * DetailSin + bzv * DetailCos);
            const double ma = MASSIF_AMPLITUDE / MASSIF_WAVELENGTH;
            j[0] += ma * (mxu * OutlineCos + mxv * OutlineSin);
            j[2] += ma * (-mxu * OutlineSin + mxv * OutlineCos);
            j[3] += ma * (mzu * OutlineCos + mzv * OutlineSin);
            j[5] += ma * (-mzu * OutlineSin + mzv * OutlineCos);
        }

        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static double FadeD(double t) => 30 * t * t * (t * (t - 2) + 1);

        /// <summary>Two independent lattice values in [−1, 1] from one hash (the two halves of its 32 bits).</summary>
        private static void Hash2(int x, int y, int z, uint seed, out double a, out double b)
        {
            unchecked
            {
                uint h = seed ^ ((uint)x * 0x8da6b343u) ^ ((uint)y * 0xd8163841u) ^ ((uint)z * 0xcb1ab31fu);
                h ^= h >> 16;
                h *= 0x7feb352du;
                h ^= h >> 15;
                h *= 0x846ca68bu;
                h ^= h >> 16;
                a = (h & 0xffff) * (2.0 / 65535.0) - 1;
                b = (h >> 16) * (2.0 / 65535.0) - 1;
            }
        }

        /// <summary>Two channels of quintic value noise in [−1, 1] with their analytic gradients.</summary>
        internal static void Noise2Pair(double u, double v, uint seed,
            out double a, out double adu, out double adv, out double b, out double bdu, out double bdv)
        {
            double fu = System.Math.Floor(u), fv = System.Math.Floor(v);
            int iu = (int)fu, iv = (int)fv;
            double tu = u - fu, tv = v - fv;
            double su = Fade(tu), sv = Fade(tv), dsu = FadeD(tu), dsv = FadeD(tv);
            Hash2(iu, iv, 0, seed, out double a00, out double b00);
            Hash2(iu + 1, iv, 0, seed, out double a10, out double b10);
            Hash2(iu, iv + 1, 0, seed, out double a01, out double b01);
            Hash2(iu + 1, iv + 1, 0, seed, out double a11, out double b11);
            Bilinear(a00, a10, a01, a11, su, sv, dsu, dsv, out a, out adu, out adv);
            Bilinear(b00, b10, b01, b11, su, sv, dsu, dsv, out b, out bdu, out bdv);
        }

        private static void Bilinear(double c00, double c10, double c01, double c11, double su, double sv, double dsu, double dsv,
            out double value, out double du, out double dv)
        {
            double k1 = c10 - c00, k2 = c01 - c00, k3 = c00 - c10 - c01 + c11;
            du = (k1 + k3 * sv) * dsu;
            dv = (k2 + k3 * su) * dsv;
            value = c00 + k1 * su + k2 * sv + k3 * su * sv;
        }
    }

    // ───────────────────────────────────────── mesh ─────────────────────────────────────────

    internal enum ChannelKind { Linear, Normal, FirstDiscrete }

    /// <summary>One lane under edit: growable vertex channels (0 = position) and indices.</summary>
    internal sealed partial class Mesh
    {
        private readonly Field field;
        private readonly List<float[]> data = new();
        private readonly List<int> width = new();
        private readonly List<ChannelKind> kind = new();
        private readonly List<bool> owned = new();
        private int vertexCount, capacity;
        private uint[] index;
        private int indexCount;
        private bool indexOwned;
        private bool[]? rigidVertex;
        /// <summary>
        /// Scratch rented from the shared array pools (a bake allocated ~38 MB of garbage per tile before, and garbage on
        /// the bake workers pauses the main thread). Rented stores never reach the payload: <see cref="Take"/> copies.
        /// </summary>
        private readonly List<Array> rented = new();

        private T[] Rent<T>(int length)
        {
            T[] array = ArrayPool<T>.Shared.Rent(System.Math.Max(1, length));
            rented.Add(array);
            return array;
        }

        /// <summary>Gives every rented store back (after the commit copied what the payload keeps).</summary>
        public void ReturnScratch()
        {
            foreach (Array array in rented)
                switch (array)
                {
                    case float[] f: ArrayPool<float>.Shared.Return(f); break;
                    case uint[] u: ArrayPool<uint>.Shared.Return(u); break;
                    case int[] i: ArrayPool<int>.Shared.Return(i); break;
                    case double[] d: ArrayPool<double>.Shared.Return(d); break;
                    case bool[] b: ArrayPool<bool>.Shared.Return(b); break;
                }
            rented.Clear();
        }

        private void GrowRigid(int size)
        {
            if (rigidVertex == null || rigidVertex.Length >= size) return;
            var grown = Rent<bool>(size);
            Array.Copy(rigidVertex, grown, rigidVertex.Length);
            rigidVertex = grown;
        }

        /// <summary>The tile's seam planes (compile px): x = seamX[i], z = seamZ[i].</summary>
        public double[] seamX = Array.Empty<double>(), seamZ = Array.Empty<double>();

        public Mesh(Field field, float[] position, uint[] index)
        {
            this.field = field;
            seamX = field.seamX;
            seamZ = field.seamZ;
            vertexCount = capacity = position.Length / 3;
            data.Add(position);
            width.Add(3);
            kind.Add(ChannelKind.Linear);
            owned.Add(false);
            this.index = index;
            indexCount = index.Length;
        }

        public int AddChannel(float[] values, int w, ChannelKind k)
        {
            data.Add(values);
            width.Add(w);
            kind.Add(k);
            owned.Add(false);
            return data.Count - 1;
        }

        public void MarkRigid(Func<int, bool> rigid)
        {
            rigidVertex = Rent<bool>(vertexCount);
            for (int v = 0; v < vertexCount; v++) rigidVertex[v] = rigid(v);
        }

        private bool RigidTriangle(int t) =>
            rigidVertex != null &&
            (index[t] < rigidVertex.Length && rigidVertex[index[t]] ||
             index[t + 1] < rigidVertex.Length && rigidVertex[index[t + 1]] ||
             index[t + 2] < rigidVertex.Length && rigidVertex[index[t + 2]]);

        public int TriangleCount => indexCount / 3;
        public int TriangleVertex(int triangle, int corner) => (int)index[triangle * 3 + corner];

        /// <summary>A new vertex; <paramref name="values"/> holds channels 1.. in order (all components).</summary>
        public int AppendVertex(double x, double y, double z, ReadOnlySpan<float> values)
        {
            EnsureVertices(1);
            for (int c = 0; c < data.Count; c++) EnsureOwned(c);
            int v = vertexCount++;
            data[0][v * 3] = (float)x;
            data[0][v * 3 + 1] = (float)y;
            data[0][v * 3 + 2] = (float)z;
            int offset = 0;
            for (int c = 1; c < data.Count; c++)
            {
                int w = width[c];
                for (int k = 0; k < w; k++) data[c][v * w + k] = offset + k < values.Length ? values[offset + k] : 0;
                offset += w;
            }
            if (rigidVertex != null)
            {
                GrowRigid(capacity);
                rigidVertex[v] = true;
            }
            return v;
        }

        public void AppendTriangle(int a, int b, int c)
        {
            if (!indexOwned)
            {
                var copy = Rent<uint>(indexCount + 48);
                Array.Copy(index, copy, indexCount);
                index = copy;
                indexOwned = true;
            }
            Emit(a, b, c);
        }

        /// <summary>Component k of vertex v in channel c (current, including added vertices).</summary>
        public float Get(int channel, int v, int k) => data[channel][v * width[channel] + k];

        /// <summary>Gives the channel its own store before an in-place edit: the payload's arrays stay untouched until commit.</summary>
        private void EnsureOwned(int channel)
        {
            if (owned[channel]) return;
            var copy = Rent<float>(capacity * width[channel]);
            Array.Copy(data[channel], copy, vertexCount * width[channel]);
            data[channel] = copy;
            owned[channel] = true;
        }

        /// <summary>Whether the field moves any point of triangle (a, b, c) (its bounding rectangle, a cell margin included).</summary>
        private bool TriangleMoves(int a, int b, int c)
        {
            double minX = System.Math.Min(Px(a), System.Math.Min(Px(b), Px(c))), maxX = System.Math.Max(Px(a), System.Math.Max(Px(b), Px(c)));
            double minZ = System.Math.Min(Pz(a), System.Math.Min(Pz(b), Pz(c))), maxZ = System.Math.Max(Pz(a), System.Math.Max(Pz(b), Pz(c)));
            return field.MovesIn(minX, minZ, maxX, maxZ);
        }

        private float Px(int v) => data[0][v * 3];
        private float Py(int v) => data[0][v * 3 + 1];
        private float Pz(int v) => data[0][v * 3 + 2];

        private void EnsureVertices(int extra)
        {
            if (vertexCount + extra <= capacity) return;
            int next = System.Math.Max(capacity * 3 / 2 + 16, vertexCount + extra);
            for (int c = 0; c < data.Count; c++)
            {
                var grown = Rent<float>(next * width[c]);
                Array.Copy(data[c], grown, vertexCount * width[c]);
                data[c] = grown;
                owned[c] = true;
            }
            capacity = next;
        }

        private void EnsureIndices(int extra)
        {
            if (indexOwned && indexCount + extra <= index.Length) return;
            var grown = Rent<uint>(System.Math.Max(index.Length * 3 / 2 + 48, indexCount + extra));
            Array.Copy(index, grown, indexCount);
            index = grown;
            indexOwned = true;
        }

        /// <summary>A vertex at (<paramref name="px"/>, <paramref name="py"/>, <paramref name="pz"/>) (exact floats) with the attributes of a→b at t.</summary>
        private int AddVertex(int a, int b, double t, float px, float py, float pz)
        {
            EnsureVertices(1);
            int v = vertexCount++;
            float[] p = data[0];
            p[v * 3] = px;
            p[v * 3 + 1] = py;
            p[v * 3 + 2] = pz;
            for (int c = 1; c < data.Count; c++)
            {
                float[] d = data[c];
                int w = width[c];
                for (int k = 0; k < w; k++)
                    d[v * w + k] = (float)(d[a * w + k] + (d[b * w + k] - d[a * w + k]) * t);
                if (kind[c] == ChannelKind.Normal)
                {
                    double nx = d[v * 3], ny = d[v * 3 + 1], nz = d[v * 3 + 2];
                    double len = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-9)
                    {
                        d[v * 3] = (float)(nx / len);
                        d[v * 3 + 1] = (float)(ny / len);
                        d[v * 3 + 2] = (float)(nz / len);
                    }
                    else Array.Copy(d, (t < 0.5 ? a : b) * 3, d, v * 3, 3);
                }
                else if (kind[c] == ChannelKind.FirstDiscrete)
                    d[v * w] = d[(t < 0.5 ? a : b) * w];
            }
            if (rigidVertex != null)
            {
                GrowRigid(capacity);
                rigidVertex[v] = false;
            }
            return v;
        }

        private void Emit(int a, int b, int c)
        {
            EnsureIndices(3);
            index[indexCount++] = (uint)a;
            index[indexCount++] = (uint)b;
            index[indexCount++] = (uint)c;
        }

        // ── T-junctions ──

        private struct Split
        {
            public double t;
            public float x, y, z;
        }

        /// <summary>
        /// Splits every edge lying in one of the tile's seam planes at canonical world positions (<see cref="AddSeamSplits"/>),
        /// in the neighbour tile alike: the two borders then share their vertices up to SEAM_STEP_PX apart.
        /// </summary>
        public void SplitSeamEdges(OrganicStats stats, bool mountain = true)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            seamMountain = mountain;
            try
            {
                if (indexCount == 0 || (seamX.Length == 0 && seamZ.Length == 0)) return;
                var edgeSplits = new List<Split>[3];
                for (int e = 0; e < 3; e++) edgeSplits[e] = new List<Split>();
                var original = index;
                int originalCount = indexCount;
                index = Rent<uint>(originalCount + 48);
                indexOwned = true;
                indexCount = 0;
                for (int t = 0; t < originalCount; t += 3)
                {
                    int a = (int)original[t], b = (int)original[t + 1], c = (int)original[t + 2];
                    bool rigid = rigidVertex != null &&
                        (a < rigidVertex.Length && rigidVertex[a] || b < rigidVertex.Length && rigidVertex[b] || c < rigidVertex.Length && rigidVertex[c]);
                    int found = 0;
                    if (!rigid)
                    {
                        found += SeamSplits(a, b, edgeSplits[0]);
                        found += SeamSplits(b, c, edgeSplits[1]);
                        found += SeamSplits(c, a, edgeSplits[2]);
                    }
                    if (found == 0 || !TriangleMoves(a, b, c))
                    {
                        Emit(a, b, c);
                        continue;
                    }
                    int before = indexCount, vertices = vertexCount;
                    SplitTriangle(a, b, c, edgeSplits[0], edgeSplits[1], edgeSplits[2]);
                    stats.seamTriangles += (indexCount - before) / 3 - 1;
                    stats.addedTriangles += (indexCount - before) / 3 - 1;
                    stats.addedVertices += vertexCount - vertices;
                }
            }
            finally { stats.repairMs += Elapsed(started); }
        }

        private int SeamSplits(int a, int b, List<Split> splits)
        {
            splits.Clear();
            double ax = Px(a), ay = Py(a), az = Pz(a), bx = Px(b), by = Py(b), bz = Pz(b);
            double len = System.Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
            if (len < 1) return 0;
            AddSeamSplits(ax, ay, az, bx, by, bz, len, splits);
            if (splits.Count > 1) splits.Sort(ByT);
            return splits.Count;
        }

        // Per-thread collections of the T-junction search (cleared per use, their capacity kept).
        [ThreadStatic] private static Dictionary<PositionKey, int>? UniqueScratch;
        [ThreadStatic] private static List<(int point, double t, double dist)>? FoundScratch;
        [ThreadStatic] private static HashSet<long>? SearchedScratch, SeamHostScratch;

        // T-junction snapping state (FindTJunctions → Warp → SnapTJunctions).
        private int[]? vertexPoint;
        private int pointCount;
        private int[]? snapA, snapB;
        private double[]? snapT;
        /// <summary>Per point: its offset from the host edge before the displacement (3 per point).</summary>
        private double[]? snapOffset;

        /// <summary>
        /// A point closer than this to its host edge IS on it (float rounding of positions compiled on the edge); a
        /// seam split then lands on the point itself so both tiles share the vertex bit for bit.
        /// </summary>
        private const double ON_EDGE_PX = 0.002;

        /// <summary>
        /// Before the displacement: finds every vertex lying strictly inside another face's edge (a T-junction) and
        /// remembers the host edge and where on it the vertex lies. <see cref="SnapTJunctions"/> puts the displaced vertex
        /// back onto the displaced edge: the lane stays watertight without a single extra face.
        /// </summary>
        public void FindTJunctions(OrganicStats stats)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                int triangles = indexCount;
                if (triangles == 0) return;
                // Unique positions of the non-rigid faces the field moves.
                var unique = UniqueScratch ??= new Dictionary<PositionKey, int>();
                unique.Clear();
                vertexPoint = Rent<int>(vertexCount);
                Array.Fill(vertexPoint, -1, 0, vertexCount);
                var points = Rent<float>(vertexCount * 3);
                var relevant = Rent<bool>(triangles / 3);
                Array.Clear(relevant, 0, triangles / 3);
                int count = 0;
                for (int t = 0; t < triangles; t += 3)
                {
                    if (RigidTriangle(t) || !TriangleMoves((int)index[t], (int)index[t + 1], (int)index[t + 2])) continue;
                    relevant[t / 3] = true;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = (int)index[t + k];
                        if (vertexPoint[v] >= 0) continue;
                        var key = new PositionKey(Px(v), Py(v), Pz(v));
                        if (!unique.TryGetValue(key, out int id))
                        {
                            id = count++;
                            unique[key] = id;
                            points[id * 3] = key.x;
                            points[id * 3 + 1] = key.y;
                            points[id * 3 + 2] = key.z;
                        }
                        vertexPoint[v] = id;
                    }
                }
                pointCount = count;
                // Every vertex at such a position belongs to its point (also those of rigid or unmoved faces).
                for (int v = 0; v < vertexCount; v++)
                    if (vertexPoint[v] < 0 && unique.TryGetValue(new PositionKey(Px(v), Py(v), Pz(v)), out int id))
                        vertexPoint[v] = id;
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    minX = System.Math.Min(minX, points[i * 3]);
                    maxX = System.Math.Max(maxX, points[i * 3]);
                    minZ = System.Math.Min(minZ, points[i * 3 + 2]);
                    maxZ = System.Math.Max(maxZ, points[i * 3 + 2]);
                }
                var grid = new ColumnGrid(minX, minZ, maxX, maxZ, count, this);
                for (int i = 0; i < count; i++) grid.Add(i, points[i * 3], points[i * 3 + 2]);

                // One host per point: the nearest edge it lies on, the longest of equally near ones. A point in a seam
                // plane is shared with the neighbour tile, which cannot know this host: it keeps its place and its host
                // triangle is split there instead.
                //
                // Nearest, not longest: T_JUNCTION_EPS also reaches vertices of a parallel layer just above or below an
                // edge — a bridge's planks lie 0.035 px over the seam bed under them (TerrainBridgeSoffit). Snapped onto
                // the bed's edge, a plank corner dropped into the bed and the two layers z-fought across the whole deck.
                // For the same reason every snapped point keeps its own offset from its host (snapOffset).
                snapA = Rent<int>(count);
                snapB = Rent<int>(count);
                snapT = Rent<double>(count);
                snapOffset = Rent<double>(count * 3);
                var hostLength = Rent<double>(count);
                var hostDistance = Rent<double>(count);
                Array.Fill(snapA, -1, 0, count);
                var found = FoundScratch ??= new List<(int point, double t, double dist)>();
                var seamSplits = new Dictionary<int, List<Split>[]>();
                // An edge shared by two faces is searched once, unless it hosts seam points (both faces need the split).
                var searched = SearchedScratch ??= new HashSet<long>(EdgeKeyComparer.Instance);
                var seamHosts = SeamHostScratch ??= new HashSet<long>(EdgeKeyComparer.Instance);
                searched.Clear();
                seamHosts.Clear();
                for (int t = 0; t < triangles; t += 3)
                {
                    if (!relevant[t / 3]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int a = (int)index[t + k], b = (int)index[t + (k + 1) % 3];
                        int pa = vertexPoint[a], pb = vertexPoint[b];
                        long edge = pa < pb ? ((long)pa << 32) | (uint)pb : ((long)pb << 32) | (uint)pa;
                        if (!searched.Add(edge) && !seamHosts.Contains(edge)) continue;
                        double len = Collect(a, b, found, grid, points);
                        foreach (var (point, at, dist) in found)
                        {
                            stats.tJunctions++;
                            // Where the point projects onto the edge.
                            double hx = Px(a) + (Px(b) - Px(a)) * at, hy = Py(a) + (Py(b) - Py(a)) * at, hz = Pz(a) + (Pz(b) - Pz(a)) * at;
                            if (OnSeam(points[point * 3], points[point * 3 + 2]))
                            {
                                seamHosts.Add(edge);
                                if (!seamSplits.TryGetValue(t, out var lists))
                                    seamSplits[t] = lists = new[] { new List<Split>(), new List<Split>(), new List<Split>() };
                                // A point of a parallel layer splits the edge where it projects: the host stays in its plane.
                                bool on = dist <= ON_EDGE_PX;
                                lists[k].Add(new Split
                                {
                                    t = at,
                                    x = on ? points[point * 3] : (float)hx,
                                    y = on ? points[point * 3 + 1] : (float)hy,
                                    z = on ? points[point * 3 + 2] : (float)hz,
                                });
                                continue;
                            }
                            if (snapA[point] >= 0 &&
                                (dist > hostDistance[point] + ON_EDGE_PX ||
                                    dist >= hostDistance[point] - ON_EDGE_PX && hostLength[point] >= len)) continue;
                            snapA[point] = vertexPoint[a];
                            snapB[point] = vertexPoint[b];
                            snapT[point] = at;
                            hostLength[point] = len;
                            hostDistance[point] = dist;
                            snapOffset[point * 3] = points[point * 3] - hx;
                            snapOffset[point * 3 + 1] = points[point * 3 + 1] - hy;
                            snapOffset[point * 3 + 2] = points[point * 3 + 2] - hz;
                        }
                    }
                }
                if (seamSplits.Count > 0) SplitAtSeamPoints(seamSplits, unique, stats);
            }
            finally { stats.repairMs += Elapsed(started); }
        }

        /// <summary>Edge keys (two point ids): long's own hash xors the halves, which collides for neighbouring ids.</summary>
        private sealed class EdgeKeyComparer : IEqualityComparer<long>
        {
            public static readonly EdgeKeyComparer Instance = new();
            public bool Equals(long a, long b) => a == b;
            public int GetHashCode(long key)
            {
                unchecked
                {
                    ulong h = (ulong)key * 0x9E3779B97F4A7C15ul;
                    return (int)(h >> 32) ^ (int)h;
                }
            }
        }

        private bool OnSeam(double x, double z)
        {
            foreach (double sx in seamX) if (System.Math.Abs(x - sx) < 0.01) return true;
            foreach (double sz in seamZ) if (System.Math.Abs(z - sz) < 0.01) return true;
            return false;
        }

        /// <summary>Splits the host triangles of T-junction points lying in a seam plane (they must not move).</summary>
        private void SplitAtSeamPoints(Dictionary<int, List<Split>[]> seamSplits, Dictionary<PositionKey, int> unique, OrganicStats stats)
        {
            var original = index;
            int originalCount = indexCount;
            index = Rent<uint>(originalCount + seamSplits.Count * 12 + 48);
            indexOwned = true;
            indexCount = 0;
            int firstNew = vertexCount;
            for (int t = 0; t < originalCount; t += 3)
            {
                int a = (int)original[t], b = (int)original[t + 1], c = (int)original[t + 2];
                if (!seamSplits.TryGetValue(t, out var lists))
                {
                    Emit(a, b, c);
                    continue;
                }
                foreach (var list in lists)
                    if (list.Count > 1) list.Sort(ByT);
                int before = indexCount;
                SplitTriangle(a, b, c, lists[0], lists[1], lists[2]);
                stats.seamTriangles += (indexCount - before) / 3 - 1;
                stats.addedTriangles += (indexCount - before) / 3 - 1;
            }
            stats.addedVertices += vertexCount - firstNew;
            // The new vertices sit on existing points (the seam T-junction positions).
            if (vertexPoint!.Length < vertexCount)
            {
                var grown = Rent<int>(vertexCount);
                Array.Copy(vertexPoint, grown, vertexPoint.Length);
                vertexPoint = grown;
            }
            for (int v = firstNew; v < vertexCount; v++)
                vertexPoint[v] = unique.TryGetValue(new PositionKey(Px(v), Py(v), Pz(v)), out int id) ? id : -1;
        }

        /// <summary>
        /// After the displacement: moves every T-junction vertex onto its host edge's displaced chord, at the offset it had
        /// from the edge before (three passes, so a host whose own ends are T-junctions settles first). Coincident vertices
        /// move together.
        /// </summary>
        public void SnapTJunctions(OrganicStats stats)
        {
            if (vertexPoint == null || snapA == null || snapB == null || snapT == null || snapOffset == null) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            float[] p = data[0];
            var snapped = Rent<double>(pointCount * 3);
            var known = Rent<bool>(pointCount);
            Array.Clear(known, 0, pointCount);
            for (int v = 0; v < vertexCount && v < vertexPoint.Length; v++)
            {
                int id = vertexPoint[v];
                if (id < 0 || known[id]) continue;
                known[id] = true;
                snapped[id * 3] = p[v * 3];
                snapped[id * 3 + 1] = p[v * 3 + 1];
                snapped[id * 3 + 2] = p[v * 3 + 2];
            }
            for (int pass = 0; pass < 3; pass++)
                for (int id = 0; id < pointCount; id++)
                {
                    int a = snapA[id], b = snapB[id];
                    if (a < 0 || b < 0) continue;
                    double t = snapT[id];
                    for (int k = 0; k < 3; k++)
                        snapped[id * 3 + k] = snapped[a * 3 + k] + (snapped[b * 3 + k] - snapped[a * 3 + k]) * t + snapOffset[id * 3 + k];
                }
            for (int v = 0; v < vertexCount && v < vertexPoint.Length; v++)
            {
                int id = vertexPoint[v];
                if (id < 0 || snapA[id] < 0) continue;
                p[v * 3] = (float)snapped[id * 3];
                p[v * 3 + 1] = (float)snapped[id * 3 + 1];
                p[v * 3 + 2] = (float)snapped[id * 3 + 2];
            }
            stats.repairMs += Elapsed(started);
        }

        private static double Elapsed(long started) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Exact position identity (bitwise floats; −0 and 0 are the same place).</summary>
        private readonly struct PositionKey : IEquatable<PositionKey>
        {
            public readonly float x, y, z;
            public PositionKey(float x, float y, float z)
            {
                this.x = x == 0 ? 0 : x;
                this.y = y == 0 ? 0 : y;
                this.z = z == 0 ? 0 : z;
            }
            public bool Equals(PositionKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object? obj) => obj is PositionKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    uint h = (uint)BitConverter.SingleToInt32Bits(x) * 0x9E3779B1u;
                    h = (h ^ (h >> 15) ^ (uint)BitConverter.SingleToInt32Bits(y)) * 0x85EBCA77u;
                    h = (h ^ (h >> 13) ^ (uint)BitConverter.SingleToInt32Bits(z)) * 0xC2B2AE3Du;
                    return (int)(h ^ (h >> 16));
                }
            }
        }

        /// <summary>Points bucketed into square columns (x, z) of <see cref="Cell"/> px over a known extent.</summary>
        private sealed class ColumnGrid
        {
            public const double Cell = 16;
            public readonly double x0, z0;
            public readonly int w, h;
            public readonly int[] head, next;

            public ColumnGrid(double minX, double minZ, double maxX, double maxZ, int points, Mesh scratch)
            {
                x0 = minX - Cell;
                z0 = minZ - Cell;
                w = System.Math.Max(1, (int)((maxX - x0) / Cell) + 2);
                h = System.Math.Max(1, (int)((maxZ - z0) / Cell) + 2);
                if ((long)w * h > 4_000_000) { w = h = 1; x0 = z0 = 0; }
                head = scratch.Rent<int>(w * h);
                Array.Fill(head, -1, 0, w * h);
                next = scratch.Rent<int>(points);
            }

            public int Column(double v, double origin, int size) =>
                System.Math.Clamp((int)System.Math.Floor((v - origin) / Cell), 0, size - 1);

            public void Add(int i, double x, double z)
            {
                int c = Column(z, z0, h) * w + Column(x, x0, w);
                next[i] = head[c];
                head[c] = i;
            }
        }
        private static readonly Comparison<Split> ByT = (l, r) => l.t.CompareTo(r.t);

        /// <summary>
        /// The points strictly inside segment a→b (within <see cref="T_JUNCTION_EPS"/>) with their parameter on it and their
        /// distance from it; returns the segment's length (0: too short to host one).
        /// </summary>
        private double Collect(int a, int b, List<(int point, double t, double dist)> found, ColumnGrid grid, float[] points)
        {
            found.Clear();
            double ax = Px(a), ay = Py(a), az = Pz(a), bx = Px(b), by = Py(b), bz = Pz(b);
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            double len2 = dx * dx + dy * dy + dz * dz;
            if (len2 < 1) return 0;
            double len = System.Math.Sqrt(len2);
            const double margin = T_JUNCTION_EPS;
            int x0 = grid.Column(System.Math.Min(ax, bx) - margin, grid.x0, grid.w), x1 = grid.Column(System.Math.Max(ax, bx) + margin, grid.x0, grid.w);
            int z0 = grid.Column(System.Math.Min(az, bz) - margin, grid.z0, grid.h), z1 = grid.Column(System.Math.Max(az, bz) + margin, grid.z0, grid.h);
            double yMin = System.Math.Min(ay, by) - margin, yMax = System.Math.Max(ay, by) + margin;
            double tMin = T_JUNCTION_EPS * 4 / len;
            for (int gz = z0; gz <= z1; gz++)
                for (int gx = x0; gx <= x1; gx++)
                    for (int i = grid.head[gz * grid.w + gx]; i >= 0; i = grid.next[i])
                    {
                        double py = points[i * 3 + 1];
                        if (py < yMin || py > yMax) continue;
                        double qx = points[i * 3] - ax, qy = py - ay, qz = points[i * 3 + 2] - az;
                        double t = (qx * dx + qy * dy + qz * dz) / len2;
                        if (t <= tMin || t >= 1 - tMin) continue;
                        double ex = qx - dx * t, ey = qy - dy * t, ez = qz - dz * t;
                        double e2 = ex * ex + ey * ey + ez * ez;
                        if (e2 > T_JUNCTION_EPS * T_JUNCTION_EPS) continue;
                        found.Add((i, t, System.Math.Sqrt(e2)));
                    }
            return len;
        }

        /// <summary>
        /// Canonical points of an edge lying in one of the tile's seam planes. The neighbour tile's faces along the seam
        /// are compiled elsewhere, so their vertices cannot be found here; instead both tiles split every seam edge at the
        /// same world positions, and the two warped borders follow the same curve within a hair.
        /// </summary>
        private void AddSeamSplits(double ax, double ay, double az, double bx, double by, double bz, double len, List<Split> splits)
        {
            const double onPlane = 0.01;
            bool horizontal = System.Math.Abs(by - ay) <= 0.5;
            // Mountain form: the lean depends on height, so a sloped seam-plane edge is split along its height too. A
            // vertical one is not: the lean is linear in height and taken after the dome's lift, so every point of the
            // edge, lifted or not, lands on one straight line — its chord.
            bool leaning = seamMountain && (field.LeanActive(ax, az) || field.LeanActive(bx, bz));
            // Relief can slope across a seam even where no mountain leans. Those edges need the same canonical
            // splits as level floors; skipping them leaves a long bent chord beside the neighbour's short edges.
            bool inX = false, inZ = false;
            foreach (double sx in seamX) inX |= System.Math.Abs(ax - sx) < onPlane && System.Math.Abs(bx - sx) < onPlane;
            foreach (double sz in seamZ) inZ |= System.Math.Abs(az - sz) < onPlane && System.Math.Abs(bz - sz) < onPlane;
            if (!inX && !inZ) return;
            double tMin = ON_EDGE_PX / len;
            // Along the seam (z in an x-plane, x in a z-plane), then along the height.
            double sa = inX ? az : ax, sb = inX ? bz : bx;
            bool vertical = System.Math.Abs(sb - sa) < onPlane;
            // The lean and the crest variation bend the field harder along a seam: its chords are kept shorter there.
            bool fine = leaning || (seamMountain && (field.DomeActive(ax, az) || field.DomeActive(bx, bz)));
            double step = fine ? SEAM_STEP_PX * 0.5 : SEAM_STEP_PX;
            for (int axis = 0; axis < (horizontal || vertical ? 1 : 2); axis++)
            {
                double u0 = axis == 0 ? sa : ay, u1 = axis == 0 ? sb : by;
                if (System.Math.Abs(u1 - u0) < step * 0.5) continue;
                double lo = System.Math.Min(u0, u1), hi = System.Math.Max(u0, u1);
                for (double k = System.Math.Ceiling(lo / step); k * step < hi; k++)
                {
                    double t = (k * step - u0) / (u1 - u0);
                    if (t <= tMin || t >= 1 - tMin) continue;
                    // Keep the old 8 px anchors everywhere. Finer canonical points are needed only where the
                    // actual deformation leaves that chord; a flat/linear seam gains no redundant triangles.
                    // Evaluate the enclosing world interval, never the triangle's length, so neighbours agree.
                    const double coarseStep = 8;
                    double coordinate = k * step;
                    double coarseFrom = System.Math.Floor(coordinate / coarseStep) * coarseStep;
                    if (coordinate - coarseFrom > ON_EDGE_PX)
                    {
                        double ta = (coarseFrom - u0) / (u1 - u0), tb = (coarseFrom + coarseStep - u0) / (u1 - u0);
                        field.Sample(ax + (bx-ax)*ta, ay + (by-ay)*ta, az + (bz-az)*ta, out double wax, out double way, out double waz, Span<double>.Empty, seamMountain);
                        field.Sample(ax + (bx-ax)*tb, ay + (by-ay)*tb, az + (bz-az)*tb, out double wbx, out double wby, out double wbz, Span<double>.Empty, seamMountain);
                        field.Sample(ax + (bx-ax)*t, ay + (by-ay)*t, az + (bz-az)*t, out double wx, out double wy, out double wz, Span<double>.Empty, seamMountain);
                        double fraction = (coordinate - coarseFrom) / coarseStep;
                        double ex = wx - (wax + (wbx-wax)*fraction), ey = wy - (way + (wby-way)*fraction), ez = wz - (waz + (wbz-waz)*fraction);
                        if (ex*ex + ey*ey + ez*ez <= .0001) continue; // <= 0.01 compile px from the coarse chord.
                    }
                    // Positions from the seam plane and the canonical coordinate: the neighbour computes the same floats.
                    double x = inX ? ax : ax + (bx - ax) * t, y = ay + (by - ay) * t, z = inX ? az + (bz - az) * t : az;
                    if (axis == 0) { if (inX) z = k * step; else x = k * step; }
                    else y = k * step;
                    splits.Add(new Split { t = t, x = (float)x, y = (float)y, z = (float)z });
                }
            }
        }

        /// <summary>
        /// Triangulates (a, b, c) with extra points on its edges (a→b, b→c, c→a, each sorted by t): splits at the
        /// median point of the fullest edge towards the opposite corner and recurses. Keeps the winding.
        /// </summary>
        private void SplitTriangle(int a, int b, int c, List<Split> ab, List<Split> bc, List<Split> ca)
        {
            // Rotate so the fullest edge is a→b.
            if (bc.Count > ab.Count && bc.Count >= ca.Count) { SplitTriangle(b, c, a, bc, ca, ab); return; }
            if (ca.Count > ab.Count && ca.Count > bc.Count) { SplitTriangle(c, a, b, ca, ab, bc); return; }
            if (ab.Count == 0) { Emit(a, b, c); return; }
            int mid = ab.Count / 2;
            Split s = ab[mid];
            int m = AddVertex(a, b, s.t, s.x, s.y, s.z);
            var left = new List<Split>(mid);
            var right = new List<Split>(ab.Count - mid - 1);
            for (int i = 0; i < mid; i++) left.Add(new Split { t = ab[i].t / s.t, x = ab[i].x, y = ab[i].y, z = ab[i].z });
            for (int i = mid + 1; i < ab.Count; i++)
                right.Add(new Split { t = (ab[i].t - s.t) / (1 - s.t), x = ab[i].x, y = ab[i].y, z = ab[i].z });
            var none = new List<Split>(0);
            // (a, m, c): edges a→m, m→c (new, empty), c→a.   (m, b, c): edges m→b, b→c, c→m (new, empty).
            SplitTriangle(a, m, c, left, none, ca);
            SplitTriangle(m, b, c, right, bc, new List<Split>(0));
        }

        // ── refinement ──

        /// <summary>
        /// Longest-edge bisection of every eligible non-rigid triangle until no edge needs a split. An edge needs one when
        /// it is longer than <paramref name="maxLength"/> and the field moves one of its ends or its middle. The rule reads
        /// the edge alone: shared edges split alike.
        /// </summary>
        public void Refine(double maxLength, OrganicStats stats, Func<int, bool>? eligible = null,
            Func<double, double, bool>? activeAt = null)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            this.activeAt = activeAt ?? field.Active;
            try { RefineCore(maxLength, stats, eligible); }
            finally { stats.refineMs += Elapsed(started); }
        }

        private Func<double, double, bool> activeAt = null!;

        private void RefineCore(double maxLength, OrganicStats stats, Func<int, bool>? eligible)
        {
            if (indexCount == 0) return;
            var original = index;
            int originalCount = indexCount;
            index = Rent<uint>(originalCount + originalCount / 2 + 48);
            indexOwned = true;
            indexCount = 0;
            var mids = new List<(int, int, int)>();
            for (int t = 0; t < originalCount; t += 3)
            {
                int a = (int)original[t], b = (int)original[t + 1], c = (int)original[t + 2];
                bool rigid = rigidVertex != null &&
                    (a < rigidVertex.Length && rigidVertex[a] || b < rigidVertex.Length && rigidVertex[b] || c < rigidVertex.Length && rigidVertex[c]);
                if (rigid || (eligible != null && !eligible(a)) || (!NeedsSplit(a, b, maxLength) && !NeedsSplit(b, c, maxLength) &&
                        !NeedsSplit(c, a, maxLength)))
                {
                    Emit(a, b, c);
                    continue;
                }
                mids.Clear();
                int before = indexCount, vertices = vertexCount;
                Bisect(a, b, c, maxLength, mids, 0);
                stats.addedTriangles += (indexCount - before) / 3 - 1;
                stats.refineTriangles += (indexCount - before) / 3 - 1;
                stats.addedVertices += vertexCount - vertices;
            }
        }

        private bool NeedsSplit(int a, int b, double maxLength)
        {
            double dx = Px(b) - Px(a), dy = Py(b) - Py(a), dz = Pz(b) - Pz(a);
            if (dx * dx + dy * dy + dz * dz <= maxLength * maxLength) return false;
            if (activeAt(Px(a), Pz(a)) || activeAt(Px(b), Pz(b))) return true;
            float mx = (Px(a) + Px(b)) * 0.5f, mz = (Pz(a) + Pz(b)) * 0.5f;
            return activeAt(mx, mz);
        }

        private void Bisect(int a, int b, int c, double maxLength, List<(int, int, int)> mids, int depth)
        {
            // Longest edge needing a split; ties by the edge order.
            double lab = NeedsSplit(a, b, maxLength) ? Len2(a, b) : -1;
            double lbc = NeedsSplit(b, c, maxLength) ? Len2(b, c) : -1;
            double lca = NeedsSplit(c, a, maxLength) ? Len2(c, a) : -1;
            if ((lab < 0 && lbc < 0 && lca < 0) || depth > 24) { Emit(a, b, c); return; }
            if (lbc > lab && lbc >= lca) { Bisect(b, c, a, maxLength, mids, depth); return; }
            if (lca > lab && lca > lbc) { Bisect(c, a, b, maxLength, mids, depth); return; }
            int m = Midpoint(a, b, mids);
            Bisect(a, m, c, maxLength, mids, depth + 1);
            Bisect(m, b, c, maxLength, mids, depth + 1);
        }

        private double Len2(int a, int b)
        {
            double dx = Px(b) - Px(a), dy = Py(b) - Py(a), dz = Pz(b) - Pz(a);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>The midpoint vertex of a→b, shared by the sub-triangles of one host (float mean: order-independent).</summary>
        private int Midpoint(int a, int b, List<(int, int, int)> mids)
        {
            int lo = System.Math.Min(a, b), hi = System.Math.Max(a, b);
            foreach (var (p, q, m) in mids)
                if (p == lo && q == hi) return m;
            float x = (Px(a) + Px(b)) * 0.5f, y = (Py(a) + Py(b)) * 0.5f, z = (Pz(a) + Pz(b)) * 0.5f;
            int v = AddVertex(a, b, 0.5, x, y, z);
            mids.Add((lo, hi, v));
            return v;
        }

        // ── displacement ──

        /// <summary>Moves every vertex by the field; the normal channel follows the displacement's Jacobian.</summary>
        public void Warp(int normalChannel, OrganicStats stats, bool domes = true)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { WarpCore(normalChannel, stats, domes); }
            finally { stats.warpMs += Elapsed(started); }
        }

        private void WarpCore(int normalChannel, OrganicStats stats, bool domes)
        {
            EnsureOwned(0);
            if (normalChannel > 0) EnsureOwned(normalChannel);
            float[] p = data[0];
            float[]? n = normalChannel > 0 ? data[normalChannel] : null;
            Span<double> j = stackalloc double[8];
            // Flipped faces are counted on the triangles (a fold would show there first).
            float[]? before = null;
            if (Diagnostics && indexCount > 0)
            {
                before = new float[vertexCount * 3];
                Array.Copy(p, before, vertexCount * 3);
            }
            // Coincident vertices (every face of the compiler owns its corners) ask the field once: a direct-mapped
            // cache of recent positions.
            var cache = WarpCache ??= new WarpEntry[WARP_CACHE_SIZE];
            Array.Clear(cache);
            for (int v = 0; v < vertexCount; v++)
            {
                float fx = p[v * 3], fy = p[v * 3 + 1], fz = p[v * 3 + 2];
                double x = fx, y = fy, z = fz;
                int slot = (int)((uint)new PositionKey(fx, fy, fz).GetHashCode() & (WARP_CACHE_SIZE - 1));
                ref WarpEntry entry = ref cache[slot];
                double wx, wy, wz;
                if (entry.valid && entry.x == fx && entry.y == fy && entry.z == fz)
                {
                    if (!entry.moved) continue;
                    wx = entry.wx; wy = entry.wy; wz = entry.wz;
                    j[0] = entry.j0; j[1] = entry.j1; j[2] = entry.j2; j[3] = entry.j3;
                    j[4] = entry.j4; j[5] = entry.j5; j[6] = entry.j6; j[7] = entry.j7;
                }
                else
                {
                    bool moved = field.Sample(x, y, z, out wx, out wy, out wz, j, domes);
                    entry.valid = true;
                    entry.x = fx; entry.y = fy; entry.z = fz;
                    entry.moved = moved;
                    if (!moved) continue;
                    entry.wx = wx; entry.wy = wy; entry.wz = wz;
                    entry.j0 = j[0]; entry.j1 = j[1]; entry.j2 = j[2]; entry.j3 = j[3];
                    entry.j4 = j[4]; entry.j5 = j[5]; entry.j6 = j[6]; entry.j7 = j[7];
                }
                p[v * 3] = (float)(x + wx);
                p[v * 3 + 1] = (float)(y + wy);
                p[v * 3 + 2] = (float)(z + wz);
                if (n == null) continue;
                // J = I + ∇w with rows (a b c), (g 0 h), (d e f); the normal maps by cof(J) = det(J)·J⁻ᵀ.
                double a = j[0], b = j[1], c = j[2], d = j[3], e = j[4], f = j[5], g = j[6], h = j[7];
                double nx = n[v * 3], ny = n[v * 3 + 1], nz = n[v * 3 + 2];
                double ox = (1 + f - h * e) * nx - (g * (1 + f) - h * d) * ny + (g * e - d) * nz;
                double oy = -(b * (1 + f) - c * e) * nx + ((1 + a) * (1 + f) - c * d) * ny - ((1 + a) * e - b * d) * nz;
                double oz = (b * h - c) * nx - ((1 + a) * h - c * g) * ny + (1 + a - b * g) * nz;
                double len = System.Math.Sqrt(ox * ox + oy * oy + oz * oz);
                if (len < 1e-9) continue;
                n[v * 3] = (float)(ox / len);
                n[v * 3 + 1] = (float)(oy / len);
                n[v * 3 + 2] = (float)(oz / len);
            }
            if (before == null) return;
            for (int t = 0; t + 2 < indexCount; t += 3)
            {
                int a = (int)index[t], b = (int)index[t + 1], c = (int)index[t + 2];
                // Horizontal orientation (signed xz area) must keep its sign.
                double s0 = (before[b * 3] - before[a * 3]) * (before[c * 3 + 2] - before[a * 3 + 2]) -
                    (before[b * 3 + 2] - before[a * 3 + 2]) * (before[c * 3] - before[a * 3]);
                double s1 = (p[b * 3] - p[a * 3]) * (p[c * 3 + 2] - p[a * 3 + 2]) - (p[b * 3 + 2] - p[a * 3 + 2]) * (p[c * 3] - p[a * 3]);
                if (System.Math.Abs(s0) > 1 && s0 * s1 < 0)
                {
                    stats.flippedTriangles++;
                    stats.flippedArea += System.Math.Abs(s0) * 0.5;
                }
            }
        }

        private const int WARP_CACHE_SIZE = 8192;
        [ThreadStatic] private static WarpEntry[]? WarpCache;

        private struct WarpEntry
        {
            public bool valid, moved;
            public float x, y, z;
            public double wx, wy, wz, j0, j1, j2, j3, j4, j5, j6, j7;
        }

        // ── output ──

        /// <summary>
        /// The channel as an exact-size store: the payload's own store if it was never edited, else a copy from the
        /// worker's transfer pool (a rented scratch store never leaves the mesh).
        /// </summary>
        public float[] Take(int channel, TerrainTransferBufferPool? pool)
        {
            int length = vertexCount * width[channel];
            float[] source = data[channel];
            if (!owned[channel] && source.Length == length) return source;
            float[] exact = pool != null ? pool.acquireFloat32(length) : new float[length];
            Array.Copy(source, exact, length);
            return exact;
        }

        public uint[] TakeIndex(TerrainTransferBufferPool? pool)
        {
            if (!indexOwned && index.Length == indexCount) return index;
            uint[] exact = pool != null ? pool.acquireUint32(indexCount) : new uint[indexCount];
            Array.Copy(index, exact, indexCount);
            return exact;
        }
    }
}
