// Port of packages/client/src/render/environment/aerialHaze.ts — keep in lockstep with the original.
//
// PORT NOTES
// * GLSL template literals are C# raw string literals with the identical text; `${x.toFixed(n)}` → Js.ToFixed.
//   Strings that interpolate runtime-computed numbers are `static readonly` (evaluated once, like the TS module).
// * three.js value types → ThreeValues.cs: `{ value: new Color(hex) }` → ThreeUniform<ThreeColor> (sRGB hex →
//   linear exactly like three's ColorManagement), `{ value: new Vector4(...) }` → ThreeUniform<ThreeVector4>.
// * `WORLD_AERIAL_HAZE` is one mutable instance on purpose (see its comment): genuinely main-thread-only
//   renderer state (the terrain layer publishes it once per frame), so it is a plain static, not [ThreadStatic].
// * SKIPPED: `installAerialHaze(material, haze)` — three.js material plumbing (wraps `onBeforeCompile` /
//   `customProgramCacheKey`, splices `varying vec3 vMmoratAir` + `vMmoratAir = (modelMatrix [* batchingMatrix |
//   * instanceMatrix] * vec4(transformed, 1.0)).xyz` after `#include <project_vertex>`, the uniform/noise/pars GLSL
//   after `#include <common>` and AERIAL_HAZE_APPLY_GLSL before `#include <tonemapping_fragment>`; a `toneMapped:
//   false` material binds `displayColor` instead of `color`). Its private constants are kept below for the record.
using static Fluitown.Render.AerialHazeModule;
using static Fluitown.Render.PresentationRenderer;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// The shared uniform refs + the per-frame publish seam.
///
/// One instance for the live world (see <see cref="AerialHazeModule.WORLD_AERIAL_HAZE"/>). Every terrain
/// surface/water/overlay material and every actor material hold these exact objects, so a frame update is three
/// writes for the whole picture rather than one per material.
/// </summary>
public sealed class AerialHaze
{
    /// <summary>LINEAR biome haze pigment (`ThreeTerrainLayer.setBiome` grades and day/night-shifts it).</summary>
    public readonly ThreeUniform<ThreeColor> color = new(new ThreeColor(0xdfe4ea));
    /// <summary>
    /// The same air as it will be DISPLAYED — `ACES(PRESENTATION_EXPOSURE · haze)`.
    ///
    /// The pole a fragment converges on has to live in the fragment's own output domain. Tone-mapped
    /// pigment converges on <see cref="color"/>, because the tone map is still ahead of it. The `toneMapped:
    /// false` family (the inverted-hull ink contour, unlit world marks) writes straight into a buffer
    /// where every other pixel has already been through exposure and the ACES shoulder, so handing it
    /// the scene-linear haze pulls it toward a value roughly twice as bright as the air the rest of the
    /// picture settles into. Measured, before this existed: the far contour reached 101/255 while the
    /// ground it was drawn on only reached 108 — the drawing stopped being the darkest mark on the sheet.
    /// </summary>
    public readonly ThreeUniform<ThreeColor> displayColor = new(new ThreeColor(0xdfe4ea));
    /// <summary>(band NEAR end in projected depth px, 1/band span, top-edge strength, final ceiling). z = 0 = off.</summary>
    public readonly ThreeUniform<ThreeVector4> cfg = new(new ThreeVector4(0, 0, 0, AERIAL_HAZE.ceiling));
    /// <summary>(pooling gain, body amplitude, ink-thinning share, ink knee). xy = 0 restores the plain ramp.</summary>
    public readonly ThreeUniform<ThreeVector4> body = new(new ThreeVector4(0, 0, AERIAL_HAZE.inkThin, AERIAL_HAZE.inkKnee));

    public AerialHaze()
    {
        this.body.value.x = AERIAL_HAZE.poolGain;
        this.body.value.y = AERIAL_HAZE.bodyGain;
    }

