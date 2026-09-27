// Port of packages/shared/src/domain/world/treeLife.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// **Tree life** — the lifecycle of the world's drawn trees, as one closed form over world time.
//
// A tree in this world is a deterministic decoration inside its chunk's terrain artifact — it has no
// state until the world *changes* it. This module is that change: **a tree gains a record the instant it
// is felled, and loses it the instant it has grown back.** Everything in between — the stump at the exact
// felling spot, the sprout, the sapling, the young tree, the moment the stand is fellable again — is a
// pure function of the one recorded instant, computable on the server and the client alike from
// `(felledAt, now)` with no per-tick traffic and no per-tick work (Simulation doctrine 1: any span,
// one call).
//
// ## Why the record is the *absence* of maturity
//
// The default state of every generated tree is "mature" — the forests of the Frontier have stood for
// longer than any colony. Only touched trees carry state, which is the same storage rule the terrain
// itself follows (only modified chunks are stored): an untouched world costs nothing, and a record
// deletes itself by construction the moment its tree is whole again. The wire and the database can only
// ever hold the world's open wounds, never the world.
//
// ## One clock, deliberately unscaled
//
// Regrowth runs on plain world time, not on the colony-scoped weather integral that scales a *site's*
// standing stock. A tree is a world fact, not a colony fact — two colonies at one treeline must agree on
// what stands there, and the client must be able to draw the stages without a colony in reach. The
// authoritative gate stays honest regardless: a tree is fellable only while **no record exists**, and
// records are created and deleted only on the server. The client draws growth; it never decides it.
//
// Porting note: a `SimInstant` is a `double` (world milliseconds since the epoch).

/// <summary>
/// The growth ladder of a regrowing tree.
///
/// A **closed enum in growth order**, so `stage &gt;= TreeGrowthStage.Young` and the ladder below can be
/// compared numerically. `Mature` is deliberately part of the vocabulary but never part of a record: a
/// mature tree *is* the deletion of its record (module comment). The renderer swaps geometry only at
/// stage boundaries — four re-bakes across a whole regrowth, never a per-frame morph — which is what
/// keeps a forest of regrowing trees as cheap to draw as a forest of static ones. The type is `int`.
/// </summary>
public static class TreeGrowthStage
{
    /// <summary>The cut face and roots of the felled trunk. Draws as the stump vocabulary, at the exact tile.</summary>
    public const int Stump = 0;
    /// <summary>First green out of the cut — unmistakably not a tree yet.</summary>
    public const int Sprout = 1;
    /// <summary>Recognisably the tree it will be, not yet fellable.</summary>
    public const int Young = 3;
    /// <summary>The default state of every drawn tree; a record never carries it.</summary>
    public const int Mature = 4;
}

public static class WorldTreeLife
{
    /// <summary>One row of <see cref="TREE_STAGE_VISUAL"/> (`{ readonly height01; readonly crown01 }`).</summary>
    public sealed class TreeStageVisual
    {
        public readonly double height01;
        public readonly double crown01;

        public TreeStageVisual(double height01, double crown01)
        {
            this.height01 = height01;
            this.crown01 = crown01;
        }
    }

    /// <summary>
    /// How a regrowing stage renders against the mature specimen it will become.
    ///
    /// One row per drawn stage, read by the render plan: `height01` scales the authored height ladder,
    /// `crown01` thins the crown budget (a sapling is mostly trunk). The stump row is present for
    /// completeness but the renderer swaps to the stump vocabulary outright — a stump is not a small tree.
    /// Kept here rather than client-side so a server test can pin that the visual ladder and the growth
    /// ladder can never drift apart.
    ///
    /// `Record&lt;TreeGrowthStage, …&gt;` with the dense keys 0..4: an array indexed by the stage (integer keys also
    /// iterate in ascending order in JS).
    /// </summary>
    public static readonly IReadOnlyList<TreeStageVisual> TREE_STAGE_VISUAL = new[]
    {
        /* [TreeGrowthStage.Stump] */ new TreeStageVisual(0, 0),
        /* [TreeGrowthStage.Sprout] */ new TreeStageVisual(0.22, 0.3),
        /* [TreeGrowthStage.Sapling] */ new TreeStageVisual(0.52, 0.55),
        /* [TreeGrowthStage.Young] */ new TreeStageVisual(0.82, 0.85),
        /* [TreeGrowthStage.Mature] */ new TreeStageVisual(1, 1),
    };

    /// <summary>
    /// The smallest specimen a regrowth stage may draw, in player heights.
    ///
    /// Deliberately below the tree ladder (2.3+) and inside bush territory: a sprout is *supposed* to read as
    /// "not yet a tree". What keeps it from being mistaken for a bush is structure, not size — a single stem
    /// with a thin forming crown against the bush's multi-stem arch — which is what the growth application
    /// below preserves.
    /// </summary>
    public const double TREE_GROWTH_MIN_HEIGHT_UNITS = 0.9;

    /// <summary>
    /// A regrowing tree's phenotype, derived from the mature specimen it will become.
    ///
    /// One shared function rather than per-stage authored visuals, for the identity that matters: the sapling
    /// at a stump IS the tree that was felled there — same seed, same profile, same pigments, same crown form
    /// — scaled down the authored ladder (<see cref="TREE_STAGE_VISUAL"/>). Height carries the read at colony zoom;
    /// branching and crown thin with it so a young tree is sparse rather than a shrunken adult; twigs and
    /// double clumps are the maturity the ladder is growing toward and arrive only with the last rung. The
    /// mature stage (and anything past it) returns the input untouched, so callers need no special case.
    ///
    /// The result is `{ ...tree, … }`: a shallow copy of the same runtime type (a derived effect keeps its own
    /// fields), with the grown axes replaced.
    /// </summary>
    /// <param name="stage">A <see cref="TreeGrowthStage"/>.</param>
    public static TreeVisual applyTreeGrowthToVisual(TreeVisual tree, int stage)
    {
        if (stage >= TreeGrowthStage.Mature) return tree;
        // A stump is not a small tree — the renderer swaps vocabularies before this function is reached — but
        // a defensive caller handing one in gets the first drawable rung rather than a zero-height specimen.
        TreeStageVisual grade =
            stage == TreeGrowthStage.Stump
                ? TREE_STAGE_VISUAL[TreeGrowthStage.Sprout]
                : TREE_STAGE_VISUAL[stage];
        double heightUnits = Math.max(TREE_GROWTH_MIN_HEIGHT_UNITS, tree.heightUnits * grade.height01);
        bool young = stage >= TreeGrowthStage.Young;
        TreeVisual grown = tree.Clone();
        grown.heightUnits = heightUnits;
        grown.scale = heightUnits / TreeVisualModule.TREE_REFERENCE_UNITS;
        // Hero stature is the crown of a long life; a regrowing tree has not lived one yet.
        grown.hero = false;
        grown.branches = (int)Math.max(3, Math.round(tree.branches * grade.crown01));
        grown.whorls = (int)Math.max(1, Math.round(tree.whorls * grade.crown01));
        grown.subBranches = (int)Math.max(1, Math.round(tree.subBranches * grade.crown01));
        grown.forks = young ? tree.forks : 0;
        grown.clumpsPerBranch = young ? tree.clumpsPerBranch : 1;
        grown.crownRadius = tree.crownRadius * (0.6 + 0.4 * grade.crown01);
        grown.crownDensity = tree.crownDensity * (0.5 + 0.5 * grade.crown01);
        return grown;
    }
}
