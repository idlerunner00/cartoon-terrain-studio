using System;
using Fluitown.Render;
using Godot;
using static Fluitown.Render.FluitownVegetation;
using static Fluitown.GodotApp.Rendering.Vegetation.HerbShapes;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>
/// The comic look's wildflowers (<see cref="FluitownFlowers"/>): each lane record grows one real low-poly flower of its
/// species — a round, leaning stem, folded leaves in the species' rhythm (basal rosettes for daisy and buttercup, leaves
/// along the stem for the others), and a head of smoothly cupped petals around a domed centre:
/// <list type="bullet">
/// <item>daisy: 14–18 slender rays drooping at their tips around a raised yellow dome;</item>
/// <item>buttercup: five or six broad, rounded petals climbing into a glossy cup;</item>
/// <item>cornflower: a ring of flared, toothed florets around shorter inner ones and a dark tuft;</item>
/// <item>poppy: four broad, ruffled petals in a bowl around a seed capsule with a star cap and a ring of stamens;</item>
/// <item>bellflower: nodding bells with five flared, pointed lobes on arching stalks.</item>
/// </list>
/// Each specimen keeps the individuality the ported <see cref="FloorFlowerGeometry"/> gives its world site (life stage,
/// openness, asymmetry, head tilt, leaf size, forks, weathering with a missing petal), so the meadow is not cloned.
/// Sizes are scaled to the Flui (0.94 m): stems 0.3–0.6 m rise just above the grass, heads 10–18 cm across.
/// </summary>
public static class FlowerMesher
{
    /// <summary>The ported sizes are authored for the original's larger player: stems and heads scaled to the Flui.</summary>
    public const float StemScale = .95f, HeadScale = .55f;

    private static readonly float[] LeafCounts = { 3, 2, 3, 2, 3 };
    private static readonly float[] LeafLengths = { .13f, .16f, .2f, .18f, .17f };
    private static readonly float[] LeafWidths = { 1.15f, 1.04f, .66f, 1.3f, .86f };
    private static readonly float[] BranchThresholds = { .7f, .8f, .56f, .86f, .52f };

    private static Color Hex(float value)
    {
        int hex = (int)MathF.Round(value);
        return new Color(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f);
    }

