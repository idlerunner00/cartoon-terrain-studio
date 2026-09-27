// Port of packages/client/src/render/environment/terrainGeometryCompilerFields.ts — keep in lockstep with the original.
using Fluitown.Domain;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>Pure allocation-free scalar and colour fields used by the terrain geometry compiler.</summary>
public static partial class TerrainGeometryCompilerFields
{
    private static readonly int[] CITY_NEON_COLORS = { 0x38ffe0, 0xff2bd6, 0xb6ff3d, 0xf4f6ff };
    private static readonly int[] OLYMPIAN_GOLD_COLORS = { 0xffe28a, 0xd8aa38, 0xfff2b8, 0xb9892f };
    private static readonly int[] CATHEDRAL_STAR_COLORS = { 0xffc55a, 0xfff0bd, 0xf0e3c4, 0xff6a2a };

    /// <summary>Deterministic 2D cell hash on world tile coordinates: chunk- and seam-stable.</summary>
    public static double cellHash(double x, double y)
    {
        int h = Math.imul(Js.ToInt32(x), 0x27d4eb2f) ^ Math.imul(Js.ToInt32(y), 0x165667b1);
        h = Math.imul(h ^ (int)((uint)h >> 15), 0x85ebca6b);
        h = Math.imul(h ^ (int)((uint)h >> 13), 0xc2b2ae35);
        return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
    }

    public static int cityNeonColor(double seed)
    {
        return CITY_NEON_COLORS[
            (int)(Math.floor(WorldPropPrimitives.propHash(seed) * CITY_NEON_COLORS.Length) % CITY_NEON_COLORS.Length)];
    }

    public static int olympianGold(double seed)
    {
        return OLYMPIAN_GOLD_COLORS[
            (int)(Math.floor(WorldPropPrimitives.propHash(seed) * OLYMPIAN_GOLD_COLORS.Length) % OLYMPIAN_GOLD_COLORS.Length)];
    }

    public static int cathedralStarfire(double seed)
    {
        return CATHEDRAL_STAR_COLORS[
            (int)(Math.floor(WorldPropPrimitives.propHash(seed) * CATHEDRAL_STAR_COLORS.Length) % CATHEDRAL_STAR_COLORS.Length)];
    }

    public static double smoothCellNoise(double x, double y, double scale, double salt)
    {
        double sx = x / scale;
        double sy = y / scale;
        double ix = Math.floor(sx);
        double iy = Math.floor(sy);
        double fx = sx - ix;
        double fy = sy - iy;
        double ux = fx * fx * (3 - 2 * fx);
        double uy = fy * fy * (3 - 2 * fy);
        double hx = salt * 131;
        double hy = salt * -197;
        double a = cellHash(ix + hx, iy + hy);
        double b = cellHash(ix + 1 + hx, iy + hy);
        double c = cellHash(ix + hx, iy + 1 + hy);
        double d = cellHash(ix + 1 + hx, iy + 1 + hy);
        return (a + (b - a) * ux) * (1 - uy) + (c + (d - c) * ux) * uy;
    }

    public static double terrainCapShade(double wx, double wy, bool isRock, bool isBridge)
    {
        double macro = smoothCellNoise(wx, wy, isRock ? 7.5 : isBridge ? 5.5 : 8.5, 17) - 0.5;
        double wash = smoothCellNoise(wx + 37, wy - 19, isBridge ? 11 : 18, 23) - 0.5;
        double detail = smoothCellNoise(wx - 11, wy + 7, isBridge ? 3.2 : 4.4, 41) - 0.5;
        double shade = isRock
            ? 1 + macro * 0.052 + wash * 0.035 + detail * 0.008
            : isBridge
                ? 1 + macro * 0.046 + wash * 0.032 + detail * 0.011
                : 1 + macro * 0.064 + wash * 0.045 + detail * 0.006;
        return clamp(shade, 0.94, 1.07);
    }

    public static double bilinearShade(double nw, double ne, double se, double sw, double u, double v)
    {
        return (nw + (ne - nw) * u) * (1 - v) + (sw + (se - sw) * u) * v;
    }

    public static int bilinearColor(int nw, int ne, int se, int sw, double u, double v)
    {
        return Palette.mix(Palette.mix(nw, ne, u), Palette.mix(sw, se, u), v);
    }

