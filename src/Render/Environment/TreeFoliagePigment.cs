// Port of packages/client/src/render/environment/treeFoliagePigment.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerTheme;
using Math = Fluitown.Runtime.JsMath;
using PropTileset = Fluitown.Render.TerrainMaterialTileset;

namespace Fluitown.Render;

/* ══ Pigment — the per-run colour dial ═════════════════════════════════════════════════════════════════
 * ONE table. A run is recoloured here and nowhere else. Every value is expressed against the theme's own
 * tileset channels, so foliage can never drift away from the world it stands in. */

public sealed class TreeFoliageRecipe
{
    /// <summary>Tileset channel the crown starts from (a TilesetPigment).</summary>
    public string @base = "";
    /// <summary>0..1 pull toward the theme's signature accent — the run's identity colour.</summary>
    public double accent;
    /// <summary>0..1 pull toward the pale peak tint; higher reads chalkier and lighter.</summary>
    public double paper;
    /// <summary>0..1 pull toward the floor tone, which is what keeps a grove sitting IN its world's light.</summary>
    public double ground;
    /// <summary>
    /// How deep the leaf's OWN shade goes — the depth handed to <see cref="TreeFoliagePigment.foliageShadeColorFor"/>, never a pull
    /// toward ink. See that function for why the old ink pull was the second half of the black-canopy defect
    /// and why these numbers are now roughly half of what they were.
    /// </summary>
    public double shade;
    /// <summary>How far `canopyHue` may move one specimen, so a grove is never one flat colour.</summary>
    public double spread;
    /// <summary>Optional authored spectrum a world insists on (the carnival's icing, the prism realm's bands).</summary>
    public IReadOnlyList<int>? palette;
    /// <summary>Blossom / fruit / lantern pigment for high-`bloom` specimens (a TilesetPigment).</summary>
    public string bloom = "";
    /// <summary>Optional bounded bloom palette. The specimen hash selects a member; leaves remain on the base recipe.</summary>
    public IReadOnlyList<string>? bloomPalette;
    /// <summary>Pull toward the world's paper pole after selecting the bloom pigment. Defaults to 0.2.</summary>
    public double? bloomPaper;
}

