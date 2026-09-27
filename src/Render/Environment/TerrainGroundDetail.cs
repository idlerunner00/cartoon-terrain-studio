// Port of packages/client/src/render/environment/terrainGroundDetail.ts — keep in lockstep with the original.
using Fluitown.Domain;
using static Fluitown.Render.TerrainGeometryCompilerFields;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/*
 * **Ground detail** — the three pigment layers that give a floor a history, extracted from the terrain
 * compiler so it stops growing (the module-size ratchet is a rule, not a suggestion).
 *
 * All three are pure functions of world-continuous fields the bake already has, folded into the existing
 * cap pigment: no vertex, material, texture or draw call is added anywhere, and the main-thread and
 * worker compilers derive identical output because the inputs are identical.
 *
 *  - pathWearPigmentAt — ruts along the route tangent, aggregate grit, a worn shoulder.
 *  - seamPigmentAt — the damp apron, deposit line, wall-foot debris and contact of every
 *    water/floor and floor/wall boundary, cornered by a distance FIELD rather than a per-cell band.
 *  - middleScaleGroundAt — the 1–5 tile band: pools and their drying rims, litter, moss, gravel.
 */

/// <summary>
/// What the compiler exposes to the ground-detail layers, under its own field names.
///
/// Mutable on purpose: the compiler re-derives these poles on every `setBiome`, and a structural
/// interface with `readonly` members cannot be satisfied by fields it has to reassign.
/// </summary>
/// <remarks>
/// PORT NOTE: the compiler passes itself (`syncGroundDetailContext(this.groundDetail, frame, terrain, this)`),
/// satisfying this interface structurally. In C# the compiler class implements it; its fields of the same names
/// are exposed through these getters (e.g. an explicit interface implementation).
/// </remarks>
public interface GroundDetailSource
{
    bool groundWear { get; }
    bool naturalGroundCover { get; }
    /// <summary>0xRRGGBB colours.</summary>
    int floorSoilPole { get; }
    int floorDryPole { get; }
    int floorWetPole { get; }
    int floorLushPole { get; }
    int floorBiomeDryPole { get; }
    int floorBiomeSoilPole { get; }
    int seamScreePole { get; }
    TerrainSurfaceProfile terrainSurfaceProfile { get; }
}

/// <summary>Everything the layers need from the compiler: the bake frame and the resolved palette poles.</summary>
public sealed class GroundDetailContext
{
    /// <summary>The frame in flight — `cell.x/y` are chunk-local and only this makes them world-absolute.</summary>
    public TerrainBakeFrame? frame;
    /// <summary>The terrain of the bake in flight; the seam distance field is world geometry, not plan data.</summary>
    public MaterializedTerrain? terrain;
    public bool groundWear;
    /// <summary>0xRRGGBB colours.</summary>
    public int soilPole;
    public int dryPole;
    public int wetPole;
    public int lushPole;
    /// <summary>Wall-foot debris pigment — the wall's own material, lightly soiled.</summary>
    public int screePole;
    public int biomeDryPole;
    public int biomeSoilPole;
    public bool naturalGroundCover;
    public TerrainSurfaceProfile surfaceProfile;
}

/// <summary>
/// Distance in TILES from a sub-cell sample point to the nearest water area, solid area and DROP — plus how
/// far that drop falls, in terrace levels.
///
/// Measured to each neighbour's actual tile BOX rather than to its centre, which is what makes every
/// consumer of this field contour-following instead of lattice-shaped — and is the whole reason the
/// shoreline's 90° corner notch disappears. Shared by the seam pigment and by the ground cover, so the
/// band a player sees and the plants growing out of it can never disagree about where the boundary is.
///
/// **`drop` is the terrace crest**, and it is what the ordinary stacked terraces of an Endless run were
/// missing entirely. `wall` finds a neighbour that stands UP from this cap; `drop` finds one the ground
/// falls AWAY to — the opposite event, and the one the eye reads as an edge from a 42° camera. A Chasm is
/// simply its deepest case and needs no lane of its own: it enters `drop` like every other fall, and
/// `dropLevels` — the fall of the nearest such neighbour — is what tells a one-step kerb from a shaft, so
/// the two earn different amounts of ONE treatment instead of needing two code paths.
/// PORT NOTE: a readonly struct (TS: an object literal) — it is created per floor-cap vertex and grass/flower
/// sample, so a class was a leading source of bake-worker garbage. It is immutable and never compared by identity.
/// </summary>
public readonly struct SeamDistances
{
    public readonly double water;
    public readonly double wall;
    public readonly double drop;
    public readonly double dropLevels;

    public SeamDistances(double water, double wall, double drop, double dropLevels)
    {
        this.water = water;
        this.wall = wall;
        this.drop = drop;
        this.dropLevels = dropLevels;
    }
}

