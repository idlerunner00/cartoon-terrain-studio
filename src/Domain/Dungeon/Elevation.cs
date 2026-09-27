// Port of packages/shared/src/domain/dungeon/elevation.ts — keep in lockstep with the original.
using System;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>
/// A gentle per-biome skew applied to the raw [0,1] height before banding. `gain` stretches contrast
/// around the mid (1 = neutral), `bias` shifts the whole surface up/down.
/// </summary>
public sealed class ElevationProfile
{
    /// <summary>Additive height shift in [-1,1] applied after gain.</summary>
    public double bias;
    /// <summary>Multiplicative contrast around 0.5 (1 = neutral).</summary>
    public double gain;

    public ElevationProfile(double bias, double gain)
    {
        this.bias = bias;
        this.gain = gain;
    }
}

/// <summary>
/// Fake-elevation field — the deterministic legacy height field, plus the shared lattice hash and
/// value-noise primitives every other deterministic field in the domain reuses.
/// </summary>
public static class Elevation
{
    /// <summary>The complete walkable elevation domain: 51 signed levels from −25 through +25.</summary>
    public const int MIN_ELEVATION = -25;
    public const int MAX_ELEVATION = 25;
    public const int ELEVATION_LEVELS = MAX_ELEVATION - MIN_ELEVATION + 1;

    /// <summary>The default, biome-neutral profile (no skew).</summary>
    public static readonly ElevationProfile NEUTRAL_ELEVATION_PROFILE = new(0, 1);

    // Octave cells (tiles) and amplitudes; see the original for why these keep terraces ≤1 level per tile.
    private const double OCT0_CELL = 337;
    private const double OCT0_AMP = 1;
    private const double OCT1_CELL = 165;
    private const double OCT1_AMP = 0.22;
    private const double OCT2_CELL = 75;
    private const double OCT2_AMP = 0.035;
    private const double FBM_NORM = OCT0_AMP + OCT1_AMP + OCT2_AMP;
    private const uint OCT1_SALT = 0x9e3779b9;
    private const uint OCT2_SALT = 0x85ebca6b;

    /// <summary>
    /// 2-D integer-lattice hash → [0,1). Pure, seed-mixed; bit-identical on every machine. The one hash
    /// primitive across the dungeon domain.
    /// </summary>
    public static double latticeHash(double seed, double ix, double iy)
    {
        int h = Js.ToInt32(seed) ^ Math.imul(Js.ToInt32(ix), 0x27d4eb2f) ^ Math.imul(Js.ToInt32(iy), 0x165667b1);
        h = Math.imul(h ^ (int)((uint)h >> 15), 0x2c1b3c6d);
        h = Math.imul(h ^ (int)((uint)h >> 13), 0x297a2d39);
        h ^= (int)((uint)h >> 16);
        return (uint)h / 4294967296.0;
    }

    /// <summary>Smoothstep (3t²−2t³) — C¹ continuous, so the banded surface has no lattice-aligned creases.</summary>
    private static double smooth(double t) => t * t * (3 - 2 * t);

    /// <summary>One octave of smoothed bilinear value noise at continuous tile coords → [0,1].</summary>
    public static double valueNoise(double seed, double x, double y, double cell)
    {
        double gx = x / cell;
        double gy = y / cell;
        double ix = Math.floor(gx);
        double iy = Math.floor(gy);
        double fx = smooth(gx - ix);
        double fy = smooth(gy - iy);
        double v00 = latticeHash(seed, ix, iy);
        double v10 = latticeHash(seed, ix + 1, iy);
        double v01 = latticeHash(seed, ix, iy + 1);
        double v11 = latticeHash(seed, ix + 1, iy + 1);
        double a = v00 + (v10 - v00) * fx;
        double b = v01 + (v11 - v01) * fx;
        return a + (b - a) * fy;
    }

    /// <summary>Normalized fractal Brownian motion over the shared value-noise primitive.</summary>
    public static double fractalValueNoise(
        double seed,
        double x,
        double y,
        double largestCell,
        double octaves = 4,
        double lacunarity = 2,
        double persistence = 0.5)
    {
        if (largestCell <= 0 || octaves < 1 || lacunarity <= 1 || persistence <= 0 || persistence > 1)
            throw new InvalidOperationException("Invalid fractal value-noise parameters");
        double value = 0;
        double normalization = 0;
        double amplitude = 1;
        double cell = largestCell;
        for (int octave = 0; octave < Math.floor(octaves); octave++)
        {
            double octaveSeed = (uint)(Js.ToInt32(seed) ^ Math.imul(octave + 1, unchecked((int)0x9e3779b9)));
            value += valueNoise(octaveSeed, x, y, cell) * amplitude;
            normalization += amplitude;
            amplitude *= persistence;
            cell /= lacunarity;
        }
        return value / normalization;
    }

    /// <summary>The raw three-octave height at a global tile coordinate, in [0,1].</summary>
    private static double fbm(double seed, double gtx, double gty)
    {
        double o0 = valueNoise(seed, gtx, gty, OCT0_CELL);
        double o1 = valueNoise((uint)(Js.ToInt32(seed) ^ unchecked((int)OCT1_SALT)), gtx, gty, OCT1_CELL);
        double o2 = valueNoise((uint)(Js.ToInt32(seed) ^ unchecked((int)OCT2_SALT)), gtx, gty, OCT2_CELL);
        return (o0 * OCT0_AMP + o1 * OCT1_AMP + o2 * OCT2_AMP) / FBM_NORM;
    }

    /// <summary>Apply a biome profile (contrast around the mid, then bias) and clamp back into [0,1].</summary>
    private static double applyProfile(double h, ElevationProfile p)
    {
        double v = 0.5 + (h - 0.5) * p.gain + p.bias;
        return v < 0 ? 0 : v > 1 ? 1 : v;
    }

    /// <summary>Continuous ground height at a GLOBAL tile coordinate, in [0,1].</summary>
    public static double elevationHeightAt(double seed, double gtx, double gty, ElevationProfile? profile = null) =>
        applyProfile(fbm(Js.ToUint32(seed), gtx, gty), profile ?? NEUTRAL_ELEVATION_PROFILE);

    /// <summary>Discrete elevation LEVEL (MIN_ELEVATION..MAX_ELEVATION) at a global tile coordinate.</summary>
    public static double elevationLevelAt(double seed, double gtx, double gty, ElevationProfile? profile = null)
    {
        double h = elevationHeightAt(seed, gtx, gty, profile);
        double band = Math.floor(h * ELEVATION_LEVELS);
        double level = MIN_ELEVATION + band;
        return level < MIN_ELEVATION ? MIN_ELEVATION : level > MAX_ELEVATION ? MAX_ELEVATION : level;
    }
}
