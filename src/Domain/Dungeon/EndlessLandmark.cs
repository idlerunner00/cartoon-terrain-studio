// Port of packages/shared/src/domain/dungeon/endlessLandmark.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * Stable landmark grammar for semantic Endless Countries.
 *
 * A landmark is deliberately split into two orthogonal lanes:
 *
 *  - a family owns the large physical idea (cascade, sinkhole, ridge gate, ...), and
 *  - a motif owns the readable arrangement (rings, forks, crescents, switchbacks, ...).
 *
 * The cartesian product is the public 100-variant catalog. This is not a list of cosmetic aliases: both lanes
 * feed the terrain rasterizer, route graph, hydrology and dressing pass. Keeping the catalog data-only also
 * means the server, client workers and audit tools select exactly the same variant without shipping assets.
 */

public static class EndlessLandmarkFamily
{
    public const string CascadeTerrace = "cascade_terrace";
    public const string ChasmCrossing = "chasm_crossing";
    public const string CliffGrove = "cliff_grove";
    public const string LabyrinthCourt = "labyrinth_court";
    public const string FloodedGarden = "flooded_garden";
    public const string CrystalScar = "crystal_scar";
    public const string RuinedCauseway = "ruined_causeway";
    public const string SinkholeCrown = "sinkhole_crown";
    public const string RidgeGate = "ridge_gate";
    public const string AbyssalConfluence = "abyssal_confluence";
}

public static class EndlessLandmarkMotif
{
    public const string Crown = "crown";
    public const string Fork = "fork";
    public const string Crescent = "crescent";
    public const string TwinRing = "twin_ring";
    public const string Switchback = "switchback";
    public const string BrokenSpokes = "broken_spokes";
    public const string Serpentine = "serpentine";
    public const string Triad = "triad";
    public const string Spiral = "spiral";
    public const string Mirror = "mirror";
}

public static class EndlessLandmarkHazard
{
    public const string None = "none";
    public const string Water = "water";
    public const string Chasm = "chasm";
    public const string Confluence = "confluence";
}

public static class EndlessLandmarkWallPattern
{
    public const string Terrace = "terrace";
    public const string Buttress = "buttress";
    public const string Islands = "islands";
    public const string Labyrinth = "labyrinth";
    public const string Garden = "garden";
    public const string Scar = "scar";
    public const string Ruins = "ruins";
    public const string Crown = "crown";
    public const string Gate = "gate";
    public const string Delta = "delta";
}

public sealed class EndlessLandmarkVariant
{
    /// <summary>Stable zero-based catalog slot. `index + 1` is safe to store in an unsigned-byte terrain mask.</summary>
    public int index;
    public string id = "";
    public string title = "";
    public string family = "";
    public int familyIndex;
    public string motif = "";
    public int motifIndex;
    public string hazard = "";
    public string wallPattern = "";
    public double radius;
    public double routeSpokes;
    public double routeRingRatio;
    public double waterWidth;
    public double chasmWidth;
    public double wallDensity;
    public double dressingBoost;
    public double phase;
    /// <summary>`1 | 2 | 3 | 4`.</summary>
    public int symmetry;
}

public static partial class EndlessLandmark
{
    private sealed class EndlessLandmarkFamilyDefinition
    {
        public string key = "";
        public string title = "";
        public string hazard = "";
        public string wallPattern = "";
        public double radius;
        public double routeSpokes;
        public double routeRingRatio;
        public double waterWidth;
        public double chasmWidth;
        public double wallDensity;
        public double dressingBoost;
    }

    private sealed class EndlessLandmarkMotifDefinition
    {
        public string key = "";
        public string title = "";
        public double radiusDelta;
        public double spokeDelta;
        public double ringDelta;
        public double phase;
        /// <summary>`1 | 2 | 3 | 4`.</summary>
        public int symmetry;
        public double hazardScale;
        public double wallScale;
        public double dressingScale;
    }

