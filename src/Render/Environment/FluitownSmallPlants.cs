// Fluitown extension — NOT a port of the original. The comic look's water plants. Without FluitownVegetation.Enabled
// (the Style drawer's "3D vegetation" switch) the ported lily discs and reed blades stay.
using System;
using Fluitown.Domain;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;
using PropTileset = Fluitown.Render.TerrainMaterialTileset;

namespace Fluitown.Render;

/// <summary>
/// Water lilies, reeds and water grass of the comic look. The original bakes a lily colony as flat notched discs and a
/// reed bed as a few ribbon blades in the terrain's surface lane: under the comic ramp they read as pink blobs and black
/// matchsticks, and nothing about them moves the way a floating leaf or a reed does. The render plan still decides WHERE
/// they grow (<see cref="TerrainLilyPadEffect"/> on calm deep water, <see cref="TerrainReedBedEffect"/> along shores); the
/// bake writes one vegetation-lane record per pad, reed clump and grass tuft (<see cref="FluitownVegetation"/> layout),
/// and the vegetation layer grows them as instanced, animated plants (godot/src/Godot/Rendering/Vegetation/InstancedPlants.cs):
/// pads drift and bob on the water and part around the Flui, reeds sway with the terrain's wind.
/// </summary>
public static class FluitownSmallPlants
{
    /// <summary>One floating lily pad (Bloom &gt; 0: it carries a flower).</summary>
    public const int KindLily = 4;
    /// <summary>A clump of reeds and cattails rooted in shallow water at a bank.</summary>
    public const int KindReed = 5;
    /// <summary>A tuft of arching water grass (sedge) at the waterline.</summary>
    public const int KindSedge = 6;
    /// <summary>Ivy or aerial roots hanging down a cliff face from under its crest (Form 1: roots).</summary>
    public const int KindVine = 7;

    private const double ELEV = TerrainProjection.TERRAIN_ELEVATION_STEP_PX;
    private const double PxPerMetre = FluitownVegetation.PxPerMetre;

    private static void Push(TileGeometryBuilder builder, ReadOnlySpan<double> record)
    {
        FloatBuf lane = builder.vegetation;
        lane.ensure(FluitownVegetation.Stride);
        for (int i = 0; i < FluitownVegetation.Stride; i++) lane.push(i < record.Length ? record[i] : 0);
    }

    /// <summary>Themed worlds (glass, deep sea, candy, clockwork, neon, starforged) keep their authored plant pigment;
    /// natural worlds get living greens.</summary>
    private static bool Themed(string? biomeKey) =>
        biomeKey != null &&
        (biomeKey.Contains("prism") || biomeKey.Contains("abyss") || biomeKey.Contains("carnival") ||
            biomeKey.Contains("sugar") || biomeKey.Contains("clockwork") || biomeKey.Contains("neon") ||
            biomeKey.Contains("starforged") || biomeKey.Contains("drone"));

    private static bool OpenWater(TerrainCell cell, string direction)
    {
        var edge = cell.edges[direction];
        return edge != null && (edge.contactType == TileType.Water || edge.contactType == TileType.Bridge);
    }

    /// <summary>The cell-local range a plant may stand in: a margin from every dry side.</summary>
    private static (double u0, double u1, double v0, double v1) WetRange(TerrainCell cell, double margin) => (
        OpenWater(cell, "w") ? -0.3 : margin,
        OpenWater(cell, "e") ? 1.3 : 1 - margin,
        OpenWater(cell, "n") ? -0.3 : margin,
        OpenWater(cell, "s") ? 1.3 : 1 - margin);

