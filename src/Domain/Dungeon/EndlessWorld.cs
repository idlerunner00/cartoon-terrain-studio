// Port of packages/shared/src/domain/dungeon/endlessWorld.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using static Fluitown.Domain.EndlessMacro;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Per-cohort composition DNA for the endless world.
//
// A run theme defines the durable fantasy (Highland Pass stays alpine, Noir Sprawl stays dense and urban),
// while the complete instance id is the seed of one rolling cohort. This layer turns that seed into a bounded
// structural signature: one cohort favours winding warrens, another chamber chains, another fractured wild
// country. The variation is deliberately stronger than ordinary tile noise but remains inside the theme's
// authored limits, so repeat runs feel composed differently without losing their identity.
//
// Every value is a pure function of `(seed, biomeKey)`. Server collision and client regeneration therefore use
// the same profile without persistence, protocol fields, mutable registries or `Math.random()`.

public static class EndlessWorldCadence
{
    /// <summary>Tight, winding country with occasional chambers as contrast.</summary>
    public const string Winding = "winding";
    /// <summary>A chain of authored combat rooms and larger halls.</summary>
    public const string Chambers = "chambers";
    /// <summary>Organic caverns and glades interrupt the maze more often.</summary>
    public const string Wild = "wild";
    /// <summary>Large halls, landmarks and rare arenas establish a monumental rhythm.</summary>
    public const string Monumental = "monumental";
    /// <summary>Broken rock, rifts and tight passages dominate the silhouette.</summary>
    public const string Fractured = "fractured";
}

public sealed class EndlessArchetypeBias
{
    public double warren;
    public double rooms;
    public double halls;
    public double cavern;
    public double arena;
    public double glade;
    public double courtyard;
    public double ruins;
}

/// <summary>Regional spatial grammars. They never change the run theme; they change how that theme composes space.</summary>
public static class EndlessSectionKind
{
    public const string Labyrinth = "labyrinth";
    public const string Chambers = "chamber_chain";
    public const string Wilds = "wilds";
    public const string Monumental = "monumental";
    public const string Floodplain = "floodplain";
    public const string Fractured = "fractured";
}

public sealed class EndlessSectionTraits
{
    /// <summary>Scales authored combat courts without changing the global maze pitch.</summary>
    public double roomScale;
    /// <summary>Number of authored room beats placed around graph junctions.</summary>
    public double roomDensity;
    /// <summary>Pillar/rubble density in halls, courtyards and ruins.</summary>
    public double coverDensity;
    /// <summary>Short optional side-pocket density in tight maze country.</summary>
    public double alcoveDensity;
    /// <summary>Probability that port approaches use a multi-bend organic route.</summary>
    public double pathWander;
}

public sealed class EndlessSectionSample
{
    public string dominant = "";
    /// <summary>
    /// Smooth normalized weights (`Record&lt;EndlessSectionKind, number&gt;`, keys in SECTION_KINDS order). Neighbouring
    /// chunks blend instead of switching a hard preset at a seam.
    /// </summary>
    public JsMap<string, double> weights;
    public double dominantStrength;
    /// <summary>High near a balanced border between grammars, low in the heart of one coherent district.</summary>
    public double transition;
    public EndlessArchetypeBias archetypes;
    public EndlessSectionTraits traits;
}

public sealed class EndlessWorldProfile
{
    /// <summary>Theme DNA after bounded cohort-level variation.</summary>
    public MacroDna macro;
    public string cadence = "";
    public EndlessArchetypeBias archetypes;
    public double mazeBraidBase;
    public double mazeBraidRange;
    /// <summary>Fractional strength of coherent wall veins retained inside otherwise open macro basins.</summary>
    public double basinWallRetention;
    /// <summary>Additive score for the native Chasm field. Kept small; topology gates still own final shape.</summary>
    public double chasmScoreBias;
    /// <summary>Extra score on land touching real Water, creating deliberate river-to-ravine set pieces.</summary>
    public double chasmWaterAffinity;
    /// <summary>Strength of broad sinkhole lobes mixed with the long ridge field.</summary>
    public double chasmBasinStrength;
}