public static partial class TerrainGroundDetail
{
    /// <summary>Create the retained context. One per compiler; <see cref="syncGroundDetailContext"/> refreshes it per bake.</summary>
    public static GroundDetailContext createGroundDetailContext(TerrainSurfaceProfile profile)
    {
        return new GroundDetailContext
        {
            frame = null,
            terrain = null,
            groundWear = true,
            soilPole = 0x856b49,
            dryPole = 0xb8ae90,
            wetPole = 0x6f7f74,
            lushPole = 0x769b68,
            screePole = 0x8d8d88,
            biomeDryPole = 0xb8ae90,
            biomeSoilPole = 0x856b49,
            naturalGroundCover = true,
            surfaceProfile = profile,
        };
    }

    /// <summary>Refresh the context for one bake. Called once per tile, never per sample.</summary>
    public static void syncGroundDetailContext(
        GroundDetailContext ctx,
        TerrainBakeFrame frame,
        MaterializedTerrain terrain,
        GroundDetailSource source)
    {
        ctx.frame = frame;
        ctx.terrain = terrain;
        ctx.groundWear = source.groundWear;
        ctx.naturalGroundCover = source.naturalGroundCover;
        ctx.soilPole = source.floorSoilPole;
        ctx.dryPole = source.floorDryPole;
        ctx.wetPole = source.floorWetPole;
        ctx.lushPole = source.floorLushPole;
        ctx.biomeDryPole = source.floorBiomeDryPole;
        ctx.biomeSoilPole = source.floorBiomeSoilPole;
        ctx.screePole = source.seamScreePole;
        ctx.surfaceProfile = source.terrainSurfaceProfile;
    }

    /// <summary>Walkable ground and the physically materialized floor at the bottom of a Chasm share one material stack.</summary>
    private static bool carriesFloorMaterial(TerrainCell cell)
    {
        return (cell.type == TileType.Floor && cell.walkable) || TerrainModel.terrainCellCarriesChasmFloor(cell);
    }
    /// <summary>
    /// The seam layer (see `TerrainGeometryCompiler.seamPigmentAt`). Reaches are in TILES: the damp
    /// apron carries about a tile and a half inland, the deposit line only the contact itself, and the
    /// wall foot a single tile. `SEAM_SCAN` is the neighbourhood radius the distance field searches — it must
    /// cover the longest reach, and no more, because it runs per cap sample.
    /// </summary>
    private const int SEAM_SCAN = 2;
    private const double SEAM_SHORE_REACH = 1.55;
    private const double SEAM_FOAM_REACH = 0.34;
    private const double SEAM_WALL_REACH = 1.05;
    private const double SEAM_SHORE_DAMP = 0.5;
    private const double SEAM_FOAM_STRENGTH = 0.46;
    private const double SEAM_WALL_SCREE = 0.26;
    private const double SEAM_WALL_CONTACT = 0.07;
    /// <summary>
    /// **The rim of a drop**: how far the ground crumbles back from an edge, and the width of the lit lip.
    ///
    /// These were authored for the Chasm rim and applied only there, which left every ORDINARY terrace crest —
    /// far more of the world than chasms are — as a bare stencil cut: the cap simply stopped and the face began.
    /// A terrace edge is the same physical event as a chasm edge with less depth behind it, so it is the same
    /// component, scaled by how far the ground actually falls (<see cref="dropRimStrength"/>).
    /// </summary>
    private const double SEAM_DROP_REACH = 1.15;
    private const double SEAM_RIM_LIP_REACH = 0.22;
    private const double SEAM_RIM_CRUMBLE = 0.34;
    private const double SEAM_RIM_LIP = 0.3;

    /// <summary>
    /// How much of the rim treatment a drop of `levels` terrace steps earns.
    ///
    /// A one-step kerb is a real edge and must read as one, but it is not a cliff: giving it the full chasm
    /// crumble turns every gentle terrace in a meadow into a quarry. The ramp reaches full strength at a
    /// two-step drop, which is where a face stops being a step and starts being a wall.
    /// </summary>
    public static double dropRimStrength(double levels)
    {
        return clamp((levels - 0.35) / 1.65, 0, 1);
    }

