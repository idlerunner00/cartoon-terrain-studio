// Port of packages/client/src/render/environment/terrainContourGeometry.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/*
 * Allocation-free contour-cap helper shared by synchronous and worker terrain compilation.
 *
 * Authoritative collision stays a square lattice; this only writes the existing visible cap polygon.
 */

public sealed class TerrainContourPoint
{
    public double x;
    public double y;
    public double z;
}

// `export type TerrainContourCorner = 'nw' | 'ne' | 'se' | 'sw';` → string.

public static partial class TerrainContourGeometry
{
    /// <summary>`{ minimum: number; maximum: number }` out-object of <see cref="terrainContourAxisIntervalAt"/>.</summary>
    public sealed class AxisInterval
    {
        public double minimum;
        public double maximum;
    }

    /// <summary>
    /// The module-level scratch storage (`CAP_STORAGE`, `CAP_POINTS`, `ARC_STORAGE`, `BAND_CLIP_A/B`,
    /// `BAND_POINTS`). Mutable, so one instance per thread — one module instance per JS worker in the original.
    /// </summary>
    internal sealed class Scratch
    {
        public readonly TerrainContourPoint[] CAP_STORAGE = points(20);
        public readonly List<TerrainContourPoint> CAP_POINTS = new();
        public readonly TerrainContourPoint[] ARC_STORAGE = points(4);
        public readonly TerrainContourPoint[] BAND_CLIP_A = points(24);
        public readonly TerrainContourPoint[] BAND_CLIP_B = points(24);
        public readonly List<TerrainContourPoint> BAND_POINTS = new();

        private static TerrainContourPoint[] points(int length)
        {
            // `Array.from({ length }, () => ({ x: 0, y: 0, z: 0 }))`
            var result = new TerrainContourPoint[length];
            for (int i = 0; i < length; i++) result[i] = new TerrainContourPoint();
            return result;
        }
    }

    [ThreadStatic] private static Scratch? _SCRATCH;
    internal static Scratch SCRATCH => _SCRATCH ??= new Scratch();

    private static readonly double CONTOUR_ARC_INSET = 1 - Math.sqrt(3) * 0.5;
    private const double CLIP_EPSILON = 1e-5;

    /// <summary>`point[axis]` for the `'x' | 'z'` (and, defensively, 'y') keys; any other key reads undefined → NaN.</summary>
    private static double axisValue(TerrainContourPoint point, string axis)
    {
        return axis == "x" ? point.x : axis == "z" ? point.z : axis == "y" ? point.y : double.NaN;
    }

    /// <summary>
    /// Write the exact three-facet quarter-round used by ordinary terrain caps.
    ///
    /// Points follow the clockwise cap outline: west-to-north (NW), north-to-east (NE), east-to-south (SE) and
    /// south-to-west (SW). Exposing this one primitive keeps deep-floor caps and their vertical closures on the
    /// identical silhouette instead of approximating the same corner twice.
    /// </summary>
    /// <param name="corner">TerrainContourCorner: "nw" | "ne" | "se" | "sw".</param>
    /// <returns>The thread's `ARC_STORAGE` (4 points), valid until the next call.</returns>
    public static TerrainContourPoint[] terrainContourCornerArcInto(
        string corner,
        double x0,
        double x1,
        double z0,
        double z1,
        double y,
        double radius)
    {
        var ARC_STORAGE = SCRATCH.ARC_STORAGE;
        void point(int index, double x, double z)
        {
            var @out = ARC_STORAGE[index];
            @out.x = x;
            @out.y = y;
            @out.z = z;
        }
        if (corner == "nw")
        {
            point(0, x0, z0 + radius);
            point(1, x0 + radius * CONTOUR_ARC_INSET, z0 + radius * 0.5);
            point(2, x0 + radius * 0.5, z0 + radius * CONTOUR_ARC_INSET);
            point(3, x0 + radius, z0);
        }
        else if (corner == "ne")
        {
            point(0, x1 - radius, z0);
            point(1, x1 - radius * 0.5, z0 + radius * CONTOUR_ARC_INSET);
            point(2, x1 - radius * CONTOUR_ARC_INSET, z0 + radius * 0.5);
            point(3, x1, z0 + radius);
        }
        else if (corner == "se")
        {
            point(0, x1, z1 - radius);
            point(1, x1 - radius * CONTOUR_ARC_INSET, z1 - radius * 0.5);
            point(2, x1 - radius * 0.5, z1 - radius * CONTOUR_ARC_INSET);
            point(3, x1 - radius, z1);
        }
        else
        {
            point(0, x0 + radius, z1);
            point(1, x0 + radius * 0.5, z1 - radius * CONTOUR_ARC_INSET);
            point(2, x0 + radius * CONTOUR_ARC_INSET, z1 - radius * 0.5);
            point(3, x0, z1 - radius);
        }
        return ARC_STORAGE;
    }

