// Port of packages/client/src/render/environment/terrainBakePigment.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Fluitown.Domain;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class TerrainWallPigmentProfile
{
    /// <summary>`TerrainSurfaceProfile['formB']`: (domain warp px, directed-ridge gain, terrace gain, directed-ridge period px).</summary>
    public double[] formB;
    /// <summary>Linear RGB biome poles; the bake writes linear vertex colours.</summary>
    public double[] crest;
    public double[] deep;
    public double[] sediment;
}

public static partial class TerrainBakePigment
{
    private static readonly TerrainWallPigmentProfile FALLBACK_PROFILE = new TerrainWallPigmentProfile
    {
        formB = new double[] { 42, 0.08, 0.08, 168 },
        crest = linearRgb(0xaeb9ae),
        deep = linearRgb(0x46534e),
        sediment = linearRgb(0x718064),
    };

    // ── Module-level mutable scratch (one per JS worker → one per thread) ─────────────────────────────────

    private sealed class TerrainSpatialShadeEffect
    {
        public double x;
        public double y;
        public double radius;
        public double strength;
    }

    private sealed class TerrainSpatialReflectionTree
    {
        public double x;
        public double y;
        public double mass;
    }

    /// <summary>All three maps are lookup-only (never iterated), hence plain dictionaries.</summary>
    private sealed class TerrainSpatialEffectIndex
    {
        public Dictionary<int, List<TerrainSpatialShadeEffect>> shade;
        public Dictionary<int, List<TerrainSpatialReflectionTree>> reflection;
        /// <summary>Keyed by the water surface Z (a JS number, SameValueZero: .NET double equality/hash agree on ±0 and NaN).</summary>
        public Dictionary<int, Dictionary<double, double>> waterReflectionHints;
    }

    /// <summary>`new WeakMap&lt;TerrainRenderPlan, TerrainSpatialEffectIndex&gt;()` — per worker in the original, so per thread here.</summary>
    [ThreadStatic] private static ConditionalWeakTable<TerrainRenderPlan, TerrainSpatialEffectIndex>? _TERRAIN_SPATIAL_EFFECT_CACHE;

    private static ConditionalWeakTable<TerrainRenderPlan, TerrainSpatialEffectIndex> TERRAIN_SPATIAL_EFFECT_CACHE =>
        _TERRAIN_SPATIAL_EFFECT_CACHE ??= new ConditionalWeakTable<TerrainRenderPlan, TerrainSpatialEffectIndex>();

    private const double TREE_REFLECTION_RADIUS = 2.65;

    /// <summary>Signed 16-bit local terrain coordinates are ample for one bake frame and avoid allocating string keys in
    /// the per-vertex pigment path.</summary>
    private static int terrainSpatialBucketKey(double x, double y)
    {
        return (Js.ToInt32(x) & 0xffff) | ((Js.ToInt32(y) & 0xffff) << 16);
    }

    private static void addTerrainSpatialEffect<T>(
        Dictionary<int, List<T>> buckets,
        T effect,
        double x,
        double y,
        double radius)
    {
        double minX = Math.floor(x - radius);
        double maxX = Math.floor(x + radius);
        double minY = Math.floor(y - radius);
        double maxY = Math.floor(y + radius);
        for (double bucketY = minY; bucketY <= maxY; bucketY++)
        {
            for (double bucketX = minX; bucketX <= maxX; bucketX++)
            {
                int key = terrainSpatialBucketKey(bucketX, bucketY);
                if (buckets.TryGetValue(key, out var bucket)) bucket.push(effect);
                else buckets[key] = new List<T> { effect };
            }
        }
    }

