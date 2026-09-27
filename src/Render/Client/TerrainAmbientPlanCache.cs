// Port of packages/client/src/terrainAmbientPlanCache.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Fluitown.Domain;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// Compact part of a terrain plan needed by the ambient renderer after the allocation-heavy shared planning
/// pass. Endless outer chunks are prepared in the generation worker and primed here before they are published
/// to the render world, so a newly visible chunk never materializes a complete terrain graph on the main
/// thread. The WeakMap follows the immutable layout lifetime and needs no eviction bookkeeping.
/// </summary>
public sealed class PreparedTerrainAmbientPlan
{
    public float[] surfaceZ = Array.Empty<float>();
    public TerrainProceduralEffects effects = null!;
    /// <summary>The shared plan's cascade channel — volumetric plunge mist anchors to the same authored falls.</summary>
    public List<TerrainWaterfallEffect> waterfalls = new();
}

public static class TerrainAmbientPlanCache
{
    // `new WeakMap<DungeonLayout, Map<string, PreparedTerrainAmbientPlan>>()` → ConditionalWeakTable (keys are held
    // weakly, exactly like the WeakMap). THREAD SAFETY: in the browser this cache lives only in the main realm;
    // the worker realms import the module solely for `terrainAmbientPlanCacheKey`/`terrainAmbientEffectSeed`.
    // In one C# process the table is deliberately shared (a [ThreadStatic] copy would hide primed plans from the
    // renderer), so every access to the inner per-layout map goes through `preparedPlansLock`. The inner maps are
    // never iterated, so a Dictionary is exact.
    private static readonly ConditionalWeakTable<DungeonLayout, Dictionary<string, PreparedTerrainAmbientPlan>> preparedPlans = new();
    private static readonly object preparedPlansLock = new();

    public static string terrainAmbientPlanCacheKey(string instanceId, string biomeKey)
    {
        return $"{instanceId}:{biomeKey}";
    }

    /// <returns>`layoutSeed ^ stableStringHash(instanceId)` — a signed int32 as a JS number.</returns>
    public static double terrainAmbientEffectSeed(string instanceId, double layoutSeed)
    {
        return Js.ToInt32(layoutSeed) ^ stableStringHash(instanceId);
    }

    public static PreparedTerrainAmbientPlan? getPreparedTerrainAmbientPlan(DungeonLayout layout, string cacheKey)
    {
        lock (preparedPlansLock)
        {
            if (!preparedPlans.TryGetValue(layout, out Dictionary<string, PreparedTerrainAmbientPlan>? byContext)) return null;
            return byContext.TryGetValue(cacheKey, out PreparedTerrainAmbientPlan? prepared) ? prepared : null;
        }
    }

    /// <summary>
    /// Forget every prepared plan for a layout, so the next request recomputes it.
    ///
    /// The cache is keyed on layout **identity** because a streamed chunk is immutable, and for streamed chunks
    /// that is exactly right. An authored world is not immutable: the map generator holds one layout object for
    /// the whole of a build and rewrites its cells in place, which is deliberate — reinstalling the layout every
    /// frame would reinstall the stream too. The consequence was that the ambient plan was computed once, against
    /// the world as it stood *before the build began*, and then kept forever: no water existed yet, so no fish
    /// were ever placed, and the sky life was anchored to ground that had since become rock.
    ///
    /// A mutable layout therefore needs a way to say "my cells moved". This is it.
    /// </summary>
    public static void dropPreparedTerrainAmbientPlans(DungeonLayout layout)
    {
        lock (preparedPlansLock) preparedPlans.Remove(layout);
    }

    public static void primePreparedTerrainAmbientPlan(
        DungeonLayout layout,
        string cacheKey,
        PreparedTerrainAmbientPlan prepared)
    {
        lock (preparedPlansLock)
        {
            if (!preparedPlans.TryGetValue(layout, out Dictionary<string, PreparedTerrainAmbientPlan>? byContext))
            {
                byContext = new Dictionary<string, PreparedTerrainAmbientPlan>();
                preparedPlans.Add(layout, byContext);
            }
            byContext[cacheKey] = prepared;
        }
    }

    private static int stableStringHash(string value)
    {
        int hash = unchecked((int)0x811c9dc5);
        for (int index = 0; index < value.Length; index++)
        {
            hash ^= value[index];
            hash = Math.imul(hash, 0x01000193);
        }
        return hash;
    }
}
