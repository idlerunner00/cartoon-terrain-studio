// Port of packages/shared/src/domain/dungeon/mapSetPieces.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;

namespace Fluitown.Domain;

/*
 * **Wonders** — the monumental set-piece registry behind Simulate Map.
 *
 * A composed world reads as generated the moment every square metre of it was made by the same statistical
 * process. What makes the Hub and the authored Tutorial map read as *places* is that they contain a handful of
 * things that are unmistakably **built on purpose**: a sunken arena, a causeway, a ring of standing stones, a
 * stepped ziggurat. Those are wonders, and this file is the catalog.
 *
 * The split mirrors the soul/skill doctrine: a wonder's **form** is one implementation in the composer (a
 * `MapWonderForm` variant, stamped by exactly one routine, reused by every entry that wants it); a wonder's
 * **identity** is data here. Adding "Sunken Colosseum" must never add a branch to the composer — it is a new
 * entry that picks a form, a size band, a site appetite and a dressing recipe.
 *
 * Every field is expressed relative to the map so the same wonder composes at 64x48 and at 320x240.
 */

/// <summary>
/// The physical grammar a wonder is built from. One variant = one stamping routine in the composer, never two.
///
/// The forms are deliberately chosen so that together they span the whole vocabulary a top-down world can
/// read: raised mass, sunken mass, ring, grid, line, spiral, field, and void.
/// </summary>
public static class MapWonderForm
{
    /// <summary>A ring rampart around a sunken bowl. The bowl floor may hold water, rift or plain ground.</summary>
    public const string Crater = "crater";
    /// <summary>Concentric terraces descending to a level floor — a bowl you can walk down into.</summary>
    public const string Amphitheatre = "amphitheatre";
    /// <summary>A square stepped pyramid climbing to a summit plaza.</summary>
    public const string Ziggurat = "ziggurat";
    /// <summary>A raised, dead-straight causeway driven across whatever it meets.</summary>
    public const string Causeway = "causeway";
    /// <summary>A rift bowl with a spiral ledge running down its wall.</summary>
    public const string Sinkhole = "sinkhole";
    /// <summary>A field of broken wall stubs on a grid — a city that is not there any more.</summary>
    public const string RuinField = "ruin_field";
    /// <summary>A walled court carrying a small, genuinely solvable maze.</summary>
    public const string LabyrinthCourt = "labyrinth_court";
    /// <summary>A stepped rock pit cut into the ground, with spoil heaps around its lip.</summary>
    public const string Quarry = "quarry";
    /// <summary>One monumental rock tower on a cleared apron.</summary>
    public const string Spire = "spire";
    /// <summary>A ring of small hot-water pockets.</summary>
    public const string Springs = "springs";
    /// <summary>A dense canopy with a hard edge and a clearing at its heart.</summary>
    public const string SacredGrove = "sacred_grove";
    /// <summary>Two gate towers facing each other across an open threshold.</summary>
    public const string Gatehouse = "gatehouse";
    /// <summary>A shallow terraced staircase climbing a slope in switchbacks.</summary>
    public const string Switchback = "switchback";
    /// <summary>Concentric rings of low walls cut into a summit — a reading of the sky, in ground.</summary>
    public const string Observatory = "observatory";
}

/// <summary>
/// What kind of ground a wonder wants under it. The composer scores every candidate site against this, so a
/// causeway looks for something to cross and a grove looks for room to grow — nothing is placed by accident.
/// </summary>
public static class MapWonderSite
{
    /// <summary>Wide, level, open ground.</summary>
    public const string Plain = "plain";
    /// <summary>The highest ground the composition offers.</summary>
    public const string High = "high";
    /// <summary>The lowest ground the composition offers.</summary>
    public const string Low = "low";
    /// <summary>Ground next to standing water.</summary>
    public const string Shore = "shore";
    /// <summary>Ground next to a rift.</summary>
    public const string RiftEdge = "rift_edge";
    /// <summary>A gap in the walkable world worth spanning (water or rift between two banks).</summary>
    public const string Span = "span";
    /// <summary>Ground pressed against rock — a wall foot.</summary>
    public const string Wallfoot = "wallfoot";
}

/// <summary>What fills the middle of a hollow form (crater bowl, sinkhole floor, quarry pit).</summary>
public static class MapWonderCore
{
    public const string Ground = "ground";
    public const string Water = "water";
    public const string Rift = "rift";
}

