using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>
/// Vertex streams of the comic look's plant meshes (one merged mesh per bake tile). Flat shaded — every facet keeps its
/// own normal, as in godot-flui's low-poly world ("the toon material keeps the face normals visible").
/// <c>COLOR</c> carries the sRGB pigment, <c>CUSTOM0</c> (RGBA float) = (wind weight, wind phase, substance, 0) with
/// godot-flui's substance numbers: 3 timber, 7 foliage.
/// </summary>
public sealed class PlantMeshBuffers
{
    public readonly List<Vector3> Vertices = new(1 << 14);
    public readonly List<Vector3> Normals = new(1 << 14);
    public readonly List<Color> Colors = new(1 << 14);
    public readonly List<float> Custom = new(1 << 16);
    public int Plants;

    // A tile's plant mesh reaches several hundred thousand vertices. Growing fresh lists for every tile allocates a
    // stream of ever larger arrays, which fragments memory, so two sets are kept for the next tiles; sets beyond that,
    // or grown past a million vertices, go to the collector.
    private const int PoolSize = 2, PoolMaxVertices = 1 << 20;
    private static readonly Stack<PlantMeshBuffers> Pool = new();

    /// <summary>Empty buffers, reused from an earlier tile where possible; hand them back with <see cref="Return"/>.
    /// </summary>
    public static PlantMeshBuffers Rent()
    {
        lock (Pool) return Pool.Count > 0 ? Pool.Pop() : new PlantMeshBuffers();
    }

    /// <summary>Empties the buffers (after <see cref="Commit"/>) and keeps their capacity for the next tile.</summary>
    public void Return()
    {
        if (Vertices.Capacity > PoolMaxVertices) return;
        Vertices.Clear();
        Normals.Clear();
        Colors.Clear();
        Custom.Clear();
        Plants = 0;
        lock (Pool) if (Pool.Count < PoolSize) Pool.Push(this);
    }

    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Color colour, float windA, float windB, float windC, float phase, float substance)
    {
        var normal = (b - a).Cross(c - a);
        float length = normal.Length();
        if (length < 1e-9f) return;
        normal /= length;
        // Godot's front face is clockwise: the outward normal is (c − a) × (b − a).
        if (normal.Dot(outward) > 0) { (b, c) = (c, b); (windB, windC) = (windC, windB); }
        else normal = -normal;
        Add(a, normal, colour, windA, phase, substance);
        Add(b, normal, colour, windB, phase, substance);
        Add(c, normal, colour, windC, phase, substance);
    }

    private void Add(Vector3 p, Vector3 n, Color colour, float wind, float phase, float substance)
    {
        Vertices.Add(p);
        Normals.Add(n);
        Colors.Add(colour);
        Custom.Add(wind);
        Custom.Add(phase);
        Custom.Add(substance);
        Custom.Add(0);
    }

    public ArrayMesh? Commit()
    {
        if (Vertices.Count < 3) return null;
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        // Straight from the lists' storage into Godot's packed arrays (no managed copy of a tile's plant mesh), and the
        // native arrays are released as soon as the mesh has them instead of at the next finalization.
        MeshArrays.Put(arrays, Mesh.ArrayType.Vertex, Variant.CreateFrom(CollectionsMarshal.AsSpan(Vertices)));
        MeshArrays.Put(arrays, Mesh.ArrayType.Normal, Variant.CreateFrom(CollectionsMarshal.AsSpan(Normals)));
        MeshArrays.Put(arrays, Mesh.ArrayType.Color, Variant.CreateFrom(CollectionsMarshal.AsSpan(Colors)));
        MeshArrays.Put(arrays, Mesh.ArrayType.Custom0, Variant.CreateFrom(CollectionsMarshal.AsSpan(Custom)));
        var flags = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
        return mesh;
    }
}

internal static class MeshArrays
{
    /// <summary>
    /// Stores a packed array in a mesh's surface arrays and releases the temporary wrapper at once: the surface array
    /// holds its own reference, and an undisposed wrapper keeps the native copy alive until managed finalization.
    /// </summary>
    public static void Put(Godot.Collections.Array arrays, Mesh.ArrayType lane, Variant value)
    {
        using (value) arrays[(int)lane] = value;
    }
}

