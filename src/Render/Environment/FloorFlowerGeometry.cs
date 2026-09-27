// Port of packages/client/src/render/environment/floorFlowerGeometry.ts — keep in lockstep with the original.
//
// PORT NOTES
// * Object spreads (`{ ...baseType, ...pigment }`, `{ ...individuality, stage: … }`) are Clone() + assignment.
//   The pigment spread also copies `ink` onto the type object in JS; nothing reads it there (the head ink comes
//   from `pigment?.ink`), so FloorFlowerType carries no ink field.
// * Site coordinates and seeds are doubles: `siteX * 92_837_111 + …` exceeds int32 and JS only truncates inside
//   cellHash (ToInt32).
// * The written scratch (WIND4, PETAL, DISC) is [ThreadStatic]: Godot compiles on several threads of one process.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Render.FloorGrassGeometry;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class FloorFlowerType
{
    public string key = "";
    /// <summary>`'ray' | 'cup' | 'star' | 'ruffle' | 'bell'`.</summary>
    public string form = "";
    public int petal;
    public int deep;
    public int centre;
    public int petals;
    public double height;
    public double radius;

    public FloorFlowerType Clone() => (FloorFlowerType)MemberwiseClone();
}

public sealed class FloorFlowerPigment
{
    public int petal;
    public int deep;
    public int centre;
    public int ink;
}

/// <summary>
/// Which authored flower forms a terrain theme may grow.
///
/// The normal Highland ground intentionally exposes exactly two species. Sakura uses those same two forms,
/// recoloured as one white and one black flower; locking the pigment keeps biome tinting from turning the
/// black flower violet or the white flower pink.
/// </summary>
public sealed class FloorFlowerThemeProfile
{
    public IReadOnlyList<int> typeIndices = Array.Empty<int>();
    public IReadOnlyList<FloorFlowerPigment>? pigments;
    public bool? lockPetalPigment;
}

/// <summary>Per-specimen morphology. Every value comes from the absolute world site and survives chunk rebakes.</summary>
public sealed class FloorFlowerIndividuality
{
    /// <summary>A real life-cycle state, not just a uniform scale of the same open head (`'bud' | 'opening' | 'mature' | 'weathered'`).</summary>
    public string stage = "";
    public double heightScale;
    public double headScale;
    public double openness;
    public double asymmetry;
    public double pigmentShift;
    public double petalJitter;
    public double leafScale;
    public double leafBias;
    public double fork;
    /// <summary>Slope and compass bearing of the flower's local head plane.</summary>
    public double headTilt;
    public double headTiltAngle;
    /// <summary>Patch-coherent colour strain plus plant-local wear.</summary>
    public double ecotype;
    public double weathering;
    /// <summary>Stable authored gap; only weathered heads actually expose it.</summary>
    public int missingPetal;

    public FloorFlowerIndividuality Clone() => (FloorFlowerIndividuality)MemberwiseClone();
}

public sealed class FloorFlowerPalette
{
    /// <summary>The same living-growth pole used by the turf and grass blades.</summary>
    public int lush;
    /// <summary>The floor's own light pole, used to seat bright petals in the biome lighting.</summary>
    public int lit;
    /// <summary>Terrain ink used for petal seams and centres, never a foreign pure black.</summary>
    public int ink;
}

public sealed class FloorFlowerOptions
{
    public FloorFlowerPalette palette;
    public FloorFlowerThemeProfile? profile;
    public double originX;
    public double originZ;
    public double tileSize;
    public double y0;
    public int cellX;
    public int cellY;
    /// <summary>Maximum complete plants emitted by this cell.</summary>
    public double flowerBudget;
    /// <summary>The floor-composition bloom field. Broad high values are patches; its shoulder yields single flowers.</summary>
    public Func<double, double, double> flowerCoverAt;
    /// <summary>The exact turf cover painted into the cap. A flower never roots beside the vegetation it belongs to.</summary>
    public Func<double, double, double> turfCoverAt;
    public Func<double, double, double> directionAt;
    public Func<double, double, int> matPigmentAt;
    public Func<double, double, double> groundLift;
}

public static partial class FloorFlowerGeometry
{
    /// <summary>
    /// Five authored wildflower silhouettes. Colour alone is not asked to carry the distinction: petal count,
    /// petal proportion, head construction and height all differ, so the species remain readable in greyscale.
    /// </summary>
    public static readonly IReadOnlyList<FloorFlowerType> FLOOR_FLOWER_TYPES = Array.AsReadOnly(new[]
    {
        new FloorFlowerType
        {
            key = "moon-daisy",
            form = "ray",
            petal = 0xf5f0dc,
            deep = 0xc9c0a8,
            centre = 0xe2ad32,
            petals = 7,
            height = 0.255,
            radius = 0.066,
        },
        new FloorFlowerType
        {
            key = "sun-buttercup",
            form = "cup",
            petal = 0xf2c33e,
            deep = 0xc98222,
            centre = 0x9d5a18,
            petals = 5,
            height = 0.205,
            radius = 0.061,
        },
        new FloorFlowerType
        {
            key = "blue-cornflower",
            form = "star",
            petal = 0x4d78cf,
            deep = 0x29478d,
            centre = 0x202f63,
            petals = 8,
            height = 0.29,
            radius = 0.061,
        },
        new FloorFlowerType
        {
            key = "scarlet-poppy",
            form = "ruffle",
            petal = 0xdf594b,
            deep = 0x9e2f38,
            centre = 0x352a35,
            petals = 4,
            height = 0.235,
            radius = 0.077,
        },
        new FloorFlowerType
        {
            key = "violet-bellflower",
            form = "bell",
            petal = 0x8f72c9,
            deep = 0x59488f,
            centre = 0xe3bd58,
            petals = 5,
            height = 0.31,
            radius = 0.058,
        },
    });

