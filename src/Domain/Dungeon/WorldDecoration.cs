// Port of packages/shared/src/domain/dungeon/worldDecoration.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Canonical visual DNA for collision-neutral world decoration.
//
// Placement belongs to the owning world (authored Hub dressing or generated terrain render plan), while this
// registry owns the silhouette variation once. A decoration therefore keeps the same proportions and ageing
// language everywhere it is explicitly used; the active biome supplies pigment, never a second geometry family.
//
// The registry also owns the one placement rule every producer obeys: which ground a kind can stand on
// (terrainTileAcceptsDecoration). Nothing grows in open air, and a river carries water growth only.

/// <summary>
/// The complete short-growth vocabulary: undergrowth and the timber a felled tree leaves behind.
///
/// This list is deliberately tiny, and it is the whole of it. The world grows plants — nothing is *placed* in
/// it as ornament. A world that needs a silhouette grows one.
/// </summary>
public static class WorldDecorationKind
{
    public const string Stump = "stump";
    public const string Thicket = "thicket";
}

// `WorldDecorationVisualKind` = `WorldDecorationKind` → `string`.

/// <summary>
/// Decorations an authored terrain artifact can place manually on individual cells.
///
/// `Tree` is the one tall slot: THE canonical tree every world in the game shares, recoloured by that world's
/// registry row. There is no second tall slot — a world states its identity through pigment, landform and the
/// shape of its own tree, never through a bespoke set piece standing next to one.
/// </summary>
public static class TerrainDecorationKind
{
    public const string Tree = "tree";
    // `...WorldDecorationKind`
    public const string Stump = WorldDecorationKind.Stump;
    public const string Thicket = WorldDecorationKind.Thicket;
}

/// <summary>
/// An authored, collision-neutral decoration on one artifact cell. The TS fields are `readonly`; they stay
/// plain public fields here so object initializers and `{ ...placement, x }` copies (<see cref="Clone"/>) work.
/// </summary>
public sealed class TerrainDecorationPlacement
{
    /// <summary>A TerrainDecorationKind value.</summary>
    public string kind = "";
    /// <summary>Artifact-local cell coordinate.</summary>
    public int tx;
    /// <summary>Artifact-local cell coordinate.</summary>
    public int ty;
    /// <summary>Stable visual-variation seed.</summary>
    public double seed;
    /// <summary>Explicit visual theme selected by the author. Missing means inherit the cell/base biome.</summary>
    public string? themeKey;
    /// <summary>
    /// The regrowth stage this tree currently draws in, or missing for the whole, mature specimen.
    ///
    /// A **presentation-side annotation**, never part of the artifact and never on the terrain wire: the
    /// client's dungeon mirror stamps it from the tree-life record (`world/treeLife.ts`) as it hands
    /// decorations to the render plan, which is the one consumer. Authoring tools and the generator leave
    /// it absent — a generated tree is mature by definition.
    /// </summary>
    public int? growthStage;

    public TerrainDecorationPlacement Clone() => (TerrainDecorationPlacement)MemberwiseClone();
}

/// <summary>
/// The ground a decoration needs under it.
///
/// A prop is authored for exactly one habitat: land growth roots in soil or rock, water growth floats on or
/// rises out of a water surface. Open air is not a habitat — a chasm reads as depth only while it stays
/// empty, so a void carries nothing at all.
/// </summary>
public static class DecorationHabitat
{
    public const string Land = "land";
    public const string Water = "water";
}

public sealed class WorldDecorationVisual
{
    /// <summary>A WorldDecorationVisualKind value.</summary>
    public string kind = "";
    public double scale;
    public double rotation;
    public double phase;
    /// <summary>Bounded discrete silhouette choice (0 | 1 | 2). Renderers interpret it generically, never by biome.</summary>
    public int variant;
    /// <summary>Number of authored masses in a cluster (stones, crystal blades, roots or flame tongues).</summary>
    public int cluster;
    /// <summary>0..1 chipped/leaning/asymmetric treatment.</summary>
    public double weathering;
    /// <summary>0..1 pigment selection within the active world's accent family.</summary>
    public double accent;

    public WorldDecorationVisual Clone() => (WorldDecorationVisual)MemberwiseClone();
}

public static class WorldDecoration
{
    public static readonly IReadOnlyList<string> WORLD_DECORATION_KINDS = Array.AsReadOnly(new[]
    {
        WorldDecorationKind.Stump,
        WorldDecorationKind.Thicket,
    });