public sealed class MapWonderDressing
{
    /// <summary>Decoration ringing the wonder's rim, in reveal order.</summary>
    public string? rim;
    /// <summary>Decoration marking the wonder's centre.</summary>
    public string? core;
    /// <summary>Scattered fill inside the wonder's footprint.</summary>
    public string? fill;
    /// <summary>Records placed on the rim ring, as a fraction of its circumference (0..1).</summary>
    public double? rimShare;
    /// <summary>Records scattered inside, as a fraction of the interior cells (0..1).</summary>
    public double? fillShare;
}

public sealed class MapWonderDef
{
    public string key = "";
    /// <summary>Title the cinematic announces when this wonder is raised.</summary>
    public string name = "";
    public string form = "";
    /// <summary>Where it wants to stand, best first. The composer walks the list until a site scores.</summary>
    public IReadOnlyList<string> sites = Array.Empty<string>();
    public string? core;
    /// <summary>
    /// Footprint radius as a fraction of the map's shorter axis, [min, max]. A wonder must be big enough to
    /// dominate its district and small enough to leave the map room to be a map.
    /// </summary>
    public (double, double) radius;
    /// <summary>How many terrace/wall steps the form builds, where the form has steps.</summary>
    public int? steps;
    /// <summary>0..1 — how irregular the form's edges are. 0 is drawn with a compass, 1 is half-ruined.</summary>
    public double ruin;
    public MapWonderDressing dressing = new();
    /// <summary>
    /// Roll weight. A wonder that can only stand in one kind of place carries a high weight so it still shows
    /// up when that place exists.
    /// </summary>
    public double weight;
    /// <summary>
    /// Lowest chaos (0..1) at which this wonder becomes reachable. The calm end of the dial keeps the world
    /// legible; the wild end is where the map starts growing things you did not expect.
    /// </summary>
    public double? minChaos;
}

