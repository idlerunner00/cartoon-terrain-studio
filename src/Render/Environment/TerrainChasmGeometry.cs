// Port of packages/client/src/render/environment/terrainChasmGeometry.ts — keep in lockstep with the original.
//
// PORT NOTES:
// * `interface ChasmGeometryBuilder extends PropGeometryBuilder` is module-private in TS and only ever satisfied by
//   the compiler's `TileGeometryBuilder` (it adds `chasmDepthLedges`, `addOverlayShaded` and `addMistWisp`, all
//   TileGeometryBuilder members). Like `Pick<TileGeometryBuilder, …>` in terrainWaterfallCornerGeometry.ts, the C#
//   port therefore takes `TileGeometryBuilder` itself: no second nominal interface has to be kept in lockstep with
//   the builder's signatures (and `chasmDepthLedges` stays the builder's plain field).
// * `TerrainContourCorner` ('nw' | 'ne' | 'se' | 'sw') and `TerrainEdgeDirection` are strings.
// * The module-private `TerrainContourPointLike { x; z }` needs no type: `normalAt` takes the two coordinates.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainRenderPlanModule;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class ChasmMistPalette
{
    public int deep;
    public int mist;
}

public static partial class TerrainChasmGeometry
{
    private const double ELEV = TerrainProjection.TERRAIN_ELEVATION_STEP_PX;
    private static readonly double[] UNIT_ZERO = { 0, 0, 0, 0 };
    // Mutable module scratch → one instance per compiler thread (see RENDER_AGENT_BRIEF "Thread safety").
    [ThreadStatic] private static double[]? _SHADE4;
    private static double[] SHADE4 => _SHADE4 ??= new double[] { 1, 1, 1, 1 };
    [ThreadStatic] private static P3[]? _QUAD;
    private static P3[] QUAD => _QUAD ??= createPoints(4);
    [ThreadStatic] private static P3[]? _SHELF_QUAD;
    private static P3[] SHELF_QUAD => _SHELF_QUAD ??= createPoints(4);

    private static P3[] createPoints(int length)
    {
        var points = new P3[length];
        for (int index = 0; index < length; index++) points[index] = new P3 { x = 0, y = 0, z = 0 };
        return points;
    }

    /// <summary>`plan.materials[id]` — undefined (here null) outside the array.</summary>
    private static TerrainMaterial? planMaterialAt(TerrainRenderPlan plan, int id)
    {
        IReadOnlyList<TerrainMaterial> materials = plan.materials;
        return (uint)id < (uint)materials.Count ? materials[id] : null;
    }

    private static void setShade4(double a, double b, double c, double d)
    {
        double[] shade = SHADE4;
        shade[0] = a;
        shade[1] = b;
        shade[2] = c;
        shade[3] = d;
    }

    // TS `quadInto(out, ...coordinates: QuadCoordinates)`: twelve explicit coordinates keep the call allocation-free.
    private static P3[] quadInto(
        P3[] @out,
        double c0,
        double c1,
        double c2,
        double c3,
        double c4,
        double c5,
        double c6,
        double c7,
        double c8,
        double c9,
        double c10,
        double c11)
    {
        @out[0].x = c0;
        @out[0].y = c1;
        @out[0].z = c2;
        @out[1].x = c3;
        @out[1].y = c4;
        @out[1].z = c5;
        @out[2].x = c6;
        @out[2].y = c7;
        @out[2].z = c8;
        @out[3].x = c9;
        @out[3].y = c10;
        @out[3].z = c11;
        return @out;
    }

    private static P3[] quad(
        double c0,
        double c1,
        double c2,
        double c3,
        double c4,
        double c5,
        double c6,
        double c7,
        double c8,
        double c9,
        double c10,
        double c11) =>
        quadInto(QUAD, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11);

    /// <param name="material">TerrainEdgeMaterial.</param>
    private static double chasmWallPatternStrength(string material)
    {
        return material == "rock" ? 0.18 : 0.17;
    }