    // ── Lilies ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a lily colony (the comic look's TerrainGeometryCompiler.addNaturalLilyColony): three to seven pads of
    /// 0.13–0.36 m radius, a large one near the anchor and smaller ones around it on the golden angle, kept a pad's width
    /// off every dry side; a blossom rides the largest pad of a flowering colony, a second one sometimes a smaller pad.
    /// </summary>
    public static bool recordLilyColony(
        PropGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainLilyPadEffect lily,
        PropTileset tileset,
        int lushPole,
        string? biomeKey)
    {
        if (!FluitownVegetation.Enabled || builder is not TileGeometryBuilder tile) return false;
        double ts = frame.tileSize;
        double y = (cell.waterLevel ?? cell.surfaceZ) * ELEV + 0.45;
        bool themed = Themed(biomeKey);
        int pad = themed
            ? FluitownSoftFoliage.saturate(mix(tileset.decal.mid, tileset.decal.ink, 0.12), 1.15)
            : mix(mix(0x5c8f3c, lushPole, 0.1), 0x6fa046, lily.bloomHue * 0.35);
        int padDark = themed ? mix(pad, tileset.decal.ink, 0.3) : mix(pad, 0x2f5a2c, 0.42);
        int bloom = themed
            ? mix(tileset.decal.accent, 0xfff6ee, 0.25)
            : biomeKey != null && biomeKey.Contains("sakura")
                ? mix(0xf3a9c0, 0xfff1f4, lily.bloomHue * 0.5)
                : mix(mix(tileset.decal.accent, 0xfff4ec, 0.62), 0xfffaf2, lily.bloomHue * 0.4);
        var range = WetRange(cell, 0.16);
        int pads = 3 + (int)Math.floor(propHash(lily.id * 3.17 + 5) * (lily.cluster >= 2 ? 5 : 3));
        double spread = (0.34 + lily.radius / PxPerMetre * 1.6) * PxPerMetre;
        Span<double> r = stackalloc double[FluitownVegetation.Stride];
        int flowers = 0;
        for (int i = 0; i < pads; i++)
        {
            double h = propHash(lily.id * 2267 + i * 61);
            double k = propHash(lily.id * 2273 + i * 67);
            // The anchor pad is the largest; the others shrink outwards (young leaves at the colony's rim).
            double radiusM = i == 0
                ? 0.26 + lily.radius / PxPerMetre * 0.3 + h * 0.06
                : 0.13 + h * 0.14 + (1 - (double)i / pads) * 0.05;
            double angle = lily.notchAngle + i * 2.39996 + (k - 0.5) * 0.7;
            double distance = i == 0 ? 0 : spread * Math.sqrt((i + k * 0.6) / pads);
            double u = clamp(lily.ox + Math.cos(angle) * distance / ts, range.u0, range.u1);
            double v = clamp(lily.oy + Math.sin(angle) * distance / ts, range.v0, range.v1);
            bool flower = lily.blossom && (i == 0 || (flowers < 2 && h > 0.72));
            if (flower) flowers++;
            r.Clear();
            r[FluitownVegetation.Kind] = KindLily;
            r[FluitownVegetation.X] = frame.originX + (frame.i0 + cell.x + u) * ts;
            // Stacked a few millimetres apart (the anchor pad lowest): overlapping pads never z-fight while they drift.
            r[FluitownVegetation.Y] = y + i * 0.09;
            r[FluitownVegetation.Z] = frame.originY + (frame.j0 + cell.y + v) * ts;
            r[FluitownVegetation.Height] = 0;
            r[FluitownVegetation.Radius] = radiusM * PxPerMetre;
            r[FluitownVegetation.Seed] = k;
            r[FluitownVegetation.Rotation] = angle + h * 2.1;
            r[FluitownVegetation.ColourLeaf] = pad;
            r[FluitownVegetation.ColourShade] = padDark;
            r[FluitownVegetation.ColourAccent] = bloom;
            r[FluitownVegetation.Bloom] = flower ? 0.55 + h * 0.45 : 0;
            r[FluitownVegetation.Form] = lily.phase;
            Push(tile, r);
        }
        return true;
    }

