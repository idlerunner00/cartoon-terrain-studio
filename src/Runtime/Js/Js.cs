using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Fluitown.Runtime;

/// <summary>
/// ECMAScript value semantics that C# does not share: the abstract integer conversions behind bitwise
/// operators and typed-array stores, Number::toString, stable Array.prototype.sort and default (string)
/// sort. Everything here exists so that ported domain code produces byte-identical results to the
/// TypeScript original.
/// </summary>
public static class Js
{
    // ── ToInt32 / ToUint32 (bitwise operators, `x | 0`, `x >>> 0`) ─────────────────────────────────────

    /// <summary>ECMAScript ToInt32: truncate, then wrap modulo 2^32. NaN and ±Infinity become 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt32(double d)
    {
        if (d >= int.MinValue && d <= int.MaxValue) return (int)d;
        return ToInt32Slow(d);
    }

    private static int ToInt32Slow(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
        double t = Math.Truncate(d);
        double m = t % 4294967296.0;
        if (m < 0) m += 4294967296.0;
        return unchecked((int)(uint)m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt32(int i) => i;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt32(uint u) => unchecked((int)u);

    /// <summary>ECMAScript ToUint32 (<c>x &gt;&gt;&gt; 0</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ToUint32(double d) => unchecked((uint)ToInt32(d));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ToUint32(int i) => unchecked((uint)i);

    // ── Typed-array element conversions ───────────────────────────────────────────────────────────────

    /// <summary>Store conversion of a Uint8Array element.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte U8(double d) => unchecked((byte)ToInt32(d));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte U8(int i) => unchecked((byte)i);

    /// <summary>Store conversion of an Int8Array element.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte I8(double d) => unchecked((sbyte)ToInt32(d));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte I8(int i) => unchecked((sbyte)i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort U16(double d) => unchecked((ushort)ToInt32(d));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short I16(int i) => unchecked((short)i);

    // ── Truthiness ────────────────────────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Truthy(double d) => d != 0 && !double.IsNaN(d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Truthy(string? s) => !string.IsNullOrEmpty(s);

    // ── Number::toString and friends ──────────────────────────────────────────────────────────────────

    /// <summary>ECMAScript Number::toString(10) — what template literals and string concatenation print.</summary>
    public static string Str(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (d == 0) return "0";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d < 0) return "-" + Str(-d);
        if (d < 9007199254740992.0 && d == Math.Floor(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);

        // Shortest round-trip digits (.NET Core 3.0+ "R" is the shortest representation, as in ECMAScript).
        string shortest = d.ToString("R", CultureInfo.InvariantCulture);
        ExtractDigits(shortest, out string digits, out int n);
        int k = digits.Length;
        var sb = new StringBuilder();
        if (k <= n && n <= 21)
        {
            sb.Append(digits);
            sb.Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n);
            sb.Append('.');
            sb.Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -n);
            sb.Append(digits);
        }
        else
        {
            int e = n - 1;
            sb.Append(digits[0]);
            if (k > 1)
            {
                sb.Append('.');
                sb.Append(digits, 1, k - 1);
            }
            sb.Append('e');
            sb.Append(e >= 0 ? '+' : '-');
            sb.Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Str(int i) => i.ToString(CultureInfo.InvariantCulture);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Str(uint i) => i.ToString(CultureInfo.InvariantCulture);

    public static string Str(bool b) => b ? "true" : "false";

    /// <summary>Splits a .NET "R" rendering into significant digits and the ECMAScript exponent n (value = 0.digits × 10^n).</summary>
    private static void ExtractDigits(string s, out string digits, out int n)
    {
        int exp = 0;
        int ePos = s.IndexOfAny(new[] { 'E', 'e' });
        string mantissa = s;
        if (ePos >= 0)
        {
            exp = int.Parse(s.Substring(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = s.Substring(0, ePos);
        }
        int dot = mantissa.IndexOf('.');
        string intPart = dot >= 0 ? mantissa.Substring(0, dot) : mantissa;
        string fracPart = dot >= 0 ? mantissa.Substring(dot + 1) : "";
        string all = intPart + fracPart;
        int pointPos = intPart.Length + exp;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') lead++;
        all = all.Substring(lead);
        pointPos -= lead;
        int trail = all.Length;
        while (trail > 1 && all[trail - 1] == '0') trail--;
        digits = all.Substring(0, trail);
        n = pointPos;
    }

    /// <summary>ECMAScript Number.prototype.toFixed for |x| &lt; 1e21.</summary>
    public static string ToFixed(double x, int fractionDigits)
    {
        if (double.IsNaN(x)) return "NaN";
        if (Math.Abs(x) >= 1e21) return Str(x);
        // ECMAScript formats the magnitude and prefixes "-" for any x < 0, even when the digits round to
        // zero ((-0.001).toFixed(2) is "-0.00"); -0 itself is not < 0 and prints unsigned. .NET's "F"
        // format renders the exact binary value and breaks exact ties away from zero, which on a
        // non-negative magnitude is ECMAScript's "pick the larger n".
        string s = Math.Abs(x).ToString("F" + fractionDigits, CultureInfo.InvariantCulture);
        return x < 0 ? "-" + s : s;
    }

    // ── Strings ───────────────────────────────────────────────────────────────────────────────────────

    // ── Array.prototype.sort ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stable in-place sort with an ECMAScript comparator (negative ⇒ a before b). V8's TimSort is
    /// stable, and for a consistent comparator every stable sort yields the same permutation.
    /// </summary>
    public static void Sort<T>(List<T> list, Func<T, T, double> compare)
    {
        int n = list.Count;
        if (n < 2) return;
        if (n <= 16)
        {
            // The insertion sort MergeSort runs for a range this short, in place: same comparisons, same permutation,
            // without the two array copies (per-frame callers such as the light setup).
            for (int i = 1; i < n; i++)
            {
                T v = list[i];
                int j = i - 1;
                while (j >= 0 && compare(v, list[j]) < 0)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = v;
            }
            return;
        }
        var src = list.ToArray();
        var tmp = new T[n];
        MergeSort(src, tmp, 0, n, compare);
        for (int i = 0; i < n; i++) list[i] = src[i];
    }

    private static void MergeSort<T>(T[] a, T[] tmp, int lo, int hi, Func<T, T, double> compare)
    {
        if (hi - lo <= 16)
        {
            // Binary-free insertion sort: move an element left only while strictly less (stability).
            for (int i = lo + 1; i < hi; i++)
            {
                T v = a[i];
                int j = i - 1;
                while (j >= lo && compare(v, a[j]) < 0)
                {
                    a[j + 1] = a[j];
                    j--;
                }
                a[j + 1] = v;
            }
            return;
        }
        int mid = (lo + hi) >> 1;
        MergeSort(a, tmp, lo, mid, compare);
        MergeSort(a, tmp, mid, hi, compare);
        if (!(compare(a[mid], a[mid - 1]) < 0)) return;
        Array.Copy(a, lo, tmp, lo, hi - lo);
        int l = lo, r = mid, k = lo;
        while (l < mid && r < hi)
        {
            if (compare(tmp[r], tmp[l]) < 0) a[k++] = tmp[r++];
            else a[k++] = tmp[l++];
        }
        while (l < mid) a[k++] = tmp[l++];
        while (r < hi) a[k++] = tmp[r++];
    }

    // ── Misc helpers for ported code ──────────────────────────────────────────────────────────────────

    /// <summary>Bounds-checked read that mirrors <c>array[i]</c> returning undefined outside the array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool InRange<T>(T[] array, int index) => (uint)index < (uint)array.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool InRange<T>(List<T> list, int index) => (uint)index < (uint)list.Count;
}

/// <summary>ECMAScript <c>Number</c> statics.</summary>
public static class Number
{
    public const double EPSILON = 2.220446049250313e-16;
    public const double POSITIVE_INFINITY = double.PositiveInfinity;
    public const double NEGATIVE_INFINITY = double.NegativeInfinity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool isFinite(double d) => double.IsFinite(d);

    public static bool isInteger(double d) => double.IsFinite(d) && Math.Floor(d) == d;
}
