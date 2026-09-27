// Port of packages/client/src/render/environment/terrainGeometryCompiler.ts — keep in lockstep with the original.
//
// Part C6: the themed cap dressings
// (`addPrismglassRoof`, `addClockworkRoof`), the bridge deck/edge/structure/abutment geometry with the theme deck
// details, the cell material resolution (`materialForCell` … `moistureHint`) and the
// module-level tail after the class (`const hash = propHash;`).
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainRenderPlanModule;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainContourGeometry;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerInk;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainGeometryCompilerTheme;
using static Fluitown.Render.TerrainGroundDetail;
using static Fluitown.Render.TerrainMaterialCompiler;
using static Fluitown.Render.TerrainSuspensionBridge;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed partial class TerrainGeometryCompiler
{
    internal void addPrismglassRoof(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        double insS)
    {
        if (this.tileset == null) return;
        double ts = this.ts;
        double x1 = x0 + ts;
        double z1 = z0 + ts - insS;
        double y = capY + 0.08;
        int pane = mix(material.topDark, this.tileset.flood.surface, 0.22);
        int edge = this.tileset.flood.foam;
        int violet = this.tileset.flood.glint;
        builder.addOverlay(
            quad(x0 + 3, y, z0 + 3, x1 - 3, y, z0 + 3, x1 - 3, y, z1 - 3, x0 + 3, y, z1 - 3),
            pane,
            0.22);
        foreach (string dir in new[] { "n", "e", "s", "w" })
        {
            TerrainEdge edgeInfo = cell.edges[dir]!;
            if (edgeInfo.contactType == TileType.Solid && edgeInfo.drop <= 0.18) continue;
            var (a, b) = capEdgeSegment(x0, x1, z0, z1, dir);
            builder.addOverlayLineFlat(
                y + 0.03,
                a.x,
                a.z,
                b.x,
                b.z,
                dir == "n" || dir == "w" ? 1.25 : 0.9,
                edge,
                0.34);
        }
        int rows = 2 + (int)Math.floor(hash(cell.id * 2131 + 5) * 2);
        for (int i = 0; i < rows; i++)
        {
            double h = hash(cell.id * 2137 + i * 31);
            double zz = z0 + ts * (0.22 + i * 0.2 + h * 0.07);
            builder.addOverlayLineFlat(
                y + 0.04,
                x0 + ts * 0.16,
                zz,
                x1 - ts * 0.14,
                zz + (h - 0.5) * ts * 0.06,
                0.68,
                i % 2 != 0 ? violet : edge,
                0.26);
        }
        if (hash(cell.id * 2141 + 11) < 0.72)
        {
            double px = x0 + ts * (0.22 + hash(cell.id * 2143 + 13) * 0.56);
            double pz = z0 + ts * (0.22 + hash(cell.id * 2147 + 17) * 0.54);
            double r = ts * (0.055 + hash(cell.id * 2153 + 19) * 0.055);
            double a = hash(cell.id * 2161 + 23) * PROP_TAU;
            double dx = Math.cos(a) * r;
            double dz = Math.sin(a) * r * 0.48;
            double nx = -Math.sin(a) * r * 0.5;
            double nz = Math.cos(a) * r * 0.24;
            builder.addOverlay(
                new P3[]
                {
                    new P3(px - dx - nx * 0.18, y + 0.02, pz - dz - nz * 0.18),
                    new P3(px + dx * 0.86, y + 0.02, pz + dz * 0.86),
                    new P3(px + nx * 0.78, y + 0.02, pz + nz * 0.78),
                    new P3(px - dx * 0.5 + nx * 0.34, y + 0.02, pz - dz * 0.5 + nz * 0.34),
                },
                mix(this.tileset.decal.mid, violet, 0.24),
                0.24);
            builder.addOverlayLineFlat(
                y + 0.05,
                px - dx * 0.6,
                pz - dz * 0.6,
                px + dx * 0.6,
                pz + dz * 0.6,
                0.48,
                edge,
                0.28);
        }
    }

    internal void addClockworkRoof(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        double insS)
    {
        if (this.tileset == null) return;
        double ts = this.ts;
        double x1 = x0 + ts;
        double z1 = z0 + ts - insS;
        // World tile coordinates stay doubles: `wx * 1301` etc. are JS double products (exact, never wrapped).
        double wx = frame.i0 + cell.x;
        double wy = frame.j0 + cell.y;
        double y = capY + 0.08;
        int capColor = terrainMaterialSurfaceTopColor(material, cell);
        int brass = mix(this.tileset.decal.mid, material.edgeLight, 0.18);
        int brassDark = mix(this.tileset.decal.mid, this.tileset.terrain.wallDeep, 0.36);
        int teal = this.tileset.decal.accent;
        int ink = this.tileset.terrain.wallLine;
        const double lipW = 2.25;
        int exposed = 0;

        foreach (string dir in new[] { "n", "e", "s", "w" })
        {
            TerrainEdge edge = cell.edges[dir]!;
            if (edge.contactType == TileType.Solid || edge.drop < 0.55) continue;
            exposed++;
            if (dir == "n")
            {
                builder.addOverlay(
                    quad(x0, y, z0, x1, y, z0, x1, y, z0 + lipW, x0, y, z0 + lipW),
                    brass,
                    0.44);
            }
            else if (dir == "s")
            {
                builder.addOverlay(
                    quad(x0, y, z1 - lipW, x1, y, z1 - lipW, x1, y, z1, x0, y, z1),
                    brass,
                    0.44);
            }
            else if (dir == "e")
            {
                builder.addOverlay(
                    quad(x1 - lipW, y, z0, x1, y, z0, x1, y, z1, x1 - lipW, y, z1),
                    brass,
                    0.44);
            }
            else
            {
                builder.addOverlay(
                    quad(x0, y, z0, x0 + lipW, y, z0, x0 + lipW, y, z1, x0, y, z1),
                    brass,
                    0.44);
            }
        }

        for (int i = 1; i < 3; i++)
        {
            double t = (double)i / 3;
            builder.addOverlayLineFlat(
                y + 0.02,
                x0 + ts * t,
                z0 + 4,
                x0 + ts * t,
                z1 - 4,
                0.52,
                brassDark,
                0.22);
            builder.addOverlayLineFlat(
                y + 0.02,
                x0 + 4,
                z0 + ts * t,
                x1 - 4,
                z0 + ts * t,
                0.52,
                brassDark,
                0.18);
        }

        double h = cellHash(wx * 1301 + 17, wy * 1303 + 19);
        double cx = x0 + ts * (0.28 + cellHash(wx * 1319, wy * 1321) * 0.44);
        double cz = z0 + ts * (0.28 + cellHash(wx * 1327, wy * 1361) * 0.44);
        if (h < 0.58 || exposed > 1)
        {
            double r = ts * (0.095 + h * 0.055);
            for (int i = 0; i < 6; i++)
            {
                double a = h * PROP_TAU + i * (PROP_TAU / 6);
                builder.addOverlayLineFlat(
                    y + 0.06,
                    cx + Math.cos(a) * r * 0.34,
                    cz + Math.sin(a) * r * 0.24,
                    cx + Math.cos(a) * r,
                    cz + Math.sin(a) * r * 0.72,
                    0.44,
                    i % 2 != 0 ? teal : brass,
                    0.18);
            }
        }
        else if (h > 0.82)
        {
            double w = ts * (0.12 + h * 0.05);
            double d = ts * 0.055;
            propBox(
                builder,
                cx - w,
                cx + w,
                cz - d,
                cz + d,
                capY + 0.2,
                capY + 3.6,
                mix(capColor, brass, 0.22),
                brassDark,
                0.82);
            outlineBoxCap(builder, cx - w, cx + w, cz - d, cz + d, capY + 3.66, ink, 0.9, 0.22);
        }

        if (exposed > 0)
        {
            builder.addOverlayLineFlat(
                y + 0.06,
                x0 + ts * 0.18,
                z0 + ts * 0.5,
                x1 - ts * 0.18,
                z0 + ts * 0.5,
                0.72,
                teal,
                0.24);
        }
    }

    /// <summary>Bridge deck dressing over the plank caps: seam ink between planks, a raised timber CURB along every
    ///  open-span edge (lit top + shaded outer lip — the readable "don't step off" rail), and a soft translucent
    ///  under-deck shadow band on the water beside the south edge, so the deck visibly hovers above the river.</summary>
    internal void addBridgeDeckDetail(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        bool hasContour,
        IReadOnlyList<TerrainContourPoint>? footprint = null,
        bool travelNorthSouth = true)
    {
        double ts = this.ts;
        bool waterEW = travelNorthSouth;
        this.addBridgeStructure(
            builder,
            frame,
            terrain,
            plan,
            cell,
            material,
            x0,
            z0,
            capY,
            waterEW,
            footprint);
        this.addBridgeEdgeArchitecture(
            builder,
            frame,
            terrain,
            cell,
            material,
            x0,
            z0,
            capY,
            footprint);
        // Theme plates, seam ink and posts are rectangular carpentry motifs. The structural deck and girders above
        // already carry the organic endpoint; suppressing those optional marks on the few endpoint cells prevents
        // them from floating over the removed corner, exactly like contoured wall-cap dressing does.
        if (hasContour) return;
        // The actual plank bands own the joints. A second three-way overlay restarted per cell on top of the
        // shared four-board period and was itself a visible ownership grid.
        if (this.cityTileset)
        {
            double y = capY + 0.08;
            int neon = this.tileset?.decal.accent ?? cityNeonColor(cell.id * 577);
            int rail = cityNeonColor((double)(frame.i0 + cell.x) * 719 + (double)(frame.j0 + cell.y) * 733);
            if (waterEW)
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.5,
                    z0 + 5,
                    x0 + ts * 0.5,
                    z0 + ts - 5,
                    1.1,
                    neon,
                    0.44);
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.28,
                    z0 + 8,
                    x0 + ts * 0.28,
                    z0 + ts - 8,
                    0.8,
                    rail,
                    0.24);
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.72,
                    z0 + 8,
                    x0 + ts * 0.72,
                    z0 + ts - 8,
                    0.8,
                    rail,
                    0.24);
                for (int i = 0; i < 3; i++)
                {
                    double zz = z0 + ts * (0.25 + i * 0.2);
                    builder.addOverlayLineFlat(
                        y + 0.02,
                        x0 + ts * 0.36,
                        zz - 2,
                        x0 + ts * 0.64,
                        zz + 2,
                        0.72,
                        i % 2 != 0 ? 0xff2bd6 : 0xb6ff3d,
                        0.23);
                }
            }
            else
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + 5,
                    z0 + ts * 0.5,
                    x0 + ts - 5,
                    z0 + ts * 0.5,
                    1.1,
                    neon,
                    0.44);
                builder.addOverlayLineFlat(
                    y,
                    x0 + 8,
                    z0 + ts * 0.28,
                    x0 + ts - 8,
                    z0 + ts * 0.28,
                    0.8,
                    rail,
                    0.24);
                builder.addOverlayLineFlat(
                    y,
                    x0 + 8,
                    z0 + ts * 0.72,
                    x0 + ts - 8,
                    z0 + ts * 0.72,
                    0.8,
                    rail,
                    0.24);
                for (int i = 0; i < 3; i++)
                {
                    double xx = x0 + ts * (0.25 + i * 0.2);
                    builder.addOverlayLineFlat(
                        y + 0.02,
                        xx - 2,
                        z0 + ts * 0.36,
                        xx + 2,
                        z0 + ts * 0.64,
                        0.72,
                        i % 2 != 0 ? 0xff2bd6 : 0xb6ff3d,
                        0.23);
                }
            }
        }
        if (this.olympianTileset)
        {
            double y = capY + 0.12;
            int gold = this.tileset?.decal.accent ?? this.olympianMetal(cell.id * 577);
            int rail = this.olympianMetal((double)(frame.i0 + cell.x) * 719 + (double)(frame.j0 + cell.y) * 733);
            int marble = mix(material.edgeLight, this.tileset?.terrain.wallLit ?? 0xffffff, 0.34);
            void addPost(double px, double pz)
            {
                propBox(
                    builder,
                    px - 1.4,
                    px + 1.4,
                    pz - 1.4,
                    pz + 1.4,
                    capY + 0.5,
                    capY + 5.6,
                    rail,
                    mix(rail, material.side, 0.34),
                    0.82);
            }
            if (waterEW)
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.5,
                    z0 + 5,
                    x0 + ts * 0.5,
                    z0 + ts - 5,
                    1.0,
                    marble,
                    0.38);
                foreach (double rx in new[] { x0 + 5.2, x0 + ts - 5.2 })
                {
                    builder.addOverlayLineFlat(y + 0.06, rx, z0 + 6, rx, z0 + ts - 6, 1.05, gold, 0.5);
                    foreach (double pz in new[] { z0 + 8, z0 + ts * 0.5, z0 + ts - 8 }) addPost(rx, pz);
                }
            }
            else
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + 5,
                    z0 + ts * 0.5,
                    x0 + ts - 5,
                    z0 + ts * 0.5,
                    1.0,
                    marble,
                    0.38);
                foreach (double rz in new[] { z0 + 5.2, z0 + ts - 5.2 })
                {
                    builder.addOverlayLineFlat(y + 0.06, x0 + 6, rz, x0 + ts - 6, rz, 1.05, gold, 0.5);
                    foreach (double px in new[] { x0 + 8, x0 + ts * 0.5, x0 + ts - 8 }) addPost(px, rz);
                }
            }
        }
        if (this.clockworkTileset)
            this.addClockworkBridgeDeckDetail(builder, frame, cell, material, x0, z0, capY, waterEW);
        if (this.prismglassTileset || this.tileset?.decal.kind == "glassShard")
            this.addPrismglassBridgeDeckDetail(builder, cell, material, x0, z0, capY, waterEW);
        if (this.cathedralTileset || this.tileset?.decal.kind == "cathedralStar")
        {
            double y = capY + 0.12;
            int bone = this.tileset?.terrain.wallLit ?? material.edgeLight;
            int gold = cathedralStarfire((double)cell.id * 1399 + (double)frame.i0 * 19 + (double)frame.j0 * 23);
            if (waterEW)
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.5,
                    z0 + 6,
                    x0 + ts * 0.5,
                    z0 + ts - 6,
                    0.92,
                    gold,
                    0.38);
                foreach (double rx in new[] { x0 + 5.4, x0 + ts - 5.4 })
                {
                    builder.addOverlayLineFlat(y + 0.04, rx, z0 + 6, rx, z0 + ts - 6, 1.05, bone, 0.46);
                }
            }
            else
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + 6,
                    z0 + ts * 0.5,
                    x0 + ts - 6,
                    z0 + ts * 0.5,
                    0.92,
                    gold,
                    0.38);
                foreach (double rz in new[] { z0 + 5.4, z0 + ts - 5.4 })
                {
                    builder.addOverlayLineFlat(y + 0.04, x0 + 6, rz, x0 + ts - 6, rz, 1.05, bone, 0.46);
                }
            }
        }
        if (this.tileset?.decal.kind == "runeKnot")
        {
            double y = capY + 0.12;
            int tar = mix(material.edgeDark, this.tileset.decal.ink, 0.34);
            int gold = this.tileset.decal.accent;
            int bone = this.tileset.flood.foam;
            if (waterEW)
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.5,
                    z0 + 5,
                    x0 + ts * 0.5,
                    z0 + ts - 5,
                    1.15,
                    gold,
                    0.44);
                for (int i = 1; i <= 5; i++)
                {
                    double pz = z0 + (ts * i) / 6;
                    builder.addOverlayLineFlat(
                        y + 0.02,
                        x0 + 5,
                        pz,
                        x0 + ts - 5,
                        pz,
                        0.78,
                        i == 3 ? bone : tar,
                        0.34);
                }
            }
            else
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + 5,
                    z0 + ts * 0.5,
                    x0 + ts - 5,
                    z0 + ts * 0.5,
                    1.15,
                    gold,
                    0.44);
                for (int i = 1; i <= 5; i++)
                {
                    double px = x0 + (ts * i) / 6;
                    builder.addOverlayLineFlat(
                        y + 0.02,
                        px,
                        z0 + 5,
                        px,
                        z0 + ts - 5,
                        0.78,
                        i == 3 ? bone : tar,
                        0.34);
                }
            }
        }
        if (this.tileset?.decal.kind == "cropCircle")
        {
            // Alloy walkway decks: a glowing centre feed-line with alloy cross ribs — the ranch tends its goo
            // canals from beam gratings, never timber planks.
            double y = capY + 0.12;
            int rib = mix(material.edgeDark, this.tileset.decal.ink, 0.3);
            int feed = this.tileset.decal.accent;
            int froth = this.tileset.flood.foam;
            if (waterEW)
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.5,
                    z0 + 5,
                    x0 + ts * 0.5,
                    z0 + ts - 5,
                    1.05,
                    feed,
                    0.4);
                for (int i = 1; i <= 4; i++)
                {
                    double pz = z0 + (ts * i) / 5;
                    builder.addOverlayLineFlat(
                        y + 0.02,
                        x0 + 4.6,
                        pz,
                        x0 + ts - 4.6,
                        pz,
                        0.72,
                        i == 2 ? froth : rib,
                        0.32);
                }
            }
            else
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + 5,
                    z0 + ts * 0.5,
                    x0 + ts - 5,
                    z0 + ts * 0.5,
                    1.05,
                    feed,
                    0.4);
                for (int i = 1; i <= 4; i++)
                {
                    double px = x0 + (ts * i) / 5;
                    builder.addOverlayLineFlat(
                        y + 0.02,
                        px,
                        z0 + 4.6,
                        px,
                        z0 + ts - 4.6,
                        0.72,
                        i == 2 ? froth : rib,
                        0.32);
                }
            }
        }
        if (this.tileset?.decal.kind == "carnival")
        {
            double y = capY + 0.11;
            IReadOnlyList<int> colors = TERRAIN_GEOMETRY_CARNIVAL_COLORS;
            int icing = this.tileset.flood.foam;
            int rail = mix(this.tileset.bridge.railLit, colors[3], 0.28);
            int railSide = mix(this.tileset.bridge.bodyShadow, this.tileset.decal.ink, 0.28);
            void addBulb(double px, double pz, int i)
            {
                propFrustum(
                    builder,
                    px,
                    pz,
                    capY + 0.5,
                    capY + 4.0,
                    0.82,
                    1.25,
                    6,
                    i,
                    colors[(i + 2) % colors.Count],
                    railSide,
                    0.86);
            }
            if (waterEW)
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + ts * 0.5,
                    z0 + 6,
                    x0 + ts * 0.5,
                    z0 + ts - 6,
                    1.08,
                    icing,
                    0.36);
                foreach (double rx in new[] { x0 + 5.2, x0 + ts - 5.2 })
                {
                    builder.addOverlayLineFlat(y + 0.05, rx, z0 + 6, rx, z0 + ts - 6, 1.24, rail, 0.5);
                    for (int i = 0; i < 3; i++) addBulb(rx, z0 + ts * (0.2 + i * 0.3), i);
                }
                for (int i = 0; i < 3; i++)
                {
                    double xx = x0 + ts * (0.3 + i * 0.2);
                    builder.addOverlayLineFlat(
                        y + 0.03,
                        xx - 2.2,
                        z0 + ts * 0.36,
                        xx + 2.2,
                        z0 + ts * 0.64,
                        0.68,
                        i % 2 != 0 ? colors[2] : colors[4],
                        0.26);
                }
            }
            else
            {
                builder.addOverlayLineFlat(
                    y,
                    x0 + 6,
                    z0 + ts * 0.5,
                    x0 + ts - 6,
                    z0 + ts * 0.5,
                    1.08,
                    icing,
                    0.36);
                foreach (double rz in new[] { z0 + 5.2, z0 + ts - 5.2 })
                {
                    builder.addOverlayLineFlat(y + 0.05, x0 + 6, rz, x0 + ts - 6, rz, 1.24, rail, 0.5);
                    for (int i = 0; i < 3; i++) addBulb(x0 + ts * (0.2 + i * 0.3), rz, i);
                }
                for (int i = 0; i < 3; i++)
                {
                    double zz = z0 + ts * (0.3 + i * 0.2);
                    builder.addOverlayLineFlat(
                        y + 0.03,
                        x0 + ts * 0.36,
                        zz - 2.2,
                        x0 + ts * 0.64,
                        zz + 2.2,
                        0.68,
                        i % 2 != 0 ? colors[2] : colors[4],
                        0.26);
                }
            }
        }
        if (this.tileset?.decal.kind == "rainbow")
        {
            double y = capY + 0.09;
            for (int i = 0; i < TERRAIN_GEOMETRY_RAINBOW_COLORS.Count; i++)
            {
                int color = TERRAIN_GEOMETRY_RAINBOW_COLORS[i];
                double t = 0.18 + ((double)i / (TERRAIN_GEOMETRY_RAINBOW_COLORS.Count - 1)) * 0.64;
                if (waterEW)
                {
                    builder.addOverlayLineFlat(
                        y,
                        x0 + ts * t,
                        z0 + 6,
                        x0 + ts * t,
                        z0 + ts - 6,
                        0.8,
                        color,
                        0.34);
                }
                else
                {
                    builder.addOverlayLineFlat(
                        y,
                        x0 + 6,
                        z0 + ts * t,
                        x0 + ts - 6,
                        z0 + ts * t,
                        0.8,
                        color,
                        0.34);
                }
            }
            int sparkle =
                TERRAIN_GEOMETRY_RAINBOW_COLORS[(int)Math.floor(hash(cell.id * 997 + 5) * TERRAIN_GEOMETRY_RAINBOW_COLORS.Count)];
            builder.addOverlayLineFlat(
                y + 0.04,
                x0 + ts * 0.28,
                z0 + ts * 0.32,
                x0 + ts * 0.72,
                z0 + ts * 0.68,
                0.58,
                mix(sparkle, material.edgeLight, 0.22),
                0.2);
        }
        if (this.tileset?.decal.kind == "reef")
        {
            double y = capY + 0.1;
            int glow = this.tileset.decal.accent;
            int plankInk = mix(material.edgeDark, this.tileset.flood.deep, 0.36);
            for (int i = 0; i < 3; i++)
            {
                double h = hash(cell.id * 1861 + i * 61);
                double wobble = (h - 0.5) * ts * 0.08;
                if (waterEW)
                {
                    double xx = x0 + ts * (0.26 + i * 0.24) + wobble;
                    builder.addOverlayLineFlat(
                        y,
                        xx,
                        z0 + 6,
                        xx + (h - 0.5) * 4,
                        z0 + ts - 6,
                        1.05,
                        plankInk,
                        0.36);
                }
                else
                {
                    double zz = z0 + ts * (0.26 + i * 0.24) + wobble;
                    builder.addOverlayLineFlat(
                        y,
                        x0 + 6,
                        zz,
                        x0 + ts - 6,
                        zz + (h - 0.5) * 4,
                        1.05,
                        plankInk,
                        0.36);
                }
            }
            builder.addOverlayLineFlat(
                y + 0.03,
                x0 + ts * 0.22,
                z0 + ts * 0.74,
                x0 + ts * 0.78,
                z0 + ts * 0.28,
                0.64,
                glow,
                0.2);
            for (int i = 0; i < 4; i++)
            {
                double h = hash(cell.id * 1873 + i * 67);
                double px = waterEW ? x0 + ts * 0.5 + (i - 1.5) * ts * 0.14 : x0 + ts * (0.2 + h * 0.6);
                double pz = waterEW ? z0 + ts * (0.2 + h * 0.6) : z0 + ts * 0.5 + (i - 1.5) * ts * 0.14;
                double r = 1.0 + h * 0.7;
                builder.addOverlay(
                    new P3[]
                    {
                        new P3(px - r, y + 0.04, pz - r * 0.6),
                        new P3(px + r, y + 0.04, pz - r * 0.6),
                        new P3(px + r, y + 0.04, pz + r * 0.6),
                        new P3(px - r, y + 0.04, pz + r * 0.6),
                    },
                    h < 0.6 ? this.tileset.flood.foam : glow,
                    0.18 + h * 0.12);
            }
        }
    }

    /// <summary>
    /// Continuous load-bearing edge architecture for every open bridge flank.
    ///
    /// The former 2.4 px curb disappeared at gameplay zoom and left the deck reading as a floating texture. A
    /// proper curb, posts, two rails and alternating diagonal braces now expose the load path at silhouette scale.
    /// Contoured endpoints are clipped against the same deck polygon as planks, fascia and soffit, so the detail
    /// cannot float over a rounded-away Water corner.
    /// </summary>
    internal void addBridgeEdgeArchitecture(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        IReadOnlyList<TerrainContourPoint>? footprint = null)
    {
        var style = CARTOON_TERRAIN_STYLE.bridgeStructure;
        double ts = frame.tileSize;
        double x1 = x0 + ts;
        double z1 = z0 + ts;
        double curbWidth = style.curbWidthCells * ts;
        double curbTopY = capY + style.curbHeightLevels * ELEV;
        double railTopY = capY + style.railHeightLevels * ELEV;
        double railMiddleY = capY + style.railMiddleHeightLevels * ELEV;
        double railWidth = style.railBeamWidthCells * ts;
        double postWidth = style.railPostWidthCells * ts;
        double braceWidth = style.railBraceWidthCells * ts;
        int curbTop = mix(material.top, material.edgeLight, 0.48);
        int railTop = mix(material.edgeLight, material.top, 0.28);
        int railSide = mix(material.edgeDark, material.side, 0.3);
        int braceTop = mix(material.topDark, material.edgeLight, 0.2);
        var interval = new AxisInterval { minimum = 0, maximum = 0 };
        int worldParity = (frame.i0 + cell.x + frame.j0 + cell.y) & 1;

        foreach (string dir in new[] { "n", "e", "s", "w" })
        {
            if (!terrainBridgeOpenSpanContact(cell.edges[dir]!.contactType)) continue;
            bool horizontal = dir == "n" || dir == "s";
            double edgeCenter = horizontal
                ? dir == "n"
                    ? z0 + curbWidth * 0.5
                    : z1 - curbWidth * 0.5
                : dir == "w"
                    ? x0 + curbWidth * 0.5
                    : x1 - curbWidth * 0.5;
            double runMin = horizontal ? x0 : z0;
            double runMax = horizontal ? x1 : z1;
            if (
                footprint != null &&
                terrainContourAxisIntervalAt(footprint, horizontal ? "z" : "x", edgeCenter, interval))
            {
                runMin = interval.minimum;
                runMax = interval.maximum;
            }
            if (runMax - runMin < postWidth * 2.4) continue;

            // `delta: -1 | 1`.
            bool closesAt(int delta)
            {
                TerrainCell? neighbor = terrainCellAt(
                    terrain,
                    cell.x + (horizontal ? delta : 0),
                    cell.y + (horizontal ? 0 : delta));
                if (neighbor == null) return false;
                if (neighbor.type != TileType.Bridge) return true;
                if (neighbor.surfaceZ > cell.surfaceZ + 0.08) return false;
                return (
                    Math.abs(neighbor.surfaceZ - cell.surfaceZ) > 0.08 ||
                    !terrainBridgeOpenSpanContact(neighbor.edges[dir]!.contactType)
                );
            }
            bool closesStart = closesAt(-1);
            bool closesEnd = closesAt(1);
            int sideMask = horizontal
                ? PROP_BOX_SIDE_NORTH |
                  PROP_BOX_SIDE_SOUTH |
                  (closesStart ? PROP_BOX_SIDE_WEST : 0) |
                  (closesEnd ? PROP_BOX_SIDE_EAST : 0)
                : PROP_BOX_SIDE_WEST |
                  PROP_BOX_SIDE_EAST |
                  (closesStart ? PROP_BOX_SIDE_NORTH : 0) |
                  (closesEnd ? PROP_BOX_SIDE_SOUTH : 0);
            double cross0 = edgeCenter - curbWidth * 0.5;
            double cross1 = edgeCenter + curbWidth * 0.5;
            double beamCross0 = edgeCenter - railWidth * 0.5;
            double beamCross1 = edgeCenter + railWidth * 0.5;
            // `mask = sideMask` default parameter → null stands for "not passed".
            void addRunBox(
                double along0,
                double along1,
                double across0,
                double across1,
                double bottomY,
                double topY,
                int top,
                int side,
                int? mask = null)
            {
                propBox(
                    builder,
                    horizontal ? along0 : across0,
                    horizontal ? along1 : across1,
                    horizontal ? across0 : along0,
                    horizontal ? across1 : along1,
                    bottomY,
                    topY,
                    top,
                    side,
                    0.82,
                    true,
                    SURF.bridge,
                    mask ?? sideMask);
            }

            addRunBox(runMin, runMax, cross0, cross1, capY, curbTopY, curbTop, railSide);
            addRunBox(
                runMin,
                runMax,
                beamCross0,
                beamCross1,
                railTopY - railWidth,
                railTopY,
                railTop,
                railSide);
            addRunBox(
                runMin + postWidth * 0.25,
                runMax - postWidth * 0.25,
                beamCross0,
                beamCross1,
                railMiddleY - railWidth * 0.5,
                railMiddleY + railWidth * 0.5,
                braceTop,
                railSide);

            var postAlong = new List<double> { (runMin + runMax) * 0.5 };
            if (closesStart) postAlong.push(runMin + postWidth * 0.55);
            if (closesEnd) postAlong.push(runMax - postWidth * 0.55);
            foreach (double along in postAlong)
                addRunBox(
                    along - postWidth * 0.5,
                    along + postWidth * 0.5,
                    edgeCenter - postWidth * 0.5,
                    edgeCenter + postWidth * 0.5,
                    capY + style.curbHeightLevels * ELEV * 0.55,
                    railTopY + railWidth * 0.18,
                    railTop,
                    railSide,
                    PROP_BOX_SIDE_NORTH | PROP_BOX_SIDE_EAST | PROP_BOX_SIDE_SOUTH | PROP_BOX_SIDE_WEST);

            double braceInset = Math.min((runMax - runMin) * 0.22, postWidth * 1.05);
            double lowAlong = worldParity == 0 ? runMin + braceInset : runMax - braceInset;
            double highAlong = worldParity == 0 ? runMax - braceInset : runMin + braceInset;
            P3 point(double along, double y) =>
                horizontal ? new P3(along, y, edgeCenter) : new P3(edgeCenter, y, along);
            addBridgeBeam(
                builder,
                point(lowAlong, curbTopY + railWidth * 0.6),
                point(highAlong, railTopY - railWidth * 1.35),
                braceWidth,
                braceTop,
                railSide,
                SURF.bridge,
                false);
            // The deck's ambient occlusion follows EVERY Water flank. The old south-only card left north/east/west
            // contacts fully lit, which read as turquoise slits cutting through the fascia at oblique camera angles.
            TerrainCell? neighbor = terrainCellAt(
                terrain,
                cell.x + (dir == "e" ? 1 : dir == "w" ? -1 : 0),
                cell.y + (dir == "s" ? 1 : dir == "n" ? -1 : 0));
            if (neighbor?.type == TileType.Water)
            {
                double waterY = (neighbor.waterLevel ?? neighbor.surfaceZ) * ELEV + 0.12;
                double band = Math.min(7, ts * 0.1);
                if (dir == "n")
                {
                    setShade4(0, 0, 0.22, 0.22);
                    builder.addOverlayShaded(
                        quad(x0, waterY, z0 - band, x1, waterY, z0 - band, x1, waterY, z0, x0, waterY, z0),
                        TERRAIN_GEOMETRY_INK_WORLD_LINE,
                        0.22,
                        SHADE4);
                }
                else if (dir == "s")
                {
                    setShade4(0.22, 0.22, 0, 0);
                    builder.addOverlayShaded(
                        quad(x0, waterY, z1, x1, waterY, z1, x1, waterY, z1 + band, x0, waterY, z1 + band),
                        TERRAIN_GEOMETRY_INK_WORLD_LINE,
                        0.22,
                        SHADE4);
                }
                else if (dir == "w")
                {
                    setShade4(0, 0.22, 0.22, 0);
                    builder.addOverlayShaded(
                        quad(x0 - band, waterY, z0, x0, waterY, z0, x0, waterY, z1, x0 - band, waterY, z1),
                        TERRAIN_GEOMETRY_INK_WORLD_LINE,
                        0.22,
                        SHADE4);
                }
                else
                {
                    setShade4(0.22, 0, 0, 0.22);
                    builder.addOverlayShaded(
                        quad(x1, waterY, z0, x1 + band, waterY, z0, x1 + band, waterY, z1, x1, waterY, z1),
                        TERRAIN_GEOMETRY_INK_WORLD_LINE,
                        0.22,
                        SHADE4);
                }
            }
            builder.bridgeRailSegments++;
        }
    }

    internal void addBridgeStructure(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        bool travelNorthSouth,
        IReadOnlyList<TerrainContourPoint>? footprint = null)
    {
        double ts = frame.tileSize;
        double x1 = x0 + ts;
        double z1 = z0 + ts;
        var structure = CARTOON_TERRAIN_STYLE.bridgeStructure;
        double girderInset = structure.girderInsetCells * ts;
        double girderWidth = structure.girderWidthCells * ts;
        double girderBottom = capY - structure.girderDepthLevels * ELEV;
        double girderTop = capY - 1.25;
        int timberTop = mix(material.topDark, material.edgeLight, 0.18);
        int timberSide = mix(material.side, material.edgeDark, 0.34);
        var intervalA = new AxisInterval { minimum = 0, maximum = 0 };
        var intervalB = new AxisInterval { minimum = 0, maximum = 0 };
        // `edge: TerrainEdgeDirection`.
        bool opensWaterfallPortal(string edge) =>
            terrainWaterfallPortalAtCellEdge(plan.waterfalls, cell.id, edge) != null;
        var portal = (
            n: opensWaterfallPortal("n"),
            e: opensWaterfallPortal("e"),
            s: opensWaterfallPortal("s"),
            w: opensWaterfallPortal("w"));
        // Structural members may continue outside the hydraulic aperture, never through it. Clearance follows the
        // shared physical sheet offset (zero for Chasm portals) plus the member's own half-width.
        double portalClearance = structure.abutmentLengthCells * ts + girderWidth;
        // The transverse deck planks already tie the two longitudinal stringers together. Separate per-cell
        // crossbeam boxes projected as detached bars into low Water/waterfall openings on tall or stepped bridges.
        if (travelNorthSouth)
        {
            foreach (double cx in new[] { x0 + girderInset, x1 - girderInset })
            {
                if ((portal.w && cx < (x0 + x1) * 0.5) || (portal.e && cx > (x0 + x1) * 0.5)) continue;
                // The deck fascia owns every exposed run end, and the neighbouring girder owns the continuation.
                // Closed per-cell boxes put coplanar end caps on every join and another cap behind the fascia.
                double girderZ0 = z0;
                double girderZ1 = z1;
                if (
                    footprint != null &&
                    terrainContourAxisIntervalAt(footprint, "x", cx - girderWidth * 0.5, intervalA) &&
                    terrainContourAxisIntervalAt(footprint, "x", cx + girderWidth * 0.5, intervalB))
                {
                    girderZ0 = Math.max(intervalA.minimum, intervalB.minimum);
                    girderZ1 = Math.min(intervalA.maximum, intervalB.maximum);
                }
                if (portal.n) girderZ0 = Math.max(girderZ0, z0 + portalClearance);
                if (portal.s) girderZ1 = Math.min(girderZ1, z1 - portalClearance);
                if (girderZ1 > girderZ0 + 0.02)
                    propBox(
                        builder,
                        cx - girderWidth * 0.5,
                        cx + girderWidth * 0.5,
                        girderZ0,
                        girderZ1,
                        girderBottom,
                        girderTop,
                        timberTop,
                        timberSide,
                        0.72,
                        true,
                        SURF.bridge,
                        PROP_BOX_SIDE_WEST | PROP_BOX_SIDE_EAST);
            }
        }
        else
        {
            foreach (double cz in new[] { z0 + girderInset, z1 - girderInset })
            {
                if ((portal.n && cz < (z0 + z1) * 0.5) || (portal.s && cz > (z0 + z1) * 0.5)) continue;
                double girderX0 = x0;
                double girderX1 = x1;
                if (
                    footprint != null &&
                    terrainContourAxisIntervalAt(footprint, "z", cz - girderWidth * 0.5, intervalA) &&
                    terrainContourAxisIntervalAt(footprint, "z", cz + girderWidth * 0.5, intervalB))
                {
                    girderX0 = Math.max(intervalA.minimum, intervalB.minimum);
                    girderX1 = Math.min(intervalA.maximum, intervalB.maximum);
                }
                if (portal.w) girderX0 = Math.max(girderX0, x0 + portalClearance);
                if (portal.e) girderX1 = Math.min(girderX1, x1 - portalClearance);
                if (girderX1 > girderX0 + 0.02)
                    propBox(
                        builder,
                        girderX0,
                        girderX1,
                        cz - girderWidth * 0.5,
                        cz + girderWidth * 0.5,
                        girderBottom,
                        girderTop,
                        timberTop,
                        timberSide,
                        0.72,
                        true,
                        SURF.bridge,
                        PROP_BOX_SIDE_NORTH | PROP_BOX_SIDE_SOUTH);
            }
        }
        builder.bridgeStructuralCells++;

        int travelDx = travelNorthSouth ? 0 : 1;
        int travelDy = travelNorthSouth ? 1 : 0;
        TerrainCell? before = terrainCellAt(terrain, cell.x - travelDx, cell.y - travelDy);
        TerrainCell? after = terrainCellAt(terrain, cell.x + travelDx, cell.y + travelDy);
        double abutmentLength = structure.abutmentLengthCells * ts;
        this.addBridgeAbutment(
            builder,
            before,
            true,
            travelNorthSouth,
            x0,
            x1,
            z0,
            z1,
            capY,
            ts,
            girderWidth,
            abutmentLength,
            material);
        this.addBridgeAbutment(
            builder,
            after,
            false,
            travelNorthSouth,
            x0,
            x1,
            z0,
            z1,
            capY,
            ts,
            girderWidth,
            abutmentLength,
            material);

        int worldTravel = travelNorthSouth ? frame.j0 + cell.y : frame.i0 + cell.x;
        // JS `%` on numbers (pierPeriodCells is a double constant): the int converts, C# double `%` is fmod like JS.
        double pierSlot =
            ((worldTravel % structure.pierPeriodCells) + structure.pierPeriodCells) %
            structure.pierPeriodCells;
        if (cell.span != TileType.Water || pierSlot != 1) return;
        double pierBottom = (cell.waterLevel ?? cell.elevation - 1) * ELEV - ELEV * 0.72;
        double pierTop = girderBottom - 0.7;
        if (pierTop <= pierBottom + 1) return;
        double pierWidth = structure.pierWidthCells * ts;
        int pierTopColor = mix(
            material.edgeLight,
            this.tileset?.terrain.wallLit ?? material.top,
            0.2);
        int pierSideColor = mix(
            material.side,
            this.tileset?.terrain.wallDeep ?? material.edgeDark,
            0.4);
        if (travelNorthSouth)
        {
            foreach (double cx in new[] { x0 + girderInset, x1 - girderInset })
                propBox(
                    builder,
                    cx - pierWidth * 0.5,
                    cx + pierWidth * 0.5,
                    z0 + ts * 0.5 - pierWidth * 0.55,
                    z0 + ts * 0.5 + pierWidth * 0.55,
                    pierBottom,
                    pierTop,
                    pierTopColor,
                    pierSideColor,
                    0.62,
                    true,
                    SURF.bridge);
        }
        else
        {
            foreach (double cz in new[] { z0 + girderInset, z1 - girderInset })
                propBox(
                    builder,
                    x0 + ts * 0.5 - pierWidth * 0.55,
                    x0 + ts * 0.5 + pierWidth * 0.55,
                    cz - pierWidth * 0.5,
                    cz + pierWidth * 0.5,
                    pierBottom,
                    pierTop,
                    pierTopColor,
                    pierSideColor,
                    0.62,
                    true,
                    SURF.bridge);
        }
        builder.bridgePiers++;
    }

    internal void addBridgeAbutment(
        TileGeometryBuilder builder,
        TerrainCell? landing,
        bool atStart,
        bool travelNorthSouth,
        double x0,
        double x1,
        double z0,
        double z1,
        double capY,
        double ts,
        double girderWidth,
        double abutmentLength,
        TerrainMaterial material)
    {
        // Abutments belong to dry landings. Water, Chasm and the exterior remain open below the deck.
        if (landing == null || landing.type == TileType.Bridge || terrainBridgeOpenSpanContact(landing.type))
            return;
        double landingY = landing.surfaceZ * ELEV;
        // An abutment is a shallow deck bearing, not a column from an arbitrary landing datum up to the bridge.
        // Taking the lower of these levels stretched a tiny endpoint block through whole Chasm/terrain drops; only
        // triangular corners of those enormous boxes remained visible behind the cliff. Clamp it to deck depth and
        // let the landing's own geological volume own everything below.
        double baseY = Math.max(capY - 4.5, landingY - 0.4);
        if (baseY >= capY - 0.37) return;
        int stoneTop = mix(material.top, this.tileset?.terrain.wallLit ?? material.edgeLight, 0.24);
        int stoneSide = mix(material.side, this.tileset?.terrain.wallDeep ?? material.edgeDark, 0.28);
        if (travelNorthSouth)
        {
            double za = atStart ? z0 : z1 - abutmentLength;
            propBox(
                builder,
                x0 + girderWidth * 0.4,
                x1 - girderWidth * 0.4,
                za,
                za + abutmentLength,
                baseY,
                capY - 0.35,
                stoneTop,
                stoneSide,
                0.74,
                true,
                SURF.rockFace);
            builder.addContactBlob(
                (x0 + x1) * 0.5,
                atStart ? z0 - ts * 0.08 : z1 + ts * 0.08,
                landingY + 0.06,
                ts * 0.34,
                ts * 0.13,
                stoneSide,
                0.16);
        }
        else
        {
            double xa = atStart ? x0 : x1 - abutmentLength;
            propBox(
                builder,
                xa,
                xa + abutmentLength,
                z0 + girderWidth * 0.4,
                z1 - girderWidth * 0.4,
                baseY,
                capY - 0.35,
                stoneTop,
                stoneSide,
                0.74,
                true,
                SURF.rockFace);
            builder.addContactBlob(
                atStart ? x0 - ts * 0.08 : x1 + ts * 0.08,
                (z0 + z1) * 0.5,
                landingY + 0.06,
                ts * 0.18,
                ts * 0.28,
                stoneSide,
                0.16);
        }
        builder.bridgeAbutments++;
    }

    internal void addClockworkBridgeDeckDetail(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        bool waterEW)
    {
        if (this.tileset == null) return;
        double ts = this.ts;
        double y = capY + 0.12;
        int brass = mix(this.tileset.decal.mid, this.tileset.bridge.railLit, 0.2);
        int brassSide = mix(this.tileset.bridge.bodyShadow, this.tileset.terrain.wallDeep, 0.24);
        int teal = this.tileset.decal.accent;
        int ink = material.edgeDark;
        void addPost(double px, double pz)
        {
            propBox(
                builder,
                px - 1.25,
                px + 1.25,
                pz - 1.25,
                pz + 1.25,
                capY + 0.3,
                capY + 5.1,
                brass,
                brassSide,
                0.76);
        }

        if (waterEW)
        {
            builder.addOverlayLineFlat(
                y,
                x0 + ts * 0.5,
                z0 + 5.5,
                x0 + ts * 0.5,
                z0 + ts - 5.5,
                0.95,
                teal,
                0.34);
            foreach (double rx in new[] { x0 + 5.4, x0 + ts - 5.4 })
            {
                builder.addOverlayLineFlat(y + 0.05, rx, z0 + 6, rx, z0 + ts - 6, 1.05, brass, 0.52);
                foreach (double pz in new[] { z0 + 8, z0 + ts * 0.5, z0 + ts - 8 }) addPost(rx, pz);
            }
        }
        else
        {
            builder.addOverlayLineFlat(
                y,
                x0 + 5.5,
                z0 + ts * 0.5,
                x0 + ts - 5.5,
                z0 + ts * 0.5,
                0.95,
                teal,
                0.34);
            foreach (double rz in new[] { z0 + 5.4, z0 + ts - 5.4 })
            {
                builder.addOverlayLineFlat(y + 0.05, x0 + 6, rz, x0 + ts - 6, rz, 1.05, brass, 0.52);
                foreach (double px in new[] { x0 + 8, x0 + ts * 0.5, x0 + ts - 8 }) addPost(px, rz);
            }
        }

        for (int i = 0; i < 4; i++)
        {
            double h = hash(cell.id * 1901 + i * 31);
            double t = 0.22 + i * 0.18 + (h - 0.5) * 0.035;
            if (waterEW)
            {
                double zz = z0 + ts * t;
                builder.addOverlayLineFlat(
                    y + 0.03,
                    x0 + ts * 0.36,
                    zz,
                    x0 + ts * 0.64,
                    zz + (h - 0.5) * 2.5,
                    0.58,
                    i % 2 != 0 ? teal : ink,
                    0.2);
            }
            else
            {
                double xx = x0 + ts * t;
                builder.addOverlayLineFlat(
                    y + 0.03,
                    xx,
                    z0 + ts * 0.36,
                    xx + (h - 0.5) * 2.5,
                    z0 + ts * 0.64,
                    0.58,
                    i % 2 != 0 ? teal : ink,
                    0.2);
            }
        }
    }

    internal void addPrismglassBridgeDeckDetail(
        TileGeometryBuilder builder,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        bool waterEW)
    {
        if (this.tileset == null) return;
        double ts = this.ts;
        double y = capY + 0.13;
        int pane = mix(material.edgeLight, this.tileset.flood.surface, 0.38);
        int rail = mix(this.tileset.flood.foam, this.tileset.decal.accent, 0.22);
        int side = mix(this.tileset.bridge.bodyShadow, this.tileset.flood.deep, 0.28);
        int glint = this.tileset.flood.glint;
        builder.addOverlay(
            quad(
                x0 + 5,
                y,
                z0 + 6,
                x0 + ts - 5,
                y,
                z0 + 6,
                x0 + ts - 5,
                y,
                z0 + ts - 6,
                x0 + 5,
                y,
                z0 + ts - 6),
            pane,
            0.18);
        builder.addOverlayLineFlat(
            y + 0.04,
            x0 + 7,
            z0 + 8,
            x0 + ts - 7,
            z0 + 8,
            0.52,
            this.tileset.flood.foam,
            0.26);
        builder.addOverlayLineFlat(
            y + 0.04,
            x0 + 7,
            z0 + ts - 8,
            x0 + ts - 7,
            z0 + ts - 8,
            0.52,
            glint,
            0.22);

        void addPost(double px, double pz)
        {
            propBox(
                builder,
                px - 1.1,
                px + 1.1,
                pz - 1.1,
                pz + 1.1,
                capY + 0.5,
                capY + 4.5,
                rail,
                side,
                0.78);
        }

        if (waterEW)
        {
            builder.addOverlayLineFlat(
                y,
                x0 + ts * 0.5,
                z0 + 6,
                x0 + ts * 0.5,
                z0 + ts - 6,
                0.72,
                glint,
                0.28);
            foreach (double rx in new[] { x0 + ts * 0.2, x0 + ts * 0.8 })
            {
                builder.addOverlayLineFlat(y + 0.05, rx, z0 + 6, rx, z0 + ts - 6, 1.05, rail, 0.5);
                foreach (double pz in new[] { z0 + ts * 0.22, z0 + ts * 0.5, z0 + ts * 0.78 }) addPost(rx, pz);
            }
            for (int i = 0; i < 3; i++)
            {
                double zz = z0 + ts * (0.25 + i * 0.2);
                builder.addOverlayLineFlat(
                    y + 0.03,
                    x0 + ts * 0.34,
                    zz - 2,
                    x0 + ts * 0.66,
                    zz + 2.5,
                    0.54,
                    i % 2 != 0 ? glint : this.tileset.flood.foam,
                    0.24);
            }
        }
        else
        {
            builder.addOverlayLineFlat(
                y,
                x0 + 6,
                z0 + ts * 0.5,
                x0 + ts - 6,
                z0 + ts * 0.5,
                0.72,
                glint,
                0.28);
            foreach (double rz in new[] { z0 + ts * 0.2, z0 + ts * 0.8 })
            {
                builder.addOverlayLineFlat(y + 0.05, x0 + 6, rz, x0 + ts - 6, rz, 1.05, rail, 0.5);
                foreach (double px in new[] { x0 + ts * 0.22, x0 + ts * 0.5, x0 + ts * 0.78 }) addPost(px, rz);
            }
            for (int i = 0; i < 3; i++)
            {
                double xx = x0 + ts * (0.25 + i * 0.2);
                builder.addOverlayLineFlat(
                    y + 0.03,
                    xx - 2,
                    z0 + ts * 0.34,
                    xx + 2.5,
                    z0 + ts * 0.66,
                    0.54,
                    i % 2 != 0 ? glint : this.tileset.flood.foam,
                    0.24);
            }
        }
    }

    /* ── Cell material resolution (biome-tinted; same derivation the 2D painter used) ───────────────────── */

    /// <summary>
    /// Biome colour coding, one derivation for every cell kind: floors climb the biome's ELEVATION ramp (low =
    /// lighter valley, high = darker plateau — the gameplay value code), walls wear the biome's stone ramp with
    /// height toning, water carries the biome's storm/flood hues, bridges the biome's timber. Weights are kept
    /// moderate so the shared "one world" chalk-paper base still binds all biomes together.
    /// </summary>
    /// <param name="cachedMoisture">`number | undefined`; null = not supplied.</param>
    internal TerrainMaterial materialForCell(
        TerrainCell cell,
        MaterializedTerrain terrain,
        double? cachedMoisture = null)
    {
        double moisture = cachedMoisture ?? this.moistureHint(cell, terrain);
        PaintedThemeVisual? paintedTheme = this.paintedThemeVisualForCell(cell);
        if (paintedTheme != null) return paintedTheme.materialForCell(cell, moisture);
        if (this.runMaterialForCell == null)
            throw new InvalidOperationException("TerrainGeometryCompiler biome was not configured");
        return this.runMaterialForCell(cell, moisture);
    }

    /// <summary>
    /// Canonical same-height cap pigment at one logical corner.
    ///
    /// Main caps and the small polygons that close clipped wall footprints must resolve this value through the
    /// same neighbourhood. If a helper polygon falls back to its owning cell's material, its three vertices no
    /// longer agree with the surrounding cap field and raster interpolation reveals its triangular topology.
    /// </summary>
    internal int compatibleCapColorAt(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        int cornerX,
        int cornerY,
        bool isRock,
        int fallback)
    {
        int color = fallback;
        int totalWeight = 0;
        // [1, 3, 3, 1] is the smallest symmetric binomial footprint that suppresses the visible first-derivative
        // kink of a four-cell rock-corner average. Floors use the four cells that genuinely meet at the corner.
        int radiusMin = isRock ? -2 : -1;
        int radiusMax = isRock ? 1 : 0;
        for (int oy = radiusMin; oy <= radiusMax; oy++)
        {
            for (int ox = radiusMin; ox <= radiusMax; ox++)
            {
                TerrainCell? candidate = terrainCellAt(terrain, cornerX + ox, cornerY + oy);
                if (
                    candidate == null ||
                    candidate.type != cell.type ||
                    Math.abs(candidate.surfaceZ - cell.surfaceZ) > 0.001
                )
                    continue;
                // `plan.materials[candidate.id] ?? …`: an index past the array reads undefined in JS.
                TerrainMaterial candidateMaterial =
                    this.biome?.materialDialect == "aegis-citadel"
                        ? this.materialForCell(candidate, terrain)
                        : (((uint)candidate.id < (uint)plan.materials.Length ? plan.materials[candidate.id] : null) ??
                           this.materialForCell(candidate, terrain));
                int candidateColor = terrainMaterialSurfaceTopColor(candidateMaterial, candidate);
                int weightX = isRock && (ox == -1 || ox == 0) ? 3 : 1;
                int weightY = isRock && (oy == -1 || oy == 0) ? 3 : 1;
                int weight = weightX * weightY;
                totalWeight += weight;
                color =
                    totalWeight == weight
                        ? candidateColor
                        : mix(color, candidateColor, (double)weight / totalWeight);
            }
        }
        return color;
    }

    internal void syncGroundDetail(MaterializedTerrain terrain, TerrainBakeFrame frame)
    {
        // Fluitown comic look: the relief mask belongs to the bake it was built for (TerrainGeometryCompiler.Relief.cs).
        this.resetGroundRelief(terrain);
        syncGroundDetailContext(this.groundDetail, frame, terrain, this);
    }

    /// <summary>
    /// Crest colours for the two inner and two outer vertices of a cap that faces away from the camera.
    ///
    /// A Run cell has one flat material cap, so all four vertices start from `fallbackCap`. A Hub floor cell
    /// instead samples the blended district field per vertex. Without a Hub source — every terrain worker — the
    /// per-vertex sample is skipped and both inner vertices resolve to the same value, which is byte-for-byte
    /// what the Run-only form always produced.
    /// </summary>
    /// <returns>The shared <c>CAMERA_AWAY_CREST_COLOR_SCRATCH</c> (TS `readonly number[]`), consumed synchronously.</returns>
    internal double[] cameraAwayCrestColors(
        IReadOnlyList<P3> _points,
        TerrainCell cell,
        int fallbackCap,
        int edgeDark,
        bool topRim = false,
        double? topRimOuterMix2 = null,
        double? topRimOuterMix3 = null)
    {
        var style = CARTOON_TERRAIN_STYLE.cameraAwayCrest;
        double[] scratch = CAMERA_AWAY_CREST_COLOR_SCRATCH;
        for (int index = 0; index < 4; index++)
        {
            int cap = fallbackCap;
            double? topRimOuterMix =
                index == 2 ? topRimOuterMix2 : index == 3 ? topRimOuterMix3 : null;
            // Fluitown comic look: the world ink draws the crest's contour. The original's painted top rim — a dark band
            // on the cap, set back from the edge by the bevel — read as a trench beside every north-facing terrace edge
            // and shore; it keeps only a trace of the edge colour.
            scratch[index] = topRim && TerrainComicGeometry.ClosedShells
                ? mix(cap, edgeDark, 0.05)
                : mix(
                cap,
                edgeDark,
                index < 2
                    ? topRim
                        ? style.topRimInnerDarkMix
                        : style.innerEdgeDarkMix
                    : topRim
                        ? (topRimOuterMix ?? style.topRimOuterDarkMix)
                        : style.outerEdgeDarkMix);
        }
        return scratch;
    }

    /// <summary>Resolve one explicit cell theme without mutating the world's base biome/light/atmosphere.</summary>
    /// <summary>
    /// The ravine palette a cell must read.
    ///
    /// A chasm is theme-bearing terrain like any other cell: when an author paints a theme over a rift, the rift's
    /// own ledges, mist and cascade impact have to follow that theme, not the world's base biome. Everything the
    /// negative volume draws therefore resolves its palette through here instead of reading `this.tileset` — that
    /// read is what left painted chasms wearing the base world's ravine.
    /// </summary>
    /// <returns>`TerrainTileset['chasm'] | undefined`.</returns>
    internal TerrainTilesetChasm? chasmPaletteForCell(TerrainCell? cell)
    {
        return (
            (cell != null ? this.paintedThemeVisualForCell(cell)?.tileset.chasm : null) ??
            this.tileset?.chasm
        );
    }

    internal PaintedThemeVisual? paintedThemeVisualForCell(TerrainCell cell)
    {
        // `this.themeBuf[cell.id]` reads undefined outside the layer (an empty layer: every cell).
        if ((uint)cell.id >= (uint)this.themeBuf.Length) return null;
        int paletteIndex = this.themeBuf[cell.id];
        if (paletteIndex == TERRAIN_THEME_INHERIT) return null;
        // `this.bakeThemePalette[paletteIndex]` reads undefined past the palette.
        string? themeKey =
            (uint)paletteIndex < (uint)this.bakeThemePalette.Count ? this.bakeThemePalette[paletteIndex] : null;
        return this.themeVisualForKey(themeKey);
    }

    internal PaintedThemeVisual? themeVisualForKey(string? themeKey)
    {
        if (string.IsNullOrEmpty(themeKey)) return null;
        this.paintedThemeVisualCache.TryGetValue(themeKey, out PaintedThemeVisual? visual);
        if (visual == null)
        {
            this.themeVisualCatalog.TryGetValue(themeKey, out TerrainCompilerThemeVisual? theme);
            if (theme == null) return null;
            var tileset = theme.tileset;
            var style = theme.style;
            visual = new PaintedThemeVisual
            {
                tileset = tileset,
                style = style,
                materialForCell = createRunTerrainMaterialResolver(tileset, style),
            };
            this.paintedThemeVisualCache[themeKey] = visual;
        }
        return visual;
    }

    internal double moistureHint(TerrainCell cell, MaterializedTerrain terrain)
    {
        double score = 0;
        for (int y = -2; y <= 2; y++)
        {
            for (int x = -2; x <= 2; x++)
            {
                if (x == 0 && y == 0) continue;
                int distance = Math.abs(x) + Math.abs(y);
                if (distance > 3) continue;
                TerrainCell? n = terrainCellAt(terrain, cell.x + x, cell.y + y);
                if (n?.type == TileType.Water || n?.span == TileType.Water)
                    score += distance == 1 ? 0.24 : 0.09;
            }
        }
        return clamp01(score);
    }
}

public static partial class TerrainGeometryCompilerModule
{
    /// <summary>The shared prop hash — the geometry modules this file delegates to must sample the exact same one.</summary>
    internal static double hash(double id) => propHash(id);

    /* ── World dressing: the shared domain says WHAT stands WHERE; `treeGeometry`/`worldDecorationGeometry` how. */
}
