// Port of packages/shared/src/domain/dungeon/terrain.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * Per-biome **terrain profile** — the single, deterministic source of a run's large-scale landform: how
 * high its ground stands (ElevationProfile) and whether **rivers** cut across it. It is the
 * mountain-biome counterpart to elevation.ts: a pure function of `(seed, GLOBAL tile)`, so the
 * authoritative server (collision) and every client (rendering/minimap) carve byte-identical water and
 * bridges into the same tile grid with nothing to stream — exactly like the rest of the dungeon geometry.
 *
 * ── Rivers as a global field ──────────────────────────────────────────────────────────────────────
 * Rivers are continuous, gently meandering channels defined over the GLOBAL tile lattice (not per chunk),
 * so they flow seamlessly across chunk seams the same way the elevation terraces do. A channel runs along
 * the world's Y axis and weaves in X, so as the cohort presses along the run's forward (+X) axis it meets
 * rivers broadside and must take a **bridge** to continue — the river is a real movement barrier
 * (TileType.Water, which blocks movement but is transparent to sight/projectiles) and the bridge a
 * walkable crossing (TileType.Bridge).
 *
 * ── Connectivity by construction ──────────────────────────────────────────────────────────────────
 * applyMountainTerrain only ever turns *floor* into water (rooms are left dry), then lays a bridge
 * deck wherever a river crosses the chunk's guaranteed-connected corridor skeleton (the braided maze's
 * corridor mask) — so every seam port still reaches every other across every river, and the whole infinite
 * chunk grid stays one connected component. A final walkable flood-fill from a skeleton seed tile solidifies
 * any bank a river cut off with no bridge, so there are never orphan pockets or dead-end traps (the same
 * guarantee the base generator upholds, extended to water).
 */

/// <summary>Tunables for the river field — channel spacing, meander and width, in TILES. Pure data.</summary>
public sealed class RiverParams
{
    /// <summary>World-tile spacing between river channels (channels sit at multiples of this in global X).</summary>
    public double spacing;
    /// <summary>Primary meander amplitude (tiles) — the long, lazy weave of a channel across X.</summary>
    public double meanderAmp;
    /// <summary>Primary meander wavelength (tiles).</summary>
    public double meanderWave;
    /// <summary>Secondary (shorter) meander amplitude/wavelength, for a less mechanical line.</summary>
    public double meanderAmp2;
    public double meanderWave2;
    /// <summary>Base half-width of a channel (tiles); the river is `~2·half` tiles wide.</summary>
    public double halfWidth;
    /// <summary>How much the half-width breathes along the channel (tiles).</summary>
    public double halfWidthVar;
    /// <summary>Wavelength of the width breathing (tiles).</summary>
    public double widthWave;
    /// <summary>Maximum per-cohort rotation away from the traditional north/south flow axis, in radians.</summary>
    public double? orientationJitter;
    /// <summary>Relative strength of a crossing/parallel secondary channel family (0 disables it).</summary>
    public double? secondaryStrength;
    /// <summary>Strength of organic lake and wetland lobes around the channel network.</summary>
    public double? lakeStrength;
    /// <summary>Multiplier on dry-belt interruption. Below 1 keeps water present more consistently.</summary>
    public double? drynessStrength;
    /// <summary>Per-theme weights for the five procedural hydrology grammars (a 5-tuple).</summary>
    public double[]? styleWeights;
}

/// <summary>The five procedural hydrology grammars; the TS type `EndlessHydrologyStyle` is `string`.</summary>
public static class EndlessHydrologyStyle
{
    public const string Meander = "meander";
    public const string Braided = "braided";
    public const string Confluence = "confluence";
    public const string LakeChain = "lake_chain";
    public const string Wetland = "wetland";
}

public sealed class EndlessHydrologyIdentity
{
    /// <summary>An <see cref="EndlessHydrologyStyle"/> value.</summary>
    public string style = "";
    /// <summary>Rotation of the primary channel family in the global tile plane.</summary>
    public double angle;
    /// <summary>Crossing angle used by confluences and wetlands.</summary>
    public double secondaryAngle;
    /// <summary>Bounded per-cohort width/wetness multiplier.</summary>
    public double wetness;
    /// <summary>Bounded per-cohort basin emphasis.</summary>
    public double basinStrength;
}

/// <summary>Optional regional composition controls supplied by the Endless landscape layer.</summary>
public sealed class EndlessTerrainVariation
{
    /// <summary>Signed widening/narrowing of the authored channel network.</summary>
    public double waterBias;
    /// <summary>Strength of additional multi-chunk organic lake basins.</summary>
    public double lakeStrength;
    /// <summary>Strength of secondary contour channels/distributaries.</summary>
    public double channelComplexity;
    /// <summary>Strength of coherent dry islands retained inside proposed water.</summary>
    public double islandStrength;
}

/// <summary>A biome's large-scale landform: its elevation skew and (optionally) its rivers. Pure data.</summary>
public sealed class TerrainProfile
{
    /// <summary>Height skew applied to the shared elevation field (mountains skew high &amp; contrasty).</summary>
    public ElevationProfile elevation = null!;
    /// <summary>River field params, or `null` for a riverless biome.</summary>
    public RiverParams? rivers;
}

public static class Terrain
{
    /// <summary>
    /// The **mountain** biome (the first run): tall, contrasty ground (high peaks, deep valleys) cut by braided
    /// rivers you must bridge. The elevation skew stays mild enough that the terraces still read as walkable
    /// steps; the genuine barriers are the rock walls and the rivers.
    /// </summary>
    private static readonly TerrainProfile MOUNTAIN_TERRAIN = new()
    {
        // Mild skew: tall, contrasty ground, but gentle enough that the TOP band (snow peaks) is reached only on the
        // genuine summits — not most of the map — and the mid terraces stay several tiles wide, so a rock massif sits
        // on near-uniform ground instead of a dense staircase of one-tile steps (the old "venetian-blind" striping).
        elevation = new ElevationProfile(0.02, 1.16),
        rivers = new RiverParams
        {
            spacing = 38,
            meanderAmp = 6,
            meanderWave = 58,
            meanderAmp2 = 2.4,
            meanderWave2 = 21,
            halfWidth = 2.2,
            halfWidthVar = 0.65,
            widthWave = 37,
            orientationJitter = 0.5,
            secondaryStrength = 0.48,
            lakeStrength = 0.55,
            drynessStrength = 0.78,
            styleWeights = new[] { 0.3, 0.27, 0.16, 0.2, 0.07 },
        },
    };

    /// <summary>A flat, riverless biome — the default landform for every space that does not opt into mountains.</summary>
    private static readonly TerrainProfile NEUTRAL_TERRAIN = new() { elevation = Elevation.NEUTRAL_ELEVATION_PROFILE, rivers = null };

    private static readonly RiverParams VERDANT_RIVERS = new()
    {
        spacing = 44,
        meanderAmp = 5.6,
        meanderWave = 92,
        meanderAmp2 = 1.8,
        meanderWave2 = 37,
        halfWidth = 3.4,
        halfWidthVar = 0.6,
        widthWave = 58,
    };

    private static readonly RiverParams ASHEN_RIVERS = new()
    {
        spacing = 38,
        meanderAmp = 6.4,
        meanderWave = 78,
        meanderAmp2 = 2.2,
        meanderWave2 = 29,
        halfWidth = 3.5,
        halfWidthVar = 0.7,
        widthWave = 45,
    };

