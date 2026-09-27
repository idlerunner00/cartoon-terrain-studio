// Port of packages/shared/src/domain/dungeon/treeVisual.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Canonical, renderer-neutral tree phenotype data — the ONE base every tree in the game grows from.
//
// Placement remains the responsibility of the owning world system (terrain render plan or Hub dressing) and
// pigment remains the responsibility of the renderer's foliage recipe. This registry owns the *form*: how tall
// a specimen stands, how its trunk sweeps, how many limbs it carries, how those limbs fan out and how much
// foliage hangs off them. Every world selects one profile; nobody grows a second silhouette family.
//
// ## The size ladder is the point
// A world full of interchangeable mid-size cones reads as wallpaper. Trees are drawn from an explicit
// small/medium/large ladder authored in PLAYER HEIGHTS, not in tile fractions: a tile is 40 px in the Hub and
// 62.5 px in Endless, and a tree — like a door or a storey — cannot change its real size because its carrier
// grid did. `TreeVisual.scale` is that height expressed against `TREE_REFERENCE_UNITS`, so the one
// number placement systems already override keeps meaning exactly what it always meant.
//
// ## Branching is data, not a renderer branch
// `branches`, `whorls`, `subBranches`, `branchPitch`, `branchSpread`, `branchDrop` and `forks` describe a
// real, recursive skeleton: a bole carries primaries, each primary divides into secondaries, and big
// specimens divide once more. A columnar cypress and a broad orchard crown are the same generator with
// different numbers here — never two code paths.
//
// Porting notes: the string-literal unions below are `string` with their literals collected in static
// classes; readonly tuples (`readonly [number, number]`) are `IReadOnlyList<double>` so `bounds[0]` reads as
// in TS; the integral phenotype counts (branches, whorls, subBranches, forks, clumpsPerBranch) are `int`.

/// <summary>`TreeVisualStyle = 'sentinel' | 'windfan' | 'spire'`.</summary>
public static class TreeVisualStyle
{
    public const string Sentinel = "sentinel";
    public const string Windfan = "windfan";
    public const string Spire = "spire";
}

/// <summary>`TreeCrownShape = 'tiered' | 'cloud'`.</summary>
public static class TreeCrownShape
{
    public const string Tiered = "tiered";
    public const string Cloud = "cloud";
}

/// <summary>The readable size ladder. Every world draws from all three; only the weights differ.</summary>
public static class TreeSizeClass
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
}

/// <summary>Crown envelope grammar. The skeleton generator reads this; it never switches on a biome.</summary>
public static class TreeCrownForm
{
    public const string Round = "round";
    public const string Conic = "conic";
    public const string Fan = "fan";
    public const string Columnar = "columnar";
}

/// <summary>Trunk grammar: a straight bole, a swept one, or one that splits into two leaders low down.</summary>
public static class TreeTrunkForm
{
    public const string Straight = "straight";
    public const string Sweep = "sweep";
    public const string Forked = "forked";
}

/// <summary>`TreeVisualProfileId` literals.</summary>
public static class TreeVisualProfileId
{
    public const string Natural = "natural";
}

/// <summary>
/// One specimen's phenotype. Not sealed: the render plan's tree dressing effect extends it (TS
/// `TerrainTreeDressingEffect extends TreeVisual`). <see cref="Clone"/> is `{ ...visual }` and keeps the runtime
/// type (and so a derived effect's own fields), exactly like an object spread of the derived record.
/// </summary>
public class TreeVisual
{
    /// <summary>
    /// The registry entry this specimen grew from. Renderers select the world's foliage pigment by it and audits
    /// group by it; it is never a licence to draw a different silhouette. <see cref="TreeVisualProfileId"/>.
    /// </summary>
    public string profile = TreeVisualProfileId.Natural;
    /// <summary>
    /// Specimen size as a multiple of <see cref="TreeVisualModule.TREE_REFERENCE_UNITS"/> player heights. Placement
    /// systems may override it (the Hub authors its own plaza specimens); everything else in the phenotype stays
    /// registry-driven.
    /// </summary>
    public double scale;
    /// <summary>Readable ladder label for <see cref="scale"/>; audits and probes group by it. <see cref="TreeSizeClass"/>.</summary>
    public string sizeClass = TreeSizeClass.Small;
    /// <summary>Boot-to-crown height in player heights — `scale` in the unit the world is actually authored in.</summary>
    public double heightUnits;
    /// <summary>Legacy crown-proportion axis retained for the themed signature silhouettes that still consume it.</summary>
    public double height;
    public double phase;
    public double lean;
    /// <summary><see cref="TreeVisualStyle"/>.</summary>
    public string style = TreeVisualStyle.Sentinel;
    /// <summary>Second independent crown grammar; every biome profile carries both forms. <see cref="TreeCrownShape"/>.</summary>
    public string crownShape = TreeCrownShape.Tiered;
    /// <summary>Rare skyline specimen selected by registry data, never by renderer branching.</summary>
    public bool hero;
    public double canopyHue;
    public double age;
    public double crownDensity;
    public double asymmetry;
    public double alpha;

