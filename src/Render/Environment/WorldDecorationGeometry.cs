// Port of packages/client/src/render/environment/worldDecorationGeometry.ts — keep in lockstep with the original.
//
// PORT NOTE: `export type { WorldDecorationGeometryOptions }` is a type-only re-export; the type lives in
// WorldPropContext.cs and is already visible namespace-wide.
using Fluitown.Domain;
using static Fluitown.Domain.TreeVisualModule;
using static Fluitown.Render.WorldScale;
using static Fluitown.Render.Palette;
using static Fluitown.Render.TreeGeometry;
using static Fluitown.Render.WorldPropContextModule;
using static Fluitown.Render.WorldPropPrimitives;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// Every collision-neutral world prop that is not a tree — which is to say: the undergrowth and the stumps.
///
/// Placement belongs to the owning world (authored dressing or the generated render plan); this module owns how
/// each kind LOOKS as real, lit, shadow-casting 3D world-base geometry baked with its tile. Props are part of
/// the terrain pass, so the shared depth buffer occludes actors behind a bush exactly like behind a wall — no
/// per-frame cost beyond the meshes that already exist.
///
/// Theme changes pigment only. It never swaps in a special prop model: one vocabulary, every world.
///
/// There used to be two sibling modules here — mineral-and-fire relics (cairn, crystal, brazier) and built
/// monuments (monolith, ruined arch, wayshrine). Both are gone with the kinds they drew: this world contains
/// what grows and what somebody built, and nothing that was merely placed.
/// </summary>
public static partial class WorldDecorationGeometry
{
    /// <summary>
    /// Bush heights in player heights, before the placement's own scale.
    ///
    /// Shin-to-shoulder. The ladder is deliberately disjoint from the tree ladder (which starts at 2.3 player
    /// heights): the player must never have to work out whether a silhouette is a small tree or a large bush.
    /// </summary>
    private const double BUSH_UNITS_MIN = 0.5;
    private const double BUSH_UNITS_MAX = 1.2;

