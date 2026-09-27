// Fluitown extension — NOT a port of the original. Cliff details of the comic look's mountain form: the height of every
// wall point above its foot for the rock shader (moss at the foot, weathered crest), and plants on the rock: single
// trees on caps and bushes along the rims. Only reached through TerrainOrganicForm.apply.
using System;
using System.Collections.Generic;
using Fluitown.Domain;

namespace Fluitown.Render;

public static partial class TerrainOrganicForm
{
    /// <summary>Share of inner rock-cap cells with a tree of their own. Plants are the form's dearest part (every one is
    /// drawn in the depth, colour and shadow passes): at 6 % trees and 14 % bushes they cost 0.25 ms GPU in the Flui
    /// perspective, more than all the rest of the world look.</summary>
    internal const double CAP_TREE_CHANCE = 0.025;
    /// <summary>Share of rim edges (a drop of two levels or more) with a bush on the cap.</summary>
    internal const double RIM_BUSH_CHANCE = 0.07;

    internal sealed partial class Mesh
    {
        public int VertexCount => vertexCount;

        /// <summary>Whether this lane's seam splits follow the mountain form (not the water: it neither domes nor leans).</summary>
        private bool seamMountain;

        /// <summary>Writes component k of vertex v in channel c (the channel gets its own store first).</summary>
        public void Set(int channel, int v, int k, float value)
        {
            EnsureOwned(channel);
            data[channel][v * width[channel] + k] = value;
        }

        public float X(int v) => Px(v);
        public float Y(int v) => Py(v);
        public float Z(int v) => Pz(v);
    }

    public sealed partial class Field
    {
        /// <summary>Per cell: the height a wall standing on it rises from (px): its surface, water at its level; NaN
        /// for chasms and missing cells.</summary>
        private double[]? footLift;

        private void BuildFootLift(MaterializedTerrain terrain)
        {
            int n = cellsW * cellsH;
            footLift = new double[n];
            for (int i = 0; i < n; i++)
            {
                TerrainCell? cell = i < terrain.cells.Length ? terrain.cells[i] : null;
                footLift[i] = cell == null || cell.type == TileType.Chasm ? double.NaN
                    : (cell.type == TileType.Water ? cell.waterLevel ?? cell.surfaceZ : cell.surfaceZ) * TerrainProjection.TERRAIN_ELEVATION_STEP_PX;
            }
        }

        /// <summary>The lowest surface among the cells touching (x, z): the foot of a wall standing there (NaN: none).</summary>
        public double FootAt(double x, double z)
        {
            if (footLift == null) return double.NaN;
            double gx = CellX(x), gz = CellZ(z), foot = double.NaN;
            const double touch = 0.03;
            for (int k = 0; k < 4; k++)
            {
                int cx = (int)System.Math.Floor(gx + ((k & 1) == 1 ? touch : -touch));
                int cz = (int)System.Math.Floor(gz + ((k >> 1) == 1 ? touch : -touch));
                if (cx < 0 || cz < 0 || cx >= cellsW || cz >= cellsH) continue;
                double v = footLift[cz * cellsW + cx];
                if (!double.IsNaN(v) && (double.IsNaN(foot) || v < foot)) foot = v;
            }
            return foot;
        }
    }

