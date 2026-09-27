// Port of packages/client/src/render/environment/treeGeometry.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `TerrainTreeLike extends TreeVisual { readonly id }` is a subclass of the domain's TreeVisual (which must
//   therefore stay unsealed). C# cannot express the structural `TreeVisual & { id }`, so producers hand in a
//   TerrainTreeLike (see WorldDecorationGeometry's stump for the `{ id, ...visual }` spread).
// * The worker-local skeleton pool and the DIR scratch are [ThreadStatic] (one per compiling thread).
using System;
using System.Collections.Generic;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Domain.TreeVisualModule;
using static Fluitown.Render.WorldScale;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TreeFoliagePigment;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;
using PropTileset = Fluitown.Render.TerrainMaterialTileset;

namespace Fluitown.Render;

/// <summary>A tree effect is a phenotype plus whatever identity the world rooted it with.</summary>
/// <remarks>
/// PORT: TS accepts any `TreeVisual & { id }` structurally — the render plan's TerrainTreeDressingEffect and a
/// stump's felled specimen (TerrainFelledTreeVisual) included. C# classes cannot share that shape across the
/// Domain/Render boundary, so those records convert implicitly (a field copy; this module only reads the tree).
/// </remarks>
public class TerrainTreeLike : TreeVisual
{
    public double id;

    public TerrainTreeLike() { }

    /// <summary>`{ id, ...visual }`: every TreeVisual field of `visual` plus the rooting identity.</summary>
    public TerrainTreeLike(double id, TreeVisual visual) : base(visual)
    {
        this.id = id;
    }
}

public sealed class TerrainTreeGeometryOptions
{
    public PropTileset tileset;
    public TerrainTreeLike tree;
    public double x;
    public double z;
    public double y0;
    public double? shadowY;
    /// <summary>Retained for callers; heights are player-relative and ignore it by contract.</summary>
    public double? tileSize;
    public int canopy;
    public int? trunkTop;
    public int? trunkSide;
    /// <summary>
    /// `'full' | 'dense-grove'`.
    /// Dense authored groves keep the complete crown silhouette, hero specimens and shadow caster, but may omit
    /// sub-pixel interior twigs and a deterministic share of fully-overlapped enrichment lobes. This is a
    /// topology policy, never a placement/density policy: every authored tree remains present at its exact size.
    /// </summary>
    public string? detail;
}

/// <summary>One woody segment. Coordinates are specimen-local: the foot is the origin and Y is up.</summary>
public sealed class TreeLimb
{
    public double ax;
    public double ay;
    public double az;
    public double bx;
    public double by;
    public double bz;
    public double r0;
    public double r1;
    public int sides;
    public double rot;
    public double windA;
    public double windB;
    /// <summary>The far cap. Suppressed where the next segment covers it.</summary>
    public bool capped;
}

/// <summary>One foliage mass. `rank` 0 is structural (a terminal tip or the apex), 1 is enrichment.</summary>
public sealed class TreeCrownMass
{
    public double x;
    public double y;
    public double z;
    public double r;
    public double h;
    public double wind;
    public int rank;
    public double squash;
    /// <summary>Rounded three-ring mass, or a cheap two-ring satellite (`2 | 3`).</summary>
    public int bands;
}

public sealed class TreeSkeleton
{
    /// <summary>Exact boot-to-crown-top height in world px. The authored size ladder, honoured to the pixel.</summary>
    public double height;
    public double baseRadius;
    public double crownRadius;
    public double roots;
    public int clumpSides;
    public IReadOnlyList<TreeLimb> limbs = Array.Empty<TreeLimb>();
    public IReadOnlyList<TreeCrownMass> masses = Array.Empty<TreeCrownMass>();
    /// <summary>Woody segments above the bole. Audits and tests read it instead of re-deriving the recursion.</summary>
    public int branchSegments;
}

/// <summary>The recursion the phenotype asks for, resolved against <see cref="TreeGeometry.treeTerminalBudget"/>.</summary>
public sealed class TreeBranchPlan
{
    public int primaries;
    public int secondaries;
    public int twigs;
    public int terminals;
}

/// <summary>
/// THE tree. One generator, every world.
///
/// Every tree in the game — the Hub's pines, all ten Endless runs, the raids, the map studio — is this
/// module. A world does not get its own silhouette family; it gets a TreeVisualProfileId (form) and a
/// TreeFoliageRecipe (pigment), and those two tables are the entire per-run difference. Adding a world
/// is two data entries, never a renderer branch.
///
/// ## What makes it a tree and not a stack of cones
/// A real specimen is a swept bole carrying a countable number of limbs, each of which forks once and ends in
/// foliage. That skeleton is what the eye reads as "tree" long before it resolves a leaf:
///
///     bole ──▶ whorls of primary limbs ──▶ elbow ──▶ optional twigs ──▶ foliage clumps
///
/// The previous implementation had none of it — a trunk, three to six stacked frusta and a couple of offset
/// blobs. It could not express a bare winter bole, a parasol, a columnar cypress or a wind-flagged giant,
/// because the only axis it owned was "how many cones".
///
/// ## Two stages, and why
/// <see cref="buildTreeSkeleton"/> is pure: it lays out limbs and foliage masses in specimen-local space and then
/// normalises the whole thing so the crown apex lands exactly on the authored height. That normalisation is
/// what makes the small/medium/large ladder a *promise* instead of an average — a "small" tree is measurably
/// small, and a test can assert it without a GPU. <see cref="addTerrainTreeGeometry"/> then just translates and
/// emits. Nothing about pigment or placement leaks into the skeleton, and nothing about form leaks into the
/// emitter.
///
/// ## Scale is authored in player heights, never in tiles
/// A Hub tile is 40 px and an Endless tile is 62.5 px. A tree, like a door or a storey, cannot change its real
/// size because its carrier grid did — so height comes from TreeVisual.heightUnits × the player
/// height, and only the foot's contact patch is tile-relative.
/// </summary>
public static partial class TreeGeometry
{
    /* ══ Pigment ═══════════════════════════════════════════════════════════════════════════════════════════
     * Not here. `treeFoliagePigment` owns every rule about what colour a leaf is — the world recipe table, the
     * lit rule, the shade rule and the crown value ramp — because canopies, shrubs, moss, vines, wreaths and
     * shrine offerings all have to answer that question identically. This module grows a skeleton and emits
     * geometry; it only asks. The re-exports keep one import site for callers that already speak to trees. */

    /// <summary>Re-export of <see cref="TreeFoliagePigment.foliageBloomColorFor"/>.</summary>
    public static int foliageBloomColorFor(PropTileset tileset, string profile, double variant = 0) =>
        TreeFoliagePigment.foliageBloomColorFor(tileset, profile, variant);

