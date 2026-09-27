using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>
/// Smooth, two-sided low-poly geometry of the comic look's herbs — wildflowers (<see cref="FlowerMesher"/>) and wall
/// plants — in world metres, drawn with <c>shaders/vegetation/terrain_herb.gdshader</c>. Every vertex carries its wind
/// weight, phase, substance and its height above the plant's foot (CUSTOM0) and the foot itself (UV2), so the shader
/// sways, parts and fades each plant as a whole.
/// </summary>
public sealed class HerbMeshBuffers
{
    /// <summary>CUSTOM0.z: what a vertex belongs to (the shader's pigment, back light and ink): leaves, petals, flower
    /// centres, wood, thin stalks and the leaves of a crown's shell. All share the world's silhouette ink;
    /// timber additionally permits interior creases.</summary>
    public const float Green = 1, Petal = 2, Centre = 3, Stalk = 5, ShellLeaf = 6;

    public readonly List<Vector3> Vertices = new(4096);
    public readonly List<Vector3> Normals = new(4096);
    public readonly List<Color> Colors = new(4096);
    public readonly List<float> Custom = new(16384);
    public readonly List<Vector2> Roots = new(4096);
    public readonly List<int> Indices = new(12288);

    /// <summary>The plant being emitted: foot (world m), wind weight at full height and height, phase.</summary>
    public Vector3 Foot;
    public float Sway, Height = 1, Phase;

    /// <param name="brushable">Whether the Flui brushes the plant aside (meadow flowers); wall plants stay put. Carried in
    /// the sign of the phase (negative: not brushed).</param>
    public void Begin(Vector3 foot, float height, float sway, float phase, bool brushable = true)
    {
        Foot = foot;
        Height = MathF.Max(.05f, height);
        Sway = sway;
        Phase = brushable ? phase : -1 - phase;
    }

    public int Vertex(Vector3 p, Vector3 n, Color colour, float substance)
    {
        if (!float.IsFinite(n.X + n.Y + n.Z) || n.LengthSquared() < 1e-12f) n = Vector3.Up;
        float lift = MathF.Max(0, p.Y - Foot.Y);
        float t = Math.Clamp(lift / Height, 0, 1.2f);
        Vertices.Add(p);
        Normals.Add(n);
        Colors.Add(colour);
        Custom.Add(Sway * t * t);
        Custom.Add(Phase);
        Custom.Add(substance);
        Custom.Add(lift);
        Roots.Add(new Vector2(Foot.X, Foot.Z));
        return Vertices.Count - 1;
    }

    /// <summary>A triangle wound so that Godot's front face (clockwise) looks along the vertices' normals.</summary>
    public void Triangle(int a, int b, int c)
    {
        Vector3 pa = Vertices[a], pb = Vertices[b], pc = Vertices[c];
        var face = (pc - pa).Cross(pb - pa);
        if (face.LengthSquared() < 1e-14f) return;
        var n = Normals[a] + Normals[b] + Normals[c];
        if (face.Dot(n) < 0) (b, c) = (c, b);
        Indices.Add(a);
        Indices.Add(b);
        Indices.Add(c);
    }

    public void Quad(int a, int b, int c, int d)
    {
        Triangle(a, b, c);
        Triangle(a, c, d);
    }

    /// <summary>The mesh, its vertices relative to <paramref name="origin"/> (where its node is placed).</summary>
    public ArrayMesh? Commit(Vector3 origin = default)
    {
        if (Indices.Count < 3) return null;
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        // Straight from the lists' storage into Godot's packed arrays (no managed copies, native copies released at
        // once); the buffers are done after this, so the origin is taken off in place.
        var vertices = CollectionsMarshal.AsSpan(Vertices);
        if (origin != Vector3.Zero)
            for (int i = 0; i < vertices.Length; i++) vertices[i] -= origin;
        MeshArrays.Put(arrays, Mesh.ArrayType.Vertex, Variant.CreateFrom(vertices));
        MeshArrays.Put(arrays, Mesh.ArrayType.Normal, Variant.CreateFrom(CollectionsMarshal.AsSpan(Normals)));
        MeshArrays.Put(arrays, Mesh.ArrayType.Color, Variant.CreateFrom(CollectionsMarshal.AsSpan(Colors)));
        MeshArrays.Put(arrays, Mesh.ArrayType.TexUV2, Variant.CreateFrom(CollectionsMarshal.AsSpan(Roots)));
        MeshArrays.Put(arrays, Mesh.ArrayType.Custom0, Variant.CreateFrom(CollectionsMarshal.AsSpan(Custom)));
        MeshArrays.Put(arrays, Mesh.ArrayType.Index, Variant.CreateFrom(CollectionsMarshal.AsSpan(Indices)));
        var flags = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
        return mesh;
    }
}

