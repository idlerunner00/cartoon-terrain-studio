// Fluitown extension — NOT a port of the original. The comic look's uneven ground (TerrainGroundRelief): the floor
// lattice of an open organic floor and the lift of everything standing on it. Only reached with
// TerrainGroundRelief.Enabled (the Style drawer's "uneven ground" switch); without it the ported organic floor stays.
using System;
using Fluitown.Domain;
using static Fluitown.Domain.TerrainVisualContour;
using static Fluitown.Render.TerrainBakePigment;
using static Fluitown.Render.TerrainCameraAwayCrest;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainGroundDetail;

namespace Fluitown.Render;

public sealed partial class TerrainGeometryCompiler
{
    /// <summary>The relief mask of the bake in progress (invalidated by <see cref="syncGroundDetail"/>).</summary>
    internal readonly TerrainGroundReliefMask groundRelief = new();
    private readonly TerrainVisualContourCorners reliefContourScratch = new() { nw = 0, ne = 0, se = 0, sw = 0 };

    private const int ReliefSide = 6; // the largest lattice side (5 subdivisions)
    [ThreadStatic] private static P3[]? _reliefPoints;
    [ThreadStatic] private static double[]? _reliefNormalX, _reliefNormalZ, _reliefColor, _reliefShade, _reliefGround;

    /// <summary>Whether the ported organic floor branch of <see cref="buildSolidCell"/> builds this cell's cap.</summary>
    private bool organicFloorConstruction()
    {
        string? construction = this.tileset?.construction;
        return construction == "natural" ||
            construction == "combined-building" ||
            construction == "prism" ||
            construction == "viking-ship-village" ||
            construction == "alien-ranch";
    }

    /// <summary>
    /// An open floor the relief may move: a walkable Floor built by the organic floor branch (no visual contour)
    /// without a crest bevel inset (its lattice would not start on the cell edge).
    /// </summary>
    private bool groundReliefOpenCell(MaterializedTerrain terrain, TerrainBakeFrame frame, TerrainCell cell)
    {
        if (cell.type != TileType.Floor || !cell.walkable) return false;
        int wx = frame.i0 + cell.x, wy = frame.j0 + cell.y;
        TerrainVisualContourCorners contour = this.coherentContours
            ? terrainCoherentContourCorners(terrain, cell, wx, wy, reliefContourScratch)
            : terrainVisualContourCorners(terrain, cell, wx, wy, reliefContourScratch);
        double outline = contour.nw + contour.ne + contour.se + contour.sw +
            (contour.n ?? 0) + (contour.e ?? 0) + (contour.s ?? 0) + (contour.w ?? 0);
        if (outline * frame.tileSize > 0.01) return false;
        TerrainEdge north = cell.edges.n!, south = cell.edges.s!;
        double insetNorth = terrainCameraAwayCrestInset(terrainCameraAwayCrestDepth(this.bevelFor(cell, north), north.drop));
        double insetSouth = this.bevelFor(cell, south) * CARTOON_TERRAIN_STYLE.junctions.earthTerraceBevelRunRatio;
        return insetNorth <= 1e-9 && insetSouth <= 1e-9;
    }

    /// <summary>
    /// The bake's own terrain. Derived terrains compiled through <see cref="buildSolidCell"/> (the chasm floor
    /// compilation) keep the ported floor: the relief belongs to open ground only.
    /// </summary>
    private MaterializedTerrain? groundReliefTerrain;

    /// <summary>A new bake starts (<see cref="syncGroundDetail"/>): the mask is rebuilt for its terrain on first use.</summary>
    internal void resetGroundRelief(MaterializedTerrain terrain)
    {
        groundReliefTerrain = terrain;
        groundRelief.Invalidate();
    }

    /// <summary>Whether the relief applies to cells of <paramref name="terrain"/> (the bake's own terrain).</summary>
    internal bool groundReliefOwns(MaterializedTerrain terrain) =>
        TerrainGroundRelief.Enabled && ReferenceEquals(terrain, groundReliefTerrain);

