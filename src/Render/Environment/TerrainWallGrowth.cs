// Port of packages/client/src/render/environment/terrainWallGrowth.ts — keep in lockstep with the original.
//
// Living wall cover and the geometry that is allowed to grow from it.
//
// Moss follows the same placement contract as floor turf: one continuous absolute-world field is baked into
// the host and sampled by every geometric root below. The rock shader deliberately does not paint moss; the
// visible colony is made from real bent wall-fibres in the shared terrain batch.
//
// PORT NOTES:
// * `TerrainWallDirection` ('n' | 'e' | 's' | 'w') and `TerrainWallDetailKind` ('none' | 'bush' | 'vine' |
//   'fern') are string-literal unions without a const object, hence plain `string`.
// * `TilesetKind` and `Biome['pattern']` (theme.ts) are string-literal unions too, hence `string`.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class TerrainWallGrowthProfile
{
    public bool enabled;
    /// <summary>Habitat amount, 0 barren .. 1 saturated. It controls patch extent and geometric density.</summary>
    public double abundance;
    public int moss;
    public int leafMid;
    public int leafLight;
    public int leafDeep;
    public int stem;
    public int crack;
    public int crackRim;
    public int accent;
}

public sealed class TerrainWallGrowthOptions
{
    public TerrainWallGrowthProfile profile;
    /// <summary>TerrainWallDirection: 'n' | 'e' | 's' | 'w'.</summary>
    public string direction;
    public double x0;
    public double x1;
    public double z0;
    public double z1;
    public double topY;
    public double bottomY;
    /// <summary>Absolute logical cell coordinates; used only for stable selection, never as a visual grid.</summary>
    public int cellX;
    public int cellY;
    /// <summary>Quality-scaled cliff dressing budget, 0..1. Moss itself is material and is not density-gated.</summary>
    public double density;
    /// <summary>Optional habitat multiplier for deep-shaft walls where small colonies need to survive distance.</summary>
    public double? mossPresenceScale;
    /// <summary>Optional minimum chance for one readable fissure plant on a deep-shaft face.</summary>
    public double? heroPresenceFloor;
}

public static partial class TerrainWallGrowth
{
    private const double TAU = Math.PI * 2;
    private static readonly double[] LEAF_LEFT_SHADE = { 0.8, 0.96, 1.1 };
    private static readonly double[] LEAF_RIGHT_SHADE = { 0.8, 1.1, 0.96 };
    private static readonly double[] MOSS_LOWER_SHADE = { 0.9, 0.94, 1, 0.98 };
    private static readonly double[] MOSS_UPPER_SHADE = { 0.96, 0.99, 1.05, 1.03 };
    private static readonly double[] FISSURE_SHADE = { 0.68, 0.78, 0.9, 0.74, 0.64, 0.72 };
    // Mutable module scratch → one instance per compiler thread (see RENDER_AGENT_BRIEF "Thread safety").
    [ThreadStatic] private static double[]? _MOSS_WIND4;
    private static double[] MOSS_WIND4 => _MOSS_WIND4 ??= new double[] { 0, 0, 0, 0 };
    [ThreadStatic] private static P3[]? _MOSS_QUAD;
    private static P3[] MOSS_QUAD => _MOSS_QUAD ??= new[]
    {
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
    };

    public static readonly TerrainWallGrowthProfile TERRAIN_WALL_GROWTH_DISABLED = new TerrainWallGrowthProfile
    {
        enabled = false,
        abundance = 0,
        moss = 0x60734f,
        leafMid = 0x687f51,
        leafLight = 0x90a968,
        leafDeep = 0x45563c,
        stem = 0x4d4032,
        crack = 0x171a16,
        crackRim = 0x85877d,
        accent = 0xd7c676,
    };

    /// <summary>
    /// The single biome-to-wall-habitat mapping, shared by the CPU bake and the renderer uniform.
    ///
    /// Only the founding natural cliff language grows living cover; the other construction languages keep their
    /// walls bare.
    /// </summary>
    /// <param name="construction">TilesetKind.</param>
    /// <param name="pattern">Biome['pattern'].</param>
    public static double terrainWallGrowthAbundance(double groundAccent, string construction, string pattern)
    {
        if (construction != "natural") return 0;
        double habitatBias =
            pattern == "mossStone"
                ? 0.2
                : pattern == "ashCracks"
                    ? -0.12
                    : pattern == "plaza"
                        ? -0.16
                        : -0.08;
        return clamp(0.46 + (groundAccent - 1) * 0.18 + habitatBias, 0.22, 1);
    }

    public static TerrainWallGrowthProfile createTerrainWallGrowthProfile(
        double abundance,
        int moss,
        int wallLit,
        int wallDeep,
        int soil,
        int accent)
    {
        double amount = clamp(abundance, 0, 1);
        if (amount <= 0) return TERRAIN_WALL_GROWTH_DISABLED;
        int leafMid = mix(moss, accent, 0.08);
        return new TerrainWallGrowthProfile
        {
            enabled = true,
            abundance = amount,
            moss = moss,
            leafMid = leafMid,
            leafLight = mix(leafMid, wallLit, 0.3),
            leafDeep = mix(leafMid, wallDeep, 0.34),
            stem = mix(wallDeep, soil, 0.68),
            crack = mix(wallDeep, 0x0d110e, 0.54),
            crackRim = mix(wallLit, wallDeep, 0.34),
            accent = mix(accent, wallLit, 0.28),
        };
    }

