// Port of packages/shared/src/domain/dungeon/terrainHazardAccess.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Inside Fluitown.Domain a bare `Js` binds to the namespace Fluitown.Runtime (outer-namespace members are searched
// before compilation-unit usings); this namespace-level alias makes it the runtime class.
using Js = Fluitown.Runtime.Js;

public class TerrainHazardAccessAnalysis
{
    public int waterComponents;
    public int chasmComponents;
    public int inaccessibleWaterComponents;
    public int inaccessibleChasmComponents;
}

public sealed class TerrainHazardAccessOptions
{
    /// <summary>Keep companion layers (most importantly elevation) aligned with newly opened ground: (index, walkableAnchorIndex).</summary>
    public Action<int, int>? onCarveCell;
    /// <summary>A finite chunk cannot judge a hazard body continued through a neighbouring chunk.</summary>
    public bool? ignoreBoundaryComponents;
}

public sealed class EnforceTerrainHazardAccessResult : TerrainHazardAccessAnalysis
{
    public int carvedCells;
    public int repairedComponents;
    public int unresolvedComponents;
}

public class TerrainChunkResourceAnalysis
{
    public int waterCells;
    public int chasmCells;
    /// <summary>Hazard cells with at least one cardinal Floor neighbour: real fishing/angling work faces.</summary>
    public int floorAccessibleWaterCells;
    public int floorAccessibleChasmCells;
}

public sealed class TerrainChunkResourceOptions
{
    public double? minimumCells;
    /// <summary>Stable world/chunk salt used only to rotate equal-quality placement choices.</summary>
    public double? seed;
    /// <summary>Important route/court cells are avoided whenever an equally valid unprotected patch exists.</summary>
    public byte[]? @protected;
    /// <summary>Cells whose authored structure/support semantics may never be replaced by a resource patch.</summary>
    public byte[]? forbidden;
    /// <summary>Keeps elevation and other companion layers aligned with newly painted resource cells: (index, hazard, floorAnchorIndex).</summary>
    public Action<int, int, int>? onPaintCell;
}

public sealed class EnforceTerrainChunkResourcesResult : TerrainChunkResourceAnalysis
{
    public int paintedWaterCells;
    public int paintedChasmCells;
    public bool unresolvedWater;
    public bool unresolvedChasm;
}

public static class TerrainHazardAccess
{
    /// <summary>Farming contract for every immutable streamed chunk.</summary>
    public const int TERRAIN_CHUNK_MIN_RESOURCE_CELLS = 4;

    private static readonly (int dx, int dy)[] CARDINAL_DIRECTIONS = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private sealed class HazardComponent
    {
        public List<int> indices = new();
        public bool accessible;
        public bool touchesBoundary;
    }

    private static bool validDimensions(byte[] tiles, int width, int height) =>
        width > 0 && height > 0 && tiles.Length >= width * height;

    private static bool hasCardinalFloor(byte[] tiles, int width, int height, int index)
    {
        int tx = index % width;
        int ty = index / width; // Math.floor of a non-negative quotient
        foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
        {
            int nx = tx + dx;
            int ny = ty + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
            if (tiles[ny * width + nx] == TileType.Floor) return true;
        }
        return false;
    }

    /// <summary>Count the two farmable terrain resources and their exact Floor-facing work sites.</summary>
    public static TerrainChunkResourceAnalysis analyzeTerrainChunkResources(byte[] tiles, int width, int height)
    {
        var analysis = new TerrainChunkResourceAnalysis
        {
            waterCells = 0,
            chasmCells = 0,
            floorAccessibleWaterCells = 0,
            floorAccessibleChasmCells = 0,
        };
        if (!validDimensions(tiles, width, height)) return analysis;
        for (int index = 0; index < width * height; index++)
        {
            int tile = tiles[index];
            if (tile == TileType.Water)
            {
                analysis.waterCells++;
                if (hasCardinalFloor(tiles, width, height, index)) analysis.floorAccessibleWaterCells++;
            }
            else if (tile == TileType.Chasm)
            {
                analysis.chasmCells++;
                if (hasCardinalFloor(tiles, width, height, index)) analysis.floorAccessibleChasmCells++;
            }
        }
        return analysis;
    }

