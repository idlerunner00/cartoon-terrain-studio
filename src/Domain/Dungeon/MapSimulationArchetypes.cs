// Port of packages/shared/src/domain/dungeon/mapSimulationArchetypes.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;

namespace Fluitown.Domain;

/*
 * The world-archetype registry behind **Simulate Map** — the composition DNA a simulated map is rolled from.
 *
 * A simulated map is never "some noise in a rectangle": it is one *place* with a readable silhouette, a
 * terraced landform, a hydrology, a road network, wonders and an ecology. All of that is
 * data here, so a new kind of world is one registry entry — never a new branch inside the composer.
 *
 * Every field is a bounded ratio or count multiplier, deliberately expressed relative to the map's cell count
 * so the same archetype composes a 64x48 sketch and a 320x240 continent with the same identity.
 */

/// <summary>How a composed map closes itself off from the outside — the silhouette the whole composition reads as.</summary>
public static class MapFrameKind
{
    /// <summary>Land surrounded by open water, with a soft shoreline: the map reads as an island.</summary>
    public const string Island = "island";
    /// <summary>Rock walls climb around the border; the interior is one sheltered valley floor.</summary>
    public const string Valley = "valley";
    /// <summary>The land rises toward the middle and drops away at the rim — a raised table.</summary>
    public const string Plateau = "plateau";
    /// <summary>The land sinks toward the middle; the rim is the high ground looking in.</summary>
    public const string Basin = "basin";
    /// <summary>Deep rifts bite into the border instead of rock or water — a shattered shelf.</summary>
    public const string Shelf = "shelf";
    /// <summary>
    /// No frame at all: the landform simply runs off the edge of the sheet, the way a crop out of a larger world
    /// does. A map that is *always* boxed in by a wall reads as the same map every time, whatever is inside it —
    /// this is the kind that lets a composition end wherever it happens to end.
    /// </summary>
    public const string Open = "open";
}

public sealed class MapSimulationArchetype
{
    public string key = "";
    /// <summary>Human title used by the generator UI and the exported artifact name.</summary>
    public string name = "";
    /// <summary>One-line composition intent, shown while the map builds.</summary>
    public string summary = "";
    /// <summary>Endless landform tier used for the bedrock pass (1..5 — higher reads more open and monumental).</summary>
    public int tier;
    /// <summary>
    /// Frames (<see cref="MapFrameKind"/> values) this archetype may close itself with, rolled per map. A
    /// single-entry list pins the silhouette; a list is what keeps two maps of the same archetype from opening with
    /// the same border every time.
    /// </summary>
    public IReadOnlyList<string> frames = Array.Empty<string>();
    /// <summary>
    /// Target share of the framed interior that must end up walkable, standing water and rift included. It is a
    /// FLOOR, not a cap: where the endless crop comes up solid, the composer opens clearings until the map
    /// reaches it, so no simulated world ever ships a dead quarter.
    /// </summary>
    public double openness;
    /// <summary>0..1 — how much of the border band the frame claims (of the smaller map axis).</summary>
    public double frameDepth;
    /// <summary>0..1 — irregularity of the frame boundary. 0 is a clean geometric rim, 1 a heavily bitten coast.</summary>
    public double frameNoise;
    /// <summary>
    /// Signed macro relief bias applied over the bedrock's own elevation, in elevation levels at the map's
    /// centre. Positive lifts a dome, negative sinks a bowl; the bedrock noise rides on top either way.
    /// </summary>
    public double reliefBias;
    /// <summary>
    /// How many walkable plateau bands the landform is quantized into.
    ///
    /// This is the single most important silhouette field. A smooth height field reads as noise from above,
    /// because nothing in it draws a line; banding the same field into shelves and letting the terrain contract
    /// materialize a rock cliff at every band edge is what turns a heightmap into a *landform* you can read at a
    /// glance. 0 keeps the old continuous slope for archetypes that genuinely want one.
    /// </summary>
    public int terraceBands;
    /// <summary>Elevation levels between neighbouring bands. Larger reads as taller cliffs and fewer, bolder shelves.</summary>
    public int terraceStep;
    /// <summary>0..1 — how ragged a band edge is. 0 is a contour line, 1 is a broken escarpment.</summary>
    public double terraceNoise;
    /// <summary>
    /// How far the rock stands above the ground it borders, in elevation levels — the *depth* of the world.
    ///
    /// A wall a single level above its floor is a kerb, and a world of kerbs reads flat from the fixed camera
    /// no matter how carefully its shelves were banded. This is an average: the composer samples a smooth
    /// massif field around it, so one map carries low banks along its water and towering faces around its
    /// uplands. The renderer adds its own skyline rise on top of whatever is stored here.
    /// </summary>
    public double wallRelief;
    /// <summary>Target share of the composed walkable interior that ends up Water (courses + basins), 0..1.</summary>
    public double waterBudget;
    /// <summary>Target share of the composed walkable interior that ends up Chasm, 0..1.</summary>
    public double riftBudget;
    /// <summary>Flora records per walkable cell. The authored Tutorial map sits at 0.137 — that is the bar.</summary>
    public double floraDensity;
    /// <summary>Share of flora records that are trees rather than undergrowth/relics, 0..1.</summary>
    public double canopyShare;
    /// <summary>Monumental set pieces (monolith / arch / wayshrine) per 4096 cells.</summary>
    public double landmarkDensity;
    /// <summary>Wonders (see <see cref="MapSetPieces.MAP_WONDERS"/>) per 4096 cells.</summary>
    public double wonderDensity;
    /// <summary>Wonder keys this archetype leans toward; the roll still reaches the whole catalog.</summary>
    public IReadOnlyList<string> wonderAffinity = Array.Empty<string>();
    /// <summary>
    /// Avenue trunks per 4096 cells. Roads are what tell the eye where a world's life happens, and a map with
    /// none of them reads as wilderness however densely it is dressed.
    /// </summary>
    public double roadDensity;
    /// <summary>Inclusive number of theme regions this archetype partitions the map into.</summary>
    public (int, int) themeRegions;
    /// <summary>Roll weight — how often a random simulation picks this archetype.</summary>
    public double weight;
}

