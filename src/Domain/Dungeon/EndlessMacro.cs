// Port of packages/shared/src/domain/dungeon/endlessMacro.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Endless **macro-landform** layer — the global fields that give the streamed endless world its large-scale
// shape, so it stops reading as a uniform chunk-grid (a "checkerboard") and gains organic macro-structure:
// open basins & arenas, rock ranges & canyon walls, broad plateaus and sunken valleys, and unique landmark
// loci — all spanning many chunks.
//
// ── Why this exists ────────────────────────────────────────────────────────────────────────────────
// Each endless chunk is generated independently from `(seed, cx, cy)`; on its own it is a self-contained
// 32×32 braided-maze box, and the world is a tiling of those boxes. Nothing spanned a chunk seam, so the
// minimap showed a regular waffle. This module adds ONE thing the chunk generator READS: pure global fields
// `(seed, globalTile)` — exactly like the elevation field (elevationHeightAt) and the river field
// (riverPenetrationAt) already are. Because the fields are pure functions of the global tile, the server
// (collision) and every client (render/minimap) sample byte-identical values with nothing to stream, and two
// chunks meeting at a seam see the SAME landform continue across the border — the feature is one big shape,
// not a per-chunk box.
//
// The braided labyrinth stays the connective BACKBONE; these fields only *modulate* it (owner's call). Two
// concerns are kept strictly apart so the terrain contracts hold:
//  - **Walkable ground height** (endlessGroundLevelAt) uses ONLY smooth, large-cell value-noise octaves, so it
//    upholds the ≤1-step neighbour-smoothness invariant the seam contract depends on (proven in
//    `endlessMacro.test.ts`). No Worley locus term touches walkable height (its cell-border crease could violate
//    ≤1-step).
//  - **Tile carving & wall summits** (endlessMacroSample, endlessRockSummitAt) MAY use the punctuated Worley
//    loci — carving is tile edits (no height contract) and wall summits are visual volume (no ≤1-step
//    contract). This is where the strong, unique landmark character comes from.
//
// ── No presets, unbounded unique loci ──────────────────────────────────────────────────────────────
// Distinctive regions are NOT a fixed library stamped on a few anchors. A warped **Worley/Voronoi partition**
// (`macroLocus`) tiles the infinite plane into unique cells; each cell hashes to a *style* (open basin / rock
// massif / broken field / rolling) plus continuous shape params (intensity, radius), and its feature fades out
// toward the cell border. So every locus is one-of-a-kind, none repeats, and there are effectively unlimited of
// them — the procedural substitute for hand-placed landmarks.
//
// Type notes: `MacroFamily` ('open' | 'rock' | 'mixed') and `MacroStyle` ('labyrinth' or one of the ≥20
// procedural landform ANCHOR archetypes below) are string literal unions and therefore plain `string`s here.
// `labyrinth` is the dominant residual — pure maze, no anchor. Each archetype has a bespoke signed-field shape
// driven by per-locus continuous params (size/aspect/rotation/roughness/count), so no two loci look alike and
// there are effectively unlimited unique landmarks — the procedural, preset-free substitute for hand-placed
// landforms.

/// <summary>
/// Per-theme **wall silhouette** — the shape language of a run's skyline, as pure data. Walls are visual volume
/// (no ≤1-step contract), so every knob here changes ONLY how high non-walkable masses tower and how their
/// heights distribute; carve/walkable geometry is untouched. Every field is optional; omitted fields keep the
/// shared ragged default, so a theme without a silhouette entry still receives the complete multi-scale profile
/// without needing bespoke data.
/// </summary>
public sealed class WallSilhouette
{
    /// <summary>
    /// Broad regional octave in tiles (default 112 ≈ 3.5 chunks). This gives the skyline a long read: a run crosses
    /// whole low and high countries instead of independently shuffling every chunk.
    /// </summary>
    public double? rangeCell;
    /// <summary>Weight of the broad regional octave (default 0.85).</summary>
    public double? rangeAmp;
    /// <summary>
    /// Belt octave cell in tiles (default 34 ≈ 1 chunk) — the breadth of taller/shorter wall country. Small ⇒
    /// block-scale districts (city); large ⇒ sweeping ranges.
    /// </summary>
    public double? beltCell;
    /// <summary>
    /// Jitter octave cell in tiles (default 9) — how fine adjacent wall masses differ. Small ⇒ needling machinery
    /// notches; large ⇒ soft rounded lumps.
    /// </summary>
    public double? jagCell;
    /// <summary>Belt octave amplitude (default 1.15).</summary>
    public double? beltAmp;
    /// <summary>Jitter octave amplitude (default 0.7).</summary>
    public double? jagAmp;
    /// <summary>
    /// Contrast around the centered three-octave signal (default 2.25). Higher spends more of the available height
    /// range; lower keeps a restrained middle skyline without clipping either tail.
    /// </summary>
    public double? contrast;
    /// <summary>
    /// Small authored shift after normalization (-0.35..0.35). Unlike the former rock-bias injection this is
    /// deliberately bounded and cannot flatten a rock-rich theme against the ceiling.
    /// </summary>
    public double? bias;
    /// <summary>
    /// Exponent on the combined relief (default 1). &gt;1 starves the mid-band into sparse SPIRES rising over low
    /// walls (cathedral needles); &lt;1 fattens the mid-band into broad MESAS (reef masses, candy lumps).
    /// </summary>
    public double? spike;
    /// <summary>
    /// Quantize the relief into exactly N plateaus (≥2) — stepped skylines: tower blocks, brass shelves, marble
    /// tiers. Omit/0 ⇒ continuous ragged heights.
    /// </summary>
    public double? terraceSteps;
    /// <summary>
    /// Absolute stored-elevation spacing between neighbouring geological wall shelves. Unlike `terraceSteps`, this
    /// quantizes the final world-space summit rather than the normalized noise signal, so a one-level change in
    /// nearby walkable ground cannot turn a massif into a stack of paper-thin contour rings.
    /// </summary>
    public double? terraceBand;
    /// <summary>
    /// Multiplier on the relief rise toward the ceiling (default 1). Lower ⇒ a deliberately LOW wall country
    /// (garden walls, tent streets); 1 ⇒ peaks reach the full ceiling.
    /// </summary>
    public double? riseScale;
}

/// <summary>
/// The anonymous `{ open, rock, mixed }` shape of <see cref="MacroDna.styleWeights"/>: probability weights that a
/// locus draws each anchor FAMILY; the remainder stays `labyrinth` (dominant).
/// </summary>
public sealed class MacroStyleWeights
{
    public double open;
    public double rock;
    public double mixed;
}

