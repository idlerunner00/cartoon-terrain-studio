// Port of packages/client/src/render/environment/floorGrassGeometry.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `const SURF = TERRAIN_SURFACE_PATTERN` and `const COVER = CARTOON_TERRAIN_STYLE.groundCover` are module
//   aliases; the port reads the original qualified names.
// * The written scratch (SHADE4, WIND4, QUAD) is [ThreadStatic]: Godot compiles on several threads of one process.
// * The cell/site hash seeds (`siteX * 92837111 + …`) exceed int32, so every hash argument is built in double
//   exactly like the JS and only truncated inside cellHash (ToInt32).
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class FloorGrassPalette
{
    /// <summary>Saturated growth pole — the same one the mat and the shader use.</summary>
    public int lush;
    /// <summary>The floor's own lit tone; blades borrow it so growth shares the terrain's light.</summary>
    public int lit;
}

public sealed class FloorGrassOptions
{
    public FloorGrassPalette palette;
    /// <summary>World position of the cell's u=0, v=0 corner, in world px.</summary>
    public double originX;
    public double originZ;
    public double tileSize;
    /// <summary>Cell surface height in world px.</summary>
    public double y0;
    /// <summary>Absolute world cell coordinates. The hash domain — this is why tufts survive chunk seams.</summary>
    public int cellX;
    public int cellY;
    /// <summary>Total blades this cell may spend.</summary>
    public double bladeBudget;
    /// <summary>
    /// The turf COVER at a point in the cell — the identical number the cap publishes to the shader at that
    /// point. A blade may only exist where the ground is already painted as grass.
    /// </summary>
    public Func<double, double, double> coverAt;
    /// <summary>Growth-direction field in radians.</summary>
    public Func<double, double, double> directionAt;
    /// <summary>
    /// The pigment the ground actually shows at this point: the cap's own colour with the turf mix already
    /// applied. Blades root in this, so a blade's base is the mat it grows out of.
    /// </summary>
    public Func<double, double, int> matPigmentAt;
    /// <summary>Extra organic ground lift at a point in the cell, in world px.</summary>
    public Func<double, double, double> groundLift;
}

public sealed class WindBladeParams
{
    public double rootX;
    public double rootZ;
    public double y0;
    public double height;
    public double width;
    public double angle;
    public double leanX;
    public double leanZ;
    public int color;
    public double wind;
    public double? bend;
    /// <summary>
    /// Upward share of the LIGHTING normal, 0 (a wall) .. 1 (flat ground). Foliage wants most of it.
    /// See <see cref="FloorGrassGeometry.addTerrainWindBlade"/> for why this is the single most important number on a blade.
    /// </summary>
    public double? normalY;
}

/// <summary>
/// The BLADES of the floor's ground cover — and nothing else.
///
/// ## The patch is not here any more
///
/// A grass patch used to be a mesh: a low turf surface ("the sward") welded across cell seams, jittered into an
/// irregular outline, tinted a whisper away from the cap beneath it, with a fringe of blades hiding its edge.
/// It was built with care and it still read as a second material laid on the world, because that is what it
/// was. Nine quads of separate polygon per cell can be matched to the ground; it cannot BE the ground.
///
/// A path in this world was always solved the other way round — as pigment folded into the cap
/// (`livingGroundPigmentAt`) — and therefore never had an edge to hide. The patch now works the same way:
/// terrainFloorTurf resolves one turf COVER from the habitat body and the floor's own material
/// mottle, the cap publishes it per vertex, and the fragment shader paints it as part of the ground it already
/// paints. No surface, no outline, no seam, and one fewer thing that can disagree with the floor.
///
/// What is left for geometry is the part pigment genuinely cannot say: blades standing up out of the mat.
///
/// ## What this module still owns
///
///  - **the understory** — cheap, wide tussocks on a world-space lattice, the layer that closes the gaps;
///  - **the crowns** — the authored tuft grammar (bent, crossed, phyllotactic), spent on a few strong tufts;
///  - **seed heads** — the meadow's tall punctuation, rare on purpose.
///
/// Every one of them samples the SAME cover the ground is painted with, at its own position, so a tuft can only
/// stand where the world is green — and takes its colour from the mat's pigment at its own root, lifted toward
/// the light. Growth is lighter than the ground it grows out of; that is what makes it read as on top.
/// </summary>
public static partial class FloorGrassGeometry
{
    /// <summary>
    /// How much lighter a blade is than the mat it grows out of.
    ///
    /// The retired pigment pulled a blade's base toward the DRY SOIL pole and gave every fourth blade an
    /// ink-darkened variant, so tufts came out darker and greyer than the ground — which is precisely how a piece
    /// of scenery announces that it does not belong to the surface it stands on. Growth catches the light; it is
    /// the brightest thing on an open floor, not the darkest.
    /// </summary>
    private const double BLADE_LIFT = 0.27;
    /// <summary>Value spread between the blades of one tuft. Small: a tuft is one plant, not a colour chart.</summary>
    private const double BLADE_SHADE_SPREAD = 0.16;

