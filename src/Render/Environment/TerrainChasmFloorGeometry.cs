// Port of packages/client/src/render/environment/terrainChasmFloorGeometry.ts — keep in lockstep with the original.
//
// PORT NOTES:
// * The module-level scratch lanes are mutable, so each is thread-local (RENDER_AGENT_BRIEF "Thread safety").
//   `Float64Array` lanes are `double[]`; the colour lane is a `double[]` too, because it is handed to the builder's
//   `number | readonly number[]` channel (NumberOrArray) exactly like the TS array.
// * `TerrainContourCorner` and `TerrainEdgeDirection` are strings.
// * terrainVisualGround exports its own `clamp`; like the TS import list, only `terrainOrganicHeightAt` is taken
//   from it (qualified), so `clamp` stays terrainGeometryCompilerFields'.
// * `TerrainContourPoint` and `P3` are the same `{ x, y, z }` shape in TS, so the original hands the contour cap
//   straight to the builder. The C# ports declare two nominal classes, so the cap is copied value-for-value into a
//   per-thread P3 scratch (`contourPointsAsP3`) right before the synchronous `addSurface` (same approach as the
//   compiler's own helper); the builder only reads x/y/z.
using System;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Render.FloorGrassGeometry;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class ChasmFloorDetailOptions
{
    public double originX;
    public double originZ;
    public double tileSize;
    public double floorY;
    public int cellX;
    public int cellY;
    public int floorColor;
    public int edgeColor;
    public int growthColor;
    public double density;
    /// <summary>Visual-only cap lift at a local 0..1 coordinate; keeps dressing rooted in organic deep-floor relief.</summary>
    public Func<double, double, double>? groundLiftAt;
}

public static partial class TerrainChasmFloorGeometry
{
    private static double chasmFloorDetailYAt(
        ChasmFloorDetailOptions options,
        double u,
        double v,
        double lift)
    {
        return options.floorY + (options.groundLiftAt?.Invoke(u, v) ?? 0) + lift;
    }

    /// <summary>Pin a deep-floor lattice sample whenever any cell sharing that point is not part of the Chasm floor.</summary>
    public static double terrainChasmFloorPointMask(
        MaterializedTerrain terrain,
        double sampleX,
        double sampleY,
        double? floorZ = null)
    {
        bool onXEdge = Number.isInteger(sampleX);
        bool onYEdge = Number.isInteger(sampleY);
        // Doubles, as in JS: a NaN sample must skip both loops rather than wrap to an int sentinel.
        double minX = Math.floor(sampleX) - (onXEdge ? 1 : 0);
        double maxX = Math.floor(sampleX);
        double minY = Math.floor(sampleY) - (onYEdge ? 1 : 0);
        double maxY = Math.floor(sampleY);
        double? sharedFloorZ = floorZ;
        for (double y = minY; y <= maxY; y++)
        {
            for (double x = minX; x <= maxX; x++)
            {
                TerrainCell? candidate = terrainCellAt(terrain, (int)x, (int)y);
                if (candidate == null || !terrainCellCarriesChasmFloor(candidate)) return 0;
                double? candidateFloorZ = terrainChasmFloorZAt(terrain, candidate);
                if (candidateFloorZ == null) return 0;
                if (sharedFloorZ == null) sharedFloorZ = candidateFloorZ;
                else if (Math.abs(candidateFloorZ.Value - sharedFloorZ.Value) > 0.001) return 0;
            }
        }
        return 1;
    }

    private static double detailSeed(int cellX, int cellY, double salt)
    {
        // Each imul result is an int32, but their JS sum is a double: add in double so it cannot wrap.
        return (double)Math.imul(cellX, 92_837_111) + Math.imul(cellY, 689_287_499) + salt;
    }

    private static void addChasmFloorScar(
        PropGeometryBuilder builder,
        ChasmFloorDetailOptions options,
        double seed)
    {
        double angle = cellHash(seed + 11, seed - 17) * PROP_TAU;
        double length = options.tileSize * (0.2 + cellHash(seed + 23, seed - 29) * 0.24);
        double u = 0.27 + cellHash(seed + 31, seed - 37) * 0.46;
        double v = 0.27 + cellHash(seed + 41, seed - 43) * 0.46;
        double cx = options.originX + options.tileSize * u;
        double cz = options.originZ + options.tileSize * v;
        double dx = Math.cos(angle) * length;
        double dz = Math.sin(angle) * length;
        double sideX = -Math.sin(angle);
        double sideZ = Math.cos(angle);
        double bend = (cellHash(seed + 47, seed - 53) - 0.5) * length * 0.42;
        double jointX = cx + sideX * bend;
        double jointZ = cz + sideZ * bend;
        int ink = mix(options.edgeColor, 0x050706, 0.66);
        int rim = mix(options.edgeColor, 0x050706, 0.26);
        double y = chasmFloorDetailYAt(options, u, v, 0.09);
        double width = clamp(options.tileSize * 0.018, 0.48, 0.86);
        builder.addOverlayLineFlat(y, cx - dx * 0.5, cz - dz * 0.5, jointX, jointZ, width, ink, 0.17);
        builder.addOverlayLineFlat(
            y,
            jointX,
            jointZ,
            cx + dx * 0.5,
            cz + dz * 0.5,
            width * 0.82,
            ink,
            0.15);
        // One displaced lip is enough to make the mark read as a shallow geological wound instead of a painted X.
        builder.addOverlayLineFlat(
            y + 0.012,
            cx - dx * 0.46 + sideX * width,
            cz - dz * 0.46 + sideZ * width,
            jointX + sideX * width,
            jointZ + sideZ * width,
            width * 0.38,
            rim,
            0.08);
    }

