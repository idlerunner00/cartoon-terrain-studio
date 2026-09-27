// Port of packages/shared/src/domain/dungeon/analysis.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Inside Fluitown.Domain a bare `Js` binds to the namespace Fluitown.Runtime (outer-namespace members are searched
// before compilation-unit usings); this namespace-level alias makes it the runtime class.
using Js = Fluitown.Runtime.Js;

/// <summary>
/// TS `TerrainAnalysisOptions`. Footprint and radius are tile counts; they are <c>int?</c> here, as
/// <see cref="TerrainValidationOptions"/> already types the same knobs (a fractional value is not a meaningful input
/// in TS either). Use <see cref="Analysis.analysisOptionsFromValidation"/> where TS passes a
/// <c>TerrainValidationOptions</c> structurally (e.g. the editor's `analyzeTerrainLayout(layout, VALIDATION_OPTIONS)`).
/// </summary>
public sealed class TerrainAnalysisOptions
{
    public double? maxClimb;
    public int? minimumFootprint;
    public int? minimumBridgeWidth;
    /// <summary>Override the substantial-water-body threshold for settings with authored narrow stream crossings.</summary>
    public double? minimumBridgeBodyCells;
    public int? openRadius;
    /// <summary>Ignore diagnostics inside this many cells from the sampled grid edge; useful for stitched finite windows.</summary>
    public double? diagnosticInset;
    /// <summary>Marks a bridge as required by an external navigation contract (for Endless: a shared seam port).</summary>
    public Func<int, int, bool>? requiredBridgeAt;
}

public sealed class TerrainAnalysisMetrics
{
    public int cells;
    public int walkable;
    public int reachable;
    public int disconnectedWalkable;
    public int steepWalkableEdges;
    public int cleftCells;
    public int underpassCells;
    public int bridgeCells;
    public int bridgeComponents;
    public int bridgeWidthIssues;
    public int bridgeWaterOverlapCells;
    public double bridgeWaterClearanceMin;
    public int bridgeUndersizedWaterBodies;
    public int bridgeRedundantComponents;
    public int bridgeUnanchoredComponents;
    public int bridgeDryComponents;
    public int waterCells;
    public int waterSurfaceCells;
    public int waterComponents;
    public int waterSmallComponents;
    public int waterMinimumBodyCells;
    public int chasmCells;
    public int chasmComponents;
    public int chasmSmallComponents;
    public int chasmNarrowComponents;
    public int chasmCoreCells;
    public double chasmCoreFraction;
    public int chasmCellsFarFromCore;
    public int floorChasmEdges;
    public int waterStepEdges;
    public int waterfallEdges;
    public int deadEnds;
    public int chokePoints;
    public int openCells;
    public int narrowFootprints;
    public int cliffEdges;
}

public sealed class TerrainAnalysisSample
{
    public int tx;
    public int ty;
    public string kind = "";
    public string? detail;
}

/// <summary>Per-cell 1/0 masks (`Uint8Array` in TS), row-major, `width * height` long.</summary>
public sealed class TerrainAnalysisOverlays
{
    /// <summary>Walkable cells reached by the climbable flood from the first walkable cell (row-major scan).</summary>
    public byte[] reachable;
    /// <summary>Walkable cells with ≤1 climbable cardinal neighbour.</summary>
    public byte[] deadEnds;
    /// <summary>Walkable cells with exactly 2 climbable cardinal neighbours.</summary>
    public byte[] chokePoints;
    /// <summary>Walkable cells whose (2r+1)² neighbourhood is walkable except for at most two cells.</summary>
    public byte[] openCells;
    /// <summary>Walkable cells not covered by any fully walkable minimumFootprint × minimumFootprint square.</summary>
    public byte[] narrowFootprints;
    /// <summary>Both cells of every walkable↔walkable edge whose connection kind is 'cliff'.</summary>
    public byte[] steepEdges;
    public byte[] bridgeWidthIssues;
    /// <summary>Bridge decks whose modelled Water span is missing or not safely below the deck.</summary>
    public byte[] bridgeWaterIssues;
    public byte[] bridgeUseIssues;
    public byte[] waterIssues;
    public byte[] chasmIssues;
    /// <summary>Both cells of every non-'cliff' edge with |delta| &gt; maxClimb touching a walkable cell.</summary>
    public byte[] cliffEdges;
}