    /// <summary>
    /// Publish the frame's depth band and the family budget.
    ///
    /// **The viewport is deliberately not a parameter.** Rounds 1 and 2 published
    /// `(bottom edge of the frame, 1 / frame height)`, which normalised the air to whatever the camera
    /// happened to be framing: at 1.8× zoom-out the same world was squeezed into the same band and gained
    /// no air at all, while zooming in fogged the background at two metres. The band is now a fixed
    /// world-space depth (<see cref="AerialHazeModule.AERIAL_HAZE_RANGE_PX"/>) hung on the camera's focus, so the mix
    /// at a fixed world point under a fixed camera is the same number at every zoom and on every viewport.
    ///
    /// The `nearFloor` dead zone is folded into the published band rather than into the shader: the ramp
    /// reads `(bandNear − depth)/bandSpan`, so offsetting the anchor by `(0.5 − nearFloor) × range` and
    /// dividing the span by `1 − nearFloor` reproduces the authored ramp exactly, for free, in the two
    /// floats already being written. The CPU twin <c>AerialHazeModule.aerialHazeMixAtDepth</c> does the same
    /// arithmetic in the same order.
    /// </summary>
    /// <param name="focusProjectedY">pre-zoom projected screen Y of the camera FOCUS (world px) — for a ground
    /// fragment this is the same quantity as the depth axis, by construction.</param>
    /// <param name="budget">`surfaceProfile.haze × composition.aerialPerspective × weatherHazeScale`.</param>
    public void publishDepthBand(double focusProjectedY, double budget)
    {
        // One ACES evaluation per frame for the whole world — the un-tone-mapped family's pole.
        // PORT (performance): the scalar overload — same arithmetic, no arrays.
        presentedLinear(this.color.value.r, this.color.value.g, this.color.value.b, out double presentedR, out double presentedG, out double presentedB);
        this.displayColor.value.setRGB(presentedR, presentedG, presentedB);
        this.cfg.value.set(
            focusProjectedY + AERIAL_BAND_NEAR_OFFSET_PX,
            1 / AERIAL_BAND_SPAN_PX,
            aerialHazeTopEdge(budget),
            AERIAL_HAZE.ceiling);
        // Fluitown extension (comic look, Flui perspective; not in the original): the band runs along the bird's-eye
        // camera's depth axis, so seen from the Flui it hazes one map direction. Godot's depth fog takes over there.
        if (SuppressDepthBand) this.cfg.value.z = 0;
        else if (DepthBandScale != 1) this.cfg.value.z *= DepthBandScale;
    }

    /// <summary>Fluitown (WorldAtmosphere, Flui perspective only): drop the depth band. False in the original.</summary>
    public static volatile bool SuppressDepthBand;

    /// <summary>
    /// Fluitown: scale on the published band strength — 1 is the ported air, 0 none.
    ///
    /// The field's own default is 1, the ported air; the studio sets 0 (TerrainView): the band bleached the far half of
    /// a zoomed-out bird's-eye frame.
    /// </summary>
    public static volatile float DepthBandScale = 1;
}