    /// <summary>A near-black pigment (the sakura world's black flower) reads as a hole under the comic ramp: it becomes a
    /// deep plum that still shows its form; brighter pigments stay.</summary>
    private static Color Readable(Color c, Color tone)
    {
        float luminance = c.R * .2126f + c.G * .7152f + c.B * .0722f;
        return luminance >= .14f ? c : tone.Lerp(c, luminance / .14f * .5f);
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

    /// <summary>Emits the flower of the lane record at <paramref name="i"/> (thread-safe); <paramref name="far"/> receives its
    /// few-triangle stand-in for the bird's-eye view, where a flower is a few pixels.</summary>
    public static void Emit(HerbMeshBuffers m, HerbMeshBuffers? far, float[] records, int i, Transform3D compileToWorld)
    {
        float scale = compileToWorld.Basis.X.Length();
        var foot = compileToWorld * new Vector3(records[i + X], records[i + Y], records[i + Z]);
        float stemHeight = records[i + Height] * scale * StemScale;
        float radius = records[i + Radius] * scale * HeadScale;
        float siteX = records[i + Seed], siteY = records[i + Bloom];
        int species = Math.Clamp((int)MathF.Round(records[i + Form]), 0, 4);
        var individual = FloorFlowerGeometry.floorFlowerIndividualityAt(siteX, siteY);
        uint seed = unchecked((uint)(int)siteX * 73856093u ^ (uint)(int)siteY * 19349663u ^ 0x5bd1e995u);
        var rng = new Rng(seed);
        float phase = rng.Next();
        var stem = Hex(records[i + ColourLeaf]);
        var plum = new Color(.38f, .26f, .4f);
        var deep = Readable(Hex(records[i + ColourShade]), plum * .8f);
        var petal = Readable(Hex(records[i + ColourWood]), plum);
        var centre = Readable(Hex(records[i + ColourAccent]), new Color(.2f, .16f, .14f));
        float leanAngle = records[i + Lean], lean = records[i + Density] * stemHeight;
        float rotation = records[i + Rotation];

        // A flower sways more than a tree for its size: its head moves a few centimetres in a breeze.
        m.Begin(foot, stemHeight + radius, .05f + stemHeight * .09f, phase);
        var leanDirection = new Vector3(MathF.Cos(leanAngle), 0, MathF.Sin(leanAngle));
        var top = foot + leanDirection * lean + Vector3.Up * stemHeight;
        var bend = foot + leanDirection * (lean * .12f) + Vector3.Up * (stemHeight * .55f);
        var root = foot - Vector3.Up * .03f;
        float stemRadius = Math.Clamp(.0055f + stemHeight * .006f, .005f, .01f);
        var stemDark = stem.Darkened(.18f);
        if (far != null) Far(far, foot, root, top, stemHeight, radius, species, rotation, phase, stem, petal, deep, centre);
        Tube(m, root, bend, top, stemRadius, stemRadius * .72f, 5, 5, stemDark, stem, HerbMeshBuffers.Stalk);

        Leaves(m, species, individual, ref rng, foot, root, bend, top, stemHeight, leanAngle, stem);
        var tangent = BezierTangent(root, bend, top, 1);
        Head(m, species, individual, ref rng, top, tangent, radius, rotation, petal, deep, centre, stem, stemRadius);

        // Forks: a side stalk with a smaller, younger head (the ported branch rule).
        int branches = individual.stage == "bud" || individual.fork <= BranchThresholds[species] ? 0
            : individual.fork > .92 && (species == 2 || species == 4) ? 2 : 1;
        for (int b = 0; b < branches; b++)
        {
            int sideSign = (individual.asymmetry < 0 ? -1 : 1) * (b % 2 == 0 ? 1 : -1);
            float angle = leanAngle + sideSign * (.68f + (float)individual.openness * .38f) + (rng.Next() - .5f) * .2f;
            float fraction = .42f + b * .11f + rng.Next() * .055f;
            var from = Bezier(root, bend, top, fraction);
            float length = stemHeight * (.24f + (float)individual.fork * .12f) * (b == 0 ? 1 : .82f);
            var dir = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var tip = from + dir * (length * .62f) + Vector3.Up * (length * .8f);
            var mid = from + dir * (length * .45f) + Vector3.Up * (length * .25f);
            Tube(m, from, mid, tip, stemRadius * .7f, stemRadius * .55f, 3, 4, stem, stem, HerbMeshBuffers.Stalk);
            var young = individual.Clone();
            young.stage = individual.fork > .84 && b == 0 ? "opening" : "bud";
            young.openness = rng.Next();
            young.headTiltAngle = angle;
            young.missingPetal = (individual.missingPetal + b + 3) % 11;
            Head(m, species, young, ref rng, tip, BezierTangent(from, mid, tip, 1), radius * (.5f + rng.Next() * .14f),
                rotation + sideSign * (.28f + b * .15f), petal, deep, centre, stem, stemRadius * .6f);
        }
    }

    /// <summary>The bird's-eye stand-in: a stalk and a star of the species' petal count in the head's colours (~20
    /// triangles instead of several hundred sub-pixel ones).</summary>
    private static void Far(HerbMeshBuffers m, Vector3 foot, Vector3 root, Vector3 top, float stemHeight, float radius, int species,
        float rotation, float phase, Color stem, Color petal, Color deep, Color centre)
    {
        m.Begin(foot, stemHeight + radius, .05f + stemHeight * .09f, phase);
        Tube(m, root, (root + top) * .5f, top, .012f, .01f, 1, 3, stem, stem, HerbMeshBuffers.Stalk);
        int points = species switch { 0 => 8, 1 => 5, 2 => 8, 3 => 4, _ => 5 };
        float inner = species == 3 || species == 1 ? .7f : .45f;
        var head = top + Vector3.Up * (radius * .15f);
        int middle = m.Vertex(head + Vector3.Up * (radius * .12f), Vector3.Up, centre, HerbMeshBuffers.Centre);
        int first = m.Vertices.Count;
        for (int k = 0; k < points * 2; k++)
        {
            float a = rotation + k * MathF.PI / points;
            float r = radius * (k % 2 == 0 ? 1.05f : inner);
            m.Vertex(head + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r), Vector3.Up, k % 2 == 0 ? petal : petal.Lerp(deep, .3f), HerbMeshBuffers.Petal);
        }
        for (int k = 0; k < points * 2; k++) m.Triangle(middle, first + k, first + (k + 1) % (points * 2));
    }

    private static float Opening(FloorFlowerIndividuality individual) => individual.stage switch
    {
        "opening" => .38f + (float)individual.openness * .28f,
        "weathered" => .88f + (float)individual.openness * .12f,
        _ => .76f + (float)individual.openness * .24f,
    };

