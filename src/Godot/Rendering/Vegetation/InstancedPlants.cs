using System;
using System.Collections.Generic;
using Fluitown.Render;
using Godot;
using static Fluitown.Render.FluitownVegetation;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>
/// The comic look's small plants of one bake tile (<see cref="FluitownSmallPlants"/>): lily pads with their flowers,
/// reed clumps with or without cattails, water grass and hanging vines or roots, as three MultiMeshes of low-poly meshes,
/// animated by <c>shaders/vegetation/terrain_small_plant.gdshader</c> (drift, bob and wading for leaves; wind and
/// brushing for stems; a swing along the wall for vines). Variants share one mesh and one draw: the part a plant does not
/// have (a pad's flower, a clump's cattails, a vine's roots or leaves) collapses in the vertex stage when the instance's
/// third pigment is negative (<c>-hex - 1</c>); reeds and water grass share one mesh too, the instance picks its part
/// with the 256 bit of its variation code. Built off the main thread in <see cref="TerrainVegetation.Prepare"/>,
/// installed with the tile's vegetation.
/// </summary>
public static class InstancedPlants
{
    /// <summary>Floats per instance: 3×4 transform + custom data (no instance colour), like the meadow.</summary>
    public const int InstanceStride = 16;

    public sealed class Prepared
    {
        public Vector3 Origin;
        public bool HasOrigin;
        public readonly List<float> Pads = new(), Reeds = new(), Vines = new();
        public Aabb Bounds;
        public int Count => (Pads.Count + Reeds.Count + Vines.Count) / InstanceStride;
        /// <summary>The MultiMesh buffers, copied on the worker (<see cref="Finish"/>) so the main thread only installs.</summary>
        public readonly Dictionary<List<float>, float[]> Buffers = new();

        public void Finish()
        {
            foreach (var list in new[] { Pads, Reeds, Vines })
                if (list.Count > 0) Buffers[list] = list.ToArray();
        }
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    private static float Unit(uint x) => (Hash(x) & 0xffffff) / 16777216f;

    /// <summary>The third pigment of an instance that shows (<paramref name="show"/>) or collapses its optional part.</summary>
    private static float Optional(float hex, bool show) => show ? hex : -hex - 1;

    private static void Add(Prepared prepared, List<float> list, Basis basis, Vector3 foot, float a, float b, float c, float variation, float phase, bool second = false)
    {
        if (!prepared.HasOrigin)
        {
            prepared.Origin = new Vector3(MathF.Round(foot.X), 0, MathF.Round(foot.Z));
            prepared.HasOrigin = true;
            prepared.Bounds = new Aabb(foot, Vector3.Zero);
        }
        prepared.Bounds = prepared.Bounds.Expand(foot);
        var p = foot - prepared.Origin;
        list.Add(basis.X.X); list.Add(basis.Y.X); list.Add(basis.Z.X); list.Add(p.X);
        list.Add(basis.X.Y); list.Add(basis.Y.Y); list.Add(basis.Z.Y); list.Add(p.Y);
        list.Add(basis.X.Z); list.Add(basis.Y.Z); list.Add(basis.Z.Z); list.Add(p.Z);
        list.Add(a); list.Add(b); list.Add(c);
        list.Add(MathF.Floor(Math.Clamp(variation, 0, 1) * 255) + (second ? 256 : 0) + (phase - MathF.Floor(phase)) * .999f);
    }

