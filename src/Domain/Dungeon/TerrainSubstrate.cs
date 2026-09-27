// Port of packages/shared/src/domain/dungeon/terrainSubstrate.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * **Substrate** — what a world's open ground is actually *made of*, as registry data.
 *
 * ## What was wrong
 *
 * Two separate places decided the material of every floor cell in the game, and both did it with a hardcoded
 * chain of biome-key string comparisons:
 *
 *  - the endless generator's surface layer asked `biomeKey === 'highland_pass' || …` and answered with exactly
 *    two values — `Stone` for rock *and every route cell*, `Grass` (or `Floor`) for the remainder. Measured
 *    over 36 chunks that is **65.9 % Stone and 10.4 % Grass**, in every theme, everywhere;
 *  - the floor-composition plan asked the same question again for its meadow, grass and flower gains.
 *
 * Eleven authored run themes therefore collapsed into three material recipes, a new world could not state its
 * own ground without editing engine code, and — because the two chains were written independently — a theme
 * could and did disagree with itself about whether its floor was soil or turf.
 *
 * ## The rule
 *
 * A world states its ground ONCE, here, as data: a small ordered list of **facies** — the ground communities
 * it is made of — plus how much of the world each one covers. A seam-safe world field then partitions the
 * plane into large contiguous regions of those facies, and both consumers read that one answer:
 *
 *  - the generator writes the facies' TerrainSurface into the artifact's surface layer, so logic,
 *    footstep material and any future gameplay query agree with what is drawn;
 *  - the floor-composition plan multiplies its ecology channels by the facies' gains, so the pigment, the turf
 *    cover and the grass geometry that already read those channels put a scree flat, a sward, a silt bed and a
 *    burnt heath on the map *as areas* — which is the thing that actually fills a world. Point props never
 *    could: at the measured budget they cover well under a percent of the ground.
 *
 * Adding a world, or changing what one is made of, is an entry in TERRAIN_SUBSTRATE_RECIPES. There is
 * no branch to edit.
 */

/// <summary>
/// One ground community.
///
/// The four gains are multipliers on the floor-composition channels the renderer already consumes, so a facies
/// is authored in the vocabulary the world is already painted with rather than in a private colour space:
/// `meadow` is the broad ecological body, `grass` the closed turf inside it, `flowers` its bloom and `bare` the
/// exposed soil/gravel that competes with all three.
/// </summary>
public sealed class TerrainSubstrateFacies
{
    /// <summary>Name, for reading the registry and for test failure messages. Never shown to a player.</summary>
    public string name = "";
    /// <summary>The logical surface id (TerrainSurface) written into the artifact — what the world IS, for logic and sound.</summary>
    public int surface;
    /// <summary>
    /// What this facies becomes where circulation has worn it: a trail through a sward is packed soil, a trail
    /// through scree is still scree, a lane across a brass deck is still brass. Stating it per facies is what
    /// keeps three quarters of the walkable world — the measured route share — from collapsing back onto one
    /// flat material, which is exactly what a single recipe-wide route surface did.
    /// </summary>
    public int worn;
    /// <summary>Relative share of the world's open ground. Shares are normalised, so they may be authored as any scale.</summary>
    public double share;
    public double meadow;
    public double grass;
    public double flowers;
    public double bare;

    public TerrainSubstrateFacies Clone() => (TerrainSubstrateFacies)MemberwiseClone();
}

/// <summary>
/// How much ecology a world carries OVERALL, area-weighted across its facies.
///
/// This is the number the retired per-biome gain chains stated, kept as data. The facies below author only the
/// CONTRAST between one ground community and the next, at whatever scale reads well; `recipe` then
/// rescales them so their area-weighted mean lands exactly on this anchor. Without that step a world silently
/// lost ecology every time a bare facies was added to it — measured, the first draft of this registry took
/// `highland_pass` from an authored flower weight of 1.22 down to 0.71, and it emptied the meadows.
/// </summary>
public sealed class TerrainSubstrateEcology
{
    public double meadow;
    public double grass;
    public double flowers;
    public double bare;
}