/// <summary>Pigments of one plant: godot-flui's five crown tints, bark, and an optional blossom accent.</summary>
public readonly record struct PlantPalette(Color[] Crown, Color Wood, Color Blossom, float BlossomShare);

public static class PlantMesher
{
    public const float Timber = 3, Foliage = 7;

    /// <summary>Emits one plan at <paramref name="foot"/> (world metres).</summary>
    /// <param name="lattice">Crown lattice cells across the plant (clusters are three cells): 24 for trees, finer for
    /// bushes so their lobes stay distinct at their small size.</param>
    public static void Emit(PlantMeshBuffers buffers, PlantPlan plan, Vector3 foot, PlantPalette palette, float phase, float windScale, uint seed, float lattice = 24)
    {
        float height = MathF.Max(.2f, plan.Height);
        float Wind(Vector3 local)
        {
            float t = Math.Clamp(local.Y / height, 0, 1);
            return t * t * windScale;
        }
        foreach (var bough in plan.Wood) Tube(buffers, bough, foot, palette.Wood, Wind, phase);
        Crown(buffers, plan, foot, palette, Wind, phase, seed, lattice);
        buffers.Plants++;
    }

    // ── Wood: godot-flui's fluted tubes (WorldEcology.Mesh / VegetationTubeProfile) ──────────────────────────────

    private static void Tube(PlantMeshBuffers buffers, PlantPlan.Bough bough, Vector3 foot, Color wood, Func<Vector3, float> wind, float phase)
    {
        float start = bough.Start;
        int rings = start > .12f ? 6 : start > .05f ? 4 : 3;
        int sides = start > .14f ? 7 : start > .06f ? 6 : 4;
        var path = new Vector3[rings + 1];
        var radii = new float[rings + 1];
        for (int i = 0; i <= rings; i++)
        {
            float t = i / (float)rings;
            path[i] = bough.A * ((1 - t) * (1 - t)) + bough.Bend * (2 * t * (1 - t)) + bough.B * (t * t);
            radii[i] = Mathf.Lerp(bough.Start, bough.End, t);
        }
        var ring = new Vector3[(rings + 1) * sides];
        var right = Vector3.Right;
        for (int i = 0; i <= rings; i++)
        {
            var axis = (path[Math.Min(rings, i + 1)] - path[Math.Max(0, i - 1)]).Normalized();
            right -= axis * right.Dot(axis);
            if (right.LengthSquared() < .01f) right = axis.Cross(Vector3.Forward);
            right = right.Normalized();
            var forward = right.Cross(axis);
            for (int j = 0; j < sides; j++)
            {
                float a = j * Mathf.Tau / sides;
                // godot-flui's bark flute.
                float flute = 1 + .055f * MathF.Cos(5 * a + .12f * i) + .025f * MathF.Sin(3 * a - .19f * i);
                ring[i * sides + j] = path[i] + (right * MathF.Cos(a) + forward * MathF.Sin(a)) * radii[i] * flute;
            }
        }
        for (int i = 0; i < rings; i++)
        {
            var centre = (path[i] + path[i + 1]) * .5f;
            for (int j = 0; j < sides; j++)
            {
                int k = (j + 1) % sides;
                var a = ring[i * sides + j];
                var b = ring[i * sides + k];
                var c = ring[(i + 1) * sides + k];
                var d = ring[(i + 1) * sides + j];
                var outward = (a + b + c + d) * .25f - centre;
                // Slight pigment variation per facet keeps a faceted limb from reading as one flat stroke.
                float shade = .92f + .08f * MathF.Sin(i * 1.7f + j * 2.3f + a.Y * 3f);
                var colour = new Color(wood.R * shade, wood.G * shade, wood.B * shade);
                float wa = wind(a), wb = wind(b), wc = wind(c), wd = wind(d);
                buffers.Triangle(foot + a, foot + b, foot + c, outward, colour, wa, wb, wc, phase, Timber);
                buffers.Triangle(foot + a, foot + c, foot + d, outward, colour, wa, wc, wd, phase, Timber);
            }
        }
        // A tip cap closes the thin end where it pokes out of a crown.
        if (bough.End > .02f)
        {
            var tip = path[rings];
            var outward = (path[rings] - path[rings - 1]).Normalized();
            for (int j = 1; j + 1 < sides; j++)
                buffers.Triangle(foot + ring[rings * sides], foot + ring[rings * sides + j], foot + ring[rings * sides + j + 1], outward,
                    wood, wind(tip), wind(tip), wind(tip), phase, Timber);
        }
    }

