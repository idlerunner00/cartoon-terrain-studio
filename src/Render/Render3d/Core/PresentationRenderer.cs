// Port of packages/client/src/render3d/core/presentationRenderer.ts — keep in lockstep with the original.
//
// PORT NOTES
// * Ported for `presentedLinear` / `PRESENTATION_EXPOSURE` (aerialHaze.ts publishes the displayed air with it).
// * SKIPPED (three.js renderer plumbing, no engine-free meaning): `configureRendererDiagnostics(renderer)` sets
//   `renderer.debug.checkShaderErrors = import.meta.env.DEV`; `configurePresentationRenderer(renderer, shadows = true)`
//   sets localClippingEnabled = true, outputColorSpace = SRGB, toneMapping = VISUAL_QUALITY.previewParity ?
//   ACESFilmic : NoToneMapping, toneMappingExposure = PRESENTATION_EXPOSURE, shadowMap.enabled =
//   VISUAL_QUALITY.previewParity && shadows, shadowMap.type = PCFShadowMap. The Godot presentation layer owns
//   the equivalent configuration.
using System.Collections.Generic;

namespace Fluitown.Render;

public static partial class PresentationRenderer
{
    /// <summary>
    /// One neutral ACES exposure shared by the gameplay output and every isolated 3D presentation surface.
    ///
    /// **Do not "correct" this against a luminance histogram.** It was raised to 1.0 on 2026-07-30 because
    /// the frame's 99th percentile sat at 135/255 and that reads as under-exposure on paper — but the low,
    /// warm key IS the art direction here (the owner's "golden style"), and lifting the median onto the
    /// filmic mid-grey destroyed it immediately. The compressed top end is intentional: this world is
    /// pigment washed onto paper, not a photographic exposure. Anything that wants more highlight range has
    /// to come from the light rig and the biome grade, under art review — never from this dial.
    /// </summary>
    public const double PRESENTATION_EXPOSURE = 0.6;

    /* ACES filmic, exactly as three.js writes it in `<tonemapping_pars_fragment>`. */
    private static readonly double[][] ACES_IN =
    {
        new[] { 0.59719, 0.35458, 0.04823 },
        new[] { 0.076, 0.90834, 0.01566 },
        new[] { 0.0284, 0.13383, 0.83777 },
    };
    private static readonly double[][] ACES_OUT =
    {
        new[] { 1.60475, -0.53108, -0.07367 },
        new[] { -0.10208, 1.10813, -0.00605 },
        new[] { -0.00327, -0.07276, 1.07602 },
    };

    /// <summary>
    /// PORT (performance): <c>presentedLinear()</c> of one colour without its five arrays —
    /// the air is published every frame. The same operations in the same order: bit-identical.
    /// </summary>
    public static void presentedLinear(double r, double g, double b, out double outR, out double outG, out double outB)
    {
        double e0 = r * PRESENTATION_EXPOSURE, e1 = g * PRESENTATION_EXPOSURE, e2 = b * PRESENTATION_EXPOSURE;
        double[] i0 = ACES_IN[0], i1 = ACES_IN[1], i2 = ACES_IN[2];
        double f0 = acesFit(i0[0] * e0 + i0[1] * e1 + i0[2] * e2);
        double f1 = acesFit(i1[0] * e0 + i1[1] * e1 + i1[2] * e2);
        double f2 = acesFit(i2[0] * e0 + i2[1] * e1 + i2[2] * e2);
        double[] o0 = ACES_OUT[0], o1 = ACES_OUT[1], o2 = ACES_OUT[2];
        outR = clampUnit(o0[0] * f0 + o0[1] * f1 + o0[2] * f2);
        outG = clampUnit(o1[0] * f0 + o1[1] * f1 + o1[2] * f2);
        outB = clampUnit(o2[0] * f0 + o2[1] * f1 + o2[2] * f2);
    }

    private static double acesFit(double x)
    {
        double a = x * (x + 0.0245786) - 0.000090537;
        double b = x * (0.983729 * x + 0.432951) + 0.238081;
        return a / b;
    }

    private static double clampUnit(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
}
