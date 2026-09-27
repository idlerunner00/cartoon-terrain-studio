// Port of packages/shared/src/domain/dungeon/terrainAmbient.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Data-only identity for animated, collision-neutral terrain details.
//
// Placement is produced by the shared terrain render plan, so a motif belongs to a generated chunk rather
// than to a player's camera. The client is free to stream and instance the resulting records, but it never
// invents their position or theme. Adding another endless world therefore means registering one profile here;
// renderer code stays generic.
//
// Porting notes: the string-valued const objects are static classes of string constants (their types are
// `string`); `{ ...DEFAULT_PROFILE, x: 1 }` is `new TerrainAmbientThemeProfile(DEFAULT_PROFILE) { x = 1 }`.
// The profiles are shared immutable tables (TS `Object.freeze`): never mutate one.

public static class TerrainAmbientMotifKind
{
    public const string MeadowPollen = "meadowPollen";
    public const string NeonRain = "neonRain";
    public const string CloudFeather = "cloudFeather";
    public const string AbyssPlankton = "abyssPlankton";
    public const string PrismSpark = "prismSpark";
    public const string ClockworkCog = "clockworkCog";
    public const string SugarConfetti = "sugarConfetti";
    public const string GlassShard = "glassShard";
    public const string StarEmber = "starEmber";
    public const string HubLeaf = "hubLeaf";
    public const string DustMote = "dustMote";
    public const string SaucerMote = "saucerMote";
}

public static class TerrainAmbientMotion
{
    public const string Drift = "drift";
    public const string Fall = "fall";
    public const string Rise = "rise";
    public const string Orbit = "orbit";
    public const string Flutter = "flutter";
}

/// <summary>`TerrainAmbientAnchor = 'walkable' | 'water' | 'either'` (type only in TS; literals collected here).</summary>
public static class TerrainAmbientAnchor
{
    public const string Walkable = "walkable";
    public const string Either = "either";
}

/// <summary>
/// What a world's SKY, WATER and GROUND carry. Every profile must name one of each — there is no `none`,
/// and that is the whole point.
///
/// These slots replace four independent `allow*` booleans. Each of those booleans was individually
/// defensible (a starforged cathedral has no sparrows) but nothing was ever put in a disabled channel's
/// place, so ten of eleven profiles ended up with no birds, ten with no butterflies, six with no fish —
/// and one world measured **zero** in every life channel. "No sparrows" silently became "no life".
///
/// A slot cannot be empty. The fiction still decides WHAT lives there; it may not decide that nothing does.
/// </summary>
public static class TerrainSkyLife
{
    /// <summary>Perch-route birds — the natural sky.</summary>
    public const string Birds = "birds";
    /// <summary>Fluttering things on the butterfly lane, tinted by the theme (moths, sparks, paper wings…).</summary>
    public const string Flutterers = "flutterers";
    /// <summary>The theme's own motif lifted into the air and given real presence (embers, cogs, plankton…).</summary>
    public const string Motes = "motes";
}

public static class TerrainWaterLife
{
    /// <summary>Fish shoals — the natural water.</summary>
    public const string Shoals = "shoals";
    /// <summary>Something the theme's own water carries instead (drifting glass, cogs, cold light…).</summary>
    public const string Drifters = "drifters";
}

public static class TerrainGroundLife
{
    /// <summary>Rolling grass and tumbling growth.</summary>
    public const string Growth = "growth";
    /// <summary>Ground-hugging motifs where growth would contradict the world.</summary>
    public const string Motes = "motes";
}

