// Port of packages/client/src/render/environment/biomeLightingComposition.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `Object.freeze` has no runtime counterpart; the composition records are immutable by construction (readonly
//   fields) and shared by identity exactly like the frozen originals.
// * `PROFILE_BY_KEY` / `RAID_PROFILE_BY_TIER` are plain-object lookups that are never iterated → Dictionary. (A plain
//   JS object would also answer inherited keys such as "constructor"; biome keys are registry keys, never those.)
// * `/^raid_(\d+)$/`: JS `\d` is ASCII-only and JS `$` (no `m` flag) matches only at the very end, so the .NET
//   pattern is `^raid_([0-9]+)\z`. `Number(raidTier)` of a digit string is the correctly rounded double
//   (double.Parse), and the numeric record key matches only an exact integer 1..12.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using static Fluitown.Render.TerrainLightRig;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class WorldLightDirection
{
    public readonly double x;
    public readonly double y;
    public readonly double z;

    public WorldLightDirection(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }
}

/// <summary>
/// Per-family split-tone signature applied in the final grade pass: authored two-colour identity
/// (cool-family shade / warm-family light) in relative linear multipliers centred on 1. Every value is
/// bounded by the shared PIGMENT_LAW (test-enforced) so no place forks the paint language.
/// </summary>
public sealed class BiomeGradeSignature
{
    /// <summary>`readonly [number, number, number]`.</summary>
    public IReadOnlyList<double> shadowBalance = Array.Empty<double>();
    /// <summary>`readonly [number, number, number]`.</summary>
    public IReadOnlyList<double> highlightBalance = Array.Empty<double>();
    public double shadowStrength;
    public double highlightStrength;
    public double midtoneDensity;
}

/// <summary>
/// Authored photographic composition for a family of places.
///
/// Intensities remain on the biome's WorldStyle. This profile owns where the key/fill come from, their subtle
/// chromatic bias, the family's split-tone grade signature and how strongly the place uses aerial perspective.
/// Every direction keeps a high positive Y: cliffs receive broad form light, never a grazing white razor at an
/// edge-on raster seam — but elevations sit low enough (~51–69°) that every family throws readable NW→SE form
/// shadows under the key-dominant rig.
/// </summary>
public sealed class BiomeLightingComposition
{
    /// <summary>'sanctuary' | 'alpine' | 'engineered' | 'celestial' | 'citadel' | 'dream' | 'drowned' | 'arcane' | 'infernal'.</summary>
    public readonly string id;
    public readonly WorldLightDirection sunDirection;
    public readonly WorldLightDirection fillDirection;
    public readonly int keyTint;
    public readonly int skyTint;
    public readonly int fillTint;
    /// <summary>Multiplier on the biome surface profile's bounded aerial-perspective budget.</summary>
    public readonly double aerialPerspective;
    public readonly BiomeGradeSignature grade;

    public BiomeLightingComposition(
        string id,
        WorldLightDirection sunDirection,
        WorldLightDirection fillDirection,
        int keyTint,
        int skyTint,
        int fillTint,
        double aerialPerspective,
        BiomeGradeSignature grade)
    {
        this.id = id;
        this.sunDirection = sunDirection;
        this.fillDirection = fillDirection;
        this.keyTint = keyTint;
        this.skyTint = skyTint;
        this.fillTint = fillTint;
        this.aerialPerspective = aerialPerspective;
        this.grade = grade;
    }
}

public static partial class BiomeLightingCompositionModule
{
    private static BiomeLightingComposition composition(
        string id,
        WorldLightDirection sunDirection,
        WorldLightDirection fillDirection,
        int keyTint,
        int skyTint,
        int fillTint,
        double aerialPerspective,
        BiomeGradeSignature grade)
    {
        return new BiomeLightingComposition(
            id,
            sunDirection,
            fillDirection,
            keyTint,
            skyTint,
            fillTint,
            aerialPerspective,
            grade);
    }