    // ── Reeds and water grass ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a reed bed (the comic look's TerrainGeometryCompiler.addNaturalReedBed): its root crowns pulled towards the
    /// bank it grows along, 0.6–1.25 m tall (the Flui wades among them; the original's blades were 0.24–0.56 m), a share
    /// with cattail heads; and a few tufts of arching water grass right at the waterline beside them.
    /// </summary>
    public static bool recordReedBed(
        PropGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainReedBedEffect reed,
        PropTileset tileset,
        int lushPole,
        string? biomeKey)
    {
        if (!FluitownVegetation.Enabled || builder is not TileGeometryBuilder tile) return false;
        double ts = frame.tileSize;
        double y = (cell.waterLevel ?? cell.surfaceZ) * ELEV;
        bool themed = Themed(biomeKey);
        int stem = themed ? mix(tileset.decal.mid, tileset.terrain.floorLit, 0.2) : mix(0x6f8f3b, lushPole, 0.1);
        int root = themed ? mix(stem, tileset.decal.ink, 0.3) : mix(stem, 0x3f5f2a, 0.5);
        int tip = themed ? mix(stem, tileset.terrain.floorLit, 0.35) : mix(stem, 0xc9b56c, 0.55);
        int head = themed ? mix(tileset.decal.accent, tileset.decal.ink, 0.3) : 0x6b4527;
        int grass = themed ? mix(tileset.decal.mid, tileset.terrain.floorLit, 0.3) : mix(0x5f8d40, lushPole, 0.12);
        int grassRoot = mix(grass, themed ? tileset.decal.ink : 0x3c6030, 0.45);
        int grassTip = mix(grass, themed ? tileset.terrain.floorLit : 0xb3bf6a, 0.4);

        // The bank: the sum of the dry sides' outward directions.
        double bx = (OpenWater(cell, "e") ? 0 : 1) - (OpenWater(cell, "w") ? 0 : 1);
        double bz = (OpenWater(cell, "s") ? 0 : 1) - (OpenWater(cell, "n") ? 0 : 1);
        double bank = Math.hypot(bx, bz);
        if (bank > 0) { bx /= bank; bz /= bank; }
        var range = WetRange(cell, 0.1);
        Span<double> r = stackalloc double[FluitownVegetation.Stride];
        int clumps = Math.max(1, Math.min(3, reed.clumps));
        double tall = clamp((reed.height - 6) / 8, 0, 1);
        for (int i = 0; i < clumps; i++)
        {
            double h = propHash(reed.id * 2237 + i * 53);
            double k = propHash(reed.id * 2243 + i * 59);
            // Along the bank (perpendicular to it), then towards it.
            double side = (i - (clumps - 1) * 0.5) * 0.3 + (h - 0.5) * 0.16;
            double u = reed.ox + bz * side + bx * (0.22 + k * 0.1);
            double v = reed.oy - bx * side + bz * (0.22 + k * 0.1);
            u = clamp(u, range.u0, range.u1);
            v = clamp(v, range.v0, range.v1);
            double metres = 0.62 + tall * 0.38 + h * 0.25;
            r.Clear();
            r[FluitownVegetation.Kind] = KindReed;
            r[FluitownVegetation.X] = frame.originX + (frame.i0 + cell.x + u) * ts;
            r[FluitownVegetation.Y] = y;
            r[FluitownVegetation.Z] = frame.originY + (frame.j0 + cell.y + v) * ts;
            r[FluitownVegetation.Height] = metres * PxPerMetre;
            r[FluitownVegetation.Radius] = (0.16 + k * 0.1 + reed.blades * 0.012) * PxPerMetre;
            r[FluitownVegetation.Seed] = k;
            r[FluitownVegetation.Rotation] = reed.phase + i * 2.1 + h;
            r[FluitownVegetation.ColourLeaf] = stem;
            r[FluitownVegetation.ColourShade] = root;
            r[FluitownVegetation.ColourAccent] = head;
            r[FluitownVegetation.Form] = tip;
            r[FluitownVegetation.Bloom] = reed.seedHeads;
            r[FluitownVegetation.Lean] = reed.sway;
            r[FluitownVegetation.Density] = reed.stiffness;
            Push(tile, r);
        }
        if (bank <= 0) return true;
        // Water grass along the waterline: tufts spread along the bank, their roots a hand's width out in the water.
        int tufts = 2 + (int)Math.floor(propHash(reed.id * 2251 + 7) * 4);
        for (int i = 0; i < tufts; i++)
        {
            double h = propHash(reed.id * 2261 + i * 71);
            double k = propHash(reed.id * 2269 + i * 73);
            double side = (h - 0.5) * 0.9;
            // The bank line of this cell: the dry side's edge, 0.12–0.2 of a cell into the water.
            double inset = 0.12 + k * 0.08;
            double u = bx != 0 ? (bx > 0 ? 1 - inset : inset) : reed.ox + bz * side;
            double v = bz != 0 ? (bz > 0 ? 1 - inset : inset) : reed.oy - bx * side;
            if (bx != 0 && bz != 0)
            {
                // A corner: alternate between the two banks.
                if (i % 2 == 0) v = clamp(reed.oy + side, 0.1, 0.9);
                else u = clamp(reed.ox + side, 0.1, 0.9);
            }
            else if (bx != 0) v = clamp(reed.oy + side, 0.08, 0.92);
            else u = clamp(reed.ox + side, 0.08, 0.92);
            double metres = 0.28 + h * 0.22;
            r.Clear();
            r[FluitownVegetation.Kind] = KindSedge;
            r[FluitownVegetation.X] = frame.originX + (frame.i0 + cell.x + u) * ts;
            r[FluitownVegetation.Y] = y;
            r[FluitownVegetation.Z] = frame.originY + (frame.j0 + cell.y + v) * ts;
            r[FluitownVegetation.Height] = metres * PxPerMetre;
            r[FluitownVegetation.Radius] = (0.16 + k * 0.1) * PxPerMetre;
            r[FluitownVegetation.Seed] = k;
            // Lean out over the water, away from the bank.
            r[FluitownVegetation.Rotation] = Math.atan2(-bz, -bx) + (h - 0.5) * 0.9;
            r[FluitownVegetation.ColourLeaf] = grass;
            r[FluitownVegetation.ColourShade] = grassRoot;
            r[FluitownVegetation.Form] = grassTip;
            r[FluitownVegetation.Lean] = reed.sway;
            Push(tile, r);
        }
        return true;
    }