    private static readonly IReadOnlyList<EndlessLandmarkFamilyDefinition> FAMILIES = new EndlessLandmarkFamilyDefinition[]
    {
        new()
        {
            key = EndlessLandmarkFamily.CascadeTerrace,
            title = "Cascade Terrace",
            hazard = EndlessLandmarkHazard.Water,
            wallPattern = EndlessLandmarkWallPattern.Terrace,
            radius = 19,
            routeSpokes = 3,
            routeRingRatio = 0.58,
            waterWidth = 4.8,
            chasmWidth = 0,
            wallDensity = 0.56,
            dressingBoost = 1.22,
        },
        new()
        {
            key = EndlessLandmarkFamily.ChasmCrossing,
            title = "Chasm Crossing",
            hazard = EndlessLandmarkHazard.Chasm,
            wallPattern = EndlessLandmarkWallPattern.Buttress,
            radius = 18,
            routeSpokes = 4,
            routeRingRatio = 0.61,
            waterWidth = 0,
            chasmWidth = 4.2,
            wallDensity = 0.5,
            dressingBoost = 1.18,
        },
        new()
        {
            key = EndlessLandmarkFamily.CliffGrove,
            title = "Cliff Grove",
            hazard = EndlessLandmarkHazard.None,
            wallPattern = EndlessLandmarkWallPattern.Islands,
            radius = 20,
            routeSpokes = 3,
            routeRingRatio = 0.54,
            waterWidth = 0,
            chasmWidth = 0,
            wallDensity = 0.64,
            dressingBoost = 1.42,
        },
        new()
        {
            key = EndlessLandmarkFamily.LabyrinthCourt,
            title = "Labyrinth Court",
            hazard = EndlessLandmarkHazard.None,
            wallPattern = EndlessLandmarkWallPattern.Labyrinth,
            radius = 19,
            routeSpokes = 4,
            routeRingRatio = 0.66,
            waterWidth = 0,
            chasmWidth = 0,
            wallDensity = 0.78,
            dressingBoost = 1.05,
        },
        new()
        {
            key = EndlessLandmarkFamily.FloodedGarden,
            title = "Flooded Garden",
            hazard = EndlessLandmarkHazard.Water,
            wallPattern = EndlessLandmarkWallPattern.Garden,
            radius = 20,
            routeSpokes = 4,
            routeRingRatio = 0.6,
            waterWidth = 5.5,
            chasmWidth = 0,
            wallDensity = 0.43,
            dressingBoost = 1.36,
        },
        new()
        {
            key = EndlessLandmarkFamily.CrystalScar,
            title = "Crystal Scar",
            hazard = EndlessLandmarkHazard.Chasm,
            wallPattern = EndlessLandmarkWallPattern.Scar,
            radius = 18,
            routeSpokes = 3,
            routeRingRatio = 0.56,
            waterWidth = 0,
            chasmWidth = 3.6,
            wallDensity = 0.58,
            dressingBoost = 1.3,
        },
        new()
        {
            key = EndlessLandmarkFamily.RuinedCauseway,
            title = "Ruined Causeway",
            hazard = EndlessLandmarkHazard.Confluence,
            wallPattern = EndlessLandmarkWallPattern.Ruins,
            radius = 21,
            routeSpokes = 4,
            routeRingRatio = 0.64,
            waterWidth = 4.2,
            chasmWidth = 3.4,
            wallDensity = 0.53,
            dressingBoost = 1.16,
        },
        new()
        {
            key = EndlessLandmarkFamily.SinkholeCrown,
            title = "Sinkhole Crown",
            hazard = EndlessLandmarkHazard.Chasm,
            wallPattern = EndlessLandmarkWallPattern.Crown,
            radius = 20,
            routeSpokes = 4,
            routeRingRatio = 0.7,
            waterWidth = 0,
            chasmWidth = 4.8,
            wallDensity = 0.62,
            dressingBoost = 1.24,
        },
        new()
        {
            key = EndlessLandmarkFamily.RidgeGate,
            title = "Ridge Gate",
            hazard = EndlessLandmarkHazard.None,
            wallPattern = EndlessLandmarkWallPattern.Gate,
            radius = 18,
            routeSpokes = 3,
            routeRingRatio = 0.57,
            waterWidth = 0,
            chasmWidth = 0,
            wallDensity = 0.76,
            dressingBoost = 1.12,
        },
        new()
        {
            key = EndlessLandmarkFamily.AbyssalConfluence,
            title = "Abyssal Confluence",
            hazard = EndlessLandmarkHazard.Confluence,
            wallPattern = EndlessLandmarkWallPattern.Delta,
            radius = 22,
            routeSpokes = 4,
            routeRingRatio = 0.62,
            waterWidth = 5.2,
            chasmWidth = 4.4,
            wallDensity = 0.48,
            dressingBoost = 1.34,
        },
    };