    private static readonly FloorFlowerThemeProfile ALL_FLOOR_FLOWERS = new FloorFlowerThemeProfile
    {
        typeIndices = Array.AsReadOnly(FLOOR_FLOWER_TYPES.map((_type, index) => index).ToArray()),
    };

    private static readonly FloorFlowerThemeProfile NORMAL_FLOOR_FLOWERS = new FloorFlowerThemeProfile
    {
        // Daisy + poppy are the most distinct pair in silhouette, even when viewed without colour.
        typeIndices = Array.AsReadOnly(new[] { 0, 3 }),
    };

    private static readonly FloorFlowerThemeProfile SAKURA_FLOOR_FLOWERS = new FloorFlowerThemeProfile
    {
        typeIndices = NORMAL_FLOOR_FLOWERS.typeIndices,
        pigments = Array.AsReadOnly(new[]
        {
            new FloorFlowerPigment { petal = 0xfffdf3, deep = 0xd8d6cf, centre = 0x080808, ink = 0x080808 },
            new FloorFlowerPigment { petal = 0x080808, deep = 0x000000, centre = 0xfffdf3, ink = 0xfffdf3 },
        }),
        lockPetalPigment = true,
    };

    public static FloorFlowerThemeProfile floorFlowerProfileForBiome(string? biomeKey)
    {
        if (biomeKey == "highland_pass") return NORMAL_FLOOR_FLOWERS;
        if (biomeKey == "sakura_temple_dream") return SAKURA_FLOOR_FLOWERS;
        return ALL_FLOOR_FLOWERS;
    }

    private static readonly double SURF = TERRAIN_SURFACE_PATTERN.floor;
    /// <summary>Never written: shared.</summary>
    private static readonly double[] SHADE4 = { 0.9, 0.98, 1.08, 0.98 };
    [ThreadStatic] private static double[]? _WIND4;
    private static double[] WIND4 => _WIND4 ??= new double[] { 0, 0, 0, 0 };
    [ThreadStatic] private static P3[]? _PETAL;
    private static P3[] PETAL => _PETAL ??= new P3[]
    {
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
    };
    [ThreadStatic] private static List<P3>? _DISC;
    private static List<P3> DISC => _DISC ??= newDisc();

    private static List<P3> newDisc()
    {
        var disc = new List<P3>(10);
        for (int i = 0; i < 10; i++) disc.Add(new P3 { x = 0, y = 0, z = 0 });
        return disc;
    }

    private static double[] setWind4(double value)
    {
        // The builder copies synchronously; one reusable tuple keeps dense flower patches allocation-free.
        double[] wind4 = WIND4;
        wind4[0] = wind4[1] = wind4[2] = wind4[3] = value;
        return wind4;
    }

    /// <summary>
    /// Stable macro species selection. Sites in the same loose 3x3-cell neighbourhood tend to agree, which turns a
    /// dense bloom core into a botanical patch instead of five-colour confetti. A restrained companion-species
    /// draw breaks up cloned monocultures without destroying that dominant local identity.
    /// </summary>
    /// <param name="typeCount">Defaults to FLOOR_FLOWER_TYPES.length (null = the TS default parameter).</param>
    public static int floorFlowerTypeAt(
        double worldSiteX,
        double worldSiteY,
        double? typeCount = null)
    {
        double typeCountValue = typeCount ?? FLOOR_FLOWER_TYPES.Count;
        double count = Math.max(1, Math.min(FLOOR_FLOWER_TYPES.Count, Math.trunc(typeCountValue)));
        double regionX = Math.floor((worldSiteX + cellHash(worldSiteY, 173) * 4) / 9);
        double regionY = Math.floor((worldSiteY + cellHash(worldSiteX, -211) * 4) / 9);
        double dominant = Math.floor(cellHash(regionX * 43 + 17, regionY * 59 - 31) * count);
        double companionDraw = cellHash(worldSiteX * 347 + 23, worldSiteY * 359 - 41);
        if (companionDraw < 0.84 || count == 1) return (int)dominant;
        double companionOffset =
            1 + Math.floor(cellHash(worldSiteX * 367 - 53, worldSiteY * 373 + 61) * (count - 1));
        return (int)((dominant + companionOffset) % count);
    }

    /// <summary>Stable individuality for one plant; no two accepted world sites share an authored clone transform.</summary>
    public static FloorFlowerIndividuality floorFlowerIndividualityAt(
        double worldSiteX,
        double worldSiteY)
    {
        double seedX = worldSiteX * 1_009 + worldSiteY * 313;
        double seedY = worldSiteY * 1_013 - worldSiteX * 317;
        double lifeRoll = cellHash(seedX + 137, seedY - 139);
        string stage =
            lifeRoll < 0.09
                ? "bud"
                : lifeRoll < 0.23
                    ? "opening"
                    : lifeRoll < 0.82
                        ? "mature"
                        : "weathered";
        // Ecotypes change slowly across the world, so a patch shares a believable local strain without becoming a
        // flat mono-colour stamp. Fine pigmentShift below remains unique to the individual.
        double strainX = Math.floor((worldSiteX + cellHash(worldSiteY, 271) * 5) / 11);
        double strainY = Math.floor((worldSiteY + cellHash(worldSiteX, -277) * 5) / 11);
        return new FloorFlowerIndividuality
        {
            stage = stage,
            heightScale = 0.76 + cellHash(seedX + 11, seedY - 7) * 0.48,
            headScale = 0.8 + cellHash(seedX + 29, seedY + 31) * 0.42,
            openness = cellHash(seedX - 43, seedY + 47),
            asymmetry = cellHash(seedX + 59, seedY - 61) * 2 - 1,
            pigmentShift = cellHash(seedX - 71, seedY + 73) * 2 - 1,
            petalJitter = cellHash(seedX + 89, seedY + 97),
            leafScale = 0.78 + cellHash(seedX - 101, seedY - 103) * 0.48,
            leafBias = cellHash(seedX + 107, seedY - 109),
            fork = cellHash(seedX - 127, seedY + 131),
            headTilt = 0.04 + cellHash(seedX + 149, seedY - 151) * 0.3,
            headTiltAngle = cellHash(seedX - 157, seedY + 163) * PROP_TAU,
            ecotype = cellHash(strainX * 281 + 17, strainY * 293 - 19) * 2 - 1,
            weathering = cellHash(seedX + 167, seedY - 173),
            missingPetal = (int)Math.floor(cellHash(seedX - 179, seedY + 181) * 11),
        };
    }