/// <summary>Shared shapes of the herbs: tubes along quadratic Bézier curves and curved blades (petals, leaves, fronds).</summary>
public static class HerbShapes
{
    public static Vector3 Bezier(Vector3 a, Vector3 bend, Vector3 b, float t) =>
        a * ((1 - t) * (1 - t)) + bend * (2 * t * (1 - t)) + b * (t * t);

    public static Vector3 BezierTangent(Vector3 a, Vector3 bend, Vector3 b, float t)
    {
        var d = (bend - a) * (2 * (1 - t)) + (b - bend) * (2 * t);
        return d.LengthSquared() > 1e-12f ? d.Normalized() : Vector3.Up;
    }

    /// <summary>Any unit vector perpendicular to <paramref name="axis"/>.</summary>
    public static Vector3 Perpendicular(Vector3 axis)
    {
        var other = MathF.Abs(axis.Y) < .9f ? Vector3.Up : Vector3.Right;
        return axis.Cross(other).Normalized();
    }

    /// <summary>A round tube along a Bézier curve from radius <paramref name="r0"/> to <paramref name="r1"/>, closed at its
    /// tip when <paramref name="cap"/>.</summary>
    public static void Tube(HerbMeshBuffers m, Vector3 a, Vector3 bend, Vector3 b, float r0, float r1, int segments, int sides,
        Color c0, Color c1, float substance, bool cap = false)
    {
        int first = m.Vertices.Count;
        var side = Perpendicular(BezierTangent(a, bend, b, 0));
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            var p = Bezier(a, bend, b, t);
            var tangent = BezierTangent(a, bend, b, t);
            // Parallel transport of the ring's frame keeps the tube from twisting.
            side = (side - tangent * side.Dot(tangent)).Normalized();
            var other = tangent.Cross(side);
            float r = r0 + (r1 - r0) * t;
            var colour = c0.Lerp(c1, t);
            for (int s = 0; s < sides; s++)
            {
                float angle = s * Mathf.Tau / sides;
                var n = side * MathF.Cos(angle) + other * MathF.Sin(angle);
                m.Vertex(p + n * r, n, colour, substance);
            }
        }
        for (int i = 0; i < segments; i++)
            for (int s = 0; s < sides; s++)
            {
                int k0 = first + i * sides + s, k1 = first + i * sides + (s + 1) % sides;
                m.Quad(k0, k1, k1 + sides, k0 + sides);
            }
        if (cap)
        {
            var tip = m.Vertex(b + BezierTangent(a, bend, b, 1) * r1 * .6f, BezierTangent(a, bend, b, 1), c1, substance);
            int ring = first + segments * sides;
            for (int s = 0; s < sides; s++) m.Triangle(ring + s, ring + (s + 1) % sides, tip);
        }
    }

    /// <summary>
    /// A curved blade — petal, leaf, frond segment: from <paramref name="root"/> along <paramref name="along"/>, its face
    /// towards <paramref name="up"/>. Rows run from root to tip; the profile, width, cup, fold and colour are sampled per
    /// row and across (s ∈ −1…1). Normals come from the finished grid, so the blade is smoothly cupped.
    /// </summary>
    public static void Blade(HerbMeshBuffers m, Vector3 root, Vector3 along, Vector3 up, float length, BladeShape shape,
        Color baseColour, Color tipColour, float substance, int rows = 4, int columns = 3)
    {
        along = along.Normalized();
        var across = up.Cross(along).Normalized();
        up = along.Cross(across).Normalized();
        Span<Vector3> grid = stackalloc Vector3[(rows + 1) * columns];
        for (int i = 0; i <= rows; i++)
        {
            float t = (float)i / rows;
            float width = shape.Width(t) * length;
            float rise = (shape.Lift * t + shape.Curl * t * t) * length;
            float forward = t * length * (1 - MathF.Abs(shape.Curl) * .15f * t);
            for (int j = 0; j < columns; j++)
            {
                float s = columns == 1 ? 0 : -1 + 2f * j / (columns - 1);
                float cup = shape.Cup * s * s * width + shape.Fold * MathF.Abs(s) * width;
                float wave = shape.Ruffle * MathF.Sin(s * 5.2f + shape.Seed) * t * t * length;
                grid[i * columns + j] = root + along * forward + across * (s * width) + up * (rise + cup + wave);
            }
        }
        int first = m.Vertices.Count;
        for (int i = 0; i <= rows; i++)
        {
            float t = (float)i / rows;
            var colour = baseColour.Lerp(tipColour, MathF.Min(1, t * 1.25f));
            for (int j = 0; j < columns; j++)
            {
                var p = grid[i * columns + j];
                var dt = grid[Math.Min(rows, i + 1) * columns + j] - grid[Math.Max(0, i - 1) * columns + j];
                var ds = columns == 1 ? across : grid[i * columns + Math.Min(columns - 1, j + 1)] - grid[i * columns + Math.Max(0, j - 1)];
                var n = ds.Cross(dt);
                if (n.LengthSquared() < 1e-14f) n = up;
                n = n.Normalized();
                if (n.Dot(up) < 0) n = -n;
                m.Vertex(p, n, colour, substance);
            }
        }
        for (int i = 0; i < rows; i++)
            for (int j = 0; j + 1 < columns; j++)
            {
                int k = first + i * columns + j;
                m.Quad(k, k + 1, k + columns + 1, k + columns);
            }
    }

    /// <summary>A dome (flower centre, bud cap) of <paramref name="radius"/> and <paramref name="height"/> on its axis.</summary>
    public static void Dome(HerbMeshBuffers m, Vector3 centre, Vector3 axis, Vector3 side, float radius, float height, int sides,
        Color rim, Color top, float substance, float skirt = 0)
    {
        axis = axis.Normalized();
        side = (side - axis * side.Dot(axis)).Normalized();
        var other = axis.Cross(side);
        int first = m.Vertices.Count;
        float[] rings = { 1f, .78f, .42f };
        int ringCount = rings.Length;
        for (int ring = 0; ring < ringCount; ring++)
        {
            float rr = rings[ring];
            float y = height * MathF.Sqrt(MathF.Max(0, 1 - rr * rr));
            var colour = rim.Lerp(top, 1 - rr);
            for (int s = 0; s < sides; s++)
            {
                float angle = s * Mathf.Tau / sides;
                var radial = side * MathF.Cos(angle) + other * MathF.Sin(angle);
                var n = (radial * (height / MathF.Max(1e-4f, radius)) * rr + axis * MathF.Max(.2f, 1 - rr * .6f)).Normalized();
                m.Vertex(centre + radial * (radius * rr) + axis * y, n, colour, substance);
            }
        }
        int apex = m.Vertex(centre + axis * height, axis, top, substance);
        for (int ring = 0; ring + 1 < ringCount; ring++)
            for (int s = 0; s < sides; s++)
            {
                int k0 = first + ring * sides + s, k1 = first + ring * sides + (s + 1) % sides;
                m.Quad(k0, k1, k1 + sides, k0 + sides);
            }
        int last = first + (ringCount - 1) * sides;
        for (int s = 0; s < sides; s++) m.Triangle(last + s, last + (s + 1) % sides, apex);
        if (skirt > 0)
        {
            // A short band down from the rim closes the dome against the petals below it.
            int band = m.Vertices.Count;
            for (int s = 0; s < sides; s++)
            {
                float angle = s * Mathf.Tau / sides;
                var radial = side * MathF.Cos(angle) + other * MathF.Sin(angle);
                m.Vertex(centre + radial * (radius * .92f) - axis * skirt, radial, rim, substance);
            }
            for (int s = 0; s < sides; s++) m.Quad(first + s, first + (s + 1) % sides, band + (s + 1) % sides, band + s);
        }
    }
}

