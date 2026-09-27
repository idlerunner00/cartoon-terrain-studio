// Port of packages/client/src/render/inkLook.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `HandInkLook = Dials<typeof HANDINK_DIALS>` (the dial tree with literal types widened) is the class tree
//   below; each `{ ...base.group, x }` spread is a MemberwiseClone of that group plus assignments, and groups the
//   TS spreads over unchanged (`...base`) stay SHARED by reference exactly like the original.
// * GLSL template literals are C# raw string literals with the identical text; `${x.toFixed(n)}` → Js.ToFixed.
// * three.js value types → ThreeValues.cs (`{ value: new Vector4(...) }` → ThreeUniform<ThreeVector4>,
//   `new Color(INK_WORLD_LINE)` → ThreeColor, sRGB hex → linear exactly like three).
// * `String.prototype.replace(string, string)` replaces the FIRST occurrence only → replaceFirst below (the
//   replacement is a toFixed digit string, so JS `$` substitution patterns cannot occur).
using static Fluitown.Render.LookPresetModule;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>The authored dial tree with its literal types widened, so a preset may substitute measured values.</summary>
public sealed class HandInkLook
{
    /// <summary>Master switch for the whole hand-inked world direction.</summary>
    public bool enabled;
    public HatchDials hatch;
    public WashDials wash;
    public ShadowInkDials shadowInk;
    public PaperFibreDials paperFibre;
    public MaterialLineDials materialLine;
    public MaterialDials material;
    public VolumeOutlineDials volumeOutline;
    public ActorReadabilityDials actorReadability;
    public ContactShadowDials contactShadow;
    public ContourDials contour;
    public WashTransmissionDials washTransmission;
    public PostDials post;

    /// <summary>`{ ...look }` (groups stay shared).</summary>
    public HandInkLook Clone() => (HandInkLook)MemberwiseClone();

    public sealed class HatchDials
    {
        public double strength;
        public double periodPx;
        public double shadeHi;
        public double shadeLo;
        public double crossShade;

        public HatchDials Clone() => (HatchDials)MemberwiseClone();
    }

    public sealed class WashDials
    {
        public double bands;
        public double strength;
        public double lowestTone;
        public double edgeSoftness;

        public WashDials Clone() => (WashDials)MemberwiseClone();
    }

    public sealed class TintDials
    {
        public double r;
        public double g;
        public double b;
    }

    public sealed class ShadowInkDials
    {
        public double strength;
        public TintDials tint;

        public ShadowInkDials Clone() => (ShadowInkDials)MemberwiseClone();
    }

    public sealed class PaperFibreDials
    {
        public double strength;
        public double terrainStrength;
    }

    public sealed class MaterialLineDials
    {
        public double floorAlpha;
        public double rockCapAlpha;
        public double geologicalFaceAlpha;
        public double washStrength;
    }

    public sealed class MaterialDials
    {
        public double terrain;
        public double worldObject;
        public double water;
        public double character;
        public double interactive;
    }

    public sealed class VolumeOutlineDials
    {
        public double character;
        public double npc;
        public double worldObject;
        public double interactive;
        public double surfaceRelief;
    }

    public sealed class ActorReadabilityDials
    {
        public double shadeLift;
        public double rimLift;
        public double rimPower;
    }

    public sealed class ContactShadowDials
    {
        public int color;
        public double actorOpacity;
        public double squash;
        public double liftPx;
        public double radiusScale;
        public double minRadiusPx;
        public double edgeStart;
    }

    public sealed class ContourDials
    {
        public double wallAlpha;
        public double wallWidth;
        public double terraceAlpha;
        public double terraceWidth;
        public double terraceMinDrop;
        public double shoreAlpha;
        public double shoreWidth;
        public double wobble;
        public double washEdgeAlpha;
        public double washEdgeWidth;
        public double footAlpha;
        public double footWidth;
        public double washEdgeActorAlpha;

        public ContourDials Clone() => (ContourDials)MemberwiseClone();
    }

    public sealed class WashTransmissionDials
    {
        public double shore;
        public double foot;
        public double edge;
    }