    // ── Foliage: the union of the crown ellipsoids as one faceted surface ─────────────────────────────────────────

    [ThreadStatic] private static float[]? _field;
    [ThreadStatic] private static short[]? _tint;
    [ThreadStatic] private static int[]? _cube;

    /// <summary>
    /// godot-flui paints crown ellipsoids into voxels and meshes them with Surface Nets, then snaps every foliage vertex to
    /// the centroid of its 0.75 m cluster (3 voxels): the connected, faceted crowns of its low-poly world. The same here,
    /// on a grid sized to the plant: the implicit union of the ellipsoids (max of 1 − |q|²) on a lattice of about 24
    /// cells across the crown, Surface Nets, and clusters of three cells.
    /// </summary>
    private static void Crown(PlantMeshBuffers buffers, PlantPlan plan, Vector3 foot, PlantPalette palette, Func<Vector3, float> wind, float phase, uint seed, float lattice)
    {
        var parts = plan.Foliage;
        if (parts.Count == 0) return;
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = -lo;
        foreach (var part in parts)
        {
            lo = lo.Min(part.Centre - part.Radius);
            hi = hi.Max(part.Centre + part.Radius);
        }
        var size = hi - lo;
        float extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        float cell = Math.Clamp(extent / lattice, .03f, .2f);
        int Count(float s) => (int)MathF.Ceiling(s / cell) + 3;
        int nx = Count(size.X), ny = Count(size.Y), nz = Count(size.Z);
        while ((long)nx * ny * nz > 220_000)
        {
            cell *= 1.15f;
            nx = Count(size.X); ny = Count(size.Y); nz = Count(size.Z);
        }
        var origin = lo - Vector3.One * cell * 1.5f;
        int points = nx * ny * nz;
        var field = _field is { } f && f.Length >= points ? f : (_field = new float[Math.Max(points, 1 << 15)]);
        var tint = _tint is { } t && t.Length >= points ? t : (_tint = new short[Math.Max(points, 1 << 15)]);
        Array.Fill(field, -1f, 0, points);
        int Index(int x, int y, int z) => (z * ny + y) * nx + x;

        for (short p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var inv = new Vector3(1 / MathF.Max(part.Radius.X, 1e-3f), 1 / MathF.Max(part.Radius.Y, 1e-3f), 1 / MathF.Max(part.Radius.Z, 1e-3f));
            int x0 = Math.Max(0, (int)MathF.Floor((part.Centre.X - part.Radius.X - origin.X) / cell));
            int x1 = Math.Min(nx - 1, (int)MathF.Ceiling((part.Centre.X + part.Radius.X - origin.X) / cell));
            int y0 = Math.Max(0, (int)MathF.Floor((part.Centre.Y - part.Radius.Y - origin.Y) / cell));
            int y1 = Math.Min(ny - 1, (int)MathF.Ceiling((part.Centre.Y + part.Radius.Y - origin.Y) / cell));
            int z0 = Math.Max(0, (int)MathF.Floor((part.Centre.Z - part.Radius.Z - origin.Z) / cell));
            int z1 = Math.Min(nz - 1, (int)MathF.Ceiling((part.Centre.Z + part.Radius.Z - origin.Z) / cell));
            short tintValue = (short)Math.Min(short.MaxValue, part.Tint);
            for (int z = z0; z <= z1; z++)
            {
                float qz = (origin.Z + z * cell - part.Centre.Z) * inv.Z;
                for (int y = y0; y <= y1; y++)
                {
                    float qy = (origin.Y + y * cell - part.Centre.Y) * inv.Y;
                    float yz = qy * qy + qz * qz;
                    if (yz >= 1) continue;
                    int row = (z * ny + y) * nx;
                    for (int x = x0; x <= x1; x++)
                    {
                        float qx = (origin.X + x * cell - part.Centre.X) * inv.X;
                        float value = 1 - (qx * qx + yz);
                        if (value > field[row + x])
                        {
                            field[row + x] = value;
                            tint[row + x] = tintValue;
                        }
                    }
                }
            }
        }

        // Surface Nets: one dual vertex per cube that the surface crosses (the mean of its edge crossings).
        int cubes = (nx - 1) * (ny - 1) * (nz - 1);
        var cube = _cube is { } c && c.Length >= cubes ? c : (_cube = new int[Math.Max(cubes, 1 << 15)]);
        Array.Fill(cube, -1, 0, cubes);
        int CubeIndex(int x, int y, int z) => (z * (ny - 1) + y) * (nx - 1) + x;
        var vertices = new List<Vector3>(4096);
        Span<float> corner = stackalloc float[8];
        for (int z = 0; z < nz - 1; z++)
            for (int y = 0; y < ny - 1; y++)
                for (int x = 0; x < nx - 1; x++)
                {
                    int mask = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        corner[k] = field[Index(x + (k & 1), y + ((k >> 1) & 1), z + ((k >> 2) & 1))];
                        if (corner[k] > 0) mask |= 1 << k;
                    }
                    if (mask == 0 || mask == 255) continue;
                    var sum = Vector3.Zero;
                    int crossings = 0;
                    for (int k = 0; k < 8; k++)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int other = k | (1 << axis);
                            if (other == k) continue;
                            bool inA = corner[k] > 0, inB = corner[other] > 0;
                            if (inA == inB) continue;
                            float s = corner[k] / (corner[k] - corner[other]);
                            var a = new Vector3(k & 1, (k >> 1) & 1, (k >> 2) & 1);
                            var b = new Vector3(other & 1, (other >> 1) & 1, (other >> 2) & 1);
                            sum += a.Lerp(b, s);
                            crossings++;
                        }
                    cube[CubeIndex(x, y, z)] = vertices.Count;
                    vertices.Add(origin + (new Vector3(x, y, z) + sum / crossings) * cell);
                }
        if (vertices.Count == 0) return;

        // godot-flui's foliage clustering: every vertex moves to the centroid of its cluster (three cells).
        float cluster = cell * 3;
        var groups = new Dictionary<(int, int, int), (Vector3 Sum, int Count, int Id)>(vertices.Count / 8);
        var key = new (int, int, int)[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i] - origin;
            var k = ((int)MathF.Floor(v.X / cluster), (int)MathF.Floor(v.Y / cluster), (int)MathF.Floor(v.Z / cluster));
            key[i] = k;
            groups.TryGetValue(k, out var g);
            groups[k] = (g.Sum + vertices[i], g.Count + 1, g.Count == 0 ? groups.Count : g.Id);
        }
        var snapped = new Vector3[vertices.Count];
        var clusterId = new int[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            var g = groups[key[i]];
            snapped[i] = g.Sum / g.Count;
            clusterId[i] = g.Id;
        }

        // Pigments: godot-flui's five crown tints by the ellipsoid that owns the facet, blossom lobes as an accent.
        var rng = new Random(unchecked((int)(seed * 2654435761u)));
        float blossomSalt = (float)rng.NextDouble();
        Color FacetColour(Vector3 centroid)
        {
            var g = (centroid - origin) / cell;
            int bx = Math.Clamp((int)g.X, 0, nx - 2), by = Math.Clamp((int)g.Y, 0, ny - 2), bz = Math.Clamp((int)g.Z, 0, nz - 2);
            float best = float.NegativeInfinity;
            int owner = 0;
            for (int k = 0; k < 8; k++)
            {
                int idx = Index(bx + (k & 1), by + ((k >> 1) & 1), bz + ((k >> 2) & 1));
                if (field[idx] > best) { best = field[idx]; owner = tint[idx]; }
            }
            if (palette.BlossomShare > 0)
            {
                float hash = Hash(owner * 0.618f + blossomSalt);
                if (hash < palette.BlossomShare) return palette.Blossom;
            }
            return palette.Crown[owner % palette.Crown.Length];
        }

        void Quad(int a, int b, int cIdx, int d, Vector3 outward)
        {
            if (a < 0 || b < 0 || cIdx < 0 || d < 0) return;
            Tri(a, b, cIdx, outward);
            Tri(a, cIdx, d, outward);
        }
        void Tri(int a, int b, int cIdx, Vector3 outward)
        {
            if (clusterId[a] == clusterId[b] || clusterId[b] == clusterId[cIdx] || clusterId[a] == clusterId[cIdx]) return;
            var pa = snapped[a];
            var pb = snapped[b];
            var pc = snapped[cIdx];
            var centroid = (pa + pb + pc) / 3;
            // Clustering folds some facets across their lattice edge: face them away from the crown's inside (the
            // field falls outwards), not along the edge they were built from.
            var gradient = Gradient(centroid);
            if (gradient.LengthSquared() > 1e-8f) outward = -gradient;
            var colour = FacetColour(centroid);
            buffers.Triangle(foot + pa, foot + pb, foot + pc, outward, colour, wind(pa), wind(pb), wind(pc), phase, Foliage);
        }
        float Sample(Vector3 p)
        {
            var g = (p - origin) / cell;
            int ix = Math.Clamp((int)MathF.Floor(g.X), 0, nx - 2), iy = Math.Clamp((int)MathF.Floor(g.Y), 0, ny - 2), iz = Math.Clamp((int)MathF.Floor(g.Z), 0, nz - 2);
            float fx = Math.Clamp(g.X - ix, 0, 1), fy = Math.Clamp(g.Y - iy, 0, 1), fz = Math.Clamp(g.Z - iz, 0, 1);
            float V(int dx, int dy, int dz) => field[Index(ix + dx, iy + dy, iz + dz)];
            float x00 = Mathf.Lerp(V(0, 0, 0), V(1, 0, 0), fx), x10 = Mathf.Lerp(V(0, 1, 0), V(1, 1, 0), fx);
            float x01 = Mathf.Lerp(V(0, 0, 1), V(1, 0, 1), fx), x11 = Mathf.Lerp(V(0, 1, 1), V(1, 1, 1), fx);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
        }
        Vector3 Gradient(Vector3 p)
        {
            float h = cell * 1.5f;
            return new Vector3(
                Sample(p + new Vector3(h, 0, 0)) - Sample(p - new Vector3(h, 0, 0)),
                Sample(p + new Vector3(0, h, 0)) - Sample(p - new Vector3(0, h, 0)),
                Sample(p + new Vector3(0, 0, h)) - Sample(p - new Vector3(0, 0, h)));
        }

        // One quad per lattice edge the surface crosses, between the four cubes around that edge.
        for (int z = 1; z < nz - 1; z++)
            for (int y = 1; y < ny - 1; y++)
                for (int x = 0; x < nx - 1; x++)
                {
                    bool a = field[Index(x, y, z)] > 0, b = field[Index(x + 1, y, z)] > 0;
                    if (a == b) continue;
                    Quad(cube[CubeIndex(x, y - 1, z - 1)], cube[CubeIndex(x, y, z - 1)], cube[CubeIndex(x, y, z)], cube[CubeIndex(x, y - 1, z)],
                        a ? Vector3.Right : Vector3.Left);
                }
        for (int z = 1; z < nz - 1; z++)
            for (int y = 0; y < ny - 1; y++)
                for (int x = 1; x < nx - 1; x++)
                {
                    bool a = field[Index(x, y, z)] > 0, b = field[Index(x, y + 1, z)] > 0;
                    if (a == b) continue;
                    Quad(cube[CubeIndex(x - 1, y, z - 1)], cube[CubeIndex(x, y, z - 1)], cube[CubeIndex(x, y, z)], cube[CubeIndex(x - 1, y, z)],
                        a ? Vector3.Up : Vector3.Down);
                }
        for (int z = 0; z < nz - 1; z++)
            for (int y = 1; y < ny - 1; y++)
                for (int x = 1; x < nx - 1; x++)
                {
                    bool a = field[Index(x, y, z)] > 0, b = field[Index(x, y, z + 1)] > 0;
                    if (a == b) continue;
                    Quad(cube[CubeIndex(x - 1, y - 1, z)], cube[CubeIndex(x, y - 1, z)], cube[CubeIndex(x, y, z)], cube[CubeIndex(x - 1, y, z)],
                        a ? Vector3.Back : Vector3.Forward);
                }
    }

    private static float Hash(float value)
    {
        float s = MathF.Sin(value * 127.1f + 311.7f) * 43758.547f;
        return s - MathF.Floor(s);
    }
}