    private static bool Missing(FloorFlowerIndividuality individual, int petal, int count) =>
        individual.stage == "weathered" && individual.weathering > .24 && individual.missingPetal % count == petal;

    /// <summary>The species' leaf rhythm (the ported tables): a basal rosette (daisy, buttercup) or leaves climbing the stem.</summary>
    private static void Leaves(HerbMeshBuffers m, int species, FloorFlowerIndividuality individual, ref Rng rng, Vector3 foot,
        Vector3 root, Vector3 bend, Vector3 top, float stemHeight, float leanAngle, Color stem)
    {
        int count = (int)MathF.Max(1, LeafCounts[species] + (individual.leafBias > .78 ? 1 : 0)
            - (individual.stage == "weathered" && individual.weathering > .72 ? 1 : 0));
        bool basal = species <= 1;
        if (basal) count += 2;
        var leafColour = stem.Lerp(new Color(.86f, .9f, .7f), .08f);
        for (int leaf = 0; leaf < count; leaf++)
        {
            float roll = rng.Next(), rollB = rng.Next();
            int side = (leaf + (individual.leafBias > .5 ? 1 : 0)) % 2 == 0 ? -1 : 1;
            float angle = basal
                ? leanAngle + (float)individual.leafBias * Mathf.Tau + leaf * 2.399963f + (roll - .5f) * .28f
                : leanAngle + side * (.82f + leaf * .18f + (float)individual.leafBias * .22f) + (roll - .5f) * .24f + (leaf % 2) * MathF.PI;
            float length = stemHeight * LeafLengths[species] * (float)individual.leafScale * (.82f + rollB * .28f + leaf * .045f) * (basal ? 1.25f : 1.45f);
            float fraction = basal ? .02f + roll * .03f : .16f + (leaf + 1f) / (count + 1) * .5f + (roll - .5f) * .045f;
            var at = Bezier(root, bend, top, fraction);
            var dir = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            // Leaves leave the stem rising and arch over; basal leaves spread low over the ground.
            float rise = basal ? .18f + roll * .2f : .55f + roll * .25f;
            var along = (dir + Vector3.Up * rise).Normalized();
            var shape = new BladeShape
            {
                Lift = basal ? .05f : .1f,
                Curl = basal ? -.18f : -.42f - rollB * .2f,
                Fold = .16f,
                Cup = -.04f,
                MaxWidth = .13f * LeafWidths[species] * (.82f + roll * .34f),
                Widest = species == 3 ? .45f : .38f,
                Blunt = species == 3 ? .5f : .15f,
                BaseWidth = .12f,
            };
            var shade = leafColour.Lerp(stem.Darkened(.1f), .35f + rollB * .2f);
            Blade(m, at, along, Vector3.Up, length, shape, shade, leafColour.Lerp(Colors.White, .03f + roll * .05f), HerbMeshBuffers.Green, 4, 3);
        }
    }

    /// <summary>A flower head at <paramref name="at"/> on a stem arriving along <paramref name="tangent"/>.</summary>
    private static void Head(HerbMeshBuffers m, int species, FloorFlowerIndividuality individual, ref Rng rng, Vector3 at,
        Vector3 tangent, float radius, float rotation, Color petal, Color deep, Color centre, Color stem, float stemRadius)
    {
        // The head faces up along the stem, tilted by its individual head tilt.
        var tilt = new Vector3(MathF.Cos((float)individual.headTiltAngle), 0, MathF.Sin((float)individual.headTiltAngle));
        float tiltAmount = (float)individual.headTilt * (individual.stage == "opening" ? 1.18f : individual.stage == "weathered" ? 1.1f : 1f);
        var axis = (Vector3.Up * .6f + tangent * .4f + tilt * tiltAmount * 1.4f).Normalized();
        var side = Perpendicular(axis).Rotated(axis, rotation);
        if (individual.stage == "bud")
        {
            Bud(m, at, axis, side, radius, petal, deep, stem);
            return;
        }
        float opening = Opening(individual);
        // A calyx cups the head from below: the stem's green thickens into it.
        Dome(m, at - axis * (radius * .06f), -axis, side, radius * .2f, radius * .14f, 7, stem, stem.Darkened(.12f), HerbMeshBuffers.Green);
        switch (species)
        {
            case 0: Daisy(m, individual, ref rng, at, axis, side, radius, opening, petal, deep, centre); break;
            case 1: Buttercup(m, individual, ref rng, at, axis, side, radius, opening, petal, deep, centre); break;
            case 2: Cornflower(m, individual, ref rng, at, axis, side, radius, opening, petal, deep, centre); break;
            case 3: Poppy(m, individual, ref rng, at, axis, side, radius, opening, petal, deep, centre); break;
            default: Bells(m, individual, ref rng, at, tangent, side, radius, opening, petal, deep, centre, stem, stemRadius); break;
        }
    }

