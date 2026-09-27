// Port of packages/client/src/render/environment/terrainGeometryCompilerStyle.ts — keep in lockstep with the original.
using Fluitown.Domain;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

// `export type CartoonTerrainSurfaceKind = 'floor' | 'rockCap' | … | 'abyss';` → string. (cartoonTerrainStyle.ts
// exports the same union; being a plain string, neither module declares a C# type for it.)

public static partial class TerrainGeometryCompilerStyle
{
    // ── Shapes of the frozen CARTOON_TERRAIN_STYLE sub-objects ─────────────────────────────────────────────
    //
    // PORT NOTE: CARTOON_TERRAIN_STYLE itself is a static class; each sub-object is a sealed instance with
    // readonly fields so consumers can hold it in a local exactly like the TypeScript
    // (`const style = CARTOON_TERRAIN_STYLE.waterShore;`). `suspensionBridge` mirrors the static-class port of
    // TERRAIN_SUSPENSION_STYLE and is therefore a nested static class of constants.

    public sealed class OrganicShapeStyle
    {
        /// <summary>Four sub-quads expose a centre vertex, so the world field can form a hill inside one 40 px cell.</summary>
        public readonly double subdivisions = 2;
    }

    public sealed class WallDepthStyle
    {
        public readonly bool enabled = false;
        public readonly double minimumDropLevels = 1.7;
        public readonly double primaryDepthLevels = 1.25;
        public readonly double primaryCoverage = 0.82;
        public readonly double secondaryMinimumDropLevels = 5.5;
        public readonly double secondaryDepthLevels = 3.85;
        public readonly double secondaryCoverage = 0.36;
        public readonly double primaryShelfDepthPx = 3.4;
        public readonly double secondaryShelfDepthPx = 2.55;
        public readonly double fasciaHeightPx = 0.72;
        public readonly double clusterSizeCells = 5;
    }

    public sealed class JunctionsStyle
    {
        /// <summary>Shared physical profile for an ordinary earth/Floor height stair and its Chasm-floor counterpart.</summary>
        public readonly double earthTerraceBevelHeightPx = 2.9;
        public readonly double earthTerraceBevelRunRatio = 0.9;
        // Keep the worker compiler byte-identical to the live terrain path: closure returns inherit wall lighting,
        // rather than presenting an upward-facing sunlit sliver at an otherwise dark edge-on join.
        public readonly double heightReturnNormalY = 0.16;
        public readonly double heightReturnFootShade = 0.86;
        public readonly double contourFaceNormalY = 0.56;
        public readonly double contourFaceFootShade = 0.92;
        public readonly double contourFaceTopBlend = 0.22;
        public readonly double contourLineTopBlend = 0.55;
        public readonly double contourLineAlpha = 0.14;
        public readonly double dryCornerApronLiftPx = 0.06;
        public readonly double mixedCornerApronTopBlend = 0.38;
    }

    public sealed class ChasmDepthStyle
    {
        /// <summary>Sparse deterministic deep-floor punctuation.</summary>
        public readonly double floorScarCoverage = 0.38;
        public readonly double floorStoneCoverage = 0.16;
        public readonly double floorTuftCoverage = 0.045;
        /// <summary>One restrained geological rim shelf establishes scale before the dark continuum becomes visible.</summary>
        public readonly double upperLedgeDepthLevels = 0.9;
        public readonly double middleLedgeDepthLevels = 2.65;
        public readonly double deepLedgeDepthLevels = 5.2;
        public readonly double upperLedgeWidthCells = 0.17;
        public readonly double middleLedgeWidthCells = 0.12;
        public readonly double deepLedgeWidthCells = 0.08;
        // One continuous upper bench carries the silhouette. Deeper benches are geological punctuation, not a
        // three-line outline repeated around every cell; broad ravines still accumulate several depth events.
        public readonly double middleLedgeCoverage = 0.3;
        public readonly double deepLedgeCoverage = 0.08;
    }

    public sealed class WaterBasinStyle
    {
        public readonly double shellBodyBlend = 0.78;
        public readonly double shellFootShade = 0.76;
        public readonly double backingWaterBlend = 0.24;
    }