    /// <summary>Re-export of <see cref="TreeFoliagePigment.foliageColorFor"/>.</summary>
    public static int foliageColorFor(PropTileset tileset, string profile, double hue, double age = 0) =>
        TreeFoliagePigment.foliageColorFor(tileset, profile, hue, age);

    /// <summary>Re-export of <see cref="TreeFoliagePigment.foliageShadeColorFor"/>.</summary>
    public static int foliageShadeColorFor(int leaf, double depth) => TreeFoliagePigment.foliageShadeColorFor(leaf, depth);

    /// <summary>Re-export of <see cref="TreeFoliagePigment.foliageShadeFor"/>.</summary>
    public static double foliageShadeFor(string profile) => TreeFoliagePigment.foliageShadeFor(profile);

    /// <summary>Re-export of <see cref="TreeFoliagePigment.foliageSideColorFor"/>.</summary>
    public static int foliageSideColorFor(string profile, int leaf) => TreeFoliagePigment.foliageSideColorFor(profile, leaf);

    /// <summary>
    /// The crown colour of one specimen: its world's recipe, moved along that recipe's own hue spread by the
    /// specimen's `canopyHue`. Two neighbouring trees in one grove therefore differ without either leaving the
    /// world's palette.
    /// </summary>
    public static int terrainTreeCanopyColor(PropTileset tileset, TerrainTreeLike tree)
    {
        return foliageColorFor(tileset, tree.profile, tree.canopyHue, tree.age);
    }

    /* ══ Skeleton — pure form, no pigment, no placement ════════════════════════════════════════════════════ */

    /// <summary>Golden angle: consecutive limbs never stack, at any limb count, without a lookup table.</summary>
    private const double PHYLLOTAXIS = 2.39996;

    /// <summary>
    /// A leaf-bearing bough: the WHOLE terminal limb, not just the point it ends at.
    ///
    /// Foliage is laid along this segment rather than balled up on its end. That is what keeps a long limb from
    /// reading as a bare stick with a pom-pom: how much leaf a bough carries is decided by how long the bough
    /// actually is, so the generator cannot produce a branch it forgot to dress.
    /// </summary>
    private sealed class TreeTip
    {
        /// <summary>Where the bough leaves its parent.</summary>
        public double fx;
        public double fy;
        public double fz;
        /// <summary>Where it ends.</summary>
        public double x;
        public double y;
        public double z;
        public double scale;
        public double wind;
        public int rank;
        public double salt;
    }

    private sealed class TreeSkeletonBuildPool
    {
        public readonly List<TreeLimb> limbs = new();
        public readonly List<TreeLimb> limbObjects = new();
        public readonly List<TreeCrownMass> masses = new();
        public readonly List<TreeCrownMass> massObjects = new();
        public readonly List<TreeTip> tips = new();
        public readonly List<TreeTip> tipObjects = new();
    }

    /// <summary>Endless terrain compilation is serialized inside each worker. Reuse its specimen-local records between
    /// trees so a dense grove does not manufacture thousands of short-lived limb/mass objects per baked tile.</summary>
    /// <remarks>PORT: one pool per compiling thread (the TS has one per worker).</remarks>
    [ThreadStatic] private static TreeSkeletonBuildPool? _TERRAIN_TREE_BUILD_POOL;
    private static TreeSkeletonBuildPool TERRAIN_TREE_BUILD_POOL => _TERRAIN_TREE_BUILD_POOL ??= new TreeSkeletonBuildPool();

    /// <summary>
    /// Hard ceiling on ENRICHMENT foliage masses per specimen.
    ///
    /// A bake tile carries roughly eight trees and its geometry is re-emitted into the shadow pass, so the crown
    /// is where a "just one more clump" decision compounds into real frame cost. Terminal masses are never
    /// dropped — a bare branch tip reads as damage — so this bounds only the shell tufts that sculpt the edge.
    /// </summary>
    public static double crownMassBudget(double size)
    {
        return Math.round(17 + size * 15);
    }

    /// <summary>
    /// How many limb TIPS one specimen may end in.
    ///
    /// The recursion multiplies — nine primaries that each split three ways and twig twice would be fifty-four
    /// crowns on one tree — so the phenotype's appetite is resolved against this cap rather than obeyed blindly.
    /// The cap is spent on the levels that read at gameplay zoom: a specimen loses its twigs before it loses the
    /// secondary branching that makes it a tree at all.
    /// </summary>
    public static double treeTerminalBudget(double size)
    {
        return Math.round(8 + size * 6);
    }

    public static TreeBranchPlan planTreeBranching(TreeVisual tree, double size)
    {
        double cap = treeTerminalBudget(size);
        int primaries = (int)Math.max(1, tree.branches);
        double share = Math.floor(cap / primaries);
        int secondaries = (int)Math.max(1, Math.min(tree.subBranches, Js.Truthy(share) ? share : 1));
        int forked = primaries * secondaries;
        // Twigs are the first thing a crowded specimen gives up: at gameplay zoom they are a few pixels, while
        // the secondary split they hang off is the silhouette itself.
        int twigs = forked * (1 + tree.forks) <= cap * 1.25 ? (int)tree.forks : 0;
        return new TreeBranchPlan
        {
            primaries = primaries,
            secondaries = secondaries,
            twigs = twigs,
            terminals = twigs > 0 ? forked * twigs : forked,
        };
    }

    /// <summary>
    /// Where along the bole the leader ends, as a fraction of skeleton height.
    ///
    /// Steep-branching worlds (conifers, cypresses, nave shafts) carry their leader almost to the top; broad
    /// worlds stop it early and let the limbs own the upper silhouette.
    /// </summary>
    private static double leaderFraction(TreeVisual tree)
    {
        return 0.5 + tree.branchPitch * 0.38;
    }

    /// <summary>Trunk half-width at parameter `t` (0 = foot, 1 = leader top).</summary>
    private static double boleRadius(double baseRadius, double t)
    {
        return baseRadius * (1 - t * 0.62);
    }

    /// <summary>Wind rises with height; the foot never moves. `t` is a fraction of skeleton height.</summary>
    private static double windAt(double t, double windMax)
    {
        double eased = Math.max(0, Math.min(1, t));
        return windMax * eased * eased * (0.35 + eased * 0.65);
    }