    /// <summary>
    /// Cap AO and water reflections previously walked every tree/world prop for every emitted vertex. Dense tree
    /// dressing therefore made mesh compilation quadratic. Build one immutable, plan-local broad phase instead.
    /// Effects are inserted in the exact legacy category/array order, so candidate evaluation retains the same
    /// additive order, radius, strength and final visual result; only effects whose influence cannot reach the
    /// queried unit bucket are omitted.
    /// </summary>
    private static TerrainSpatialEffectIndex terrainSpatialEffectIndex(MaterializedTerrain terrain, TerrainRenderPlan plan)
    {
        if (TERRAIN_SPATIAL_EFFECT_CACHE.TryGetValue(plan, out var cached)) return cached;

        var shade = new Dictionary<int, List<TerrainSpatialShadeEffect>>();
        var reflection = new Dictionary<int, List<TerrainSpatialReflectionTree>>();
        // `addShadeEffects(effects, strength, trees)` walks `{ id, ox, oy, scale? }` records; the two callers pass
        // differently typed lists, so the per-record body is this local function and the loops live below.
        void addShadeEffect(double id, double ox, double oy, double scale, double strength, TerrainTreeDressingEffect? tree)
        {
            // `terrain.cells[source.id]` — undefined for a missing/out-of-range id.
            var anchor = id >= 0 && id < terrain.cells.Length && Number.isInteger(id) ? terrain.cells[(int)id] : null;
            if (anchor == null) return;
            double x = anchor.x + ox;
            double y = anchor.y + oy;
            // `(source.scale ?? 1)`: both effect families carry a required `scale`.
            double radius = 0.24 + Math.min(0.18, scale * 0.055);
            var shadeEffect = new TerrainSpatialShadeEffect { x = x, y = y, radius = radius, strength = strength };
            addTerrainSpatialEffect(shade, shadeEffect, x, y, radius);
            if (tree == null) return;
            var reflectionTree = new TerrainSpatialReflectionTree
            {
                x = x,
                y = y,
                mass = clamp(0.36 + tree.scale * 0.22 + tree.height * 0.12, 0.36, 0.82),
            };
            addTerrainSpatialEffect(reflection, reflectionTree, x, y, TREE_REFLECTION_RADIUS);
        }
        // addShadeEffects(plan.effects.trees, 0.115, true);
        foreach (var source in plan.effects.trees) addShadeEffect(source.id, source.ox, source.oy, source.scale, 0.115, source);
        // addShadeEffects(plan.effects.worldDecorations, 0.09);
        foreach (var source in plan.effects.worldDecorations) addShadeEffect(source.id, source.ox, source.oy, source.scale, 0.09, null);

        var index = new TerrainSpatialEffectIndex
        {
            shade = shade,
            reflection = reflection,
            waterReflectionHints = new Dictionary<int, Dictionary<double, double>>(),
        };
        TERRAIN_SPATIAL_EFFECT_CACHE.AddOrUpdate(plan, index);
        return index;
    }

    public static TerrainWallPigmentProfile createTerrainWallPigmentProfile(
        TerrainSurfaceProfile form,
        int crest,
        int deep,
        int sediment)
    {
        return new TerrainWallPigmentProfile
        {
            formB = form.formB,
            crest = linearRgb(crest),
            deep = linearRgb(deep),
            sediment = linearRgb(sediment),
        };
    }

    public static TerrainWallPigmentProfile fallbackTerrainWallPigmentProfile()
    {
        return FALLBACK_PROFILE;
    }