    private static readonly RiverParams VOID_RIVERS = new()
    {
        spacing = 47,
        meanderAmp = 6.8,
        meanderWave = 104,
        meanderAmp2 = 2.6,
        meanderWave2 = 35,
        halfWidth = 3.5,
        halfWidthVar = 0.72,
        widthWave = 54,
    };

    /* ── Per-run world themes (the ten endless runs) ──────────────────────────────────────────────────────
     * Every endless run's descriptor carries the run's own THEME key (`RunDef.themeKey`), so each of the ten
     * descents owns a bespoke landform here — matched to its name, never a colour-swap of its bracket sibling.
     * The client's theme registry mirrors these keys for palette/material/atmosphere; this side owns what the
     * ground DOES (relief + water), one profile per run. The biome-family entries below stay for raids, the
     * editor and legacy fixtures. */
    private static readonly Dictionary<string, TerrainProfile> RUN_TERRAIN = new()
    {
        // Run 1 — Highland Pass: the gentle alpine opener. Bridged braided rivers, plastic peaks (the authored
        // mountain profile IS this run; the Tutorial corridor wears it too).
        ["highland_pass"] = MOUNTAIN_TERRAIN,
        // Run 2 — Noir Sprawl: wet blacktop blocks between tower masses, crossed by oily service canals that are
        // nearly engineered-straight. Its `city` tileset dresses it with ad signs, steam gullies and neon puddles.
        ["noir_sprawl"] = new()
        {
            elevation = new ElevationProfile(-0.02, 1.18),
            rivers = new RiverParams
            {
                spacing = 34,
                meanderAmp = 0.85,
                meanderWave = 220,
                meanderAmp2 = 0.28,
                meanderWave2 = 72,
                halfWidth = 2.25,
                halfWidthVar = 0.18,
                widthWave = 96,
                orientationJitter = 0.12,
                secondaryStrength = 0.34,
                lakeStrength = 0.2,
                drynessStrength = 0.72,
                styleWeights = new[] { 0.46, 0.17, 0.25, 0.06, 0.06 },
            },
        },
        // Run 3 - Olympian Sky Borough: marble old-town islands above the clouds. High plateaus, airy gaps,
        // sky-blue cloud canals and narrow bridge crossings make the route feel suspended instead of grounded.
        ["olympian_sky_borough"] = new()
        {
            elevation = new ElevationProfile(0.08, 1.24),
            rivers = new RiverParams
            {
                spacing = 38,
                meanderAmp = 3.6,
                meanderWave = 132,
                meanderAmp2 = 1.15,
                meanderWave2 = 48,
                halfWidth = 1.95,
                halfWidthVar = 0.42,
                widthWave = 64,
                orientationJitter = 0.55,
                secondaryStrength = 0.48,
                lakeStrength = 0.68,
                drynessStrength = 0.68,
                styleWeights = new[] { 0.18, 0.14, 0.22, 0.34, 0.12 },
            },
        },
        // Run 4 - Sakura Temple Dream: quiet pond canals, pale temple terraces and lacquer bridge rhythm. The
        // channel field is calmer and more deliberate than verdant wilderness, so crossings read as garden bridges.
        ["sakura_temple_dream"] = new()
        {
            elevation = new ElevationProfile(-0.01, 1.1),
            rivers = new RiverParams
            {
                spacing = 36,
                meanderAmp = 2.9,
                meanderWave = 116,
                meanderAmp2 = 0.95,
                meanderWave2 = 46,
                halfWidth = 3.15,
                halfWidthVar = 0.32,
                widthWave = 74,
                orientationJitter = 0.34,
                secondaryStrength = 0.36,
                lakeStrength = 0.78,
                drynessStrength = 0.62,
                styleWeights = new[] { 0.21, 0.11, 0.17, 0.38, 0.13 },
            },
        },
        // Run 5 - Abyssal Deepsea: a compressed ocean trench. Low relief keeps the floor basin-like, while broad,
        // slow meanders carve dark water canals that demand wreck/reef bridge crossings.
        ["abyssal_deepsea"] = new()
        {
            elevation = new ElevationProfile(-0.12, 1.18),
            rivers = new RiverParams
            {
                spacing = 31,
                meanderAmp = 8.2,
                meanderWave = 118,
                meanderAmp2 = 3.4,
                meanderWave2 = 39,
                halfWidth = 4.75,
                halfWidthVar = 1.25,
                widthWave = 58,
                orientationJitter = 0.62,
                secondaryStrength = 0.38,
                lakeStrength = 0.52,
                drynessStrength = 0.58,
                styleWeights = new[] { 0.1, 0.18, 0.24, 0.28, 0.2 },
            },
        },
        // Run 6 - Rainbowland: overpainted lacquer hills cut by fast soapwater canals. The rivers are close,
        // jittery and glossy so bridges become bright crossing moments rather than occasional wilderness planks.
        ["rainbowland"] = new()
        {
            elevation = new ElevationProfile(0.035, 1.23),
            rivers = new RiverParams
            {
                spacing = 29,
                meanderAmp = 9.8,
                meanderWave = 58,
                meanderAmp2 = 3.6,
                meanderWave2 = 19,
                halfWidth = 3.25,
                halfWidthVar = 1.25,
                widthWave = 27,
                orientationJitter = 0.78,
                secondaryStrength = 0.56,
                lakeStrength = 0.42,
                drynessStrength = 0.7,
                styleWeights = new[] { 0.12, 0.35, 0.23, 0.12, 0.18 },
            },
        },
        // Run 7 - Clockwork Moon Bazaar: fractured moon terraces, brass alley blocks and narrow mercury gutters.
        // The channels are straighter and tighter than wilderness rivers, so crossings read as mechanical bridges
        // over market drainage rather than natural streams.
        ["clockwork_moon_bazaar"] = new()
        {
            elevation = new ElevationProfile(0.055, 1.24),
            rivers = new RiverParams
            {
                spacing = 34,
                meanderAmp = 1.45,
                meanderWave = 154,
                meanderAmp2 = 0.55,
                meanderWave2 = 58,
                halfWidth = 1.65,
                halfWidthVar = 0.28,
                widthWave = 48,
                orientationJitter = 0.16,
                secondaryStrength = 0.42,
                lakeStrength = 0.18,
                drynessStrength = 0.66,
                styleWeights = new[] { 0.4, 0.16, 0.29, 0.07, 0.08 },
            },
        },
        // Run 8 - Sugarstorm-Carnival Run: low frosting streets and broad midway courts cut by sticky syrup
        // channels. The canals sit closer and wider than garden water, so candy bridges become frequent set-pieces
        // and the terrain reads as fairground architecture instead of wilderness.
        ["sugarstorm_carnival"] = new()
        {
            elevation = new ElevationProfile(-0.045, 1.04),
            rivers = new RiverParams
            {
                spacing = 30,
                meanderAmp = 7.8,
                meanderWave = 68,
                meanderAmp2 = 3.1,
                meanderWave2 = 24,
                halfWidth = 3.8,
                halfWidthVar = 1.15,
                widthWave = 34,
                orientationJitter = 0.7,
                secondaryStrength = 0.5,
                lakeStrength = 0.52,
                drynessStrength = 0.66,
                styleWeights = new[] { 0.14, 0.23, 0.24, 0.2, 0.19 },
            },
        },
        // Run 9 - Prismglass Archive: mirrored shelf corridors and hard glass courts. The channels are clean,
        // straighter and closer than wilderness rivers, so the route repeatedly crosses reflective archive canals.
        ["prismglass_archive"] = new()
        {
            elevation = new ElevationProfile(0.045, 1.28),
            rivers = new RiverParams
            {
                spacing = 36,
                meanderAmp = 1.6,
                meanderWave = 172,
                meanderAmp2 = 0.52,
                meanderWave2 = 64,
                halfWidth = 2.55,
                halfWidthVar = 0.24,
                widthWave = 88,
                orientationJitter = 0.14,
                secondaryStrength = 0.4,
                lakeStrength = 0.16,
                // Engineered canals remain narrow, but never disappear for an entire streamed district: unlike a natural
                // dry belt, an archive's water grid is part of its authored spatial identity.
                drynessStrength = 0.45,
                styleWeights = new[] { 0.42, 0.15, 0.29, 0.07, 0.07 },
            },
        },
        // Run 10 - Starforged Cathedral Endrun: monumental nave terraces cut by black-star rivers. The highest
        // relief in the set, built for end-run silhouettes, altar ledges and solemn bridge crossings.
        ["starforged_cathedral_endrun"] = new()
        {
            elevation = new ElevationProfile(0.075, 1.32),
            rivers = new RiverParams
            {
                spacing = 52,
                meanderAmp = 4.8,
                meanderWave = 148,
                meanderAmp2 = 1.7,
                meanderWave2 = 42,
                halfWidth = 4.7,
                halfWidthVar = 1.2,
                widthWave = 58,
                orientationJitter = 0.46,
                secondaryStrength = 0.44,
                lakeStrength = 0.46,
                drynessStrength = 0.64,
                styleWeights = new[] { 0.18, 0.17, 0.32, 0.23, 0.1 },
            },
        },
    };