    /// <summary>
    /// The crown envelope: how wide foliage may reach at height `u` inside the crown band (0 = crown foot,
    /// 1 = apex).
    ///
    /// This is what turns a scatter of green balls into a SHAPE. Every mass is scaled toward the envelope, so a
    /// conic crown really tapers, a fan really flattens and a columnar crown really stays a column — while the
    /// limbs underneath keep their own independent variation.
    /// </summary>
    private static double crownEnvelope(string form, double u)
    {
        double t = Math.max(0, Math.min(1, u));
        switch (form)
        {
            case "conic":
                return 1 - t * 0.8;
            case "fan":
                // Wide and low: a parasol that thins abruptly rather than tapering evenly.
                return 1 - Math.pow(t, 2.6) * 0.72;
            case "columnar":
                return 0.66 + Math.sin(t * Math.PI) * 0.34 - t * 0.22;
            case "round":
            default:
                // A soft ellipse widest a little below centre, the way a broadleaf crown actually sits.
                return 0.46 + Math.sin(Math.pow(t, 0.86) * Math.PI) * 0.54;
        }
    }

    /// <summary>Unit-length limb direction from an azimuth and a 0..1 pitch (0 = horizontal reach, 1 = vertical climb).</summary>
    private static void limbDirection(double azimuth, double pitch, double[] @out)
    {
        double outward = 1 - pitch * 0.52;
        double upward = 0.24 + pitch * 1.4;
        double norm = Math.hypot(outward, upward);
        if (!Js.Truthy(norm)) norm = 1;
        @out[0] = (Math.cos(azimuth) * outward) / norm;
        @out[1] = upward / norm;
        @out[2] = (Math.sin(azimuth) * outward) / norm;
    }

    [ThreadStatic] private static double[]? _DIR;
    private static double[] DIR => _DIR ??= new double[] { 0, 0, 0 };

    /// <summary>Where a child limb leaves its parent, as a fraction of the parent's length.</summary>
    private static readonly double[] CHILD_ALONG = { 0.44, 0.72, 0.9 };

