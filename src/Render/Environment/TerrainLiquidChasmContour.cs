// Port of packages/client/src/render/environment/terrainLiquidChasmContour.ts — keep in lockstep with the original.
//
// World-continuous liquid deformation at Water/Bridge -> Chasm contacts.
//
// The deformation deliberately keeps a regular grid topology.  In particular,
// it must not turn a whole water cell into a polygon fan: long fan triangles
// make the otherwise subtle water payload interpolation visible as geometric
// wedges.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class LiquidChasmContour
{
    public double nw;
    public double ne;
    public double se;
    public double sw;
    public bool chasmN;
    public bool chasmE;
    public bool chasmS;
    public bool chasmW;
    public double macroNwX;
    public double macroNwZ;
    public double macroNwOutwardX;
    public double macroNwOutwardZ;
    public double macroNeX;
    public double macroNeZ;
    public double macroNeOutwardX;
    public double macroNeOutwardZ;
    public double macroSeX;
    public double macroSeZ;
    public double macroSeOutwardX;
    public double macroSeOutwardZ;
    public double macroSwX;
    public double macroSwZ;
    public double macroSwOutwardX;
    public double macroSwOutwardZ;
    public bool hasMacroDeformation;
}

public sealed class LiquidChasmEdgePoint
{
    public double x;
    public double z;
}

public static partial class TerrainLiquidChasmContour
{
    /// <summary>Kept equal to the water transition grid so boundary and interior share vertices.</summary>
    public const int LIQUID_CHASM_EDGE_SEGMENTS = 7;

    private const double LIP_WANDER_MAX_PX = 4.2;
    private const double LIP_WANDER_SCALE_PX = 96;
    private const double LIP_WANDER_DETAIL_SCALE_PX = 34;
    private const double LIP_WANDER_DETAIL_WEIGHT = 0.24;
    private const double LIP_INTERIOR_FADE_TILES = 0.42;
    private const double LIP_ENDPOINT_FADE_TILES = 0.28;
    private const double MACRO_CORNER_REACH_TILES = 0.245;
    private const double MACRO_CORNER_VARIATION_TILES = 0.022;

    /// <summary>Allocate one reusable contour record; callers share the field definition instead of restating its topology.</summary>
    public static LiquidChasmContour createLiquidChasmContour()
    {
        return new LiquidChasmContour
        {
            nw = 0,
            ne = 0,
            se = 0,
            sw = 0,
            chasmN = false,
            chasmE = false,
            chasmS = false,
            chasmW = false,
            macroNwX = 0,
            macroNwZ = 0,
            macroNwOutwardX = 0,
            macroNwOutwardZ = 0,
            macroNeX = 0,
            macroNeZ = 0,
            macroNeOutwardX = 0,
            macroNeOutwardZ = 0,
            macroSeX = 0,
            macroSeZ = 0,
            macroSeOutwardX = 0,
            macroSeOutwardZ = 0,
            macroSwX = 0,
            macroSwZ = 0,
            macroSwOutwardX = 0,
            macroSwOutwardZ = 0,
            hasMacroDeformation = false,
        };
    }

    /// <summary>TS `interface MacroCornerControl extends LiquidChasmEdgePoint` (module-private).</summary>
    private sealed class MacroCornerControl
    {
        public double x;
        public double z;
        public double outwardX;
        public double outwardZ;
    }

    private static double directionDx(string direction) => direction == "e" ? 1 : direction == "w" ? -1 : 0;

    private static double directionDz(string direction) => direction == "s" ? 1 : direction == "n" ? -1 : 0;