    public sealed class PostDials
    {
        public double paperAlpha;
        public int vignetteTint;
        public double vignetteAlpha;
        public double vignetteStart;
        public double filmToe;
        public double filmShoulder;
        public double filmGamutCompression;

        public PostDials Clone() => (PostDials)MemberwiseClone();
    }
}

/// <summary>
/// Shared uniform set for the HANDINK terrain shader hook — create ONCE per renderer and hand the same
/// objects to every material's `onBeforeCompile`, so one runtime write reaches all passes.
///  - inkA: (hatchStrength, hatchPeriodPx, washBands, washStrength)
///  - inkB: (terrainPaperFibreStrength, shadowInkStrength, hatchShadeHi, hatchShadeLo)
///  - inkTint: cool shadow tint (linear multipliers)
/// </summary>
public sealed class InkLookUniforms
{
    public ThreeUniform<ThreeVector4> inkA;
    public ThreeUniform<ThreeVector4> inkB;
    public ThreeUniform<ThreeVector4> inkStructure;
    public ThreeUniform<ThreeVector3> inkTint;
    public ThreeUniform<ThreeColor> inkLine;
}

/// <summary>
/// **HANDINK** — the project's hand-inked storybook art direction, in ONE place.
///
/// The actors, gear, creatures and UI already speak a single illustrated language (painted volume + thin
/// confident ink, see `ink.ts` and the ink UI theme). What used to fall OUT of that language
/// was the real-3D terrain base: physically lit, smooth-gradient, CG-plastic. This module pulls the WORLD into
/// the drawn language — so the whole frame reads like one illustrator worked on paper:
///
///  1. **Wash banding** — the terrain's smooth lighting is quantized into a few soft tone planes, the way an
///     ink-wash painter lays flat washes instead of CG ramps (bounded: gradients are damped, never destroyed).
///  2. **Shadow hatching** — where the light drops off, diagonal hand-hatch strokes fade in (a second, crossed
///     direction in the deepest shade). World-anchored under the shear projection, so strokes are stable on
///     screen and never boil.
///  3. **Cool ink shadows** — deep shade drifts toward the cool ink the actor painter uses (`ink.ts` COOL),
///     instead of neutral CG black.
///  4. **Laid paper** — a world-anchored, page-oriented sheet (pulp + the mould's laid wires) applied to the
///     terrain's diffuse LIGHT, so flats read as a wash laid on paper rather than as vinyl.
///  5. **Drawn contours** — baked ink strokes with hand-pressure width wobble along terrace crests, wall caps
///     and shorelines (the terrain finally gets the same bold, confident ink silhouette the actors carry).
///  6. **Paper screen finish** — a faint procedural paper sheet + an ink-tinted (never pure-black) vignette
///     composited over the finished frame, binding terrain, actors, FX and UI chrome into one sheet.
///
/// Everything is driven from the <see cref="INK_LOOK"/> dials below and applied through shader UNIFORMS + a few
/// config-gated bake/overlay branches — flip `INK_LOOK.enabled` to `false` and the whole direction
/// switches off (uniform strengths go to 0, bake branches skip, post overlays hide); no other file needs to
/// change. Palette anchors stay WORLD_INK — this module styles, it does not fork colour truth.
///
/// HANDINK_DIALS is the AUTHORED, shipping dial set. <see cref="INK_LOOK"/> is what the session actually
/// renders with: identical to the baseline unless a staged LOOK_PRESET substitutes a second authored art
/// direction. Consumers read <see cref="INK_LOOK"/> and are unaffected — that is the point of keeping one
/// stylizer with dials instead of a second stylizer laid over the first.
/// </summary>
public static partial class InkLook
{
    private static readonly HandInkLook HANDINK_DIALS = new HandInkLook
    {
        /** Master switch for the whole hand-inked world direction. */
        enabled = true,

        /** Shadow hatching (terrain surface + water, strength-scaled per material). Hatch depth follows the
         *  SHADING (lit result ÷ albedo — i.e. how much the LIGHT darkened a pixel), not absolute brightness, so
         *  strokes appear in cast shadow and on shade faces identically in a bright alpine and a dark void biome. */
        hatch = new HandInkLook.HatchDials
        {
            /** Stroke darkness (0..1) at full depth. */
            // Characters carry no hatch raster. Terrain now follows that clean cartoon hierarchy too: volume comes
            // from toon wash planes, cool shade and silhouette ink; paper fibre supplies the only fine texture.
            strength = 0,
            /** Stroke spacing in world px (world-anchored → chunk-stable, never boiling). */
            periodPx = 10.5,
            /** Shading level below which the first hatch direction fades in / is fully present. Calibrated against
             *  the LIGHT_RIG: a sun-shadowed cap sits at ~0.56 shading (solid strokes), the lit west face at ~0.61
             *  (light strokes), south/east faces at ~0.37–0.43 (full + crossing). */
            shadeHi = 0.7,
            shadeLo = 0.42,
            /** Shading below which the crossed second direction joins (the deepest shade). */
            crossShade = 0.44,
        },

        /** Ink-wash banding of the LIGHT (0 bands disables): the smooth CG shading ramp is quantized into a few
         *  flat wash planes, so cast shadows and face gradients read as deliberate layered washes. */
        wash = new HandInkLook.WashDials
        {
            // Four planes preserve the graphic toon read without the broad, dirty-looking three-band shadow blocks.
            bands = 4,
            /** How far a pixel is pulled toward its quantized wash plane (0..1). */
            strength = 0.4,
            /** Lowest actor/toon-ramp plateau; shared so figures and terrain inhabit one value language. */
            lowestTone = 0.4,
            /** Fraction of each actor ramp band used for a compact anti-flicker transition. */
            edgeSoftness = 0.075,
        },

        /** Cool ink drift of deep shade (temperature contrast — warm light, cool shade, like the actor painter). */
        shadowInk = new HandInkLook.ShadowInkDials
        {
            // The light rig and final split tone already cool shade. Keep this local pigment contribution restrained
            // so their combination gives coloured form instead of large green-grey blocks.
            strength = 0.28,
            /** Multiplied onto shaded colour: a cool, slightly blue-violet ink cast. */
            tint = new HandInkLook.TintDials { r = 0.88, g = 0.92, b = 1.1 },
        },

        /** Anisotropic paper-fibre grain (0 disables the tap's visibility).
         *
         *  TWO dials because there are two sampling DOMAINS, not because the rule forked. Actor/prop micro-grain
         *  is taken in actor-scene units over a toon ramp that already carries painted volume
         *  (`render3d/core/materials.ts` scales this by 1.6); the terrain laid line is taken in WORLD
         *  PIXELS on a large flat that carries nothing else. Raising one number for both would have multiplied
         *  the actor grain by four as a side effect of retuning the ground. */
        paperFibre = new HandInkLook.PaperFibreDials
        {
            /** Actor/prop micro-grain over painted volume — deliberately the quiet member of the family. */
            strength = 0.022,
            /** Terrain laid line, as a fraction of the diffuse LIGHT it modulates (peak excursion is this times
             *  INK_LAID_PAPER_PEAK ≈ 0.90). The ground is the largest flat in the frame and the only layer
             *  that can put a material octave in the screen-pixel band — the three material octaves run at 30–390
             *  world px, two orders above it.
             *
             *  The number is not taste: `inkLaidPaper.test.ts` ports the shipped GLSL and measures
             *  the layer's one-pixel neighbour delta at the gameplay zoom. A smooth noise blob cannot reach the
             *  legibility threshold at any sane amplitude (band-limited signals cap at `4·A/period`, and a bell-shaped
             *  value-noise sample spends its range on a peak it almost never reaches) — which is why the previous
             *  0.09 of pure noise measured 0.4/255 and shipped invisible. The laid RIB carries the band energy at a
             *  bounded amplitude, and this dial then sets the weight of the whole sheet.
             *
             *  The terrain already carries broad wash planes, material marks and silhouettes. Keep this paper tooth
             *  below those authored forms: it remains visible on large quiet caps without turning every surface into
             *  a field of high-contrast scanlines. */
            terrainStrength = 0.06,
        },

        /** Sparse, material-following linework inside large terrain shapes. Unlike shadow hatching this does not
         *  rasterise a whole shade region: ground veins, rock-cap joints and geological bedding only catch ink at
         *  selected ridges of the existing material field. The order stays quieter than the true silhouette. */
        materialLine = new HandInkLook.MaterialLineDials
        {
            floorAlpha = 0.09,
            rockCapAlpha = 0.12,
            geologicalFaceAlpha = 0.2,
            washStrength = 0.08,
        },

        /** Semantic material hierarchy. Consumers select a role, never an arbitrary hatch strength. */
        material = new HandInkLook.MaterialDials
        {
            /** Physical terrain and terrain-baked props define the full world treatment. */
            terrain = 1,
            /** Static world volumes share the terrain language with slightly quieter strokes. */
            worldObject = 0.72,
            /** Liquid keeps the pigment language while preserving moving highlights and translucency. */
            water = 0.55,
            /** Characters deliberately stay clean: wash/grain/cool shade, but no world-space hatch raster. */
            character = 0,
            /** Interactives use clean silhouettes and emissive gameplay accents over decorative texture. */
            interactive = 0.3,
        },

        /** Nominal inverted-hull widths in scene units. Tiny props may scale these, but keep the role hierarchy. */
        volumeOutline = new HandInkLook.VolumeOutlineDials
        {
            // These widths deliberately survive the far gameplay zoom. The old 0.032 character hull frequently
            // resolved to a single partially covered MSAA pixel, so the game's defining ink read as a grey hairline.
            character = 0.04,
            // Service NPCs need a distinct mid-scale silhouette: stronger than world objects, quieter than players.
            npc = 0.024,
            worldObject = 0.018,
            interactive = 0.022,
            /**
             * **SURFACE RELIEF FALLBACK**, in AUTHOR units. Near actors and separate world-volume hulls use the
             * exact full-or-none stencil union in `inkHull.ts`: any body sample rejects ink and
             * any exterior sample keeps the complete nib. No depth threshold can guarantee that for articulated
             * parts — a head may stand arbitrarily further forward than the arm it overlaps on screen.
             *
             * The single-draw horde deliberately interleaves body and reverse-wound hull triangles in one material,
             * so it cannot take two stencil states. This measured relief remains its conservative fallback. Below
             * ~0.16 the production Atlas retains shoulder, belly, hip and tail-chain slivers; at 0.16 they clear while
             * the hull itself remains at authored terrain depth. Ink-coloured drawings carry no hull at source.
             *
             * The tradeoff is bounded and explicit: a fallback surface may appear behind a terrace by 0.16 author
             * units (about 5.6 world px). Do not raise this to chase a near-actor overlap; the binary mask owns that
             * problem now, without exchanging it for a larger terrain-occlusion error.
             */
            surfaceRelief = 0.16,
        },

        /** Readability lift shared by hero, pets and the instanced crowd. Both terms are bounded pulls back toward
         * the actor's own pigment, never white emissive light, so pale and dark Fluis retain their identity. */
        actorReadability = new HandInkLook.ActorReadabilityDials
        {
            /** Recovers a little albedo only in the deepest toon-shade band. */
            shadeLift = 0.045,
            /** Very subtle paper bounce at grazing angles, applied after the darker watercolour edge pool. */
            rimLift = 0.026,
            rimPower = 3.2,
        },

        /** One pooled dynamic contact-shadow rule; static terrain props use the matching baked rule.
         *  EVERY figure ground read derives from these dials — the hero pool (`render3d/core/blobShadows.ts`
         *  BlobShadows) and the instanced crowd shadow (`render3d/core/materials.ts`
         *  createCrowdContactShadowMaterial) compose the same colour/opacity/squash/lift/falloff, never
         *  re-approximate them. */
        contactShadow = new HandInkLook.ContactShadowDials
        {
            color = 0x10151f,
            // Dynamic bodies need a little more pigment than static relief shade to remain visibly planted on the
            // palest paper caps. Kept below one third opacity so a crowd never pools into a black carpet.
            actorOpacity = 0.325,
            squash = 0.55,
            liftPx = 0.6,
            radiusScale = 0.99,
            minRadiusPx = 2,
            /** Radial fraction where the soft ink edge starts fading (1 − smoothstep(edgeStart, 1, r)) —
             *  the "soft dark ellipse" both shadow paths always documented, now one shared profile. */
            edgeStart = 0.36,
        },

        /** Baked drawn contours (applied at tile-bake time).
         *
         *  **These are authored against DELIVERED contrast, not against the palette.** The terrain overlay batch is
         *  an unlit `MeshBasicMaterial`, so a baked mark is composited raw over a cap that has already been through
         *  the biome's light rig, the exposure and the tone map: "mixed 18 % toward ink" is a palette statement, not
         *  a frame statement, and three of the marks below shipped invisible because they were authored as the
         *  former. `terrainGeometryCompiler.test.ts` measures what each one actually removes from the lit cap it is
         *  drawn on (shared `litSurfaceSample` instrument) and asserts a floor per role. Retune against that
         *  measurement — a number here alone cannot tell you whether a mark arrives. */
        contour = new HandInkLook.ContourDials
        {
            /** Wall-cap silhouette ink (the house style's bold rock outline). */
            wallAlpha = 0.78,
            wallWidth = 2.2,
            /** Walkable terrace-crest strokes (steps of ≥ this drop, in levels, get a drawn edge). */
            terraceAlpha = 0.56,
            terraceWidth = 1.75,
            terraceMinDrop = 0.9,
            /** Shoreline ink on the waterline (the drawn bank cut).
             *
             *  `terrainGeometryCompiler.ts` draws this along every Floor→Water contact.
             *  It is the loudest contour in the family on purpose: a waterline is the one structural edge whose two
             *  sides carry no height difference at all, so nothing but the drawn line separates them. It shipped
             *  configured-but-unconsumed for a long time (the crest emitter skipped fluid contacts outright), which is
             *  why a lake bank used to resolve as a two-pixel colour change with no drawn event.
             *
             *  Width sits between the terrace and the wall: wide enough to actually cover a pixel at the gameplay
             *  zoom once the ±35 % hand wobble has had it, never the rock silhouette's weight. */
            shoreAlpha = 0.62,
            shoreWidth = 2.05,
            /** Hand-pressure wobble: ± this fraction of the width, keyed to the world cell (chunk-stable). */
            wobble = 0.35,
            /** Watercolour edge-pooling: a soft pigment gradient gathering along a cap's cut edges (inside the ink
             *  line) — the wash signature that makes wall caps and terrace steps read as CUT PAPER at any zoom.
             *
             *  These two were authored as a hairline: 0.11 alpha spread over 5.8 world px against the pale paper caps
             *  peaks under 3/255, i.e. under the 8-bit legibility floor the laid-paper study established for this
             *  sheet. The pool is the signature that makes a cap read as cut paper, so it is authored to actually
             *  arrive — still a wash INSIDE the contour, never a second contour, and still bounded well below the
             *  terrace line's own alpha.
             *
             *  Alpha was never the lever: doubling it took the measured peak from 2 to 4/255 because the wash COLOUR
             *  sat halfway between the cap and its own edge pigment. The colour carries it now (see `addWashEdge`'s
             *  caller); this dial stays where the hierarchy allows it. And the role weight (rock reads heavier than
             *  floor) no longer multiplies the WIDTH as well, which used to deliver 5.6 px of this 9. */
            washEdgeAlpha = 0.26,
            washEdgeWidth = 9,
            /** The FOOT of a riser: soft pigment gathering on the LOWER ground where a wall or terrace face lands.
             *
             *  A cut edge has two events, not one. The crest gets the ink line above; the foot is where the wash a
             *  watercolourist floods down a riser actually pools, and without it a terrace reads as a wash floating
             *  over the ground instead of a piece of paper standing on it. It is pigment (a darkened lower-cap tone
             *  fading outward to nothing), NOT a contour and NOT a halo: no line, no brightness, and it may never
             *  approach the crest line's alpha, or the step acquires two silhouettes.
             *
             *  Width is not a substitute for contrast — but it is a PRECONDITION. The pigment is chosen by the shared
             *  transmission law (`terrainGeometryCompilerInk.ts` terrainInkWashPigment),
             *  which fixes the delivered drop; the width then decides whether the band is resolvable at the zoom the
             *  player actually plays at. 4.5 world px measured 1.7 SCREEN px on an ordinary wheel-out framing, which
             *  is a hairline however dark it is. Both were wrong, and both are fixed — never one without the other. */
            footAlpha = 0.45,
            footWidth = 12,
            /** The ACTOR twin of washEdgeAlpha: the same watercolour edge pool, on bodies.
             *
             *  Terrain pools along a BAKED cut edge (an alpha over a pigment gradient `washEdgeWidth` px wide);
             *  a body has no cut edge, so its pool is masked by the grazing angle in the SHADING domain instead
             *  — `mix(rgb, rgb × shadowInk.tint, pow(1 − facing, 2.4) · this)`. Two domains, therefore two
             *  numbers, but ONE block and one idea: they are siblings so the sheet's edge behaviour is tuned
             *  together and can never drift into two languages.
             *
             *  What it replaces matters more than its size: actors used to receive `rgb += rgb · rim · 0.035`,
             *  a CG rim LIGHT — the exact opposite of pigment, which GATHERS (darker, cooler) where a wash runs
             *  off a form. Bounded to the same optical budget as the term it replaces: the tint's luminance ratio
             *  is 0.9245, so the silhouette-most pixel loses 0.55 × 7.55 % ≈ 4.2 % of its luminance and nothing
             *  else in the frame moves. It is a wash INSIDE the contour, never a second contour. */
            washEdgeActorAlpha = 0.55,
        },

        /**
         * TRANSMISSION per washed terrain mark — the half of the authoring that is NOT a dial above.
         *
         * Mirrored from `terrainGeometryCompilerInk.ts` TERRAIN_GEOMETRY_INK.wash
         * (the bake runs in a worker that may not import this module). Alpha decides how much pigment is laid;
         * transmission decides how dark that pigment is, and the two together are the only thing that decides
         * whether a mark exists in the frame at all — see `terrainInkWashPigment` for the measured reason a
         * palette-authored wash delivers nothing.
         */
        washTransmission = new HandInkLook.WashTransmissionDials
        {
            shore = 0.16,
            foot = 0.4,
            edge = 0.42,
        },

        /** Screen-space paper finish (PostFx). */
        post = new HandInkLook.PostDials
        {
            /** Multiply alpha of the procedural paper sheet over the finished frame. */
            paperAlpha = 0.041,
            /** Vignette pigment — warm-black drawn ink, never pure #000. */
            vignetteTint = 0x141018,
            /** Maximum corner pull toward the vignette pigment. Deepened with the focal-frame composition so
             *  the frame edge falls into cool ink while the play focus carries the light — still bounded well
             *  below anything that could hide an enemy or telegraph at the view rim. */
            vignetteAlpha = 0.06,
            /** Normalized radial distance where the edge pigment begins gathering. */
            vignetteStart = 0.52,
            /** Hue-safe print curve: restrained black pigment toe, highlight shoulder and bright-gamut compression. */
            filmToe = 0.025,
            filmShoulder = 0.045,
            filmGamutCompression = 0.035,
        },
    };