    /// <summary>
    /// Write one stratified wall colour into `out`.
    ///
    /// The base material remains dominant. Broad vertical value structure, narrow laid-wash strata and the
    /// low sediment/moss deposit are all bake-time vertex colour work: no shader family, texture, pass or draw is
    /// introduced. `height01` is zero at the wall foot and one at the crest.
    /// </summary>
    /// <param name="out">`[number, number, number]`.</param>
    public static void terrainWallPigmentInto(
        double[] @out,
        double baseR,
        double baseG,
        double baseB,
        double worldX,
        double worldY,
        double worldZ,
        double height01,
        double edge01,
        TerrainWallPigmentProfile profile)
    {
        double h = clamp01(height01);
        double edge = clamp01(edge01);
        double warp = profile.formB[0], ridge = profile.formB[1], terrace = profile.formB[2], directedPeriod = profile.formB[3];
        double bandPeriod = clamp(9.5 + terrace * 12 + ridge * 2.5, 9.5, 16);
        double directed =
            (worldX * Math.cos(directedPeriod * 0.006) + worldZ * Math.sin(directedPeriod * 0.006)) /
            Math.max(24, directedPeriod);
        double phase =
            worldY / bandPeriod +
            directed * (0.34 + ridge * 0.52) +
            wallHash(worldX / Math.max(18, warp), worldZ / Math.max(18, warp)) * 0.22;
        double stripeDistance = Math.abs(fract(phase) - 0.5) * 2;
        double strataInk = stripeDistance < 0.18 ? 0.82 + (stripeDistance / 0.18) * 0.12 : 1;
        double verticalWash = 0.83 + h * 0.2;
        // Shared vertical joins and geological inner corners receive restrained occlusion without black ink.
        double cornerAo = 1 - (1 - edge) * (0.025 + ridge * 0.025);
        double foot = clamp01((0.24 - h) / 0.24);
        double footAo = 1 - foot * (0.1 + Math.min(0.055, ridge * 0.07));
        double scale = strataInk * verticalWash * cornerAo * footAo;

        double heightTint = 0.06 + h * 0.07;
        double[] tint = h > 0.52 ? profile.crest : profile.deep;
        double r = baseR + (tint[0] - baseR) * heightTint;
        double g = baseG + (tint[1] - baseG) * heightTint;
        double b = baseB + (tint[2] - baseB) * heightTint;
        double sedimentMix = foot * (0.12 + terrace * 0.12);
        r += (profile.sediment[0] - r) * sedimentMix;
        g += (profile.sediment[1] - g) * sedimentMix;
        b += (profile.sediment[2] - b) * sedimentMix;
        double outR = r * scale;
        double outG = g * scale;
        double outB = b * scale;
        // A wall wash may change hue and darken into strata, but must never become a brighter raster sliver than
        // the already reviewed wall pigment. That exact failure mode exposed the pale backdrop at edge-on cliff
        // returns in the historic White-Cliff regression.
        double baseLuminance = baseR * 0.2126 + baseG * 0.7152 + baseB * 0.0722;
        double outLuminance = outR * 0.2126 + outG * 0.7152 + outB * 0.0722;
        double luminanceCeiling = baseLuminance * 1.015;
        if (outLuminance > luminanceCeiling)
        {
            double safeScale = luminanceCeiling / Math.max(0.000001, outLuminance);
            outR *= safeScale;
            outG *= safeScale;
            outB *= safeScale;
        }
        @out[0] = outR;
        @out[1] = outG;
        @out[2] = outB;
    }