    /// <summary>Collects the water plant of the lane record at <paramref name="i"/> (thread-safe). Returns false for any
    /// other kind.</summary>
    public static bool Collect(ref Prepared? prepared, float[] records, int i, Vector3 foot, float scale)
    {
        int kind = (int)MathF.Round(records[i + Kind]);
        if (kind != FluitownSmallPlants.KindLily && kind != FluitownSmallPlants.KindReed && kind != FluitownSmallPlants.KindSedge &&
            kind != FluitownSmallPlants.KindVine) return false;
        prepared ??= new Prepared();
        float seed = records[i + Seed];
        float yaw = records[i + Rotation];
        uint salt = (uint)(seed * 16777215f) * 2654435761u ^ (uint)(int)MathF.Round(records[i + X]) * 40503u;
        switch (kind)
        {
            case FluitownSmallPlants.KindLily:
            {
                float radius = records[i + Radius] * scale;
                float phase = records[i + Form] + seed * 7.13f;
                var basis = new Basis(Vector3.Up, yaw).Scaled(Vector3.One * radius);
                // The pad mesh carries its blossom; a pad without one collapses it.
                Add(prepared, prepared.Pads, basis, foot, records[i + ColourLeaf], records[i + ColourShade],
                    Optional(records[i + ColourAccent], records[i + Bloom] > 0), seed, phase);
                break;
            }
            case FluitownSmallPlants.KindReed:
            {
                // Rooted a quarter metre under the (opaque) water: the stems rise out of it.
                const float sunk = .25f;
                float height = records[i + Height] * scale + sunk;
                float spread = Math.Clamp(records[i + Radius] * scale / (.2f * height), .75f, 1.35f);
                var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(height * spread, height, height * spread));
                // A clump carries the cattail heads with the record's seed-head share.
                bool cattails = Unit(salt ^ 0x9e3779b9u) < records[i + Bloom];
                Add(prepared, prepared.Reeds, basis, foot - Vector3.Up * sunk,
                    records[i + ColourLeaf], records[i + ColourShade], Optional(records[i + ColourAccent], cattails), seed, seed * 5.37f + records[i + Lean]);
                break;
            }
            case FluitownSmallPlants.KindVine:
            {
                // Hangs from its record point down the wall; local +Z faces out of the wall.
                float length = records[i + Height] * scale;
                var basis = new Basis(Vector3.Up, yaw).Scaled(Vector3.One * length);
                // One mesh with both strands: the leafy vine shows for a positive third pigment, the roots for a negative.
                bool roots = records[i + Form] > .5f;
                Add(prepared, prepared.Vines, basis, foot,
                    records[i + ColourLeaf], records[i + ColourShade], Optional(records[i + ColourAccent], !roots), seed, seed * 4.77f + records[i + Lean]);
                break;
            }
            default:
            {
                const float sunk = .06f;
                float height = records[i + Height] * scale + sunk;
                var basis = new Basis(Vector3.Up, yaw).Scaled(Vector3.One * height);
                // Water grass shares the reeds' mesh (and draw): the second part of it.
                Add(prepared, prepared.Reeds, basis, foot - Vector3.Up * sunk,
                    records[i + ColourLeaf], records[i + ColourShade], records[i + Form], seed, seed * 3.91f, second: true);
                break;
            }
        }
        return true;
    }

    // ── Meshes ─────────────────────────────────────────────────────────────────────────────────────────

    private static ArrayMesh? _pad, _flower, _cattails, _grass;

