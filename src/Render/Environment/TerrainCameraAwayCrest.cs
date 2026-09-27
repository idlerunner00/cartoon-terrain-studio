// Port of packages/client/src/render/environment/terrainCameraAwayCrest.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using static Fluitown.Render.TerrainProjection;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class TerrainCameraAwayCrest
{
    /// <summary>
    /// Shape of <see cref="TERRAIN_CAMERA_AWAY_CREST_STYLE"/> (an anonymous frozen object in the original). An
    /// instance rather than a static class so consumers can hold it in a local (`const style = …`) exactly like
    /// the TypeScript, and so <c>CARTOON_TERRAIN_STYLE.cameraAwayCrest</c> can share the very same object.
    /// </summary>
    public sealed class CameraAwayCrestStyle
    {
        /// <summary>Minimum physical shoulder thickness at zoom 1 after the shared oblique projection.</summary>
        public readonly double minimumProjectedWidthPx = 3.8;
        /// <summary>Preserve material character (rock stays crisper than earth) above the visibility floor.</summary>
        public readonly double authoredDepthScale = 1.9;
        /// <summary>Never let the cosmetic shoulder swallow the complete physical terrace drop.</summary>
        public readonly double maximumDropFraction = 0.42;
        /// <summary>Small inward run keeps the shoulder steep enough to remain visible behind the nearer cap.</summary>
        public readonly double insetPerDepth = 0.12;
        /// <summary>Pigment pooling grows toward the outer break, producing form without an ink outline.</summary>
        public readonly double innerEdgeDarkMix = 0.14;
        public readonly double outerEdgeDarkMix = 0.56;
        public readonly double outerShade = 0.94;
        /// <summary>
        /// The descending north shoulder projects INTO its own higher cap and is therefore depth-hidden in the fixed
        /// gameplay camera. A narrow top-plane pigment rim is the visible comic contour; the shoulder remains the
        /// physical shell for presentation-orbit views. Resolve the rim against the approved desktop zoom floor so
        /// its CSS-pixel weight does not disappear when the camera pulls out.
        /// </summary>
        public readonly double referenceGameplayZoom = 0.75;
        public readonly double minimumTopRimProjectedWidthPx = 1.35;
        public readonly double authoredTopRimScale = 1.2;
        public readonly double maximumTopRimCellFraction = 0.11;
        /// <summary>East/west walls are exactly edge-on under the shared projection. Their cap rim is only a sub-pixel-safe
        /// material seam; it must never recreate the former dark vertical height bar.</summary>
        public readonly double minimumEdgeOnRimProjectedWidthPx = 0.85;
        public readonly double authoredEdgeOnRimScale = 0.6;
        public readonly double maximumEdgeOnRimCellFraction = 0.045;
        /// <summary>
        /// A perfectly constant ribbon exposes the underlying 40 px cell lattice. Shared world-corner samples vary
        /// only the IN-CAP edge, so neighbouring cells still meet exactly while the pigment reads as a softly eroded
        /// geological lip. Variation grows inward from the approved projected visibility floor instead of shrinking
        /// below it, so thin samples remain legible while pooled sections acquire a natural, irregular body.
        /// </summary>
        public readonly double organicRunVariation = 0.3;
        public readonly double organicShadeVariation = 0.04;
        public readonly double organicPigmentVariation = 0.13;
        public readonly double organicProfileScaleCells = 3;
        /// <summary>Collision-neutral lift keeps the opaque rim deterministically in front of its owning cap.</summary>
        public readonly double topRimLiftPx = 0.065;
        /// <summary>A restrained rolled gradient reads as pooled earth/stone rather than a uniform graphic outline.</summary>
        public readonly double topRimInnerDarkMix = 0.035;
        public readonly double topRimOuterDarkMix = 0.24;
        public readonly double topRimOuterShade = 0.98;
    }

    /// <summary>
    /// One shared visual/geometry contract for a north (camera-away) terrain crest.
    ///
    /// The main-thread Hub compiler and the worker-owned Endless compiler must emit byte-identical run geometry.
    /// Keeping both the style values and projection math in this dependency-light leaf prevents those two hot
    /// paths from silently drifting while still letting their material samplers remain independent.
    /// </summary>
    public static readonly CameraAwayCrestStyle TERRAIN_CAMERA_AWAY_CREST_STYLE = new();

    // `export type TerrainCrestRimAxis = 'x' | 'z';` → string ("x" | "z").

    /// <summary>Resolve a material's authored bevel to a projection-readable, drop-bounded camera-away shoulder.</summary>
    public static double terrainCameraAwayCrestDepth(double authoredBevelDepthPx, double dropLevels)
    {
        if (authoredBevelDepthPx <= 0 || dropLevels <= 0) return 0;
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double projectedPerDepth = Math.max(
            0.01,
            TERRAIN_VIEW_HEIGHT_SCALE - style.insetPerDepth * TERRAIN_VIEW_GROUND_SCALE);
        return Math.min(
            dropLevels * TERRAIN_ELEVATION_STEP_PX * style.maximumDropFraction,
            Math.max(
                authoredBevelDepthPx * style.authoredDepthScale,
                style.minimumProjectedWidthPx / projectedPerDepth));
    }

    public static double terrainCameraAwayCrestInset(double depthPx)
    {
        return Math.max(0, depthPx) * TERRAIN_CAMERA_AWAY_CREST_STYLE.insetPerDepth;
    }

    /// <summary>Ground run of the visible north cap rim. Unlike the physical shoulder, this projection cannot self-occlude.</summary>
    public static double terrainCameraAwayCrestTopRimRun(double authoredBevelDepthPx, double tileSizePx)
    {
        if (authoredBevelDepthPx <= 0 || tileSizePx <= 0) return 0;
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double minimumWorldRun =
            style.minimumTopRimProjectedWidthPx / (TERRAIN_VIEW_GROUND_SCALE * style.referenceGameplayZoom);
        return organicRimBaseRun(
            authoredBevelDepthPx * style.authoredTopRimScale,
            minimumWorldRun,
            tileSizePx * style.maximumTopRimCellFraction);
    }

    /// <summary>In-cap run for an east/west crest whose real vertical face has zero projected width at gameplay yaw.</summary>
    public static double terrainEdgeOnCrestTopRimRun(double authoredBevelDepthPx, double tileSizePx)
    {
        if (authoredBevelDepthPx <= 0 || tileSizePx <= 0) return 0;
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double minimumWorldRun = style.minimumEdgeOnRimProjectedWidthPx / style.referenceGameplayZoom;
        return organicRimBaseRun(
            authoredBevelDepthPx * style.authoredEdgeOnRimScale,
            minimumWorldRun,
            tileSizePx * style.maximumEdgeOnRimCellFraction);
    }

    /// <summary>World-corner run for a north rim. Adjacent cells resolve the shared endpoint byte-for-byte.</summary>
    public static double terrainCameraAwayCrestTopRimRunAt(
        double authoredBevelDepthPx,
        double tileSizePx,
        double worldVertexX,
        double worldVertexY)
    {
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double minimumWorldRun =
            style.minimumTopRimProjectedWidthPx / (TERRAIN_VIEW_GROUND_SCALE * style.referenceGameplayZoom);
        return organicRimRunAt(
            terrainCameraAwayCrestTopRimRun(authoredBevelDepthPx, tileSizePx),
            minimumWorldRun,
            tileSizePx * style.maximumTopRimCellFraction,
            worldVertexX,
            worldVertexY,
            "x");
    }

    /// <summary>World-corner run for an east/west rim. Both directions share the same axis field at a common boundary.</summary>
    public static double terrainEdgeOnCrestTopRimRunAt(
        double authoredBevelDepthPx,
        double tileSizePx,
        double worldVertexX,
        double worldVertexY)
    {
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double minimumWorldRun = style.minimumEdgeOnRimProjectedWidthPx / style.referenceGameplayZoom;
        return organicRimRunAt(
            terrainEdgeOnCrestTopRimRun(authoredBevelDepthPx, tileSizePx),
            minimumWorldRun,
            tileSizePx * style.maximumEdgeOnRimCellFraction,
            worldVertexX,
            worldVertexY,
            "z");
    }

    /// <summary>Outer-edge shade at one shared world corner; broad drift plus a small tooth avoids per-cell striping.</summary>
    /// <param name="axis">TerrainCrestRimAxis: "x" | "z".</param>
    public static double terrainCrestTopRimShadeAt(double worldVertexX, double worldVertexY, string axis)
    {
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double variation =
            (organicRimNoise(worldVertexX, worldVertexY, axis, 0x6a09e667) * 2 - 1) *
            style.organicShadeVariation;
        return style.topRimOuterShade * (1 + variation);
    }

    /// <summary>Material pooling at a shared endpoint; separated from shade so colour and light never pulse in lockstep.</summary>
    /// <param name="axis">TerrainCrestRimAxis: "x" | "z".</param>
    public static double terrainCrestTopRimOuterMixAt(double worldVertexX, double worldVertexY, string axis)
    {
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double variation =
            (organicRimNoise(worldVertexX, worldVertexY, axis, 0xbb67ae85) * 2 - 1) *
            style.organicPigmentVariation;
        return style.topRimOuterDarkMix * (1 + variation);
    }

    private static double organicRimBaseRun(double authoredRun, double minimumRun, double maximumRun)
    {
        return Math.min(maximumRun, Math.max(Math.min(minimumRun, maximumRun), authoredRun));
    }

    private static double organicRimRunAt(
        double baseRun,
        double minimumRun,
        double maximumRun,
        double worldVertexX,
        double worldVertexY,
        string axis)
    {
        if (baseRun <= 0 || maximumRun <= 0) return 0;
        double variation = TERRAIN_CAMERA_AWAY_CREST_STYLE.organicRunVariation;
        double noise = organicRimNoise(worldVertexX, worldVertexY, axis, 0x243f6a88);
        double varied = baseRun * (1 + noise * variation);
        return Math.min(maximumRun, Math.max(Math.min(minimumRun, maximumRun), varied));
    }

    /// <param name="salt">A JS number (0xbb67ae85 exceeds int32 and stays a double until a bitwise operator).</param>
    private static double organicRimNoise(double worldVertexX, double worldVertexY, string axis, double salt)
    {
        var style = TERRAIN_CAMERA_AWAY_CREST_STYLE;
        double tangent = axis == "x" ? worldVertexX : worldVertexY;
        double side = axis == "x" ? worldVertexY : worldVertexX;
        double scaled = tangent / style.organicProfileScaleCells;
        double lattice = Math.floor(scaled);
        double fraction = scaled - lattice;
        double smooth = fraction * fraction * (3 - 2 * fraction);
        double axisSalt = axis == "x" ? salt : (double)(Js.ToInt32(salt) ^ 0x3c6ef372);
        double broadA = rimHash01(lattice, side, axisSalt);
        double broadB = rimHash01(lattice + 1, side, axisSalt);
        double broad = broadA + (broadB - broadA) * smooth;
        double tooth = rimHash01(worldVertexX, worldVertexY, Js.ToInt32(axisSalt) ^ unchecked((int)0xa54ff53a));
        return broad * 0.76 + tooth * 0.24;
    }

    private static double rimHash01(double x, double y, double salt)
    {
        int value = Math.imul(Js.ToInt32(x), 0x1f123bb5) ^ Math.imul(Js.ToInt32(y), 0x5f356495) ^ Js.ToInt32(salt);
        value = Math.imul(value ^ (int)((uint)value >> 16), 0x45d9f3b);
        value = Math.imul(value ^ (int)((uint)value >> 16), 0x45d9f3b);
        return (uint)(value ^ (int)((uint)value >> 16)) / 4294967296.0;
    }
}
