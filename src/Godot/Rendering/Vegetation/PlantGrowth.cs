using System;
using System.Collections.Generic;
using Godot;

namespace Fluitown.GodotApp.Rendering.Vegetation;

/// <summary>
/// Growth plans of the comic look's trees and bushes — godot-flui's <c>UnderstoryPlant.Grow</c> (runtime/worldgen), sized
/// for this terrain. A plan is wood (quadratic Bézier boughs with a radius ramp) and foliage (crown ellipsoids: a
/// connected heart plus uneven overlapping lobes and a finer perimeter). <see cref="PlantMesher"/> turns the wood into
/// fluted tubes and the union of the crown ellipsoids into one faceted low-poly crown, as godot-flui's LowPolyMesher does
/// for its voxel crowns.
///
/// Differences to godot-flui, all for this terrain: sizes come from the terrain's phenotype (trees 3–8 m, bushes
/// 0.5–1.2 m, against godot-flui's 3–24 m understory and 58–86 m giants); the terrain's crown forms (conic, columnar,
/// fan) get their own plans next to godot-flui's round crown; limbs may be thinner than a 25 cm voxel; no voxels,
/// no destruction.
/// </summary>
public sealed class PlantPlan
{
    public readonly record struct Bough(Vector3 A, Vector3 Bend, Vector3 B, float Start, float End);
    public readonly record struct Crown(Vector3 Centre, Vector3 Radius, int Tint);

    public readonly List<Bough> Wood = new(48);
    public readonly List<Crown> Foliage = new(512);
    /// <summary>Height of the plan's top (m above the foot); the wind weight is measured against it.</summary>
    public float Height;
    /// <summary>Trunk radius at the foot (collision).</summary>
    public float TrunkRadius;
    /// <summary>Height up to which the trunk is solid for collision.</summary>
    public float TrunkSolidHeight;
}

public static class PlantGrowth
{
    public const int FormConic = 1, FormFan = 2, FormColumnar = 3;

