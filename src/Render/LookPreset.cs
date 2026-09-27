// Port of packages/client/src/render/lookPreset.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;

namespace Fluitown.Render;

// **Look presets** — which authored art direction this session renders in.
//
// The shipping direction is HANDINK (ink wash on paper, `inkLook.ts`). It is already a complete
// stylizer: cel-banded light, inverted-hull silhouette ink, baked world contours, cool shade pigment
// and a paper/print screen finish. The recurring question "should we put a Borderlands-style shader
// over everything" is therefore NOT a pipeline question — a second global stylizer would be a second
// implementation of a concern that already has one home. It is a question about where the DIALS of
// that one stylizer sit.
//
// This module makes that question answerable from a frame instead of from a description: `?look=comic`
// selects a second authored dial set that pushes the same treatments toward a printed-comic read
// (harder cel steps, screentone in the shade zone, a visibly thicker ink line, more pigment). It adds
// **no render pass, no render target and no new shader**; every value below already had a consumer.
//
// `?look=comic-ink` is the same direction with the screentone raster switched back off. The two exist
// as separate arms because the first capture round showed the raster is not a minor accent: it is the
// one dial that changes every pixel of the frame, and it has to be judged on its own rather than
// bundled with the ink/pigment moves it would otherwise mask.
//
// Resolution follows the established lever pattern (resolveVisualQualityFeatures, resolveGpuPerfPolicy): a
// pure resolver, read ONCE from `location.search` and frozen, because a look dial feeds compiled GLSL
// literals, a generated wash ramp texture and bake-time contour geometry — none of which may change while
// frames are in flight. An unknown or absent `look` resolves to the shipping direction, so no query string
// can produce anything other than the authored HANDINK values unless a preset is named exactly.
//
// Known gap, deliberately not papered over: INK_LOOK.material.character is documentation only — no consumer
// reads it, and the actor material (`injectHandInk`) carries no hatch raster at all. Character-side
// screentone would be real shader work (the actor splice has no world-space drawing sheet), so this preset
// does not pretend to deliver it.
//
// `LookPresetKey` is the string union 'handink' | 'comic' | 'comic-ink' → `string`.

/// <summary>
/// The complete dial set of one non-shipping art direction. Absolute values where the baseline holds a
/// single authored dial; SCALES where the baseline holds a role hierarchy (outline widths, contour
/// pigment), so a preset can change the weight of a family without ever inverting the authored
/// relationship between its members.
/// </summary>
public sealed class LookPresetDials
{
    /// <summary>Shade-zone stroke darkness at full depth (HANDINK ships 0 — the raster is off, not absent).</summary>
    public double hatchStrength;
    /// <summary>Stroke spacing in world px. Printed screentone is finer than a painter's ink-wash hatch.</summary>
    public double hatchPeriodPx;
    /// <summary>Number of flat wash planes the direct light is quantized into. Fewer = harder cel steps.</summary>
    public double washBands;
    /// <summary>How far a pixel is pulled onto its wash plane (0..1). The dial that makes a band edge READ.</summary>
    public double washStrength;
    /// <summary>Lowest wash plateau shared by actors and terrain — the floor of the value language.</summary>
    public double washLowestTone;
    /// <summary>Fraction of each actor ramp band spent on the anti-flicker transition. Lower = crisper step.</summary>
    public double washEdgeSoftness;
    /// <summary>Cool drift of deep shade. A comic shadow is dark and neutral, not blue-violet.</summary>
    public double shadowInkStrength;
    /// <summary>Multiplier on every inverted-hull width. The single most visible dial at production zoom.</summary>
    public double outlineWidthScale;
    /// <summary>Multiplier on every baked world-contour opacity (clamped to fully opaque ink).</summary>
    public double contourAlphaScale;
    /// <summary>Multiplier on every baked world-contour width.</summary>
    public double contourWidthScale;
    /// <summary>Screen paper-fibre strength. A printed page carries far less fibre than watercolour paper.</summary>
    public double paperAlpha;
    /// <summary>Edge pigment gathered at the frame rim.</summary>
    public double vignetteAlpha;
    /// <summary>HSL saturation ceiling for actor body pigment — a deliberate, staged widening of PIGMENT_LAW.</summary>
    public double actorChromaCeiling;
    /// <summary>How much of each biome's authored grade identity survives into the final output draw.</summary>
    public GradeResidualDials gradeResidual;

    /// <summary>The inline `{ brightness, contrast, saturation }` type of <see cref="gradeResidual"/>.</summary>
    public sealed class GradeResidualDials
    {
        public double brightness;
        public double contrast;
        public double saturation;
    }

    /// <summary>Shallow copy, the equivalent of `{ ...dials }` (the nested grade residual stays shared).</summary>
    public LookPresetDials Clone() => (LookPresetDials)MemberwiseClone();
}

public sealed class LookPreset
{
    /// <summary>LookPresetKey: 'handink' | 'comic' | 'comic-ink'.</summary>
    public string key;
    /// <summary>`undefined` (null) for the shipping direction: every consumer keeps its own authored constant.</summary>
    public LookPresetDials? dials;
}