public sealed class TerrainAnalysis
{
    public int width;
    public int height;
    public TerrainAnalysisMetrics metrics;
    public TerrainAnalysisOverlays overlays;
    public List<TerrainAnalysisSample> samples;
}

public static class Analysis
{
    private static readonly (int dx, int dy)[] CARDINAL_DIRS = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly (int dx, int dy)[] FORWARD_DIRS = { (1, 0), (0, 1) };

    // ── C# port helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C# addition. TS passes a <c>TerrainValidationOptions</c> where a <c>TerrainAnalysisOptions</c> is expected
    /// (structural typing); the fields the two interfaces share by name are <c>maxClimb</c>,
    /// <c>minimumFootprint</c> and <c>minimumBridgeWidth</c>, which is exactly what this copies.
    /// </summary>
    public static TerrainAnalysisOptions analysisOptionsFromValidation(TerrainValidationOptions? options)
    {
        if (options == null) return new TerrainAnalysisOptions();
        return new TerrainAnalysisOptions
        {
            maxClimb = options.maxClimb,
            minimumFootprint = options.minimumFootprint,
            minimumBridgeWidth = options.minimumBridgeWidth,
        };
    }

    // ── analysis.ts ───────────────────────────────────────────────────────────────────────────────────

    private static int levelAt(sbyte[]? elevation, int idx)
    {
        return elevation != null && (uint)idx < (uint)elevation.Length ? elevation[idx] : 0;
    }

    // TS builds a fresh `{ maxClimb }` literal per call; the C# port passes one shared, never-mutated instance.
    private static int climbableNeighbours(MaterializedTerrain terrain, int idx, TerrainConnectionOptions climb)
    {
        var tiles = terrain.tiles;
        int width = terrain.width;
        int height = terrain.height;
        int tx = idx % width;
        int ty = idx / width;
        int n = 0;
        foreach (var (dx, dy) in CARDINAL_DIRS)
        {
            int nx = tx + dx;
            int ny = ty + dy;
            if (!inBounds(width, height, nx, ny)) continue;
            int ni = tileIndex(width, nx, ny);
            if (!isWalkable(tiles[ni])) continue;
            if (TerrainRules.terrainMoveBlockedInMaterialized(terrain, tx, ty, nx, ny, climb)) continue;
            n++;
        }
        return n;
    }

    private static int firstWalkable(byte[] tiles)
    {
        for (int i = 0; i < tiles.Length; i++) if (isWalkable(tiles[i])) return i;
        return -1;
    }

    private static byte[] floodClimbable(MaterializedTerrain terrain, TerrainConnectionOptions climb)
    {
        var tiles = terrain.tiles;
        int width = terrain.width;
        int height = terrain.height;
        var reached = new byte[tiles.Length];
        int start = firstWalkable(tiles);
        if (start < 0) return reached;
        var queue = new int[tiles.Length];
        int head = 0;
        int tail = 0;
        reached[start] = 1;
        queue[tail++] = start;
        while (head < tail)
        {
            int idx = queue[head++];
            int tx = idx % width;
            int ty = idx / width;
            foreach (var (dx, dy) in CARDINAL_DIRS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int ni = tileIndex(width, nx, ny);
                if (reached[ni] != 0 || !isWalkable(tiles[ni])) continue;
                if (TerrainRules.terrainMoveBlockedInMaterialized(terrain, tx, ty, nx, ny, climb)) continue;
                reached[ni] = 1;
                queue[tail++] = ni;
            }
        }
        return reached;
    }