public sealed class TerrainSubstrateRecipe
{
    public string key = "";
    /// <summary>
    /// Lattice of the facies field, in tiles. A region must be big enough to be a *place* — at 74 tiles a facies
    /// covers roughly two chunks, so a squad crosses two or three of them on the way to a landmark instead of
    /// walking over a mosaic that reads as noise.
    /// </summary>
    public double regionCell;
    /// <summary>The surface (TerrainSurface) of solid rock in this world.</summary>
    public int rockSurface;
    public TerrainSubstrateEcology ecology = null!;
    public IReadOnlyList<TerrainSubstrateFacies> facies = null!;
    /// <summary>Normalised cumulative shares, resolved once when the registry is built.</summary>
    public double[] cumulative = null!;
    /// <summary>PORT ADDITION (allocation): `terrainEcologyBiomeSalt(`${key}:substrate`)`, resolved once when the
    /// registry is built instead of building the key string per sampled cell.</summary>
    public int substrateSalt;
}

public static class TerrainSubstrate
{
    private sealed class TerrainSubstrateInput
    {
        public string key = "";
        public double? regionCell;
        public int? rockSurface;
        public TerrainSubstrateEcology ecology = null!;
        public IReadOnlyList<TerrainSubstrateFacies> facies = null!;

        public TerrainSubstrateInput Clone() => (TerrainSubstrateInput)MemberwiseClone();
    }

    /// <summary>Neutral overall weight: what a world carried before it had facies at all.</summary>
    private static readonly TerrainSubstrateEcology NEUTRAL_ECOLOGY =
        new() { meadow = 1, grass = 1, flowers = 0.88, bare = 1 };

    private static TerrainSubstrateRecipe recipe(TerrainSubstrateInput input)
    {
        var authored = input.facies;
        if (authored.Count == 0) throw new InvalidOperationException($"terrainSubstrate({input.key}): no facies authored");
        double total = 0;
        foreach (var entry in authored) total += Math.max(0, entry.share);
        if (total <= 0) throw new InvalidOperationException($"terrainSubstrate({input.key}): facies shares sum to zero");

        // Rescale each ecology channel so its area-weighted mean is exactly the world's authored anchor. The facies
        // therefore state contrast and the recipe states weight, and the two can never quietly cancel each other.
        double mean(Func<TerrainSubstrateFacies, double> pick)
        {
            double sum = 0;
            foreach (var entry in authored) sum += (Math.max(0, entry.share) / total) * pick(entry);
            return sum;
        }
        double scaleFor(double current, double target) => current > 1e-6 ? target / current : 0;
        double meadowScale = scaleFor(
            mean((f) => f.meadow),
            input.ecology.meadow);
        double grassScale = scaleFor(
            mean((f) => f.grass),
            input.ecology.grass);
        double flowerScale = scaleFor(
            mean((f) => f.flowers),
            input.ecology.flowers);
        double bareScale = scaleFor(
            mean((f) => f.bare),
            input.ecology.bare);
        List<TerrainSubstrateFacies> facies = authored.map((entry) =>
        {
            var copy = entry.Clone();
            copy.meadow = entry.meadow * meadowScale;
            copy.grass = entry.grass * grassScale;
            copy.flowers = entry.flowers * flowerScale;
            copy.bare = entry.bare * bareScale;
            return copy;
        });

        var cumulative = new double[facies.Count];
        double running = 0;
        for (int index = 0; index < facies.Count; index++)
        {
            running += Math.max(0, facies[index].share) / total;
            cumulative[index] = running;
        }
        cumulative[facies.Count - 1] = 1;
        return new TerrainSubstrateRecipe
        {
            key = input.key,
            regionCell = input.regionCell ?? 74,
            rockSurface = input.rockSurface ?? TerrainSurface.Stone,
            ecology = input.ecology,
            facies = facies,
            cumulative = cumulative,
            substrateSalt = TerrainCompositionField.terrainEcologyBiomeSalt($"{input.key}:substrate"),
        };
    }

    /// <summary>Shorthand so a recipe reads as the world it describes rather than as six property names per line.</summary>
    private static TerrainSubstrateFacies facies(
        string name,
        int surface,
        int worn,
        double share,
        double meadow,
        double grass,
        double flowers,
        double bare) =>
        new()
        {
            name = name,
            surface = surface,
            worn = worn,
            share = share,
            meadow = meadow,
            grass = grass,
            flowers = flowers,
            bare = bare,
        };