/// <summary>
/// Per-run **World-DNA**: the structural signature that makes each of the ten runs its own place. Pure data,
/// resolved by <see cref="EndlessMacro.endlessMacroDnaFor"/> from the run's theme key (the descriptor `biomeKey`).
/// </summary>
public sealed class MacroDna
{
    /// <summary>Baseline openness pushed onto the whole run (−0.5..0.5). Higher ⇒ more/larger open ground overall.</summary>
    public double opennessBias;
    /// <summary>Baseline rock pushed onto the whole run (−0.5..0.5). Higher ⇒ more/larger solid massifs &amp; ranges.</summary>
    public double rockBias;
    /// <summary>
    /// Signed height amplitude of the macro relief octave added to walkable ground (in [0,1] height units; ~0.08
    /// height ≈ 1 elevation level). Kept small-gradient (large cell) to preserve ≤1-step smoothness.
    /// </summary>
    public double reliefAmp;
    /// <summary>Macro feature scale multiplier (1 ≈ ~7-chunk loci; &lt;1 tighter clusters, &gt;1 broader landforms).</summary>
    public double featureScale;
    /// <summary>
    /// Lattice pitch `P` (tiles) for the global node-graph maze (endlessMaze) — this run's fundamental junction
    /// spacing (~5.5 dense … ~8 coarse). Constant over the run, so the lattice never resets and the seam halo math
    /// stays trivial.
    /// </summary>
    public double latticePitch;
    /// <summary>
    /// Probability weights that a locus draws each anchor FAMILY; the remainder stays `labyrinth` (dominant).
    /// `open` ⇒ carved plazas/basins/gorges, `rock` ⇒ raised massifs/forts/ridges, `mixed` ⇒ both.
    /// </summary>
    public MacroStyleWeights styleWeights;
    /// <summary>Optional per-archetype pick weight (`Partial&lt;Record&lt;Exclude&lt;MacroStyle, 'labyrinth'&gt;, number&gt;&gt;`); lookup only.</summary>
    public Dictionary<string, double>? styleBias;
    /// <summary>
    /// Optional hard ceiling on WALKABLE ground, as a SHARE of the elevation ceiling (walls still tower above): a
    /// deliberately low/flat run keeps its top bands for peaks only. Omit ⇒ walkable may reach the ceiling like
    /// any high run.
    ///
    /// A share rather than a level, because these six numbers are authored intent — "this run walks up to two
    /// thirds of the world" — and were silently re-read as "up to a third" the moment the domain doubled. A
    /// fraction of the ceiling states the intent the author actually had, at any ceiling.
    /// </summary>
    public double? walkCeilingShare;
    /// <summary>Optional per-theme wall-silhouette shaping (<see cref="WallSilhouette"/>) — visual wall volume only.</summary>
    public WallSilhouette? wallStyle;

    /// <summary>Shallow copy — the `{ ...dna }` object spread (nested objects stay shared, as in JS).</summary>
    public MacroDna Clone() => (MacroDna)MemberwiseClone();
}

/// <summary>The macro sample used by the tile-carving pass (openness/rock MAY carry the punctuated Worley locus term).</summary>
public sealed class MacroSample
{
    /// <summary>How open the ground should be here, 0..1 (drives basin/plaza carving).</summary>
    public double openness;
    /// <summary>How much solid rock mass should stand here, 0..1 (drives massif raising &amp; wall summit height).</summary>
    public double rock;
    /// <summary>This tile's locus style (a MacroStyle key).</summary>
    public string style = "";
    /// <summary>1 near the locus site, 0 at its border.</summary>
    public double interior;
    /// <summary>Raw macro relief height 0..1 (low valley … high plateau).</summary>
    public double reliefN;
}

public static partial class EndlessMacro
{
    /// <summary>Archetype keys grouped by family — the DNA draws a family, then a uniform archetype within it.</summary>
    private static readonly string[] OPEN_ANCHORS =
    {
        "plaza",
        "amphitheatre",
        "lake_isles",
        "cave_warren",
        "gorge_network",
        "plateau_steppe",
        "canyon_web",
        "terraces_fan",
    };
    private static readonly string[] ROCK_ANCHORS =
    {
        "mesa_cluster",
        "dune_ridges",
        "star_fort",
        "ring_fort",
        "basalt_columns",
        "switchback_ridge",
        "bastion_bar",
        "hedge_spiral",
    };
    private static readonly string[] MIXED_ANCHORS =
    {
        "crater_field",
        "sinkhole_cluster",
        "rubble_maze",
        "colonnade_avenue",
        "moat_island",
        "chasm_rift",
        "obelisk_field",
        "plaza_spiral",
    };

    /// <summary>Continuous per-locus shape parameters (all hashed from the locus cell) — the source of unbounded variety.</summary>
    private sealed class AnchorParams
    {
        /// <summary>Feature radius as a fraction of the locus cell (0.7 tight … 1.4 sprawling).</summary>
        public double size;
        /// <summary>Cross-axis stretch (0.65 squashed … 1.55 elongated).</summary>
        public double aspect;
        /// <summary>Rotation of the whole form, radians.</summary>
        public double rot;
        /// <summary>Internal frequency multiplier — how fine the arms/ridges/pits are.</summary>
        public double rough;
        /// <summary>Arm / lobe / column count for the periodic archetypes (2..5).</summary>
        public double count;
    }

    /// <summary>
    /// A signed openness field in normalized local coords `(u,v)` (and precomputed `r=|u,v|`): positive ⇒ carve
    /// open ground here, negative ⇒ raise rock here. Each archetype is one such pure function.
    /// </summary>
    private delegate double AnchorShape(double u, double v, double r, AnchorParams p);

    /// <summary>
    /// The ≥20 landform archetypes as pure signed-field shapes. Positive = open, negative = rock; the caller fades
    /// each by a radial falloff, the locus interior and a per-locus intensity, then folds it into openness/rock —
    /// so the SAME downstream carve pass (basin carve / massif raise in endless.ts) draws every archetype with zero
    /// new plumbing. Shapes touch ONLY the carve fields, never walkable height, so the ≤1-step contract is untouched.
    /// </summary>
    private static readonly Dictionary<string, AnchorShape> ANCHOR_SHAPES = new()
    {
        // ── open family ──────────────────────────────────────────────────────────────────────────────────
        ["plaza"] = (_u, _v, r, _p) => 1 - r * 1.05, // a broad clear open plaza
        ["amphitheatre"] = (_u, _v, r, p) => (1 - r) * (0.85 + 0.15 * Math.sin(r * 7 * p.rough)), // tiered open bowl
        ["lake_isles"] = (u, v, r, p) =>
            1 -
            r -
            Math.max(0, Math.cos(u * 2.6 * p.rough + p.rot) * Math.cos(v * 2.6 * p.rough) - 0.35) * 2.4, // open basin studded with rock isles
        ["cave_warren"] = (u, v, r, p) =>
            0.65 - r + 0.5 * Math.sin(u * 3.4 * p.rough + p.rot) * Math.sin(v * 3.4 * p.rough - p.rot), // blobby open cells
        ["gorge_network"] = (u, v, r, p) =>
            (0.9 - r) * (0.55 - Math.min(1, Math.abs(Math.sin(u * 2.4 * p.rough + p.rot)) * 3)), // open branching gorges
        ["plateau_steppe"] = (_u, _v, r, _p) => 0.8 - r * 0.7, // broad gentle open steppe
        ["canyon_web"] = (u, v, r, p) =>
            (0.9 - r) *
            (0.55 -
                Math.min(
                    Math.abs(Math.sin(u * 2.2 * p.rough)),
                    Math.abs(Math.sin(v * 2.2 * p.rough + p.rot))) *
                1.6), // open channel web
        ["terraces_fan"] = (u, v, r, p) =>
            (0.85 - r) * (0.55 + 0.45 * Math.sin(Math.atan2(v, u) * p.count + p.rot)), // radiating open wedges
        // ── rock family ──────────────────────────────────────────────────────────────────────────────────
        ["mesa_cluster"] = (u, v, r, p) =>
            -(0.85 - r) *
            (Math.abs(Math.sin(u * 2.2 * p.rough + p.rot)) > 0.35 &&
            Math.abs(Math.sin(v * 2.2 * p.rough)) > 0.35
                ? 1
                : -0.4), // rock mesas, open lanes
        ["dune_ridges"] = (u, v, r, p) =>
            (0.95 - r) *
            (-0.35 - 0.65 * Math.sin((u * Math.cos(p.rot) + v * Math.sin(p.rot)) * 2.6 * p.rough)), // parallel rock ridges
        ["star_fort"] = (u, v, r, p) =>
        {
            double wall = 0.72 + 0.16 * Math.cos(Math.atan2(v, u) * p.count + p.rot);
            return r < wall - 0.12 ? 0.6 - r : -(1 - Math.min(1, Math.abs(r - wall) * 6)); // star rampart round an open bailey
        },
        ["ring_fort"] = (_u, _v, r, _p) => (r < 0.42 ? 0.6 - r : -(1 - Math.min(1, Math.abs(r - 0.62) * 6))), // rock ring wall, open interior
        ["basalt_columns"] = (u, v, r, p) =>
            -(0.8 - r) * (0.5 + 0.5 * Math.cos(u * 4 * p.rough + p.rot) * Math.cos(v * 4 * p.rough)), // clustered rock columns
        ["switchback_ridge"] = (u, v, r, p) =>
        {
            double zig = (Math.abs(((v * 1.3 * p.rough + 100) % 2) - 1) - 0.5) * 1.3;
            return (0.95 - r) * -(Math.abs(u - zig) < 0.28 ? 1 : -0.35); // zigzag rock ridge with a switchback path
        },
        ["bastion_bar"] = (u, v, _r, p) => -(0.95 - Math.max(Math.abs(u * p.aspect), Math.abs(v)) * 1.25), // a solid rectangular keep
        ["hedge_spiral"] = (u, v, r, p) =>
            (1 - r) * -Math.sin(Math.atan2(v, u) * p.count - r * 5 * p.rough + p.rot), // rock spiral arm, open lane between
        // ── mixed family ─────────────────────────────────────────────────────────────────────────────────
        ["crater_field"] = (u, v, r, p) =>
            (0.95 - r) * (Math.cos(u * 2.4 * p.rough + p.rot) * Math.cos(v * 2.4 * p.rough) - 0.25), // open bowls with rock rims
        ["sinkhole_cluster"] = (u, v, r, p) =>
            (0.9 - r) * (Math.cos(u * 3.6 * p.rough + p.rot) * Math.cos(v * 3.6 * p.rough) - 0.15), // dense small pits
        ["rubble_maze"] = (u, v, r, p) =>
            (0.9 - r) *
            0.6 *
            Math.sign(Math.sin(u * 4.5 * p.rough + p.rot) * Math.sin(v * 4.5 * p.rough - p.rot)), // broken rubble field
        ["colonnade_avenue"] = (u, v, _r, p) =>
            0.75 - Math.abs(v * p.aspect) - (Math.cos(u * p.count + p.rot) > 0.55 ? 1.1 : 0), // open avenue with rock pillars
        ["moat_island"] = (_u, _v, r, _p) =>
            r < 0.32 ? -(0.55 - r) : Math.abs(r - 0.55) < 0.16 ? 0.6 : -(0.5 - r) * 0.3, // rock isle, open moat ring
        ["chasm_rift"] = (u, v, r, p) =>
        {
            double d = Math.abs(u * Math.cos(p.rot) + v * Math.sin(p.rot));
            return (0.95 - r) * (d < 0.16 ? -1.2 : d < 0.55 ? 0.5 : -0.1); // sheer rock rift with open troughs beside
        },
        ["obelisk_field"] = (u, v, r, p) =>
            (0.85 - r) *
            (Math.cos(u * 3.2 * p.rough + p.rot) * Math.cos(v * 3.2 * p.rough) > 0.72 ? -1 : 0.35), // open ground, sparse rock obelisks
        ["plaza_spiral"] = (u, v, r, p) =>
            (1 - r) * Math.sin(Math.atan2(v, u) * p.count - r * 5 * p.rough + p.rot), // spiral of open + rock arms
    };