public sealed class TerrainAmbientThemeProfile
{
    /// <summary><see cref="TerrainAmbientMotifKind"/>.</summary>
    public string motif = TerrainAmbientMotifKind.DustMote;
    /// <summary><see cref="TerrainAmbientMotion"/>.</summary>
    public string motion = TerrainAmbientMotion.Drift;
    /// <summary><see cref="TerrainAmbientAnchor"/>.</summary>
    public string anchor = TerrainAmbientAnchor.Walkable;
    /// <summary>
    /// MANDATORY life slots — see <see cref="TerrainSkyLife"/>. Non-empty TUPLES, not single choices and not
    /// booleans: the type makes "this world's sky carries nothing" unrepresentable, while a living meadow
    /// can still carry birds AND flutterers the way a real one does.
    /// </summary>
    public IReadOnlyList<string> skyLife = null!;
    public IReadOnlyList<string> waterLife = null!;
    public IReadOnlyList<string> groundLife = null!;
    /// <summary>
    /// Target records per terrain cell. A bounded min/max keeps tiny and very large plans useful.
    ///
    /// These were measured on a streamed 32x32 chunk — the unit the endless client actually plans in — and a
    /// full desktop screen is 23 % of one. At the retired densities that put **1.2 motifs and 1.4 birds on
    /// screen**, so a world with a complete ambient bestiary still read as still air. The lane is instanced and
    /// cheap next to the prop budget, so it carries roughly twice what it did.
    /// </summary>
    public double density;
    public double minimum;
    public double maximum;
    /// <summary>
    /// Flutterer records per walkable floor cell.
    ///
    /// It lives here because it is the same question `skyLife` already answers — "what does this world's air
    /// carry" — and it used to be answered a second time, in the render plan, by a thirteen-branch chain of
    /// biome-key comparisons that no world could extend without editing engine code. A world whose `skyLife`
    /// omits <see cref="TerrainSkyLife.Flutterers"/> carries none whatever this says; the value is what a world that
    /// DOES carry them gets.
    /// </summary>
    public double flutterDensity;
    /// <summary>Motion envelope in tile units.</summary>
    public double travelMin;
    public double travelMax;
    /// <summary>Height above the materialized anchor surface, in elevation-level units.</summary>
    public double heightMin;
    public double heightMax;
    public double scaleMin;
    public double scaleMax;
    public double alphaMin;
    public double alphaMax;

    public TerrainAmbientThemeProfile() { }

    /// <summary>`{ ...source }` — copy every field (the lists are shared, they are immutable).</summary>
    public TerrainAmbientThemeProfile(TerrainAmbientThemeProfile source)
    {
        motif = source.motif;
        motion = source.motion;
        anchor = source.anchor;
        skyLife = source.skyLife;
        waterLife = source.waterLife;
        groundLife = source.groundLife;
        density = source.density;
        minimum = source.minimum;
        maximum = source.maximum;
        flutterDensity = source.flutterDensity;
        travelMin = source.travelMin;
        travelMax = source.travelMax;
        heightMin = source.heightMin;
        heightMax = source.heightMax;
        scaleMin = source.scaleMin;
        scaleMax = source.scaleMax;
        alphaMin = source.alphaMin;
        alphaMax = source.alphaMax;
    }
}

public static class TerrainAmbient
{
    private static string[] L(params string[] values) => values;

    private static readonly TerrainAmbientThemeProfile DEFAULT_PROFILE = new()
    {
        motif = TerrainAmbientMotifKind.DustMote,
        motion = TerrainAmbientMotion.Drift,
        anchor = TerrainAmbientAnchor.Walkable,
        skyLife = L(TerrainSkyLife.Birds, TerrainSkyLife.Flutterers),
        waterLife = L(TerrainWaterLife.Shoals),
        groundLife = L(TerrainGroundLife.Growth),
        density = 0.00532,
        minimum = 4,
        maximum = 18,
        flutterDensity = 0.01,
        travelMin = 0.45,
        travelMax = 1.35,
        heightMin = 0.28,
        heightMax = 1.05,
        scaleMin = 0.72,
        scaleMax = 1.18,
        alphaMin = 0.46,
        alphaMax = 0.72,
    };

    /* ── Derived life predicates ───────────────────────────────────────────────────────────────────────
     * Renderers still think in lanes (birds / flutterers / shoals / growth / motifs). The slots decide which
     * lane a world's life uses; these resolve one from the other so no consumer has to know both models.
     */

    public static bool ambientAllowsBirds(TerrainAmbientThemeProfile profile) =>
        false; // Current fauna roster: Flui chicken, ox and fish only.

    public static bool ambientAllowsFlutterers(TerrainAmbientThemeProfile profile) =>
        false;

    public static bool ambientAllowsShoals(TerrainAmbientThemeProfile profile) =>
        profile.waterLife.includes(TerrainWaterLife.Shoals);