    // Split-tone signatures: shade drifts toward the family's cool pigment, light toward its warm one.
    // Strengths sit above the neutral default (0.07/0.06) so every place has a readable two-colour identity,
    // but inside PIGMENT_LAW so the shared paper/ink language always wins.
    private static readonly BiomeLightingComposition SANCTUARY = composition(
        "sanctuary",
        // `LIGHT_RIG.sunDir` / `LIGHT_RIG.fillDir` — the rig's own direction records.
        new WorldLightDirection(LIGHT_RIG.sunDir.x, LIGHT_RIG.sunDir.y, LIGHT_RIG.sunDir.z),
        new WorldLightDirection(LIGHT_RIG.fillDir.x, LIGHT_RIG.fillDir.y, LIGHT_RIG.fillDir.z),
        0xffd8a6,
        0xd8eadf,
        0xb9d8e8,
        0.86,
        new BiomeGradeSignature
        {
            // Storybook warmth: honeyed light, soft blue-violet shade.
            shadowBalance = new[] { 0.92, 0.97, 1.09 },
            highlightBalance = new[] { 1.07, 1.02, 0.94 },
            shadowStrength = 0.1,
            highlightStrength = 0.09,
            midtoneDensity = 0.06,
        });
    private static readonly BiomeLightingComposition ALPINE = composition(
        "alpine",
        new WorldLightDirection(-0.72, 1.18, -0.32),
        new WorldLightDirection(0.34, 0.78, 0.86),
        0xffe7bb,
        0xc5def0,
        0xb3d5eb,
        0.98,
        new BiomeGradeSignature
        {
            // Fresh mountain morning: golden sun, crisp sky-blue shade.
            shadowBalance = new[] { 0.9, 0.96, 1.12 },
            highlightBalance = new[] { 1.08, 1.03, 0.93 },
            shadowStrength = 0.11,
            highlightStrength = 0.1,
            midtoneDensity = 0.055,
        });
    private static readonly BiomeLightingComposition ENGINEERED = composition(
        "engineered",
        new WorldLightDirection(-0.34, 1.02, -0.72),
        new WorldLightDirection(0.7, 0.82, 0.34),
        0x9ee8f2,
        0x778baa,
        0xa9b8ff,
        1.08,
        new BiomeGradeSignature
        {
            // Noir machine city: deep indigo shade, cool neon-cyan light.
            shadowBalance = new[] { 0.88, 0.94, 1.15 },
            highlightBalance = new[] { 0.98, 1.04, 1.1 },
            shadowStrength = 0.13,
            highlightStrength = 0.11,
            midtoneDensity = 0.07,
        });
    private static readonly BiomeLightingComposition CELESTIAL = composition(
        "celestial",
        new WorldLightDirection(-0.5, 1.42, -0.22),
        new WorldLightDirection(0.2, 0.88, 0.86),
        0xffe1aa,
        0xdce9ff,
        0xbfd4ef,
        0.94,
        new BiomeGradeSignature
        {
            // Divine above-the-clouds gold against airy blue shade. Keeps the family's high top light.
            shadowBalance = new[] { 0.9, 0.95, 1.13 },
            highlightBalance = new[] { 1.1, 1.04, 0.92 },
            shadowStrength = 0.11,
            highlightStrength = 0.11,
            midtoneDensity = 0.06,
        });
    private static readonly BiomeLightingComposition CITADEL = composition(
        "citadel",
        new WorldLightDirection(-0.58, 1.32, -0.3),
        new WorldLightDirection(0.4, 0.82, 0.72),
        0xf0f0ed,
        0xd8dade,
        0xaeb2b7,
        0.82,
        new BiomeGradeSignature
        {
            // Broad paper-white planes and compact ink shade replace the former glossy marble response.
            shadowBalance = new[] { 0.98, 0.99, 1.02 },
            highlightBalance = new[] { 1.01, 1, 0.98 },
            shadowStrength = 0.065,
            highlightStrength = 0.055,
            midtoneDensity = 0.08,
        });
    private static readonly BiomeLightingComposition DREAM = composition(
        "dream",
        new WorldLightDirection(-0.64, 1.12, -0.3),
        new WorldLightDirection(0.36, 0.8, 0.78),
        0xffd1bd,
        0xd8d8f1,
        0xc5c9ef,
        0.9,
        new BiomeGradeSignature
        {
            // Sakura dream: rosé light, violet shade.
            shadowBalance = new[] { 1.0, 0.91, 1.11 },
            highlightBalance = new[] { 1.09, 0.99, 0.97 },
            shadowStrength = 0.12,
            highlightStrength = 0.11,
            midtoneDensity = 0.065,
        });
    private static readonly BiomeLightingComposition DROWNED = composition(
        "drowned",
        new WorldLightDirection(-0.28, 1.16, -0.74),
        new WorldLightDirection(0.54, 0.76, 0.54),
        0x8dd5df,
        0x557e92,
        0x74b9cd,
        1.14,
        new BiomeGradeSignature
        {
            // Drowned light: indigo depth shade, cyan filtered light. Diffuse elevation stays — water scatters.
            shadowBalance = new[] { 0.86, 0.95, 1.14 },
            highlightBalance = new[] { 0.96, 1.06, 1.05 },
            shadowStrength = 0.13,
            highlightStrength = 0.1,
            midtoneDensity = 0.06,
        });
    private static readonly BiomeLightingComposition ARCANE = composition(
        "arcane",
        new WorldLightDirection(-0.42, 1.04, -0.62),
        new WorldLightDirection(0.64, 0.76, 0.42),
        0xc7b5ff,
        0x8797c8,
        0x8ccce0,
        1.05,
        new BiomeGradeSignature
        {
            // Arcane archive: violet shade, teal-glass light.
            shadowBalance = new[] { 0.94, 0.9, 1.14 },
            highlightBalance = new[] { 0.98, 1.06, 1.03 },
            shadowStrength = 0.13,
            highlightStrength = 0.11,
            midtoneDensity = 0.07,
        });
    private static readonly BiomeLightingComposition INFERNAL = composition(
        "infernal",
        new WorldLightDirection(-0.74, 0.95, -0.2),
        new WorldLightDirection(0.22, 0.7, 0.94),
        0xffb06e,
        0x9b8290,
        0xe48b6a,
        1.02,
        new BiomeGradeSignature
        {
            // Forge light: low ember sun (the family's longest shadows), cool soot shade.
            shadowBalance = new[] { 0.94, 0.95, 1.06 },
            highlightBalance = new[] { 1.14, 1.02, 0.88 },
            shadowStrength = 0.1,
            highlightStrength = 0.13,
            midtoneDensity = 0.075,
        });