    /// <summary>
    /// The neutral ground: one temperate sward, one worn soil flat, one gravel scree.
    ///
    /// Any world that does not author its own gets this rather than a single flat material, because a world with
    /// one ground community is the defect this module exists to remove.
    /// </summary>
    public static readonly TerrainSubstrateRecipe DEFAULT_TERRAIN_SUBSTRATE = recipe(new TerrainSubstrateInput
    {
        key = "default",
        ecology = NEUTRAL_ECOLOGY,
        facies = new[]
        {
            facies("sward", TerrainSurface.Grass, TerrainSurface.Floor, 44, 1, 1, 0.88, 0.9),
            facies("soil-flat", TerrainSurface.Floor, TerrainSurface.Floor, 34, 0.62, 0.34, 0.34, 1.35),
            facies("scree", TerrainSurface.Stone, TerrainSurface.Stone, 22, 0.3, 0.12, 0.1, 1.7),
        },
    });

    /// <summary>The normal terrain recipe. Sakura reuses this complete material/ecology vocabulary under its own palette.</summary>
    private static readonly TerrainSubstrateInput HIGHLAND_SUBSTRATE_INPUT = new()
    {
        key = "highland_pass",
        ecology = new TerrainSubstrateEcology { meadow = 1.04, grass = 0.96, flowers = 0.78, bare = 1.04 },
        // Sakura reuses this recipe for the permanent world. The previous 64-tile countries and 50 % authored
        // sward could resolve more than 60 % of an entire camera belt as one grass family. A 40-tile country still
        // reads as a place, while showing several materials per view and keeping the measured shares near intent.
        regionCell = 40,
        facies = new[]
        {
            facies("alp-sward", TerrainSurface.Grass, TerrainSurface.Floor, 22, 1.12, 1.08, 0.88, 0.82),
            facies(
                "cropped-pasture",
                TerrainSurface.Grass,
                TerrainSurface.Floor,
                14,
                0.86,
                0.74,
                0.58,
                1.02),
            facies("slate-scree", TerrainSurface.Stone, TerrainSurface.Stone, 28, 0.26, 0.08, 0.05, 1.85),
            facies("peat-flat", TerrainSurface.Floor, TerrainSurface.Floor, 36, 0.7, 0.44, 0.24, 1.3),
        },
    };

    private static TerrainSubstrateInput withKey(TerrainSubstrateInput input, string key)
    {
        // `{ ...input, key }`
        var copy = input.Clone();
        copy.key = key;
        return copy;
    }