    public static readonly IReadOnlyList<string> TERRAIN_DECORATION_KINDS = Array.AsReadOnly(
        new List<string> { TerrainDecorationKind.Tree }.concat(WORLD_DECORATION_KINDS).ToArray());

    /// <summary>TS `isTerrainDecorationKind(value: unknown)`; decoration kinds are strings here.</summary>
    public static bool isTerrainDecorationKind(string? value) => value != null && TERRAIN_DECORATION_KINDS.includes(value);

    /// <summary>
    /// Habitat per decoration kind — the data half of the placement rule.
    ///
    /// Every kind in the registry today is land growth; the water column is carried by the render plan's own
    /// reed beds and lily pads. Making the habitat explicit per kind is what lets a future paintable water plant
    /// (seagrass, water lily) become a single row here instead of a placement branch in the editor, the world
    /// generators and the renderer.
    /// </summary>
    public static readonly JsMap<string, string> TERRAIN_DECORATION_HABITAT = new JsMap<string, string>()
        .set("tree", DecorationHabitat.Land)
        .set("stump", DecorationHabitat.Land)
        .set("thicket", DecorationHabitat.Land);

    /// <summary>
    /// The habitat a physical terrain cell offers, or null (TS: undefined) when it can carry no decoration.
    ///
    /// Rock, ground, a plank deck and the space beneath one are all land: a prop stands on their surface. Water
    /// offers the water column. A chasm offers nothing.
    /// </summary>
    public static string? terrainTileDecorationHabitat(int tile)
    {
        if (tile == TileType.Chasm) return null;
        if (tile == TileType.Water) return DecorationHabitat.Water;
        return DecorationHabitat.Land;
    }

    /// <summary>
    /// THE decoration placement rule: may this kind stand on this physical tile?
    ///
    /// Every producer asks this one question — the terrain editor while painting, the world generators while
    /// composing, and the render plan as the final gate on an authored set it did not create. An unknown kind is
    /// refused everywhere, so a stale payload can never float a prop over a ravine.
    /// </summary>
    public static bool terrainTileAcceptsDecoration(string kind, int tile)
    {
        string? habitat = terrainTileDecorationHabitat(tile);
        return habitat != null && kind != null && TERRAIN_DECORATION_HABITAT.get(kind) == habitat;
    }

    // type WorldDecorationProfileId = 'sanctuary' | 'natural' | 'ceremonial' | 'arcane' | 'industrial' | 'playful' → string

    private sealed class WorldDecorationProfile
    {
        public readonly (double, double) scale;
        public readonly (double, double) weathering;
        public readonly (double, double) accent;
        public readonly int clusterBonus;

        public WorldDecorationProfile((double, double) scale, (double, double) weathering, (double, double) accent, int clusterBonus)
        {
            this.scale = scale;
            this.weathering = weathering;
            this.accent = accent;
            this.clusterBonus = clusterBonus;
        }
    }

    private static readonly Dictionary<string, WorldDecorationProfile> PROFILES = new()
    {
        ["sanctuary"] = new WorldDecorationProfile((0.96, 1.18), (0.22, 0.62), (0.18, 0.72), 1),
        ["natural"] = new WorldDecorationProfile((0.88, 1.24), (0.38, 0.92), (0.08, 0.68), 0),
        ["ceremonial"] = new WorldDecorationProfile((0.96, 1.3), (0.12, 0.52), (0.48, 0.96), 1),
        ["arcane"] = new WorldDecorationProfile((0.94, 1.28), (0.18, 0.7), (0.62, 1), 1),
        ["industrial"] = new WorldDecorationProfile((0.9, 1.18), (0.42, 0.96), (0.24, 0.82), 0),
        ["playful"] = new WorldDecorationProfile((0.92, 1.26), (0.08, 0.48), (0.58, 1), 2),
    };