    /// <summary>A lily pad of unit radius: a disc with its notch (a V cut to the heart at +X), flat with a slightly
    /// raised heart and an up-curled rim a few centimetres wide (the world ink draws its contour and the rim crease).
    /// UV = (angle/τ, radius); COLOR.r shades the heart.</summary>
    public static ArrayMesh PadMesh()
    {
        if (_pad != null) return _pad;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        float[] rings = { 0f, .34f, .7f, .9f, 1f };
        float[] lift = { .03f, .01f, 0f, .012f, .055f };
        const int segments = 18;
        const float notch = .2f;
        var grid = new Vector3[rings.Length, segments + 1];
        for (int s = 0; s <= segments; s++)
        {
            float a = notch + (Mathf.Tau - 2 * notch) * s / segments;
            for (int r = 0; r < rings.Length; r++)
            {
                // The notch closes towards the heart: the two lobes meet in a V.
                float ring = rings[r];
                float wobble = 1 + MathF.Sin(a * 5 + 1.3f) * .025f * ring;
                grid[r, s] = new Vector3(MathF.Cos(a) * ring * wobble, lift[r], MathF.Sin(a) * ring * wobble);
            }
        }
        void V(int r, int s)
        {
            float a = notch + (Mathf.Tau - 2 * notch) * s / segments;
            tool.SetUV(new Vector2(a / Mathf.Tau, rings[r]));
            tool.SetColor(new Color((1 - rings[r]) * .3f, 0, 0, 0));
            tool.AddVertex(grid[r, s]);
        }
        for (int r = 0; r + 1 < rings.Length; r++)
            for (int s = 0; s < segments; s++)
            {
                V(r, s); V(r + 1, s); V(r + 1, s + 1);
                if (r > 0) { V(r, s); V(r + 1, s + 1); V(r, s + 1); }
            }
        tool.GenerateNormals();
        return _pad = tool.Commit();
    }

    /// <summary>A water lily blossom of unit petal length: three rings of pointed, cupped petals (8, 7 and 5, ever more
    /// upright) around a golden heart; flat shaded. UV.y runs along a petal; COLOR.r = 1 on the heart.</summary>
    public static ArrayMesh FlowerMesh()
    {
        if (_flower != null) return _flower;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        void Tri(Vector3 a, float ua, Vector3 b, float ub, Vector3 c, float uc, float heart)
        {
            var n = (b - a).Cross(c - a).Normalized();
            if (n.Y < 0) { (b, c) = (c, b); (ub, uc) = (uc, ub); n = -n; }
            foreach (var (p, u) in new[] { (a, ua), (b, ub), (c, uc) })
            {
                tool.SetNormal(n);
                tool.SetUV(new Vector2(0, u));
                tool.SetColor(new Color(heart, 0, 0, .25f));
                tool.AddVertex(p);
            }
        }
        void Ring(int count, float length, float tilt, float width, float turn)
        {
            for (int k = 0; k < count; k++)
            {
                float a = turn + k * Mathf.Tau / count;
                var outward = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                var side = new Vector3(-outward.Z, 0, outward.X);
                var along = (outward * MathF.Cos(tilt) + Vector3.Up * MathF.Sin(tilt)).Normalized();
                var up = along.Cross(side).Normalized();
                if (up.Y < 0) up = -up;
                var basePoint = outward * .1f + Vector3.Up * .04f;
                var tip = basePoint + along * length;
                var mid = basePoint + along * (length * .45f);
                var left = mid + side * width * .5f;
                var right = mid - side * width * .5f;
                // The petal is cupped: its midrib lies deeper than its flanks.
                var rib = mid - up * width * .22f;
                Tri(basePoint, 0, left, .45f, rib, .45f, 0);
                Tri(basePoint, 0, rib, .45f, right, .45f, 0);
                Tri(left, .45f, tip, 1, rib, .45f, 0);
                Tri(rib, .45f, tip, 1, right, .45f, 0);
            }
        }
        Ring(8, 1f, .42f, .36f, 0);
        Ring(7, .78f, .82f, .3f, .45f);
        Ring(5, .5f, 1.18f, .24f, .2f);
        // The golden heart: a low hexagonal crown.
        for (int k = 0; k < 6; k++)
        {
            float a0 = k * Mathf.Tau / 6, a1 = (k + 1) * Mathf.Tau / 6;
            var p0 = new Vector3(MathF.Cos(a0) * .13f, .05f, MathF.Sin(a0) * .13f);
            var p1 = new Vector3(MathF.Cos(a1) * .13f, .05f, MathF.Sin(a1) * .13f);
            var top = new Vector3(0, .2f, 0);
            Tri(p0, 0, p1, 0, top, 0, 1);
        }
        return _flower = tool.Commit();
    }

