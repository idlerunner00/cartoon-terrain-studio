// Port of packages/client/src/render/environment/terrainFloorGrowthGeometry.ts — keep in lockstep with the original.
using System;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.FloorFlowerGeometry;
using static Fluitown.Render.FloorGrassGeometry;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainFloorTurf;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGroundDetail;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>The optional final Chasm pigment compression (`Readonly&lt;{ color: number; mix: number }&gt;`).</summary>
public sealed class IntegratedFloorGrowthDepthTint
{
    public int color;
    public double mix;
}

public sealed class IntegratedFloorGrowthOptions
{
    public TerrainBakeFrame frame;
    public MaterializedTerrain terrain;
    public TerrainRenderPlan plan;
    public TerrainCell cell;
    public GroundDetailContext groundDetail;
    public int lushPole;
    public int litPole;
    public int inkPole;
    public FloorFlowerThemeProfile? flowerProfile;
    public double y0;
    public double bladeBudget;
    public Func<int, int, int> cornerColorAt;
    public Func<double, double, double> groundLift;
    /// <summary>Physical standing-cover multiplier; the painted ground composition remains untouched.</summary>
    public double? standingCoverScale;
    /// <summary>Optional explicit flower budget for distant/special floor carriers.</summary>
    public double? flowerBudget;
    /// <summary>Optional final pigment compression for distant growth rooted on a deep Chasm floor.</summary>
    public IntegratedFloorGrowthDepthTint? depthTint;
}

public static partial class TerrainFloorGrowthGeometry
{
    /// <summary>
    /// Emit the standing part of the shared Floor composition. Both ordinary ground and the real deep Chasm floor
    /// call this exact path; Chasm merely applies a final dark tint to the completed plant pigments.
    /// </summary>
    public static void addIntegratedFloorGrowthGeometry(
        PropGeometryBuilder builder,
        IntegratedFloorGrowthOptions options)
    {
        TerrainBakeFrame frame = options.frame;
        MaterializedTerrain terrain = options.terrain;
        TerrainRenderPlan plan = options.plan;
        TerrainCell cell = options.cell;
        GroundDetailContext groundDetail = options.groundDetail;
        double ts = frame.tileSize;
        int worldCellX = frame.i0 + cell.x;
        int worldCellY = frame.j0 + cell.y;
        double originX = frame.originX + worldCellX * ts;
        double originZ = frame.originY + worldCellY * ts;
        int colorAt(int color) =>
            options.depthTint != null ? mix(color, options.depthTint.color, options.depthTint.mix) : color;
        int colorNw = options.cornerColorAt(cell.x, cell.y);
        int colorNe = options.cornerColorAt(cell.x + 1, cell.y);
        int colorSe = options.cornerColorAt(cell.x + 1, cell.y + 1);
        int colorSw = options.cornerColorAt(cell.x, cell.y + 1);
        double coverAt(double u, double v) =>
            turfCoverAt(groundDetail, plan, cell, originX + u * ts, originZ + v * ts, u, v) *
            (options.standingCoverScale ?? 1);
        int matPigmentAt(double u, double v) =>
            colorAt(
                terrainTurfMatPigment(
                    livingGroundPigmentAt(
                        groundDetail,
                        bilinearColor(colorNw, colorNe, colorSe, colorSw, u, v),
                        plan,
                        cell,
                        u,
                        v,
                        Js.InRange(plan.moisture, cell.id) ? plan.moisture[cell.id] : 0,
                        terrain),
                    options.lushPole,
                    coverAt(u, v)));
        double directionAt(double u, double v) =>
            terrainFloorFieldAt(plan, cell, u, v, plan.floor.direction);

        addFloorGrass(builder, new FloorGrassOptions
        {
            palette = new FloorGrassPalette { lush = colorAt(options.lushPole), lit = colorAt(options.litPole) },
            coverAt = coverAt,
            matPigmentAt = matPigmentAt,
            originX = originX,
            originZ = originZ,
            tileSize = ts,
            y0 = options.y0,
            cellX = worldCellX,
            cellY = worldCellY,
            bladeBudget = options.bladeBudget,
            directionAt = directionAt,
            groundLift = options.groundLift,
        });
        addFloorFlowers(builder, new FloorFlowerOptions
        {
            palette = new FloorFlowerPalette
            {
                lush = colorAt(options.lushPole),
                lit = colorAt(options.litPole),
                ink = colorAt(options.inkPole),
            },
            profile = options.flowerProfile,
            originX = originX,
            originZ = originZ,
            tileSize = ts,
            y0 = options.y0,
            cellX = worldCellX,
            cellY = worldCellY,
            flowerBudget =
                options.flowerBudget ?? Math.min(2, Math.max(0, Math.round(options.bladeBudget / 8))),
            flowerCoverAt = (u, v) => terrainFloorFieldAt(plan, cell, u, v, plan.floor.flowers),
            turfCoverAt = coverAt,
            directionAt = directionAt,
            matPigmentAt = matPigmentAt,
            groundLift = options.groundLift,
        });
    }
}