    private sealed class ResourcePatch
    {
        public List<int> indices = new();
        public int floorAnchorIndex;
        public int protectedChanges;
        public int floorChanges;
        public int changedCells;
        public double tie;
    }

    private static double resourceTie(double seed, int x, int y, int width, int height)
    {
        int value = Js.ToInt32(seed) ^ Math.imul(x + 1, unchecked((int)0x9e3779b1)) ^ Math.imul(y + 1, unchecked((int)0x85ebca77));
        value ^= Math.imul(width, unchecked((int)0xc2b2ae3d)) ^ Math.imul(height, 0x27d4eb2f);
        value ^= (int)((uint)value >> 16);
        value = Math.imul(value, 0x7feb352d);
        value ^= (int)((uint)value >> 15);
        return (uint)value;
    }

    private static bool betterResourcePatch(ResourcePatch candidate, ResourcePatch? current)
    {
        if (current == null) return true;
        if (candidate.protectedChanges != current.protectedChanges)
            return candidate.protectedChanges < current.protectedChanges;
        if (candidate.floorChanges != current.floorChanges)
            return candidate.floorChanges < current.floorChanges;
        if (candidate.changedCells != current.changedCells)
            return candidate.changedCells < current.changedCells;
        return candidate.tie < current.tie;
    }

    /// <summary>
    /// Find one compact, topology-valid resource body beside an exact Floor bank.
    ///
    /// Water uses a 3x4 pool (12 cells, above the shared ten-cell minimum). Chasm uses 5x6 (30 cells): its 3x4
    /// interior is exactly forty percent of the body, satisfying the shared aperture/core contract. A patch first
    /// consumes existing matching hazard, then Solid, and only as a last resort ordinary Floor. The opposite
    /// hazard and authored depth structures are never overwritten.
    /// </summary>
    private static ResourcePatch? findResourcePatch(
        byte[] tiles,
        int width,
        int height,
        int hazard,
        int patchWidth,
        int patchHeight,
        TerrainChunkResourceOptions options)
    {
        ResourcePatch? best = null;
        var forbidden = options.forbidden;
        var protectedMask = options.@protected;
        foreach (var (rectWidth, rectHeight) in new (int, int)[] { (patchWidth, patchHeight), (patchHeight, patchWidth) })
        {
            if (rectWidth > width - 2 || rectHeight > height - 2) continue;
            for (int y0 = 1; y0 <= height - rectHeight - 1; y0++)
            {
                for (int x0 = 1; x0 <= width - rectWidth - 1; x0++)
                {
                    var indices = new List<int>();
                    int protectedChanges = 0;
                    int floorChanges = 0;
                    int changedCells = 0;
                    bool allowed = true;
                    for (int y = y0; y < y0 + rectHeight && allowed; y++)
                    {
                        for (int x = x0; x < x0 + rectWidth; x++)
                        {
                            int index = y * width + x;
                            int tile = tiles[index];
                            // `options.forbidden?.[index]`: a read past the mask is undefined (falsy).
                            if (forbidden != null && (uint)index < (uint)forbidden.Length && forbidden[index] != 0 && tile != hazard)
                            {
                                allowed = false;
                                break;
                            }
                            if (tile != hazard && tile != TileType.Solid && tile != TileType.Floor)
                            {
                                allowed = false;
                                break;
                            }
                            indices.push(index);
                            if (tile == hazard) continue;
                            changedCells++;
                            if (tile == TileType.Floor) floorChanges++;
                            if (protectedMask != null && (uint)index < (uint)protectedMask.Length && protectedMask[index] != 0) protectedChanges++;
                        }
                    }
                    if (!allowed) continue;

                    int floorAnchorIndex = -1;
                    for (int y = y0; y < y0 + rectHeight && floorAnchorIndex < 0; y++)
                    {
                        for (int x = x0; x < x0 + rectWidth && floorAnchorIndex < 0; x++)
                        {
                            if (y == y0)
                            {
                                int index = (y - 1) * width + x;
                                if (tiles[index] == TileType.Floor) floorAnchorIndex = index;
                            }
                            if (y == y0 + rectHeight - 1 && floorAnchorIndex < 0)
                            {
                                int index = (y + 1) * width + x;
                                if (tiles[index] == TileType.Floor) floorAnchorIndex = index;
                            }
                            if (x == x0 && floorAnchorIndex < 0)
                            {
                                int index = y * width + x - 1;
                                if (tiles[index] == TileType.Floor) floorAnchorIndex = index;
                            }
                            if (x == x0 + rectWidth - 1 && floorAnchorIndex < 0)
                            {
                                int index = y * width + x + 1;
                                if (tiles[index] == TileType.Floor) floorAnchorIndex = index;
                            }
                        }
                    }
                    if (floorAnchorIndex < 0) continue;
                    var candidate = new ResourcePatch
                    {
                        indices = indices,
                        floorAnchorIndex = floorAnchorIndex,
                        protectedChanges = protectedChanges,
                        floorChanges = floorChanges,
                        changedCells = changedCells,
                        tie = resourceTie(options.seed ?? 0, x0, y0, rectWidth, rectHeight),
                    };
                    if (betterResourcePatch(candidate, best)) best = candidate;
                }
            }
        }
        return best;
    }

