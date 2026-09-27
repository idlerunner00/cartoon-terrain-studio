// Port of packages/client/src/render/environment/terrainWallShelfGeometry.ts — keep in lockstep with the original.
using System;
using Fluitown.Domain;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainProjection;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class WallDepthShelfOptions
{
    /// <summary><see cref="TerrainEdgeDirection"/> ('n' | 'e' | 's' | 'w').</summary>
    public string direction;
    public int cellX;
    public int cellY;
    public double x0;
    public double x1;
    public double z0;
    public double z1;
    public double topY;
    public double bottomY;
    public int faceColor;
    public TerrainMaterial material;
}

public static partial class TerrainWallShelfGeometry
{
    [ThreadStatic] private static P3[]? _QUAD;
    private static P3[] QUAD => _QUAD ??= createQuad();
    private static readonly double[] UNIT_SHADE = { 1, 1, 1, 1 };
    private static readonly double[] UNIT_ZERO = { 0, 0, 0, 0 };

    private static P3[] createQuad()
    {
        var quad = new P3[4];
        for (int index = 0; index < 4; index++) quad[index] = new P3 { x = 0, y = 0, z = 0 };
        return quad;
    }

    // TS `quad(...coordinates)`: twelve explicit coordinates keep the call allocation-free.
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
        double c11)
    {
        P3[] q = QUAD;
        q[0].x = c0;
        q[0].y = c1;
        q[0].z = c2;
        q[1].x = c3;
        q[1].y = c4;
        q[1].z = c5;
        q[2].x = c6;
        q[2].y = c7;
        q[2].z = c8;
        q[3].x = c9;
        q[3].y = c10;
        q[3].z = c11;
        return q;
    }

    /// <summary>Physical horizontal strata for tall ordinary walls, shared by every compass orientation.</summary>
    public static void addWallDepthShelves(PropGeometryBuilder builder, WallDepthShelfOptions options)
    {
        string direction = options.direction;
        int cellX = options.cellX;
        int cellY = options.cellY;
        double x0 = options.x0;
        double x1 = options.x1;
        double z0 = options.z0;
        double z1 = options.z1;
        double topY = options.topY;
        double bottomY = options.bottomY;
        int faceColor = options.faceColor;
        TerrainMaterial material = options.material;
        double dropLevels = (topY - bottomY) / TERRAIN_ELEVATION_STEP_PX;
        double clusterX = Math.floor((double)cellX / CARTOON_TERRAIN_STYLE.wallDepth.clusterSizeCells);
        double clusterY = Math.floor((double)cellY / CARTOON_TERRAIN_STYLE.wallDepth.clusterSizeCells);
        int directionSalt = direction == "n" ? 11 : direction == "s" ? 23 : direction == "e" ? 37 : 53;
        double primarySample = cellHash(clusterX * 97 + directionSalt, clusterY * 89 - directionSalt);
        double secondarySample = cellHash(clusterX * 131 - directionSalt, clusterY * 127 + directionSalt);
        var count = cartoonTerrainWallShelfCount(dropLevels, primarySample, secondarySample);
        if (count == 0) return;
        int topColor = mix(faceColor, material.edgeLight, 0.42);
        int fasciaColor = mix(faceColor, material.edgeDark, 0.38);
        void addShelf(double depthLevels, double depthPx)
        {
            double shelfY = topY - depthLevels * TERRAIN_ELEVATION_STEP_PX;
            if (shelfY <= bottomY + CARTOON_TERRAIN_STYLE.wallDepth.fasciaHeightPx + 0.25) return;
            double outerX = direction == "e" ? x1 + depthPx : direction == "w" ? x0 - depthPx : 0;
            double outerZ = direction == "s" ? z1 + depthPx : direction == "n" ? z0 - depthPx : 0;
            P3[] shelf =
                direction == "s"
                    ? quad(x0, shelfY, z1, x1, shelfY, z1, x1, shelfY, outerZ, x0, shelfY, outerZ)
                    : direction == "n"
                        ? quad(x0, shelfY, outerZ, x1, shelfY, outerZ, x1, shelfY, z0, x0, shelfY, z0)
                        : direction == "e"
                            ? quad(x1, shelfY, z0, outerX, shelfY, z0, outerX, shelfY, z1, x1, shelfY, z1)
                            : quad(outerX, shelfY, z0, x0, shelfY, z0, x0, shelfY, z1, outerX, shelfY, z1);
            builder.addSurface(shelf, 0, 1, 0, topColor, TERRAIN_SURFACE_PATTERN.rockCap, 0.14);
            double fasciaBottom = shelfY - CARTOON_TERRAIN_STYLE.wallDepth.fasciaHeightPx;
            P3[] fascia =
                direction == "s"
                    ? quad(x0, shelfY, outerZ, x1, shelfY, outerZ, x1, fasciaBottom, outerZ, x0, fasciaBottom, outerZ)
                    : direction == "n"
                        ? quad(x1, shelfY, outerZ, x0, shelfY, outerZ, x0, fasciaBottom, outerZ, x1, fasciaBottom, outerZ)
                        : direction == "e"
                            ? quad(outerX, shelfY, z1, outerX, shelfY, z0, outerX, fasciaBottom, z0, outerX, fasciaBottom, z1)
                            : quad(outerX, shelfY, z0, outerX, shelfY, z1, outerX, fasciaBottom, z1, outerX, fasciaBottom, z0);
            builder.addSurface(
                fascia,
                direction == "e" ? 1 : direction == "w" ? -1 : 0,
                0,
                direction == "s" ? 1 : direction == "n" ? -1 : 0,
                fasciaColor,
                TERRAIN_SURFACE_PATTERN.rockFace,
                0.17,
                UNIT_SHADE,
                UNIT_ZERO,
                false,
                false,
                direction == "n");
        }
        addShelf(CARTOON_TERRAIN_STYLE.wallDepth.primaryDepthLevels, CARTOON_TERRAIN_STYLE.wallDepth.primaryShelfDepthPx);
        if (count == 2)
            addShelf(CARTOON_TERRAIN_STYLE.wallDepth.secondaryDepthLevels, CARTOON_TERRAIN_STYLE.wallDepth.secondaryShelfDepthPx);
    }
}
