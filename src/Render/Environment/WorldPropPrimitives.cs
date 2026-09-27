// Port of packages/client/src/render/environment/worldPropPrimitives.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `PropTileset` is the TS type alias `TerrainMaterialTileset & { readonly surfaceForTile?: unknown }`, i.e.
//   structurally the terrain tileset with an optional resolver. C# has no cross-file type aliases, so every file
//   that names it declares `using PropTileset = Fluitown.Render.TerrainMaterialTileset;` (TerrainTileset derives
//   from it, so both the live tileset and the worker's material tileset are accepted).
// * The builder's `number | readonly number[]` channel arguments are the struct <see cref="NumberOrArray"/>
//   (implicit from double/int and from double[]/List<double>), so call sites read exactly like the TypeScript.
// * The written per-vertex scratch (SHADE4, WIND4, QUAD, limb rings…) is [ThreadStatic]: the TS has one copy per
//   worker, Godot compiles on several threads of one process. The builder copies vertex data out synchronously,
//   so one mutable set PER THREAD is safe. Never-written tables stay static readonly.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Fluitown.Runtime;
using static Fluitown.Render.Palette;
using static Fluitown.Render.RenderWorldLighting;
using static Fluitown.Render.TerrainGeometryCompilerTheme;
using static Fluitown.Render.TerrainGeometryCompilerStyle;
using static Fluitown.Render.TerrainProjection;
using Math = Fluitown.Runtime.JsMath;
using PropTileset = Fluitown.Render.TerrainMaterialTileset;

namespace Fluitown.Render;

/// <summary>
/// JS <c>number | readonly number[]</c>: a surface channel given either once for the whole polygon or per vertex.
/// The builder distinguishes the two exactly as the original does with <c>typeof x === 'number'</c>
/// (<see cref="isNumber"/>). Implicitly constructed from a <c>double</c> (and therefore any <c>int</c>) or from a
/// <c>double[]</c> / <c>List&lt;double&gt;</c>, so ported call sites pass the same values the TypeScript passes.
/// </summary>
public readonly struct NumberOrArray
{
    public readonly double number;
    public readonly IReadOnlyList<double>? array;

    public NumberOrArray(double value)
    {
        number = value;
        array = null;
    }

    public NumberOrArray(IReadOnlyList<double> values)
    {
        number = 0;
        array = values;
    }

    /// <summary><c>typeof x === 'number'</c>.</summary>
    public bool isNumber => array is null;

    public static implicit operator NumberOrArray(double value) => new(value);

    public static implicit operator NumberOrArray(double[] values) => new(values);
}

/// <summary>
/// The slice of the terrain tile builder props need.
///
/// Structural on purpose: the live layer and the worker each hand in their own builder and neither module has
/// to know about the other. Signatures mirror `TileGeometryBuilder` exactly.
/// </summary>
/// <remarks>Universal lit-surface subset shared by terrain props and soft foliage.</remarks>
public interface PropSurfaceBuilder
{
    void addSurface(
        IReadOnlyList<P3> points,
        NumberOrArray nx,
        NumberOrArray ny,
        NumberOrArray nz,
        NumberOrArray hex,
        NumberOrArray kind,
        NumberOrArray strength,
        IReadOnlyList<double>? shade = null,
        NumberOrArray? wind = null,
        bool actorWall = false,
        bool preserveEdgeOn = false,
        bool orbitBackside = false,
        NumberOrArray? emissive = null);
}

public interface PropGeometryBuilder : PropSurfaceBuilder
{
    void addOverlay(IReadOnlyList<P3> points, int hex, double alpha, double edgeOnNormalX = 0);

    void addOverlayLineFlat(
        double y,
        double ax,
        double az,
        double bx,
        double bz,
        double width,
        int hex,
        double alpha);

    void addContactBlob(
        double cx,
        double cz,
        double y,
        double rx,
        double rz,
        int hex,
        double alpha);

    /// <summary>Geometry that is never drawn and exists only to throw a sun shadow. See <see cref="WorldPropPrimitives.propShadowVolume"/>.</summary>
    void addShadowCaster(IReadOnlyList<P3> points);
}

/// <summary>
/// Prop material family, derived ONCE from the biome tileset so every prop is built from the SAME colour
/// truths the terrain, bridges and walls already wear (timber = the bridge timber, stone = the wall chalk,
/// ink = the silhouette line) — dressing can never read as a foreign object dropped into the world.
/// </summary>
public sealed class HubPropPalette
{
    public int ink;
    public int paper;
    public int timber;
    public int timberDark;
    public int timberLit;
    public IReadOnlyList<int> canopies = Array.Empty<int>();
    public int stoneTop;
    public int stoneLit;
    public int stoneSide;
    public IReadOnlyList<int> accents = Array.Empty<int>();
}