public static partial class EndlessWorld
{
    private static class PROFILE_SALTS
    {
        public const uint cadence = 0x2f6e2b1d;
        public const uint openness = 0x6c8e9cf5;
        public const uint rock = 0x51ab3e75;
        public const uint relief = 0x27d4eb2f;
        public const uint featureScale = 0x7a2fb1c9;
        public const uint latticePitch = 0x3d9f42a7;
        public const uint family = 0x18bf54a7;
        public const uint braid = 0x6ea3c8d1;
        public const uint basinRetention = 0x5bd1e995;
        public const uint chasm = 0x4cf5ad43;
        public const uint waterAffinity = 0x133111eb;
        public const uint chasmBasin = 0x94d049bb;
    }

    // `(seed ^ salt) >>> 0` is `Js.ToUint32(seed) ^ salt`: the same low 32 bits, reinterpreted as unsigned.
    private static double sample(double seed, uint salt)
    {
        return latticeHash(Js.ToUint32(seed) ^ salt, 0, 0);
    }

    private static double clamp(double value, double min, double max)
    {
        return value < min ? min : value > max ? max : value;
    }

    private static string cadenceAt(double seed)
    {
        string[] deck =
        {
            EndlessWorldCadence.Winding,
            EndlessWorldCadence.Chambers,
            EndlessWorldCadence.Wild,
            EndlessWorldCadence.Monumental,
            EndlessWorldCadence.Fractured,
        };
        return deck[(int)Math.min(deck.Length - 1, Math.floor(sample(seed, PROFILE_SALTS.cadence) * deck.Length))];
    }

    private static EndlessArchetypeBias cadenceBias(string cadence)
    {
        switch (cadence)
        {
            case EndlessWorldCadence.Winding:
                return new EndlessArchetypeBias
                {
                    warren = 1.42,
                    rooms = 1.02,
                    halls = 0.78,
                    cavern = 0.88,
                    arena = 0.68,
                    glade = 0.78,
                    courtyard = 0.72,
                    ruins = 1.08,
                };
            case EndlessWorldCadence.Chambers:
                return new EndlessArchetypeBias
                {
                    warren = 0.78,
                    rooms = 1.48,
                    halls = 1.32,
                    cavern = 0.72,
                    arena = 0.82,
                    glade = 0.82,
                    courtyard = 1.3,
                    ruins = 0.9,
                };
            case EndlessWorldCadence.Wild:
                return new EndlessArchetypeBias
                {
                    warren = 0.82,
                    rooms = 0.76,
                    halls = 0.7,
                    cavern = 1.58,
                    arena = 0.72,
                    glade = 1.34,
                    courtyard = 0.74,
                    ruins = 1.18,
                };
            case EndlessWorldCadence.Monumental:
                return new EndlessArchetypeBias
                {
                    warren = 0.68,
                    rooms = 0.92,
                    halls = 1.38,
                    cavern = 0.72,
                    arena = 1.5,
                    glade = 1.08,
                    courtyard = 1.62,
                    ruins = 1.04,
                };
            case EndlessWorldCadence.Fractured:
                return new EndlessArchetypeBias
                {
                    warren = 1.12,
                    rooms = 0.82,
                    halls = 1.14,
                    cavern = 1.26,
                    arena = 0.84,
                    glade = 0.72,
                    courtyard = 0.68,
                    ruins = 1.58,
                };
        }
        // Unreachable for a real cadence (the TS switch is exhaustive and falls through to `undefined`).
        return null!;
    }

    private static readonly string[] SECTION_KINDS =
    {
        EndlessSectionKind.Labyrinth,
        EndlessSectionKind.Chambers,
        EndlessSectionKind.Wilds,
        EndlessSectionKind.Monumental,
        EndlessSectionKind.Floodplain,
        EndlessSectionKind.Fractured,
    };

    private sealed class EndlessSectionGrammar
    {
        public uint salt;
        public double scale;
        public EndlessArchetypeBias archetypes;
        public EndlessSectionTraits traits;
    }