    /// <summary>A reed clump of unit height: tapering, arching leaf blades and (with <paramref name="cattails"/>) three
    /// stalks carrying cattail heads with their spikes. UV.y is the height along a stem; COLOR.r: 0 blade, 0.5 stalk,
    /// 1 head; COLOR.g: the stem's id.</summary>
    private static ArrayMesh ReedMesh(bool cattails, uint seed)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        int stem = 0;
        void Blade(Vector3 root, float height, float width, float lean, float leanAngle, float twist, float kind)
        {
            float id = Unit(seed + (uint)stem * 977u);
            stem++;
            var leanDir = new Vector3(MathF.Cos(leanAngle), 0, MathF.Sin(leanAngle));
            const int segs = 5;
            Vector3 Axis(float t) => root + Vector3.Up * (t * height) + leanDir * (t * t * lean * height);
            var face = new Vector3(MathF.Cos(leanAngle + twist), 0, MathF.Sin(leanAngle + twist));
            var side = new Vector3(-face.Z, 0, face.X);
            for (int s = 0; s < segs; s++)
            {
                float t0 = s / (float)segs, t1 = (s + 1) / (float)segs;
                float w0 = width * (1 - t0 * .85f), w1 = s == segs - 1 ? 0 : width * (1 - t1 * .85f);
                Vector3 a = Axis(t0) - side * w0, b = Axis(t0) + side * w0, c = Axis(t1) + side * w1, d = Axis(t1) - side * w1;
                var n = face;
                void V(Vector3 p, float t) { tool.SetNormal(n); tool.SetUV(new Vector2(0, t)); tool.SetColor(new Color(kind, id, 0, .5f)); tool.AddVertex(p); }
                V(a, t0); V(b, t0); V(c, t1);
                if (w1 > 0) { V(a, t0); V(c, t1); V(d, t1); }
            }
        }
        void Head(Vector3 root, float height, float leanAngle, float lean)
        {
            float id = Unit(seed + (uint)stem * 977u);
            stem++;
            var leanDir = new Vector3(MathF.Cos(leanAngle), 0, MathF.Sin(leanAngle));
            Vector3 Axis(float t) => root + Vector3.Up * (t * height) + leanDir * (t * t * lean * height);
            // The stalk: two crossed thin cards.
            for (int k = 0; k < 2; k++)
            {
                var side = k == 0 ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);
                var n = k == 0 ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
                const int segs = 4;
                for (int s = 0; s < segs; s++)
                {
                    float t0 = s / (float)segs * .74f, t1 = (s + 1) / (float)segs * .74f;
                    Vector3 a = Axis(t0) - side * .006f, b = Axis(t0) + side * .006f, c = Axis(t1) + side * .005f, d = Axis(t1) - side * .005f;
                    void V(Vector3 p, float t) { tool.SetNormal(n); tool.SetUV(new Vector2(0, t)); tool.SetColor(new Color(.5f, id, 0, .5f)); tool.AddVertex(p); }
                    V(a, t0); V(b, t0); V(c, t1); V(a, t0); V(c, t1); V(d, t1);
                }
            }
            // The cattail head: a hexagonal cigar from 74 % to 90 % of the stalk, then a thin spike.
            const int sides = 6;
            float r = .024f;
            Vector3 h0 = Axis(.74f), h1 = Axis(.9f), tip = Axis(1f);
            for (int k = 0; k < sides; k++)
            {
                float a0 = k * Mathf.Tau / sides, a1 = (k + 1) * Mathf.Tau / sides;
                var d0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0));
                var d1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1));
                var n = ((d0 + d1) * .5f).Normalized();
                void V(Vector3 p, float t, float kind, Vector3 normal) { tool.SetNormal(normal); tool.SetUV(new Vector2(0, t)); tool.SetColor(new Color(kind, id, 0, .5f)); tool.AddVertex(p); }
                Vector3 a = h0 + d0 * r * .8f, b = h0 + d1 * r * .8f, c = h1 + d1 * r, d = h1 + d0 * r;
                V(a, .74f, 1, n); V(b, .74f, 1, n); V(c, .9f, 1, n);
                V(a, .74f, 1, n); V(c, .9f, 1, n); V(d, .9f, 1, n);
                // Rounded top of the head and the spike.
                var capN = (n + Vector3.Up).Normalized();
                V(d, .9f, 1, capN); V(c, .9f, 1, capN); V(h1 + Vector3.Up * .012f, .92f, 1, capN);
                if (k < 2)
                {
                    var s = k == 0 ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);
                    V(h1 - s * .003f, .9f, .5f, n); V(h1 + s * .003f, .9f, .5f, n); V(tip, 1f, .5f, n);
                }
            }
        }
        int blades = cattails ? 9 : 12;
        for (int k = 0; k < blades; k++)
        {
            float a = k * 2.39996f + Unit(seed + (uint)k * 31u) * .6f;
            float rr = .03f + Unit(seed + (uint)k * 37u) * .1f;
            var root = new Vector3(MathF.Cos(a) * rr, 0, MathF.Sin(a) * rr);
            float height = .55f + Unit(seed + (uint)k * 41u) * .45f;
            Blade(root, height, .02f + Unit(seed + (uint)k * 43u) * .01f, .08f + Unit(seed + (uint)k * 47u) * .2f, a, (Unit(seed + (uint)k * 53u) - .5f) * 1.2f, 0);
        }
        if (cattails)
            for (int k = 0; k < 3; k++)
            {
                float a = 1.1f + k * 2.1f + Unit(seed + (uint)k * 59u) * .5f;
                var root = new Vector3(MathF.Cos(a) * .05f, 0, MathF.Sin(a) * .05f);
                Head(root, .88f + Unit(seed + (uint)k * 61u) * .14f, a, .04f + Unit(seed + (uint)k * 67u) * .05f);
            }
        return tool.Commit();
    }
    public static ArrayMesh CattailMesh() => _cattails ??= ReedMesh(true, 91);

    private static ArrayMesh? _padWithFlower, _hanging, _shore;

    /// <summary>Reed clump with cattails and a water grass tuft in one mesh (the instance shows one of them).</summary>
    public static ArrayMesh ShorePlantsMesh()
    {
        if (_shore != null) return _shore;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.AppendFrom(CattailMesh(), 0, Transform3D.Identity);
        tool.AppendFrom(GrassMesh(), 0, Transform3D.Identity);
        return _shore = tool.Commit();
    }

    /// <summary>The pad with its blossom at the heart (the blossom at 0.55 of the pad's radius).</summary>
    public static ArrayMesh PadWithFlowerMesh()
    {
        if (_padWithFlower != null) return _padWithFlower;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.AppendFrom(PadMesh(), 0, Transform3D.Identity);
        tool.AppendFrom(FlowerMesh(), 0, new Transform3D(Basis.Identity.Scaled(Vector3.One * .55f), new Vector3(0, .035f, 0)));
        return _padWithFlower = tool.Commit();
    }

    /// <summary>Vine and roots in one mesh (COLOR.b = 1 on the roots).</summary>
    public static ArrayMesh HangingPlantsMesh()
    {
        if (_hanging != null) return _hanging;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.AppendFrom(VineMesh(), 0, Transform3D.Identity);
        tool.AppendFrom(RootMesh(), 0, Transform3D.Identity);
        return _hanging = tool.Commit();
    }

    /// <summary>A tuft of water grass of unit height: eleven thin blades arching out over the water, most of them
    /// towards +X (the record turns +X away from the bank), their tips hanging down towards the surface.</summary>
    public static ArrayMesh GrassMesh()
    {
        if (_grass != null) return _grass;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        const int blades = 11;
        for (int k = 0; k < blades; k++)
        {
            uint seed = 4001u + (uint)k * 131u;
            float id = Unit(seed);
            // Mostly out over the water (+X), some back and to the sides.
            float a = (Unit(seed + 1) - .5f) * (k % 4 == 0 ? 5.5f : 2.2f);
            var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var side = new Vector3(-dir.Z, 0, dir.X);
            float height = .6f + Unit(seed + 2) * .4f;
            float arch = .35f + Unit(seed + 3) * .45f;
            var root = dir * (Unit(seed + 4) * .05f);
            Vector3 P(float t) => root + Vector3.Up * (height * (t * 1.25f - t * t * .55f)) + dir * (arch * height * t * t);
            const int segs = 5;
            for (int s = 0; s < segs; s++)
            {
                float t0 = s / (float)segs, t1 = (s + 1) / (float)segs;
                float w0 = .022f * (1 - t0), w1 = .022f * (1 - t1);
                Vector3 a0 = P(t0) - side * w0, b0 = P(t0) + side * w0, a1 = P(t1) - side * w1, b1 = P(t1) + side * w1;
                var n = (P(t1) - P(t0)).Cross(side).Normalized();
                if (n.Y < 0) n = -n;
                void V(Vector3 p, float t) { tool.SetNormal((n * .5f + Vector3.Up * .5f).Normalized()); tool.SetUV(new Vector2(0, t)); tool.SetColor(new Color(0, id, 0, .75f)); tool.AddVertex(p); }
                V(a0, t0); V(b0, t0); V(b1, t1);
                if (s < segs - 1) { V(a0, t0); V(b1, t1); V(a1, t1); }
            }
        }
        return _grass = tool.Commit();
    }

    private static ArrayMesh? _vine, _root;

    /// <summary>Ivy hanging from the origin down to y = -1 in front of a wall at z = 0 (leaves up to z ≈ 0.05): a wavy
    /// stem with two shorter strands, alternate heart-shaped leaves shrinking towards the young tip, a few tiny blossoms.
    /// UV.y is the depth below the crest (0..1); COLOR.r: 0 stem, 0.5 blossom, 1 leaf; COLOR.g: random per leaf.</summary>
    public static ArrayMesh VineMesh() => _vine ??= HangingMesh(false);

    /// <summary>Aerial roots: two rope-like strands with rootlets, no leaves (same conventions as <see cref="VineMesh"/>).</summary>
    public static ArrayMesh RootMesh() => _root ??= HangingMesh(true);

    private static ArrayMesh HangingMesh(bool roots)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        void V(Vector3 p, Vector3 n, float depth, float leaf, float id)
        {
            tool.SetNormal(n);
            tool.SetUV(new Vector2(0, depth));
            tool.SetColor(new Color(leaf, id, roots ? 1 : 0, .9f));
            tool.AddVertex(p);
        }
        void Strand(float x0, float y0, float length, float phase, float width, uint seed)
        {
            Vector3 P(float t) => new(
                x0 + MathF.Sin(t * 5.3f + phase) * .035f + MathF.Sin(t * 11.7f + phase * 2) * .012f,
                y0 - t * length,
                .012f + MathF.Sin(t * 7.1f + phase) * .006f);
            const int segs = 12;
            var back = new Vector3(0, 0, 1);
            for (int s = 0; s < segs; s++)
            {
                float t0 = s / (float)segs, t1 = (s + 1) / (float)segs;
                Vector3 a = P(t0), b = P(t1);
                float w0 = width * (1 - t0 * .6f), w1 = width * (1 - t1 * .6f);
                var side = new Vector3(1, 0, 0);
                V(a - side * w0, back, -a.Y, 0, 0); V(a + side * w0, back, -a.Y, 0, 0); V(b + side * w1, back, -b.Y, 0, 0);
                V(a - side * w0, back, -a.Y, 0, 0); V(b + side * w1, back, -b.Y, 0, 0); V(b - side * w1, back, -b.Y, 0, 0);
            }
            if (roots)
            {
                // Rootlets: short thin whiskers off the strand.
                for (int k = 0; k < 5; k++)
                {
                    float t = .15f + k * .17f + Unit(seed + (uint)k) * .05f;
                    var root = P(t);
                    float dir = k % 2 == 0 ? 1 : -1;
                    var tip = root + new Vector3(dir * .05f, -.05f, .01f);
                    var side = new Vector3(0, .004f, 0);
                    V(root - side, back, -root.Y, 0, 0); V(root + side, back, -root.Y, 0, 0); V(tip, back, -tip.Y, 0, 0);
                }
                return;
            }
            int count = (int)(length / .042f);
            for (int k = 0; k < count; k++)
            {
                float t = (k + .5f) / count;
                var at = P(t);
                float id = Unit(seed + (uint)k * 13u);
                float size = (.046f + id * .016f) * (1 - t * .4f);
                float dir = k % 2 == 0 ? 1 : -1;
                // A heart-shaped leaf: its stalk points out and down, its blade lies almost on the wall, tilted up.
                var outward = new Vector3(dir * .7f, -.55f, .45f).Normalized();
                var centre = at + outward * size * 1.1f;
                var across = new Vector3(-outward.Y, outward.X, 0).Normalized() * dir;
                var n = new Vector3(dir * .15f, .35f, 1).Normalized();
                var tip = centre + outward * size;
                var baseL = at + outward * size * .35f + across * size * .75f;
                var baseR = at + outward * size * .35f - across * size * .75f;
                var notch = at + outward * size * .5f + back * .004f;
                float leaf = id > .93f ? .5f : 1;
                V(notch, n, -at.Y, leaf, id); V(baseL, n, -at.Y, leaf, id); V(tip, n, -tip.Y, leaf, id);
                V(notch, n, -at.Y, leaf, id); V(tip, n, -tip.Y, leaf, id); V(baseR, n, -at.Y, leaf, id);
            }
        }
        if (roots)
        {
            Strand(0, 0, 1, .4f, .012f, 71);
            Strand(.07f, -.05f, .72f, 2.1f, .009f, 83);
        }
        else
        {
            Strand(0, 0, 1, .9f, .006f, 11);
            Strand(.09f, -.12f, .62f, 3.3f, .005f, 29);
            Strand(-.07f, -.04f, .4f, 5.1f, .004f, 47);
        }
        return tool.Commit();
    }

    // ── Nodes ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Instantiates the prepared water plants under <paramref name="group"/> (main thread).</summary>
    public static int Create(Node3D group, Prepared? prepared, ShaderMaterial material)
    {
        if (prepared == null || prepared.Count == 0) return 0;
        var bounds = new Aabb(prepared.Bounds.Position - prepared.Origin, prepared.Bounds.Size).Grow(1.6f);
        int nodes = 0;
        void Instances(string name, List<float> list, ArrayMesh mesh, bool shadow)
        {
            int count = list.Count / InstanceStride;
            if (count == 0) return;
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = mesh,
                InstanceCount = count,
            };
            multi.CustomAabb = bounds;
            multi.Buffer = prepared.Buffers.TryGetValue(list, out var buffer) ? buffer : list.ToArray();
            group.AddChild(new MultiMeshInstance3D
            {
                Name = name,
                Position = prepared.Origin,
                Multimesh = multi,
                MaterialOverride = material,
                CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            });
            nodes++;
        }
        // No sun shadows: a stem is a few shadow-map texels wide, and its shadow aliased into dashes on the ground.
        Instances("LilyPads", prepared.Pads, PadWithFlowerMesh(), false);
        Instances("Reeds", prepared.Reeds, ShorePlantsMesh(), false);
        Instances("Vines", prepared.Vines, HangingPlantsMesh(), false);
        return nodes;
    }
}