    [ThreadStatic] private static double[]? _SHADE4;
    private static double[] SHADE4 => _SHADE4 ??= new double[] { 1, 1, 1, 1 };
    [ThreadStatic] private static double[]? _WIND4;
    private static double[] WIND4 => _WIND4 ??= new double[] { 0, 0, 0, 0 };
    [ThreadStatic] private static P3[]? _QUAD;
    private static P3[] QUAD => _QUAD ??= new P3[]
    {
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
    };

    private static void setShade4(double a, double b, double c, double d)
    {
        double[] shade4 = SHADE4;
        shade4[0] = a;
        shade4[1] = b;
        shade4[2] = c;
        shade4[3] = d;
    }

    private static void setWind4(double a, double b, double c, double d)
    {
        double[] wind4 = WIND4;
        wind4[0] = a;
        wind4[1] = b;
        wind4[2] = c;
        wind4[3] = d;
    }

    /// <summary>
    /// One tapered, lit blade in the shared terrain batch — the single implementation used by floor grass, reed
    /// beds and every thematic tendril. Root vertices always stay fixed; `bend` buys the two-ribbon silhouette that
    /// hero grass needs, and omitting it keeps the cheap one-quad form for dense understory.
    ///
    /// ## Why blades used to come out grey
    ///
    /// A blade's face normal was assembled as `(-sideZ, normalY, sideX)` and normalised by the builder. `side` is
    /// the blade's half-width in WORLD PX — around three — while `normalY` was a fraction of one, so the resulting
    /// normal was better than 95 % horizontal: every blade in the world was being lit as a vertical wall face. Under
    /// a high key that is the darkest orientation there is, which is why tufts read as grey-green cutouts sitting on
    /// a bright floor no matter what pigment they were given. Grass does not look like that, because a real blade is
    /// thin, translucent and surrounded by others; it gathers light from above far more than a wall does.
    ///
    /// So the normal is now built in the UNIT frame and blended toward straight up by `normalY`. The blade keeps a
    /// facing component — that is what still gives a tuft form and lets one leaf read against the next — but it is
    /// lit as foliage. Every consumer of this function (floor grass, reed beds, thematic tendrils) inherits the fix,
    /// which is the whole reason there is only one implementation.
    ///
    /// The root end is likewise only slightly darker than the tip. A steep root-to-tip ramp (the old 0.74) reads as
    /// a shadow painted onto the blade and drags the whole tuft below the value of the ground behind it.
    /// </summary>
    public static void addTerrainWindBlade(PropGeometryBuilder builder, WindBladeParams @params)
    {
        double rootX = @params.rootX;
        double rootZ = @params.rootZ;
        double y0 = @params.y0;
        double height = @params.height;
        double width = @params.width;
        double angle = @params.angle;
        double leanX = @params.leanX;
        double leanZ = @params.leanZ;
        int color = @params.color;
        double wind = @params.wind;
        double bend = @params.bend ?? 0;
        double up = clamp(@params.normalY ?? 0.38, 0, 1);
        // Fluitown comic look (not in the original): a dark root that sinks into the floor, a light tip.
        bool comic = FluitownSoftFoliage.Enabled;
        double facing = 1 - up;
        double sideX = Math.cos(angle) * width;
        double sideZ = Math.sin(angle) * width;
        // Unit face direction, independent of how wide the blade happens to be.
        double faceX = -Math.sin(angle) * facing;
        double faceZ = Math.cos(angle) * facing;
        double tipX = rootX + leanX;
        double tipZ = rootZ + leanZ;
        P3[] q = QUAD;
        if (bend > 0.01)
        {
            double midT = clamp(bend, 0.42, 0.72);
            double midLean = midT * 0.42;
            double midX = rootX + leanX * midLean;
            double midZ = rootZ + leanZ * midLean;
            double midY = y0 + height * midT;
            if (comic) setShade4(0.76, 0.76, 0.94, 0.94); else setShade4(0.9, 0.94, 1, 0.98);
            setWind4(0, 0, wind * 0.42, wind * 0.42);
            q[0].x = rootX - sideX;
            q[0].y = y0;
            q[0].z = rootZ - sideZ;
            q[1].x = rootX + sideX;
            q[1].y = y0;
            q[1].z = rootZ + sideZ;
            q[2].x = midX + sideX * 0.48;
            q[2].y = midY;
            q[2].z = midZ + sideZ * 0.48;
            q[3].x = midX - sideX * 0.48;
            q[3].y = midY;
            q[3].z = midZ - sideZ * 0.48;
            builder.addSurface(q, faceX, up, faceZ, color, TERRAIN_SURFACE_PATTERN.floor, 0.04, SHADE4, WIND4);
            if (comic) setShade4(0.94, 0.94, 1.16, 1.16); else setShade4(1, 0.98, 1.08, 1.05);
            setWind4(wind * 0.42, wind * 0.42, wind, wind);
            q[0].x = midX - sideX * 0.48;
            q[0].y = midY;
            q[0].z = midZ - sideZ * 0.48;
            q[1].x = midX + sideX * 0.48;
            q[1].y = midY;
            q[1].z = midZ + sideZ * 0.48;
            q[2].x = tipX + sideX * CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeTipWidth;
            q[2].y = y0 + height;
            q[2].z = tipZ + sideZ * CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeTipWidth;
            q[3].x = tipX - sideX * CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeTipWidth;
            q[3].y = y0 + height;
            q[3].z = tipZ - sideZ * CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeTipWidth;
            // The upper ribbon leans away from the root, so it turns a little further toward the sky.
            builder.addSurface(
                q,
                faceX * 0.88,
                Math.min(1, up * 1.12),
                faceZ * 0.88,
                color,
                TERRAIN_SURFACE_PATTERN.floor,
                0.04,
                SHADE4,
                WIND4);
            return;
        }
        if (comic) setShade4(0.76, 0.76, 1.14, 1.14); else setShade4(0.92, 0.95, 1.06, 1.03);
        setWind4(0, 0, wind, wind);
        q[0].x = rootX - sideX;
        q[0].y = y0;
        q[0].z = rootZ - sideZ;
        q[1].x = rootX + sideX;
        q[1].y = y0;
        q[1].z = rootZ + sideZ;
        q[2].x = tipX + sideX * 0.1;
        q[2].y = y0 + height;
        q[2].z = tipZ + sideZ * 0.1;
        q[3].x = tipX - sideX * 0.1;
        q[3].y = y0 + height;
        q[3].z = tipZ - sideZ * 0.1;
        builder.addSurface(q, faceX, up, faceZ, color, TERRAIN_SURFACE_PATTERN.floor, 0.04, SHADE4, WIND4);
    }

