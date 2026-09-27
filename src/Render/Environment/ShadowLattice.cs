// Port of packages/client/src/render/environment/shadowLattice.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `ShadowLightDirection` is structural in TS ("both rigs already have one"): the terrain passes a three Vector3,
//   the lighting state a WorldLightDirection. C# needs a nominal type, so it is a small class with implicit
//   conversions from WorldLightDirection and ThreeVector3 (a field copy; the function only reads it).
// * The `{ x, z }` result is a value tuple `(double x, double z)`.
// * `TERRAIN_SHADOW_ZOOM_TRANSITION` frozen record → static class of consts.
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>A world direction FROM the scene TOWARD a light. Structural on purpose: both rigs already have one.</summary>
public sealed class ShadowLightDirection
{
    public readonly double x;
    public readonly double y;
    public readonly double z;

    public ShadowLightDirection(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public static implicit operator ShadowLightDirection(ThreeVector3 direction) =>
        new ShadowLightDirection(direction.x, direction.y, direction.z);
}

/// <summary>
/// The ONE rule for where a sun's orthographic depth map may sit, shared by both rigs in the frame.
///
/// A directional shadow map is a raster over a lattice in LIGHT space. Two frames draw the same shadow edge
/// only if they agree on that lattice — its cell size (the ortho half-extent, since the map is a fixed 1024²)
/// and its phase (where the anchor sits inside a cell). Change either one and every cast shadow in the world
/// is re-rasterised at once: edges shift by up to a texel and change width together, and the whole sheet reads
/// as a step in brightness. That is a whole-frame event, which is why it belongs to one module rather than to
/// whichever renderer happened to need it first.
///
/// Pure numbers, no three import — consumable from `ThreeTerrainLayer` (the static world map), from
/// `render3d/core/lightRig.ts` (the per-frame actor map) and from node tests.
/// </summary>
public static partial class ShadowLattice
{
    /// <summary>Below this sun elevation (|ŷ| of the unit light direction) the snap below would slide the anchor tens of
    ///  texels for half a texel of gain, so a grazing sun keeps the raw view centre.</summary>
    private const double SHADOW_ANCHOR_MIN_ELEVATION = 0.2;

    /// <summary>
    /// Put a sun's depth map on its OWN texel lattice.
    ///
    /// Without this the whole texel grid slides continuously as the view moves, and a STATIONARY caster is
    /// re-rasterised at a different sub-texel phase every time the map is republished — its silhouette staircase
    /// crawls. A wide filter kernel hides that; a tight, drawn edge does not.
    ///
    /// Three builds the shadow camera by looking from the light to its target with `up = +Y`, so the lattice axes
    /// are `X = (ẑ, 0, −x̂)/h` (always horizontal) and `Y = (−ŷ·x̂/h, h, −ŷ·ẑ/h)`, with `h = hypot(x̂, ẑ)`. Both are
    /// perpendicular to the light direction, which is why snapping the TARGET also snaps the camera position, and
    /// why the light DIRECTION — the only thing a directional light shades with — is untouched.
    ///
    /// Note that those axes are NOT world X/Z unless the sun is straight overhead. Quantising the anchor on a
    /// world-axis grid therefore does not land on texel boundaries at all: it lands at an arbitrary phase, which
    /// is the same defect as not snapping. The invariant this function guarantees (pinned in `lightRig.test.ts`)
    /// is that the returned anchor's light-space coordinates are exact texel multiples, so the grid only ever
    /// jumps by WHOLE texels and a static caster's depth silhouette holds still.
    /// </summary>
    public static (double x, double z) snapAnchorToShadowTexels(
        double centerX,
        double centerZ,
        ShadowLightDirection sunDir,
        double texelWorldPx)
    {
        double dl = Math.hypot(sunDir.x, sunDir.y, sunDir.z);
        if (!(texelWorldPx > 0) || !(dl > 1e-6)) return (centerX, centerZ);
        double fx = sunDir.x / dl;
        double fy = sunDir.y / dl;
        double fz = sunDir.z / dl;
        double h = Math.hypot(fx, fz);
        // A sun exactly overhead has no horizontal lattice axis to snap along; a grazing one costs far more anchor
        // travel than it buys. Both degeneracies fall back to the unsnapped centre rather than to a special case.
        if (h < 1e-3 || Math.abs(fy) < SHADOW_ANCHOR_MIN_ELEVATION) return (centerX, centerZ);
        double u = (centerX * fz - centerZ * fx) / h;
        double v = -(fy / h) * (centerX * fx + centerZ * fz);
        double du = Math.round(u / texelWorldPx) * texelWorldPx - u;
        double dv = Math.round(v / texelWorldPx) * texelWorldPx - v;
        // Move by (du, dv) along those axes while staying in the XZ plane: a 2×2 solve with determinant h².
        double a = h * du;
        double b = (-h * dv) / fy;
        double inv = 1 / (h * h);
        return (centerX + (fz * a + fx * b) * inv, centerZ + (fz * b - fx * a) * inv);
    }

    /* ── The window's own size ────────────────────────────────────────────────────────────────────────────────
     *
     * Snapping the phase is only half of a stable lattice; the CELL SIZE is the other half, and a map fitted
     * continuously to the view has a different one on every frame of a zoom. Measured on the shipping client
     * (`?flickerlog=1`, a single 4 s wheel zoom): the terrain sun's depth map was republished 3–4 times per
     * second, each time at a new half-extent, so every shadow in the frame changed width and phase together
     * three or four times a second. That is the reported "light on / light off" unrest while zooming — no light
     * intensity moves at all during it, which is exactly why four earlier passes over the light rig found
     * nothing.
     *
     * The window therefore lives on a coarse geometric ladder instead. A zoom crosses a rung once or twice
     * rather than stepping ten times, a zoom out and back returns to the SAME lattice (so the shadows come back
     * bit-identical rather than merely similar), and panning inside a rung republishes with an unchanged cell
     * size — which, with the phase snapped above, is invisible.
     */

    /// <summary>Smallest window a rung may take (world px). Below this the map is denser than any gameplay zoom needs.</summary>
    public const double SHADOW_WINDOW_BASE_PX = 1_024;
    /// <summary>One rung. 1.5 is the authored compromise: a full gameplay zoom (≈6× view radius) crosses four rungs
    ///  instead of ~20 fits, while the coarsest a window can ever be over its requirement stays at 1.5×.</summary>
    public const double SHADOW_WINDOW_STEP = 1.5;

    /// <summary>The rung at or above `required`. Exported for tests and for reasoning about texel density in probes.</summary>
    public static double shadowWindowRung(double required)
    {
        if (!(required > 0)) return SHADOW_WINDOW_BASE_PX;
        double steps = Math.ceil(
            Math.log(Math.max(required, SHADOW_WINDOW_BASE_PX) / SHADOW_WINDOW_BASE_PX) /
                Math.log(SHADOW_WINDOW_STEP));
        return SHADOW_WINDOW_BASE_PX * Math.pow(SHADOW_WINDOW_STEP, Math.max(0, steps));
    }
}
