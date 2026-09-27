// Port of packages/client/src/render/environment/terrainGeometryCompiler.ts — keep in lockstep with the original.
//
// PART C3 (TS lines 6202–9024): `buildSolidCell` (the cliff/wall/cap compiler), `addTerrainCrestInk`,
// `addShoreInkLine`, `addFaceFootBand`, the water colour/depth/shore corner helpers, `addSeamlessWaterContourFill`
// and `addContourCornerFace`. Instance fields, module-level helpers and `TileGeometryBuilder` live in part C1
// (TerrainGeometryCompiler.cs).
//
// PORT NOTES
// * TS closures inside `buildSolidCell` (`compatibleCapColorAt`, `capShadeAt`, `addProfiledWaterContactFace`,
//   `addNearLevelWaterFace`) are C# local functions. The options object of `addProfiledWaterContactFace` became
//   named parameters (same names, same order); call sites pass them by name exactly like the object literal.
// * `TerrainContourPoint` and `P3` are the same `{ x, y, z }` shape in TS, so the original hands contour polygons
//   straight to the builder / soffit. The C# ports declare two nominal classes, so such a polygon is copied
//   value-for-value into P3 (`contourPointsAsP3`, per-thread scratch) right before its synchronous consumer.
//   Nothing else changes: the builder only reads x/y/z.
// * `addSeamlessWaterContourFill` writes `WATER_TRANSITION_FOAM4[4]` for its fifth outline point. In JS that
//   silently grows the 4-slot scratch array; the C# scratch is a fixed double[4]. The fifth value is therefore kept
//   in a local array (read back by the second loop exactly as the TS reads the grown slot); slots 0..3 are still
//   written to the shared scratch. The only other readers of WATER_TRANSITION_FOAM4 (buildWaterCell) consume
//   four-point quads, so slot 4 is never observable elsewhere.
// * `terrainOrganicHeightAt` is called qualified (`TerrainVisualGround.`): that module also exports `clamp` and
//   `mix`, which would be ambiguous with TerrainGeometryCompilerFields.clamp / Palette.mix under `using static`.
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Domain.TerrainRenderPlanModule;
using static Fluitown.Domain.TerrainVisualContour;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerInk;
using static Fluitown.Render.TerrainCameraAwayCrest;
using static Fluitown.Render.TerrainGroundDetail;
using static Fluitown.Render.TerrainContourGeometry;
using static Fluitown.Render.TerrainProjection;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainBakePigment;
using static Fluitown.Render.TerrainBridgeSoffit;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainWallShelfGeometry;
using static Fluitown.Render.TerrainChasmGeometry;
using static Fluitown.Render.TerrainWaterGeometryScratch;
using static Fluitown.Render.TerrainGeometryCompilerModule;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed partial class TerrainGeometryCompiler
{
    internal void buildSolidCell(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        TerrainMaterial material)
    {
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double z1 = z0 + ts;
        double capY = cell.surfaceZ * ELEV;
        int capColor = terrainMaterialSurfaceTopColor(material, cell);
        bool isRock = cell.type == TileType.Solid;
        bool isBridge = cell.type == TileType.Bridge;
        bool bridgeTravelNorthSouth = isBridge && terrainBridgeTravelAxis(cell) == "vertical";
        int capKind = isRock ? SURF.rockCap : isBridge ? SURF.bridge : SURF.floor;
        double capStrength = isRock ? 0.15 : isBridge ? 0.12 : 0.12;
        int wx = frame.i0 + cell.x;
        int wy = frame.j0 + cell.y;
        // Same-height cap pigment is sampled at logical world corners, not chosen by the owning cell. This applies
        // to both ground and massive wall plateaus: every cell touching a corner writes the exact same colour there,
        // so material/variant changes become a continuous field instead of revealing the one-cell raster.
        int compatibleCapColorAt(int cornerX, int cornerY) =>
            isBridge
                ? capColor
                : this.compatibleCapColorAt(terrain, plan, cell, cornerX, cornerY, isRock, capColor);
        int capColorNw = compatibleCapColorAt(cell.x, cell.y);
        int capColorNe = compatibleCapColorAt(cell.x + 1, cell.y);
        int capColorSe = compatibleCapColorAt(cell.x + 1, cell.y + 1);
        int capColorSw = compatibleCapColorAt(cell.x, cell.y + 1);
        double capShadeAt(double sampleX, double sampleY)
        {
            double shade = terrainCapShade(sampleX, sampleY, isRock, isBridge);
            if (!isRock) return shade;
            // Theme identity remains, but its former quantised plate hash produced rectangular tone blocks.
            // Cubic world fields keep the same restrained ranges without introducing a discontinuity at block bounds.
            if (this.cityTileset) shade *= 0.93 + smoothCellNoise(sampleX, sampleY, 5, 101) * 0.15;
            else if (this.olympianTileset)
                shade *= 1.01 + smoothCellNoise(sampleX, sampleY, 4, 103) * 0.08;
            else if (this.carnivalTileset)
                shade *= 1.02 + smoothCellNoise(sampleX, sampleY, 4, 107) * 0.1;
            else if (this.clockworkTileset)
                shade *= 0.9 + smoothCellNoise(sampleX, sampleY, 3, 109) * 0.18;
            else if (this.cathedralTileset)
                shade *= 0.98 + smoothCellNoise(sampleX, sampleY, 5, 113) * 0.12;
            return shade;
        }
        double shadeNw = capShadeAt(wx, wy);
        double shadeNe = capShadeAt(wx + 1, wy);
        double shadeSe = capShadeAt(wx + 1, wy + 1);
        double shadeSw = capShadeAt(wx, wy + 1);
        TerrainVisualContourCorners contour = this.coherentContours
            ? terrainCoherentContourCorners(terrain, cell, wx, wy, this.terrainContourScratch)
            : terrainVisualContourCorners(terrain, cell, wx, wy, this.terrainContourScratch);
        double contourNw = contour.nw * ts;
        double contourNe = contour.ne * ts;
        double contourSe = contour.se * ts;
        double contourSw = contour.sw * ts;
        double contourN = (contour.n ?? 0) * ts;
        double contourE = (contour.e ?? 0) * ts;
        double contourS = (contour.s ?? 0) * ts;
        double contourW = (contour.w ?? 0) * ts;
        bool hasContour =
            contourNw + contourNe + contourSe + contourSw + contourN + contourE + contourS + contourW >
            0.01;
        // Use the shared-corner mean for shoulders and other one-value cap details as well. It is world-stable and
        // cannot resurrect the owner-cell hash after the main cap has been made continuous.
        double capShade = (shadeNw + shadeNe + shadeSe + shadeSw) * 0.25;

        // Opposing crest bevels keep the terrain volume convincing through the short-creator's complete orbit. The
        // north wall itself is back-facing at gameplay yaw, so its shoulder must carry the complete camera-away
        // height cue. Resolve its size in PROJECTED pixels: unlike a raw material constant, this survives the
        // oblique cancellation between inward ground run and downward height drop.
        TerrainEdge nEdge = cell.edges.n!;
        double northBaseBevel = this.bevelFor(cell, nEdge);
        var northCrestStyle = CARTOON_TERRAIN_STYLE.cameraAwayCrest;
        double nBevel = terrainCameraAwayCrestDepth(northBaseBevel, nEdge.drop);
        double insN = terrainCameraAwayCrestInset(nBevel);
        double northTopRimWest = terrainCameraAwayCrestTopRimRunAt(northBaseBevel, ts, wx, wy);
        double northTopRimEast = terrainCameraAwayCrestTopRimRunAt(northBaseBevel, ts, wx + 1, wy);
        TerrainEdge sEdge = cell.edges.s!;
        double sBevel = this.bevelFor(cell, sEdge);
        double insS = sBevel * CARTOON_TERRAIN_STYLE.junctions.earthTerraceBevelRunRatio;
        double northX0 = x0 + contourNw;
        double northX1 = x1 - contourNe;
        double southX0 = x0 + contourSw;
        double southX1 = x1 - contourSe;
        double eastZ0 = z0 + contourNe;
        double eastZ1 = z1 - contourSe;
        double westZ0 = z0 + contourNw;
        double westZ1 = z1 - contourSw;

        /*
         * Emit the last dry wall segment as two welded strips when it meets Water: the visible bank ends on the
         * exact hydraulic profile, while a second strip continues invisibly to the basin support. One unsplit quad
         * interpolated both its foot darkness and face field all the way from the crest to the deep basin. A sloped
         * Water surface then uncovered one of that quad's two interpolation triangles as the reported black wedge.
         */
        bool addProfiledWaterContactFace(
            string direction,
            double alongStart,
            double alongEnd,
            double topStartY,
            double topEndY,
            double bottomY,
            double normalX,
            double normalY,
            double normalZ,
            int color,
            int kind,
            double strength,
            bool actorWall,
            bool preserveEdgeOn,
            bool orbitBackside,
            double crestReferenceY,
            bool requireVisibleDryInterval = false)
        {
            if (cell.type == TileType.Bridge) return false;
            TerrainCell? neighbor = terrainCellAt(
                terrain,
                cell.x + (direction == "e" ? 1 : direction == "w" ? -1 : 0),
                cell.y + (direction == "s" ? 1 : direction == "n" ? -1 : 0));
            if (neighbor == null || !terrainCellCarriesWater(neighbor)) return false;

            double fallback = neighbor.waterLevel ?? neighbor.surfaceZ;
            bool verticalEdge = direction == "e" || direction == "w";
            int subdivisionCount =
                this.vegetationBladeCap <= 0 ? 1 : (int)CARTOON_TERRAIN_STYLE.waterShore.transitionSegments;
            double topAt(double t) => topStartY + (topEndY - topStartY) * t;
            double alongAt(double t) => alongStart + (alongEnd - alongStart) * t;
            P3 pointAt(double t, double y)
            {
                double along = alongAt(t);
                return verticalEdge
                    ? new P3 { x = direction == "e" ? x1 : x0, y = y, z = along }
                    : new P3 { x = along, y = y, z = direction == "s" ? z1 : z0 };
            }
            double waterContactYAt(double t)
            {
                double along = alongAt(t);
                double gridX = verticalEdge
                    ? cell.x + (direction == "e" ? 1 : 0)
                    : cell.x + (along - x0) / ts;
                double gridY = verticalEdge
                    ? cell.y + (along - z0) / ts
                    : cell.y + (direction == "s" ? 1 : 0);
                return visualWaterLevelAt(terrain, gridX, gridY, fallback) * ELEV - 0.025;
            }
            if (requireVisibleDryInterval)
            {
                bool visiblyExposed = false;
                for (int sample = 0; sample <= subdivisionCount; sample++)
                {
                    double t = (double)sample / subdivisionCount;
                    if (topAt(t) > waterContactYAt(t) + 0.001)
                    {
                        visiblyExposed = true;
                        break;
                    }
                }
                if (!visiblyExposed) return false;
            }
            double shadeAt(double y) => wallDepthShadeAt(crestReferenceY, y, false);
            double pigmentCrestY = Math.max(topStartY, topEndY);
            void emitStrip(
                double t0,
                double t1,
                double topY0,
                double topY1,
                double bottomY0,
                double bottomY1)
            {
                if (topY0 <= bottomY0 + 0.001 && topY1 <= bottomY1 + 0.001) return;
                setShade4(shadeAt(topY0), shadeAt(topY1), shadeAt(bottomY1), shadeAt(bottomY0));
                builder.addSurface(
                    new[] { pointAt(t0, topY0), pointAt(t1, topY1), pointAt(t1, bottomY1), pointAt(t0, bottomY0) },
                    normalX,
                    normalY,
                    normalZ,
                    color,
                    kind,
                    strength,
                    SHADE4,
                    UNIT_ZERO,
                    actorWall,
                    preserveEdgeOn,
                    orbitBackside,
                    0,
                    0,
                    bottomY,
                    pigmentCrestY,
                    pigmentCrestY);
            }

            for (int segment = 0; segment < subdivisionCount; segment++)
            {
                double t0 = (double)segment / subdivisionCount;
                double t1 = (double)(segment + 1) / subdivisionCount;
                double topY0 = topAt(t0);
                double topY1 = topAt(t1);
                double rawContactY0 = waterContactYAt(t0);
                double rawContactY1 = waterContactYAt(t1);
                double contactY0 = clamp(rawContactY0, bottomY, topY0);
                double contactY1 = clamp(rawContactY1, bottomY, topY1);

                // The submerged continuation is still terrain-owned, but its own top is the meniscus. Keeping it as a
                // separate strip prevents the basin-depth foot shade from leaking into the visible bank triangle.
                emitStrip(t0, t1, contactY0, contactY1, bottomY, bottomY);

                double gap0 = topY0 - contactY0;
                double gap1 = topY1 - contactY1;
                if (gap0 <= 0.001 && gap1 <= 0.001) continue;
                if (gap0 <= 0.001)
                {
                    double cut = (0.001 - gap0) / Math.max(0.000001, gap1 - gap0);
                    t0 += (t1 - t0) * clamp01(cut);
                    topY0 = topAt(t0);
                    contactY0 = clamp(waterContactYAt(t0), bottomY, topY0 - 0.001);
                    gap0 = topY0 - contactY0;
                }
                if (gap1 <= 0.001)
                {
                    double cut = (gap0 - 0.001) / Math.max(0.000001, gap0 - gap1);
                    t1 = t0 + (t1 - t0) * clamp01(cut);
                    topY1 = topAt(t1);
                    contactY1 = clamp(waterContactYAt(t1), bottomY, topY1 - 0.001);
                    gap1 = topY1 - contactY1;
                }
                if (gap0 > 0.0005 || gap1 > 0.0005)
                {
                    emitStrip(t0, t1, topY0, topY1, contactY0, contactY1);
                }
            }
            return true;
        }

        string? construction = this.tileset?.construction;
        List<TerrainContourPoint>? bridgeFootprint = null;
        if (isBridge)
        {
            // Every layer of a deck consumes ONE footprint. Planks, underside and vertical span backing previously
            // rebuilt x0/x1/z0/z1 independently, so merely enabling the shared contour resolver would have cut the
            // fascia while leaving three rectangular slabs protruding through it.
            bridgeFootprint = hasContour
                ? contourCapInto(
                    x0,
                    x1,
                    z0,
                    z1,
                    capY,
                    contourNw,
                    contourNe,
                    contourSe,
                    contourSw,
                    0,
                    0,
                    0,
                    0)
                : null;
            // The bed comes first: the planks laid on top of it have real gaps, and a gap must show timber.
            addBridgeDeckSoffit(new BridgeSoffitInput
            {
                builder = builder,
                x0 = x0,
                x1 = x1,
                z0 = z0,
                z1 = z1,
                footprint = bridgeFootprint != null ? contourPointsAsP3(bridgeFootprint) : null,
                undersideY = cell.baseZ * ELEV,
                deckY = capY,
                material = material,
                surfaceKind = SURF.bridge,
            });
            if (cell.span == TileType.Floor)
                this.buildBridgeGroundSupport(
                    builder,
                    terrain,
                    cell,
                    material,
                    x0,
                    x1,
                    z0,
                    z1,
                    bridgeFootprint);
            this.buildBridgePlanks(
                builder,
                frame,
                cell,
                material,
                x0,
                z0,
                capY,
                insS,
                capShade,
                bridgeFootprint,
                bridgeTravelNorthSouth);
        }
        else
        {
            bool organicFloor =
                cell.type == TileType.Floor &&
                (construction == "natural" ||
                    construction == "combined-building" ||
                    construction == "prism" ||
                    construction == "viking-ship-village" ||
                    construction == "alien-ranch");
            if (
                this.groundReliefOwns(terrain) &&
                organicFloor &&
                !hasContour &&
                !TERRAIN_GPU_VERTEX_SHAPING &&
                this.visualGrounding &&
                this.terrainSurfaceProfile.organicGround)
            {
                // Fluitown comic look: uneven ground (TerrainGeometryCompiler.Relief.cs).
                this.buildReliefFloorCap(builder, frame, terrain, plan, cell, x0, x1, z0 + insN, z1 - insS, capY, capKind,
                    capStrength, wx, wy, shadeNw, shadeNe, shadeSe, shadeSw, capColorNw, capColorNe, capColorSe, capColorSw);
            }
            else if (
                organicFloor &&
                !hasContour &&
                !TERRAIN_GPU_VERTEX_SHAPING &&
                this.visualGrounding &&
                this.terrainSurfaceProfile.organicGround)
            {
                // Byte-identical worker mirror of the live CPU-authored rolling field. The absolute-world samples and
                // topology mask keep independently compiled cells/tiles watertight without any per-frame vertex noise.
                double northZ = z0 + insN;
                double southZ = z1 - insS;
                int subdivisions = (int)CARTOON_TERRAIN_STYLE.organicShape.subdivisions;
                int row = subdivisions + 1;
                for (int gy = 0; gy <= subdivisions; gy++)
                {
                    double v = (double)gy / subdivisions;
                    double pz = northZ + (southZ - northZ) * v;
                    for (int gx = 0; gx <= subdivisions; gx++)
                    {
                        double u = (double)gx / subdivisions;
                        double px = x0 + (x1 - x0) * u;
                        double topologyMask = terrainOrganicPointMask(
                            terrain,
                            cell.x + u,
                            cell.y + v,
                            cell.surfaceZ);
                        int sampleIndex = gy * row + gx;
                        ORGANIC_HEIGHT_SCRATCH[sampleIndex] =
                            capY + TerrainVisualGround.terrainOrganicHeightAt(px, pz, this.terrainSurfaceProfile) * topologyMask;
                        if (topologyMask > 0.5)
                        {
                            // The derivative kernel is matched to the TESSELLATION, not to the field. A 2.4 px central
                            // difference measures the height field's slope at a point, but the rasteriser then interpolates
                            // that normal linearly over the 20 px between lattice points — so the interpolated normal's
                            // gradient breaks at every lattice line, which is the classic facet crease on a coarsely
                            // displaced surface. Measuring the slope over HALF a sub-quad makes each vertex normal the mean
                            // slope of the span it actually represents. Measured on a flat highland cap (see
                            // `terrainGeometryCompiler.test.ts`): the luminance second difference at the lattice line falls
                            // 2.8x and the cross-axis normal's 5.0x, at zero cost — it is the same two samples.
                            double normalRadius = (x1 - x0) / subdivisions / 2;
                            ORGANIC_NORMAL_X_SCRATCH[sampleIndex] =
                                -(
                                    TerrainVisualGround.terrainOrganicHeightAt(px + normalRadius, pz, this.terrainSurfaceProfile) -
                                    TerrainVisualGround.terrainOrganicHeightAt(px - normalRadius, pz, this.terrainSurfaceProfile)
                                ) /
                                (normalRadius * 2);
                            ORGANIC_NORMAL_Z_SCRATCH[sampleIndex] =
                                -(
                                    TerrainVisualGround.terrainOrganicHeightAt(px, pz + normalRadius, this.terrainSurfaceProfile) -
                                    TerrainVisualGround.terrainOrganicHeightAt(px, pz - normalRadius, this.terrainSurfaceProfile)
                                ) /
                                (normalRadius * 2);
                        }
                        else
                        {
                            ORGANIC_NORMAL_X_SCRATCH[sampleIndex] = 0;
                            ORGANIC_NORMAL_Z_SCRATCH[sampleIndex] = 0;
                        }
                        CAP_SHADE_SCRATCH[sampleIndex] = terrainCapBakeShade(
                            terrain,
                            plan,
                            cell,
                            wx,
                            wy,
                            u,
                            v,
                            capCornerShade(shadeNw, shadeNe, shadeSe, shadeSw, u, v));
                        CAP_COLOR_SCRATCH[sampleIndex] = livingGroundPigmentAt(
                            this.groundDetail,
                            capCornerColor(capColorNw, capColorNe, capColorSe, capColorSw, u, v),
                            plan,
                            cell,
                            u,
                            v,
                            planMoistureAt(plan, cell.id),
                            terrain);
                        int groundSlot = sampleIndex * TERRAIN_GROUND_CHANNEL_STRIDE;
                        CAP_GROUND_SCRATCH[groundSlot] = turfCoverAt(
                            this.groundDetail,
                            plan,
                            cell,
                            px,
                            pz,
                            u,
                            v);
                        var surface = groundSurfaceVectorAt(this.groundDetail, plan, cell, u, v);
                        CAP_GROUND_SCRATCH[groundSlot + 1] = surface.wear;
                        CAP_GROUND_SCRATCH[groundSlot + 2] = surface.tangentX;
                        CAP_GROUND_SCRATCH[groundSlot + 3] = surface.tangentZ;
                    }
                }
                // ONE welded patch. Each interior lattice point is a single vertex shared by its two or four
                // sub-quads, instead of two or four copies of the same position re-deriving the same colour, normal
                // and turf from the same scratch entry: 9 vertices for the 2x2 patch instead of 16, identical
                // triangles, identical positions, no extra draw call.
                for (int index = 0; index < row * row; index++)
                {
                    int gx = index % row;
                    int gy = (index - gx) / row;
                    P3 point = ORGANIC_LATTICE_POINTS[index];
                    point.x = x0 + ((x1 - x0) * gx) / subdivisions;
                    point.y = ORGANIC_HEIGHT_SCRATCH[index];
                    point.z = northZ + ((southZ - northZ) * gy) / subdivisions;
                }
                builder.addSurfaceLattice(
                    ORGANIC_LATTICE_POINTS,
                    row,
                    ORGANIC_NORMAL_X_SCRATCH,
                    ORGANIC_NORMAL_Z_SCRATCH,
                    CAP_COLOR_SCRATCH,
                    capKind,
                    capStrength,
                    CAP_SHADE_SCRATCH,
                    CAP_GROUND_SCRATCH);
            }
            else
            {
                if (!isBridge && !hasContour)
                {
                    // Mirror the live shared-corner cap for ground and massive wall plateaus.
                    setShade4(shadeNw, shadeNe, shadeSe, shadeSw);
                }
                else
                {
                    setShade4(capShade, capShade, capShade, capShade);
                }
                IReadOnlyList<P3> capPoints = hasContour
                    ? contourPointsAsP3(contourCapInto(
                        x0,
                        x1,
                        z0 + insN,
                        z1 - insS,
                        capY,
                        contourNw,
                        contourNe,
                        contourSe,
                        contourSw,
                        contourN,
                        contourE,
                        contourS,
                        contourW))
                    : quad(
                        x0,
                        capY,
                        z0 + insN,
                        x1,
                        capY,
                        z0 + insN,
                        x1,
                        capY,
                        z1 - insS,
                        x0,
                        capY,
                        z1 - insS);
                for (int index = 0; index < capPoints.Count; index++)
                {
                    P3 point = capPoints[index];
                    double u = clamp((point.x - x0) / Math.max(0.001, x1 - x0), 0, 1);
                    double v = clamp((point.z - (z0 + insN)) / Math.max(0.001, z1 - insS - (z0 + insN)), 0, 1);
                    CAP_SHADE_SCRATCH[index] = terrainCapBakeShade(
                        terrain,
                        plan,
                        cell,
                        wx,
                        wy,
                        u,
                        v,
                        !isBridge
                            ? capCornerShade(shadeNw, shadeNe, shadeSe, shadeSw, u, v)
                            : (optAt(SHADE4, index) ?? SHADE4[0]));
                    CAP_COLOR_SCRATCH[index] = livingGroundPigmentAt(
                        this.groundDetail,
                        !isBridge
                            ? capCornerColor(capColorNw, capColorNe, capColorSe, capColorSw, u, v)
                            : capColor,
                        plan,
                        cell,
                        u,
                        v,
                        planMoistureAt(plan, cell.id),
                        terrain);
                    int groundSlot = index * TERRAIN_GROUND_CHANNEL_STRIDE;
                    // TS: `const surface = isBridge ? undefined : groundSurfaceVectorAt(...)`, read as `surface?.x ?? d`.
                    double surfaceWear = 0;
                    double surfaceTangentX = 1;
                    double surfaceTangentZ = 0;
                    if (!isBridge)
                    {
                        var surface = groundSurfaceVectorAt(this.groundDetail, plan, cell, u, v);
                        surfaceWear = surface.wear;
                        surfaceTangentX = surface.tangentX;
                        surfaceTangentZ = surface.tangentZ;
                    }
                    CAP_GROUND_SCRATCH[groundSlot] = isBridge
                        ? 0
                        : turfCoverAt(this.groundDetail, plan, cell, point.x, point.z, u, v);
                    CAP_GROUND_SCRATCH[groundSlot + 1] = surfaceWear;
                    CAP_GROUND_SCRATCH[groundSlot + 2] = surfaceTangentX;
                    CAP_GROUND_SCRATCH[groundSlot + 3] = surfaceTangentZ;
                }
                builder.addSurface(
                    capPoints,
                    0,
                    1,
                    0,
                    CAP_COLOR_SCRATCH,
                    capKind,
                    capStrength,
                    CAP_SHADE_SCRATCH,
                    UNIT_ZERO,
                    isRock,
                    false,
                    false,
                    0,
                    CAP_GROUND_SCRATCH);
            }
        }

        if (nBevel > 0)
        {
            P3[] shoulder = quad(
                northX1,
                capY,
                z0 + insN,
                northX0,
                capY,
                z0 + insN,
                northX0,
                capY - nBevel,
                z0,
                northX1,
                capY - nBevel,
                z0);
            var shoulderColors = this.cameraAwayCrestColors(
                shoulder,
                cell,
                capColor,
                material.edgeDark);
            setShade4(
                capShade,
                capShade,
                capShade * northCrestStyle.outerShade,
                capShade * northCrestStyle.outerShade);
            builder.addSurface(
                shoulder,
                0,
                insN,
                -nBevel,
                // A continuous material-colour gradient plus the geometric slope normal makes this read as one rolled
                // mass. No screen-facing line is needed, so long runs and topology corners cannot fragment or alias.
                shoulderColors,
                capKind,
                capStrength,
                SHADE4,
                UNIT_ZERO,
                isRock,
                false,
                true);

            // Default-yaw depth hides the descending shoulder behind its own high cap. The matching top-plane rim
            // is therefore the visible comic contour; it remains in this existing opaque surface batch.
            double rimY = capY + northCrestStyle.topRimLiftPx;
            P3[] topRim = quad(
                northX1,
                rimY,
                z0 + insN + northTopRimEast,
                northX0,
                rimY,
                z0 + insN + northTopRimWest,
                northX0,
                rimY,
                z0 + insN,
                northX1,
                rimY,
                z0 + insN);
            double westRimMix = terrainCrestTopRimOuterMixAt(wx, wy, "x");
            double eastRimMix = terrainCrestTopRimOuterMixAt(wx + 1, wy, "x");
            var topRimColors = this.cameraAwayCrestColors(
                topRim,
                cell,
                capColor,
                material.edgeDark,
                true,
                westRimMix,
                eastRimMix);
            double westRimShade = terrainCrestTopRimShadeAt(wx, wy, "x");
            double eastRimShade = terrainCrestTopRimShadeAt(wx + 1, wy, "x");
            setShade4(capShade, capShade, capShade * westRimShade, capShade * eastRimShade);
            builder.addSurface(
                topRim,
                0,
                1,
                0,
                topRimColors,
                capKind,
                capStrength,
                SHADE4,
                UNIT_ZERO,
                false);
        }

        if (sBevel > 0)
        {
            setShade4(capShade, capShade, capShade, capShade);
            builder.addSurface(
                quad(
                    southX0,
                    capY,
                    z1 - insS,
                    southX1,
                    capY,
                    z1 - insS,
                    southX1,
                    capY - sBevel,
                    z1,
                    southX0,
                    capY - sBevel,
                    z1),
                0,
                insS,
                sBevel,
                // Fluitown comic look: the bevel is the cap's own rounded edge. The original's lift towards the edge
                // light (a lip for its paper wash) read as a pale cream strip along every terrace under the comic ramp.
                mix(capColor, material.edgeLight, TerrainComicGeometry.ClosedShells ? 0.06 : 0.42),
                capKind,
                capStrength,
                SHADE4,
                UNIT_ZERO,
                isRock);
        }
        if (TerrainComicGeometry.ClosedShells && !isBridge)
            this.addCrestBevelEndCaps(
                builder,
                terrain,
                cell,
                material,
                x0,
                x1,
                z0,
                z1,
                capY,
                nBevel,
                insN,
                sBevel,
                insS,
                contourNw,
                contourNe,
                contourSe,
                contourSw,
                capColor,
                capKind,
                capStrength);

        // South face: true vertical geometry from the (bevelled) crest down to the neighbour. The baked crest→foot
        // gradient grounds the wall (bright break line up top, weight sinking into the contact at the base).
        if (
            sEdge.visibleFace &&
            sEdge.drop > 0.02 &&
            !(sEdge.contactType == TileType.Water && sEdge.drop < 0.03))
        {
            double bottomY = this.faceBottomY(terrain, cell, "s", sEdge);
            double topY = capY - sBevel;
            if (topY > bottomY)
            {
                bool chasmEdge = sEdge.faceSegments.some(candidate => candidate.role == "chasm");
                bool seamlessChasmFace = chasmEdge && isRock;
                double chasmDatumY =
                    (sEdge.faceSegments.find(candidate => candidate.role == "chasm")?.fromZ ?? 0) * ELEV;
                int segmentCount = sEdge.faceSegments.Count;
                for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                {
                    TerrainFaceSegment segment = sEdge.faceSegments[segmentIndex];
                    double segmentTopY = segmentIndex == 0 ? topY : segment.fromZ * ELEV;
                    double segmentBottomY =
                        segmentIndex == sEdge.faceSegments.Count - 1 ? bottomY : segment.toZ * ELEV;
                    if (segmentTopY <= segmentBottomY + 0.02) continue;
                    bool chasmFace = chasmEdge;

                    TerrainMaterial segmentMaterial = chasmFace
                        ? resolveChasmFaceMaterial(terrain, plan, cell, "s")
                        : material;
                    string segmentRole = chasmFace ? chasmContinuationMaterial(cell, sEdge) : segment.material;
                    bool bridgeRiser = isBridge && sEdge.contactType == TileType.Bridge;
                    double crestShade = bridgeRiser
                        ? 0.98
                        : wallDepthShadeAt(topY, segmentTopY, chasmFace, chasmDatumY);
                    double foot = bridgeRiser
                        ? 0.78
                        : wallDepthShadeAt(topY, segmentBottomY, chasmFace, chasmDatumY);
                    setShade4(crestShade, crestShade, foot, foot);
                    int baseFaceColor = terrainFaceBaseColor(segmentMaterial, segmentRole);
                    // A height change inside one deck is a timber stair riser, not the shaded outside of a terrain
                    // volume. Carry the tread pigment down its face so it reads as carpentry instead of a black hole.
                    int faceColor = bridgeRiser ? mix(baseFaceColor, capColor, 0.48) : baseFaceColor;
                    int faceKind =
                        chasmFace && !isRock
                            ? SURF.chasmWall
                            : isBridge
                                ? SURF.bridge
                                : isRock || segment.material == "rock"
                                    ? SURF.rockFace
                                    : SURF.earthFace;
                    double faceStrength =
                        chasmFace && !isRock
                            ? chasmWallPatternStrength(segmentRole)
                            : isBridge
                                ? capStrength
                                : 0.17;
                    bool profiledWaterContact =
                        segmentIndex == segmentCount - 1 &&
                        addProfiledWaterContactFace(
                            direction: "s",
                            alongStart: southX0,
                            alongEnd: southX1,
                            topStartY: segmentTopY,
                            topEndY: segmentTopY,
                            bottomY: segmentBottomY,
                            normalX: 0,
                            normalY: 0,
                            normalZ: 1,
                            color: faceColor,
                            kind: faceKind,
                            strength: faceStrength,
                            actorWall: isRock,
                            preserveEdgeOn: false,
                            orbitBackside: false,
                            crestReferenceY: topY);
                    if (!profiledWaterContact)
                    {
                        builder.addSurface(
                            quad(
                                southX0,
                                segmentTopY,
                                z1,
                                southX1,
                                segmentTopY,
                                z1,
                                southX1,
                                segmentBottomY,
                                z1,
                                southX0,
                                segmentBottomY,
                                z1),
                            0,
                            0,
                            1,
                            faceColor,
                            faceKind,
                            faceStrength,
                            SHADE4,
                            UNIT_ZERO,
                            isRock,
                            false,
                            false,
                            0,
                            0,
                            seamlessChasmFace ? chasmDatumY : (double?)null,
                            seamlessChasmFace ? topY : (double?)null);
                    }
                    // Decorative bedding is a straight-run treatment. At a topology turn it used to terminate against
                    // the diagonal closure as several parallel chevrons, making the structurally valid corner look like
                    // overlapping cards. The clean junction owns the silhouette; strata resume on the next straight cell.
                    if (!hasContour && ((!chasmFace && isRock) || chasmFace))
                    {
                        if (!chasmFace)
                        {
                            this.addNormalSouthFaceDetail(
                                builder,
                                wx,
                                wy,
                                southX0,
                                southX1,
                                z1,
                                segmentTopY,
                                segmentBottomY,
                                faceColor,
                                foot);
                        }
                        this.addLivingWallDetail(
                            builder,
                            "s",
                            wx,
                            wy,
                            x0,
                            x1,
                            z0,
                            z1,
                            segmentTopY,
                            segmentBottomY,
                            chasmFace);
                    }
                }
                if (
                    !hasContour &&
                    isRock &&
                    !sEdge.faceSegments.some(segment => segment.role == "chasm"))
                {
                    addWallDepthShelves(builder, new WallDepthShelfOptions
                    {
                        direction = "s",
                        cellX = wx,
                        cellY = wy,
                        x0 = southX0,
                        x1 = southX1,
                        z0 = z0,
                        z1 = z1,
                        topY = topY,
                        bottomY = bottomY,
                        faceColor = terrainFaceBaseColor(material, sEdge.material),
                        material = material,
                    });
                }
                this.addContactStrip(builder, frame, cell, "s", sEdge, contourSw, contourSe, material);
            }
        }

        // Closed north wall for the existing 360-degree presentation orbit. Opposite projected winding deliberately
        // keeps it back-face culled in ordinary gameplay; after a half turn it becomes the visible shell, carrying
        // the same material split, foot weight, shadow casting and actor-occlusion truth as the south frontage.
        if (
            nEdge.visibleFace &&
            nEdge.drop > 0.02 &&
            !(nEdge.contactType == TileType.Water && nEdge.drop < 0.03))
        {
            double bottomY = this.faceBottomY(terrain, cell, "n", nEdge);
            double topY = capY - nBevel;
            if (topY > bottomY)
            {
                bool chasmEdge = nEdge.faceSegments.some(candidate => candidate.role == "chasm");
                bool seamlessChasmFace = chasmEdge && isRock;
                double chasmDatumY =
                    (nEdge.faceSegments.find(candidate => candidate.role == "chasm")?.fromZ ?? 0) * ELEV;
                int segmentCount = nEdge.faceSegments.Count;
                for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                {
                    TerrainFaceSegment segment = nEdge.faceSegments[segmentIndex];
                    double segmentTopY = segmentIndex == 0 ? topY : segment.fromZ * ELEV;
                    double segmentBottomY =
                        segmentIndex == nEdge.faceSegments.Count - 1 ? bottomY : segment.toZ * ELEV;
                    if (segmentTopY <= segmentBottomY + 0.02) continue;
                    bool chasmFace = chasmEdge;
                    TerrainMaterial segmentMaterial = chasmFace
                        ? resolveChasmFaceMaterial(terrain, plan, cell, "n")
                        : material;
                    string segmentRole = chasmFace ? chasmContinuationMaterial(cell, nEdge) : segment.material;
                    bool bridgeRiser = isBridge && nEdge.contactType == TileType.Bridge;
                    double crestShade = bridgeRiser
                        ? 0.98
                        : wallDepthShadeAt(topY, segmentTopY, chasmFace, chasmDatumY);
                    double foot = bridgeRiser
                        ? 0.78
                        : wallDepthShadeAt(topY, segmentBottomY, chasmFace, chasmDatumY);
                    setShade4(crestShade, crestShade, foot, foot);
                    int baseFaceColor = terrainFaceBaseColor(segmentMaterial, segmentRole);
                    int faceColor = bridgeRiser ? mix(baseFaceColor, capColor, 0.48) : baseFaceColor;
                    int faceKind =
                        chasmFace && !isRock
                            ? SURF.chasmWall
                            : isBridge
                                ? SURF.bridge
                                : isRock || segment.material == "rock"
                                    ? SURF.rockFace
                                    : SURF.earthFace;
                    double faceStrength =
                        chasmFace && !isRock
                            ? chasmWallPatternStrength(segmentRole)
                            : isBridge
                                ? capStrength
                                : 0.17;
                    bool profiledWaterContact =
                        segmentIndex == segmentCount - 1 &&
                        addProfiledWaterContactFace(
                            direction: "n",
                            alongStart: northX0,
                            alongEnd: northX1,
                            topStartY: segmentTopY,
                            topEndY: segmentTopY,
                            bottomY: segmentBottomY,
                            normalX: 0,
                            normalY: 0,
                            normalZ: -1,
                            color: faceColor,
                            kind: faceKind,
                            strength: faceStrength,
                            actorWall: isRock,
                            preserveEdgeOn: false,
                            orbitBackside: true,
                            crestReferenceY: topY);
                    if (!profiledWaterContact)
                    {
                        builder.addSurface(
                            quad(
                                northX1,
                                segmentTopY,
                                z0,
                                northX0,
                                segmentTopY,
                                z0,
                                northX0,
                                segmentBottomY,
                                z0,
                                northX1,
                                segmentBottomY,
                                z0),
                            0,
                            0,
                            -1,
                            faceColor,
                            faceKind,
                            faceStrength,
                            SHADE4,
                            UNIT_ZERO,
                            isRock,
                            false,
                            true,
                            0,
                            0,
                            seamlessChasmFace ? chasmDatumY : (double?)null,
                            seamlessChasmFace ? topY : (double?)null);
                    }
                    if (!hasContour && ((!chasmFace && isRock) || chasmFace))
                    {
                        this.addLivingWallDetail(
                            builder,
                            "n",
                            wx,
                            wy,
                            x0,
                            x1,
                            z0,
                            z1,
                            segmentTopY,
                            segmentBottomY,
                            chasmFace);
                    }
                }
                if (
                    !hasContour &&
                    isRock &&
                    !nEdge.faceSegments.some(segment => segment.role == "chasm"))
                {
                    addWallDepthShelves(builder, new WallDepthShelfOptions
                    {
                        direction = "n",
                        cellX = wx,
                        cellY = wy,
                        x0 = northX0,
                        x1 = northX1,
                        z0 = z0,
                        z1 = z1,
                        topY = topY,
                        bottomY = bottomY,
                        faceColor = terrainFaceBaseColor(material, nEdge.material),
                        material = material,
                    });
                }
                this.addContactStrip(builder, frame, cell, "n", nEdge, contourNw, contourNe, material);
            }
        }

        // East/west faces may be exactly edge-on in the default projection, but the presentation orbit exposes
        // them. Keep those real faces in the bake; Chasm continuations also keep crest and foot on one exact datum.
        foreach (string dir in new[] { "e", "w" })
        {
            TerrainEdge edge = cell.edges[dir]!;
            if (
                !edge.visibleFace ||
                edge.drop <= 0.02 ||
                (edge.contactType == TileType.Water && edge.drop < 0.03))
                continue;
            double bx = dir == "e" ? x1 : x0;
            double faceZ0 = dir == "e" ? eastZ0 : westZ0;
            double faceZ1 = dir == "e" ? eastZ1 : westZ1;
            bool chasmEdge = edge.faceSegments.some(segment => segment.role == "chasm");
            bool seamlessChasmFace = chasmEdge && isRock;
            double chasmDatumY =
                (edge.faceSegments.find(segment => segment.role == "chasm")?.fromZ ?? 0) * ELEV;
            double bottomY = this.faceBottomY(terrain, cell, dir, edge);
            double northDropY = nBevel > 0 ? nBevel : 0;
            double southDropY = sBevel > 0 ? sBevel : 0;

            // Keep the exact structural datum (and therefore the wedge fix) while restoring the reference image's
            // readable E/W contour with a narrow, in-cap pigment rim.
            double edgeBaseBevel = this.bevelFor(cell, edge);
            int edgeVertexX = dir == "e" ? wx + 1 : wx;
            double edgeOnRimNorth = terrainEdgeOnCrestTopRimRunAt(edgeBaseBevel, ts, edgeVertexX, wy);
            double edgeOnRimSouth = terrainEdgeOnCrestTopRimRunAt(edgeBaseBevel, ts, edgeVertexX, wy + 1);
            // The inward FOLD that used to lean every land east/west wall is gone, and with it the reported seam.
            //
            // It existed because "every land east/west wall is exactly edge-on at gameplay yaw": a mathematically
            // flat side wall showed the ground through its raster joint, so the wall was tilted inward to gain
            // projected coverage. That premise died when the world gained its yaw — the side wall now has real
            // width of its own. What the fold still did was move the side wall's corner while its north/south
            // frontage stayed put, so the two faces no longer shared an edge and a wedge opened between them,
            // widening toward the foot where the lean is strongest. That wedge is the "gap" a player sees at
            // every side-to-front junction; removing the fold welds 38 of the 47 failing junction variations.
            double northCornerRimRun = dir == "e" ? northTopRimEast : northTopRimWest;
            double rimZ0 = Math.max(faceZ0, z0 + (nBevel > 0 ? insN + northCornerRimRun : 0));
            double rimZ1 = Math.min(faceZ1, z1 - (sBevel > 0 ? insS : 0));
            if (edgeOnRimNorth > 0 && edgeOnRimSouth > 0 && rimZ1 > rimZ0 + 0.1)
            {
                double rimY = capY + northCrestStyle.topRimLiftPx;
                P3[] sideRim =
                    dir == "e"
                        ? quad(
                            x1 - edgeOnRimSouth,
                            rimY,
                            rimZ1,
                            x1 - edgeOnRimNorth,
                            rimY,
                            rimZ0,
                            x1,
                            rimY,
                            rimZ0,
                            x1,
                            rimY,
                            rimZ1)
                        : quad(
                            x0 + edgeOnRimNorth,
                            rimY,
                            rimZ0,
                            x0 + edgeOnRimSouth,
                            rimY,
                            rimZ1,
                            x0,
                            rimY,
                            rimZ1,
                            x0,
                            rimY,
                            rimZ0);
                double northRimMix = terrainCrestTopRimOuterMixAt(edgeVertexX, wy, "z");
                double southRimMix = terrainCrestTopRimOuterMixAt(edgeVertexX, wy + 1, "z");
                var sideRimColors = this.cameraAwayCrestColors(
                    sideRim,
                    cell,
                    capColor,
                    material.edgeDark,
                    true,
                    dir == "e" ? northRimMix : southRimMix,
                    dir == "e" ? southRimMix : northRimMix);
                double northRimShade = terrainCrestTopRimShadeAt(edgeVertexX, wy, "z");
                double southRimShade = terrainCrestTopRimShadeAt(edgeVertexX, wy + 1, "z");
                if (dir == "e")
                {
                    setShade4(capShade, capShade, capShade * northRimShade, capShade * southRimShade);
                }
                else
                {
                    setShade4(capShade, capShade, capShade * southRimShade, capShade * northRimShade);
                }
                builder.addSurface(
                    sideRim,
                    0,
                    1,
                    0,
                    sideRimColors,
                    capKind,
                    capStrength,
                    SHADE4,
                    UNIT_ZERO,
                    false);

                // Mirror the main contour footprint exactly: an organic stair corner owns one continuous pooled rim
                // instead of two ribbons ending at opposite sides of an unshaded arc.
                double northCornerCut = dir == "e" ? contourNe : contourNw;
                if (nBevel > 0 && northCornerCut > 0)
                {
                    double northOuterShade = terrainCrestTopRimShadeAt(dir == "e" ? wx + 1 : wx, wy, "x");
                    double northOuterMix = terrainCrestTopRimOuterMixAt(dir == "e" ? wx + 1 : wx, wy, "x");
                    P3[] cornerRim =
                        dir == "e"
                            ? quad(
                                northX1,
                                rimY,
                                z0 + insN + northTopRimEast,
                                x1 - edgeOnRimNorth,
                                rimY,
                                eastZ0,
                                x1,
                                rimY,
                                eastZ0,
                                northX1,
                                rimY,
                                z0 + insN)
                            : quad(
                                x0 + edgeOnRimNorth,
                                rimY,
                                westZ0,
                                northX0,
                                rimY,
                                z0 + insN + northTopRimWest,
                                northX0,
                                rimY,
                                z0 + insN,
                                x0,
                                rimY,
                                westZ0);
                    var cornerRimColors = this.cameraAwayCrestColors(
                        cornerRim,
                        cell,
                        capColor,
                        material.edgeDark,
                        true,
                        dir == "e" ? northRimMix : northOuterMix,
                        dir == "e" ? northOuterMix : northRimMix);
                    if (dir == "e")
                    {
                        setShade4(capShade, capShade, capShade * northRimShade, capShade * northOuterShade);
                    }
                    else
                    {
                        setShade4(capShade, capShade, capShade * northOuterShade, capShade * northRimShade);
                    }
                    builder.addSurface(
                        cornerRim,
                        0,
                        1,
                        0,
                        cornerRimColors,
                        capKind,
                        capStrength,
                        SHADE4,
                        UNIT_ZERO,
                        false);
                }
            }
            // North/south crest bevels move the horizontal cap inward, while this cardinal side wall keeps its
            // topology edge. Joining only the two CORNER chords leaves the straight E/W segment between them open:
            // under the presentation yaw that missing ribbon projects as the thin backdrop-coloured triangle at a
            // Floor/Floor terrace turn. Close the entire edge with one shoulder whose four datums are taken from the
            // same cap and wall endpoints — no epsilon skirt and no separately invented corner rule.
            double capFaceZ0 = faceZ0 + (nBevel > 0 ? insN : 0);
            double capFaceZ1 = faceZ1 - (sBevel > 0 ? insS : 0);
            if (
                (nBevel > 0.01 || sBevel > 0.01) &&
                capFaceZ1 > capFaceZ0 + 0.02 &&
                faceZ1 > faceZ0 + 0.02)
            {
                setShade4(capShade, capShade, capShade * 0.94, capShade * 0.94);
                builder.addSurface(
                    dir == "e"
                        ? quad(
                            bx,
                            capY,
                            capFaceZ0,
                            bx,
                            capY,
                            capFaceZ1,
                            bx,
                            capY - sBevel,
                            faceZ1,
                            bx,
                            capY - nBevel,
                            faceZ0)
                        : quad(
                            bx,
                            capY,
                            capFaceZ1,
                            bx,
                            capY,
                            capFaceZ0,
                            bx,
                            capY - nBevel,
                            faceZ0,
                            bx,
                            capY - sBevel,
                            faceZ1),
                    dir == "e" ? 1 : -1,
                    CARTOON_TERRAIN_STYLE.junctions.contourFaceNormalY,
                    0,
                    // Fluitown comic look: the cap's own rounded edge (see the south bevel above), not a pale lip.
                    mix(capColor, material.edgeLight, TerrainComicGeometry.ClosedShells ? 0.06 : 0.34),
                    capKind,
                    capStrength,
                    SHADE4,
                    UNIT_ZERO,
                    isRock,
                    isBridge);
            }
            int segmentCount = edge.faceSegments.Count;
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                TerrainFaceSegment segment = edge.faceSegments[segmentIndex];
                double segmentTopY = segmentIndex == 0 ? capY : segment.fromZ * ELEV;
                double segmentBottomY =
                    segmentIndex == edge.faceSegments.Count - 1 ? bottomY : segment.toZ * ELEV;
                if (segmentTopY <= segmentBottomY + 0.02) continue;
                bool chasmFace = chasmEdge;
                TerrainMaterial segmentMaterial = chasmFace
                    ? resolveChasmFaceMaterial(terrain, plan, cell, dir)
                    : material;
                string segmentRole = chasmFace ? chasmContinuationMaterial(cell, edge) : segment.material;
                bool bridgeRiser = isBridge && edge.contactType == TileType.Bridge;
                double crestShade = bridgeRiser
                    ? 0.98
                    : wallDepthShadeAt(capY, segmentTopY, chasmFace, chasmDatumY);
                double foot = bridgeRiser
                    ? 0.78
                    : wallDepthShadeAt(capY, segmentBottomY, chasmFace, chasmDatumY);
                setShade4(crestShade, crestShade, foot, foot);
                int baseEwFaceColor = terrainFaceBaseColor(segmentMaterial, segmentRole);
                int ewFaceColor = bridgeRiser ? mix(baseEwFaceColor, capColor, 0.48) : baseEwFaceColor;
                int faceKind =
                    chasmFace && !isRock
                        ? SURF.chasmWall
                        : isBridge
                            ? SURF.bridge
                            : isRock || segment.material == "rock"
                                ? SURF.rockFace
                                : SURF.earthFace;
                double faceStrength =
                    chasmFace && !isRock
                        ? chasmWallPatternStrength(segmentRole)
                        : isBridge
                            ? capStrength
                            : 0.17;
                bool occludesActor = isRock;
                bool profiledWaterContact =
                    segmentIndex == segmentCount - 1 &&
                    addProfiledWaterContactFace(
                        direction: dir,
                        alongStart: faceZ0,
                        alongEnd: faceZ1,
                        topStartY: segmentTopY - (segmentIndex == 0 ? northDropY : 0),
                        topEndY: segmentTopY - (segmentIndex == 0 ? southDropY : 0),
                        bottomY: segmentBottomY,
                        normalX: dir == "e" ? 1 : -1,
                        normalY: 0.28,
                        normalZ: 0,
                        color: ewFaceColor,
                        kind: faceKind,
                        strength: faceStrength,
                        actorWall: occludesActor,
                        preserveEdgeOn: isBridge,
                        orbitBackside: false,
                        crestReferenceY: capY);
                if (!profiledWaterContact)
                {
                    builder.addSurface(
                        quad(
                            bx,
                            segmentTopY - (segmentIndex == 0 ? northDropY : 0),
                            faceZ0,
                            bx,
                            segmentTopY - (segmentIndex == 0 ? southDropY : 0),
                            faceZ1,
                            bx,
                            segmentBottomY,
                            faceZ1,
                            bx,
                            segmentBottomY,
                            faceZ0),
                        dir == "e" ? 1 : -1,
                        0.28,
                        0,
                        ewFaceColor,
                        faceKind,
                        faceStrength,
                        SHADE4,
                        UNIT_ZERO,
                        occludesActor,
                        isBridge,
                        false,
                        0,
                        0,
                        seamlessChasmFace ? chasmDatumY : (double?)null,
                        seamlessChasmFace ? capY : (double?)null);
                }
                if (nEdge.visibleFace)
                {
                    // A contoured corner ends the side wall at faceZ0, not at the removed square corner z0. Keeping the
                    // return on z0 left a detached, full-depth dark card hanging below the grey diagonal chamfer.
                    this.addLeaningFaceReturn(
                        builder,
                        "n",
                        bx,
                        0,
                        0,
                        segmentTopY - (segmentIndex == 0 ? northDropY : 0),
                        segmentBottomY,
                        faceZ0,
                        mix(ewFaceColor, segmentMaterial.edgeDark, 0.035),
                        faceKind,
                        faceStrength,
                        occludesActor,
                        CARTOON_TERRAIN_STYLE.junctions.heightReturnNormalY,
                        CARTOON_TERRAIN_STYLE.junctions.heightReturnFootShade);
                }
                if (sEdge.visibleFace)
                {
                    // Match the south return to the clipped side endpoint for the same reason. With no contour faceZ1 is
                    // exactly z1, so ordinary square height joints retain their existing watertight closure.
                    this.addLeaningFaceReturn(
                        builder,
                        "s",
                        bx,
                        0,
                        0,
                        segmentTopY - (segmentIndex == 0 ? southDropY : 0),
                        segmentBottomY,
                        faceZ1,
                        mix(ewFaceColor, segmentMaterial.edgeDark, 0.06),
                        faceKind,
                        faceStrength,
                        occludesActor,
                        CARTOON_TERRAIN_STYLE.junctions.heightReturnNormalY,
                        CARTOON_TERRAIN_STYLE.junctions.heightReturnFootShade);
                }
                if (!hasContour && ((!chasmFace && isRock) || chasmFace))
                {
                    this.addLivingWallDetail(
                        builder,
                        dir,
                        wx,
                        wy,
                        x0,
                        x1,
                        z0,
                        z1,
                        segmentTopY,
                        segmentBottomY,
                        chasmFace);
                }
            }

            // (The horizontal landing that used to close the inward fold went with it: a wall standing in its
            // own plane needs no ledge to bridge back to the tile boundary.)

            if (!hasContour && isRock && !chasmEdge)
            {
                addWallDepthShelves(builder, new WallDepthShelfOptions
                {
                    direction = dir,
                    cellX = wx,
                    cellY = wy,
                    x0 = x0,
                    x1 = x1,
                    z0 = faceZ0,
                    z1 = faceZ1,
                    topY = capY - Math.max(northDropY, southDropY),
                    bottomY = bottomY,
                    faceColor = terrainFaceBaseColor(material, edge.material),
                    material = material,
                });
            }

            this.addContactStrip(
                builder,
                frame,
                cell,
                dir,
                edge,
                dir == "e" ? contourNe : contourNw,
                dir == "e" ? contourSe : contourSw,
                material);
        }

        // A stored shore datum can be level with the dry cap while the shared hydraulic field bends downward
        // nearby toward a much lower Water cell. The model edge then has no ordinary wall to emit, but the visible
        // spline exposes a real triangular interval. Author that interval from the dry owner exactly like every
        // other profiled wet bank; otherwise the only fragment behind it is the abyss safety foundation.
        if (!isBridge)
        {
            int nearLevelWaterFaceColor = terrainFaceBaseColor(material, isRock ? "rock" : "earth");
            // This is dry terrain revealed by the hydraulic curve, not an inner basin shell. Keep the owner's ordinary
            // geological surface family so its pattern and pigment continue the adjacent wall; `waterBank` invokes the
            // blue basin luminance treatment and turned the repaired interval into a conspicuous cyan triangle.
            int nearLevelWaterFaceKind = isRock ? SURF.rockFace : SURF.earthFace;
            void addNearLevelWaterFace(string dir)
            {
                TerrainEdge edge = cell.edges[dir]!;
                if (edge.contactType != TileType.Water || edge.drop >= 0.03) return;
                double bottomY = this.faceBottomY(terrain, cell, dir, edge);
                if (dir == "s")
                {
                    addProfiledWaterContactFace(
                        direction: dir,
                        alongStart: southX0,
                        alongEnd: southX1,
                        topStartY: capY,
                        topEndY: capY,
                        bottomY: bottomY,
                        normalX: 0,
                        normalY: 0,
                        normalZ: 1,
                        color: nearLevelWaterFaceColor,
                        kind: nearLevelWaterFaceKind,
                        strength: 0.17,
                        actorWall: isRock,
                        preserveEdgeOn: false,
                        orbitBackside: false,
                        crestReferenceY: capY,
                        requireVisibleDryInterval: true);
                    return;
                }
                if (dir == "n")
                {
                    addProfiledWaterContactFace(
                        direction: dir,
                        alongStart: northX0,
                        alongEnd: northX1,
                        topStartY: capY,
                        topEndY: capY,
                        bottomY: bottomY,
                        normalX: 0,
                        normalY: 0,
                        normalZ: -1,
                        color: nearLevelWaterFaceColor,
                        kind: nearLevelWaterFaceKind,
                        strength: 0.17,
                        actorWall: isRock,
                        preserveEdgeOn: false,
                        orbitBackside: true,
                        crestReferenceY: capY,
                        requireVisibleDryInterval: true);
                    return;
                }
                addProfiledWaterContactFace(
                    direction: dir,
                    alongStart: dir == "e" ? eastZ0 : westZ0,
                    alongEnd: dir == "e" ? eastZ1 : westZ1,
                    topStartY: capY,
                    topEndY: capY,
                    bottomY: bottomY,
                    normalX: dir == "e" ? 1 : -1,
                    normalY: 0.28,
                    normalZ: 0,
                    color: nearLevelWaterFaceColor,
                    kind: nearLevelWaterFaceKind,
                    strength: 0.17,
                    actorWall: isRock,
                    preserveEdgeOn: false,
                    orbitBackside: false,
                    crestReferenceY: capY,
                    requireVisibleDryInterval: true);
            }
            addNearLevelWaterFace("n");
            addNearLevelWaterFace("e");
            addNearLevelWaterFace("s");
            addNearLevelWaterFace("w");
        }

        if (contourNw > 0.01)
            this.addContourCornerFace(
                builder,
                terrain,
                plan,
                cell,
                material,
                "nw",
                contourNw,
                x0,
                x1,
                z0,
                z1,
                capY,
                insN,
                capY - nBevel,
                isRock);
        if (contourNe > 0.01)
            this.addContourCornerFace(
                builder,
                terrain,
                plan,
                cell,
                material,
                "ne",
                contourNe,
                x0,
                x1,
                z0,
                z1,
                capY,
                insN,
                capY - nBevel,
                isRock);
        if (contourSe > 0.01)
            this.addContourCornerFace(
                builder,
                terrain,
                plan,
                cell,
                material,
                "se",
                contourSe,
                x0,
                x1,
                z0,
                z1,
                capY,
                -insS,
                capY - sBevel,
                isRock);
        if (contourSw > 0.01)
            this.addContourCornerFace(
                builder,
                terrain,
                plan,
                cell,
                material,
                "sw",
                contourSw,
                x0,
                x1,
                z0,
                z1,
                capY,
                -insS,
                capY - sBevel,
                isRock);

        // TS `INK_LOOK.enabled` (`TERRAIN_GEOMETRY_INK as INK_LOOK`).
        if (!isBridge && TERRAIN_GEOMETRY_INK.enabled)
        {
            this.addTerrainCrestInk(
                builder,
                frame,
                terrain,
                cell,
                material,
                capColor,
                x0,
                x1,
                z0,
                z1,
                capY,
                insN,
                insS,
                contourNw,
                contourNe,
                contourSe,
                contourSw,
                isRock);
        }

        if (isRock)
        {
            // The topology outline above owns the silhouette; material-specific motifs add only interior identity.
            this.addWallCapDetail(
                builder,
                frame,
                terrain,
                cell,
                material,
                x0,
                z0,
                capY,
                insS,
                hasContour);
        }
        else if (isBridge)
        {
            this.addBridgeDeckDetail(
                builder,
                frame,
                terrain,
                plan,
                cell,
                material,
                x0,
                z0,
                capY,
                hasContour,
                bridgeFootprint,
                bridgeTravelNorthSouth);
        }
    }

    /// <summary>
    /// Lay HANDINK only on real crest topology. Same-height neighbours never enter this method's emission path,
    /// so a broad floor or wall plateau remains one unbroken painted mass instead of exposing its cell lattice.
    /// Corner cuts shorten the straight strokes and keep the existing diagonal junction line authoritative.
    /// </summary>
    internal void addTerrainCrestInk(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainCell cell,
        TerrainMaterial material,
        int capColor,
        double x0,
        double x1,
        double z0,
        double z1,
        double capY,
        double insN,
        double insS,
        double contourNw,
        double contourNe,
        double contourSe,
        double contourSw,
        bool isRock)
    {
        double northZ = z0 + insN;
        double southZ = z1 - insS;
        var segments = new[]
        {
            new TerrainCrestSegment
            {
                dir = "n",
                x0 = x0 + contourNw,
                z0 = northZ,
                x1 = x1 - contourNe,
                z1 = northZ,
                startInset = contourNw,
                endInset = contourNe,
            },
            new TerrainCrestSegment
            {
                dir = "e",
                x0 = x1,
                z0 = Math.max(northZ, z0 + contourNe),
                x1 = x1,
                z1 = Math.min(southZ, z1 - contourSe),
                startInset = Math.max(0, contourNe - insN),
                endInset = Math.max(0, contourSe - insS),
            },
            new TerrainCrestSegment
            {
                dir = "s",
                x0 = x0 + contourSw,
                z0 = southZ,
                x1 = x1 - contourSe,
                z1 = southZ,
                startInset = contourSw,
                endInset = contourSe,
            },
            new TerrainCrestSegment
            {
                dir = "w",
                x0 = x0,
                z0 = Math.max(northZ, z0 + contourNw),
                x1 = x0,
                z1 = Math.min(southZ, z1 - contourSw),
                startInset = Math.max(0, contourNw - insN),
                endInset = Math.max(0, contourSw - insS),
            },
        };
        int worldX = frame.i0 + cell.x;
        int worldY = frame.j0 + cell.y;
        // TS `INK_LOOK.contour.*` / `INK_WORLD_LINE` are import aliases of TERRAIN_GEOMETRY_INK(_WORLD_LINE).
        double baseWidth = isRock ? TERRAIN_GEOMETRY_INK.contour.wallWidth : TERRAIN_GEOMETRY_INK.contour.terraceWidth;
        double baseAlpha = isRock ? TERRAIN_GEOMETRY_INK.contour.wallAlpha : TERRAIN_GEOMETRY_INK.contour.terraceAlpha;
        int lineColor = mix(
            this.tileset?.terrain.wallLine ?? material.edgeDark,
            TERRAIN_GEOMETRY_INK_WORLD_LINE,
            isRock ? 0.3 : 0.18);
        // ── The three WASHED marks: one rule, three transmissions ────────────────────────────────────────────
        //
        // Every one of these was authored as a palette mix ("the cap, 82 % of the way to its own edgeDark") and
        // every one shipped invisible or with the WRONG SIGN — on the Hub sea the bank got *brighter* at the
        // waterline. The reason is structural, not a taste error, and it is written up once on
        // `terrainInkWashPigment`: a palette statement says nothing about a mark composited over a LIT cap.
        //
        // These pigments are now the host surface's own colour TRANSMITTED at a fixed fraction, so the light
        // cancels out of the composite and the delivered drop is a function of the two authored numbers alone.
        // Hue survives (each mark is the ground's own pigment, gathered), and no mark can lighten its host.
        int washColor = terrainInkWashPigment(
            capColor,
            TERRAIN_GEOMETRY_INK.washTransmission.edge * (isRock ? 0.86 : 1));
        // The waterline: the loudest of the three, because it is the only structural edge in the world whose two
        // sides carry no height difference at all — nothing but the drawn line separates them. Still a ~2 px
        // contour with hand-pressure wobble, never a fill and never a ring.
        int shoreLineColor = terrainInkWashPigment(
            capColor,
            TERRAIN_GEOMETRY_INK.washTransmission.shore);
        // The riser foot: pigment gathering on the LOWER ground where the wash ran down the face. A band, not a
        // line, not a shadow and not a halo — it fades to exactly zero at its outer lip.
        int footColor = terrainInkWashPigment(capColor, TERRAIN_GEOMETRY_INK.washTransmission.foot);

        for (int index = 0; index < segments.Length; index++)
        {
            TerrainCrestSegment segment = segments[index];
            TerrainEdge edge = cell.edges[segment.dir]!;
            TerrainCell? contactCell = terrainCellAt(
                terrain,
                cell.x + (segment.dir == "e" ? 1 : segment.dir == "w" ? -1 : 0),
                cell.y + (segment.dir == "s" ? 1 : segment.dir == "n" ? -1 : 0));
            bool waterContact =
                contactCell != null &&
                terrainCellCarriesWater(contactCell) &&
                contactCell.waterLevel != null &&
                (contactCell.type != TileType.Bridge || bridgeWaterIsSafelyBelowDeck(contactCell));
            // A SHORE is the one structural edge in this world with no height step: the land simply stops. Gating it
            // on a drop (or on a visible face) is what silently deleted the most important line in every water frame
            // for as long as `shoreAlpha`/`shoreWidth` have existed. Bridges keep their exclusion — a deck join is
            // carpentry, and its own fascia already draws it.
            bool shoreContact = !isRock && waterContact;
            double minimumDrop = isRock ? 0.02 : TERRAIN_GEOMETRY_INK.contour.terraceMinDrop;
            if (!shoreContact && (!edge.visibleFace || edge.drop < minimumDrop)) continue;
            if (!isRock && !shoreContact && waterContact) continue;
            double length = Math.hypot(segment.x1 - segment.x0, segment.z1 - segment.z0);
            if (length < 0.5) continue;

            bool shadowSide = segment.dir == "s" || segment.dir == "e";
            string edgeProfile = terrainEdgeProfileKindAt(worldX, worldY, index, !isRock);
            double edgeWidthScale =
                edgeProfile == "calm"
                    ? 0.88
                    : edgeProfile == "weathered"
                        ? 1
                        : edgeProfile == "notched"
                            ? 1.16
                            : 0.78;
            // A waterline reads from every side: unlike a terrace crest it has no lit/shade face to fall back on, so
            // the camera-away banks keep most of their weight instead of the terrace's 0.34.
            double roleAlpha = shoreContact
                ? TERRAIN_GEOMETRY_INK.contour.shoreAlpha * (shadowSide ? 1 : 0.9)
                : isRock
                    ? baseAlpha * (shadowSide ? 0.82 : 0.52)
                    : baseAlpha * (shadowSide ? 0.88 : 0.34);
            // STUDIO: the comic look's world ink draws the bank's contour and the water's foam line its waterline. The
            // original's hand-drawn shore line sits a hand's width INLAND on the damp bank its dissolve painted; without
            // that bank it read as a dark trench beside every shore, so it is not drawn.
            if (shoreContact) continue;
            builder.addOverlayLineFlat(
                capY + 0.075,
                segment.x0,
                segment.z0,
                segment.x1,
                segment.z1,
                terrainGeometryInkContourWidth(
                    baseWidth * edgeWidthScale,
                    cellHash((double)worldX * 17 + index * 13, (double)worldY * 19 + index * 7)),
                lineColor,
                roleAlpha);

            // A cut edge is TWO events. The crest line above draws where the paper was cut; this draws where the
            // wash ran down the riser and pooled on the ground it lands on. Only real land datums qualify: a chasm
            // lip has no floor to pool on and a waterline's pigment is the shore dissolve's job, not a foot band.
            if (
                !shoreContact &&
                edge.drop > 0.02 &&
                (edge.contactType == TileType.Floor || edge.contactType == TileType.Solid))
            {
                this.addFaceFootBand(
                    builder,
                    segment,
                    x0,
                    x1,
                    z0,
                    z1,
                    edge.toZ * ELEV + 0.06,
                    footColor,
                    TERRAIN_GEOMETRY_INK.contour.footAlpha *
                        (shadowSide ? 1 : 0.92) *
                        clamp(0.88 + edge.drop * 0.12, 0.88, 1),
                    TERRAIN_GEOMETRY_INK.contour.footWidth);
            }

            // A restrained pooled wash belongs behind the ink on the two shadow-side cuts, INCLUDING where the cap
            // is chamfered: `addWashEdge` mitres its inner boundary along the same 45-degree cross-section the
            // corner cut uses, so the pigment stays on real cap geometry and no longer stops dead at every turn —
            // which is precisely where a watercolourist's wash pools hardest.
            if (shadowSide && (shoreContact || !waterContact))
            {
                this.addWashEdge(
                    builder,
                    x0,
                    x1,
                    northZ,
                    southZ,
                    segment.dir,
                    capY + 0.05,
                    shoreContact ? shoreLineColor : washColor,
                    isRock ? 0.72 : 0.62,
                    segment.startInset,
                    segment.endInset);
            }
        }

        // No corner primitive. The bank's turns are drawn by the same marched contour as its straight runs — see
        // `terrainShoreInkPointAt`. The promontory chord and the concave pen lift that used to live here were a
        // machine bevel on a lattice, and the lattice is what actually had to go.
    }

    /// <summary>
    /// The pigment foot of a riser: one strip on the LOWER ground, full weight against the wall and fading to
    /// nothing `width` px out. Emitted from the crest loop so it inherits the same corner cuts, and pushed into
    /// the existing overlay batch — one quad per visible face, no new material and no new draw call.
    /// </summary>
    internal void addFaceFootBand(
        TileGeometryBuilder builder,
        TerrainCrestSegment segment,
        double x0,
        double x1,
        double z0,
        double z1,
        double footY,
        int hex,
        double alpha,
        double width)
    {
        if (alpha <= 0.004 || width <= 0.2) return;
        // Vertices 0/1 sit against the wall (full pigment), 2/3 out on the open ground (nothing).
        setShade4(alpha, alpha, 0, 0);
        double a = segment.dir == "n" || segment.dir == "s" ? segment.x0 : segment.z0;
        double b = segment.dir == "n" || segment.dir == "s" ? segment.x1 : segment.z1;
        if (segment.dir == "n" || segment.dir == "s")
        {
            double wallZ = segment.dir == "n" ? z0 : z1;
            double outZ = wallZ + (segment.dir == "n" ? -width : width);
            builder.addOverlayShaded(
                quad(a, footY, wallZ, b, footY, wallZ, b, footY, outZ, a, footY, outZ),
                hex,
                alpha,
                SHADE4);
        }
        else
        {
            double wallX = segment.dir == "e" ? x1 : x0;
            double outX = wallX + (segment.dir == "e" ? width : -width);
            builder.addOverlayShaded(
                quad(wallX, footY, a, wallX, footY, b, outX, footY, b, outX, footY, a),
                hex,
                alpha,
                SHADE4);
        }
    }

    internal TerrainCell? continuousWaterCellAt(
        MaterializedTerrain terrain,
        int tx,
        int ty)
    {
        TerrainCell? candidate = terrainCellAt(terrain, tx, ty);
        return candidate != null &&
            terrainCellCarriesWater(candidate) &&
            candidate.waterLevel != null &&
            (candidate.type != TileType.Bridge || bridgeWaterIsSafelyBelowDeck(candidate))
            ? candidate
            : null;
    }

    internal TerrainMaterial exposedWaterMaterialForCell(
        TerrainCell waterCell,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        TerrainMaterial? planned = plan.materials[waterCell.id];
        return this.biome?.materialDialect == "aegis-citadel"
            ? this.materialForCell(waterCell, terrain)
            : planned?.id == "water"
                ? planned!
                : STANDARD_TERRAIN_MATERIALS.water;
    }

    internal int waterSurfaceColorForCell(
        TerrainCell waterCell,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan)
    {
        TerrainMaterial material = this.exposedWaterMaterialForCell(waterCell, terrain, plan);
        return mix(material.mid ?? material.top, material.shallow ?? material.topLight, 0.15);
    }

    /// <summary>Shared absolute-corner pigment used by ordinary owners and dry-corner liquid continuations.</summary>
    internal int waterCornerColorAt(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        int cornerX,
        int cornerY,
        int fallback)
    {
        int color = fallback;
        double totalWeight = 0;
        for (int oy = -2; oy <= 1; oy++)
        {
            for (int ox = -2; ox <= 1; ox++)
            {
                TerrainCell? sample = this.continuousWaterCellAt(terrain, cornerX + ox, cornerY + oy);
                if (sample == null) continue;
                double weightX = Math.abs(ox + 0.5) < 1 ? 0.82 : 0.18;
                double weightY = Math.abs(oy + 0.5) < 1 ? 0.82 : 0.18;
                double weight = weightX * weightY;
                int sampleColor = this.waterSurfaceColorForCell(sample, terrain, plan);
                color =
                    totalWeight == 0
                        ? sampleColor
                        : mix(color, sampleColor, weight / (totalWeight + weight));
                totalWeight += weight;
            }
        }
        return color;
    }

    internal double waterCornerDepthAt(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        int cornerX,
        int cornerY,
        double fallback)
    {
        double depthSum = 0;
        double depthWeight = 0;
        for (int oy = -2; oy <= 1; oy++)
        {
            for (int ox = -2; ox <= 1; ox++)
            {
                TerrainCell? sample = this.continuousWaterCellAt(terrain, cornerX + ox, cornerY + oy);
                if (sample == null) continue;
                double weightX = Math.abs(ox + 0.5) < 1 ? 0.82 : 0.18;
                double weightY = Math.abs(oy + 0.5) < 1 ? 0.82 : 0.18;
                double weight = weightX * weightY;
                depthSum +=
                    (sample.type == TileType.Bridge
                        ? fallback
                        : (plan.water[sample.id]?.depth ?? fallback)) * weight;
                depthWeight += weight;
            }
        }
        return depthWeight > 0 ? depthSum / depthWeight : fallback;
    }

    internal double waterCornerShoreAt(
        MaterializedTerrain terrain,
        int cornerX,
        int cornerY)
    {
        int immediateCount = 0;
        for (int oy = -1; oy <= 0; oy++)
        {
            for (int ox = -1; ox <= 0; ox++)
            {
                if (this.continuousWaterCellAt(terrain, cornerX + ox, cornerY + oy) != null) immediateCount++;
            }
        }
        return clamp((double)(4 - immediateCount) / 3, 0, 1);
    }

    internal int waterFloorPigmentAt(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell floorCell,
        double gridX,
        double gridY)
    {
        TerrainMaterial floorMaterial = this.materialForCell(floorCell, terrain, planMoistureOrUndefined(plan, floorCell.id));
        int fallback = terrainMaterialSurfaceTopColor(floorMaterial, floorCell);
        double floorU = clamp(gridX - floorCell.x, 0, 1);
        double floorV = clamp(gridY - floorCell.y, 0, 1);
        int @base = bilinearColor(
            this.compatibleCapColorAt(
                terrain,
                plan,
                floorCell,
                floorCell.x,
                floorCell.y,
                false,
                fallback),
            this.compatibleCapColorAt(
                terrain,
                plan,
                floorCell,
                floorCell.x + 1,
                floorCell.y,
                false,
                fallback),
            this.compatibleCapColorAt(
                terrain,
                plan,
                floorCell,
                floorCell.x + 1,
                floorCell.y + 1,
                false,
                fallback),
            this.compatibleCapColorAt(
                terrain,
                plan,
                floorCell,
                floorCell.x,
                floorCell.y + 1,
                false,
                fallback),
            floorU,
            floorV);
        return livingGroundPigmentAt(
            this.groundDetail,
            @base,
            plan,
            floorCell,
            floorU,
            floorV,
            planMoistureAt(plan, floorCell.id),
            terrain);
    }

    /// <summary>World-stable Floor/Water dissolve sample, independent of the water cell owning the point.</summary>
    internal void waterGroundTransitionAt(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        double gridX,
        double gridY,
        double worldX,
        double worldZ,
        double waterY,
        int surfaceColor,
        double[] @out)
    {
        // PORT NOTE (allocation): the two callbacks are created once per compiler and read the terrain/plan of the
        // current call from fields (saved and restored, so a nested call cannot leak its inputs) instead of a new
        // closure + two delegates per shore sample.
        MaterializedTerrain? outerTerrain = shoreDissolveTerrain;
        TerrainRenderPlan? outerPlan = shoreDissolvePlan;
        shoreDissolveTerrain = terrain;
        shoreDissolvePlan = plan;
        terrainShoreDissolveAt(
            terrain,
            gridX,
            gridY,
            this.ts,
            worldX,
            worldZ,
            waterY,
            surfaceColor,
            shoreDissolveLiquidAt ??= (tileX, tileY) => this.continuousWaterCellAt(shoreDissolveTerrain!, tileX, tileY),
            shoreDissolveFloorPigmentAt ??= (floorCell, sampleX, sampleY) =>
                this.waterFloorPigmentAt(shoreDissolveTerrain!, shoreDissolvePlan!, floorCell, sampleX, sampleY),
            @out);
        shoreDissolveTerrain = outerTerrain;
        shoreDissolvePlan = outerPlan;
    }

    private MaterializedTerrain? shoreDissolveTerrain;
    private TerrainRenderPlan? shoreDissolvePlan;
    private Func<int, int, TerrainCell?>? shoreDissolveLiquidAt;
    private Func<TerrainCell, double, double, int>? shoreDissolveFloorPigmentAt;

    /// <summary>Close a rounded dry corner with the exact hydraulic field of its two adjacent Water owners.</summary>
    /// <param name="corner">'nw' | 'ne' | 'se' | 'sw'.</param>
    internal void addSeamlessWaterContourFill(
        TileGeometryBuilder builder,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell dryCell,
        string corner,
        double radius,
        double x0,
        double z0,
        IReadOnlyList<TerrainContourPoint> outline,
        TerrainCell ownerA,
        TerrainCell ownerB)
    {
        List<P3> points = outline.map(point => new P3 { x = point.x, y = 0, z = point.z });
        // The outline runs A -> square corner -> B -> the two interior arc samples -> A. Select the cardinal
        // Water owner nearest each sample. The hydraulic field itself is world-continuous; the owner only supplies
        // a stable material/depth fallback when the sample lies exactly on a crop boundary.
        TerrainCell[] owners = { ownerA, ownerA, ownerB, ownerB, ownerA };
        bool ownerUsesGroundLane(TerrainCell owner)
        {
            double fallbackLevel = owner.waterLevel ?? owner.surfaceZ;
            double visualCenterLevel = visualWaterLevelAt(
                terrain,
                owner.x + 0.5,
                owner.y + 0.5,
                fallbackLevel);
            int fallbackColor = this.waterSurfaceColorForCell(owner, terrain, plan);
            foreach (var (u, v) in new (int, int)[] { (0, 0), (1, 0), (1, 1), (0, 1) })
            {
                int gridX = owner.x + u;
                int gridY = owner.y + v;
                this.waterGroundTransitionAt(
                    terrain,
                    plan,
                    gridX,
                    gridY,
                    x0 + (gridX - dryCell.x) * this.ts,
                    z0 + (gridY - dryCell.y) * this.ts,
                    visualCenterLevel * ELEV,
                    fallbackColor,
                    WATER_TRANSITION_SAMPLE);
                if (WATER_TRANSITION_SAMPLE[0] > 0.0001) return true;
            }
            return false;
        }
        bool ownerAGroundLane = ownerUsesGroundLane(ownerA);
        bool ownerBGroundLane = ownerUsesGroundLane(ownerB);
        // PORT NOTE: TS stores every groundBlend in WATER_TRANSITION_FOAM4[index], growing that 4-slot JS array to
        // five; see the file header. `groundBlends` carries the identical values for the second loop.
        var groundBlends = new double[points.Count];
        for (int index = 0; index < points.Count; index++)
        {
            P3 point = points[index];
            TerrainCell owner = owners[index];
            double gridX = dryCell.x + (point.x - x0) / this.ts;
            double gridY = dryCell.y + (point.z - z0) / this.ts;
            double u = clamp(gridX - owner.x, 0, 1);
            double v = clamp(gridY - owner.y, 0, 1);
            double fallbackLevel = owner.waterLevel ?? owner.surfaceZ;
            double visualCenterLevel = visualWaterLevelAt(
                terrain,
                owner.x + 0.5,
                owner.y + 0.5,
                fallbackLevel);
            point.y = visualWaterLevelAt(terrain, gridX, gridY, visualCenterLevel) * ELEV;
            visualWaterNormalAt(
                terrain,
                gridX,
                gridY,
                visualCenterLevel,
                ELEV,
                this.ts,
                index,
                WATER_TRANSITION_NORMAL_X4,
                WATER_TRANSITION_NORMAL_Y4,
                WATER_TRANSITION_NORMAL_Z4);
            int fallbackColor = this.waterSurfaceColorForCell(owner, terrain, plan);
            int nwColor = this.waterCornerColorAt(terrain, plan, owner.x, owner.y, fallbackColor);
            int neColor = this.waterCornerColorAt(terrain, plan, owner.x + 1, owner.y, fallbackColor);
            int seColor = this.waterCornerColorAt(
                terrain,
                plan,
                owner.x + 1,
                owner.y + 1,
                fallbackColor);
            int swColor = this.waterCornerColorAt(terrain, plan, owner.x, owner.y + 1, fallbackColor);
            double fallbackDepth = plan.water[owner.id]?.depth ?? 0.4;
            double nwDepth = this.waterCornerDepthAt(terrain, plan, owner.x, owner.y, fallbackDepth);
            double neDepth = this.waterCornerDepthAt(terrain, plan, owner.x + 1, owner.y, fallbackDepth);
            double seDepth = this.waterCornerDepthAt(
                terrain,
                plan,
                owner.x + 1,
                owner.y + 1,
                fallbackDepth);
            double swDepth = this.waterCornerDepthAt(terrain, plan, owner.x, owner.y + 1, fallbackDepth);
            double nwShore = this.waterCornerShoreAt(terrain, owner.x, owner.y);
            double neShore = this.waterCornerShoreAt(terrain, owner.x + 1, owner.y);
            double seShore = this.waterCornerShoreAt(terrain, owner.x + 1, owner.y + 1);
            double swShore = this.waterCornerShoreAt(terrain, owner.x, owner.y + 1);
            int baseColor = capCornerColor(nwColor, neColor, seColor, swColor, u, v);
            this.waterGroundTransitionAt(
                terrain,
                plan,
                gridX,
                gridY,
                point.x,
                point.z,
                point.y,
                baseColor,
                WATER_TRANSITION_SAMPLE);
            double groundBlend = WATER_TRANSITION_SAMPLE[0];
            groundBlends[index] = groundBlend;
            if (index < WATER_TRANSITION_FOAM4.Length) WATER_TRANSITION_FOAM4[index] = groundBlend;
            WATER_COLOR_SCRATCH[index] = mix(baseColor, WATER_TRANSITION_SAMPLE[1], groundBlend);
            WATER_FOAM_SCRATCH[index] = capCornerShade(nwShore, neShore, seShore, swShore, u, v);
            WATER_DEPTH_SCRATCH[index] = capCornerShade(nwDepth, neDepth, seDepth, swDepth, u, v);
            WATER_REFLECTION_SCRATCH[index] = terrainWaterReflectionHintAt(
                terrain,
                plan,
                gridX,
                gridY,
                point.y / ELEV);
        }
        // This polygon owns the footprint physically cut out of the dry cap. Its Water datum may legitimately be
        // level with (or slightly above) a low bank; suppressing the owner in that case did not avoid an overlap —
        // it left the entire clipped triangle empty and exposed the purple backdrop. The diagonal face handles the
        // vertical ordering, while this liquid cap must exist for every wet corner.
        for (int index = 0; index < points.Count; index++)
        {
            bool usesGroundLane = owners[index] == ownerA ? ownerAGroundLane : ownerBGroundLane;
            WATER_FOAM_SCRATCH[index] = usesGroundLane
                ? -groundBlends[index]
                : dryCell.type == TileType.Floor
                    ? 0
                    : WATER_FOAM_SCRATCH[index];
        }
        double quantizedRadius = clamp(Math.round((radius / this.ts) * 32), 0, 15);
        double bankPayload =
            quantizedRadius *
            (corner == "nw" ? 16 : corner == "ne" ? 256 : corner == "se" ? 4_096 : 65_536);
        // This corner ring is CONCAVE. `addWater` fan-triangulates convex polygons from vertex zero; passing the
        // five points as one polygon makes the arc triangles wind opposite the square-corner triangle, so WebGL
        // culls two thirds of the fill and reveals the purple backdrop. The square corner (index 1) sees the whole
        // polygon, therefore it is the one valid fan root. Emit its three triangles explicitly and preserve every
        // appearance-bearing hydraulic channel one-for-one.
        foreach (int[] sourceIndices in SEAMLESS_WATER_FAN)
        {
            for (int target = 0; target < 3; target++)
            {
                int source = sourceIndices[target];
                P3 sourcePoint = points[source];
                P3 targetPoint = WATER_CONTOUR_TRIANGLE[target];
                targetPoint.x = sourcePoint.x;
                targetPoint.y = sourcePoint.y;
                targetPoint.z = sourcePoint.z;
                WATER_CONTOUR_COLOR3[target] = WATER_COLOR_SCRATCH[source];
                WATER_CONTOUR_FOAM3[target] = WATER_FOAM_SCRATCH[source];
                WATER_CONTOUR_DEPTH3[target] = WATER_DEPTH_SCRATCH[source];
                WATER_CONTOUR_REFLECTION3[target] = WATER_REFLECTION_SCRATCH[source];
                WATER_CONTOUR_NORMAL_X3[target] = WATER_TRANSITION_NORMAL_X4[source];
                WATER_CONTOUR_NORMAL_Y3[target] = WATER_TRANSITION_NORMAL_Y4[source];
                WATER_CONTOUR_NORMAL_Z3[target] = WATER_TRANSITION_NORMAL_Z4[source];
            }
            builder.addWater(
                WATER_CONTOUR_TRIANGLE,
                WATER_CONTOUR_COLOR3,
                WATER_CONTOUR_FOAM3,
                WATER_CONTOUR_DEPTH3,
                this.ts,
                bankPayload,
                this.ts,
                WATER_CONTOUR_REFLECTION3,
                WATER_CONTOUR_NORMAL_X3,
                WATER_CONTOUR_NORMAL_Y3,
                WATER_CONTOUR_NORMAL_Z3);
        }
    }

    /// <summary>`[[1, 2, 3], [1, 3, 4], [1, 4, 0]] as const` — never written.</summary>
    private static readonly int[][] SEAMLESS_WATER_FAN =
    {
        new[] { 1, 2, 3 },
        new[] { 1, 3, 4 },
        new[] { 1, 4, 0 },
    };

    /// <summary>Close one topology-derived corner cut with the same lit/depth geometry used by its two parent faces.</summary>
    /// <param name="corner">'nw' | 'ne' | 'se' | 'sw'.</param>
    internal void addContourCornerFace(
        TileGeometryBuilder builder,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        TerrainMaterial material,
        string corner,
        double radius,
        double x0,
        double x1,
        double z0,
        double z1,
        double capY,
        double capZOffset,
        double topY,
        bool isRock)
    {
        bool isBridge = cell.type == TileType.Bridge;
        bool bridgeHasOpenCarrier =
            isBridge && (cell.span == TileType.Water || cell.span == TileType.Chasm);
        string dirA = corner == "nw" || corner == "ne" ? "n" : "s";
        string dirB = corner == "nw" || corner == "sw" ? "w" : "e";
        TerrainEdge edgeA = cell.edges[dirA]!;
        TerrainEdge edgeB = cell.edges[dirB]!;
        if (!edgeA.visibleFace || !edgeB.visibleFace) return;
        double bottomA = this.faceBottomY(terrain, cell, dirA, edgeA);
        double bottomB = this.faceBottomY(terrain, cell, dirB, edgeB);
        if (topY <= Math.max(bottomA, bottomB) + 0.02) return;
        bool edgeAChasm = edgeA.faceSegments.some(segment => segment.role == "chasm");
        bool edgeBChasm = edgeB.faceSegments.some(segment => segment.role == "chasm");
        bool chasmFace = edgeAChasm || edgeBChasm;
        bool chasmFloorCorner = edgeAChasm && edgeBChasm;
        string chasmOwnerDirection = edgeAChasm ? dirA : dirB;
        TerrainEdge chasmOwnerEdge = edgeAChasm ? edgeA : edgeB;
        double chasmDatumY =
            (chasmOwnerEdge.faceSegments.find(segment => segment.role == "chasm")?.fromZ ?? 0) * ELEV;
        TerrainMaterial faceMaterial = chasmFace
            ? resolveChasmFaceMaterial(terrain, plan, cell, chasmOwnerDirection)
            : material;
        string faceRole = chasmFace ? chasmContinuationMaterial(cell, chasmOwnerEdge) : edgeA.material;
        int faceColor = terrainFaceBaseColor(faceMaterial, faceRole);
        double ax;
        double az;
        double bx;
        double bz;
        if (corner == "nw")
        {
            ax = x0 + radius;
            az = z0;
            bx = x0;
            bz = z0 + radius;
        }
        else if (corner == "ne")
        {
            ax = x1;
            az = z0 + radius;
            bx = x1 - radius;
            bz = z0;
        }
        else if (corner == "se")
        {
            ax = x1 - radius;
            az = z1;
            bx = x1;
            bz = z1 - radius;
        }
        else
        {
            ax = x0;
            az = z1 - radius;
            bx = x0 + radius;
            bz = z1;
        }
        // Each endpoint terminates on its own authoritative neighbour datum. Using the lower datum for both ends
        // left an open-looking side notch whenever the two adjoining terraces differed in height.
        bool aUsesEdgeA = corner == "nw" || corner == "se";
        TerrainCell? edgeNeighborA = terrainCellAt(terrain, cell.x, cell.y + (dirA == "s" ? 1 : -1));
        TerrainCell? edgeNeighborB = terrainCellAt(terrain, cell.x + (dirB == "e" ? 1 : -1), cell.y);
        bool waterCorner =
            edgeNeighborA != null &&
            edgeNeighborB != null &&
            this.continuousWaterCellAt(terrain, edgeNeighborA.x, edgeNeighborA.y) != null &&
            this.continuousWaterCellAt(terrain, edgeNeighborB.x, edgeNeighborB.y) != null;
        TerrainCell? ownerA = aUsesEdgeA ? edgeNeighborA : edgeNeighborB;
        TerrainCell? ownerB = aUsesEdgeA ? edgeNeighborB : edgeNeighborA;
        double aBottomY = aUsesEdgeA ? bottomA : bottomB;
        double bBottomY = aUsesEdgeA ? bottomB : bottomA;
        if (!isBridge && waterCorner && ownerA != null && ownerB != null)
        {
            double aGridX = cell.x + (ax - x0) / this.ts;
            double aGridY = cell.y + (az - z0) / this.ts;
            double bGridX = cell.x + (bx - x0) / this.ts;
            double bGridY = cell.y + (bz - z0) / this.ts;
            double aFallback = ownerA.waterLevel ?? ownerA.surfaceZ;
            double bFallback = ownerB.waterLevel ?? ownerB.surfaceZ;
            // The visible liquid is a spline over the authoritative water datums. Continue the diagonal dry face a
            // hair below that exact visual surface at both ends: the fixed model-datum overlap could end too high
            // whenever the spline dipped at a Floor/Wall corner, exposing a tiny background-coloured wedge.
            aBottomY = Math.min(
                aBottomY,
                visualWaterLevelAt(terrain, aGridX, aGridY, aFallback) * ELEV - 0.06);
            bBottomY = Math.min(
                bBottomY,
                visualWaterLevelAt(terrain, bGridX, bGridY, bFallback) * ELEV - 0.06);
        }
        bool seamlessDryChasmFace = chasmFace && isRock;
        int faceKind = isBridge
            ? SURF.bridge
            : seamlessDryChasmFace
                ? SURF.rockFace
                : chasmFace
                    ? SURF.chasmWall
                    : isRock
                        ? SURF.rockFace
                        : SURF.earthFace;
        double faceStrength = isBridge
            ? 0.1
            : chasmFace && !seamlessDryChasmFace
                ? chasmWallPatternStrength(faceRole)
                : 0.17;
        int capColor = terrainMaterialSurfaceTopColor(material, cell);
        int junctionFaceColor = isBridge
            ? mix(material.side, material.edgeDark, 0.26)
            : chasmFace
                ? faceColor
                : mix(faceColor, capColor, CARTOON_TERRAIN_STYLE.junctions.contourFaceTopBlend);
        double cornerX = corner == "ne" || corner == "se" ? x1 : x0;
        double cornerZ = corner == "se" || corner == "sw" ? z1 : z0;
        // The cap, fascia and the lower carrier must share one boundary primitive. A cap made from the quarter-round
        // arc over a diagonal wall/apron leaves two crescent-shaped holes; those were the purple slivers and timber
        // triangles reported at Bridge, Water and Chasm joins. Copy the module scratch immediately and traverse it
        // from the legacy A endpoint to B so all three layers use exactly the same three segments.
        TerrainContourPoint[] cornerArc = terrainContourCornerArcInto(corner, x0, x1, z0, z1, topY, radius);
        List<TerrainContourPoint> curvedTop = CORNER_ARC_ORDER.map(index => new TerrainContourPoint
        {
            x = cornerArc[index].x,
            y = topY,
            z = cornerArc[index].z,
        });
        TerrainContourPoint[] apronOutline =
        {
            new TerrainContourPoint { x = curvedTop[0].x, y = topY, z = curvedTop[0].z },
            new TerrainContourPoint { x = cornerX, y = topY, z = cornerZ },
            new TerrainContourPoint { x = curvedTop[3].x, y = topY, z = curvedTop[3].z },
            new TerrainContourPoint { x = curvedTop[2].x, y = topY, z = curvedTop[2].z },
            new TerrainContourPoint { x = curvedTop[1].x, y = topY, z = curvedTop[1].z },
        };
        (double, double) curvedNormalAt(TerrainContourPoint from, TerrainContourPoint to)
        {
            double outwardX = cornerX - (from.x + to.x) * 0.5;
            double outwardZ = cornerZ - (from.z + to.z) * 0.5;
            double length = Math.hypot(outwardX, outwardZ);
            if (!Js.Truthy(length)) length = 1; // `Math.hypot(…) || 1`
            return (outwardX / length, outwardZ / length);
        }
        // The north/south crest bevel shifts the cap's complete diagonal edge inward. Seal that exact strip down
        // to the diagonal wall; omitting it exposed the hollow mesh as a black side gap at height corners.
        if (Math.abs(capZOffset) > 0.01 || capY > topY + 0.01)
        {
            setShade4(1, 1, 0.94, 0.94);
            for (int segment = 0; segment < curvedTop.Count - 1; segment++)
            {
                TerrainContourPoint from = curvedTop[segment];
                TerrainContourPoint to = curvedTop[segment + 1];
                var (normalX, normalZ) = curvedNormalAt(from, to);
                builder.addSurface(
                    quad(
                        from.x,
                        capY,
                        from.z + capZOffset,
                        to.x,
                        capY,
                        to.z + capZOffset,
                        to.x,
                        topY,
                        to.z,
                        from.x,
                        topY,
                        from.z),
                    normalX * 0.28,
                    0.72,
                    normalZ * 0.28,
                    // Fluitown comic look: the rounded corner's bevel continues the straight bevels' cap-coloured edge;
                    // the original's lift towards the edge light drew a pale cream band around every rounded corner.
                    TerrainComicGeometry.ClosedShells
                        ? mix(terrainMaterialSurfaceTopColor(material, cell), faceColor, 0.25)
                        : mix(faceColor, material.edgeLight, 0.38),
                    faceKind,
                    faceStrength,
                    SHADE4,
                    UNIT_ZERO,
                    isRock,
                    false,
                    normalZ < 0);
            }
        }
        // Keep the diagonal closure a proper four-corner face all the way to the lower datum. The former Floor
        // special case collapsed both foot vertices into the square corner, turning every ordinary terrace turn
        // into a dark pointed card. The separate horizontal apron below now owns the clipped ground triangle.
        double footA = wallDepthShadeAt(topY, aBottomY, chasmFace, chasmDatumY);
        double footB = wallDepthShadeAt(topY, bBottomY, chasmFace, chasmDatumY);
        double stableFootA = chasmFace
            ? footA
            : Math.max(CARTOON_TERRAIN_STYLE.junctions.contourFaceFootShade, footA);
        double stableFootB = chasmFace
            ? footB
            : Math.max(CARTOON_TERRAIN_STYLE.junctions.contourFaceFootShade, footB);
        double aFootShade = aUsesEdgeA ? stableFootA : stableFootB;
        double bFootShade = aUsesEdgeA ? stableFootB : stableFootA;
        double curvedBottomAt(double progress) => aBottomY + (bBottomY - aBottomY) * progress;
        double curvedFootShadeAt(double progress) => aFootShade + (bFootShade - aFootShade) * progress;
        for (int segment = 0; segment < curvedTop.Count - 1; segment++)
        {
            TerrainContourPoint from = curvedTop[segment];
            TerrainContourPoint to = curvedTop[segment + 1];
            double fromProgress = (double)segment / (curvedTop.Count - 1);
            double toProgress = (double)(segment + 1) / (curvedTop.Count - 1);
            double fromBottomY = curvedBottomAt(fromProgress);
            double toBottomY = curvedBottomAt(toProgress);
            var (normalX, normalZ) = curvedNormalAt(from, to);
            setShade4(1, 1, curvedFootShadeAt(toProgress), curvedFootShadeAt(fromProgress));
            builder.addSurface(
                quad(
                    from.x,
                    topY,
                    from.z,
                    to.x,
                    topY,
                    to.z,
                    to.x,
                    toBottomY,
                    to.z,
                    from.x,
                    fromBottomY,
                    from.z),
                normalX,
                chasmFace ? 0.12 : CARTOON_TERRAIN_STYLE.junctions.contourFaceNormalY,
                normalZ,
                junctionFaceColor,
                faceKind,
                faceStrength,
                SHADE4,
                UNIT_ZERO,
                isRock,
                false,
                normalZ < 0,
                0,
                0,
                seamlessDryChasmFace ? chasmDatumY : (double?)null,
                seamlessDryChasmFace ? topY : (double?)null);
        }
        bool unevenDryFoot = !chasmFace && !waterCorner && Math.abs(aBottomY - bBottomY) > 0.02;
        if (unevenDryFoot)
        {
            double lowerFootY = Math.min(aBottomY, bBottomY);
            // One coherent contour may join two ordinary dry edges whose lower neighbours sit at slightly different
            // heights (most notably a low Solid terrace meeting Floor). The main diagonal correctly terminates each
            // endpoint on its own neighbour, but that leaves the sloped part of its foot above the lower walk plane.
            // Continue only that bounded remainder to the lower datum; the higher neighbour's real volume occludes
            // the overlap, while the Floor side can no longer see the clear/backdrop as a black triangular wedge.
            for (int segment = 0; segment < curvedTop.Count - 1; segment++)
            {
                TerrainContourPoint from = curvedTop[segment];
                TerrainContourPoint to = curvedTop[segment + 1];
                double fromProgress = (double)segment / (curvedTop.Count - 1);
                double toProgress = (double)(segment + 1) / (curvedTop.Count - 1);
                double fromBottomY = curvedBottomAt(fromProgress);
                double toBottomY = curvedBottomAt(toProgress);
                double fromShade = curvedFootShadeAt(fromProgress);
                double toShade = curvedFootShadeAt(toProgress);
                var (normalX, normalZ) = curvedNormalAt(from, to);
                setShade4(fromShade, toShade, toShade, fromShade);
                builder.addSurface(
                    quad(
                        from.x,
                        fromBottomY,
                        from.z,
                        to.x,
                        toBottomY,
                        to.z,
                        to.x,
                        lowerFootY,
                        to.z,
                        from.x,
                        lowerFootY,
                        from.z),
                    normalX,
                    CARTOON_TERRAIN_STYLE.junctions.contourFaceNormalY,
                    normalZ,
                    junctionFaceColor,
                    faceKind,
                    faceStrength,
                    SHADE4,
                    UNIT_ZERO,
                    isRock,
                    false,
                    normalZ < 0);
            }
        }
        // A Bridge over an OPEN carrier closes only the physical deck slab. Water and Chasm are compiled by their
        // canonical pipeline over the complete tile. Reusing the massive-land corner apron here emitted a second
        // sloped liquid/earth card from the deck outline and produced the large cyan/purple wedges below contoured
        // endpoints. A dry Floor span retains its geological support/apron contract.
        if (!bridgeHasOpenCarrier && !chasmFloorCorner)
        {
            bool floorContinuation =
                edgeNeighborA?.type == TileType.Floor &&
                edgeNeighborB?.type == TileType.Floor &&
                Math.abs(edgeNeighborA.surfaceZ - edgeNeighborB.surfaceZ) < 0.01;
            TerrainCell? mixedFloorOwner =
                unevenDryFoot && edgeA.toZ < edgeB.toZ - 0.01 && edgeNeighborA?.type == TileType.Floor
                    ? edgeNeighborA
                    : unevenDryFoot && edgeB.toZ < edgeA.toZ - 0.01 && edgeNeighborB?.type == TileType.Floor
                        ? edgeNeighborB
                        : null;
            TerrainCell? floorApronOwner = floorContinuation ? edgeNeighborA : mixedFloorOwner;
            bool floorAlignedApron = floorApronOwner != null;
            // When two liquid edges meet below one clipped wall corner, the missing footprint belongs to the same
            // animated water body. The former raised dry triangle was the conspicuous left/right-pointing spur at
            // wall feet. Fill the bounded corner with the normal water batch so pigment, wave phase and lighting all
            // continue in world space; navigation and the authoritative wall footprint remain unchanged.
            if (waterCorner && edgeNeighborA != null && edgeNeighborB != null && ownerA != null && ownerB != null)
            {
                this.addSeamlessWaterContourFill(
                    builder,
                    terrain,
                    plan,
                    cell,
                    corner,
                    radius,
                    x0,
                    z0,
                    apronOutline,
                    ownerA,
                    ownerB);
            }
            // A dry convex wall corner is visually part of the surrounding walk plane. Put its missing triangle on
            // that exact datum and feed it through the same floor colour/pattern path; the shader's world-space field
            // then continues across the boundary without a UV seam or the former pale rock-face patch. A mixed-height
            // corner follows its lower Floor neighbour; all-rock mixtures keep the lifted geological apron. The
            // dedicated water branch above owns wet corners.
            double apronDatum = floorAlignedApron
                ? floorApronOwner!.surfaceZ
                : unevenDryFoot
                    ? Math.min(edgeA.toZ, edgeB.toZ)
                    : Math.max(edgeA.toZ, edgeB.toZ);
            double apronY =
                apronDatum * ELEV +
                (floorAlignedApron ? 0 : CARTOON_TERRAIN_STYLE.junctions.dryCornerApronLiftPx);
            if (!waterCorner && apronY < topY - 0.02)
            {
                List<P3> apronPoints = apronOutline.map(point => new P3
                {
                    x = point.x,
                    y = apronY,
                    z = point.z,
                });
                if (floorAlignedApron)
                {
                    TerrainCell? diagonalNeighbor = terrainCellAt(
                        terrain,
                        cell.x + (corner == "ne" || corner == "se" ? 1 : -1),
                        cell.y + (corner == "se" || corner == "sw" ? 1 : -1));
                    TerrainCell cornerFloor =
                        diagonalNeighbor?.type == TileType.Floor &&
                        Math.abs(diagonalNeighbor.surfaceZ - apronDatum) < 0.01
                            ? diagonalNeighbor
                            : floorApronOwner!;
                    for (int index = 0; index < apronPoints.Count; index++)
                    {
                        P3 point = apronPoints[index];
                        TerrainCell floorCell = (
                            floorContinuation
                                ? index == 0 || index == 4
                                    ? ownerA
                                    : index == 1
                                        ? cornerFloor
                                        : ownerB
                                : floorApronOwner
                        )!;
                        TerrainMaterial floorMaterial =
                            this.biome?.materialDialect == "aegis-citadel"
                                ? this.materialForCell(floorCell, terrain)
                                : (plan.materials[floorCell.id] ?? this.materialForCell(floorCell, terrain));
                        double floorWorldX = x0 / this.ts + floorCell.x - cell.x;
                        double floorWorldY = z0 / this.ts + floorCell.y - cell.y;
                        double u = point.x / this.ts - floorWorldX;
                        double v = point.z / this.ts - floorWorldY;
                        double floorU = clamp(u, 0, 1);
                        double floorV = clamp(v, 0, 1);
                        int floorBase = bilinearColor(
                            this.compatibleCapColorAt(
                                terrain,
                                plan,
                                floorCell,
                                floorCell.x,
                                floorCell.y,
                                false,
                                terrainMaterialSurfaceTopColor(floorMaterial, floorCell)),
                            this.compatibleCapColorAt(
                                terrain,
                                plan,
                                floorCell,
                                floorCell.x + 1,
                                floorCell.y,
                                false,
                                terrainMaterialSurfaceTopColor(floorMaterial, floorCell)),
                            this.compatibleCapColorAt(
                                terrain,
                                plan,
                                floorCell,
                                floorCell.x + 1,
                                floorCell.y + 1,
                                false,
                                terrainMaterialSurfaceTopColor(floorMaterial, floorCell)),
                            this.compatibleCapColorAt(
                                terrain,
                                plan,
                                floorCell,
                                floorCell.x,
                                floorCell.y + 1,
                                false,
                                terrainMaterialSurfaceTopColor(floorMaterial, floorCell)),
                            floorU,
                            floorV);
                        // This helper polygon must be indistinguishable from the canonical floor cap. Reusing only the
                        // legacy path-wear tint here left its trail/dirt/meadow values out of sync and exposed the fill as
                        // a differently coloured triangle at every convex wall corner.
                        CAP_COLOR_SCRATCH[index] = livingGroundPigmentAt(
                            this.groundDetail,
                            floorBase,
                            plan,
                            floorCell,
                            floorU,
                            floorV,
                            planMoistureAt(plan, floorCell.id),
                            terrain);
                        CAP_SHADE_SCRATCH[index] = terrainCapBakeShade(
                            terrain,
                            plan,
                            floorCell,
                            floorWorldX,
                            floorWorldY,
                            u,
                            v,
                            bilinearShade(
                                terrainCapShade(floorWorldX, floorWorldY, false, false),
                                terrainCapShade(floorWorldX + 1, floorWorldY, false, false),
                                terrainCapShade(floorWorldX + 1, floorWorldY + 1, false, false),
                                terrainCapShade(floorWorldX, floorWorldY + 1, false, false),
                                u,
                                v));
                        // A closure triangle is still the canonical lower Floor. Carry its complete material vector, not
                        // just the base pigment: omitting turf/wear/tangent made the shader treat these three vertices as
                        // bare neutral ground, which is the grey triangular card visible in otherwise continuous grass.
                        int groundSlot = index * TERRAIN_GROUND_CHANNEL_STRIDE;
                        var groundSurface = groundSurfaceVectorAt(
                            this.groundDetail,
                            plan,
                            floorCell,
                            floorU,
                            floorV);
                        CAP_GROUND_SCRATCH[groundSlot] = turfCoverAt(
                            this.groundDetail,
                            plan,
                            floorCell,
                            point.x,
                            point.z,
                            floorU,
                            floorV);
                        CAP_GROUND_SCRATCH[groundSlot + 1] = groundSurface.wear;
                        CAP_GROUND_SCRATCH[groundSlot + 2] = groundSurface.tangentX;
                        CAP_GROUND_SCRATCH[groundSlot + 3] = groundSurface.tangentZ;
                    }
                }
                builder.addSurface(
                    apronPoints,
                    0,
                    1,
                    0,
                    floorAlignedApron
                        ? (NumberOrArray)CAP_COLOR_SCRATCH
                        : mix(
                            junctionFaceColor,
                            capColor,
                            CARTOON_TERRAIN_STYLE.junctions.mixedCornerApronTopBlend),
                    floorAlignedApron ? SURF.floor : isRock ? SURF.rockFace : SURF.earthFace,
                    floorAlignedApron ? 0.12 : 0.13,
                    floorAlignedApron ? CAP_SHADE_SCRATCH : new double[] { 0.96, 0.92, 0.96, 0.96, 0.96 },
                    UNIT_ZERO,
                    false,
                    false,
                    false,
                    0,
                    floorAlignedApron ? (NumberOrArray)CAP_GROUND_SCRATCH : 0,
                    // The concave apron is visible in full from its square corner only. A fan rooted at the arc
                    // endpoint overlaps the cliff footprint and winds the two arc triangles backwards.
                    fanRoot: 1);
                if (TerrainComicGeometry.ClosedShells && unevenDryFoot)
                    this.addCornerApronFlanks(
                        builder,
                        terrain,
                        plan,
                        corner,
                        apronOutline,
                        apronY,
                        aUsesEdgeA ? dirA : dirB,
                        aBottomY,
                        aUsesEdgeA ? edgeNeighborA : edgeNeighborB,
                        aUsesEdgeA ? dirB : dirA,
                        bBottomY,
                        aUsesEdgeA ? edgeNeighborB : edgeNeighborA);
            }
        }
        else if (!bridgeHasOpenCarrier)
        {
            // This is the deep analogue of the ordinary Floor apron above. The complete Chasm tiles stop on their
            // cell grid; the high wall owns the complete footprint removed by its rounded corner. Emitting the same
            // arc-bounded polygon at the common deep datum keeps wall foot and abyss floor watertight.
            double deepFloorY = Math.min(aBottomY, bBottomY);
            builder.addSurface(
                apronOutline.map(point => new P3 { x = point.x, y = deepFloorY, z = point.z }),
                0,
                1,
                0,
                mix(terrainMaterialSurfaceTopColor(faceMaterial, cell), faceMaterial.edgeDark, 0.68),
                SURF.chasmFloor,
                0.16,
                new double[] { 1, 1, 1, 1, 1 },
                fanRoot: 1);
        }
        for (int segment = 0; segment < curvedTop.Count - 1; segment++)
        {
            TerrainContourPoint from = curvedTop[segment];
            TerrainContourPoint to = curvedTop[segment + 1];
            builder.addOverlayLineFlat(
                topY + 0.07,
                from.x,
                from.z,
                to.x,
                to.z,
                chasmFace ? 1.6 : isBridge ? 0.72 : 1.0,
                isBridge
                    ? material.edgeDark
                    : chasmFace
                        ? faceMaterial.edgeDark
                        : mix(
                            faceMaterial.edgeDark,
                            capColor,
                            CARTOON_TERRAIN_STYLE.junctions.contourLineTopBlend),
                chasmFace ? 0.42 : isBridge ? 0.46 : CARTOON_TERRAIN_STYLE.junctions.contourLineAlpha);
        }
    }

    /// <summary>`[3, 2, 1, 0]` — the corner arc traversed from the legacy A endpoint to B. Never written.</summary>
    private static readonly int[] CORNER_ARC_ORDER = { 3, 2, 1, 0 };

    // ── Port adapters (C3) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// `plan.moisture[id] ?? 0`. The plan fills one entry per terrain cell and every caller indexes it with an id of
    /// that terrain, so the TS `?? 0` (a hole / out-of-range read) never fires; the guard keeps the JS outcome anyway.
    /// </summary>
    private static double planMoistureAt(TerrainRenderPlan plan, int id)
    {
        IReadOnlyList<double> moisture = plan.moisture;
        return (uint)id < (uint)moisture.Count ? moisture[id] : 0;
    }

    /// <summary>`plan.moisture[id]` handed to `materialForCell(cell, terrain, cachedMoisture?)`: undefined (null)
    /// outside the array, so the callee falls back to its own moisture hint exactly like the TS.</summary>
    private static double? planMoistureOrUndefined(TerrainRenderPlan plan, int id)
    {
        IReadOnlyList<double> moisture = plan.moisture;
        return (uint)id < (uint)moisture.Count ? moisture[id] : null;
    }

    [ThreadStatic] private static List<P3>? _CONTOUR_P3_STORAGE;
    [ThreadStatic] private static List<P3>? _CONTOUR_P3_SCRATCH;

    /// <summary>
    /// A contour polygon (`TerrainContourPoint[]`) as the builder's `P3[]`: TS passes the very same objects (the two
    /// interfaces are structurally identical); C# copies the coordinates into per-thread P3 scratch. The result is
    /// valid until the next call on this thread and is always consumed synchronously.
    /// </summary>
    private static List<P3> contourPointsAsP3(IReadOnlyList<TerrainContourPoint> points)
    {
        List<P3> storage = _CONTOUR_P3_STORAGE ??= new List<P3>(20);
        List<P3> scratch = _CONTOUR_P3_SCRATCH ??= new List<P3>(20);
        while (storage.Count < points.Count) storage.Add(new P3(0, 0, 0));
        scratch.Clear();
        for (int index = 0; index < points.Count; index++)
        {
            P3 target = storage[index];
            TerrainContourPoint source = points[index];
            target.x = source.x;
            target.y = source.y;
            target.z = source.z;
            scratch.Add(target);
        }
        return scratch;
    }
}
