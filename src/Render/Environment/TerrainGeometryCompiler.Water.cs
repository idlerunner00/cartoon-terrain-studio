// Port of packages/client/src/render/environment/terrainGeometryCompiler.ts — keep in lockstep with the original.
//
// PART C5 (TS lines 11978–15196): `buildWaterCell` (the water surface/basin compiler), the theme water
// reflections/slicks (`addPrismglassWaterReflections`, `addCityWaterSlicks`, `addClockworkWaterMercury`,
// `addCarnivalSyrupSlicks`), `addAoPatch`, `addMicroDetail`, `addFloorInlay` and `addWallCapDetail`. Instance
// fields, the builder and the module-level helpers live in TerrainGeometryCompiler.cs (C1).
//
// PORT NOTES (C5)
// * The closures of `buildWaterCell` are C# local functions in the original order. Object-literal records become
//   value tuples with the TS field names (`fluid`, `normalFloorEdge`, `normalDryWallEdge`, `cornerWater(...)`);
//   `fluid[direction]` is `fluidAt(direction)`. The anonymous `{ cell, material, top }` shore record is the nested
//   `WaterDryShore`. Default parameters that are not constants (`normalX = WATER_TRANSITION_NORMAL_X4`,
//   `worldX = x0 + u * ts`) are nullable parameters resolved with `??`.
// * `{ ...waterCell, type: TileType.Water }` is `TerrainCell.Clone()` + assignment.
// * Hash seeds such as `cell.id * 2179 + 7` or `(frame.i0 + cell.x) * 941` stay `int` arithmetic: `hash`/`propHash`,
//   `cellHash`, `cityNeonColor` and `cathedralStarfire` all read their argument through ToInt32 first, and integer
//   `+`/`*` wrapping modulo 2^32 is exactly ToInt32 of the (exact) JS double result.
// * Colours are `int` (0xRRGGBB); arrays of colours handed to the builder are the `double[]` scratch lanes.
// * `import { X as Y }` aliases are called by their original names: INK_WORLD_LINE →
//   TERRAIN_GEOMETRY_INK_WORLD_LINE, SUGARSTORM_CARNIVAL_COLORS → TERRAIN_GEOMETRY_CARNIVAL_COLORS,
//   RAINBOW_PRISM_COLORS → TERRAIN_GEOMETRY_RAINBOW_COLORS. `hash` is the module-level `const hash = propHash`
//   (ported with the module tail, part C6).
// * `addAoPatch`/`addMicroDetail` declare their `patch`/`detail` parameters with inline structural types in TS;
//   the plan records they are shaped after are TerrainAmbientOcclusionPatch / TerrainMicroDetail.
// * `WATER_BASIN_DEPTH` is qualified (TerrainGeometryCompilerModule.WATER_BASIN_DEPTH): TerrainModel exports a
//   constant of the same name, which the TS module does not import.
// * Verified differentially against the original private methods (fake `this` + recording builder, domain terrain
//   and render plan from both ports): 39,333 calls, 0 mismatches (see the C5 agent report).
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainRenderPlanModule;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainBakePigment;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerInk;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainGeometryCompilerTheme;
using static Fluitown.Render.TerrainLiquidChasmContour;
using static Fluitown.Render.TerrainWaterGeometryScratch;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed partial class TerrainGeometryCompiler
{
    /// <summary>`{ cell: TerrainCell; material: TerrainMaterial; top: number }` — a dry neighbour of a Water cell.</summary>
    private sealed class WaterDryShore
    {
        public TerrainCell cell = null!;
        public TerrainMaterial material = null!;
        public int top;
    }

    /// <summary>The 3×3 relief probe of <see cref="buildWaterCell"/> (TS: two array literals per cell). Never written.</summary>
    private static readonly double[] WATER_RELIEF_UV = { 0, 0.5, 1 };

    internal void buildWaterCell(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell)
    {
        TerrainMaterial exposedWaterMaterialFor(TerrainCell waterCell)
        {
            // A Bridge is a carrier, not a second kind of liquid. Resolve its appearance through the exact Water
            // material pipeline at the carrier's own theme/moisture slot. Falling back to STANDARD water when the
            // exposed owner sits outside a worker crop made the complete under-deck patch grey and revealed every
            // tile boundary even though height and UVs were already continuous.
            TerrainCell appearanceCell;
            if (waterCell.type == TileType.Water)
            {
                appearanceCell = waterCell;
            }
            else
            {
                appearanceCell = waterCell.Clone();
                appearanceCell.type = TileType.Water;
            }
            TerrainMaterial? planned = waterCell.type == TileType.Water ? plan.materials[waterCell.id] : null;
            return this.biome?.materialDialect == "aegis-citadel"
                ? this.materialForCell(appearanceCell, terrain, plan.moisture[waterCell.id])
                : planned?.id == "water"
                    ? planned!
                    : this.materialForCell(appearanceCell, terrain, plan.moisture[waterCell.id]);
        }
        TerrainMaterial water = exposedWaterMaterialFor(cell);
        // Pigment/depth come from the exposed patch, but the shared model's Bridge datum is the physical authority:
        // it is capped below the deck underside. Copying the neighbour's height here could submerge the timber cap.
        double sourceWaterLevel = cell.waterLevel ?? cell.surfaceZ;
        double ts = frame.tileSize;
        int hydraulicSegments =
            this.vegetationBladeCap <= 0 ? 1 : (int)CARTOON_TERRAIN_STYLE.waterShore.transitionSegments;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double z1 = z0 + ts;
        double visualCenterLevel = visualWaterLevelAt(
            terrain,
            cell.x + 0.5,
            cell.y + 0.5,
            sourceWaterLevel);
        double waterZ = visualCenterLevel * ELEV;
        double basinY = waterZ - TerrainGeometryCompilerModule.WATER_BASIN_DEPTH * ELEV;
        double visualWaterLevelAtUv(double u, double v) =>
            visualWaterLevelAt(terrain, cell.x + u, cell.y + v, visualCenterLevel);
        double visualWaterYAtUv(double u, double v) => visualWaterLevelAtUv(u, v) * ELEV;
        void visualWaterNormalAtUv(
            double u,
            double v,
            int index,
            double[]? normalX = null,
            double[]? normalY = null,
            double[]? normalZ = null) =>
            visualWaterNormalAt(
                terrain,
                cell.x + u,
                cell.y + v,
                visualCenterLevel,
                ELEV,
                ts,
                index,
                normalX ?? WATER_TRANSITION_NORMAL_X4,
                normalY ?? WATER_TRANSITION_NORMAL_Y4,
                normalZ ?? WATER_TRANSITION_NORMAL_Z4);
        TerrainWaterInfo? info = plan.water[cell.id];
        double depth = info?.depth ?? 0.4;
        int basinColor = mix(water.deep ?? water.topDark, TERRAIN_GEOMETRY_INK_WORLD_LINE, 0.25);
        int surfaceColor = mix(water.mid ?? water.top, water.shallow ?? water.topLight, 0.15);

        // Animated water surface. Every cell ends on the exact shared grid line: an inset would open pinholes at
        // concave banks, while the former 0.7 px overlap produced coplanar z-fighting and made the tile lattice
        // visible. Shore depth/foam are still smooth per-corner fields; all motion is world-anchored in the shader.
        TerrainCell? continuousWaterCellAt(int tx, int ty)
        {
            TerrainCell? neighbor = terrainCellAt(terrain, tx, ty);
            return neighbor != null &&
                terrainCellCarriesWater(neighbor) &&
                neighbor.waterLevel != null &&
                (neighbor.type != TileType.Bridge || bridgeWaterIsSafelyBelowDeck(neighbor))
                ? neighbor
                : null;
        }
        int waterSurfaceColorForCell(TerrainCell sample)
        {
            TerrainMaterial sampleWater = exposedWaterMaterialFor(sample);
            return mix(
                sampleWater.mid ?? sampleWater.top,
                sampleWater.shallow ?? sampleWater.topLight,
                0.15);
        }
        int cornerWaterColor(int cornerX, int cornerY)
        {
            int color = surfaceColor;
            double totalWeight = 0;
            // A four-cell corner average is continuous but still leaves one bilinear pigment patch per tile. Sample
            // a symmetric four-by-four footprint so material dialects dissolve over a broad water mass instead of
            // exposing their 40 px ownership grid. The absolute corner makes this bit-identical for every owner.
            for (int oy = -2; oy <= 1; oy++)
            {
                for (int ox = -2; ox <= 1; ox++)
                {
                    TerrainCell? sample = continuousWaterCellAt(cornerX + ox, cornerY + oy);
                    if (sample == null) continue;
                    double weightX = Math.abs(ox + 0.5) < 1 ? 0.82 : 0.18;
                    double weightY = Math.abs(oy + 0.5) < 1 ? 0.82 : 0.18;
                    double weight = weightX * weightY;
                    int sampleColor = waterSurfaceColorForCell(sample);
                    color =
                        totalWeight == 0
                            ? sampleColor
                            : mix(color, sampleColor, weight / (totalWeight + weight));
                    totalWeight += weight;
                }
            }
            return color;
        }
        (bool n, bool e, bool s, bool w) fluid = (
            continuousWaterCellAt(cell.x, cell.y - 1) != null,
            continuousWaterCellAt(cell.x + 1, cell.y) != null,
            continuousWaterCellAt(cell.x, cell.y + 1) != null,
            continuousWaterCellAt(cell.x - 1, cell.y) != null);
        bool fluidAt(string direction) =>
            direction == "n" ? fluid.n : direction == "e" ? fluid.e : direction == "s" ? fluid.s : fluid.w;
        // A scenic cascade owns the complete liquid cross-section at its crest. Leaving the shallow basin shell in
        // front of it inserted a dark horizontal bar between the source surface and the animated falling water.
        bool hasVisibleWaterfall(string direction) =>
            terrainWaterfallPortalForEdge(plan.waterfalls, cell.id, direction) != null;
        bool opensWaterfallEdge(string direction) =>
            hasVisibleWaterfall(direction);
        double waterfallMouthWeight(double u, double v) =>
            Math.max(
                opensWaterfallEdge("n") ? 1 - clamp(v * 8, 0, 1) : 0,
                opensWaterfallEdge("e") ? 1 - clamp((1 - u) * 8, 0, 1) : 0,
                opensWaterfallEdge("s") ? 1 - clamp((1 - v) * 8, 0, 1) : 0,
                opensWaterfallEdge("w") ? 1 - clamp(u * 8, 0, 1) : 0);

        // Ordinary Floor shores and Chasm lips share one regular grid topology. Chasm contacts only deform that
        // grid's XZ positions with a world-continuous field. This keeps the silhouette organic while preventing the
        // long, payload-interpolating triangle fans which previously showed up as geometric wedges in the water.
        LiquidChasmContour liquidContour = liquidChasmContourForCell(
            terrain,
            cell,
            frame.i0 + cell.x,
            frame.j0 + cell.y,
            this.coherentContours,
            this.liquidChasmContourScratch);
        double contourNw = liquidContour.nw;
        double contourNe = liquidContour.ne;
        double contourSe = liquidContour.se;
        double contourSw = liquidContour.sw;
        bool hasChasmBoundary =
            liquidContour.chasmN || liquidContour.chasmE || liquidContour.chasmS || liquidContour.chasmW;
        WaterDryShore? dryShoreFor(string direction)
        {
            TerrainCell? neighbor = terrainCellAt(
                terrain,
                cell.x + (direction == "e" ? 1 : direction == "w" ? -1 : 0),
                cell.y + (direction == "s" ? 1 : direction == "n" ? -1 : 0));
            if (neighbor == null || neighbor.type == TileType.Chasm || terrainCellCarriesWater(neighbor))
                return null;
            TerrainMaterial material = this.materialForCell(neighbor, terrain);
            return new WaterDryShore
            {
                cell = neighbor,
                material = material,
                top = terrainMaterialSurfaceTopColor(material, neighbor),
            };
        }
        WaterDryShore? normalFloorEdgeFor(string direction)
        {
            if (fluidAt(direction) || opensWaterfallEdge(direction)) return null;
            WaterDryShore? shore = dryShoreFor(direction);
            return shore?.cell.type == TileType.Floor ? shore : null;
        }
        WaterDryShore? normalDryWallEdgeFor(string direction)
        {
            if (fluidAt(direction) || opensWaterfallEdge(direction)) return null;
            WaterDryShore? shore = dryShoreFor(direction);
            return shore?.cell.type == TileType.Floor || shore?.cell.type == TileType.Solid
                ? shore
                : null;
        }
        (WaterDryShore? n, WaterDryShore? e, WaterDryShore? s, WaterDryShore? w) normalFloorEdge = (
            normalFloorEdgeFor("n"),
            normalFloorEdgeFor("e"),
            normalFloorEdgeFor("s"),
            normalFloorEdgeFor("w"));
        // Floor and Solid both publish their real, topology-contoured wall down to the basin floor. The basin's
        // generic straight shell must therefore stand down at either dry wall family; drawing it as well would put
        // a second rectangular card in front of the canonical earth/rock face at precisely the mixed corners this
        // compiler is required to keep seamless.
        (WaterDryShore? n, WaterDryShore? e, WaterDryShore? s, WaterDryShore? w) normalDryWallEdge = (
            normalDryWallEdgeFor("n"),
            normalDryWallEdgeFor("e"),
            normalDryWallEdgeFor("s"),
            normalDryWallEdgeFor("w"));
        double normalFloorEdgeWeight(double u, double v) =>
            Math.max(
                normalFloorEdge.n != null ? 1 - clamp(v * 8, 0, 1) : 0,
                normalFloorEdge.e != null ? 1 - clamp((1 - u) * 8, 0, 1) : 0,
                normalFloorEdge.s != null ? 1 - clamp((1 - v) * 8, 0, 1) : 0,
                normalFloorEdge.w != null ? 1 - clamp(u * 8, 0, 1) : 0);
        // The canonical Floor pigment this dissolve gathers lives on `waterFloorPigmentAt`, shared with the
        // seamless corner fill. A second copy here was the reason a rounded dry corner and the pool beside it could
        // paint the same bank two ways.
        // The bank dissolve at one point of this cell's liquid, in the ONE shared implementation
        // (terrainShoreDissolveAt). Output is [groundBlend, canonicalFloorPigment].
        void shoreTransitionAt(double worldX, double worldZ, double[] @out)
        {
            @out[0] = 0;
            @out[1] = surfaceColor;
            this.waterGroundTransitionAt(
                terrain,
                plan,
                cell.x + (worldX - x0) / ts,
                cell.y + (worldZ - z0) / ts,
                worldX,
                worldZ,
                waterZ,
                surfaceColor,
                @out);
        }
        // A support floor is useful below an enclosed pool, but any open rapid/curtain owns its complete liquid
        // cross-section. The support is emitted from the SAME hydraulic lattice as the visible surface below; a
        // flat cell-centre quad can rise above a strongly sloped edge and become the large opaque triangle that was
        // visible in authored stepped-water maps.
        bool hasBasinSupport =
            !opensWaterfallEdge("n") &&
            !opensWaterfallEdge("e") &&
            !opensWaterfallEdge("s") &&
            !opensWaterfallEdge("w");
        double basinSupportOffset = TerrainGeometryCompilerModule.WATER_BASIN_DEPTH * ELEV;
        // The model owns the datum. Averaging neighbouring corners turned a terrace into four unrelated slopes and
        // exposed backing geometry below it. A cell is a flat water plane; an explicit transition owns every height
        // change, so source plane, falling sheet and target plane meet on the exact same edge coordinates.
        P3[] pts = quadInto(
            WATER_SURFACE_QUAD,
            x0,
            waterZ,
            z0,
            x1,
            waterZ,
            z0,
            x1,
            waterZ,
            z1,
            x0,
            waterZ,
            z1);
        foreach (P3 point in pts)
        {
            double u = clamp((point.x - x0) / ts, 0, 1);
            double v = clamp((point.z - z0) / ts, 0, 1);
            point.y = visualWaterYAtUv(u, v);
        }
        (double depth, double shore, int color) cornerWater(int cornerX, int cornerY)
        {
            int immediateCount = 0;
            for (int oy = -1; oy <= 0; oy++)
            {
                for (int ox = -1; ox <= 0; ox++)
                {
                    TerrainCell? sample = continuousWaterCellAt(cornerX + ox, cornerY + oy);
                    if (sample == null) continue;
                    immediateCount++;
                }
            }
            double depthSum = 0;
            double depthWeight = 0;
            for (int oy = -2; oy <= 1; oy++)
            {
                for (int ox = -2; ox <= 1; ox++)
                {
                    TerrainCell? sample = continuousWaterCellAt(cornerX + ox, cornerY + oy);
                    if (sample == null) continue;
                    double weightX = Math.abs(ox + 0.5) < 1 ? 0.82 : 0.18;
                    double weightY = Math.abs(oy + 0.5) < 1 ? 0.82 : 0.18;
                    double weight = weightX * weightY;
                    // Render-plan hydrology already treats Water-span Bridges as fluid. Substituting the CURRENT cell's
                    // depth for every sampled Bridge makes the same shared corner depend on which side compiled it: the
                    // exposed owner and covered owner then publish visibly different absorption/caustic patterns.
                    depthSum += (plan.water[sample.id]?.depth ?? depth) * weight;
                    depthWeight += weight;
                }
            }
            return (
                depthWeight > 0 ? depthSum / depthWeight : depth,
                clamp((double)(4 - immediateCount) / 3, 0, 1),
                cornerWaterColor(cornerX, cornerY));
        }
        var nw = cornerWater(cell.x, cell.y);
        var ne = cornerWater(cell.x + 1, cell.y);
        var se = cornerWater(cell.x + 1, cell.y + 1);
        var sw = cornerWater(cell.x, cell.y + 1);
        for (int index = 0; index < pts.Length; index++)
        {
            P3 point = pts[index];
            double u = clamp((point.x - x0) / ts, 0, 1);
            double v = clamp((point.z - z0) / ts, 0, 1);
            // A lower Water/Chasm neighbour is not a shoreline: it is the same liquid turning over the crest. The
            // corner sampler cannot infer that from equal-height connectivity and used to assign a bright animated
            // shore value exactly on this edge. Zero only the actual opening vertices; interpolation then provides a
            // soft material hand-off while perpendicular real banks retain their meniscus.
            double waterfallMouth = waterfallMouthWeight(u, v);
            double floorEdge = normalFloorEdgeWeight(u, v);
            WATER_FOAM_SCRATCH[index] =
                capCornerShade(nw.shore, ne.shore, se.shore, sw.shore, u, v) *
                (1 - Math.max(waterfallMouth, floorEdge));
            WATER_DEPTH_SCRATCH[index] = capCornerShade(nw.depth, ne.depth, se.depth, sw.depth, u, v);
            WATER_COLOR_SCRATCH[index] = capCornerColor(nw.color, ne.color, se.color, sw.color, u, v);
        }

        // A tapered, opaque inner tub meets the exact water polygon at the meniscus and the basin floor below.
        // Unlike four unrelated edge cards its shared corner coordinates are watertight at every bank junction.
        double topX0 = x0;
        double topX1 = x1;
        double topZ0 = z0;
        double topZ1 = z1;
        double taper = clamp(ts * 0.026, WATER_BASIN_SHELL_TAPER_MIN, WATER_BASIN_SHELL_TAPER_MAX);
        double bottomX0 = topX0 + taper;
        double bottomX1 = topX1 - taper;
        double bottomZ0 = topZ0 + taper;
        double bottomZ1 = topZ1 - taper;
        double waterfallMouthClearance = Math.min(
            ts * 0.18,
            CARTOON_TERRAIN_STYLE.waterShore.waterfallMouthClearancePx);
        double northMouthClearance = opensWaterfallEdge("n") ? waterfallMouthClearance : 0;
        double southMouthClearance = opensWaterfallEdge("s") ? waterfallMouthClearance : 0;
        // Taper only at genuine component corners. Along a continuous exposed run, neighbouring cells must share
        // the exact same shell/backing foot vertex or the two per-cell tapers open a black vertical seam.
        double runBottomX0 = fluid.w ? topX0 : bottomX0;
        double runBottomX1 = fluid.e ? topX1 : bottomX1;
        double runBottomZ0 = fluid.n ? topZ0 : bottomZ0;
        double runBottomZ1 = fluid.s ? topZ1 : bottomZ1;
        double northBottomX0 = runBottomX0 + contourNw;
        double northBottomX1 = runBottomX1 - contourNe;
        double southBottomX0 = runBottomX0 + contourSw;
        double southBottomX1 = runBottomX1 - contourSe;
        double eastBottomZ0 = runBottomZ0 + contourNe;
        double eastBottomZ1 = runBottomZ1 - contourSe;
        double westBottomZ0 = runBottomZ0 + contourNw;
        double westBottomZ1 = runBottomZ1 - contourSw;
        double shellTopY = waterZ - 0.025;
        double shellBottomY = basinY + 0.025;
        // A biome-coloured liquid cross-section replaces the old near-black generic side material. Directional
        // colour differences remain deliberately narrow; the top-to-foot shade carries depth without producing a
        // black south face or a white north face at concave bank junctions.
        int shellDeep = water.deep ?? water.topDark;
        int shellBody = water.mid ?? water.top;
        int shellSide = mix(shellDeep, shellBody, CARTOON_TERRAIN_STYLE.waterBasin.shellBodyBlend);
        int shellLit = mix(shellSide, water.shallow ?? water.topLight, 0.1);
        int shellShade = mix(shellSide, shellDeep, 0.22);
        int bankShellColor(string direction, int fallback)
        {
            WaterDryShore? shore = dryShoreFor(direction);
            return shore != null
                ? mix(shore.material.side, fallback, CARTOON_TERRAIN_STYLE.waterShore.shellWaterBlend)
                : fallback;
        }
        setShade4(
            1,
            1,
            CARTOON_TERRAIN_STYLE.waterBasin.shellFootShade,
            CARTOON_TERRAIN_STYLE.waterBasin.shellFootShade);
        if (
            !fluid.n &&
            normalDryWallEdge.n == null &&
            !opensWaterfallEdge("n") &&
            !terrainBridgeWaterOpensIntoChasm(cell, "n"))
        {
            builder.addSurface(
                quad(
                    topX1 - contourNe,
                    shellTopY,
                    topZ0,
                    topX0 + contourNw,
                    shellTopY,
                    topZ0,
                    northBottomX0,
                    shellBottomY,
                    bottomZ0,
                    northBottomX1,
                    shellBottomY,
                    bottomZ0),
                0,
                0.14,
                -1,
                bankShellColor("n", shellLit),
                SURF.basin,
                0.16,
                SHADE4,
                UNIT_ZERO,
                false,
                false,
                true);
        }
        if (
            !fluid.s &&
            normalDryWallEdge.s == null &&
            !opensWaterfallEdge("s") &&
            !terrainBridgeWaterOpensIntoChasm(cell, "s"))
        {
            builder.addSurface(
                quad(
                    topX0 + contourSw,
                    shellTopY,
                    topZ1,
                    topX1 - contourSe,
                    shellTopY,
                    topZ1,
                    southBottomX1,
                    shellBottomY,
                    bottomZ1,
                    southBottomX0,
                    shellBottomY,
                    bottomZ1),
                0,
                0.14,
                1,
                bankShellColor("s", shellShade),
                SURF.basin,
                0.16,
                SHADE4);
        }
        if (
            !fluid.e &&
            normalDryWallEdge.e == null &&
            !opensWaterfallEdge("e") &&
            !terrainBridgeWaterOpensIntoChasm(cell, "e"))
        {
            builder.addSurface(
                quad(
                    topX1,
                    shellTopY,
                    topZ0 + contourNe + northMouthClearance,
                    topX1,
                    shellTopY,
                    topZ1 - contourSe - southMouthClearance,
                    bottomX1,
                    shellBottomY,
                    eastBottomZ1 - southMouthClearance,
                    bottomX1,
                    shellBottomY,
                    eastBottomZ0 + northMouthClearance),
                1,
                0.16,
                0,
                bankShellColor("e", mix(shellSide, shellDeep, 0.16)),
                SURF.basin,
                0.16,
                SHADE4);
        }
        if (
            !fluid.w &&
            normalDryWallEdge.w == null &&
            !opensWaterfallEdge("w") &&
            !terrainBridgeWaterOpensIntoChasm(cell, "w"))
        {
            builder.addSurface(
                quad(
                    topX0,
                    shellTopY,
                    topZ1 - contourSw - southMouthClearance,
                    topX0,
                    shellTopY,
                    topZ0 + contourNw + northMouthClearance,
                    bottomX0,
                    shellBottomY,
                    westBottomZ0 + northMouthClearance,
                    bottomX0,
                    shellBottomY,
                    westBottomZ1 - southMouthClearance),
                -1,
                0.16,
                0,
                bankShellColor("w", mix(shellSide, shellLit, 0.55)),
                SURF.basin,
                0.16,
                SHADE4);
        }

        // Close every rounded shoreline corner with one faceted liquid cross-section. These four optional quads are
        // baked into the existing opaque terrain batch; they add no draw call and replace the unsupported square
        // corner/sliver that used to shimmer as the camera moved.
        void addRoundedBasinCorner(
            double radius,
            P3 topA,
            P3 topB,
            P3 bottomB,
            P3 bottomA,
            P3 topCorner,
            P3 bottomCorner,
            int color)
        {
            if (radius <= 0.01) return;
            P3 quadratic(P3 a, P3 control, P3 b, double t)
            {
                double inverse = 1 - t;
                return new P3(
                    inverse * inverse * a.x + 2 * inverse * t * control.x + t * t * b.x,
                    inverse * inverse * a.y + 2 * inverse * t * control.y + t * t * b.y,
                    inverse * inverse * a.z + 2 * inverse * t * control.z + t * t * b.z);
            }
            for (int segment = 0; segment < 3; segment++)
            {
                double t0 = (double)segment / 3;
                double t1 = (double)(segment + 1) / 3;
                P3 segmentTopA = quadratic(topA, topCorner, topB, t0);
                P3 segmentTopB = quadratic(topA, topCorner, topB, t1);
                P3 segmentBottomA = quadratic(bottomA, bottomCorner, bottomB, t0);
                P3 segmentBottomB = quadratic(bottomA, bottomCorner, bottomB, t1);
                double dx = segmentTopB.x - segmentTopA.x;
                double dz = segmentTopB.z - segmentTopA.z;
                builder.addSurface(
                    new[] { segmentTopA, segmentTopB, segmentBottomB, segmentBottomA },
                    -dz,
                    0.14,
                    dx,
                    color,
                    SURF.basin,
                    0.16,
                    SHADE4,
                    UNIT_ZERO,
                    false,
                    false,
                    true);
            }
        }
        addRoundedBasinCorner(
            (normalDryWallEdge.n != null && normalDryWallEdge.w != null) ||
                opensWaterfallEdge("n") ||
                opensWaterfallEdge("w")
                ? 0
                : contourNw,
            new P3(x0 + contourNw, shellTopY, z0),
            new P3(x0, shellTopY, z0 + contourNw),
            new P3(bottomX0, shellBottomY, bottomZ0 + contourNw),
            new P3(bottomX0 + contourNw, shellBottomY, bottomZ0),
            new P3(x0, shellTopY, z0),
            new P3(bottomX0, shellBottomY, bottomZ0),
            mix(bankShellColor("n", shellLit), bankShellColor("w", shellLit), 0.5));
        addRoundedBasinCorner(
            (normalDryWallEdge.n != null && normalDryWallEdge.e != null) ||
                opensWaterfallEdge("n") ||
                opensWaterfallEdge("e")
                ? 0
                : contourNe,
            new P3(x1, shellTopY, z0 + contourNe),
            new P3(x1 - contourNe, shellTopY, z0),
            new P3(bottomX1 - contourNe, shellBottomY, bottomZ0),
            new P3(bottomX1, shellBottomY, bottomZ0 + contourNe),
            new P3(x1, shellTopY, z0),
            new P3(bottomX1, shellBottomY, bottomZ0),
            mix(bankShellColor("n", shellLit), bankShellColor("e", shellLit), 0.5));
        addRoundedBasinCorner(
            (normalDryWallEdge.s != null && normalDryWallEdge.e != null) ||
                opensWaterfallEdge("s") ||
                opensWaterfallEdge("e")
                ? 0
                : contourSe,
            new P3(x1 - contourSe, shellTopY, z1),
            new P3(x1, shellTopY, z1 - contourSe),
            new P3(bottomX1, shellBottomY, bottomZ1 - contourSe),
            new P3(bottomX1 - contourSe, shellBottomY, bottomZ1),
            new P3(x1, shellTopY, z1),
            new P3(bottomX1, shellBottomY, bottomZ1),
            mix(bankShellColor("s", shellShade), bankShellColor("e", shellShade), 0.5));
        addRoundedBasinCorner(
            (normalDryWallEdge.s != null && normalDryWallEdge.w != null) ||
                opensWaterfallEdge("s") ||
                opensWaterfallEdge("w")
                ? 0
                : contourSw,
            new P3(x0, shellTopY, z1 - contourSw),
            new P3(x0 + contourSw, shellTopY, z1),
            new P3(bottomX0 + contourSw, shellBottomY, bottomZ1),
            new P3(bottomX0, shellBottomY, bottomZ1 - contourSw),
            new P3(x0, shellTopY, z1),
            new P3(bottomX0, shellBottomY, bottomZ1),
            mix(bankShellColor("s", shellShade), bankShellColor("w", shellShade), 0.5));

        // An authored Water terrace can sit above adjacent dry terrain. Close that exceptional height ordering with
        // the neighbour's ordinary earth/rock face, whose crest follows every sample of the SAME hydraulic edge as
        // the translucent surface. A cell-centre crest cuts straight through a sloped edge: above it the backing
        // becomes the reported grey triangle; below it the scene background becomes the matching black triangle.
        void addDryBankBacking(string dir, int tx, int ty)
        {
            TerrainCell? neighbor = terrainCellAt(terrain, tx, ty);
            if (neighbor == null || neighbor.type == TileType.Chasm || terrainCellCarriesWater(neighbor))
            {
                return;
            }
            double bankBottomY = neighbor.surfaceZ * ELEV + 0.035;
            TerrainMaterial bankMaterial = plan.materials[neighbor.id] ?? STANDARD_TERRAIN_MATERIALS.floorCool;
            bool rockBacking = neighbor.type == TileType.Solid;
            int bankColor = terrainFaceBaseColor(bankMaterial, rockBacking ? "rock" : "earth");
            int faceKind = rockBacking ? SURF.rockFace : SURF.earthFace;
            bool verticalEdge = dir == "e" || dir == "w";
            double edgeStart = verticalEdge ? northMouthClearance / ts : 0;
            double edgeEnd = verticalEdge ? 1 - southMouthClearance / ts : 1;
            double topYAt(double t) =>
                visualWaterYAtUv(
                    dir == "e" ? 1 : dir == "w" ? 0 : t,
                    dir == "s" ? 1 : dir == "n" ? 0 : t) - 0.025;
            double minimumTopY = bankBottomY + 0.02;
            setShade4(1, 1, 0.76, 0.76);
            for (int segment = 0; segment < hydraulicSegments; segment++)
            {
                double t0 = edgeStart + ((edgeEnd - edgeStart) * segment) / hydraulicSegments;
                double t1 = edgeStart + ((edgeEnd - edgeStart) * (segment + 1)) / hydraulicSegments;
                double topY0 = topYAt(t0);
                double topY1 = topYAt(t1);
                if (topY0 <= minimumTopY && topY1 <= minimumTopY) continue;
                if (topY0 <= minimumTopY)
                {
                    double cut = (minimumTopY - topY0) / Math.max(0.000001, topY1 - topY0);
                    t0 += (t1 - t0) * clamp01(cut);
                    topY0 = minimumTopY;
                }
                if (topY1 <= minimumTopY)
                {
                    double cut = (topY0 - minimumTopY) / Math.max(0.000001, topY0 - topY1);
                    t1 = t0 + (t1 - t0) * clamp01(cut);
                    topY1 = minimumTopY;
                }
                double along0 = verticalEdge ? z0 + t0 * ts : x0 + t0 * ts;
                double along1 = verticalEdge ? z0 + t1 * ts : x0 + t1 * ts;
                P3[] points =
                    dir == "n"
                        ? quad(
                            along1,
                            topY1,
                            z0,
                            along0,
                            topY0,
                            z0,
                            along0,
                            bankBottomY,
                            z0,
                            along1,
                            bankBottomY,
                            z0)
                        : dir == "s"
                            ? quad(
                                along0,
                                topY0,
                                z1,
                                along1,
                                topY1,
                                z1,
                                along1,
                                bankBottomY,
                                z1,
                                along0,
                                bankBottomY,
                                z1)
                            : dir == "e"
                                ? quad(
                                    x1,
                                    topY0,
                                    along0,
                                    x1,
                                    topY1,
                                    along1,
                                    x1,
                                    bankBottomY,
                                    along1,
                                    x1,
                                    bankBottomY,
                                    along0)
                                : quad(
                                    x0,
                                    topY1,
                                    along1,
                                    x0,
                                    topY0,
                                    along0,
                                    x0,
                                    bankBottomY,
                                    along0,
                                    x0,
                                    bankBottomY,
                                    along1);
                builder.addSurface(
                    points,
                    dir == "e" ? 1 : dir == "w" ? -1 : 0,
                    verticalEdge ? 0.14 : 0.12,
                    dir == "n" ? -1 : dir == "s" ? 1 : 0,
                    bankColor,
                    faceKind,
                    0.17,
                    SHADE4,
                    UNIT_ZERO,
                    false,
                    false,
                    dir != "s");
            }
        }
        if (!fluid.n) addDryBankBacking("n", cell.x, cell.y - 1);
        if (!fluid.e) addDryBankBacking("e", cell.x + 1, cell.y);
        if (!fluid.s) addDryBankBacking("s", cell.x, cell.y + 1);
        if (!fluid.w) addDryBankBacking("w", cell.x - 1, cell.y);

        // Covered and open spans share one pigment, reflection hint and world-continuous field. A deck occludes the
        // physical pixels above it; the liquid must not encode a second sky/shadow state of its own, because that
        // state makes every Bridge footprint a hard rectangular material seam.
        WATER_REFLECTION_CORNERS[0] = terrainWaterReflectionHintAt(
            terrain,
            plan,
            cell.x,
            cell.y,
            visualWaterLevelAtUv(0, 0));
        WATER_REFLECTION_CORNERS[1] = terrainWaterReflectionHintAt(
            terrain,
            plan,
            cell.x + 1,
            cell.y,
            visualWaterLevelAtUv(1, 0));
        WATER_REFLECTION_CORNERS[2] = terrainWaterReflectionHintAt(
            terrain,
            plan,
            cell.x + 1,
            cell.y + 1,
            visualWaterLevelAtUv(1, 1));
        WATER_REFLECTION_CORNERS[3] = terrainWaterReflectionHintAt(
            terrain,
            plan,
            cell.x,
            cell.y + 1,
            visualWaterLevelAtUv(0, 1));
        for (int index = 0; index < pts.Length; index++)
        {
            P3 point = pts[index];
            double u = clamp((point.x - x0) / ts, 0, 1);
            double v = clamp((point.z - z0) / ts, 0, 1);
            double reflectionHint = capCornerShade(
                WATER_REFLECTION_CORNERS[0],
                WATER_REFLECTION_CORNERS[1],
                WATER_REFLECTION_CORNERS[2],
                WATER_REFLECTION_CORNERS[3],
                u,
                v);
            // Height-change mouths retain the ordinary surface reflection field. Encoding a signed edge marker here
            // made the otherwise normal source water a separate material strip.
            WATER_REFLECTION_SCRATCH[index] = reflectionHint;
        }
        // The bit mask describes only real exposed banks. A lower receiving pool deliberately leaves its landing
        // edge open so the falling field can become the horizontal field without an animated foam seam.
        double stableBankMask =
            (!fluid.n && !opensWaterfallEdge("n") ? 1 : 0) +
            (!fluid.e && !opensWaterfallEdge("e") ? 1 : 0) * 2 +
            (!fluid.s && !opensWaterfallEdge("s") ? 1 : 0) * 4 +
            (!fluid.w && !opensWaterfallEdge("w") ? 1 : 0) * 8;
        double quantizedContour(double radius) =>
            clamp(Math.round((radius / ts) * 32), 0, 15);
        // Float32 represents this <20-bit integer exactly. Packing the four quantized corner radii beside the bank
        // bits avoids another vertex attribute while giving the fragment shader the real rounded shoreline rather
        // than an approximate square distance field.
        double stableBankPayload =
            stableBankMask +
            quantizedContour(contourNw) * 16 +
            quantizedContour(contourNe) * 256 +
            quantizedContour(contourSe) * 4_096 +
            quantizedContour(contourSw) * 65_536;
        void sampleTransitionGridVertex(
            int index,
            double u,
            double v,
            double? worldX = null,
            double? worldZ = null)
        {
            worldX ??= x0 + u * ts;
            worldZ ??= z0 + v * ts;
            shoreTransitionAt(worldX.Value, worldZ.Value, WATER_TRANSITION_SAMPLE);
            double groundBlend = WATER_TRANSITION_SAMPLE[0];
            // The values are shared at lattice vertices, but a linear blend still changes derivative at every tile
            // edge and the eye reconstructs that gradient break as a quadrilateral. Use the same C2 corner field as
            // dry terrain so pigment, absorption and reflection cross Water ownership boundaries without a Mach band.
            int waterColor = capCornerColor(nw.color, ne.color, se.color, sw.color, u, v);
            WATER_TRANSITION_GRID_COLOR[index] = mix(
                waterColor,
                WATER_TRANSITION_SAMPLE[1],
                groundBlend);
            // Negative aWater.x is a dedicated, interpolation-safe ground-dissolve lane. The ordinary shore channel
            // is non-negative, so the shader can decode this without another GPU attribute or a material variant.
            WATER_TRANSITION_GRID_FOAM[index] = -groundBlend;
            WATER_TRANSITION_GRID_DEPTH[index] = capCornerShade(
                nw.depth,
                ne.depth,
                se.depth,
                sw.depth,
                u,
                v);
            WATER_TRANSITION_GRID_REFLECTION[index] = capCornerShade(
                WATER_REFLECTION_CORNERS[0],
                WATER_REFLECTION_CORNERS[1],
                WATER_REFLECTION_CORNERS[2],
                WATER_REFLECTION_CORNERS[3],
                u,
                v);
        }
        void copyTransitionGridVertex(int targetIndex, int gridIndex)
        {
            WATER_TRANSITION_COLOR4[targetIndex] = WATER_TRANSITION_GRID_COLOR[gridIndex];
            WATER_TRANSITION_FOAM4[targetIndex] = WATER_TRANSITION_GRID_FOAM[gridIndex];
            WATER_TRANSITION_DEPTH4[targetIndex] = WATER_TRANSITION_GRID_DEPTH[gridIndex];
            WATER_TRANSITION_REFLECTION4[targetIndex] = WATER_TRANSITION_GRID_REFLECTION[gridIndex];
            WATER_TRANSITION_NORMAL_X4[targetIndex] = WATER_TRANSITION_GRID_NORMAL_X[gridIndex];
            WATER_TRANSITION_NORMAL_Y4[targetIndex] = WATER_TRANSITION_GRID_NORMAL_Y[gridIndex];
            WATER_TRANSITION_NORMAL_Z4[targetIndex] = WATER_TRANSITION_GRID_NORMAL_Z[gridIndex];
        }
        bool hasFloorTransition = false;
        for (int corner = 0; corner < 4; corner++)
        {
            double u = corner == 1 || corner == 2 ? 1 : 0;
            double v = corner >= 2 ? 1 : 0;
            shoreTransitionAt(x0 + u * ts, z0 + v * ts, WATER_TRANSITION_SAMPLE);
            if (WATER_TRANSITION_SAMPLE[0] > 0.0001) hasFloorTransition = true;
        }
        double minimumVisualLevel = Number.POSITIVE_INFINITY;
        double maximumVisualLevel = Number.NEGATIVE_INFINITY;
        foreach (double v in WATER_RELIEF_UV)
        {
            foreach (double u in WATER_RELIEF_UV)
            {
                double level = visualWaterLevelAtUv(u, v);
                minimumVisualLevel = Math.min(minimumVisualLevel, level);
                maximumVisualLevel = Math.max(maximumVisualLevel, level);
            }
        }
        bool hasWaterRelief = maximumVisualLevel - minimumVisualLevel > 0.012;
        if (
            hasChasmBoundary ||
            liquidContour.hasMacroDeformation ||
            hasFloorTransition ||
            hasWaterRelief)
        {
            // The compact software-WebGL tier retains the exact water ownership and corner heights but represents a
            // cell with one bilinear quad. The seven-by-seven premium shore lattice costs 98 triangles per wet cell;
            // across the first viewport that lane alone exceeded the rest of the structural terrain. Hardware keeps
            // the full organic contour and relief topology.
            int segments = hydraulicSegments;
            int gridSide = segments + 1;
            var deformedPoint = new LiquidChasmEdgePoint { x = 0, z = 0 };
            for (int row = 0; row <= segments; row++)
            {
                double v = (double)row / segments;
                for (int column = 0; column <= segments; column++)
                {
                    double u = (double)column / segments;
                    int gridIndex = row * gridSide + column;
                    if (hasChasmBoundary || liquidContour.hasMacroDeformation)
                    {
                        liquidChasmSurfacePointAtUv(
                            terrain,
                            cell,
                            liquidContour,
                            x0,
                            x1,
                            z0,
                            z1,
                            u,
                            v,
                            deformedPoint);
                        WATER_TRANSITION_GRID_X[gridIndex] = deformedPoint.x;
                        WATER_TRANSITION_GRID_Z[gridIndex] = deformedPoint.z;
                    }
                    else
                    {
                        WATER_TRANSITION_GRID_X[gridIndex] = x0 + u * ts;
                        WATER_TRANSITION_GRID_Z[gridIndex] = z0 + v * ts;
                    }
                    WATER_TRANSITION_GRID_Y[gridIndex] = visualWaterYAtUv(u, v);
                    sampleTransitionGridVertex(
                        gridIndex,
                        u,
                        v,
                        WATER_TRANSITION_GRID_X[gridIndex],
                        WATER_TRANSITION_GRID_Z[gridIndex]);
                    visualWaterNormalAtUv(
                        u,
                        v,
                        gridIndex,
                        WATER_TRANSITION_GRID_NORMAL_X,
                        WATER_TRANSITION_GRID_NORMAL_Y,
                        WATER_TRANSITION_GRID_NORMAL_Z);
                }
            }
            for (int row = 0; row < segments; row++)
            {
                for (int column = 0; column < segments; column++)
                {
                    int gridNw = row * gridSide + column;
                    int gridNe = gridNw + 1;
                    int gridSw = gridNw + gridSide;
                    int gridSe = gridSw + 1;
                    quadInto(
                        WATER_TRANSITION_QUAD,
                        WATER_TRANSITION_GRID_X[gridNw],
                        WATER_TRANSITION_GRID_Y[gridNw],
                        WATER_TRANSITION_GRID_Z[gridNw],
                        WATER_TRANSITION_GRID_X[gridNe],
                        WATER_TRANSITION_GRID_Y[gridNe],
                        WATER_TRANSITION_GRID_Z[gridNe],
                        WATER_TRANSITION_GRID_X[gridSe],
                        WATER_TRANSITION_GRID_Y[gridSe],
                        WATER_TRANSITION_GRID_Z[gridSe],
                        WATER_TRANSITION_GRID_X[gridSw],
                        WATER_TRANSITION_GRID_Y[gridSw],
                        WATER_TRANSITION_GRID_Z[gridSw]);
                    if (hasBasinSupport)
                    {
                        builder.addSurface(
                            quad(
                                WATER_TRANSITION_GRID_X[gridNw],
                                WATER_TRANSITION_GRID_Y[gridNw] - basinSupportOffset,
                                WATER_TRANSITION_GRID_Z[gridNw],
                                WATER_TRANSITION_GRID_X[gridNe],
                                WATER_TRANSITION_GRID_Y[gridNe] - basinSupportOffset,
                                WATER_TRANSITION_GRID_Z[gridNe],
                                WATER_TRANSITION_GRID_X[gridSe],
                                WATER_TRANSITION_GRID_Y[gridSe] - basinSupportOffset,
                                WATER_TRANSITION_GRID_Z[gridSe],
                                WATER_TRANSITION_GRID_X[gridSw],
                                WATER_TRANSITION_GRID_Y[gridSw] - basinSupportOffset,
                                WATER_TRANSITION_GRID_Z[gridSw]),
                            0,
                            1,
                            0,
                            basinColor,
                            SURF.basin,
                            0.09,
                            UNIT_SHADE,
                            UNIT_ZERO,
                            false,
                            false,
                            false);
                    }
                    copyTransitionGridVertex(0, gridNw);
                    copyTransitionGridVertex(1, gridNe);
                    copyTransitionGridVertex(2, gridSe);
                    copyTransitionGridVertex(3, gridSw);
                    builder.addWater(
                        WATER_TRANSITION_QUAD,
                        WATER_TRANSITION_COLOR4,
                        WATER_TRANSITION_FOAM4,
                        WATER_TRANSITION_DEPTH4,
                        ts,
                        stableBankPayload,
                        ts,
                        WATER_TRANSITION_REFLECTION4,
                        WATER_TRANSITION_NORMAL_X4,
                        WATER_TRANSITION_NORMAL_Y4,
                        WATER_TRANSITION_NORMAL_Z4);
                }
            }
        }
        else
        {
            // Contoured banks may have more than the four square corners. Sample every emitted point; falling back
            // to the polygon face normal for an arc vertex created a tiny but real lighting split where a tessellated
            // neighbouring owner wrote the same world position with its spline derivative.
            for (int index = 0; index < pts.Length; index++)
            {
                P3 point = pts[index];
                visualWaterNormalAtUv(
                    clamp((point.x - x0) / ts, 0, 1),
                    clamp((point.z - z0) / ts, 0, 1),
                    index);
            }
            if (hasBasinSupport)
            {
                builder.addSurface(
                    quad(
                        pts[0].x,
                        pts[0].y - basinSupportOffset,
                        pts[0].z,
                        pts[1].x,
                        pts[1].y - basinSupportOffset,
                        pts[1].z,
                        pts[2].x,
                        pts[2].y - basinSupportOffset,
                        pts[2].z,
                        pts[3].x,
                        pts[3].y - basinSupportOffset,
                        pts[3].z),
                    0,
                    1,
                    0,
                    basinColor,
                    SURF.basin,
                    0.09,
                    UNIT_SHADE,
                    UNIT_ZERO,
                    false,
                    false,
                    false);
            }
            builder.addWater(
                pts,
                WATER_COLOR_SCRATCH,
                WATER_FOAM_SCRATCH,
                WATER_DEPTH_SCRATCH,
                ts,
                stableBankPayload,
                ts,
                WATER_REFLECTION_SCRATCH,
                WATER_TRANSITION_NORMAL_X4,
                WATER_TRANSITION_NORMAL_Y4,
                WATER_TRANSITION_NORMAL_Z4);
        }
        // Theme identity lives in the pigment and world-continuous liquid field. The former per-cell slick,
        // mercury and glass line overlays restarted inside every 40 px cell and were the visible horizontal bars
        // in broad lakes; no static overlay is allowed to reveal water ownership now.

        // No static shoreline overlay: the tessellated liquid pigment dissolve and organic shader distance own it.
    }

    /* ── Overlay decor (ported from the render plan; flat, pre-composited, baked once) ──────────────────── */

    internal void addWallCapDetail(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        double insS,
        bool hasContour)
    {
        // Theme roof grammars are authored as rectangular plates, lips and line motifs. A topology contour has
        // already removed one or more square cap corners; emitting those uncut decorations would leave their N/E/
        // S/W ends floating over the diagonal closure as detached strokes. The structural contour rim remains, so
        // the clean junction keeps the active material.
        if (hasContour) return;
        if (this.tileset?.decal.kind == "runeKnot")
        {
            double ts = this.ts;
            double x1 = x0 + ts;
            double z1 = z0 + ts - insS;
            double y = capY + 0.08;
            int timber = mix(material.topDark, this.tileset!.bridge.body, 0.34);
            int tar = mix(material.edgeDark, this.tileset.decal.ink, 0.3);
            builder.addOverlay(
                quad(
                    x0 + 2.8,
                    y,
                    z0 + 2.8,
                    x1 - 2.8,
                    y,
                    z0 + 2.8,
                    x1 - 2.8,
                    y,
                    z1 - 2.8,
                    x0 + 2.8,
                    y,
                    z1 - 2.8),
                timber,
                0.25);
            for (int i = 1; i <= 3; i++)
            {
                double px = x0 + (ts * i) / 4;
                builder.addOverlayLineFlat(y + 0.02, px, z0 + 4, px, z1 - 4, 0.64, tar, 0.34);
            }
            builder.addOverlayLineFlat(
                y + 0.04,
                x0 + 6,
                z0 + 6,
                x1 - 6,
                z1 - 6,
                0.72,
                this.tileset.decal.accent,
                0.32);
            builder.addOverlayLineFlat(
                y + 0.04,
                x1 - 6,
                z0 + 6,
                x0 + 6,
                z1 - 6,
                0.62,
                this.tileset.decal.mid,
                0.26);
            return;
        }
        if (this.tileset?.decal.kind == "cropCircle")
        {
            // Bluff tops are beam-tended landing pads: a scorch ring walked in six alternating chords around a
            // glow beacon heart — the same glyph language as the pasture, so mesas and turf tell ONE fiction
            // from the air.
            double ts = this.ts;
            double x1 = x0 + ts;
            double z1 = z0 + ts - insS;
            double y = capY + 0.08;
            int scorch = mix(material.edgeDark, this.tileset!.decal.ink, 0.32);
            int comb = mix(material.topDark, this.tileset.decal.mid, 0.4);
            double cx = (x0 + x1) * 0.5;
            double cz = (z0 + z1) * 0.5;
            double r = Math.min(x1 - x0, z1 - z0) * 0.34;
            double lastX = cx + r;
            double lastZ = cz;
            for (int i = 1; i <= 6; i++)
            {
                double a = ((double)i / 6) * Math.PI * 2;
                double nx = cx + Math.cos(a) * r;
                double nz = cz + Math.sin(a) * r * 0.8;
                builder.addOverlayLineFlat(
                    y + 0.02,
                    lastX,
                    lastZ,
                    nx,
                    nz,
                    0.66,
                    i % 2 != 0 ? comb : scorch,
                    0.32);
                lastX = nx;
                lastZ = nz;
            }
            builder.addOverlay(
                quad(
                    cx - 1.6,
                    y + 0.04,
                    cz - 1.2,
                    cx + 1.6,
                    y + 0.04,
                    cz - 1.2,
                    cx + 1.6,
                    y + 0.04,
                    cz + 1.2,
                    cx - 1.6,
                    y + 0.04,
                    cz + 1.2),
                this.tileset.decal.accent,
                0.3);
            return;
        }
        if (this.clockworkTileset)
        {
            this.addClockworkRoof(builder, frame, cell, material, x0, z0, capY, insS);
            return;
        }
        if (this.prismglassTileset || this.tileset?.decal.kind == "glassShard")
        {
            this.addPrismglassRoof(builder, frame, cell, material, x0, z0, capY, insS);
            return;
        }
        if (this.cathedralTileset || this.tileset?.decal.kind == "cathedralStar")
        {
            double ts = this.ts;
            double x1 = x0 + ts;
            double z1 = z0 + ts - insS;
            double y = capY + 0.08;
            int gold = cathedralStarfire(cell.id * 1279 + frame.i0 * 13 + frame.j0 * 17);
            int bone = this.tileset?.terrain.wallLit ?? material.edgeLight;
            builder.addOverlay(
                quad(x0 + 3, y, z0 + 3, x1 - 3, y, z0 + 3, x1 - 3, y, z1 - 3, x0 + 3, y, z1 - 3),
                material.topDark,
                0.16);
            builder.addOverlayLineFlat(y + 0.03, x0 + 5, z0 + 5, x1 - 5, z1 - 5, 0.72, gold, 0.28);
            builder.addOverlayLineFlat(
                y + 0.04,
                x0 + ts * 0.22,
                z0 + ts * 0.5,
                x1 - ts * 0.22,
                z0 + ts * 0.5,
                0.58,
                bone,
                0.22);
            return;
        }
        double h = hash(cell.id + 191);
        if (this.tileset?.decal.kind == "carnival")
        {
            if (h >= 0.66) return;
            double ts = this.ts;
            double x1 = x0 + ts;
            double z1 = z0 + ts - insS;
            IReadOnlyList<int> colors = TERRAIN_GEOMETRY_CARNIVAL_COLORS;
            double y = capY + 0.08;
            double stripeZ = z0 + ts * (0.22 + hash(cell.id * 193 + 3) * 0.56);
            builder.addOverlayLineFlat(
                y,
                x0 + ts * 0.16,
                stripeZ,
                x1 - ts * 0.16,
                stripeZ + (h - 0.5) * ts * 0.08,
                1.15,
                h < 0.28 ? this.tileset!.flood.foam : colors[0],
                0.28);
            int bulbs = 2 + (int)Math.floor(hash(cell.id * 197 + 5) * 3);
            for (int i = 0; i < bulbs; i++)
            {
                double t = (i + 0.5) / bulbs;
                double px = x0 + ts * (0.18 + t * 0.64);
                double pz = z0 + ts * (0.24 + hash(cell.id * 211 + i * 17) * 0.52);
                double r = 1.5 + hash(cell.id * 223 + i * 19) * 1.4;
                builder.addOverlay(
                    new[]
                    {
                        new P3(px - r, y + 0.02, pz),

                        new P3(px, y + 0.02, pz - r * 0.7),
                        new P3(px + r, y + 0.02, pz),
                        new P3(px, y + 0.02, pz + r * 0.7),
                    },
                    colors[(i + (int)Math.floor(h * colors.Count)) % colors.Count],
                    0.24);
            }
            if (hash(cell.id * 229 + 7) < 0.34)
            {
                builder.addOverlayLineFlat(
                    y + 0.02,
                    x0 + ts * 0.2,
                    z1 - 3,
                    x1 - ts * 0.2,
                    z1 - 3,
                    0.72,
                    colors[4],
                    0.22);
            }
            return;
        }
        if (this.tileset?.decal.kind == "rainbow")
        {
            if (h >= 0.54) return;
            double ts = this.ts;
            double px = x0 + ts * (0.2 + hash(cell.id * 193 + 3) * 0.6);
            double pz = z0 + ts * (0.18 + hash(cell.id * 197 + 5) * 0.58);
            double size = 4.8 + h * 10.5;
            int color =
                TERRAIN_GEOMETRY_RAINBOW_COLORS[
                    (int)Math.floor(hash(cell.id * 991 + 23) * TERRAIN_GEOMETRY_RAINBOW_COLORS.Count)];
            builder.addOverlay(
                new[]
                {
                    new P3(px, capY + 0.08, pz - size * 0.5),
                    new P3(px + size * 0.78, capY + 0.08, pz),
                    new P3(px, capY + 0.08, pz + size * 0.5),
                    new P3(px - size * 0.78, capY + 0.08, pz),
                },
                mix(color, material.edgeLight, 0.12),
                h < 0.22 ? 0.27 : 0.19);
            builder.addOverlayLineFlat(
                capY + 0.1,
                px - size * 0.42,
                pz - size * 0.12,
                px + size * 0.44,
                pz + size * 0.1,
                0.65,
                material.edgeLight,
                0.18);
            return;
        }
    }
}