    private static Vector3 Around(Vector3 axis, Vector3 side, float angle) => side.Rotated(axis, angle);

    private static void Daisy(HerbMeshBuffers m, FloorFlowerIndividuality individual, ref Rng rng, Vector3 at, Vector3 axis,
        Vector3 side, float radius, float opening, Color petal, Color deep, Color centre)
    {
        int count = 14 + (int)MathF.Floor((float)individual.petalJitter * 5);
        for (int k = 0; k < count; k++)
        {
            if (Missing(individual, k, count)) continue;
            float angle = k * Mathf.Tau / count + (rng.Next() - .5f) * (.13f + (float)individual.asymmetry * .025f);
            var dir = Around(axis, side, angle);
            float length = radius * (.78f + opening * .22f) * (.9f + rng.Next() * .2f);
            var shape = new BladeShape
            {
                Lift = .06f + (1 - opening) * .9f + (k % 2) * .05f,
                Curl = individual.stage == "weathered" ? -.55f : -.28f - rng.Next() * .1f,
                Cup = .14f,
                MaxWidth = .105f + rng.Next() * .025f,
                Widest = .62f,
                Blunt = .7f,
                BaseWidth = .35f,
            };
            var root = at + dir * (radius * .2f) + axis * (radius * (.05f + (k % 2) * .02f));
            var colour = petal.Lerp(deep, rng.Next() * .12f);
            Blade(m, root, dir, axis, length, shape, colour.Lerp(deep, .3f), colour, HerbMeshBuffers.Petal, 3, 3);
        }
        Dome(m, at + axis * (radius * .06f), axis, side, radius * .26f, radius * .16f, 10, centre.Darkened(.25f), centre.Lightened(.08f),
            HerbMeshBuffers.Centre, radius * .06f);
    }

    private static void Buttercup(HerbMeshBuffers m, FloorFlowerIndividuality individual, ref Rng rng, Vector3 at, Vector3 axis,
        Vector3 side, float radius, float opening, Color petal, Color deep, Color centre)
    {
        int count = individual.petalJitter > .82 ? 6 : 5;
        for (int k = 0; k < count; k++)
        {
            if (Missing(individual, k, count)) continue;
            float angle = k * Mathf.Tau / count + (rng.Next() - .5f) * .1f * (float)individual.openness;
            var dir = Around(axis, side, angle);
            var shape = new BladeShape
            {
                Lift = .45f + (1 - opening) * .8f,
                Curl = .12f,
                Cup = .32f,
                MaxWidth = .44f + rng.Next() * .06f,
                Widest = .66f,
                Blunt = 1,
                BaseWidth = .25f,
            };
            var root = at + dir * (radius * .08f) + axis * (radius * .03f * (k % 2));
            Blade(m, root, dir, axis, radius * (.8f + rng.Next() * .12f), shape, petal.Lerp(deep, .45f), petal.Lightened(.06f),
                HerbMeshBuffers.Petal, 3, 5);
        }
        Dome(m, at + axis * (radius * .12f), axis, side, radius * .2f, radius * .12f, 8, centre.Lerp(deep, .3f), centre, HerbMeshBuffers.Centre);
    }