    /// <summary>
    /// The height of every earth and rock face point above the foot of its wall, into the ground channel's third slot
    /// (unused on faces): the rock shader grows moss and damp at the foot and weathers the crest with it. Runs on the
    /// unwarped positions (the lean is horizontal; a dome only lifts the crest).
    /// </summary>
    private static void MarkWallHeights(Mesh mesh, Field field, int kindChannel, int groundChannel)
    {
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float kind = mesh.Get(kindChannel, v, 0);
            if (kind < 1.5f || kind > 3.5f || mesh.Get(kindChannel, v, 1) < 0) continue;
            double foot = field.FootAt(mesh.X(v), mesh.Z(v));
            if (double.IsNaN(foot)) continue;
            mesh.Set(groundChannel, v, 2, (float)System.Math.Max(0, mesh.Y(v) - foot));
        }
    }

    /// <summary>
    /// Plants on the rock (vegetation records in compile space, before the displacement, which then moves them with
    /// their ground): a tree on some inner rock-cap cells and a bush on some rims of two levels or more. They take the
    /// pigments of the tile's own plants (the first tree and bush of its lane); a tile without such a plant grows none of
    /// that kind. Never on structures, and a cap tree keeps clear of the trees already there.
    /// </summary>
    private static float[]? CliffPlants(TerrainBakeFrame frame, MaterializedTerrain terrain, TerrainVegetationLane? lane)
    {
        if (lane == null || lane.records.Length == 0) return null;
        const int S = FluitownVegetation.Stride;
        float[] records = lane.records;
        int tree = -1, bush = -1;
        var trees = new List<(double x, double z)>();
        for (int r = 0; r + S <= records.Length; r += S)
        {
            int kind = (int)MathF.Round(records[r]);
            if (kind == FluitownVegetation.KindTree) { if (tree < 0) tree = r; trees.Add((records[r + 1], records[r + 3])); }
            else if (kind == FluitownVegetation.KindBush && bush < 0) bush = r;
        }
        if (tree < 0 && bush < 0) return null;
        int w = terrain.width, h = terrain.height;
        double ts = frame.tileSize;
        var extra = new List<float>();
        void Add(int template, double x, double y, double z, double height, double radius, double seed, double rotation)
        {
            int at = extra.Count;
            for (int k = 0; k < S; k++) extra.Add(records[template + k]);
            extra[at + FluitownVegetation.X] = (float)x;
            extra[at + FluitownVegetation.Y] = (float)y;
            extra[at + FluitownVegetation.Z] = (float)z;
            extra[at + FluitownVegetation.Height] = (float)height;
            extra[at + FluitownVegetation.Radius] = (float)radius;
            extra[at + FluitownVegetation.Seed] = (float)seed;
            extra[at + FluitownVegetation.Rotation] = (float)rotation;
        }
        bool Open(int x, int y)
        {
            TerrainCell? cell = terrain.cells[y * w + x];
            if (cell == null || cell.type != TileType.Solid) return false;
            return true;
        }
        for (int y = TerrainTileLattice.TILE_BORDER; y < TerrainTileLattice.TILE_BORDER + TerrainTileLattice.TILE_CELLS && y < h; y++)
            for (int x = TerrainTileLattice.TILE_BORDER; x < TerrainTileLattice.TILE_BORDER + TerrainTileLattice.TILE_CELLS && x < w; x++)
            {
                if (!Open(x, y)) continue;
                TerrainCell cell = terrain.cells[y * w + x];
                int wx = frame.i0 + x, wy = frame.j0 + y;
                double x0 = frame.originX + (frame.i0 + x) * ts, z0 = frame.originY + (frame.j0 + y) * ts;
                double capY = cell.surfaceZ * TerrainProjection.TERRAIN_ELEVATION_STEP_PX;
                bool inner = true;
                for (int side = 0; side < 4; side++)
                {
                    int nx = x + (side == 1 ? 1 : side == 3 ? -1 : 0), ny = y + (side == 2 ? 1 : side == 0 ? -1 : 0);
                    TerrainCell? next = TerrainModel.terrainCellAt(terrain, nx, ny);
                    double drop = next == null ? 0 : cell.surfaceZ - next.surfaceZ;
                    if (next == null || next.type != TileType.Solid || drop > 0.5) inner = false;
                    if (next == null || drop < 2 || next.type == TileType.Bridge) continue;
                    // A rim of two levels or more: now and then a bush, just inside the cap. (Grass tufts along the rims
                    // were tried and dropped: from above they read as black stubble on every crest; the rock shader
                    // paints the green over the edge instead.)
                    double ex = side == 1 ? x0 + ts : x0, ez = side == 2 ? z0 + ts : z0;
                    double ax = side == 0 || side == 2 ? 1 : 0, az = side == 1 || side == 3 ? 1 : 0;
                    double inX = side == 1 ? -1 : side == 3 ? 1 : 0, inZ = side == 2 ? -1 : side == 0 ? 1 : 0;
                    if (bush >= 0 && Random01(wx, wy, side, 310) < RIM_BUSH_CHANCE)
                    {
                        double along = (0.25 + 0.5 * Random01(wx, wy, side, 311)) * ts;
                        double inset = 12 + 8 * Random01(wx, wy, side, 312);
                        double tall = (0.55 + 0.45 * Random01(wx, wy, side, 313)) * FluitownVegetation.PxPerMetre;
                        Add(bush, ex + ax * along + inX * inset, capY, ez + az * along + inZ * inset, tall, tall * 0.85,
                            Random01(wx, wy, side, 314), Random01(wx, wy, side, 315) * System.Math.PI * 2);
                    }
                }
                if (!inner || tree < 0 || Random01(wx, wy, 7, 320) >= CAP_TREE_CHANCE) continue;
                double tx = x0 + ts * (0.3 + 0.4 * Random01(wx, wy, 7, 321)), tz = z0 + ts * (0.3 + 0.4 * Random01(wx, wy, 7, 322));
                bool crowded = false;
                foreach (var (ox, oz) in trees)
                    if ((ox - tx) * (ox - tx) + (oz - tz) * (oz - tz) < ts * ts * 2.25) { crowded = true; break; }
                if (crowded) continue;
                trees.Add((tx, tz));
                double heightPx = (3.2 + 2.4 * Random01(wx, wy, 7, 323)) * FluitownVegetation.PxPerMetre;
                double ratio = records[tree + FluitownVegetation.Height] > 1
                    ? records[tree + FluitownVegetation.Radius] / records[tree + FluitownVegetation.Height] : 0.3;
                Add(tree, tx, capY, tz, heightPx, heightPx * System.Math.Clamp(ratio, 0.18, 0.45),
                    Random01(wx, wy, 7, 324), Random01(wx, wy, 7, 325) * System.Math.PI * 2);
            }
        return extra.Count > 0 ? extra.ToArray() : null;
    }
}