    private sealed class BladePigment
    {
        public int mid;
        public int light;
        public int deep;
    }

    /// <summary>
    /// A blade's colour: the MAT it grows out of, lifted toward the light.
    ///
    /// Rooting in the mat is what ties growth to the ground — two points of a meadow with different ground grow
    /// visibly different grass, exactly as they should. Lifting it is what puts the growth on TOP: the blade is a
    /// brighter, greener version of the same pigment, never a foreign green and never an ink-darkened silhouette.
    /// </summary>
    private static BladePigment bladePigment(FloorGrassOptions options, double cover, double u, double v)
    {
        FloorGrassPalette palette = options.palette;
        int mat = options.matPigmentAt(u, v);
        // Toward the growth pole FIRST, then toward the light. Lifting a blade straight at the floor's lit tone
        // bleaches it: the theme's `floorLit` is a very pale wash, and grass that has lost its green reads as
        // straw lying on the ground rather than as living cover.
        //
        // The lift grows a little with cover: dense turf carries taller, fresher growth than a thin shoulder does.
        int mid = mix(mix(mat, palette.lush, 0.45), palette.lit, BLADE_LIFT * (0.72 + cover * 0.4));
        // Fluitown comic look (not in the original): under the comic ramp a bleached blade reads as a pale spike on the
        // floor; the growth stays a deeper, richer tone of its mat and gets its light from the blade gradient instead.
        if (FluitownSoftFoliage.Enabled)
            mid = FluitownSoftFoliage.saturate(mix(mix(mat, palette.lush, 0.55), palette.lit, 0.1), 1.12);
        return new BladePigment
        {
            mid = mid,
            light = mix(mid, palette.lit, BLADE_SHADE_SPREAD * 1.4),
            deep = mix(mid, mat, BLADE_SHADE_SPREAD),
        };
    }