    /// <summary>Cover a crest earns on its own — see <see cref="crestCoverAt"/>.</summary>
    private const double CREST_COVER = 0.48;
    private const double CREST_COVER_REACH = 0.85;
    /// <summary>Cover a wall joint earns on its own, independent of the habitat field.</summary>
    private const double WALL_JOINT_COVER = 0.62;
    /// <summary>
    /// The middle scale (see `TerrainGeometryCompiler.middleScaleGroundAt`). Feature sizes are in TILES
    /// and deliberately sit in the 1–5 tile band: below it the cap's vertex sampling averages them away, above
    /// it they stop being ground character and become biome zoning, which the splat already owns.
    /// </summary>
    private const double MIDSCALE_POOL_TILES = 3.4;
    private const double MIDSCALE_LITTER_TILES = 2.1;
    private const double MIDSCALE_MOSS_TILES = 2.8;
    private const double MIDSCALE_GRAVEL_TILES = 1.6;
    private const double MIDSCALE_POOL_DEPTH = 0.34;
    private const double MIDSCALE_POOL_RIM = 0.16;
    private const double MIDSCALE_LITTER = 0.2;
    private const double MIDSCALE_MOSS = 0.26;
    private const double MIDSCALE_GRAVEL = 0.22;
    private const double PATH_GRIT_STRENGTH = 0.3;
    private const double PATH_SHOULDER_DUST = 0.2;
    // Fully resolved visual vocabulary for one explicitly painted artifact theme.

    /// <summary>
    /// Absolute world tile coordinates of a sub-cell sample point.
    ///
    /// `cell.x/y` are CHUNK-LOCAL. Sampling a world-continuous field with them makes the field restart at
    /// every chunk origin: the same pattern repeats in each chunk and the boundary shows as a hard straight
    /// line — which is exactly what a bake seam must never do. Anything that consumes a world field goes
    /// through here; only relative work (the seam distance search, which never leaves its own frame) may use
    /// the local pair.
    /// </summary>
    public static (double x, double y) worldPointAt(GroundDetailContext ctx, TerrainCell cell, double u, double v)
    {
        var frame = ctx.frame;
        return ((frame?.i0 ?? 0) + cell.x + u, (frame?.j0 ?? 0) + cell.y + v);
    }

    public static SeamDistances seamDistancesAt(MaterializedTerrain terrain, TerrainCell cell, double u, double v)
    {
        double px = cell.x + u;
        double pz = cell.y + v;
        double water = double.PositiveInfinity;
        double wall = double.PositiveInfinity;
        double drop = double.PositiveInfinity;
        double dropLevels = 0;
        for (int dy = -SEAM_SCAN; dy <= SEAM_SCAN; dy++)
        {
            for (int dx = -SEAM_SCAN; dx <= SEAM_SCAN; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var neighbour = TerrainModel.terrainCellAt(terrain, cell.x + dx, cell.y + dy);
                if (neighbour == null) continue;
                bool deepFloor = TerrainModel.terrainCellCarriesChasmFloor(cell);
                bool isWater = !deepFloor && neighbour.type == TileType.Water;
                bool isWall = deepFloor
                    ? !TerrainModel.terrainCellCarriesChasmFloor(neighbour)
                    : neighbour.type == TileType.Solid;
                bool isVoid = !deepFloor && neighbour.type == TileType.Chasm;
                // A neighbour the ground falls away to. Solid is excluded on purpose even when it is lower: a wall
                // occupies its tile, so the cap never overlooks it and there is no crest to draw.
                double fall = isWall ? 0 : cell.elevation - neighbour.elevation;
                bool falls = isVoid || fall > 0;
                if (!isWater && !isWall && !falls) continue;
                double ox = Math.max(neighbour.x - px, 0, px - (neighbour.x + 1));
                double oz = Math.max(neighbour.y - pz, 0, pz - (neighbour.y + 1));
                double distance = Math.hypot(ox, oz);
                if (falls && distance < drop)
                {
                    drop = distance;
                    // A void has no floor to measure against, so it counts as the deepest authored fall rather than as
                    // whatever its elevation field happens to hold.
                    dropLevels = isVoid ? Math.max(2, fall) : fall;
                }
                if (isWater) water = Math.min(water, distance);
                else if (isWall) wall = Math.min(wall, distance);
            }
        }
        return new SeamDistances(water, wall, drop, dropLevels);
    }