    // ── Skeleton ────────────────────────────────────────────────────────────────────────────────────────
    /// <summary><see cref="TreeCrownForm"/>.</summary>
    public string crownForm = TreeCrownForm.Round;
    /// <summary><see cref="TreeTrunkForm"/>.</summary>
    public string trunkForm = TreeTrunkForm.Straight;
    /// <summary>Primary limbs leaving the bole. The single strongest "no two trees alike" axis.</summary>
    public int branches;
    /// <summary>Height bands the limbs are drawn from — 1 reads as a parasol, 3 as a layered forest tree.</summary>
    public int whorls;
    /// <summary>
    /// Secondary limbs each primary splits into. THE axis that turns a spoke into a branch: a tree whose limbs
    /// do not themselves divide reads as an umbrella frame no matter how good its foliage is.
    /// </summary>
    public int subBranches;
    /// <summary>0 = limbs reach out horizontally, 1 = limbs climb almost parallel to the trunk.</summary>
    public double branchPitch;
    /// <summary>How far a limb reaches, relative to the crown radius.</summary>
    public double branchSpread;
    /// <summary>Fraction of trunk height at which the lowest whorl starts. High values bare the bole.</summary>
    public double branchDrop;
    /// <summary>Secondary twigs per limb. Large and hero specimens earn the extra silhouette detail.</summary>
    public int forks;
    /// <summary>Crown radius relative to total height.</summary>
    public double crownRadius;
    /// <summary>Foliage masses hung on each limb.</summary>
    public int clumpsPerBranch;
    /// <summary>0..1 selection inside the world's bark pigment family.</summary>
    public double barkHue;
    /// <summary>0..1 blossom / fruit / lantern punctuation carried by the crown.</summary>
    public double bloom;

    public TreeVisual() { }

    /// <summary>Copy constructor: the TreeVisual part of `{ ...source }`, for records that extend TreeVisual.</summary>
    public TreeVisual(TreeVisual source) => CopyTreeVisualFrom(source);

    /// <summary>Assigns every TreeVisual field of `source` (the TreeVisual part of an object spread).</summary>
    public void CopyTreeVisualFrom(TreeVisual source)
    {
        profile = source.profile;
        scale = source.scale;
        sizeClass = source.sizeClass;
        heightUnits = source.heightUnits;
        height = source.height;
        phase = source.phase;
        lean = source.lean;
        style = source.style;
        crownShape = source.crownShape;
        hero = source.hero;
        canopyHue = source.canopyHue;
        age = source.age;
        crownDensity = source.crownDensity;
        asymmetry = source.asymmetry;
        alpha = source.alpha;
        crownForm = source.crownForm;
        trunkForm = source.trunkForm;
        branches = source.branches;
        whorls = source.whorls;
        subBranches = source.subBranches;
        branchPitch = source.branchPitch;
        branchSpread = source.branchSpread;
        branchDrop = source.branchDrop;
        forks = source.forks;
        crownRadius = source.crownRadius;
        clumpsPerBranch = source.clumpsPerBranch;
        barkHue = source.barkHue;
        bloom = source.bloom;
    }

    /// <summary>`{ ...visual }` — a shallow copy of the same runtime type.</summary>
    public TreeVisual Clone() => (TreeVisual)MemberwiseClone();
}

/// <summary>
/// `Partial&lt;TreeVisual&gt;` — the `overrides` argument of <see cref="TreeVisualModule.createTreeVisual"/>. A null
/// field is an absent key; a set field replaces the generated value (`{ ...visual, ...overrides }`).
/// </summary>
public sealed class TreeVisualOverrides
{
    public string? profile;
    public double? scale;
    public string? sizeClass;
    public double? heightUnits;
    public double? height;
    public double? phase;
    public double? lean;
    public string? style;
    public string? crownShape;
    public bool? hero;
    public double? canopyHue;
    public double? age;
    public double? crownDensity;
    public double? asymmetry;
    public double? alpha;
    public string? crownForm;
    public string? trunkForm;
    public int? branches;
    public int? whorls;
    public int? subBranches;
    public double? branchPitch;
    public double? branchSpread;
    public double? branchDrop;
    public int? forks;
    public double? crownRadius;
    public int? clumpsPerBranch;
    public double? barkHue;
    public double? bloom;