    private sealed class FlowerHeadPose
    {
        public double tilt;
        public double angle;
    }

    private static void addPetal(
        PropGeometryBuilder builder,
        double cx,
        double cy,
        double cz,
        double angle,
        double length,
        double halfWidth,
        double lift,
        int color,
        double wind,
        FlowerHeadPose? pose = null)
    {
        double dx = Math.cos(angle);
        double dz = Math.sin(angle);
        double tx = -dz;
        double tz = dx;
        double planeSlope = pose != null ? Math.cos(angle - pose.angle) * pose.tilt : 0;
        P3[] petal = PETAL;
        petal[0].x = cx - tx * halfWidth * 0.22;
        petal[0].y = cy;
        petal[0].z = cz - tz * halfWidth * 0.22;
        petal[1].x = cx + dx * length * 0.56 - tx * halfWidth;
        petal[1].y = cy + length * 0.56 * planeSlope + lift * 0.46;
        petal[1].z = cz + dz * length * 0.56 - tz * halfWidth;
        petal[2].x = cx + dx * length;
        petal[2].y = cy + length * planeSlope + lift;
        petal[2].z = cz + dz * length;
        petal[3].x = cx + dx * length * 0.56 + tx * halfWidth;
        petal[3].y = cy + length * 0.56 * planeSlope + lift * 0.46;
        petal[3].z = cz + dz * length * 0.56 + tz * halfWidth;
        builder.addSurface(
            petal,
            pose != null ? -Math.cos(pose.angle) * pose.tilt : -dx * lift * 0.08,
            0.96,
            pose != null ? -Math.sin(pose.angle) * pose.tilt : -dz * lift * 0.08,
            color,
            SURF,
            0.04,
            SHADE4,
            setWind4(wind));
    }

    private static void addDisc(
        PropGeometryBuilder builder,
        double cx,
        double cy,
        double cz,
        double radius,
        int sides,
        double rotation,
        int color,
        double wind,
        FlowerHeadPose? pose = null)
    {
        List<P3> disc = DISC;
        while (disc.Count < sides) disc.push(new P3 { x = 0, y = 0, z = 0 });
        if (disc.Count > sides) disc.RemoveRange(sides, disc.Count - sides);
        for (int i = 0; i < sides; i++)
        {
            double angle = rotation + ((double)i / sides) * PROP_TAU;
            P3 point = disc[i];
            point.x = cx + Math.cos(angle) * radius;
            point.y = cy + (pose != null ? Math.cos(angle - pose.angle) * radius * pose.tilt : 0);
            point.z = cz + Math.sin(angle) * radius;
        }
        builder.addSurface(
            disc,
            pose != null ? -Math.cos(pose.angle) * pose.tilt : 0,
            1,
            pose != null ? -Math.sin(pose.angle) * pose.tilt : 0,
            color,
            SURF,
            0.04,
            null,
            wind);
    }

    private static void addOutlinedPetal(
        PropGeometryBuilder builder,
        double cx,
        double cy,
        double cz,
        double angle,
        double length,
        double halfWidth,
        double lift,
        int color,
        int ink,
        double wind,
        FlowerHeadPose? pose = null)
    {
        addPetal(builder, cx, cy, cz, angle, length * 1.075, halfWidth * 1.14, lift, ink, wind, pose);
        addPetal(builder, cx, cy + 0.055, cz, angle, length, halfWidth, lift, color, wind, pose);
    }

    private static double petalRoll(double seed, double petal, double channel)
    {
        return cellHash(seed + petal * 149 + channel * 41, seed - petal * 193 - channel * 67);
    }

    private static FlowerHeadPose flowerHeadPose(FloorFlowerIndividuality individuality)
    {
        return new FlowerHeadPose
        {
            tilt =
                individuality.headTilt *
                (individuality.stage == "opening" ? 1.18 : individuality.stage == "weathered" ? 1.1 : 1),
            angle = individuality.headTiltAngle,
        };
    }

    private static bool omitsWeatheredPetal(
        FloorFlowerIndividuality individuality,
        int petal,
        int count)
    {
        return
            individuality.stage == "weathered" &&
            individuality.weathering > 0.24 &&
            individuality.missingPetal % count == petal;
    }

    private static double flowerOpening(FloorFlowerIndividuality individuality)
    {
        if (individuality.stage == "opening") return 0.38 + individuality.openness * 0.28;
        if (individuality.stage == "weathered") return 0.88 + individuality.openness * 0.12;
        return 0.76 + individuality.openness * 0.24;
    }