    /// <summary>The neutral macro DNA — a balanced world for any space that does not name its own run signature.</summary>
    public static readonly MacroDna NEUTRAL_MACRO_DNA = new()
    {
        opennessBias = 0,
        rockBias = 0,
        reliefAmp = 0.34,
        featureScale = 1,
        latticePitch = 6.5,
        styleWeights = new MacroStyleWeights { open = 0.22, rock = 0.16, mixed = 0.14 },
    };

    /// <summary>
    /// Per-run macro signatures, keyed by the run THEME key (`RunDef.themeKey`, which is the endless descriptor's
    /// `biomeKey`). Each entry pairs with that run's river/elevation profile in `terrain.ts` to give the descent a
    /// distinct silhouette — never a colour-swap of a sibling. Runs not listed fall back to NEUTRAL_MACRO_DNA.
    /// </summary>
    private static readonly Dictionary<string, MacroDna> RUN_MACRO_DNA = new()
    {
        // Run 1 — Highland Pass: open, rolling alpine opener with light maze between broad meadows and a few crags.
        // Its anchors are the ALPINE set — switchback ridges, gorge networks, steppe shelves and terrace fans — so
        // the opener reads as mountain country, not as a uniform draw over every archetype.
        ["highland_pass"] = new MacroDna
        {
            opennessBias = 0.12,
            rockBias = 0.02,
            reliefAmp = 0.4,
            featureScale = 1.15,
            latticePitch = 7.2,
            styleWeights = new MacroStyleWeights { open = 0.3, rock = 0.1, mixed = 0.08 },
            styleBias = new Dictionary<string, double>
            {
                ["switchback_ridge"] = 2.1,
                ["gorge_network"] = 1.9,
                ["plateau_steppe"] = 1.8,
                ["canyon_web"] = 1.6,
                ["terraces_fan"] = 1.45,
                ["mesa_cluster"] = 1.35,
                ["amphitheatre"] = 1.25,
                ["dune_ridges"] = 1.2,
            },
            // Rolling ranges with the odd crag: broad belts, soft jitter, a light spire accent on the tallest crests.
            wallStyle = new WallSilhouette { beltCell = 40, jagCell = 10, jagAmp = 0.8, spike = 1.1 },
        },
        // Run 2 — Noir Sprawl: dense wet megacity blocks — compressed street canyons, heavy tower masses, rare
        // holo-plazas and service-rift avenues; de-phased so the city breathes like districts, not a perfect grid.
        ["noir_sprawl"] = new MacroDna
        {
            opennessBias = -0.18,
            rockBias = 0.32,
            reliefAmp = 0.2,
            featureScale = 0.62,
            latticePitch = 4.8,
            styleWeights = new MacroStyleWeights { open = 0.06, rock = 0.42, mixed = 0.24 },
            // City forms only: solid block keeps, street-canyon webs, avenue colonnades, demolition lots and service
            // rifts; the pastoral/fort shapes are starved so no hedge spiral ever reads through the megacity.
            styleBias = new Dictionary<string, double>
            {
                ["bastion_bar"] = 2.5,
                ["canyon_web"] = 2.1,
                ["colonnade_avenue"] = 1.9,
                ["rubble_maze"] = 1.85,
                ["mesa_cluster"] = 1.7,
                ["chasm_rift"] = 1.6,
                ["plaza"] = 1.35,
                ["star_fort"] = 0.35,
                ["ring_fort"] = 0.5,
                ["hedge_spiral"] = 0.3,
                ["dune_ridges"] = 0.4,
            },
            walkCeilingShare = 2.0 / 3,
            // Stepped tower blocks: block-scale belts quantized into five storey tiers — a hard city skyline, taller
            // than the shared default (riseScale 0.92) so the sprawl looms over its wet streets.
            wallStyle = new WallSilhouette
            {
                beltCell = 24,
                jagCell = 6,
                beltAmp = 1.0,
                jagAmp = 0.55,
                terraceSteps = 5,
                riseScale = 0.92,
            },
        },
        // Run 3 - Olympian Sky Borough: high cloud-islands, marble plazas, broken colonnades and moat-isle
        // cloud canals. Open air dominates, but mixed city-ruin anchors keep the skyline unmistakably old-town.
        // Its relief intentionally reaches the ceiling: this is the vertical Olympus run.
        ["olympian_sky_borough"] = new MacroDna
        {
            opennessBias = 0.24,
            rockBias = 0.14,
            reliefAmp = 0.54,
            featureScale = 1.04,
            latticePitch = 6.4,
            styleWeights = new MacroStyleWeights { open = 0.32, rock = 0.18, mixed = 0.24 },
            styleBias = new Dictionary<string, double>
            {
                ["plaza"] = 1.55,
                ["amphitheatre"] = 1.18,
                ["lake_isles"] = 1.5,
                ["plateau_steppe"] = 1.35,
                ["colonnade_avenue"] = 2.25,
                ["moat_island"] = 2.05,
                ["plaza_spiral"] = 1.65,
                ["obelisk_field"] = 1.35,
                ["star_fort"] = 1.5, // acropolis ramparts on the cloud rim
                ["basalt_columns"] = 1.45, // freestanding marble column clusters
                ["terraces_fan"] = 1.4, // stepped agora wedges
            },
            // Broad marble tiers: sweeping belts quantized into four temple terraces, gently flattened tops.
            wallStyle = new WallSilhouette { beltCell = 46, jagCell = 11, terraceSteps = 4, spike = 0.92 },
        },
        // Run 4 - Sakura Temple Dream: open temple gardens, pond courts, moss terraces and deliberate shrine-wall
        // silhouettes. The biased anchors pull the same global macro system toward courtyards, isles and colonnades.
        ["sakura_temple_dream"] = new MacroDna
        {
            opennessBias = 0.27,
            rockBias = 0.02,
            reliefAmp = 0.28,
            featureScale = 1.32,
            latticePitch = 7.8,
            styleWeights = new MacroStyleWeights { open = 0.38, rock = 0.06, mixed = 0.18 },
            styleBias = new Dictionary<string, double>
            {
                ["plaza"] = 1.4,
                ["amphitheatre"] = 1.15,
                ["lake_isles"] = 1.85,
                ["terraces_fan"] = 1.45,
                ["colonnade_avenue"] = 1.8,
                ["moat_island"] = 1.75,
                ["plaza_spiral"] = 1.2,
                ["hedge_spiral"] = 1.6, // raked-garden spiral hedges
                ["gorge_network"] = 1.35, // stream ravines under the temple walks
                ["cave_warren"] = 1.2, // moss grottoes
            },
            walkCeilingShare = 5.0 / 6,
            // Low garden and shrine walls: soft and rounded, but with enough headroom for a few legible shrine crags.
            // A 0.88 rise remains below the ceiling from ordinary low garden ground.
            wallStyle = new WallSilhouette { beltCell = 32, jagCell = 12, jagAmp = 0.5, spike = 0.85, riseScale = 0.88 },
        },
        // Run 5 - Abyssal Deepsea: compressed black trench floors, broad water channels, reef ridges and wreck-isle
        // crossings. The macro stays low and heavy; biased basin/island anchors make the route feel carved by ocean
        // pressure rather than by ordinary hills.
        ["abyssal_deepsea"] = new MacroDna
        {
            opennessBias = 0.08,
            rockBias = 0.2,
            reliefAmp = 0.42,
            featureScale = 1.12,
            latticePitch = 6.8,
            styleWeights = new MacroStyleWeights { open = 0.3, rock = 0.24, mixed = 0.28 },
            styleBias = new Dictionary<string, double>
            {
                ["lake_isles"] = 2.15,
                ["moat_island"] = 2.4,
                ["chasm_rift"] = 1.85,
                ["sinkhole_cluster"] = 2.2,
                ["plateau_steppe"] = 1.25,
                ["terraces_fan"] = 1.35,
                ["colonnade_avenue"] = 0.8,
                ["cave_warren"] = 1.95, // black grotto cells
                ["basalt_columns"] = 1.85, // smoker-column clusters on the trench floor
                ["gorge_network"] = 1.6, // branching trench cuts
                ["dune_ridges"] = 1.4, // current-rippled sea-floor ridges
            },
            walkCeilingShare = 2.0 / 3,
            // Heavy smooth reef masses: broad belts, damped jitter, mesa-fattened and LOW (riseScale 0.75) — ocean
            // pressure country, not alpine crags.
            wallStyle = new WallSilhouette { beltCell = 42, beltAmp = 1.3, jagAmp = 0.45, spike = 0.8, riseScale = 0.75 },
        },
        // Run 6 - Rainbowland: punchy prism-plazas, looping lacquer hills and candy-wall knots. It stays readable,
        // but its landmarks arrive faster and louder than the gentler bright runs.
        ["rainbowland"] = new MacroDna
        {
            opennessBias = 0.21,
            rockBias = 0.08,
            reliefAmp = 0.48,
            featureScale = 0.78,
            latticePitch = 5.7,
            styleWeights = new MacroStyleWeights { open = 0.36, rock = 0.14, mixed = 0.28 },
            // Loud looping candy forms: spiral hedges and plaza whorls, candy-stick column clusters, bonbon craters —
            // while grim keep/fort masses are starved out of the palette.
            styleBias = new Dictionary<string, double>
            {
                ["hedge_spiral"] = 2.3,
                ["plaza_spiral"] = 2.1,
                ["basalt_columns"] = 1.75,
                ["crater_field"] = 1.5,
                ["lake_isles"] = 1.45,
                ["cave_warren"] = 1.3,
                ["bastion_bar"] = 0.5,
                ["star_fort"] = 0.6,
            },
            // Round candy lumps: big soft jitter blobs, mesa-fattened — a lacquer-hill skyline without spikes.
            wallStyle = new WallSilhouette { beltCell = 30, jagCell = 14, jagAmp = 0.95, spike = 0.75 },
        },
        // Run 7 - Clockwork Moon Bazaar: tight brass market lanes across a fractured moon, with crater rims,
        // gearwork courtyards, moat-islands and colonnade-like stall avenues interrupting the maze.
        ["clockwork_moon_bazaar"] = new MacroDna
        {
            opennessBias = -0.12,
            rockBias = 0.27,
            reliefAmp = 0.44,
            featureScale = 0.68,
            latticePitch = 4.9,
            styleWeights = new MacroStyleWeights { open = 0.08, rock = 0.34, mixed = 0.28 },
            styleBias = new Dictionary<string, double>
            {
                ["crater_field"] = 2.25,
                ["ring_fort"] = 1.7,
                ["bastion_bar"] = 1.55,
                ["colonnade_avenue"] = 1.8,
                ["moat_island"] = 1.7,
                ["plaza_spiral"] = 1.45,
                ["obelisk_field"] = 1.35,
                ["sinkhole_cluster"] = 1.45,
                ["star_fort"] = 1.25,
                ["switchback_ridge"] = 1.5, // zig-zag gear-track ridges
                ["rubble_maze"] = 1.35, // scrap-yard lots between the stalls
            },
            walkCeilingShare = 5.0 / 6,
            // Brass machinery shelves: six fine-notched tiers at block scale — a fractured-moon workshop skyline.
            wallStyle = new WallSilhouette { beltCell = 28, jagCell = 6, jagAmp = 0.6, terraceSteps = 6, riseScale = 0.88 },
        },
        // Run 8 - Sugarstorm-Carnival Run: broad midway courts, tent-ramp fans, syrup moat islands and a little
        // candy-wall architecture. It is open and low, but its biased anchors keep the route feeling staged and
        // theatrical rather than like plain fields.
        ["sugarstorm_carnival"] = new MacroDna
        {
            opennessBias = 0.3,
            rockBias = 0.02,
            reliefAmp = 0.24,
            featureScale = 1.28,
            latticePitch = 7.8,
            styleWeights = new MacroStyleWeights { open = 0.44, rock = 0.08, mixed = 0.28 },
            styleBias = new Dictionary<string, double>
            {
                ["plaza"] = 2.25,
                ["amphitheatre"] = 1.85,
                ["lake_isles"] = 1.4,
                ["terraces_fan"] = 2.4,
                ["ring_fort"] = 1.55,
                ["hedge_spiral"] = 1.5,
                ["colonnade_avenue"] = 1.65,
                ["moat_island"] = 2.25,
                ["plaza_spiral"] = 2.35,
                ["obelisk_field"] = 1.35,
                ["dune_ridges"] = 1.45, // rolled sugar dunes along the midway
                ["cave_warren"] = 1.2, // tent-cellar pockets
            },
            walkCeilingShare = 3.0 / 4,
            // Low striped tent country: soft rounded crests well UNDER the default height (riseScale 0.62) — the
            // carnival is a fairground plain, its silhouettes are tents and stands, not cliffs.
            wallStyle = new WallSilhouette
            {
                beltCell = 36,
                jagCell = 13,
                jagAmp = 0.9,
                contrast = 2.75,
                spike = 0.8,
                riseScale = 0.62,
            },
        },
        // Run 9 - Prismglass Archive: compact reflective corridors, strong shelf walls and measured glass courts.
        // Bias the generic macro system toward archive-like colonnade aisles, moated mirror courts, rifts and bars.
        ["prismglass_archive"] = new MacroDna
        {
            opennessBias = -0.06,
            rockBias = 0.32,
            reliefAmp = 0.46,
            featureScale = 0.72,
            latticePitch = 5.2,
            styleWeights = new MacroStyleWeights { open = 0.12, rock = 0.36, mixed = 0.3 },
            styleBias = new Dictionary<string, double>
            {
                ["plaza"] = 1.25,
                ["lake_isles"] = 1.45,
                ["bastion_bar"] = 2.1,
                ["ring_fort"] = 1.45,
                ["basalt_columns"] = 1.25,
                ["colonnade_avenue"] = 2.55,
                ["moat_island"] = 2.2,
                ["chasm_rift"] = 1.7,
                ["obelisk_field"] = 1.55,
                ["hedge_spiral"] = 1.4, // a spiral reading-gallery aisle
                ["star_fort"] = 1.2,
            },
            walkCeilingShare = 3.0 / 4,
            // Tall glass shelf bars: aisle-scale belts quantized into THREE hard shelf heights, near-full rise —
            // the archive's stacks tower in clean measured steps.
            wallStyle = new WallSilhouette
            {
                beltCell = 22,
                jagCell = 7,
                beltAmp = 1.25,
                terraceSteps = 3,
                terraceBand = 4,
                spike = 1.05,
                riseScale = 0.96,
            },
        },
        // Run 10 - Starforged Cathedral Endrun: end-run nave scale, altar terraces, sacred bridges, black-star
        // channels and towering cathedral wall families together.
        ["starforged_cathedral_endrun"] = new MacroDna
        {
            opennessBias = 0.16,
            rockBias = 0.26,
            reliefAmp = 0.54,
            featureScale = 1.44,
            latticePitch = 7.4,
            styleWeights = new MacroStyleWeights { open = 0.34, rock = 0.24, mixed = 0.26 },
            styleBias = new Dictionary<string, double>
            {
                ["amphitheatre"] = 2.15,
                ["terraces_fan"] = 2.2,
                ["colonnade_avenue"] = 2.35,
                ["obelisk_field"] = 2.0,
                ["star_fort"] = 1.75,
                ["ring_fort"] = 1.55,
                ["moat_island"] = 1.9,
                ["plaza_spiral"] = 1.55,
                ["bastion_bar"] = 1.45,
                ["basalt_columns"] = 1.6, // freestanding pillar clusters in the nave fields
                ["rubble_maze"] = 1.2, // collapsed transept ruins
            },
            // Needle spires: a strong spike exponent starves the mid band, leaving sparse black towers that rise to
            // the FULL ceiling (riseScale 1) over low ruin walls — the end-run's cathedral skyline.
            wallStyle = new WallSilhouette { jagCell = 7, jagAmp = 1.05, beltAmp = 1.2, spike = 1.65, riseScale = 1 },
        },
        // Alien Ranch (GENERATOR-ONLY — not a run key, resolved only for `theme:alien_ranch` generator seeds):
        // broad open grazing country whose anchors are relentlessly CIRCULAR — saucer-scorched crater fields,
        // ring paddock enclosures, spiral crop hedges, moated pens and goo-pond isles — so the macro map itself
        // reads like an aerial photograph of crop circles. Human fort/city forms are starved (never zeroed: the
        // ≥20-archetype reachability gate needs every family alive).
        ["alien_ranch"] = new MacroDna
        {
            opennessBias = 0.2,
            rockBias = 0.06,
            reliefAmp = 0.36,
            featureScale = 1.22,
            latticePitch = 7.4,
            styleWeights = new MacroStyleWeights { open = 0.34, rock = 0.12, mixed = 0.16 },
            styleBias = new Dictionary<string, double>
            {
                ["crater_field"] = 2.3,
                ["ring_fort"] = 2.0,
                ["hedge_spiral"] = 1.85,
                ["plateau_steppe"] = 1.7,
                ["lake_isles"] = 1.6,
                ["moat_island"] = 1.5,
                ["mesa_cluster"] = 1.45,
                ["sinkhole_cluster"] = 1.35,
                ["obelisk_field"] = 1.3,
                ["star_fort"] = 0.4,
                ["colonnade_avenue"] = 0.45,
                ["bastion_bar"] = 0.5,
            },
            // Flat-topped paddock mesas: a fattened mid band (spike < 1) quantized into THREE stacked shelf tiers,
            // deliberately low (riseScale 0.82) — terraced grazing bluffs, never an alpine wall country.
            wallStyle = new WallSilhouette
            {
                rangeCell = 128,
                beltCell = 44,
                jagCell = 13,
                jagAmp = 0.55,
                spike = 0.8,
                terraceSteps = 3,
                riseScale = 0.82,
            },
        },
    };