/// <summary>
/// The one geometry vocabulary every collision-neutral world prop is built from.
///
/// Trees, bushes and stumps all speak these primitives,
/// so a Hub grove and an Endless grove are literally the same shapes in different pigment. The module owns no
/// Scene, Material or GPU resource: it fills a caller-supplied builder, which is what lets the live render
/// layer and the bake worker run one implementation.
///
/// ## FORM DOCTRINE — every prop is held against the world's own style anchors
///   1. Slab silhouettes: a prop is a few large stacked blocks/frusta (cap + south face, like a terrace);
///      nothing spindly — posts are fat shafts, never needles.
///   2. World scale: heights that a person must be able to judge (storeys, trees) are authored in player
///      heights, not tile fractions — a tile is 40 px in the Hub and 62.5 px in Endless.
///   3. Material family: ink shafts/roofs + paper/chalk faces + the bridges' timber, and every cap wears the
///      terrain's drawn ink contour on its edge.
///   4. ONE saturated accent per object — never stripes of competing hues; the paper/ink body carries the read.
///   5. Silhouette-first: each prop must still read as its block silhouette fully zoomed out.
///
/// ## Visibility
/// The gameplay camera is a fixed oblique orthographic view looking down and north, so a face is visible iff
/// `n · (0, G, H) > 0` with G/H the camera basis in terrainProjection. Upright frusta cull on `nz`
/// alone because their side normals barely tilt; limbs, which point in every direction, use the real test in
/// <see cref="propLimbVisible"/>. Culling here is what keeps a branching tree affordable.
/// </summary>
public static partial class WorldPropPrimitives
{
    public const double PROP_TAU = Math.PI * 2;

    private static readonly double SURF_FLOOR = TERRAIN_SURFACE_PATTERN.floor;

    /// <summary>
    /// How far a foliage clump's CROWN CAP is pulled toward the lobe's own shaded side. `?leafcap=0` restores the
    /// undamped cap for an A/B.
    ///
    /// A clump is a rounded mass, and the disc that closes its top is the smallest facet on it — about a third of
    /// the lobe's radius. Painted with the full LIT pigment and turned straight at the sky, it is also the
    /// brightest thing on the plant: measured on a shipping frame, ~188/255 against a ~140 ground and a ~120
    /// canopy. That is fine while a lobe is twenty pixels across. At colony zoom and beyond, each of those discs
    /// collapses to ONE pixel, and a grove turns into a field of isolated bright specks — the reported "glitter"
    /// on trees, bushes and trunks. Measured: damping the caps removes 28 % of the frame's isolated bright pixels
    /// and 30 % of their energy, and no other lever tried (MSAA 4/8, the terrain's direct specular, the relief
    /// bump's footprint fade) moved it at all.
    ///
    /// Doctrine, not taste: a prop must still read as its block silhouette fully zoomed out, and a facet the
    /// camera cannot resolve may not be the loudest value on the object. The lobe keeps its sun side — that lives
    /// in the crown's value ramp and in the side bands' own shading, both untouched.
    /// </summary>
    /// <remarks>
    /// PORT: the original reads `?leafcap=0` from `location.search` when a `location` exists. The engine-free port
    /// has no page URL, so it always takes the shipping value (the one the bake worker and Node use).
    /// </remarks>
    private const double FOLIAGE_CROWN_CAP_DAMP = 0.45;

    /// <summary>Reused per-vertex scratch. The builder copies vertex data out synchronously, so one mutable set is safe.</summary>
    /// <remarks>UNIT_SHADE / UNIT_ZERO are never written and stay shared; the written scratch is per thread.</remarks>
    private static readonly double[] UNIT_SHADE = { 1, 1, 1, 1 };
    private static readonly double[] UNIT_ZERO = { 0, 0, 0, 0 };
    [ThreadStatic] private static double[]? _SHADE4;
    private static double[] SHADE4 => _SHADE4 ??= new double[] { 1, 1, 1, 1 };
    [ThreadStatic] private static double[]? _WIND4;
    private static double[] WIND4 => _WIND4 ??= new double[] { 0, 0, 0, 0 };
    [ThreadStatic] private static double[]? _SPIKE_COLOR;
    private static double[] SPIKE_COLOR => _SPIKE_COLOR ??= new double[] { 0, 0, 0 };
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