    /// <summary>Applies every present key onto `target` (the `...overrides` half of the spread).</summary>
    public void ApplyTo(TreeVisual target)
    {
        if (profile != null) target.profile = profile;
        if (scale != null) target.scale = scale.Value;
        if (sizeClass != null) target.sizeClass = sizeClass;
        if (heightUnits != null) target.heightUnits = heightUnits.Value;
        if (height != null) target.height = height.Value;
        if (phase != null) target.phase = phase.Value;
        if (lean != null) target.lean = lean.Value;
        if (style != null) target.style = style;
        if (crownShape != null) target.crownShape = crownShape;
        if (hero != null) target.hero = hero.Value;
        if (canopyHue != null) target.canopyHue = canopyHue.Value;
        if (age != null) target.age = age.Value;
        if (crownDensity != null) target.crownDensity = crownDensity.Value;
        if (asymmetry != null) target.asymmetry = asymmetry.Value;
        if (alpha != null) target.alpha = alpha.Value;
        if (crownForm != null) target.crownForm = crownForm;
        if (trunkForm != null) target.trunkForm = trunkForm;
        if (branches != null) target.branches = branches.Value;
        if (whorls != null) target.whorls = whorls.Value;
        if (subBranches != null) target.subBranches = subBranches.Value;
        if (branchPitch != null) target.branchPitch = branchPitch.Value;
        if (branchSpread != null) target.branchSpread = branchSpread.Value;
        if (branchDrop != null) target.branchDrop = branchDrop.Value;
        if (forks != null) target.forks = forks.Value;
        if (crownRadius != null) target.crownRadius = crownRadius.Value;
        if (clumpsPerBranch != null) target.clumpsPerBranch = clumpsPerBranch.Value;
        if (barkHue != null) target.barkHue = barkHue.Value;
        if (bloom != null) target.bloom = bloom.Value;
    }
}

public static class TreeVisualModule
{
    public static readonly IReadOnlyList<string> TREE_SIZE_CLASSES = new[]
    {
        TreeSizeClass.Small,
        TreeSizeClass.Medium,
        TreeSizeClass.Large,
    };

    public static readonly IReadOnlyList<string> TREE_CROWN_FORMS = new[]
    {
        TreeCrownForm.Round,
        TreeCrownForm.Conic,
        TreeCrownForm.Fan,
        TreeCrownForm.Columnar,
    };

    public static readonly IReadOnlyList<string> TREE_TRUNK_FORMS = new[]
    {
        TreeTrunkForm.Straight,
        TreeTrunkForm.Sweep,
        TreeTrunkForm.Forked,
    };

    /// <summary>`Readonly&lt;Record&lt;TreeSizeClass, readonly [number, number]&gt;&gt;` — fields plus a string indexer.</summary>
    public sealed class TreeSizeUnitsRecord
    {
        public readonly IReadOnlyList<double> small;
        public readonly IReadOnlyList<double> medium;
        public readonly IReadOnlyList<double> large;

        public TreeSizeUnitsRecord(IReadOnlyList<double> small, IReadOnlyList<double> medium, IReadOnlyList<double> large)
        {
            this.small = small;
            this.medium = medium;
            this.large = large;
        }

        public IReadOnlyList<double> this[string sizeClass] => sizeClass switch
        {
            TreeSizeClass.Small => small,
            TreeSizeClass.Medium => medium,
            TreeSizeClass.Large => large,
            _ => throw new InvalidOperationException($"Unknown tree size class '{sizeClass}'"),
        };
    }

    /// <summary>
    /// Boot-to-crown height of one specimen, in player heights, per size class.
    ///
    /// These are absolute world facts, shared by every profile: a small tree is a small tree in the Hub and in
    /// Run 10. A profile scales the whole ladder with <see cref="TreeVisualProfile.heightScale"/> but can never
    /// reorder it, so "small / medium / large" stays a promise the player can read across the whole game.
    /// </summary>
    public static readonly TreeSizeUnitsRecord TREE_SIZE_UNITS = new(
        new double[] { 2.3, 3.1 },
        new double[] { 3.2, 4.4 },
        new double[] { 4.6, 6.2 });

    /// <summary>Rare skyline specimens grow one clear step past their class instead of becoming a different tree.</summary>
    public const double TREE_HERO_HEIGHT_GAIN = 1.2;

    /// <summary>The height <see cref="TreeVisual.scale"/> 1.0 represents, in player heights. Renderers multiply, never guess.</summary>
    public const double TREE_REFERENCE_UNITS = 3.7;