    /// <summary>
    /// A tree: godot-flui's <c>UnderstoryPlant.Tree</c>/<c>Sapling</c> for round crowns, and plans of the same grammar for
    /// the terrain's conic (pine), columnar (cypress) and fan (parasol) forms.
    /// </summary>
    public static PlantPlan Tree(float h, float crownRadius, int form, float leanAmount, float leanAngle, uint seed)
    {
        var random = new Random(unchecked((int)seed));
        float R() => (float)random.NextDouble();
        var plan = new PlantPlan { Height = h };
        float yaw = R() * Mathf.Tau;
        var basis = new Basis(Vector3.Up, yaw);
        Vector3 P(float x, float y, float z) => basis * new Vector3(x, y, z);
        // The phenotype's lean (0..1 along its phase) plus godot-flui's own random lean.
        float leanX = MathF.Cos(leanAngle) * leanAmount * h * .10f, leanZ = MathF.Sin(leanAngle) * leanAmount * h * .10f;
        leanX += (R() - .5f) * h * .08f;
        leanZ += (R() - .5f) * h * .06f;
        // Lean is authored in world space; P() turns local offsets by the specimen's yaw.
        var leanWorld = new Vector3(leanX, 0, leanZ);
        Vector3 L(float fraction) => leanWorld * fraction;

        void AddBough(Vector3 a, Vector3 bend, Vector3 b, float start, float end) =>
            plan.Wood.Add(new(a, bend, b, MathF.Max(.018f, start), MathF.Max(.012f, end)));

        void Cluster(Vector3 at, Vector3 radius, int tint, int lobes)
        {
            // godot-flui: connected hearts, uneven overlapping lobes and a finer perimeter.
            plan.Foliage.Add(new(at, radius, tint));
            for (int i = 0; i < lobes; i++)
            {
                float v = 1 - 2 * (i + .5f) / lobes, a = i * 2.399963f + R() * .25f;
                float radial = MathF.Sqrt(MathF.Max(0, 1 - v * v));
                var direction = new Vector3(MathF.Cos(a) * radial, v, MathF.Sin(a) * radial);
                var lobe = radius * (.26f + R() * .13f);
                lobe = lobe.Max(Vector3.One * MathF.Max(.06f, h * .018f));
                plan.Foliage.Add(new(at + direction * radius * .85f, lobe, tint + i));
                if (i % 2 == 0) plan.Foliage.Add(new(at + direction * radius * 1.07f, lobe * .60f, tint + i + 1));
            }
        }

        bool young = h < 3.6f;
        float trunkRadius = MathF.Max(.07f, h * (young ? .034f : .040f));
        plan.TrunkRadius = trunkRadius;
        var basePoint = P(0, -.12f, 0);

        // Roots: godot-flui's buttress boughs from a toe below the ground up into the bole.
        void Roots(int count, float spread)
        {
            for (int root = 0; root < count; root++)
            {
                float a = root * 2.399963f;
                var toe = P(MathF.Cos(a) * trunkRadius * spread, -.10f, MathF.Sin(a) * trunkRadius * spread);
                AddBough(toe, basePoint + Vector3.Up * .06f, P(0, h * .07f, 0), MathF.Max(.03f, trunkRadius * .22f), trunkRadius * .66f);
            }
        }

        switch (form)
        {
            case FormConic:
            {
                // A conifer: a straight leader to the tip and tiers of drooping skirts that narrow upwards.
                float r = Math.Clamp(crownRadius, h * .2f, h * .32f);
                var top = L(1) + Vector3.Up * h * .97f;
                AddBough(basePoint, L(.4f) + Vector3.Up * h * .45f, top, trunkRadius, trunkRadius * .12f);
                Roots(young ? 3 : 5, 2.6f);
                int tiers = young ? 4 : 5 + (int)(seed % 2);
                for (int tier = 0; tier < tiers; tier++)
                {
                    float t = tier / (float)(tiers - 1);
                    float level = h * (.24f + t * .64f);
                    float tierR = r * (1f - t * .72f) * (.92f + R() * .12f);
                    var centre = L(level / h) + Vector3.Up * level;
                    // Skirt: a flat heart with lobes hanging on its lower half.
                    plan.Foliage.Add(new(centre, new Vector3(tierR * .72f, h * .07f, tierR * .72f), tier));
                    int lobes = young ? 9 : 12;
                    for (int i = 0; i < lobes; i++)
                    {
                        float a = i * Mathf.Tau / lobes + R() * .4f + tier * .7f;
                        var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                        float reach = tierR * (.62f + R() * .2f);
                        var lobeR = new Vector3(tierR * .34f, h * (.055f + R() * .02f), tierR * .34f);
                        plan.Foliage.Add(new(centre + dir * reach - Vector3.Up * h * (.035f + R() * .02f), lobeR, tier + i));
                    }
                    // A short limb into the tier, visible from below.
                    float limbA = tier * 2.399963f;
                    var limbEnd = centre + new Vector3(MathF.Cos(limbA), 0, MathF.Sin(limbA)) * tierR * .7f - Vector3.Up * h * .03f;
                    AddBough(centre - Vector3.Up * h * .02f, centre.Lerp(limbEnd, .5f), limbEnd, trunkRadius * .32f, .02f);
                }
                plan.Foliage.Add(new(top - Vector3.Up * h * .04f, new Vector3(r * .16f, h * .08f, r * .16f), 2));
                plan.TrunkSolidHeight = h * .25f;
                break;
            }
            case FormColumnar:
            {
                // A cypress: a narrow column of stacked, overlapping masses around a straight leader.
                float r = Math.Clamp(crownRadius, h * .12f, h * .19f);
                var top = L(1) + Vector3.Up * h * .95f;
                AddBough(basePoint, L(.4f) + Vector3.Up * h * .5f, top, trunkRadius * .9f, trunkRadius * .15f);
                Roots(young ? 3 : 4, 2.4f);
                int masses = young ? 5 : 7;
                for (int i = 0; i < masses; i++)
                {
                    float t = i / (float)(masses - 1);
                    float level = h * (.2f + t * .72f);
                    float width = r * (.66f + MathF.Sin(t * MathF.PI) * .34f - t * .22f) * (.92f + R() * .14f);
                    var centre = L(level / h) + Vector3.Up * level + P((R() - .5f) * r * .2f, 0, (R() - .5f) * r * .2f);
                    Cluster(centre, new Vector3(width, h * .11f, width), i, young ? 10 : 14);
                }
                plan.TrunkSolidHeight = h * .2f;
                break;
            }
            case FormFan:
            {
                // A parasol: a short bole that forks into rising limbs carrying one broad, flat crown.
                float r = Math.Clamp(crownRadius * .72f, h * .32f, h * .5f);
                var fork = L(.5f) + Vector3.Up * h * (.46f + R() * .08f);
                AddBough(basePoint, L(.2f) + Vector3.Up * h * .25f, fork, trunkRadius * 1.05f, trunkRadius * .7f);
                Roots(young ? 4 : 6, 2.8f);
                int limbs = 3 + (int)(seed % 3);
                for (int limb = 0; limb < limbs; limb++)
                {
                    float a = limb * Mathf.Tau / limbs + (R() - .5f) * .6f;
                    float reach = r * (.62f + R() * .25f);
                    var tip = L(.9f) + new Vector3(MathF.Cos(a) * reach, h * (.80f + R() * .06f), MathF.Sin(a) * reach);
                    var bend = fork.Lerp(tip, .5f) + Vector3.Up * h * .06f;
                    AddBough(fork, bend, tip, trunkRadius * .55f, trunkRadius * .16f);
                    Cluster(tip + Vector3.Up * h * .03f, new Vector3(r * .46f, h * .085f, r * .42f), limb, young ? 16 : 22);
                }
                Cluster(L(.95f) + Vector3.Up * h * .9f, new Vector3(r * .5f, h * .08f, r * .5f), 2, young ? 16 : 24);
                plan.TrunkSolidHeight = h * .45f;
                break;
            }
            default:
            {
                // godot-flui's UnderstoryPlant.Tree / Sapling.
                float r = Math.Clamp(crownRadius * .62f, h * .22f, h * .38f);
                var fork = L(.55f) + Vector3.Up * h * (.39f + R() * .10f);
                var top = L(1) + Vector3.Up * h * .90f;
                AddBough(basePoint, L(-.25f) + Vector3.Up * h * .22f, fork, trunkRadius, trunkRadius * .57f);
                AddBough(fork, L(1.25f) + Vector3.Up * h * .72f, top, trunkRadius * .60f, MathF.Max(.03f, trunkRadius * .16f));
                Roots(young ? 4 : 6, 3.1f);
                int lobes = young ? 20 : 28;
                int branches = young ? 6 + (int)(seed % 3) : 8 + (int)(seed % 4);
                // An asymmetric inner crown joins the branch sprays into one growing canopy.
                Cluster(L(.8f) + Vector3.Up * h * .70f, new Vector3(r * .67f, h * .235f, r * .58f), 0, lobes);
                Cluster(L(1) + P(-r * .17f, h * .84f, r * .10f), new Vector3(r * .56f, h * .17f, r * .52f), 1, lobes);
                float forkY = fork.Y;
                for (int branch = 0; branch < branches; branch++)
                {
                    float fraction = branch / (float)(branches - 1), a = branch * 2.399963f + (R() - .5f) * .45f;
                    float level = h * (.35f + fraction * .40f), reach = r * (1.0f - fraction * .47f) * (.8f + R() * .2f);
                    var attach = level < forkY
                        ? basePoint.Lerp(fork, Math.Clamp(level / forkY, 0, 1))
                        : fork.Lerp(top, Math.Clamp((level - forkY) / (h * .9f - forkY), 0, 1));
                    var shoulder = L(level / h) + P(MathF.Cos(a) * reach * .60f, level + h * .04f, MathF.Sin(a) * reach * .60f);
                    var tip = L(level / h) + P(MathF.Cos(a) * reach, level + h * (.13f + R() * .05f), MathF.Sin(a) * reach);
                    float thickness = trunkRadius * (.44f - fraction * .20f);
                    AddBough(attach, shoulder - Vector3.Up * h * .035f, shoulder, thickness, thickness * .55f);
                    AddBough(shoulder, tip - Vector3.Up * h * .10f, tip, thickness * .57f, .02f);
                    // A young crown develops around ascending leaders; older crowns broaden and fork.
                    Cluster(tip, new Vector3(r * (young ? .39f : .36f), h * (young ? .13f : .10f), r * (.32f + R() * .07f)), branch, lobes / 2);
                    if (!young || branch % 2 == 0)
                        for (int twig = 0; twig < 2; twig++)
                        {
                            float side = a + (twig == 0 ? -.82f : .82f);
                            var end = tip + basis * new Vector3(MathF.Cos(side) * r * .32f, (R() - .15f) * h * .09f, MathF.Sin(side) * r * .32f);
                            AddBough(shoulder.Lerp(tip, .3f), end - Vector3.Up * h * .06f, end, thickness * .38f, .016f);
                            Cluster(end, new Vector3(r * .30f, h * .083f, r * .28f), branch + twig + 1, lobes / 3);
                        }
                }
                Cluster(top, new Vector3(r * .50f, h * .105f, r * .47f), 2, lobes / 2);
                plan.TrunkSolidHeight = forkY * .9f;
                break;
            }
        }
        return plan;
    }