    /// <summary>
    /// Data-owned regional grammars. Adding another section is a registry entry plus its public key, not another
    /// generator branch. All spatial values are blended below; the `dominant` key exists for audits and authoring.
    /// Only ever read by key (iteration goes through SECTION_KINDS).
    /// </summary>
    private static readonly Dictionary<string, EndlessSectionGrammar> SECTION_GRAMMARS = new()
    {
        [EndlessSectionKind.Labyrinth] = new EndlessSectionGrammar
        {
            salt = 0x2c1b3c6d,
            scale = 5.2,
            archetypes = new EndlessArchetypeBias
            {
                warren = 1.85,
                rooms = 1.08,
                halls = 0.62,
                cavern = 0.5,
                arena = 0.38,
                glade = 0.48,
                courtyard = 0.48,
                ruins = 1.02,
            },
            traits = new EndlessSectionTraits
            {
                roomScale = 0.84,
                roomDensity = 0.3,
                coverDensity = 0.48,
                alcoveDensity = 1,
                pathWander = 0.9,
            },
        },
        [EndlessSectionKind.Chambers] = new EndlessSectionGrammar
        {
            salt = 0x85ebca6b,
            scale = 5.8,
            archetypes = new EndlessArchetypeBias
            {
                warren = 0.64,
                rooms = 1.78,
                halls = 1.32,
                cavern = 0.46,
                arena = 0.72,
                glade = 0.55,
                courtyard = 1.42,
                ruins = 0.9,
            },
            traits = new EndlessSectionTraits
            {
                roomScale = 1.1,
                roomDensity = 0.96,
                coverDensity = 0.64,
                alcoveDensity = 0.5,
                pathWander = 0.62,
            },
        },
        [EndlessSectionKind.Wilds] = new EndlessSectionGrammar
        {
            salt = 0x165667b1,
            scale = 6.6,
            archetypes = new EndlessArchetypeBias
            {
                warren = 0.5,
                rooms = 0.58,
                halls = 0.5,
                cavern = 1.9,
                arena = 0.48,
                glade = 1.7,
                courtyard = 0.7,
                ruins = 1.2,
            },
            traits = new EndlessSectionTraits
            {
                roomScale = 1.18,
                roomDensity = 0.46,
                coverDensity = 0.36,
                alcoveDensity = 0.34,
                pathWander = 0.94,
            },
        },
        [EndlessSectionKind.Monumental] = new EndlessSectionGrammar
        {
            salt = 0xd3a2646c,
            scale = 7.2,
            archetypes = new EndlessArchetypeBias
            {
                warren = 0.42,
                rooms = 0.86,
                halls = 1.62,
                cavern = 0.42,
                arena = 1.82,
                glade = 0.82,
                courtyard = 1.92,
                ruins = 0.88,
            },
            traits = new EndlessSectionTraits
            {
                roomScale = 1.3,
                roomDensity = 0.68,
                coverDensity = 0.94,
                alcoveDensity = 0.2,
                pathWander = 0.45,
            },
        },
        [EndlessSectionKind.Floodplain] = new EndlessSectionGrammar
        {
            salt = 0x9e3779b9,
            scale = 6.1,
            archetypes = new EndlessArchetypeBias
            {
                warren = 0.52,
                rooms = 0.78,
                halls = 0.72,
                cavern = 1.18,
                arena = 0.5,
                glade = 1.62,
                courtyard = 1.28,
                ruins = 1.12,
            },
            traits = new EndlessSectionTraits
            {
                roomScale = 1.12,
                roomDensity = 0.58,
                coverDensity = 0.42,
                alcoveDensity = 0.28,
                pathWander = 0.74,
            },
        },
        [EndlessSectionKind.Fractured] = new EndlessSectionGrammar
        {
            salt = 0x7f4a7c15,
            scale = 4.7,
            archetypes = new EndlessArchetypeBias
            {
                warren = 1.18,
                rooms = 0.62,
                halls = 1.06,
                cavern = 1.36,
                arena = 0.74,
                glade = 0.34,
                courtyard = 0.56,
                ruins = 1.86,
            },
            traits = new EndlessSectionTraits
            {
                roomScale = 0.9,
                roomDensity = 0.5,
                coverDensity = 1,
                alcoveDensity = 0.86,
                pathWander = 0.96,
            },
        },
    };