public static class MapSimulationArchetypes
{
    public static readonly IReadOnlyList<string> MAP_FRAME_KINDS = Array.AsReadOnly(new[]
    {
        MapFrameKind.Island,
        MapFrameKind.Valley,
        MapFrameKind.Plateau,
        MapFrameKind.Basin,
        MapFrameKind.Shelf,
        MapFrameKind.Open,
    });

    private static IReadOnlyList<string> List(params string[] values) => Array.AsReadOnly(values);

    /// <summary>
    /// The registry. Ordering is stable and part of the deterministic roll, so appending an archetype never
    /// re-rolls existing seeds into a different world — it only makes new worlds reachable.
    /// </summary>
    public static readonly IReadOnlyList<MapSimulationArchetype> MAP_SIMULATION_ARCHETYPES = Array.AsReadOnly(new[]
    {
        new MapSimulationArchetype
        {
            key = "highland_basin",
            name = "Highland Basin",
            summary = "A sheltered upland floor ringed by climbing rock, veined with meltwater",
            tier = 2,
            frames = List(MapFrameKind.Valley, MapFrameKind.Basin, MapFrameKind.Open),
            openness = 0.4,
            frameDepth = 0.085,
            frameNoise = 0.55,
            reliefBias = -1.1,
            terraceBands = 4,
            terraceStep = 2,
            terraceNoise = 0.55,
            wallRelief = 1.82,
            waterBudget = 0.14,
            riftBudget = 0.04,
            floraDensity = 0.125,
            canopyShare = 0.62,
            landmarkDensity = 1.1,
            wonderDensity = 1.1,
            wonderAffinity = List("sacred_grove", "terraced_quarry", "cliff_switchback"),
            roadDensity = 1.4,
            themeRegions = (1, 2),
            weight = 1.15,
        },
        new MapSimulationArchetype
        {
            key = "river_delta",
            name = "River Delta",
            summary = "Braided water splits the low country into a fan of green islands",
            tier = 2,
            frames = List(MapFrameKind.Island, MapFrameKind.Open, MapFrameKind.Basin),
            openness = 0.42,
            frameDepth = 0.07,
            frameNoise = 0.72,
            reliefBias = -0.4,
            terraceBands = 3,
            terraceStep = 1,
            terraceNoise = 0.7,
            wallRelief = 1.29,
            waterBudget = 0.28,
            riftBudget = 0.0,
            floraDensity = 0.15,
            canopyShare = 0.7,
            landmarkDensity = 0.7,
            wonderDensity = 1.0,
            wonderAffinity = List("great_causeway", "sacred_grove", "boiling_springs"),
            roadDensity = 1.7,
            themeRegions = (2, 3),
            weight = 1,
        },
        new MapSimulationArchetype
        {
            key = "rift_plateau",
            name = "Rift Plateau",
            summary = "A raised table split by deep rifts and stitched back together by spans",
            tier = 3,
            frames = List(MapFrameKind.Plateau, MapFrameKind.Shelf, MapFrameKind.Open),
            openness = 0.38,
            frameDepth = 0.08,
            frameNoise = 0.5,
            reliefBias = 1.5,
            terraceBands = 3,
            terraceStep = 3,
            terraceNoise = 0.35,
            wallRelief = 2.28,
            waterBudget = 0.05,
            riftBudget = 0.17,
            floraDensity = 0.085,
            canopyShare = 0.34,
            landmarkDensity = 1.8,
            wonderDensity = 1.35,
            wonderAffinity = List("great_causeway", "spiral_sinkhole", "sky_observatory"),
            roadDensity = 1.2,
            themeRegions = (1, 2),
            weight = 1,
        },
        new MapSimulationArchetype
        {
            key = "island_chain",
            name = "Island Chain",
            summary = "Scattered land in open water, linked by narrow crossings",
            tier = 2,
            frames = List(MapFrameKind.Island),
            openness = 0.4,
            frameDepth = 0.105,
            frameNoise = 0.85,
            reliefBias = 0.3,
            terraceBands = 3,
            terraceStep = 2,
            terraceNoise = 0.65,
            wallRelief = 1.44,
            waterBudget = 0.36,
            riftBudget = 0.0,
            floraDensity = 0.14,
            canopyShare = 0.66,
            landmarkDensity = 1.0,
            wonderDensity = 1.2,
            wonderAffinity = List("great_causeway", "watch_gate", "drowned_caldera"),
            roadDensity = 1.1,
            themeRegions = (2, 4),
            weight = 0.9,
        },
        new MapSimulationArchetype
        {
            key = "terraced_valley",
            name = "Terraced Valley",
            summary = "Stepped shelves descend to a sheltered valley floor",
            tier = 3,
            frames = List(MapFrameKind.Valley, MapFrameKind.Open, MapFrameKind.Plateau),
            openness = 0.38,
            frameDepth = 0.095,
            frameNoise = 0.4,
            reliefBias = -1.6,
            terraceBands = 6,
            terraceStep = 2,
            terraceNoise = 0.3,
            wallRelief = 1.98,
            waterBudget = 0.13,
            riftBudget = 0.05,
            floraDensity = 0.12,
            canopyShare = 0.55,
            landmarkDensity = 0.9,
            wonderDensity = 1.15,
            wonderAffinity = List("cliff_switchback", "terraced_quarry", "sunken_arena"),
            roadDensity = 2.1,
            themeRegions = (2, 3),
            weight = 1,
        },
        new MapSimulationArchetype
        {
            key = "canyon_fork",
            name = "Canyon Fork",
            summary = "Two canyons meet; the land between them is a narrow high spine",
            tier = 3,
            frames = List(MapFrameKind.Shelf, MapFrameKind.Open, MapFrameKind.Plateau),
            openness = 0.4,
            frameDepth = 0.055,
            frameNoise = 0.68,
            reliefBias = 0.8,
            terraceBands = 4,
            terraceStep = 2,
            terraceNoise = 0.5,
            wallRelief = 2.58,
            waterBudget = 0.08,
            riftBudget = 0.2,
            floraDensity = 0.09,
            canopyShare = 0.4,
            landmarkDensity = 1.5,
            wonderDensity = 1.3,
            wonderAffinity = List("great_causeway", "cliff_switchback", "spiral_sinkhole"),
            roadDensity = 1.3,
            themeRegions = (1, 3),
            weight = 0.95,
        },
        new MapSimulationArchetype
        {
            key = "lake_district",
            name = "Lake District",
            summary = "Still water pools between low wooded ridges",
            tier = 2,
            frames = List(MapFrameKind.Basin, MapFrameKind.Island, MapFrameKind.Open),
            openness = 0.4,
            frameDepth = 0.08,
            frameNoise = 0.6,
            reliefBias = -1.2,
            terraceBands = 4,
            terraceStep = 1,
            terraceNoise = 0.6,
            wallRelief = 1.37,
            waterBudget = 0.26,
            riftBudget = 0.02,
            floraDensity = 0.165,
            canopyShare = 0.76,
            landmarkDensity = 0.8,
            wonderDensity = 1.05,
            wonderAffinity = List("sacred_grove", "boiling_springs", "drowned_caldera"),
            roadDensity = 1.5,
            themeRegions = (2, 3),
            weight = 1,
        },
        new MapSimulationArchetype
        {
            key = "crown_massif",
            name = "Crown Massif",
            summary = "One monumental massif crowns the map; everything else looks up at it",
            tier = 4,
            frames = List(MapFrameKind.Plateau, MapFrameKind.Open, MapFrameKind.Valley),
            openness = 0.36,
            frameDepth = 0.07,
            frameNoise = 0.45,
            reliefBias = 2.2,
            terraceBands = 6,
            terraceStep = 2,
            terraceNoise = 0.32,
            wallRelief = 2.74,
            waterBudget = 0.1,
            riftBudget = 0.11,
            floraDensity = 0.1,
            canopyShare = 0.48,
            landmarkDensity = 2.2,
            wonderDensity = 1.4,
            wonderAffinity = List("sky_observatory", "step_ziggurat", "cliff_switchback"),
            roadDensity = 1.6,
            themeRegions = (1, 2),
            weight = 0.85,
        },
        new MapSimulationArchetype
        {
            key = "ruined_metropolis",
            name = "Ruined Metropolis",
            summary = "A dead city on a broken grid; the ground remembers streets nobody walks",
            tier = 4,
            frames = List(MapFrameKind.Valley, MapFrameKind.Open, MapFrameKind.Shelf),
            openness = 0.46,
            frameDepth = 0.06,
            frameNoise = 0.5,
            reliefBias = 0.2,
            terraceBands = 3,
            terraceStep = 2,
            terraceNoise = 0.25,
            wallRelief = 2.13,
            waterBudget = 0.07,
            riftBudget = 0.09,
            floraDensity = 0.115,
            canopyShare = 0.3,
            landmarkDensity = 2.4,
            wonderDensity = 1.8,
            wonderAffinity = List("broken_quarter", "labyrinth_court", "watch_gate"),
            roadDensity = 3.2,
            themeRegions = (1, 3),
            weight = 0.95,
        },
        new MapSimulationArchetype
        {
            key = "shattered_atoll",
            name = "Shattered Atoll",
            summary = "A drowned caldera rim; everything worth standing on is a shard of the old wall",
            tier = 3,
            frames = List(MapFrameKind.Island, MapFrameKind.Shelf),
            openness = 0.34,
            frameDepth = 0.12,
            frameNoise = 0.9,
            reliefBias = -2.0,
            terraceBands = 4,
            terraceStep = 2,
            terraceNoise = 0.75,
            wallRelief = 1.67,
            waterBudget = 0.34,
            riftBudget = 0.08,
            floraDensity = 0.13,
            canopyShare = 0.5,
            landmarkDensity = 1.4,
            wonderDensity = 1.5,
            wonderAffinity = List("drowned_caldera", "great_causeway", "boiling_springs"),
            roadDensity = 1.0,
            themeRegions = (2, 3),
            weight = 0.8,
        },
        new MapSimulationArchetype
        {
            key = "ashen_steppe",
            name = "Ashen Steppe",
            summary = "Open country under a wide sky; the map is what stands on it, not what encloses it",
            tier = 3,
            frames = List(MapFrameKind.Open, MapFrameKind.Basin),
            openness = 0.56,
            frameDepth = 0.03,
            frameNoise = 0.8,
            reliefBias = -0.3,
            terraceBands = 2,
            terraceStep = 2,
            terraceNoise = 0.7,
            wallRelief = 1.52,
            waterBudget = 0.06,
            riftBudget = 0.07,
            floraDensity = 0.075,
            canopyShare = 0.24,
            landmarkDensity = 2.6,
            wonderDensity = 1.6,
            wonderAffinity = List("impact_crater", "lonely_spire", "sky_observatory"),
            roadDensity = 1.9,
            themeRegions = (1, 2),
            weight = 1.05,
        },
        new MapSimulationArchetype
        {
            key = "sky_terraces",
            name = "Sky Terraces",
            summary = "Stacked shelves stepping into thin air, stitched by switchbacks and spans",
            tier = 5,
            frames = List(MapFrameKind.Plateau, MapFrameKind.Shelf),
            openness = 0.34,
            frameDepth = 0.09,
            frameNoise = 0.55,
            reliefBias = 2.6,
            terraceBands = 7,
            terraceStep = 2,
            terraceNoise = 0.28,
            wallRelief = 2.43,
            waterBudget = 0.05,
            riftBudget = 0.2,
            floraDensity = 0.095,
            canopyShare = 0.36,
            landmarkDensity = 1.9,
            wonderDensity = 1.7,
            wonderAffinity = List("cliff_switchback", "sky_observatory", "great_causeway"),
            roadDensity = 1.8,
            themeRegions = (1, 2),
            weight = 0.85,
        },
    });
}
