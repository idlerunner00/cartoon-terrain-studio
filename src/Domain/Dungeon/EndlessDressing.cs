// Port of packages/shared/src/domain/dungeon/endlessDressing.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;

namespace Fluitown.Domain;

/*
 * Dressing DNA for the generated world — the registry that decides WHAT stands on a cell.
 *
 * Two ideas carry the whole registry:
 *
 *  - **Every world grows the same tree.** The `tree` slot is THE canonical tree — one generator, recoloured
 *    and reshaped per world by the tree-visual registry. A world states its identity through pigment, landform
 *    and the shape of its own tree; it never states it by standing a bespoke set piece next to one.
 *  - **Context owns the vocabulary.** A cell on a water bank, on a chasm lip, deep inside a rock mass or on
 *    open ground wants different growth, and the weights that express that belong in data next to the theme.
 *
 * ## The vocabulary is growth, and only growth
 * A tree, the scrub under it and the stump of one that was felled. That is the whole list. Stone furniture
 * (cairns, crystal shelves), fire (braziers), built monuments (standing stones, ruined arches, wayshrines),
 * the bespoke per-world `signature` vertical and the fallen log were all reachable here, and every one of them
 * was scenery the Fluis never made, never used and never referred to. They are gone rather than weighted to
 * zero: a weight of zero is an invitation to put them back, and a monument lattice with nothing to place is a
 * mechanism modelling a world we do not have.
 *
 * Removing them does not thin the world. Weights inside a table are relative and the per-context `density`
 * carries the count, so a context that used to spend half its slots on gravel now spends them on scrub.
 *
 * ## The open floor is the world too
 * The measured census of the frames that failed review is unambiguous: the walkable, hazard-free floor is
 * 27..35% of every generated chunk and received 12..19% of the dressing — and, far more damagingly, 1.6..2.8
 * tall silhouettes per chunk against 8.5..11 on the wall edges. The budget SPLIT was already fair; the DATA
 * was not. A context's `density` is a share, so a floor at 0.55 against a wall edge at 1.1 says "half as
 * dressed per cell as the terrace", which is exactly the frame where the treeline stands on the terrace caps
 * and the field the colony works in is bare. The floor now runs at parity with the wall edge and its
 * vocabulary leads with the canopy instead of with ground cover.
 *
 * Parity alone was still not enough, and the correction is worth recording because it is counter-intuitive:
 * a fair SHARE is not a DISTRIBUTION. The floor's slots were all won by the grove field's cores, so a 38%
 * ground share coexisted with a bare plate. The placement pass now spends most of the floor's own share on a
 * SPACED skeleton before anything is ranked; the registry's part of that contract is that every context which
 * dresses open ground names a vertical.
 *
 * Adding a run theme is a registry entry: no engine branch, no new placement code. Unknown keys fall back to
 * DEFAULT_ENDLESS_DRESSING, so a modded theme still dresses its world.
 */

/// <summary>
/// Where a candidate cell sits in the terrain. The placement pass classifies every cell into exactly one of
/// these, then draws a kind from the matching weight table.
/// </summary>
public static class EndlessDressingContext
{
    /// <summary>Rock within two cells of a non-rock neighbour — the visible face of a wall mass.</summary>
    public const string WallEdge = "wallEdge";
    /// <summary>Rock at least three cells deep inside its mass — the summit/plateau body.</summary>
    public const string WallInterior = "wallInterior";
    /// <summary>Walkable ground away from any hazard.</summary>
    public const string Ground = "ground";
    /// <summary>A cell touching Water.</summary>
    public const string WaterBank = "waterBank";
    /// <summary>A cell touching Chasm.</summary>
    public const string ChasmLip = "chasmLip";
}