    private static void addChasmFloorStone(
        PropGeometryBuilder builder,
        ChasmFloorDetailOptions options,
        double seed)
    {
        double u = 0.2 + cellHash(seed + 61, seed - 67) * 0.6;
        double v = 0.2 + cellHash(seed + 71, seed - 73) * 0.6;
        double x = options.originX + options.tileSize * u;
        double z = options.originZ + options.tileSize * v;
        double radius = options.tileSize * (0.025 + cellHash(seed + 79, seed - 83) * 0.026);
        double height = radius * (0.34 + cellHash(seed + 89, seed - 97) * 0.34);
        double y = chasmFloorDetailYAt(options, u, v, 0.02);
        propFrustum(
            builder,
            x,
            z,
            y,
            y + height,
            radius,
            radius * 0.62,
            5,
            cellHash(seed + 101, seed - 103) * PROP_TAU,
            mix(options.floorColor, options.edgeColor, 0.18),
            mix(options.edgeColor, 0x030504, 0.44),
            0.64);
    }

    private static void addChasmFloorTuft(
        PropGeometryBuilder builder,
        ChasmFloorDetailOptions options,
        double seed)
    {
        double u = 0.23 + cellHash(seed + 107, seed - 109) * 0.54;
        double v = 0.23 + cellHash(seed + 113, seed - 127) * 0.54;
        double rootX = options.originX + options.tileSize * u;
        double rootZ = options.originZ + options.tileSize * v;
        double rootY = chasmFloorDetailYAt(options, u, v, 0.04);
        double bladeCount = 3 + Math.floor(cellHash(seed + 131, seed - 137) * 4);
        double baseAngle = cellHash(seed + 139, seed - 149) * PROP_TAU;
        for (int blade = 0; blade < bladeCount; blade++)
        {
            double bladeSeed = seed + blade * 157;
            double angle =
                baseAngle + (blade / bladeCount) * PROP_TAU + (cellHash(bladeSeed, -bladeSeed) - 0.5) * 0.5;
            double height = options.tileSize * (0.075 + cellHash(bladeSeed + 5, bladeSeed - 7) * 0.075);
            double lean = height * (0.14 + cellHash(bladeSeed + 11, bladeSeed - 13) * 0.16);
            addTerrainWindBlade(builder, new WindBladeParams
            {
                rootX = rootX + Math.cos(angle) * options.tileSize * 0.018,
                rootZ = rootZ + Math.sin(angle) * options.tileSize * 0.018,
                y0 = rootY,
                height = height,
                width = clamp(options.tileSize * 0.012, 0.36, 0.62),
                angle = angle + Math.PI * 0.5,
                leanX = Math.cos(angle) * lean,
                leanZ = Math.sin(angle) * lean,
                color = mix(options.growthColor, options.edgeColor, 0.5 + (blade % 3) * 0.08),
                wind = 0.35 + cellHash(bladeSeed + 17, bladeSeed - 19) * 0.32,
                bend = 0.48,
                normalY = 0.62,
            });
        }
    }

    /// <summary>
    /// Sparse physical detail for the deep, non-walkable floor. Its restrained abyss palette and deterministic
    /// budget keep the plane distant and readable rather than turning it into a second gameplay surface.
    /// </summary>
    public static void addChasmFloorDetails(PropGeometryBuilder builder, ChasmFloorDetailOptions options)
    {
        double density = clamp(options.density, 0, 1);
        if (density <= 0) return;
        double seed = detailSeed(options.cellX, options.cellY, 1_409);
        if (cellHash(seed + 3, seed - 5) < CARTOON_TERRAIN_STYLE.chasmDepth.floorScarCoverage * density)
            addChasmFloorScar(builder, options, seed);
        if (cellHash(seed + 7, seed - 11) < CARTOON_TERRAIN_STYLE.chasmDepth.floorStoneCoverage * density)
            addChasmFloorStone(builder, options, seed + 2_003);
        if (cellHash(seed + 13, seed - 17) < CARTOON_TERRAIN_STYLE.chasmDepth.floorTuftCoverage * density)
            addChasmFloorTuft(builder, options, seed + 4_009);
    }
}