    /// <summary>Slender uneven rays around a raised, seeded dome.</summary>
    private static void addDaisyHead(
        PropGeometryBuilder builder,
        FloorFlowerType type,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int ink,
        double wind,
        double seed)
    {
        int count = type.petals - 1 + (int)Math.floor(individuality.petalJitter * 3);
        FlowerHeadPose pose = flowerHeadPose(individuality);
        double opening = flowerOpening(individuality);
        for (int petal = 0; petal < count; petal++)
        {
            if (omitsWeatheredPetal(individuality, petal, count)) continue;
            double spacing = ((double)petal / count) * PROP_TAU;
            double angle =
                rotation +
                spacing +
                (petalRoll(seed, petal, 0) - 0.5) * (0.13 + individuality.asymmetry * 0.025);
            double length = radius * (0.78 + opening * 0.22) * (0.92 + petalRoll(seed, petal, 1) * 0.22);
            double width = radius * (0.17 + petalRoll(seed, petal, 2) * 0.055);
            double lift =
                radius *
                (0.1 +
                    individuality.openness * 0.12 +
                    (1 - opening) * 0.38 +
                    petalRoll(seed, petal, 3) * 0.05);
            int color = mix(type.petal, type.deep, petalRoll(seed, petal, 4) * 0.12);
            addOutlinedPetal(builder, cx, cy, cz, angle, length, width, lift, color, ink, wind, pose);
        }
        propFrustum(
            builder,
            cx,
            cz,
            cy + radius * 0.04,
            cy + radius * 0.19,
            radius * 0.23,
            radius * 0.15,
            8,
            rotation,
            mix(type.centre, type.deep, 0.12),
            type.centre,
            0.82,
            wind,
            wind);
        addDisc(
            builder,
            cx,
            cy + radius * 0.195,
            cz,
            radius * 0.145,
            8,
            rotation,
            type.centre,
            wind,
            pose);
    }

    /// <summary>Five overlapping heart-like petals climbing into a glossy shallow cup.</summary>
    private static void addButtercupHead(
        PropGeometryBuilder builder,
        FloorFlowerType type,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int ink,
        double wind,
        double seed)
    {
        int count = individuality.petalJitter > 0.82 ? 6 : type.petals;
        FlowerHeadPose pose = flowerHeadPose(individuality);
        double opening = flowerOpening(individuality);
        for (int petal = 0; petal < count; petal++)
        {
            if (omitsWeatheredPetal(individuality, petal, count)) continue;
            double angle =
                rotation +
                ((double)petal / count) * PROP_TAU +
                (petalRoll(seed, petal, 0) - 0.5) * 0.1 * individuality.openness;
            double length = radius * (0.72 + opening * 0.16) * (0.8 + petalRoll(seed, petal, 1) * 0.13);
            double width = radius * (0.32 + petalRoll(seed, petal, 2) * 0.06);
            double lift = radius * (0.19 + (1 - opening) * 0.54);
            addOutlinedPetal(
                builder,
                cx,
                cy,
                cz,
                angle,
                length,
                width,
                lift,
                mix(type.petal, type.deep, petalRoll(seed, petal, 3) * 0.09),
                ink,
                wind,
                pose);
            // The offset inner lobe pinches each broad petal into the buttercup's heart silhouette.
            addPetal(
                builder,
                cx,
                cy + radius * 0.035,
                cz,
                angle + (petalRoll(seed, petal, 4) - 0.5) * 0.13,
                length * 0.72,
                width * 0.7,
                lift * 1.06,
                mix(type.petal, type.centre, 0.1),
                wind,
                pose);
        }
        propFrustum(
            builder,
            cx,
            cz,
            cy + radius * 0.13,
            cy + radius * 0.3,
            radius * 0.19,
            radius * 0.12,
            7,
            rotation,
            type.deep,
            type.centre,
            0.78,
            wind,
            wind);
    }

    /// <summary>A forked outer star and a smaller ragged inner whorl make the cornflower readable at distance.</summary>
    private static void addCornflowerHead(
        PropGeometryBuilder builder,
        FloorFlowerType type,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int ink,
        double wind,
        double seed)
    {
        int count = type.petals - 1 + (int)Math.floor(individuality.petalJitter * 3);
        FlowerHeadPose pose = flowerHeadPose(individuality);
        double opening = flowerOpening(individuality);
        for (int petal = 0; petal < count; petal++)
        {
            if (omitsWeatheredPetal(individuality, petal, count)) continue;
            double angle = rotation + ((double)petal / count) * PROP_TAU + (petalRoll(seed, petal, 0) - 0.5) * 0.11;
            double length = radius * (0.78 + opening * 0.22) * (0.92 + petalRoll(seed, petal, 1) * 0.2);
            int color = mix(type.petal, type.deep, petalRoll(seed, petal, 2) * 0.24);
            // Twin narrow prongs create the characteristic split tips instead of a generic pointed petal.
            foreach (int forkSide in FORK_SIDES)
            {
                addOutlinedPetal(
                    builder,
                    cx,
                    cy,
                    cz,
                    angle + forkSide * (0.055 + individuality.openness * 0.025),
                    length * (0.94 + forkSide * individuality.asymmetry * 0.025),
                    radius * 0.105,
                    radius * (0.12 + individuality.openness * 0.1),
                    color,
                    ink,
                    wind,
                    pose);
            }
            addPetal(
                builder,
                cx,
                cy + radius * 0.045,
                cz,
                angle + PROP_TAU / (count * 2),
                radius * (0.54 + petalRoll(seed, petal, 3) * 0.12),
                radius * 0.12,
                radius * 0.24,
                mix(type.petal, type.deep, 0.32),
                wind,
                pose);
        }
        addDisc(builder, cx, cy + radius * 0.17, cz, radius * 0.18, 9, rotation, type.centre, wind, pose);
    }

    /// <summary>`for (const forkSide of [-1, 1])`.</summary>
    private static readonly int[] FORK_SIDES = { -1, 1 };