    /// <summary>
    /// Grow one specimen.
    ///
    /// The layout runs against a nominal height of 1, so every proportion here is a pure ratio and the final
    /// uniform scale is the only place the authored height enters. That ordering is deliberate: a limb that
    /// overshoots simply makes the whole specimen slightly stockier, instead of silently breaking the size ladder
    /// an additive "trunk + crown" budget would.
    ///
    /// ## The recursion
    /// ```
    ///   bole  →  primaries (branches, spread over whorls)  →  secondaries (subBranches)  →  twigs (forks)
    /// ```
    /// Secondaries leave their parent PART-WAY ALONG it, not all at its tip: a limb that only divides at its end
    /// reads as a fork, while one that sheds branches along its length reads as a tree. Foliage hangs on terminal
    /// tips, so the inner crown stays open and the branching is actually visible — the single biggest reason the
    /// old stacked-cone crown read as a shrub on a stick.
    /// </summary>
    private static TreeSkeleton buildTreeSkeletonInternal(
        TerrainTreeLike tree,
        double height,
        TreeSkeletonBuildPool? pool = null)
    {
        double size = height / (PLAYER_HEIGHT_PX * TREE_REFERENCE_UNITS);
        List<TreeLimb> limbs = pool?.limbs ?? new List<TreeLimb>();
        List<TreeCrownMass> masses = pool?.masses ?? new List<TreeCrownMass>();
        List<TreeTip> tips = pool?.tips ?? new List<TreeTip>();
        limbs.Clear();
        masses.Clear();
        tips.Clear();
        int limbObjectCount = 0;
        int massObjectCount = 0;
        int tipObjectCount = 0;
        void pushLimb(
            double ax,
            double ay,
            double az,
            double bx,
            double by,
            double bz,
            double r0,
            double r1,
            int sides,
            double rot,
            double windA,
            double windB,
            bool capped)
        {
            TreeLimb limb = pool != null && limbObjectCount < pool.limbObjects.Count
                ? pool.limbObjects[limbObjectCount]
                : new TreeLimb();
            limb.ax = ax;
            limb.ay = ay;
            limb.az = az;
            limb.bx = bx;
            limb.by = by;
            limb.bz = bz;
            limb.r0 = r0;
            limb.r1 = r1;
            limb.sides = sides;
            limb.rot = rot;
            limb.windA = windA;
            limb.windB = windB;
            limb.capped = capped;
            if (pool != null && limbObjectCount == pool.limbObjects.Count) pool.limbObjects.push(limb);
            limbObjectCount++;
            limbs.push(limb);
        }
        void pushMass(
            double x,
            double y,
            double z,
            double r,
            double h,
            double wind,
            int rank,
            double squash,
            int bands)
        {
            TreeCrownMass mass = pool != null && massObjectCount < pool.massObjects.Count
                ? pool.massObjects[massObjectCount]
                : new TreeCrownMass();
            mass.x = x;
            mass.y = y;
            mass.z = z;
            mass.r = r;
            mass.h = h;
            mass.wind = wind;
            mass.rank = rank;
            mass.squash = squash;
            mass.bands = bands;
            if (pool != null && massObjectCount == pool.massObjects.Count) pool.massObjects.push(mass);
            massObjectCount++;
            masses.push(mass);
        }
        void pushTip(
            double fx,
            double fy,
            double fz,
            double x,
            double y,
            double z,
            double scale,
            double wind,
            int rank,
            double salt)
        {
            TreeTip tip = pool != null && tipObjectCount < pool.tipObjects.Count
                ? pool.tipObjects[tipObjectCount]
                : new TreeTip();
            tip.fx = fx;
            tip.fy = fy;
            tip.fz = fz;
            tip.x = x;
            tip.y = y;
            tip.z = z;
            tip.scale = scale;
            tip.wind = wind;
            tip.rank = rank;
            tip.salt = salt;
            if (pool != null && tipObjectCount == pool.tipObjects.Count) pool.tipObjects.push(tip);
            tipObjectCount++;
            tips.push(tip);
        }
        double budget = crownMassBudget(size);
        int enriched = 0;

        double crownR = tree.crownRadius;
        double leaderT = leaderFraction(tree);
        // Chunky by doctrine: a bole thin enough to be "realistic" projects to two or three pixels at gameplay
        // zoom and the tree loses its trunk entirely. Posts are fat shafts, never needles.
        double baseRadius = 0.055 + tree.age * 0.028;
        double windMax = (2.4 + tree.crownDensity * 1.5) * size;
        double leanDir = tree.phase * PROP_TAU;
        double leanX = Math.cos(leanDir);
        double leanZ = Math.sin(leanDir);
        double lean = tree.lean * 0.13;
        double sweep = tree.trunkForm == "sweep" ? 0.062 : tree.trunkForm == "forked" ? 0.03 : 0.012;
        int boleSides = size > 1.15 ? 6 : 5;
        int branchSides = size > 1.05 ? 5 : 4;
        int clumpSides = size > 1.35 ? 7 : size > 0.95 ? 6 : 5;

        double offsetX(double t) =>
            lean * Math.pow(t, 1.35) * leanX + Math.sin(t * Math.PI) * sweep * leanZ;
        double offsetZ(double t) =>
            lean * Math.pow(t, 1.35) * leanZ - Math.sin(t * Math.PI) * sweep * leanX * 0.42;

        TreeBranchPlan plan = planTreeBranching(tree, size);
        double clumpSquash = tree.crownForm == "columnar" ? 1.26 : 1;
        double clumpAspect = tree.crownForm == "columnar" ? 1.8 : tree.crownForm == "fan" ? 1 : 1.18;
        /*
         * Foliage size is the single most important number in this file, and it cannot be a constant.
         *
         * A lobe wider than the gap between neighbouring limb tips merges with them into ONE silhouette, the
         * branches vanish inside it, and the specimen reads as a lollipop no matter how good its skeleton is.
         * How wide that gap is depends on limb count, reach, pitch and crown form together — so the radius is
         * measured from the tips the recursion ACTUALLY produced, not guessed from the profile. That is what
         * makes the same generator hold up for a narrow cypress and a sprawling reef tree.
         */
        double clumpRadiusFor(List<TreeTip> tipsIn)
        {
            double radial = 0;
            double lowest = double.PositiveInfinity;
            double highest = double.NegativeInfinity;
            foreach (TreeTip tip in tipsIn)
            {
                radial += Math.hypot(tip.x, tip.z);
                lowest = Math.min(lowest, tip.y);
                highest = Math.max(highest, tip.y);
            }
            int count = Math.max(5, tipsIn.Count);
            radial = tipsIn.Count > 0 ? radial / tipsIn.Count : crownR * 0.5;
            // A broad crown distributes its tips around a ring; a columnar one stacks them up a shaft. Taking the
            // larger of the two spreads is what stops a narrow conifer from being given pin-head foliage.
            double around = PROP_TAU * radial;
            double along = tipsIn.Count > 1 ? (highest - lowest) * 1.7 : 0;
            double spacing = Math.max(around, along, crownR * 1.4) / count;
            return Math.min(
                crownR * 0.4,
                Math.max(crownR * 0.15, spacing * 0.6 * (0.9 + tree.crownDensity * 0.26)));
        }

        // The crown band the envelope is measured across: from the lowest limb anchor to the apex.
        double crownBase = Math.min(leaderT * 0.9, tree.branchDrop);
        double crownSpan = Math.max(0.12, 1 - crownBase);

        // A limb tip the recursion produced. Foliage is sized from these, then attached to them.
        double clumpRadius = crownR * 0.2;

        // Attach a foliage mass, scaled toward the crown envelope at its own height.
        void attach(
            double x,
            double y,
            double z,
            double scale,
            double wind,
            int rank,
            double salt)
        {
            if (rank > 0 && enriched >= budget) return;
            double h = propHash(tree.id * 4703 + salt * 131);
            double envelope = crownEnvelope(tree.crownForm, (y - crownBase) / crownSpan);
            // Phototropism: the lit side of a crown really is fuller, and `asymmetry` picks which side that is.
            double lit = 1 + tree.asymmetry * ((x * leanX + z * leanZ) / Math.max(1e-3, crownR)) * 0.16;
            double r = Math.max(0, clumpRadius * scale * (0.66 + envelope * 0.56) * (0.82 + h * 0.42) * lit);
            if (r <= 1e-4) return;
            if (rank > 0) enriched++;
            // Sit the mass a little ABOVE its tip: the last stretch of limb stays visible underneath, which is what
            // tells the eye the foliage is carried by a branch instead of floating.
            pushMass(
                x,
                y - r * 0.2,
                z,
                r,
                r * clumpAspect * (0.86 + h * 0.3),
                wind,
                rank,
                clumpSquash,
                rank == 0 ? 3 : 2);
        }

        /*
         * Grow one limb and everything that comes off it.
         *
         * `depth` counts down: 2 = a primary off the bole, 1 = a secondary, 0 = a twig that only carries leaves.
         * Each limb is drawn as several segments with progressive upward bend (gravitropism), which is what makes
         * a branch read as grown rather than pinned on.
         */
        void grow(
            double ax,
            double ay,
            double az,
            double azimuth,
            double pitch,
            double length,
            double radius,
            int depth,
            double salt)
        {
            int segments = depth >= 2 ? 3 : 2;
            int sides = depth >= 2 ? branchSides : 4;
            int children = depth == 2 ? plan.secondaries : depth == 1 ? plan.twigs : 0;

            double px = ax;
            double py = ay;
            double pz = az;
            double carriedPitch = pitch;
            double[] dir = DIR;
            for (int seg = 0; seg < segments; seg++)
            {
                double t0 = (double)seg / segments;
                double t1 = (double)(seg + 1) / segments;
                // Gravitropism: a limb turns gently upward along its length, with a per-segment wobble that keeps it
                // off a ruler line. GENTLY is the operative word — a strong turn curls every limb parallel to the
                // trunk within three segments, which stacks the whole crown vertically and hides the branching.
                double bend = propHash(tree.id * 811 + salt * 47 + seg * 13);
                carriedPitch = Math.min(0.96, carriedPitch + 0.04 + bend * 0.07);
                double segAzimuth = azimuth + (bend - 0.5) * 0.3;
                limbDirection(segAzimuth, carriedPitch, dir);
                double step = length * (t1 - t0) * (0.92 + bend * 0.2);
                double nx = px + dir[0] * step;
                double ny = py + dir[1] * step;
                double nz = pz + dir[2] * step;
                pushLimb(
                    px,
                    py,
                    pz,
                    nx,
                    ny,
                    nz,
                    radius * (1 - t0 * 0.66),
                    radius * (1 - t1 * 0.66),
                    sides,
                    segAzimuth,
                    windAt(py, windMax) * (0.6 + t0 * 0.4),
                    windAt(ny, windMax) * (0.6 + t1 * 0.4),
                    seg == segments - 1);
                px = nx;
                py = ny;
                pz = nz;
            }

            if (children <= 0)
            {
                pushTip(ax, ay, az, px, py, pz, 1, windAt(py, windMax), 0, salt);
                return;
            }

            for (int c = 0; c < children; c++)
            {
                double h = propHash(tree.id * 1279 + salt * 97 + c * 29);
                double alongC = CHILD_ALONG[Math.min(CHILD_ALONG.Length - 1, c)];
                int side = c % 2 == 0 ? 1 : -1;
                double spread = (0.5 + h * 0.66) * side * (depth == 2 ? 1 : 0.76);
                grow(
                    ax + (px - ax) * alongC,
                    ay + (py - ay) * alongC,
                    az + (pz - az) * alongC,
                    azimuth + spread,
                    Math.min(0.96, carriedPitch * (0.84 + h * 0.28) + 0.06),
                    length * (depth == 2 ? 0.52 : 0.44) * (0.74 + h * 0.54),
                    radius * (0.52 + h * 0.16) * (1 - alongC * 0.28),
                    depth - 1,
                    salt * 3 + c + 1);
            }
            // A secondary that carries twigs still ends in a small tuft of its own; a PRIMARY's tip sits deep
            // inside the crown where nothing would ever see it, so it stays bare and cheap.
            // EVERY limb's outer end carries leaf, at every depth.
            //
            // Children leave at `CHILD_ALONG`, so the stretch from the last of them to the limb's own tip belongs to
            // nobody. Left undressed it is exactly the defect the owner flagged: a very long branch with almost no
            // foliage on it. Dressing that stretch as its own bough makes a bare stick structurally impossible.
            double lastAlong = CHILD_ALONG[Math.min(CHILD_ALONG.Length - 1, children - 1)];
            pushTip(
                ax + (px - ax) * lastAlong,
                ay + (py - ay) * lastAlong,
                az + (pz - az) * lastAlong,
                px,
                py,
                pz,
                depth == 2 ? 0.82 : 0.66,
                windAt(py, windMax),
                depth == 2 ? 0 : 1,
                salt * 7 + 5);
        }

        // ── The bole. Three segments, so lean and sweep are real curvature rather than a tilted stick. ──
        const int BOLE_SEGMENTS = 3;
        double bpx = 0;
        double bpy = 0;
        double bpz = 0;
        for (int seg = 1; seg <= BOLE_SEGMENTS; seg++)
        {
            double t = (double)seg / BOLE_SEGMENTS;
            double prev = (double)(seg - 1) / BOLE_SEGMENTS;
            double nx = offsetX(t);
            double ny = leaderT * t;
            double nz = offsetZ(t);
            pushLimb(
                bpx,
                bpy,
                bpz,
                nx,
                ny,
                nz,
                boleRadius(baseRadius, prev),
                boleRadius(baseRadius, t),
                boleSides,
                leanDir,
                windAt(prev * leaderT, windMax),
                windAt(t * leaderT, windMax),
                false);
            bpx = nx;
            bpy = ny;
            bpz = nz;
        }
        double leaderX = bpx;
        double leaderY = bpy;
        double leaderZ = bpz;
        int boleSegments = limbs.Count;

        // ── Primary limbs. `branches`, `whorls`, `subBranches` and `branchPitch` are the whole character. ──
        double whorls = Math.max(1, Math.min(tree.whorls, tree.branches));
        for (int i = 0; i < tree.branches; i++)
        {
            double band = whorls == 1 ? 0.5 : (i % whorls) / (whorls - 1);
            // `branchDrop` is a fraction OF THE LEADER, not of total height: a profile that wants a bare bole gets
            // one at every size, and the whorls always have room to spread instead of collapsing onto one anchor.
            double anchorT = leaderT * (tree.branchDrop + (0.98 - tree.branchDrop) * (0.06 + band * 0.86));
            double jitter = propHash(tree.id * 733 + i * 61);
            double azimuth = leanDir + i * PHYLLOTAXIS + (jitter - 0.5) * 0.8;
            // Lobes: a low-frequency function of azimuth makes the crown's plan outline irregular, so a stand of
            // trees never reads as a field of circles.
            double lobe = 0.82 + 0.32 * Math.abs(Math.cos(azimuth * 1.5 + tree.asymmetry));
            grow(
                offsetX(anchorT / leaderT),
                anchorT,
                offsetZ(anchorT / leaderT),
                azimuth,
                Math.min(0.96, tree.branchPitch * (0.74 + band * 0.5)),
                // Bounded spread. The old bands multiplied out to a 2.2× range between the shortest and longest limb
                // on one specimen, and the longest always read as an outlier reaching out of its own crown.
                Math.min(
                    crownR * 1.32,
                    crownR *
                        tree.branchSpread *
                        (0.82 + (1 - band) * 0.32) *
                        (0.82 + jitter * 0.34) *
                        lobe *
                        (1 + tree.asymmetry * Math.cos(azimuth - leanDir) * 0.16)),
                boleRadius(baseRadius, anchorT / leaderT) * (0.54 + (1 - band) * 0.18),
                2,
                i + 1);
        }

        // ── A forked bole grows a genuine second leader carrying its own share of the crown. ──
        if (tree.trunkForm == "forked")
        {
            const double splitT = 0.46;
            grow(
                offsetX(splitT),
                leaderT * splitT,
                offsetZ(splitT),
                leanDir + Math.PI * 0.5 + tree.asymmetry * 0.6,
                Math.min(0.95, 0.62 + tree.branchPitch * 0.34),
                crownR * (0.62 + tree.branchSpread * 0.3),
                boleRadius(baseRadius, splitT) * 0.72,
                2,
                991);
        }

        // ── Foliage. Sized from the tips the recursion produced, then hung on them. ──
        clumpRadius = clumpRadiusFor(tips.filter((tip) => tip.rank == 0));
        /*
         * Dress each bough along its LENGTH.
         *
         * One clump per limb tip is only right when the limb is about as long as the clump is wide. A profile that
         * reaches far — a reef sprawl, a blossom parasol — then produces exactly what the owner flagged: very long
         * branches carrying almost no leaf. Deriving the clump count from `length / clumpRadius` makes that
         * impossible by construction, at any spread, in any world. Foliage starts partway out so the inner crown
         * stays open and the branching still reads.
         */
        double boughCount(TreeTip tip) =>
            Math.max(
                1,
                Math.min(
                    4,
                    Math.round(
                        Math.hypot(tip.x - tip.fx, tip.y - tip.fy, tip.z - tip.fz) /
                            Math.max(1e-4, clumpRadius * 1.45))));
        void dressBough(TreeTip tip, double i, double count)
        {
            double t = count == 1 ? 1 : 0.36 + (0.64 * i) / (count - 1);
            attach(
                tip.fx + (tip.x - tip.fx) * t,
                tip.fy + (tip.y - tip.fy) * t,
                tip.fz + (tip.z - tip.fz) * t,
                // The outermost mass is the biggest: a bough thickens with leaf toward its end, not at its shoulder.
                tip.scale * (0.62 + 0.44 * t),
                tip.wind * (0.7 + 0.3 * t),
                i == count - 1 ? tip.rank : 1,
                tip.salt * 13 + i * 7);
        }
        // Every bough gets its outermost mass FIRST — a bare tip reads as damage, so it is never the thing that
        // loses to the budget. The inner dressing then fills the remaining allowance evenly across all boughs.
        foreach (TreeTip tip in tips) dressBough(tip, boughCount(tip) - 1, boughCount(tip));
        for (int ring = 2; ring >= 0; ring--)
        {
            foreach (TreeTip tip in tips)
            {
                double count = boughCount(tip);
                double i = count - 1 - ring;
                if (i >= 0 && masses.Count < budget) dressBough(tip, i, count);
            }
        }

        // The apex closes the leader. Every crown form has one; only how tightly it sits differs.
        attach(
            leaderX,
            leaderY + (tree.crownForm == "columnar" ? 0.14 : tree.crownForm == "conic" ? 0.1 : 0.04),
            leaderZ,
            tree.crownForm == "fan" ? 0.84 : 1.18,
            windMax,
            0,
            7);

        // ── Crown shell.
        //
        // Terminal masses alone leave a ring of pom-poms with sky between them. These fill the envelope BETWEEN
        // the tips: sampled on the crown surface, sized by the same envelope function, and always inside the
        // outermost terminal so they read as one sculpted canopy rather than as extra floating blobs. This is the
        // difference between "green balls on sticks" and a crown.
        double shellCount = Math.round(1 + tree.crownDensity * 2 + size * 1.5);
        double reach = 0;
        foreach (TreeCrownMass mass in masses) reach = Math.max(reach, Math.hypot(mass.x, mass.z) + mass.r * 0.5);
        for (int i = 0; i < shellCount; i++)
        {
            double h = propHash(tree.id * 6151 + i * 173);
            double g = propHash(tree.id * 6151 + i * 173 + 7);
            // Sample from the crown's upper body downward: the underside is hidden by the canopy above it.
            double u = 0.3 + ((i + h) / shellCount) * 0.66;
            double azimuth = leanDir + i * PHYLLOTAXIS + (g - 0.5) * 0.5;
            // The OUTER shell only. Filling the crown's interior would hide exactly the branching this whole
            // rewrite exists to expose; the open middle is what makes a crown read as foliage on a tree.
            double radial = reach * crownEnvelope(tree.crownForm, u) * (0.62 + g * 0.3);
            attach(
                leaderX + Math.cos(azimuth) * radial,
                crownBase + crownSpan * u,
                leaderZ + Math.sin(azimuth) * radial,
                0.74 + h * 0.28,
                windAt(crownBase + crownSpan * u, windMax),
                1,
                i * 29 + 101);
        }

        // ── Normalise to the authored height. This is what makes "small / medium / large" measurable. ──
        double top = 0;
        foreach (TreeLimb limb in limbs) top = Math.max(top, limb.by + limb.r1);
        foreach (TreeCrownMass mass in masses) top = Math.max(top, mass.y + mass.h);
        double k = height / Math.max(1e-3, top);
        foreach (TreeLimb limb in limbs)
        {
            limb.ax *= k;
            limb.ay *= k;
            limb.az *= k;
            limb.bx *= k;
            limb.by *= k;
            limb.bz *= k;
            limb.r0 *= k;
            limb.r1 *= k;
        }
        foreach (TreeCrownMass mass in masses)
        {
            mass.x *= k;
            mass.y *= k;
            mass.z *= k;
            mass.r *= k;
            mass.h *= k;
        }

        return new TreeSkeleton
        {
            height = height,
            baseRadius = baseRadius * k,
            crownRadius = crownR * k,
            roots = 2 + Math.round(tree.age * 3),
            clumpSides = clumpSides,
            limbs = limbs,
            masses = masses,
            branchSegments = limbs.Count - boleSegments,
        };
    }