    public static bool ambientAllowsGrowth(TerrainAmbientThemeProfile profile) =>
        profile.groundLife.includes(TerrainGroundLife.Growth);

    /// <summary>
    /// How much the theme's own motif lane has to carry.
    ///
    /// A slot that resolves to the world's motif rather than to birds/shoals/growth moves that channel's job
    /// onto the motif lane — so the motif budget MUST rise with every such slot. This is the mechanism that
    /// makes "this world has no sparrows" mean "this world's sky carries something else" instead of "this
    /// world's sky is empty", which is what the old booleans produced.
    /// </summary>
    public static double ambientMotifDensityGain(TerrainAmbientThemeProfile profile)
    {
        double gain = 1;
        if (profile.skyLife.includes(TerrainSkyLife.Motes)) gain += 0.55;
        if (profile.waterLife.includes(TerrainWaterLife.Drifters)) gain += 0.4;
        if (profile.groundLife.includes(TerrainGroundLife.Motes)) gain += 0.45;
        return gain;
    }

    private static readonly TerrainAmbientThemeProfile HIGHLAND_AMBIENT_PROFILE = new(DEFAULT_PROFILE)
    {
        flutterDensity = 0.01,
        skyLife = L(TerrainSkyLife.Birds, TerrainSkyLife.Flutterers),
        waterLife = L(TerrainWaterLife.Shoals),
        groundLife = L(TerrainGroundLife.Growth),
        motif = TerrainAmbientMotifKind.MeadowPollen,
        motion = TerrainAmbientMotion.Drift,
        density = 0.00798,
        minimum = 10,
        maximum = 30,
        heightMin = 0.2,
        heightMax = 1.35,
    };