    private static readonly IReadOnlyList<EndlessLandmarkMotifDefinition> MOTIFS = new EndlessLandmarkMotifDefinition[]
    {
        new()
        {
            key = EndlessLandmarkMotif.Crown,
            title = "Crown",
            radiusDelta = 0,
            spokeDelta = 0,
            ringDelta = 0.03,
            phase = 0,
            symmetry = 4,
            hazardScale = 1,
            wallScale = 1.08,
            dressingScale = 1.08,
        },
        new()
        {
            key = EndlessLandmarkMotif.Fork,
            title = "Fork",
            radiusDelta = -1,
            spokeDelta = 1,
            ringDelta = -0.04,
            phase = 0.31,
            symmetry = 3,
            hazardScale = 0.88,
            wallScale = 0.94,
            dressingScale = 1.02,
        },
        new()
        {
            key = EndlessLandmarkMotif.Crescent,
            title = "Crescent",
            radiusDelta = 2,
            spokeDelta = -1,
            ringDelta = 0.06,
            phase = 0.73,
            symmetry = 1,
            hazardScale = 1.12,
            wallScale = 0.9,
            dressingScale = 1.18,
        },
        new()
        {
            key = EndlessLandmarkMotif.TwinRing,
            title = "Twin Ring",
            radiusDelta = 1,
            spokeDelta = 0,
            ringDelta = 0.09,
            phase = 1.07,
            symmetry = 2,
            hazardScale = 1.04,
            wallScale = 1.04,
            dressingScale = 0.96,
        },
        new()
        {
            key = EndlessLandmarkMotif.Switchback,
            title = "Switchback",
            radiusDelta = 3,
            spokeDelta = 1,
            ringDelta = -0.07,
            phase = 1.41,
            symmetry = 2,
            hazardScale = 0.92,
            wallScale = 1.12,
            dressingScale = 1.06,
        },
        new()
        {
            key = EndlessLandmarkMotif.BrokenSpokes,
            title = "Broken Spokes",
            radiusDelta = 0,
            spokeDelta = 2,
            ringDelta = 0,
            phase = 1.89,
            symmetry = 4,
            hazardScale = 1.08,
            wallScale = 1.16,
            dressingScale = 0.9,
        },
        new()
        {
            key = EndlessLandmarkMotif.Serpentine,
            title = "Serpentine",
            radiusDelta = 2,
            spokeDelta = 0,
            ringDelta = -0.02,
            phase = 2.27,
            symmetry = 1,
            hazardScale = 1.2,
            wallScale = 0.88,
            dressingScale = 1.14,
        },
        new()
        {
            key = EndlessLandmarkMotif.Triad,
            title = "Triad",
            radiusDelta = -2,
            spokeDelta = 1,
            ringDelta = 0.04,
            phase = 2.71,
            symmetry = 3,
            hazardScale = 0.96,
            wallScale = 1.02,
            dressingScale = 1.22,
        },
        new()
        {
            key = EndlessLandmarkMotif.Spiral,
            title = "Spiral",
            radiusDelta = 3,
            spokeDelta = -1,
            ringDelta = 0.01,
            phase = 3.19,
            symmetry = 1,
            hazardScale = 1.16,
            wallScale = 1.1,
            dressingScale = 1.1,
        },
        new()
        {
            key = EndlessLandmarkMotif.Mirror,
            title = "Mirror",
            radiusDelta = 1,
            spokeDelta = 0,
            ringDelta = -0.05,
            phase = 3.67,
            symmetry = 2,
            hazardScale = 1,
            wallScale = 0.98,
            dressingScale = 1,
        },
    };

    public static readonly int ENDLESS_LANDMARK_VARIANT_COUNT = FAMILIES.Count * MOTIFS.Count;

    public static readonly IReadOnlyList<EndlessLandmarkVariant> ENDLESS_LANDMARK_VARIANTS = buildVariants();

    /// <summary>`FAMILIES.flatMap((family, familyIndex) => MOTIFS.map((motif, motifIndex) => …))` in catalog order.</summary>
    private static List<EndlessLandmarkVariant> buildVariants()
    {
        var variants = new List<EndlessLandmarkVariant>(FAMILIES.Count * MOTIFS.Count);
        for (int familyIndex = 0; familyIndex < FAMILIES.Count; familyIndex++)
        {
            EndlessLandmarkFamilyDefinition family = FAMILIES[familyIndex];
            for (int motifIndex = 0; motifIndex < MOTIFS.Count; motifIndex++)
            {
                EndlessLandmarkMotifDefinition motif = MOTIFS[motifIndex];
                int index = familyIndex * MOTIFS.Count + motifIndex;
                variants.Add(new EndlessLandmarkVariant
                {
                    index = index,
                    id = $"{family.key}:{motif.key}",
                    title = $"{family.title} / {motif.title}",
                    family = family.key,
                    familyIndex = familyIndex,
                    motif = motif.key,
                    motifIndex = motifIndex,
                    hazard = family.hazard,
                    wallPattern = family.wallPattern,
                    radius = family.radius + motif.radiusDelta,
                    routeSpokes = Math.max(2, Math.min(6, family.routeSpokes + motif.spokeDelta)),
                    routeRingRatio = family.routeRingRatio + motif.ringDelta,
                    waterWidth = family.waterWidth * motif.hazardScale,
                    chasmWidth = family.chasmWidth * motif.hazardScale,
                    wallDensity = Math.min(0.9, family.wallDensity * motif.wallScale),
                    dressingBoost = Math.min(1.65, family.dressingBoost * motif.dressingScale),
                    phase = motif.phase,
                    symmetry = motif.symmetry,
                });
            }
        }
        return variants;
    }

    public static EndlessLandmarkVariant endlessLandmarkVariantAt(double index)
    {
        double normalized =
            ((Math.trunc(index) % ENDLESS_LANDMARK_VARIANT_COUNT) + ENDLESS_LANDMARK_VARIANT_COUNT) %
            ENDLESS_LANDMARK_VARIANT_COUNT;
        return ENDLESS_LANDMARK_VARIANTS[(int)normalized];
    }
}