public sealed class EndlessDressingContextProfile
{
    /// <summary>
    /// The SHARE of a chunk's dressing this context should receive, relative to the theme's other contexts and
    /// weighted by how many cells of each context the terrain actually offers. It is a proportion, not a
    /// priority: a theme that wants bare summits sets a low WallInterior share without losing its shorelines,
    /// and the open floor keeps its share even though a bank cell is individually the more eager host.
    /// </summary>
    public double density;
    /// <summary>
    /// `EndlessDressingVocabulary = Partial&lt;Record&lt;TerrainDecorationKind, number&gt;&gt;`: a weighted decoration
    /// vocabulary. Weights are relative within one table; zero entries are simply omitted. Insertion-ordered,
    /// because `Object.entries` walks it to build the draw tables.
    /// </summary>
    public JsMap<string, double> vocabulary = new();
    /// <summary>
    /// The vocabulary flattened into a draw table, resolved once when the registry is built.
    ///
    /// Selection runs once per candidate CELL — hundreds of times per streamed chunk on both the authoritative
    /// server and the client predictor — so walking `Object.entries` there would allocate an array and a pair of
    /// tuples per cell purely to read constant data.
    /// </summary>
    public IReadOnlyList<string> kinds = Array.Empty<string>();
    public double[] cumulative = Array.Empty<double>();
    /// <summary>
    /// The same table with the canopy removed and the remaining growth re-weighted toward UNDERSTORY — the
    /// vocabulary a COMPANION prop draws from.
    ///
    /// A bush at a tree's foot or an understory inside a thicket is the same world speaking with a quieter voice,
    /// so it must come from the cell's own context vocabulary. What it may never be is a second full-height
    /// object growing out of the first. The remaining weights are biased by UNDERSTORY_AFFINITY so scrub
    /// wins the companion slot over the stump of a felled tree.
    /// </summary>
    public IReadOnlyList<string> understoryKinds = Array.Empty<string>();
    public double[] understoryCumulative = Array.Empty<double>();
    /// <summary>
    /// Only the full-height slot: the table an OPEN-FIELD anchor draws from.
    ///
    /// The open plate's whole defect was that it received records but no silhouettes. A field anchor is placed
    /// precisely because that plate has nothing to read depth against, so drawing an ankle-high kind there would
    /// spend the slot and change nothing in the frame.
    /// </summary>
    public IReadOnlyList<string> canopyKinds = Array.Empty<string>();
    public double[] canopyCumulative = Array.Empty<double>();
}

public sealed class EndlessDressingTheme
{
    public string key = "";
    /// <summary>
    /// Target props per 1000 terrain cells before context and landscape scaling. A world aiming at the authored
    /// reference density lives in the 30..55 band, with drier or more open themes deliberately lower.
    /// </summary>
    public double density;
    /// <summary>
    /// Coherence scale (in cells) of the grove field. Small values scatter individual props; large values build
    /// readable stands and thickets with genuine clearings between them.
    /// </summary>
    public double groveScale;
    /// <summary>
    /// How strongly the grove field gates placement (0 = even scatter, 1 = only the densest grove cores dress).
    /// This is what makes a forest read as woodland rather than as wallpaper.
    /// </summary>
    public double groveContrast;
    /// <summary>
    /// Minimum cells between two props of the theme's canopy family. Lower packs stands tighter. Two crowns may
    /// never grow out of each other, so this is the one spacing rule the companion pass may not relax: an
    /// accepted prop can adopt neighbours, but only from the understory vocabulary.
    /// </summary>
    public double canopySpacing;
    /// <summary>`Record&lt;EndlessDressingContext, EndlessDressingContextProfile&gt;`, keyed by the context string.</summary>
    public JsMap<string, EndlessDressingContextProfile> contexts = new();
}

public static partial class EndlessDressing
{
    /// <summary>
    /// The one full-height slot.
    ///
    /// Kept as a predicate rather than an equality test at every call site because it answers a question the
    /// placement pass asks in three different places — "does this claim canopy spacing?", "may this stand beside
    /// that?", "is this what an empty plate needs?" — and those three must never be allowed to disagree.
    /// </summary>
    public static bool isTallDressingKind(string kind)
    {
        return kind == TerrainDecorationKind.Tree;
    }

    /// <summary>
    /// How eagerly each kind takes a COMPANION slot, relative to its ordinary weight in the same vocabulary.
    ///
    /// A companion's whole job is to sit at another prop's foot and read, at gameplay distance, as the same plant
    /// community. Scrub does that most readily; the stump of a felled tree reads as one too, just less often.
    /// Kinds absent from this table keep their weight unchanged.
    /// </summary>
    private static readonly Dictionary<string, double> UNDERSTORY_AFFINITY = new()
    {
        [TerrainDecorationKind.Thicket] = 2.1,
        // A stump is a rare woodland story beat, not the default short object whenever a canopy roll is demoted.
        [TerrainDecorationKind.Stump] = 0.38,
    };