    private static double smoothRange(double low, double high, double value)
    {
        double t = clamp((value - low) / Math.max(0.0001, high - low), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Continuous moss cover at one point on a rock face.
    ///
    /// The horizontal domain combines X and Z, so all four wall orientations sample the same world field without
    /// revealing their tile direction. Broad colonies, a warped middle octave and restrained foot humidity create
    /// recognizable patches; the shader reuses its existing fine rock grain for their micro edge/tooth.
    /// </summary>
    public static double terrainWallMossCoverAt(
        double worldX,
        double worldY,
        double worldZ,
        double height01,
        TerrainWallGrowthProfile profile)
    {
        if (!profile.enabled || profile.abundance <= 0) return 0;
        double along = worldX * 0.819 + worldZ * 1.173;
        double broad = smoothCellNoise(along + 137, worldY - 211, 74, 113);
        double middle = smoothCellNoise(along * 1.31 - worldY * 0.17 - 89, worldY + along * 0.08, 31, 197);
        double tooth = smoothCellNoise(along * 0.73 + worldY * 0.21 + 43, worldY - along * 0.11, 16, 271);
        double footHumidity = 1 - smoothRange(0.1, 0.82, clamp(height01, 0, 1));
        // What grows on the cap SPILLS over its edge. Without this the face carried moss at its damp foot and
        // nothing at its crest, so the top edge stayed the one line in the picture with growth on neither side of
        // it — a cut rather than a place where two surfaces meet. The band is narrow (the top ~14 % of the face)
        // and it rides the SAME field as the rest of the cover, so a face that is bare stays bare: this raises the
        // probability of growth at the crest, it does not paint a green stripe along it.
        double crestSpill = smoothRange(0.86, 1, clamp(height01, 0, 1));
        double field =
            broad * 0.58 + middle * 0.29 + tooth * 0.13 + footHumidity * 0.075 + crestSpill * 0.085;
        double low = 0.58 - profile.abundance * 0.13;
        return smoothRange(low, low + 0.19, field);
    }

    private sealed class WallFrame
    {
        public double tx;
        public double tz;
        public double nx;
        public double nz;
        public bool orbitBackside;
        public bool edgeOn;
    }

    private static WallFrame frameFor(string direction)
    {
        switch (direction)
        {
            case "n":
                return new WallFrame { tx = 1, tz = 0, nx = 0, nz = -1, orbitBackside = true, edgeOn = false };
            case "s":
                return new WallFrame { tx = 1, tz = 0, nx = 0, nz = 1, orbitBackside = false, edgeOn = false };
            case "e":
                return new WallFrame { tx = 0, tz = 1, nx = 1, nz = 0, orbitBackside = false, edgeOn = true };
            case "w":
                return new WallFrame { tx = 0, tz = 1, nx = -1, nz = 0, orbitBackside = false, edgeOn = true };
        }
        // TS falls off the end of the switch (undefined); the first `frame.` access then throws.
        return null!;
    }

    private static P3 wallPoint(
        TerrainWallGrowthOptions options,
        WallFrame frame,
        double u,
        double y,
        double outward)
    {
        if (options.direction == "n" || options.direction == "s")
        {
            return new P3
            {
                x = options.x0 + (options.x1 - options.x0) * u,
                y = y,
                z = (options.direction == "s" ? options.z1 : options.z0) + frame.nz * outward,
            };
        }
        return new P3
        {
            x = (options.direction == "e" ? options.x1 : options.x0) + frame.nx * outward,
            y = y,
            z = options.z0 + (options.z1 - options.z0) * u,
        };
    }

    private static void addLeaf(
        PropGeometryBuilder builder,
        WallFrame frame,
        P3 root,
        double lateral,
        double rise,
        double protrusion,
        double width,
        int color,
        double wind)
    {
        double travel = Math.hypot(lateral, rise);
        if (!Js.Truthy(travel)) travel = 1;
        double crossT = (rise / travel) * width;
        double crossY = (-lateral / travel) * width;
        var tip = new P3
        {
            x = root.x + frame.tx * lateral + frame.nx * protrusion,
            y = root.y + rise,
            z = root.z + frame.tz * lateral + frame.nz * protrusion,
        };
        var shoulder = new P3
        {
            x = root.x + frame.tx * lateral * 0.46 + frame.nx * protrusion * 0.56,
            y = root.y + rise * 0.46,
            z = root.z + frame.tz * lateral * 0.46 + frame.nz * protrusion * 0.56,
        };
        var left = new P3
        {
            x = shoulder.x + frame.tx * crossT,
            y = shoulder.y + crossY,
            z = shoulder.z + frame.tz * crossT,
        };
        var right = new P3
        {
            x = shoulder.x - frame.tx * crossT,
            y = shoulder.y - crossY,
            z = shoulder.z - frame.tz * crossT,
        };
        // Two pitched facets form a central leaf ridge. This catches both the world key light and the wall bounce;
        // one camera-facing quad stays the same colour everywhere and collapses into a sprite at gameplay zoom.
        builder.addSurface(
            new[] { root, left, tip },
            frame.nx * 0.72 - frame.tx * 0.13,
            0.68,
            frame.nz * 0.72 - frame.tz * 0.13,
            color,
            TERRAIN_SURFACE_PATTERN.floor,
            0.04,
            LEAF_LEFT_SHADE,
            new[] { wind * 0.08, wind * 0.48, wind },
            false,
            frame.edgeOn,
            frame.orbitBackside);
        builder.addSurface(
            new[] { root, tip, right },
            frame.nx * 0.72 + frame.tx * 0.13,
            0.68,
            frame.nz * 0.72 + frame.tz * 0.13,
            color,
            TERRAIN_SURFACE_PATTERN.floor,
            0.04,
            LEAF_RIGHT_SHADE,
            new[] { wind * 0.08, wind, wind * 0.48 },
            false,
            frame.edgeOn,
            frame.orbitBackside);
    }

    private static void addWallFissure(
        PropGeometryBuilder builder,
        WallFrame frame,
        P3 root,
        double scale,
        TerrainWallGrowthProfile profile)
    {
        P3 point(double along, double rise, double outward = 0.025) => new P3
        {
            x = root.x + frame.tx * along * scale + frame.nx * outward,
            y = root.y + rise * scale,
            z = root.z + frame.tz * along * scale + frame.nz * outward,
        };
        // The dark, irregular opening remains visible around the woody collar. It supplies the missing contact cue:
        // the carrier vanishes INTO fractured stone instead of ending cleanly on its front plane.
        builder.addSurface(
            new[]
            {
                point(-2.75, 0.35),
                point(-0.92, 3.65),
                point(0.18, 1.38),
                point(2.85, -0.55),
                point(0.72, -3.05),
                point(-0.68, -1.18),
            },
            frame.nx,
            0.04,
            frame.nz,
            profile.crack,
            TERRAIN_SURFACE_PATTERN.floor,
            0.03,
            FISSURE_SHADE,
            0,
            false,
            frame.edgeOn,
            frame.orbitBackside);
        // Two hairline fractures escape the load-bearing opening. A restrained lit rim on one lip gives the core
        // enough contrast to survive both a pale limestone wall and a dark wet cliff.
        builder.addSurface(
            new[]
            {
                point(-0.78, 2.85, 0.032),
                point(-1.62, 5.55, 0.034),
                point(-1.18, 5.72, 0.036),
                point(-0.35, 2.62, 0.036),
            },
            frame.nx,
            0.03,
            frame.nz,
            profile.crack,
            TERRAIN_SURFACE_PATTERN.floor,
            0.025,
            new[] { 0.72, 0.62, 0.78, 0.86 },
            0,
            false,
            frame.edgeOn,
            frame.orbitBackside);
        builder.addSurface(
            new[]
            {
                point(0.62, -2.18, 0.033),
                point(2.05, -4.45, 0.035),
                point(2.42, -4.12, 0.037),
                point(1.02, -1.88, 0.037),
            },
            frame.nx,
            0.03,
            frame.nz,
            profile.crack,
            TERRAIN_SURFACE_PATTERN.floor,
            0.025,
            new[] { 0.76, 0.64, 0.8, 0.9 },
            0,
            false,
            frame.edgeOn,
            frame.orbitBackside);
        builder.addSurface(
            new[]
            {
                point(-2.7, 0.55, 0.052),
                point(-0.88, 3.6, 0.052),
                point(-0.48, 3.08, 0.054),
                point(-2.28, 0.18, 0.054),
            },
            frame.nx,
            0.08,
            frame.nz,
            profile.crackRim,
            TERRAIN_SURFACE_PATTERN.floor,
            0.025,
            new[] { 0.72, 0.88, 0.94, 0.78 },
            0,
            false,
            frame.edgeOn,
            frame.orbitBackside);
    }

    /// <summary>A bent two-ribbon moss shoot: the vertical-wall twin of the floor grass blade.</summary>
    /// <remarks>PORT NOTE (allocation): the root arrives as three numbers and `mid`/`tip` are locals — the TS point
    /// objects are only ever read, and a wall-moss carpet emits dozens of blades per colony.</remarks>
    private static void addWallMossBlade(
        PropGeometryBuilder builder,
        WallFrame frame,
        double rootX,
        double rootY,
        double rootZ,
        double directionAlong,
        double directionY,
        double bladeLength,
        double width,
        double lift,
        int color,
        double wind)
    {
        P3[] mossQuad = MOSS_QUAD;
        double[] mossWind4 = MOSS_WIND4;
        double directionLength = Math.hypot(directionAlong, directionY);
        if (!Js.Truthy(directionLength)) directionLength = 1;
        double along = directionAlong / directionLength;
        double rise = directionY / directionLength;
        double sideAlong = -rise * width;
        double sideY = along * width;
        double midX = rootX + frame.tx * along * bladeLength * 0.48 + frame.nx * lift * 0.34;
        double midY = rootY + rise * bladeLength * 0.48;
        double midZ = rootZ + frame.tz * along * bladeLength * 0.48 + frame.nz * lift * 0.34;
        double tipX = rootX + frame.tx * along * bladeLength + frame.nx * lift;
        double tipY = rootY + rise * bladeLength;
        double tipZ = rootZ + frame.tz * along * bladeLength + frame.nz * lift;
        mossQuad[0].x = rootX - frame.tx * sideAlong;
        mossQuad[0].y = rootY - sideY;
        mossQuad[0].z = rootZ - frame.tz * sideAlong;
        mossQuad[1].x = rootX + frame.tx * sideAlong;
        mossQuad[1].y = rootY + sideY;
        mossQuad[1].z = rootZ + frame.tz * sideAlong;
        mossQuad[2].x = midX + frame.tx * sideAlong * 0.56;
        mossQuad[2].y = midY + sideY * 0.56;
        mossQuad[2].z = midZ + frame.tz * sideAlong * 0.56;
        mossQuad[3].x = midX - frame.tx * sideAlong * 0.56;
        mossQuad[3].y = midY - sideY * 0.56;
        mossQuad[3].z = midZ - frame.tz * sideAlong * 0.56;
        mossWind4[0] = 0;
        mossWind4[1] = 0;
        mossWind4[2] = wind * 0.38;
        mossWind4[3] = wind * 0.38;
        builder.addSurface(
            mossQuad,
            frame.nx * 0.88,
            0.42,
            frame.nz * 0.88,
            color,
            TERRAIN_SURFACE_PATTERN.floor,
            0.04,
            MOSS_LOWER_SHADE,
            mossWind4,
            false,
            frame.edgeOn,
            frame.orbitBackside);
        mossQuad[0].x = midX - frame.tx * sideAlong * 0.56;
        mossQuad[0].y = midY - sideY * 0.56;
        mossQuad[0].z = midZ - frame.tz * sideAlong * 0.56;
        mossQuad[1].x = midX + frame.tx * sideAlong * 0.56;
        mossQuad[1].y = midY + sideY * 0.56;
        mossQuad[1].z = midZ + frame.tz * sideAlong * 0.56;
        mossQuad[2].x = tipX + frame.tx * sideAlong * 0.08;
        mossQuad[2].y = tipY + sideY * 0.08;
        mossQuad[2].z = tipZ + frame.tz * sideAlong * 0.08;
        mossQuad[3].x = tipX - frame.tx * sideAlong * 0.08;
        mossQuad[3].y = tipY - sideY * 0.08;
        mossQuad[3].z = tipZ - frame.tz * sideAlong * 0.08;
        mossWind4[0] = wind * 0.38;
        mossWind4[1] = wind * 0.38;
        mossWind4[2] = wind;
        mossWind4[3] = wind;
        builder.addSurface(
            mossQuad,
            frame.nx * 0.82,
            0.54,
            frame.nz * 0.82,
            color,
            TERRAIN_SURFACE_PATTERN.floor,
            0.04,
            MOSS_UPPER_SHADE,
            mossWind4,
            false,
            frame.edgeOn,
            frame.orbitBackside);
    }

    /// <summary>A dense patch of short wall fibres. There is no closed green backing surface: every visible mark is growth.</summary>
    private static void addWallMossCarpet(
        PropGeometryBuilder builder,
        WallFrame frame,
        P3 root,
        double seed,
        double cover,
        int fibres,
        double radiusAlong,
        double radiusY,
        TerrainWallGrowthProfile profile)
    {
        double colonyLean = (cellHash(seed + 17, seed - 23) - 0.5) * 0.42;
        int mossMid = mix(profile.moss, profile.crackRim, 0.61);
        int mossLight = mix(profile.moss, profile.crackRim, 0.48);
        int mossDeep = mix(mossMid, profile.crack, 0.1);
        for (int fibre = 0; fibre < fibres; fibre++)
        {
            double roll = cellHash(seed + fibre * 97 + 31, seed - fibre * 61 - 17);
            double rollB = cellHash(seed + fibre * 149 - 13, seed + fibre * 113 + 41);
            double rollC = cellHash(seed + fibre * 211 + 53, seed - fibre * 137 - 71);
            double radial = Math.sqrt(roll);
            double rootAngle = rollB * TAU;
            double irregularity = 0.76 + rollC * 0.36;
            double fibreRootX = root.x + frame.tx * Math.cos(rootAngle) * radiusAlong * radial * irregularity;
            double fibreRootY = root.y + Math.sin(rootAngle) * radiusY * radial * (0.82 + rollB * 0.25);
            double fibreRootZ = root.z + frame.tz * Math.cos(rootAngle) * radiusAlong * radial * irregularity;
            double growthAngle = Math.PI * 0.5 + colonyLean + (rollC - 0.5) * 1.05;
            double edgeBody = 1 - radial;
            double length = 1.05 + cover * 0.62 + edgeBody * 1.28 + rollB * 0.52;
            int color = fibre % 5 == 0 ? mossLight : fibre % 4 == 0 ? mossDeep : mossMid;
            addWallMossBlade(
                builder,
                frame,
                fibreRootX,
                fibreRootY,
                fibreRootZ,
                Math.cos(growthAngle),
                Math.sin(growthAngle),
                length,
                0.3 + edgeBody * 0.16 + rollC * 0.11,
                0.22 + length * (0.1 + roll * 0.025),
                color,
                0.28 + cover * 0.22 + rollB * 0.16);
        }
    }

    /// <summary>One shared-root moss colony, dense enough to read as a patch rather than detached punctuation.</summary>
    private static void addWallMossTuft(
        PropGeometryBuilder builder,
        WallFrame frame,
        P3 root,
        double seed,
        double cover,
        bool hero,
        TerrainWallGrowthProfile profile)
    {
        addWallMossCarpet(
            builder,
            frame,
            root,
            seed,
            cover,
            hero ? 44 : 32,
            (hero ? 8.6 : 7.2) + cover * 1.8,
            (hero ? 5.5 : 4.7) + cover * 1.25,
            profile);
    }

    private static void addWallMossPatches(
        PropGeometryBuilder builder,
        TerrainWallGrowthOptions options,
        WallFrame frame,
        double seed,
        double density)
    {
        double height = options.topY - options.bottomY;
        double alongLength = Math.hypot(options.x1 - options.x0, options.z1 - options.z0);
        // Kept a double (a NaN extent must skip the loop exactly as the JS comparison does).
        double columns = Math.max(2, Math.min(4, Math.round(alongLength / 16)));
        const double verticalPitch = 14;
        double firstVerticalSite = Math.floor(options.bottomY / verticalPitch) - 1;
        double lastVerticalSite = Math.ceil(options.topY / verticalPitch) + 1;
        int alongCell =
            options.direction == "n" || options.direction == "s" ? options.cellX : options.cellY;
        for (double verticalSite = firstVerticalSite; verticalSite <= lastVerticalSite; verticalSite++)
        {
            for (int column = 0; column < columns; column++)
            {
                double alongSite = alongCell * columns + column;
                double candidateSeed = seed + Math.imul(alongSite, 1543) + Math.imul(verticalSite, 8111);
                double u = (column + 0.16 + cellHash(candidateSeed + 13, candidateSeed - 31) * 0.68) / columns;
                double y =
                    (verticalSite + 0.5 + (cellHash(candidateSeed + 47, candidateSeed - 59) - 0.5) * 0.82) *
                    verticalPitch;
                if (y <= options.bottomY + 1.2 || y >= options.topY - 1.2) continue;
                double v = (y - options.bottomY) / height;
                P3 anchor = wallPoint(options, frame, u, y, 0.04);
                double cover = terrainWallMossCoverAt(anchor.x, anchor.y, anchor.z, v, options.profile);
                if (cover < 0.44) continue;
                double keep = clamp(
                    density * (0.012 + cover * 0.1) * (options.mossPresenceScale ?? 1),
                    0,
                    0.42);
                if (cellHash(candidateSeed + 83, candidateSeed - 107) > keep) continue;
                double tier = cellHash(candidateSeed + 139, candidateSeed - 163);
                addWallMossTuft(
                    builder,
                    frame,
                    anchor,
                    candidateSeed,
                    cover,
                    tier > 0.8 && cover > 0.56,
                    options.profile);
            }
        }
    }

    private static P3 branchPoint(P3 start, P3 end, double t)
    {
        return new P3
        {
            x = start.x + (end.x - start.x) * t,
            y = start.y + (end.y - start.y) * t,
            z = start.z + (end.z - start.z) * t,
        };
    }

    /// <summary>Alternating leaf pairs follow a real twig axis; there is deliberately no radial endpoint rosette.</summary>
    private static void addLeavesAlongTwig(
        PropGeometryBuilder builder,
        WallFrame frame,
        P3 start,
        P3 end,
        double seed,
        double scale,
        TerrainWallGrowthProfile profile)
    {
        double along = (end.x - start.x) * frame.tx + (end.z - start.z) * frame.tz;
        double rise = end.y - start.y;
        double length = Math.hypot(along, rise);
        if (!Js.Truthy(length)) length = 1;
        double directionAlong = along / length;
        double directionY = rise / length;
        double perpendicularAlong = -directionY;
        double perpendicularY = directionAlong;
        int leafMid = mix(profile.leafMid, profile.crackRim, 0.34);
        int leafLight = mix(profile.leafLight, profile.crackRim, 0.3);
        int leafDeep = mix(profile.leafDeep, profile.crackRim, 0.26);
        for (int node = 0; node < 3; node++)
        {
            double roll = cellHash(seed + node * 83 + 7, seed - node * 101 - 19);
            double t = 0.28 + node * 0.24;
            P3 root = branchPoint(start, end, t);
            double side = node % 2 == 0 ? -1 : 1;
            double reach = (1.65 + roll * 0.72) * scale;
            double forward = (0.38 + roll * 0.24) * scale;
            int color = node == 1 ? leafLight : node == 2 ? leafDeep : leafMid;
            addLeaf(
                builder,
                frame,
                root,
                perpendicularAlong * side * reach + directionAlong * forward,
                perpendicularY * side * reach + directionY * forward + 0.16 * scale,
                (0.52 + roll * 0.48) * scale,
                (0.66 + roll * 0.24) * scale,
                color,
                1.05 + roll * 0.55);
            if (node < 2)
            {
                addLeaf(
                    builder,
                    frame,
                    root,
                    perpendicularAlong * -side * reach * 0.7 + directionAlong * forward * 0.72,
                    perpendicularY * -side * reach * 0.7 + directionY * forward * 0.72 + 0.12 * scale,
                    (0.46 + roll * 0.38) * scale,
                    (0.58 + roll * 0.2) * scale,
                    node == 0 ? leafDeep : leafMid,
                    0.94 + roll * 0.48);
            }
        }
        addLeaf(
            builder,
            frame,
            end,
            directionAlong * 1.75 * scale,
            directionY * 1.75 * scale + 0.26 * scale,
            0.72 * scale,
            0.76 * scale,
            leafLight,
            1.42);
    }

    private static void addWallBush(
        PropGeometryBuilder builder,
        TerrainWallGrowthOptions options,
        WallFrame frame,
        P3 root,
        double seed,
        double scale)
    {
        TerrainWallGrowthProfile profile = options.profile;
        addWallFissure(builder, frame, root, clamp(scale * 1.08, 0.58, 0.78), profile);

        double rootRoll = cellHash(seed + 23, seed - 31);
        int stemMid = mix(profile.stem, profile.crackRim, 0.24);
        int stemLit = mix(stemMid, profile.crackRim, 0.18);
        // A few short moss fibres bridge stone and bark, so the plant belongs to the damp crevice instead of reading
        // as an unrelated prop centred on a black mark.
        addWallMossCarpet(
            builder,
            frame,
            new P3
            {
                x = root.x + frame.tx * 1.15,
                y = root.y - 0.95,
                z = root.z + frame.tz * 1.15,
            },
            seed + 809,
            0.72,
            10,
            2.5 + scale * 0.7,
            1.8 + scale * 0.5,
            profile);
        var collar = new P3
        {
            x = root.x + frame.tx * (rootRoll - 0.5) * 0.75 * scale + frame.nx * 1.15 * scale,
            y = root.y + (0.28 + rootRoll * 0.5) * scale,
            z = root.z + frame.tz * (rootRoll - 0.5) * 0.75 * scale + frame.nz * 1.15 * scale,
        };
        var hub = new P3
        {
            x = root.x + frame.tx * (rootRoll - 0.5) * 1.15 * scale + frame.nx * 2.15 * scale,
            y = root.y + (1.05 + rootRoll * 0.55) * scale,
            z = root.z + frame.tz * (rootRoll - 0.5) * 1.15 * scale + frame.nz * 2.15 * scale,
        };
        // Two short bark sections disappear into the slit. Their lightened pigment prevents the high-contrast black
        // star that the old long radial carriers produced on a pale wall.
        propLimb(
            builder,
            root.x - frame.nx * 0.38,
            root.y,
            root.z - frame.nz * 0.38,
            collar.x,
            collar.y,
            collar.z,
            0.72 * scale,
            0.54 * scale,
            5,
            rootRoll * TAU,
            stemLit,
            stemMid,
            0.76,
            0,
            0.18,
            false);
        propLimb(
            builder,
            collar.x,
            collar.y,
            collar.z,
            hub.x,
            hub.y,
            hub.z,
            0.54 * scale,
            0.38 * scale,
            5,
            rootRoll * TAU + 0.43,
            stemLit,
            stemMid,
            0.8,
            0.18,
            0.48,
            false);

        const int stems = 2;
        for (int branch = 0; branch < stems; branch++)
        {
            double roll = cellHash(seed + branch * 79, seed - branch * 47);
            double branchSide = branch == 0 ? -1 : 1;
            double hierarchy = branch == 0 ? 0.86 : 1;
            double side = branchSide * (4.1 + roll * 1.25) * scale * hierarchy;
            double rise = (branch == 0 ? 3.4 + roll * 1.2 : 4.55 + roll * 1.35) * scale * hierarchy;
            double outward = (0.78 + roll * 0.72) * scale * hierarchy;
            var elbow = new P3
            {
                x = hub.x + frame.tx * side * 0.52 + frame.nx * outward * 0.48,
                y = hub.y + rise * 0.52,
                z = hub.z + frame.tz * side * 0.52 + frame.nz * outward * 0.48,
            };
            var end = new P3
            {
                x = hub.x + frame.tx * side + frame.nx * outward,
                y = hub.y + rise,
                z = hub.z + frame.tz * side + frame.nz * outward,
            };
            double radius = (0.46 + roll * 0.12) * scale;
            propLimb(
                builder,
                hub.x,
                hub.y,
                hub.z,
                elbow.x,
                elbow.y,
                elbow.z,
                radius,
                radius * 0.7,
                4,
                branchSide * 0.52,
                stemLit,
                stemMid,
                0.84,
                0.48,
                0.9,
                false);
            propLimb(
                builder,
                elbow.x,
                elbow.y,
                elbow.z,
                end.x,
                end.y,
                end.z,
                radius * 0.7,
                radius * 0.36,
                4,
                branchSide * 0.52 + 0.31,
                stemLit,
                stemMid,
                0.88,
                0.9,
                1.62 + roll * 0.35);

            addLeavesAlongTwig(builder, frame, elbow, end, seed + branch * 419, scale, profile);

            // Only the leader forks, and the fork remains inside the same fan. One hierarchy is enough at this scale.
            if (branch == 1)
            {
                var twigEnd = new P3
                {
                    x = elbow.x - frame.tx * (1.8 + roll * 0.65) * scale + frame.nx * 0.45 * scale,
                    y = elbow.y + (2.05 + roll * 0.7) * scale,
                    z = elbow.z - frame.tz * (1.8 + roll * 0.65) * scale + frame.nz * 0.45 * scale,
                };
                propLimb(
                    builder,
                    elbow.x,
                    elbow.y,
                    elbow.z,
                    twigEnd.x,
                    twigEnd.y,
                    twigEnd.z,
                    radius * 0.48,
                    radius * 0.2,
                    4,
                    -0.38,
                    stemLit,
                    stemMid,
                    0.88,
                    1.05,
                    1.58);
                addLeavesAlongTwig(
                    builder,
                    frame,
                    elbow,
                    twigEnd,
                    seed + branch * 613 + 107,
                    scale * 0.72,
                    profile);
            }
        }
        builder.addShadowCaster(new[]
        {
            root,
            new P3
            {
                x = hub.x - frame.tx * 5.5 * scale + frame.nx * 1.2,
                y = hub.y + 3.4 * scale,
                z = hub.z - frame.tz * 5.5 * scale + frame.nz * 1.2,
            },
            new P3
            {
                x = hub.x + frame.nx * 2.8,
                y = hub.y + 7.2 * scale,
                z = hub.z + frame.nz * 2.8,
            },
            new P3
            {
                x = hub.x + frame.tx * 5.5 * scale + frame.nx * 1.2,
                y = hub.y + 3.4 * scale,
                z = hub.z + frame.tz * 5.5 * scale + frame.nz * 1.2,
            },
        });
    }

    private static void addHangingVine(
        PropGeometryBuilder builder,
        TerrainWallGrowthOptions options,
        WallFrame frame,
        P3 root,
        double seed,
        double scale)
    {
        TerrainWallGrowthProfile profile = options.profile;
        double rootRoll = cellHash(seed + 17, seed - 53);
        addWallFissure(builder, frame, root, scale * 0.78, profile);
        var collar = new P3
        {
            x = root.x + frame.tx * (rootRoll - 0.5) * 1.4 * scale + frame.nx * 3.1 * scale,
            y = root.y + (1.1 + rootRoll) * scale,
            z = root.z + frame.tz * (rootRoll - 0.5) * 1.4 * scale + frame.nz * 3.1 * scale,
        };
        var vineRoot = new P3
        {
            x = root.x + frame.tx * (rootRoll - 0.5) * 3.2 * scale + frame.nx * 5.9 * scale,
            y = root.y - (1.4 + rootRoll * 1.6) * scale,
            z = root.z + frame.tz * (rootRoll - 0.5) * 3.2 * scale + frame.nz * 5.9 * scale,
        };
        // The liana has a woody hook before its flexible fall. Leaving both segments exposed proves that the main
        // carrier comes from inside the wall; no foliage blob is allowed to hide this junction.
        propLimb(
            builder,
            root.x - frame.nx,
            root.y,
            root.z - frame.nz,
            collar.x,
            collar.y,
            collar.z,
            1.12 * scale,
            0.82 * scale,
            5,
            rootRoll * TAU,
            mix(profile.stem, profile.leafLight, 0.1),
            profile.stem,
            0.7,
            0,
            0.35,
            false);
        propLimb(
            builder,
            collar.x,
            collar.y,
            collar.z,
            vineRoot.x,
            vineRoot.y,
            vineRoot.z,
            0.82 * scale,
            0.6 * scale,
            5,
            rootRoll * TAU + 0.48,
            mix(profile.stem, profile.leafLight, 0.12),
            profile.stem,
            0.76,
            0.35,
            0.8,
            false);
        addLeaf(
            builder,
            frame,
            vineRoot,
            -4.4 * scale,
            3.1 * scale,
            2.8 * scale,
            2.3 * scale,
            profile.leafLight,
            1.5);
        addLeaf(
            builder,
            frame,
            vineRoot,
            3.5 * scale,
            1.8 * scale,
            2.2 * scale,
            1.8 * scale,
            profile.leafMid,
            1.35);

        double available = vineRoot.y - options.bottomY - 1.6;
        double length = Math.min(available, (18 + cellHash(seed + 31, seed - 19) * 23) * scale);
        if (length < 5) return;

        P3 previous = vineRoot;
        var knots = new List<P3> { vineRoot };
        const int segments = 6;
        double phase = cellHash(seed, seed + 7) * TAU;
        for (int index = 1; index <= segments; index++)
        {
            double t = (double)index / segments;
            double sway = Math.sin(t * Math.PI * 1.55 + phase) * (3.2 + t * 4.4);
            var next = new P3
            {
                x = vineRoot.x + frame.tx * sway + frame.nx * (0.3 + t * t * 2.4),
                y = vineRoot.y - length * t,
                z = vineRoot.z + frame.tz * sway + frame.nz * (0.3 + t * t * 2.4),
            };
            double startRadius = Math.max(0.42, (0.92 - t * 0.36) * scale);
            propLimb(
                builder,
                previous.x,
                previous.y,
                previous.z,
                next.x,
                next.y,
                next.z,
                startRadius,
                Math.max(0.24, startRadius * 0.72),
                4,
                phase + t * 0.9,
                mix(profile.stem, profile.leafLight, 0.08),
                profile.stem,
                0.78,
                t * 1.25,
                1.2 + t * 1.5,
                index == segments);
            knots.push(next);
            if (index < segments || length > 13)
            {
                double side = index % 2 == 0 ? 1 : -1;
                double leafRoll = cellHash(seed + index * 149, seed - index * 127);
                addLeaf(
                    builder,
                    frame,
                    next,
                    side * (4.6 + leafRoll * 4.2) * scale,
                    1.5 + leafRoll * 2.8,
                    2.5 + leafRoll * 3.4,
                    (2.3 + leafRoll * 1.6) * scale,
                    index % 3 == 0 ? profile.leafLight : profile.leafMid,
                    1.5 + t * 1.8);
                if (index > 1 && index < segments - 1)
                {
                    addLeaf(
                        builder,
                        frame,
                        next,
                        -side * (3.2 + leafRoll * 2.7) * scale,
                        -0.5 + leafRoll * 2.1,
                        1.8 + leafRoll * 2.2,
                        (1.7 + leafRoll * 1.1) * scale,
                        profile.leafDeep,
                        1.3 + t * 1.6);
                }
            }
            previous = next;
        }

        // A shorter fork prevents the recognisable procedural "one wavy line" silhouette.
        P3 forkRoot = knots[2];
        double forkSide = cellHash(seed + 811, seed - 659) > 0.5 ? 1 : -1;
        var forkMid = new P3
        {
            x = forkRoot.x + frame.tx * forkSide * 6.2 * scale + frame.nx * 1.5,
            y = forkRoot.y - length * 0.16,
            z = forkRoot.z + frame.tz * forkSide * 6.2 * scale + frame.nz * 1.5,
        };
        var forkTip = new P3
        {
            x = forkMid.x + frame.tx * forkSide * 3.4 * scale + frame.nx * 1.2,
            y = forkMid.y - length * 0.14,
            z = forkMid.z + frame.tz * forkSide * 3.4 * scale + frame.nz * 1.2,
        };
        propLimb(
            builder,
            forkRoot.x,
            forkRoot.y,
            forkRoot.z,
            forkMid.x,
            forkMid.y,
            forkMid.z,
            0.54 * scale,
            0.37 * scale,
            4,
            phase + 1.3,
            profile.stem,
            profile.leafDeep,
            0.8,
            1.2,
            2.2,
            false);
        propLimb(
            builder,
            forkMid.x,
            forkMid.y,
            forkMid.z,
            forkTip.x,
            forkTip.y,
            forkTip.z,
            0.37 * scale,
            0.2 * scale,
            4,
            phase + 1.7,
            profile.stem,
            profile.leafDeep,
            0.82,
            2.2,
            2.8);
        addLeaf(
            builder,
            frame,
            forkMid,
            forkSide * 5.2 * scale,
            1.4 * scale,
            3.2 * scale,
            2.7 * scale,
            profile.leafLight,
            2.5);
        builder.addShadowCaster(new[]
        {
            new P3
            {
                x = root.x - frame.tx * 3.2,
                y = root.y,
                z = root.z - frame.tz * 3.2,
            },
            new P3
            {
                x = root.x + frame.tx * 3.2,
                y = root.y,
                z = root.z + frame.tz * 3.2,
            },
            new P3
            {
                x = previous.x + frame.tx * 4.2,
                y = previous.y,
                z = previous.z + frame.tz * 4.2,
            },
            new P3
            {
                x = previous.x - frame.tx * 4.2,
                y = previous.y,
                z = previous.z - frame.tz * 4.2,
            },
        });
    }

    private static void addWallFern(
        PropGeometryBuilder builder,
        TerrainWallGrowthOptions options,
        WallFrame frame,
        P3 root,
        double seed,
        double scale)
    {
        TerrainWallGrowthProfile profile = options.profile;
        double rootRoll = cellHash(seed + 41, seed - 73);
        addWallFissure(builder, frame, root, clamp(scale * 1.55, 0.72, 1), profile);
        var rhizome = new P3
        {
            x = root.x + frame.tx * (rootRoll - 0.5) * 1.5 * scale + frame.nx * 4.2 * scale,
            y = root.y + (0.8 + rootRoll) * scale,
            z = root.z + frame.tz * (rootRoll - 0.5) * 1.5 * scale + frame.nz * 4.2 * scale,
        };
        propLimb(
            builder,
            root.x - frame.nx * 0.8,
            root.y,
            root.z - frame.nz * 0.8,
            rhizome.x,
            rhizome.y,
            rhizome.z,
            0.92 * scale,
            0.58 * scale,
            5,
            rootRoll * TAU,
            mix(profile.stem, profile.leafLight, 0.1),
            profile.leafDeep,
            0.72,
            0,
            0.8);

        const int fronds = 4;
        for (int frond = 0; frond < fronds; frond++)
        {
            double roll = cellHash(seed + frond * 137, seed - frond * 109);
            double fan = ((double)frond / (fronds - 1) - 0.5) * 1.7 + (roll - 0.5) * 0.18;
            double lateral = Math.sin(fan) * (8 + roll * 3.6) * scale;
            double rise = Math.cos(fan) * (10.8 + roll * 4.8) * scale;
            double outward = (3.2 + roll * 2.3) * scale;
            var elbow = new P3
            {
                x = rhizome.x + frame.tx * lateral * 0.42 + frame.nx * outward * 0.48,
                y = rhizome.y + rise * 0.6 + (1 - Math.abs(fan)) * 1.4 * scale,
                z = rhizome.z + frame.tz * lateral * 0.42 + frame.nz * outward * 0.48,
            };
            var tip = new P3
            {
                x = rhizome.x + frame.tx * lateral + frame.nx * outward,
                y = rhizome.y + rise,
                z = rhizome.z + frame.tz * lateral + frame.nz * outward,
            };
            double radius = (0.38 + roll * 0.11) * scale;
            propLimb(
                builder,
                rhizome.x,
                rhizome.y,
                rhizome.z,
                elbow.x,
                elbow.y,
                elbow.z,
                radius,
                radius * 0.68,
                4,
                fan,
                profile.leafMid,
                profile.leafDeep,
                0.82,
                0.15,
                1.2,
                false);
            propLimb(
                builder,
                elbow.x,
                elbow.y,
                elbow.z,
                tip.x,
                tip.y,
                tip.z,
                radius * 0.68,
                radius * 0.22,
                4,
                fan + 0.4,
                profile.leafLight,
                profile.leafDeep,
                0.84,
                1.2,
                2.7 + roll * 0.5);
            double perpT = rise / Math.max(1, Math.hypot(lateral, rise));
            double perpY = -lateral / Math.max(1, Math.hypot(lateral, rise));
            for (int leaflet = 1; leaflet <= 3; leaflet++)
            {
                double t = 0.18 + leaflet * 0.2;
                bool onFirst = t < 0.6;
                double local = onFirst ? t / 0.6 : (t - 0.6) / 0.4;
                P3 a = onFirst ? rhizome : elbow;
                P3 b = onFirst ? elbow : tip;
                var @base = new P3
                {
                    x = a.x + (b.x - a.x) * local,
                    y = a.y + (b.y - a.y) * local,
                    z = a.z + (b.z - a.z) * local,
                };
                double reach = (3.2 - t * 1.4) * scale;
                foreach (double side in new double[] { -1, 1 })
                {
                    addLeaf(
                        builder,
                        frame,
                        @base,
                        side * perpT * reach,
                        side * perpY * reach + (1 - t) * 0.55 * scale,
                        (1 + (1 - t) * 1.35) * scale,
                        (0.76 + (1 - t) * 0.46) * scale,
                        (frond + leaflet) % 3 == 0 ? profile.leafLight : profile.leafMid,
                        1.35 + t * 1.8);
                }
            }
        }
        builder.addShadowCaster(new[]
        {
            root,
            new P3
            {
                x = rhizome.x - frame.tx * 8 * scale + frame.nx * 1.8,
                y = rhizome.y + 4 * scale,
                z = rhizome.z - frame.tz * 8 * scale + frame.nz * 1.8,
            },
            new P3
            {
                x = rhizome.x + frame.nx * 3.6,
                y = rhizome.y + 14 * scale,
                z = rhizome.z + frame.nz * 3.6,
            },
            new P3
            {
                x = rhizome.x + frame.tx * 8 * scale + frame.nx * 1.8,
                y = rhizome.y + 4 * scale,
                z = rhizome.z + frame.tz * 8 * scale + frame.nz * 1.8,
            },
        });
    }

    /// <summary>
    /// Emit at most one authored wall-growth event for one exposed face span.
    ///
    /// The category deck deliberately separates silhouette families: a bush pushes outward, a vine falls down, and
    /// a fern fans from a crevice. Dense shared-root moss fibres are emitted first from the same continuous field.
    /// </summary>
    /// <returns>TerrainWallDetailKind: 'none' | 'bush' | 'vine' | 'fern'.</returns>
    public static string addTerrainWallGrowth(PropGeometryBuilder builder, TerrainWallGrowthOptions options)
    {
        TerrainWallGrowthProfile profile = options.profile;
        double density = clamp(options.density, 0, 1);
        double height = options.topY - options.bottomY;
        if (!profile.enabled || density <= 0 || height < 4) return "none";

        int directionSalt =
            options.direction == "n"
                ? 17
                : options.direction == "e"
                    ? 43
                    : options.direction == "s"
                        ? 71
                        : 101;
        double seed = (double)options.cellX * 92837111 + (double)options.cellY * 689287499 + directionSalt * 104729;
        WallFrame frame = frameFor(options.direction);
        addWallMossPatches(builder, options, frame, seed, density);
        // Hero silhouettes collapse into unreadable sticks on the two wall directions that are almost edge-on in
        // the fixed terrain camera. Those faces retain low-relief moss, while authored plants live on readable faces.
        // Fluitown comic look: the Flui perspective sees every face, so east and west faces carry plants too.
        if ((frame.edgeOn && !FluitownWallPlants.AllFaces) || height < 7) return "none";
        double kindRoll = cellHash(seed + 191, seed - 223);
        string kind = kindRoll < 0.36 ? "bush" : kindRoll < 0.7 ? "vine" : "fern";
        double bestU = 0.5;
        double bestV = 0.5;
        double bestCover = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            double u = 0.14 + cellHash(seed + attempt * 61 + 7, seed - attempt * 83 - 11) * 0.72;
            double verticalRoll = cellHash(seed - attempt * 97 + 29, seed + attempt * 107 + 13);
            double v =
                kind == "vine"
                    ? 0.56 + verticalRoll * 0.34
                    : kind == "fern"
                        ? 0.14 + verticalRoll * 0.48
                        : 0.24 + verticalRoll * 0.5;
            double y = options.bottomY + height * v;
            P3 point = wallPoint(options, frame, u, y, 0);
            double cover = terrainWallMossCoverAt(point.x, point.y, point.z, v, profile);
            if (cover <= bestCover) continue;
            bestCover = cover;
            bestU = u;
            bestV = v;
        }
        if (bestCover < 0.16) return "none";
        double presence = Math.max(
            options.heroPresenceFloor ?? 0,
            density * profile.abundance * (0.08 + bestCover * 0.24));
        if (cellHash(seed + 401, seed - 433) > presence) return "none";

        double rootY = options.bottomY + height * bestV;
        // The stem begins exactly on the host plane. Only the crown advances out of it, which is the visual proof
        // that the plant emerges from a crevice instead of hovering in front of the cliff.
        P3 root = wallPoint(options, frame, bestU, rootY, 0.08);
        // Wall growth has to survive the same gameplay zoom as a ground bush. The ladder remains broad enough that
        // repetition is invisible, but its lower rung is deliberately above one: vertical foreshortening otherwise
        // turns even well-modelled leaves back into one-pixel punctuation.
        double scale = 1.06 + cellHash(seed + 557, seed - 593) * 0.46;
        // Fluitown comic look: the plant grows in the vegetation layer (FluitownWallPlants). The ported crevice (a dark
        // wedge standing off the face) is left out: seen from the side it reads as a black flag on the rock.
        if (FluitownWallPlants.record(builder,
                kind == "bush" ? FluitownWallPlants.FormShrub : kind == "vine" ? FluitownWallPlants.FormIvy : FluitownWallPlants.FormFern,
                root.x, root.y, root.z, frame.nx, frame.nz, frame.tx, frame.tz, height, options.bottomY, scale, seed, bestCover, profile))
            return kind;
        if (kind == "bush") addWallBush(builder, options, frame, root, seed, scale * 0.58);
        else if (kind == "vine") addHangingVine(builder, options, frame, root, seed, scale);
        else addWallFern(builder, options, frame, root, seed, scale * 0.76);
        return kind;
    }
}