    /// <summary>
    /// One crown inside the cover: broad bent leaves overlapping around a real shared root, fanned on the golden
    /// angle so no two blades cross the same way twice. Returns the blades actually spent.
    /// </summary>
    private static double addCrown(
        PropGeometryBuilder builder,
        FloorGrassOptions options,
        double rootX,
        double rootZ,
        double y0,
        double u,
        double v,
        double cover,
        double seed,
        double bladeCount,
        double windAngle,
        double windNoise,
        double scale)
    {
        double ts = options.tileSize;
        BladePigment pigment = bladePigment(options, cover, u, v);
        double skirtCount = Math.max(2, Math.round(bladeCount * 0.42));
        double crownPhase = cellHash(seed * 13 + 7, seed * 29 - 3) * Math.PI * 2;
        for (int blade = 0; blade < bladeCount; blade++)
        {
            double roll = cellHash(seed * 31 + blade * 73 + 11, seed * 17 - blade * 51 - 29);
            double rollB = cellHash(seed * 47 - blade * 19 + 5, seed * 61 + blade * 37 - 13);
            bool skirt = blade < skirtCount;
            double radialAngle = crownPhase + blade * 2.399963 + (roll - 0.5) * 0.42;
            double rootRadius = ts * (skirt ? 0.028 + rollB * 0.028 : rollB * 0.018) * scale;
            double bladeRootX = rootX + Math.cos(radialAngle) * rootRadius;
            double bladeRootZ = rootZ + Math.sin(radialAngle) * rootRadius;
            // Aspect matters more than either dimension on its own. A leaf that is nearly as wide as it is tall reads
            // as foliage lying on the ground; grass is a narrow, upright stroke, and that silhouette is most of what
            // says "meadow" at gameplay zoom.
            double height =
                ts * scale * (skirt ? 0.2 + cover * 0.085 + roll * 0.06 : 0.31 + cover * 0.13 + roll * 0.085);
            double width =
                (skirt ? 2.25 + cover * 0.7 + rollB * 0.55 : 1.95 + cover * 0.62 + rollB * 0.5) * scale;
            double outwardWeight = skirt ? 0.84 : 0.68;
            double growthAngle =
                Math.atan2(
                    Math.sin(radialAngle) * outwardWeight + Math.sin(windAngle) * (1 - outwardWeight),
                    Math.cos(radialAngle) * outwardWeight + Math.cos(windAngle) * (1 - outwardWeight)) +
                (rollB - 0.5) * 0.22;
            double leanDistance = height * (skirt ? 0.35 + roll * 0.15 : 0.24 + roll * 0.12);
            int color = blade % 3 == 0 ? pigment.light : blade % 4 == 0 ? pigment.deep : pigment.mid;
            double wind = 1.55 + windNoise * 1.25 + cover * 0.4 + roll * 0.28;
            addTerrainWindBlade(builder, new WindBladeParams
            {
                rootX = bladeRootX,
                rootZ = bladeRootZ,
                y0 = y0 + (blade % 3) * 0.012,
                height = height,
                width = width,
                angle = growthAngle + Math.PI * 0.5,
                leanX = Math.cos(growthAngle) * leanDistance,
                leanZ = Math.sin(growthAngle) * leanDistance,
                color = color,
                wind = wind,
                bend = CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeBend * (0.78 + rollB * 0.28),
                normalY = 0.78,
            });
            // The radial skirt gets a narrow crossed face. This removes camera-angle disappearances on the leaves that
            // carry the crown's outline, without doubling every blade in a dense colony.
            if (skirt)
            {
                addTerrainWindBlade(builder, new WindBladeParams
                {
                    rootX = bladeRootX,
                    rootZ = bladeRootZ,
                    y0 = y0 + (blade % 3) * 0.012 + 0.006,
                    height = height * 0.92,
                    width = width * 0.52,
                    angle = growthAngle,
                    leanX = Math.cos(growthAngle) * leanDistance * 0.92,
                    leanZ = Math.sin(growthAngle) * leanDistance * 0.92,
                    color = mix(color, pigment.deep, 0.12),
                    wind = wind * 0.96,
                    bend = CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeBend * (0.8 + roll * 0.22),
                    normalY = 0.8,
                });
            }
        }
        return bladeCount;
    }