    /// <summary>The authored worlds. Only ever looked up by key, never iterated.</summary>
    public static readonly IReadOnlyDictionary<string, TerrainSubstrateRecipe> TERRAIN_SUBSTRATE_RECIPES =
        new Dictionary<string, TerrainSubstrateRecipe>
        {
            // Run 1 — alpine woodland: deep turf shelves, weathered pasture, and the scree the peaks shed.
            ["highland_pass"] = recipe(HIGHLAND_SUBSTRATE_INPUT),
            // Run 2 — rain-slick city: wet asphalt, cracked lots where the weeds win, sealed plaza deck.
            ["noir_sprawl"] = recipe(new TerrainSubstrateInput
            {
                key = "noir_sprawl",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 66,
                rockSurface = TerrainSurface.Metal,
                facies = new[]
                {
                    facies(
                        "wet-asphalt",
                        TerrainSurface.Metal,
                        TerrainSurface.Metal,
                        40,
                        0.24,
                        0.06,
                        0.04,
                        1.55),
                    facies("cracked-lot", TerrainSurface.Floor, TerrainSurface.Floor, 30, 0.6, 0.42, 0.3, 1.2),
                    facies(
                        "sodium-plaza",
                        TerrainSurface.Metal,
                        TerrainSurface.Metal,
                        18,
                        0.14,
                        0.02,
                        0.02,
                        1.7),
                    facies("ditch-weed", TerrainSurface.Grass, TerrainSurface.Floor, 12, 0.95, 0.78, 0.5, 0.9),
                },
            }),
            // Run 3 — marble borough above the clouds: dressed stone, sun-bleached lawn, drifted cloud sand.
            ["olympian_sky_borough"] = recipe(new TerrainSubstrateInput
            {
                key = "olympian_sky_borough",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 72,
                facies = new[]
                {
                    facies(
                        "dressed-marble",
                        TerrainSurface.Stone,
                        TerrainSurface.Stone,
                        34,
                        0.2,
                        0.05,
                        0.06,
                        1.6),
                    facies(
                        "temple-lawn",
                        TerrainSurface.Grass,
                        TerrainSurface.Floor,
                        30,
                        1.05,
                        0.92,
                        1.1,
                        0.82),
                    facies("cloud-sand", TerrainSurface.Sand, TerrainSurface.Sand, 22, 0.34, 0.1, 0.12, 1.45),
                    facies(
                        "olive-terrace",
                        TerrainSurface.Floor,
                        TerrainSurface.Floor,
                        14,
                        0.76,
                        0.5,
                        0.6,
                        1.12),
                },
            }),
            // Run 4 — the complete normal ground grammar, recoloured by the Sakura material theme.
            ["sakura_temple_dream"] = recipe(withKey(HIGHLAND_SUBSTRATE_INPUT, "sakura_temple_dream")),
            // Run 5 — the deep: silt plains, shell gravel, weed meadow, bare basalt.
            ["abyssal_deepsea"] = recipe(new TerrainSubstrateInput
            {
                key = "abyssal_deepsea",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 76,
                facies = new[]
                {
                    facies("abyssal-silt", TerrainSurface.Sand, TerrainSurface.Sand, 36, 0.46, 0.16, 0.12, 1.5),
                    facies("weed-meadow", TerrainSurface.Grass, TerrainSurface.Floor, 26, 1, 0.9, 0.66, 0.84),
                    facies(
                        "shell-gravel",
                        TerrainSurface.Stone,
                        TerrainSurface.Stone,
                        22,
                        0.28,
                        0.08,
                        0.08,
                        1.72),
                    facies("basalt-flat", TerrainSurface.Floor, TerrainSurface.Floor, 16, 0.42, 0.2, 0.14, 1.4),
                },
            }),
            // Run 6 — prism meadow: saturated turf, chalk flats, crystal sand.
            ["rainbowland"] = recipe(new TerrainSubstrateInput
            {
                key = "rainbowland",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 68,
                facies = new[]
                {
                    facies("prism-turf", TerrainSurface.Grass, TerrainSurface.Floor, 40, 1.1, 1.06, 1.3, 0.8),
                    facies(
                        "chalk-flat",
                        TerrainSurface.Floor,
                        TerrainSurface.Floor,
                        26,
                        0.62,
                        0.38,
                        0.52,
                        1.24),
                    facies("crystal-sand", TerrainSurface.Sand, TerrainSurface.Sand, 20, 0.34, 0.12, 0.24, 1.5),
                    facies(
                        "lichen-shelf",
                        TerrainSurface.Stone,
                        TerrainSurface.Stone,
                        14,
                        0.4,
                        0.18,
                        0.12,
                        1.55),
                },
            }),
            // Run 7 — clockwork bazaar: brass deck, oil-dark grit, carpet moss between the stalls.
            ["clockwork_moon_bazaar"] = recipe(new TerrainSubstrateInput
            {
                key = "clockwork_moon_bazaar",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 64,
                rockSurface = TerrainSurface.Metal,
                facies = new[]
                {
                    facies("brass-deck", TerrainSurface.Metal, TerrainSurface.Metal, 36, 0.2, 0.05, 0.06, 1.62),
                    facies("oil-grit", TerrainSurface.Floor, TerrainSurface.Floor, 28, 0.44, 0.16, 0.1, 1.46),
                    facies("stall-moss", TerrainSurface.Grass, TerrainSurface.Floor, 20, 0.92, 0.74, 0.6, 0.9),
                    facies("moon-dust", TerrainSurface.Sand, TerrainSurface.Sand, 16, 0.3, 0.1, 0.12, 1.55),
                },
            }),
            // Run 8 — sugar carnival: candy floss lawn, spun-sugar sand, sticky boardwalk.
            ["sugarstorm_carnival"] = recipe(new TerrainSubstrateInput
            {
                key = "sugarstorm_carnival",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 66,
                facies = new[]
                {
                    facies("floss-lawn", TerrainSurface.Grass, TerrainSurface.Floor, 38, 1.08, 1.02, 1.32, 0.8),
                    facies("spun-sand", TerrainSurface.Sand, TerrainSurface.Sand, 26, 0.36, 0.12, 0.28, 1.48),
                    facies("toffee-board", TerrainSurface.Floor, TerrainSurface.Floor, 22, 0.5, 0.24, 0.3, 1.4),
                    facies(
                        "brittle-shelf",
                        TerrainSurface.Stone,
                        TerrainSurface.Stone,
                        14,
                        0.3,
                        0.1,
                        0.12,
                        1.6),
                },
            }),
            // Run 9 — prismglass archive: polished floor, glass grit, the moss that got in anyway.
            ["prismglass_archive"] = recipe(new TerrainSubstrateInput
            {
                key = "prismglass_archive",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 62,
                rockSurface = TerrainSurface.Stone,
                facies = new[]
                {
                    facies(
                        "polished-floor",
                        TerrainSurface.Stone,
                        TerrainSurface.Stone,
                        36,
                        0.22,
                        0.06,
                        0.08,
                        1.62),
                    facies("glass-grit", TerrainSurface.Sand, TerrainSurface.Sand, 28, 0.3, 0.1, 0.14, 1.55),
                    facies(
                        "archive-moss",
                        TerrainSurface.Grass,
                        TerrainSurface.Floor,
                        20,
                        0.9,
                        0.76,
                        0.62,
                        0.88),
                    facies("dust-flat", TerrainSurface.Floor, TerrainSurface.Floor, 16, 0.48, 0.22, 0.18, 1.38),
                },
            }),
            // Run 10 — starforged cathedral: burnt nave, ember ash, the last surviving turf.
            ["starforged_cathedral_endrun"] = recipe(new TerrainSubstrateInput
            {
                key = "starforged_cathedral_endrun",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 70,
                facies = new[]
                {
                    facies("burnt-nave", TerrainSurface.Stone, TerrainSurface.Stone, 36, 0.24, 0.06, 0.05, 1.7),
                    facies("ember-ash", TerrainSurface.Sand, TerrainSurface.Sand, 28, 0.28, 0.08, 0.06, 1.6),
                    facies("star-flag", TerrainSurface.Floor, TerrainSurface.Floor, 20, 0.46, 0.2, 0.14, 1.42),
                    facies("vigil-turf", TerrainSurface.Grass, TerrainSurface.Floor, 16, 0.9, 0.72, 0.54, 0.9),
                },
            }),
            // The alien ranch: grazed alien sward, mineral pan, spore crust.
            ["alien_ranch"] = recipe(new TerrainSubstrateInput
            {
                key = "alien_ranch",
                ecology = NEUTRAL_ECOLOGY,
                regionCell = 72,
                facies = new[]
                {
                    facies(
                        "grazed-sward",
                        TerrainSurface.Grass,
                        TerrainSurface.Floor,
                        34,
                        0.98,
                        0.86,
                        0.7,
                        0.86),
                    facies("mineral-pan", TerrainSurface.Sand, TerrainSurface.Sand, 28, 0.32, 0.1, 0.12, 1.55),
                    facies(
                        "spore-crust",
                        TerrainSurface.Floor,
                        TerrainSurface.Floor,
                        22,
                        0.54,
                        0.28,
                        0.36,
                        1.35),
                    facies("shale-shelf", TerrainSurface.Stone, TerrainSurface.Stone, 16, 0.3, 0.1, 0.08, 1.62),
                },
            }),
            // The Hub keeps the tuned ecology it was authored with, gaining only a worn court and a gravel verge.
            ["hub"] = recipe(new TerrainSubstrateInput
            {
                key = "hub",
                ecology = new TerrainSubstrateEcology { meadow = 1, grass = 1, flowers = 1.08, bare = 1 },
                regionCell = 58,
                facies = new[]
                {
                    facies("commons-turf", TerrainSurface.Grass, TerrainSurface.Floor, 46, 1, 1, 1.08, 0.9),
                    facies("worn-court", TerrainSurface.Floor, TerrainSurface.Floor, 32, 0.66, 0.4, 0.44, 1.28),
                    facies(
                        "gravel-verge",
                        TerrainSurface.Stone,
                        TerrainSurface.Stone,
                        22,
                        0.34,
                        0.12,
                        0.14,
                        1.6),
                },
            }),
            // Sealed combat floors carry no ecology at all; the registry states that instead of a branch stating it.
            ["arena"] = recipe(new TerrainSubstrateInput
            {
                key = "arena",
                ecology = new TerrainSubstrateEcology { meadow = 0.12, grass = 0.08, flowers = 0, bare = 1 },
                regionCell = 40,
                facies = new[]
                {
                    facies("arena-sand", TerrainSurface.Sand, TerrainSurface.Sand, 62, 0.12, 0.08, 0, 1.4),
                    facies("arena-flag", TerrainSurface.Stone, TerrainSurface.Stone, 38, 0.08, 0.04, 0, 1.55),
                },
            }),
            ["raid_holdthefort"] = recipe(new TerrainSubstrateInput
            {
                key = "raid_holdthefort",
                ecology = new TerrainSubstrateEcology { meadow = 0, grass = 0, flowers = 0, bare = 1 },
                regionCell = 44,
                facies = new[]
                {
                    facies("fort-flag", TerrainSurface.Stone, TerrainSurface.Stone, 60, 0, 0, 0, 1.5),
                    facies("siege-mud", TerrainSurface.Floor, TerrainSurface.Floor, 40, 0, 0, 0, 1.68),
                },
            }),
            ["raid_wyrmforge"] = recipe(new TerrainSubstrateInput
            {
                key = "raid_wyrmforge",
                ecology = new TerrainSubstrateEcology { meadow = 0.22, grass = 0.18, flowers = 0.025, bare = 1 },
                regionCell = 46,
                rockSurface = TerrainSurface.Metal,
                facies = new[]
                {
                    facies(
                        "forge-slag",
                        TerrainSurface.Metal,
                        TerrainSurface.Metal,
                        56,
                        0.22,
                        0.18,
                        0.025,
                        1.6),
                    facies("cinder-bed", TerrainSurface.Sand, TerrainSurface.Sand, 44, 0.22, 0.18, 0.025, 1.66),
                },
            }),
        };