    private static P3[] quad(
        double x0,
        double y0,
        double z0,
        double x1,
        double y1,
        double z1,
        double x2,
        double y2,
        double z2,
        double x3,
        double y3,
        double z3)
    {
        P3[] q = QUAD;
        q[0].x = x0;
        q[0].y = y0;
        q[0].z = z0;
        q[1].x = x1;
        q[1].y = y1;
        q[1].z = z1;
        q[2].x = x2;
        q[2].y = y2;
        q[2].z = z2;
        q[3].x = x3;
        q[3].y = y3;
        q[3].z = z3;
        return q;
    }

    /* ── Which side of a cluster the key light is on ────────────────────────────────────────────────────────
     * Baked dressing never enters a shadow pass (`castShadow: false` on the surface batch, by design), so the
     * one thing a scatter of lobes can never be given by the renderer is the shading the SUN-SIDE lobes throw
     * on the ones behind them. Without it a crown, a shrub or a wreath is a body of revolution: every lobe
     * shows the same span of azimuths, so its lit half and its far half average out to the same value and the
     * whole cluster reads as a flat field of discs.
     *
     * The cue is therefore authored as pigment, off the ONE global key direction the terrain already lights
     * against (LIGHT_DIR) — never a second, private idea of where the sun is. */

    /// <summary>Horizontal projection of the global key light, in world (x, z). The screen row is world +z.</summary>
    private static readonly double KEY_X = LIGHT_DIR.x;
    private static readonly double KEY_Z = LIGHT_DIR.y;

    /// <summary>
    /// How far along the key light's azimuth a point sits inside its cluster: −1 the far side, +1 the sun side.
    /// </summary>
    /// <param name="dx">offset from the cluster centre in world X</param>
    /// <param name="dz">offset from the cluster centre in world Z</param>
    /// <param name="reach">the cluster's own half-width; the facing is measured against it so a shrub and a canopy
    /// resolve to the same −1..+1 scale and share one authored ramp.</param>
    public static double propSunFacing(double dx, double dz, double reach)
    {
        double facing = (dx * KEY_X + dz * KEY_Z) / Math.max(1e-4, reach);
        return facing < -1 ? -1 : facing > 1 ? 1 : facing;
    }

    /// <summary>Deterministic scalar hash to a stable [0, 1) sample (pure and allocation-free).</summary>
    public static double propHash(double id)
    {
        int h = Math.imul(Js.ToInt32(id), 0x45d9f3b);
        h = Math.imul(h ^ (int)((uint)h >> 16), 0x45d9f3b);
        return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
    }

    /// <summary>Accent table for banner/stall props (indexes the shared prop's `accent` hint): gold (the trading stands'
    ///  warm coin gold), cyan, vermilion, the raid's violet, moss — ONE saturated identity hue per object.</summary>
    private static readonly IReadOnlyList<int> HUB_ACCENTS = new int[]
    {
        0xd8a24a,
        TERRAIN_GEOMETRY_HUB_PALETTE.neon[0],
        TERRAIN_GEOMETRY_HUB_PALETTE.fire,
        TERRAIN_GEOMETRY_HUB_PALETTE.neon[2],
        TERRAIN_GEOMETRY_HUB_PALETTE.patchMoss,
    };

    private static readonly ConditionalWeakTable<PropTileset, HubPropPalette> propPaletteCache = new();

    public static HubPropPalette propPaletteFor(PropTileset tileset)
    {
        if (propPaletteCache.TryGetValue(tileset, out HubPropPalette? p)) return p;
        int ground = tileset.terrain.floorTone;
        int paper = tileset.terrain.wallLit;
        p = new HubPropPalette
        {
            ink = tileset.terrain.wallLine,
            paper = paper,
            timber = tileset.bridge.body,
            timberDark = tileset.bridge.bodyShadow,
            timberLit = tileset.bridge.lip,
            // Soft in-world pine greens: the Hub's foliage tones pulled toward the ground paper so a grove sits IN
            // the pasture light instead of punching a saturated hole into it.
            canopies = new int[]
            {
                mix(TERRAIN_GEOMETRY_HUB_PALETTE.patchMoss, ground, 0.3),
                mix(TERRAIN_GEOMETRY_HUB_PALETTE.treeMid, ground, 0.34),
                mix(TERRAIN_GEOMETRY_HUB_PALETTE.patchGrass, ground, 0.3),
            },
            stoneTop = tileset.terrain.wall,
            stoneLit = tileset.terrain.wallLit,
            stoneSide = tileset.terrain.wallFace,
            accents = HUB_ACCENTS.map((hex) => mix(hex, paper, 0.16)),
        };
        propPaletteCache.AddOrUpdate(tileset, p);
        return p;
    }