    /// <summary>Write one clockwise convex cap with up to four deterministic three-facet quarter-rounds.</summary>
    /// <returns>The thread's `CAP_POINTS`, valid until the next call.</returns>
    public static List<TerrainContourPoint> contourCapInto(
        double x0,
        double x1,
        double z0,
        double z1,
        double y,
        double nw,
        double ne,
        double se,
        double sw,
        double n,
        double e,
        double s,
        double w)
    {
        var scratch = SCRATCH;
        var CAP_POINTS = scratch.CAP_POINTS;
        var CAP_STORAGE = scratch.CAP_STORAGE;
        CAP_POINTS.Clear();
        int count = 0;
        void point(double x, double z, double drop = 0)
        {
            var @out = CAP_STORAGE[count++];
            @out.x = x;
            @out.y = y - drop;
            @out.z = z;
            CAP_POINTS.push(@out);
        }
        if (nw > 0)
        {
            var arc = terrainContourCornerArcInto("nw", x0, x1, z0, z1, y, nw);
            point(arc[3].x, arc[3].z);
        }
        else point(x0, z0);
        if (n > 0) point((x0 + x1) * 0.5, z0, n);
        if (ne > 0)
        {
            var arc = terrainContourCornerArcInto("ne", x0, x1, z0, z1, y, ne);
            foreach (var arcPoint in arc) point(arcPoint.x, arcPoint.z);
        }
        else point(x1, z0);
        if (e > 0) point(x1, (z0 + z1) * 0.5, e);
        if (se > 0)
        {
            var arc = terrainContourCornerArcInto("se", x0, x1, z0, z1, y, se);
            foreach (var arcPoint in arc) point(arcPoint.x, arcPoint.z);
        }
        else point(x1, z1);
        if (s > 0) point((x0 + x1) * 0.5, z1, s);
        if (sw > 0)
        {
            var arc = terrainContourCornerArcInto("sw", x0, x1, z0, z1, y, sw);
            foreach (var arcPoint in arc) point(arcPoint.x, arcPoint.z);
        }
        else point(x0, z1);
        if (w > 0) point(x0, (z0 + z1) * 0.5, w);
        if (nw > 0)
        {
            var arc = terrainContourCornerArcInto("nw", x0, x1, z0, z1, y, nw);
            point(arc[0].x, arc[0].z);
            point(arc[1].x, arc[1].z);
            point(arc[2].x, arc[2].z);
        }
        return CAP_POINTS;
    }