    /// <summary>Four large offset sheets overlap into a loose, asymmetric poppy ruffle.</summary>
    private static void addPoppyHead(
        PropGeometryBuilder builder,
        FloorFlowerType type,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int ink,
        double wind,
        double seed)
    {
        int count = individuality.petalJitter > 0.88 ? 5 : type.petals;
        FlowerHeadPose pose = flowerHeadPose(individuality);
        double opening = flowerOpening(individuality);
        for (int petal = 0; petal < count; petal++)
        {
            if (omitsWeatheredPetal(individuality, petal, count)) continue;
            double angle =
                rotation +
                ((double)petal / count) * PROP_TAU +
                individuality.asymmetry * (petal % 2 == 0 ? 0.08 : -0.05) +
                (petalRoll(seed, petal, 0) - 0.5) * 0.16;
            double length = radius * (0.78 + opening * 0.22) * (0.82 + petalRoll(seed, petal, 1) * 0.22);
            double width = radius * (0.43 + petalRoll(seed, petal, 2) * 0.11);
            double lift = radius * (0.05 + (1 - opening) * 0.36 + petalRoll(seed, petal, 3) * 0.1);
            addOutlinedPetal(
                builder,
                cx,
                cy + (petal % 2) * radius * 0.025,
                cz,
                angle,
                length,
                width,
                lift,
                mix(type.petal, type.deep, petalRoll(seed, petal, 4) * 0.18),
                ink,
                wind,
                pose);
            addPetal(
                builder,
                cx,
                cy + radius * 0.065,
                cz,
                angle + 0.08 * (petal % 2 == 0 ? 1 : -1),
                length * 0.78,
                width * 0.7,
                lift + radius * 0.045,
                mix(type.petal, type.deep, 0.08),
                wind,
                pose);
        }
        propFrustum(
            builder,
            cx,
            cz,
            cy + radius * 0.055,
            cy + radius * 0.18,
            radius * 0.24,
            radius * 0.19,
            9,
            rotation,
            mix(type.centre, type.deep, 0.16),
            type.centre,
            0.84,
            wind,
            wind);
        for (int stamen = 0; stamen < 6; stamen++)
        {
            double angle = rotation + ((double)stamen / 6) * PROP_TAU;
            addPetal(
                builder,
                cx,
                cy + radius * 0.18,
                cz,
                angle,
                radius * 0.31,
                radius * 0.035,
                radius * 0.1,
                mix(type.centre, type.deep, stamen % 2 == 0 ? 0.12 : 0.32),
                wind,
                pose);
        }
    }

    /// <summary>A genuinely drooping five-lobed bell, not the radial daisy grammar recoloured violet.</summary>
    private static void addBellHead(
        PropGeometryBuilder builder,
        FloorFlowerType type,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int ink,
        double wind)
    {
        double opening = flowerOpening(individuality);
        double mouth = 0.58 + opening * 0.36;
        propFrustum(
            builder,
            cx,
            cz,
            cy - radius * 0.95,
            cy,
            radius * mouth,
            radius * 0.34,
            5,
            rotation,
            mix(type.petal, type.deep, 0.2),
            type.deep,
            0.74,
            wind,
            wind);
        // Five individually displaced lips complete the bell instead of repeating a camera-facing three-card icon.
        for (int lobe = 0; lobe < type.petals; lobe++)
        {
            if (omitsWeatheredPetal(individuality, lobe, type.petals)) continue;
            double angle =
                rotation +
                ((double)lobe / type.petals) * PROP_TAU +
                individuality.asymmetry * (lobe - (type.petals - 1) * 0.5) * 0.025;
            addPetal(
                builder,
                cx,
                cy - radius * 0.9,
                cz,
                angle,
                radius * (0.34 + opening * 0.17),
                radius * (0.15 + petalRoll(Math.round(rotation * 10_000), lobe, 1) * 0.05),
                -radius * (0.22 + opening * 0.18),
                mix(type.petal, ink, petalRoll(Math.round(rotation * 10_000), lobe, 2) * 0.09),
                wind);
        }
        propFrustum(
            builder,
            cx,
            cz + radius * 0.12,
            cy - radius * 1.32,
            cy - radius * 0.86,
            radius * 0.09,
            radius * 0.12,
            5,
            rotation,
            type.centre,
            mix(type.centre, ink, 0.3),
            0.8,
            wind,
            wind);
    }

    private static void addFlowerCalyx(
        PropGeometryBuilder builder,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int stem,
        int ink,
        double wind)
    {
        FlowerHeadPose pose = flowerHeadPose(individuality);
        propFrustum(
            builder,
            cx,
            cz,
            cy - radius * 0.24,
            cy + radius * 0.025,
            radius * 0.25,
            radius * 0.12,
            5,
            rotation,
            mix(stem, ink, 0.16),
            stem,
            0.7,
            wind * 0.88,
            wind);
        for (int sepal = 0; sepal < 3; sepal++)
        {
            double angle = rotation + ((double)sepal / 3) * PROP_TAU + individuality.asymmetry * 0.08;
            addPetal(
                builder,
                cx,
                cy - radius * 0.08,
                cz,
                angle,
                radius * (0.38 + sepal * 0.045),
                radius * 0.075,
                -radius * (0.16 + sepal * 0.025),
                sepal == 1 ? mix(stem, ink, 0.08) : stem,
                wind * 0.9,
                pose);
        }
    }