    private static int applyResourcePatch(
        byte[] tiles,
        int hazard,
        ResourcePatch? patch,
        TerrainChunkResourceOptions options)
    {
        if (patch == null) return 0;
        int painted = 0;
        foreach (int index in patch.indices)
        {
            if (tiles[index] == hazard) continue;
            tiles[index] = (byte)hazard;
            options.onPaintCell?.Invoke(index, hazard, patch.floorAnchorIndex);
            painted++;
        }
        return painted;
    }

    /// <summary>
    /// Final streamed-chunk gate for fish and spore farming.
    ///
    /// Every chunk leaves with at least four exposed Water cells, four exposed Chasm cells, and at least one cell
    /// of each cardinally adjacent to exact Floor. Existing compliant terrain is byte-untouched. A missing or
    /// sealed resource receives one compact body which also satisfies the stricter visual topology minima, so the
    /// guarantee cannot be erased by the water/chasm quality rules or ship as four decorative specks.
    /// </summary>
    public static EnforceTerrainChunkResourcesResult enforceTerrainChunkResources(
        byte[] tiles,
        int width,
        int height,
        TerrainChunkResourceOptions? options = null)
    {
        options ??= new TerrainChunkResourceOptions();
        double minimumCells = Math.max(
            1,
            Math.floor(options.minimumCells ?? TERRAIN_CHUNK_MIN_RESOURCE_CELLS));
        if (!validDimensions(tiles, width, height))
        {
            var invalid = analyzeTerrainChunkResources(tiles, width, height);
            return new EnforceTerrainChunkResourcesResult
            {
                waterCells = invalid.waterCells,
                chasmCells = invalid.chasmCells,
                floorAccessibleWaterCells = invalid.floorAccessibleWaterCells,
                floorAccessibleChasmCells = invalid.floorAccessibleChasmCells,
                paintedWaterCells = 0,
                paintedChasmCells = 0,
                unresolvedWater = true,
                unresolvedChasm = true,
            };
        }

        var analysis = analyzeTerrainChunkResources(tiles, width, height);
        int paintedWaterCells = 0;
        int paintedChasmCells = 0;
        if (analysis.waterCells < minimumCells || analysis.floorAccessibleWaterCells < 1)
        {
            // 3x4 is compact enough to read as a pool and exceeds the shared ten-cell water-body minimum.
            var patch = findResourcePatch(tiles, width, height, TileType.Water, 3, 4, options);
            paintedWaterCells = applyResourcePatch(tiles, TileType.Water, patch, options);
            analysis = analyzeTerrainChunkResources(tiles, width, height);
        }
        if (analysis.chasmCells < minimumCells || analysis.floorAccessibleChasmCells < 1)
        {
            // 5x6 => 30 cells with a 3x4 cardinal core: 12/30 exactly meets the shared 0.4 core fraction.
            var patch = findResourcePatch(tiles, width, height, TileType.Chasm, 5, 6, options);
            paintedChasmCells = applyResourcePatch(tiles, TileType.Chasm, patch, options);
            analysis = analyzeTerrainChunkResources(tiles, width, height);
        }

        // Keep these references executable: changing either topology constant must make this contract fail loudly in
        // tests instead of silently shrinking the canonical bodies below the renderer's own readability threshold.
        int waterPatchCells = 3 * 4;
        int chasmPatchCells = 5 * 6;
        double chasmPatchCoreFraction = (double)((5 - 2) * (6 - 2)) / chasmPatchCells;
        if (
            waterPatchCells < TerrainWater.TERRAIN_WATER_MIN_COMPONENT_CELLS ||
            chasmPatchCells < TerrainChasm.TERRAIN_CHASM_MIN_COMPONENT_CELLS ||
            chasmPatchCoreFraction < TerrainChasm.TERRAIN_CHASM_MIN_CORE_FRACTION)
            throw new InvalidOperationException("terrain chunk resource patch no longer satisfies shared hazard topology");

        return new EnforceTerrainChunkResourcesResult
        {
            waterCells = analysis.waterCells,
            chasmCells = analysis.chasmCells,
            floorAccessibleWaterCells = analysis.floorAccessibleWaterCells,
            floorAccessibleChasmCells = analysis.floorAccessibleChasmCells,
            paintedWaterCells = paintedWaterCells,
            paintedChasmCells = paintedChasmCells,
            unresolvedWater = analysis.waterCells < minimumCells || analysis.floorAccessibleWaterCells < 1,
            unresolvedChasm = analysis.chasmCells < minimumCells || analysis.floorAccessibleChasmCells < 1,
        };
    }

