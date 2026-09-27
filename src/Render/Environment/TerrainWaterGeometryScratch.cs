// Port of packages/client/src/render/environment/terrainWaterGeometryScratch.ts — keep in lockstep with the original.
//
// Reused, allocation-free storage for the continuous terrain-water compiler.
//
// PORT NOTES:
// * Every buffer here is mutable module scratch, so each is thread-local (RENDER_AGENT_BRIEF "Thread safety"):
//   one instance per compiler thread, created on first use with the TS fill value.
// * TS `number[]` scratch (colours included — the compiler also parks a colour in WATER_TRANSITION_SAMPLE[1])
//   is `double[]`; `[P3, P3, P3, P3]` tuples are `P3[]`. Only the immutable WATERFALL_CURVE_ROWS table is
//   `static readonly` (`readonly [...] as const` → IReadOnlyList).
using System;
using System.Collections.Generic;
using static Fluitown.Render.TerrainGeometryCompilerStyle;

namespace Fluitown.Render;

public static partial class TerrainWaterGeometryScratch
{
    private static double[] filled(int length, double value)
    {
        var array = new double[length];
        for (int index = 0; index < length; index++) array[index] = value;
        return array;
    }

    private static P3[] points(int length)
    {
        var array = new P3[length];
        for (int index = 0; index < length; index++) array[index] = new P3 { x = 0, y = 0, z = 0 };
        return array;
    }

