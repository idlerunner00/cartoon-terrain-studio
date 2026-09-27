// Cliff-foot dressing is baked against the finished terrain, after contouring and displacement.
using System;
using Fluitown.Domain;
using M = System.Math;
using V3 = System.Numerics.Vector3;

namespace Fluitown.Render;

public static partial class TerrainOrganicForm
{
    private const int BOULDER_MAX_ROUTE = 40;
    internal const double BOULDER_MIN_DROP_LEVELS = 2;

    /// <summary>Diagnostics only; bake workers report finished stones to the offline geometry checks.</summary>
    [field: ThreadStatic] internal static Action<CliffRockPlacement>? RockProbe { get; set; }
    internal readonly record struct CliffRockPlacement(V3 Center, V3 Wall, V3 Outward, float Height,
        float Radius, int Family, int Triangles, bool Attached, float BuriedDepth);

    /// <summary>
    /// Broken blocks, slabs and small fragments. One floor cell owns each group, using world-coordinate seeds.
    /// The actual wall supplies the contact plane and pigment; the actual floor supports the whole exposed base.
    /// All work stays on the bake worker and in the existing opaque terrain/collision batch.
    /// </summary>
    private static void AddCliffRocks(Mesh mesh, Field field, TerrainBakeFrame frame, MaterializedTerrain terrain,
        int kindChannel, OrganicStats stats)
    {
        using var ground = new CliffSurfaceIndex(mesh, frame, terrain.width, terrain.height, kindChannel);
        double ts = frame.tileSize;
        // Reused scratch: no lists/arrays per stone or per triangle.
        Span<V3> points = stackalloc V3[21];
        Span<float> values = stackalloc float[13];
        Span<V3> placed = stackalloc V3[5];
        Span<float> radii = stackalloc float[5];
        for (int y = TerrainTileLattice.TILE_BORDER; y < TerrainTileLattice.TILE_BORDER + TerrainTileLattice.TILE_CELLS && y < terrain.height; y++)
            for (int x = TerrainTileLattice.TILE_BORDER; x < TerrainTileLattice.TILE_BORDER + TerrainTileLattice.TILE_CELLS && x < terrain.width; x++)
            {
                TerrainCell? cell = terrain.cells[y * terrain.width + x];
                if (cell == null || cell.type != TileType.Floor || !cell.walkable || !BoulderFloor(terrain, x, y)) continue;
                int wx = frame.i0 + x, wz = frame.j0 + y;
                // Keep the two sides of a concave corner from dressing the same pocket twice.
                int occupied = 0;
                for (int side = 0; side < 4 && occupied < 5; side++)
                {
                    int nx = x + (side == 1 ? 1 : side == 3 ? -1 : 0), nz = y + (side == 2 ? 1 : side == 0 ? -1 : 0);
                    TerrainCell? high = TerrainModel.terrainCellAt(terrain, nx, nz);
                    if (high == null || (high.type != TileType.Solid && high.type != TileType.Floor)) continue;
                    double drop = high.surfaceZ - cell.surfaceZ;
                    if (drop < BOULDER_MIN_DROP_LEVELS) continue;
                    // Irregular pockets and breathing room; no continuous skirt or repeated per-tile mound.
                    double pocket = Random01(wx / 3, wz / 3, side, 82);
                    if (Random01(wx, wz, side, 90) > 0.30 + pocket * 0.27) continue;
                    float floorY = (float)(cell.surfaceZ * TerrainProjection.TERRAIN_ELEVATION_STEP_PX);
                    float wallHeight = (float)(drop * TerrainProjection.TERRAIN_ELEVATION_STEP_PX);
                    var outward = side switch { 0 => V3.UnitZ, 1 => -V3.UnitX, 2 => -V3.UnitZ, _ => V3.UnitX };
                    var along = new V3(outward.Z, 0, -outward.X);
                    var edge = new V3((float)(frame.originX + (wx + .5) * ts), floorY,
                        (float)(frame.originY + (wz + .5) * ts)) - outward * (float)(ts * .5);
                    float primary = (float)((8 + 6 * Random01(wx, wz, side, 91)) * M.Min(1, .65 + drop * .05));
                    float anchor = (float)((Random01(wx, wz, side, 92) - .5) * ts * .24);
                    int pieces = 2 + (Random01(wx, wz, side, 93) > .35 ? 1 : 0) + (pocket > .65 ? 1 : 0);
                    for (int k = 0; k < pieces && occupied < 5; k++)
                    {
                        int seed = unchecked(wx * 73856093 ^ wz * 19349663 ^ (side * 7 + k) * 83492791);
                        float size = k == 0 ? primary : primary * (float)(k == 1 ? .55 + .18 * Random01(seed, 0, 0, 1) : .22 + .20 * Random01(seed, 0, 0, 2));
                        float offset = k == 0 ? anchor : anchor + (k % 2 == 1 ? 1 : -1) * primary * (float)(1.08 + .22 * Random01(seed, 0, 0, 3));
                        offset = M.Clamp(offset, (float)(-ts * .42 + size), (float)(ts * .42 - size));
                        int family = (int)(Random01(seed, 0, 0, 4) * 4);
                        float height = size * (family == 0 ? .60f : family == 1 ? 1.18f : family == 2 ? .88f : .76f);
                        height *= (float)(.85 + .3 * Random01(seed, 0, 0, 5));
                        height = M.Min(height, wallHeight * .32f);
                        var query = edge + along * offset + V3.UnitY * (height * .48f);
                        field.Sample(query.X, query.Y, query.Z, out double sx, out _, out double sz, Span<double>.Empty);
                        query += new V3((float)sx, 0, (float)sz);
                        if (!ground.WallAt(query, outward, (float)(ts * .55), out var wall, out var normal, out var pigment)) continue;
                        // Follow the real facet, including clipped corners. Lean is kept in the sampled contact.
                        var outFlat = V3.Normalize(new V3(normal.X, 0, normal.Z));
                        float depth = size * (family == 0 ? .58f : family == 2 ? .74f : .85f);
                        bool attached = k < 2;
                        var center = wall + outFlat * depth * (attached ? .40f : 1.65f);
                        center.Y = floorY;
                        if (!ground.FloorAt(center, (float)(ts * .28), out var support, out var soil)) continue;
                        center.Y = support.Y;
                        bool crowded = false;
                        for (int j = 0; j < occupied; j++)
                        {
                            var d = center - placed[j]; d.Y = 0;
                            if (d.Length() < (size + radii[j]) * .66f) { crowded = true; break; }
                        }
                        if (crowded) continue;
                        float yaw = (float)((Random01(seed, 0, 0, 6) - .5) * (family == 0 ? .65 : 1.15));
                        int corners = 5 + (int)(Random01(seed, 0, 0, 7) * 3);
                        int before = stats.boulderTriangles;
                        if (!BuildCliffStone(points, corners, center, outFlat, size, depth, height, yaw, seed, family,
                            ground, wall, attached, soil, pigment, mesh, values, stats, out float buried)) continue;
                        placed[occupied] = center; radii[occupied++] = size;
                        RockProbe?.Invoke(new CliffRockPlacement(center, wall, outFlat, height, size, family,
                            stats.boulderTriangles - before, attached, buried));
                    }
                }
            }
    }