    public sealed class WaterShoreStyle
    {
        /// <summary>Only bank cells are tessellated; seven world-aligned spans resolve an organic 7..22 px pigment dissolve.</summary>
        public readonly double transitionSegments = 7;
        public readonly double minimumBlendPx = 7.2;
        public readonly double maximumBlendPx = 22.4;
        public readonly double noiseScalePx = 53;
        public readonly double detailNoiseScalePx = 19;
        public readonly double detailNoiseWeight = 0.34;
        /// <summary>
        /// The bank MEANDER — how far the drawn shoreline pushes OUT over the liquid, in world px.
        ///
        /// The dissolve above only ever decided how WIDE the damp band is; where it starts was the tile contact
        /// itself, so every pool's outline was the lattice with soft edges on it. This term moves the start: the
        /// band's full-strength end wanders out into the water on a broad landform octave with a finer nibble, so
        /// a straight tile run reads as spits and shallow bays.
        ///
        /// It is ONE-SIDED by construction. Letting the bank retreat inland of the contact would leave the liquid's
        /// own outermost row carrying water pigment against a Floor cap that carries ground pigment — the ruler-
        /// clean colour step this whole field exists to remove. Advancing is free: the liquid paints the canonical
        /// Floor pigment itself, so a spit is the water cell drawing beach.
        ///
        /// The amplitude is authored in absolute px (like the reach it rides on), so a 40 px Hub pool and a
        /// 78.125 px run lake get the same physical shoreline rather than the same fraction of a tile. It stays
        /// well under half a cell: a one-cell channel must narrow, never silt up.
        /// </summary>
        public readonly double meanderReachPx = 13;
        public readonly double meanderScalePx = 96;
        public readonly double meanderDetailScalePx = 34;
        public readonly double meanderDetailWeight = 0.36;
        /// <summary>
        /// Two averaged value-noise octaves are strongly concentrated around 0.5, so an authored 13 px amplitude
        /// delivered barely 5 px of actual swing — a coastline that technically meandered and visibly did not.
        /// This stretches the combined field about its own midpoint before the ease, which is what makes the
        /// authored number the number you get: short stretches hug the contact, others run a full spit out.
        /// </summary>
        public readonly double meanderContrast = 2.1;
        /// <summary>
        /// Corner softening for the bank distance, in world px.
        ///
        /// The dissolve measures the nearest of several tile contact segments, and a plain nearest-of is what put
        /// the right angles in: where two perpendicular contacts meet, the distance field creases and its
        /// iso-contour turns through exactly 90 degrees — the pool corner reads as a drawn square no matter how
        /// much the straight runs meander. A polynomial smooth-minimum removes the crease itself, so a corner
        /// becomes a turn with a radius instead of a vertex, with no corner classification anywhere.
        /// </summary>
        public readonly double bankCornerSoftenPx = 40;
        public readonly double maximumHeightDeltaLevels = 0.72;
        public readonly double waterfallMouthClearancePx = 6.4;
        public readonly double shellWaterBlend = 0.12;
    }

    public sealed class BridgeStructureStyle
    {
        public readonly double deckPlanksPerCell = 6;
        public readonly double deckPlankGapCells = 0.022;
        public readonly double girderInsetCells = 0.115;
        public readonly double girderWidthCells = 0.085;
        public readonly double girderDepthLevels = 0.3;
        public readonly double abutmentLengthCells = 0.24;
        public readonly double pierPeriodCells = 3;
        public readonly double pierWidthCells = 0.13;
        public readonly double curbWidthCells = 0.055;
        public readonly double curbHeightLevels = 0.16;
        public readonly double railPostWidthCells = 0.052;
        public readonly double railHeightLevels = 0.78;
        public readonly double railBeamWidthCells = 0.042;
        public readonly double railMiddleHeightLevels = 0.43;
        public readonly double railBraceWidthCells = 0.026;
    }

    /// <summary>
    /// The environment's character-alignment contract.
    ///
    /// Characters are painted as a few broad, saturated colour blocks with a confident ink silhouette. Terrain
    /// must therefore avoid noisy PBR sparkle, per-cell colour switches and dense full-face hatching. Keep these
    /// high-level dials together so Hub, finite dungeons and endless chunks cannot drift into different looks.
    /// </summary>
    public static class CARTOON_TERRAIN_STYLE
    {
        /// <summary>
        /// Real, visual-only undulation for broad natural walkable sheets. The authoritative terrace remains the
        /// gameplay plane; only interior floor vertices opt in and every structural corner (water, bridge, cliff,
        /// underpass, different elevation) is pinned to that plane. Absolute world coordinates plus a topology mask
        /// make independently baked tiles meet bit-for-bit without skirts or duplicated seam geometry.
        ///
        /// The amplitude intentionally stays far below one elevation step (15 px). It is large enough to move a cast
        /// shadow and break a ruler-flat silhouette, but small enough that feet, decals and collision-neutral dressing
        /// still visually belong to the unchanged gameplay surface.
        /// </summary>
        public static readonly OrganicShapeStyle organicShape = new();
        /// <summary>
        /// Legacy full-width wall benches are retained as an explicit rollback contract only. They projected as dark
        /// horizontal bars and their edge-on ends became the black vertical "cliff protrusions" beside Water. Natural
        /// wall depth now comes from the rolled crest, material relief and flush broken strata instead.
        /// </summary>
        public static readonly WallDepthStyle wallDepth = new();
        /// <summary>
        /// The fixed gameplay camera sees a south crest as bevel + complete wall face, but a north (camera-away)
        /// crest hides that wall behind its own plateau, while east/west faces are exactly edge-on. Keep the real
        /// rolled shell for orbit views and add a narrow opaque pigment rim on the owning cap for the fixed gameplay
        /// view. Both remain in the existing terrain surface batch; there is no screen-space line or translucent pass.
        /// </summary>
        public static readonly TerrainCameraAwayCrest.CameraAwayCrestStyle cameraAwayCrest =
            TerrainCameraAwayCrest.TERRAIN_CAMERA_AWAY_CREST_STYLE;
        /// <summary>
        /// Closed material transitions at topology corners and unequal-height wall joins. Contour mitres bias gently
        /// upward to keep broad corner lighting continuous; narrow edge-on height returns instead inherit the dark wall
        /// normal so they cannot become bright vertical cards. A dry apron just above a Water datum owns the square
        /// outside a chamfered bank, so the full continuous Water surface remains seam-free without projecting through
        /// the clipped dry corner.
        /// </summary>
        public static readonly JunctionsStyle junctions = new();
        /// <summary>
        /// Every Chasm is a closed terrain volume: the neighbouring geological wall reaches the shared negative floor
        /// datum unchanged and each Chasm cell emits one dark floor cap there. The remote continuum remains only as a
        /// streamed-world fallback below that real geometry. The explicit floor value keeps its structure readable
        /// without making the blocked deep ground look walkable. Gameplay datums stay unchanged.
        /// </summary>
        public static readonly ChasmDepthStyle chasmDepth = new();
        /// <summary>
        /// Exposed liquid banks are a short, opaque cross-section of the pool, not an unlit void. Their body borrows
        /// enough of the biome's mid-water colour to remain recognisably liquid, darkens towards the basin floor and
        /// retains a small lighting-independent value floor on camera-away faces.
        /// </summary>
        public static readonly WaterBasinStyle waterBasin = new();
        /// <summary>
        /// Floor owns every ordinary shore through its canonical cap and earth face. Water only retains the values
        /// needed for structural waterfall openings, non-Floor basin shells and its broad shader-side transition.
        /// </summary>
        public static readonly WaterShoreStyle waterShore = new();
        /// <summary>Static bridge cross-section authored into the existing surface batch.</summary>
        public static readonly BridgeStructureStyle bridgeStructure = new();
    }