    private static double cadenceSectionMultiplier(string cadence, string section)
    {
        if (cadence == EndlessWorldCadence.Winding && section == EndlessSectionKind.Labyrinth)
            return 1.32;
        if (cadence == EndlessWorldCadence.Chambers && section == EndlessSectionKind.Chambers)
            return 1.32;
        if (cadence == EndlessWorldCadence.Monumental && section == EndlessSectionKind.Monumental)
            return 1.32;
        if (cadence == EndlessWorldCadence.Fractured && section == EndlessSectionKind.Fractured)
            return 1.32;
        if (
            cadence == EndlessWorldCadence.Wild &&
            (section == EndlessSectionKind.Wilds || section == EndlessSectionKind.Floodplain)
        )
            return 1.2;
        return 0.96;
    }

    /// <summary>
    /// Resolve the smoothly blended section grammar at a chunk-lattice position. Fields are deliberately several
    /// chunks wide, so one motif forms a readable sequence before giving way through mixed transition chunks.
    /// </summary>
    public static EndlessSectionSample endlessSectionSampleAt(double seed, double cx, double cy, string cadence)
    {
        var weights = new JsMap<string, double>();
        double total = 0;
        foreach (string kind in SECTION_KINDS)
        {
            EndlessSectionGrammar grammar = SECTION_GRAMMARS[kind];
            double broad = valueNoise(Js.ToUint32(seed) ^ grammar.salt, cx, cy, grammar.scale);
            double shaped = 0.08 + Math.pow(clamp((broad - 0.24) / 0.62, 0, 1), 2.15);
            double weight = shaped * cadenceSectionMultiplier(cadence, kind);
            weights.set(kind, weight);
            total += weight;
        }
        string dominant = SECTION_KINDS[0];
        double strongest = double.NegativeInfinity;
        double second = double.NegativeInfinity;
        foreach (string kind in SECTION_KINDS)
        {
            double normalized = weights[kind] / Math.max(Number.EPSILON, total);
            weights.set(kind, normalized);
            if (normalized > strongest)
            {
                second = strongest;
                strongest = normalized;
                dominant = kind;
            }
            else if (normalized > second)
            {
                second = normalized;
            }
        }

        var archetypes = new EndlessArchetypeBias
        {
            warren = 0,
            rooms = 0,
            halls = 0,
            cavern = 0,
            arena = 0,
            glade = 0,
            courtyard = 0,
            ruins = 0,
        };
        var traits = new EndlessSectionTraits { roomScale = 0, roomDensity = 0, coverDensity = 0, alcoveDensity = 0, pathWander = 0 };
        foreach (string kind in SECTION_KINDS)
        {
            double weight = weights[kind];
            EndlessSectionGrammar grammar = SECTION_GRAMMARS[kind];
            // `for (const key of Object.keys(archetypes)) archetypes[key] += grammar.archetypes[key] * weight;`
            archetypes.warren += grammar.archetypes.warren * weight;
            archetypes.rooms += grammar.archetypes.rooms * weight;
            archetypes.halls += grammar.archetypes.halls * weight;
            archetypes.cavern += grammar.archetypes.cavern * weight;
            archetypes.arena += grammar.archetypes.arena * weight;
            archetypes.glade += grammar.archetypes.glade * weight;
            archetypes.courtyard += grammar.archetypes.courtyard * weight;
            archetypes.ruins += grammar.archetypes.ruins * weight;
            // `for (const key of Object.keys(traits)) traits[key] += grammar.traits[key] * weight;`
            traits.roomScale += grammar.traits.roomScale * weight;
            traits.roomDensity += grammar.traits.roomDensity * weight;
            traits.coverDensity += grammar.traits.coverDensity * weight;
            traits.alcoveDensity += grammar.traits.alcoveDensity * weight;
            traits.pathWander += grammar.traits.pathWander * weight;
        }
        return new EndlessSectionSample
        {
            dominant = dominant,
            weights = weights,
            dominantStrength = strongest,
            transition = clamp(1 - (strongest - Math.max(0, second)) * 3.2, 0, 1),
            archetypes = archetypes,
            traits = traits,
        };
    }