/// <summary>
/// **Aerial perspective — the one air the whole picture stands in.**
///
/// Under an oblique 2.5D camera there is no perspective convergence and no depth of field, so the ONLY
/// depth cue a wide shot has left is the air itself: the further a thing is, the more of the sky's
/// pigment sits between it and the eye, which lowers its contrast and pulls its colour toward the
/// biome's haze. That cue is what makes a vista read as space rather than as a flat sheet of stickers.
///
/// It existed, but only for the terrain, and only barely:
///  - `HAZE_MAX` 0.13 scaled by the Hub's authored family budget (0.78) and the sanctuary composition
///    (0.86) put **8.7 %** at the very top pixel row, and a curve exponent of 1.7 put **2.7 %** at
///    mid-screen — below the paper-fibre grain, i.e. invisible in the frame it was measured in.
///  - The ACTOR scene received none at all. `worldLightingState` computes `aerialPerspective` for both
///    scenes and only the terrain ever consumed it, so a monster at the far edge of the frame was
///    painted at exactly the air density of one standing at your feet (measured: NPC highlight maxima
///    spread ≤ 2/255 over the full vertical depth of the frame, while the terrain's own row mean moved
///    17/255 — the figures visibly did not belong to the ground they stood on).
///
/// This module is the single home of that cue. The GLSL is self-contained (it needs one world position
/// and three uniforms), the uniform objects are shared by identity so a frame update is a handful of
/// writes for the entire world, and BOTH scenes evaluate the byte-identical function: the actor scene's
/// root carries `scale = 1 / SCENE_UNITS_PER_WORLD_PX`, so its world space IS the terrain's world-pixel
/// space and the screen-Y term needs no conversion at all (the same property ActorShadowProjection relies on).
///
/// **The part that is easy to get catastrophically wrong**, and did ship wrong once: an airlight is
/// NOT a `mix(colour, sky, f)` in radiance. This picture is carried by a thin ink contour, the bottom
/// two stops of the sheet occupy a sliver of the linear range, and the contour material is
/// `toneMapped: false` — so a nominal 20 % air lifted the drawn line from 0.6/255 to 116/255, *above*
/// the ground it was drawn on, and there was no contour left in the far half of the frame. The two
/// corrections live in <c>AERIAL_HAZE_MIX_GLSL</c> (mix under a gamma-2 proxy, so the air compresses
/// the tonal range instead of lifting the black point) and <see cref="AerialHaze.displayColor"/> (an
/// un-tone-mapped mark converges on the DISPLAYED air, not the scene-linear one). Measured result:
/// the far contour sits 52.5/255 below the ground mid-tone and keeps 58 % of its near-field contrast —
/// `artifacts/aaa-current/aerial-perspective-ink.md`.
///
/// This is biome grade / atmosphere, NOT the exposure dial: `PRESENTATION_EXPOSURE` is untouched, and
/// the haze colour is already a graded biome pigment (`mix(biome.haze, paper, 0.42)` in
/// `threeTerrain.setBiome`), so distance warms and settles rather than washing the sheet grey.
/// Cost: zero draws, zero geometry; one `pow`, two value-noise taps and six `sqrt` per shaded fragment,
/// plus one CPU ACES evaluation per frame for the whole world.
/// </summary>
public static partial class AerialHazeModule
{
    /// <summary>
    /// **The depth the air is measured over, in WORLD px — not in viewport fractions.**
    ///
    /// The first two rounds published the band as `(bottom edge of the frame, 1 / frame height)`, which made
    /// the cue a screen GRADIENT rather than air: zooming out 1.8× added no haze at all (the same world was
    /// squeezed into the same normalised band), and zooming in fogged the background at two metres. Air is a
    /// property of the DISTANCE between the eye and the thing, so the band is now a fixed world-space depth
    /// anchored on the camera focus. The frame no longer appears in the arithmetic at all: a fixed world
    /// point in a fixed camera carries the identical mix at every zoom and on every viewport.
    ///
    /// The value is the projected depth a DEFAULT desktop gameplay frame spans — an 846 px viewport divided
    /// by `CAMERA.baseZoom` 0.74 ≈ 1143 world px — deliberately, so the default shot stays within ~0.3 % of
    /// the ramp round 2 shipped and the only thing that changes is what happens when the camera zooms.
    /// </summary>
    public const double AERIAL_HAZE_RANGE_PX = 1150;