    /// <summary>
    /// Apply a staged art-direction preset to the authored dials. Role FAMILIES (outline widths, contour
    /// pigment) are scaled rather than replaced, so a preset changes a family's weight and can never invert
    /// the authored hierarchy inside it — the character &gt; npc &gt; worldObject silhouette order and the
    /// shore &gt; terrace contour order are preserved by construction, not by a preset author remembering to.
    /// </summary>
    public static HandInkLook resolveInkLook(LookPresetDials? dials, HandInkLook? @base = null)
    {
        @base ??= HANDINK_DIALS;
        if (dials == null) return @base;
        double ink(double alpha) => Math.max(0, Math.min(1, alpha * dials.contourAlphaScale));
        double width(double px) => px * dials.contourWidthScale;
        double hull(double units) => units * dials.outlineWidthScale;
        HandInkLook resolved = @base.Clone();
        HandInkLook.HatchDials hatch = @base.hatch.Clone();
        hatch.strength = dials.hatchStrength;
        hatch.periodPx = dials.hatchPeriodPx;
        resolved.hatch = hatch;
        HandInkLook.WashDials wash = @base.wash.Clone();
        wash.bands = dials.washBands;
        wash.strength = dials.washStrength;
        wash.lowestTone = dials.washLowestTone;
        wash.edgeSoftness = dials.washEdgeSoftness;
        resolved.wash = wash;
        HandInkLook.ShadowInkDials shadowInk = @base.shadowInk.Clone();
        shadowInk.strength = dials.shadowInkStrength;
        resolved.shadowInk = shadowInk;
        resolved.materialLine = new HandInkLook.MaterialLineDials
        {
            floorAlpha = ink(@base.materialLine.floorAlpha),
            rockCapAlpha = ink(@base.materialLine.rockCapAlpha),
            geologicalFaceAlpha = ink(@base.materialLine.geologicalFaceAlpha),
            washStrength = ink(@base.materialLine.washStrength),
        };
        resolved.volumeOutline = new HandInkLook.VolumeOutlineDials
        {
            character = hull(@base.volumeOutline.character),
            npc = hull(@base.volumeOutline.npc),
            worldObject = hull(@base.volumeOutline.worldObject),
            interactive = hull(@base.volumeOutline.interactive),
            // Scaled with the widths it is derived from: a preset that doubles the nib doubles how far a
            // hull can break out of its neighbour, so the relief that re-buries it has to follow.
            surfaceRelief = hull(@base.volumeOutline.surfaceRelief),
        };
        HandInkLook.ContourDials contour = @base.contour.Clone();
        contour.wallAlpha = ink(@base.contour.wallAlpha);
        contour.wallWidth = width(@base.contour.wallWidth);
        contour.terraceAlpha = ink(@base.contour.terraceAlpha);
        contour.terraceWidth = width(@base.contour.terraceWidth);
        contour.shoreAlpha = ink(@base.contour.shoreAlpha);
        contour.shoreWidth = width(@base.contour.shoreWidth);
        contour.footAlpha = ink(@base.contour.footAlpha);
        contour.footWidth = width(@base.contour.footWidth);
        contour.washEdgeAlpha = ink(@base.contour.washEdgeAlpha);
        contour.washEdgeActorAlpha = ink(@base.contour.washEdgeActorAlpha);
        resolved.contour = contour;
        HandInkLook.PostDials post = @base.post.Clone();
        post.paperAlpha = dials.paperAlpha;
        post.vignetteAlpha = dials.vignetteAlpha;
        resolved.post = post;
        return resolved;
    }