/// <summary>
/// How a leaf takes light — the ONE pigment truth every plant in the game is coloured by.
///
/// Split out of `treeGeometry` because form and colour are different jobs with different reasons to change:
/// that module grows a skeleton and emits geometry, this one answers "what colour is this leaf, here, in this
/// world, at this point on its crown's value ramp". Canopies, shrubs, moss, vines, wreaths and shrine
/// offerings all come through here, which is what stops a run reading as two different ecologies — and what
/// makes recolouring a world one row of TREE_FOLIAGE rather than a renderer branch.
///
/// Three rules, and no fourth path anywhere:
///   - <see cref="foliageColorFor"/>      the world's leaf, on its own hue spread
///   - <see cref="foliageLitColorFor"/>   that leaf where the light reaches it: brighter, no less saturated
///   - <see cref="foliageShadeColorFor"/> and where it does not: darker, MORE saturated
/// </summary>
public static partial class TreeFoliagePigment
{
    /// <summary>Keyed by TreeVisualProfileId; never iterated.</summary>
    private static readonly Dictionary<string, TreeFoliageRecipe> TREE_FOLIAGE = new()
    {
        ["natural"] = new TreeFoliageRecipe
        {
            @base = "decalMid",
            accent = 0.26,
            paper = 0.05,
            ground = 0.1,
            shade = 0.1,
            spread = 0.2,
            bloom = "decalAccent",
        },
        ["pine"] = new TreeFoliageRecipe
        {
            @base = "decalMid",
            accent = 0.2,
            paper = 0.02,
            ground = 0.12,
            shade = 0.12,
            spread = 0.16,
            bloom = "decalAccent",
        },
        ["cypress"] = new TreeFoliageRecipe
        {
            @base = "decalMid",
            accent = 0.24,
            paper = 0.12,
            ground = 0.08,
            shade = 0.12,
            spread = 0.14,
            bloom = "peakTint",
        },
        ["sakura"] = new TreeFoliageRecipe
        {
            // A living green crown carries explicit flowers; the old pink crown made every leaf read as blossom.
            @base = "decalMid",
            accent = 0.2,
            paper = 0.04,
            ground = 0.1,
            shade = 0.09,
            spread = 0.18,
            bloom = "peakTint",
            bloomPalette = new[] { "peakTint", "decalInk" },
            bloomPaper = 0,
        },
        ["abyssal"] = new TreeFoliageRecipe
        {
            @base = "floodDeep",
            accent = 0.46,
            paper = 0.04,
            ground = 0.04,
            shade = 0.11,
            spread = 0.3,
            bloom = "floodGlint",
        },
        ["prismatic"] = new TreeFoliageRecipe
        {
            @base = "floodGlint",
            accent = 0.42,
            paper = 0.2,
            ground = 0.04,
            shade = 0.1,
            spread = 0.28,
            bloom = "decalAccent",
        },
        ["rainbow"] = new TreeFoliageRecipe
        {
            @base = "decalAccent",
            accent = 0.3,
            paper = 0.1,
            ground = 0.03,
            shade = 0.09,
            spread = 0.4,
            palette = TERRAIN_GEOMETRY_RAINBOW_COLORS,
            bloom = "floodGlint",
        },
        ["clockwork"] = new TreeFoliageRecipe
        {
            @base = "railLit",
            accent = 0.34,
            paper = 0.06,
            ground = 0.1,
            shade = 0.13,
            spread = 0.22,
            bloom = "decalAccent",
        },
        ["carnival"] = new TreeFoliageRecipe
        {
            @base = "decalAccent",
            accent = 0.28,
            paper = 0.24,
            ground = 0.02,
            shade = 0.09,
            spread = 0.34,
            palette = TERRAIN_GEOMETRY_CARNIVAL_COLORS,
            bloom = "peakTint",
        },
        ["cathedral"] = new TreeFoliageRecipe
        {
            @base = "peakTint",
            accent = 0.36,
            paper = 0.14,
            ground = 0.06,
            shade = 0.11,
            spread = 0.24,
            bloom = "decalAccent",
        },
        ["city"] = new TreeFoliageRecipe
        {
            @base = "decalMid",
            accent = 0.18,
            paper = 0.02,
            ground = 0.14,
            shade = 0.14,
            spread = 0.14,
            bloom = "decalAccent",
        },
        ["paradise"] = new TreeFoliageRecipe
        {
            @base = "decalMid",
            accent = 0.32,
            paper = 0.08,
            ground = 0.08,
            shade = 0.1,
            spread = 0.24,
            bloom = "decalAccent",
        },
    };

    /// <remarks>
    /// PORT: the TS switch has no default and returns `undefined` for a channel outside the union (only reachable
    /// through <see cref="foliageBloomChannelFor"/>'s NaN index). Every consumer reads the result through
    /// `mix`, whose bitwise ops turn `undefined` into 0 — so 0 is the exact equivalent here.
    /// </remarks>
    public static int tilesetPigment(PropTileset tileset, string? channel)
    {
        switch (channel)
        {
            case "decalMid":
                return tileset.decal.mid;
            case "decalAccent":
                return tileset.decal.accent;
            case "decalInk":
                return tileset.decal.ink;
            case "floorLit":
                return tileset.terrain.floorLit;
            case "peakTint":
                return tileset.peakTint;
            case "floodDeep":
                return tileset.flood.deep;
            case "floodGlint":
                return tileset.flood.glint;
            case "railLit":
                return tileset.bridge.railLit;
        }
        return 0;
    }

    public static TreeFoliageRecipe foliageRecipe(string profile)
    {
        return TREE_FOLIAGE.TryGetValue(profile, out TreeFoliageRecipe? recipe) ? recipe : TREE_FOLIAGE["natural"];
    }

    /// <summary>
    /// The world's leaf colour at one point on its own hue spread.
    ///
    /// Trees and bushes both come through here, which is the whole point: a run's undergrowth belongs to the same
    /// plant as its canopy, and recolouring a run means editing one row of TREE_FOLIAGE.
    /// </summary>
    public static int foliageColorFor(
        PropTileset tileset,
        string profile,
        double hue,
        double age = 0)
    {
        TreeFoliageRecipe recipe = foliageRecipe(profile);
        IReadOnlyList<int>? authored = recipe.palette;
        int @base;
        if (authored != null)
        {
            // `authored[Math.floor(hue * n) % n]!` — an index outside the list (negative / NaN hue) is `undefined`,
            // which `mix` reads as 0.
            double index = Math.floor(hue * authored.Count) % authored.Count;
            @base = index >= 0 && index < authored.Count ? authored[(int)index] : 0;
        }
        else
        {
            @base = tilesetPigment(tileset, recipe.@base);
        }
        int grounded = mix(@base, tileset.terrain.floorTone, recipe.ground);
        int accented = mix(grounded, tileset.decal.accent, recipe.accent * (0.62 + hue * 0.6));
        return mix(accented, tileset.peakTint, recipe.paper * (0.4 + hue * 0.7) + age * 0.04);
    }