    /// <summary>An object-literal vocabulary, in source (and therefore `Object.entries`) order.</summary>
    private static JsMap<string, double> vocabulary(params (string kind, double weight)[] entries)
    {
        var map = new JsMap<string, double>();
        foreach (var (kind, weight) in entries) map.set(kind, weight);
        return map;
    }

    private static EndlessDressingContextProfile profile(double density, JsMap<string, double> vocabulary)
    {
        var kinds = new List<string>();
        var weights = new List<double>();
        var understoryKinds = new List<string>();
        var understoryWeights = new List<double>();
        var canopyKinds = new List<string>();
        var canopyWeights = new List<double>();
        double running = 0;
        double understoryRunning = 0;
        double canopyRunning = 0;
        foreach (var (kind, weight) in vocabulary)
        {
            if (!(weight > 0)) continue;
            running += weight;
            kinds.push(kind);
            weights.push(running);
            if (isTallDressingKind(kind))
            {
                canopyRunning += weight;
                canopyKinds.push(kind);
                canopyWeights.push(canopyRunning);
                continue;
            }
            understoryRunning += weight * (UNDERSTORY_AFFINITY.TryGetValue(kind, out double affinity) ? affinity : 1);
            understoryKinds.push(kind);
            understoryWeights.push(understoryRunning);
        }
        return new EndlessDressingContextProfile
        {
            density = density,
            vocabulary = vocabulary,
            kinds = kinds,
            cumulative = weights.ToArray(),
            understoryKinds = understoryKinds,
            understoryCumulative = understoryWeights.ToArray(),
            canopyKinds = canopyKinds,
            canopyCumulative = canopyWeights.ToArray(),
        };
    }

    /// <summary>The `contexts` argument of <see cref="dressing"/>; only `wallInterior` may be omitted.</summary>
    private sealed class DressingContextsInput
    {
        public EndlessDressingContextProfile wallEdge = null!;
        public EndlessDressingContextProfile? wallInterior;
        public EndlessDressingContextProfile ground = null!;
        public EndlessDressingContextProfile waterBank = null!;
        public EndlessDressingContextProfile chasmLip = null!;
    }

    private sealed class DressingInput
    {
        public string key = "";
        public double density;
        public double? groveScale;
        public double? groveContrast;
        public double? canopySpacing;
        public DressingContextsInput contexts = null!;
    }

    /// <summary>
    /// Build a theme from its canopy-led ground/wall story plus the two hazard vocabularies. Every theme must name
    /// a canopy kind: a world without tall verticals reads as an empty corridor whatever else it dresses.
    /// </summary>
    private static EndlessDressingTheme dressing(DressingInput input)
    {
        var contexts = new JsMap<string, EndlessDressingContextProfile>();
        contexts.set(EndlessDressingContext.WallEdge, input.contexts.wallEdge);
        contexts.set(EndlessDressingContext.WallInterior, input.contexts.wallInterior ?? profile(0.45, input.contexts.wallEdge.vocabulary));
        contexts.set(EndlessDressingContext.Ground, input.contexts.ground);
        contexts.set(EndlessDressingContext.WaterBank, input.contexts.waterBank);
        contexts.set(EndlessDressingContext.ChasmLip, input.contexts.chasmLip);
        return new EndlessDressingTheme
        {
            key = input.key,
            density = input.density,
            groveScale = input.groveScale ?? 13,
            groveContrast = input.groveContrast ?? 0.55,
            canopySpacing = input.canopySpacing ?? 1.7,
            contexts = contexts,
        };
    }

    /// <summary>
    /// The neutral fallback: a temperate rock-and-woodland world. Unknown/modded theme keys inherit it, so a new
    /// run is dressed the moment it exists and only needs an entry here to gain its own face.
    /// </summary>
    public static readonly EndlessDressingTheme DEFAULT_ENDLESS_DRESSING = dressing(new DressingInput
    {
        key = "default",
        density = 45,
        contexts = new DressingContextsInput
        {
            wallEdge = profile(1.05, vocabulary(
                (TerrainDecorationKind.Tree, 6),
                (TerrainDecorationKind.Thicket, 3),
                (TerrainDecorationKind.Stump, 1))),
            wallInterior = profile(0.5, vocabulary(
                (TerrainDecorationKind.Tree, 4),
                (TerrainDecorationKind.Thicket, 2.4),
                (TerrainDecorationKind.Stump, 1.2))),
            ground = profile(1.1, vocabulary(
                (TerrainDecorationKind.Tree, 6),
                (TerrainDecorationKind.Thicket, 3),
                (TerrainDecorationKind.Stump, 1.4))),
            waterBank = profile(1.15, vocabulary(
                (TerrainDecorationKind.Thicket, 5),
                (TerrainDecorationKind.Tree, 3),
                (TerrainDecorationKind.Stump, 1.2))),
            chasmLip = profile(0.85, vocabulary(
                (TerrainDecorationKind.Thicket, 4),
                (TerrainDecorationKind.Stump, 1.6),
                (TerrainDecorationKind.Tree, 1.2))),
        },
    });