    /// <summary>A seed head: one tall stem with a small nodding ear. The tallest thing in a meadow, and rare.</summary>
    private static void addSeedHead(
        PropGeometryBuilder builder,
        FloorGrassOptions options,
        double rootX,
        double rootZ,
        double y0,
        double u,
        double v,
        double cover,
        double seed,
        double windAngle)
    {
        double ts = options.tileSize;
        BladePigment pigment = bladePigment(options, cover, u, v);
        double roll = cellHash(seed * 71 + 3, seed * 89 - 17);
        double stem = ts * (0.34 + cover * 0.12 + roll * 0.06);
        double lean = stem * (0.3 + roll * 0.14);
        addTerrainWindBlade(builder, new WindBladeParams
        {
            rootX = rootX,
            rootZ = rootZ,
            y0 = y0,
            height = stem,
            width = 1.5 + roll * 0.5,
            angle = windAngle + Math.PI * 0.5,
            leanX = Math.cos(windAngle) * lean,
            leanZ = Math.sin(windAngle) * lean,
            color = mix(pigment.mid, pigment.light, 0.4),
            wind = 2.6 + roll,
            bend = CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeBend * 1.1,
            normalY = 0.72,
        });
        addTerrainWindBlade(builder, new WindBladeParams
        {
            rootX = rootX + Math.cos(windAngle) * lean * 0.72,
            rootZ = rootZ + Math.sin(windAngle) * lean * 0.72,
            y0 = y0 + stem * 0.66,
            height = stem * 0.34,
            width = 2.9 + roll * 0.9,
            angle = windAngle,
            leanX = Math.cos(windAngle) * stem * 0.2,
            leanZ = Math.sin(windAngle) * stem * 0.2,
            color = mix(pigment.light, options.palette.lit, 0.24),
            wind = 3.1 + roll,
            bend = CartoonTerrainStyle.CARTOON_TERRAIN_STYLE.groundCover.bladeBend * 1.25,
            normalY = 0.8,
        });
    }

    /// <summary>
    /// Opt-in bake diagnostics (the TS `globalThis.__grassAudit`). A world can look bare either because the
    /// emitter is broken or because the shared habitat field is empty, and those are opposite fixes — assign an
    /// empty dictionary before a bake to find out which. Off (null) by default; it costs one read per cell.
    /// </summary>
    public static Dictionary<string, double>? __grassAudit;

