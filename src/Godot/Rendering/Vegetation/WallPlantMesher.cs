using System;
using Fluitown.Render;
using Godot;
using static Fluitown.Render.FluitownVegetation;
using static Fluitown.GodotApp.Rendering.Vegetation.HerbShapes;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>
/// The comic look's plants on cliff faces (<see cref="FluitownWallPlants"/>), grown from a root on the wall and the wall's
/// run next to it (both displaced with the bent wall):
/// <list type="bullet">
/// <item>ivy climbing from the wall's foot: branching woody stems a finger's width off the rock, alternate lobed leaves
/// on short stalks facing out of the wall, older and deeper low, young and light at the tips;</item>
/// <item>a fern fanning from its fissure: arching, drooping fronds of paired, tapering pinnae and a curled fiddlehead;</item>
/// <item>a shrub leaning out of its crevice: godot-flui's bush (<see cref="PlantGrowth.Bush"/>), tilted away from the
/// rock so its crown clears it.</item>
/// </list>
/// Ivy and fern are smooth two-sided herbs (<see cref="HerbMeshBuffers"/>); the shrub shares the tile's plant mesh.
/// </summary>
public static class WallPlantMesher
{
    private static Color Hex(float value)
    {
        int hex = (int)MathF.Round(value);
        return new Color(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f);
    }