    public static TerrainSubstrateRecipe terrainSubstrateFor(string? biomeKey)
    {
        // `(biomeKey && TERRAIN_SUBSTRATE_RECIPES[biomeKey]) || DEFAULT_TERRAIN_SUBSTRATE`
        if (!string.IsNullOrEmpty(biomeKey) && TERRAIN_SUBSTRATE_RECIPES.TryGetValue(biomeKey, out var found) && found != null)
            return found;
        return DEFAULT_TERRAIN_SUBSTRATE;
    }

    /// <summary>
    /// Which facies covers one world tile.
    ///
    /// Two broad octaves compose one continuous 0..1 coordinate, which the recipe's cumulative shares partition
    /// into regions. Because the coordinate is continuous, a region is a contiguous blob with a soft, wandering
    /// boundary rather than a lattice cell — and because it is a pure function of world coordinates it is
    /// identical on both sides of every immutable chunk seam, on server and client alike.
    /// </summary>
    public static int terrainSubstrateFaciesIndexAt(
        TerrainSubstrateRecipe substrate,
        double worldCellX,
        double worldCellY)
    {
        double coordinate = terrainSubstrateCoordinateAt(substrate, worldCellX, worldCellY);
        double[] cumulative = substrate.cumulative;
        for (int index = 0; index < cumulative.Length; index++)
            if (coordinate < cumulative[index]) return index;
        return cumulative.Length - 1;
    }

