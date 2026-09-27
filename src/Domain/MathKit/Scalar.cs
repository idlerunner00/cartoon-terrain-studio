// Port of packages/shared/src/math/scalar.ts — keep in lockstep with the original.

namespace Fluitown.Domain;

/// <summary>Scalar math helpers. Pure, allocation-free.</summary>
public static class Scalar
{
    public static double clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

    public static double clamp01(double v) => clamp(v, 0, 1);

    public static double lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Hermite smoothstep of `t` (clamped to [0,1]): 3t²−2t³.</summary>
    public static double smoothstep(double t)
    {
        double u = clamp01(t);
        return u * u * (3 - 2 * u);
    }
}