    private void ensureGroundRelief(MaterializedTerrain terrain, TerrainBakeFrame frame)
    {
        if (groundRelief.Built || !ReferenceEquals(terrain, groundReliefTerrain)) return;
        int w = terrain.width, h = terrain.height;
        groundRelief.Begin(w, h);
        bool organic = this.visualGrounding && this.terrainSurfaceProfile.organicGround && organicFloorConstruction();
        if (organic)
        {
            for (int i = 0; i < w * h && i < terrain.cells.Length; i++)
            {
                TerrainCell? cell = terrain.cells[i];
                if (cell != null && groundReliefOpenCell(terrain, frame, cell)) groundRelief.SetCell(i, cell.surfaceZ);
            }
        }
        groundRelief.Finish();
    }

    /// <summary>
    /// The comic look's ground lift (px) at a compile-space point: the ported organic landform plus the Fluitown relief,
    /// times the relief mask. 0 wherever the floor must keep its terrace height.
    /// </summary>
    internal double groundReliefLiftAt(MaterializedTerrain terrain, TerrainBakeFrame frame, double x, double z, bool lumps = false)
    {
        if (!ReferenceEquals(terrain, groundReliefTerrain)) return 0;
        ensureGroundRelief(terrain, frame);
        double s = groundRelief.At((x - frame.originX) / frame.tileSize - frame.i0, (z - frame.originY) / frame.tileSize - frame.j0);
        if (s <= 0) return 0;
        return (TerrainVisualGround.terrainOrganicHeightAt(x, z, this.terrainSurfaceProfile) + TerrainGroundRelief.Shape(x, z, lumps)) * s;
    }

    /// <summary>The ground lift under frame cell (cellX + u, cellY + v) (comic look; 0 when the relief is off).</summary>
    internal double groundReliefLiftAtCell(MaterializedTerrain terrain, TerrainBakeFrame frame, double cellX, double cellY)
    {
        if (!TerrainGroundRelief.Enabled) return 0;
        double ts = frame.tileSize;
        return groundReliefLiftAt(terrain, frame, frame.originX + (frame.i0 + cellX) * ts, frame.originY + (frame.j0 + cellY) * ts);
    }

    /// <summary>
    /// <see cref="groundReliefLiftAtCell"/> for callers without the terrain at hand: reads the mask as built for this
    /// bake (<see cref="buildRunDressing"/> builds it first), 0 before that.
    /// </summary>
    internal double groundReliefLiftAtBuiltCell(TerrainBakeFrame frame, double cellX, double cellY)
    {
        if (!TerrainGroundRelief.Enabled || !groundRelief.Built) return 0;
        double ts = frame.tileSize;
        double x = frame.originX + (frame.i0 + cellX) * ts, z = frame.originY + (frame.j0 + cellY) * ts;
        double s = groundRelief.At(cellX, cellY);
        if (s <= 0) return 0;
        return (TerrainVisualGround.terrainOrganicHeightAt(x, z, this.terrainSurfaceProfile) + TerrainGroundRelief.Shape(x, z)) * s;
    }

    /// <summary>Builds the relief mask of this bake before the dressing places anything on the ground.</summary>
    internal void prepareGroundRelief(MaterializedTerrain terrain, TerrainBakeFrame frame)
    {
        if (TerrainGroundRelief.Enabled) ensureGroundRelief(terrain, frame);
    }