    // A fully rounded Chasm lip carries four CPU-authored samples per edge plus four-facet corner turns. Thirty-two
    // vertices is the exact maximum; keep one power-of-two scratch block so the hot path remains allocation-free.
    [ThreadStatic] private static double[]? _WATER_FOAM_SCRATCH;
    public static double[] WATER_FOAM_SCRATCH => _WATER_FOAM_SCRATCH ??= filled(32, 0);
    [ThreadStatic] private static double[]? _WATER_DEPTH_SCRATCH;
    public static double[] WATER_DEPTH_SCRATCH => _WATER_DEPTH_SCRATCH ??= filled(32, 0);
    [ThreadStatic] private static double[]? _WATER_COLOR_SCRATCH;
    public static double[] WATER_COLOR_SCRATCH => _WATER_COLOR_SCRATCH ??= filled(32, 0xffffff);
    [ThreadStatic] private static double[]? _WATER_REFLECTION_CORNERS;
    public static double[] WATER_REFLECTION_CORNERS => _WATER_REFLECTION_CORNERS ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATER_REFLECTION_SCRATCH;
    public static double[] WATER_REFLECTION_SCRATCH => _WATER_REFLECTION_SCRATCH ??= filled(32, 0);
    [ThreadStatic] private static P3[]? _WATER_TRANSITION_QUAD;
    public static P3[] WATER_TRANSITION_QUAD => _WATER_TRANSITION_QUAD ??= points(4);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_COLOR4;
    public static double[] WATER_TRANSITION_COLOR4 => _WATER_TRANSITION_COLOR4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_FOAM4;
    public static double[] WATER_TRANSITION_FOAM4 => _WATER_TRANSITION_FOAM4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_DEPTH4;
    public static double[] WATER_TRANSITION_DEPTH4 => _WATER_TRANSITION_DEPTH4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_REFLECTION4;
    public static double[] WATER_TRANSITION_REFLECTION4 => _WATER_TRANSITION_REFLECTION4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_NORMAL_X4;
    public static double[] WATER_TRANSITION_NORMAL_X4 => _WATER_TRANSITION_NORMAL_X4 ??= filled(32, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_NORMAL_Y4;
    public static double[] WATER_TRANSITION_NORMAL_Y4 => _WATER_TRANSITION_NORMAL_Y4 ??= filled(32, 1);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_NORMAL_Z4;
    public static double[] WATER_TRANSITION_NORMAL_Z4 => _WATER_TRANSITION_NORMAL_Z4 ??= filled(32, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_SAMPLE;
    public static double[] WATER_TRANSITION_SAMPLE => _WATER_TRANSITION_SAMPLE ??= filled(2, 0);
    /// <summary>Three-vertex fan owned by a rounded dry-corner Water fill; see `addSeamlessWaterContourFill`.</summary>
    [ThreadStatic] private static P3[]? _WATER_CONTOUR_TRIANGLE;
    public static P3[] WATER_CONTOUR_TRIANGLE => _WATER_CONTOUR_TRIANGLE ??= points(3);
    [ThreadStatic] private static double[]? _WATER_CONTOUR_COLOR3;
    public static double[] WATER_CONTOUR_COLOR3 => _WATER_CONTOUR_COLOR3 ??= filled(3, 0);
    [ThreadStatic] private static double[]? _WATER_CONTOUR_FOAM3;
    public static double[] WATER_CONTOUR_FOAM3 => _WATER_CONTOUR_FOAM3 ??= filled(3, 0);
    [ThreadStatic] private static double[]? _WATER_CONTOUR_DEPTH3;
    public static double[] WATER_CONTOUR_DEPTH3 => _WATER_CONTOUR_DEPTH3 ??= filled(3, 0);
    [ThreadStatic] private static double[]? _WATER_CONTOUR_REFLECTION3;
    public static double[] WATER_CONTOUR_REFLECTION3 => _WATER_CONTOUR_REFLECTION3 ??= filled(3, 0);
    [ThreadStatic] private static double[]? _WATER_CONTOUR_NORMAL_X3;
    public static double[] WATER_CONTOUR_NORMAL_X3 => _WATER_CONTOUR_NORMAL_X3 ??= filled(3, 0);
    [ThreadStatic] private static double[]? _WATER_CONTOUR_NORMAL_Y3;
    public static double[] WATER_CONTOUR_NORMAL_Y3 => _WATER_CONTOUR_NORMAL_Y3 ??= filled(3, 1);
    [ThreadStatic] private static double[]? _WATER_CONTOUR_NORMAL_Z3;
    public static double[] WATER_CONTOUR_NORMAL_Z3 => _WATER_CONTOUR_NORMAL_Z3 ??= filled(3, 0);
    private static readonly int WATER_TRANSITION_GRID_SIDE =
        (int)(CARTOON_TERRAIN_STYLE.waterShore.transitionSegments + 1);
    private static readonly int WATER_TRANSITION_GRID_COUNT = WATER_TRANSITION_GRID_SIDE * WATER_TRANSITION_GRID_SIDE;
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_X;
    public static double[] WATER_TRANSITION_GRID_X => _WATER_TRANSITION_GRID_X ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_Y;
    public static double[] WATER_TRANSITION_GRID_Y => _WATER_TRANSITION_GRID_Y ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_Z;
    public static double[] WATER_TRANSITION_GRID_Z => _WATER_TRANSITION_GRID_Z ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_COLOR;
    public static double[] WATER_TRANSITION_GRID_COLOR =>
        _WATER_TRANSITION_GRID_COLOR ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_FOAM;
    public static double[] WATER_TRANSITION_GRID_FOAM =>
        _WATER_TRANSITION_GRID_FOAM ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_DEPTH;
    public static double[] WATER_TRANSITION_GRID_DEPTH =>
        _WATER_TRANSITION_GRID_DEPTH ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_REFLECTION;
    public static double[] WATER_TRANSITION_GRID_REFLECTION =>
        _WATER_TRANSITION_GRID_REFLECTION ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_NORMAL_X;
    public static double[] WATER_TRANSITION_GRID_NORMAL_X =>
        _WATER_TRANSITION_GRID_NORMAL_X ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_NORMAL_Y;
    public static double[] WATER_TRANSITION_GRID_NORMAL_Y =>
        _WATER_TRANSITION_GRID_NORMAL_Y ??= filled(WATER_TRANSITION_GRID_COUNT, 1);
    [ThreadStatic] private static double[]? _WATER_TRANSITION_GRID_NORMAL_Z;
    public static double[] WATER_TRANSITION_GRID_NORMAL_Z =>
        _WATER_TRANSITION_GRID_NORMAL_Z ??= filled(WATER_TRANSITION_GRID_COUNT, 0);
    [ThreadStatic] private static P3[]? _WATERFALL_SHEET;
    public static P3[] WATERFALL_SHEET => _WATERFALL_SHEET ??= points(4);
    [ThreadStatic] private static double[]? _WATERFALL_COLOR4;
    public static double[] WATERFALL_COLOR4 => _WATERFALL_COLOR4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_DEPTH4;
    public static double[] WATERFALL_DEPTH4 => _WATERFALL_DEPTH4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_REFLECTION4;
    public static double[] WATERFALL_REFLECTION4 => _WATERFALL_REFLECTION4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_CREST4;
    public static double[] WATERFALL_CREST4 => _WATERFALL_CREST4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_NORMAL_X4;
    public static double[] WATERFALL_NORMAL_X4 => _WATERFALL_NORMAL_X4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_NORMAL_Y4;
    public static double[] WATERFALL_NORMAL_Y4 => _WATERFALL_NORMAL_Y4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_NORMAL_Z4;
    public static double[] WATERFALL_NORMAL_Z4 => _WATERFALL_NORMAL_Z4 ??= filled(4, 0);
    /// <summary>Per-vertex waterfall topology scratch used by rounded corner returns.</summary>
    [ThreadStatic] private static double[]? _WATERFALL_AXIS4;
    public static double[] WATERFALL_AXIS4 => _WATERFALL_AXIS4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_MARKER4;
    public static double[] WATERFALL_MARKER4 => _WATERFALL_MARKER4 ??= filled(4, 0);
    /// <summary>World-space surface coordinates after the falling sheet is unfolded around its crest.</summary>
    [ThreadStatic] private static double[]? _WATERFALL_FOLD_X4;
    public static double[] WATERFALL_FOLD_X4 => _WATERFALL_FOLD_X4 ??= filled(4, 0);
    [ThreadStatic] private static double[]? _WATERFALL_FOLD_Z4;
    public static double[] WATERFALL_FOLD_Z4 => _WATERFALL_FOLD_Z4 ??= filled(4, 0);

    /// <summary>
    /// Extra density around the crest resolves the horizontal-to-vertical turn.  Twelve short, smoothly shaded
    /// intervals keep the rolling carrier from exposing horizontal facet bands at the oblique gameplay camera.
    /// </summary>
    public static readonly IReadOnlyList<double> WATERFALL_CURVE_ROWS = new double[]
    {
        0, 0.025, 0.055, 0.095, 0.15, 0.22, 0.31, 0.42, 0.54, 0.66, 0.78, 0.89, 1,
    };

    [ThreadStatic] private static P3[]? _WATER_SURFACE_QUAD;
    public static P3[] WATER_SURFACE_QUAD => _WATER_SURFACE_QUAD ??= points(4);
}