    private struct Rng
    {
        private uint _state;
        public Rng(uint seed) => _state = seed == 0 ? 0x9e3779b9u : seed;
        public float Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (_state & 0xffffff) / 16777216f;
        }
    }

    /// <summary>The wall frame at a plant: root (world m), outward normal and tangent (horizontal), the wall's foot below the
    /// root (world m), its height and the plant's scale.</summary>
    public readonly record struct Frame(Vector3 Root, Vector3 Normal, Vector3 Tangent, Vector3 Foot, float WallHeight, float Scale)
    {
        public float FootY => Foot.Y;

        /// <summary>The wall's point in the root's column at height <paramref name="y"/>: the mountain form leans walls
        /// linearly with height, so the line through foot and root is the wall's (extrapolated above the root).</summary>
        public Vector3 WallAt(float y)
        {
            float span = Root.Y - Foot.Y;
            if (span < .05f) return new Vector3(Root.X, y, Root.Z);
            var p = Foot + (Root - Foot) * ((y - Foot.Y) / span);
            return new Vector3(p.X, y, p.Z);
        }
    }

    /// <summary>The frame of the plant whose record starts at <paramref name="i"/>; the tangent record follows it.</summary>
    public static Frame FrameOf(float[] records, int i, Transform3D compileToWorld)
    {
        float scale = compileToWorld.Basis.X.Length();
        var root = compileToWorld * new Vector3(records[i + X], records[i + Y], records[i + Z]);
        float angle = records[i + Rotation];
        var authored = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
        var tangent = authored.Cross(Vector3.Up).Normalized();
        int j = i + Stride;
        if (j + Stride <= records.Length && (int)MathF.Round(records[j + Kind]) == FluitownWallPlants.KindWallTangent)
        {
            var along = compileToWorld * new Vector3(records[j + X], records[j + Y], records[j + Z]) - root;
            along.Y = 0;
            if (along.LengthSquared() > 1e-8f) tangent = along.Normalized();
        }
        var normal = tangent.Cross(Vector3.Up).Normalized();
        if (normal.Dot(authored) < 0) normal = -normal;
        var foot = new Vector3(root.X, root.Y - records[i + Lean] * scale, root.Z);
        int k = i + 2 * Stride;
        if (k + Stride <= records.Length && (int)MathF.Round(records[k + Kind]) == FluitownWallPlants.KindWallFoot)
            foot = compileToWorld * new Vector3(records[k + X], records[k + Y], records[k + Z]);
        return new Frame(root, normal, tangent, foot, records[i + Height] * scale, records[i + Radius]);
    }

    /// <summary>Emits the wall plant of the record at <paramref name="i"/>: ivy and fern into <paramref name="herbs"/> and
    /// the bird's-eye mesh <paramref name="far"/> (the same plant: the generator is deterministic), a shrub into
    /// <paramref name="plants"/> with its leaf shell in <paramref name="shells"/> (thread-safe).</summary>
    public static void Emit(HerbMeshBuffers herbs, HerbMeshBuffers far, PlantMeshBuffers plants, HerbMeshBuffers shells, float[] records, int i,
        Transform3D compileToWorld)
    {
        var frame = FrameOf(records, i, compileToWorld);
        int form = (int)MathF.Round(records[i + Form]);
        uint seed = unchecked((uint)(records[i + Seed] * 16777215f) * 2654435761u ^ (uint)(int)MathF.Round(records[i + X]) * 40503u ^
            (uint)(int)MathF.Round(records[i + Z]) * 69069u);
        var mid = Hex(records[i + ColourLeaf]);
        var deep = Hex(records[i + ColourShade]);
        var stem = Hex(records[i + ColourWood]);
        var accent = Hex(records[i + ColourAccent]);
        var light = Hex(records[i + Bloom]);
        switch (form)
        {
            case FluitownWallPlants.FormIvy:
                Ivy(herbs, frame, seed, mid, deep, light, stem);
                Ivy(far, frame, seed, mid, deep, light, stem);
                break;
            case FluitownWallPlants.FormFern:
                Fern(herbs, frame, seed, mid, deep, light, stem);
                Fern(far, frame, seed, mid, deep, light, stem);
                break;
            default: Shrub(plants, shells, frame, seed, mid, deep, light, stem, accent); break;
        }
    }

    // ── Ivy ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Ivy(HerbMeshBuffers m, Frame f, uint seed, Color mid, Color deep, Color light, Color stem)
    {
        var rng = new Rng(seed);
        // From the wall's foot up past its root; on a tall face (a chasm) a patch of up to about three metres around it.
        float footY = MathF.Max(f.FootY, f.Root.Y - 2.2f);
        float reachTop = MathF.Min(f.FootY + f.WallHeight - .25f, f.Root.Y + .5f + rng.Next() * .6f) - footY;
        float height = Math.Clamp(reachTop, 1.1f, 3.4f);
        float width = .7f + rng.Next() * .8f + MathF.Min(1f, height * .15f);
        var foot = f.WallAt(footY);
        m.Begin(foot, height, .012f, rng.Next(), brushable: false);
        var wood = stem.Lerp(deep, .25f);
        int stems = 3 + (int)(rng.Next() * 3);
        for (int s = 0; s < stems; s++)
        {
            float u = stems == 1 ? 0 : (s / (stems - 1f) - .5f);
            float along = u * width * .55f + (rng.Next() - .5f) * .12f;
            float drift = u * .55f + (rng.Next() - .5f) * .3f;
            float top = height * (.62f + rng.Next() * .38f) * (1 - MathF.Abs(u) * .35f);
            Climb(m, f, ref rng, foot, along, drift, top, .009f, wood, mid, deep, light, 0);
        }
    }

    /// <summary>One climbing stem from the foot (or a branch point) up the wall, with its leaves and side branches.</summary>
    private static void Climb(HerbMeshBuffers m, Frame f, ref Rng rng, Vector3 foot, float along, float drift, float top,
        float radius, Color wood, Color mid, Color deep, Color light, int depth, float startY = 0)
    {
        const float step = .11f;
        int steps = Math.Max(2, (int)((top - startY) / step));
        var previous = f.WallAt(foot.Y + startY) + f.Tangent * along + f.Normal * .018f;
        float wander = (rng.Next() - .5f) * 6.28f;
        int leafSide = rng.Next() < .5f ? -1 : 1;
        for (int k = 1; k <= steps; k++)
        {
            float t = (float)k / steps;
            // Wander across the wall and fan out as it climbs; branches lean further sideways.
            wander += (rng.Next() - .5f) * .9f;
            float lateral = MathF.Sin(wander) * .035f + drift * step * (depth == 0 ? .55f : 1.4f);
            var next = previous + f.Tangent * lateral + Vector3.Up * step * (depth == 0 ? 1 : .7f);
            // Hug the rock: a finger's width off the wall at this height (it leans with the mountain form).
            float off = (next - f.WallAt(next.Y)).Dot(f.Normal);
            next += f.Normal * (.018f - off) * .8f;
            float r = MathF.Max(.0028f, radius * (1 - t * .7f));
            Tube(m, previous, (previous + next) * .5f, next, r, r * .92f, 1, 4, wood, wood, HerbMeshBuffers.Stalk);
            // Two leaves per node, alternating sides; older (lower) leaves larger and deeper.
            float age = 1 - t;
            for (int l = 0; l < 2; l++)
            {
                leafSide = -leafSide;
                float size = (.08f + age * .06f + rng.Next() * .035f) * (depth == 0 ? 1 : .85f);
                var at = previous.Lerp(next, .35f + l * .4f);
                Color tone = rng.Next() < .3f ? deep : rng.Next() < .5f ? mid : mid.Lerp(light, .5f);
                tone = tone.Lerp(light, (1 - age) * .35f);
                IvyLeaf(m, f, ref rng, at, leafSide, size, tone, deep);
            }
            if (depth < 1 && k > 1 && k < steps - 1 && rng.Next() < .16f)
            {
                float side = rng.Next() < .5f ? -1 : 1;
                float y = previous.Y - foot.Y;
                Climb(m, f, ref rng, foot, (previous - foot).Dot(f.Tangent), side * (.8f + rng.Next() * .6f),
                    MathF.Min(top, y + .35f + rng.Next() * .45f), r * .8f, wood, mid, deep, light, depth + 1, y);
            }
            previous = next;
        }
    }

    /// <summary>A five-lobed ivy leaf on a short stalk, its face turned out of the wall (and a little up to the light).</summary>
    private static void IvyLeaf(HerbMeshBuffers m, Frame f, ref Rng rng, Vector3 at, int side, float size, Color tone, Color deep)
    {
        var sideways = f.Tangent * side;
        var stalkEnd = at + f.Normal * (.02f + rng.Next() * .015f) + sideways * (.012f + rng.Next() * .01f) - Vector3.Up * .006f;
        Tube(m, at, (at + stalkEnd) * .5f + f.Normal * .004f, stalkEnd, .0018f, .0014f, 1, 3, tone.Darkened(.2f), tone, HerbMeshBuffers.Stalk);
        // Face: out of the wall, tipped up and turned a little to its side; the blade hangs from the stalk.
        var face = (f.Normal * (1 + rng.Next() * .3f) + Vector3.Up * (.35f + rng.Next() * .3f) + sideways * (rng.Next() * .35f)).Normalized();
        var hang = (-Vector3.Up + sideways * (.4f + rng.Next() * .5f)).Normalized();
        hang = (hang - face * hang.Dot(face)).Normalized();
        var across = face.Cross(hang).Normalized();
        var centre = stalkEnd + hang * (size * .45f) + face * (size * .08f);
        const int outline = 10;
        int middle = m.Vertex(centre + face * (size * .06f), face, tone, HerbMeshBuffers.Green);
        int first = m.Vertices.Count;
        var rim = tone.Lerp(deep, .25f);
        for (int k = 0; k < outline; k++)
        {
            // Polar outline from the stalk (angle 0 points back up the stalk): a notch at the stalk, five pointed lobes.
            float a = k * Mathf.Tau / outline;
            float lobe = MathF.Pow(MathF.Abs(MathF.Cos(a * 2.5f)), 1.4f);
            float notch = 1 - .55f * MathF.Exp(-a * a * 6) - .55f * MathF.Exp(-(a - Mathf.Tau) * (a - Mathf.Tau) * 6);
            float r = size * (.66f + .34f * lobe) * notch * (.9f + rng.Next() * .15f);
            var direction = -hang * MathF.Cos(a) + across * MathF.Sin(a);
            var p = centre + direction * r - face * (r * .12f);
            var n = (face + direction * .25f).Normalized();
            m.Vertex(p, n, rim, HerbMeshBuffers.Green);
        }
        for (int k = 0; k < outline; k++) m.Triangle(middle, first + k, first + (k + 1) % outline);
    }

    // ── Fern ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Fern(HerbMeshBuffers m, Frame f, uint seed, Color mid, Color deep, Color light, Color stem)
    {
        var rng = new Rng(seed);
        float size = .38f + f.Scale * .12f + rng.Next() * .15f;
        var root = f.Root - f.Normal * .02f;
        m.Begin(root - Vector3.Up * size, size * 1.6f, .05f, rng.Next(), brushable: false);
        var rachis = stem.Lerp(mid, .45f);
        int fronds = 8 + (int)(rng.Next() * 5);
        for (int k = 0; k < fronds; k++)
        {
            float spread = (k / (fronds - 1f) - .5f) * 2.4f + (rng.Next() - .5f) * .25f;
            float elevation = .75f - MathF.Abs(spread) * .35f + (rng.Next() - .5f) * .35f;
            var horizontal = (f.Normal * MathF.Cos(spread) + f.Tangent * MathF.Sin(spread)).Normalized();
            float length = size * (.75f + rng.Next() * .4f) * (1 - MathF.Abs(spread) * .12f);
            var start = root + horizontal * .02f;
            var bend = start + horizontal * (length * .5f) + Vector3.Up * (length * MathF.Sin(elevation) * .75f);
            var tip = start + horizontal * (length * .88f) + Vector3.Up * (length * (MathF.Sin(elevation) * .55f - .38f));
            Frond(m, ref rng, start, bend, tip, length, rachis, deep, mid, light);
        }
        // A young frond still curled into its fiddlehead.
        var up = (f.Normal * .4f + Vector3.Up).Normalized();
        var a = root + f.Normal * .02f;
        var b = a + up * (size * .3f);
        Tube(m, a, a + up * (size * .18f) + f.Normal * .03f, b, .006f, .005f, 3, 4, rachis, light, HerbMeshBuffers.Green);
        var coil = b;
        var outward = f.Normal;
        for (int k = 0; k < 7; k++)
        {
            float angle = k * .95f;
            float r = size * .06f * (1 - k * .11f);
            var next = b + (outward * MathF.Sin(angle) + up * MathF.Cos(angle)) * r + up * (size * .03f);
            Tube(m, coil, (coil + next) * .5f, next, .0055f - k * .0004f, .005f - k * .0004f, 1, 4, light, light, HerbMeshBuffers.Green, k == 6);
            coil = next;
        }
    }

    /// <summary>One arching frond: a rachis with paired, tapering pinnae angled towards its tip.</summary>
    private static void Frond(HerbMeshBuffers m, ref Rng rng, Vector3 a, Vector3 bend, Vector3 b, float length, Color rachis,
        Color deep, Color mid, Color light)
    {
        Tube(m, a, bend, b, .0045f, .0015f, 4, 3, rachis, rachis.Lerp(light, .3f), HerbMeshBuffers.Stalk);
        const int pairs = 13;
        for (int k = 0; k < pairs; k++)
        {
            float t = .1f + .88f * k / (pairs - 1f);
            var p = Bezier(a, bend, b, t);
            var tangent = BezierTangent(a, bend, b, t);
            // The frond's plane faces up; pinnae leave the rachis to both sides, angled towards the tip.
            var side = tangent.Cross(Vector3.Up);
            if (side.LengthSquared() < 1e-6f) side = Perpendicular(tangent);
            side = side.Normalized();
            var face = side.Cross(tangent).Normalized();
            if (face.Y < 0) face = -face;
            float taper = MathF.Pow(MathF.Max(0, MathF.Sin(MathF.PI * MathF.Min(1, .12f + .95f * t))), .8f) * (1 - .45f * t);
            float pinna = length * .2f * taper;
            if (pinna < .006f) continue;
            var tone = deep.Lerp(mid, .4f + t * .4f).Lerp(light, t * t * .5f);
            for (int s = -1; s <= 1; s += 2)
            {
                var along = (side * s + tangent * .55f - face * .12f).Normalized();
                var shape = new BladeShape
                {
                    Lift = .05f,
                    Curl = -.25f,
                    Cup = .1f,
                    MaxWidth = .2f,
                    Widest = .3f,
                    Blunt = .1f,
                    BaseWidth = .45f,
                };
                Blade(m, p, along, face, pinna * (.9f + rng.Next() * .15f), shape, tone.Lerp(deep, .2f), tone.Lerp(light, .15f),
                    HerbMeshBuffers.Green, 2, 3);
            }
        }
    }

    // ── Shrub ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Color[] CrownTints(Color leaf, Color shade, float lift)
    {
        var ivory = new Color(0.78f, 0.80f, 0.71f);
        return new[] { leaf, leaf.Lerp(ivory, .12f * lift), leaf.Lerp(ivory, .23f * lift), leaf.Lerp(shade, .36f), leaf.Lerp(shade, .18f) };
    }

    private static void Shrub(PlantMeshBuffers plants, HerbMeshBuffers shells, Frame f, uint seed, Color mid, Color deep, Color light, Color stem, Color accent)
    {
        var rng = new Rng(seed);
        float height = .5f + f.Scale * .12f + rng.Next() * .3f;
        float radius = height * (.8f + rng.Next() * .25f);
        var plan = PlantGrowth.Bush(height, radius * .8f, 4 + (int)(rng.Next() * 2), false, seed);
        // Deeper and richer than the meadow's bushes: it grows in a damp crevice.
        var leaf = mid.Lerp(deep, .25f);
        leaf = Color.FromHsv(leaf.H, Math.Min(1, leaf.S * 1.25f), leaf.V);
        var palette = new PlantPalette(CrownTints(leaf, deep, .6f), stem, accent.Lerp(light, .3f), 0);
        int first = plants.Vertices.Count;
        var foot = f.Root - f.Normal * .04f;
        // The crown's inner mass under its leaves (LeafShell): a little smaller and deeper than the leaves.
        PlantMesher.Emit(plants, LeafShell.Core(plan, .9f), foot, LeafShell.Shaded(palette, deep, .25f), rng.Next(), .03f, seed, 34);
        // Lean out of the rock: the bush's up tips towards the wall normal, so its crown clears the face.
        float tilt = .75f + rng.Next() * .3f;
        var axis = f.Tangent;
        var rotation = new Basis(axis, -tilt);
        if ((rotation * Vector3.Up).Dot(f.Normal) < 0) rotation = new Basis(axis, tilt);
        for (int v = first; v < plants.Vertices.Count; v++)
        {
            plants.Vertices[v] = foot + rotation * (plants.Vertices[v] - foot);
            plants.Normals[v] = rotation * plants.Normals[v];
        }
        LeafShell.Emit(shells, plan, foot, palette, rng.Next(), .03f, seed, rotation);
    }
}