    /// <summary>
    /// Collect exact Water or Chasm bodies. A body is usable when at least one cardinal bank is walkable: Fluis
    /// have a real tile to stand on instead of a decorative surface sealed inside a mountain mass.
    /// </summary>
    private static List<HazardComponent> hazardComponents(byte[] tiles, int width, int height, int hazard)
    {
        int count = width * height;
        var seen = new byte[count];
        var queue = new int[count];
        var components = new List<HazardComponent>();

        for (int start = 0; start < count; start++)
        {
            if (seen[start] != 0 || tiles[start] != hazard) continue;
            int head = 0;
            int tail = 0;
            bool accessible = false;
            bool touchesBoundary = false;
            var indices = new List<int>();
            seen[start] = 1;
            queue[tail++] = start;

            while (head < tail)
            {
                int index = queue[head++];
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                indices.push(index);
                if (tx == 0 || ty == 0 || tx == width - 1 || ty == height - 1) touchesBoundary = true;
                foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int ni = ny * width + nx;
                    int tile = tiles[ni];
                    if (isWalkable(tile)) accessible = true;
                    if (seen[ni] != 0 || tile != hazard) continue;
                    seen[ni] = 1;
                    queue[tail++] = ni;
                }
            }
            components.push(new HazardComponent { indices = indices, accessible = accessible, touchesBoundary = touchesBoundary });
        }
        return components;
    }

    /// <summary>Measure the direct, cardinally standable banks of every Water and Chasm body.</summary>
    public static TerrainHazardAccessAnalysis analyzeTerrainHazardAccess(
        byte[] tiles,
        int width,
        int height,
        TerrainHazardAccessOptions? options = null)
    {
        // TS: `Pick<TerrainHazardAccessOptions, 'ignoreBoundaryComponents'>`; only that field is read.
        options ??= new TerrainHazardAccessOptions();
        if (!validDimensions(tiles, width, height))
        {
            return new TerrainHazardAccessAnalysis
            {
                waterComponents = 0,
                chasmComponents = 0,
                inaccessibleWaterComponents = 0,
                inaccessibleChasmComponents = 0,
            };
        }
        var water = hazardComponents(tiles, width, height, TileType.Water);
        var chasm = hazardComponents(tiles, width, height, TileType.Chasm);
        bool ignoreBoundaryComponents = options.ignoreBoundaryComponents == true;
        return new TerrainHazardAccessAnalysis
        {
            waterComponents = water.Count,
            chasmComponents = chasm.Count,
            inaccessibleWaterComponents = water.filter(
                component =>
                    !component.accessible && !(ignoreBoundaryComponents && component.touchesBoundary)).Count,
            inaccessibleChasmComponents = chasm.filter(
                component =>
                    !component.accessible && !(ignoreBoundaryComponents && component.touchesBoundary)).Count,
        };
    }

    /// <summary>
    /// Open the shortest deterministic one-tile passage from a sealed hazard body to existing walkable terrain.
    ///
    /// The search may cross Solid only: it never drains Water, fills Chasms, cuts through a Cleft, or edits an
    /// authored crossing. Starting from every cell on the body perimeter makes the selected passage globally
    /// shortest for that body. Scan order is the stable tie-break, so chunks remain byte-reproducible.
    /// </summary>
    private static int openShortestAccess(
        byte[] tiles,
        int width,
        int height,
        HazardComponent component,
        TerrainHazardAccessOptions options)
    {
        int count = width * height;
        var previous = new int[count];
        previous.fill(-2);
        var queue = new int[count];
        int head = 0;
        int tail = 0;

        foreach (int index in component.indices)
        {
            int tx = index % width;
            int ty = index / width; // Math.floor of a non-negative quotient
            foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int ni = ny * width + nx;
                if (tiles[ni] != TileType.Solid || previous[ni] != -2) continue;
                previous[ni] = -1;
                queue[tail++] = ni;
            }
        }

        int landing = -1;
        int walkableAnchor = -1;
        while (head < tail)
        {
            int index = queue[head++];
            int tx = index % width;
            int ty = index / width; // Math.floor of a non-negative quotient
            foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int ni = ny * width + nx;
                int tile = tiles[ni];
                if (isWalkable(tile))
                {
                    landing = index;
                    walkableAnchor = ni;
                    goto search_done; // `break search`
                }
                if (tile != TileType.Solid || previous[ni] != -2) continue;
                previous[ni] = index;
                queue[tail++] = ni;
            }
        }
    search_done:

        if (landing < 0 || walkableAnchor < 0) return 0;
        int carved = 0;
        for (int index = landing; index >= 0; index = previous[index])
        {
            if (tiles[index] != TileType.Solid) continue;
            tiles[index] = TileType.Floor;
            options.onCarveCell?.Invoke(index, walkableAnchor);
            carved++;
        }
        return carved;
    }

    private static readonly int[] HAZARD_ORDER = { TileType.Water, TileType.Chasm };

    /// <summary>
    /// Shipping gate: every Water and Chasm component gets a narrow standable approach. Existing open banks are
    /// untouched; only components completely enclosed by Solid are repaired.
    /// </summary>
    public static EnforceTerrainHazardAccessResult enforceTerrainHazardAccess(
        byte[] tiles,
        int width,
        int height,
        TerrainHazardAccessOptions? options = null)
    {
        options ??= new TerrainHazardAccessOptions();
        if (!validDimensions(tiles, width, height))
        {
            var invalid = analyzeTerrainHazardAccess(tiles, width, height, options);
            return new EnforceTerrainHazardAccessResult
            {
                waterComponents = invalid.waterComponents,
                chasmComponents = invalid.chasmComponents,
                inaccessibleWaterComponents = invalid.inaccessibleWaterComponents,
                inaccessibleChasmComponents = invalid.inaccessibleChasmComponents,
                carvedCells = 0,
                repairedComponents = 0,
                unresolvedComponents = 0,
            };
        }

        int carvedCells = 0;
        int repairedComponents = 0;
        int unresolvedComponents = 0;
        // Water first is a stable part of the contract. A passage it opens may also become a valid bank for an
        // adjacent Chasm, which avoids cutting a redundant second tunnel through the same wall mass.
        foreach (int hazard in HAZARD_ORDER)
        {
            foreach (var component in hazardComponents(tiles, width, height, hazard))
            {
                if (component.accessible) continue;
                if (options.ignoreBoundaryComponents == true && component.touchesBoundary) continue;
                int carved = openShortestAccess(tiles, width, height, component, options);
                if (carved > 0)
                {
                    carvedCells += carved;
                    repairedComponents++;
                }
                else
                {
                    unresolvedComponents++;
                }
            }
        }

        var analysis = analyzeTerrainHazardAccess(tiles, width, height, options);
        return new EnforceTerrainHazardAccessResult
        {
            waterComponents = analysis.waterComponents,
            chasmComponents = analysis.chasmComponents,
            inaccessibleWaterComponents = analysis.inaccessibleWaterComponents,
            inaccessibleChasmComponents = analysis.inaccessibleChasmComponents,
            carvedCells = carvedCells,
            repairedComponents = repairedComponents,
            unresolvedComponents = unresolvedComponents,
        };
    }
}