    /// <summary>
    /// **The seam layer** — every water↔floor and floor↔wall boundary in the world, treated as one thing.
    ///
    /// These are the two most common surfaces in a chunk (measured: 34–230 water seams and 463–679 wall-foot
    /// seams per four generated chunks) and they were the two most obviously untreated: the water plane
    /// simply stopped, a flat step face dropped, and the floor cap began. Land does not meet water at a right
    /// angle — it wades in — and a wall does not stand on clean ground, it stands in what fell off it.
    ///
    /// **Why a distance FIELD and not a per-cell rule.** The corner was the visible failure: where the
    /// shoreline staircase turned, two straight segments met in a 90° notch and the tile grid became
    /// readable instantly. A per-cell band cannot fix that because the cell IS the grid. Distance to the
    /// nearest water/solid AREA, measured from the sub-cell sample point to the neighbour's actual box, is
    /// radial by construction — so it rounds an inside corner and pinches an outside one exactly the way a
    /// real waterline does, and it does it continuously across cell and chunk seams because the geometry it
    /// measures is world-absolute.
    ///
    /// Pure pigment on data the bake already has: no vertex, no material, no texture, no draw call.
    /// </summary>
    public static int seamPigmentAt(
        GroundDetailContext ctx,
        int @base,
        MaterializedTerrain? terrain,
        TerrainCell cell,
        double u,
        double v,
        double grass)
    {
        if (terrain == null || !carriesFloorMaterial(cell)) return @base;
        var world = worldPointAt(ctx, cell, u, v);
        double px = world.x;
        double pz = world.y;
        var distances = seamDistancesAt(terrain, cell, u, v);
        double waterDistance = distances.water;
        double wallDistance = distances.wall;
        double dropDistance = distances.drop;
        double dropLevels = distances.dropLevels;

        int color = @base;
        if (waterDistance < SEAM_SHORE_REACH)
        {
            double shore = 1 - waterDistance / SEAM_SHORE_REACH;
            double wet = shore * shore;
            // The apron: ground that is damp because the water is right there. It darkens and saturates toward
            // the world's own wet pole rather than toward a generic grey, so a warm shore stays warm.
            color = Palette.mix(color, ctx.wetPole, clamp(wet * SEAM_SHORE_DAMP, 0, 0.42));
            color = Palette.mix(color, 0x000000, wet * 0.05);
            // The scum/foam line: a narrow bleached band at the contact itself, broken along the contour by a
            // world-space hash so it reads as deposited material and never as a drawn outline.
            double line = Math.max(0, 1 - waterDistance / SEAM_FOAM_REACH);
            if (line > 0)
            {
                double broken = smoothCellNoise(px * 2.6 - 17.3, pz * 2.6 + 9.1, 0.55, 181);
                double deposit = line * line * (0.35 + broken * 0.85);
                color = Palette.mix(color, ctx.dryPole, clamp(deposit * SEAM_FOAM_STRENGTH, 0, 0.5));
            }
        }
        if (dropDistance < SEAM_DROP_REACH)
        {
            // **The rim of a drop is a LIP, not a cut.** A boundary that simply stops is a stencil edge, and the eye
            // reads the tile grid the moment it sees one. Real ground at a drop is undercut: it loses its cover, its
            // last half tile crumbles and breaks, and the very edge catches the light that the fall below cannot
            // return. The break is sampled on a world hash so the line is ragged, never drawn.
            //
            // This used to fire on Chasm rims ONLY. Every ordinary terrace crest in the world — the stacked steps
            // that make up most of an Endless run's relief — therefore had no rim at all, which is precisely why
            // they read as stacked grey boxes: a razor-sharp geometric line with nothing on either side of it. The
            // strength now follows how far the ground actually falls, so a kerb gets a kerb's edge and a cliff gets
            // a cliff's, out of ONE component.
            double depth = dropRimStrength(dropLevels);
            double rim = (1 - dropDistance / SEAM_DROP_REACH) * depth;
            double crumble = smoothCellNoise(px * 3.1 - 27.7, pz * 3.1 + 44.3, 0.4, 197);
            color = Palette.mix(color, ctx.soilPole, clamp(rim * rim * (0.4 + crumble * 0.8) * SEAM_RIM_CRUMBLE, 0, 0.4));
            // The last sliver is the lit edge itself — it is what gives the drop a thickness to look over.
            double lip = Math.max(0, 1 - dropDistance / SEAM_RIM_LIP_REACH) * depth;
            color = Palette.mix(color, ctx.dryPole, lip * lip * SEAM_RIM_LIP);
        }
        if (wallDistance < SEAM_WALL_REACH)
        {
            double foot = 1 - wallDistance / SEAM_WALL_REACH;
            // What fell off the wall: soil and scree piled in the joint, thinning outward, plus the contact
            // darkening a real crack has. Vegetation is handled where the cover is emitted — a tuft growing OUT
            // of the joint is what finally makes the two surfaces read as one world.
            double scree = smoothCellNoise(px * 1.9 + 41.7, pz * 1.9 - 5.5, 0.85, 193);
            // Cubed, not squared: the pile hugs the joint instead of spreading a wedge across the corner. At an
            // outside corner two seams meet, so a broad falloff concentrated there and printed a visible brown
            // triangle — the corner is exactly where this term has to be at its tightest.
            double pile = foot * foot * foot * (0.55 + scree * 0.75) * (1 - grass * 0.5);
            // And it is the WALL's material, not generic brown soil: what lies at the foot of a wall is what
            // fell off it. Taking the pigment from the wall face is what makes the pile read as debris rather
            // than as a painted band of a colour the scene does not otherwise contain.
            color = Palette.mix(color, ctx.screePole, clamp(pile * SEAM_WALL_SCREE, 0, 0.3));
            color = Palette.mix(color, 0x000000, foot * foot * foot * SEAM_WALL_CONTACT);
        }
        return color;
    }