    /// <summary>Pure inspection form: callers own the returned arrays and may retain them indefinitely.</summary>
    public static TreeSkeleton buildTreeSkeleton(TerrainTreeLike tree, double height)
    {
        return buildTreeSkeletonInternal(tree, height);
    }

    /// <summary>The authored boot-to-crown-top height of one specimen, in world px.</summary>
    public static double treeHeightPx(TreeVisual tree)
    {
        double units = tree.heightUnits > 0 ? tree.heightUnits : tree.scale * TREE_REFERENCE_UNITS;
        return PLAYER_HEIGHT_PX * units;
    }

    /* ══ Emission ══════════════════════════════════════════════════════════════════════════════════════════ */

    /// <summary>
    /// The specimen's shadow, in about twenty triangles.
    ///
    /// A tree is several hundred faces; pushing that through the depth pass for every tree in view would make the
    /// shadow map cost more than the world. The eye only reads two things from a tree's shadow anyway — a bole
    /// line and a canopy mass — so the proxy is exactly those two prisms, measured from the crown the skeleton
    /// actually grew. Both stay INSIDE the real silhouette, so the shadow can always be traced back to the tree
    /// that threw it.
    /// </summary>
    private static void addTreeShadowCaster(
        PropGeometryBuilder builder,
        TerrainTreeLike tree,
        TreeSkeleton skeleton,
        double cx,
        double y0,
        double cz)
    {
        double sumX = 0;
        double sumZ = 0;
        double weight = 0;
        double crownLow = double.PositiveInfinity;
        double crownHigh = 0;
        double reach = 0;
        foreach (TreeCrownMass mass in skeleton.masses)
        {
            double w = mass.r * mass.r;
            sumX += mass.x * w;
            sumZ += mass.z * w;
            weight += w;
            crownLow = Math.min(crownLow, mass.y);
            crownHigh = Math.max(crownHigh, mass.y + mass.h);
            reach = Math.max(reach, Math.hypot(mass.x, mass.z) + mass.r);
        }
        if (weight <= 0) return;
        double centreX = cx + sumX / weight;
        double centreZ = cz + sumZ / weight;

        // The bole, from the foot to where the crown starts carrying the silhouette.
        double boleTop = y0 + Math.max(skeleton.height * 0.12, crownLow * 0.9);
        propShadowVolume(
            builder,
            cx,
            cz,
            y0,
            boleTop,
            skeleton.baseRadius * 1.1,
            skeleton.baseRadius * 0.7,
            4,
            tree.phase * PROP_TAU);
        // The canopy — a SLAB under the crown, never a prism around it.
        //
        // This proxy used to span `crownLow .. crownHigh` at up to `reach * 0.82`, i.e. it enclosed the very leaves
        // it stands for. The visible surface batch is `receiveShadow: true` and this batch is `castShadow: true`,
        // so every specimen was shadowing ITSELF: measured, 82–96 % of crown pixels received no key light at all.
        // That is what flattened every canopy into one dark value — the authored sun-side pigment
        // (`propSunFacing` + `foliageRampValue`) was still there, it was just being multiplied by an almost-zero
        // sun term everywhere, so a crown's west and east halves came out 3.9/255 apart where 12 was asked for.
        //
        // A shadow proxy exists to put the crown's silhouette on the GROUND. Sitting just under the crown does
        // exactly that — the ground shadow keeps the canopy's full width and its offset under the low sun — while
        // being physically incapable of shadowing anything above it, which is the whole crown. The trunk below
        // stays shaded, as a trunk under a canopy should be.
        //
        // A PARTIAL skirt was measured and is worse than either extreme, which is why this is a slab and not a
        // fraction of the crown: covering the bottom 35 % re-crushes the widest part of the mass, and west-minus-east
        // fell to 3.0/255 — below even the fully enclosed case. The sun-side pigment only has a voice when nothing
        // is multiplying it by zero. Measured on hub#7: mean/ground 0.334 → 0.515, W−E 3.9 → 11.6, widest bucket
        // 46 % → 28 %. `0.82` still keeps the width inside the outermost leaf, and the slab dips slightly below
        // `crownLow` so the depth pass has volume to rasterise rather than a degenerate plane.
        double canopyBase = y0 + crownLow;
        double crownDepth = Math.max(0.12, crownHigh - crownLow);
        propShadowVolume(
            builder,
            centreX,
            centreZ,
            canopyBase - crownDepth * 0.12,
            canopyBase,
            reach * 0.6,
            reach * 0.82,
            6,
            tree.phase * PROP_TAU + 0.4);
    }