    /// <summary>
    /// The cap's own corner blend, C1 across the cell boundary.
    ///
    /// Two lattices used to cross every walkable cap. One is the semantic floor field, sampled at CELL CENTRES
    /// (see <see cref="terrainFloorFieldAt"/>); the other is this — the four cell CORNER pigments blended over the cap.
    /// Their breaks are half a cell apart, which is exactly the ~20 world px period the frame review measured as a
    /// 1-px dark grid at 27.1 screen px. The corner VALUES are shared with the neighbouring cell either way, so
    /// the pigment was already continuous; what the eye drew was the gradient step. Fading with the quintic makes
    /// both one-sided gradients zero at the boundary, so there is no step left to read.
    ///
    /// Geometry deliberately does NOT use this: vertex positions stay linear in `u, v` so patches keep tiling.
    /// </summary>
    public static int capCornerColor(int nw, int ne, int se, int sw, double u, double v)
    {
        return bilinearColor(nw, ne, se, sw, quinticFade(u), quinticFade(v));
    }

    /// <summary><see cref="capCornerColor"/>'s twin for the cap's corner SHADE — same lattice, same fade, same reason.</summary>
    public static double capCornerShade(double nw, double ne, double se, double sw, double u, double v)
    {
        return bilinearShade(nw, ne, se, sw, quinticFade(u), quinticFade(v));
    }