    /// <summary>
    /// **How worn the ground at this point is** — the ONE definition, read by both evaluators.
    ///
    /// The cap pigment below and the fragment shader's rut/grit surface must agree about where wear is, or a
    /// drawn groove lands on ground the pigment considers untrodden. So the quantity is defined once here and
    /// consumed twice: as a weight on <see cref="pathWearPigmentAt"/>'s ≥1-tile terms, and — published through
    /// <see cref="groundSurfaceVectorAt"/> onto the vertex channel — as the gate on the sub-tile structure the cap
    /// lattice provably cannot carry (see <c>CAP_PIGMENT_MIN_PERIOD_TILES</c>).
    ///
    /// Vegetation wins wherever it actually grew: wear belongs to BARE ground — the trodden route AND the open
    /// soil around it, which is the larger share of an Endless floor and was the flattest.
    /// </summary>
    public static double groundWearAt(double trail, double trailCore, double dirt, double grass)
    {
        return clamp(Math.max(trail * 0.5 + trailCore, dirt) * (1 - grass * 0.85), 0, 1);
    }

    /// <summary>
    /// **The per-vertex ground surface parameters** — what the fragment shader needs to draw the sub-tile
    /// structure this channel cannot draw itself.
    ///
    /// Two quantities in three floats:
    ///
    ///  - `wear` (0..1) — how bare and trodden the ground is, gating the GRIT. This is the whole open-soil
    ///    reading, route or not, because exposed aggregate is a property of bare ground everywhere.
    ///  - the route tangent, as a vector whose DIRECTION is the local travel direction and whose LENGTH is the
    ///    route core (0 off-route .. 1 in the middle of a path), gating the RUTS.
    ///
    /// **Packing the core into the vector's length is not a trick to save a slot, it is the correct object.**
    /// Ruts are cut by traffic and belong to the route only — the CPU term they replace was weighted by
    /// `trailCore`, and gating them on `wear` instead put grooves across every patch of open dirt in the world
    /// (visible immediately in the first after-frame). "Where the traffic goes, and how much of it there is" is
    /// one vector quantity, and carrying it as one means the two halves cannot drift apart.
    ///
    /// The vector form is also what makes interpolation safe: an angle is discontinuous at ±π, so interpolating
    /// one across a vertex where the route turns sweeps the grooves through every intermediate orientation and
    /// shears them into a fan. A vector interpolates as the direction it is, and a zero length — a vertex with
    /// no route at all — is unambiguously "no ruts here" rather than a direction pointing somewhere arbitrary.
    ///
    /// Both quantities are smooth at whole-tile scale, which is exactly why they may ride the cap lattice while
    /// the structure they parameterise may not.
    /// </summary>
    public static (double wear, double tangentX, double tangentZ) groundSurfaceVectorAt(
        GroundDetailContext ctx,
        TerrainRenderPlan plan,
        TerrainCell cell,
        double u,
        double v)
    {
        if (!ctx.groundWear || !carriesFloorMaterial(cell))
            return (0, 0, 0);
        double trail = terrainFloorFieldAt(plan, cell, u, v, plan.floor.route);
        double trailCore = terrainFloorFieldAt(plan, cell, u, v, plan.floor.routeCore);
        double dirt = terrainFloorFieldAt(plan, cell, u, v, plan.floor.dirt);
        double grass = terrainFloorFieldAt(plan, cell, u, v, plan.floor.grass);
        double tangent = terrainFloorFieldAt(plan, cell, u, v, plan.floor.direction);
        // The same weighting the retired CPU rut used: traffic in the core, thinning onto the shoulder.
        double core = clamp(trailCore * (0.62 + dirt * 0.38), 0, 1);
        return (
            groundWearAt(trail, trailCore, dirt, grass),
            Math.cos(tangent) * core,
            Math.sin(tangent) * core);
    }

