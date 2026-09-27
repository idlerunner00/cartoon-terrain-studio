// Port of packages/client/src/terrainAmbientPlanWorker.ts — keep in lockstep with the original.
//
// PORT NOTE: a Web Worker script; its `workerScope.onmessage` body is the pure function
// <see cref="TerrainAmbientPlanWorker.onmessage"/>, run by a `ThreadedClientWorker`/`InlineClientWorker` lane (see
// ClientWorkers.cs). The browser structured-clones `request.layout` into the worker; here the worker thread reads the
// main thread's layout object directly. That is safe because a published layout is only ever read after it
// arrives — the only in-place writers are `ClientDungeon.applyChunkPatches` (server patch lane, absent in the port)
// and editor builds of an authored layout, which call `refreshAuthoredAmbientPlan` afterwards.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fluitown.Domain;
using Fluitown.Runtime;
using static Fluitown.Render.TerrainAmbientPlanCache;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public sealed class TerrainAmbientPlanWorkerRequest
{
    public int id;
    /// <summary>Always `'prepare-ambient'`.</summary>
    public string kind = "prepare-ambient";
    public int cx;
    public int cy;
    public string ambientInstanceId = "";
    public string ambientBiomeKey = "";
    public DungeonLayout layout = null!;
}

public sealed class TerrainAmbientPlanWorkerResponse
{
    public int id;
    /// <summary>Always `'prepare-ambient'`.</summary>
    public string kind = "prepare-ambient";
    public int cx;
    public int cy;
    public double durationMs;
    public double ambientDurationMs;
    public string ambientCacheKey = "";
    public float[] ambientSurfaceZ = Array.Empty<float>();
    public TerrainProceduralEffects ambientEffects = null!;
    public List<TerrainWaterfallEffect> ambientWaterfalls = new();
}

/// <summary>Ambient-plan-only worker. Finite/Hub worlds must never download the Endless generator to prepare dressing.</summary>
public static class TerrainAmbientPlanWorker
{
    public static TerrainAmbientPlanWorkerResponse onmessage(TerrainAmbientPlanWorkerRequest request)
    {
        long started = Stopwatch.GetTimestamp();
        DungeonLayout layout = request.layout;
        double baseTx = Math.round(layout.originX / layout.tileSize);
        double baseTy = Math.round(layout.originY / layout.tileSize);
        MaterializedTerrain terrain = TerrainModel.materializeTerrainGrid(
            layout.tiles,
            layout.width,
            layout.height,
            layout.elevation,
            new TerrainModelOptions
            {
                // `baseTx + tx` is an integral JS number; the C# wall-rise rule takes the tile coordinate as int.
                solidWallRiseAt = (tx, ty, stored) => TerrainRules.endlessWallRiseAt((int)(baseTx + tx), (int)(baseTy + ty), stored),
            });
        terrain.decorations = layout.terrain?.decorations?.map((decoration) => decoration.Clone());
        terrain.floorUsage = layout.terrain?.floorUsage;
        TerrainProceduralEffects ambientEffects = TerrainRenderPlanModule.createTerrainProceduralEffectsPlan(terrain, new TerrainRenderPlanOptions
        {
            // `request.ambientBiomeKey || layout.biomeKey`: an empty string is falsy.
            biomeKey = !string.IsNullOrEmpty(request.ambientBiomeKey) ? request.ambientBiomeKey : layout.biomeKey,
            effectSeed = terrainAmbientEffectSeed(request.ambientInstanceId, layout.seed),
            originCellX = layout.originX / layout.tileSize,
            originCellY = layout.originY / layout.tileSize,
        });
        List<TerrainWaterfallEffect> ambientWaterfalls = TerrainRenderPlanModule.createTerrainWaterfalls(terrain);
        var ambientSurfaceZ = new float[terrain.cells.Length];
        for (int index = 0; index < terrain.cells.Length; index++)
            ambientSurfaceZ[index] = (float)(terrain.cells[index]?.surfaceZ ?? 0);
        double finishedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // `postMessage(response, [ambientSurfaceZ.buffer])`: the surface array changes owner here.
        return new TerrainAmbientPlanWorkerResponse
        {
            id = request.id,
            kind = "prepare-ambient",
            cx = request.cx,
            cy = request.cy,
            durationMs = 0,
            ambientDurationMs = finishedMs,
            ambientCacheKey = terrainAmbientPlanCacheKey(
                request.ambientInstanceId,
                !string.IsNullOrEmpty(request.ambientBiomeKey) ? request.ambientBiomeKey : layout.biomeKey),
            ambientSurfaceZ = ambientSurfaceZ,
            ambientEffects = ambientEffects,
            ambientWaterfalls = ambientWaterfalls,
        };
    }
}