    private static void Cornflower(HerbMeshBuffers m, FloorFlowerIndividuality individual, ref Rng rng, Vector3 at, Vector3 axis,
        Vector3 side, float radius, float opening, Color petal, Color deep, Color centre)
    {
        int outer = 8 + (individual.petalJitter > .6 ? 1 : 0);
        for (int ring = 0; ring < 2; ring++)
        {
            int count = ring == 0 ? outer : 6;
            float reach = ring == 0 ? 1f : .55f;
            for (int k = 0; k < count; k++)
            {
                if (ring == 0 && Missing(individual, k, count)) continue;
                float angle = (k + ring * .5f) * Mathf.Tau / count + (rng.Next() - .5f) * .12f;
                var dir = Around(axis, side, angle);
                // A floret: a narrow funnel flaring into a ragged, toothed mouth.
                var shape = new BladeShape
                {
                    Lift = (ring == 0 ? .28f : .9f) + (1 - opening) * .7f,
                    Curl = ring == 0 ? .05f : -.1f,
                    Cup = .38f,
                    MaxWidth = .2f + rng.Next() * .04f,
                    Widest = .95f,
                    Blunt = .85f,
                    BaseWidth = .12f,
                    Ruffle = .06f,
                    Seed = rng.Next() * 6.28f,
                };
                var root = at + dir * (radius * .1f) + axis * (radius * (.06f + ring * .06f));
                Blade(m, root, dir, axis, radius * .78f * reach * (.9f + rng.Next() * .18f), shape,
                    deep.Lerp(petal, ring == 0 ? .35f : .1f), ring == 0 ? petal.Lightened(.05f) : petal, HerbMeshBuffers.Petal, 3, 5);
            }
        }
        Dome(m, at + axis * (radius * .12f), axis, side, radius * .14f, radius * .12f, 7, centre, centre.Lerp(deep, .4f), HerbMeshBuffers.Centre);
    }

    private static void Poppy(HerbMeshBuffers m, FloorFlowerIndividuality individual, ref Rng rng, Vector3 at, Vector3 axis,
        Vector3 side, float radius, float opening, Color petal, Color deep, Color centre)
    {
        int count = individual.petalJitter > .88 ? 5 : 4;
        for (int k = 0; k < count; k++)
        {
            if (Missing(individual, k, count)) continue;
            float angle = k * Mathf.Tau / count + (rng.Next() - .5f) * .2f;
            var dir = Around(axis, side, angle);
            var shape = new BladeShape
            {
                Lift = .75f + (1 - opening) * .9f + (k % 2) * .05f,
                Curl = -.28f - (individual.stage == "weathered" ? .3f : 0),
                Cup = .42f,
                MaxWidth = .58f + rng.Next() * .08f,
                Widest = .6f,
                Blunt = 1,
                BaseWidth = .3f,
                Ruffle = .045f,
                Seed = rng.Next() * 6.28f,
            };
            var root = at + dir * (radius * .05f) + axis * (radius * (.02f + (k % 2) * .025f));
            Blade(m, root, dir, axis, radius * (.95f + rng.Next() * .1f), shape, petal.Lerp(deep, .55f), petal,
                HerbMeshBuffers.Petal, 4, 5);
        }
        // The seed capsule with its star cap, ringed by dark stamens.
        var capsule = at + axis * (radius * .05f);
        Tube(m, capsule, capsule + axis * (radius * .14f), capsule + axis * (radius * .28f), radius * .15f, radius * .12f, 2, 8,
            centre.Lerp(deep, .2f), centre, HerbMeshBuffers.Centre);
        Dome(m, capsule + axis * (radius * .28f), axis, side, radius * .17f, radius * .05f, 8, deep.Darkened(.2f), centre.Darkened(.1f),
            HerbMeshBuffers.Centre);
        var stamen = deep.Darkened(.45f);
        for (int k = 0; k < 12; k++)
        {
            var dir = Around(axis, side, k * Mathf.Tau / 12 + .13f);
            var from = capsule + dir * (radius * .14f) + axis * (radius * .04f);
            var to = from + (dir * .6f + axis).Normalized() * (radius * .2f);
            Tube(m, from, from.Lerp(to, .5f), to, radius * .018f, radius * .012f, 1, 3, stamen, stamen.Lightened(.1f), HerbMeshBuffers.Centre, true);
        }
    }

    /// <summary>Nodding bells on arching stalks: one at the top, a second or third lower when the plant is vigorous.</summary>
    private static void Bells(HerbMeshBuffers m, FloorFlowerIndividuality individual, ref Rng rng, Vector3 at, Vector3 tangent,
        Vector3 side, float radius, float opening, Color petal, Color deep, Color centre, Color stem, float stemRadius)
    {
        int bells = 1 + (individual.openness > .45 ? 1 : 0) + (individual.fork > .7 ? 1 : 0);
        for (int b = 0; b < bells; b++)
        {
            float angle = (float)individual.headTiltAngle + b * 2.1f;
            var dir = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var from = at - tangent * (radius * 1.1f * b);
            float size = radius * (1 - b * .16f);
            // The stalk arches out and over; the bell hangs from its end, mouth down and a little outward.
            var stalkTip = from + dir * (size * .9f) + Vector3.Up * (size * .15f);
            var stalkBend = from + dir * (size * .35f) + Vector3.Up * (size * .55f);
            Tube(m, from, stalkBend, stalkTip, stemRadius * .7f, stemRadius * .5f, 3, 4, stem, stem, HerbMeshBuffers.Stalk);
            var mouth = (Vector3.Down * (1.1f - opening * .25f) + dir * (.35f + opening * .3f)).Normalized();
            Bell(m, stalkTip, mouth, Perpendicular(mouth).Rotated(mouth, rng.Next() * 6.28f), size, opening, petal, deep, centre);
        }
    }