    /// <summary>
    /// **Path wear** — the compacted route is a SURFACE, not a colour band.
    ///
    /// Route pigment above turns a broad wash into a legible path, but at gameplay zoom the result is
    /// still a flat gradient: the single largest area of screen with nothing in it.
    ///
    /// This function owns the part of that surface a cap vertex can actually represent — the terms at or above
    /// <c>CAP_PIGMENT_MIN_PERIOD_TILES</c>:
    ///
    ///  1. **Grit.** A broad world-space speckle of exposed aggregate, strongest in the compacted core.
    ///  2. **A worn shoulder.** A dusty lift exactly where the route hands over to vegetation — the edge
    ///     is where a path reads as a path, and a pure colour ramp has no edge at all.
    ///
    /// **What used to be here and is now drawn per fragment.** The ruts (0.833-tile period) and the fine grit
    /// octave (0.9-tile period) both sat UNDER the lattice's one-tile Nyquist limit. They were computed, mixed
    /// into the pigment, and then averaged out of existence by the vertex interpolator — the measured cost of
    /// keeping them was a full route-tangent field evaluation and two noise taps per cap vertex for a surface
    /// nobody could see. They now live in `TERRAIN_GROUND_SURFACE_GLSL`, gated by the very same
    /// <see cref="groundWearAt"/> weight, at the frequency they were always authored for.
    ///
    /// Everything is pigment on absolute-world fields: no vertex, material, texture or draw call is
    /// added, the result is continuous across chunk seams, and the main-thread and worker compilers
    /// derive it from the same pure inputs, so their output stays byte-identical. Off-route ground is
    /// returned untouched.
    /// </summary>
    public static int pathWearPigmentAt(
        GroundDetailContext ctx,
        int @base,
        TerrainCell cell,
        double u,
        double v,
        double trail,
        double trailCore,
        double dirt,
        double grass,
        double wetness)
    {
        double wear = groundWearAt(trail, trailCore, dirt, grass);
        if (wear < 0.02) return @base;
        var world = worldPointAt(ctx, cell, u, v);
        double wx = world.x;
        double wy = world.y;

        // 1 — Grit: exposed aggregate, at the one octave the lattice can resolve. Its fine partner is drawn per
        // fragment now, so this term is the broad body of the aggregate rather than half of a two-octave field.
        double grit = (smoothCellNoise(wx + 61.1, wy - 24.9, 2.8, 149) - 0.5) * 2 * wear;

        // 2 — The worn shoulder: a dusty lift where the route hands over, peaking at the mid ramp.
        double shoulder = Math.max(0, 1 - Math.abs(trail - 0.46) * 3.4) * (1 - trailCore) * (1 - grass);

        // Grit reads as both catch-light and shadow on the aggregate; a wet route stops sparkling.
        double gritDry = 1 - Math.min(0.8, wetness * 1.3);
        int color = Palette.mix(@base, ctx.dryPole, clamp(grit, 0, 1) * PATH_GRIT_STRENGTH * gritDry);
        color = Palette.mix(color, 0x000000, clamp(-grit, 0, 1) * PATH_GRIT_STRENGTH * 0.5 * gritDry);
        color = Palette.mix(color, ctx.dryPole, clamp(shoulder, 0, 1) * PATH_SHOULDER_DUST);
        return color;
    }

    /// <summary>
    /// **The middle scale of ground** — the 1–5 tile band between per-pixel grain and per-chunk zones.
    ///
    /// The floor had detail at both ends and nothing in between: three material octaves per pixel, biome
    /// zones per chunk, and between them one flat wash. That gap is exactly where real ground gets its
    /// character, because it is the scale of a puddle, of the litter under a tree, of the moss on the shaded
    /// side, of a gravel fan below a slope. Without it, ground can be perfectly shaded and still read as a
    /// surface with no history.
    ///
    /// Everything here is a world-continuous field the bake already has (moisture, hollow, cover, wear),
    /// folded into the cap pigment. No vertex, no material, no draw call — the same shape as the path wear
    /// and the seam layer.
    /// </summary>
    public static int middleScaleGroundAt(
        GroundDetailContext ctx,
        int @base,
        TerrainCell cell,
        double u,
        double v,
        double grass,
        double dirt,
        double wetness)
    {
        if (!ctx.groundWear || !carriesFloorMaterial(cell)) return @base;
        var world = worldPointAt(ctx, cell, u, v);
        double wx = world.x;
        double wy = world.y;
        int color = @base;

        // 1 — Damp hollows and puddles. A hollow that is also moist holds water; its RIM dries first and
        // cracks, which is what makes a puddle read as a depression rather than as a stain.
        double hollow = smoothCellNoise(wx * 0.55 + 12.7, wy * 0.55 - 31.3, MIDSCALE_POOL_TILES, 211);
        // Moisture MUST come from the continuous floor field, never from the cell-local `plan.moisture`
        // lane: a cell-local term repaints a whole cap and exposes the tile raster at a bank, which is
        // exactly what the water-adjacency guard exists to catch (it caught this).
        double moisture = clamp(wetness * 1.35, 0, 1);
        double pool = clamp((hollow - (0.62 - moisture * 0.26)) * 6, 0, 1) * (1 - grass * 0.7);
        if (pool > 0.001)
        {
            color = Palette.mix(color, ctx.wetPole, pool * MIDSCALE_POOL_DEPTH);
            color = Palette.mix(color, 0x000000, pool * 0.06);
            // The drying rim: a thin bleached collar exactly where the pool ends.
            double rim = clamp(1 - Math.abs(pool - 0.32) * 7, 0, 1);
            color = Palette.mix(color, ctx.dryPole, rim * MIDSCALE_POOL_RIM);
        }

        // 2 — Litter and moss. Litter gathers where growth is (under and around what grows); moss takes the
        // damp side of the same field. Both are broad, soft and low-contrast — they are texture, not props.
        double litter = smoothCellNoise(wx * 0.9 - 7.1, wy * 0.9 + 22.9, MIDSCALE_LITTER_TILES, 223);
        double litterMask = clamp((litter - 0.5) * 2, 0, 1) * clamp(grass * 1.4, 0, 1);
        color = Palette.mix(color, ctx.soilPole, litterMask * MIDSCALE_LITTER);
        double moss = smoothCellNoise(wx * 0.7 + 55.3, wy * 0.7 - 14.7, MIDSCALE_MOSS_TILES, 227);
        double mossMask = clamp((moss - 0.56) * 3.2, 0, 1) * moisture * (1 - dirt * 0.6);
        color = Palette.mix(color, ctx.lushPole, mossMask * MIDSCALE_MOSS);

        // 3 — Gravel fans on bare, dry ground: the coarse aggregate that collects where nothing grows.
        double fan = smoothCellNoise(wx * 1.25 - 63.9, wy * 1.25 - 3.7, MIDSCALE_GRAVEL_TILES, 229);
        double fanMask = clamp((fan - 0.58) * 3.4, 0, 1) * clamp(dirt * 1.2, 0, 1) * (1 - moisture * 0.7);
        color = Palette.mix(color, ctx.dryPole, fanMask * MIDSCALE_GRAVEL);
        return color;
    }