    /// <summary>The macro DNA for a run theme / biome key. One source of truth (mirrors terrainProfileFor).</summary>
    public static MacroDna endlessMacroDnaFor(string? biomeKey)
    {
        return (!string.IsNullOrEmpty(biomeKey) && RUN_MACRO_DNA.TryGetValue(biomeKey, out MacroDna? dna) ? dna : null)
            ?? NEUTRAL_MACRO_DNA;
    }

    /* ── Field salts (independent of the elevation octave salts) ──────────────────────────────────────── */
    private const uint RELIEF_SALT = 0x2df1a6c3;
    private const uint RELIEF_DETAIL_SALT = 0x7bb0f13d;
    private const uint OPEN_SALT = 0x1c9e6b57;
    private const uint ROCK_SALT = 0x51d3ac9f;
    private const uint WARP_X_SALT = 0x64bf27a1;
    private const uint WARP_Y_SALT = 0x38e5c9b7;
    private const uint LOCUS_X_SALT = 0x9a1b8f2d;
    private const uint LOCUS_Y_SALT = 0x4e6d3157;
    private const uint STYLE_SALT = 0x27f4ab8d;
    private const uint ARCHETYPE_SALT = 0x3e9a71cf;
    private const uint INTENSITY_SALT = 0x6cd91e4b;
    private const uint SIZE_SALT = 0x13a7fe95;
    private const uint ASPECT_SALT = 0x48c2b9e7;
    private const uint ROT_SALT = 0x7d3e1a95;
    private const uint ROUGH_SALT = 0x2b6fc84d;
    private const uint COUNT_SALT = 0x5a91d3fb;