    private static readonly Dictionary<string, BiomeLightingComposition> PROFILE_BY_KEY = new()
    {
        ["hub"] = SANCTUARY,
        ["pockettown"] = SANCTUARY,
        ["museum"] = SANCTUARY,
        ["highland_pass"] = ALPINE,
        ["viking_ship_village"] = ALPINE,
        ["alien_ranch"] = DREAM,
        ["noir_sprawl"] = ENGINEERED,
        ["olympian_sky_borough"] = CELESTIAL,
        ["aegis_citadel"] = CITADEL,
        ["sakura_temple_dream"] = DREAM,
        ["abyssal_deepsea"] = DROWNED,
        ["rainbowland"] = CELESTIAL,
        ["clockwork_moon_bazaar"] = ENGINEERED,
        ["sugarstorm_carnival"] = DREAM,
        ["prismglass_archive"] = ARCANE,
        ["starforged_cathedral_endrun"] = ARCANE,
        ["machineworks"] = ENGINEERED,
        ["tidecage"] = DROWNED,
        ["papertemple"] = DREAM,
        ["wyrmforge"] = INFERNAL,
        ["moonroot"] = DREAM,
        ["crownbower"] = CELESTIAL,
        ["starossuary"] = ARCANE,
        ["resonanceeyrie"] = ARCANE,
        ["regrowthcanals"] = DROWNED,
        ["hollowcartography"] = ENGINEERED,
        ["arena"] = INFERNAL,
    };

    private static readonly Dictionary<int, BiomeLightingComposition> RAID_PROFILE_BY_TIER = new()
    {
        [1] = ENGINEERED,
        [2] = DROWNED,
        [3] = DREAM,
        [4] = INFERNAL,
        [5] = DREAM,
        [6] = CITADEL,
        [7] = CELESTIAL,
        [8] = ARCANE,
        [9] = ARCANE,
        [10] = DROWNED,
        [12] = ENGINEERED,
    };

    private static readonly Regex RAID_TIER_PATTERN = new Regex(@"^raid_([0-9]+)\z", RegexOptions.CultureInvariant);

    /// <summary>Resolve one stable composition without allocating. Unknown places inherit the safe sanctuary rig.</summary>
    public static BiomeLightingComposition biomeLightingComposition(Biome biome)
    {
        // `PROFILE_BY_KEY[biome.key]`; an absent key (JS "undefined") is simply not in the table.
        if (biome.key != null && PROFILE_BY_KEY.TryGetValue(biome.key, out BiomeLightingComposition? direct)) return direct;
        // `.exec(biome.key)` stringifies an absent key to "undefined", which never matches.
        Match match = RAID_TIER_PATTERN.Match(biome.key ?? "undefined");
        string? raidTier = match.Success ? match.Groups[1].Value : null;
        if (!string.IsNullOrEmpty(raidTier))
        {
            double tier = double.Parse(raidTier, NumberStyles.None, CultureInfo.InvariantCulture);
            return tier >= 1 && tier <= 12 && tier == Math.floor(tier) &&
                RAID_PROFILE_BY_TIER.TryGetValue((int)tier, out BiomeLightingComposition? raid)
                ? raid
                : ARCANE;
        }
        return SANCTUARY;
    }
}