public static partial class MapSetPieces
{
    /// <summary>
    /// The catalog. Ordering is stable and part of the deterministic roll: appending an entry never re-rolls an
    /// existing seed into a different world, it only makes new worlds reachable.
    /// </summary>
    public static readonly IReadOnlyList<MapWonderDef> MAP_WONDERS = new MapWonderDef[]
    {
        new()
        {
            key = "sunken_arena",
            name = "Sunken Arena",
            form = MapWonderForm.Amphitheatre,
            sites = new[] { MapWonderSite.Plain, MapWonderSite.Low },
            radius = (0.09, 0.15),
            steps = 3,
            ruin = 0.25,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.1,
            },
            weight = 1.1,
        },
        new()
        {
            key = "impact_crater",
            name = "Impact Crater",
            form = MapWonderForm.Crater,
            sites = new[] { MapWonderSite.Plain, MapWonderSite.High },
            core = MapWonderCore.Ground,
            radius = (0.08, 0.14),
            ruin = 0.55,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Stump,
                fill = TerrainDecorationKind.Thicket,
                rimShare = 0.14,
                fillShare = 0.06,
            },
            weight = 1,
        },
        new()
        {
            key = "drowned_caldera",
            name = "Drowned Caldera",
            form = MapWonderForm.Crater,
            sites = new[] { MapWonderSite.Low, MapWonderSite.Plain },
            core = MapWonderCore.Water,
            radius = (0.09, 0.15),
            ruin = 0.4,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.16,
            },
            weight = 0.9,
        },
        new()
        {
            key = "step_ziggurat",
            name = "Step Ziggurat",
            form = MapWonderForm.Ziggurat,
            sites = new[] { MapWonderSite.Plain, MapWonderSite.High },
            radius = (0.07, 0.12),
            steps = 4,
            ruin = 0.18,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.12,
            },
            weight = 1,
        },
        new()
        {
            key = "great_causeway",
            name = "Great Causeway",
            form = MapWonderForm.Causeway,
            sites = new[] { MapWonderSite.Span },
            radius = (0.1, 0.2),
            ruin = 0.3,
            dressing = new MapWonderDressing { rim = TerrainDecorationKind.Thicket, rimShare = 0.26 },
            weight = 1.5,
        },
        new()
        {
            key = "spiral_sinkhole",
            name = "Spiral Sinkhole",
            form = MapWonderForm.Sinkhole,
            sites = new[] { MapWonderSite.RiftEdge, MapWonderSite.Plain },
            core = MapWonderCore.Rift,
            radius = (0.08, 0.13),
            steps = 3,
            ruin = 0.45,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.18,
            },
            weight = 1.15,
        },
        new()
        {
            key = "broken_quarter",
            name = "Broken Quarter",
            form = MapWonderForm.RuinField,
            sites = new[] { MapWonderSite.Plain, MapWonderSite.Wallfoot },
            radius = (0.1, 0.17),
            ruin = 0.8,
            dressing = new MapWonderDressing
            {
                fill = TerrainDecorationKind.Stump,
                fillShare = 0.13,
            },
            weight = 1.2,
        },
        new()
        {
            key = "labyrinth_court",
            name = "Labyrinth Court",
            form = MapWonderForm.LabyrinthCourt,
            sites = new[] { MapWonderSite.Plain, MapWonderSite.Wallfoot },
            radius = (0.09, 0.14),
            ruin = 0.35,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.12,
            },
            weight = 0.95,
            minChaos = 0.25,
        },
        new()
        {
            key = "terraced_quarry",
            name = "Terraced Quarry",
            form = MapWonderForm.Quarry,
            sites = new[] { MapWonderSite.Wallfoot, MapWonderSite.High },
            radius = (0.075, 0.13),
            steps = 3,
            ruin = 0.55,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Stump,
                fill = TerrainDecorationKind.Thicket,
                rimShare = 0.2,
                fillShare = 0.08,
            },
            weight = 1,
        },
        new()
        {
            key = "lonely_spire",
            name = "Lonely Spire",
            form = MapWonderForm.Spire,
            sites = new[] { MapWonderSite.High, MapWonderSite.Plain },
            radius = (0.05, 0.085),
            ruin = 0.4,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.22,
            },
            weight = 1.05,
        },
        new()
        {
            key = "boiling_springs",
            name = "Boiling Springs",
            form = MapWonderForm.Springs,
            sites = new[] { MapWonderSite.Low, MapWonderSite.Shore, MapWonderSite.Plain },
            radius = (0.07, 0.12),
            ruin = 0.6,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Stump,
                fill = TerrainDecorationKind.Thicket,
                rimShare = 0.24,
                fillShare = 0.07,
            },
            weight = 0.9,
            minChaos = 0.2,
        },
        new()
        {
            key = "sacred_grove",
            name = "Sacred Grove",
            form = MapWonderForm.SacredGrove,
            sites = new[] { MapWonderSite.Plain, MapWonderSite.Shore },
            radius = (0.08, 0.14),
            ruin = 0.35,
            dressing = new MapWonderDressing
            {
                core = TerrainDecorationKind.Tree,
                fill = TerrainDecorationKind.Tree,
                fillShare = 0.44,
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.3,
            },
            weight = 1.3,
        },
        new()
        {
            key = "watch_gate",
            name = "Watch Gate",
            form = MapWonderForm.Gatehouse,
            sites = new[] { MapWonderSite.Wallfoot, MapWonderSite.Span, MapWonderSite.Plain },
            radius = (0.05, 0.085),
            ruin = 0.25,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Thicket,
                rimShare = 0.3,
            },
            weight = 1.1,
        },
        new()
        {
            key = "cliff_switchback",
            name = "Cliff Switchback",
            form = MapWonderForm.Switchback,
            sites = new[] { MapWonderSite.Wallfoot, MapWonderSite.High },
            radius = (0.08, 0.14),
            steps = 4,
            ruin = 0.3,
            dressing = new MapWonderDressing { rim = TerrainDecorationKind.Thicket, rimShare = 0.18 },
            weight = 1,
        },
        new()
        {
            key = "sky_observatory",
            name = "Sky Observatory",
            form = MapWonderForm.Observatory,
            sites = new[] { MapWonderSite.High },
            radius = (0.07, 0.115),
            steps = 2,
            ruin = 0.2,
            dressing = new MapWonderDressing
            {
                rim = TerrainDecorationKind.Stump,
                rimShare = 0.26,
            },
            weight = 1.05,
            minChaos = 0.15,
        },
    };

    /// <summary>The wonders a given chaos setting unlocks — the calm end of the dial keeps the exotic entries out.</summary>
    public static IReadOnlyList<MapWonderDef> mapWondersForChaos(double chaos)
    {
        return MAP_WONDERS.filter((wonder) => (wonder.minChaos ?? 0) <= chaos);
    }
}