    /// <summary>Continue the nearest real dry material through Water and Bridge openings into the complete shaft.</summary>
    public static TerrainMaterial resolveChasmFaceMaterial(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        string direction)
    {
        if (cell.type == TileType.Solid || cell.type == TileType.Floor)
        {
            return (
                planMaterialAt(plan, cell.id) ??
                (cell.type == TileType.Solid
                    ? STANDARD_TERRAIN_MATERIALS.wallChalk
                    : STANDARD_TERRAIN_MATERIALS.floorCool));
        }
        TerrainMaterial? dryFallback = null;
        int preferredDx = direction == "e" ? -1 : direction == "w" ? 1 : 0;
        int preferredDy = direction == "s" ? -1 : direction == "n" ? 1 : 0;
        TerrainCell? preferred = terrainCellAt(terrain, cell.x + preferredDx, cell.y + preferredDy);
        if (preferred?.type == TileType.Solid)
            return planMaterialAt(plan, preferred.id) ?? STANDARD_TERRAIN_MATERIALS.wallChalk;
        if (preferred?.type == TileType.Floor)
            dryFallback ??= planMaterialAt(plan, preferred.id) ?? STANDARD_TERRAIN_MATERIALS.floorCool;
        for (int radius = 1; radius <= 5; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.max(Math.abs(dx), Math.abs(dy)) != radius) continue;
                    TerrainCell? neighbor = terrainCellAt(terrain, cell.x + dx, cell.y + dy);
                    if (neighbor?.type == TileType.Solid)
                        return planMaterialAt(plan, neighbor.id) ?? STANDARD_TERRAIN_MATERIALS.wallChalk;
                    if (neighbor?.type == TileType.Floor)
                        dryFallback ??= planMaterialAt(plan, neighbor.id) ?? STANDARD_TERRAIN_MATERIALS.floorCool;
                }
            }
        }
        return dryFallback ?? planMaterialAt(plan, cell.id) ?? STANDARD_TERRAIN_MATERIALS.wallChalk;
    }

    /// <summary>Resolve the Chasm material hidden below a deck without ever inheriting the Bridge's timber material.</summary>
    public static TerrainMaterial resolveChasmFloorMaterial(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell)
    {
        if (cell.type == TileType.Chasm)
            return planMaterialAt(plan, cell.id) ?? STANDARD_TERRAIN_MATERIALS.chasm;
        for (int radius = 1; radius <= 5; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.max(Math.abs(dx), Math.abs(dy)) != radius) continue;
                    TerrainCell? neighbor = terrainCellAt(terrain, cell.x + dx, cell.y + dy);
                    if (neighbor?.type == TileType.Chasm)
                        return planMaterialAt(plan, neighbor.id) ?? STANDARD_TERRAIN_MATERIALS.chasm;
                }
            }
        }
        return STANDARD_TERRAIN_MATERIALS.chasm;
    }

    /// <summary>Preserve the ordinary wall role above a Chasm continuation; depth comes from light, not a palette jump.</summary>
    /// <returns>TerrainEdgeMaterial.</returns>
    public static string chasmContinuationMaterial(TerrainCell cell, TerrainEdge edge)
    {
        foreach (TerrainFaceSegment segment in edge.faceSegments)
        {
            if (segment.role == "chasm" || segment.material == "abyss" || segment.material == "none")
                continue;
            return segment.material;
        }
        if (cell.type == TileType.Solid) return "rock";
        if (cell.type == TileType.Bridge) return "deck";
        return "earth";
    }

    public static void addChasmFluidBoundaryClosure(
        TileGeometryBuilder builder,
        string direction,
        double x0,
        double x1,
        double z0,
        double z1,
        double bottomY,
        double topY,
        TerrainMaterial material)
    {
        if (topY <= bottomY + 0.02) return;
        P3[] face =
            direction == "n"
                ? quad(x0, topY, z0, x1, topY, z0, x1, bottomY, z0, x0, bottomY, z0)
                : direction == "s"
                    ? quad(x1, topY, z1, x0, topY, z1, x0, bottomY, z1, x1, bottomY, z1)
                    : direction == "e"
                        ? quad(x1, topY, z0, x1, topY, z1, x1, bottomY, z1, x1, bottomY, z0)
                        : quad(x0, topY, z1, x0, topY, z0, x0, bottomY, z0, x0, bottomY, z1);
        setShade4(1, 1, 1, 1);
        builder.addSurface(
            face,
            direction == "e" ? -1 : direction == "w" ? 1 : 0,
            0.08,
            direction == "n" ? 1 : direction == "s" ? -1 : 0,
            terrainFaceBaseColor(material, "earth"),
            TERRAIN_SURFACE_PATTERN.chasmWall,
            chasmWallPatternStrength("earth"),
            SHADE4,
            UNIT_ZERO,
            false,
            false,
            direction == "s");
    }

    public static void addChasmDepthLedge(
        TileGeometryBuilder builder,
        string direction,
        double x0,
        double x1,
        double z0,
        double z1,
        double yA,
        double yB,
        double widthA,
        double widthB,
        double layer,
        int topColor,
        int sideColor)
    {
        P3[] shelfQuad = SHELF_QUAD;
        double fasciaDepth = ELEV * (0.75 + layer * 0.5);
        P3[] shelf;
        P3[] fascia;
        double nx = 0;
        double nz = 0;
        bool backside = false;
        if (direction == "n")
        {
            shelf = quadInto(shelfQuad, x0, yA, z0, x1, yB, z0, x1, yB, z0 + widthB, x0, yA, z0 + widthA);
            fascia = quad(
                x0,
                yA,
                z0 + widthA,
                x1,
                yB,
                z0 + widthB,
                x1,
                yB - fasciaDepth,
                z0 + widthB,
                x0,
                yA - fasciaDepth,
                z0 + widthA);
            nz = 1;
        }
        else if (direction == "s")
        {
            shelf = quadInto(shelfQuad, x0, yA, z1 - widthA, x1, yB, z1 - widthB, x1, yB, z1, x0, yA, z1);
            fascia = quad(
                x1,
                yB,
                z1 - widthB,
                x0,
                yA,
                z1 - widthA,
                x0,
                yA - fasciaDepth,
                z1 - widthA,
                x1,
                yB - fasciaDepth,
                z1 - widthB);
            nz = -1;
            backside = true;
        }
        else if (direction == "e")
        {
            shelf = quadInto(shelfQuad, x1 - widthA, yA, z0, x1, yA, z0, x1, yB, z1, x1 - widthB, yB, z1);
            fascia = quad(
                x1 - widthA,
                yA,
                z0,
                x1 - widthB,
                yB,
                z1,
                x1,
                yB - fasciaDepth,
                z1,
                x1,
                yA - fasciaDepth,
                z0);
            nx = -1;
        }
        else
        {
            shelf = quadInto(shelfQuad, x0, yA, z0, x0 + widthA, yA, z0, x0 + widthB, yB, z1, x0, yB, z1);
            fascia = quad(
                x0 + widthB,
                yB,
                z1,
                x0 + widthA,
                yA,
                z0,
                x0,
                yA - fasciaDepth,
                z0,
                x0,
                yB - fasciaDepth,
                z1);
            nx = 1;
        }
        double benchShade = layer == 0 ? 0.6 : layer == 1 ? 0.44 : 0.32;
        setShade4(benchShade, benchShade, benchShade, benchShade);
        builder.addSurface(shelf, 0, 1, 0, topColor, TERRAIN_SURFACE_PATTERN.rockCap, 0.14, SHADE4);
        setShade4(0.94 * benchShade, 0.94 * benchShade, 0.58 * benchShade, 0.58 * benchShade);
        builder.addSurface(
            fascia,
            nx,
            0.08,
            nz,
            sideColor,
            TERRAIN_SURFACE_PATTERN.chasmWall,
            chasmWallPatternStrength("rock"),
            SHADE4,
            UNIT_ZERO,
            false,
            false,
            backside);
        builder.chasmDepthLedges++;
    }

    public static void addChasmMist(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainMaterial material,
        TerrainChasmMistEffect effect,
        ChasmMistPalette? palette = null)
    {
        double ts = frame.tileSize;
        int wx = frame.i0 + cell.x;
        int wy = frame.j0 + cell.y;
        double cx = frame.originX + wx * ts + effect.ox * ts;
        double cz = frame.originY + wy * ts + effect.oy * ts;
        double layerT = clamp(
            (effect.height - TERRAIN_CHASM_MIST_MIN_HEIGHT) /
            (TERRAIN_CHASM_MIST_MAX_HEIGHT - TERRAIN_CHASM_MIST_MIN_HEIGHT),
            0,
            1);
        double farY = -ELEV * (8.15 - layerT * 0.72);
        double middleY = -ELEV * (5.45 - layerT * 0.62);
        double nearY = -ELEV * (2.95 - layerT * 0.48);
        double rx = effect.radiusX * ts;
        double ry = effect.radiusY * ts;
        double drift = effect.drift * ts;
        var deep = palette != null ? palette.deep : material.edgeDark;
        var tint = palette != null ? palette.mist : material.topLight;
        double seed = cellHash((double)wx * 1061 + 431, (double)wy * 1063 + 433);
        builder.addMistWisp(
            cx - rx * 0.04,
            farY,
            cz + ts * 0.13,
            rx * 1.35,
            ry * 2.2,
            mix(deep, tint, 0.42),
            effect.alpha * 0.22,
            effect.phase,
            drift * 0.62,
            seed,
            0,
            0.24);
        builder.addMistWisp(
            cx - rx * 0.02,
            middleY + ry * 0.1,
            cz,
            rx * 1.18,
            ry * 2.75,
            mix(deep, tint, 0.58),
            effect.alpha * 0.32,
            effect.phase + 0.29,
            drift * 0.96,
            seed + 0.43,
            1,
            0.46);
        builder.addMistWisp(
            cx + rx * 0.12,
            nearY + ry * 0.16,
            cz - ts * 0.085,
            rx * 0.72,
            ry * 3.25,
            mix(deep, tint, 0.68),
            effect.alpha * 0.25,
            effect.phase + 0.68,
            drift * 1.24,
            seed + 0.79,
            2,
            0.72);
    }
}