    /// <summary>How far a world's foliage darkens on its shaded faces — a saturated shade, never near-black.</summary>
    public static double foliageShadeFor(string profile)
    {
        return foliageRecipe(profile).shade;
    }

    /* ── The leaf's own shade ───────────────────────────────────────────────────────────────────────────────
     * The second half of the black-canopy defect, and the half that survived the lighting fix.
     *
     * Every shaded leaf face in the game was `mix(leaf, tileset.decal.ink, shade)` with `shade` 0.18–0.32 and
     * `decal.ink` = #050505. The recipe's own comment demanded "a saturated block plus its DARKER SATURATED
     * shade — never block plus near-black", and the code did precisely the thing the comment forbids: it lerped
     * a third of the way to black. Measured on the Hub pine, the leaf's albedo luminance is 0.610 and its shaded
     * side came out 0.283 — the side band lost 54 % of its pigment BEFORE a single light touched it, which is
     * why a band already receiving 69 % of a lit cap's irradiance still rendered at 42/255.
     *
     * Lerping toward black is also the one operation that destroys chroma: it drags all three channels to the
     * same place, so a crushed leaf reads as charcoal rather than as a leaf in shadow. Washed ink does the
     * opposite — pigment pools DARKER AND MORE SATURATED where the wash sits.
     *
     * foliageShadeColorFor therefore deepens the leaf against ITSELF: the dominant channel keeps most of
     * its value (FOLIAGE_SHADE_VALUE_BITE) and the weak channels give up the rest
     * (FOLIAGE_SHADE_CHROMA), so the shade is the same leaf, lower in value and higher in chroma, in
     * every biome and for every palette — no per-world shade table, and no way to author a near-black canopy. */

    /// <summary>Share of `depth` the DOMINANT channel gives up. Low on purpose: value separation is the light's job.</summary>
    private const double FOLIAGE_SHADE_VALUE_BITE = 0.5;
    /// <summary>Extra bite the weak channels take, proportional to how far below the dominant channel they sit.</summary>
    private const double FOLIAGE_SHADE_CHROMA = 1.6;

    /// <summary>
    /// The world's leaf pigment where the light does not reach it: darker, MORE saturated, same leaf.
    ///
    /// One implementation for every consumer — canopies, bushes, moss, vines, wreaths and blossom all shade the
    /// same way, because "how a leaf shades" is one rule and it lives here.
    /// </summary>
    public static int foliageShadeColorFor(int leaf, double depth)
    {
        double bite = Math.min(1, Math.max(0, depth));
        if (bite <= 0) return leaf;
        int r = (leaf >> 16) & 0xff;
        int g = (leaf >> 8) & 0xff;
        int b = leaf & 0xff;
        double peak = Math.max(1, r, g, b);
        double deepen(double channel)
        {
            double weakness = 1 - channel / peak;
            double factor = 1 - bite * (FOLIAGE_SHADE_VALUE_BITE + FOLIAGE_SHADE_CHROMA * weakness);
            return Math.max(0, Math.min(255, Math.round(channel * Math.max(0, factor))));
        }
        return (Js.ToInt32(deepen(r)) << 16) | (Js.ToInt32(deepen(g)) << 8) | Js.ToInt32(deepen(b));
    }

    /// <summary>The shaded side of one world's foliage — the ONE place a shaded leaf colour is derived.</summary>
    public static int foliageSideColorFor(string profile, int leaf)
    {
        return foliageShadeColorFor(leaf, foliageRecipe(profile).shade);
    }