    /// <summary>
    /// The registered world themes. Each one carries its own density, grove coherence and canopy rhythm, so two
    /// worlds can never read as the same place in a different pigment. Insertion-ordered like the object literal
    /// (the offered world roster enumerates its keys).
    /// </summary>
    public static readonly JsMap<string, EndlessDressingTheme> ENDLESS_DRESSING_THEMES = new JsMap<string, EndlessDressingTheme>()
        /* Run 1 — alpine woodland. The reference density for the whole registry. */
        .set("highland_pass", dressing(new DressingInput
        {
            key = "highland_pass",
            density = 56,
            groveScale = 15,
            groveContrast = 0.5,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.05, vocabulary(
                    (TerrainDecorationKind.Tree, 8),
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.58, vocabulary(
                    (TerrainDecorationKind.Tree, 7),
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Stump, 1.2))),
                // The valley floor grows the same forest as the terrace above it. Canopy leads, so the field the colony
                // works in carries silhouettes and not only ankle-high scrub.
                ground = profile(1.15, vocabulary(
                    (TerrainDecorationKind.Tree, 7),
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Stump, 1.2))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 5),
                    (TerrainDecorationKind.Tree, 4),
                    (TerrainDecorationKind.Stump, 1.4))),
                chasmLip = profile(0.85, vocabulary(
                    (TerrainDecorationKind.Thicket, 4),
                    (TerrainDecorationKind.Stump, 2),
                    (TerrainDecorationKind.Tree, 1.5))),
            },
        }))
        /* Run 2 — wet street grid. Pruned, high-crowned street planting; sparse, spaced, deliberate. */
        .set("noir_sprawl", dressing(new DressingInput
        {
            key = "noir_sprawl",
            density = 51,
            groveScale = 9,
            groveContrast = 0.74,
            canopySpacing = 2.1,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.08, vocabulary(
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.35, vocabulary(
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 0.9),
                    (TerrainDecorationKind.Tree, 0.8))),
                // Street planting is the sprawl's vertical. A boulevard with no crowns above it was the "gravel yard"
                // read; the tree leads it now.
                ground = profile(1.12, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 2.2),
                    (TerrainDecorationKind.Stump, 1))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Tree, 2),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.9, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Stump, 1),
                    (TerrainDecorationKind.Tree, 0.8))),
            },
        }))
        /* Run 3 — high open borough. Sparse, ceremonial spacing; the cypress is its one vertical. */
        .set("olympian_sky_borough", dressing(new DressingInput
        {
            key = "olympian_sky_borough",
            density = 50,
            groveScale = 17,
            groveContrast = 0.72,
            canopySpacing = 2.2,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.05, vocabulary(
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Thicket, 1.5),
                    (TerrainDecorationKind.Stump, 0.9))),
                // The terrace body is laurel scrub and the stumps of felled olives.
                wallInterior = profile(0.4, vocabulary(
                    (TerrainDecorationKind.Thicket, 2.2),
                    (TerrainDecorationKind.Tree, 2),
                    (TerrainDecorationKind.Stump, 1))),
                ground = profile(1.12, vocabulary(
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Thicket, 1.2),
                    (TerrainDecorationKind.Stump, 0.9))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Tree, 2),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.85, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Stump, 1),
                    (TerrainDecorationKind.Tree, 0.9))),
            },
        }))
        /* Run 4 — temple garden under blossom. Dense, soft, water-led. */
        .set("sakura_temple_dream", dressing(new DressingInput
        {
            key = "sakura_temple_dream",
            density = 58,
            groveScale = 14,
            groveContrast = 0.46,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.08, vocabulary(
                    (TerrainDecorationKind.Tree, 8),
                    (TerrainDecorationKind.Thicket, 4),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.55, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 2.4),
                    (TerrainDecorationKind.Stump, 1))),
                ground = profile(1.18, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 4),
                    (TerrainDecorationKind.Stump, 1.2))),
                waterBank = profile(1.25, vocabulary(
                    (TerrainDecorationKind.Thicket, 5),
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.85, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.8),
                    (TerrainDecorationKind.Stump, 1.2),
                    (TerrainDecorationKind.Tree, 1))),
            },
        }))
        /* Run 5 — trench reef. Pressure-grown trees with few thick sprawling limbs. */
        .set("abyssal_deepsea", dressing(new DressingInput
        {
            key = "abyssal_deepsea",
            density = 54,
            groveScale = 11,
            groveContrast = 0.58,
            canopySpacing = 1.5,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.08, vocabulary(
                    (TerrainDecorationKind.Tree, 7),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1.2))),
                wallInterior = profile(0.5, vocabulary(
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1.1))),
                ground = profile(1.15, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Stump, 0.8))),
                waterBank = profile(1.25, vocabulary(
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Thicket, 4),
                    (TerrainDecorationKind.Stump, 1.4))),
                chasmLip = profile(0.95, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.8),
                    (TerrainDecorationKind.Tree, 2),
                    (TerrainDecorationKind.Stump, 1))),
            },
        }))
        /* Run 6 — prism realm. Loud, many-limbed crowns. */
        .set("rainbowland", dressing(new DressingInput
        {
            key = "rainbowland",
            density = 54,
            groveScale = 12,
            groveContrast = 0.44,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.05, vocabulary(
                    (TerrainDecorationKind.Tree, 7),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.55, vocabulary(
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 0.9))),
                ground = profile(1.15, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Stump, 1))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 5),
                    (TerrainDecorationKind.Tree, 4),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.9, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Tree, 1.4),
                    (TerrainDecorationKind.Stump, 0.9))),
            },
        }))
        /* Run 7 — night market country. Sparse growth between the stalls. */
        .set("clockwork_moon_bazaar", dressing(new DressingInput
        {
            key = "clockwork_moon_bazaar",
            density = 54,
            groveScale = 8.5,
            groveContrast = 0.76,
            canopySpacing = 1.9,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.08, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.4, vocabulary(
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Tree, 1.2),
                    (TerrainDecorationKind.Stump, 1))),
                ground = profile(1.15, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 2.6),
                    (TerrainDecorationKind.Tree, 2),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.9, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Stump, 1),
                    (TerrainDecorationKind.Tree, 0.8))),
            },
        }))
        /* Run 8 — fairground country. Fat sugar-crowned trees; density is the point. */
        .set("sugarstorm_carnival", dressing(new DressingInput
        {
            key = "sugarstorm_carnival",
            density = 56,
            groveScale = 10,
            groveContrast = 0.5,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.08, vocabulary(
                    (TerrainDecorationKind.Tree, 7),
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.5, vocabulary(
                    (TerrainDecorationKind.Tree, 5),
                    (TerrainDecorationKind.Thicket, 2.2),
                    (TerrainDecorationKind.Stump, 1))),
                ground = profile(1.18, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 3.5),
                    (TerrainDecorationKind.Stump, 0.8))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 5),
                    (TerrainDecorationKind.Tree, 4),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.88, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Tree, 1.2),
                    (TerrainDecorationKind.Stump, 0.9))),
            },
        }))
        /* Run 9 — mirrored archive country. Rigid tiered crowns, spaced. */
        .set("prismglass_archive", dressing(new DressingInput
        {
            key = "prismglass_archive",
            density = 53,
            groveScale = 9.5,
            groveContrast = 0.72,
            canopySpacing = 2,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.08, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.45, vocabulary(
                    (TerrainDecorationKind.Tree, 3),
                    (TerrainDecorationKind.Thicket, 1.8),
                    (TerrainDecorationKind.Stump, 1))),
                ground = profile(1.12, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 1.2),
                    (TerrainDecorationKind.Stump, 1))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Tree, 3),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.95, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Tree, 1.2),
                    (TerrainDecorationKind.Stump, 0.9))),
            },
        }))
        /* Run 10 — cathedral country. Tall bare shafts opening into vaulted crowns. */
        .set("starforged_cathedral_endrun", dressing(new DressingInput
        {
            key = "starforged_cathedral_endrun",
            density = 50,
            groveScale = 16,
            groveContrast = 0.74,
            canopySpacing = 2.3,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.05, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 1.2),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.4, vocabulary(
                    (TerrainDecorationKind.Tree, 3),
                    (TerrainDecorationKind.Thicket, 1.8),
                    (TerrainDecorationKind.Stump, 1))),
                ground = profile(1.12, vocabulary(
                    (TerrainDecorationKind.Tree, 5.5),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                waterBank = profile(1.15, vocabulary(
                    (TerrainDecorationKind.Tree, 3.5),
                    (TerrainDecorationKind.Thicket, 2.4),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.9, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Tree, 1.2),
                    (TerrainDecorationKind.Stump, 0.9))),
            },
        }))
        /* Generator-only preview theme; kept dressed so the map tool shows a real world. */
        .set("alien_ranch", dressing(new DressingInput
        {
            key = "alien_ranch",
            density = 43,
            groveScale = 13,
            groveContrast = 0.6,
            contexts = new DressingContextsInput
            {
                wallEdge = profile(1.05, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                wallInterior = profile(0.45, vocabulary(
                    (TerrainDecorationKind.Tree, 3),
                    (TerrainDecorationKind.Thicket, 2),
                    (TerrainDecorationKind.Stump, 1))),
                ground = profile(1.12, vocabulary(
                    (TerrainDecorationKind.Tree, 6),
                    (TerrainDecorationKind.Thicket, 3),
                    (TerrainDecorationKind.Stump, 1))),
                waterBank = profile(1.2, vocabulary(
                    (TerrainDecorationKind.Thicket, 5),
                    (TerrainDecorationKind.Tree, 3),
                    (TerrainDecorationKind.Stump, 1.2))),
                chasmLip = profile(0.9, vocabulary(
                    (TerrainDecorationKind.Thicket, 1.6),
                    (TerrainDecorationKind.Tree, 1.2),
                    (TerrainDecorationKind.Stump, 0.9))),
            },
        }));

    /// <summary>
    /// Themes that exist as content but are NOT part of the offered world roster: they render fully when a
    /// caller names them explicitly (`theme:alien_ranch`), and are skipped when something enumerates the
    /// worlds a player is offered. Stated here, beside the registry, so the roster stays one derivation.
    /// </summary>
    public static readonly JsSet<string> GENERATOR_ONLY_DRESSING_THEMES = new(new[] { "alien_ranch" });

    /// <summary>The dressing DNA for a world key — never undefined, so placement code needs no fallback branch.</summary>
    public static EndlessDressingTheme endlessDressingFor(string biomeKey)
    {
        return ENDLESS_DRESSING_THEMES.get(biomeKey) ?? DEFAULT_ENDLESS_DRESSING;
    }

    /// <summary>THE weighted draw. Every vocabulary uses it, so the tables can never grow two selection rules.</summary>
    private static string? drawKind(IReadOnlyList<string> kinds, double[] cumulative, double roll)
    {
        int count = kinds.Count;
        if (count == 0) return null;
        double cursor = roll * cumulative[count - 1];
        for (int slot = 0; slot < count; slot++)
        {
            if (cursor <= cumulative[slot]) return kinds[slot];
        }
        return kinds[count - 1];
    }

    /// <summary>
    /// Draw a decoration kind for one cell from a context's vocabulary. `roll` must be a uniform [0,1) sample that
    /// varies per cell — the whole point of the table is that neighbouring cells disagree.
    /// </summary>
    public static string endlessDressingKindAt(EndlessDressingTheme theme, string context, double roll)
    {
        EndlessDressingContextProfile profile = theme.contexts[context];
        return drawKind(profile.kinds, profile.cumulative, roll) ?? TerrainDecorationKind.Thicket;
    }

    /// <summary>
    /// Draw the kind a COMPANION prop takes on this cell: the same context vocabulary with the canopy removed.
    ///
    /// A context that names no growth at all borrows the theme's GROUND growth rather than its own canopy — a
    /// companion beside a crown may never be a second crown. `null` means the theme states no growth anywhere,
    /// in which case the pass leaves the cell empty instead of inventing a plant the fiction never authored.
    /// </summary>
    public static string? endlessDressingUnderstoryKindAt(EndlessDressingTheme theme, string context, double roll)
    {
        EndlessDressingContextProfile local = theme.contexts[context];
        EndlessDressingContextProfile ground = theme.contexts[EndlessDressingContext.Ground];
        return
            drawKind(local.understoryKinds, local.understoryCumulative, roll) ??
            drawKind(ground.understoryKinds, ground.understoryCumulative, roll);
    }
}