    /// <summary>The dial set this session renders with — the authored HANDINK baseline unless a preset is staged.</summary>
    public static readonly HandInkLook INK_LOOK = resolveInkLook(LOOK_PRESET.dials);

    /// <summary>The ink pigment used by baked world contours — the shared near-black of the whole identity.</summary>
    public const int INK_WORLD_LINE = Theme.WORLD_INK.ink;

    public static InkLookUniforms createInkLookUniforms()
    {
        double on = INK_LOOK.enabled ? 1 : 0;
        return new InkLookUniforms
        {
            inkA = new ThreeUniform<ThreeVector4>(
                new ThreeVector4(
                    INK_LOOK.hatch.strength * on,
                    INK_LOOK.hatch.periodPx,
                    INK_LOOK.wash.bands * on,
                    INK_LOOK.wash.strength * on)),
            inkB = new ThreeUniform<ThreeVector4>(
                new ThreeVector4(
                    INK_LOOK.paperFibre.terrainStrength * on,
                    INK_LOOK.shadowInk.strength * on,
                    INK_LOOK.hatch.shadeHi,
                    INK_LOOK.hatch.shadeLo)),
            inkStructure = new ThreeUniform<ThreeVector4>(
                new ThreeVector4(
                    INK_LOOK.materialLine.floorAlpha * on,
                    INK_LOOK.materialLine.rockCapAlpha * on,
                    INK_LOOK.materialLine.geologicalFaceAlpha * on,
                    INK_LOOK.materialLine.washStrength * on)),
            inkTint = new ThreeUniform<ThreeVector3>(
                new ThreeVector3(
                    INK_LOOK.shadowInk.tint.r,
                    INK_LOOK.shadowInk.tint.g,
                    INK_LOOK.shadowInk.tint.b)),
            inkLine = new ThreeUniform<ThreeColor>(new ThreeColor(INK_WORLD_LINE)),
        };
    }
}