    /* ── Field cell sizes (in global tiles; 32 tiles = 1 chunk) ──────────────────────────────────────── */
    private const double RELIEF_CELL = 352; // ~11 chunks — broad plateaus & valleys
    private const double RELIEF_DETAIL_CELL = 168;
    private const double RELIEF_DETAIL_AMP = 0.34;
    private const double OPEN_CELL = 168;
    private const double ROCK_CELL = 200;
    private const double MACRO_CELL = 232; // ~7.25 chunks per Worley locus (scaled by dna.featureScale)

    // `const clamp01 = scalarClamp01;`
    private static double clamp01(double v) => Scalar.clamp01(v);

    // Every `(seed ^ salt) >>> 0` below is `Js.ToUint32(seed) ^ salt`: ToUint32 and ToInt32 share their low 32
    // bits, XOR is bitwise, and `>>> 0` reinterprets the result as unsigned — the same uint32 value.

    /// <summary>
    /// Domain-warped value noise at global TILE coords → [0,1]. The warp breaks the axis-aligned value-noise
    /// lattice so the fields read as organic blobs, not square banding. Used ONLY for carve/wall fields (their
    /// extra gradient does not matter there); the walkable-height octaves stay unwarped for ≤1-step safety.
    /// </summary>
    private static double warpTile(double seed, uint salt, double gx, double gy, double cell)
    {
        double warpCell = cell * 1.7;
        double warpAmp = cell * 0.55;
        double wx =
            (valueNoise(Js.ToUint32(seed) ^ salt ^ WARP_X_SALT, gx + 19.3, gy - 11.7, warpCell) - 0.5) * warpAmp;
        double wy =
            (valueNoise(Js.ToUint32(seed) ^ salt ^ WARP_Y_SALT, gx - 27.1, gy + 23.9, warpCell) - 0.5) * warpAmp;
        return valueNoise(Js.ToUint32(seed) ^ salt, gx + wx, gy + wy, cell);
    }