    /// <summary>
    /// Static ambient occlusion and broad ground tooth for one cap vertex. The output is a colour multiplier, kept
    /// deliberately above paper-black so the wall/floor luminance contract survives every palette.
    /// </summary>
    public static double terrainCapBakeShade(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        TerrainCell cell,
        double worldCellX,
        double worldCellY,
        double u,
        double v,
        double baseShade)
    {
        double worldX = worldCellX + u;
        double worldY = worldCellY + v;
        double frameOriginX = worldCellX - cell.x;
        double frameOriginY = worldCellY - cell.y;
        double terrainX = cell.x + u;
        double terrainY = cell.y + v;
        double occlusion = 0;
        // Sample high mass by distance to its real square footprint. The former cardinal/diagonal terms were
        // expressed in the OWNING cell's (u,v) frame. At a shared ground vertex, three floor cells therefore wrote
        // three different AO values; raster interpolation exposed their topology as dark triangles around terrace
        // corners. This field depends only on the absolute terrain point and surrounding height mass, so duplicate
        // vertices — including clipped-corner helper polygons — now receive exactly the same shade.
        int sampleCellX = (int)Math.floor(terrainX);
        int sampleCellY = (int)Math.floor(terrainY);
        for (int candidateY = sampleCellY - 1; candidateY <= sampleCellY + 1; candidateY++)
        {
            for (int candidateX = sampleCellX - 1; candidateX <= sampleCellX + 1; candidateX++)
            {
                var candidate = TerrainModel.terrainCellAt(terrain, candidateX, candidateY);
                if (candidate == null) continue;
                double rise = candidate.surfaceZ - cell.surfaceZ;
                if (rise <= 0.08 && !(candidate.type == TileType.Solid && cell.type != TileType.Solid))
                    continue;
                double distanceX = Math.max(candidate.x - terrainX, 0, terrainX - (candidate.x + 1));
                double distanceY = Math.max(candidate.y - terrainY, 0, terrainY - (candidate.y + 1));
                if (distanceX * distanceX + distanceY * distanceY >= 1) continue;
                double distance = Math.hypot(distanceX, distanceY);
                double proximity = smooth01(1 - clamp01(distance));
                occlusion += proximity * clamp(0.035 + rise * 0.032, 0.035, 0.13);
            }
        }

        terrainSpatialEffectIndex(terrain, plan).shade.TryGetValue(
            terrainSpatialBucketKey(Math.floor(terrainX), Math.floor(terrainY)),
            out var propEffects);
        if (propEffects != null)
        {
            foreach (var effect in propEffects)
            {
                double deltaX = worldX - (frameOriginX + effect.x);
                double deltaY = worldY - (frameOriginY + effect.y);
                if (deltaX * deltaX + deltaY * deltaY >= effect.radius * effect.radius) continue;
                double distance = Math.hypot(deltaX, deltaY);
                occlusion +=
                    (1 - smoothRange(effect.radius * 0.35, effect.radius, distance)) * effect.strength;
            }
        }

        // Smooth absolute-world washes replace the old independently hashed vertex tooth. Both cells that duplicate
        // a shared vertex now receive the same value and the field has a continuous first derivative between lattice
        // samples, so directional light/ink cannot reconstruct the implementation grid from tiny shade jumps.
        double broad = smoothBakeNoise(worldX / 3.8, worldY / 3.8, 0x51f1) - 0.5;
        double middle = smoothBakeNoise(worldX / 1.45 + 17, worldY / 1.45 - 29, 0x85eb) - 0.5;
        double micro = 1 + broad * 0.045 + middle * 0.022;
        return baseShade * micro * clamp(1 - occlusion, 0.68, 1);
    }