    /// <summary>
    /// Shader index of each surface pattern family (see `SURF_NOISE_GLSL`). The order IS the shader's switch
    /// order and rides `aSurf.x`, so it is a wire contract between the geometry compiler and the material —
    /// append only. Props and terrain both name it from here rather than each holding their own copy.
    /// </summary>
    public static class TERRAIN_SURFACE_PATTERN
    {
        public const int floor = 0;
        public const int rockCap = 1;
        public const int earthFace = 2;
        public const int rockFace = 3;
        public const int bridge = 4;
        public const int basin = 5;
        public const int waterBank = 6;
        public const int chasmWall = 7;
        /// <summary>Non-walkable, fully materialized Chasm floor at the shared negative-height datum. Append-only wire id.</summary>
        public const int chasmFloor = 8;
    }

    /// <summary>Pure audit/geometry gate for the retired full-width depth shelves.</summary>
    /// <returns>0 | 1 | 2.</returns>
    public static int cartoonTerrainWallShelfCount(
        double dropLevels,
        double primaryClusterSample,
        double secondaryClusterSample)
    {
        var style = CARTOON_TERRAIN_STYLE.wallDepth;
        if (!style.enabled) return 0;
        if (dropLevels < style.minimumDropLevels || primaryClusterSample > style.primaryCoverage)
            return 0;
        return dropLevels >= style.secondaryMinimumDropLevels &&
            secondaryClusterSample <= style.secondaryCoverage
            ? 2
            : 1;
    }

    private static double smoothstep(double lo, double hi, double value)
    {
        double t = Math.min(1, Math.max(0, (value - lo) / Math.max(1e-6, hi - lo)));
        return t * t * (3 - 2 * t);
    }

    private static TerrainMaterial blendMaterial(TerrainMaterial a, TerrainMaterial b, double t)
    {
        double k = Math.min(1, Math.max(0, t));
        return new TerrainMaterial
        {
            id = $"{a.id}:{b.id}:{Js.Str(Math.round(k * 16))}",
            top = Palette.mix(a.top, b.top, k),
            topLight = Palette.mix(a.topLight, b.topLight, k),
            topDark = Palette.mix(a.topDark, b.topDark, k),
            side = Palette.mix(a.side, b.side, k),
            edgeLight = Palette.mix(a.edgeLight, b.edgeLight, k),
            edgeDark = Palette.mix(a.edgeDark, b.edgeDark, k),
            detail = Palette.mix(a.detail, b.detail, k),
            roughness = a.roughness + (b.roughness - a.roughness) * k,
        };
    }

    /// <summary>
    /// Stable floor-family selection. Water proximity is intentionally absent here: moisture is sampled once per
    /// logical cell, so feeding it into the base material paints the hydrology raster back onto the ground as dark
    /// squares. Damp detail belongs to world-continuous vertex/shader fields; a complete cap may only change family
    /// for a physical elevation terrace.
    /// </summary>
    public static TerrainMaterial cartoonFloorBase(double _moisture, double elevation)
    {
        double dry = smoothstep(2.75, 5.25, elevation);
        return blendMaterial(
            TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.floorCool,
            TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.floorDry,
            dry * 0.72);
    }
}