    /// <summary>
    /// A shrub: the tree's own grammar, one order of magnitude down.
    ///
    /// Several woody stems leave a common root, arch outward, and each carries foliage along its length — the
    /// exact bole/limb/clump vocabulary the canopy uses, and the exact pigment recipe its world's trees use. That
    /// is what makes a run read as one ecology instead of "trees, plus some green lumps".
    ///
    /// The old thicket was a ring of vertical cones with three toothpick stems that the crowns fully hid. It had
    /// no size ladder, no arch, and its pigment came from a Hub-pine constant, so undergrowth in the reef, the
    /// carnival and the cathedral all looked like the same moss pile.
    /// </summary>
    private static void addBushGeometry(PropGeometryBuilder builder, WorldPropContext ctx)
    {
        // Fluitown comic look (not in the original): the bush grows in the Godot vegetation layer — see FluitownVegetation.
        if (FluitownVegetation.recordBush(builder, ctx)) return;
        WorldDecorationVisual decoration = ctx.decoration;
        double x = ctx.x;
        double z = ctx.z;
        double y0 = ctx.y0;
        double maturity = (double)decoration.variant / 2;
        double units =
            BUSH_UNITS_MIN + (BUSH_UNITS_MAX - BUSH_UNITS_MIN) * (maturity * 0.6 + decoration.phase * 0.4);
        double height = PLAYER_HEIGHT_PX * units * ctx.sizeBias;
        double spread = height * (0.66 + decoration.weathering * 0.34);

        int stems = Math.min(6, 3 + decoration.variant + (decoration.cluster >= 4 ? 1 : 0));
        double stemRadius = height * 0.052;
        double clumpRadius = spread * (0.44 + 0.24 / Math.sqrt(stems)) * (0.9 + decoration.accent * 0.2);
        double clumpHeight = clumpRadius * 1.22;
        double wind = 1.9 + decoration.weathering * 1.3;

        // A bush is low and broad: its proxy is one squat prism at the heart mass.
        ctx.ground(builder, spread * 0.86, height * 0.86, new() { sides = 5, taper = 0.86, alpha = 0.1 });

        for (int index = 0; index < stems; index++)
        {
            double jitter = ctx.hash(index);
            double angle = decoration.rotation + index * 2.39996 + (jitter - 0.5) * 0.6;
            double reach = spread * (0.62 + jitter * 0.38);
            double rise = height * (0.6 + jitter * 0.3);
            double footX = x + Math.cos(angle) * spread * 0.12;
            double footZ = z + Math.sin(angle) * spread * 0.12;
            double midX = footX + Math.cos(angle) * reach * 0.5;
            double midZ = footZ + Math.sin(angle) * reach * 0.5;
            double midY = y0 + rise * 0.62;
            double tipX = midX + Math.cos(angle) * reach * 0.5;
            double tipZ = midZ + Math.sin(angle) * reach * 0.5;
            double tipY = y0 + rise;
            // Two segments, so the stem ARCHES. One straight stick reads as a stake, not as growth.
            propLimb(
                builder,
                footX,
                y0,
                footZ,
                midX,
                midY,
                midZ,
                stemRadius,
                stemRadius * 0.72,
                4,
                angle,
                ctx.barkTop,
                ctx.barkSide,
                0.86,
                0,
                wind * 0.5,
                false);
            propLimb(
                builder,
                midX,
                midY,
                midZ,
                tipX,
                tipY,
                tipZ,
                stemRadius * 0.72,
                stemRadius * 0.4,
                4,
                angle + 0.4,
                ctx.barkTop,
                ctx.barkSide,
                0.86,
                wind * 0.5,
                wind,
                true);
            int tint = mix(ctx.foliage, ctx.tileset.peakTint, jitter * 0.12);
            // Two masses per stem: the arch is long enough that one ball on its end leaves it visibly bare.
            propClump(
                builder,
                midX + (tipX - midX) * 0.34,
                midY + (tipY - midY) * 0.3,
                midZ + (tipZ - midZ) * 0.34,
                clumpRadius * 0.58,
                clumpHeight * 0.58,
                5,
                angle + 1.1,
                tint,
                ctx.foliageSide,
                wind * 0.8,
                1,
                2);
            propClump(
                builder,
                tipX,
                tipY - clumpHeight * 0.42,
                tipZ,
                clumpRadius * (0.82 + jitter * 0.3),
                clumpHeight * (0.85 + jitter * 0.3),
                5 + (index & 1),
                angle + 0.35,
                tint,
                ctx.foliageSide,
                wind);
            // Berries / blossom: the same punctuation the canopy carries, at undergrowth scale.
            if (decoration.accent > 0.54 && index % 2 == 1)
            {
                int bloom = foliageBloomColorFor(
                    ctx.tileset,
                    ctx.options.foliageProfile ?? "natural",
                    decoration.accent);
                propClump(
                    builder,
                    tipX + Math.cos(angle + 0.8) * clumpRadius * 0.5,
                    tipY - clumpHeight * 0.1,
                    tipZ + Math.sin(angle + 0.8) * clumpRadius * 0.36,
                    clumpRadius * 0.24,
                    clumpRadius * 0.3,
                    5,
                    angle,
                    bloom,
                    mix(bloom, ctx.ink, 0.2),
                    wind,
                    1,
                    2);
            }
        }

        // The heart mass ties the stems into one shrub instead of a ring of separate sprigs.
        double heartR = clumpRadius * (1.02 + decoration.cluster * 0.05);
        double heartY = y0 + height * 0.2;
        propClump(
            builder,
            x,
            heartY,
            z,
            heartR,
            clumpHeight * 1.05,
            6,
            decoration.rotation + 0.3,
            mix(ctx.foliage, ctx.ink, 0.06),
            ctx.foliageSide,
            wind * 0.62);
        outlineCap(
            builder,
            x,
            z,
            heartY + clumpHeight * 1.05 + 0.055,
            heartR * 0.44,
            6,
            decoration.rotation + 0.61,
            ctx.tileset.terrain.wallLine,
            0.9,
            0.24);
    }