    /// <summary>
    /// Broad shoreline silhouette approximation sampled at a logical water vertex. Solid/high banks and nearby
    /// tree anchors contribute a dark reflected mass; because the function keys from the absolute grid sample,
    /// independently baked cells/chunks agree at shared corners.
    /// </summary>
    public static double terrainWaterReflectionHintAt(
        MaterializedTerrain terrain,
        TerrainRenderPlan plan,
        double sampleX,
        double sampleY,
        double waterSurfaceZ)
    {
        var spatialIndex = terrainSpatialEffectIndex(terrain, plan);
        bool cacheable = Number.isInteger(sampleX) && Number.isInteger(sampleY);
        int sampleKey = cacheable ? terrainSpatialBucketKey(sampleX, sampleY) : 0;
        Dictionary<double, double>? heightHints = null;
        if (cacheable) spatialIndex.waterReflectionHints.TryGetValue(sampleKey, out heightHints);
        if (heightHints != null && heightHints.TryGetValue(waterSurfaceZ, out double cachedHint)) return cachedHint;

        double hint = 0;
        int minX = (int)Math.floor(sampleX) - 2;
        int maxX = (int)Math.floor(sampleX) + 2;
        int minY = (int)Math.floor(sampleY) - 2;
        int maxY = (int)Math.floor(sampleY) + 2;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var candidate = TerrainModel.terrainCellAt(terrain, x, y);
                // A Water-span Bridge is transparent to the liquid's broad reflection field. Counting the deck carrier
                // as a nearby cliff made reflection strength jump at the exact Bridge footprint and produced a dark,
                // cell-aligned rectangle even though the water geometry and world-space wave phase were continuous.
                if (candidate == null || TerrainModel.terrainCellCarriesWater(candidate)) continue;
                double deltaX = x + 0.5 - sampleX;
                double deltaY = y + 0.5 - sampleY;
                if (deltaX * deltaX + deltaY * deltaY > 2.35 * 2.35) continue;
                double distance = Math.hypot(deltaX, deltaY);
                double rise = candidate.surfaceZ - waterSurfaceZ;
                double solid = candidate.type == TileType.Solid ? 1 : 0;
                double mass = Math.max(solid * 0.62, clamp01(rise * 0.22));
                hint = Math.max(hint, mass * (1 - smoothRange(0.45, 2.35, distance)));
            }
        }
        spatialIndex.reflection.TryGetValue(
            terrainSpatialBucketKey(Math.floor(sampleX), Math.floor(sampleY)),
            out var reflectionTrees);
        if (reflectionTrees != null)
        {
            foreach (var tree in reflectionTrees)
            {
                double deltaX = tree.x - sampleX;
                double deltaY = tree.y - sampleY;
                if (deltaX * deltaX + deltaY * deltaY > TREE_REFLECTION_RADIUS * TREE_REFLECTION_RADIUS)
                    continue;
                double distance = Math.hypot(deltaX, deltaY);
                hint = Math.max(hint, tree.mass * (1 - smoothRange(0.35, TREE_REFLECTION_RADIUS, distance)));
            }
        }
        hint = clamp01(hint);
        if (cacheable)
        {
            if (heightHints != null) heightHints[waterSurfaceZ] = hint;
            else spatialIndex.waterReflectionHints[sampleKey] = new Dictionary<double, double> { [waterSurfaceZ] = hint };
        }
        return hint;
    }

    private static double[] linearRgb(int hex)
    {
        double channel(int value)
        {
            double c = (double)value / 255;
            return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
        }
        return new[] { channel((hex >> 16) & 0xff), channel((hex >> 8) & 0xff), channel(hex & 0xff) };
    }

    private static double wallHash(double x, double z)
    {
        double ix = Math.floor(x * 17);
        double iz = Math.floor(z * 17);
        int value = Math.imul(Js.ToInt32(ix) ^ 0x51f15e5d, 0x1b873593) ^ Math.imul(Js.ToInt32(iz), 0x85ebca6b);
        value = Math.imul(value ^ (int)((uint)value >> 15), 0xc2b2ae35);
        return (uint)(value ^ (int)((uint)value >> 16)) / 4294967296.0;
    }

    private static double bakeLatticeHash(double x, double z, double salt)
    {
        int value =
            Math.imul(Js.ToInt32(x) ^ Js.ToInt32(salt) ^ 0x51f15e5d, 0x1b873593) ^
            Math.imul(Js.ToInt32(z + salt * 17), 0x85ebca6b);
        value = Math.imul(value ^ (int)((uint)value >> 15), 0xc2b2ae35);
        return (uint)(value ^ (int)((uint)value >> 16)) / 4294967296.0;
    }

    private static double smoothBakeNoise(double x, double z, double salt)
    {
        double ix = Math.floor(x);
        double iz = Math.floor(z);
        double fx = x - ix;
        double fz = z - iz;
        double ux = fx * fx * (3 - 2 * fx);
        double uz = fz * fz * (3 - 2 * fz);
        double a = bakeLatticeHash(ix, iz, salt);
        double b = bakeLatticeHash(ix + 1, iz, salt);
        double c = bakeLatticeHash(ix, iz + 1, salt);
        double d = bakeLatticeHash(ix + 1, iz + 1, salt);
        return (a + (b - a) * ux) * (1 - uz) + (c + (d - c) * ux) * uz;
    }

    private static double fract(double value)
    {
        return value - Math.floor(value);
    }

    private static double clamp(double value, double min, double max)
    {
        return value < min ? min : value > max ? max : value;
    }

    private static double clamp01(double value)
    {
        return clamp(value, 0, 1);
    }

    private static double smooth01(double value)
    {
        double t = clamp01(value);
        return t * t * (3 - 2 * t);
    }

    private static double smoothRange(double lo, double hi, double value)
    {
        return smooth01((value - lo) / Math.max(0.0001, hi - lo));
    }
}
