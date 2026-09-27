// Port of packages/shared/src/domain/dungeon/endlessLandscape.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.WaveFunctionCollapse;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Regional landscape composition for Endless runs.
//
// Section grammars decide the encounter rhythm (warrens, chambers, courts, ...). This module owns a separate
// perceptual layer: the large-scale combination of water, rock, ravines, islands and landmark silhouettes.
// Every chunk activates exactly two or three settings. Slow global fields keep a setting readable for several
// chunks, while normalized blends let neighbouring regions transition without a hard preset boundary.
//
// The registry is pure data and the sampler is a pure function of `(seed, chunk coordinate)`. Both runtimes can
// therefore regenerate the same composition without protocol state, persistence or `Math.random()`.

public static class EndlessLandscapeKind
{
    public const string AlpinePass = "alpine_pass";
    public const string GrandCanyon = "grand_canyon";
    public const string GreatLake = "great_lake";
    public const string IslandLake = "island_lake";
    public const string FracturedConfluence = "fractured_confluence";
    public const string CascadeTerraces = "cascade_terraces";
    public const string CalderaLake = "caldera_lake";
    public const string Archipelago = "archipelago";
    public const string RiverDelta = "river_delta";
    public const string SinkholeKarst = "sinkhole_karst";
    public const string RidgeMaze = "ridge_maze";
    public const string StoneForest = "stone_forest";
    public const string MesaBadlands = "mesa_badlands";
    public const string FloodedCaverns = "flooded_caverns";
    public const string HighlandMoor = "highland_moor";
    public const string GlacialFjord = "glacial_fjord";
    public const string OasisBasins = "oasis_basins";
    public const string RuinedCauseways = "ruined_causeways";
}

/// <summary>Continuous generator controls. Signed fields are deliberately bounded so arbitrary 2-3-way blends remain safe.</summary>
public sealed class EndlessLandscapeTraits
{
    /// <summary>Opens or closes the residual macro maze before water/ravines are placed.</summary>
    public double opennessBias;
    /// <summary>Raises coherent wall and outcrop country away from the guaranteed route skeleton.</summary>
    public double rockDensity;
    /// <summary>Slow normalized-height offset. Lakes/deltas settle into low country; ridges/mesas claim high shelves.</summary>
    public double altitudeBias;
    /// <summary>Contrast around the middle of the walkable height field (1 = neutral).</summary>
    public double reliefContrast;
    /// <summary>Strength of folded mid/fine-scale relief inside a broad height country (0 = calm, 1 = violently folded).</summary>
    public double reliefRuggedness;
    /// <summary>Strength of solid terrace rims outside protected routes (0 = rolling, 1 = pronounced escarpments).</summary>
    public double escarpmentStrength;
    /// <summary>Maximum desired walkable share before Water/Chasm are composed. This is a composition target, not a cut.</summary>
    public double openGroundCap;
    /// <summary>Desired frequency of visually explicit bridge landmarks; other required crossings become stone causeways.</summary>
    public double bridgeLandmarkDensity;
    /// <summary>Widens/narrows authored channels.</summary>
    public double waterBias;
    /// <summary>Adds large organic lake basins independently of the cohort's river grammar.</summary>
    public double lakeStrength;
    /// <summary>Adds secondary contour channels and distributaries.</summary>
    public double channelComplexity;
    /// <summary>Retains coherent dry rock/ground islands inside large water bodies.</summary>
    public double islandStrength;
    /// <summary>Strength of the native Chasm field.</summary>
    public double chasmStrength;
    /// <summary>Favors basin-shaped pits over only long rift contours.</summary>
    public double chasmBasins;
    /// <summary>Favors long, strong ravine cores and permits a larger readable aperture.</summary>
    public double riftStrength;
    /// <summary>Couples river/lake edges to ravines.</summary>
    public double waterChasmAffinity;
    /// <summary>Extends coherent Water-to-Chasm lips so the renderer can materialize occasional falls.</summary>
    public double waterfallAffinity;
    /// <summary>Keeps the landscape tight around the &gt;=3-tile protected route country.</summary>
    public double pathConfinement;
    /// <summary>Adds broken columns/cover masses off the route skeleton.</summary>
    public double pillarDensity;
    /// <summary>Adds organic bends to authored port approaches.</summary>
    public double pathWander;

    /// <summary>Shallow copy — the `{ ...traits }` object spread.</summary>
    public EndlessLandscapeTraits Clone() => (EndlessLandscapeTraits)MemberwiseClone();

    public const int KEY_COUNT = 20;