/// <summary>The shape of a <see cref="HerbShapes.Blade"/>, in units of its length.</summary>
public struct BladeShape
{
    /// <summary>Rise of the blade's midline per unit length (linear) and its curl (quadratic; negative droops).</summary>
    public float Lift, Curl;
    /// <summary>Across: a cup lifts both edges (s²), a fold lifts them linearly (a creased leaf).</summary>
    public float Cup, Fold;
    /// <summary>Edge waves towards the tip (poppy petals).</summary>
    public float Ruffle, Seed;
    /// <summary>Half width at the widest point, where along the blade it is (0..1), and how blunt the tip is (0..1).</summary>
    public float MaxWidth, Widest, Blunt, BaseWidth;

    public readonly float Width(float t)
    {
        // Rises from the base to the widest point, then narrows to a pointed (blunt 0) or rounded (blunt 1) tip.
        float w = t < Widest
            ? BaseWidth + (1 - BaseWidth) * MathF.Sin(t / MathF.Max(1e-3f, Widest) * MathF.PI * .5f)
            : MathF.Pow(MathF.Max(0, 1 - (t - Widest) / MathF.Max(1e-3f, 1 - Widest)), .55f + (1 - Blunt) * .6f) * (1 - Blunt * .35f) + Blunt * .35f * (t < 1 ? 1 : .6f);
        return MaxWidth * MathF.Max(0, w);
    }
}

