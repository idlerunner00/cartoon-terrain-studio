// Port of packages/client/src/render/environment/terrainVisualGround.ts — keep in lockstep with the original.
using System;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class TerrainVisualGround
{
    /// <summary>
    /// Ceiling of the organic ground-lift field in world px, mirrored by `organicShape.amplitudePx` in the two
    /// terrain style modules. Raised from 2.75 to 4.5 by the Break-the-Staircase pass so broad walkable sheets
    /// carry a readable roll. CPU-only by contract: `TERRAIN_GPU_VERTEX_SHAPING` stays false, the authoritative
    /// walk plane never moves, and every structural corner (water, bridge, cliff, underpass, elevation change)
    /// stays pinned at zero lift through the topology mask below.
    /// </summary>
    public const double TERRAIN_ORGANIC_LIFT_MAX_PX = 4.5;
    /// <summary>The procedural field is continuous, but its two value-noise octaves read integer lattice corners. Dense
    /// crowds therefore repeat the same few corner hashes thousands of times per frame. This exact direct-mapped
    /// cache retains those immutable scalar results; collisions only recompute and can never change the field.</summary>
    private const int NOISE_CELL_CACHE_SIZE = 1_024;
    private const int NOISE_CELL_CACHE_MASK = NOISE_CELL_CACHE_SIZE - 1;

    /// <summary>`noiseCellX` / `noiseCellY` / `noiseCellCorners` — mutable module state, one per thread.</summary>
    internal sealed class NoiseCellCache
    {
        public readonly double[] noiseCellX = new double[NOISE_CELL_CACHE_SIZE].fill(double.NaN);
        public readonly double[] noiseCellY = new double[NOISE_CELL_CACHE_SIZE].fill(double.NaN);
        public readonly double[] noiseCellCorners = new double[NOISE_CELL_CACHE_SIZE * 4];
    }

    [ThreadStatic] private static NoiseCellCache? _NOISE_CELL_CACHE;
    internal static NoiseCellCache NOISE_CELL_CACHE => _NOISE_CELL_CACHE ??= new NoiseCellCache();

    /// <summary>Strongest authored biome amplitude; the field scales every profile so this maps exactly to the ceiling.</summary>
    private static readonly double TERRAIN_ORGANIC_PROFILE_CEILING = TerrainSurfaceProfileModule.TERRAIN_SURFACE_PROFILE_BIOMES.reduce(
        (double max, string biomeKey) => Math.max(max, TerrainSurfaceProfileModule.terrainSurfaceProfileForBiome(biomeKey).formA[2]),
        TerrainSurfaceProfileModule.terrainSurfaceProfileForBiome(null).formA[2]);
    private static readonly double TERRAIN_ORGANIC_LIFT_SCALE = TERRAIN_ORGANIC_LIFT_MAX_PX / TERRAIN_ORGANIC_PROFILE_CEILING;

    /// <summary>
    /// CPU mirror of the rolling-ground vertex field in `threeTerrain.ts`.
    ///
    /// Rendering and grounding deliberately share this exact expression. The server/collision plane remains flat;
    /// only presentation roots, grounded FX and the camera receive this bounded visual lift.
    /// </summary>
    public static double terrainOrganicHeightAt(double x, double z, TerrainSurfaceProfile profile)
    {
        if (!profile.organicGround || profile.formA[2] <= 0) return 0;
        return terrainOrganicHeightPreparedAt(
            x,
            z,
            profile,
            Math.cos(profile.formA[3]),
            Math.sin(profile.formA[3]));
    }

    internal static double terrainOrganicHeightPreparedAt(
        double x,
        double z,
        TerrainSurfaceProfile profile,
        double cos,
        double sin)
    {
        double broadScale = profile.formA[0], moundScale = profile.formA[1], amplitude = profile.formA[2];
        double domainWarp = profile.formB[0], ridgeGain = profile.formB[1], terraceGain = profile.formB[2], ridgePeriod = profile.formB[3];
        double rx = cos * x - sin * z;
        double rz = sin * x + cos * z;
        double broad = terrainNoise(
            rx / Math.max(1, broadScale) + 31.7,
            rz / Math.max(1, broadScale) + 31.7);
        double mound = terrainNoise(
            (rx + broad * domainWarp) / Math.max(1, moundScale) - 17.3,
            (rz - broad * domainWarp) / Math.max(1, moundScale) - 17.3);
        double ridgePhase = (rx + broad * domainWarp * 0.42) / Math.max(12, ridgePeriod);
        double directedRidge = 1 - Math.abs(fract(ridgePhase) * 2 - 1);
        directedRidge = smoothstep(0.28, 0.86, directedRidge) - 0.5;
        double height = (broad - 0.5) * 1.18 + (mound - 0.5) * 0.72 + directedRidge * ridgeGain * 0.42;
        double terraced = Math.floor(height * 3 + 0.5) / 3;
        height = mix(height, terraced, clamp(terraceGain, 0, 0.48));
        return height * amplitude * TERRAIN_ORGANIC_LIFT_SCALE;
    }

    /// <summary>Dave Hoskins-style hash/noise mirrored from the terrain shader.</summary>
    private static double terrainNoise(double x, double y)
    {
        double ix = Math.floor(x);
        double iy = Math.floor(y);
        // Reuse the already-resolved lattice coordinates; `fract` performed these same two floors again.
        double fx = smooth01(x - ix);
        double fy = smooth01(y - iy);
        int index =
            (Math.imul(Js.ToInt32(ix), -1_640_531_527) ^ Math.imul(Js.ToInt32(iy), -2_047_114_867)) & NOISE_CELL_CACHE_MASK;
        int @base = index * 4;
        var cache = NOISE_CELL_CACHE;
        double[] noiseCellX = cache.noiseCellX;
        double[] noiseCellY = cache.noiseCellY;
        double[] noiseCellCorners = cache.noiseCellCorners;
        if (noiseCellX[index] != ix || noiseCellY[index] != iy)
        {
            noiseCellX[index] = ix;
            noiseCellY[index] = iy;
            noiseCellCorners[@base] = terrainHash12(ix, iy);
            noiseCellCorners[@base + 1] = terrainHash12(ix + 1, iy);
            noiseCellCorners[@base + 2] = terrainHash12(ix, iy + 1);
            noiseCellCorners[@base + 3] = terrainHash12(ix + 1, iy + 1);
        }
        double a = noiseCellCorners[@base];
        double b = noiseCellCorners[@base + 1];
        double c = noiseCellCorners[@base + 2];
        double d = noiseCellCorners[@base + 3];
        return mix(mix(a, b, fx), mix(c, d, fx), fy);
    }

    private static double terrainHash12(double x, double y)
    {
        double px = fract(x * 0.1031);
        double py = fract(y * 0.1031);
        double pz = fract(x * 0.1031);
        double dot = px * (pz + 31.32) + py * (py + 31.32) + pz * (px + 31.32);
        px += dot;
        py += dot;
        pz += dot;
        return fract((px + py) * pz);
    }

    private static double fract(double value)
    {
        return value - Math.floor(value);
    }

    private static double smooth01(double value)
    {
        return value * value * (3 - 2 * value);
    }

    private static double smoothstep(double lo, double hi, double value)
    {
        return smooth01(clamp((value - lo) / Math.max(1e-6, hi - lo), 0, 1));
    }

    internal static double mix(double a, double b, double t)
    {
        return a + (b - a) * t;
    }

    internal static double clamp(double value, double min, double max)
    {
        return value < min ? min : value > max ? max : value;
    }
}
