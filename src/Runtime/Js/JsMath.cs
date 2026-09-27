using System;
using System.Runtime.CompilerServices;

namespace Fluitown.Runtime;

/// <summary>
/// ECMAScript <c>Math</c> with V8's results. Ported code calls these instead of <see cref="System.Math"/> for
/// everything that is not correctly rounded by IEEE 754 itself (sqrt, floor, ceil, abs, min, max and the basic
/// operators already agree bit for bit), so the terrain stays a pure function of its seed.
/// <para>
/// The transcendental functions are V8's fdlibm port (<see cref="Ieee754"/>). <c>Math.pow</c> is V8's own special
/// cases in front of the C runtime's pow (--use-std-math-pow), which is also what <see cref="System.Math.Pow"/> calls.
/// STUDIO: <c>Math.sin/cos</c> are fdlibm's too, as in V8 up to version 11. Newer V8 switched to glibc's routines,
/// whose licence (LGPL) does not fit this project; the two differ in the last bit for a few percent of the results.
/// </para>
/// </summary>
public static class JsMath
{
    public const double PI = Math.PI;
    public const double SQRT2 = 1.4142135623730951;
    public const double SQRT1_2 = 0.7071067811865476;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double sin(double x) => Ieee754.Sin(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double cos(double x) => Ieee754.Cos(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double tan(double x) => Ieee754.Tan(x);
    public static double atan(double x) => Ieee754.Atan(x);
    public static double atan2(double y, double x) => Ieee754.Atan2(y, x);
    public static double exp(double x) => Ieee754.Exp(x);
    public static double log(double x) => Ieee754.Log(x);

    /// <summary><c>Math.pow</c> and the <c>**</c> operator.</summary>
    public static double pow(double x, double y)
    {
        // v8::internal::math::pow (src/numbers/ieee754.cc, V8 14.9).
        if (double.IsNaN(y)) return double.NaN;                               // 1. exponent NaN → NaN
        if (double.IsInfinity(y) && (x == 1 || x == -1)) return double.NaN;   // 9b/10b. |base| = 1, exponent ±∞ → NaN
        // Special cases that exist to match the optimizing compilers, which avoid calling pow for them.
        if (y == 2) return x * x;
        if (y == 0.5) return double.IsInfinity(x) ? double.PositiveInfinity : Math.Sqrt(x + 0);
        // --use-std-math-pow (default on): std::pow of the C runtime.
        return Math.Pow(x, y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double sqrt(double x) => Math.Sqrt(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double floor(double x) => Math.Floor(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ceil(double x) => Math.Ceiling(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double abs(double x) => Math.Abs(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double trunc(double x) => Math.Truncate(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double fround(double x) => (float)x;

    /// <summary>
    /// <c>Math.round</c>: ties go towards +Infinity and the sign of zero is kept (Math.round(-0.4) is -0).
    /// This is V8's own lowering (ceil, then step back when the ceiling overshot by more than a half).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double round(double x)
    {
        double r = Math.Ceiling(x);
        if (r - 0.5 > x) r -= 1.0;
        return r;
    }

    /// <summary><c>Math.sign</c>: keeps -0/+0 and NaN.</summary>
    public static double sign(double x)
    {
        if (double.IsNaN(x) || x == 0) return x;
        return x > 0 ? 1 : -1;
    }

    // Math.min / Math.max: .NET Core 3.0+ already returns NaN for NaN inputs and orders -0 below +0,
    // exactly like ECMAScript, so these are thin aliases kept for a 1:1 reading of ported code.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double min(double a, double b) => Math.Min(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double max(double a, double b) => Math.Max(a, b);

    public static double min(double a, double b, double c) => Math.Min(Math.Min(a, b), c);
    public static double max(double a, double b, double c) => Math.Max(Math.Max(a, b), c);
    public static double min(double a, double b, double c, double d) => Math.Min(Math.Min(a, b), Math.Min(c, d));
    public static double max(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    public static double min(params double[] values)
    {
        double r = double.PositiveInfinity;
        foreach (double v in values) r = Math.Min(r, v);
        return r;
    }

    public static double max(params double[] values)
    {
        double r = double.NegativeInfinity;
        foreach (double v in values) r = Math.Max(r, v);
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int min(int a, int b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int max(int a, int b) => a > b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int abs(int a) => a < 0 ? -a : a;

    /// <summary>V8's <c>Math.hypot</c>: normalise by the largest magnitude, then Kahan-sum the squares.</summary>
    public static double hypot(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
        {
            if (double.IsInfinity(a) || double.IsInfinity(b)) return double.PositiveInfinity;
            return double.NaN;
        }
        double aa = Math.Abs(a), ab = Math.Abs(b);
        double max = aa > ab ? aa : ab;
        if (max == double.PositiveInfinity) return double.PositiveInfinity;
        if (max == 0) return 0;
        double sum = 0, compensation = 0;
        double n = aa / max;
        double summand = n * n - compensation;
        double preliminary = sum + summand;
        compensation = (preliminary - sum) - summand;
        sum = preliminary;
        n = ab / max;
        summand = n * n - compensation;
        preliminary = sum + summand;
        sum = preliminary;
        return Math.Sqrt(sum) * max;
    }

    /// <summary>
    /// <c>hypot()</c> for three arguments without the argument array (called per vertex by the bake
    /// compiler): the same NaN/Infinity rules, the same normalisation by the largest magnitude and the same Kahan
    /// sum in argument order, so the result is bit-identical.
    /// </summary>
    public static double hypot(double a, double b, double c)
    {
        bool oneArgIsNaN = false;
        double max = 0, aa = 0, ab = 0, ac = 0;
        if (double.IsNaN(a)) oneArgIsNaN = true;
        else
        {
            aa = Math.Abs(a);
            if (aa > max) max = aa;
        }
        if (double.IsNaN(b)) oneArgIsNaN = true;
        else
        {
            ab = Math.Abs(b);
            if (ab > max) max = ab;
        }
        if (double.IsNaN(c)) oneArgIsNaN = true;
        else
        {
            ac = Math.Abs(c);
            if (ac > max) max = ac;
        }
        if (max == double.PositiveInfinity) return double.PositiveInfinity;
        if (oneArgIsNaN) return double.NaN;
        if (max == 0) return 0;
        double sum = 0, compensation = 0;
        double n = aa / max;
        double summand = n * n - compensation;
        double preliminary = sum + summand;
        compensation = (preliminary - sum) - summand;
        sum = preliminary;
        n = ab / max;
        summand = n * n - compensation;
        preliminary = sum + summand;
        compensation = (preliminary - sum) - summand;
        sum = preliminary;
        n = ac / max;
        summand = n * n - compensation;
        preliminary = sum + summand;
        sum = preliminary;
        return Math.Sqrt(sum) * max;
    }

    /// <summary><c>Math.imul</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int imul(int a, int b) => unchecked(a * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int imul(double a, double b) => unchecked(Js.ToInt32(a) * Js.ToInt32(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int imul(uint a, int b) => unchecked((int)a * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int imul(int a, uint b) => unchecked(a * (int)b);

    /// <summary><c>Math.clz32</c>.</summary>
    public static int clz32(double x) => System.Numerics.BitOperations.LeadingZeroCount(Js.ToUint32(x));
}