public static partial class LookPresetModule
{
    /// <summary>
    /// The **printed-comic** direction — the evaluated answer to "how far toward Borderlands can the
    /// existing stack go without a second stylizer".
    ///
    /// Every number is a deliberate move away from the shipping baseline, listed with its counterpart:
    ///
    /// | dial                | HANDINK | COMIC | why |
    /// | ------------------- | ------- | ----- | --- |
    /// | hatch strength      |   0     | 0.30  | the screentone the ink-wash direction switched off |
    /// | hatch period px     |  10.5   |  8.0  | printed tone is finer than a painted hatch |
    /// | wash bands          |   4     |   3   | fewer, harder cel steps |
    /// | wash strength       |  0.40   | 0.72  | the band edge has to be a drawn edge |
    /// | wash lowest tone    |  0.40   | 0.32  | deeper darks — comic value contrast |
    /// | wash edge softness  | 0.075   | 0.035 | crisper step on actor bodies |
    /// | shadow ink strength |  0.28   | 0.16  | dark shade, not a blue wash |
    /// | outline width       |   1x    | 1.6x  | ~1 CSS px → ~1.6 CSS px at production zoom |
    /// | contour alpha/width |   1x    | 1.22x / 1.35x | the world's drawn edges match the actors' |
    /// | paper alpha         | 0.055   | 0.018 | print, not watercolour paper |
    /// | vignette alpha      |  0.06   | 0.09  | a comic frames its panel harder |
    /// | actor chroma        |  0.72   | 0.90  | saturated flats are half the comic read |
    /// | grade residual      | .32/.48/.58 | .32/.78/.92 | let the authored biome identity through |
    ///
    /// Exposure is deliberately untouched: PRESENTATION_EXPOSURE was measured against real frames (median
    /// 111–115/255, no clipping) and is a display-transform result, not an art direction.
    /// </summary>
    public static readonly LookPresetDials COMIC_DIALS = new()
    {
        hatchStrength = 0.3,
        hatchPeriodPx = 8,
        washBands = 3,
        washStrength = 0.72,
        washLowestTone = 0.32,
        washEdgeSoftness = 0.035,
        shadowInkStrength = 0.16,
        outlineWidthScale = 1.6,
        contourAlphaScale = 1.22,
        contourWidthScale = 1.35,
        paperAlpha = 0.018,
        vignetteAlpha = 0.09,
        actorChromaCeiling = 0.9,
        gradeResidual = new LookPresetDials.GradeResidualDials { brightness = 0.32, contrast = 0.78, saturation = 0.92 },
    };

    /// <summary>
    /// The comic direction with the shade-zone raster switched back off — every other move kept.
    ///
    /// This is the arm that answers the actually interesting half of the question: how much of a comic read
    /// comes from the ink line, the harder cel steps and the pigment, once the screentone is not covering
    /// the whole frame. Sharing <see cref="COMIC_DIALS"/> by spread guarantees the two arms differ in exactly one
    /// dial, which is what makes the capture pair evidence rather than an impression.
    /// </summary>
    public static readonly LookPresetDials COMIC_INK_DIALS = withHatchStrength(COMIC_DIALS, 0);

    private static LookPresetDials withHatchStrength(LookPresetDials source, double hatchStrength)
    {
        var dials = source.Clone();
        dials.hatchStrength = hatchStrength;
        return dials;
    }

    private static readonly Dictionary<string, LookPresetDials> STAGED_PRESETS = new()
    {
        ["comic"] = COMIC_DIALS,
        ["comic-ink"] = COMIC_INK_DIALS,
    };

    /// <summary>Pure resolver — unit-testable with an explicit query string; no browser globals touched.</summary>
    public static LookPreset resolveLookPreset(string? search = null)
    {
        search ??= currentSearch();
        string? requested = urlSearchParamsGet(search, "look");
        LookPresetDials? dials = requested == null ? null : STAGED_PRESETS.TryGetValue(requested, out var d) ? d : null;
        if (dials == null) return new LookPreset { key = "handink", dials = null };
        return new LookPreset { key = requested!, dials = dials };
    }

    /// <summary>
    /// `globalThis.location?.search ?? ''`. The engine-free port has no browser location, so this is always the
    /// empty query and the session renders the shipping HANDINK direction (exactly what the original does in
    /// any context without `location`).
    /// </summary>
    private static string currentSearch()
    {
        return "";
    }

    /// <summary>
    /// `new URLSearchParams(search).get(name)`: strip one leading "?", split on "&amp;", split each pair at its first
    /// "=", decode "+" as space and percent escapes, and return the first value whose decoded name matches (or
    /// null when absent).
    /// </summary>
    private static string? urlSearchParamsGet(string search, string name)
    {
        string input = search.StartsWith("?", StringComparison.Ordinal) ? search.Substring(1) : search;
        foreach (string sequence in input.Split('&'))
        {
            if (sequence.Length == 0) continue;
            int eq = sequence.IndexOf('=');
            string rawName = eq >= 0 ? sequence.Substring(0, eq) : sequence;
            string rawValue = eq >= 0 ? sequence.Substring(eq + 1) : "";
            if (urlFormDecode(rawName) == name) return urlFormDecode(rawValue);
        }
        return null;
    }

    private static string urlFormDecode(string value)
    {
        return Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    /// <summary>The browser-bound singleton every look consumer reads (frozen at module load).</summary>
    public static readonly LookPreset LOOK_PRESET = resolveLookPreset();
}