    /// <summary>Closed heads are authored geometry in their own right, not mature flowers shrunk to a dot.</summary>
    private static void addFlowerBud(
        PropGeometryBuilder builder,
        FloorFlowerType type,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int stem,
        int ink,
        double wind)
    {
        bool bell = type.form == "bell";
        int sides = Math.max(4, type.petals);
        double budRadius = radius * (0.5 + individuality.openness * 0.08);
        addFlowerCalyx(builder, individuality, cx, cy, cz, budRadius, rotation, stem, ink, wind);
        propFrustum(
            builder,
            cx,
            cz,
            bell ? cy - budRadius * 0.72 : cy,
            bell ? cy : cy + budRadius * 0.92,
            bell ? budRadius * 0.42 : budRadius * 0.5,
            bell ? budRadius * 0.22 : budRadius * 0.1,
            sides,
            rotation,
            mix(type.petal, type.deep, 0.38),
            mix(type.petal, type.deep, 0.16),
            0.78,
            wind * 0.92,
            wind);
    }

    private static void addAuthoredFlowerHead(
        PropGeometryBuilder builder,
        FloorFlowerType type,
        FloorFlowerIndividuality individuality,
        double cx,
        double cy,
        double cz,
        double radius,
        double rotation,
        int stem,
        int ink,
        double wind,
        double seed)
    {
        if (individuality.stage == "bud")
        {
            addFlowerBud(builder, type, individuality, cx, cy, cz, radius, rotation, stem, ink, wind);
            return;
        }
        if (type.form != "bell")
            addFlowerCalyx(builder, individuality, cx, cy, cz, radius, rotation, stem, ink, wind);
        switch (type.form)
        {
            case "ray":
                addDaisyHead(builder, type, individuality, cx, cy, cz, radius, rotation, ink, wind, seed);
                break;
            case "cup":
                addButtercupHead(builder, type, individuality, cx, cy, cz, radius, rotation, ink, wind, seed);
                break;
            case "star":
                addCornflowerHead(
                    builder,
                    type,
                    individuality,
                    cx,
                    cy,
                    cz,
                    radius,
                    rotation,
                    ink,
                    wind,
                    seed);
                break;
            case "ruffle":
                addPoppyHead(builder, type, individuality, cx, cy, cz, radius, rotation, ink, wind, seed);
                break;
            case "bell":
                addBellHead(builder, type, individuality, cx, cy, cz, radius, rotation, ink, wind);
                break;
        }
    }

    // Each species owns a leaf rhythm as well as a flower head: basal daisy rosette, compact buttercup,
    // narrow alternating cornflower, broad poppy pair, and the bellflower's climbing leaves.
    // (Hoisted out of addFlower; an index past a table is `undefined` in JS, read here as NaN.)
    private static readonly double[] LEAF_COUNTS = { 3, 2, 3, 2, 3 };
    private static readonly double[] LEAF_LENGTHS = { 0.13, 0.16, 0.2, 0.18, 0.17 };
    private static readonly double[] LEAF_WIDTHS = { 1.15, 1.04, 0.66, 1.3, 0.86 };
    private static readonly double[] BRANCH_THRESHOLDS = { 0.7, 0.8, 0.56, 0.86, 0.52 };

    private static double tableAt(double[] table, int index) =>
        (uint)index < (uint)table.Length ? table[index] : double.NaN;