    private static readonly Dictionary<string, string> BIOME_PROFILE = new()
    {
        ["hub"] = "sanctuary",
        ["museum"] = "sanctuary",
        ["highland_pass"] = "natural",
        ["viking_ship_village"] = "ceremonial",
        ["alien_ranch"] = "arcane",
        ["sakura_temple_dream"] = "ceremonial",
        ["olympian_sky_borough"] = "ceremonial",
        ["raid_holdthefort"] = "ceremonial",
        ["abyssal_deepsea"] = "arcane",
        ["rainbowland"] = "arcane",
        ["prismglass_archive"] = "arcane",
        ["starforged_cathedral_endrun"] = "ceremonial",
        ["noir_sprawl"] = "industrial",
        ["clockwork_moon_bazaar"] = "industrial",
        ["raid_hollowcartography"] = "industrial",
        ["sugarstorm_carnival"] = "playful",
    };

    /// <summary>
    /// Per-kind scale bands.
    ///
    /// These are a RELATIVE variation hint, not an absolute size: a renderer that treats them as a multiplier on
    /// an already-absolute height double-counts them, which is how the old monoliths ended up nearly eight player
    /// heights tall. Renderers normalise against <see cref="worldDecorationScaleMid"/> and keep only the deviation.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double, double)> WORLD_DECORATION_KIND_SCALE =
        new Dictionary<string, (double, double)>
        {
            ["stump"] = (0.82, 1.12),
            ["thicket"] = (0.88, 1.2),
        };

    private static double sample(double seed, int salt)
    {
        int h = Math.imul(Js.ToInt32(seed) ^ salt, 0x45d9f3b);
        h = Math.imul(h ^ (int)((uint)h >> 16), 0x45d9f3b);
        return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
    }

    private static double range((double, double) bounds, double value) => bounds.Item1 + (bounds.Item2 - bounds.Item1) * value;

    /// <summary>TS `Partial&lt;Omit&lt;WorldDecorationVisual, 'kind'&gt;&gt;`; a null field is "not overridden".</summary>
    public sealed class WorldDecorationVisualOverrides
    {
        public double? scale;
        public double? rotation;
        public double? phase;
        public int? variant;
        public int? cluster;
        public double? weathering;
        public double? accent;
    }

    /// <summary>Resolve stable, renderer-neutral variation for one decoration placement.</summary>
    public static WorldDecorationVisual createWorldDecorationVisual(
        double seed,
        string? biomeKey,
        string kind,
        WorldDecorationVisualOverrides? overrides = null)
    {
        overrides ??= new WorldDecorationVisualOverrides();
        var profile = PROFILES[BIOME_PROFILE.TryGetValue(biomeKey ?? "", out var profileId) ? profileId : "natural"];
        var kindScale = WORLD_DECORATION_KIND_SCALE[kind];
        double profileScale = range(profile.scale, sample(seed, 601));
        var visual = new WorldDecorationVisual
        {
            kind = kind,
            scale = profileScale * range(kindScale, sample(seed, 607)),
            rotation = sample(seed, 613) * Math.PI * 2,
            phase = sample(seed, 617),
            variant = (int)Math.floor(sample(seed, 619) * 3),
            cluster = (int)Math.min(5, 2 + profile.clusterBonus + Math.floor(sample(seed, 631) * 2)),
            weathering = range(profile.weathering, sample(seed, 641)),
            accent = range(profile.accent, sample(seed, 643)),
        };
        // `{ ...visual, ...overrides }`
        var result = visual.Clone();
        if (overrides.scale.HasValue) result.scale = overrides.scale.Value;
        if (overrides.rotation.HasValue) result.rotation = overrides.rotation.Value;
        if (overrides.phase.HasValue) result.phase = overrides.phase.Value;
        if (overrides.variant.HasValue) result.variant = overrides.variant.Value;
        if (overrides.cluster.HasValue) result.cluster = overrides.cluster.Value;
        if (overrides.weathering.HasValue) result.weathering = overrides.weathering.Value;
        if (overrides.accent.HasValue) result.accent = overrides.accent.Value;
        return result;
    }

    /// <summary>
    /// The middle of a kind's scale band.
    ///
    /// Renderers divide <see cref="WorldDecorationVisual.scale"/> by this to recover a pure ±variation around 1,
    /// so an absolute authored height (a standing stone is ~3 player heights) stays absolute and the placement's
    /// scale only nudges it.
    /// </summary>
    public static double worldDecorationScaleMid(string kind)
    {
        var band = WORLD_DECORATION_KIND_SCALE[kind];
        // The profile scale bands all centre on ~1.06, and it multiplies the kind band before reaching a renderer.
        return ((band.Item1 + band.Item2) / 2) * 1.06;
    }
}