    // ── Hanging vines ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the hanging growth of a floor cell's cliff (the comic look's TerrainGeometryCompiler.addNaturalWallStrand).
    /// The original hung two to five dark strips 0.14–0.4 m long, 0.4 m out in front of the wall — floating dark streaks
    /// under the comic ramp. Here one to three ivy vines (or aerial roots, the plan's "root" style) hang ON the wall face
    /// from just under its crest, a third to most of the way down; the organic form moves them with the wall (its
    /// horizontal field does not depend on height, so a vertical line on the wall stays on it).
    /// </summary>
    public static bool recordWallStrand(
        PropGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainWallStrandEffect strand,
        PropTileset tileset,
        int lushPole,
        string? biomeKey)
    {
        if (!FluitownVegetation.Enabled || builder is not TileGeometryBuilder tile) return false;
        TerrainEdge? edge = cell.edges[strand.direction];
        if (edge == null) return true;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double floor = cell.surfaceZ * ELEV;
        double crest = (edge.contactSurfaceZ ?? Math.max(edge.fromZ, edge.toZ)) * ELEV;
        double top = crest - 0.12 * ELEV;
        double wall = top - floor;
        // A step too low to hang anything from.
        if (wall < 0.45 * PxPerMetre) return true;
        bool roots = strand.style == "root";
        bool themed = Themed(biomeKey);
        int leaf = roots
            ? mix(tileset.bridge.bodyShadow, tileset.decal.ink, 0.12)
            : themed ? FluitownSoftFoliage.saturate(mix(tileset.decal.mid, tileset.decal.ink, 0.15), 1.1) : mix(0x5a9140, lushPole, 0.1);
        int stem = roots ? mix(tileset.bridge.bodyShadow, tileset.decal.ink, 0.3) : mix(leaf, 0x3b4a26, 0.5);
        int bloom = mix(tileset.decal.accent, 0xfff6ee, 0.45);
        // Outward: from the wall into this cell.
        double ox = strand.direction == "e" ? -1 : strand.direction == "w" ? 1 : 0;
        double oz = strand.direction == "s" ? -1 : strand.direction == "n" ? 1 : 0;
        double wallX = strand.direction == "e" ? x0 + ts : strand.direction == "w" ? x0 : 0;
        double wallZ = strand.direction == "s" ? z0 + ts : strand.direction == "n" ? z0 : 0;
        bool alongX = strand.direction == "n" || strand.direction == "s";
        double share = 0.3 + clamp((strand.length - 0.24) / 0.42, 0, 1) * 0.55;
        double length = Math.min(wall - 0.08 * PxPerMetre, Math.min(2.4 * PxPerMetre, wall * share));
        int vines = Math.max(1, Math.min(3, strand.strands - 1));
        Span<double> r = stackalloc double[FluitownVegetation.Stride];
        for (int i = 0; i < vines; i++)
        {
            double h = propHash(strand.id * 2293 + i * 67);
            double k = propHash(strand.id * 2297 + i * 71);
            double t = clamp(strand.t + (i - (vines - 1) * 0.5) * 0.14 + (h - 0.5) * 0.06, 0.2, 0.8);
            // A hand's width off the face, so the leaves lie on it rather than in it.
            double lift = 0.03 * PxPerMetre;
            double x = alongX ? x0 + t * ts : wallX + ox * lift;
            double z = alongX ? wallZ + oz * lift : z0 + t * ts;
            r.Clear();
            r[FluitownVegetation.Kind] = KindVine;
            r[FluitownVegetation.X] = x;
            r[FluitownVegetation.Y] = top + (k - 0.5) * 0.06 * ELEV;
            r[FluitownVegetation.Z] = z;
            r[FluitownVegetation.Height] = length * (0.62 + h * 0.38);
            r[FluitownVegetation.Seed] = k;
            r[FluitownVegetation.Rotation] = Math.atan2(ox, oz);
            r[FluitownVegetation.ColourLeaf] = leaf;
            r[FluitownVegetation.ColourShade] = stem;
            r[FluitownVegetation.ColourAccent] = bloom;
            r[FluitownVegetation.Form] = roots ? 1 : 0;
            r[FluitownVegetation.Lean] = strand.sway;
            Push(tile, r);
        }
        return true;
    }
}