    /// <summary>`{ ...RUN_TERRAIN, … }` — only ever looked up by key, never iterated.</summary>
    private static readonly Dictionary<string, TerrainProfile> TERRAIN_BY_BIOME = new(RUN_TERRAIN)
    {
        // Alien Ranch (GENERATOR-ONLY theme — never in the run rotation): gently rolling pasture steppe under
        // engineered irrigation. The canal field meanders just enough to read grown-not-built, but its high lake
        // strength pools the nutrient goo into paddock ponds — the ranch is watered, not drained. Reached only via
        // `theme:alien_ranch` seeds in the standalone map generator.
        ["alien_ranch"] = new()
        {
            elevation = new ElevationProfile(0.04, 1.12),
            rivers = new RiverParams
            {
                spacing = 40,
                meanderAmp = 4.2,
                meanderWave = 96,
                meanderAmp2 = 1.3,
                meanderWave2 = 34,
                halfWidth = 2.6,
                halfWidthVar = 0.5,
                widthWave = 52,
                orientationJitter = 0.35,
                secondaryStrength = 0.42,
                lakeStrength = 0.85,
                drynessStrength = 0.6,
                styleWeights = new[] { 0.34, 0.08, 0.14, 0.3, 0.14 },
            },
        },
        ["mountain"] = MOUNTAIN_TERRAIN,
        ["verdant"] = new() { elevation = new ElevationProfile(-0.02, 1.1), rivers = VERDANT_RIVERS },
        ["ashen"] = new() { elevation = new ElevationProfile(-0.03, 1.15), rivers = ASHEN_RIVERS },
        ["frost"] = new() { elevation = new ElevationProfile(0.04, 1.16), rivers = null },
        ["storm"] = new() { elevation = new ElevationProfile(0.01, 1.18), rivers = VOID_RIVERS },
        ["void"] = new() { elevation = new ElevationProfile(-0.06, 1.2), rivers = VOID_RIVERS },
        ["frogmire"] = new() { elevation = new ElevationProfile(-0.08, 1.08), rivers = VERDANT_RIVERS },
        ["aegisCitadel"] = new() { elevation = new ElevationProfile(0.015, 1.04), rivers = null },
        ["machineworks"] = new() { elevation = new ElevationProfile(0, 1.06), rivers = null },
        ["paradisebower"] = new() { elevation = new ElevationProfile(0.08, 1.24), rivers = null },
        ["resonanceeyrie"] = new() { elevation = new ElevationProfile(0.06, 1.2), rivers = null },
        ["regrowthcanals"] = new() { elevation = new ElevationProfile(-0.04, 1.1), rivers = VERDANT_RIVERS },
        ["hollowcartography"] = new() { elevation = new ElevationProfile(-0.08, 1.26), rivers = null },
    };