    /* ── The leaf where the light DOES reach it ─────────────────────────────────────────────────────────────
     * The mirror image of the defect above, and the reason lit foliage read as cauliflower.
     *
     * Every brighter leaf tone in this file was `mix(leaf, tileset.peakTint, …)`, and `peakTint` is a pale, nearly
     * neutral chalk. Lerping toward it does to chroma exactly what lerping toward ink does: it drags the three
     * channels together. A bush cap at a 0.24 pull came out near-neutral cream — brighter than the grass it stood
     * on, with the green gone — so on green ground the shrub lost its figure-ground read entirely.
     *
     * A sunlit leaf is not a paler leaf. It is a brighter, MORE saturated one: the dominant channel takes most of
     * the gain and the weak channels take least, which is the same asymmetry foliageShadeColorFor applies
     * on the way down. One rule up, one rule down, and no path through this module can produce a grey leaf. */

    /// <summary>Share of `lift` the WEAKEST channel takes. Deliberately small: value alone would wash the hue out.</summary>
    private const double FOLIAGE_LIT_VALUE_GAIN = 0.34;
    /// <summary>Extra gain in proportion to a channel's dominance — this is what keeps a lit leaf a brighter GREEN.</summary>
    private const double FOLIAGE_LIT_CHROMA = 0.72;

    /// <summary>
    /// The world's leaf pigment where the light reaches it: brighter, no less saturated, the same leaf.
    ///
    /// Clipping is handled by scaling the whole triple rather than per channel, so an already-bright pigment
    /// saturates in VALUE without its hue collapsing toward white on the way.
    /// </summary>
    public static int foliageLitColorFor(int leaf, double lift)
    {
        double gain = Math.max(0, lift);
        if (gain <= 0) return leaf;
        int r = (leaf >> 16) & 0xff;
        int g = (leaf >> 8) & 0xff;
        int b = leaf & 0xff;
        double peak = Math.max(1, r, g, b);
        double raise(double channel) =>
            channel * (1 + gain * (FOLIAGE_LIT_VALUE_GAIN + FOLIAGE_LIT_CHROMA * (channel / peak)));
        double lr = raise(r);
        double lg = raise(g);
        double lb = raise(b);
        double top = Math.max(lr, lg, lb);
        double scale = top > 255 ? 255 / top : 1;
        double clamp(double v) => Math.max(0, Math.min(255, Math.round(v * scale)));
        return (Js.ToInt32(clamp(lr)) << 16) | (Js.ToInt32(clamp(lg)) << 8) | Js.ToInt32(clamp(lb));
    }

    /* ── The crown's own sun side ───────────────────────────────────────────────────────────────────────────
     * The last thing missing from a canopy after the lighting fix, and the one a normal cannot give it.
     *
     * Every band of every clump already carries its own azimuth (`cos(mid)`, `sin(mid)`), so each lobe has a lit
     * facet and a shaded facet — a ~1.5× spread across one lobe. But a clump is a body of revolution: wherever it
     * hangs in the crown it shows the SAME span of azimuths, so averaging the west half of a canopy against its
     * east half comes out flat, which is precisely what the frame measured. What separates the two halves of a
     * real crown is the lobes on the sun side shading the ones behind them — occlusion, not orientation — and
     * baked dressing never enters a shadow pass (`castShadow: false`, by design).
     *
     * So the crown's sun side is authored, as pigment, on two axes that are free at bake time: how far along the
     * key's azimuth a mass sits, and how high it sits in the crown. Together they are the value ramp that turns a
     * field of green discs into a volume. */