    private static void addFlower(
        PropGeometryBuilder builder,
        FloorFlowerOptions options,
        double rootX,
        double rootZ,
        double y0,
        double u,
        double v,
        double bloom,
        double siteX,
        double siteY,
        double seed)
    {
        FloorFlowerThemeProfile profile = options.profile ?? ALL_FLOOR_FLOWERS;
        int profileIndex = floorFlowerTypeAt(siteX, siteY, profile.typeIndices.Count);
        int typeIndex = (uint)profileIndex < (uint)profile.typeIndices.Count ? profile.typeIndices[profileIndex] : 0;
        FloorFlowerType baseType =
            (uint)typeIndex < (uint)FLOOR_FLOWER_TYPES.Count ? FLOOR_FLOWER_TYPES[typeIndex] : FLOOR_FLOWER_TYPES[0];
        FloorFlowerPigment? pigment =
            profile.pigments != null && (uint)profileIndex < (uint)profile.pigments.Count
                ? profile.pigments[profileIndex]
                : null;
        FloorFlowerType authored = baseType;
        if (pigment != null)
        {
            authored = baseType.Clone();
            authored.petal = pigment.petal;
            authored.deep = pigment.deep;
            authored.centre = pigment.centre;
        }
        FloorFlowerIndividuality individuality = floorFlowerIndividualityAt(siteX, siteY);
        double roll = cellHash(seed * 31 + 7, seed * 47 - 13);
        double rollB = cellHash(seed * 61 - 17, seed * 73 + 29);
        double stageHeight =
            individuality.stage == "bud"
                ? 0.76
                : individuality.stage == "opening"
                    ? 0.9
                    : individuality.stage == "weathered"
                        ? 0.95
                        : 1;
        double scale = individuality.heightScale * (0.94 + bloom * 0.1) * stageHeight;
        double height = options.tileSize * authored.height * scale;
        double radius = options.tileSize * authored.radius * individuality.headScale;
        double windAngle =
            options.directionAt(u, v) + (rollB - 0.5) * 0.46 + individuality.asymmetry * 0.11;
        double lean = height * (0.07 + roll * 0.07 + Math.abs(individuality.asymmetry) * 0.025);
        double tipX = rootX + Math.cos(windAngle) * lean;
        double tipZ = rootZ + Math.sin(windAngle) * lean;
        int mat = options.matPigmentAt(u, v);
        int livingStem = mix(mix(mat, options.palette.lush, 0.58), options.palette.lit, 0.14);
        int stem =
            individuality.stage == "weathered"
                ? mix(livingStem, mat, 0.08 + individuality.weathering * 0.16)
                : livingStem;
        // The head's pigments (pure: computed before the stem so the comic look can record the whole plant).
        int flowerPetal = authored.petal;
        if (profile.lockPetalPigment != true)
        {
            int strainTarget = individuality.ecotype >= 0 ? options.palette.lit : authored.deep;
            int strainPetal = mix(
                authored.petal,
                strainTarget,
                0.025 + Math.abs(individuality.ecotype) * 0.105);
            int pigmentTarget = individuality.pigmentShift >= 0 ? options.palette.lit : authored.deep;
            flowerPetal = mix(
                strainPetal,
                pigmentTarget,
                0.045 + Math.abs(individuality.pigmentShift) * 0.11);
            if (individuality.stage == "weathered")
                flowerPetal = mix(
                    flowerPetal,
                    individuality.pigmentShift > 0 ? options.palette.lit : authored.deep,
                    0.06 + individuality.weathering * 0.13);
        }
        FloorFlowerType flowerType = authored.Clone();
        flowerType.petal = flowerPetal;
        flowerType.deep = profile.lockPetalPigment == true
            ? authored.deep
            : mix(authored.deep, flowerPetal, 0.06 + individuality.openness * 0.08);
        int ink = pigment?.ink ?? mix(options.palette.ink, authored.deep, 0.18);
        double rotation = rollB * PROP_TAU;
        // Fluitown comic look: the flower grows in the vegetation layer (FluitownFlowers).
        if (FluitownFlowers.record(builder, rootX, y0, rootZ, height, radius, windAngle, lean, rotation, typeIndex, siteX, siteY,
                stem, flowerType.petal, flowerType.deep, authored.centre))
            return;
        double wind = 1.35 + bloom * 0.72 + rollB * 0.38;
        addTerrainWindBlade(builder, new WindBladeParams
        {
            rootX = rootX,
            rootZ = rootZ,
            y0 = y0,
            height = height,
            width = 0.5 + scale * 0.26,
            angle = windAngle + Math.PI * 0.5,
            leanX = tipX - rootX,
            leanZ = tipZ - rootZ,
            color = stem,
            wind = wind,
            bend = 0.57,
            normalY = 0.76,
        });

        // Each species owns a leaf rhythm as well as a flower head: basal daisy rosette, compact buttercup,
        // narrow alternating cornflower, broad poppy pair, and the bellflower's climbing leaves.
        double leafCount = Math.max(
            1,
            tableAt(LEAF_COUNTS, typeIndex) +
                (individuality.leafBias > 0.78 ? 1 : 0) -
                (individuality.stage == "weathered" && individuality.weathering > 0.72 ? 1 : 0));
        for (int leaf = 0; leaf < leafCount; leaf++)
        {
            double leafRoll = petalRoll(seed + 4_099, leaf, 0);
            double leafRollB = petalRoll(seed - 8_191, leaf, 1);
            int side = (leaf + (individuality.leafBias > 0.5 ? 1 : 0)) % 2 == 0 ? -1 : 1;
            bool basal = typeIndex <= 1;
            double leafAngle = basal
                ? windAngle + individuality.leafBias * PROP_TAU + leaf * 2.399963 + (leafRoll - 0.5) * 0.28
                : windAngle +
                    side * (0.82 + leaf * 0.18 + individuality.leafBias * 0.22) +
                    (leafRoll - 0.5) * 0.24;
            double leafHeight =
                height *
                tableAt(LEAF_LENGTHS, typeIndex) *
                individuality.leafScale *
                (0.82 + leafRollB * 0.28 + leaf * 0.045);
            double stemFraction = basal
                ? 0.055 + leafRoll * 0.075
                : 0.14 + ((leaf + 1) / (leafCount + 1)) * 0.56 + (leafRoll - 0.5) * 0.045;
            double leafWidth = tableAt(LEAF_WIDTHS, typeIndex) * individuality.leafScale * (0.82 + leafRoll * 0.34);
            double leafRootX = rootX + (tipX - rootX) * stemFraction;
            double leafRootZ = rootZ + (tipZ - rootZ) * stemFraction;
            double leafY = y0 + height * stemFraction;
            double leafLean = leafHeight * (0.58 + leafRollB * 0.24);
            int leafColor =
                leaf % 3 == 0 ? stem : mix(stem, options.palette.lit, 0.045 + leafRoll * 0.075);
            addTerrainWindBlade(builder, new WindBladeParams
            {
                rootX = leafRootX,
                rootZ = leafRootZ,
                y0 = leafY,
                height = leafHeight,
                width = leafWidth,
                angle = leafAngle + Math.PI * 0.5,
                leanX = Math.cos(leafAngle) * leafLean,
                leanZ = Math.sin(leafAngle) * leafLean,
                color = leafColor,
                wind = wind * (0.62 + leaf * 0.08),
                bend = 0.48 + leafRoll * 0.12,
                normalY = 0.82,
            });
            // Broad leaves receive one narrow crossed ribbon so they retain a silhouette at oblique camera angles.
            if ((typeIndex == 0 || typeIndex == 3) && leaf % 2 == 0)
            {
                addTerrainWindBlade(builder, new WindBladeParams
                {
                    rootX = leafRootX,
                    rootZ = leafRootZ,
                    y0 = leafY + 0.006,
                    height = leafHeight * 0.92,
                    width = leafWidth * 0.46,
                    angle = leafAngle,
                    leanX = Math.cos(leafAngle) * leafLean * 0.92,
                    leanZ = Math.sin(leafAngle) * leafLean * 0.92,
                    color = mix(leafColor, stem, 0.12),
                    wind = wind * (0.6 + leaf * 0.075),
                    bend = 0.5 + leafRollB * 0.1,
                    normalY = 0.84,
                });
            }
        }

        addAuthoredFlowerHead(
            builder,
            flowerType,
            individuality,
            tipX,
            y0 + height,
            tipZ,
            radius,
            rotation,
            stem,
            ink,
            wind,
            seed);

        double threshold = tableAt(BRANCH_THRESHOLDS, typeIndex);
        int branchCount =
            individuality.stage == "bud" || individuality.fork <= threshold
                ? 0
                : individuality.fork > 0.92 && (typeIndex == 2 || typeIndex == 4)
                    ? 2
                    : 1;
        for (int branch = 0; branch < branchCount; branch++)
        {
            int branchSide = (individuality.asymmetry < 0 ? -1 : 1) * (branch % 2 == 0 ? 1 : -1);
            double branchAngle =
                windAngle +
                branchSide * (0.68 + individuality.openness * 0.38) +
                (petalRoll(seed, branch, 8) - 0.5) * 0.2;
            double branchFraction = 0.42 + branch * 0.11 + petalRoll(seed, branch, 9) * 0.055;
            double branchHeight = height * (0.2 + individuality.fork * 0.105) * (branch == 0 ? 1 : 0.82);
            double branchRootX = rootX + (tipX - rootX) * branchFraction;
            double branchRootZ = rootZ + (tipZ - rootZ) * branchFraction;
            double branchY = y0 + height * branchFraction;
            double branchLean = branchHeight * (0.68 + individuality.asymmetry * branchSide * 0.06);
            double branchTipX = branchRootX + Math.cos(branchAngle) * branchLean;
            double branchTipZ = branchRootZ + Math.sin(branchAngle) * branchLean;
            addTerrainWindBlade(builder, new WindBladeParams
            {
                rootX = branchRootX,
                rootZ = branchRootZ,
                y0 = branchY,
                height = branchHeight,
                width = 0.44 + individuality.headScale * 0.08,
                angle = branchAngle + Math.PI * 0.5,
                leanX = branchTipX - branchRootX,
                leanZ = branchTipZ - branchRootZ,
                color = stem,
                wind = wind * (0.82 + branch * 0.04),
                bend = 0.48 + branch * 0.04,
                normalY = 0.78,
            });
            FloorFlowerIndividuality secondaryIndividuality = individuality.Clone();
            secondaryIndividuality.stage = individuality.fork > 0.84 && branch == 0 ? "opening" : "bud";
            secondaryIndividuality.headScale = individuality.headScale * (0.9 + petalRoll(seed, branch, 10) * 0.14);
            secondaryIndividuality.openness = petalRoll(seed, branch, 11);
            secondaryIndividuality.headTilt = individuality.headTilt * (0.86 + branch * 0.08);
            secondaryIndividuality.headTiltAngle = branchAngle;
            secondaryIndividuality.missingPetal = (individuality.missingPetal + branch + 3) % 11;
            addAuthoredFlowerHead(
                builder,
                flowerType,
                secondaryIndividuality,
                branchTipX,
                branchY + branchHeight,
                branchTipZ,
                radius * (0.48 + petalRoll(seed, branch, 12) * 0.14),
                rotation + branchSide * (0.28 + branch * 0.15),
                stem,
                ink,
                wind * 0.9,
                seed + (branch + 1) * 104_729);
        }
    }