    /// <summary>A bell: a lathe from the stalk to a flared mouth with five pointed lobes, open towards <paramref name="mouth"/>.</summary>
    private static void Bell(HerbMeshBuffers m, Vector3 top, Vector3 mouth, Vector3 side, float size, float opening,
        Color petal, Color deep, Color centre)
    {
        const int sides = 15; // three per lobe: a lobe tip between two notches
        float length = size * 1.25f;
        float[] depth = { 0f, .18f, .5f, .82f, 1f };
        float[] width = { .12f, .42f, .5f, .56f, .78f + opening * .12f };
        int rings = depth.Length;
        var other = mouth.Cross(side);
        int first = m.Vertices.Count;
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < sides; s++)
            {
                float angle = s * Mathf.Tau / sides;
                var radial = side * MathF.Cos(angle) + other * MathF.Sin(angle);
                bool lobe = s % 3 == 0;
                float w = width[r] * size, d = depth[r] * length;
                if (r == rings - 1)
                {
                    // Lobe tips reach further and curl outward; the notches between them stay back.
                    w *= lobe ? 1.18f : .92f;
                    d *= lobe ? 1.08f : .9f;
                }
                var p = top + mouth * d + radial * w;
                var n = (radial - mouth * (r == 0 ? .8f : .25f)).Normalized();
                var colour = deep.Lerp(petal, MathF.Min(1, depth[r] * 1.3f));
                m.Vertex(p, n, colour, HerbMeshBuffers.Petal);
            }
        }
        for (int r = 0; r + 1 < rings; r++)
            for (int s = 0; s < sides; s++)
            {
                int k0 = first + r * sides + s, k1 = first + r * sides + (s + 1) % sides;
                m.Quad(k0, k1, k1 + sides, k0 + sides);
            }
        int cap = m.Vertex(top - mouth * (size * .03f), -mouth, deep, HerbMeshBuffers.Petal);
        for (int s = 0; s < sides; s++) m.Triangle(first + s, first + (s + 1) % sides, cap);
        // The style hangs in the mouth, a pale clapper.
        var from = top + mouth * (length * .3f);
        Tube(m, from, from + mouth * (length * .3f), from + mouth * (length * .72f), size * .03f, size * .045f, 2, 4, centre, centre.Lightened(.1f),
            HerbMeshBuffers.Centre, true);
    }

    /// <summary>A closed bud: a green-sepaled teardrop tinged with the petal pigment at its tip.</summary>
    private static void Bud(HerbMeshBuffers m, Vector3 at, Vector3 axis, Vector3 side, float radius, Color petal, Color deep, Color stem)
    {
        var other = axis.Cross(side);
        const int sides = 8;
        float[] depth = { 0f, .25f, .55f, .82f };
        float[] width = { .16f, .3f, .27f, .14f };
        float length = radius * .75f;
        int first = m.Vertices.Count;
        for (int r = 0; r < depth.Length; r++)
            for (int s = 0; s < sides; s++)
            {
                float angle = s * Mathf.Tau / sides;
                var radial = side * MathF.Cos(angle) + other * MathF.Sin(angle);
                var colour = r < 2 ? stem.Lerp(deep, r * .25f) : deep.Lerp(petal, (r - 1) * .5f);
                m.Vertex(at + axis * (depth[r] * length) + radial * (width[r] * radius), (radial + axis * (depth[r] - .4f)).Normalized(),
                    colour, r < 2 ? HerbMeshBuffers.Green : HerbMeshBuffers.Petal);
            }
        for (int r = 0; r + 1 < depth.Length; r++)
            for (int s = 0; s < sides; s++)
            {
                int k0 = first + r * sides + s, k1 = first + r * sides + (s + 1) % sides;
                m.Quad(k0, k1, k1 + sides, k0 + sides);
            }
        int tip = m.Vertex(at + axis * length, axis, petal, HerbMeshBuffers.Petal);
        int last = first + (depth.Length - 1) * sides;
        for (int s = 0; s < sides; s++) m.Triangle(last + s, last + (s + 1) % sides, tip);
    }
}
