// Port of packages/client/src/render/environment/terrainGeometryCompiler.ts — keep in lockstep with the original.
//
// PART C4 (TS lines 9016–11977): `addCliffStrata` … `buildWaterfall` of `TerrainGeometryCompiler`. Instance fields,
// the builder and the module-level helpers live in part C1 (TerrainGeometryCompiler.cs).
//
// PORT NOTES
// * Helper modules whose names could collide under `using static` (TerrainRenderPlanModule, TerrainChasmGeometry,
//   TerrainChasmFloorGeometry, TerrainGroundingGeometry, TerrainWallGrowth, TerrainContourGeometry,
//   TerrainBakePigment, TerrainVisualGround, TerrainVariationStyle, TerrainLighting, WorldPropPrimitives,
//   WaterfallVisual) are called qualified by their module class. `WATER_BASIN_DEPTH` exists in both TerrainModel
//   and the compiler module; the compiler's own constant is named explicitly.
// * World cell coordinates (`wx`, `wy`, `cellX`, `cellY`) are `int`. Products such as `wx * 911` only ever feed
//   `cellHash`/`propHash`, which apply ToInt32 first; unchecked int wrap-around is congruent modulo 2^32 with the
//   exact JS double product, so the hash inputs are bit-identical. Every `/` between ints is cast to double.
// * `terrain.cells[id]`, `plan.materials[id]`, `plan.water[id]` and `plan.moisture[id]` reads that the TS guards
//   with `?.`/`??`/`!x` go through `partC4ItemAt` (JS `undefined` past the end → null).
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TerrainModel;
using static Fluitown.Render.Palette;
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
    // `for (const direction of ['n', 'e', 's', 'w'] as const)` and friends: never-written literal tables.
    private static readonly string[] PART_C4_NESW = { "n", "e", "s", "w" };
    private static readonly string[] PART_C4_NSEW = { "n", "s", "e", "w" };
    private static readonly double[] PART_C4_PEG_ALONG = { 0.13, 0.87 };
    private static readonly double[] PART_C4_EDGE_SAMPLES = { 0, 0.5, 1 };

    /// <summary>`list[index]` with the JS outcome past the end (`undefined` → null).</summary>
    private static T? partC4ItemAt<T>(IReadOnlyList<T> list, int index) where T : class =>
        (uint)index < (uint)list.Count ? list[index] : null;

    /// <summary>`plan.moisture[index]` (a JS `number | undefined` read).</summary>
    private static double? partC4MoistureAt(IReadOnlyList<double> moisture, int index) =>
        (uint)index < (uint)moisture.Count ? moisture[index] : null;

    /// <summary>
    /// NATURAL tileset cliff detail: bakes sparse, flush SEDIMENTARY fragments into a tall rock face. Physical
    /// shelves used to project as stacked horizontal bars and exposed black end caps beside Water, so depth now
    /// comes from the rolled crest, the lit wall relief and irregular pigment held on the parent plane.
    /// </summary>
    internal void addCliffStrata(
        TileGeometryBuilder builder,
        int wx,
        int wy,
        double x0,
        double x1,
        double z,
        double topY,
        double bottomY,
        int faceColor,
        double footShade)
    {
        double height = topY - bottomY;
        if (height < ELEV * 2.2) return; // a short step, not a cliff — leave the face clean
        double zf = z + 0.22; // strata sit just proud of the rock face
        // Strata are pigment IN the rock, so they must render in the LIT surface batch with the wall's own
        // crest→foot gradient. As unlit overlays their albedo ignored the key-dominant rig entirely: on a
        // back-lit south face the pale mineral seams stayed at full brightness over a near-black wall and read
        // as flickering white strokes along every tall chasm-side cliff.
        double shadeAt(double y) => 1 + (footShade - 1) * clamp((topY - y) / height, 0, 1);
        // The theme's strata gain gives every run its own geology from pure data: a strongly-banded ashen scarp,
        // a sparsely-veined ice rift, the neutral alpine wall — band count AND presence scale with the dial.
        double strataGain = Math.max(0.35, Math.min(1.8, this.styleStrata));
        TerrainWallSegmentProfile segmentProfile = TerrainVariationStyle.terrainWallSegmentVariantAt(wx, wy);
        double bands = Math.min(
            4,
            Math.max(
                2,
                Math.round(
                    (height / (ELEV * 1.2)) * (0.38 + 0.38 * strataGain) * segmentProfile.bandDensity)));
        int shadowInk = mix(faceColor, this.tileset?.terrain.wallDeep ?? 0x101010, 0.46);
        int mineral = mix(faceColor, this.tileset?.terrain.wallLit ?? 0xffffff, 0.38);
        double presence = Math.min(1, 0.55 + 0.45 * strataGain) * segmentProfile.inkPresence;
        for (int b = 1; b < bands; b++)
        {
            // Bands drift a touch off the even grid (chunk-stable) so the strata read laid down by water, not ruled.
            double frac =
                b / bands + (cellHash(wx * 7 + b, wy * 11 + b) - 0.5) * segmentProfile.verticalDrift;
            double y = bottomY + height * Math.min(0.97, Math.max(0.03, frac));
            double thick = (0.55 + cellHash(wx * 5 + b, wy * 13) * 0.9) * segmentProfile.thickness;
            bool seam = cellHash(wx * 3 + b, wy * 17 + b) > 1 - segmentProfile.mineralChance;
            double yHi = y + thick * 0.5;
            double yLo = y - thick * 0.5;
            // Bedding is a broken deposit inside the wall, not a ruler line painted across every gameplay cell. Wide,
            // offset fragments retain the geological read while their irregular terminals stop revealing the tile grid.
            double width = x1 - x0;
            double spanSeed = cellHash(wx * 79 + b * 19, wy * 83 - b * 23);
            double centreSeed = cellHash(wx * 89 - b * 29, wy * 97 + b * 31);
            double bandWidth = width * (segmentProfile.spanMin + spanSeed * segmentProfile.spanRange);
            double bandCentre =
                x0 +
                width *
                    ((1 - segmentProfile.centreSpread) * 0.5 + centreSeed * segmentProfile.centreSpread);
            double bandX0 = Math.max(x0 + 1.2, bandCentre - bandWidth * 0.5);
            double bandX1 = Math.min(x1 - 1.2, bandCentre + bandWidth * 0.5);
            if (bandX1 <= bandX0 + 1.4) continue;
            double tilt = (centreSeed - 0.5) * height * segmentProfile.tilt;
            setShade4(shadeAt(yHi + tilt), shadeAt(yHi - tilt), shadeAt(yLo - tilt), shadeAt(yLo + tilt));
            builder.addSurface(
                quad(
                    bandX0,
                    yHi + tilt,
                    zf,
                    bandX1,
                    yHi - tilt,
                    zf,
                    bandX1,
                    yLo - tilt,
                    zf,
                    bandX0,
                    yLo + tilt,
                    zf),
                0,
                0,
                1,
                mix(faceColor, seam ? mineral : shadowInk, (seam ? 0.24 : 0.28) * presence),
                SURF.rockFace,
                0.16,
                SHADE4);
        }

        // One broad shelf replaces several painted bands with an actual geological event. It stays comfortably
        // inside the cell, so adjacent bake owners never need to coordinate geometry and no implementation grid is
        // revealed. The open underside is intentional: the key light can shade it naturally against the parent
        // wall, and the front/side closures preserve a solid silhouette at close range.
        double shelfSeed = cellHash(wx * 43 + 17, wy * 47 + 23);
        if (CARTOON_TERRAIN_STYLE.wallDepth.enabled && height >= ELEV * 2.7 && shelfSeed > 0.26)
        {
            double width = x1 - x0;
            double shelfWidth = width * (0.46 + shelfSeed * 0.28);
            double centre = x0 + width * (0.28 + cellHash(wx * 53 + 29, wy * 59 + 31) * 0.44);
            double shelfX0 = Math.max(x0 + 2.2, centre - shelfWidth * 0.5);
            double shelfX1 = Math.min(x1 - 2.2, centre + shelfWidth * 0.5);
            double shelfY = bottomY + height * (0.28 + cellHash(wx * 61 + 37, wy * 67 + 41) * 0.44);
            double shelfDepth = 0.8 + shelfSeed * 1.25;
            double shelfThickness = 0.58 + cellHash(wx * 71 + 43, wy * 73 + 47) * 0.72;
            double shelfFront = z + shelfDepth;
            double shelfBack = z + 0.12;
            double shelfTop = shelfY + shelfThickness * 0.36;
            double shelfBottom = shelfY - shelfThickness * 0.64;
            int shelfTopColor = mix(faceColor, this.tileset?.terrain.wallLit ?? mineral, 0.3);
            int shelfFaceColor = mix(faceColor, this.tileset?.terrain.wallDeep ?? shadowInk, 0.12);
            builder.addSurface(
                quad(
                    shelfX0,
                    shelfTop,
                    shelfBack,
                    shelfX1,
                    shelfTop,
                    shelfBack,
                    shelfX1 - 0.35,
                    shelfTop,
                    shelfFront,
                    shelfX0 + 0.45,
                    shelfTop,
                    shelfFront),
                0,
                1,
                0.05,
                shelfTopColor,
                SURF.rockCap,
                0.14);
            builder.addSurface(
                quad(
                    shelfX0 + 0.45,
                    shelfTop,
                    shelfFront,
                    shelfX1 - 0.35,
                    shelfTop,
                    shelfFront,
                    shelfX1 - 0.65,
                    shelfBottom,
                    shelfFront - 0.18,
                    shelfX0 + 0.7,
                    shelfBottom,
                    shelfFront - 0.12),
                0,
                0.08,
                1,
                shelfFaceColor,
                SURF.rockFace,
                0.16);
            builder.addSurface(
                quad(
                    shelfX0,
                    shelfTop,
                    shelfBack,
                    shelfX0 + 0.45,
                    shelfTop,
                    shelfFront,
                    shelfX0 + 0.7,
                    shelfBottom,
                    shelfFront - 0.12,
                    shelfX0 + 0.18,
                    shelfBottom,
                    shelfBack),
                -1,
                0.06,
                0.12,
                mix(shelfFaceColor, shadowInk, 0.18),
                SURF.rockFace,
                0.14,
                UNIT_SHADE,
                UNIT_ZERO,
                false,
                true);
            builder.addSurface(
                quad(
                    shelfX1 - 0.35,
                    shelfTop,
                    shelfFront,
                    shelfX1,
                    shelfTop,
                    shelfBack,
                    shelfX1 - 0.18,
                    shelfBottom,
                    shelfBack,
                    shelfX1 - 0.65,
                    shelfBottom,
                    shelfFront - 0.18),
                1,
                0.06,
                0.12,
                shelfFaceColor,
                SURF.rockFace,
                0.14,
                UNIT_SHADE,
                UNIT_ZERO,
                false,
                true);
        }
        // A rare DIAGONAL mineral vein cutting the bedding — the one-off geological event that makes a particular
        // cliff a landmark. Strictly hash-gated so it stays a discovery, never a pattern. Like the bedding it is
        // rock pigment, so it lives in the lit batch at the wall's value — never a glowing unlit slash.
        if (cellHash(wx * 19 + 5, wy * 23 + 11) > 0.9)
        {
            double v = cellHash(wx * 29, wy * 31);
            double yA = bottomY + height * (0.15 + v * 0.3);
            double yB = bottomY + height * (0.6 + v * 0.32);
            double w = 1.3 + v * 1.2;
            setShade4(shadeAt(yA + w), shadeAt(yB + w), shadeAt(yB - w), shadeAt(yA - w));
            builder.addSurface(
                quad(
                    x0 + 2,
                    yA + w,
                    zf + 0.03,
                    x1 - 2,
                    yB + w,
                    zf + 0.03,
                    x1 - 2,
                    yB - w,
                    zf + 0.03,
                    x0 + 2,
                    yA - w,
                    zf + 0.03),
                0,
                0,
                1,
                mix(faceColor, mineral, 0.4),
                SURF.rockFace,
                0.16,
                SHADE4);
        }
        if (this.tileset?.decal.kind == "rainbow")
        {
            double panes = Math.min(5, Math.max(2, Math.floor(height / (ELEV * 1.6))));
            for (int i = 0; i < panes; i++)
            {
                double h = cellHash(wx * 37 + i * 5, wy * 41 + i * 7);
                double t = (i + 0.5) / panes;
                double y = bottomY + height * Math.min(0.9, Math.max(0.12, t + (h - 0.5) * 0.08));
                double w = 1.1 + h * 1.6;
                double xA = x0 + 2 + (x1 - x0 - 4) * Math.min(0.78, Math.max(0.12, h));
                double xB = xA + (h - 0.5) * 12 + 6;
                builder.addOverlay(
                    quad(
                        xA - w,
                        y + w * 1.6,
                        zf + 0.06,
                        xB + w,
                        y + w * 0.8,
                        zf + 0.06,
                        xB,
                        y - w * 1.7,
                        zf + 0.06,
                        xA - w * 1.5,
                        y - w * 0.8,
                        zf + 0.06),
                    TERRAIN_GEOMETRY_RAINBOW_COLORS[
                        (int)((i + Math.floor(h * TERRAIN_GEOMETRY_RAINBOW_COLORS.Count)) %
                            TERRAIN_GEOMETRY_RAINBOW_COLORS.Count)],
                    0.2);
            }
        }
        if (this.tileset?.decal.kind == "glassShard")
        {
            double panes = Math.min(6, Math.max(3, Math.floor(height / (ELEV * 1.35))));
            int edge = this.tileset.flood.foam;
            int violet = this.tileset.flood.glint;
            for (int i = 0; i < panes; i++)
            {
                double h = cellHash(wx * 3313 + i * 41, wy * 3329 + i * 47);
                double t = (i + 0.5) / panes;
                double cy = bottomY + height * Math.min(0.92, Math.max(0.1, t + (h - 0.5) * 0.08));
                double xA = x0 + 2 + (x1 - x0 - 4) * (0.12 + h * 0.72);
                double xB = xA + (h - 0.5) * 9 + 4;
                double w = 0.9 + h * 1.3;
                builder.addOverlay(
                    quad(
                        xA - w,
                        cy + w * 1.4,
                        zf + 0.08,
                        xB + w,
                        cy + w * 0.7,
                        zf + 0.08,
                        xB,
                        cy - w * 1.5,
                        zf + 0.08,
                        xA - w * 1.4,
                        cy - w * 0.7,
                        zf + 0.08),
                    i % 2 != 0 ? violet : mix(faceColor, this.tileset.decal.mid, 0.36),
                    0.2);
                builder.addOverlayLineFlat(cy + 0.08, x0 + 2, zf + 0.1, x1 - 2, zf + 0.1, 0.52, edge, 0.22);
            }
        }
        if (this.tileset?.decal.kind == "reef")
        {
            double points = Math.min(5, Math.max(2, Math.floor(height / (ELEV * 1.4))));
            for (int i = 0; i < points; i++)
            {
                double h = cellHash(wx * 3203 + i * 41, wy * 3217 + i * 47);
                if (h < 0.24) continue;
                double cy = bottomY + height * (0.16 + ((i + h * 0.7) / points) * 0.72);
                double cx =
                    x0 + 2 + (x1 - x0 - 4) * (0.18 + cellHash(wx * 3221 + i * 53, wy * 3229 + i * 59) * 0.64);
                double r = 1.1 + h * 1.3;
                builder.addOverlay(
                    quad(
                        cx - r,
                        cy + r * 1.1,
                        zf + 0.08,
                        cx + r,
                        cy + r * 0.74,
                        zf + 0.08,
                        cx + r * 0.8,
                        cy - r * 0.85,
                        zf + 0.08,
                        cx - r * 0.9,
                        cy - r * 0.66,
                        zf + 0.08),
                    h > 0.62 ? this.tileset.flood.foam : this.tileset.decal.accent,
                    0.22 + h * 0.14);
            }
        }
    }

    /// <summary>
    /// Fail-closed support for a deck classified over ordinary ground.
    ///
    /// Water and Chasm are complete carrier topologies rendered independently below the timber and must never
    /// enter this path: a wall following a deck perimeter plugs the open water/shaft the Bridge exists to cross.
    /// A Floor span has no carrier mesh, so only that total-classification fallback closes down to ground.
    /// </summary>
    internal void buildBridgeGroundSupport(
        TileGeometryBuilder builder,
        MaterializedTerrain terrain,
        TerrainCell cell,
        TerrainMaterial bridgeMaterial,
        double x0,
        double x1,
        double z0,
        double z1,
        IReadOnlyList<TerrainContourPoint>? footprint = null)
    {
        if (cell.span != TileType.Floor) return;
        double topY = cell.baseZ * ELEV;
        double supportLevelFor(TerrainCell candidate)
        {
            double support = candidate.baseZ;
            foreach (string direction in PART_C4_NESW)
            {
                TerrainCell? neighbor = terrainCellAt(
                    terrain,
                    candidate.x + (direction == "e" ? 1 : direction == "w" ? -1 : 0),
                    candidate.y + (direction == "s" ? 1 : direction == "n" ? -1 : 0));
                if (neighbor == null || neighbor.type == TileType.Bridge) continue;
                support = Math.min(support, neighbor.waterLevel ?? neighbor.surfaceZ);
            }
            return support;
        }
        double supportLevel = supportLevelFor(cell);
        double bottomY = supportLevel * ELEV - 0.035;
        if (topY <= bottomY + 0.02) return;

        bool continues(string direction)
        {
            TerrainCell? neighbor = terrainCellAt(
                terrain,
                cell.x + (direction == "e" ? 1 : direction == "w" ? -1 : 0),
                cell.y + (direction == "s" ? 1 : direction == "n" ? -1 : 0));
            return neighbor?.type == TileType.Bridge && neighbor.span == cell.span;
        }
        void addClosure(IReadOnlyList<P3> points, double nx, double nz)
        {
            int color = mix(bridgeMaterial.side, bridgeMaterial.edgeDark, 0.4);
            double footShade = wallDepthShadeAt(topY, bottomY, false);
            setShade4(1, 1, footShade, footShade);
            builder.addSurface(
                points,
                nx,
                0.04,
                nz,
                color,
                SURF.bridge,
                0.1,
                SHADE4,
                UNIT_ZERO,
                false,
                true);
        }
        if (footprint != null && footprint.Count >= 3)
        {
            const double epsilon = 0.02;
            string directionOf(TerrainContourPoint a, TerrainContourPoint b)
            {
                if (Math.abs(a.z - z0) < epsilon && Math.abs(b.z - z0) < epsilon) return "n";
                if (Math.abs(a.x - x1) < epsilon && Math.abs(b.x - x1) < epsilon) return "e";
                if (Math.abs(a.z - z1) < epsilon && Math.abs(b.z - z1) < epsilon) return "s";
                if (Math.abs(a.x - x0) < epsilon && Math.abs(b.x - x0) < epsilon) return "w";
                // A cut segment belongs to the nearest north/south corner. Both adjoining boundaries are compatible
                // by definition (otherwise the shared resolver emits no corner), so either one resolves the same span
                // family while this stable choice keeps material sampling deterministic.
                return (a.z + b.z) * 0.5 < (z0 + z1) * 0.5 ? "n" : "s";
            }
            for (int index = 0; index < footprint.Count; index++)
            {
                TerrainContourPoint a = footprint[index];
                TerrainContourPoint b = footprint[(index + 1) % footprint.Count];
                double dx = b.x - a.x;
                double dz = b.z - a.z;
                double length = Math.hypot(dx, dz);
                if (length <= epsilon) continue;
                string direction = directionOf(a, b);
                bool cardinal =
                    (direction == "n" && Math.abs(a.z - z0) < epsilon && Math.abs(b.z - z0) < epsilon) ||
                    (direction == "e" && Math.abs(a.x - x1) < epsilon && Math.abs(b.x - x1) < epsilon) ||
                    (direction == "s" && Math.abs(a.z - z1) < epsilon && Math.abs(b.z - z1) < epsilon) ||
                    (direction == "w" && Math.abs(a.x - x0) < epsilon && Math.abs(b.x - x0) < epsilon);
                if (cardinal && continues(direction)) continue;
                addClosure(
                    quad(a.x, topY, a.z, b.x, topY, b.z, b.x, bottomY, b.z, a.x, bottomY, a.z),
                    dz / length,
                    -dx / length);
            }
            return;
        }
        if (!continues("n"))
            addClosure(quad(x1, topY, z0, x0, topY, z0, x0, bottomY, z0, x1, bottomY, z0), 0, -1);
        if (!continues("s"))
            addClosure(quad(x0, topY, z1, x1, topY, z1, x1, bottomY, z1, x0, bottomY, z1), 0, 1);
        if (!continues("e"))
            addClosure(quad(x1, topY, z0, x1, topY, z1, x1, bottomY, z1, x1, bottomY, z0), 1, 0);
        if (!continues("w"))
            addClosure(quad(x0, topY, z1, x0, topY, z0, x0, bottomY, z0, x0, bottomY, z1), -1, 0);
    }

    /// <summary>
    /// Bridge deck as real timber on one absolute-world course lattice. The visible boards never rotate with a
    /// clipped bake-local component, so a broad deck reads as one assembly across cell, chunk and bake seams.
    /// </summary>
    internal void buildBridgePlanks(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        TerrainMaterial material,
        double x0,
        double z0,
        double capY,
        double insS,
        double capShade,
        IReadOnlyList<TerrainContourPoint>? footprint = null,
        bool travelNorthSouth = true)
    {
        double ts = frame.tileSize;
        int capColor = TerrainRenderPlanModule.terrainMaterialSurfaceTopColor(material, cell);
        // One world period for the whole deck. A random four/five-plank choice per cell restarted the joint
        // lattice at every ownership boundary and made a broad boardwalk read as stitched rectangles.
        double planks = CARTOON_TERRAIN_STYLE.bridgeStructure.deckPlanksPerCell;
        double gap = CARTOON_TERRAIN_STYLE.bridgeStructure.deckPlankGapCells * ts * 0.5;
        if (this.prismglassTileset || this.tileset?.decal.kind == "glassShard")
        {
            int glass = mix(capColor, this.tileset?.flood.surface ?? capColor, 0.34);
            setShade4(capShade * 1.04, capShade * 1.04, capShade * 0.96, capShade * 0.96);
            for (int i = 0; i < planks; i++)
            {
                if (travelNorthSouth)
                {
                    double za = z0 + (ts / planks) * i + (i == 0 ? 0 : gap);
                    double zb =
                        (i == planks - 1 ? z0 + ts - insS : z0 + (ts / planks) * (i + 1)) -
                        (i == planks - 1 ? 0 : gap);
                    // TS: `let plank: P3[] = footprint ? band(footprint, 'z', …) : quad(…); if (footprint) plank =
                    // band(plank, 'x', …);`. The contour band stays a TerrainContourPoint list until it reaches the
                    // builder (see the C3 PORT NOTE on `contourPointsAsP3`). Feeding the band back in is the
                    // original's own call on its BAND_POINTS scratch and yields the same (empty) band.
                    IReadOnlyList<P3> plank;
                    if (footprint != null)
                    {
                        List<TerrainContourPoint> band = TerrainContourGeometry.terrainContourAxisBandInto(footprint, "z", za, zb);
                        band = TerrainContourGeometry.terrainContourAxisBandInto(band, "x", x0 + 1.4, x0 + ts - 1.4);
                        plank = contourPointsAsP3(band);
                    }
                    else
                    {
                        plank = quad(
                            x0 + 1.4,
                            capY,
                            za,
                            x0 + ts - 1.4,
                            capY,
                            za,
                            x0 + ts - 1.4,
                            capY,
                            zb,
                            x0 + 1.4,
                            capY,
                            zb);
                    }
                    builder.addSurface(plank, 0, 1, 0, glass, SURF.bridge, 0.14, SHADE4);
                }
                else
                {
                    double xa = x0 + (ts / planks) * i + (i == 0 ? 0 : gap);
                    double xb = x0 + (ts / planks) * (i + 1) - (i == planks - 1 ? 0 : gap);
                    IReadOnlyList<P3> plank;
                    if (footprint != null)
                    {
                        List<TerrainContourPoint> band = TerrainContourGeometry.terrainContourAxisBandInto(footprint, "x", xa, xb);
                        band = TerrainContourGeometry.terrainContourAxisBandInto(band, "z", z0 + 1.4, z0 + ts - insS - 1.4);
                        plank = contourPointsAsP3(band);
                    }
                    else
                    {
                        plank = quad(
                            xa,
                            capY,
                            z0 + 1.4,
                            xb,
                            capY,
                            z0 + 1.4,
                            xb,
                            capY,
                            z0 + ts - insS - 1.4,
                            xa,
                            capY,
                            z0 + ts - insS - 1.4);
                    }
                    builder.addSurface(plank, 0, 1, 0, glass, SURF.bridge, 0.14, SHADE4);
                }
            }
            return;
        }
        for (int i = 0; i < planks; i++)
        {
            double plankWorldIndex =
                (travelNorthSouth ? frame.j0 + cell.y : frame.i0 + cell.x) * planks + i;
            // A board keeps one pigment along its complete perpendicular run. Hashing the other cell coordinate
            // changed the same physical plank's shade at every tile seam.
            double shade =
                capShade *
                (1 + (cellHash(plankWorldIndex, travelNorthSouth ? 0x4b1d : 0x6a31) - 0.5) * 0.14);
            setShade4(shade, shade, shade, shade);
            if (travelNorthSouth)
            {
                // Seams run east-west: planks stacked along z.
                double za = z0 + (ts / planks) * i + (i == 0 ? 0 : gap);
                double zb =
                    (i == planks - 1 ? z0 + ts - insS : z0 + (ts / planks) * (i + 1)) -
                    (i == planks - 1 ? 0 : gap);
                IReadOnlyList<P3> plank = footprint != null
                    ? contourPointsAsP3(TerrainContourGeometry.terrainContourAxisBandInto(footprint, "z", za, zb))
                    : quad(x0, capY, za, x0 + ts, capY, za, x0 + ts, capY, zb, x0, capY, zb);
                builder.addSurface(plank, 0, 1, 0, capColor, SURF.bridge, 0.15, SHADE4);
                if (footprint == null)
                    this.addBridgePlankJoinery(
                        builder,
                        material,
                        capY,
                        true,
                        x0,
                        z0,
                        ts,
                        (za + zb) * 0.5,
                        plankWorldIndex);
            }
            else
            {
                // Seams run north-south: planks side by side along x.
                double xa = x0 + (ts / planks) * i + (i == 0 ? 0 : gap);
                double xb = x0 + (ts / planks) * (i + 1) - (i == planks - 1 ? 0 : gap);
                IReadOnlyList<P3> plank = footprint != null
                    ? contourPointsAsP3(TerrainContourGeometry.terrainContourAxisBandInto(footprint, "x", xa, xb))
                    : quad(xa, capY, z0, xb, capY, z0, xb, capY, z0 + ts - insS, xa, capY, z0 + ts - insS);
                builder.addSurface(plank, 0, 1, 0, capColor, SURF.bridge, 0.15, SHADE4);
                if (footprint == null)
                    this.addBridgePlankJoinery(
                        builder,
                        material,
                        capY,
                        false,
                        x0,
                        z0,
                        ts,
                        (xa + xb) * 0.5,
                        plankWorldIndex);
            }
        }
    }

    /// <summary>Iron pegs, hairline checks and restrained tone variation turn the plank shader into readable carpentry.</summary>
    internal void addBridgePlankJoinery(
        TileGeometryBuilder builder,
        TerrainMaterial material,
        double capY,
        bool travelNorthSouth,
        double x0,
        double z0,
        double ts,
        double cross,
        double plankWorldIndex)
    {
        double pegRadius = Math.max(0.5, ts * 0.009);
        int pegColor = mix(material.edgeDark, material.detail, 0.18);
        double lift = capY + 0.045;
        foreach (double along in PART_C4_PEG_ALONG)
        {
            double px = travelNorthSouth ? x0 + ts * along : cross;
            double pz = travelNorthSouth ? cross : z0 + ts * along;
            builder.addOverlay(
                quad(
                    px - pegRadius,
                    lift,
                    pz - pegRadius,
                    px + pegRadius,
                    lift,
                    pz - pegRadius,
                    px + pegRadius,
                    lift,
                    pz + pegRadius,
                    px - pegRadius,
                    lift,
                    pz + pegRadius),
                pegColor,
                0.36);
            builder.bridgeJoineryMarks++;
        }
        if (cellHash(plankWorldIndex, 0x71e9) <= 0.68) return;
        double crackLength = ts * (0.12 + cellHash(plankWorldIndex, 0x193d) * 0.11);
        double offset = (cellHash(plankWorldIndex, 0x4f2b) - 0.5) * ts * 0.08;
        builder.addOverlayLineFlat(
            capY + 0.052,
            travelNorthSouth ? x0 + ts * 0.42 : cross + offset,
            travelNorthSouth ? cross + offset : z0 + ts * 0.42,
            travelNorthSouth ? x0 + ts * 0.42 + crackLength : cross - offset * 0.3,
            travelNorthSouth ? cross - offset * 0.3 : z0 + ts * 0.42 + crackLength,
            Math.max(0.42, ts * 0.0065),
            material.edgeDark,
            0.24);
        builder.bridgeJoineryMarks++;
    }

    /// <summary>
    /// HANDINK watercolour edge-pooling: a soft pigment gradient laid INSIDE a cap's cut edge (full strength at
    /// the edge, fading inward) — the classic dried-wash signature that makes every cap/terrace read as a piece
    /// of painted paper cut and laid down, at any zoom. Baked once with the tile; overlay pass (true alpha).
    /// </summary>
    /// <param name="dir">TerrainEdgeDirection.</param>
    internal void addWashEdge(
        TileGeometryBuilder builder,
        double x0,
        double x1,
        double z0,
        double z1,
        string dir,
        double y,
        int hex,
        double scale = 1,
        double startInset = 0,
        double endInset = 0)
    {
        // Corners used to bail out entirely (`startInset > 0 → return`), which is the exact opposite of how a wash
        // behaves: pigment pools HARDEST where an edge turns. That bail predated the mitred inner boundary below,
        // and once the inner edge follows the same 45-degree cross-section as the corner cut, the pigment stays on
        // real cap geometry — no floating chevron over the diagonal wall, and one continuous pool around the turn.
        // `scale` is the ROLE weight (rock reads heavier than floor). It weights the pigment — it must not also
        // shrink the geometry, which is what silently delivered 5.6 px of a 9 px authored pool and made the
        // authored dial unfalsifiable from a frame. One number, one job.
        double w = Math.min(
            TERRAIN_GEOMETRY_INK.contour.washEdgeWidth,
            (dir == "n" || dir == "s" ? z1 - z0 : x1 - x0) * 0.45);
        if (w <= 0.5) return;
        double alpha = TERRAIN_GEOMETRY_INK.contour.washEdgeAlpha * scale;
        // A contour cut removes the square cap corner. The former full-width wash rectangles ignored that cut and
        // floated over the diagonal wall, producing exactly the repeated dark chevrons seen at height junctions.
        // The inner wash edge follows the same 45-degree cross-section, so pigment remains on real cap geometry.
        double innerStartInset = Math.max(0, startInset - w);
        double innerEndInset = Math.max(0, endInset - w);
        if (dir == "n")
        {
            setShade4(alpha, alpha, 0, 0);
            builder.addOverlayShaded(
                quad(
                    x0 + startInset,
                    y,
                    z0,
                    x1 - endInset,
                    y,
                    z0,
                    x1 - innerEndInset,
                    y,
                    z0 + w,
                    x0 + innerStartInset,
                    y,
                    z0 + w),
                hex,
                alpha,
                SHADE4);
        }
        else if (dir == "s")
        {
            setShade4(0, 0, alpha, alpha);
            builder.addOverlayShaded(
                quad(
                    x0 + innerStartInset,
                    y,
                    z1 - w,
                    x1 - innerEndInset,
                    y,
                    z1 - w,
                    x1 - endInset,
                    y,
                    z1,
                    x0 + startInset,
                    y,
                    z1),
                hex,
                alpha,
                SHADE4);
        }
        else if (dir == "e")
        {
            setShade4(0, alpha, alpha, 0);
            builder.addOverlayShaded(
                quad(
                    x1 - w,
                    y,
                    z0 + innerStartInset,
                    x1,
                    y,
                    z0 + startInset,
                    x1,
                    y,
                    z1 - endInset,
                    x1 - w,
                    y,
                    z1 - innerEndInset),
                hex,
                alpha,
                SHADE4);
        }
        else
        {
            setShade4(alpha, 0, 0, alpha);
            builder.addOverlayShaded(
                quad(
                    x0,
                    y,
                    z0 + startInset,
                    x0 + w,
                    y,
                    z0 + innerStartInset,
                    x0 + w,
                    y,
                    z1 - innerEndInset,
                    x0,
                    y,
                    z1 - endInset),
                hex,
                alpha,
                SHADE4);
        }
    }

    /// <summary>Wall face detail belongs exclusively to the non-negative part of a split cliff.
    /// `footShade` is the host wall's baked crest→foot gradient endpoint, so face detail that renders in the
    /// lit surface batch can sit at exactly the wall's value at its height.</summary>
    internal void addNormalSouthFaceDetail(
        TileGeometryBuilder builder,
        int wx,
        int wy,
        double x0,
        double x1,
        double z,
        double topY,
        double bottomY,
        int faceColor,
        double footShade)
    {
        // Every wall is natural rock: its strata and mineral spots, in every world.
        this.addCliffStrata(builder, wx, wy, x0, x1, z, topY, bottomY, faceColor, footShade);
    }

    /// <summary>
    /// The one entry point for collision-neutral living wall geometry on every exposed orientation. Moss itself
    /// is already inside the host face through the shared organic-cover channel; this spends geometry only on
    /// the bush/vine/fern silhouette chosen by that same field.
    /// </summary>
    /// <param name="direction">TerrainWallDirection.</param>
    internal void addLivingWallDetail(
        TileGeometryBuilder builder,
        string direction,
        int cellX,
        int cellY,
        double x0,
        double x1,
        double z0,
        double z1,
        double topY,
        double bottomY,
        bool chasmFace = false)
    {
        TerrainWallGrowth.addTerrainWallGrowth(
            builder,
            new TerrainWallGrowthOptions
            {
                profile = chasmFace ? this.chasmWallGrowthProfile : this.wallGrowthProfile,
                direction = direction,
                x0 = x0,
                x1 = x1,
                z0 = z0,
                z1 = z1,
                topY = topY,
                bottomY = bottomY,
                cellX = cellX,
                cellY = cellY,
                density = this.cliffDressingDensity,
                mossPresenceScale = chasmFace ? 2.2 : 1,
                heroPresenceFloor = chasmFace ? 0.42 : 0,
            });
    }

    /// <summary>Crest chamfer size for a cell's south edge (0 = no bevel).</summary>
    internal double bevelFor(TerrainCell cell, TerrainEdge edge)
    {
        if (!edge.visibleFace || edge.drop < BEVEL_MIN_DROP) return 0;
        if (cell.type == TileType.Bridge) return 0;
        if (edge.material == "deck") return 0;
        if (edge.material == "bank") return BEVEL_BANK;
        if (edge.material == "abyss") return BEVEL_ROCK;
        return cell.type == TileType.Solid || edge.material == "rock" ? BEVEL_ROCK : BEVEL_EARTH;
    }

    /// <summary>Where a face's bottom lands (px).
    ///
    ///  A dry face touching Water owns the complete bank down to the opaque basin floor. Stopping that real,
    ///  contoured face at the meniscus forced the Water cell to add a second rectangular earth card underneath
    ///  it. The card did not share the dry cap's rounded endpoints, so it protruded as the grey slabs and dark
    ///  corner wedges visible along otherwise continuous shores. One physical shore now has one owner and one
    ///  outline: the dry face itself.</summary>
    /// <param name="dir">TerrainEdgeDirection.</param>
    internal double faceBottomY(
        MaterializedTerrain terrain,
        TerrainCell cell,
        string dir,
        TerrainEdge edge)
    {
        // Bridge cross-section ownership lives in the shared model. The last segment already ends at the physical
        // soffit (or at a higher occluding neighbour); extending it to the contact datum here would recreate a
        // renderer-only timber terrain column and break carrier continuity below the deck.
        if (cell.type == TileType.Bridge) return (edge.faceSegments.at(-1)?.toZ ?? cell.baseZ) * ELEV;
        TerrainCell? neighbor = terrainCellAt(
            terrain,
            cell.x + (dir == "e" ? 1 : dir == "w" ? -1 : 0),
            cell.y + (dir == "s" ? 1 : dir == "n" ? -1 : 0));
        // A shaft's real bottom is its deep floor, not the throat datum its cap sits on, so a boundary face has to
        // continue all the way down or it stops in mid-air over the abyss. Asking for the raw tile type answered
        // "Bridge" for a Chasm-CROSSING deck and dropped this wall onto the timber instead of into the shaft: the
        // land beside a crossing then carried no wall at all below deck level, which is the missing floor/wall a
        // player sees under every bridge. A crossing carries the same planes the open Chasm beside it carries.
        if (neighbor != null && terrainCellCarriesChasmFloor(neighbor))
        {
            // The contoured terraces are visible interior detail; the closed shaft itself terminates at the one
            // canonical abyss foundation. Ending a boundary wall on a local terrace leaves the projection interval
            // below that shelf open and exposes the backdrop whenever the oblique camera looks past its cap.
            return CHASM_ABYSS_SURFACE_Z * ELEV;
        }
        if (edge.contactType == TileType.Water || edge.contactType == TileType.Bridge)
        {
            if (neighbor != null && (neighbor.type == TileType.Water || neighbor.span == TileType.Water))
            {
                // The shore must end on the liquid the renderer actually DRAWS, which is the smoothed hydraulic field
                // — not this one neighbour's stored terrace datum. Where a pond steps down, the drawn surface ramps
                // across the step while the stored datums jump, so a shore cut to the upper datum stopped short of the
                // water beside it and left a sliver of backdrop along the whole bank. Sampling the same field at the
                // shared edge's own corners makes the two meet exactly, by construction rather than by luck.
                double fallback = neighbor.waterLevel ?? neighbor.surfaceZ;
                // The shared edge runs along z for east/west and along x for north/south; sample both ends and the
                // middle and take the LOWEST, so the flat-bottomed face reaches the liquid over its whole run.
                bool alongZ = dir == "e" || dir == "w";
                int edgeX = cell.x + (dir == "e" ? 1 : 0);
                int edgeY = cell.y + (dir == "s" ? 1 : 0);
                double lowest = fallback;
                foreach (double t in PART_C4_EDGE_SAMPLES)
                {
                    lowest = Math.min(
                        lowest,
                        visualWaterLevelAt(
                            terrain,
                            alongZ ? edgeX : cell.x + t,
                            alongZ ? cell.y + t : edgeY,
                            fallback));
                }
                return lowest * ELEV - TerrainGeometryCompilerModule.WATER_BASIN_DEPTH * ELEV - 0.5;
            }
        }
        return edge.toZ * ELEV - 0.5;
    }

    /// <summary>
    /// Close the triangular/quadrilateral return between a leaned east/west face and its straight north/south
    /// frontage. Without this miter the two valid faces meet only at their crest while their feet separate,
    /// exposing the hollow terrain interior. Returns are emitted only where the orthogonal edge is itself open;
    /// a continuous long side therefore remains one clean plane without artificial per-cell seams.
    /// </summary>
    /// <param name="dir">'n' | 's'.</param>
    internal void addLeaningFaceReturn(
        TileGeometryBuilder builder,
        string dir,
        double bx,
        double topLean,
        double bottomLean,
        double topY,
        double bottomY,
        double z,
        int color,
        int kind,
        double strength,
        bool actorWall,
        double normalY = 0.04,
        double footShade = 0.72)
    {
        if (topY <= bottomY + 0.02 || (Math.abs(topLean) < 0.015 && Math.abs(bottomLean) < 0.015))
            return;
        bool triangular = Math.abs(topLean) < 0.015;
        P3[] points = triangular
            ? new P3[]
            {
                new P3(bx, topY, z),
                new P3(bx + bottomLean, bottomY, z),
                new P3(bx, bottomY, z),
            }
            : quad(bx, topY, z, bx + topLean, topY, z, bx + bottomLean, bottomY, z, bx, bottomY, z);
        builder.addSurface(
            points,
            0,
            normalY,
            dir == "n" ? -1 : 1,
            color,
            kind,
            strength,
            triangular ? new double[] { 1, footShade, footShade } : new double[] { 1, 1, footShade, footShade },
            UNIT_ZERO,
            actorWall,
            false,
            dir == "n");
    }

    /// <summary>A soft grounding gradient on the LOW floor at the immediate foot of a face — full contact darkness at
    ///  the base, fading to nothing outward (real per-vertex alpha, so the floor's lit grain stays alive under
    ///  it). This is the fine ambient occlusion the coarse shadow map cannot resolve; the sun still casts the
    ///  actual shadow on top.</summary>
    /// <param name="dir">TerrainEdgeDirection.</param>
    internal void addContactStrip(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        TerrainCell cell,
        string dir,
        TerrainEdge edge,
        double startInset = 0,
        double endInset = 0,
        TerrainMaterial? material = null)
    {
        // Terrain edges are described from both adjacent cells. Only the high-side owner has a real wall foot and
        // the matching visual contour. Letting the low-side E/W cell emit as well placed its strip at `toZ` (the
        // upper cap), producing a full-height translucent card across clipped corners.
        if (cell.surfaceZ <= edge.toZ + 0.02) return;
        if (edge.drop < 0.45) return;
        if (
            edge.contactType == TileType.Water ||
            edge.contactType == TileType.Bridge ||
            edge.contactType == TileType.Chasm ||
            edge.contactType == TERRAIN_CONTACT_OUTSIDE)
            return;
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double floorY = edge.toZ * ELEV + 0.05;
        string direction = dir == "n" ? "north" : dir == "s" ? "south" : dir == "e" ? "east" : "west";
        bool climbable = edge.passable && edge.drop <= 1.05;
        double depth = Math.min(
            CONTACT_WIDTH_MAX,
            TerrainLighting.terrainContactShadowWidthPx(direction, edge.drop, ts, climbable));
        double alpha = Math.min(
            CONTACT_ALPHA_MAX,
            TerrainLighting.terrainContactShadowAlpha(direction, edge.drop, climbable) * 1.55);
        if (depth <= 0.1 || alpha <= 0.004) return;
        int shadow = this.tileset?.elevation.shadow ?? TERRAIN_GEOMETRY_INK_WORLD_LINE;
        bool northSouth = dir == "n" || dir == "s";
        double start = (northSouth ? x0 : z0) + startInset;
        double end = (northSouth ? x0 : z0) + ts - endInset;
        double edgeCoordinate = northSouth ? (dir == "n" ? z0 : z0 + ts) : dir == "w" ? x0 : x0 + ts;
        bool emitted = TerrainGroundingGeometry.addGroundContactBand(
            new GroundContactBandOptions
            {
                builder = builder,
                direction = dir,
                start = start,
                end = end,
                edge = edgeCoordinate,
                y = floorY,
                width = depth,
                alpha = alpha,
                color = shadow,
            });
        if (
            !emitted ||
            material == null ||
            (cell.type != TileType.Solid && cell.type != TileType.Floor) ||
            this.tileset?.construction != "natural" ||
            TerrainOrganicForm.Enabled)
            return;
        TerrainGroundingGeometry.addCliffFootPebbles(
            new CliffFootPebbleOptions
            {
                builder = builder,
                direction = dir,
                start = start,
                end = end,
                edge = edgeCoordinate,
                floorY = edge.toZ * ELEV + 0.04,
                tileSize = ts,
                dropLevels = edge.drop,
                worldCellX = frame.i0 + cell.x,
                worldCellY = frame.j0 + cell.y,
                density = this.cliffDressingDensity,
                capColor = mix(material.topDark, material.edgeLight, 0.22),
                sideColor = mix(material.side, material.edgeDark, 0.18),
            });
    }

    /// <summary>
    /// Materializes one genuine deep-floor tile. This deliberately follows the ordinary Floor cap contract:
    /// shared-corner pigment, a welded 3x3 lattice and topology-pinned organic relief. Like an ordinary low Floor,
    /// the tile itself remains complete up to the wall. The high wall owner contributes the small floor apron under
    /// a rounded corner, so wall and floor share one exact outline instead of independently cutting two silhouettes.
    /// </summary>
    internal void buildChasmFloor(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell sourceCell,
        ChasmFloorCompilation? compilation)
    {
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + sourceCell.x) * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + (frame.j0 + sourceCell.y) * ts;
        double z1 = z0 + ts;
        TerrainMaterial safetyMaterial = TerrainChasmGeometry.resolveChasmFloorMaterial(terrain, plan, sourceCell);
        int safetyColor = mix(
            safetyMaterial.deep ?? safetyMaterial.topDark,
            safetyMaterial.edgeDark,
            0.38);
        TerrainCell? compiledCell = compilation != null ? partC4ItemAt(compilation.terrain.cells, sourceCell.id) : null;
        // One welded maximum-depth foundation is the final ownership layer beneath EVERY Chasm carrier. The
        // detailed basin may terrace above it, but gaps between independently contoured terrace caps/walls must see
        // genuine Chasm ground, never the still deeper global backdrop. Keeping this on the shared abyss datum also
        // welds neighbouring carriers (including Bridge carriers) into one continuous fail-closed plane instead of
        // stacking disconnected per-cell safety cards at different heights.
        double safetyY = CHASM_ABYSS_SURFACE_Z * ELEV - 0.035;
        builder.addSurface(
            quad(x0, safetyY, z0, x1, safetyY, z0, x1, safetyY, z1, x0, safetyY, z1),
            0,
            1,
            0,
            safetyColor,
            SURF.chasmFloor,
            0.08,
            UNIT_SHADE);
        if (compilation == null)
        {
            builder.chasmFloorCells++;
            return;
        }
        TerrainCell? cell = compiledCell;
        TerrainMaterial? material = partC4ItemAt(compilation.plan.materials, sourceCell.id);
        if (cell == null || material == null)
        {
            builder.chasmFloorCells++;
            return;
        }
        builder.withTerrainChannelRemap(remapFloorSurfaceToChasm, remapFloorOverlayToChasm, () =>
        {
            this.buildSolidCell(builder, frame, compilation.terrain, compilation.plan, cell, material);
        });
        if (sourceCell.type == TileType.Chasm && this.tileset != null && this.cliffDressingDensity > 0)
        {
            double floorZ = terrainChasmFloorZAt(terrain, sourceCell) ?? CHASM_ABYSS_SURFACE_Z;
            bool organic = this.visualGrounding && this.terrainSurfaceProfile.organicGround;
            TerrainChasmFloorGeometry.addChasmFloorDetails(
                builder,
                new ChasmFloorDetailOptions
                {
                    originX = x0,
                    originZ = z0,
                    tileSize = ts,
                    floorY = floorZ * ELEV,
                    cellX = frame.i0 + sourceCell.x,
                    cellY = frame.j0 + sourceCell.y,
                    floorColor = mix(material.top, material.edgeDark, 0.58),
                    edgeColor = mix(material.edgeDark, this.tileset.decal.ink, 0.42),
                    growthColor = mix(this.floorLushPole, material.edgeDark, 0.54),
                    density = this.cliffDressingDensity,
                    groundLiftAt = (u, v) =>
                        organic
                            ? TerrainVisualGround.terrainOrganicHeightAt(x0 + u * ts, z0 + v * ts, this.terrainSurfaceProfile) *
                              TerrainChasmFloorGeometry.terrainChasmFloorPointMask(terrain, sourceCell.x + u, sourceCell.y + v, floorZ)
                            : 0,
                });
        }
        builder.chasmFloorCells++;
    }

    /// <summary>
    /// Close a Chasm carrier's shaft where it meets standing fluid.
    ///
    /// EVERY carrier owes this, which is why it is its own component: an exposed Chasm cell builds it as part of
    /// <see cref="buildChasmCell"/>, but a Chasm-crossing Bridge never enters that path at all — it only materialises
    /// its deep floor — so a pond running up against a crossed shaft had nothing between the camera and the
    /// backdrop over the whole drop. The interval is the carrier's own throat-to-floor span, taken from the
    /// shared model rather than from `surfaceZ`/`baseZ`, which on a Bridge describe the timber deck.
    /// </summary>
    /// <param name="direction">TerrainEdgeDirection.</param>
    internal IReadOnlyList<LiquidChasmEdgePoint> liquidChasmBoundaryInto(
        MaterializedTerrain terrain,
        TerrainBakeFrame frame,
        TerrainCell liquidCell,
        string direction)
    {
        double ts = frame.tileSize;
        int worldCellX = frame.i0 + liquidCell.x;
        int worldCellY = frame.j0 + liquidCell.y;
        double x0 = frame.originX + worldCellX * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + worldCellY * ts;
        double z1 = z0 + ts;
        LiquidChasmContour contour = liquidChasmContourForCell(
            terrain,
            liquidCell,
            worldCellX,
            worldCellY,
            this.coherentContours,
            this.liquidChasmContourScratch);
        return liquidChasmEdgePathInto(
            terrain,
            liquidCell,
            contour,
            direction,
            x0,
            x1,
            z0,
            z1,
            this.liquidChasmEdgeScratch);
    }

    internal void buildChasmFluidBoundaryClosures(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell)
    {
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double z1 = z0 + ts;
        double shaftThroatZ = terrainChasmThroatZAt(terrain, cell) ?? cell.surfaceZ;
        double shaftFloorZ = CHASM_ABYSS_SURFACE_Z;
        foreach (string dir in PART_C4_NESW)
        {
            TerrainCell? neighbor = terrainCellAt(
                terrain,
                cell.x + (dir == "e" ? 1 : dir == "w" ? -1 : 0),
                cell.y + (dir == "s" ? 1 : dir == "n" ? -1 : 0));
            if (neighbor == null || terrainCellCarriesChasmFloor(neighbor)) continue;
            if (
                neighbor.type != TileType.Water &&
                !(neighbor.type == TileType.Bridge && neighbor.span == TileType.Water))
                continue;
            string sourceDirection =
                dir == "n" ? "s" : dir == "s" ? "n" : dir == "e" ? "w" : "e";
            // A planned Chasm fall owns this complete portal. Emitting the cardinal shaft wall as well places an
            // opaque geological card inside the curved sheet; projection exposes it as the reported bars and wedges.
            // Non-falling liquid contacts still retain the fail-closed wall.
            if (TerrainRenderPlanModule.terrainWaterfallPortalForEdge(plan.waterfalls, neighbor.id, sourceDirection, cell.id) != null)
                continue;
            TerrainChasmGeometry.addChasmFluidBoundaryClosure(
                builder,
                dir,
                x0,
                x1,
                z0,
                z1,
                shaftFloorZ * ELEV,
                shaftThroatZ * ELEV,
                TerrainChasmGeometry.resolveChasmFaceMaterial(terrain, plan, neighbor, sourceDirection));
        }
    }

    internal void buildChasmCell(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        ChasmFloorCompilation? chasmFloorCompilation)
    {
        double ts = frame.tileSize;
        int wx = frame.i0 + cell.x;
        int wy = frame.j0 + cell.y;
        double x0 = frame.originX + wx * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + wy * ts;
        double z1 = z0 + ts;
        this.buildChasmFloor(builder, frame, terrain, plan, cell, chasmFloorCompilation);
        this.buildChasmFluidBoundaryClosures(builder, frame, terrain, plan, cell);
        var depth = CARTOON_TERRAIN_STYLE.chasmDepth;
        double boundaryTopZFor(TerrainCell candidate) =>
            candidate.type == TileType.Bridge && candidate.waterLevel != null
                ? candidate.waterLevel.Value
                : candidate.surfaceZ;
        foreach (string dir in PART_C4_NESW)
        {
            TerrainCell? neighbor = terrainCellAt(
                terrain,
                cell.x + (dir == "e" ? 1 : dir == "w" ? -1 : 0),
                cell.y + (dir == "s" ? 1 : dir == "n" ? -1 : 0));
            if (neighbor == null || terrainCellCarriesChasmFloor(neighbor)) continue;
            string sourceDirection =
                dir == "n" ? "s" : dir == "s" ? "n" : dir == "e" ? "w" : "e";
            // A Water/Bridge carrier owns the liquid opening; never place a horizontal rock bench behind or beside it.
            // In the edge-on east/west case no waterfall card is emitted, so these benches were left visible with the
            // Water material as isolated cyan rectangles and a straight projected bar over the Chasm silhouette.
            if (
                neighbor.type == TileType.Water ||
                (neighbor.type == TileType.Bridge && neighbor.span == TileType.Water))
            {
                continue;
            }
            // The falling sheet owns this complete boundary interval. A Chasm rim bench underneath it begins one cell
            // inside a run and ends one cell early, which projected as the characteristic inset black bar across wide
            // waterfalls even after their backing wall and basin support had been removed.
            if (TerrainRenderPlanModule.terrainWaterfallPortalForEdge(plan.waterfalls, neighbor.id, sourceDirection, cell.id) != null)
                continue;
            TerrainMaterial material = TerrainChasmGeometry.resolveChasmFaceMaterial(terrain, plan, neighbor, sourceDirection);
            double neighborBoundaryTopZ = boundaryTopZFor(neighbor);
            double topY = neighborBoundaryTopZ * ELEV;
            int runCoordinate = dir == "n" || dir == "s" ? wx : wy;
            int sideCoordinate = dir == "n" || dir == "s" ? wy : wx;
            int tangentX = dir == "n" || dir == "s" ? 1 : 0;
            int tangentY = tangentX == 0 ? 1 : 0;
            int normalX = dir == "e" ? 1 : dir == "w" ? -1 : 0;
            int normalY = dir == "s" ? 1 : dir == "n" ? -1 : 0;
            TerrainCell? previousChasm = terrainCellAt(terrain, cell.x - tangentX, cell.y - tangentY);
            TerrainCell? nextChasm = terrainCellAt(terrain, cell.x + tangentX, cell.y + tangentY);
            TerrainCell? previousDry = previousChasm != null
                ? terrainCellAt(terrain, previousChasm.x + normalX, previousChasm.y + normalY)
                : null;
            TerrainCell? nextDry = nextChasm != null
                ? terrainCellAt(terrain, nextChasm.x + normalX, nextChasm.y + normalY)
                : null;
            bool continuesAtStart =
                previousChasm?.type == TileType.Chasm &&
                previousDry != null &&
                previousDry.type != TileType.Chasm &&
                Math.abs(boundaryTopZFor(previousDry) - neighborBoundaryTopZ) <= 0.35;
            bool continuesAtEnd =
                nextChasm?.type == TileType.Chasm &&
                nextDry != null &&
                nextDry.type != TileType.Chasm &&
                Math.abs(boundaryTopZFor(nextDry) - neighborBoundaryTopZ) <= 0.35;
            // Adjacent boundary cells share the average at their common endpoint. The ledge therefore remains one
            // watertight sloping band when the authoritative plateau height changes instead of forming stacked cards.
            // At a run end or 90-degree junction its width converges to zero; perpendicular benches can therefore
            // never overlap into the conspicuous layered chevrons that made ravine corners look corrupted.
            double startTopY = continuesAtStart
                ? (topY + boundaryTopZFor(previousDry!) * ELEV) * 0.5
                : topY;
            double endTopY = continuesAtEnd ? (topY + boundaryTopZFor(nextDry!) * ELEV) * 0.5 : topY;
            for (int layer = 0; layer < 1; layer++)
            {
                double fieldScale = layer == 0 ? 7.5 : layer == 1 ? 9.5 : 12;
                int fieldSalt = layer * 17 + (dir == "n" ? 7 : dir == "e" ? 17 : dir == "s" ? 29 : 43);
                double fieldA = smoothCellNoise(runCoordinate, sideCoordinate, fieldScale, fieldSalt);
                double fieldB = smoothCellNoise(runCoordinate + 1, sideCoordinate, fieldScale, fieldSalt);
                double coverage =
                    layer == 0 ? 1 : layer == 1 ? depth.middleLedgeCoverage : depth.deepLedgeCoverage;
                double presenceA = layer == 0 ? 1 : clamp((coverage + 0.12 - fieldA) / 0.24, 0, 1);
                double presenceB = layer == 0 ? 1 : clamp((coverage + 0.12 - fieldB) / 0.24, 0, 1);
                if (presenceA < 0.02 && presenceB < 0.02) continue;
                double baseDepth =
                    layer == 0
                        ? depth.upperLedgeDepthLevels
                        : layer == 1
                            ? depth.middleLedgeDepthLevels
                            : depth.deepLedgeDepthLevels;
                double widthCells =
                    layer == 0
                        ? depth.upperLedgeWidthCells
                        : layer == 1
                            ? depth.middleLedgeWidthCells
                            : depth.deepLedgeWidthCells;
                double depthVariation = 0.16 + layer * 0.08;
                double ledgeYA = startTopY - (baseDepth + (fieldA - 0.5) * depthVariation) * ELEV;
                double ledgeYB = endTopY - (baseDepth + (fieldB - 0.5) * depthVariation) * ELEV;
                double ledgeWidthA =
                    ts * widthCells * (0.9 + fieldA * 0.2) * presenceA * (continuesAtStart ? 1 : 0);
                double ledgeWidthB =
                    ts * widthCells * (0.9 + fieldB * 0.2) * presenceB * (continuesAtEnd ? 1 : 0);
                if (ledgeWidthA + ledgeWidthB < 0.02) continue;
                // A terminal taper piece converges onto the boundary plane exactly where the neighbouring wall may
                // be cut (waterfall backing opening / open corner). Its degenerate tip then rasterises a sub-pixel
                // wedge straight through to the clear colour: the reported one-frame white dot at overhang rims.
                // Benches therefore live only on interior run cells; a run simply ends one cell earlier.
                if (!continuesAtStart || !continuesAtEnd) continue;
                int geologicalBase = mix(material.side, material.edgeLight, 0.16 - layer * 0.035);
                int topColor = mix(
                    geologicalBase,
                    this.chasmPaletteForCell(cell)?.face ?? material.edgeDark,
                    0.12 + layer * 0.1);
                int sideColor = mix(
                    topColor,
                    this.chasmPaletteForCell(cell)?.deep ?? material.edgeDark,
                    0.24);
                TerrainChasmGeometry.addChasmDepthLedge(
                    builder,
                    dir,
                    x0,
                    x1,
                    z0,
                    z1,
                    ledgeYA,
                    ledgeYB,
                    ledgeWidthA,
                    ledgeWidthB,
                    layer,
                    topColor,
                    sideColor);
            }
        }
    }

    /// <summary>Geological shoulders around visible or under-Bridge Water drops into lower Water and Chasm.</summary>
    internal void buildWaterDropWalls(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell)
    {
        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + cell.x) * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + (frame.j0 + cell.y) * ts;
        double z1 = z0 + ts;
        double sourceLevel = cell.waterLevel ?? cell.surfaceZ;
        TerrainMaterial? plannedWaterMaterial = partC4ItemAt(plan.materials, cell.id);
        TerrainMaterial sourceWaterMaterial =
            plannedWaterMaterial?.id == "water"
                ? plannedWaterMaterial
                : TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.water;
        int wetBankPigment = mix(
            sourceWaterMaterial.deep ?? sourceWaterMaterial.topDark,
            sourceWaterMaterial.mid ?? sourceWaterMaterial.top,
            0.34);
        TerrainCell? targetFor(string dir) =>
            terrainCellAt(
                terrain,
                cell.x + (dir == "e" ? 1 : dir == "w" ? -1 : 0),
                cell.y + (dir == "s" ? 1 : dir == "n" ? -1 : 0));
        // The shaft a neighbour opens, whether it is an exposed Chasm cell or a Chasm-crossing Bridge deck. Reading
        // `target.surfaceZ` directly answered with the DECK of such a bridge, so the geological drop wall beside a
        // Bridge-crossed shaft was skipped entirely and left the abyss open from the water line to the deep floor.
        double? chasmThroatFor(TerrainCell? target) =>
            target != null && terrainCellCarriesChasmFloor(target)
                ? terrainChasmThroatZAt(terrain, target)
                : null;
        foreach (string dir in PART_C4_NSEW)
        {
            TerrainEdge edge = cell.edges[dir];
            TerrainCell? target = targetFor(dir);
            // Different Water datums are now samples of one smooth hydraulic surface, not geological openings.
            double? targetLevelOrNull = chasmThroatFor(target);
            if (targetLevelOrNull == null) continue;
            double targetLevel = targetLevelOrNull.Value;
            double drop = sourceLevel - targetLevel;
            if (drop < TERRAIN_WATERFALL_MIN_DROP) continue;
            if (TerrainRenderPlanModule.terrainWaterfallPortalForEdge(plan.waterfalls, cell.id, dir, target?.id) != null)
                continue;
            // No hydraulic portal exists on this edge, so this is a non-falling liquid-to-shaft contact and retains
            // its geological closure. Planned Chasm falls were consumed above because their curtain owns the opening.
            double completeBottomY = targetLevel * ELEV;
            double completeTopY = sourceLevel * ELEV;
            double completeHeight = Math.max(0.01, completeTopY - completeBottomY);
            IReadOnlyList<LiquidChasmEdgePoint> boundary = this.liquidChasmBoundaryInto(terrain, frame, cell, dir);
            // TerrainEdge.faceSegments describes the visible source volume. For a Bridge that is deliberately only
            // the thin deck fascia; derive the geological Water drop from the fluid datums so it starts below the
            // deck, retains the zero-level Chasm material split, and can leave the waterfall interval unoccluded.
            (double fromZ, double toZ)[] waterDropSegments =
                sourceLevel > 0
                    ? new (double fromZ, double toZ)[]
                    {
                        (sourceLevel, 0),
                        (0, targetLevel),
                    }
                    : new (double fromZ, double toZ)[] { (sourceLevel, targetLevel) };
            TerrainMaterial backingMaterial = TerrainChasmGeometry.resolveChasmFaceMaterial(terrain, plan, cell, dir);
            string backingRole =
                cell.type == TileType.Bridge ? "earth" : TerrainChasmGeometry.chasmContinuationMaterial(cell, edge);
            int backingBaseColor = TerrainRenderPlanModule.terrainFaceBaseColor(backingMaterial, backingRole);
            // One wet geological carrier continues from the ordinary bank into the shaft. Material family, pigment
            // and world-space pattern remain identical at level zero; only the continuous depth shade changes.
            int continuousFaceColor = mix(
                backingBaseColor,
                wetBankPigment,
                CARTOON_TERRAIN_STYLE.waterBasin.backingWaterBlend);
            int continuousFaceKind = SURF.waterBank;
            double continuousFaceStrength = 0.18;
            foreach ((double fromZ, double toZ) segment in waterDropSegments)
            {
                double topY = segment.fromZ * ELEV;
                double bottomY = segment.toZ * ELEV;
                if (topY <= bottomY + 0.02) continue;
                int faceColor = continuousFaceColor;
                double backingCrest = wallDepthShadeAt(completeTopY, topY, true);
                double backingFoot = wallDepthShadeAt(completeTopY, bottomY, true);
                setShade4(backingCrest, backingCrest, backingFoot, backingFoot);
                // The crest is the exact same deformed boundary as horizontal liquid and waterfall lip. With depth the
                // backing relaxes continuously to the cardinal shaft edge that owns the complete Chasm floor. The old
                // all-cardinal wall broke the crest; an all-deformed vertical wall moved away from the abyss foundation.
                // This one ruled surface joins both authoritative boundaries without the ruler-straight return cards.
                for (int index = 1; index < boundary.Count; index++)
                {
                    LiquidChasmEdgePoint a = boundary[index - 1];
                    LiquidChasmEdgePoint b = boundary[index];
                    double progressA = (double)(index - 1) / (boundary.Count - 1);
                    double progressB = (double)index / (boundary.Count - 1);
                    bool northSouth = dir == "n" || dir == "s";
                    double cardinalAx = northSouth ? x0 + (x1 - x0) * progressA : dir == "e" ? x1 : x0;
                    double cardinalBx = northSouth ? x0 + (x1 - x0) * progressB : dir == "e" ? x1 : x0;
                    double cardinalAz = northSouth ? (dir == "n" ? z0 : z1) : z0 + (z1 - z0) * progressA;
                    double cardinalBz = northSouth ? (dir == "n" ? z0 : z1) : z0 + (z1 - z0) * progressB;
                    double topDeformation = clamp((topY - completeBottomY) / completeHeight, 0, 1);
                    double bottomDeformation = clamp((bottomY - completeBottomY) / completeHeight, 0, 1);
                    double topAx = cardinalAx + (a.x - cardinalAx) * topDeformation;
                    double topAz = cardinalAz + (a.z - cardinalAz) * topDeformation;
                    double topBx = cardinalBx + (b.x - cardinalBx) * topDeformation;
                    double topBz = cardinalBz + (b.z - cardinalBz) * topDeformation;
                    double bottomAx = cardinalAx + (a.x - cardinalAx) * bottomDeformation;
                    double bottomAz = cardinalAz + (a.z - cardinalAz) * bottomDeformation;
                    double bottomBx = cardinalBx + (b.x - cardinalBx) * bottomDeformation;
                    double bottomBz = cardinalBz + (b.z - cardinalBz) * bottomDeformation;
                    bool reverse = dir == "s" || dir == "w";
                    double startX = reverse ? topBx : topAx;
                    double startZ = reverse ? topBz : topAz;
                    double endX = reverse ? topAx : topBx;
                    double endZ = reverse ? topAz : topBz;
                    double bottomEndX = reverse ? bottomAx : bottomBx;
                    double bottomEndZ = reverse ? bottomAz : bottomBz;
                    double bottomStartX = reverse ? bottomBx : bottomAx;
                    double bottomStartZ = reverse ? bottomBz : bottomAz;
                    builder.addSurface(
                        quad(
                            startX,
                            topY,
                            startZ,
                            endX,
                            topY,
                            endZ,
                            bottomEndX,
                            bottomY,
                            bottomEndZ,
                            bottomStartX,
                            bottomY,
                            bottomStartZ),
                        dir == "e" ? 1 : dir == "w" ? -1 : 0,
                        dir == "e" || dir == "w" ? 0.28 : 0,
                        dir == "n" ? -1 : dir == "s" ? 1 : 0,
                        faceColor,
                        continuousFaceKind,
                        continuousFaceStrength,
                        SHADE4,
                        UNIT_ZERO,
                        false,
                        dir == "e" || dir == "w",
                        dir == "n");
                }
            }
        }
    }

    internal void buildWaterfall(
        TileGeometryBuilder builder,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell source,
        TerrainWaterfallEffect waterfall,
        List<WaterfallCurtainBoundary> boundaries)
    {
        TerrainCell? target = partC4ItemAt(terrain.cells, waterfall.targetCellId);
        if (target == null) return;
        // Adjacent Water belongs to one visual hydraulic surface. Its stored datums are control samples for the
        // component-wide spline below, never permission to replace an entire logical cell with a ramp/card.
        if (!TerrainRenderPlanModule.terrainWaterfallOwnsPortal(waterfall)) return;
        bool touchesBridge = source.type == TileType.Bridge || target.type == TileType.Bridge;
        // Bridge portals do not invent a second hydraulic transition. Their recessed span is the same neighbouring
        // water body, hidden only by the deck; a real Bridge-to-Chasm opening is still allowed to fall.
        if (waterfall.landing == "water" && touchesBridge) return;

        double edgeGridX =
            source.x + (waterfall.direction == "e" ? 1 : waterfall.direction == "w" ? 0 : 0.5);
        double edgeGridY =
            source.y + (waterfall.direction == "s" ? 1 : waterfall.direction == "n" ? 0 : 0.5);
        double topLevel = visualWaterLevelAt(
            terrain,
            edgeGridX,
            edgeGridY,
            source.waterLevel ?? waterfall.topZ);
        double bottomLevel =
            waterfall.landing == "water" ? (target.waterLevel ?? waterfall.bottomZ) : waterfall.bottomZ;
        double drop = topLevel - bottomLevel;
        if (drop < TERRAIN_WATERFALL_MIN_DROP) return;

        TerrainMaterial waterMaterialFor(TerrainCell cell)
        {
            TerrainCell appearanceCell;
            if (cell.type == TileType.Water)
            {
                appearanceCell = cell;
            }
            else
            {
                // `({ ...cell, type: TileType.Water } as TerrainCell)`
                appearanceCell = cell.Clone();
                appearanceCell.type = TileType.Water;
            }
            TerrainMaterial? planned = cell.type == TileType.Water ? partC4ItemAt(plan.materials, cell.id) : null;
            return this.biome?.materialDialect == "aegis-citadel"
                ? this.materialForCell(appearanceCell, terrain, partC4MoistureAt(plan.moisture, cell.id))
                : planned?.id == "water"
                    ? planned
                    : this.materialForCell(appearanceCell, terrain, partC4MoistureAt(plan.moisture, cell.id));
        }
        TerrainMaterial sourceMaterial = waterMaterialFor(source);
        TerrainMaterial targetMaterial =
            waterfall.landing == "water" && target.type == TileType.Water
                ? waterMaterialFor(target)
                : sourceMaterial;
        static bool sameMaterial(TerrainMaterial a, TerrainMaterial b) =>
            a.top == b.top &&
            a.mid == b.mid &&
            a.shallow == b.shallow &&
            a.deep == b.deep &&
            a.highlight == b.highlight;

        int axis = waterfall.direction == "n" || waterfall.direction == "s" ? 1 : -1;
        int along = axis > 0 ? source.x : source.y;
        int cross = axis > 0 ? source.y : source.x;
        bool joinsRun(TerrainWaterfallEffect candidate, int expectedAlong)
        {
            TerrainCell? candidateSource = partC4ItemAt(terrain.cells, candidate.sourceCellId);
            TerrainCell? candidateTarget = partC4ItemAt(terrain.cells, candidate.targetCellId);
            if (
                candidateSource == null ||
                candidateTarget == null ||
                !this.ownCell(candidateSource) ||
                candidate.direction != waterfall.direction ||
                candidate.landing != waterfall.landing ||
                (axis > 0 ? candidateSource.y : candidateSource.x) != cross ||
                (axis > 0 ? candidateSource.x : candidateSource.y) != expectedAlong)
                return false;
            double candidateBottom =
                candidate.landing == "water"
                    ? (candidateTarget.waterLevel ?? candidate.bottomZ)
                    : candidate.bottomZ;
            if (
                // A receiving Water surface is a real join and must share one datum. A Chasm bottom is not the liquid
                // join: it is terrain below a common cloud-entry interval, and may terrace beneath a broad fall. Using
                // that hidden floor as a run key split one continuous lip into the one-cell curtain cards seen in game.
                candidate.landing == "water" &&
                Math.abs(candidateBottom - bottomLevel) > 0.08)
                return false;
            TerrainMaterial candidateSourceMaterial = waterMaterialFor(candidateSource);
            TerrainMaterial candidateTargetMaterial =
                candidate.landing == "water" && candidateTarget.type == TileType.Water
                    ? waterMaterialFor(candidateTarget)
                    : candidateSourceMaterial;
            return
                sameMaterial(candidateSourceMaterial, sourceMaterial) &&
                sameMaterial(candidateTargetMaterial, targetMaterial);
        }

        int runStart = along;
        int runEnd = along;
        while (plan.waterfalls.some(candidate => joinsRun(candidate, runStart - 1))) runStart--;
        if (along != runStart) return;
        while (plan.waterfalls.some(candidate => joinsRun(candidate, runEnd + 1))) runEnd++;

        // East/west Chasm curtains used to be dropped here because ±X was mathematically edge-on in the fixed
        // projection, so the sheet would have carried no screen area. The presentation orbit
        // (TERRAIN_CAMERA_WORLD_YAW) ends that: an east-facing curtain now has real width, and skipping it
        // leaves the pond ending at the chasm lip in a bare vertical cut — the reported "open side" where water
        // should visibly fall. The plan already authors these falls (`createTerrainWaterfalls` walks all four
        // directions); only the compiler was discarding half of them. Cost is bounded — one curtain per lip cell,
        // exactly as for north/south.

        double ts = frame.tileSize;
        double x0 = frame.originX + (frame.i0 + source.x) * ts;
        double x1 = x0 + ts;
        double z0 = frame.originY + (frame.j0 + source.y) * ts;
        double z1 = z0 + ts;
        int runLength = runEnd - runStart + 1;
        // The crest is the source surface's exact topological edge. Even a nominal sub-pixel kick exposed the dark
        // backdrop after projection and read as a ruler-straight frame across the waterfall.
        double sheetCoordinate =
            waterfall.direction == "n"
                ? z0
                : waterfall.direction == "s"
                    ? z1
                    : waterfall.direction == "w"
                        ? x0
                        : x1;
        // Build the crest from the exact same regular deformation field as the horizontal Water grid. Internal run
        // vertices are duplicated intentionally here (the builder welds by value), so independently authored source
        // cells still agree bit-for-bit at their join without introducing a polygon fan or a second lip profile.
        var lateralAcross = new List<double>();
        var lateralGridAlong = new List<double>();
        var lateralBottomY = new List<double>();
        var lateralLip = new List<double>();
        var lateralTopologyOutwardX = new List<double>();
        var lateralTopologyOutwardZ = new List<double>();
        var lipPoint = new LiquidChasmEdgePoint { x = 0, z = 0 };
        var lipOutward = new LiquidChasmEdgePoint { x = 0, z = 0 };
        for (int runIndex = 0; runIndex < runLength; runIndex++)
        {
            int alongCell = runStart + runIndex;
            TerrainCell? runCell = terrainCellAt(
                terrain,
                axis > 0 ? alongCell : source.x,
                axis > 0 ? source.y : alongCell);
            if (runCell == null) continue;
            TerrainWaterfallEffect runWaterfall =
                plan.waterfalls.find(candidate => joinsRun(candidate, alongCell)) ?? waterfall;
            double cellX0 = frame.originX + (frame.i0 + runCell.x) * ts;
            double cellX1 = cellX0 + ts;
            double cellZ0 = frame.originY + (frame.j0 + runCell.y) * ts;
            double cellZ1 = cellZ0 + ts;
            LiquidChasmContour runContour = liquidChasmContourForCell(
                terrain,
                runCell,
                frame.i0 + runCell.x,
                frame.j0 + runCell.y,
                this.coherentContours,
                this.liquidChasmContourScratch);
            for (int segment = runIndex == 0 ? 0 : 1; segment <= LIQUID_CHASM_EDGE_SEGMENTS; segment++)
            {
                liquidChasmEdgePointAt(
                    terrain,
                    runCell,
                    runContour,
                    waterfall.direction,
                    cellX0,
                    cellX1,
                    cellZ0,
                    cellZ1,
                    (double)segment / LIQUID_CHASM_EDGE_SEGMENTS,
                    lipPoint);
                liquidChasmEdgeOutwardAt(
                    runContour,
                    waterfall.direction,
                    (double)segment / LIQUID_CHASM_EDGE_SEGMENTS,
                    lipOutward);
                lateralAcross.push(axis > 0 ? lipPoint.x : lipPoint.z);
                // The horizontal liquid grid owns height at the logical cell edge, while XZ macro deformation only
                // changes that edge's silhouette. Recovering this coordinate from the displaced world point made two
                // perpendicular curtains assign different heights to their one shared corner. Carry the exact logical
                // parameter instead, so surface and every curtain endpoint are byte-identical in all three axes.
                lateralGridAlong.push(
                    (axis > 0 ? runCell.x : runCell.y) + (double)segment / LIQUID_CHASM_EDGE_SEGMENTS);
                double columnBottomLevel = bottomLevel;
                if (waterfall.landing == "chasm")
                {
                    columnBottomLevel = runWaterfall.bottomZ;
                    if (segment == 0 || segment == LIQUID_CHASM_EDGE_SEGMENTS)
                    {
                        double cornerGridX =
                            axis > 0 ? runCell.x + (double)segment / LIQUID_CHASM_EDGE_SEGMENTS : edgeGridX;
                        double cornerGridY =
                            axis > 0 ? edgeGridY : runCell.y + (double)segment / LIQUID_CHASM_EDGE_SEGMENTS;
                        // Both corner coordinates are integral here (segment is 0 or LIQUID_CHASM_EDGE_SEGMENTS and
                        // the fixed edge coordinate of a n/s resp. e/w fall is a whole cell edge), so the domain
                        // port's int grid-corner parameters receive the exact JS values.
                        columnBottomLevel =
                            TerrainRenderPlanModule.terrainWaterfallBottomZAtCorner(
                                terrain,
                                plan.waterfalls,
                                (int)cornerGridX,
                                (int)cornerGridY) ??
                            columnBottomLevel;
                    }
                }
                lateralBottomY.push(columnBottomLevel * ELEV);
                lateralLip.push(axis > 0 ? lipPoint.z : lipPoint.x);
                lateralTopologyOutwardX.push(lipOutward.x);
                lateralTopologyOutwardZ.push(lipOutward.z);
            }
        }
        if (lateralAcross.Count < 2) return;
        int lateralSegments = lateralAcross.Count - 1;
        var lateralWorldX = new double[lateralSegments + 1];
        var lateralWorldZ = new double[lateralSegments + 1];
        var lateralFloorX = new double[lateralSegments + 1];
        var lateralFloorZ = new double[lateralSegments + 1];
        var lateralTangentX = new double[lateralSegments + 1];
        var lateralTangentZ = new double[lateralSegments + 1];
        var lateralOutwardX = new double[lateralSegments + 1];
        var lateralOutwardZ = new double[lateralSegments + 1];
        int cardinalOutwardX = waterfall.direction == "e" ? 1 : waterfall.direction == "w" ? -1 : 0;
        int cardinalOutwardZ = waterfall.direction == "s" ? 1 : waterfall.direction == "n" ? -1 : 0;
        for (int column = 0; column <= lateralSegments; column++)
        {
            lateralWorldX[column] = axis > 0 ? lateralAcross[column] : lateralLip[column];
            lateralWorldZ[column] = axis > 0 ? lateralLip[column] : lateralAcross[column];
            double cardinalAcross =
                (axis > 0
                    ? frame.originX + (frame.i0 + runStart) * ts
                    : frame.originY + (frame.j0 + runStart) * ts) +
                ((double)column / lateralSegments) * runLength * ts;
            lateralFloorX[column] = axis > 0 ? cardinalAcross : sheetCoordinate;
            lateralFloorZ[column] = axis > 0 ? sheetCoordinate : cardinalAcross;
        }
        double crestSpan = 0;
        for (int column = 0; column < lateralSegments; column++)
        {
            crestSpan += Math.hypot(
                lateralWorldX[column + 1] - lateralWorldX[column],
                lateralWorldZ[column + 1] - lateralWorldZ[column]);
        }
        for (int column = 0; column <= lateralSegments; column++)
        {
            int low = Math.max(0, column - 1);
            int high = Math.min(lateralSegments, column + 1);
            double tangentX = lateralWorldX[high] - lateralWorldX[low];
            double tangentZ = lateralWorldZ[high] - lateralWorldZ[low];
            double tangentLength = Math.hypot(tangentX, tangentZ);
            if (!Js.Truthy(tangentLength)) tangentLength = 1; // `|| 1`
            lateralTangentX[column] = tangentX / tangentLength;
            lateralTangentZ[column] = tangentZ / tangentLength;
            double localOutwardX = -lateralTangentZ[column];
            double localOutwardZ = lateralTangentX[column];
            if (localOutwardX * cardinalOutwardX + localOutwardZ * cardinalOutwardZ < 0)
            {
                localOutwardX *= -1;
                localOutwardZ *= -1;
            }
            if (Math.hypot(lateralTopologyOutwardX[column], lateralTopologyOutwardZ[column]) > 0.5)
            {
                localOutwardX = lateralTopologyOutwardX[column];
                localOutwardZ = lateralTopologyOutwardZ[column];
            }
            lateralOutwardX[column] = localOutwardX;
            lateralOutwardZ[column] = localOutwardZ;
        }
        double acrossStart = lateralAcross[0];
        double acrossEnd = lateralAcross[lateralAcross.Count - 1];
        double halfWidth = Math.max(0.01, crestSpan * 0.5);
        double acrossCenter = (acrossStart + acrossEnd) * 0.5;
        double topY = topLevel * ELEV;
        // The shared plan owns every physical receiving datum. A broad Chasm fall may cross several materialised
        // floor terraces, so its bottom is a lateral field rather than one run-wide number; shared corners already
        // use the lowest participating receiver. The scalar minimum is only the run's extent/visibility bound.
        double bottomY = Math.min(lateralBottomY.ToArray());
        // The one restrained batch-side impact wisp sits at the same centre column used for its XZ anchor. An
        // average of unlike terrace heights is not a physical floor and visibly floats between them.
        double impactBottomY = lateralBottomY[(int)Math.round(lateralSegments * 0.5)];
        if (topY - bottomY < ELEV * 0.55) return;
        // Chasm water uses the same full-cell carrier as an ordinary authored water stair. It starts on the source
        // block's exact topological edge, bends through the same eased descent and occupies the complete receiving
        // cell instead of turning into a detached vertical card. The lower datum is deeper, but the visual grammar
        // and the continuous printed surface are deliberately identical.
        bool rearFacing = waterfall.direction == "n";
        // A Chasm portal has no backing wall, so its physical sheet remains on the owned boundary plane. The
        // animated normal/fold field still turns over the lip; only a pool-to-pool rapid travels sideways.
        double landingOffset =
            waterfall.landing == "chasm" ? ts * WaterfallVisual.WATERFALL_CHASM_CREST_ROLL_TILES : ts;
        int sourceColor = mix(
            sourceMaterial.mid ?? sourceMaterial.top,
            sourceMaterial.shallow ?? sourceMaterial.topLight,
            0.15);
        int targetColor =
            waterfall.landing == "chasm"
                ? mix(sourceMaterial.deep ?? sourceMaterial.topDark, sourceColor, 0.36)
                : mix(
                    targetMaterial.mid ?? targetMaterial.top,
                    targetMaterial.shallow ?? targetMaterial.topLight,
                    0.15);
        double sourceDepth = partC4ItemAt(plan.water, source.id)?.depth ?? 0.4;
        double targetDepth =
            waterfall.landing == "water" ? (partC4ItemAt(plan.water, target.id)?.depth ?? sourceDepth) : sourceDepth;
        double fallingDepth = -(clamp(drop / 8, 0.55, 1) + (waterfall.landing == "chasm" ? 1.1 : 0));
        // A tangent S-curve owns the complete fall; projection-critical directions bow without moving either join.
        // `axis < 0` (an east/west fall) used to force the hard eased bow here for the same dead reason the wall
        // fold existed: a side-on sheet was mathematically edge-on, so it was deformed until it had visible area.
        // The world yaw gives it real width, and the deformation is now simply a bend the player can see.
        bool bowsProjectionCriticalRapid = rearFacing;
        var lateralTopY = new double[lateralSegments + 1];
        var lateralTopSlope = new double[lateralSegments + 1];
        for (int column = 0; column <= lateralSegments; column++)
        {
            double gridAlong = lateralGridAlong[column];
            lateralTopY[column] =
                visualWaterLevelAt(
                    terrain,
                    axis > 0 ? gridAlong : edgeGridX,
                    axis > 0 ? edgeGridY : gridAlong,
                    topLevel) * ELEV;
        }
        for (int column = 0; column <= lateralSegments; column++)
        {
            int low = Math.max(0, column - 1);
            int high = Math.min(lateralSegments, column + 1);
            double worldDistance = Math.max(
                0.001,
                Math.hypot(
                    lateralWorldX[high] - lateralWorldX[low],
                    lateralWorldZ[high] - lateralWorldZ[low]));
            lateralTopSlope[column] = (lateralTopY[high] - lateralTopY[low]) / worldDistance;
        }
        // Unfold the curved sheet around its crest in real arc-length space. The zero row is therefore exactly the
        // source pool's world XZ coordinate, while every lower row advances by the distance actually travelled over
        // the fall geometry. This is the geometric equivalent of bending one printed water sheet over the edge:
        // there is no second vertical UV projection that can restart, stretch or rotate the cartoon surface.
        int foldStride = lateralSegments + 1;
        var foldDistance = new float[WATERFALL_CURVE_ROWS.Count * foldStride];
        for (int column = 0; column <= lateralSegments; column++)
        {
            double columnTopY = lateralTopY[column];
            double columnBottomY = lateralBottomY[column];
            double previousY = columnTopY;
            double previousOutward = 0;
            double accumulated = 0;
            for (int rowIndex = 1; rowIndex < WATERFALL_CURVE_ROWS.Count; rowIndex++)
            {
                double progress = WATERFALL_CURVE_ROWS[rowIndex];
                double y = columnTopY + (columnBottomY - columnTopY) * waterfallDescentCurve(progress);
                double outward =
                    landingOffset * waterfallLandingCurve(progress, bowsProjectionCriticalRapid);
                accumulated += Math.hypot(y - previousY, outward - previousOutward);
                foldDistance[rowIndex * foldStride + column] = (float)accumulated;
                previousY = y;
                previousOutward = outward;
            }
        }
        if (waterfall.landing == "chasm")
        {
            int fixedGridCoordinate =
                axis > 0
                    ? source.y + (waterfall.direction == "s" ? 1 : 0)
                    : source.x + (waterfall.direction == "e" ? 1 : 0);
            double fixedWorldCoordinate = sheetCoordinate;
            void registerBoundary(bool atEnd)
            {
                int foldColumn = atEnd ? lateralSegments : 0;
                int alongGridCoordinate = atEnd ? runEnd + 1 : runStart;
                double alongWorldCoordinate = lateralAcross[foldColumn];
                double crestLipCoordinate = lateralLip[foldColumn];
                double cornerX =
                    axis > 0 ? frame.originX + (frame.i0 + alongGridCoordinate) * ts : fixedWorldCoordinate;
                double cornerZ =
                    axis > 0 ? fixedWorldCoordinate : frame.originY + (frame.j0 + alongGridCoordinate) * ts;
                int terminalSideX = axis > 0 ? (atEnd ? 1 : -1) : 0;
                int terminalSideZ = axis > 0 ? 0 : atEnd ? 1 : -1;
                int terminalReceiverGridX =
                    (axis > 0 ? alongGridCoordinate : fixedGridCoordinate) +
                    (cardinalOutwardX + terminalSideX < 0 ? -1 : 0);
                int terminalReceiverGridY =
                    (axis > 0 ? fixedGridCoordinate : alongGridCoordinate) +
                    (cardinalOutwardZ + terminalSideZ < 0 ? -1 : 0);
                TerrainCell? terminalReceiver = terrainCellAt(
                    terrain,
                    terminalReceiverGridX,
                    terminalReceiverGridY);
                bool terminalUsesWater =
                    terminalReceiver != null &&
                    (terrainCellCarriesChasmFloor(terminalReceiver) ||
                        terrainCellCarriesWater(terminalReceiver));
                string terminalWallDirection =
                    terminalSideX > 0 ? "w" : terminalSideX < 0 ? "e" : terminalSideZ > 0 ? "n" : "s";
                TerrainEdge? terminalWallEdge = terminalReceiver?.edges[terminalWallDirection];
                bool terminalUsesCanonicalWall =
                    terminalReceiver != null &&
                    !terminalUsesWater &&
                    (terminalReceiver.type == TileType.Floor || terminalReceiver.type == TileType.Solid) &&
                    terminalWallEdge?.visibleFace == true &&
                    terminalWallEdge.faceSegments.some(segment => segment.role == "chasm");
                bool terminalRock = terminalReceiver?.type == TileType.Solid;
                TerrainMaterial terminalMaterial = terminalReceiver != null
                    ? (partC4ItemAt(plan.materials, terminalReceiver.id) ?? TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.floorCool)
                    : TerrainRenderPlanModule.STANDARD_TERRAIN_MATERIALS.floorCool;
                boundaries.push(
                    new WaterfallCurtainBoundary
                    {
                        cornerGridX = axis > 0 ? alongGridCoordinate : fixedGridCoordinate,
                        cornerGridY = axis > 0 ? fixedGridCoordinate : alongGridCoordinate,
                        cornerX = cornerX,
                        cornerZ = cornerZ,
                        crestX = axis > 0 ? alongWorldCoordinate : crestLipCoordinate,
                        crestZ = axis > 0 ? crestLipCoordinate : alongWorldCoordinate,
                        floorX = cornerX,
                        floorZ = cornerZ,
                        direction = waterfall.direction,
                        topY = lateralTopY[foldColumn],
                        bottomY = lateralBottomY[foldColumn],
                        landingOffset = landingOffset,
                        projectionCritical = bowsProjectionCriticalRapid,
                        sourceColor = sourceColor,
                        targetColor = targetColor,
                        sourceDepth = sourceDepth,
                        targetDepth = targetDepth,
                        fallingDepth = fallingDepth,
                        foldDistance = foldDistance,
                        foldStride = foldStride,
                        foldColumn = foldColumn,
                        curtainAcrossCenter = acrossCenter,
                        curtainHalfArc = halfWidth,
                        outwardX = lateralOutwardX[foldColumn],
                        outwardZ = lateralOutwardZ[foldColumn],
                        terminalSideX = terminalSideX,
                        terminalSideZ = terminalSideZ,
                        terminalReceiverGridX = terminalReceiverGridX,
                        terminalReceiverGridY = terminalReceiverGridY,
                        terminalUsesWater = terminalUsesWater,
                        terminalUsesCanonicalWall = terminalUsesCanonicalWall,
                        terminalColor = TerrainRenderPlanModule.terrainFaceBaseColor(terminalMaterial, terminalRock ? "rock" : "earth"),
                        terminalKind = terminalRock ? SURF.rockFace : SURF.earthFace,
                        terminalStrength = 0.17,
                    });
            }
            registerBoundary(false);
            registerBoundary(true);
        }
        void writeWaterfallNormal(
            int index,
            int column,
            double acrossSlope,
            double verticalTangent,
            double outwardTangent)
        {
            double[] normalX4 = WATERFALL_NORMAL_X4;
            double[] normalY4 = WATERFALL_NORMAL_Y4;
            double[] normalZ4 = WATERFALL_NORMAL_Z4;
            normalX4[index] =
                -lateralOutwardX[column] * verticalTangent -
                lateralTangentX[column] * acrossSlope * outwardTangent;
            normalY4[index] = outwardTangent;
            normalZ4[index] =
                -lateralOutwardZ[column] * verticalTangent -
                lateralTangentZ[column] * acrossSlope * outwardTangent;
            // Both easing derivatives are exactly zero at a welded join. The geometric normal is undefined at that
            // single parameter row, so inherit the horizontal Water/floor join's world-up limit instead of emitting
            // a zero vector that the GPU cannot light deterministically.
            if (
                normalX4[index] == 0 &&
                normalY4[index] == 0 &&
                normalZ4[index] == 0)
                normalY4[index] = 1;
        }
        double[] color4 = WATERFALL_COLOR4;
        double[] depth4 = WATERFALL_DEPTH4;
        double[] foldX4 = WATERFALL_FOLD_X4;
        double[] foldZ4 = WATERFALL_FOLD_Z4;
        double[] crest4 = WATERFALL_CREST4;
        double[] reflection4 = WATERFALL_REFLECTION4;
        for (int row = 0; row < WATERFALL_CURVE_ROWS.Count - 1; row++)
        {
            double progressA = WATERFALL_CURVE_ROWS[row];
            double progressB = WATERFALL_CURVE_ROWS[row + 1];
            double descentA = waterfallDescentCurve(progressA);
            double descentB = waterfallDescentCurve(progressB);
            double outwardTravelA =
                landingOffset * waterfallLandingCurve(progressA, bowsProjectionCriticalRapid);
            double outwardTravelB =
                landingOffset * waterfallLandingCurve(progressB, bowsProjectionCriticalRapid);
            double outwardTangentA =
                landingOffset * waterfallLandingTangent(progressA, bowsProjectionCriticalRapid);
            double outwardTangentB =
                landingOffset * waterfallLandingTangent(progressB, bowsProjectionCriticalRapid);
            color4[0] = mix(sourceColor, targetColor, descentA);
            color4[1] = color4[0];
            color4[2] = mix(sourceColor, targetColor, descentB);
            color4[3] = color4[2];
            // PORT NOTE: `mix` is the packed-RGB palette blend (ToInt32 on both depths), exactly as in the original.
            depth4[0] = mix(sourceDepth, targetDepth, descentA);
            depth4[1] = depth4[0];
            depth4[2] = mix(sourceDepth, targetDepth, descentB);
            depth4[3] = depth4[2];
            for (int column = 0; column < lateralSegments; column++)
            {
                double topYA = lateralTopY[column];
                double topYB = lateralTopY[column + 1];
                double bottomYA = lateralBottomY[column];
                double bottomYB = lateralBottomY[column + 1];
                double yAa = topYA + (bottomYA - topYA) * descentA;
                double yAb = topYB + (bottomYB - topYB) * descentA;
                double yBa = topYA + (bottomYA - topYA) * descentB;
                double yBb = topYB + (bottomYB - topYB) * descentB;
                double crestXA = lateralWorldX[column];
                double crestXB = lateralWorldX[column + 1];
                double crestZA = lateralWorldZ[column];
                double crestZB = lateralWorldZ[column + 1];
                double outwardXA = lateralOutwardX[column];
                double outwardXB = lateralOutwardX[column + 1];
                double outwardZA = lateralOutwardZ[column];
                double outwardZB = lateralOutwardZ[column + 1];
                double floorXA = lateralFloorX[column];
                double floorXB = lateralFloorX[column + 1];
                double floorZA = lateralFloorZ[column];
                double floorZB = lateralFloorZ[column + 1];
                double xAa = crestXA + (floorXA - crestXA) * descentA + outwardXA * outwardTravelA;
                double xAb = crestXB + (floorXB - crestXB) * descentA + outwardXB * outwardTravelA;
                double xBa = crestXA + (floorXA - crestXA) * descentB + outwardXA * outwardTravelB;
                double xBb = crestXB + (floorXB - crestXB) * descentB + outwardXB * outwardTravelB;
                double zAa = crestZA + (floorZA - crestZA) * descentA + outwardZA * outwardTravelA;
                double zAb = crestZB + (floorZB - crestZB) * descentA + outwardZB * outwardTravelA;
                double zBa = crestZA + (floorZA - crestZA) * descentB + outwardZA * outwardTravelB;
                double zBb = crestZB + (floorZB - crestZB) * descentB + outwardZB * outwardTravelB;
                double foldAa = foldDistance[row * foldStride + column];
                double foldAb = foldDistance[row * foldStride + column + 1];
                double foldBb = foldDistance[(row + 1) * foldStride + column + 1];
                double foldBa = foldDistance[(row + 1) * foldStride + column];
                foldX4[0] = crestXA + outwardXA * foldAa;
                foldX4[1] = crestXB + outwardXB * foldAb;
                foldX4[2] = crestXB + outwardXB * foldBb;
                foldX4[3] = crestXA + outwardXA * foldBa;
                foldZ4[0] = crestZA + outwardZA * foldAa;
                foldZ4[1] = crestZB + outwardZB * foldAb;
                foldZ4[2] = crestZB + outwardZB * foldBb;
                foldZ4[3] = crestZA + outwardZA * foldBa;
                P3[] sheet = quadInto(
                    WATERFALL_SHEET,
                    xAa,
                    yAa,
                    zAa,
                    xAb,
                    yAb,
                    zAb,
                    xBb,
                    yBb,
                    zBb,
                    xBa,
                    yBa,
                    zBa);
                crest4[0] = topYA;
                crest4[1] = topYB;
                crest4[2] = topYB;
                crest4[3] = topYA;
                double slopeA0 = lateralTopSlope[column] * (1 - descentA);
                double slopeB0 = lateralTopSlope[column + 1] * (1 - descentA);
                double slopeB1 = lateralTopSlope[column + 1] * (1 - descentB);
                double slopeA1 = lateralTopSlope[column] * (1 - descentB);
                double verticalA0 = Math.abs((bottomYA - topYA) * waterfallDescentTangent(progressA));
                double verticalB0 = Math.abs((bottomYB - topYB) * waterfallDescentTangent(progressA));
                double verticalB1 = Math.abs((bottomYB - topYB) * waterfallDescentTangent(progressB));
                double verticalA1 = Math.abs((bottomYA - topYA) * waterfallDescentTangent(progressB));
                // Pool-to-pool rapids and Chasm descents share one rounded water-stair normal frame. The tangent is
                // world-up at both joins and turns continuously through the descent, so lighting, surface relief and
                // the geometric bend all describe the same volume instead of exposing a rigid vertical card.
                writeWaterfallNormal(0, column, slopeA0, verticalA0, outwardTangentA);
                writeWaterfallNormal(1, column + 1, slopeB0, verticalB0, outwardTangentA);
                writeWaterfallNormal(2, column + 1, slopeB1, verticalB1, outwardTangentB);
                writeWaterfallNormal(3, column, slopeA1, verticalA1, outwardTangentB);
                if (waterfall.landing == "water")
                {
                    // This is one continuous lake/river surface, not a waterfall decal. Positive depth keeps it on the
                    // ordinary world-anchored water field and zero shore payload prevents a full-width foam ruler. The
                    // shared world-up lighting frame is intentional: turning the MeshStandard normal across the carrier
                    // produced a dark static ruler even though pigment and geometry were continuous. Animated water relief
                    // still describes the flow, while both pool joins now shade identically. Reflection hints are sampled
                    // at the real curved vertices so neither endpoint changes material.
                    setShade4(0, 0, 0, 0);
                    for (int vertex = 0; vertex < sheet.Length; vertex++)
                    {
                        P3 point = sheet[vertex];
                        reflection4[vertex] = TerrainBakePigment.terrainWaterReflectionHintAt(
                            terrain,
                            plan,
                            (point.x - frame.originX) / ts - frame.i0,
                            (point.z - frame.originY) / ts - frame.j0,
                            point.y / ELEV);
                    }
                    builder.addWaterFace(
                        sheet,
                        0,
                        1,
                        0,
                        color4,
                        SHADE4,
                        depth4,
                        ts,
                        0,
                        false,
                        false,
                        reflection4,
                        false,
                        0,
                        0,
                        0,
                        foldX4,
                        foldZ4);
                }
                else
                {
                    double responseA = descentA * 0.98;
                    double responseB = descentB * 0.98;
                    setShade4(responseA, responseA, responseB, responseB);
                    builder.addWaterFace(
                        sheet,
                        WATERFALL_NORMAL_X4,
                        WATERFALL_NORMAL_Y4,
                        WATERFALL_NORMAL_Z4,
                        color4,
                        SHADE4,
                        fallingDepth,
                        axis,
                        crest4,
                        false,
                        false,
                        depth4,
                        false,
                        0,
                        acrossCenter,
                        halfWidth,
                        foldX4,
                        foldZ4,
                        // A curtain hangs unbacked inside the shaft. East/west ones stand nearly edge-on under the world
                        // yaw, where their crest-up shading normals decide the winding at random — half of them came out
                        // inside-out and were culled, opening the very shaft the sheet exists to close.
                        true);
                }
            }
        }

        // An ordinary rapid remains one opaque liquid surface. A translucent full-width impact card reintroduced
        // exactly the horizontal rectangle this path removes; mist is reserved for a genuine Chasm curtain.
        if (waterfall.landing == "water") return;
        // A real Bridge-to-Chasm liquid sheet may remain visible below the open span, but the bridge itself must
        // never emit a translucent impact cloud. The separate 3D waterfall-smoke path applies the same rule.
        if (touchesBridge) return;
        // The terrain batch contributes only a restrained depth wisp. The actual impact cloud belongs to the shared
        // Render3D volumetric pool; duplicating it here turned broad Hub falls into huge bright transparent polygons.
        double impactHeight = Math.min(ELEV * 1.02, Math.max(ELEV * 0.4, (topY - bottomY) * 0.15));
        int impactColor = mix(
            this.chasmPaletteForCell(source)?.mist ?? targetColor,
            targetColor,
            0.24);
        double impactX = 0;
        double impactZ = 0;
        for (int column = 0; column <= lateralSegments; column++)
        {
            impactX += lateralWorldX[column] + lateralOutwardX[column] * (landingOffset + 1.4);
            impactZ += lateralWorldZ[column] + lateralOutwardZ[column] * (landingOffset + 1.4);
        }
        impactX /= lateralSegments + 1;
        impactZ /= lateralSegments + 1;
        builder.addMistWisp(
            impactX,
            impactBottomY + impactHeight * 0.36,
            impactZ,
            halfWidth * 0.9,
            impactHeight,
            impactColor,
            0.085,
            waterfall.phase,
            ts * 0.035,
            source.id * 0.031 + 0.17,
            2,
            0.7);
    }
}