    /// <summary>Signed swing between the sun side of a crown and its far side. The crown's biggest single value step.</summary>
    private const double FOLIAGE_SUN_SIDE = 0.36;
    /// <summary>Swing from the crown's foot to its apex: the second axis of the same ramp.</summary>
    private const double FOLIAGE_CROWN_RISE = 0.18;
    /// <summary>Where in the crown the rise ramp crosses zero — a shade below, a lift above. Slightly under centre, so
    ///  the underside of a canopy is the part that darkens rather than the whole mass.</summary>
    private const double FOLIAGE_CROWN_RISE_PIVOT = 0.45;
    /// <summary>Head start a terminal mass has over the enrichment sitting behind it — the silhouette catches the sky.</summary>
    private const double FOLIAGE_TERMINAL_LIFT = 0.09;
    /// <summary>
    /// How far enrichment sinks behind the silhouette.
    ///
    /// Bounded hard, and this is why: the interstices between lobes are the only foliage the eye reads as a HOLE,
    /// and at the previous 0.34–0.5 they sank to ink level while the mass around them was lifted — hard black
    /// tears punched into a green field, which is area-fill ink and the one thing the style forbids.
    /// </summary>
    private const double FOLIAGE_ENRICHMENT_SINK = 0.08;
    /// <summary>How far the specimen's own hue spread may move ONE lobe — a grove is never one flat colour.</summary>
    private const double FOLIAGE_SPECIMEN_DRIFT = 0.28;
    /// <summary>
    /// The hard floor on the ramp: no lobe may sink past this, however many terms stack against it.
    ///
    /// Every axis above is bounded on its own, but they compound — an enrichment lobe at the bottom of the far
    /// side of a crown collected all four at once, and under the deepest lighting band that is the pixel the
    /// frame reads as a black tear between two lit lumps. Ink is a CONTOUR; a black wedge inside a crown is a
    /// fill, and the style forbids it. With this floor the deepest baked foliage face sits at ~0.55× the crown's
    /// brightest cap, which is a shade — visibly a shade — and never a hole.
    /// </summary>
    private const double FOLIAGE_VALUE_FLOOR = -0.26;
    /// <summary>
    /// …and the matching ceiling, which is the CAP dial.
    ///
    /// A clump's crest is the one face the rig lights fully, so whatever pigment it wears lands at the top of the
    /// crown's histogram. Let the ramp run free and every sun-side crest resolves to the lit ground's own value —
    /// the pale disc pasted on a leaf mass that the frame kept catching. The ramp is therefore asymmetric on
    /// purpose: it lifts a little and sinks a lot, which is also what a real canopy does (the sun side sits at the
    /// leaf's own albedo; it is the far side that falls away).
    /// </summary>
    private const double FOLIAGE_VALUE_CEILING = 0.16;

    /// <summary>
    /// Where one lobe sits on its cluster's value ramp: negative is a shade, positive is a lit leaf.
    ///
    /// The ONE rule, shared by canopies and undergrowth — a bush is the same plant as the tree above it, so it
    /// cannot have its own idea of how a leaf mass turns from its sun side to its far side.
    /// </summary>
    /// <param name="terminal">owns the cluster's silhouette (a bough tip, a shrub's outer crown) rather than sitting behind it</param>
    /// <param name="facing">propSunFacing across the cluster: −1 the far side, +1 the sun side</param>
    /// <param name="rise">0 at the cluster's foot, 1 at its apex</param>
    /// <param name="drift">the specimen's own signed hue/value jitter, ±0.5 scaled by its world's spread</param>
    public static double foliageRampValue(
        bool terminal,
        double facing,
        double rise,
        double drift)
    {
        return Math.max(
            FOLIAGE_VALUE_FLOOR,
            Math.min(
                FOLIAGE_VALUE_CEILING,
                (terminal ? FOLIAGE_TERMINAL_LIFT : -FOLIAGE_ENRICHMENT_SINK) +
                    FOLIAGE_CROWN_RISE * (Math.max(0, Math.min(1, rise)) - FOLIAGE_CROWN_RISE_PIVOT) +
                    FOLIAGE_SUN_SIDE * Math.max(-1, Math.min(1, facing)) +
                    drift * FOLIAGE_SPECIMEN_DRIFT));
    }

    /// <summary>Resolve a ramp value against one world's leaf: the lit rule up, the shade rule down, no third path.</summary>
    public static int foliageRampColorFor(int leaf, double value)
    {
        return value >= 0 ? foliageLitColorFor(leaf, value) : foliageShadeColorFor(leaf, -value);
    }

    /// <summary>The world's blossom / fruit / lantern pigment.</summary>
    /// <remarks>Returns null where the TS indexes past the palette (NaN variant) and yields `undefined`.</remarks>
    private static string? foliageBloomChannelFor(TreeFoliageRecipe recipe, double variant)
    {
        IReadOnlyList<string>? palette = recipe.bloomPalette;
        if (palette != null && palette.Count > 0)
        {
            double index = Math.min(
                palette.Count - 1,
                Math.floor(Math.max(0, Math.min(0.999999, variant)) * palette.Count));
            return index >= 0 && index < palette.Count ? palette[(int)index] : null;
        }
        return recipe.bloom;
    }

    public static int foliageBloomColorFor(
        PropTileset tileset,
        string profile,
        double variant = 0)
    {
        TreeFoliageRecipe recipe = foliageRecipe(profile);
        string? channel = foliageBloomChannelFor(recipe, variant);
        return mix(tilesetPigment(tileset, channel), tileset.peakTint, recipe.bloomPaper ?? 0.2);
    }
}