    /// <summary>
    /// The authored atmosphere budget.
    ///
    /// `maxAtTopEdge` is the mix toward the biome haze pigment at the far end of <see cref="AERIAL_HAZE_RANGE_PX"/>
    /// for a family whose combined budget (`TerrainSurfaceProfile.haze × BiomeLightingComposition.
    /// aerialPerspective`) is exactly 1. The ramp is `pow(u, curve)` over a band that starts `nearFloor` of
    /// the range in front of that far end — the dead zone is what keeps the foreground plane completely clean
    /// while the MIDDLE distance still gains readable air. Quoted against the DEFAULT frame, where the range
    /// is one frame deep and the screen fraction below is therefore also a depth:
    ///
    /// | screen fraction | 0.15 (near stand) | 0.5 (mid) | 0.85 | 1.0 (top row) |
    /// | --- | --- | --- | --- | --- |
    /// | Hub mix | 0.8 % | 4.7 % | 8.0 % | 9.4 % |
    ///
    /// (Hub combined budget `0.78 × 0.86 = 0.671`. A stronger calibration reached 7.9 % at mid-screen
    /// and 15.8 % at the top row. Together with its tall-display tail and 2.4× chroma pull that read as a
    /// pale desaturation veil, so this budget deliberately keeps the depth cue below the world palette.)
    ///
    /// `ceiling` is the FINAL clamp, applied after the pooling/body modulation, so the number a reviewer
    /// reads here is genuinely the most air any fragment can carry. The thickest clear-weather family
    /// (drowned: `1.22 × 1.14 = 1.391` → 19.5 %) still fits below it, while pooling and tall-display tails
    /// can no longer turn the upper world into a low-chroma sheet.
    /// </summary>
    public static class AERIAL_HAZE
    {
        public const double maxAtTopEdge = 0.14;
        /// <summary>Fraction of the frame at the BOTTOM that carries no air at all — the untouched foreground plane.</summary>
        public const double nearFloor = 0.1;
        public const double ceiling = 0.2;
        /// <summary>Extra haze in genuinely LOW terrain (basins, chasm throats) as a fraction of the ramp value.</summary>
        public const double poolGain = 0.35;
        /// <summary>± amplitude of the slow world-anchored density body, as a fraction of the ramp value.</summary>
        public const double bodyGain = 0.18;
        /// <summary>
        /// Extra share of the air the DARKEST marks take, on top of the uniform tonal compression.
        ///
        /// An illustrator draws distance with a lighter, thinner line — but never with a line that has been
        /// filled in with sky. This term thins the drawing (contour, hatch, deep shadow) toward the same
        /// pole as everything else, so the mark stays the darkest thing in its own band while visibly losing
        /// weight.
        ///
        /// Round 2 set it at 0.7 and got the RELATION backwards: the line lightened by less than the wash
        /// under it did, so a far contour punched HARDER against its ground than a near one — the exact
        /// opposite of how distance is drawn. At 1.25 the contour's contrast against its own local ground
        /// falls monotonically with depth (85.4 → 64.9 → 49.0 → 39.5 /255 across the default frame) and keeps
        /// ~31 % of its near-field weight at the worst-case top-row air, while staying 27.8/255 below the
        /// ground it is drawn on — well clear of the 18/255 floor that keeps it the darkest mark on the sheet.
        /// See `artifacts/aaa-current/aerial-perspective-ink.md`.
        /// </summary>
        public const double inkThin = 1.25;
        /// <summary>
        /// Perceptual value below which a fragment counts as a drawn MARK rather than a shaded surface.
        ///
        /// It was 0.34 (gamma-2), which is not a line — it is the whole shadow side of every mid-dark pigment.
        /// Raising `inkThin` under that knee therefore lifted a distant figure's shaded cloth almost as far as
        /// the radiance lerp this module exists to avoid (a dark indigo mass reached 62/255 against the lerp's
        /// 64.5 — a 2.6/255 margin, i.e. the fix had been spent). At 0.18 the extra share reaches only genuine
        /// marks (&lt; 0.032 linear: contour, hatch, deep crevice ink) and the same mass lands at 39.4/255, a
        /// 25.1/255 margin, while the contour keeps exactly the thinning the review asked for.
        /// </summary>
        public const double inkKnee = 0.18;
    }

    /// <summary>The live band's span in projected world px, after the near dead zone is folded in.</summary>
    internal const double AERIAL_BAND_SPAN_PX = AERIAL_HAZE_RANGE_PX * (1 - AERIAL_HAZE.nearFloor);
    /// <summary>How far in FRONT of the camera focus the band's zero (fully clean) end sits, in projected px.</summary>
    internal const double AERIAL_BAND_NEAR_OFFSET_PX = AERIAL_HAZE_RANGE_PX * (0.5 - AERIAL_HAZE.nearFloor);

    /// <summary>The mix at the top pixel row for a family budget, after the weather ceiling.</summary>
    public static double aerialHazeTopEdge(double budget)
    {
        return Math.min(AERIAL_HAZE.ceiling, AERIAL_HAZE.maxAtTopEdge * Math.max(0, budget));
    }

    /// <summary>
    /// The live world's atmosphere.
    ///
    /// A module-level instance is deliberate: the terrain layer is the only producer of the screen band and
    /// the biome pigment, the actor material family is a pure consumer that is constructed long before any
    /// terrain layer exists, and threading a reference between the two scenes through five call sites would
    /// be exactly the kind of second truth this codebase forbids. A page shows ONE world at a time; a
    /// consumer compiled without a producer reads strength 0 and is byte-identical to the un-hazed program.
    /// </summary>
    /// <remarks>Main-thread-only renderer state (one live world per process) — deliberately not [ThreadStatic].</remarks>
    public static readonly AerialHaze WORLD_AERIAL_HAZE = new AerialHaze();
}