    private static bool BuildCliffStone(Span<V3> p, int n, V3 center, V3 outward, float width, float depth, float height,
        float yaw, int seed, int family, CliffSurfaceIndex ground, V3 wall, bool attached, V3 soil, V3 pigment,
        Mesh mesh, Span<float> values, OrganicStats stats, out float buried)
    {
        var tangent = new V3(outward.Z, 0, -outward.X);
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        var axisX = tangent * c + outward * s;
        var axisZ = outward * c - tangent * s;
        float slopeX = (float)(Random01(seed, 1, 0, 0) - .5) * (family == 2 ? .70f : .42f);
        float slopeZ = (float)(Random01(seed, 1, 0, 1) - .5) * .38f;
        float crown = (family == 1 ? .57f : family == 2 ? .36f : .67f)
            + (float)(Random01(seed, 1, 0, 8) - .5) * .15f;
        float bury = M.Max(.8f, height * .16f);
        float minimumGround = float.MaxValue, maximumGround = float.MinValue;
        buried = bury;
        for (int i = 0; i < n; i++)
        {
            float a = (float)((i + .13 + (Random01(seed, i, 1, 2) - .5) * .28) * M.Tau / n);
            float r = (float)(.85 + .15 * Random01(seed, i, 1, 3));
            float u = MathF.Cos(a) * r, v = MathF.Sin(a) * r;
            var basePoint = center + axisX * (u * width) + axisZ * (v * depth);
            // Each exposed rim vertex has its own floor sample. Bury the rim below that surface; reject cliff lips.
            if (ground.FloorAt(basePoint, M.Max(6, height * .55f), out var foot, out _))
            {
                basePoint.Y = foot.Y - bury;
                minimumGround = M.Min(minimumGround, foot.Y); maximumGround = M.Max(maximumGround, foot.Y);
            }
            else if (attached && V3.Dot(basePoint - wall, outward) < 1.0f) basePoint.Y = center.Y - bury;
            else return false;
            p[i] = basePoint;
            // Three irregular rings form a buried base, a broken shoulder and a sloping crown, never one apex.
            float shoulder = .43f + (float)Random01(seed, i, 1, 4) * .16f;
            p[n + i] = center + axisX * (u * width * .94f) + axisZ * (v * depth * .94f)
                + V3.UnitY * (height * shoulder);
            p[2 * n + i] = center + axisX * ((u * crown + slopeX) * width)
                + axisZ * ((v * crown - .12f) * depth)
                + V3.UnitY * (height * (1 + u * slopeX + v * slopeZ));
        }
        if (maximumGround - minimumGround > M.Max(3, height * .30f)) return false;
        // Sample between rim corners too: a footprint may span a rounded cliff lip or a narrow ground opening.
        for (int i = 0; i < n; i++)
        {
            var midpoint = (p[i] + p[(i + 1) % n]) * .5f;
            if (V3.Dot(midpoint - wall, outward) <= 1 && attached) continue;
            if (!ground.FloorAt(midpoint, M.Max(6, height * .55f), out var foot, out _)
                || midpoint.Y > foot.Y - .15f) return false;
        }
        // Contact is tested at the actual shoulder, at its own height and along-wall position.
        // A clipped/rounded corner with no host under the back of the stone must remain empty.
        if (attached)
        {
            int rear = 0;
            for (int i = 1; i < n; i++)
                if (V3.Dot(p[n + i] - wall, outward) < V3.Dot(p[n + rear] - wall, outward)) rear = i;
            if (!ground.WallAt(p[n + rear], outward, width * 1.5f, out var contact, out _, out _)
                || V3.Dot(p[n + rear] - contact, outward) > -.2f) return false;
        }
        float tint = (float)(.99 + .15 * Random01(seed, 2, 0, 0));
        pigment *= tint;
        for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                int a = ring * n + i, b = ring * n + next, cc = (ring + 1) * n + next, d = (ring + 1) * n + i;
                var mid = (p[a] + p[b] + p[cc] + p[d]) * .25f;
                // Back faces wholly inside the host rock never contribute to the silhouette or collision.
                // Confirm all corners and the middle against the real contour before omitting the pair.
                if (attached && V3.Dot(mid - wall, outward) < 0
                    && BehindCliff(p[a], ground, outward, width) && BehindCliff(p[b], ground, outward, width)
                    && BehindCliff(p[cc], ground, outward, width) && BehindCliff(p[d], ground, outward, width)
                    && BehindCliff(mid, ground, outward, width)) continue;
                // A broad fracture gets one normal across both triangles, avoiding arbitrary triangle mosaics.
                var normal = V3.Normalize(V3.Cross(p[b] - p[a], p[d] - p[a]));
                if (V3.Dot(normal, mid - (center + V3.UnitY * height * .45f)) < 0) normal = -normal;
                float faceTint = (float)(.96 + .08 * Random01(seed, i, ring, 33));
                EmitCliffTriangle(mesh, p[a], p[b], p[cc], normal, center.Y, height, pigment * faceTint, soil, values, stats);
                EmitCliffTriangle(mesh, p[a], p[cc], p[d], normal, center.Y, height, pigment * faceTint, soil, values, stats);
            }
        var capNormal = V3.Normalize(V3.Cross(p[2 * n + 1] - p[2 * n], p[2 * n + 2] - p[2 * n]));
        if (capNormal.Y < 0) capNormal = -capNormal;
        for (int i = 1; i < n - 1; i++)
            EmitCliffTriangle(mesh, p[2 * n], p[2 * n + i], p[2 * n + i + 1], capNormal,
                center.Y, height, pigment * 1.035f, soil, values, stats);
        return true;
    }

    private static bool BehindCliff(V3 point, CliffSurfaceIndex ground, V3 outward, float reach) =>
        ground.WallAt(point, outward, reach * 1.5f, out var hit, out _, out _)
        && V3.Dot(point - hit, outward) < -.5f;

    private static void EmitCliffTriangle(Mesh mesh, V3 a, V3 b, V3 c, V3 normal, float floor, float height,
        V3 pigment, V3 soil, Span<float> values, OrganicStats stats)
    {
        if (V3.Dot(V3.Cross(b - a, c - a), normal) < 0) (b, c) = (c, b);
        int first = mesh.VertexCount;
        for (int i = 0; i < 3; i++)
        {
            var v = i == 0 ? a : i == 1 ? b : c;
            float rise = M.Clamp((v.Y - floor) / height, 0, 1);
            var color = V3.Lerp(pigment, soil, (1 - rise) * .18f) * (.83f + .17f * M.Min(1, rise * 2.5f));
            values.Clear();
            values[0] = normal.X; values[1] = normal.Y; values[2] = normal.Z;
            values[3] = color.X; values[4] = color.Y; values[5] = color.Z;
            values[6] = 1; values[7] = .15f; // Existing rock-cap pigment and comic lighting; no new material/pass.
            values[9] = .3f; values[10] = -1; // Loose fragment: no mountain-crown grass, wall reflectance.
            mesh.AppendVertex(v.X, v.Y, v.Z, values);
        }
        mesh.AppendTriangle(first, first + 1, first + 2);
        stats.addedVertices += 3; stats.addedTriangles++; stats.boulderTriangles++;
    }
    /// <summary>Floors that may carry rock: no route, no pinned neighbour.</summary>
    private static bool BoulderFloor(MaterializedTerrain terrain, int x, int y)
    {
        int w = terrain.width, h = terrain.height;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) return false;
                TerrainCell? cell = terrain.cells[ny * w + nx];
                if (cell == null) return false;
                if (cell.type == TileType.Bridge || cell.type == TileType.Underpass || cell.type == TileType.Cleft) return false;
            }
        TerrainCell self = terrain.cells[y * w + x];
        if (terrain.floorUsage != null && (uint)self.id < (uint)terrain.floorUsage.Length && terrain.floorUsage[self.id] > BOULDER_MAX_ROUTE) return false;
        return true;
    }

    /// <summary>A uniform number in [0, 1) from integer coordinates (world-deterministic).</summary>
    private static double Random01(int x, int y, int z, int salt)
    {
        unchecked
        {
            uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ (uint)z * 0xcb1ab31fu ^ (uint)salt * 0x9e3779b9u;
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h / 4294967296.0;
        }
    }

}