    private static string? normalizeBiomeKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        // The Drone Foundry is a MACHINE ROOM: flat conduit floors, no rivers — the old 'verdant' mapping put
        // green-forest rivers under a foundry. The other soul dungeons author their terrain outright (tide rings /
        // terraces / wyrmforge carve their own water + elevation), so their profile only skews visual height.
        if (key == "raid_verdant") return "machineworks";
        if (key == "raid_holdthefort") return "aegisCitadel";
        if (key == "raid_tidecage") return "frogmire";
        if (key == "raid_thousandfolds") return "void";
        if (key == "raid_wyrmforge") return "machineworks";
        if (key == "raid_crownbower") return "paradisebower";
        if (key == "raid_moonroot") return "verdant";
        if (key == "raid_resonanceeyrie") return "resonanceeyrie";
        if (key == "raid_starossuary") return "void"; // the ossuary authors its terrain; void only skews visual height
        if (key == "raid_regrowthcanals") return "regrowthcanals";
        if (key == "raid_cragsummit") return "mountain";
        if (key == "raid_hollowcartography") return "hollowcartography";
        return key;
    }

    /// <summary>The terrain profile for a biome key. One source of truth.</summary>
    public static TerrainProfile terrainProfileFor(string? biomeKey)
    {
        string? key = normalizeBiomeKey(biomeKey);
        // `(key && TERRAIN_BY_BIOME[key]) || NEUTRAL_TERRAIN`
        if (!string.IsNullOrEmpty(key) && TERRAIN_BY_BIOME.TryGetValue(key, out var profile) && profile != null)
            return profile;
        return NEUTRAL_TERRAIN;
    }

    /// <summary>The elevation skew for a biome key — the slice the client renderer/minimap reads (visual height only).</summary>
    public static ElevationProfile elevationProfileFor(string? biomeKey) => terrainProfileFor(biomeKey).elevation;

    /* ── River field (global, continuous) ──────────────────────────────────────────────────────────────── */

    private const double TWO_PI = Math.PI * 2;
    private const int HYDRO_STYLE_SALT = 0x6ea3c8d1;
    private const int HYDRO_ANGLE_SALT = 0x18bf54a7;
    private const int HYDRO_SECONDARY_SALT = 0x4cf5ad43;
    private const int HYDRO_WETNESS_SALT = 0x5bd1e995;
    private const int HYDRO_BASIN_SALT = 0x27d4eb2f;
    private const int HYDRO_LAKE_SALT = 0x7a2fb1c9;
    private const int LANDSCAPE_LAKE_SALT = 0x510e527f;
    /// <summary>0x9b05688c exceeds int32; every use goes through ToInt32 (`seed ^ SALT`), i.e. this bit pattern.</summary>
    private const int LANDSCAPE_CHANNEL_SALT = unchecked((int)0x9b05688c);
    private const int LANDSCAPE_ISLAND_SALT = 0x1f83d9ab;
    private static readonly double[] DEFAULT_HYDRO_STYLE_WEIGHTS = { 0.3, 0.22, 0.2, 0.17, 0.11 };

    /// <summary>Deterministic hash of a (seed, channel index, salt) → [0,1). Bit-identical on every machine.</summary>
    private static double chanHash(double seed, double k, double salt)
    {
        int h = Js.ToInt32(seed) ^ Math.imul(Js.ToInt32(k), 0x27d4eb2f) ^ Math.imul(Js.ToInt32(salt), unchecked((int)0x9e3779b9));
        h = Math.imul(h ^ (int)((uint)h >> 15), 0x2c1b3c6d);
        h ^= (int)((uint)h >> 13);
        return (uint)h / 4294967296.0;
    }

    /// <summary>World-X (in tiles) of channel `k`'s centreline at global row `gty`.</summary>
    private static double channelCenterAcross(
        double seed,
        double k,
        double along,
        RiverParams p,
        double spacingScale,
        double meanderScale,
        double offset)
    {
        double phase1 = chanHash(seed, k, 1) * TWO_PI;
        double phase2 = chanHash(seed, k, 2) * TWO_PI;
        return
            k * p.spacing * spacingScale +
            offset +
            p.meanderAmp * meanderScale * Math.sin((along / p.meanderWave) * TWO_PI + phase1) +
            p.meanderAmp2 * meanderScale * Math.sin((along / p.meanderWave2) * TWO_PI + phase2);
    }

    /// <summary>Channel `k`'s half-width (tiles) at global row `gty` — breathes a little so the banks aren't parallel.</summary>
    private static double channelHalfWidth(
        double seed,
        double k,
        double along,
        RiverParams p,
        double widthScale,
        double basinScale)
    {
        double phase = chanHash(seed, k, 3) * TWO_PI;
        double @base = Math.max(
            0.9,
            (p.halfWidth + p.halfWidthVar * Math.sin((along / p.widthWave) * TWO_PI + phase)) * widthScale);
        // Broad, infrequent source basins turn the meandering channel into mountain lakes and linked pools without
        // ever painting a rectangular flood plane. The bulge is a smooth world-space lobe on the same channel, so it
        // remains seam-stable and automatically inherits the normal room, bridge and connectivity protections.
        double basinPeriod = Math.max(54, p.widthWave * 1.55);
        double basinPhase = chanHash(seed, k, 31) * basinPeriod;
        double wrapped = (((along + basinPhase) % basinPeriod) + basinPeriod) % basinPeriod;
        double distance = Math.abs(wrapped - basinPeriod * 0.5);
        double basinHalfLength = Math.min(basinPeriod * 0.22, 7 + chanHash(seed, k, 37) * 6);
        double basinT = Math.max(0, 1 - distance / basinHalfLength);
        double basinEase = basinT * basinT * (3 - 2 * basinT);
        double basinBulge =
            basinEase * (0.9 + Math.min(2.4, p.halfWidth * 0.42)) * Math.max(0, basinScale);
        return @base + basinBulge;
    }

    private static string hydrologyStyleFor(double seed, RiverParams p)
    {
        string[] styles =
        {
            EndlessHydrologyStyle.Meander,
            EndlessHydrologyStyle.Braided,
            EndlessHydrologyStyle.Confluence,
            EndlessHydrologyStyle.LakeChain,
            EndlessHydrologyStyle.Wetland,
        };
        double[] weights = p.styleWeights ?? DEFAULT_HYDRO_STYLE_WEIGHTS;
        double total = 0;
        foreach (double weight in weights) total += Math.max(0, weight);
        if (total <= 0) return EndlessHydrologyStyle.Meander;
        double pick = chanHash(seed, 0, HYDRO_STYLE_SALT) * total;
        for (int i = 0; i < styles.Length; i++)
        {
            // `weights[i] ?? 0`: a shorter weight list reads undefined past its end.
            pick -= Math.max(0, i < weights.Length ? weights[i] : 0);
            if (pick <= 0) return styles[i];
        }
        return styles[styles.Length - 1];
    }

    /// <summary>The stable hydrology grammar drawn by one cohort seed inside its authored run theme.</summary>
    public static EndlessHydrologyIdentity endlessHydrologyIdentityFor(
        double seed,
        RiverParams p)
    {
        double spread = Math.max(0, p.orientationJitter ?? 0.42);
        double angle = (chanHash(seed, 0, HYDRO_ANGLE_SALT) - 0.5) * 2 * spread;
        double turn = 0.62 + chanHash(seed, 0, HYDRO_SECONDARY_SALT) * 0.62;
        double direction = chanHash(seed, 1, HYDRO_SECONDARY_SALT) < 0.5 ? -1 : 1;
        return new EndlessHydrologyIdentity
        {
            style = hydrologyStyleFor(seed, p),
            angle = angle,
            secondaryAngle = angle + turn * direction,
            wetness = 0.9 + chanHash(seed, 0, HYDRO_WETNESS_SALT) * 0.22,
            basinStrength =
                Math.max(0, p.lakeStrength ?? 0.4) * (0.72 + chanHash(seed, 0, HYDRO_BASIN_SALT) * 0.56),
        };
    }

    private static double channelFamilyPenetrationAt(
        double seed,
        double gtx,
        double gty,
        RiverParams p,
        double angle,
        double spacingScale,
        double widthScale,
        double meanderScale,
        double basinScale,
        double offset = 0)
    {
        double cos = Math.cos(angle);
        double sin = Math.sin(angle);
        double across = gtx * cos + gty * sin;
        double along = -gtx * sin + gty * cos;
        double spacing = p.spacing * spacingScale;
        double kApprox = Math.round((across - offset) / spacing);
        double best = double.NegativeInfinity;
        for (double k = kApprox - 1; k <= kApprox + 1; k++)
        {
            double penetration =
                channelHalfWidth(seed, k, along, p, widthScale, basinScale) -
                Math.abs(
                    across - channelCenterAcross(seed, k, along, p, spacingScale, meanderScale * 0.62, offset));
            if (penetration > best) best = penetration;
        }
        return best;
    }

    private static double lakePenetrationAt(
        double seed,
        double gtx,
        double gty,
        RiverParams p,
        double strength,
        bool wetland)
    {
        if (strength <= 0) return double.NegativeInfinity;
        double cell = Math.max(34, p.widthWave * (wetland ? 0.72 : 1.02));
        double broad = Elevation.valueNoise((uint)(Js.ToInt32(seed) ^ HYDRO_LAKE_SALT), gtx, gty, cell);
        double detail = Elevation.valueNoise(
            (uint)(Js.ToInt32(seed) ^ HYDRO_LAKE_SALT ^ unchecked((int)0x85ebca6b)), gtx, gty, cell * 0.43);
        double field = broad * 0.52 + detail * 0.48;
        double threshold = wetland ? 0.64 : 0.67;
        return (field - threshold) * 8 * (0.35 + strength) - 1.45;
    }

    private static double riverPenetrationWithIdentity(
        double seed,
        double gtx,
        double gty,
        RiverParams p,
        EndlessHydrologyIdentity identity)
    {
        double secondary = Math.max(0, p.secondaryStrength ?? 0.38);
        switch (identity.style)
        {
            case EndlessHydrologyStyle.Meander:
                return channelFamilyPenetrationAt(
                    seed,
                    gtx,
                    gty,
                    p,
                    identity.angle,
                    1,
                    identity.wetness,
                    1,
                    0.8 + identity.basinStrength);
            case EndlessHydrologyStyle.Braided:
            {
                double primary = channelFamilyPenetrationAt(
                    seed,
                    gtx,
                    gty,
                    p,
                    identity.angle,
                    1,
                    identity.wetness * 0.67,
                    1,
                    0.45 + identity.basinStrength * 0.45);
                double branch = channelFamilyPenetrationAt(
                    Js.ToInt32(seed) ^ HYDRO_SECONDARY_SALT,
                    gtx,
                    gty,
                    p,
                    identity.angle,
                    1,
                    identity.wetness * (0.42 + secondary * 0.26),
                    1.28,
                    0.2,
                    p.spacing * 0.28);
                return Math.max(primary, branch);
            }
            case EndlessHydrologyStyle.Confluence:
            {
                double primary = channelFamilyPenetrationAt(
                    seed,
                    gtx,
                    gty,
                    p,
                    identity.angle,
                    1,
                    identity.wetness * 0.82,
                    1,
                    0.55 + identity.basinStrength * 0.55);
                double crossing = channelFamilyPenetrationAt(
                    Js.ToInt32(seed) ^ HYDRO_SECONDARY_SALT,
                    gtx,
                    gty,
                    p,
                    identity.secondaryAngle,
                    1.28,
                    identity.wetness * (0.34 + secondary * 0.42),
                    0.72,
                    identity.basinStrength * 0.45);
                return Math.max(primary, crossing);
            }
            case EndlessHydrologyStyle.LakeChain:
                return Math.max(
                    channelFamilyPenetrationAt(
                        seed,
                        gtx,
                        gty,
                        p,
                        identity.angle,
                        1.08,
                        identity.wetness * 0.78,
                        0.86,
                        1.35 + identity.basinStrength * 1.8),
                    lakePenetrationAt(seed, gtx, gty, p, identity.basinStrength, false));
            case EndlessHydrologyStyle.Wetland:
                return Math.max(
                    channelFamilyPenetrationAt(
                        seed,
                        gtx,
                        gty,
                        p,
                        identity.angle,
                        1.05,
                        identity.wetness * 0.45,
                        1.34,
                        0.3),
                    channelFamilyPenetrationAt(
                        Js.ToInt32(seed) ^ HYDRO_SECONDARY_SALT,
                        gtx,
                        gty,
                        p,
                        identity.secondaryAngle,
                        1.7,
                        identity.wetness * (0.24 + secondary * 0.2),
                        0.76,
                        0.15),
                    lakePenetrationAt(seed, gtx, gty, p, identity.basinStrength, true));
        }
        // Unreachable: the TS switch is exhaustive over EndlessHydrologyStyle. Falling off it would return
        // `undefined`, which every consumer's arithmetic turns into NaN.
        return double.NaN;
    }

    /// <summary>Salt of the dryness belt field (independent of the elevation octaves and channel hashes).</summary>
    private const int DRYNESS_SALT = 0x3fb7e921;

    private static double riverCarveThresholdWithIdentity(
        double seed,
        double gtx,
        double gty,
        RiverParams? p,
        EndlessHydrologyIdentity? identity)
    {
        double dryness = Scalar.smoothstep(
            (Elevation.valueNoise((uint)(Js.ToInt32(seed) ^ DRYNESS_SALT), gtx, gty, 118) - 0.55) / 0.18);
        // A wet belt may widen a configured channel by half a tile, but never manufacture a second lake-sized plane
        // around it. The authored RiverParams still own the biome's width; the low-frequency field only narrows and
        // breaks that channel into pools/dry washes. This keeps Water a readable accent beside the native Chasm
        // landform instead of letting several neighbouring channels fill a gameplay viewport with turquoise quads.
        double strength = Math.max(0, p?.drynessStrength ?? 1);
        double wetness = identity?.wetness ?? 1;
        return dryness * 4.6 * strength - (0.76 + (wetness - 1) * 1.1);
    }

    /// <summary>
    /// Regional water score composed over the authored river grammar. Lake and distributary fields are global,
    /// continuous and broad; the landscape recipe only controls their bounded amplitude. A setting can therefore
    /// turn a normal channel into a great lake/delta without stamping a per-chunk rectangle or changing seams.
    /// </summary>
    private static double endlessTerrainWaterScoreAt(
        double seed,
        double gtx,
        double gty,
        RiverParams p,
        EndlessHydrologyIdentity identity,
        EndlessTerrainVariation? variation)
    {
        double river =
            riverPenetrationWithIdentity(seed, gtx, gty, p, identity) -
            riverCarveThresholdWithIdentity(seed, gtx, gty, p, identity) +
            (variation?.waterBias ?? 0) * 1.35;
        double lakeStrength = Math.max(0, variation?.lakeStrength ?? 0);
        double lake = endlessTerrainLakeScoreAt(seed, gtx, gty, lakeStrength);
        double channelStrength = Math.max(0, variation?.channelComplexity ?? 0);
        double distributary = double.NegativeInfinity;
        if (channelStrength > 0.01)
        {
            double contour = Elevation.valueNoise((uint)(Js.ToInt32(seed) ^ LANDSCAPE_CHANNEL_SALT), gtx, gty, 31);
            double branch = 1 - Math.abs(contour - 0.5) * 2;
            distributary = (branch - (0.91 - channelStrength * 0.13)) * 5.4;
        }
        return Math.max(river, lake, distributary);
    }

    private static double endlessTerrainLakeScoreAt(
        double seed,
        double gtx,
        double gty,
        double lakeStrength)
    {
        if (lakeStrength <= 0.01) return double.NegativeInfinity;
        double broad = Elevation.valueNoise((uint)(Js.ToInt32(seed) ^ LANDSCAPE_LAKE_SALT), gtx, gty, 72);
        double shore = Elevation.valueNoise(
            (uint)(Js.ToInt32(seed) ^ LANDSCAPE_LAKE_SALT ^ unchecked((int)0x85ebca6b)),
            gtx + 23.5,
            gty - 17.5,
            29);
        double basin = broad * 0.74 + shore * 0.26;
        double threshold = 0.718 - lakeStrength * 0.15;
        return (basin - threshold) * (6.5 + lakeStrength * 5.5);
    }

    private static bool endlessTerrainIslandAt(
        double seed,
        double gtx,
        double gty,
        double strength,
        double mixedLakeIslandBoost)
    {
        // A jittered global site lattice creates separate rounded land nuclei rather than merely shrinking the
        // shoreline. Where a lake covers one of these sites, its dry footprint becomes a real interior island (or a
        // rock island after reachability repair). Low island strength keeps only rare skerries; archipelago settings
        // grow several 3-4-tile nuclei per gameplay viewport. Site positions are global, so islands cross seams.
        const double cell = 15;
        double ix = Math.floor(gtx / cell);
        double iy = Math.floor(gty / cell);
        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                double sx = ix + ox;
                double sy = iy + oy;
                double siteX =
                    (sx + 0.2 + Elevation.latticeHash((uint)(Js.ToInt32(seed) ^ LANDSCAPE_ISLAND_SALT), sx, sy) * 0.6) * cell;
                double siteY =
                    (sy + 0.2 + Elevation.latticeHash((uint)(Js.ToInt32(seed) ^ LANDSCAPE_ISLAND_SALT ^ 0x27d4eb2f), sx, sy) * 0.6) *
                    cell;
                double radius =
                    0.8 +
                    strength * 3.4 +
                    mixedLakeIslandBoost +
                    Elevation.latticeHash((uint)(Js.ToInt32(seed) ^ LANDSCAPE_ISLAND_SALT ^ unchecked((int)0x85ebca6b)), sx, sy) * 0.8;
                double dx = gtx - siteX;
                double dy = gty - siteY;
                if (dx * dx + dy * dy <= radius * radius) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Complete a boundary-clipped surface inward rather than deleting its globally authored seam cells. Expansion
    /// chooses the strongest neighbouring hydrology score, never enters a room, and stays off the route skeleton for
    /// an unbridged pond. The bounded target is at most 20 cells, so this remains a cheap deterministic contour pass.
    /// </summary>
    private static int growBoundaryWaterComponent(
        byte[] tiles,
        int width,
        int height,
        IReadOnlyList<int> componentIndices,
        double targetCells,
        byte[] corridorMask,
        IReadOnlyList<DungeonRoom> rooms,
        bool allowSkeleton,
        double seed,
        int baseGtx,
        int baseGty,
        RiverParams p,
        EndlessHydrologyIdentity identity,
        EndlessTerrainVariation? variation)
    {
        var member = new byte[tiles.Length];
        var contour = new List<int>(componentIndices);
        foreach (int index in contour) member[index] = 1;
        while (contour.Count < targetCells)
        {
            int best = -1;
            double bestScore = double.NegativeInfinity;
            // `for (const index of contour)` — the contour only grows after this scan.
            for (int contourIndex = 0; contourIndex < contour.Count; contourIndex++)
            {
                int index = contour[contourIndex];
                int tx = index % width;
                int ty = (int)Math.floor((double)index / width);
                // [[1, 0], [-1, 0], [0, 1], [0, -1]]
                for (int direction = 0; direction < 4; direction++)
                {
                    int dx = direction == 0 ? 1 : direction == 1 ? -1 : 0;
                    int dy = direction == 2 ? 1 : direction == 3 ? -1 : 0;
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int ni = ny * width + nx;
                    if (
                        member[ni] != 0 ||
                        tiles[ni] != TileType.Floor ||
                        (!allowSkeleton && (uint)ni < (uint)corridorMask.Length && corridorMask[ni] != 0) ||
                        inAnyRoom(nx, ny, rooms, 1)
                    )
                        continue;
                    int gtx = baseGtx + nx;
                    int gty = baseGty + ny;
                    double score = endlessTerrainWaterScoreAt(seed, gtx, gty, p, identity, variation);
                    if (score > bestScore || (score == bestScore && ni < best))
                    {
                        best = ni;
                        bestScore = score;
                    }
                }
            }
            if (best < 0) break;
            tiles[best] = TileType.Water;
            member[best] = 1;
            contour.push(best);
        }
        return contour.Count;
    }

    /* ── Applying the mountain terrain to a chunk ───────────────────────────────────────────────────────── */

    /// <summary>Is global tile (within this chunk) inside any (dry-kept) room rect, +`margin` tiles? Rivers skip rooms.</summary>
    private static bool inAnyRoom(int tx, int ty, IReadOnlyList<DungeonRoom> rooms, int margin)
    {
        foreach (DungeonRoom r in rooms)
        {
            TileRect rr = r.rect;
            if (
                tx >= rr.tx - margin &&
                tx < rr.tx + rr.tw + margin &&
                ty >= rr.ty - margin &&
                ty < rr.ty + rr.th + margin
            )
            {
                return true;
            }
        }
        return false;
    }

    private static void trimBridgeComponentsToSpines(
        byte[] tiles,
        int w,
        int h,
        int barrier)
    {
        var seen = new byte[tiles.Length];
        var componentMask = new byte[tiles.Length];
        var queue = new int[tiles.Length];
        var prev = new int[tiles.Length];
        var dist = new short[tiles.Length];
        var stack = new List<int>();
        var component = new List<int>();
        var anchors = new List<int>();
        const int seamStubMargin = 4;

        bool hasFloorNeighbor(int tx, int ty) =>
            Grid.getTile(tiles, w, h, tx - 1, ty) == TileType.Floor ||
            Grid.getTile(tiles, w, h, tx + 1, ty) == TileType.Floor ||
            Grid.getTile(tiles, w, h, tx, ty - 1) == TileType.Floor ||
            Grid.getTile(tiles, w, h, tx, ty + 1) == TileType.Floor;

        int farthestAnchorFrom(int start, bool recordPrev)
        {
            dist.fill((short)-1);
            if (recordPrev) prev.fill(-1);
            int head = 0;
            int tail = 0;
            dist[start] = 0;
            queue[tail++] = start;
            int best = start;
            while (head < tail)
            {
                int idx = queue[head++];
                if (anchors.includes(idx) && dist[idx] > dist[best]) best = idx;
                int tx = idx % w;
                int ty = (int)Math.floor((double)idx / w);
                // [[tx - 1, ty], [tx + 1, ty], [tx, ty - 1], [tx, ty + 1]]
                for (int k = 0; k < 4; k++)
                {
                    int nx = k == 0 ? tx - 1 : k == 1 ? tx + 1 : tx;
                    int ny = k == 2 ? ty - 1 : k == 3 ? ty + 1 : ty;
                    if (nx <= 0 || ny <= 0 || nx >= w - 1 || ny >= h - 1) continue;
                    int ni = Grid.tileIndex(w, nx, ny);
                    if (componentMask[ni] == 0 || dist[ni] >= 0) continue;
                    dist[ni] = Js.I16(dist[idx] + 1);
                    if (recordPrev) prev[ni] = idx;
                    queue[tail++] = ni;
                }
            }
            return best;
        }

        for (int ty = 1; ty < h - 1; ty++)
        {
            for (int tx = 1; tx < w - 1; tx++)
            {
                int start = Grid.tileIndex(w, tx, ty);
                if (tiles[start] != TileType.Bridge || seen[start] != 0) continue;
                seen[start] = 1;
                stack.push(tx, ty);
                component.Clear();
                anchors.Clear();
                int barrierTouches = 0;
                bool nearSeamStub = false;
                while (stack.Count > 0)
                {
                    int y = stack.pop();
                    int x = stack.pop();
                    int idx = Grid.tileIndex(w, x, y);
                    component.push(idx);
                    componentMask[idx] = 1;
                    nearSeamStub = nearSeamStub ||
                        x <= seamStubMargin ||
                        y <= seamStubMargin ||
                        x >= w - 1 - seamStubMargin ||
                        y >= h - 1 - seamStubMargin;
                    if (hasFloorNeighbor(x, y)) anchors.push(idx);
                    // [[x - 1, y], [x + 1, y], [x, y - 1], [x, y + 1]]
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = k == 0 ? x - 1 : k == 1 ? x + 1 : x;
                        int ny = k == 2 ? y - 1 : k == 3 ? y + 1 : y;
                        if (nx <= 0 || ny <= 0 || nx >= w - 1 || ny >= h - 1) continue;
                        int ni = Grid.tileIndex(w, nx, ny);
                        if (tiles[ni] == barrier) barrierTouches++;
                        if (tiles[ni] != TileType.Bridge || seen[ni] != 0) continue;
                        seen[ni] = 1;
                        stack.push(nx, ny);
                    }
                }

                if (barrierTouches > 0 && anchors.Count < 2 && !nearSeamStub)
                {
                    foreach (int idx in component) tiles[idx] = TileType.Floor;
                    foreach (int idx in component) componentMask[idx] = 0;
                    continue;
                }

                if (component.Count > 3 && anchors.Count >= 2)
                {
                    int a = farthestAnchorFrom(anchors[0], false);
                    int b = farthestAnchorFrom(a, true);
                    var keep = new byte[tiles.Length];
                    for (int cur = b; cur >= 0; cur = prev[cur])
                    {
                        keep[cur] = 1;
                        if (cur == a) break;
                    }
                    foreach (int idx in component)
                    {
                        if (keep[idx] == 0) tiles[idx] = (byte)barrier;
                    }
                }

                foreach (int idx in component) componentMask[idx] = 0;
            }
        }
    }

    private static void clearBridgeLandApproaches(byte[] tiles, int w, int h)
    {
        var clear = new byte[tiles.Length];
        for (int ty = 1; ty < h - 1; ty++)
        {
            for (int tx = 1; tx < w - 1; tx++)
            {
                if (Grid.getTile(tiles, w, h, tx, ty) != TileType.Bridge) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = tx + dx;
                        int y = ty + dy;
                        if (x <= 0 || y <= 0 || x >= w - 1 || y >= h - 1) continue;
                        int idx = Grid.tileIndex(w, x, y);
                        if (tiles[idx] == TileType.Solid) clear[idx] = 1;
                    }
                }
            }
        }
        for (int i = 0; i < clear.Length; i++) if (clear[i] != 0) tiles[i] = TileType.Floor;
    }

    /// <summary>
    /// Carve the mountain biome's rivers, bridges and rock outcrops into a freshly-generated chunk, then repair
    /// connectivity. `baseGtx/baseGty` are the GLOBAL tile coordinates of this chunk's tile (0,0) (so a tile's
    /// global coord is `baseGtx+tx`). `corridorMask` is the chunk's connective skeleton — a `w*h` flag set by the
    /// generator on every carved corridor / port-stub tile (the generalization of the old fixed "+" cross to the
    /// braided maze): wherever a river crosses it, a bridge is laid, so the maze stays crossable across every
    /// river. `seedTx/seedTy` is a tile guaranteed to be on that skeleton (so it is walkable after bridging) and
    /// connected to the whole chunk — the walkable repair fills from it. Mutates `tiles`.
    /// </summary>
    public static void applyMountainTerrain(
        byte[] tiles,
        int w,
        int h,
        int baseGtx,
        int baseGty,
        byte[] corridorMask,
        int seedTx,
        int seedTy,
        IReadOnlyList<DungeonRoom> rooms,
        double seed,
        RiverParams p,
        EndlessTerrainVariation? variation = null)
    {
        // Out-of-range reads are `undefined`, and `undefined === 1` is false.
        bool onSkeleton(int tx, int ty)
        {
            int index = ty * w + tx;
            return (uint)index < (uint)corridorMask.Length && corridorMask[index] == 1;
        }
        EndlessHydrologyIdentity hydrology = endlessHydrologyIdentityFor(seed, p);

        // 1) Rock outcrops — a few small solid massifs for mountain relief in otherwise-open ground. Kept off the
        //    maze corridors and away from rooms, so the connected backbone and encounters stay clear; the final
        //    repair guarantees nothing is sealed off.
        // The channel index is a JS double (`baseGtx * 131` can pass 2^31) that chanHash truncates with `| 0`.
        int outcrops = 2 + Js.ToInt32(Math.floor(chanHash(seed, (double)baseGtx * 131 + (double)baseGty * 17, 9) * 3));
        for (int o = 0; o < outcrops; o++)
        {
            int ocx = (int)Math.floor(3 + chanHash(seed, (double)baseGtx + o * 7, 11) * (w - 6));
            int ocy = (int)Math.floor(3 + chanHash(seed, (double)baseGty + o * 7, 13) * (h - 6));
            if (inAnyRoom(ocx, ocy, rooms, 2)) continue;
            int rx = 2 + (int)Math.floor(chanHash(seed, (double)o * 31 + baseGtx, 15) * 2.5);
            int ry = 2 + (int)Math.floor(chanHash(seed, (double)o * 31 + baseGty, 17) * 2.5);
            for (int dy = -ry; dy <= ry; dy++)
            {
                for (int dx = -rx; dx <= rx; dx++)
                {
                    if ((double)(dx * dx) / (rx * rx) + (double)(dy * dy) / (ry * ry) > 1) continue;
                    int tx = ocx + dx;
                    int ty = ocy + dy;
                    if (tx <= 0 || ty <= 0 || tx >= w - 1 || ty >= h - 1) continue;
                    if (onSkeleton(tx, ty)) continue; // never wall off the backbone
                    if (Grid.getTile(tiles, w, h, tx, ty) == TileType.Floor)
                        Grid.setTile(tiles, w, h, tx, ty, TileType.Solid);
                }
            }
        }
        byte[] beforeWaterTiles = tiles.slice();

        // 2) Rivers — turn OPEN ground inside a river channel into water (rooms stay dry so encounters aren't
        //    drowned). Solid remains geological mass and Chasm remains negative terrain: horizontal water may not
        //    overwrite either merely because their positive-height field happens to sit in a valley. The
        //    dryness-belt threshold lets authored channels narrow and vanish across the world.
        for (int ty = 0; ty < h; ty++)
        {
            int gty = baseGty + ty;
            for (int tx = 0; tx < w; tx++)
            {
                int tile = Grid.getTile(tiles, w, h, tx, ty);
                if (tile != TileType.Floor && tile != TileType.Solid) continue;
                if (inAnyRoom(tx, ty, rooms, 1)) continue;
                int gtx = baseGtx + tx;
                double waterScore = endlessTerrainWaterScoreAt(seed, gtx, gty, p, hydrology, variation);
                // A genuine great-lake recipe may drown interior rock to create one broad surface instead of a blue maze
                // threaded through every old wall. Ordinary rivers still carve open ground only. A stronger lake-core
                // threshold keeps the outer geological frame and the global island sites above water.
                bool mayFloodRock =
                    tile == TileType.Solid &&
                    (variation?.lakeStrength ?? 0) >= 0.5 &&
                    endlessTerrainLakeScoreAt(seed, gtx, gty, variation?.lakeStrength ?? 0) > 0.62;
                if (tile == TileType.Solid && !mayFloodRock) continue;
                if (waterScore > 0)
                {
                    Grid.setTile(tiles, w, h, tx, ty, TileType.Water);
                }
            }
        }

        // Normalize water before any deck exists. Closed components below ten cells are noise. A 10..19-cell pond
        // remains only when the guaranteed route skeleton avoids it; otherwise it would force a prohibited bridge.
        // Small edge fragments grow inward to their authored minimum, preserving the globally continuous seam cells.
        var bridgeEligibleMask = new byte[tiles.Length];
        var initialWaterTopology = TerrainWater.analyzeTerrainWaterTopology(tiles, w, h);
        foreach (var component in initialWaterTopology.components)
        {
            bool touchesSkeleton = component.indices.some((index) =>
                (uint)index < (uint)corridorMask.Length && corridorMask[index] == 1);
            double targetCells = touchesSkeleton
                ? TerrainWater.TERRAIN_BRIDGE_MIN_WATER_BODY_CELLS
                : TerrainWater.TERRAIN_WATER_MIN_COMPONENT_CELLS;
            if (component.touchesBoundary && component.surfaceCells < targetCells)
            {
                growBoundaryWaterComponent(
                    tiles,
                    w,
                    h,
                    component.indices,
                    targetCells,
                    corridorMask,
                    rooms,
                    false,
                    seed,
                    baseGtx,
                    baseGty,
                    p,
                    hydrology,
                    variation);
            }
            else if (component.surfaceCells < targetCells)
            {
                foreach (int index in component.indices)
                {
                    tiles[index] = touchesSkeleton ? beforeWaterTiles[index] : (byte)TileType.Solid;
                }
            }
        }

        // Cut the global island nuclei only after boundary fragments have been normalized. Doing this before the
        // minimum-surface pass could split one lake into several small edge fragments that each grow independently,
        // perversely increasing water coverage when island strength rises. Restoring the pre-water geology here makes
        // islands strictly subtractive while the following topology pass still removes any undersized water remnants.
        double islandStrength = Math.max(0, variation?.islandStrength ?? 0);
        if (islandStrength > 0.02)
        {
            double lakeStrength = Math.max(0, variation?.lakeStrength ?? 0);
            double channelComplexity = Math.max(0, variation?.channelComplexity ?? 0);
            // These recipe-only terms are invariant across the chunk. Hoisting them avoids three smoothstep chains for
            // every water tile while retaining the exact same global island test and therefore byte-identical seams.
            double mixedLakeIslandBoost =
                Scalar.smoothstep((0.62 - lakeStrength) / 0.08) *
                (1 - Scalar.smoothstep((channelComplexity - 0.35) / 0.2)) *
                Scalar.smoothstep((islandStrength - 0.1) / 0.08) *
                2.2;
            for (int ty = 0; ty < h; ty++)
            {
                int gty = baseGty + ty;
                for (int tx = 0; tx < w; tx++)
                {
                    int index = ty * w + tx;
                    if (
                        tiles[index] == TileType.Water &&
                        endlessTerrainIslandAt(seed, baseGtx + tx, gty, islandStrength, mixedLakeIslandBoost)
                    )
                    {
                        tiles[index] = beforeWaterTiles[index];
                    }
                }
            }
        }

        // Growth can merge neighbouring fragments, so classify the actual result once more. If an edge component had
        // too little eligible ground to reach its target, reject it just like a closed undersized component.
        var normalizedWaterTopology = TerrainWater.analyzeTerrainWaterTopology(tiles, w, h);
        foreach (var component in normalizedWaterTopology.components)
        {
            bool touchesSkeleton = component.indices.some((index) =>
                (uint)index < (uint)corridorMask.Length && corridorMask[index] == 1);
            double minimumCells = touchesSkeleton
                ? TerrainWater.TERRAIN_BRIDGE_MIN_WATER_BODY_CELLS
                : TerrainWater.TERRAIN_WATER_MIN_COMPONENT_CELLS;
            if (component.surfaceCells < minimumCells)
            {
                foreach (int index in component.indices)
                {
                    tiles[index] = touchesSkeleton ? beforeWaterTiles[index] : (byte)TileType.Solid;
                }
                continue;
            }
            if (component.surfaceCells >= TerrainWater.TERRAIN_BRIDGE_MIN_WATER_BODY_CELLS)
            {
                foreach (int index in component.indices) bridgeEligibleMask[index] = 1;
            }
        }

        // 3) Bridges — wherever a river crosses the maze's corridor skeleton, lay a walkable deck, so every port
        //    still reaches every other across the water (the connectivity backbone survives the river).
        for (int ty = 0; ty < h; ty++)
        {
            for (int tx = 0; tx < w; tx++)
            {
                int index = ty * w + tx;
                if (
                    Grid.getTile(tiles, w, h, tx, ty) == TileType.Water &&
                    bridgeEligibleMask[index] != 0 &&
                    onSkeleton(tx, ty)
                )
                {
                    Grid.setTile(tiles, w, h, tx, ty, TileType.Bridge);
                }
            }
        }
        trimBridgeComponentsToSpines(tiles, w, h, TileType.Water);
        TerrainBridge.enforceMinimumBridgeThickness(tiles, w, h);
        clearBridgeLandApproaches(tiles, w, h);
        TerrainBridge.enforceMinimumBridgeThickness(tiles, w, h);

        // 3b) Demote a bridge deck that spans nothing — a lone tile from a one-tile river pinch with no water or
        //     other deck beside it — back to plain floor, so bridges only ever appear as genuine crossings.
        for (int ty = 1; ty < h - 1; ty++)
        {
            for (int tx = 1; tx < w - 1; tx++)
            {
                if (Grid.getTile(tiles, w, h, tx, ty) != TileType.Bridge) continue;
                bool spans =
                    Grid.getTile(tiles, w, h, tx - 1, ty) == TileType.Water ||
                    Grid.getTile(tiles, w, h, tx + 1, ty) == TileType.Water ||
                    Grid.getTile(tiles, w, h, tx, ty - 1) == TileType.Water ||
                    Grid.getTile(tiles, w, h, tx, ty + 1) == TileType.Water ||
                    Grid.getTile(tiles, w, h, tx - 1, ty) == TileType.Bridge ||
                    Grid.getTile(tiles, w, h, tx + 1, ty) == TileType.Bridge ||
                    Grid.getTile(tiles, w, h, tx, ty - 1) == TileType.Bridge ||
                    Grid.getTile(tiles, w, h, tx, ty + 1) == TileType.Bridge;
                if (!spans) Grid.setTile(tiles, w, h, tx, ty, TileType.Floor);
            }
        }

        var seenBridge = new byte[tiles.Length];
        for (int ty = 1; ty < h - 1; ty++)
        {
            for (int tx = 1; tx < w - 1; tx++)
            {
                int start = Grid.tileIndex(w, tx, ty);
                if (tiles[start] != TileType.Bridge || seenBridge[start] != 0) continue;
                var stack = new List<int> { tx, ty };
                var component = new List<int>();
                seenBridge[start] = 1;
                bool touchesWater = false;
                while (stack.Count > 0)
                {
                    int y = stack.pop();
                    int x = stack.pop();
                    int idx = Grid.tileIndex(w, x, y);
                    component.push(idx);
                    // [[x - 1, y], [x + 1, y], [x, y - 1], [x, y + 1]]
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = k == 0 ? x - 1 : k == 1 ? x + 1 : x;
                        int ny = k == 2 ? y - 1 : k == 3 ? y + 1 : y;
                        int ni = Grid.tileIndex(w, nx, ny);
                        int tile = Grid.getTile(tiles, w, h, nx, ny);
                        if (tile == TileType.Water) touchesWater = true;
                        // A Bridge tile is always in bounds (getTile reads Solid outside), so `ni` is valid here.
                        if (tile != TileType.Bridge || seenBridge[ni] != 0) continue;
                        seenBridge[ni] = 1;
                        stack.push(nx, ny);
                    }
                }
                if (!touchesWater)
                {
                    foreach (int idx in component) tiles[idx] = TileType.Floor;
                }
            }
        }

        // 4) Repair: keep only walkable ground reachable from the seed across bridges (drop banks a river cut off
        //    with no crossing). The seed tile is on the bridged corridor skeleton, so it is always walkable.
        byte[] mask = Grid.floodFillWalkable(tiles, w, h, seedTx, seedTy);
        for (int i = 0; i < tiles.Length; i++)
        {
            // `!mask[i]` is also true past the end of the w*h mask (undefined).
            if (tiles[i] == TileType.Floor && ((uint)i >= (uint)mask.Length || mask[i] == 0)) tiles[i] = TileType.Solid;
        }
    }
}