    private sealed class GrassSite
    {
        public double u;
        public double v;
        public double cover;
        public double seed;
        public bool crown;
        public bool seedHead;
    }

    /// <summary>
    /// Emit the standing growth of ONE floor cell.
    ///
    /// There is deliberately no "does this cell get grass" decision: cover is sampled from the continuous field
    /// wherever a tuft is about to be placed, and a point with no cover simply produces nothing. Because that field
    /// is the very one the ground is painted with, a tuft can never end up standing on bare floor.
    /// </summary>
    public static void addFloorGrass(PropGeometryBuilder builder, FloorGrassOptions options)
    {
        // Fluitown comic look (not in the original): the meadow grows in the Godot vegetation layer — see FluitownVegetation.
        if (FluitownVegetation.recordFloorGrass(builder, options)) return;
        double budget = Math.max(0, Math.round(options.bladeBudget));
        if (budget <= 0) return;
        double centre = options.coverAt(0.5, 0.5);
        double corners =
            options.coverAt(0.08, 0.08) +
            options.coverAt(0.92, 0.08) +
            options.coverAt(0.08, 0.92) +
            options.coverAt(0.92, 0.92);
        // Opt-in bake diagnostics. A world can look bare either because the emitter is broken or because the shared
        // habitat field is empty, and those are opposite fixes — set `globalThis.__grassAudit = {}` before a bake to
        // find out which. Off by default, it costs one property read per cell.
        Dictionary<string, double>? audit = __grassAudit;
        if (audit != null)
        {
            lock (audit)
            {
                audit["cells"] = (audit.TryGetValue("cells", out double cells) ? cells : 0) + 1;
                audit["coverSum"] = (audit.TryGetValue("coverSum", out double coverSum) ? coverSum : 0) + centre;
                if (centre > 0) audit["alive"] = (audit.TryGetValue("alive", out double alive) ? alive : 0) + 1;
            }
        }
        if (centre <= 0 && corners <= 0.04) return;

        double windNoise = smoothCellNoise(options.cellX + 0.5, options.cellY + 0.5, 11, 73);
        double windAngle = 0.58 + windNoise * 0.76 + Math.sin(options.directionAt(0.5, 0.5)) * 0.08;

        // The lattice is indexed in ABSOLUTE world sites and a site belongs to the cell containing its index, so
        // tufts never duplicate at a seam. Two decisions here matter more than anything else in this module:
        //
        //  - **Sites, not one hero per cell.** Spending most of a cell's budget on a single crown puts exactly one
        //    clump per cell, and a grid of cells then produces a visible GRID of clumps.
        //  - **Crowns are chosen by a WORLD hash, not per cell.** A hero every N sites lands in its own sparse
        //    pattern that is unrelated to the cell grid, so hero tufts cluster and gap the way real grass does.
        const int lattice = 2;
        var sites = new List<GrassSite>();
        for (int j = 0; j < lattice; j++)
        {
            for (int i = 0; i < lattice; i++)
            {
                double siteX = (double)options.cellX * lattice + i;
                double siteY = (double)options.cellY * lattice + j;
                double jx = cellHash(siteX * 73 + 19, siteY * 61 - 11);
                double jz = cellHash(siteX * 89 - 7, siteY * 97 + 29);
                double u = clamp((i + 0.5 + (jx - 0.5) * 0.92) / lattice, 0, 1);
                double v = clamp((j + 0.5 + (jz - 0.5) * 0.92) / lattice, 0, 1);
                double cover = options.coverAt(u, v);
                if (cover <= 0.04) continue;
                // Density thinning: a site SURVIVES with a probability driven by the cover it stands in. The draw is per
                // absolute WORLD site, so the resulting scatter has no relationship to the cell grid — this is not the
                // retired per-cell colony lottery, which decided whether a whole cell got anything at all.
                if (cellHash(siteX * 211 - 29, siteY * 197 + 43) > 0.18 + cover * 0.9) continue;
                double tier = cellHash(siteX * 307 + 11, siteY * 331 - 19);
                sites.push(new GrassSite
                {
                    u = u,
                    v = v,
                    cover = cover,
                    seed = siteX * 92837111 + siteY * 689287499,
                    crown = tier > 0.66 && cover > 0.3,
                    seedHead = tier > 0.955 && cover > 0.6,
                });
            }
        }
        if (sites.Count == 0) return;

        // Every site gets a real tussock rather than a lone ribbon. A single blade at this scale is a two-pixel
        // sliver — invisible on its own, and the reason a thin "spread" layer bought no coverage at all.
        //
        // The blade count comes from COVER, and the budget is only a ceiling. Dividing the remaining budget by the
        // surviving site count instead made sparse cover as expensive as lush cover: when thinning left a single
        // site, that site inherited the entire cell budget and grew a bush.
        double spent = 0;
        foreach (GrassSite site in sites)
        {
            if (spent >= budget) break;
            double u = clamp(site.u, 0.02, 0.98);
            double v = clamp(site.v, 0.02, 0.98);
            double rootX = options.originX + u * options.tileSize;
            double rootZ = options.originZ + v * options.tileSize;
            // Rooted ON the cap, with no lift of its own: there is no turf surface to stand on any more, and a blade
            // floating above the ground it is painted into is the one way this could still look pasted on.
            double y0 = options.y0 + options.groundLift(u, v);
            double want = site.crown ? Math.round(4 + site.cover * 5) : Math.round(2 + site.cover * 3);
            double blades = Math.min(want, budget - spent);
            if (blades <= 0) break;
            if (site.crown)
            {
                // Crowns are the authored grammar: bent, crossed, phyllotactic. Slightly under full scale so they read
                // as part of the cover rather than as bushes standing on it.
                spent += addCrown(
                    builder,
                    options,
                    rootX,
                    rootZ,
                    y0,
                    u,
                    v,
                    site.cover,
                    site.seed,
                    blades,
                    windAngle,
                    windNoise,
                    0.86);
                // Seed heads are the meadow's tall punctuation and stay rare, so a field never reads as wheat.
                if (site.seedHead && spent + 2 <= budget)
                {
                    addSeedHead(builder, options, rootX, rootZ, y0, u, v, site.cover, site.seed, windAngle);
                    spent += 2;
                }
                continue;
            }
            // Sprig tussocks are short, wide and cheap: one ribbon each, fanned on the golden angle from a shared
            // root. Their job is closing the gaps between crowns, and at gameplay zoom a ribbon does that for a third
            // of a crown blade's cost.
            BladePigment pigment = bladePigment(options, site.cover, u, v);
            for (int b = 0; b < blades; b++)
            {
                double roll = cellHash(site.seed + b * 37, site.seed - b * 53 + 7);
                double rollB = cellHash(site.seed + b * 71 + 3, site.seed - b * 19 - 11);
                double angle = windAngle * 0.35 + b * 2.399963 + (roll - 0.5) * 0.8;
                double spreadRadius = options.tileSize * (0.014 + rollB * 0.05);
                double height = options.tileSize * (0.135 + site.cover * 0.105 + roll * 0.06);
                addTerrainWindBlade(builder, new WindBladeParams
                {
                    rootX = rootX + Math.cos(angle) * spreadRadius,
                    rootZ = rootZ + Math.sin(angle) * spreadRadius,
                    y0 = y0,
                    height = height,
                    width = 2.2 + site.cover * 0.78 + rollB * 0.56,
                    angle = angle + Math.PI * 0.5,
                    leanX = Math.cos(angle) * height * (0.34 + roll * 0.2),
                    leanZ = Math.sin(angle) * height * (0.34 + roll * 0.2),
                    color = b % 3 == 0 ? pigment.light : b % 4 == 0 ? pigment.deep : pigment.mid,
                    wind = 1.4 + windNoise * 1.1 + roll * 0.4,
                    // Single ribbon, deliberately. A tussock of four ribbons reads the same at gameplay zoom as one of
                    // four bent blades and costs half the quads, and the understory is the layer we buy the most of.
                    normalY = 0.74,
                });
                spent++;
                if (spent >= budget) break;
            }
        }
    }
}