    /// <summary>
    /// Raw macro relief height at a global tile → [0,1] (0 = deepest valley, 1 = highest plateau). Two UNWARPED
    /// value-noise octaves; the cell sizes/amplitudes keep the per-tile gradient tiny, so adding a bounded multiple
    /// of this to the biome height never breaks the ≤1-step banding invariant (see endlessGroundLevelAt).
    /// </summary>
    public static double endlessMacroReliefRaw(double seed, double gtx, double gty)
    {
        double @base = valueNoise(Js.ToUint32(seed) ^ RELIEF_SALT, gtx, gty, RELIEF_CELL);
        double detail = valueNoise(Js.ToUint32(seed) ^ RELIEF_DETAIL_SALT, gtx, gty, RELIEF_DETAIL_CELL);
        return clamp01((@base + detail * RELIEF_DETAIL_AMP) / (1 + RELIEF_DETAIL_AMP));
    }

    /// <summary>
    /// Walkable ground LEVEL at a global tile for an endless run — the biome height field plus the macro relief
    /// octave, banded into `0..maxLevel`. This is the `levelAt` fed to `buildStandardElevationField`. Pure
    /// `(seed, tile)` and ≤1-step-smooth between neighbours (the seam contract relies on it — `endlessMacro.test`
    /// proves it), because every term is a large-cell smooth octave (no Worley locus term here).
    ///
    /// Continuous Endless ground height before it is assigned to the standard 0..12 ladder. Keeping this as the
    /// single source lets the vertical-composition layer apply a landscape's slow altitude/contrast shaping before
    /// quantization without duplicating the biome + macro-relief formula.
    /// </summary>
    public static double endlessGroundHeightAt(double seed, double gtx, double gty, ElevationProfile profile, MacroDna dna)
    {
        double h = elevationHeightAt(seed, gtx, gty, profile);
        double relief = (endlessMacroReliefRaw(seed, gtx, gty) - 0.5) * dna.reliefAmp;
        return clamp01(h + relief);
    }

    /// <summary>
    /// Quantize one already-composed normalized height through the run's authored walkable ladder. Returns an
    /// integral level (JS returns the same integral number for the integral `maxLevel` every caller passes).
    /// </summary>
    public static int endlessGroundLevelFromHeight(double height, MacroDna dna, double maxLevel = MAX_ELEVATION)
    {
        double v = clamp01(height);
        // A run may cap its walkable ground below the ceiling (a deliberately LOW/flat run keeps its top band for
        // walls only). Quantize DIRECTLY into that run's available bands instead of quantizing into 0..12 and then
        // clamping: the latter collapsed the complete upper tail into one repeated ceiling plateau (up to a third
        // of a streamed region in low themes). Direct banding spends the full authored range without a cap pile-up.
        // Reducing the number of bands is monotonic and non-expansive, so the ≤1-step smoothness still holds.
        double walkCap =
            dna.walkCeilingShare == null
                ? maxLevel
                : Math.max(1, Math.min(maxLevel, Math.round(maxLevel * dna.walkCeilingShare.Value)));
        double walkBands = walkCap + 1;
        double lvl = Math.floor(v * walkBands);
        return (int)(lvl < 0 ? 0 : lvl > walkCap ? walkCap : lvl);
    }

    /// <summary>
    /// A macro locus: the winning Worley cell, how deep inside it this tile sits (1 = at the site, 0 = border), and
    /// the tile's offset from that site in CELL units (the local frame the anchor shape functions are drawn in).
    /// </summary>
    private sealed class MacroLocus
    {
        public double cix;
        public double ciy;
        public double interior;
        /// <summary>Warped-space offset (cell units) from the winning site to this tile — the anchor shape's local `(du,dv)`.</summary>
        public double du;
        public double dv;
    }

    /// <summary>
    /// Warped Worley/Voronoi partition of the infinite plane into unique feature loci. Returns the nearest jittered
    /// site's cell coords and an `interior` scalar that is 1 near the site and eases to 0 at the boundary with the
    /// next locus (via the normalized gap between the two closest sites) — so a locus' feature fades out organically
    /// toward its neighbours instead of tiling hard. The sample point is domain-warped so cell borders are wiggly.
    /// </summary>
    private static MacroLocus macroLocus(double seed, double gtx, double gty, double cell)
    {
        // Domain warp so Voronoi borders are organic, not straight bisectors.
        double warpCell = cell * 1.6;
        double warpAmp = cell * 0.4;
        double wx =
            (valueNoise(Js.ToUint32(seed) ^ LOCUS_X_SALT ^ WARP_X_SALT, gtx + 5.1, gty - 9.3, warpCell) - 0.5) *
            warpAmp;
        double wy =
            (valueNoise(Js.ToUint32(seed) ^ LOCUS_Y_SALT ^ WARP_Y_SALT, gtx - 7.7, gty + 3.9, warpCell) - 0.5) *
            warpAmp;
        double fx = (gtx + wx) / cell;
        double fy = (gty + wy) / cell;
        double ix = Math.floor(fx);
        double iy = Math.floor(fy);
        double best = double.PositiveInfinity;
        double second = double.PositiveInfinity;
        double bcx = ix;
        double bcy = iy;
        double bdu = 0;
        double bdv = 0;
        for (int j = -1; j <= 1; j++)
        {
            for (int i = -1; i <= 1; i++)
            {
                double cx = ix + i;
                double cy = iy + j;
                double jx = latticeHash(Js.ToUint32(seed) ^ LOCUS_X_SALT, cx, cy);
                double jy = latticeHash(Js.ToUint32(seed) ^ LOCUS_Y_SALT, cx, cy);
                double dx = fx - (cx + jx);
                double dy = fy - (cy + jy);
                double d = dx * dx + dy * dy;
                if (d < best)
                {
                    second = best;
                    best = d;
                    bcx = cx;
                    bcy = cy;
                    bdu = dx;
                    bdv = dy;
                }
                else if (d < second)
                {
                    second = d;
                }
            }
        }
        double d1 = Math.sqrt(best);
        double d2 = Math.sqrt(second);
        // Interior: 1 well inside the cell, 0 at the equidistant border; smoothstep damps the border crease.
        double interior = Scalar.smoothstep(clamp01((d2 - d1) / 0.5));
        return new MacroLocus { cix = bcx, ciy = bcy, interior = interior, du = bdu, dv = bdv };
    }

    /// <summary>Deterministic [0,1) hash of a locus cell + salt.</summary>
    private static double locusHash(double seed, double cix, double ciy, uint salt)
    {
        return latticeHash(Js.ToUint32(seed) ^ salt, cix, ciy);
    }