    private static double quintic(double value)
    {
        double t = Math.max(0, Math.min(1, value));
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    private static bool opensIntoChasm(MaterializedTerrain terrain, TerrainCell cell, string direction)
    {
        TerrainCell? adjacent = terrainCellAt(
            terrain,
            cell.x + (int)directionDx(direction),
            cell.y + (int)directionDz(direction));
        return adjacent != null && terrainCellCarriesChasmFloor(adjacent);
    }

    private static bool carriesLiquid(TerrainCell? cell) =>
        cell != null &&
        (cell.type == TileType.Water || (cell.type == TileType.Bridge && cell.span == TileType.Water));

    /// <summary>
    /// Shared marching-squares style displacement for one logical grid corner.
    ///
    /// A one-quadrant Chasm notch and the following one-quadrant liquid tip form the two turns of the familiar
    /// 40 px staircase. Moving both turns by roughly one quarter tile makes the two orthogonal runs meet on one
    /// diagonal. The corner is classified from all four cells, so every surface and waterfall which owns it gets
    /// bit-identical coordinates. Mixed dry-bank junctions deliberately stay put; their contour is owned by the
    /// shore system instead.
    /// </summary>
    private static MacroCornerControl macroCornerShiftAt(
        MaterializedTerrain terrain,
        int localGridX,
        int localGridY,
        int worldGridX,
        int worldGridY,
        MacroCornerControl @out)
    {
        TerrainCell? nw = terrainCellAt(terrain, localGridX - 1, localGridY - 1);
        TerrainCell? ne = terrainCellAt(terrain, localGridX, localGridY - 1);
        TerrainCell? se = terrainCellAt(terrain, localGridX, localGridY);
        TerrainCell? sw = terrainCellAt(terrain, localGridX - 1, localGridY);

        bool nwChasm = nw != null && terrainCellCarriesChasmFloor(nw);
        bool neChasm = ne != null && terrainCellCarriesChasmFloor(ne);
        bool seChasm = se != null && terrainCellCarriesChasmFloor(se);
        bool swChasm = sw != null && terrainCellCarriesChasmFloor(sw);
        bool nwLiquid = carriesLiquid(nw);
        bool neLiquid = carriesLiquid(ne);
        bool seLiquid = carriesLiquid(se);
        bool swLiquid = carriesLiquid(sw);

        if (
            (!nwChasm && !nwLiquid) ||
            (!neChasm && !neLiquid) ||
            (!seChasm && !seLiquid) ||
            (!swChasm && !swLiquid))
        {
            @out.x = 0;
            @out.z = 0;
            @out.outwardX = 0;
            @out.outwardZ = 0;
            return @out;
        }

        int chasmCount = (nwChasm ? 1 : 0) + (neChasm ? 1 : 0) + (seChasm ? 1 : 0) + (swChasm ? 1 : 0);
        if (chasmCount != 1 && chasmCount != 3)
        {
            @out.x = 0;
            @out.z = 0;
            @out.outwardX = 0;
            @out.outwardZ = 0;
            return @out;
        }

        // Fill a one-Chasm notch; trim a one-liquid tip. Both operations converge on the same continuous contour.
        bool seekChasm = chasmCount == 1;
        bool targetNw = seekChasm ? nwChasm : nwLiquid;
        bool targetNe = seekChasm ? neChasm : neLiquid;
        bool targetSe = seekChasm ? seChasm : seLiquid;
        bool targetSw = seekChasm ? swChasm : swLiquid;
        double variation =
            (smoothCellNoise(worldGridX + 11.7, worldGridY - 8.3, 4.2, 2861) - 0.5) *
            MACRO_CORNER_VARIATION_TILES;
        double reach = MACRO_CORNER_REACH_TILES + variation;

        double targetX = targetNw || targetSw ? -1 : 1;
        double targetZ = targetNw || targetNe ? -1 : 1;
        @out.x = targetX * reach;
        @out.z = targetZ * reach;
        double outwardSign = seekChasm ? 1 : -1;
        @out.outwardX = targetX * outwardSign * Math.SQRT1_2;
        @out.outwardZ = targetZ * outwardSign * Math.SQRT1_2;
        return @out;
    }

    // Mutable module scratch → one instance per compiler thread (see RENDER_AGENT_BRIEF "Thread safety").
    [ThreadStatic] private static MacroCornerControl? _MACRO_CORNER_SHIFT_SCRATCH;
    private static MacroCornerControl MACRO_CORNER_SHIFT_SCRATCH => _MACRO_CORNER_SHIFT_SCRATCH ??= new MacroCornerControl
    {
        x = 0,
        z = 0,
        outwardX = 0,
        outwardZ = 0,
    };

    public static LiquidChasmContour liquidChasmContourForCell(
        MaterializedTerrain terrain,
        TerrainCell cell,
        int worldCellX,
        int worldCellY,
        bool _coherent,
        LiquidChasmContour @out)
    {
        MacroCornerControl scratch = MACRO_CORNER_SHIFT_SCRATCH;
        // Dry-bank corner fills are authored by the dedicated continuous liquid batch. Chasm contacts deliberately
        // do not reuse large corner cuts: their visible liquid uses the regular deformation field below, which
        // retains a seamless square topology without polygon fans.
        @out.nw = 0;
        @out.ne = 0;
        @out.se = 0;
        @out.sw = 0;
        @out.chasmN = opensIntoChasm(terrain, cell, "n");
        @out.chasmE = opensIntoChasm(terrain, cell, "e");
        @out.chasmS = opensIntoChasm(terrain, cell, "s");
        @out.chasmW = opensIntoChasm(terrain, cell, "w");

        macroCornerShiftAt(terrain, cell.x, cell.y, worldCellX, worldCellY, scratch);
        @out.macroNwX = scratch.x;
        @out.macroNwZ = scratch.z;
        @out.macroNwOutwardX = scratch.outwardX;
        @out.macroNwOutwardZ = scratch.outwardZ;
        macroCornerShiftAt(
            terrain,
            cell.x + 1,
            cell.y,
            worldCellX + 1,
            worldCellY,
            scratch);
        @out.macroNeX = scratch.x;
        @out.macroNeZ = scratch.z;
        @out.macroNeOutwardX = scratch.outwardX;
        @out.macroNeOutwardZ = scratch.outwardZ;
        macroCornerShiftAt(
            terrain,
            cell.x + 1,
            cell.y + 1,
            worldCellX + 1,
            worldCellY + 1,
            scratch);
        @out.macroSeX = scratch.x;
        @out.macroSeZ = scratch.z;
        @out.macroSeOutwardX = scratch.outwardX;
        @out.macroSeOutwardZ = scratch.outwardZ;
        macroCornerShiftAt(
            terrain,
            cell.x,
            cell.y + 1,
            worldCellX,
            worldCellY + 1,
            scratch);
        @out.macroSwX = scratch.x;
        @out.macroSwZ = scratch.z;
        @out.macroSwOutwardX = scratch.outwardX;
        @out.macroSwOutwardZ = scratch.outwardZ;
        @out.hasMacroDeformation =
            @out.macroNwX != 0 ||
            @out.macroNwZ != 0 ||
            @out.macroNeX != 0 ||
            @out.macroNeZ != 0 ||
            @out.macroSeX != 0 ||
            @out.macroSeZ != 0 ||
            @out.macroSwX != 0 ||
            @out.macroSwZ != 0;
        return @out;
    }

    public static double liquidChasmLipOffsetAt(double worldX, double worldZ)
    {
        double macro = smoothCellNoise(worldX, worldZ, LIP_WANDER_SCALE_PX, 2801);
        double detail = smoothCellNoise(worldX + 37.1, worldZ - 19.7, LIP_WANDER_DETAIL_SCALE_PX, 2819);
        double field = macro * (1 - LIP_WANDER_DETAIL_WEIGHT) + detail * LIP_WANDER_DETAIL_WEIGHT;
        return LIP_WANDER_MAX_PX * (0.1 + quintic(field) * 0.9);
    }

    private static bool edgeContinues(
        MaterializedTerrain terrain,
        TerrainCell cell,
        string direction,
        bool atEnd)
    {
        bool alongX = direction == "n" || direction == "s";
        TerrainCell? adjacent = terrainCellAt(
            terrain,
            cell.x + (alongX ? (atEnd ? 1 : -1) : 0),
            cell.y + (alongX ? 0 : atEnd ? 1 : -1));
        if (!carriesLiquid(adjacent)) return false;
        return opensIntoChasm(terrain, adjacent!, direction);
    }

    private static double edgeOffset(
        MaterializedTerrain terrain,
        TerrainCell cell,
        string direction,
        double progress,
        double worldX,
        double worldZ)
    {
        string startTurn = direction == "n" || direction == "s" ? "w" : "n";
        string endTurn = direction == "n" || direction == "s" ? "e" : "s";
        bool atStartContinues =
            edgeContinues(terrain, cell, direction, false) || opensIntoChasm(terrain, cell, startTurn);
        bool atEndContinues =
            edgeContinues(terrain, cell, direction, true) || opensIntoChasm(terrain, cell, endTurn);
        double startEnvelope = atStartContinues ? 1 : quintic(progress / LIP_ENDPOINT_FADE_TILES);
        double endEnvelope = atEndContinues ? 1 : quintic((1 - progress) / LIP_ENDPOINT_FADE_TILES);
        return liquidChasmLipOffsetAt(worldX, worldZ) * Math.min(startEnvelope, endEnvelope);
    }

    public static LiquidChasmEdgePoint liquidChasmSurfacePointAtUv(
        MaterializedTerrain terrain,
        TerrainCell cell,
        LiquidChasmContour contour,
        double x0,
        double x1,
        double z0,
        double z1,
        double u,
        double v,
        LiquidChasmEdgePoint @out)
    {
        double baseX = x0 + (x1 - x0) * u;
        double baseZ = z0 + (z1 - z0) * v;
        double tileSize = Math.max(1e-6, Math.min(x1 - x0, z1 - z0));

        double north = contour.chasmN ? edgeOffset(terrain, cell, "n", u, baseX, z0) : 0;
        double south = contour.chasmS ? edgeOffset(terrain, cell, "s", u, baseX, z1) : 0;
        double west = contour.chasmW ? edgeOffset(terrain, cell, "w", v, x0, baseZ) : 0;
        double east = contour.chasmE ? edgeOffset(terrain, cell, "e", v, x1, baseZ) : 0;

        double fadePx = tileSize * LIP_INTERIOR_FADE_TILES;
        double northWeight = 1 - quintic((baseZ - z0) / fadePx);
        double southWeight = 1 - quintic((z1 - baseZ) / fadePx);
        double westWeight = 1 - quintic((baseX - x0) / fadePx);
        double eastWeight = 1 - quintic((x1 - baseX) / fadePx);

        // Bilinear interpolation preserves the exact shared grid-corner controls. Along a digital stair edge the
        // +1/-1 quarter-tile controls turn the old horizontal/vertical run into the same diagonal on both cells.
        double northMixX = contour.macroNwX + (contour.macroNeX - contour.macroNwX) * u;
        double southMixX = contour.macroSwX + (contour.macroSeX - contour.macroSwX) * u;
        double northMixZ = contour.macroNwZ + (contour.macroNeZ - contour.macroNwZ) * u;
        double southMixZ = contour.macroSwZ + (contour.macroSeZ - contour.macroSwZ) * u;
        double macroX = (northMixX + (southMixX - northMixX) * v) * tileSize;
        double macroZ = (northMixZ + (southMixZ - northMixZ) * v) * tileSize;
        double macroStrength = Math.min(1, Math.hypot(macroX, macroZ) / (tileSize * 0.18));
        double lipScale = 1 - macroStrength;

        @out.x = baseX + macroX + (-west * westWeight + east * eastWeight) * lipScale;
        @out.z = baseZ + macroZ + (-north * northWeight + south * southWeight) * lipScale;
        return @out;
    }

    public static LiquidChasmEdgePoint liquidChasmEdgePointAt(
        MaterializedTerrain terrain,
        TerrainCell cell,
        LiquidChasmContour contour,
        string direction,
        double x0,
        double x1,
        double z0,
        double z1,
        double progress,
        LiquidChasmEdgePoint @out) =>
        liquidChasmSurfacePointAtUv(
            terrain,
            cell,
            contour,
            x0,
            x1,
            z0,
            z1,
            direction == "e" ? 1 : direction == "w" ? 0 : progress,
            direction == "s" ? 1 : direction == "n" ? 0 : progress,
            @out);

    /// <summary>
    /// Materialize the one authoritative Water/Chasm boundary polyline.
    ///
    /// The horizontal liquid, waterfall curtain and wet-bank CREST use these exact samples. Geological backing
    /// then relaxes continuously to the cardinal shaft/floor edge with depth; keeping the deformed lip vertical all
    /// the way down would move the wall away from the complete domain-owned abyss floor. `out` is caller-owned
    /// scratch so the terrain worker remains allocation-free in its hot path.
    /// </summary>
    /// <remarks>
    /// PORT NOTE: `out` is an <see cref="IList{T}"/> so the compiler's fixed eight-point scratch array and a growable
    /// List both fit. The TS grows (`push`) or truncates (`length =`) it to exactly eight points; for an array of the
    /// right length both are no-ops, exactly as in JS (an array of another length cannot be resized and throws).
    /// </remarks>
    public static IReadOnlyList<LiquidChasmEdgePoint> liquidChasmEdgePathInto(
        MaterializedTerrain terrain,
        TerrainCell cell,
        LiquidChasmContour contour,
        string direction,
        double x0,
        double x1,
        double z0,
        double z1,
        IList<LiquidChasmEdgePoint> @out)
    {
        const int pointCount = LIQUID_CHASM_EDGE_SEGMENTS + 1;
        while (@out.Count < pointCount) @out.Add(new LiquidChasmEdgePoint { x = 0, z = 0 });
        while (@out.Count > pointCount) @out.RemoveAt(@out.Count - 1);
        for (int segment = 0; segment < pointCount; segment++)
        {
            liquidChasmEdgePointAt(
                terrain,
                cell,
                contour,
                direction,
                x0,
                x1,
                z0,
                z1,
                (double)segment / LIQUID_CHASM_EDGE_SEGMENTS,
                @out[segment]);
        }
        // Arrays and Lists both implement IReadOnlyList<T>; the TS returns the very same `out`.
        return (IReadOnlyList<LiquidChasmEdgePoint>)@out;
    }

    /// <summary>Shared outward direction for a macro-smoothed edge; zero means the local geometric tangent owns it.</summary>
    public static LiquidChasmEdgePoint liquidChasmEdgeOutwardAt(
        LiquidChasmContour contour,
        string direction,
        double progress,
        LiquidChasmEdgePoint @out)
    {
        double startX =
            direction == "n"
                ? contour.macroNwOutwardX
                : direction == "e"
                    ? contour.macroNeOutwardX
                    : direction == "s"
                        ? contour.macroSwOutwardX
                        : contour.macroNwOutwardX;
        double startZ =
            direction == "n"
                ? contour.macroNwOutwardZ
                : direction == "e"
                    ? contour.macroNeOutwardZ
                    : direction == "s"
                        ? contour.macroSwOutwardZ
                        : contour.macroNwOutwardZ;
        double endX =
            direction == "n"
                ? contour.macroNeOutwardX
                : direction == "e"
                    ? contour.macroSeOutwardX
                    : direction == "s"
                        ? contour.macroSeOutwardX
                        : contour.macroSwOutwardX;
        double endZ =
            direction == "n"
                ? contour.macroNeOutwardZ
                : direction == "e"
                    ? contour.macroSeOutwardZ
                    : direction == "s"
                        ? contour.macroSeOutwardZ
                        : contour.macroSwOutwardZ;
        double x = startX + (endX - startX) * progress;
        double z = startZ + (endZ - startZ) * progress;
        double length = Math.hypot(x, z);
        @out.x = length > 1e-6 ? x / length : 0;
        @out.z = length > 1e-6 ? z / length : 0;
        return @out;
    }
}