    /// <summary>
    /// A registry profile (TS non-exported `interface TreeVisualProfile`, reachable through the exported
    /// <see cref="TREE_VISUAL_PROFILES"/>).
    /// </summary>
    public sealed class TreeVisualProfile
    {
        /// <summary>Relative weights in sentinel, windfan, spire order.</summary>
        public IReadOnlyList<double> styles = null!;
        /// <summary>Relative weights in tiered/cloud order.</summary>
        public IReadOnlyList<double> crowns = null!;
        /// <summary>Relative weights in small, medium, large order — the world's silhouette rhythm.</summary>
        public IReadOnlyList<double> sizes = null!;
        /// <summary>Relative weights in round, conic, fan, columnar order.</summary>
        public IReadOnlyList<double> crownForms = null!;
        /// <summary>Relative weights in straight, sweep, forked order.</summary>
        public IReadOnlyList<double> trunkForms = null!;
        public double heroChance;
        /// <summary>Multiplier on <see cref="TREE_SIZE_UNITS"/>; a world of saplings or of giants without a second ladder.</summary>
        public double heightScale;
        public IReadOnlyList<double> height = null!;
        public IReadOnlyList<double> lean = null!;
        public IReadOnlyList<double> canopyHue = null!;
        public IReadOnlyList<double> alpha = null!;
        /// <summary>Primary limb count band. Small specimens draw from the bottom of it, large ones from the top.</summary>
        public IReadOnlyList<double> branches = null!;
        public IReadOnlyList<double> whorls = null!;
        /// <summary>How many ways a primary limb divides. Sparse mechanical worlds fork less than a wild woodland.</summary>
        public IReadOnlyList<double> subBranches = null!;
        public IReadOnlyList<double> branchPitch = null!;
        public IReadOnlyList<double> branchSpread = null!;
        public IReadOnlyList<double> branchDrop = null!;
        public IReadOnlyList<double> crownRadius = null!;
        public IReadOnlyList<double> bloom = null!;
        public IReadOnlyList<double> barkHue = null!;
    }

    private static double[] T(params double[] values) => values;