    /// <summary>
    /// Draw a locus' anchor style: first its FAMILY from the run DNA weights (residual weight ⇒ `labyrinth`), then a
    /// specific archetype uniformly within that family from a second cell hash. So the DNA controls each run's
    /// open/rock character while every locus still draws one of the ≥20 unique landform shapes.
    /// </summary>
    private static string pickStyle(double seed, double cix, double ciy, MacroDna dna)
    {
        double r = locusHash(seed, cix, ciy, STYLE_SALT);
        MacroStyleWeights w = dna.styleWeights;
        double acc = w.open;
        string[]? list = null;
        if (r < acc) list = OPEN_ANCHORS;
        else if (r < (acc += w.rock)) list = ROCK_ANCHORS;
        else if (r < (acc += w.mixed)) list = MIXED_ANCHORS;
        if (list == null) return "labyrinth";
        double pick = locusHash(seed, cix, ciy, ARCHETYPE_SALT);
        if (dna.styleBias != null) return pickWeightedStyle(list, dna.styleBias, pick);
        return list[(int)Math.min(list.Length - 1, Math.floor(pick * list.Length))];
    }

    private static string pickWeightedStyle(string[] list, Dictionary<string, double> bias, double pick)
    {
        double total = 0;
        foreach (string style in list)
            total += Math.max(0, bias.TryGetValue(style, out double b) ? b : 1);
        if (total <= 0) return list[0];
        double remaining = pick * total;
        foreach (string style in list)
        {
            remaining -= Math.max(0, bias.TryGetValue(style, out double b) ? b : 1);
            if (remaining <= 0) return style;
        }
        return list[list.Length - 1];
    }

    /// <summary>Continuous per-locus shape params — all hashed from the locus cell, so every locus is one of a kind.</summary>
    private static AnchorParams anchorParamsFor(double seed, double cix, double ciy)
    {
        return new AnchorParams
        {
            size = 0.7 + locusHash(seed, cix, ciy, SIZE_SALT) * 0.7, // 0.7 … 1.4
            aspect = 0.65 + locusHash(seed, cix, ciy, ASPECT_SALT) * 0.9, // 0.65 … 1.55
            rot = locusHash(seed, cix, ciy, ROT_SALT) * Math.PI * 2,
            rough = 0.7 + locusHash(seed, cix, ciy, ROUGH_SALT) * 0.8, // 0.7 … 1.5
            count = 2 + Math.floor(locusHash(seed, cix, ciy, COUNT_SALT) * 4), // 2 … 5
        };
    }

    /// <summary>
    /// Sample the macro fields at a global tile for the carve pass. `openness` and `rock` combine a smooth base
    /// (valleys tend open, highs tend rocky — plus independent warped noise) with a punctuated per-locus ANCHOR: the
    /// winning locus draws one of the ≥20 archetype shapes (ANCHOR_SHAPES), evaluated at this tile's local `(u,v)`
    /// frame (rotated/scaled/skewed by the locus' continuous params), faded by a radial falloff, the locus interior
    /// and a per-locus intensity. A positive shape value carves the ground open here; a negative one raises rock —
    /// so a spiral plaza, ring fort, chasm rift or mesa cluster each carves its own unmistakable signature through
    /// the SAME downstream carve pass. All of this touches only `openness`/`rock` (never walkable height).
    /// </summary>
    public static MacroSample endlessMacroSample(double seed, double gtx, double gty, MacroDna dna)
    {
        double reliefN = endlessMacroReliefRaw(seed, gtx, gty);
        double openNoise = warpTile(seed, OPEN_SALT, gtx, gty, OPEN_CELL);
        double rockNoise = warpTile(seed, ROCK_SALT, gtx, gty, ROCK_CELL);
        // Smooth base: low ground reads open, high ground reads rocky, each with its own independent variation.
        double openness = clamp01(dna.opennessBias + (0.52 - reliefN) * 0.7 + (openNoise - 0.5) * 0.95);
        double rock = clamp01(dna.rockBias + (reliefN - 0.55) * 0.66 + (rockNoise - 0.5) * 0.72);

        MacroLocus locus = macroLocus(seed, gtx, gty, MACRO_CELL * dna.featureScale);
        string style = pickStyle(seed, locus.cix, locus.ciy, dna);
        if (style != "labyrinth")
        {
            AnchorParams p = anchorParamsFor(seed, locus.cix, locus.ciy);
            // Local anchor frame: offset from the site (cell units) → rotate → scale by the feature radius / aspect.
            double cos = Math.cos(p.rot);
            double sin = Math.sin(p.rot);
            double half = 0.5 * p.size; // feature radius in cell units
            double u = (locus.du * cos + locus.dv * sin) / half;
            double v = (-locus.du * sin + locus.dv * cos) / (half * p.aspect);
            double r = Math.hypot(u, v);
            double falloff = clamp01(1.25 - r); // the shape only reaches ~1.25 feature-radii, then fades to nothing
            if (falloff > 0)
            {
                double intensity = 0.6 + locusHash(seed, locus.cix, locus.ciy, INTENSITY_SALT) * 0.7;
                double signed = ANCHOR_SHAPES[style](u, v, r, p) * falloff * locus.interior * intensity;
                const double k = 0.7;
                if (signed > 0)
                {
                    openness = clamp01(openness + signed * k);
                    rock = clamp01(rock - signed * 0.4 * k);
                }
                else
                {
                    rock = clamp01(rock - signed * k);
                    openness = clamp01(openness + signed * 0.3 * k);
                }
            }
        }
        return new MacroSample { openness = openness, rock = rock, style = style, interior = locus.interior, reliefN = reliefN };
    }

    /* ── Wall-relief field salts/cells (the ragged skyline; independent of the carve fields) ─────────────── */
    private const uint WALL_BELT_SALT = 0x1f83d9ab;
    private const uint WALL_JAG_SALT = 0x5be0cd19;
    private const uint WALL_RANGE_SALT = 0x3c6ef372;
    private const double WALL_RANGE_CELL = 112; // ~3.5 chunks — whole skyline countries rather than per-chunk shuffles
    private const double WALL_BELT_CELL = 34; // ~1 chunk — broad belts of taller / shorter wall country
    private const double WALL_JAG_CELL = 9; // per-cluster jitter so neighbouring wall masses never match height
    private const double WALL_RANGE_AMP = 0.85;
    private const double WALL_RISE_SCALE = 1; // the default silhouette may spend the complete remaining headroom
    private const double WALL_RELIEF_CONTRAST = 2.25;

    /// <summary>
    /// Uneven wall-relief signal at a global tile → [0,1]. Three normalized value-noise octaves compose whole
    /// skyline countries, wall belts and a restrained local contour. Normalizing the weighted signal BEFORE
    /// contrast/theme shaping is important: the former additive rock bias clipped dense themes against 1.0, so
    /// their supposedly tall skyline became one repeated ceiling slab. The bounded bias below changes character
    /// without throwing away either tail of the height range.
    /// The theme's WallSilhouette then SHAPES the signal — belt/jag scale, spire exponent, terrace quantization — so
    /// a noir tower district, a cathedral needle skyline and a candy lump country come from the same field under
    /// different DNA (a theme without `wallStyle` uses the shared ragged defaults).
    /// Pure `(seed, tile)` ⇒ seam-continuous. Carries no walkable contract (walls are visual volume).
    /// </summary>
    public static double endlessWallReliefAt(double seed, double gtx, double gty, MacroDna dna)
    {
        WallSilhouette? ws = dna.wallStyle;
        double range = valueNoise(Js.ToUint32(seed) ^ WALL_RANGE_SALT, gtx, gty, ws?.rangeCell ?? WALL_RANGE_CELL);
        double belt = valueNoise(Js.ToUint32(seed) ^ WALL_BELT_SALT, gtx, gty, ws?.beltCell ?? WALL_BELT_CELL);
        double jag = valueNoise(Js.ToUint32(seed) ^ WALL_JAG_SALT, gtx, gty, ws?.jagCell ?? WALL_JAG_CELL);
        double rangeAmp = Math.max(0.01, ws?.rangeAmp ?? WALL_RANGE_AMP);
        double beltAmp = Math.max(0.01, ws?.beltAmp ?? 1.15);
        double jagAmp = Math.max(0.01, ws?.jagAmp ?? 0.7);
        double normalized = (range * rangeAmp + belt * beltAmp + jag * jagAmp) / (rangeAmp + beltAmp + jagAmp);
        // Rock-rich themes lean only slightly upward. Their actual massiveness comes from the independent macro
        // rock field; using it as a large additive skyline offset was the source of ceiling saturation.
        double authoredBias = ws?.bias ?? dna.rockBias * 0.16;
        double relief = Scalar.clamp01(0.5 + (normalized - 0.5) * (ws?.contrast ?? WALL_RELIEF_CONTRAST) + authoredBias);
        // Spire/mesa shaping: >1 starves the mid-band (sparse needles over low walls), <1 fattens it (broad masses).
        double spike = ws?.spike ?? 1;
        if (spike != 1) relief = Math.pow(relief, spike);
        // Round to N symmetric plateau heights. The former floor quantizer over-weighted the top tier after a
        // positive theme bias and was a second source of repeated ceiling-height walls.
        double steps = ws?.terraceSteps ?? 0;
        if (steps >= 2) relief = Math.round(relief * (steps - 1)) / (steps - 1);
        return relief;
    }

