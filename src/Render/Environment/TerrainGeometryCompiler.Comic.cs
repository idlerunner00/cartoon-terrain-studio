// Fluitown extension — NOT a port of the original. Geometry the comic look adds so the terrain is closed from every
// side; only reached with TerrainComicGeometry.ClosedShells.
using System.Collections.Generic;
using Fluitown.Domain;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainRenderPlanModule;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using static Fluitown.Render.Palette;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed partial class TerrainGeometryCompiler
{
    /// <summary>Compile px the corner flanks reach into the surfaces they meet (≈1.5 cm at the world's scale).</summary>
    private const double CORNER_FLANK_OVERLAP_PX = 0.6;

    /// <summary>
    /// Closes the ends of a cell's north and south crest bevels where the bevel stops at a square cell corner.
    ///
    /// A crest bevel pulls the cap edge inward (<c>ins</c>) and drops it (<c>bevel</c>) to the top of the wall below. At
    /// a corner whose neighbour along the crest stands at the same height without a bevel of its own — the inner corner
    /// of a terrace turn — the bevel's triangular cross-section is left open towards that neighbour's hollow volume
    /// (where the neighbour's own edge drops, <c>buildSolidCell</c> closes it with its side shoulder). The original's
    /// camera looks along that triangle; seen across the turn it is a small notch of sky at the top of the corner.
    /// </summary>
    internal void addCrestBevelEndCaps(
        TileGeometryBuilder builder,
        MaterializedTerrain terrain,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double x1,
        double z0,
        double z1,
        double capY,
        double nBevel,
        double insN,
        double sBevel,
        double insS,
        double contourNw,
        double contourNe,
        double contourSe,
        double contourSw,
        int capColor,
        double capKind,
        double capStrength)
    {
        if (sBevel > 0) crest("s", sBevel, insS, contourSw, contourSe);
        if (nBevel > 0) crest("n", nBevel, insN, contourNw, contourNe);

        void crest(string side, double bevel, double ins, double westCut, double eastCut)
        {
            if (westCut <= 0.01) end(side, "w", bevel, ins);
            if (eastCut <= 0.01) end(side, "e", bevel, ins);
        }

        void end(string side, string toward, double bevel, double ins)
        {
            TerrainEdge sideEdge = cell.edges[toward]!;
            // A dropping side edge carries its own shoulder over the complete bevel run.
            if (sideEdge.visibleFace && sideEdge.drop > 0.02) return;
            TerrainCell? neighbour = terrainCellAt(terrain, cell.x + (toward == "e" ? 1 : -1), cell.y);
            if (neighbour == null || neighbour.type == TileType.Bridge) return;
            if (neighbour.type != TileType.Floor && neighbour.type != TileType.Solid) return;
            if (neighbour.surfaceZ * ELEV < capY - 0.02) return;
            // A neighbour with a bevel on the same crest continues this one; the cross-section is interior.
            if (this.bevelFor(neighbour, neighbour.edges[side]!) > 0) return;
            double x = toward == "e" ? x1 : x0;
            double edgeZ = side == "s" ? z1 : z0;
            double insetZ = side == "s" ? z1 - ins : z0 + ins;
            setShade4(1, 1, 0.94, 0.94);
            builder.addSurface(
                new[]
                {
                    new P3 { x = x, y = capY, z = insetZ },
                    new P3 { x = x, y = capY, z = edgeZ },
                    new P3 { x = x, y = capY - bevel, z = edgeZ },
                },
                // Facing into the air above the bevel: away from the neighbour it closes.
                toward == "e" ? -1 : 1,
                0,
                0,
                // The cap's own rounded edge, like the bevel it closes (TerrainGeometryCompiler.Solid.cs).
                mix(capColor, material.edgeLight, 0.06),
                capKind,
                capStrength,
                SHADE4,
                UNIT_ZERO,
                false,
                true);
        }
    }

    /// <summary>
    /// Closes the flanks of a rounded convex wall corner whose two neighbours stand at different heights.
    ///
    /// <see cref="addContourCornerFace"/> fills the footprint the rounded corner cuts out of its cell (the apron) at
    /// the LOWER neighbour's datum. The higher neighbour emits no face towards the cell (the cell is higher still), so
    /// between that neighbour's cap and the apron one straight apron edge stays open: a narrow vertical window into
    /// the hollow terrain beside every such corner. The original's camera looks along it and never sees it; the Flui
    /// perspective sees the sky through it. Each straight apron edge whose neighbour stands above the apron gets the
    /// neighbour's own flank, from its cap down to the apron, facing into the corner.
    /// </summary>
    /// <param name="apronOutline">The apron: [A endpoint, square corner, B endpoint, arc…] at the cell's top.</param>
    internal void addCornerApronFlanks(
        TileGeometryBuilder builder,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        string corner,
        IReadOnlyList<TerrainContourPoint> apronOutline,
        double apronY,
        string edgeOfA,
        double topAY,
        TerrainCell? neighbourA,
        string edgeOfB,
        double topBY,
        TerrainCell? neighbourB)
    {
        TerrainContourPoint square = apronOutline[1];
        addFlank(apronOutline[0], edgeOfA, topAY, neighbourA);
        addFlank(apronOutline[2], edgeOfB, topBY, neighbourB);

        void addFlank(TerrainContourPoint end, string edge, double topY, TerrainCell? neighbour)
        {
            if (neighbour == null || topY <= apronY + 0.02) return;
            if (neighbour.type != TileType.Floor && neighbour.type != TileType.Solid) return;
            bool rock = neighbour.type == TileType.Solid;
            TerrainMaterial material =
                this.biome?.materialDialect == "aegis-citadel"
                    ? this.materialForCell(neighbour, terrain)
                    : (plan.materials[neighbour.id] ?? this.materialForCell(neighbour, terrain));
            // Facing away from the neighbour: into the cell whose corner was cut.
            double nx = edge == "e" ? -1 : edge == "w" ? 1 : 0;
            double nz = edge == "s" ? -1 : edge == "n" ? 1 : 0;
            double foot = Math.max(
                CARTOON_TERRAIN_STYLE.junctions.contourFaceFootShade,
                wallDepthShadeAt(topY, apronY, false, 0));
            setShade4(1, 1, foot, foot);
            // A hair of overlap into the neighbour's cap and the apron: the joins are T-junctions with those lattices.
            double flankTop = topY + CORNER_FLANK_OVERLAP_PX;
            double flankFoot = apronY - CORNER_FLANK_OVERLAP_PX;
            builder.addSurface(
                quad(
                    square.x,
                    flankTop,
                    square.z,
                    end.x,
                    flankTop,
                    end.z,
                    end.x,
                    flankFoot,
                    end.z,
                    square.x,
                    flankFoot,
                    square.z),
                nx,
                CARTOON_TERRAIN_STYLE.junctions.contourFaceNormalY,
                nz,
                terrainFaceBaseColor(material, rock ? "rock" : "earth"),
                rock ? SURF.rockFace : SURF.earthFace,
                0.17,
                SHADE4,
                UNIT_ZERO,
                rock,
                true,
                nz < 0);
        }
    }
}