    /// <summary>
    /// Data-only profile registry. New tree-bearing worlds select a profile; renderers never branch on them.
    ///
    /// Every entry is the SAME generator with different numbers — that is the whole design. A profile changes how
    /// often a world grows giants, how bare its boles are, how steeply its limbs climb and how wide its crowns
    /// spread. It cannot introduce a shape the generator does not already know how to grow.
    /// </summary>
    public static readonly JsMap<string, TreeVisualProfile> TREE_VISUAL_PROFILES = new JsMap<string, TreeVisualProfile>()
        /* The neutral temperate tree: broad crowns, generous branching, a real mix of ages. */
        .set("natural", new TreeVisualProfile
        {
            styles = T(0.5, 0.28, 0.22),
            crowns = T(0.54, 0.46),
            sizes = T(0.32, 0.44, 0.24),
            crownForms = T(0.46, 0.2, 0.24, 0.1),
            trunkForms = T(0.34, 0.44, 0.22),
            heroChance = 0.085,
            heightScale = 1,
            height = T(1.08, 1.82),
            lean = T(-0.45, 0.45),
            canopyHue = T(0, 1),
            alpha = T(0.72, 0.92),
            branches = T(4, 8),
            whorls = T(2, 3),
            subBranches = T(2, 3),
            branchPitch = T(0.18, 0.44),
            branchSpread = T(0.95, 1.25),
            branchDrop = T(0.42, 0.6),
            crownRadius = T(0.46, 0.62),
            bloom = T(0, 0.3),
            barkHue = T(0.2, 0.62),
        })
        /* Alpine conifer: steep short limbs, bare lower bole, narrow conic crowns, occasional giants. */
        .set("pine", new TreeVisualProfile
        {
            styles = T(0.18, 0.06, 0.76),
            crowns = T(0.72, 0.28),
            sizes = T(0.28, 0.44, 0.28),
            crownForms = T(0.12, 0.56, 0.06, 0.26),
            trunkForms = T(0.62, 0.3, 0.08),
            heroChance = 0.075,
            heightScale = 1.06,
            height = T(1.18, 1.88),
            lean = T(-0.34, 0.34),
            canopyHue = T(0.16, 0.62),
            alpha = T(0.76, 0.94),
            branches = T(5, 9),
            whorls = T(2, 3),
            subBranches = T(2, 4),
            branchPitch = T(0.4, 0.66),
            branchSpread = T(0.72, 1.02),
            branchDrop = T(0.34, 0.56),
            crownRadius = T(0.3, 0.42),
            bloom = T(0, 0.14),
            barkHue = T(0.3, 0.7),
        })
        /* Marble-terrace cypress: tall, narrow, ceremonial columns of foliage. */
        .set("cypress", new TreeVisualProfile
        {
            styles = T(0.26, 0.1, 0.64),
            crowns = T(0.62, 0.38),
            sizes = T(0.24, 0.42, 0.34),
            crownForms = T(0.1, 0.24, 0.06, 0.6),
            trunkForms = T(0.7, 0.24, 0.06),
            heroChance = 0.07,
            heightScale = 1.12,
            height = T(1.12, 1.86),
            lean = T(-0.24, 0.24),
            canopyHue = T(0.22, 0.72),
            alpha = T(0.78, 0.96),
            branches = T(4, 7),
            whorls = T(2, 3),
            subBranches = T(2, 4),
            branchPitch = T(0.6, 0.84),
            branchSpread = T(0.52, 0.74),
            branchDrop = T(0.28, 0.5),
            crownRadius = T(0.2, 0.3),
            bloom = T(0, 0.12),
            barkHue = T(0.34, 0.76),
        })
        /* Temple blossom: low forked boles, wide flat crowns, heavy bloom. */
        .set("sakura", new TreeVisualProfile
        {
            styles = T(0.3, 0.62, 0.08),
            crowns = T(0.34, 0.66),
            sizes = T(0.34, 0.46, 0.2),
            crownForms = T(0.34, 0.08, 0.5, 0.08),
            trunkForms = T(0.16, 0.42, 0.42),
            heroChance = 0.09,
            heightScale = 0.94,
            height = T(1.08, 1.82),
            lean = T(-0.45, 0.45),
            canopyHue = T(0.72, 1),
            alpha = T(0.78, 0.98),
            branches = T(5, 9),
            whorls = T(1, 2),
            subBranches = T(2, 4),
            branchPitch = T(0.08, 0.3),
            branchSpread = T(1.05, 1.4),
            branchDrop = T(0.46, 0.64),
            crownRadius = T(0.54, 0.72),
            bloom = T(0.46, 1),
            barkHue = T(0.1, 0.44),
        })
        /* Trench growth: few, thick, sprawling limbs and dense low crowns — pressure-grown, never airy. */
        .set("abyssal", new TreeVisualProfile
        {
            styles = T(0.16, 0.52, 0.32),
            crowns = T(0.42, 0.58),
            sizes = T(0.38, 0.42, 0.2),
            crownForms = T(0.42, 0.12, 0.36, 0.1),
            trunkForms = T(0.18, 0.5, 0.32),
            heroChance = 0.08,
            heightScale = 0.9,
            height = T(1.08, 1.82),
            lean = T(-0.45, 0.45),
            canopyHue = T(0.64, 1),
            alpha = T(0.78, 0.98),
            branches = T(3, 6),
            whorls = T(1, 2),
            subBranches = T(2, 4),
            branchPitch = T(0.1, 0.36),
            branchSpread = T(1.1, 1.45),
            branchDrop = T(0.4, 0.58),
            crownRadius = T(0.52, 0.7),
            bloom = T(0.3, 0.82),
            barkHue = T(0.5, 0.92),
        })
        /* Archive glass: rigid, near-symmetrical, sharply tiered — a grown shelf. */
        .set("prismatic", new TreeVisualProfile
        {
            styles = T(0.32, 0.1, 0.58),
            crowns = T(0.64, 0.36),
            sizes = T(0.3, 0.44, 0.26),
            crownForms = T(0.16, 0.44, 0.1, 0.3),
            trunkForms = T(0.66, 0.26, 0.08),
            heroChance = 0.075,
            heightScale = 1.04,
            height = T(1.08, 1.82),
            lean = T(-0.2, 0.2),
            canopyHue = T(0.82, 1),
            alpha = T(0.78, 0.98),
            branches = T(4, 8),
            whorls = T(2, 3),
            subBranches = T(2, 3),
            branchPitch = T(0.32, 0.58),
            branchSpread = T(0.8, 1.06),
            branchDrop = T(0.4, 0.6),
            crownRadius = T(0.36, 0.5),
            bloom = T(0.2, 0.6),
            barkHue = T(0.56, 1),
        })
        /* Prism realm: loud, many-limbed, wide fans that carry the world's whole spectrum. */
        .set("rainbow", new TreeVisualProfile
        {
            styles = T(0.34, 0.34, 0.32),
            crowns = T(0.48, 0.52),
            sizes = T(0.3, 0.42, 0.28),
            crownForms = T(0.38, 0.14, 0.38, 0.1),
            trunkForms = T(0.28, 0.44, 0.28),
            heroChance = 0.095,
            heightScale = 1,
            height = T(1.08, 1.82),
            lean = T(-0.4, 0.4),
            canopyHue = T(0, 1),
            alpha = T(0.8, 0.98),
            branches = T(6, 9),
            whorls = T(2, 3),
            subBranches = T(2, 4),
            branchPitch = T(0.16, 0.42),
            branchSpread = T(1, 1.3),
            branchDrop = T(0.4, 0.58),
            crownRadius = T(0.48, 0.66),
            bloom = T(0.5, 1),
            barkHue = T(0.3, 0.9),
        })
        /* Brass bazaar: sparse mechanical growth, bare boles, few precise limbs. */
        .set("clockwork", new TreeVisualProfile
        {
            styles = T(0.4, 0.42, 0.18),
            crowns = T(0.58, 0.42),
            sizes = T(0.42, 0.4, 0.18),
            crownForms = T(0.3, 0.24, 0.22, 0.24),
            trunkForms = T(0.6, 0.24, 0.16),
            heroChance = 0.065,
            heightScale = 0.96,
            height = T(1.08, 1.82),
            lean = T(-0.26, 0.26),
            canopyHue = T(0.18, 1),
            alpha = T(0.78, 0.98),
            branches = T(3, 6),
            whorls = T(2, 3),
            subBranches = T(1, 2),
            branchPitch = T(0.28, 0.54),
            branchSpread = T(0.86, 1.16),
            branchDrop = T(0.5, 0.7),
            crownRadius = T(0.32, 0.46),
            bloom = T(0.12, 0.5),
            barkHue = T(0.4, 0.86),
        })
        /* Fairground: fat, round, over-sweet crowns on short trunks. */
        .set("carnival", new TreeVisualProfile
        {
            styles = T(0.3, 0.22, 0.48),
            crowns = T(0.46, 0.54),
            sizes = T(0.36, 0.44, 0.2),
            crownForms = T(0.56, 0.14, 0.22, 0.08),
            trunkForms = T(0.3, 0.46, 0.24),
            heroChance = 0.085,
            heightScale = 0.92,
            height = T(1.08, 1.82),
            lean = T(-0.36, 0.36),
            canopyHue = T(0.18, 1),
            alpha = T(0.78, 0.98),
            branches = T(4, 8),
            whorls = T(1, 2),
            subBranches = T(2, 3),
            branchPitch = T(0.14, 0.4),
            branchSpread = T(0.95, 1.25),
            branchDrop = T(0.44, 0.62),
            crownRadius = T(0.5, 0.68),
            bloom = T(0.55, 1),
            barkHue = T(0.24, 0.72),
        })
        /* Cathedral nave: tall bare shafts opening into a high vaulted crown. */
        .set("cathedral", new TreeVisualProfile
        {
            styles = T(0.34, 0.1, 0.56),
            crowns = T(0.68, 0.32),
            sizes = T(0.24, 0.42, 0.34),
            crownForms = T(0.2, 0.34, 0.14, 0.32),
            trunkForms = T(0.66, 0.26, 0.08),
            heroChance = 0.08,
            heightScale = 1.14,
            height = T(1.08, 1.82),
            lean = T(-0.22, 0.22),
            canopyHue = T(0.78, 1),
            alpha = T(0.78, 0.98),
            branches = T(4, 7),
            whorls = T(2, 3),
            subBranches = T(2, 3),
            branchPitch = T(0.44, 0.7),
            branchSpread = T(0.74, 1.02),
            branchDrop = T(0.48, 0.68),
            crownRadius = T(0.32, 0.46),
            bloom = T(0.16, 0.54),
            barkHue = T(0.46, 0.94),
        })
        /* Street planting: pruned, high-crowned, deliberately uniform — a city keeps its trees in line. */
        .set("city", new TreeVisualProfile
        {
            styles = T(0.32, 0.14, 0.54),
            crowns = T(0.62, 0.38),
            sizes = T(0.4, 0.46, 0.14),
            crownForms = T(0.44, 0.16, 0.16, 0.24),
            trunkForms = T(0.7, 0.22, 0.08),
            heroChance = 0.05,
            heightScale = 0.94,
            height = T(1.08, 1.82),
            lean = T(-0.18, 0.18),
            canopyHue = T(0.58, 1),
            alpha = T(0.78, 0.98),
            branches = T(3, 6),
            whorls = T(1, 2),
            subBranches = T(2, 3),
            branchPitch = T(0.22, 0.48),
            branchSpread = T(0.8, 1.06),
            branchDrop = T(0.56, 0.76),
            crownRadius = T(0.34, 0.48),
            bloom = T(0, 0.26),
            barkHue = T(0.5, 0.9),
        })
        /* Engineered orchard: tall palm-like fans, long bare stems, heavy fruit. */
        .set("paradise", new TreeVisualProfile
        {
            styles = T(0.16, 0.76, 0.08),
            crowns = T(0.28, 0.72),
            sizes = T(0.26, 0.44, 0.3),
            crownForms = T(0.22, 0.06, 0.62, 0.1),
            trunkForms = T(0.36, 0.52, 0.12),
            heroChance = 0.1,
            heightScale = 1.08,
            height = T(1.18, 1.88),
            lean = T(-0.45, 0.45),
            canopyHue = T(0.05, 0.98),
            alpha = T(0.82, 0.98),
            branches = T(5, 9),
            whorls = T(1, 2),
            subBranches = T(1, 2),
            branchPitch = T(0.06, 0.28),
            branchSpread = T(1.1, 1.45),
            branchDrop = T(0.62, 0.82),
            crownRadius = T(0.46, 0.64),
            bloom = T(0.4, 0.94),
            barkHue = T(0.18, 0.6),
        });