/// <summary>
/// A shell of single leaves over a faceted godot-flui crown (<see cref="PlantMesher"/>): the crown stays the volume that
/// casts the shadow and reads from afar; up close, leaves on its outer surface — each on the ellipsoid it belongs to,
/// facing out of it, in that ellipsoid's crown tint — turn the crumpled paper-like mass into foliage.
/// </summary>
public static class LeafShell
{
    /// <summary>Leaf samples per plant before burial (about half survive on a bush's outer surface).</summary>
    public const float MaxLeaves = 900;

    private static float Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return (x & 0xffffff) / 16777216f;
    }

    /// <summary>
    /// The inner mass under a leaf shell: the plan's crown ellipsoids shrunk (the leaves stand on the full ones), in the
    /// crown tints stepped towards the shade — a dark, deep interior under lit leaves.
    /// </summary>
    public static PlantPlan Core(PlantPlan plan, float shrink)
    {
        var core = new PlantPlan { Height = plan.Height, TrunkRadius = plan.TrunkRadius, TrunkSolidHeight = plan.TrunkSolidHeight };
        core.Wood.AddRange(plan.Wood);
        foreach (var part in plan.Foliage) core.Foliage.Add(new(part.Centre, part.Radius * shrink, part.Tint));
        return core;
    }

    public static PlantPalette Shaded(PlantPalette palette, Color shade, float amount)
    {
        var crown = new Color[palette.Crown.Length];
        for (int i = 0; i < crown.Length; i++) crown[i] = palette.Crown[i].Lerp(shade, amount);
        return palette with { Crown = crown };
    }

    /// <summary>Emits the leaves of <paramref name="plan"/>'s crown (local to its foot) at <paramref name="foot"/> (world metres).</summary>
    /// <param name="transform">Applied around the foot after placement (a wall shrub's lean), or identity.</param>
    public static void Emit(HerbMeshBuffers m, PlantPlan plan, Vector3 foot, PlantPalette palette, float phase, float sway, uint seed,
        Basis transform, float leafSize = 0)
    {
        var parts = plan.Foliage;
        if (parts.Count == 0) return;
        float extent = MathF.Max(.2f, plan.Height);
        float size = leafSize > 0 ? leafSize : Math.Clamp(extent * .15f, .07f, .15f);
        m.Begin(foot, extent, sway, phase, brushable: false);
        float leafArea = size * size * .55f;
        // Bounding spheres of the lobes: the burial test only asks lobes that can reach a sample.
        Span<float> reach = parts.Count <= 512 ? stackalloc float[parts.Count] : new float[parts.Count];
        for (int j = 0; j < parts.Count; j++)
        {
            var r = parts[j].Radius;
            float max = MathF.Max(r.X, MathF.Max(r.Y, r.Z));
            reach[j] = max * max;
        }
        uint salt = seed * 747796405u + 2891336453u;
        // A budget of leaves per plant, shared out by lobe surface (the small outer lobes carry most of the silhouette).
        float total = 0;
        foreach (var part in parts)
        {
            float mean = (part.Radius.X + part.Radius.Y + part.Radius.Z) / 3;
            total += 4 * MathF.PI * mean * mean;
        }
        float share = MathF.Min(.9f / leafArea, MaxLeaves / MathF.Max(1e-4f, total));
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            float mean = (part.Radius.X + part.Radius.Y + part.Radius.Z) / 3;
            int count = Math.Clamp((int)(4 * MathF.PI * mean * mean * share + .5f), 1, 60);
            var tone = palette.Crown[part.Tint % palette.Crown.Length];
            for (int k = 0; k < count; k++)
            {
                // Fibonacci directions on the ellipsoid, jittered.
                uint h = salt + (uint)(p * 977 + k * 131);
                float v = 1 - 2 * (k + .5f) / count;
                float a = k * 2.399963f + Hash(h) * .8f;
                float radial = MathF.Sqrt(MathF.Max(0, 1 - v * v));
                var d = new Vector3(MathF.Cos(a) * radial, v, MathF.Sin(a) * radial);
                var q = part.Centre + part.Radius * d * (.96f + Hash(h ^ 0x51u) * .08f);
                // Only the union's outer surface: not buried in another lobe.
                bool buried = false;
                for (int j = 0; j < parts.Count && !buried; j++)
                {
                    if (j == p) continue;
                    var o = parts[j];
                    var offset = q - o.Centre;
                    if (offset.LengthSquared() >= reach[j]) continue;
                    var r = offset / o.Radius.Max(Vector3.One * 1e-3f);
                    if (r.LengthSquared() < .8f) buried = true;
                }
                if (buried) continue;
                var normal = ((q - part.Centre) / (part.Radius * part.Radius).Max(Vector3.One * 1e-6f)).Normalized();
                // A leaf leaves the crown obliquely and droops a little; a random twist around its normal.
                var twist = HerbShapes.Perpendicular(normal).Rotated(normal, Hash(h ^ 0x9e37u) * 6.283f);
                var along = (twist * .8f + normal * .45f - Vector3.Up * .15f).Normalized();
                // Plans are local to the foot (PlantMesher adds it).
                var root = foot + q - normal * (size * .1f);
                var colour = tone.Lerp(Hash(h ^ 0x77u) < .5f ? palette.Crown[1 % palette.Crown.Length] : palette.Crown[3 % palette.Crown.Length], Hash(h ^ 0x1234u) * .5f);
                // A flowering cushion (heath) shows its blossom among the leaves.
                if (palette.BlossomShare > 0 && Hash(h ^ 0xb105u) < palette.BlossomShare) colour = palette.Blossom;
                float length = size * (.8f + Hash(h ^ 0x4321u) * .45f);
                var shape = new BladeShape
                {
                    Lift = .05f,
                    Curl = -.22f,
                    Fold = .12f,
                    Cup = .05f,
                    MaxWidth = .3f,
                    Widest = .45f,
                    Blunt = .45f,
                    BaseWidth = .15f,
                };
                int first = m.Vertices.Count;
                HerbShapes.Blade(m, root, along, normal, length, shape, colour.Darkened(.06f), colour.Lightened(.07f), HerbMeshBuffers.ShellLeaf, 2, 3);
                if (transform != Basis.Identity)
                    for (int i = first; i < m.Vertices.Count; i++)
                    {
                        m.Vertices[i] = foot + transform * (m.Vertices[i] - foot);
                        m.Normals[i] = transform * m.Normals[i];
                    }
            }
        }
    }
}