    /// <summary>`traits[key]` by position in <c>KEYS</c> (an int switch instead of a string lookup).</summary>
    public double this[int key]
    {
        get
        {
            switch (key)
            {
                case 0: return altitudeBias;
                case 1: return reliefContrast;
                case 2: return reliefRuggedness;
                case 3: return escarpmentStrength;
                case 4: return openGroundCap;
                case 5: return bridgeLandmarkDensity;
                case 6: return waterBias;
                case 7: return lakeStrength;
                case 8: return channelComplexity;
                case 9: return islandStrength;
                case 10: return chasmStrength;
                case 11: return chasmBasins;
                case 12: return riftStrength;
                case 13: return waterChasmAffinity;
                case 14: return waterfallAffinity;
                case 15: return pathConfinement;
                case 16: return pillarDensity;
                case 17: return pathWander;
                case 18: return opennessBias;
                case 19: return rockDensity;
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
        }
        set
        {
            switch (key)
            {
                case 0: altitudeBias = value; break;
                case 1: reliefContrast = value; break;
                case 2: reliefRuggedness = value; break;
                case 3: escarpmentStrength = value; break;
                case 4: openGroundCap = value; break;
                case 5: bridgeLandmarkDensity = value; break;
                case 6: waterBias = value; break;
                case 7: lakeStrength = value; break;
                case 8: channelComplexity = value; break;
                case 9: islandStrength = value; break;
                case 10: chasmStrength = value; break;
                case 11: chasmBasins = value; break;
                case 12: riftStrength = value; break;
                case 13: waterChasmAffinity = value; break;
                case 14: waterfallAffinity = value; break;
                case 15: pathConfinement = value; break;
                case 16: pillarDensity = value; break;
                case 17: pathWander = value; break;
                case 18: opennessBias = value; break;
                case 19: rockDensity = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
        }
    }
}

public sealed class EndlessLandscapeDefinition
{
    public string kind = "";
    /// <summary>Independent field salt.</summary>
    public uint salt;
    /// <summary>Coherence scale in chunks.</summary>
    public double scale;
    public EndlessLandscapeTraits traits;
    /// <summary>
    /// Existing signed-field archetypes which should materialize especially clearly in this setting
    /// (`Partial&lt;Record&lt;Exclude&lt;MacroStyle, 'labyrinth'&gt;, number&gt;&gt;`; lookup only).
    /// </summary>
    public Dictionary<string, double> macroAffinities;
}

public sealed class EndlessLandscapeInfluence
{
    public string kind = "";
    public double weight;
}

public sealed class EndlessLandscapeSample
{
    public string dominant = "";
    /// <summary>Exactly two or three normalized influences, ordered strongest first.</summary>
    public IReadOnlyList<EndlessLandscapeInfluence> active;
    /// <summary>
    /// Smooth physical blend over the full registry. Non-active settings normally contribute only a small tail;
    /// retaining that tail prevents geometry controls from jumping when the third-ranked identity changes.
    /// (`Record&lt;EndlessLandscapeKind, number&gt;` built by `Object.fromEntries` — keys in ranked order.)
    /// </summary>
    public JsMap<string, double> weights;
    public double dominantStrength;
    /// <summary>High where the two strongest settings are balanced.</summary>
    public double transition;
    public EndlessLandscapeTraits traits;
}

/// <summary>
/// One semantic Country has one physical landscape grammar and, occasionally, one restrained supporting motif.
/// It deliberately excludes the low-weight tails used by the legacy chunk transition sampler.
/// </summary>
public sealed class EndlessCountryLandscape
{
    public string primary = "";
    public string? support;
    public double supportWeight;
    public EndlessLandscapeTraits traits;
}

/// <summary>
/// V4's high-level composition DNA. Affinities are soft rather than exclusive: every physical grammar remains
/// reachable, while each authored run identity develops a recognisable regional silhouette. `chapterStrength`
/// controls broad 12-24-Country swings and `wildcardStrength` admits rare, much sharper counter-regions.
/// </summary>
public sealed class EndlessBiomeCompositionDna
{
    public double baseAffinity;
    public double contrast;
    public double chapterScale;
    public double chapterStrength;
    public double wildcardStrength;
    public double supportRate;
    /// <summary>`Readonly&lt;Partial&lt;Record&lt;EndlessLandscapeKind, number&gt;&gt;&gt;`; lookup only.</summary>
    public IReadOnlyDictionary<string, double> affinities;
}

public static partial class EndlessLandscape
{
    private static EndlessLandscapeTraits traits(
        double opennessBias,
        double rockDensity,
        double altitudeBias = 0,
        double reliefContrast = 1,
        double reliefRuggedness = 0.78,
        double escarpmentStrength = 0.45,
        double openGroundCap = 0.56,
        double bridgeLandmarkDensity = 0.4,
        double waterBias = 0,
        double lakeStrength = 0,
        double channelComplexity = 0,
        double islandStrength = 0,
        double chasmStrength = 0,
        double chasmBasins = 0,
        double riftStrength = 0,
        double waterChasmAffinity = 0,
        double waterfallAffinity = 0,
        double pathConfinement = 0,
        double pillarDensity = 0,
        double pathWander = 0.5)
    {
        return new EndlessLandscapeTraits
        {
            altitudeBias = altitudeBias,
            reliefContrast = reliefContrast,
            reliefRuggedness = reliefRuggedness,
            escarpmentStrength = escarpmentStrength,
            openGroundCap = openGroundCap,
            bridgeLandmarkDensity = bridgeLandmarkDensity,
            waterBias = waterBias,
            lakeStrength = lakeStrength,
            channelComplexity = channelComplexity,
            islandStrength = islandStrength,
            chasmStrength = chasmStrength,
            chasmBasins = chasmBasins,
            riftStrength = riftStrength,
            waterChasmAffinity = waterChasmAffinity,
            waterfallAffinity = waterfallAffinity,
            pathConfinement = pathConfinement,
            pillarDensity = pillarDensity,
            pathWander = pathWander,
            opennessBias = opennessBias,
            rockDensity = rockDensity,
        };
    }

    /// <summary>
    /// Eighteen composable landscape settings. The first five are the explicitly requested signatures; the
    /// remaining thirteen provide equally concrete counter-rhythms instead of generic noise variations.
    /// </summary>
    public static readonly IReadOnlyList<EndlessLandscapeDefinition> ENDLESS_LANDSCAPE_DEFINITIONS = new EndlessLandscapeDefinition[]
    {
        new()
        {
            kind = EndlessLandscapeKind.AlpinePass,
            salt = 0x243f6a88,
            scale = 9.6,
            traits = traits(
                opennessBias: -0.34,
                rockDensity: 0.82,
                altitudeBias: 0.1,
                reliefContrast: 1.18,
                reliefRuggedness: 0.94,
                escarpmentStrength: 0.82,
                openGroundCap: 0.46,
                bridgeLandmarkDensity: 0.25,
                waterBias: -0.08,
                lakeStrength: 0.12,
                chasmStrength: 0.2,
                riftStrength: 0.36,
                waterChasmAffinity: 0.42,
                waterfallAffinity: 0.92,
                pathConfinement: 0.9,
                pillarDensity: 0.18,
                pathWander: 0.72),
            macroAffinities = new() { ["switchback_ridge"] = 1, ["dune_ridges"] = 0.65, ["gorge_network"] = 0.72 },
        },
        new()
        {
            kind = EndlessLandscapeKind.GrandCanyon,
            salt = 0x85a308d3,
            scale = 10.8,
            traits = traits(
                opennessBias: -0.08,
                rockDensity: 0.38,
                altitudeBias: 0.04,
                reliefContrast: 1.3,
                reliefRuggedness: 1,
                escarpmentStrength: 1,
                openGroundCap: 0.5,
                bridgeLandmarkDensity: 0.15,
                waterBias: -0.2,
                chasmStrength: 0.95,
                chasmBasins: 0.85,
                riftStrength: 1,
                waterChasmAffinity: 0.24,
                waterfallAffinity: 0.38,
                pathConfinement: 0.5,
                pathWander: 0.88),
            macroAffinities = new() { ["chasm_rift"] = 1, ["canyon_web"] = 0.9, ["gorge_network"] = 0.82, ["terraces_fan"] = 0.55 },
        },
        new()
        {
            kind = EndlessLandscapeKind.GreatLake,
            salt = 0x13198a2e,
            scale = 11.8,
            traits = traits(
                opennessBias: 0.64,
                rockDensity: -0.42,
                altitudeBias: -0.16,
                reliefContrast: 0.82,
                reliefRuggedness: 0.72,
                // The open water is the landmark here. Folded walkable relief still creates island/shore steps, while a
                // deliberately sparse solid rim keeps the hydrology pass from inheriting a pre-filled lake basin.
                escarpmentStrength: 0.06,
                openGroundCap: 0.7,
                bridgeLandmarkDensity: 0.35,
                waterBias: 1,
                lakeStrength: 1.2,
                islandStrength: 0.08,
                chasmStrength: -0.32,
                pathWander: 0.42),
            macroAffinities = new() { ["lake_isles"] = 0.88, ["amphitheatre"] = 0.72, ["plaza"] = 0.62 },
        },
        new()
        {
            kind = EndlessLandscapeKind.IslandLake,
            salt = 0x03707344,
            scale = 10.4,
            traits = traits(
                opennessBias: 0.42,
                rockDensity: -0.08,
                altitudeBias: -0.1,
                reliefContrast: 0.9,
                reliefRuggedness: 0.8,
                escarpmentStrength: 0.35,
                openGroundCap: 0.62,
                bridgeLandmarkDensity: 0.3,
                waterBias: 0.8,
                lakeStrength: 0.97,
                islandStrength: 1,
                chasmStrength: -0.18,
                pillarDensity: 0.12,
                pathWander: 0.62),
            macroAffinities = new() { ["lake_isles"] = 1, ["moat_island"] = 0.9, ["basalt_columns"] = 0.36 },
        },
        new()
        {
            kind = EndlessLandscapeKind.FracturedConfluence,
            salt = 0xa4093822,
            scale = 8.7,
            traits = traits(
                opennessBias: 0.08,
                rockDensity: 0.18,
                altitudeBias: -0.02,
                reliefContrast: 1.15,
                reliefRuggedness: 0.98,
                escarpmentStrength: 0.78,
                // Preserve enough basin country for the braided confluence before Chasm mouths cut through it.
                openGroundCap: 0.58,
                bridgeLandmarkDensity: 0.35,
                waterBias: 0.52,
                lakeStrength: 0.42,
                channelComplexity: 0.68,
                islandStrength: 0.18,
                chasmStrength: 0.82,
                chasmBasins: 0.38,
                riftStrength: 0.8,
                waterChasmAffinity: 1,
                waterfallAffinity: 0.86,
                pathConfinement: 0.32,
                pathWander: 0.9),
            macroAffinities = new() { ["chasm_rift"] = 1, ["gorge_network"] = 0.92, ["canyon_web"] = 0.84, ["moat_island"] = 0.5 },
        },
        new()
        {
            kind = EndlessLandscapeKind.CascadeTerraces,
            salt = 0x299f31d0,
            scale = 9.3,
            traits = traits(
                opennessBias: 0.24,
                rockDensity: 0.16,
                altitudeBias: 0.06,
                reliefContrast: 1.25,
                reliefRuggedness: 1,
                escarpmentStrength: 1,
                openGroundCap: 0.56,
                bridgeLandmarkDensity: 0.45,
                waterBias: 0.3,
                lakeStrength: 0.2,
                channelComplexity: 0.2,
                chasmStrength: 0.24,
                riftStrength: 0.2,
                waterChasmAffinity: 0.72,
                waterfallAffinity: 1,
                pathWander: 0.68),
            macroAffinities = new()
            {
                ["terraces_fan"] = 1,
                ["amphitheatre"] = 0.7,
                ["plateau_steppe"] = 0.62,
                ["gorge_network"] = 0.58,
            },
        },
        new()
        {
            kind = EndlessLandscapeKind.CalderaLake,
            salt = 0x082efa98,
            scale = 11.2,
            traits = traits(
                opennessBias: 0.2,
                rockDensity: 0.38,
                altitudeBias: -0.05,
                reliefContrast: 1.2,
                reliefRuggedness: 0.96,
                escarpmentStrength: 0.86,
                openGroundCap: 0.56,
                bridgeLandmarkDensity: 0.35,
                waterBias: 0.42,
                lakeStrength: 0.72,
                islandStrength: 0.32,
                chasmStrength: 0.42,
                chasmBasins: 1,
                riftStrength: 0.14,
                waterChasmAffinity: 0.6,
                waterfallAffinity: 0.5,
                pathConfinement: 0.24),
            macroAffinities = new() { ["crater_field"] = 1, ["moat_island"] = 0.95, ["ring_fort"] = 0.7, ["amphitheatre"] = 0.58 },
        },
        new()
        {
            kind = EndlessLandscapeKind.Archipelago,
            salt = 0xec4e6c89,
            scale = 12.2,
            traits = traits(
                opennessBias: 0.38,
                rockDensity: 0.04,
                altitudeBias: -0.12,
                reliefContrast: 0.95,
                reliefRuggedness: 0.84,
                escarpmentStrength: 0.35,
                openGroundCap: 0.58,
                bridgeLandmarkDensity: 0.3,
                waterBias: 0.68,
                lakeStrength: 0.8,
                channelComplexity: 0.34,
                islandStrength: 1,
                chasmStrength: -0.14,
                pillarDensity: 0.18,
                pathWander: 0.72),
            macroAffinities = new() { ["lake_isles"] = 1, ["basalt_columns"] = 0.62, ["moat_island"] = 0.74 },
        },
        new()
        {
            kind = EndlessLandscapeKind.RiverDelta,
            salt = 0x452821e6,
            scale = 9.8,
            traits = traits(
                opennessBias: 0.48,
                rockDensity: -0.22,
                altitudeBias: -0.18,
                reliefContrast: 0.78,
                reliefRuggedness: 0.68,
                escarpmentStrength: 0.14,
                openGroundCap: 0.62,
                bridgeLandmarkDensity: 0.25,
                waterBias: 0.58,
                lakeStrength: 0.46,
                channelComplexity: 1,
                islandStrength: 0.72,
                chasmStrength: -0.12,
                pathWander: 0.82),
            macroAffinities = new() { ["canyon_web"] = 1, ["gorge_network"] = 0.92, ["lake_isles"] = 0.7, ["cave_warren"] = 0.42 },
        },
        new()
        {
            kind = EndlessLandscapeKind.SinkholeKarst,
            salt = 0x38d01377,
            scale = 8.4,
            traits = traits(
                opennessBias: -0.04,
                rockDensity: 0.34,
                altitudeBias: -0.05,
                reliefContrast: 1.15,
                reliefRuggedness: 0.94,
                escarpmentStrength: 0.72,
                openGroundCap: 0.48,
                bridgeLandmarkDensity: 0.2,
                waterBias: 0.08,
                lakeStrength: 0.16,
                islandStrength: 0.08,
                chasmStrength: 0.82,
                chasmBasins: 1,
                riftStrength: 0.18,
                waterChasmAffinity: 0.32,
                waterfallAffinity: 0.28,
                pathConfinement: 0.3,
                pillarDensity: 0.24,
                pathWander: 0.84),
            macroAffinities = new() { ["sinkhole_cluster"] = 1, ["cave_warren"] = 0.88, ["crater_field"] = 0.76 },
        },
        new()
        {
            kind = EndlessLandscapeKind.RidgeMaze,
            salt = 0xbe5466cf,
            scale = 8.9,
            traits = traits(
                opennessBias: -0.38,
                rockDensity: 0.9,
                altitudeBias: 0.14,
                reliefContrast: 1.28,
                reliefRuggedness: 1,
                escarpmentStrength: 0.95,
                openGroundCap: 0.42,
                bridgeLandmarkDensity: 0.1,
                waterBias: -0.28,
                chasmStrength: 0.18,
                riftStrength: 0.28,
                pathConfinement: 1,
                pillarDensity: 0.18,
                pathWander: 1),
            macroAffinities = new()
            {
                ["switchback_ridge"] = 1,
                ["dune_ridges"] = 0.94,
                ["hedge_spiral"] = 0.82,
                ["rubble_maze"] = 0.52,
            },
        },
        new()
        {
            kind = EndlessLandscapeKind.StoneForest,
            salt = 0x34e90c6c,
            scale = 9.1,
            traits = traits(
                opennessBias: 0.08,
                rockDensity: 0.62,
                altitudeBias: 0.08,
                reliefContrast: 1.12,
                reliefRuggedness: 0.92,
                escarpmentStrength: 0.58,
                openGroundCap: 0.46,
                bridgeLandmarkDensity: 0.15,
                waterBias: -0.12,
                chasmStrength: 0.08,
                pathConfinement: 0.28,
                pillarDensity: 1,
                pathWander: 0.76),
            macroAffinities = new() { ["basalt_columns"] = 1, ["obelisk_field"] = 0.94, ["colonnade_avenue"] = 0.72 },
        },
        new()
        {
            kind = EndlessLandscapeKind.MesaBadlands,
            salt = 0xc0ac29b7,
            scale = 10.5,
            traits = traits(
                opennessBias: 0.02,
                rockDensity: 0.7,
                altitudeBias: 0.12,
                reliefContrast: 1.3,
                reliefRuggedness: 1,
                escarpmentStrength: 1,
                openGroundCap: 0.48,
                bridgeLandmarkDensity: 0.15,
                waterBias: -0.36,
                chasmStrength: 0.46,
                chasmBasins: 0.38,
                riftStrength: 0.52,
                pathConfinement: 0.35,
                pillarDensity: 0.3,
                pathWander: 0.7),
            macroAffinities = new() { ["mesa_cluster"] = 1, ["dune_ridges"] = 0.82, ["canyon_web"] = 0.74, ["crater_field"] = 0.5 },
        },
        new()
        {
            kind = EndlessLandscapeKind.FloodedCaverns,
            salt = 0xc97c50dd,
            scale = 9.4,
            traits = traits(
                opennessBias: 0.48,
                rockDensity: 0.18,
                altitudeBias: -0.12,
                reliefContrast: 1,
                reliefRuggedness: 0.84,
                escarpmentStrength: 0.42,
                openGroundCap: 0.55,
                bridgeLandmarkDensity: 0.25,
                waterBias: 0.54,
                lakeStrength: 0.62,
                channelComplexity: 0.35,
                islandStrength: 0.42,
                chasmStrength: 0.16,
                chasmBasins: 0.28,
                waterChasmAffinity: 0.36,
                waterfallAffinity: 0.32,
                pathWander: 0.94),
            macroAffinities = new() { ["cave_warren"] = 1, ["lake_isles"] = 0.82, ["sinkhole_cluster"] = 0.62 },
        },
        new()
        {
            kind = EndlessLandscapeKind.HighlandMoor,
            salt = 0x3f84d5b5,
            scale = 12.6,
            traits = traits(
                opennessBias: 0.72,
                rockDensity: -0.38,
                altitudeBias: 0.05,
                reliefContrast: 0.86,
                reliefRuggedness: 0.76,
                escarpmentStrength: 0.28,
                openGroundCap: 0.68,
                bridgeLandmarkDensity: 0.25,
                waterBias: 0.2,
                lakeStrength: 0.34,
                channelComplexity: 0.18,
                islandStrength: 0.1,
                chasmStrength: -0.18,
                pathWander: 0.52),
            macroAffinities = new() { ["plateau_steppe"] = 1, ["plaza"] = 0.78, ["terraces_fan"] = 0.54 },
        },
        new()
        {
            kind = EndlessLandscapeKind.GlacialFjord,
            salt = 0xb5470917,
            scale = 11.4,
            traits = traits(
                opennessBias: 0.04,
                rockDensity: 0.68,
                altitudeBias: -0.02,
                reliefContrast: 1.25,
                reliefRuggedness: 1,
                escarpmentStrength: 0.92,
                openGroundCap: 0.48,
                bridgeLandmarkDensity: 0.35,
                waterBias: 0.52,
                lakeStrength: 0.58,
                channelComplexity: 0.2,
                islandStrength: 0.22,
                chasmStrength: 0.52,
                chasmBasins: 0.18,
                riftStrength: 0.72,
                waterChasmAffinity: 0.9,
                waterfallAffinity: 0.78,
                pathConfinement: 0.58,
                pathWander: 0.68),
            macroAffinities = new()
            {
                ["chasm_rift"] = 0.92,
                ["bastion_bar"] = 0.72,
                ["gorge_network"] = 1,
                ["switchback_ridge"] = 0.5,
            },
        },
        new()
        {
            kind = EndlessLandscapeKind.OasisBasins,
            salt = 0x9216d5d9,
            scale = 10.7,
            traits = traits(
                opennessBias: 0.38,
                rockDensity: 0.18,
                altitudeBias: -0.08,
                reliefContrast: 1.1,
                reliefRuggedness: 0.9,
                escarpmentStrength: 0.68,
                openGroundCap: 0.58,
                bridgeLandmarkDensity: 0.25,
                waterBias: 0.24,
                lakeStrength: 0.68,
                islandStrength: 0.3,
                chasmStrength: 0.08,
                chasmBasins: 0.5,
                waterChasmAffinity: 0.24,
                waterfallAffinity: 0.18,
                pathWander: 0.58),
            macroAffinities = new() { ["amphitheatre"] = 1, ["crater_field"] = 0.82, ["moat_island"] = 0.72, ["plaza"] = 0.54 },
        },
        new()
        {
            kind = EndlessLandscapeKind.RuinedCauseways,
            salt = 0x8979fb1b,
            scale = 9.7,
            traits = traits(
                opennessBias: 0.18,
                rockDensity: 0.42,
                altitudeBias: 0.02,
                reliefContrast: 1.05,
                reliefRuggedness: 0.88,
                escarpmentStrength: 0.6,
                openGroundCap: 0.5,
                bridgeLandmarkDensity: 0.35,
                waterBias: 0.18,
                lakeStrength: 0.16,
                channelComplexity: 0.2,
                islandStrength: 0.42,
                chasmStrength: 0.3,
                chasmBasins: 0.16,
                riftStrength: 0.28,
                waterChasmAffinity: 0.36,
                waterfallAffinity: 0.24,
                pathConfinement: 0.2,
                pillarDensity: 0.86,
                pathWander: 0.64),
            macroAffinities = new() { ["colonnade_avenue"] = 1, ["rubble_maze"] = 0.92, ["star_fort"] = 0.6, ["plaza_spiral"] = 0.72 },
        },
    };

    private const uint COMPOSITION_COUNT_SALT = 0x6a09e667;
    private const uint DETAIL_SALT = 0xbb67ae85;
    private const uint COHORT_SALT = 0x3c6ef372;

    private static double clamp01(double value)
    {
        return value < 0 ? 0 : value > 1 ? 1 : value;
    }

    private sealed class RankedLandscape
    {
        public string kind = "";
        public double raw;
    }

    // Every `(seed ^ salt) >>> 0` below is `Js.ToUint32(seed) ^ salt`: ToUint32 and ToInt32 share their low 32
    // bits, XOR is bitwise, and `>>> 0` reinterprets the result as unsigned — the same uint32 value.

    /// <summary>`b.raw - a.raw || a.kind.localeCompare(b.kind)` — a zero (or NaN) difference falls to the key order.</summary>
    private static double compareRanked(RankedLandscape a, RankedLandscape b)
    {
        double difference = b.raw - a.raw;
        return Js.Truthy(difference) ? difference : string.CompareOrdinal(a.kind, b.kind);
    }

    private static List<RankedLandscape> rankedLandscapePotentials(double seed, double cx, double cy)
    {
        List<RankedLandscape> ranked = ENDLESS_LANDSCAPE_DEFINITIONS.map(definition =>
        {
            double broad = valueNoise(Js.ToUint32(seed) ^ definition.salt, cx, cy, definition.scale * 2.15);
            double detail = valueNoise(
                Js.ToUint32(seed) ^ definition.salt ^ DETAIL_SALT,
                cx + 19.25,
                cy - 31.75,
                definition.scale * 1.05);
            double field = broad * 0.78 + detail * 0.22;
            double cohortPreference =
                0.86 + latticeHash(Js.ToUint32(seed) ^ COHORT_SALT, Js.ToInt32(definition.salt), 0) * 0.28;
            double cohortFoundation =
                Math.pow(latticeHash(Js.ToUint32(seed) ^ COHORT_SALT, Js.ToInt32(definition.salt), 1), 10) * 0.08;
            return new RankedLandscape
            {
                kind = definition.kind,
                // A steep sparse curve keeps each district legible. With a broad linear curve many of eighteen almost
                // equal fields could exchange the complete top-three set between adjacent chunks; sparse peaks instead
                // make an outgoing influence approach zero before another setting enters the composition.
                raw =
                    0.00001 +
                    cohortFoundation +
                    Math.pow(clamp01((field - 0.36) / 0.55), 4.2) * cohortPreference,
            };
        });
        ranked.sort(compareRanked);
        return ranked;
    }

    /// <summary>Resolve one coherent 2-3-setting landscape composition at a chunk-lattice position.</summary>
    public static EndlessLandscapeSample endlessLandscapeSampleAt(double seed, double cx, double cy)
    {
        List<RankedLandscape> ranked = rankedLandscapePotentials(seed, cx, cy);

        double compositionField = valueNoise(Js.ToUint32(seed) ^ COMPOSITION_COUNT_SALT, cx, cy, 24);
        int count = compositionField >= 0.5 ? 3 : 2;
        List<RankedLandscape> selected = ranked.slice(0, count);
        double total = selected.reduce((double sum, RankedLandscape entry) => sum + entry.raw, 0.0);
        List<EndlessLandscapeInfluence> active = selected.map(entry => new EndlessLandscapeInfluence
        {
            kind = entry.kind,
            weight = entry.raw / Math.max(Number.EPSILON, total),
        });

        double physicalTotal = ranked.reduce((double sum, RankedLandscape entry) => sum + Math.pow(entry.raw, 1.4), 0.0);
        var weights = new JsMap<string, double>();
        foreach (RankedLandscape entry in ranked)
            weights.set(entry.kind, Math.pow(entry.raw, 1.4) / Math.max(Number.EPSILON, physicalTotal));

        EndlessLandscapeTraits blended = traits(opennessBias: 0, rockDensity: 0);
        for (int key = 0; key < EndlessLandscapeTraits.KEY_COUNT; key++) blended[key] = 0;
        foreach (EndlessLandscapeDefinition definition in ENDLESS_LANDSCAPE_DEFINITIONS)
        {
            double weight = weights[definition.kind];
            for (int key = 0; key < EndlessLandscapeTraits.KEY_COUNT; key++)
            {
                blended[key] += definition.traits[key] * weight;
            }
        }

        double strongest = active[0].weight;
        double second = active[1].weight;
        return new EndlessLandscapeSample
        {
            dominant = active[0].kind,
            active = active,
            weights = weights,
            dominantStrength = strongest,
            transition = clamp01(1 - (strongest - second) * 3.4),
            traits = blended,
        };
    }

    private const uint COUNTRY_SUPPORT_SALT = 0x4a7484aa;

    /// <summary>V2's independent field winner, retained so explicitly versioned historical cohorts stay reproducible.</summary>
    public static EndlessCountryLandscape endlessCountryLandscapeAtLegacy(double seed, double countryX, double countryY)
    {
        EndlessLandscapeSample sample = endlessLandscapeSampleAt(seed, countryX * 1.65 + 0.5, countryY * 1.65 + 0.5);
        EndlessLandscapeDefinition primary = ENDLESS_LANDSCAPE_DEFINITIONS.find(definition => definition.kind == sample.dominant);
        string? supportCandidate = sample.active.Count > 1 ? sample.active[1].kind : null;
        double supportRoll = latticeHash(Js.ToUint32(seed) ^ COUNTRY_SUPPORT_SALT, countryX, countryY);
        string? support = !string.IsNullOrEmpty(supportCandidate) && supportRoll < 0.32 ? supportCandidate : null;
        EndlessLandscapeDefinition? supporting = !string.IsNullOrEmpty(support)
            ? ENDLESS_LANDSCAPE_DEFINITIONS.find(definition => definition.kind == support)
            : null;
        double supportWeight = supporting != null ? 0.14 + supportRoll * 0.2 : 0;
        double primaryWeight = 1 - supportWeight;
        EndlessLandscapeTraits countryTraits = primary.traits.Clone();
        if (supporting != null)
        {
            for (int key = 0; key < EndlessLandscapeTraits.KEY_COUNT; key++)
            {
                countryTraits[key] = primary.traits[key] * primaryWeight + supporting.traits[key] * supportWeight;
            }
        }
        return new EndlessCountryLandscape
        {
            primary = primary.kind,
            support = !string.IsNullOrEmpty(support) ? support : null,
            supportWeight = supportWeight,
            traits = countryTraits,
        };
    }

    /// <summary>
    /// V3 composes semantic Countries in deterministic 8x8 waves. The perimeter of every wave is a shared Markov
    /// bridge, so independently streamed neighbouring waves still collapse to the same compatible boundary states.
    /// Inside the perimeter, minimum-entropy WFC propagates the authored physical constraints.
    /// </summary>
    public const int ENDLESS_LANDSCAPE_WAVE_SIZE = 8;
    private static readonly int LANDSCAPE_COUNT = ENDLESS_LANDSCAPE_DEFINITIONS.Count;
    private const int LANDSCAPE_WAVE_CACHE_LIMIT = 96;
    private const uint LANDSCAPE_WAVE_SALT = 0x510e527f;
    private const uint LANDSCAPE_VERTEX_SALT = 0x9b05688c;
    private const uint LANDSCAPE_EDGE_SALT = 0x1f83d9ab;
    private const uint LANDSCAPE_BIOME_SALT = 0xc2b2ae35;

    /// <summary>`new Map(definitions.map((definition, index) => [definition.kind, index]))`; lookup only.</summary>
    private static readonly Dictionary<string, int> LANDSCAPE_INDEX = buildLandscapeIndex();

    private static Dictionary<string, int> buildLandscapeIndex()
    {
        var index = new Dictionary<string, int>();
        for (int i = 0; i < ENDLESS_LANDSCAPE_DEFINITIONS.Count; i++) index[ENDLESS_LANDSCAPE_DEFINITIONS[i].kind] = i;
        return index;
    }

    private static EndlessLandscapeDefinition landscapeDefinition(string kind)
    {
        return ENDLESS_LANDSCAPE_DEFINITIONS[LANDSCAPE_INDEX[kind]];
    }

    private static EndlessBiomeCompositionDna dna(
        double contrast,
        double chapterScale,
        double chapterStrength,
        double wildcardStrength,
        double supportRate,
        Dictionary<string, double> affinities,
        double baseAffinity = 0.72)
    {
        // `Object.freeze({ baseAffinity: 0.72, ...values })`
        return new EndlessBiomeCompositionDna
        {
            baseAffinity = baseAffinity,
            contrast = contrast,
            chapterScale = chapterScale,
            chapterStrength = chapterStrength,
            wildcardStrength = wildcardStrength,
            supportRate = supportRate,
            affinities = affinities,
        };
    }

    /// <summary>Keyed by run theme; lookup only.</summary>
    private static readonly Dictionary<string, EndlessBiomeCompositionDna> ENDLESS_BIOME_COMPOSITION_DNA = new()
    {
        ["highland_pass"] = dna(
            contrast: 1.2,
            chapterScale: 19,
            chapterStrength: 1.18,
            wildcardStrength: 0.72,
            supportRate: 0.3,
            affinities: new()
            {
                [EndlessLandscapeKind.AlpinePass] = 1.95,
                [EndlessLandscapeKind.HighlandMoor] = 1.62,
                [EndlessLandscapeKind.GlacialFjord] = 1.42,
                [EndlessLandscapeKind.CascadeTerraces] = 1.36,
                [EndlessLandscapeKind.RidgeMaze] = 1.34,
                [EndlessLandscapeKind.GrandCanyon] = 1.18,
            }),
        ["noir_sprawl"] = dna(
            contrast: 1.26,
            chapterScale: 15,
            chapterStrength: 1.28,
            wildcardStrength: 0.82,
            supportRate: 0.4,
            affinities: new()
            {
                [EndlessLandscapeKind.RuinedCauseways] = 2.05,
                [EndlessLandscapeKind.FloodedCaverns] = 1.62,
                [EndlessLandscapeKind.FracturedConfluence] = 1.42,
                [EndlessLandscapeKind.RiverDelta] = 1.32,
                [EndlessLandscapeKind.StoneForest] = 1.22,
                [EndlessLandscapeKind.RidgeMaze] = 1.18,
            }),
        ["olympian_sky_borough"] = dna(
            contrast: 1.28,
            chapterScale: 22,
            chapterStrength: 1.34,
            wildcardStrength: 0.88,
            supportRate: 0.34,
            affinities: new()
            {
                [EndlessLandscapeKind.Archipelago] = 1.92,
                [EndlessLandscapeKind.CascadeTerraces] = 1.76,
                [EndlessLandscapeKind.RuinedCauseways] = 1.55,
                [EndlessLandscapeKind.AlpinePass] = 1.34,
                [EndlessLandscapeKind.GrandCanyon] = 1.28,
                [EndlessLandscapeKind.OasisBasins] = 1.12,
            }),
        ["sakura_temple_dream"] = dna(
            contrast: 1.18,
            chapterScale: 20,
            chapterStrength: 1.14,
            wildcardStrength: 0.68,
            supportRate: 0.46,
            affinities: new()
            {
                [EndlessLandscapeKind.IslandLake] = 2.05,
                [EndlessLandscapeKind.HighlandMoor] = 1.58,
                [EndlessLandscapeKind.CascadeTerraces] = 1.52,
                [EndlessLandscapeKind.OasisBasins] = 1.45,
                [EndlessLandscapeKind.Archipelago] = 1.24,
                [EndlessLandscapeKind.RuinedCauseways] = 1.18,
            }),
        ["abyssal_deepsea"] = dna(
            contrast: 1.34,
            chapterScale: 17,
            chapterStrength: 1.42,
            wildcardStrength: 0.94,
            supportRate: 0.44,
            affinities: new()
            {
                [EndlessLandscapeKind.FloodedCaverns] = 2.08,
                [EndlessLandscapeKind.GreatLake] = 1.75,
                [EndlessLandscapeKind.FracturedConfluence] = 1.72,
                [EndlessLandscapeKind.SinkholeKarst] = 1.58,
                [EndlessLandscapeKind.GlacialFjord] = 1.48,
                [EndlessLandscapeKind.Archipelago] = 1.32,
            }),
        ["rainbowland"] = dna(
            contrast: 1.38,
            chapterScale: 13,
            chapterStrength: 1.5,
            wildcardStrength: 1.1,
            supportRate: 0.5,
            affinities: new()
            {
                [EndlessLandscapeKind.Archipelago] = 1.76,
                [EndlessLandscapeKind.OasisBasins] = 1.68,
                [EndlessLandscapeKind.IslandLake] = 1.6,
                [EndlessLandscapeKind.CascadeTerraces] = 1.48,
                [EndlessLandscapeKind.MesaBadlands] = 1.28,
                [EndlessLandscapeKind.CalderaLake] = 1.24,
            }),
        ["clockwork_moon_bazaar"] = dna(
            contrast: 1.3,
            chapterScale: 14,
            chapterStrength: 1.4,
            wildcardStrength: 0.96,
            supportRate: 0.36,
            affinities: new()
            {
                [EndlessLandscapeKind.RuinedCauseways] = 1.98,
                [EndlessLandscapeKind.RidgeMaze] = 1.7,
                [EndlessLandscapeKind.SinkholeKarst] = 1.48,
                [EndlessLandscapeKind.MesaBadlands] = 1.4,
                [EndlessLandscapeKind.StoneForest] = 1.38,
                [EndlessLandscapeKind.FracturedConfluence] = 1.18,
            }),
        ["sugarstorm_carnival"] = dna(
            contrast: 1.36,
            chapterScale: 12,
            chapterStrength: 1.48,
            wildcardStrength: 1.06,
            supportRate: 0.52,
            affinities: new()
            {
                [EndlessLandscapeKind.OasisBasins] = 1.86,
                [EndlessLandscapeKind.IslandLake] = 1.65,
                [EndlessLandscapeKind.RiverDelta] = 1.54,
                [EndlessLandscapeKind.CascadeTerraces] = 1.42,
                [EndlessLandscapeKind.HighlandMoor] = 1.3,
                [EndlessLandscapeKind.MesaBadlands] = 1.2,
            }),
        ["prismglass_archive"] = dna(
            contrast: 1.42,
            chapterScale: 16,
            chapterStrength: 1.5,
            wildcardStrength: 1.02,
            supportRate: 0.34,
            affinities: new()
            {
                [EndlessLandscapeKind.StoneForest] = 1.92,
                [EndlessLandscapeKind.GlacialFjord] = 1.62,
                [EndlessLandscapeKind.RidgeMaze] = 1.58,
                [EndlessLandscapeKind.FracturedConfluence] = 1.46,
                [EndlessLandscapeKind.Archipelago] = 1.34,
                [EndlessLandscapeKind.CalderaLake] = 1.26,
            }),
        ["starforged_cathedral_endrun"] = dna(
            contrast: 1.48,
            chapterScale: 24,
            chapterStrength: 1.58,
            wildcardStrength: 1.12,
            supportRate: 0.3,
            affinities: new()
            {
                [EndlessLandscapeKind.GrandCanyon] = 1.86,
                [EndlessLandscapeKind.RuinedCauseways] = 1.82,
                [EndlessLandscapeKind.CalderaLake] = 1.7,
                [EndlessLandscapeKind.FracturedConfluence] = 1.52,
                [EndlessLandscapeKind.StoneForest] = 1.42,
                [EndlessLandscapeKind.CascadeTerraces] = 1.18,
            }),
    };

    private static readonly EndlessBiomeCompositionDna DEFAULT_BIOME_COMPOSITION_DNA = dna(
        baseAffinity: 1,
        contrast: 1,
        chapterScale: 18,
        chapterStrength: 0,
        wildcardStrength: 0,
        supportRate: 0.32,
        affinities: new());

    /// <summary>Data-authoring hook used by audits; unknown modded biome keys retain V3's neutral distribution.</summary>
    public static EndlessBiomeCompositionDna endlessBiomeCompositionDnaFor(string biomeKey)
    {
        return ENDLESS_BIOME_COMPOSITION_DNA.TryGetValue(biomeKey, out EndlessBiomeCompositionDna? found)
            ? found
            : DEFAULT_BIOME_COMPOSITION_DNA;
    }

    /// <summary>FNV-1a over UTF-16 code units; returned as the uint32 it is (`value >>> 0`) because it is only ever XORed.</summary>
    private static uint biomeSalt(string biomeKey)
    {
        int value = unchecked((int)0x811c9dc5);
        for (int index = 0; index < biomeKey.Length; index++)
        {
            value ^= biomeKey[index];
            value = Math.imul(value, 0x01000193);
        }
        return (uint)value;
    }

    /// <summary>
    /// Perceptual distance between two physical grammars. Hydrology and void aperture dominate because an abrupt
    /// GreatLake -> dry canyon jump is much more visible than a cover-density change; altitude and rock structure
    /// are the next strongest terms. The distance feeds both the hard adjacency graph and its soft Markov weights.
    /// </summary>
    private static double landscapeDistance(EndlessLandscapeDefinition a, EndlessLandscapeDefinition b)
    {
        EndlessLandscapeTraits at = a.traits;
        EndlessLandscapeTraits bt = b.traits;
        double waterA = at.waterBias * 0.38 + at.lakeStrength * 0.72 + at.channelComplexity * 0.24;
        double waterB = bt.waterBias * 0.38 + bt.lakeStrength * 0.72 + bt.channelComplexity * 0.24;
        double voidA = at.chasmStrength * 0.62 + at.riftStrength * 0.58 + at.chasmBasins * 0.24;
        double voidB = bt.chasmStrength * 0.62 + bt.riftStrength * 0.58 + bt.chasmBasins * 0.24;
        double massA = at.rockDensity * 0.52 + at.escarpmentStrength * 0.34 + at.pillarDensity * 0.14;
        double massB = bt.rockDensity * 0.52 + bt.escarpmentStrength * 0.34 + bt.pillarDensity * 0.14;
        return
            Math.abs(waterA - waterB) * 0.9 +
            Math.abs(voidA - voidB) * 0.82 +
            Math.abs(massA - massB) * 0.68 +
            Math.abs(at.altitudeBias - bt.altitudeBias) * 1.35 +
            Math.abs(at.opennessBias - bt.opennessBias) * 0.42 +
            Math.abs(at.reliefContrast - bt.reliefContrast) * 0.32 +
            Math.abs(at.pathConfinement - bt.pathConfinement) * 0.18;
    }

    private static bool isTransitionLandscape(string kind)
    {
        return
            kind == EndlessLandscapeKind.CascadeTerraces ||
            kind == EndlessLandscapeKind.HighlandMoor ||
            kind == EndlessLandscapeKind.RuinedCauseways ||
            kind == EndlessLandscapeKind.RidgeMaze;
    }

    /// <summary>Public for generator audits and future data-authoring validation.</summary>
    public static bool endlessLandscapesCompatible(string a, string b)
    {
        if (a == b) return true;
        double distance = landscapeDistance(landscapeDefinition(a), landscapeDefinition(b));
        return distance <= 1.48 || (distance <= 1.92 && (isTransitionLandscape(a) || isTransitionLandscape(b)));
    }

    /// <summary>Markov transition preference after hard compatibility has been established.</summary>
    public static double endlessLandscapeTransitionWeight(string from, string to)
    {
        if (!endlessLandscapesCompatible(from, to)) return 0;
        // Staying is still the single most likely outcome, but it is no longer twice as likely as every close
        // neighbour combined. At the former 2.85 the Markov term alone kept whole runs inside one grammar even
        // after the potential field was given a run-length chapter cadence; a Country already spans 10,000 world
        // units, so "the same again" needs to be a preference, not a near-certainty. Hard compatibility is
        // untouched, so every transition this admits remains a physically credible one.
        if (from == to) return 1.55;
        return 0.52 + Math.exp(-landscapeDistance(landscapeDefinition(from), landscapeDefinition(to)) * 1.08) * 1.5;
    }

    private static readonly uint[] LANDSCAPE_COMPATIBILITY = buildLandscapeCompatibility();

    private static uint[] buildLandscapeCompatibility()
    {
        var masks = new uint[LANDSCAPE_COUNT * 4];
        for (int state = 0; state < LANDSCAPE_COUNT; state++)
        {
            int mask = 0;
            for (int neighbour = 0; neighbour < LANDSCAPE_COUNT; neighbour++)
            {
                if (endlessLandscapesCompatible(
                        ENDLESS_LANDSCAPE_DEFINITIONS[state].kind,
                        ENDLESS_LANDSCAPE_DEFINITIONS[neighbour].kind))
                    mask |= 1 << neighbour;
            }
            for (int direction = 0; direction < 4; direction++) masks[state * 4 + direction] = (uint)mask;
        }
        return masks;
    }

    private static readonly double[] LANDSCAPE_TRANSITIONS = buildLandscapeTransitions();

    private static double[] buildLandscapeTransitions()
    {
        var weights = new double[LANDSCAPE_COUNT * LANDSCAPE_COUNT];
        for (int from = 0; from < LANDSCAPE_COUNT; from++)
        {
            for (int to = 0; to < LANDSCAPE_COUNT; to++)
            {
                weights[from * LANDSCAPE_COUNT + to] = endlessLandscapeTransitionWeight(
                    ENDLESS_LANDSCAPE_DEFINITIONS[from].kind,
                    ENDLESS_LANDSCAPE_DEFINITIONS[to].kind);
            }
        }
        return weights;
    }

    private static readonly double[][] LANDSCAPE_GRAPH_DISTANCE = buildLandscapeGraphDistance();

    private static double[][] buildLandscapeGraphDistance()
    {
        var distances = new double[LANDSCAPE_COUNT][];
        for (int from = 0; from < LANDSCAPE_COUNT; from++)
        {
            distances[from] = new double[LANDSCAPE_COUNT];
            for (int to = 0; to < LANDSCAPE_COUNT; to++)
            {
                distances[from][to] =
                    from == to
                        ? 0
                        : endlessLandscapesCompatible(
                              ENDLESS_LANDSCAPE_DEFINITIONS[from].kind,
                              ENDLESS_LANDSCAPE_DEFINITIONS[to].kind)
                            ? 1
                            : double.PositiveInfinity;
            }
        }
        for (int via = 0; via < LANDSCAPE_COUNT; via++)
        {
            for (int from = 0; from < LANDSCAPE_COUNT; from++)
            {
                for (int to = 0; to < LANDSCAPE_COUNT; to++)
                {
                    distances[from][to] = Math.min(distances[from][to], distances[from][via] + distances[via][to]);
                }
            }
        }
        return distances;
    }

    /// <summary>
    /// Countries per unit of landscape-potential field coordinate — i.e. how fast a run walks through the field
    /// that decides which grammar wins.
    ///
    /// This single number sets the length of a landscape CHAPTER, and it is the reason runs used to read as one
    /// place. A Country is 4 chunks = 10,000 world units, about a minute of travel at the reference walk speed. At
    /// the former rate of 1.65 the potential fields stayed coherent for 11..16 Countries — 110,000 to 165,000 units,
    /// over ten minutes of running before the world could even want to become something else, so a whole run
    /// routinely finished inside a single grammar. At this rate the primary field turns over every ~2..3 Countries
    /// and its detail octaves every ~1..2, which gives a run a sequence of chapters instead of one setting. Nothing
    /// about coherence is given up: the WFC's hard adjacency graph and Markov weights still compose every
    /// transition, so chapters change through physically credible ground rather than by cutting.
    /// </summary>
    private const double LANDSCAPE_FIELD_RATE = 8.6;
    /// <summary>Shipped V3/V4 field cadence, retained for deterministic historical generation.</summary>
    public const double LEGACY_LANDSCAPE_FIELD_RATE = 1.65;

    private static double[] potentialVector(
        double seed,
        double countryX,
        double countryY,
        string? biomeKey = null,
        double fieldRate = LANDSCAPE_FIELD_RATE)
    {
        double fieldX = countryX * fieldRate;
        double fieldY = countryY * fieldRate;
        List<RankedLandscape> ranked = rankedLandscapePotentials(seed, fieldX + 0.5, fieldY + 0.5);
        var vector = new double[LANDSCAPE_COUNT];
        EndlessBiomeCompositionDna? biome = !string.IsNullOrEmpty(biomeKey) ? endlessBiomeCompositionDnaFor(biomeKey) : null;
        uint identitySalt = !string.IsNullOrEmpty(biomeKey) ? biomeSalt(biomeKey) : 0;
        foreach (RankedLandscape entry in ranked)
        {
            EndlessLandscapeDefinition definition = landscapeDefinition(entry.kind);
            double depth = clamp01((Math.hypot(countryX, countryY) - 10) / 54);
            double recursiveDetail = fractalValueNoise(
                Js.ToUint32(seed) ^ definition.salt ^ LANDSCAPE_WAVE_SALT,
                fieldX + 37.25,
                fieldY - 21.75,
                definition.scale * 1.4,
                3,
                2.05,
                0.48);
            // The exponent retains regional identity but leaves enough probability mass for constraints to compose a
            // physically credible transition instead of repeatedly contradicting an overconfident local noise winner.
            // Fine recursive structure fades in with run depth: the origin remains readable, while later country
            // generations gain self-similar subdistricts without weakening any WFC adjacency constraint.
            double v3Potential =
                0.000_001 + Math.pow(entry.raw, 1.15) * (1 + (recursiveDetail - 0.5) * depth * 0.72);
            if (biome == null)
            {
                // This exact branch is the shipped V3 distribution and deliberately remains byte-stable.
                vector[LANDSCAPE_INDEX[entry.kind]] = v3Potential;
                continue;
            }
            // The theme's own chapter and wildcard fields ride the same rate, so a biome's authored `chapterScale`
            // keeps meaning "relative to the run's chapter cadence" rather than silently becoming a 20-minute band.
            double chapter = fractalValueNoise(
                Js.ToUint32(seed) ^ definition.salt ^ identitySalt ^ LANDSCAPE_BIOME_SALT,
                fieldX + 91.5,
                fieldY - 47.25,
                biome.chapterScale,
                3,
                2.08,
                0.5);
            double wildcard = fractalValueNoise(
                Js.ToUint32(seed) ^ definition.salt ^ identitySalt ^ 0x85ebca6bu,
                fieldX - 153.75,
                fieldY + 68.5,
                biome.chapterScale * 0.56,
                2,
                2.14,
                0.46);
            double affinity = biome.affinities.TryGetValue(entry.kind, out double authored) ? authored : biome.baseAffinity;
            double chapterMultiplier = Math.pow(0.38 + chapter * 1.72, biome.chapterStrength);
            // Only the upper tail becomes a wildcard district. This produces rare counter-biomes without converting
            // ordinary local noise into visual confetti; WFC still guarantees a physically credible path in and out.
            double wildcardMultiplier =
                wildcard > 0.72 ? 1 + Math.pow((wildcard - 0.72) / 0.28, 2) * biome.wildcardStrength * 3.2 : 1;
            vector[LANDSCAPE_INDEX[entry.kind]] =
                0.000_001 +
                Math.pow(Math.max(0.000_001, v3Potential), biome.contrast) *
                    affinity *
                    chapterMultiplier *
                    wildcardMultiplier;
        }
        return vector;
    }

    private static int weightedState(
        double[] weights,
        double seed,
        double x,
        double y,
        uint salt,
        Func<int, bool>? predicate = null)
    {
        predicate ??= _ => true;
        double total = 0;
        for (int state = 0; state < LANDSCAPE_COUNT; state++)
        {
            if (predicate(state)) total += weights[state];
        }
        if (total <= 0) throw new InvalidOperationException("Endless landscape grammar has no valid weighted state");
        double roll = latticeHash(Js.ToUint32(seed) ^ salt, x, y) * total;
        for (int state = 0; state < LANDSCAPE_COUNT; state++)
        {
            if (!predicate(state)) continue;
            roll -= weights[state];
            if (roll <= 0) return state;
        }
        for (int state = LANDSCAPE_COUNT - 1; state >= 0; state--) if (predicate(state)) return state;
        throw new InvalidOperationException("Endless landscape grammar failed to choose a state");
    }

    private static int waveVertexState(
        double seed,
        double vertexX,
        double vertexY,
        string? biomeKey = null,
        double fieldRate = LANDSCAPE_FIELD_RATE)
    {
        double[] potential = potentialVector(
            seed,
            vertexX * ENDLESS_LANDSCAPE_WAVE_SIZE - 1,
            vertexY * ENDLESS_LANDSCAPE_WAVE_SIZE - 1,
            biomeKey,
            fieldRate);
        int strongest = 0;
        for (int state = 1; state < LANDSCAPE_COUNT; state++)
        {
            double tieBreak = latticeHash(
                Js.ToUint32(seed) ^ LANDSCAPE_VERTEX_SALT,
                vertexX * LANDSCAPE_COUNT + state,
                vertexY);
            double bestTieBreak = latticeHash(
                Js.ToUint32(seed) ^ LANDSCAPE_VERTEX_SALT,
                vertexX * LANDSCAPE_COUNT + strongest,
                vertexY);
            if (
                potential[state] > potential[strongest] ||
                (potential[state] == potential[strongest] && tieBreak > bestTieBreak)
            )
                strongest = state;
        }
        return strongest;
    }

    private static byte[] waveEdgePath(
        double seed,
        double edgeX,
        double edgeY,
        bool vertical,
        string? biomeKey = null,
        double fieldRate = LANDSCAPE_FIELD_RATE)
    {
        var path = new byte[ENDLESS_LANDSCAPE_WAVE_SIZE];
        int end = vertical
            ? waveVertexState(seed, edgeX, edgeY + 1, biomeKey, fieldRate)
            : waveVertexState(seed, edgeX + 1, edgeY, biomeKey, fieldRate);
        path[0] = (byte)waveVertexState(seed, edgeX, edgeY, biomeKey, fieldRate);
        path[path.Length - 1] = (byte)end;
        for (int offset = 1; offset < path.Length - 1; offset++)
        {
            int previous = path[offset - 1];
            int remaining = path.Length - 1 - offset;
            double countryX = vertical
                ? edgeX * ENDLESS_LANDSCAPE_WAVE_SIZE - 1
                : edgeX * ENDLESS_LANDSCAPE_WAVE_SIZE + offset;
            double countryY = vertical
                ? edgeY * ENDLESS_LANDSCAPE_WAVE_SIZE + offset
                : edgeY * ENDLESS_LANDSCAPE_WAVE_SIZE - 1;
            double[] weights = potentialVector(seed, countryX, countryY, biomeKey, fieldRate);
            for (int state = 0; state < LANDSCAPE_COUNT; state++)
            {
                weights[state] = weights[state] * LANDSCAPE_TRANSITIONS[previous * LANDSCAPE_COUNT + state];
            }
            path[offset] = (byte)weightedState(
                weights,
                seed,
                edgeX * 131 + edgeY,
                offset,
                LANDSCAPE_EDGE_SALT ^ (vertical ? 0x9e3779b9u : 0x85ebca6bu),
                state =>
                    LANDSCAPE_GRAPH_DISTANCE[previous][state] <= 1 &&
                    LANDSCAPE_GRAPH_DISTANCE[state][end] <= remaining);
        }
        if (LANDSCAPE_GRAPH_DISTANCE[path[path.Length - 2]][end] > 1)
            throw new InvalidOperationException("Endless landscape edge could not reach its shared vertex state");
        return path;
    }

    /// <summary>
    /// Insertion-ordered like the JS Map so the oldest-entry eviction matches. The C# port is called from chunk
    /// worker threads, so every access is serialized on the map itself; entries are immutable once stored and a
    /// racing duplicate computation yields byte-identical data, so the lock cannot change any generated result.
    /// </summary>
    private static readonly JsMap<string, byte[]> landscapeWaveCache = new();

    private static byte[] collapseLandscapeWave(
        double seed,
        double blockX,
        double blockY,
        string? biomeKey = null,
        double fieldRate = LANDSCAPE_FIELD_RATE)
    {
        string key = $"{biomeKey ?? "v3"}:{Js.Str(fieldRate)}:{Js.Str(seed)}:{Js.Str(blockX)}:{Js.Str(blockY)}";
        lock (landscapeWaveCache)
        {
            byte[]? cached = landscapeWaveCache.get(key);
            if (cached != null) return cached;
        }

        const int size = ENDLESS_LANDSCAPE_WAVE_SIZE;
        var presets = new short[size * size];
        presets.fill((short)-1);
        byte[] north = waveEdgePath(seed, blockX, blockY, false, biomeKey, fieldRate);
        byte[] south = waveEdgePath(seed, blockX, blockY + 1, false, biomeKey, fieldRate);
        byte[] west = waveEdgePath(seed, blockX, blockY, true, biomeKey, fieldRate);
        byte[] east = waveEdgePath(seed, blockX + 1, blockY, true, biomeKey, fieldRate);
        for (int offset = 0; offset < size; offset++)
        {
            presets[offset] = north[offset];
            presets[(size - 1) * size + offset] = south[offset];
            presets[offset * size] = west[offset];
            presets[offset * size + size - 1] = east[offset];
        }

        var weights = new double[size * size * LANDSCAPE_COUNT];
        for (int localY = 0; localY < size; localY++)
        {
            for (int localX = 0; localX < size; localX++)
            {
                double[] potential = potentialVector(
                    seed,
                    blockX * size + localX,
                    blockY * size + localY,
                    biomeKey,
                    fieldRate);
                weights.set(potential, (localY * size + localX) * LANDSCAPE_COUNT);
            }
        }

        byte[]? collapsed = null;
        WaveCollapseContradiction? lastContradiction = null;
        // A WFC observation can paint itself into a corner. Deterministic salted restarts are bounded and cheap on
        // this 8x8 semantic grid; perimeter constraints never change, so streamed neighbours remain identical.
        for (int attempt = 0; attempt < 6 && collapsed == null; attempt++)
        {
            try
            {
                collapsed = collapseWaveFunction(new WaveCollapseModel
                {
                    width = size,
                    height = size,
                    stateCount = LANDSCAPE_COUNT,
                    weights = weights,
                    compatibility = LANDSCAPE_COMPATIBILITY,
                    transitions = LANDSCAPE_TRANSITIONS,
                    presets = presets,
                    seed = (uint)(
                        Js.ToInt32(seed) ^
                        unchecked((int)LANDSCAPE_WAVE_SALT) ^
                        Math.imul(blockX, 0x27d4eb2f) ^
                        Math.imul(blockY, 0x165667b1) ^
                        Math.imul(attempt, 0x2c1b3c6d)),
                });
            }
            catch (WaveCollapseContradiction error)
            {
                lastContradiction = error;
            }
        }
        if (collapsed == null)
            throw (Exception?)lastContradiction ?? new InvalidOperationException("Endless landscape wave failed to collapse");
        lock (landscapeWaveCache)
        {
            landscapeWaveCache.set(key, collapsed);
            if (landscapeWaveCache.size > LANDSCAPE_WAVE_CACHE_LIMIT)
            {
                foreach (string oldest in landscapeWaveCache.keys())
                {
                    landscapeWaveCache.delete(oldest);
                    break;
                }
            }
        }
        return collapsed;
    }

    private static string collapsedCountryLandscapeKind(
        double seed,
        double countryX,
        double countryY,
        string? biomeKey = null,
        double fieldRate = LANDSCAPE_FIELD_RATE)
    {
        const int size = ENDLESS_LANDSCAPE_WAVE_SIZE;
        double blockX = Math.floor(countryX / size);
        double blockY = Math.floor(countryY / size);
        double localX = ((countryX % size) + size) % size;
        double localY = ((countryY % size) + size) % size;
        int state = collapseLandscapeWave(seed, blockX, blockY, biomeKey, fieldRate)[(int)(localY * size + localX)];
        return ENDLESS_LANDSCAPE_DEFINITIONS[state].kind;
    }

    /// <summary>
    /// Resolve the constrained landscape grammar for one 4x4-chunk semantic Country. A null `fieldRate` is the TS
    /// `undefined` (the current LANDSCAPE_FIELD_RATE); pass LEGACY_LANDSCAPE_FIELD_RATE for historical cohorts.
    /// </summary>
    public static EndlessCountryLandscape endlessCountryLandscapeAt(
        double seed,
        double countryX,
        double countryY,
        string? biomeKey = null,
        double? fieldRate = null)
    {
        double rate = fieldRate ?? LANDSCAPE_FIELD_RATE;
        string primaryKind = collapsedCountryLandscapeKind(seed, countryX, countryY, biomeKey, rate);
        EndlessLandscapeDefinition primary = landscapeDefinition(primaryKind);
        double[]? localPotential = !string.IsNullOrEmpty(biomeKey)
            ? potentialVector(seed, countryX, countryY, biomeKey, rate)
            : null;
        EndlessLandscapeSample? local = localPotential != null
            ? null
            : endlessLandscapeSampleAt(seed, countryX * 1.65 + 0.5, countryY * 1.65 + 0.5);
        List<EndlessLandscapeDefinition> candidates = new List<EndlessLandscapeDefinition>(ENDLESS_LANDSCAPE_DEFINITIONS)
            .filter(definition =>
                definition.kind != primaryKind &&
                endlessLandscapesCompatible(primaryKind, definition.kind));
        candidates.sort((a, b) =>
        {
            double difference =
                (localPotential != null ? localPotential[LANDSCAPE_INDEX[b.kind]] : local!.weights[b.kind]) -
                (localPotential != null ? localPotential[LANDSCAPE_INDEX[a.kind]] : local!.weights[a.kind]);
            return Js.Truthy(difference) ? difference : string.CompareOrdinal(a.kind, b.kind);
        });
        EndlessLandscapeDefinition? supportCandidate = candidates.Count > 0 ? candidates[0] : null;
        double supportRoll = latticeHash(Js.ToUint32(seed) ^ COUNTRY_SUPPORT_SALT, countryX, countryY);
        double supportRate = !string.IsNullOrEmpty(biomeKey) ? endlessBiomeCompositionDnaFor(biomeKey).supportRate : 0.32;
        EndlessLandscapeDefinition? support = supportCandidate != null && supportRoll < supportRate ? supportCandidate : null;
        double supportWeight = support != null
            ? !string.IsNullOrEmpty(biomeKey)
                ? 0.12 + (supportRoll / supportRate) * 0.1
                : 0.14 + supportRoll * 0.2
            : 0;
        double primaryWeight = 1 - supportWeight;
        EndlessLandscapeTraits countryTraits = primary.traits.Clone();
        if (support != null)
        {
            for (int key = 0; key < EndlessLandscapeTraits.KEY_COUNT; key++)
            {
                countryTraits[key] = primary.traits[key] * primaryWeight + support.traits[key] * supportWeight;
            }
        }
        return new EndlessCountryLandscape
        {
            primary = primary.kind,
            support = support?.kind,
            supportWeight = supportWeight,
            traits = countryTraits,
        };
    }

    /// <summary>Weighted affinity of the active landscape recipe for one existing signed-field macro archetype.</summary>
    public static double endlessLandscapeMacroAffinity(EndlessLandscapeSample sample, string style)
    {
        if (style == "labyrinth") return 0;
        double affinity = 0;
        foreach (EndlessLandscapeDefinition definition in ENDLESS_LANDSCAPE_DEFINITIONS)
        {
            affinity += (definition.macroAffinities.TryGetValue(style, out double authored) ? authored : 0) *
                sample.weights[definition.kind];
        }
        return clamp01(affinity);
    }
}
