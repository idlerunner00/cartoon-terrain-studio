using System;
using System.Buffers;
using M = System.Math;
using V3 = System.Numerics.Vector3;

namespace Fluitown.Render;

public static partial class TerrainOrganicForm
{
    /// <summary>
    /// Short bake-time rays into the finished mesh. A pooled cell index avoids scanning an entire tile for every
    /// stone. Built before any stone is appended, so placement cannot depend on earlier groups or bake ordering.
    /// </summary>
    internal sealed class CliffSurfaceIndex : IDisposable
    {
        private readonly Mesh mesh;
        private readonly float x0, z0, scale;
        private readonly int width, height, kindChannel;
        private readonly int[] offsets, triangles;

        public CliffSurfaceIndex(Mesh mesh, TerrainBakeFrame frame, int width, int height, int kindChannel)
        {
            this.mesh = mesh; this.width = width; this.height = height; this.kindChannel = kindChannel;
            x0 = (float)(frame.originX + frame.i0 * frame.tileSize);
            z0 = (float)(frame.originY + frame.j0 * frame.tileSize);
            scale = (float)(1 / frame.tileSize);
            int cells = width * height;
            offsets = ArrayPool<int>.Shared.Rent(cells + 1);
            Array.Clear(offsets, 0, cells + 1);
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                if (Kind(t) == 0) continue;
                Bounds(t, out int xa, out int xb, out int za, out int zb);
                for (int z = za; z <= zb; z++) for (int x = xa; x <= xb; x++) offsets[z * width + x + 1]++;
            }
            for (int i = 1; i <= cells; i++) offsets[i] += offsets[i - 1];
            triangles = ArrayPool<int>.Shared.Rent(M.Max(1, offsets[cells]));
            int[] cursor = ArrayPool<int>.Shared.Rent(cells);
            Array.Copy(offsets, cursor, cells);
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                int kind = Kind(t);
                if (kind == 0) continue;
                Bounds(t, out int xa, out int xb, out int za, out int zb);
                for (int z = za; z <= zb; z++) for (int x = xa; x <= xb; x++) triangles[cursor[z * width + x]++] = (t + 1) * kind;
            }
            ArrayPool<int>.Shared.Return(cursor);
        }

        private int Kind(int triangle)
        {
            int v = mesh.TriangleVertex(triangle, 0);
            float kind = mesh.Get(kindChannel, v, 0);
            if (mesh.Get(kindChannel, v, 1) < 0) return 0;
            if (kind < .5f && mesh.Get(1, v, 1) > .35f) return -1;
            return kind > 1.5f && kind < 3.5f ? 1 : 0;
        }

        private V3 Point(int v) => new(mesh.X(v), mesh.Y(v), mesh.Z(v));
        private V3 Color(int v) => new(mesh.Get(2, v, 0), mesh.Get(2, v, 1), mesh.Get(2, v, 2));
        private int X(float x) => M.Clamp((int)MathF.Floor((x - x0) * scale), 0, width - 1);
        private int Z(float z) => M.Clamp((int)MathF.Floor((z - z0) * scale), 0, height - 1);

        private void Bounds(int t, out int xa, out int xb, out int za, out int zb)
        {
            var a = Point(mesh.TriangleVertex(t, 0)); var b = Point(mesh.TriangleVertex(t, 1)); var c = Point(mesh.TriangleVertex(t, 2));
            xa = X(M.Min(a.X, M.Min(b.X, c.X)) - .01f); xb = X(M.Max(a.X, M.Max(b.X, c.X)) + .01f);
            za = Z(M.Min(a.Z, M.Min(b.Z, c.Z)) - .01f); zb = Z(M.Max(a.Z, M.Max(b.Z, c.Z)) + .01f);
        }

        internal bool WallAt(V3 point, V3 outward, float reach, out V3 hit, out V3 normal, out V3 pigment) =>
            Ray(point + outward * reach, -outward, reach * 2, true, out hit, out normal, out pigment);

        internal bool FloorAt(V3 point, float reach, out V3 hit, out V3 pigment) =>
            Ray(point + V3.UnitY * reach, -V3.UnitY, reach * 2, false, out hit, out _, out pigment);

        private bool Ray(V3 origin, V3 direction, float reach, bool wall, out V3 hit, out V3 normal, out V3 pigment)
        {
            hit = normal = pigment = default;
            var end = origin + direction * reach;
            int xa = X(M.Min(origin.X, end.X)), xb = X(M.Max(origin.X, end.X));
            int za = Z(M.Min(origin.Z, end.Z)), zb = Z(M.Max(origin.Z, end.Z));
            float nearest = reach;
            bool found = false;
            for (int z = za; z <= zb; z++) for (int x = xa; x <= xb; x++)
            {
                int cell = z * width + x;
                for (int j = offsets[cell]; j < offsets[cell + 1]; j++)
                {
                    int encoded = triangles[j];
                    if ((encoded > 0) != wall) continue;
                    int t = M.Abs(encoded) - 1;
                    int ai = mesh.TriangleVertex(t, 0), bi = mesh.TriangleVertex(t, 1), ci = mesh.TriangleVertex(t, 2);
                    var a = Point(ai); var ab = Point(bi) - a; var ac = Point(ci) - a;
                    var cross = V3.Cross(direction, ac);
                    float det = V3.Dot(ab, cross);
                    if (MathF.Abs(det) < 1e-7f) continue;
                    var ao = origin - a;
                    float u = V3.Dot(ao, cross) / det;
                    if (u < -.0001f || u > 1.0001f) continue;
                    var q = V3.Cross(ao, ab);
                    float v = V3.Dot(direction, q) / det;
                    if (v < -.0001f || u + v > 1.0001f) continue;
                    float distance = V3.Dot(ac, q) / det;
                    if (distance < 0 || distance > nearest) continue;
                    var n = V3.Cross(ab, ac);
                    if (n.LengthSquared() < 1e-10f) continue;
                    n = V3.Normalize(n);
                    var authored = new V3(mesh.Get(1, ai, 0), mesh.Get(1, ai, 1), mesh.Get(1, ai, 2));
                    if (V3.Dot(n, authored) < 0) n = -n;
                    if (V3.Dot(n, -direction) < (wall ? .3f : .45f)) continue;
                    nearest = distance; found = true;
                    hit = origin + direction * distance; normal = n;
                    pigment = Color(ai) * (1 - u - v) + Color(bi) * u + Color(ci) * v;
                }
            }
            return found;
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(offsets);
            ArrayPool<int>.Shared.Return(triangles);
        }
    }
}