    /// <summary>
    /// 0..1 cover a wall joint earns by itself, independent of the habitat field.
    ///
    /// A wall foot is sheltered, damp and never trodden, so it beats the habitat field on its own. Because
    /// the ONE turf cover this feeds is read by both the cap pigment and the blades, the green in the joint
    /// and the tufts standing in it can never disagree — which is what welds wall and ground into one world
    /// instead of leaving a wall standing ON the ground.
    /// </summary>
    public static double wallJointCoverAt(
        GroundDetailContext ctx,
        TerrainCell cell,
        double u,
        double v,
        SeamDistances? distances = null)
    {
        var terrain = ctx.terrain;
        if (terrain == null) return 0;
        double wall = (distances ?? seamDistancesAt(terrain, cell, u, v)).wall;
        if (wall >= SEAM_WALL_REACH) return 0;
        double nearness = 1 - wall / SEAM_WALL_REACH;
        return nearness * nearness * WALL_JOINT_COVER;
    }

    /// <summary>
    /// 0..1 cover the ground just inland of a CREST earns — the other half of <see cref="wallJointCoverAt"/>.
    ///
    /// A wall foot and a terrace crest are the two edges a cap has, and only the foot was ever treated. An edge
    /// is a good place to be a plant: light from the side, drainage, and nothing walking over it. So growth
    /// gathers in a band behind the lip — and, critically, NOT on the lip itself, which is the broken bare rock
    /// the rim pigment above is busy crumbling. That pairing is what makes an edge read: a bright bare sliver
    /// with green immediately behind it, rather than one geometric line.
    ///
    /// Because this feeds the ONE <see cref="turfCoverAt"/> the cap pigment and the blade emitter both read, a tuft
    /// standing at the brink and the green painted under it cannot disagree — the same property that welds a
    /// wall foot to its ground.
    /// </summary>
    public static double crestCoverAt(
        GroundDetailContext ctx,
        TerrainCell cell,
        double u,
        double v,
        SeamDistances? distances = null)
    {
        var terrain = ctx.terrain;
        if (terrain == null) return 0;
        var resolved = distances ?? seamDistancesAt(terrain, cell, u, v);
        double drop = resolved.drop;
        double dropLevels = resolved.dropLevels;
        if (drop >= CREST_COVER_REACH) return 0;
        // Suppressed inside the lip, peaking just behind it, gone by the reach.
        double inland = clamp((drop - SEAM_RIM_LIP_REACH) / (CREST_COVER_REACH - SEAM_RIM_LIP_REACH), 0, 1);
        double band = Math.sin(inland * Math.PI);
        return band * band * CREST_COVER * dropRimStrength(dropLevels);
    }