    public static TerrainSubstrateFacies terrainSubstrateFaciesAt(
        TerrainSubstrateRecipe substrate,
        double worldCellX,
        double worldCellY) =>
        substrate.facies[terrainSubstrateFaciesIndexAt(substrate, worldCellX, worldCellY)];

    /// <summary>
    /// The facies coordinate itself, 0..1 — **rank-equalised**, so an authored share is the share a world gets.
    ///
    /// The raw composition of two value-noise octaves is bell-shaped, not uniform: partitioning it directly by
    /// cumulative share handed the middle bands far more ground than they asked for and starved the ends. Measured
    /// on `highland_pass`, an authored 18 % facies covered 4.7 % of the world while a 24 % facies covered 35.8 %.
    /// The coordinate is therefore mapped through the field's own quantiles before it is partitioned, which makes
    /// "38 % alp-sward" mean 38 % of the ground rather than 38 % of an interval nothing occupies.
    /// </summary>
    private static double terrainSubstrateCoordinateAt(
        TerrainSubstrateRecipe substrate,
        double worldCellX,
        double worldCellY)
    {
        int salt = substrate.substrateSalt;
        // The primitive's own broad octave is 8.5 cells wide; dividing the coordinates lifts that to the recipe's
        // authored region lattice without touching the shared noise.
        double scale = substrate.regionCell / 8.5;
        return equalise(rawSubstrateCoordinate(salt, worldCellX / scale, worldCellY / scale, scale));
    }