    /// <summary>
    /// Clip one convex terrain contour to an axis-aligned band.
    ///
    /// Bridge planks, soffits and substructure consume the cap's actual polygon rather than re-stating its corner
    /// equations. Sutherland-Hodgman clipping keeps that single organic footprint authoritative for every coherent
    /// radius policy. The returned scratch array is valid until the next call.
    /// </summary>
    /// <remarks>
    /// Passing the previous result back in (the polygon IS the thread's `BAND_POINTS`) clears it first, exactly like
    /// the original's `BAND_POINTS.length = 0`, and therefore yields an empty band.
    /// </remarks>
    /// <param name="axis">"x" | "z".</param>
    public static List<TerrainContourPoint> terrainContourAxisBandInto(
        IReadOnlyList<TerrainContourPoint> polygon,
        string axis,
        double minimum,
        double maximum)
    {
        var scratch = SCRATCH;
        var BAND_POINTS = scratch.BAND_POINTS;
        BAND_POINTS.Clear();
        if (polygon.Count < 3 || maximum <= minimum + CLIP_EPSILON) return BAND_POINTS;

        var source = scratch.BAND_CLIP_A;
        var target = scratch.BAND_CLIP_B;
        int sourceCount = polygon.Count;
        for (int index = 0; index < sourceCount; index++) copyPoint(source[index], polygon[index]);

        void clip(double boundary, bool keepGreater)
        {
            int targetCount = 0;
            if (sourceCount == 0) return;
            var previous = source[sourceCount - 1];
            double previousValue = axisValue(previous, axis);
            bool previousInside = keepGreater
                ? previousValue >= boundary - CLIP_EPSILON
                : previousValue <= boundary + CLIP_EPSILON;
            for (int index = 0; index < sourceCount; index++)
            {
                var current = source[index];
                double currentValue = axisValue(current, axis);
                bool currentInside = keepGreater
                    ? currentValue >= boundary - CLIP_EPSILON
                    : currentValue <= boundary + CLIP_EPSILON;
                if (currentInside != previousInside)
                {
                    double denominator = currentValue - previousValue;
                    double progress =
                        Math.abs(denominator) < CLIP_EPSILON
                            ? 0
                            : (boundary - previousValue) / denominator;
                    var intersection = target[targetCount++];
                    intersection.x = previous.x + (current.x - previous.x) * progress;
                    intersection.y = previous.y + (current.y - previous.y) * progress;
                    intersection.z = previous.z + (current.z - previous.z) * progress;
                }
                if (currentInside) copyPoint(target[targetCount++], current);
                previous = current;
                previousValue = currentValue;
                previousInside = currentInside;
            }
            sourceCount = targetCount;
            var swap = source;
            source = target;
            target = swap;
        }

        clip(minimum, true);
        clip(maximum, false);
        for (int index = 0; index < sourceCount; index++) BAND_POINTS.push(source[index]);
        return BAND_POINTS;
    }

    /// <summary>Interval of a convex contour on one vertical/horizontal sampling line, or `false` outside the polygon.</summary>
    /// <param name="fixedAxis">"x" | "z".</param>
    public static bool terrainContourAxisIntervalAt(
        IReadOnlyList<TerrainContourPoint> polygon,
        string fixedAxis,
        double coordinate,
        AxisInterval @out)
    {
        string valueAxis = fixedAxis == "x" ? "z" : "x";
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        for (int index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            double av = axisValue(a, fixedAxis);
            double bv = axisValue(b, fixedAxis);
            if (
                coordinate < Math.min(av, bv) - CLIP_EPSILON ||
                coordinate > Math.max(av, bv) + CLIP_EPSILON)
                continue;
            if (Math.abs(bv - av) < CLIP_EPSILON)
            {
                if (Math.abs(coordinate - av) > CLIP_EPSILON) continue;
                minimum = Math.min(minimum, axisValue(a, valueAxis), axisValue(b, valueAxis));
                maximum = Math.max(maximum, axisValue(a, valueAxis), axisValue(b, valueAxis));
                continue;
            }
            double progress = (coordinate - av) / (bv - av);
            double value = axisValue(a, valueAxis) + (axisValue(b, valueAxis) - axisValue(a, valueAxis)) * progress;
            minimum = Math.min(minimum, value);
            maximum = Math.max(maximum, value);
        }
        if (!Number.isFinite(minimum) || !Number.isFinite(maximum)) return false;
        @out.minimum = minimum;
        @out.maximum = maximum;
        return maximum > minimum + CLIP_EPSILON;
    }

    private static void copyPoint(TerrainContourPoint target, TerrainContourPoint source)
    {
        target.x = source.x;
        target.y = source.y;
        target.z = source.z;
    }
}