    /// <summary>
    /// Quintic (Perlin's) fade — the C2 interpolant.
    ///
    /// This is the whole difference between a smooth ground and a lattice. A LINEAR fade makes bilinear
    /// interpolation C0: the value is continuous across a cell boundary but its GRADIENT jumps there, and a
    /// gradient jump on an axis-aligned line is a Mach band — the eye draws an edge the data does not contain.
    /// The same break kinks every iso-contour of the field at the same lines, which is what made soil islands and
    /// zone boundaries resolve as polygons with 45/90-degree corners on a half-cell lattice.
    ///
    /// `t³(6t² − 15t + 10)` has zero first AND second derivative at both ends, so the interpolated field's
    /// gradient matches across every cell boundary: no band, and iso-contours cross the lattice as curves. It is
    /// two extra multiply-adds per sample, paid once per vertex in the bake worker — nothing per frame.
    /// </summary>
    public static double quinticFade(double t)
    {
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>Bilinear sampling over cell-centred semantic floor fields; continuous at tile and bake seams.</summary>
    public static double terrainFloorFieldAt(
        TerrainRenderPlan plan,
        TerrainCell cell,
        double u,
        double v,
        float[] field)
    {
        var terrain = plan.terrain;
        if (terrain == null || field.Length != terrain.cells.Length) return 0;
        double px = cell.x + u - 0.5;
        double py = cell.y + v - 0.5;
        double x0 = Math.floor(px);
        double y0 = Math.floor(py);
        // The fade is quintic, not linear: see {@link quinticFade}. The sampled CORNERS are unchanged, so this
        // does not move a single field value at a cell centre — it removes the derivative break BETWEEN them.
        double tx = quinticFade(px - x0);
        double ty = quinticFade(py - y0);
        double sample(double x, double y)
        {
            double sx = clamp(x, 0, terrain.width - 1);
            double sy = clamp(y, 0, terrain.height - 1);
            // `field[sy * terrain.width + sx] ?? 0` — a NaN index (NaN u/v) reads undefined.
            double index = sy * terrain.width + sx;
            return index >= 0 && index < field.Length ? field[(int)index] : 0;
        }
        return bilinearShade(
            sample(x0, y0),
            sample(x0 + 1, y0),
            sample(x0 + 1, y0 + 1),
            sample(x0, y0 + 1),
            tx,
            ty);
    }

    /// <param name="dir">A Fluitown.Domain.TerrainEdgeDirection key: "n" | "e" | "s" | "w".</param>
    /// <returns>`[{ x, z }, { x, z }]` as a pair of fresh value tuples.</returns>
    public static ((double x, double z) a, (double x, double z) b) capEdgeSegment(
        double x0,
        double x1,
        double z0,
        double z1,
        string dir,
        double startInset = 0,
        double endInset = 0)
    {
        if (dir == "n")
            return (
                (x0 + startInset, z0),
                (x1 - endInset, z0));
        if (dir == "s")
            return (
                (x0 + startInset, z1),
                (x1 - endInset, z1));
        if (dir == "e")
            return (
                (x1, z0 + startInset),
                (x1, z1 - endInset));
        return (
            (x0, z0 + startInset),
            (x0, z1 - endInset));
    }

    public static double clamp(double value, double min, double max)
    {
        return Math.max(min, Math.min(max, value));
    }

    public static double clamp01(double value)
    {
        return clamp(value, 0, 1);
    }

    private static double waterSplineWeight(double distance)
    {
        double d = Math.abs(distance);
        if (d < 1) return 2.0 / 3 - d * d + (d * d * d) / 2;
        if (d < 2)
        {
            double remainder = 2 - d;
            return (remainder * remainder * remainder) / 6;
        }
        return 0;
    }

    /// <summary>C2-continuous visual hydrology sampled in absolute grid coordinates for byte-identical shared seams.</summary>
    public static double visualWaterLevelAt(MaterializedTerrain terrain, double gridX, double gridY, double fallback)
    {
        double weightedLevel = 0;
        double totalWeight = 0;
        int minX = (int)Math.floor(gridX) - 2;
        int minY = (int)Math.floor(gridY) - 2;
        for (int ty = minY; ty <= minY + 4; ty++)
        {
            double weightY = waterSplineWeight(gridY - (ty + 0.5));
            if (weightY <= 0) continue;
            for (int tx = minX; tx <= minX + 4; tx++)
            {
                double weightX = waterSplineWeight(gridX - (tx + 0.5));
                if (weightX <= 0) continue;
                var sample = TerrainModel.terrainCellAt(terrain, tx, ty);
                if (
                    sample == null ||
                    !TerrainModel.terrainCellCarriesWater(sample) ||
                    sample.waterLevel == null ||
                    (sample.type == TileType.Bridge && !TerrainModel.bridgeWaterIsSafelyBelowDeck(sample)))
                    continue;
                double weight = weightX * weightY;
                weightedLevel += sample.waterLevel.Value * weight;
                totalWeight += weight;
            }
        }
        return TerrainModel.constrainTerrainWaterLevelBelowBridges(
            terrain,
            gridX,
            gridY,
            totalWeight > 0.000001 ? weightedLevel / totalWeight : fallback);
    }

    private const double WATER_TRANSITION_MAX_LIGHTING_SLOPE = 0.58;

    /// <summary>Writes the seam-stable, deliberately calm lighting normal for one point of the exact hydraulic field.</summary>
    public static void visualWaterNormalAt(
        MaterializedTerrain terrain,
        double gridX,
        double gridY,
        double fallback,
        double elevationStep,
        double tileSize,
        int index,
        double[] normalX,
        double[] normalY,
        double[] normalZ)
    {
        const double delta = 0.035;
        double dLevelX =
            visualWaterLevelAt(terrain, gridX + delta, gridY, fallback) -
            visualWaterLevelAt(terrain, gridX - delta, gridY, fallback);
        double dLevelZ =
            visualWaterLevelAt(terrain, gridX, gridY + delta, fallback) -
            visualWaterLevelAt(terrain, gridX, gridY - delta, fallback);
        double slopeX = -(dLevelX * elevationStep) / (delta * 2 * tileSize);
        double slopeZ = -(dLevelZ * elevationStep) / (delta * 2 * tileSize);
        double slopeLength = Math.hypot(slopeX, slopeZ);
        if (slopeLength > WATER_TRANSITION_MAX_LIGHTING_SLOPE)
        {
            double scale = WATER_TRANSITION_MAX_LIGHTING_SLOPE / slopeLength;
            slopeX *= scale;
            slopeZ *= scale;
        }
        normalX[index] = slopeX;
        normalY[index] = 1;
        normalZ[index] = slopeZ;
    }

    public static double waterfallDescentCurve(double progress)
    {
        // Horizontal at the crest, vertical by the time it enters the Chasm cloud. The former symmetric smoothstep
        // also flattened at the bottom, so the carrier read as one straight diagonal card with two tiny kinks.
        return progress * progress * (2 - progress);
    }

    public static double waterfallDescentTangent(double progress)
    {
        return progress * (4 - 3 * progress);
    }

    public static double waterfallLandingCurve(double progress, bool projectionCritical)
    {
        _ = projectionCritical;
        double inverse = 1 - progress;
        // Fast crest roll, then a true plumb body. Together with waterfallDescentCurve this is a quarter-turning
        // sheet: horizontal source tangent at p=0, vertical fall tangent at p=1.
        return 1 - inverse * inverse * inverse;
    }

    public static double waterfallLandingTangent(double progress, bool projectionCritical)
    {
        _ = projectionCritical;
        double inverse = 1 - progress;
        return 3 * inverse * inverse;
    }
}