    /// <summary>`Readonly&lt;Record&lt;string, TerrainAmbientThemeProfile&gt;&gt;` — insertion-ordered because its keys are listed.</summary>
    private static readonly JsMap<string, TerrainAmbientThemeProfile> PROFILES = new JsMap<string, TerrainAmbientThemeProfile>()
        .set("hub", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            flutterDensity = 0.012,
            skyLife = L(TerrainSkyLife.Birds, TerrainSkyLife.Flutterers),
            waterLife = L(TerrainWaterLife.Shoals),
            groundLife = L(TerrainGroundLife.Growth),
            motif = TerrainAmbientMotifKind.HubLeaf,
            motion = TerrainAmbientMotion.Flutter,
            density = 0.00684,
            minimum = 10,
            maximum = 27,
            travelMin = 0.7,
            travelMax = 1.8,
        })
        .set("museum", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            flutterDensity = 0.01,
            skyLife = L(TerrainSkyLife.Flutterers, TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Shoals),
            groundLife = L(TerrainGroundLife.Growth),
            motif = TerrainAmbientMotifKind.HubLeaf,
            motion = TerrainAmbientMotion.Flutter,
            density = 0.00456,
            minimum = 6,
            maximum = 15,
            travelMin = 0.7,
            travelMax = 1.8,
        })
        .set("highland_pass", HIGHLAND_AMBIENT_PROFILE)
        .set("noir_sprawl", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.NeonRain,
            motion = TerrainAmbientMotion.Fall,
            density = 0.00646,
            minimum = 10,
            maximum = 27,
            travelMin = 0.15,
            travelMax = 0.55,
            heightMin = 1.2,
            heightMax = 2.8,
            scaleMin = 0.78,
            scaleMax = 1.4,
            alphaMin = 0.58,
            alphaMax = 0.84,
        })
        .set("olympian_sky_borough", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Birds, TerrainSkyLife.Flutterers),
            waterLife = L(TerrainWaterLife.Shoals),
            groundLife = L(TerrainGroundLife.Growth),
            motif = TerrainAmbientMotifKind.CloudFeather,
            motion = TerrainAmbientMotion.Drift,
            density = 0.00608,
            minimum = 8,
            maximum = 24,
            travelMin = 1.15,
            travelMax = 2.8,
            heightMin = 0.85,
            heightMax = 2.2,
            scaleMin = 0.9,
            scaleMax = 1.55,
        })
        // Exact content parity with normal terrain: birds, flutterers, shoals, growth and meadow pollen.
        .set("sakura_temple_dream", HIGHLAND_AMBIENT_PROFILE)
        .set("abyssal_deepsea", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            flutterDensity = 0.002,
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Shoals),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.AbyssPlankton,
            motion = TerrainAmbientMotion.Rise,
            anchor = TerrainAmbientAnchor.Either,
            density = 0.0076,
            minimum = 12,
            maximum = 33,
            travelMin = 0.35,
            travelMax = 1.25,
            heightMin = 0.2,
            heightMax = 1.6,
            scaleMin = 0.66,
            scaleMax = 1.24,
            alphaMin = 0.68,
            alphaMax = 0.94,
        })
        .set("rainbowland", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            flutterDensity = 0.012,
            skyLife = L(TerrainSkyLife.Flutterers, TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Shoals),
            groundLife = L(TerrainGroundLife.Growth),
            motif = TerrainAmbientMotifKind.PrismSpark,
            motion = TerrainAmbientMotion.Orbit,
            density = 0.00779,
            minimum = 12,
            maximum = 33,
            travelMin = 0.45,
            travelMax = 1.45,
            heightMin = 0.45,
            heightMax = 1.9,
            alphaMin = 0.68,
            alphaMax = 0.96,
        })
        .set("clockwork_moon_bazaar", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.ClockworkCog,
            motion = TerrainAmbientMotion.Orbit,
            density = 0.0057,
            minimum = 8,
            maximum = 22,
            travelMin = 0.28,
            travelMax = 0.9,
            heightMin = 0.45,
            heightMax = 1.45,
            scaleMin = 0.78,
            scaleMax = 1.32,
            alphaMin = 0.56,
            alphaMax = 0.82,
        })
        .set("sugarstorm_carnival", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            flutterDensity = 0.006,
            skyLife = L(TerrainSkyLife.Flutterers, TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Growth),
            motif = TerrainAmbientMotifKind.SugarConfetti,
            motion = TerrainAmbientMotion.Fall,
            density = 0.0095,
            minimum = 14,
            maximum = 36,
            travelMin = 0.65,
            travelMax = 1.9,
            heightMin = 1,
            heightMax = 2.7,
            alphaMin = 0.7,
            alphaMax = 0.98,
        })
        .set("prismglass_archive", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            flutterDensity = 0.002,
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.GlassShard,
            motion = TerrainAmbientMotion.Orbit,
            density = 0.00608,
            minimum = 10,
            maximum = 26,
            travelMin = 0.3,
            travelMax = 1.05,
            heightMin = 0.55,
            heightMax = 1.8,
            scaleMin = 0.7,
            scaleMax = 1.3,
            alphaMin = 0.64,
            alphaMax = 0.92,
        })
        .set("starforged_cathedral_endrun", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.StarEmber,
            motion = TerrainAmbientMotion.Rise,
            density = 0.00684,
            minimum = 10,
            maximum = 27,
            travelMin = 0.35,
            travelMax = 1.25,
            heightMin = 0.25,
            heightMax = 2.1,
            alphaMin = 0.68,
            alphaMax = 0.96,
        })
        /*
         * The three SEALED combat floors. They carry no life at all, and they say so HERE — the render plan used to
         * carry that fact a second time, as three zero-valued branches of a hardcoded biome chain, which is why an
         * arena could be given flutterers by the ecology registry and refused them by the renderer in the same
         * frame. A sealed floor's air still moves: its whole ambient budget goes to its own motif.
         */
        .set("arena", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.DustMote,
            motion = TerrainAmbientMotion.Drift,
            density = 0.00456,
            minimum = 6,
            maximum = 18,
            flutterDensity = 0,
            travelMin = 0.2,
            travelMax = 0.8,
            heightMin = 0.25,
            heightMax = 1.4,
            scaleMin = 0.6,
            scaleMax = 1.05,
            alphaMin = 0.32,
            alphaMax = 0.56,
        })
        .set("raid_wyrmforge", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.StarEmber,
            motion = TerrainAmbientMotion.Rise,
            density = 0.00494,
            minimum = 8,
            maximum = 21,
            flutterDensity = 0,
            travelMin = 0.24,
            travelMax = 0.9,
            heightMin = 0.4,
            heightMax = 2,
            scaleMin = 0.55,
            scaleMax = 1,
            alphaMin = 0.4,
            alphaMax = 0.7,
        })
        .set("raid_verdant", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.ClockworkCog,
            motion = TerrainAmbientMotion.Drift,
            density = 0.00456,
            minimum = 6,
            maximum = 18,
            flutterDensity = 0,
            travelMin = 0.2,
            travelMax = 0.85,
            heightMin = 0.3,
            heightMax = 1.6,
            scaleMin = 0.6,
            scaleMax = 1.02,
            alphaMin = 0.36,
            alphaMax = 0.6,
        })
        .set("raid_holdthefort", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            // A besieged court: nothing nests, nothing swims, nothing grows — but the air is full of the stone
            // its walls are losing, and the motif budget rises to carry all three channels.
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.DustMote,
            motion = TerrainAmbientMotion.Drift,
            density = 0.00456,
            minimum = 6,
            maximum = 18,
            travelMin = 0.18,
            travelMax = 0.72,
            heightMin = 0.3,
            heightMax = 1.5,
            scaleMin = 0.62,
            scaleMax = 1.02,
            alphaMin = 0.34,
            alphaMax = 0.58,
        })
        // Alien Ranch (GENERATOR-ONLY theme, like the raid entries below it is deliberately absent from
        // ENDLESS_TERRAIN_AMBIENT_THEME_KEYS): hovering harvest-drone saucers patrol the paddocks in slow orbits
        // over turf and goo alike. No birds and no butterflies — the ranch's sky belongs to its keepers — but the
        // nutrient canals hold fish and the alien turf still rolls in the wind.
        .set("alien_ranch", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            flutterDensity = 0.008,
            skyLife = L(TerrainSkyLife.Motes, TerrainSkyLife.Flutterers),
            waterLife = L(TerrainWaterLife.Shoals),
            groundLife = L(TerrainGroundLife.Growth),
            motif = TerrainAmbientMotifKind.SaucerMote,
            motion = TerrainAmbientMotion.Orbit,
            anchor = TerrainAmbientAnchor.Either,
            density = 0.00722,
            minimum = 10,
            maximum = 27,
            travelMin = 0.6,
            travelMax = 1.7,
            heightMin = 0.6,
            heightMax = 2.2,
            scaleMin = 0.8,
            scaleMax = 1.4,
            alphaMin = 0.6,
            alphaMax = 0.9,
        })
        .set("raid_hollowcartography", new TerrainAmbientThemeProfile(DEFAULT_PROFILE)
        {
            skyLife = L(TerrainSkyLife.Motes),
            waterLife = L(TerrainWaterLife.Drifters),
            groundLife = L(TerrainGroundLife.Motes),
            motif = TerrainAmbientMotifKind.DustMote,
            motion = TerrainAmbientMotion.Rise,
            density = 0.00722,
            minimum = 10,
            maximum = 27,
            travelMin = 0.22,
            travelMax = 0.82,
            heightMin = 0.08,
            heightMax = 1.28,
            scaleMin = 0.62,
            scaleMax = 1.18,
            alphaMin = 0.42,
            alphaMax = 0.72,
        });

    public static TerrainAmbientThemeProfile terrainAmbientThemeProfile(string? biomeKey)
    {
        // `(biomeKey && PROFILES[biomeKey]) || DEFAULT_PROFILE`
        if (!string.IsNullOrEmpty(biomeKey) && PROFILES.TryGetValue(biomeKey, out TerrainAmbientThemeProfile profile))
            return profile;
        return DEFAULT_PROFILE;
    }

    public static int terrainAmbientMotifLimit(TerrainAmbientThemeProfile profile, int cellCount)
    {
        if (cellCount <= 0) return 0;
        // A world whose sky/water/ground resolve to motifs needs MORE motifs, not the same budget: the motif
        // lane is carrying those channels' life (see ambientMotifDensityGain).
        double gain = ambientMotifDensityGain(profile);
        return (int)Math.max(
            0,
            Math.min(
                Math.round(profile.maximum * gain),
                Math.max(Math.round(profile.minimum * gain), Math.round(cellCount * profile.density * gain))));
    }
}