    private static bool hasWalkableFootprint(byte[] tiles, int width, int height, int tx, int ty, int size)
    {
        if (size <= 1) return true;
        for (int top = ty - size + 1; top <= ty; top++)
        {
            for (int left = tx - size + 1; left <= tx; left++)
            {
                if (left < 0 || top < 0 || left + size > width || top + size > height) continue;
                bool ok = true;
                for (int y = top; y < top + size && ok; y++)
                {
                    for (int x = left; x < left + size; x++)
                    {
                        if (!isWalkable(tiles[tileIndex(width, x, y)]))
                        {
                            ok = false;
                            break;
                        }
                    }
                }
                if (ok) return true;
            }
        }
        return false;
    }

    private static int countOpenAround(byte[] tiles, int width, int height, int tx, int ty, int radius)
    {
        int n = 0;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = tx + dx;
                int y = ty + dy;
                if (!inBounds(width, height, x, y)) continue;
                if (isWalkable(tiles[tileIndex(width, x, y)])) n++;
            }
        }
        return n;
    }

    private static void pushSample(List<TerrainAnalysisSample> samples, int tx, int ty, string kind, string? detail = null)
    {
        if (samples.Count >= 32) return;
        samples.push(new TerrainAnalysisSample { tx = tx, ty = ty, kind = kind, detail = detail });
    }

    private static bool inDiagnosticArea(int width, int height, int tx, int ty, int inset)
    {
        return tx >= inset && ty >= inset && tx < width - inset && ty < height - inset;
    }

    public static TerrainAnalysis analyzeTerrainGrid(
        byte[] tiles,
        int width,
        int height,
        sbyte[]? elevation = null,
        TerrainAnalysisOptions? options = null)
    {
        options ??= new TerrainAnalysisOptions();
        double maxClimb = options.maxClimb ?? TerrainRules.TERRAIN_STANDARD_MAX_CLIMB;
        int minimumFootprint = Math.max(1, options.minimumFootprint ?? 5);
        int openRadius = Math.max(1, options.openRadius ?? 2);
        // `Math.max(0, Math.floor(options.diagnosticInset ?? 0))`; NaN would compare false everywhere (inset 0 does too).
        double insetRaw = Math.max(0, Math.floor(options.diagnosticInset ?? 0));
        int diagnosticInset = double.IsNaN(insetRaw) ? 0 : insetRaw >= int.MaxValue ? int.MaxValue : (int)insetRaw;
        var terrain = TerrainRules.materializeTerrainForRules(tiles, elevation, width, height, new TerrainModelOptions
        {
            maxStep = maxClimb,
        });
        var climb = new TerrainConnectionOptions { maxClimb = maxClimb };
        var reached = floodClimbable(terrain, climb);
        var deadEnds = new byte[tiles.Length];
        var chokePoints = new byte[tiles.Length];
        var openCells = new byte[tiles.Length];
        var narrowFootprints = new byte[tiles.Length];
        var steepEdges = new byte[tiles.Length];
        var bridgeWidthMask = new byte[tiles.Length];
        var bridgeWaterMask = new byte[tiles.Length];
        var cliffEdges = new byte[tiles.Length];
        var samples = new List<TerrainAnalysisSample>();
        var metrics = new TerrainAnalysisMetrics
        {
            cells = tiles.Length,
            walkable = 0,
            reachable = 0,
            disconnectedWalkable = 0,
            steepWalkableEdges = 0,
            cleftCells = 0,
            underpassCells = 0,
            bridgeCells = 0,
            bridgeComponents = 0,
            bridgeWidthIssues = 0,
            bridgeWaterOverlapCells = 0,
            bridgeWaterClearanceMin = double.PositiveInfinity,
            bridgeUndersizedWaterBodies = 0,
            bridgeRedundantComponents = 0,
            bridgeUnanchoredComponents = 0,
            bridgeDryComponents = 0,
            waterCells = 0,
            waterSurfaceCells = 0,
            waterComponents = 0,
            waterSmallComponents = 0,
            waterMinimumBodyCells = 0,
            chasmCells = 0,
            chasmComponents = 0,
            chasmSmallComponents = 0,
            chasmNarrowComponents = 0,
            chasmCoreCells = 0,
            chasmCoreFraction = 0,
            chasmCellsFarFromCore = 0,
            floorChasmEdges = 0,
            waterStepEdges = 0,
            waterfallEdges = 0,
            deadEnds = 0,
            chokePoints = 0,
            openCells = 0,
            narrowFootprints = 0,
            cliffEdges = 0,
        };

        int openThreshold = (openRadius * 2 + 1) * (openRadius * 2 + 1) - 2;
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int idx = tileIndex(width, tx, ty);
                int tile = tiles[idx];
                if (tile == TileType.Bridge)
                {
                    metrics.bridgeCells++;
                    TerrainCell? bridgeCell = (uint)idx < (uint)terrain.cells.Length ? terrain.cells[idx] : null;
                    double? clearance = TerrainModel.bridgeWaterClearanceForTerrainCell(bridgeCell);
                    if (bridgeCell?.span == TileType.Water && clearance != null)
                    {
                        metrics.bridgeWaterClearanceMin = Math.min(metrics.bridgeWaterClearanceMin, clearance.Value);
                    }
                    // A deck within `diagnosticInset` of the sampled edge may span a body that continues in the
                    // neighbouring chunk this window simply does not contain, so its span reads as `null` here while the
                    // streamed world crosses real water. Every other boundary-sensitive diagnostic already insets for
                    // exactly this; without it a legitimate crossing at the window rim counts as a defect.
                    if (
                        bridgeCell?.span != TileType.Chasm &&
                        !TerrainModel.bridgeWaterIsSafelyBelowDeck(bridgeCell) &&
                        inDiagnosticArea(width, height, tx, ty, diagnosticInset))
                    {
                        bridgeWaterMask[idx] = 1;
                        metrics.bridgeWaterOverlapCells++;
                        pushSample(
                            samples,
                            tx,
                            ty,
                            "bridge_water_overlap",
                            clearance == null ? "missing span" : Js.ToFixed(clearance.Value, 3));
                    }
                }
                if (tile == TileType.Chasm) metrics.chasmCells++;
                if (tile == TileType.Cleft) metrics.cleftCells++;
                if (tile == TileType.Underpass) metrics.underpassCells++;
                if (!isWalkable(tile)) continue;
                metrics.walkable++;
                if (reached[idx] != 0) metrics.reachable++;
                else if (inDiagnosticArea(width, height, tx, ty, diagnosticInset))
                {
                    metrics.disconnectedWalkable++;
                    pushSample(samples, tx, ty, "disconnected", $"L{Js.Str(levelAt(elevation, idx))}");
                }
                bool diagnosticCell = inDiagnosticArea(width, height, tx, ty, diagnosticInset);
                int neighbours = climbableNeighbours(terrain, idx, climb);
                if (diagnosticCell && neighbours <= 1)
                {
                    deadEnds[idx] = 1;
                    metrics.deadEnds++;
                    pushSample(samples, tx, ty, "dead_end");
                }
                else if (diagnosticCell && neighbours == 2)
                {
                    chokePoints[idx] = 1;
                    metrics.chokePoints++;
                }
                int open = countOpenAround(tiles, width, height, tx, ty, openRadius);
                if (diagnosticCell && open >= openThreshold)
                {
                    openCells[idx] = 1;
                    metrics.openCells++;
                }
                if (diagnosticCell && !hasWalkableFootprint(tiles, width, height, tx, ty, minimumFootprint))
                {
                    narrowFootprints[idx] = 1;
                    metrics.narrowFootprints++;
                    pushSample(samples, tx, ty, "narrow_footprint");
                }
            }
        }

        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int idx = tileIndex(width, tx, ty);
                int tile = tiles[idx];
                bool hereWalkable = isWalkable(tile);
                foreach (var (dx, dy) in FORWARD_DIRS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int ni = tileIndex(width, nx, ny);
                    int other = tiles[ni];
                    if (
                        (tile == TileType.Floor && other == TileType.Chasm) ||
                        (tile == TileType.Chasm && other == TileType.Floor))
                        metrics.floorChasmEdges++;
                    if (
                        (tile == TileType.Water && other == TileType.Chasm) ||
                        (tile == TileType.Chasm && other == TileType.Water))
                        metrics.waterfallEdges++;
                    TerrainCell? hereCell = (uint)idx < (uint)terrain.cells.Length ? terrain.cells[idx] : null;
                    TerrainCell? otherCell = (uint)ni < (uint)terrain.cells.Length ? terrain.cells[ni] : null;
                    if (
                        (tile == TileType.Water || other == TileType.Water) &&
                        TerrainModel.terrainTileCarriesWater(tile, hereCell?.span) &&
                        TerrainModel.terrainTileCarriesWater(other, otherCell?.span))
                    {
                        double? hereLevel = hereCell?.waterLevel;
                        double? otherLevel = otherCell?.waterLevel;
                        if (
                            hereLevel != null &&
                            otherLevel != null &&
                            Math.abs(hereLevel.Value - otherLevel.Value) >= TerrainModel.TERRAIN_WATERFALL_MIN_DROP)
                        {
                            metrics.waterStepEdges++;
                            metrics.waterfallEdges++;
                        }
                    }
                    bool otherWalkable = isWalkable(other);
                    var connection = TerrainRules.terrainConnectionInMaterialized(terrain, tx, ty, nx, ny, climb);
                    double diff = Math.abs(connection.delta);
                    bool diagnosticEdge =
                        inDiagnosticArea(width, height, tx, ty, diagnosticInset) &&
                        inDiagnosticArea(width, height, nx, ny, diagnosticInset);
                    if (diagnosticEdge && hereWalkable && otherWalkable && connection.kind == "cliff")
                    {
                        steepEdges[idx] = 1;
                        steepEdges[ni] = 1;
                        metrics.steepWalkableEdges++;
                        pushSample(samples, tx, ty, "steep_walkable_edge", Js.Str(diff));
                    }
                    else if (diagnosticEdge && diff > maxClimb && (hereWalkable || otherWalkable))
                    {
                        cliffEdges[idx] = 1;
                        cliffEdges[ni] = 1;
                        metrics.cliffEdges++;
                    }
                }
            }
        }

        var bridgeIssues = TerrainBridge.findBridgeWidthIssues(
            tiles,
            width,
            height,
            options.minimumBridgeWidth ?? 2,
            double.PositiveInfinity);
        foreach (var issue in bridgeIssues)
        {
            if (!inDiagnosticArea(width, height, issue.tx, issue.ty, diagnosticInset)) continue;
            metrics.bridgeWidthIssues++;
            bridgeWidthMask[issue.index] = 1;
            pushSample(samples, issue.tx, issue.ty, "bridge_width", issue.axis);
        }

        var seenBridge = new byte[tiles.Length];
        var stack = new List<int>();
        for (int sy = 0; sy < height; sy++)
        {
            for (int sx = 0; sx < width; sx++)
            {
                int start = tileIndex(width, sx, sy);
                if (seenBridge[start] != 0 || tiles[start] != TileType.Bridge) continue;
                seenBridge[start] = 1;
                stack.push(sx, sy);
                metrics.bridgeComponents++;
                while (stack.Count > 0)
                {
                    int y = stack.pop();
                    int x = stack.pop();
                    // `[[x - 1, y], [x + 1, y], [x, y - 1], [x, y + 1]]`
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = k == 0 ? x - 1 : k == 1 ? x + 1 : x;
                        int ny = k == 2 ? y - 1 : k == 3 ? y + 1 : y;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (seenBridge[ni] != 0 || tiles[ni] != TileType.Bridge) continue;
                        seenBridge[ni] = 1;
                        stack.push(nx, ny);
                    }
                }
            }
        }

        var chasmTopology = TerrainChasm.analyzeTerrainChasmTopology(tiles, width, height, new TerrainChasmTopologyOptions
        {
            ignoreBoundaryComponents = diagnosticInset > 0,
        });
        metrics.chasmComponents = chasmTopology.components.Count;
        metrics.chasmSmallComponents = chasmTopology.smallComponents;
        metrics.chasmNarrowComponents = chasmTopology.narrowComponents;
        metrics.chasmCoreCells = chasmTopology.coreCells;
        metrics.chasmCoreFraction = chasmTopology.coreFraction;
        metrics.chasmCellsFarFromCore = chasmTopology.cellsFarFromCore;
        foreach (var component in chasmTopology.components)
        {
            if (component.valid || component.indices.Count == 0) continue;
            int index = component.indices[0];
            pushSample(
                samples,
                index % width,
                index / width,
                "chasm_topology",
                $"{Js.Str(component.indices.Count)} cells, {Js.ToFixed(component.coreFraction * 100, 1)}% core, {Js.Str(component.farFromCoreIndices.Count)} thin");
        }

        var waterTopology = TerrainWater.analyzeTerrainWaterTopology(tiles, width, height, new TerrainWaterTopologyOptions
        {
            ignoreBoundaryComponents = diagnosticInset > 0,
        });
        metrics.waterCells = waterTopology.waterCells;
        metrics.waterSurfaceCells = waterTopology.surfaceCells;
        metrics.waterComponents = waterTopology.components.Count;
        metrics.waterSmallComponents = waterTopology.smallComponents;
        metrics.waterMinimumBodyCells = waterTopology.minimumObservedComponentCells;
        var bridgeUse = TerrainWater.analyzeTerrainBridgeUse(tiles, width, height, new TerrainBridgeUseOptions
        {
            ignoreBoundaryComponents = diagnosticInset > 0,
            requiredBridgeAt = options.requiredBridgeAt,
            minimumBridgeBodyCells = options.minimumBridgeBodyCells,
        });
        metrics.bridgeUndersizedWaterBodies = bridgeUse.undersizedWaterBodies;
        metrics.bridgeRedundantComponents = bridgeUse.redundantComponents;
        metrics.bridgeUnanchoredComponents = bridgeUse.unanchoredComponents;
        metrics.bridgeDryComponents = bridgeUse.dryComponents;
        foreach (var component in waterTopology.components)
        {
            if (component.valid || component.indices.Count == 0) continue;
            int index = component.indices[0];
            pushSample(
                samples,
                index % width,
                index / width,
                "water_topology",
                $"{Js.Str(component.surfaceCells)} cells, {Js.Str(component.bridgeCells)} bridge");
        }

        if (!Number.isFinite(metrics.bridgeWaterClearanceMin)) metrics.bridgeWaterClearanceMin = 0;

        return new TerrainAnalysis
        {
            width = width,
            height = height,
            metrics = metrics,
            overlays = new TerrainAnalysisOverlays
            {
                reachable = reached,
                deadEnds = deadEnds,
                chokePoints = chokePoints,
                openCells = openCells,
                narrowFootprints = narrowFootprints,
                steepEdges = steepEdges,
                bridgeWidthIssues = bridgeWidthMask,
                bridgeWaterIssues = bridgeWaterMask,
                bridgeUseIssues = bridgeUse.issueMask,
                waterIssues = waterTopology.issueMask,
                chasmIssues = chasmTopology.issueMask,
                cliffEdges = cliffEdges,
            },
            samples = samples,
        };
    }

    public static TerrainAnalysis analyzeTerrainLayout(DungeonLayout layout, TerrainAnalysisOptions? options = null)
    {
        return analyzeTerrainGrid(layout.tiles, layout.width, layout.height, layout.elevation, options);
    }
}