    /// <summary>
    /// Turf cover at a point on a walkable floor cap — the grass patch itself.
    ///
    /// This is the ONE evaluation. The cap publishes it per vertex so the fragment shader can paint the patch as
    /// part of the ground, and `addIntegratedFloorGrass` samples the same function so a blade can only ever
    /// stand where the ground is already painted as grass. Nothing else is allowed to decide where grass is.
    /// </summary>
    public static double turfCoverAt(
        GroundDetailContext ctx,
        TerrainRenderPlan plan,
        TerrainCell cell,
        double worldX,
        double worldZ,
        double u,
        double v)
    {
        if (!ctx.naturalGroundCover || !carriesFloorMaterial(cell)) return 0;
        double cover = TerrainFloorTurf.terrainTurfCoverAt(
            terrainFloorFieldAt(plan, cell, u, v, plan.floor.grass),
            terrainFloorFieldAt(plan, cell, u, v, plan.floor.route),
            worldX,
            worldZ,
            ctx.surfaceProfile);
        // Plants love a crack. A wall foot is sheltered, damp and never trodden, so it beats the habitat
        // field on its own — and because this is the ONE cover the cap pigment AND the blades both read, the
        // green in the joint and the tufts standing in it can never disagree. Without this a wall stands ON
        // the ground; with it the ground grows INTO the wall, which is what welds the two into one world.
        // The crest is the same argument at the cap's OTHER edge, and it is what puts growth across a terrace
        // top instead of leaving the world's most common edge as a bare cut.
        // ONE neighbourhood scan for both edges. `seamDistancesAt` walks a 5x5 box per call and this runs per cap
        // vertex (nine per tile, every tile of every bake), so letting the two helpers each fetch their own
        // distances would have added a second full scan to the hottest loop in the compiler for no new information.
        SeamDistances? distances = ctx.terrain != null ? seamDistancesAt(ctx.terrain, cell, u, v) : null;
        double edge = Math.max(
            wallJointCoverAt(ctx, cell, u, v, distances),
            crestCoverAt(ctx, cell, u, v, distances));
        return edge > cover ? edge : cover;
    }

    /// <summary>
    /// Fold paths, exposed earth and the low meadow body into the existing cap pigment. The masks are continuous
    /// absolute-world fields, so this adds neither a material/draw call nor a chunk seam.
    ///
    /// The meadow/turf term here is deliberately the BODY of the green and nothing more. Its contour, its
    /// texture and its edge are the shader's job, driven by the turf channel this same cap publishes — see
    /// `terrainFloorTurf`. Painting the full green twice, once per vertex and once per fragment,
    /// is how a patch starts looking like a polygon again.
    /// </summary>
    public static int livingGroundPigmentAt(
        GroundDetailContext ctx,
        int @base,
        TerrainRenderPlan plan,
        TerrainCell cell,
        double u,
        double v,
        double _moisture,
        MaterializedTerrain? terrain = null)
    {
        if (!ctx.groundWear || !carriesFloorMaterial(cell)) return @base;
        double trail = terrainFloorFieldAt(plan, cell, u, v, plan.floor.route);
        double trailCore = terrainFloorFieldAt(plan, cell, u, v, plan.floor.routeCore);
        double dirt = terrainFloorFieldAt(plan, cell, u, v, plan.floor.dirt);
        double meadow = terrainFloorFieldAt(plan, cell, u, v, plan.floor.meadow);
        double grass = terrainFloorFieldAt(plan, cell, u, v, plan.floor.grass);
        double wetness = terrainFloorFieldAt(plan, cell, u, v, plan.floor.wetness);
        // `plan.terrain?.floorUsage !== undefined`
        bool semanticRoute = plan.terrain?.floorUsage != null;
        int dryPole = semanticRoute ? ctx.dryPole : ctx.biomeDryPole;
        int soilPole = semanticRoute ? ctx.soilPole : ctx.biomeSoilPole;
        // The shoulder carries a lighter dry wash; the compacted core sinks into darker exposed soil. Separating
        // them is what turns a broad colour band into a trodden path at gameplay zoom.
        int color = Palette.mix(@base, dryPole, trail * (semanticRoute ? 0.56 : 0.46));
        color = Palette.mix(
            color,
            soilPole,
            semanticRoute
                ? clamp(trailCore * 0.48 + dirt * 0.34, 0, 0.78)
                : trailCore * 0.48 + dirt * 0.34);
        color = Palette.mix(
            color,
            0x000000,
            semanticRoute ? trailCore * 0.035 + dirt * 0.028 : trailCore * 0.105 + dirt * 0.07);
        // The broad ecological body only. It keeps a meadow reading as a meadow at distance and under every
        // low-detail path, while the patch's actual shape and value arrive per fragment from the turf channel.
        if (ctx.naturalGroundCover)
            color = Palette.mix(color, ctx.lushPole, clamp(meadow * 0.14 + grass * 0.26, 0, 0.4));
        // Damp sediment sits below the meadow/turf read and is strongest off the compacted route. A tiny value
        // reduction supplies contact depth without the black shoreline band that screen-space AO produced.
        double wetMix = wetness * (0.16 + dirt * 0.12) * (1 - trailCore * 0.42);
        color = Palette.mix(color, ctx.wetPole, clamp(wetMix, 0, 0.26));
        color = Palette.mix(color, 0x000000, wetness * 0.018);
        color = pathWearPigmentAt(ctx, color, cell, u, v, trail, trailCore, dirt, grass, wetness);
        color = seamPigmentAt(ctx, color, terrain, cell, u, v, grass);
        return middleScaleGroundAt(ctx, color, cell, u, v, grass, dirt, wetness);
    }
}