    private static double rawSubstrateCoordinate(int salt, double x, double y, double scale)
    {
        double body = TerrainCompositionField.terrainEcologyPatchWithSalt(x, y, salt);
        double drift = TerrainCompositionField.terrainEcologyPatchWithSalt(
            (y * scale + 137.5) / (scale * 1.7),
            (x * scale - 91.25) / (scale * 1.7),
            salt ^ 0x2545f491);
        return body * 0.68 + drift * 0.32;
    }

    /*
     * Quantiles of rawSubstrateCoordinate, resolved once.
     *
     * The noise construction is identical for every salt — only the lattice values differ — so one table
     * describes the distribution for every recipe. It is built from a fixed lattice at module load, which makes
     * it a constant of the build rather than something a caller can perturb.
     */
    private const int EQUALISE_STEPS = 128;

    // Declared after every recipe field so the static initialisers run in the original module's order.
    private static readonly double[] EQUALISE_TABLE = buildEqualiseTable();

    /// <summary>The module-level IIFE that builds <see cref="EQUALISE_TABLE"/>.</summary>
    private static double[] buildEqualiseTable()
    {
        // The sample lattice has to span MANY noise cells or the table describes one accident of the field rather
        // than its distribution: at 87 units it covered ten lattice cells and left the first facies 14 points over
        // its authored share. This spans about 150.
        const int side = 128;
        const int SAMPLES = side * side;
        var values = new double[SAMPLES];
        int salt = TerrainCompositionField.terrainEcologyBiomeSalt("substrate:equalise");
        for (int index = 0; index < SAMPLES; index++)
        {
            // An irrational stride keeps the sample lattice from aligning with the noise lattice.
            double x = (index % side) * 9.973;
            double y = Math.floor((double)index / side) * 9.973;
            values[index] = rawSubstrateCoordinate(salt, x, y, 8.7);
        }
        // Float64Array.prototype.sort() is a NUMERIC sort; every sample is a finite value in [0, 1], so an
        // (unstable) numeric Array.Sort yields the identical sequence.
        System.Array.Sort(values);
        var table = new double[EQUALISE_STEPS + 1];
        for (int step = 0; step <= EQUALISE_STEPS; step++)
            table[step] =
                values[(int)Math.min(SAMPLES - 1, Math.round(((double)step / EQUALISE_STEPS) * (SAMPLES - 1)))];
        return table;
    }

    /// <summary>Map a raw coordinate onto its rank in the field's distribution, linearly inside each quantile bucket.</summary>
    private static double equalise(double value)
    {
        if (value <= EQUALISE_TABLE[0]) return 0;
        if (value >= EQUALISE_TABLE[EQUALISE_STEPS]) return 0.999999;
        int low = 0;
        int high = EQUALISE_STEPS;
        while (high - low > 1)
        {
            int mid = (low + high) >> 1;
            if (value >= EQUALISE_TABLE[mid]) low = mid;
            else high = mid;
        }
        double lo = EQUALISE_TABLE[low];
        double hi = EQUALISE_TABLE[high];
        double within = hi > lo ? (value - lo) / (hi - lo) : 0;
        return Math.min(0.999999, (low + within) / EQUALISE_STEPS);
    }
}