/// <summary>
/// Herb buffers per square chunk of the ground (world metres), so the Flui perspective submits only the chunks within
/// their draw range (a tile-wide mesh submits every leaf of the tile to every pass, near or not).
/// </summary>
public sealed class HerbChunks
{
    public readonly float Size;
    private readonly Dictionary<(int, int), (HerbMeshBuffers Buffers, double FootY, int Plants)> _chunks = new();

    public HerbChunks(float size) => Size = size;

    /// <summary>The buffers of the chunk containing <paramref name="foot"/>.</summary>
    public HerbMeshBuffers At(Vector3 foot)
    {
        var key = ((int)MathF.Floor(foot.X / Size), (int)MathF.Floor(foot.Z / Size));
        if (!_chunks.TryGetValue(key, out var chunk)) chunk = (new HerbMeshBuffers(), 0, 0);
        _chunks[key] = (chunk.Buffers, chunk.FootY + foot.Y, chunk.Plants + 1);
        return chunk.Buffers;
    }

    /// <summary>One mesh per non-empty chunk, its vertices relative to the chunk's centre at its plants' mean foot height.</summary>
    public List<HerbChunk> Commit()
    {
        var list = new List<HerbChunk>(_chunks.Count);
        foreach (var ((cx, cz), chunk) in _chunks)
        {
            var origin = new Vector3((cx + .5f) * Size, (float)(chunk.FootY / Math.Max(1, chunk.Plants)), (cz + .5f) * Size);
            if (chunk.Buffers.Commit(origin) is { } mesh) list.Add(new HerbChunk(origin, mesh));
        }
        return list;
    }
}

public readonly record struct HerbChunk(Vector3 Origin, ArrayMesh Mesh);