    public static void addTerrainTreeGeometry(
        PropGeometryBuilder builder,
        TerrainTreeGeometryOptions opts)
    {
        // Fluitown comic look (not in the original): the tree grows in the Godot vegetation layer — see FluitownVegetation.
        if (FluitownVegetation.recordTree(builder, opts)) return;
        PropTileset tileset = opts.tileset;
        TerrainTreeLike tree = opts.tree;
        double cx = opts.x;
        double cz = opts.z;
        double y0 = opts.y0;
        double shadowY = opts.shadowY ?? y0;

        double height = treeHeightPx(tree);
        double size = height / (PLAYER_HEIGHT_PX * TREE_REFERENCE_UNITS);
        // Terrain emission consumes one skeleton completely before advancing to the next tree. Reusing this worker-
        // local object graph removes allocation/GC spikes while leaving the public retained-skeleton API pure.
        TreeSkeleton skeleton = buildTreeSkeletonInternal(tree, height, TERRAIN_TREE_BUILD_POOL);
        // A dense Hub grove can put hundreds of specimens on screen at once. Preserve a deterministic set of fully
        // detailed trees so neighbouring crowns never acquire one repeated topology, while simplifying only the
        // ordinary specimens between them. Hero trees are landmarks and therefore always retain the full graph.
        bool denseGrove =
            opts.detail == "dense-grove" && !tree.hero && propHash(tree.id * 7193 + 0x2d31) < 0.92;

        int canopy = opts.canopy;
        TreeFoliageRecipe recipe = foliageRecipe(tree.profile);
        double leafShade = foliageShadeFor(tree.profile);
        /*
         * Bark, not charcoal.
         *
         * A limb is a thin shape seen against bright ground, so any pull toward ink reads as pure black and the
         * whole skeleton turns into a burnt scribble — which defeats the point of having a skeleton at all. Wood
         * therefore starts from the bridges' SUNLIT timber (the world's existing wood truth), keeps a real lit
         * cap, and its shaded side stays a warm mid-tone rather than a silhouette.
         */
        int bark = mix(tileset.bridge.lip, tileset.bridge.body, 0.34 + tree.barkHue * 0.3);
        int trunkTop = opts.trunkTop ?? mix(bark, tileset.terrain.wallLit, 0.16);
        int trunkSide =
            opts.trunkSide ?? mix(bark, tileset.bridge.bodyShadow, 0.4 + tree.barkHue * 0.2);

        /*
         * Grounding, in two parts.
         *
         * The contact patch is AMBIENT OCCLUSION at the foot — it stays tight around the roots now that the sun
         * shadow is real, instead of pretending to be a canopy shadow painted flat on the floor.
         */
        propShadow(
            builder,
            cx,
            cz,
            shadowY,
            skeleton.baseRadius * 2.8 + skeleton.crownRadius * 0.16,
            skeleton.baseRadius * 1.5 + skeleton.crownRadius * 0.08,
            (0.1 + tree.crownDensity * 0.05) * tree.alpha);
        // Fluitown comic look (not in the original): the crown's own puffs throw its shadow — see FluitownSoftFoliage.
        if (FluitownSoftFoliage.Enabled) FluitownSoftFoliage.addSoftTreeShadowCaster(builder, tree, skeleton, cx, y0, cz);
        else addTreeShadowCaster(builder, tree, skeleton, cx, y0, cz);

        // ── Root buttresses. Old specimens flare; saplings do not. ──
        double leanDir = tree.phase * PROP_TAU;
        for (int i = 0; i < skeleton.roots; i++)
        {
            double a = leanDir + (i / skeleton.roots) * PROP_TAU + tree.asymmetry * 0.3;
            double reach = skeleton.baseRadius * (1.05 + tree.age * 0.85);
            propFrustum(
                builder,
                cx + Math.cos(a) * reach * 0.62,
                cz + Math.sin(a) * reach * 0.62,
                y0,
                y0 + height * (0.016 + tree.age * 0.022),
                skeleton.baseRadius * (0.62 + tree.age * 0.3),
                skeleton.baseRadius * 0.22,
                4,
                a,
                trunkTop,
                trunkSide,
                0.7);
        }

        for (int limbIndex = 0; limbIndex < skeleton.limbs.Count; limbIndex++)
        {
            TreeLimb limb = skeleton.limbs[limbIndex];
            // The first three records are the visible bole. Below 20% of its base radius, later records are terminal
            // twigs sitting inside an opaque foliage lobe; at gameplay zoom they are sub-pixel and cannot affect the
            // silhouette. Keeping thicker primaries/secondaries preserves the readable grown-tree skeleton.
            if (denseGrove && limbIndex >= 3 && limb.r0 < skeleton.baseRadius * 0.2) continue;
            propLimb(
                builder,
                cx + limb.ax,
                y0 + limb.ay,
                cz + limb.az,
                cx + limb.bx,
                y0 + limb.by,
                cz + limb.bz,
                limb.r0,
                limb.r1,
                limb.sides,
                limb.rot,
                trunkTop,
                trunkSide,
                // Barely any foot shading: a limb is a few pixels wide, so a gradient across it only darkens it.
                0.9,
                limb.windA,
                limb.windB,
                limb.capped);
        }

        // ── Foliage. One primitive, one pigment family, per-mass hue drift and per-mass rotation. ──
        int outlined = 0;
        int outlineBudget = size > 1.35 ? 3 : size > 0.95 ? 2 : 1;
        // Only the crown's biggest masses wear the drawn contour. Outlining every tuft turns the silhouette into
        // a scribble at gameplay zoom and costs an overlay quad per side.
        double outlineFloor = 0;
        foreach (TreeCrownMass mass in skeleton.masses)
            if (mass.rank == 0) outlineFloor = Math.max(outlineFloor, mass.r);
        outlineFloor *= 0.78;
        // An enrichment lobe can occasionally be the authored crown's highest point. Keep that exact outer sample
        // even in a dense grove; only lobes strictly inside the measured envelope are eligible for omission.
        double crownTop = double.NegativeInfinity;
        double crownReach = 0;
        // The crown band the value ramp is measured across. It is the crown's OWN extent, not the specimen's, so a
        // squat parasol and a tall conifer spend the same ramp over their own foliage.
        double crownFoot = double.PositiveInfinity;
        for (int i = 0; i < skeleton.masses.Count; i++)
        {
            TreeCrownMass mass = skeleton.masses[i];
            double h = propHash(tree.id * 2311 + i * 71);
            crownTop = Math.max(crownTop, mass.y + mass.h * (0.9 + h * 0.2));
            crownFoot = Math.min(crownFoot, mass.y);
            crownReach = Math.max(crownReach, Math.hypot(mass.x, mass.z) + mass.r * (0.9 + h * 0.22));
        }
        double crownSpan = Math.max(1e-3, crownTop - crownFoot);
        for (int i = 0; i < skeleton.masses.Count; i++)
        {
            TreeCrownMass mass = skeleton.masses[i];
            double h = propHash(tree.id * 2311 + i * 71);
            double clumpH = mass.h * (0.9 + h * 0.2);
            bool ownsCrownTop = mass.y + clumpH >= crownTop - 1e-6;
            double massReach = Math.hypot(mass.x, mass.z) + mass.r * (0.9 + h * 0.22);
            bool ownsOuterCrown = massReach >= crownReach * 0.95;
            // Rank zero owns every terminal tip and the crown apex. Rank one is explicitly interior enrichment; in a
            // dense grove most of it is depth-occluded by adjacent lobes. A stable blue-noise-like subset retains the
            // broken organic fill without submitting every overlapped shell to the vertex and fragment stages. The
            // measured crown apex is protected even when an enrichment lobe happens to own it.
            if (
                denseGrove &&
                mass.rank > 0 &&
                !ownsCrownTop &&
                !ownsOuterCrown &&
                propHash(tree.id * 3571 + i * 109 + 0x71d) < 0.58
            )
                continue;
            /*
             * Where this lobe sits on the crown's value ramp.
             *
             * Not a hue drift and not a pull toward chalk or ink: two neutral colours are exactly what a leaf may
             * never be lerped toward, because both drag the three channels together and a crushed or washed leaf
             * stops reading as a leaf. foliageRampValue answers on two axes that are free at bake time —
             * how far along the key's azimuth the mass sits, and how high it sits in the crown — and
             * foliageRampColorFor resolves that one number against the world's own leaf: brighter and MORE
             * saturated upward, darker and MORE saturated downward. This is what turns a field of green discs into
             * a volume with a sun side, and it is the same rule the undergrowth answers with.
             */
            double rampValue = foliageRampValue(
                mass.rank == 0,
                propSunFacing(mass.x, mass.z, crownReach),
                (mass.y + clumpH * 0.5 - crownFoot) / crownSpan,
                (h - 0.5) * recipe.spread);
            int tint = foliageRampColorFor(canopy, rampValue);
            // The lobe's own turned-away facets: the same leaf again, deepened against ITSELF by the world's shade.
            int massSide = foliageShadeColorFor(tint, leafShade);
            double rot = leanDir + i * 0.53 + h;
            double r = mass.r * (0.9 + h * 0.22);
            // Terminal masses on the measured crown envelope retain the full profile-specific faceting. Interior
            // masses sit behind that contour as overlapping lobes; fewer sides and bands there stay below a gameplay
            // pixel while multiplying across every grove and its shadow pass. Pigment and animation remain unchanged.
            bool interiorDenseMass = denseGrove && !ownsCrownTop && !ownsOuterCrown;
            int massSides =
                mass.rank == 0
                    ? interiorDenseMass
                        ? Math.max(5, skeleton.clumpSides - 1)
                        : skeleton.clumpSides
                    : interiorDenseMass
                        ? 4
                        : Math.max(4, skeleton.clumpSides - 2);
            int massBands = interiorDenseMass ? 2 : mass.bands;
            propClump(
                builder,
                cx + mass.x,
                y0 + mass.y,
                cz + mass.z,
                r,
                clumpH,
                massSides,
                rot,
                tint,
                massSide,
                mass.wind,
                mass.squash,
                massBands);
            // The comic look's world ink draws the crown's contour itself (Fluitown, not in the original).
            if (!FluitownSoftFoliage.Enabled && mass.rank == 0 && mass.r >= outlineFloor && outlined < outlineBudget)
            {
                outlined++;
                outlineCap(
                    builder,
                    cx + mass.x,
                    cz + mass.z,
                    y0 + mass.y + clumpH + 0.06,
                    r * 0.42,
                    skeleton.clumpSides,
                    rot + 0.31,
                    tileset.terrain.wallLine,
                    0.9 + size * 0.2,
                    0.24 * tree.alpha);
            }
            // Blossom / fruit / lantern punctuation. Bounded: it decorates a crown, it never becomes the crown.
            if (tree.bloom > 0.5 && mass.rank == 0 && h > 0.62 - tree.bloom * 0.24)
            {
                int bloomHex = foliageBloomColorFor(tileset, tree.profile, h);
                if (FluitownSoftFoliage.Enabled) bloomHex = FluitownSoftFoliage.softBloomHex(bloomHex, tint, tileset);
                propClump(
                    builder,
                    cx + mass.x + Math.cos(rot) * r * 0.68,
                    y0 + mass.y + clumpH * 0.42,
                    cz + mass.z + Math.sin(rot) * r * 0.68,
                    r * (0.2 + tree.bloom * 0.14),
                    r * (0.24 + tree.bloom * 0.16),
                    5,
                    rot + 1.1,
                    bloomHex,
                    foliageShadeColorFor(bloomHex, leafShade),
                    mass.wind);
            }
        }
    }
}