    /// <summary>
    /// The cap of an organic floor cell in the comic look: one welded lattice like the ported branch, with
    /// <see cref="TerrainGroundRelief.SUBDIVISIONS"/> subdivisions where the relief reaches the cell (2 and flat
    /// elsewhere), heights and normals from <see cref="groundReliefLiftAt"/>, pigment, shade and ground channels sampled
    /// per lattice point exactly as the ported branch samples them.
    /// </summary>
    internal void buildReliefFloorCap(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        double x0,
        double x1,
        double northZ,
        double southZ,
        double capY,
        int capKind,
        double capStrength,
        int wx,
        int wy,
        double shadeNw,
        double shadeNe,
        double shadeSe,
        double shadeSw,
        int colorNw,
        int colorNe,
        int colorSe,
        int colorSw)
    {
        ensureGroundRelief(terrain, frame);
        bool active = groundRelief.CellActive(cell.x, cell.y);
        int subdivisions = active ? TerrainGroundRelief.SUBDIVISIONS : 2;
        int row = subdivisions + 1;
        P3[] points = _reliefPoints ??= CreatePoints(ReliefSide * ReliefSide);
        double[] normalX = _reliefNormalX ??= new double[ReliefSide * ReliefSide];
        double[] normalZ = _reliefNormalZ ??= new double[ReliefSide * ReliefSide];
        double[] color = _reliefColor ??= new double[ReliefSide * ReliefSide];
        double[] shade = _reliefShade ??= new double[ReliefSide * ReliefSide];
        double[] ground = _reliefGround ??= new double[ReliefSide * ReliefSide * TERRAIN_GROUND_CHANNEL_STRIDE];
        // Slope over half a sub-quad: each vertex normal is the mean slope of the span it represents (as the ported
        // lattice measures it), so the interpolated normals do not crease at the lattice lines.
        double radius = (x1 - x0) / subdivisions / 2;
        double moisture = planMoistureAt(plan, cell.id);
        for (int gy = 0; gy <= subdivisions; gy++)
        {
            double v = (double)gy / subdivisions;
            double pz = gy == subdivisions ? southZ : northZ + (southZ - northZ) * v;
            for (int gx = 0; gx <= subdivisions; gx++)
            {
                double u = (double)gx / subdivisions;
                double px = gx == subdivisions ? x1 : x0 + (x1 - x0) * u;
                int index = gy * row + gx;
                P3 point = points[index];
                point.x = px;
                point.z = pz;
                point.y = capY;
                normalX[index] = 0;
                normalZ[index] = 0;
                double cavity = 0, form = 0;
                if (active)
                {
                    // Heights without the lumps (the collision stays smooth), normals and paint with them.
                    point.y = capY + groundReliefLiftAt(terrain, frame, px, pz);
                    double lift = groundReliefLiftAt(terrain, frame, px, pz, true);
                    double east = groundReliefLiftAt(terrain, frame, px + radius, pz, true), west = groundReliefLiftAt(terrain, frame, px - radius, pz, true);
                    double south = groundReliefLiftAt(terrain, frame, px, pz + radius, true), north = groundReliefLiftAt(terrain, frame, px, pz - radius, true);
                    normalX[index] = -(east - west) / (radius * 2);
                    normalZ[index] = -(south - north) / (radius * 2);
                    // Curvature over the same span, positive in a hollow: the comic ramp's lit band hides gentle slopes
                    // under a high sun, so the ground is painted like it (hollows shaded and a touch lusher, where water
                    // collects; crests lighter), direction-free and baked, at no cost per frame.
                    cavity = Math.Clamp((east + west + south + north - 4 * lift) / (radius * radius) * TerrainGroundRelief.CAVITY_SCALE, -1, 1);
                    form = TerrainGroundRelief.FormLight(normalX[index], normalZ[index]);
                }
                shade[index] = terrainCapBakeShade(terrain, plan, cell, wx, wy, u, v, capCornerShade(shadeNw, shadeNe, shadeSe, shadeSw, u, v)) *
                    (1 - TerrainGroundRelief.CAVITY_SHADE * cavity) * (1 + TerrainGroundRelief.FORM_SHADE * form);
                int pigment = livingGroundPigmentAt(this.groundDetail, capCornerColor(colorNw, colorNe, colorSe, colorSw, u, v), plan, cell, u, v, moisture, terrain);
                color[index] = cavity > 0 ? Palette.mix(pigment, this.floorLushPole, TerrainGroundRelief.CAVITY_LUSH * cavity) : pigment;
                int slot = index * TERRAIN_GROUND_CHANNEL_STRIDE;
                ground[slot] = turfCoverAt(this.groundDetail, plan, cell, px, pz, u, v);
                var surface = groundSurfaceVectorAt(this.groundDetail, plan, cell, u, v);
                ground[slot + 1] = surface.wear;
                ground[slot + 2] = surface.tangentX;
                ground[slot + 3] = surface.tangentZ;
            }
        }
        // The lattice reads the first row × row entries of each scratch.
        builder.addSurfaceLattice(points, row, normalX, normalZ, color, capKind, capStrength, shade, ground);
    }

    private static P3[] CreatePoints(int count)
    {
        var points = new P3[count];
        for (int i = 0; i < count; i++) points[i] = new P3(0, 0, 0);
        return points;
    }
}