    /// <summary>
    /// The LEVEL the ragged wall-relief field raises a wall to at a global tile — `ground + 1` at zero relief up to
    /// near the ceiling at full relief (scaled by the theme's `riseScale`). This is the ONE shared formula behind
    /// both the elevation build's summit field (endlessRockSummitAt) and the endless generator's kerb-wall lift
    /// post-pass, so the two can never drift apart. Cheap by design (3 value-noise octaves — no per-tile Worley),
    /// pure `(seed, tile)` ⇒ seam-continuous; walls carry no ≤1-step contract.
    /// </summary>
    public static int endlessWallReliefRiseAt(
        double seed,
        double gtx,
        double gty,
        MacroDna dna,
        double groundLevel,
        double maxLevel = MAX_ELEVATION)
    {
        double head = Math.max(0, maxLevel - groundLevel);
        double relief = endlessWallReliefAt(seed, gtx, gty, dna);
        // Reserve the one-level structural wall first, then distribute ONLY the remaining budget. The former
        // `1 + relief * head` overshot its own headroom and relied on a clamp, turning every upper-tail sample into
        // the same MAX wall. This reaches MAX exactly only at full relief and never piles values up beyond it.
        double scalableHead = Math.max(0, head - 1);
        double rise = head <= 0 ? 0 : 1 + relief * scalableHead * (dna.wallStyle?.riseScale ?? WALL_RISE_SCALE);
        double lvl = groundLevel + Math.round(rise);
        return (int)(lvl < 0 ? 0 : lvl > maxLevel ? maxLevel : lvl);
    }

    /// <summary>
    /// The level a wall towers to at a global tile — the endless world's `wallMassifAt`, and the field the
    /// wall-relief post-pass raises EVERY wall to. The wall rises to the GREATER of (a) its rock-massif summit (so
    /// real ranges &amp; canyon walls still peak where the macro rock field is high) and (b) its ragged
    /// endlessWallReliefRiseAt height, so even a thin plain-labyrinth wall gets an uneven mid height instead of a
    /// flat one-step kerb — a deep, varied skyline that uses the full 1..MAX height range. Walls carry no ≤1-step
    /// contract (visual volume), so this may use the punctuated locus term. `groundLevel` is the walkable ground
    /// level (the mass rises from it).
    /// </summary>
    public static int endlessRockSummitAt(
        double seed,
        double gtx,
        double gty,
        MacroDna dna,
        double groundLevel,
        double maxLevel = MAX_ELEVATION)
    {
        MacroSample s = endlessMacroSample(seed, gtx, gty, dna);
        double head = Math.max(0, maxLevel - groundLevel);
        double scalableHead = Math.max(0, head - 1);
        // Rare massif cores may stand above their theme's ordinary wall country, but still consume the same finite
        // budget and therefore do not collapse a whole rock-rich district onto MAX.
        double wallScale = dna.wallStyle?.riseScale ?? WALL_RISE_SCALE;
        double massifScale = Math.min(1, wallScale + 0.18);
        double massifRise = head <= 0 ? 0 : 1 + s.rock * s.rock * scalableHead * massifScale;
        double massifLvl = groundLevel + Math.round(massifRise);
        double reliefLvl = endlessWallReliefRiseAt(seed, gtx, gty, dna, groundLevel, maxLevel);
        double lvl = Math.max(massifLvl, reliefLvl);
        return (int)(lvl < 0 ? 0 : lvl > maxLevel ? maxLevel : lvl);
    }

    /// <summary>
    /// World-space spacing used by the final Solid-cap profile. Authored terrace counts remain meaningful: a
    /// five-step skyline over the twelve-level Endless range becomes three-level shelves, while themes without an
    /// explicit stepped silhouette use the same reference-like three-level geological rhythm.
    /// </summary>
    public static int endlessWallTerraceBandFor(MacroDna dna, double maxLevel = MAX_ELEVATION)
    {
        double safeCeiling = Math.max(2, Math.round(maxLevel));
        double? authoredBand = dna.wallStyle?.terraceBand;
        if (authoredBand != null)
            return (int)Math.max(2, Math.min(safeCeiling, Math.round(authoredBand.Value)));
        double steps = dna.wallStyle?.terraceSteps ?? 0;
        if (steps >= 2) return (int)Math.max(2, Math.min(safeCeiling, Math.round(safeCeiling / (steps - 1))));
        return (int)Math.min(3, safeCeiling);
    }

    /// <summary>
    /// Structural height reserved above the neighbouring walkable country before a Solid summit is snapped to its
    /// absolute geological lattice. Endless keeps only one level in the stored cap and supplies the rest through
    /// its 6.4-level physical wall shell plus coherent skyline accents. Splitting the height this way preserves a
    /// monumental visible average even where stored ground and wall caps both saturate at the shared ceiling.
    /// </summary>
    public const int ENDLESS_WALL_BASELINE_STORED_RISE = 1;

    /// <summary>
    /// Final stored elevation of a Solid cap. Quantizing the absolute summit (not merely its rise above the local
    /// floor) produces broad coherent masses separated by a few decisive 2..6-level escarpments. The rounded-up
    /// ground band guarantees that every wall still overtops its local walkable bank; the standard wall rise is
    /// applied later by the shared terrain model and supplies the visible face volume.
    /// </summary>
    public static int endlessWallTerraceLevelAt(
        double seed,
        double gtx,
        double gty,
        MacroDna dna,
        double groundLevel,
        double maxLevel = MAX_ELEVATION)
    {
        double band = endlessWallTerraceBandFor(dna, maxLevel);
        double summit = endlessRockSummitAt(seed, gtx, gty, dna, groundLevel, maxLevel);
        // Snap a one-level structural reserve upward. With the default three-level lattice this yields a 1..3
        // stored rise before summit relief; the 6.4-level Endless shell keeps the complete visible population well
        // above authored Hub walls while retaining meaningful headroom at the shared elevation ceiling.
        // Absolute snapping preserves broad massifs and avoids reintroducing cell-local contour rings.
        double groundBand = Math.ceil((groundLevel + ENDLESS_WALL_BASELINE_STORED_RISE) / band) * band;
        double summitBand = Math.floor(summit / band) * band;
        return (int)Math.max(0, Math.min(maxLevel, Math.max(groundBand, summitBand)));
    }

    /// <summary>
    /// Macro depth ease: the spawn chunk + opening ring stay calm (no macro carving); features ramp in a few chunks
    /// out. Slightly earlier/gentler than the relief ease so the world gains character without a jarring flat
    /// opening. `depthChunks` is the radial distance from spawn in chunks.
    /// </summary>
    public static double endlessMacroEaseAt(double depthChunks)
    {
        return Scalar.smoothstep(clamp01((depthChunks - 0.6) / 2.3));
    }
}