    /// <summary>
    /// A bush: godot-flui's <c>UnderstoryPlant.Shrub</c> (arching stems, each with a crown and a secondary stem) or its
    /// flowering <c>Heath</c> cushion.
    /// </summary>
    public static PlantPlan Bush(float h, float r, int stems, bool heath, uint seed)
    {
        var random = new Random(unchecked((int)seed));
        float R() => (float)random.NextDouble();
        var plan = new PlantPlan { Height = h };
        var basis = new Basis(Vector3.Up, R() * Mathf.Tau);
        Vector3 P(float x, float y, float z) => basis * new Vector3(x, y, z);
        void Cluster(Vector3 at, Vector3 radius, int tint)
        {
            plan.Foliage.Add(new(at, radius, tint));
            int count = heath ? 14 : 18;
            for (int i = 0; i < count; i++)
            {
                float v = 1 - 2 * (i + .5f) / count, a = i * 2.399963f + R() * .25f;
                float radial = MathF.Sqrt(MathF.Max(0, 1 - v * v));
                var direction = new Vector3(MathF.Cos(a) * radial, v, MathF.Sin(a) * radial);
                var lobe = (radius * (.26f + R() * .13f)).Max(Vector3.One * .045f);
                plan.Foliage.Add(new(at + direction * radius * .85f, lobe, tint + i));
                if (i % 2 == 0) plan.Foliage.Add(new(at + direction * radius * 1.07f, lobe * .60f, tint + i + 1));
            }
        }
        if (heath) stems = Math.Max(3, stems - 1);
        // godot-flui's shrub, one change for this terrain's small bushes: the crowns ride a little higher on their
        // stems (tips at 0.6–0.95 of the height, masses 0.3 of it tall), so the arching stems show below them and a
        // bush does not read as a boulder on the meadow.
        for (int stem = 0; stem < stems; stem++)
        {
            float a = stem * 2.399963f + R() * .3f, spread = r * (.36f + R() * .31f), height = h * (.60f + R() * .35f);
            var root = P(MathF.Cos(a) * r * .10f, -.06f, MathF.Sin(a) * r * .10f);
            var tip = P(MathF.Cos(a) * spread, height, MathF.Sin(a) * spread);
            plan.Wood.Add(new(root, tip - Vector3.Up * height * .62f, tip, heath ? .02f : .025f + h * .012f, .012f));
            Cluster(tip, new Vector3(r * (heath ? .42f : .46f), h * (heath ? .30f : .28f), r * (.40f + R() * .10f)), stem);
            if (!heath)
            {
                var second = tip + P(MathF.Cos(a + .9f) * r * .35f, -height * .18f, MathF.Sin(a + .9f) * r * .35f);
                plan.Wood.Add(new(root.Lerp(tip, .55f), second - Vector3.Up * h * .15f, second, .018f, .01f));
                Cluster(second, new Vector3(r * .34f, h * .24f, r * .33f), stem + 2);
            }
        }
        return plan;
    }
}