    /// <summary>
    /// A cut stump.
    ///
    /// It has to read as *this world's tree, felled*: the same buttressed root flare and the same bark, closed by
    /// a pale heartwood cut face with a growth ring on it. The old stump was a plain drum with four root pegs and
    /// no cut face at all, so at gameplay zoom it was indistinguishable from a small rock.
    /// </summary>
    private static void addStumpGeometry(PropGeometryBuilder builder, WorldPropContext ctx)
    {
        WorldDecorationVisual decoration = ctx.decoration;
        double x = ctx.x;
        double z = ctx.z;
        double y0 = ctx.y0;
        // Every stump speaks the canonical tree's bole proportions. A tree-life stump keeps the exact mature
        // specimen that stood here; a free-standing prop deterministically grows a normal specimen from its own
        // placement salt and the world's foliage profile. Neither path may fall back to a height-derived drum:
        // stump height describes the cut, not the trunk's width.
        double smallTreeHeight =
            TREE_SIZE_UNITS.small[0] +
            (TREE_SIZE_UNITS.small[1] - TREE_SIZE_UNITS.small[0]) * decoration.phase;
        // `{ id: ctx.seed, ...createTreeVisual(…) } as const` — the stump's own specimen, rooted with its salt.
        TerrainTreeLike sourceTree =
            ctx.options.felledTree ??
            new TerrainTreeLike(
                ctx.seed,
                createTreeVisual(ctx.seed, ctx.options.foliageProfile, new TreeVisualOverrides
                {
                    sizeClass = "small",
                    heightUnits = smallTreeHeight,
                    scale = smallTreeHeight / TREE_REFERENCE_UNITS,
                    hero = false,
                }));
        double radius = buildTreeSkeleton(sourceTree, treeHeightPx(sourceTree)).baseRadius;
        bool snapped = decoration.variant == 2;
        // The drum stops lower on a snapped stump because its splinters carry the rest of the height.
        double top = y0 + ctx.height * (snapped ? 0.74 : 1);
        ctx.ground(builder, radius, ctx.height, new() { sides = 5, taper = 0.86 });

        // Buttress roots: broad low wedges that reach out and dive into the ground, not pegs standing beside it.
        int roots = 3 + Math.min(3, decoration.cluster - 1);
        for (int i = 0; i < roots; i++)
        {
            double h = ctx.hash(i * 11 + 2);
            double a = ctx.rot + ((double)i / roots) * PROP_TAU + (h - 0.5) * 0.5;
            double reach = radius * (1.15 + h * 0.6);
            propLimb(
                builder,
                x + Math.cos(a) * radius * 0.72,
                y0 + ctx.height * 0.22,
                z + Math.sin(a) * radius * 0.58,
                x + Math.cos(a) * reach,
                y0 + ctx.height * 0.02,
                z + Math.sin(a) * reach * 0.78,
                radius * 0.3,
                radius * 0.12,
                4,
                a,
                ctx.barkTop,
                ctx.barkSide,
                0.84);
        }

        propFrustum(
            builder,
            x,
            z,
            y0,
            top,
            radius * (1.02 + decoration.weathering * 0.1),
            radius * 0.86,
            7,
            ctx.rot,
            ctx.barkTop,
            ctx.barkSide,
            0.78);

        if (snapped)
        {
            // A snapped bole ends in splinters, and that silhouette is the whole difference from a sawn one.
            for (int i = 0; i < 3; i++)
            {
                double h = ctx.hash(i * 23 + 6);
                double a = ctx.rot + i * 2.2 + h;
                propSpike(
                    builder,
                    x + Math.cos(a) * radius * 0.44,
                    top - ctx.height * 0.06,
                    z + Math.sin(a) * radius * 0.34,
                    Math.cos(a) * 0.2,
                    1,
                    Math.sin(a) * 0.2,
                    ctx.height * (0.24 + h * 0.14),
                    radius * (0.2 + h * 0.12),
                    4,
                    a,
                    mix(ctx.palette.paper, ctx.barkTop, 0.3),
                    ctx.barkSide,
                    0.8);
            }
        }
        else
        {
            // The cut face: pale heartwood, a bark rim and one growth ring. This is what says "felled".
            int heartwood = mix(ctx.palette.paper, ctx.barkTop, 0.16 + decoration.weathering * 0.16);
            propFrustum(
                builder,
                x,
                z,
                top - ctx.height * 0.03,
                top + ctx.height * 0.012,
                radius * 0.86,
                radius * 0.82,
                7,
                ctx.rot,
                heartwood,
                ctx.barkSide,
                0.86);
            outlineCap(
                builder,
                x,
                z,
                top + ctx.height * 0.012 + 0.05,
                radius * 0.78,
                7,
                ctx.rot,
                ctx.ink,
                1.15,
                0.34);
            outlineCap(
                builder,
                x,
                z,
                top + ctx.height * 0.012 + 0.07,
                radius * 0.44,
                7,
                ctx.rot + 0.3,
                mix(ctx.barkSide, ctx.ink, 0.2),
                0.85,
                0.24);
        }

        // Regrowth or shelf fungus. A stump in a living world is never inert.
        if (decoration.accent > 0.42)
        {
            for (int i = 0; i < 2; i++)
            {
                double h = ctx.hash(i * 31 + 19);
                double a = ctx.rot + 1.3 + i * 2.6 + h;
                propClump(
                    builder,
                    x + Math.cos(a) * radius * 0.95,
                    y0 + ctx.height * (0.2 + h * 0.24),
                    z + Math.sin(a) * radius * 0.74,
                    radius * (0.3 + h * 0.16),
                    ctx.height * 0.16,
                    5,
                    a,
                    ctx.foliage,
                    ctx.foliageSide,
                    1.2,
                    1,
                    2);
            }
        }
    }

    /// <summary>
    /// The one geometry vocabulary for every authored and generated short prop. Terrain-derived pigment, a contact
    /// patch and one ink cap line make each object read as part of the same illustrated ecology at gameplay zoom.
    /// Theme changes pigment only; it never swaps in a special prop model.
    /// </summary>
    public static void addWorldDecorationGeometry(
        PropGeometryBuilder builder,
        WorldDecorationGeometryOptions options)
    {
        WorldPropContext ctx = worldPropContext(options);
        switch (options.decoration.kind)
        {
            case "thicket":
                addBushGeometry(builder, ctx);
                return;
            case "stump":
                addStumpGeometry(builder, ctx);
                return;
        }
    }
}
