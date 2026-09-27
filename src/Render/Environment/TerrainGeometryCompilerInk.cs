// Port of packages/client/src/render/environment/terrainGeometryCompilerInk.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public static partial class TerrainGeometryCompilerInk
{
    /// <summary>
    /// Geometry-only subset of the canonical HANDINK contract.
    ///
    /// This is a MIRROR, not a second art direction. `render/inkLook.ts` owns the dials, but it imports `three`
    /// (uniform factories) and the world palette, and the terrain bake also runs inside a worker whose leaf-module
    /// graph may not pull either in. The numbers below are therefore the same numbers, carried across that
    /// boundary — and `render/inkLook.test.ts` asserts field-for-field equality with `INK_LOOK.contour`, so the
    /// two can never silently drift into two languages.
    /// </summary>
    public static class TERRAIN_GEOMETRY_INK
    {
        public const bool enabled = true;

        public static class contour
        {
            public const double wallAlpha = 0.78;
            public const double wallWidth = 2.2;
            public const double terraceAlpha = 0.56;
            public const double terraceWidth = 1.75;
            public const double terraceMinDrop = 0.9;
            /// <summary>A waterline has no drop, so it is gated on the CONTACT alone — see `terraceMinDrop`'s counterpart.</summary>
            public const double shoreAlpha = 0.62;
            /// <summary>Pigment pooling on the lower ground at the foot of a riser (the crest line's missing second event).
            ///
            ///  The width is in WORLD px and has to survive the zoom-out: at 4.5 it measured 1.7 SCREEN px on an
            ///  ordinary wheel-out framing, which is under the width at which a soft band can be told from a hairline
            ///  at all. Widening alone is worthless (a wide nothing is a wide nothing) — it is only allowed here
            ///  because the pigment below now carries a measured drop.</summary>
            public const double footAlpha = 0.45;
            public const double footWidth = 12;
            public const double wobble = 0.35;
            public const double washEdgeAlpha = 0.26;
            public const double washEdgeWidth = 9;
        }

        /// <summary>
        /// TRANSMISSION of each washed mark — the one number that decides whether it arrives in the frame.
        ///
        /// See <see cref="terrainInkWashPigment"/>. A mark's delivered drop is `1 - (1 - alpha*(1 - transmission))^(1/2.2)`
        /// of whatever it is laid on; lower transmission = darker pigment = more drop.
        /// </summary>
        public static class washTransmission
        {
            /// <summary>The drawn waterline: a true contour, so it is close to ink.</summary>
            public const double shore = 0.16;
            /// <summary>The riser foot: pigment, not a line — the band gathers, it does not draw. Chosen from the measured
            ///  hierarchy, not by feel: at this transmission the band's peak delivers ~53 % of the terrace crest line
            ///  it belongs to on the same probe tile, so the step keeps ONE silhouette.</summary>
            public const double foot = 0.4;
            /// <summary>The cap's own cut-edge pool: the gentlest member, and the one a frame review already accepted as
            ///  landed. Held within ~15 % of that delivered weight — the change here is the LAW, not the level.</summary>
            public const double edge = 0.42;
        }
    }

    public const int TERRAIN_GEOMETRY_INK_WORLD_LINE = 0x050505;

    public static double terrainGeometryInkContourWidth(double @base, double hash01)
    {
        return @base * (1 + (hash01 - 0.5) * 2 * TERRAIN_GEOMETRY_INK.contour.wobble);
    }

    /// <summary>
    /// The cool drift of a washed pigment — the painter's shade temperature, so a wash is never CG black.
    ///
    /// Deliberately luminance-neutral (the three weights average to 1 under Rec.709 to within 1 %): it moves the
    /// HUE of a wash, never its weight, so the delivered-drop law below stays a function of `transmission` alone.
    /// </summary>
    private static readonly double[] TERRAIN_INK_WASH_TINT = { 0.9, 0.99, 1.16 };

    /// <summary>
    /// The ONE rule for every washed terrain mark: pigment that darkens by a KNOWN amount whatever it lands on.
    ///
    /// ## Why this exists — three marks shipped invisible before it did
    ///
    /// The terrain overlay batch is an unlit `MeshBasicMaterial` blended into a linear HDR target, so a mark
    /// composites as `lit·(1−α) + pigment·α`. Authoring the pigment as a PALETTE colour ("the cap mixed 88 % toward
    /// its own edgeDark") therefore says nothing at all about the frame: the cap under it has been through the
    /// biome's light rig and `PRESENTATION_EXPOSURE`, the pigment has not, and the gap between the two is a
    /// different number in every biome and on every material. Two frame reviews measured the results — the riser
    /// foot band at 0.5/255, the waterline at under 2/255 on a bright bank, and on the Hub sea the bank got
    /// *brighter* at the waterline, i.e. the mark delivered the WRONG SIGN.
    ///
    /// ## The rule
    ///
    /// A wash on paper does not paint a colour over the light; it TRANSMITS a fraction of it. So the pigment is the
    /// host surface's own colour scaled to `transmission` of its value (and cooled, because shade is cool). The
    /// composite is then `lit·(1 − α·(1 − transmission))` — the light cancels, and the delivered drop becomes a
    /// pure function of the two authored numbers and the brightness the mark is laid on:
    ///
    /// ```
    /// drop/255 ≈ presented · (1 − (1 − α·(1 − transmission))^(1/2.2))
    /// ```
    ///
    /// which is what <c>terrainInkWashDropFraction</c> states and what the bake test asserts across four biomes.
    /// Hue survives (the mark is the ground's own pigment, gathered), no mark can ever lighten its host, and no
    /// mark needs a per-biome number.
    /// </summary>
    public static int terrainInkWashPigment(int hostHex, double transmission)
    {
        double t = transmission < 0 ? 0 : transmission > 1 ? 1 : transmission;
        int @out = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            int shift = 16 - channel * 8;
            int value = (hostHex >> shift) & 255;
            double scaled = Math.round(Math.min(255, value * t * TERRAIN_INK_WASH_TINT[channel]));
            @out |= Js.ToInt32(scaled) << shift;
        }
        return @out;
    }
}