    /// <summary>
    /// World → profile. Every Endless run appears here: the canopy slot grows a real tree in all ten of them, and
    /// a run's identity is carried by which profile (and which foliage pigment) it selects, never by a bespoke
    /// silhouette that only that run can draw.
    /// </summary>
    private static readonly Dictionary<string, string> BIOME_TREE_PROFILE = new()
    {
        ["hub"] = "pine",
        // ── The ten Endless runs, in order ────────────────────────────────────────────────────────────────
        ["highland_pass"] = "pine",
        ["noir_sprawl"] = "city",
        ["olympian_sky_borough"] = "cypress",
        ["sakura_temple_dream"] = "sakura",
        ["abyssal_deepsea"] = "abyssal",
        ["rainbowland"] = "rainbow",
        ["clockwork_moon_bazaar"] = "clockwork",
        ["sugarstorm_carnival"] = "carnival",
        ["prismglass_archive"] = "prismatic",
        ["starforged_cathedral_endrun"] = "cathedral",
        // ── Generator-only worlds, raids and the arena ────────────────────────────────────────────────────
        ["alien_ranch"] = "paradise",
        ["viking_ship_village"] = "pine",
        ["museum"] = "natural",
        ["arena"] = "natural",
        ["raid_thousandfolds"] = "sakura",
        ["raid_tidecage"] = "abyssal",
        ["raid_holdthefort"] = "cathedral",
        ["raid_verdant"] = "clockwork",
        ["raid_wyrmforge"] = "cathedral",
        ["raid_moonroot"] = "natural",
        ["raid_crownbower"] = "paradise",
        ["raid_resonanceeyrie"] = "cathedral",
        ["raid_starossuary"] = "cathedral",
        ["raid_regrowthcanals"] = "abyssal",
        ["raid_cragsummit"] = "natural",
        ["raid_hollowcartography"] = "clockwork",
    };