    private static MacroStyleWeights variedStyleWeights(double seed, MacroStyleWeights @base)
    {
        double family = Math.min(2, Math.floor(sample(seed, PROFILE_SALTS.family) * 3));
        double accent = 1.12 + sample(seed, PROFILE_SALTS.family ^ 0x5f356495) * 0.18;
        double quiet = 0.86 + sample(seed, PROFILE_SALTS.family ^ 0x2c1b3c6d) * 0.12;
        double open = @base.open * (family == 0 ? accent : quiet);
        double rock = @base.rock * (family == 1 ? accent : quiet);
        double mixed = @base.mixed * (family == 2 ? accent : quiet);
        // Keep at least eight percent of loci as residual labyrinth even for the loudest authored theme.
        double total = open + rock + mixed;
        if (total > 0.92)
        {
            double scale = 0.92 / total;
            open *= scale;
            rock *= scale;
            mixed *= scale;
        }
        return new MacroStyleWeights { open = open, rock = rock, mixed = mixed };
    }

    /// <summary>Resolve the bounded structural identity of one real Endless cohort.</summary>
    public static EndlessWorldProfile endlessWorldProfileFor(double seed, string? biomeKey)
    {
        MacroDna @base = endlessMacroDnaFor(biomeKey);
        string cadence = cadenceAt(seed);
        double cadenceOpenShift =
            cadence == EndlessWorldCadence.Winding
                ? -0.035
                : cadence == EndlessWorldCadence.Wild
                    ? 0.025
                    : cadence == EndlessWorldCadence.Monumental
                        ? 0.018
                        : cadence == EndlessWorldCadence.Fractured
                            ? -0.025
                            : 0;
        double cadenceRockShift =
            cadence == EndlessWorldCadence.Fractured
                ? 0.04
                : cadence == EndlessWorldCadence.Monumental
                    ? 0.025
                    : cadence == EndlessWorldCadence.Wild
                        ? -0.02
                        : 0;
        // `{ ...base, <overrides> }`: a shallow copy with the varied fields replaced.
        MacroDna macro = @base.Clone();
        macro.opennessBias = clamp(
            @base.opennessBias + cadenceOpenShift + (sample(seed, PROFILE_SALTS.openness) - 0.5) * 0.08,
            -0.42,
            0.42);
        macro.rockBias = clamp(
            @base.rockBias + cadenceRockShift + (sample(seed, PROFILE_SALTS.rock) - 0.5) * 0.075,
            -0.42,
            0.42);
        macro.reliefAmp = clamp(
            @base.reliefAmp * (0.88 + sample(seed, PROFILE_SALTS.relief) * 0.24),
            0.18,
            0.62);
        macro.featureScale = clamp(
            @base.featureScale * (0.86 + sample(seed, PROFILE_SALTS.featureScale) * 0.28),
            0.55,
            1.65);
        macro.latticePitch = clamp(
            @base.latticePitch * (0.91 + sample(seed, PROFILE_SALTS.latticePitch) * 0.18),
            4.5,
            8.35);
        macro.styleWeights = variedStyleWeights(seed, @base.styleWeights);
        double fracturedBonus = cadence == EndlessWorldCadence.Fractured ? 0.012 : 0;
        double wildBonus = cadence == EndlessWorldCadence.Wild ? 0.006 : 0;
        return new EndlessWorldProfile
        {
            macro = macro,
            cadence = cadence,
            archetypes = cadenceBias(cadence),
            mazeBraidBase = 0.07 + sample(seed, PROFILE_SALTS.braid) * 0.11,
            mazeBraidRange = 0.16 + sample(seed, PROFILE_SALTS.braid ^ 0x85ebca6b) * 0.17,
            basinWallRetention = 0.18 + sample(seed, PROFILE_SALTS.basinRetention) * 0.12,
            chasmScoreBias = 0.006 + sample(seed, PROFILE_SALTS.chasm) * 0.018 + fracturedBonus + wildBonus,
            chasmWaterAffinity = 0.03 + sample(seed, PROFILE_SALTS.waterAffinity) * 0.03,
            chasmBasinStrength = 0.035 + sample(seed, PROFILE_SALTS.chasmBasin) * 0.04,
        };
    }
}