    /// <summary>
    /// Emit wildflowers from one floor cell. The 3x3 world lattice never restarts at chunk boundaries. High values
    /// of the continuous bloom field retain several neighbouring sites (patches); the low field shoulder only wins
    /// a rare hash draw (individual flowers). Every accepted root is then revalidated against the exact turf cover.
    /// </summary>
    public static int addFloorFlowers(PropGeometryBuilder builder, FloorFlowerOptions options)
    {
        double budget = Math.max(0, Math.round(options.flowerBudget));
        if (budget <= 0) return 0;
        const int lattice = 3;
        int emitted = 0;
        for (int j = 0; j < lattice && emitted < budget; j++)
        {
            for (int i = 0; i < lattice && emitted < budget; i++)
            {
                double siteX = (double)options.cellX * lattice + i;
                double siteY = (double)options.cellY * lattice + j;
                double jitterX = cellHash(siteX * 101 + 17, siteY * 113 - 29);
                double jitterZ = cellHash(siteX * 127 - 37, siteY * 139 + 41);
                double u = clamp((i + 0.5 + (jitterX - 0.5) * 0.86) / lattice, 0.025, 0.975);
                double v = clamp((j + 0.5 + (jitterZ - 0.5) * 0.86) / lattice, 0.025, 0.975);
                double bloom = clamp(options.flowerCoverAt(u, v), 0, 1);
                if (bloom <= 0.012 || options.turfCoverAt(u, v) <= 0.035) continue;
                // Exact bloom regimes keep a lone shoulder flower from turning into a mini patch. Mid-values top out at
                // two plants per cell; only the rare field core is permitted to use the full authored budget.
                bool isSingle = bloom < 0.16;
                bool isField = bloom >= 0.4;
                double localBudget = Math.min(budget, isSingle ? 1 : isField ? 4 : 2);
                if (emitted >= localBudget) continue;
                double survival = isSingle
                    ? 0.008 + bloom * 0.24
                    : isField
                        ? 0.28 + clamp((bloom - 0.4) / 0.22, 0, 1) * 0.16
                        : 0.055 + clamp((bloom - 0.16) / 0.24, 0, 1) * 0.18;
                if (cellHash(siteX * 191 + 53, siteY * 181 - 47) > survival) continue;
                double rootX = options.originX + u * options.tileSize;
                double rootZ = options.originZ + v * options.tileSize;
                double y0 = options.y0 + options.groundLift(u, v);
                double seed = siteX * 92_837_111 + siteY * 689_287_499;
                addFlower(builder, options, rootX, rootZ, y0, u, v, bloom, siteX, siteY, seed);
                emitted++;
            }
        }
        return emitted;
    }
}