    /// <returns>A <see cref="TreeVisualProfileId"/>.</returns>
    public static string treeVisualProfileForBiome(string? biomeKey)
    {
        // `(biomeKey ? BIOME_TREE_PROFILE[biomeKey] : undefined) ?? 'natural'`
        if (!string.IsNullOrEmpty(biomeKey) && BIOME_TREE_PROFILE.TryGetValue(biomeKey, out string? profile))
            return profile;
        return "natural";
    }

    /// <summary>Deterministic integer hash to a stable [0, 1) sample (pure and allocation-free).</summary>
    private static double sample(double seed, int salt)
    {
        int h = Math.imul(Js.ToInt32(seed) ^ salt, 0x45d9f3b);
        h = Math.imul(h ^ (int)((uint)h >> 16), 0x45d9f3b);
        return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
    }

    private static double range(IReadOnlyList<double> bounds, double value) => bounds[0] + (bounds[1] - bounds[0]) * value;

    /// <returns>A <see cref="TreeVisualStyle"/>.</returns>
    private static string chooseStyle(IReadOnlyList<double> weights, double roll)
    {
        double total = weights[0] + weights[1] + weights[2];
        double value = roll * total;
        if (value < weights[0]) return TreeVisualStyle.Sentinel;
        if (value < weights[0] + weights[1]) return TreeVisualStyle.Windfan;
        return TreeVisualStyle.Spire;
    }

    /// <returns>A <see cref="TreeCrownShape"/>.</returns>
    private static string chooseCrown(IReadOnlyList<double> weights, double roll) =>
        roll * (weights[0] + weights[1]) < weights[0] ? TreeCrownShape.Tiered : TreeCrownShape.Cloud;