    public static List<P3> propRingPts(
        double cx,
        double cz,
        double y,
        double r,
        int sides,
        double rot)
    {
        var pts = new List<P3>(sides);
        for (int i = 0; i < sides; i++)
        {
            double a = rot + ((double)i / sides) * PROP_TAU;
            pts.push(new P3 { x = cx + Math.cos(a) * r, y = y, z = cz + Math.sin(a) * r });
        }
        return pts;
    }

    /// <summary>A lit prop frustum (cap + the south-facing side band) — the one primitive most props are built from.</summary>
    /// <param name="emitCap">Suppressed where the cap is provably interior — the waist of a foliage clump, a stacked shaft joint.</param>
    public static void propFrustum(
        PropSurfaceBuilder builder,
        double cx,
        double cz,
        double yBottom,
        double yTop,
        double rBottom,
        double rTop,
        int sides,
        double rot,
        int capHex,
        int sideHex,
        double footShade = 0.82,
        double windTop = 0,
        double windBottom = 0,
        bool emitCap = true)
    {
        List<P3> top = propRingPts(cx, cz, yTop, rTop, sides, rot);
        List<P3> bottom = propRingPts(cx, cz, yBottom, rBottom, sides, rot);
        if (emitCap) builder.addSurface(top, 0, 1, 0, capHex, SURF_FLOOR, 0.05, UNIT_SHADE, windTop);
        double ny = propFrustumSideRise(yTop - yBottom, rBottom, rTop);
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            double mid = rot + ((i + 0.5) / sides) * PROP_TAU;
            double nz = Math.sin(mid);
            // Comic look: the Flui perspective walks around the prop, so the north half exists too.
            if (nz <= 0.06 && !TerrainComicGeometry.ClosedShells) continue;
            setShade4(1, 1, footShade, footShade);
            setWind4(windTop, windTop, windBottom, windBottom);
            builder.addSurface(
                new P3[] { top[i], top[j], bottom[j], bottom[i] },
                Math.cos(mid),
                ny,
                nz,
                sideHex,
                SURF_FLOOR,
                0.05,
                SHADE4,
                WIND4);
        }
    }

    /// <summary>
    /// The prop family's small authored sky lean on a near-vertical face. Kept exactly as it was: every upright
    /// shaft, plinth and post in the world was authored against it.
    /// </summary>
    private const double PROP_SIDE_SKY_LEAN = 0.22;

    /// <summary>
    /// The vertical component of a frustum side face's normal: the authored sky lean PLUS the taper it has.
    ///
    /// The taper term used to be missing entirely — every side band, at every caller, was handed the flat
    /// <see cref="PROP_SIDE_SKY_LEAN"/>. For a shaft that is right, and nothing about a shaft changes here. For a
    /// FOLIAGE CLUMP it is the same defect the grass blades had, a SHAPE lit as a WALL: a clump is built from
    /// this primitive as a pinched foot flaring to a full waist and closing again into a crown, so its upper
    /// bands are turned nearly skyward and its foot is turned at the floor. Held at one value the whole mass
    /// receives the light of a cylinder — no crest catches the sky, no underside falls away, and every lobe in a
    /// crown resolves to the same value however it is shaped.
    ///
    /// `dr/dy` is the missing term and it is added, not substituted, so a straight prop is untouched and only
    /// geometry that really tapers starts being lit like it.
    /// </summary>
    public static double propFrustumSideRise(double height, double rBottom, double rTop)
    {
        if (height <= 1e-4) return PROP_SIDE_SKY_LEAN;
        double taper = (rBottom - rTop) / height;
        double rise = PROP_SIDE_SKY_LEAN + (taper < -1.6 ? -1.6 : taper > 1.6 ? 1.6 : taper);
        return rise;
    }

    /// <summary>
    /// Is a face with this normal turned toward the fixed gameplay camera?
    ///
    /// Upright props can cull on `nz` alone, but a limb reaching north-and-up is genuinely visible — its cap faces
    /// the eye even though it points away in Z. Getting this wrong either opens a hole in a branch or doubles the
    /// face count of every tree in the world, so limbs use the real dot product against the camera basis.
    /// </summary>
    public static bool propLimbVisible(double nx, double ny, double nz)
    {
        _ = nx;
        return ny * TERRAIN_VIEW_GROUND_SCALE + nz * TERRAIN_VIEW_HEIGHT_SCALE > 0.045;
    }

    /// <summary>Scratch limb rings, so a branch chain allocates nothing per segment.</summary>
    [ThreadStatic] private static List<P3>? _LIMB_A;
    private static List<P3> LIMB_A => _LIMB_A ??= new List<P3>();
    [ThreadStatic] private static List<P3>? _LIMB_B;
    private static List<P3> LIMB_B => _LIMB_B ??= new List<P3>();
    [ThreadStatic] private static P3[]? _LIMB_FACE;
    private static P3[] LIMB_FACE => _LIMB_FACE ??= new P3[]
    {
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
    };

    private static void limbRing(
        List<P3> into,
        double cx,
        double cy,
        double cz,
        double ux,
        double uy,
        double uz,
        double vx,
        double vy,
        double vz,
        double radius,
        int sides,
        double rot)
    {
        into.Clear();
        for (int i = 0; i < sides; i++)
        {
            double a = rot + ((double)i / sides) * PROP_TAU;
            double c = Math.cos(a) * radius;
            double s = Math.sin(a) * radius;
            into.push(new P3 { x = cx + ux * c + vx * s, y = cy + uy * c + vy * s, z = cz + uz * c + vz * s });
        }
    }

    /// <summary>
    /// A tapered limb between two arbitrary points — the primitive a real tree is made of.
    ///
    /// The tube is closed by its far cap only: the near cap is always inside the parent limb or the bole, and the
    /// back half of the tube is culled, so one branch segment costs roughly `sides / 2 + 1` faces. Wind rises
    /// along the limb, so a twig at the tip moves and the shoulder at the trunk does not.
    /// </summary>
    public static void propLimb(
        PropSurfaceBuilder builder,
        double x0,
        double y0,
        double z0,
        double x1,
        double y1,
        double z1,
        double rStart,
        double rEnd,
        int sides,
        double rot,
        int capHex,
        int sideHex,
        double footShade = 0.78,
        double windStart = 0,
        double windEnd = 0,
        bool capped = true)
    {
        double ax = x1 - x0;
        double ay = y1 - y0;
        double az = z1 - z0;
        double length = Math.hypot(ax, ay, az);
        if (length < 1e-4) return;
        ax /= length;
        ay /= length;
        az /= length;
        // Any stable perpendicular basis will do; pick the world axis the limb is least parallel to so the cross
        // product never degenerates on a vertical leader.
        double helperY = Math.abs(ay) > 0.94 ? 0 : 1;
        double helperX = helperY == 0 ? 1 : 0;
        double ux = ay * 0 - az * helperY;
        double uy = az * helperX - ax * 0;
        double uz = ax * helperY - ay * helperX;
        double ulen = Math.hypot(ux, uy, uz);
        if (!Js.Truthy(ulen)) ulen = 1;
        ux /= ulen;
        uy /= ulen;
        uz /= ulen;
        double vx = ay * uz - az * uy;
        double vy = az * ux - ax * uz;
        double vz = ax * uy - ay * ux;

        List<P3> limbA = LIMB_A;
        List<P3> limbB = LIMB_B;
        P3[] limbFace = LIMB_FACE;
        limbRing(limbA, x0, y0, z0, ux, uy, uz, vx, vy, vz, rStart, sides, rot);
        limbRing(limbB, x1, y1, z1, ux, uy, uz, vx, vy, vz, rEnd, sides, rot);

        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            double a = rot + ((i + 0.5) / sides) * PROP_TAU;
            double c = Math.cos(a);
            double s = Math.sin(a);
            double nx = ux * c + vx * s;
            double ny = uy * c + vy * s;
            double nz = uz * c + vz * s;
            if (!TerrainComicGeometry.ClosedShells && !propLimbVisible(nx, ny, nz)) continue;
            limbFace[0] = limbB[i];
            limbFace[1] = limbB[j];
            limbFace[2] = limbA[j];
            limbFace[3] = limbA[i];
            setShade4(1, 1, footShade, footShade);
            setWind4(windEnd, windEnd, windStart, windStart);
            builder.addSurface(limbFace, nx, ny, nz, sideHex, SURF_FLOOR, 0.05, SHADE4, WIND4);
        }
        if (capped && (TerrainComicGeometry.ClosedShells || propLimbVisible(ax, ay, az)))
        {
            setWind4(windEnd, windEnd, windEnd, windEnd);
            builder.addSurface(limbB, ax, ay, az, capHex, SURF_FLOOR, 0.05, UNIT_SHADE, windEnd);
        }
    }

    /// <summary>
    /// One foliage mass: a squat faceted spheroid built from two frusta that share a waist.
    ///
    /// This is the leaf unit of the whole world — a tree crown is several of these hung on limb tips, a bush is a
    /// few of them on the ground. Keeping it one primitive is what makes a bush read as "the same plant language"
    /// as the tree above it.
    /// </summary>
    /// <param name="bands">Three rings read as a rounded leaf mass; two are enough for a small satellite or a berry (`2 | 3`).</param>
    public static void propClump(
        PropSurfaceBuilder builder,
        double cx,
        double cy,
        double cz,
        double radius,
        double height,
        int sides,
        double rot,
        int topHex,
        int sideHex,
        double wind = 0,
        double squash = 1,
        int bands = 3)
    {
        // Fluitown comic look (not in the original): one smooth, closed puff per mass — see FluitownSoftFoliage.
        if (FluitownSoftFoliage.Enabled)
        {
            FluitownSoftFoliage.propSoftClump(builder, cx, cy, cz, radius, height, sides, rot, topHex, sideHex, wind, squash);
            return;
        }
        // Ring radii along the mass: a pinched foot, a full waist, a softened shoulder and a small crown. Only the
        // last band emits a cap — every interior cap would be hidden geometry that still costs a draw and can
        // z-fight along the shared ring.
        double rFoot = radius * (0.5 + squash * 0.14);
        double rWaist = radius;
        double rShoulder = radius * (0.78 - squash * 0.04);
        double rTop = radius * (0.3 + squash * 0.08);
        // The one facet on this mass that the camera stops resolving first — see FOLIAGE_CROWN_CAP_DAMP.
        int crownHex = mix(topHex, sideHex, FOLIAGE_CROWN_CAP_DAMP);
        if (bands == 3)
        {
            double waist3 = cy + height * 0.3;
            double shoulder = cy + height * 0.68;
            propFrustum(
                builder,
                cx,
                cz,
                cy,
                waist3,
                rFoot,
                rWaist,
                sides,
                rot,
                topHex,
                sideHex,
                0.68,
                wind * 0.86,
                wind * 0.7,
                false);
            propFrustum(
                builder,
                cx,
                cz,
                waist3,
                shoulder,
                rWaist,
                rShoulder,
                sides,
                rot + 0.22,
                topHex,
                sideHex,
                0.8,
                wind * 0.94,
                wind * 0.86,
                false);
            propFrustum(
                builder,
                cx,
                cz,
                shoulder,
                cy + height,
                rShoulder,
                rTop,
                sides,
                rot + 0.44,
                crownHex,
                sideHex,
                0.9,
                wind,
                wind * 0.94);
            return;
        }
        double waist = cy + height * 0.36;
        propFrustum(
            builder,
            cx,
            cz,
            cy,
            waist,
            rFoot,
            rWaist,
            sides,
            rot,
            topHex,
            sideHex,
            0.7,
            wind * 0.86,
            wind * 0.7,
            false);
        propFrustum(
            builder,
            cx,
            cz,
            waist,
            cy + height,
            rWaist,
            rTop,
            sides,
            rot + 0.31,
            crownHex,
            sideHex,
            0.84,
            wind,
            wind * 0.9);
    }

    /// <summary>
    /// A true spike: a base ring converging on a single apex POINT.
    ///
    /// A frustum with a tiny top ring is not the same shape — it ends in a visible flat facet, which reads as a
    /// snapped-off stub rather than a point. Crystals, splinters, flame tongues and thorns all need the real
    /// thing, and at these sizes the triangle fan is cheaper than the frustum it replaces.
    /// </summary>
    public static void propSpike(
        PropGeometryBuilder builder,
        double x,
        double y,
        double z,
        double dirX,
        double dirY,
        double dirZ,
        double length,
        double radius,
        int sides,
        double rot,
        int tipHex,
        int sideHex,
        double footShade = 0.74,
        double wind = 0)
    {
        double len = Math.hypot(dirX, dirY, dirZ);
        if (!Js.Truthy(len)) len = 1;
        double ax = dirX / len;
        double ay = dirY / len;
        double az = dirZ / len;
        double helperY = Math.abs(ay) > 0.94 ? 0 : 1;
        double helperX = helperY == 0 ? 1 : 0;
        double ux = -az * helperY;
        double uy = az * helperX;
        double uz = ax * helperY - ay * helperX;
        double ulen = Math.hypot(ux, uy, uz);
        if (!Js.Truthy(ulen)) ulen = 1;
        ux /= ulen;
        uy /= ulen;
        uz /= ulen;
        double vx = ay * uz - az * uy;
        double vy = az * ux - ax * uz;
        double vz = ax * uy - ay * ux;
        List<P3> limbA = LIMB_A;
        double[] spikeColor = SPIKE_COLOR;
        limbRing(limbA, x, y, z, ux, uy, uz, vx, vy, vz, radius, sides, rot);
        var apex = new P3 { x = x + ax * length, y = y + ay * length, z = z + az * length };
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            double a = rot + ((i + 0.5) / sides) * PROP_TAU;
            double c = Math.cos(a);
            double s = Math.sin(a);
            // The facet normal of a cone leans toward the apex; without that lean every spike lights up flat.
            double taper = radius / Math.max(1e-4, length);
            double nx = ux * c + vx * s + ax * taper;
            double ny = uy * c + vy * s + ay * taper;
            double nz = uz * c + vz * s + az * taper;
            if (!TerrainComicGeometry.ClosedShells && !propLimbVisible(nx, ny, nz)) continue;
            setShade4(1, footShade, footShade, footShade);
            setWind4(wind, wind * 0.3, wind * 0.3, wind * 0.3);
            // Per-vertex pigment: a bright apex fading into the body is what makes a crystal read as a crystal and
            // a flame tongue as flame, without a second material or an emissive pass.
            spikeColor[0] = tipHex;
            spikeColor[1] = sideHex;
            spikeColor[2] = sideHex;
            builder.addSurface(
                new P3[] { apex, limbA[i], limbA[j] },
                nx,
                ny,
                nz,
                spikeColor,
                SURF_FLOOR,
                0.05,
                SHADE4,
                WIND4);
        }
    }

    /// <summary>An axis-aligned prop box (top cap + south face — the two faces the shear projection shows).</summary>
    public const int PROP_BOX_SIDE_NORTH = 1 << 0;
    public const int PROP_BOX_SIDE_EAST = 1 << 1;
    public const int PROP_BOX_SIDE_SOUTH = 1 << 2;
    public const int PROP_BOX_SIDE_WEST = 1 << 3;
    public const int PROP_BOX_ALL_SIDES =
        PROP_BOX_SIDE_NORTH | PROP_BOX_SIDE_EAST | PROP_BOX_SIDE_SOUTH | PROP_BOX_SIDE_WEST;

    /// <param name="surfaceKind">Defaults to TERRAIN_SURFACE_PATTERN.floor (null = the TS default parameter).</param>
    public static void propBox(
        PropGeometryBuilder builder,
        double x0,
        double x1,
        double z0,
        double z1,
        double y0,
        double y1,
        int topHex,
        int sideHex,
        double footShade = 0.85,
        bool closedSides = false,
        double? surfaceKind = null,
        int closedSideMask = PROP_BOX_ALL_SIDES)
    {
        double kind = surfaceKind ?? SURF_FLOOR;
        // Comic look: every side, the Flui perspective walks around the box.
        if (TerrainComicGeometry.ClosedShells) closedSides = true;
        builder.addSurface(
            quad(x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1),
            0,
            1,
            0,
            topHex,
            kind,
            0.05);
        setShade4(1, 1, footShade, footShade);
        if (!closedSides || (closedSideMask & PROP_BOX_SIDE_SOUTH) != 0)
        {
            builder.addSurface(
                quad(x0, y1, z1, x1, y1, z1, x1, y0, z1, x0, y0, z1),
                0,
                0.12,
                1,
                sideHex,
                kind,
                0.05,
                SHADE4);
        }
        if (!closedSides) return;
        if ((closedSideMask & PROP_BOX_SIDE_NORTH) != 0)
        {
            builder.addSurface(
                quad(x1, y1, z0, x0, y1, z0, x0, y0, z0, x1, y0, z0),
                0,
                0.12,
                -1,
                mix(sideHex, topHex, 0.08),
                kind,
                0.05,
                SHADE4);
        }
        if ((closedSideMask & PROP_BOX_SIDE_WEST) != 0)
        {
            builder.addSurface(
                quad(x0, y1, z0, x0, y1, z1, x0, y0, z1, x0, y0, z0),
                -1,
                0.12,
                0,
                mix(sideHex, topHex, 0.12),
                kind,
                0.05,
                SHADE4,
                UNIT_ZERO,
                false,
                true);
        }
        if ((closedSideMask & PROP_BOX_SIDE_EAST) != 0)
        {
            builder.addSurface(
                quad(x1, y1, z1, x1, y1, z0, x1, y0, z0, x1, y0, z1),
                1,
                0.12,
                0,
                sideHex,
                kind,
                0.05,
                SHADE4,
                UNIT_ZERO,
                false,
                true);
        }
    }

    /* ── Shadow casting ─────────────────────────────────────────────────────────────────────────────────────
     * A prop's sun shadow does NOT come from its own geometry.
     *
     * The visible surface batch is `castShadow: false` on purpose: it is the whole world, and pushing it through
     * the depth pass would cost more than everything else the renderer does. The shadow prepass traverses one
     * small positions-only batch instead, and this is how dressing joins it — a handful of triangles per prop
     * that approximate its silhouette. A canonical tree throws a real trunk-and-canopy shadow for about twenty
     * triangles rather than the five hundred faces it is actually made of, and because the terrain shadow map is
     * static (`shadowMap.autoUpdate = false`), that cost is paid once per bake and never per frame. */

    [ThreadStatic] private static P3[]? _SHADOW_FACE;
    private static P3[] SHADOW_FACE => _SHADOW_FACE ??= new P3[]
    {
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
        new P3 { x = 0, y = 0, z = 0 },
    };

    /// <summary>
    /// A low-poly shadow proxy: one tapered prism standing in for a prop's mass.
    ///
    /// Keep `sides` small (4–6) and keep the volume INSIDE the silhouette it stands for — a proxy wider than its
    /// prop throws a shadow the player cannot trace back to anything.
    /// </summary>
    public static void propShadowVolume(
        PropGeometryBuilder builder,
        double cx,
        double cz,
        double yBottom,
        double yTop,
        double rBottom,
        double rTop,
        int sides,
        double rot)
    {
        if (yTop - yBottom < 0.05 || (rBottom < 0.05 && rTop < 0.05)) return;
        List<P3> top = propRingPts(cx, cz, yTop, Math.max(0.05, rTop), sides, rot);
        List<P3> bottom = propRingPts(cx, cz, yBottom, Math.max(0.05, rBottom), sides, rot);
        builder.addShadowCaster(top);
        P3[] shadowFace = SHADOW_FACE;
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            shadowFace[0] = top[i];
            shadowFace[1] = top[j];
            shadowFace[2] = bottom[j];
            shadowFace[3] = bottom[i];
            builder.addShadowCaster(shadowFace);
        }
    }

    /// <summary>Feathered contact AO grounding a prop onto its terrace (the sun shadow does the long throw).</summary>
    public static void propShadow(
        PropGeometryBuilder builder,
        double cx,
        double cz,
        double y,
        double rx,
        double rz,
        double alpha = 0.13)
    {
        builder.addContactBlob(cx, cz, y + 0.05, rx, rz, TERRAIN_GEOMETRY_HUB_PALETTE.propInk, alpha);
    }

    /// <summary>The drawn ink contour around a faceted cap edge.</summary>
    public static void outlineCap(
        PropGeometryBuilder builder,
        double cx,
        double cz,
        double y,
        double r,
        int sides,
        double rot,
        int hex,
        double width = 1.5,
        double alpha = 0.4)
    {
        List<P3> pts = propRingPts(cx, cz, y, r, sides, rot);
        for (int i = 0; i < sides; i++)
        {
            P3 a = pts[i];
            P3 b = pts[(i + 1) % sides];
            builder.addOverlayLineFlat(y, a.x, a.z, b.x, b.z, width, hex, alpha);
        }
    }

    /// <summary>The drawn ink contour around a rectangular cap edge.</summary>
    public static void outlineBoxCap(
        PropGeometryBuilder builder,
        double x0,
        double x1,
        double z0,
        double z1,
        double y,
        int hex,
        double width = 1.5,
        double alpha = 0.4)
    {
        builder.addOverlayLineFlat(y, x0, z0, x1, z0, width, hex, alpha);
        builder.addOverlayLineFlat(y, x1, z0, x1, z1, width, hex, alpha);
        builder.addOverlayLineFlat(y, x1, z1, x0, z1, width, hex, alpha);
        builder.addOverlayLineFlat(y, x0, z1, x0, z0, width, hex, alpha);
    }

    /// <summary>A flat colour panel laid onto a prop's SOUTH face (the face the shear projection presents) — glass
    ///  panes, heraldic bands, door plaques. Pure overlay; the body underneath stays the lit volume.</summary>
    public static void propSouthPanel(
        PropGeometryBuilder builder,
        double x0,
        double x1,
        double yBottom,
        double yTop,
        double z,
        int hex,
        double alpha)
    {
        builder.addOverlay(quad(x0, yTop, z, x1, yTop, z, x1, yBottom, z, x0, yBottom, z), hex, alpha);
    }
}
