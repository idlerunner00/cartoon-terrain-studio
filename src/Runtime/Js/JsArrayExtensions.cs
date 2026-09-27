using System;
using System.Collections.Generic;
using System.Text;

namespace Fluitown.Runtime;

/// <summary>
/// Array.prototype methods on <see cref="List{T}"/> and arrays, named as in ECMAScript so that ported code
/// can be read side by side with the TypeScript original. Semantics follow the specification for the cases
/// the ported code uses (no holes, no sparse arrays, callbacks never mutate the array they iterate).
/// </summary>
public static class JsArrayExtensions
{
    public static int push<T>(this List<T> list, T value)
    {
        list.Add(value);
        return list.Count;
    }

    public static int push<T>(this List<T> list, T a, T b)
    {
        list.Add(a);
        list.Add(b);
        return list.Count;
    }

    /// <summary>Removes and returns the last element; returns default when empty (JS: undefined).</summary>
    public static T pop<T>(this List<T> list)
    {
        int n = list.Count;
        if (n == 0) return default!;
        T v = list[n - 1];
        list.RemoveAt(n - 1);
        return v;
    }

    public static T shift<T>(this List<T> list)
    {
        if (list.Count == 0) return default!;
        T v = list[0];
        list.RemoveAt(0);
        return v;
    }

    public static int unshift<T>(this List<T> list, T value)
    {
        list.Insert(0, value);
        return list.Count;
    }

    private static int RelativeIndex(int index, int length)
    {
        if (index < 0) return Math.Max(length + index, 0);
        return Math.Min(index, length);
    }

    public static List<T> slice<T>(this List<T> list, int start = 0, int? end = null)
    {
        int n = list.Count;
        int from = RelativeIndex(start, n);
        int to = end.HasValue ? RelativeIndex(end.Value, n) : n;
        var result = new List<T>(Math.Max(0, to - from));
        for (int i = from; i < to; i++) result.Add(list[i]);
        return result;
    }

    public static T[] slice<T>(this T[] array, int start = 0, int? end = null)
    {
        int n = array.Length;
        int from = RelativeIndex(start, n);
        int to = end.HasValue ? RelativeIndex(end.Value, n) : n;
        var result = new T[Math.Max(0, to - from)];
        if (result.Length > 0) Array.Copy(array, from, result, 0, result.Length);
        return result;
    }

    public static List<T> splice<T>(this List<T> list, int start, int? deleteCount = null, params T[] items)
    {
        int n = list.Count;
        int from = RelativeIndex(start, n);
        int count = deleteCount.HasValue ? Math.Min(Math.Max(deleteCount.Value, 0), n - from) : n - from;
        var removed = list.GetRange(from, count);
        list.RemoveRange(from, count);
        if (items.Length > 0) list.InsertRange(from, items);
        return removed;
    }

    public static int indexOf<T>(this IReadOnlyList<T> list, T value)
    {
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < list.Count; i++)
            if (cmp.Equals(list[i], value)) return i;
        return -1;
    }

    public static bool includes<T>(this IReadOnlyList<T> list, T value) => indexOf(list, value) >= 0;

    public static bool some<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (int i = 0; i < list.Count; i++)
            if (predicate(list[i])) return true;
        return false;
    }

    public static bool some<T>(this IReadOnlyList<T> list, Func<T, int, bool> predicate)
    {
        for (int i = 0; i < list.Count; i++)
            if (predicate(list[i], i)) return true;
        return false;
    }

    public static bool every<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (int i = 0; i < list.Count; i++)
            if (!predicate(list[i])) return false;
        return true;
    }

    public static T find<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (int i = 0; i < list.Count; i++)
            if (predicate(list[i])) return list[i];
        return default!;
    }

    public static int findIndex<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (int i = 0; i < list.Count; i++)
            if (predicate(list[i])) return i;
        return -1;
    }

    public static List<T> filter<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        var result = new List<T>();
        for (int i = 0; i < list.Count; i++)
            if (predicate(list[i])) result.Add(list[i]);
        return result;
    }

    public static List<T> filter<T>(this IReadOnlyList<T> list, Func<T, int, bool> predicate)
    {
        var result = new List<T>();
        for (int i = 0; i < list.Count; i++)
            if (predicate(list[i], i)) result.Add(list[i]);
        return result;
    }

    public static List<TOut> map<T, TOut>(this IReadOnlyList<T> list, Func<T, TOut> selector)
    {
        var result = new List<TOut>(list.Count);
        for (int i = 0; i < list.Count; i++) result.Add(selector(list[i]));
        return result;
    }

    public static List<TOut> map<T, TOut>(this IReadOnlyList<T> list, Func<T, int, TOut> selector)
    {
        var result = new List<TOut>(list.Count);
        for (int i = 0; i < list.Count; i++) result.Add(selector(list[i], i));
        return result;
    }

    public static TAcc reduce<T, TAcc>(this IReadOnlyList<T> list, Func<TAcc, T, TAcc> reducer, TAcc initial)
    {
        TAcc acc = initial;
        for (int i = 0; i < list.Count; i++) acc = reducer(acc, list[i]);
        return acc;
    }

    public static List<T> concat<T>(this IReadOnlyList<T> list, IEnumerable<T> other)
    {
        var result = new List<T>(list);
        result.AddRange(other);
        return result;
    }

    public static string join<T>(this IReadOnlyList<T> list, string separator = ",")
    {
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(separator);
            object? v = list[i];
            sb.Append(v switch
            {
                null => "",
                double d => Js.Str(d),
                float f => Js.Str(f),
                bool b => Js.Str(b),
                _ => v.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>Returns the element or default when out of range (JS: undefined).</summary>
    public static T at<T>(this IReadOnlyList<T> list, int index)
    {
        if (index < 0) index += list.Count;
        return (uint)index < (uint)list.Count ? list[index] : default!;
    }

    /// <summary>Stable sort with an ECMAScript comparator; returns the list like Array.prototype.sort.</summary>
    public static List<T> sort<T>(this List<T> list, Func<T, T, double> compare)
    {
        Js.Sort(list, compare);
        return list;
    }

    public static T[] fill<T>(this T[] array, T value)
    {
        Array.Fill(array, value);
        return array;
    }

    public static T[] fill<T>(this T[] array, T value, int start, int? end = null)
    {
        int n = array.Length;
        int from = RelativeIndex(start, n);
        int to = end.HasValue ? RelativeIndex(end.Value, n) : n;
        for (int i = from; i < to; i++) array[i] = value;
        return array;
    }

    /// <summary>TypedArray.prototype.set(source, offset).</summary>
    public static void set<T>(this T[] array, T[] source, int offset = 0) => Array.Copy(source, 0, array, offset, source.Length);
}