    /// <summary>Pick from a weighted table by index; the caller owns the value list so this stays allocation-free.</summary>
    private static int chooseIndex(IReadOnlyList<double> weights, double roll)
    {
        double total = 0;
        for (int i = 0; i < weights.Count; i++) total += weights[i];
        double cursor = roll * total;
        for (int index = 0; index < weights.Count; index++)
        {
            cursor -= weights[index];
            if (cursor < 0) return index;
        }
        return weights.Count - 1;
    }

    /// <summary>
    /// Resolve a canonical tree phenotype from a stable seed and biome/profile id.
    ///
    /// Callers may override a placement-owned value such as an authored Hub specimen size while keeping every
    /// other axis registry-driven — that is how the Hub keeps its composed plaza trees without forking the system.
    /// </summary>
    public static TreeVisual createTreeVisual(double seed, string? biomeOrProfile, TreeVisualOverrides? overrides = null)
    {
        string profileId =
            biomeOrProfile != null && TREE_VISUAL_PROFILES.has(biomeOrProfile)
                ? biomeOrProfile
                : treeVisualProfileForBiome(biomeOrProfile);
        TreeVisualProfile profile = TREE_VISUAL_PROFILES[profileId];
        bool hero = sample(seed, 487) < profile.heroChance;

        string sizeClass = TREE_SIZE_CLASSES[chooseIndex(profile.sizes, sample(seed, 503))];
        // Where this specimen sits INSIDE its class. Reused for every trait that should grow with the tree, so a
        // large specimen is consistently a large specimen — more limbs, deeper forks, a wider crown — instead of a
        // tall trunk with a sapling's branching.
        double maturity = sample(seed, 509);
        double heightUnits =
            range(TREE_SIZE_UNITS[sizeClass], maturity) *
            profile.heightScale *
            (hero ? TREE_HERO_HEIGHT_GAIN : 1);
        double bigness =
            (sizeClass == TreeSizeClass.Small ? 0 : sizeClass == TreeSizeClass.Medium ? 0.5 : 1) * 0.72 + maturity * 0.28;

        double branchBand = range(profile.branches, sample(seed, 521) * 0.45 + bigness * 0.55);
        double whorls = Math.max(
            1,
            Math.round(range(profile.whorls, sample(seed, 523) * 0.5 + bigness * 0.5)));
        double subBranches = Math.max(
            1,
            Math.round(range(profile.subBranches, sample(seed, 601) * 0.62 + bigness * 0.38)));
        var visual = new TreeVisual
        {
            profile = profileId,
            scale = heightUnits / TREE_REFERENCE_UNITS,
            sizeClass = sizeClass,
            heightUnits = heightUnits,
            height = range(profile.height, sample(seed, 439)),
            phase = sample(seed, 443),
            lean = range(profile.lean, sample(seed, 449)),
            style = chooseStyle(profile.styles, sample(seed, 419)),
            crownShape = chooseCrown(profile.crowns, sample(seed, 491)),
            hero = hero,
            canopyHue = range(profile.canopyHue, sample(seed, 457)),
            age = sample(seed, 463),
            crownDensity = 0.38 + sample(seed, 467) * 0.62,
            asymmetry = (sample(seed, 479) - 0.5) * 2,
            alpha = range(profile.alpha, sample(seed, 461)),

            crownForm = TREE_CROWN_FORMS[chooseIndex(profile.crownForms, sample(seed, 541))],
            trunkForm = TREE_TRUNK_FORMS[chooseIndex(profile.trunkForms, sample(seed, 547))],
            // Round up on the way in so the bottom of a profile's band is reachable and the top is never exceeded.
            branches = (int)Math.max(3, Math.round(branchBand)),
            whorls = (int)whorls,
            subBranches = (int)subBranches,
            branchPitch = range(profile.branchPitch, sample(seed, 557)),
            branchSpread = range(profile.branchSpread, sample(seed, 563)),
            branchDrop = range(profile.branchDrop, sample(seed, 569)),
            // Twigs are what separates a mature tree from a sapling; they arrive with size, never with pigment.
            forks = hero
                ? 2
                : sizeClass == TreeSizeClass.Large
                    ? 1 + (sample(seed, 571) < 0.42 ? 1 : 0)
                    : sizeClass == TreeSizeClass.Medium
                        ? sample(seed, 571) < 0.55
                            ? 1
                            : 0
                        : 0,
            crownRadius = range(profile.crownRadius, sample(seed, 577)),
            clumpsPerBranch = sample(seed, 587) < 0.34 + bigness * 0.42 ? 2 : 1,
            barkHue = range(profile.barkHue, sample(seed, 593)),
            bloom = range(profile.bloom, sample(seed, 599)),
        };
        // `{ ...visual, ...overrides }`
        TreeVisual result = visual.Clone();
        overrides?.ApplyTo(result);
        return result;
    }
}
