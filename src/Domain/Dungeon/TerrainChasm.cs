// Port of packages/shared/src/domain/dungeon/terrainChasm.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Inside Fluitown.Domain a bare `Js` binds to the namespace Fluitown.Runtime (outer-namespace members are searched
// before compilation-unit usings); this namespace-level alias makes it the runtime class.

public sealed class TerrainChasmComponent
{
    public List<int> indices = new();
    public List<int> coreIndices = new();
    public List<int> farFromCoreIndices = new();
    public bool touchesBoundary;
    public double coreFraction;
    public bool valid;
}

public sealed class TerrainChasmTopology
{
    public int cells;
    public int coreCells;
    public double coreFraction;
    public List<TerrainChasmComponent> components = new();
    public int smallComponents;
    public int narrowComponents;
    public int cellsFarFromCore;
    public byte[] issueMask = Array.Empty<byte>();
}

public sealed class TerrainChasmTopologyOptions
{
    public double? minimumComponentCells;
    public double? minimumCoreFraction;
    /// <summary>A sampled/stitched window cannot judge a component clipped by its outer edge. Generator gates keep false.</summary>
    public bool? ignoreBoundaryComponents;
    /// <summary>Keep companion layers (for example elevation) in lockstep when a Chasm cell is restored: (index, restoredTile).</summary>
    public Action<int, int>? onRestoreCell;
}

public sealed class EnforceTerrainChasmTopologyResult
{
    public int removedCells;
    public int removedComponents;
    public TerrainChasmTopology topology = new();
}

public static class TerrainChasm
{
    /// <summary>A ravine smaller than this cannot reveal enough depth under the production oblique camera.</summary>
    public const int TERRAIN_CHASM_MIN_COMPONENT_CELLS = 24;
    /// <summary>At least this share of every component must be genuine four-neighbour interior, not perimeter wall.</summary>
    public const double TERRAIN_CHASM_MIN_CORE_FRACTION = 0.4;

    private static readonly (int dx, int dy)[] CARDINAL_DIRECTIONS = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private static bool validDimensions(byte[] tiles, int width, int height) =>
        width > 0 && height > 0 && tiles.Length >= width * height;

    /// <summary>
    /// Measures plan-view Chasm readability independently of rendering. A core cell has Chasm on all four cardinal
    /// sides; a perimeter cell farther than one tile from any core is a thin tendril that can only project as wall.
    /// </summary>
    public static TerrainChasmTopology analyzeTerrainChasmTopology(
        byte[] tiles,
        int width,
        int height,
        TerrainChasmTopologyOptions? options = null)
    {
        options ??= new TerrainChasmTopologyOptions();
        double minimumComponentCells = options.minimumComponentCells ?? TERRAIN_CHASM_MIN_COMPONENT_CELLS;
        double minimumCoreFraction = options.minimumCoreFraction ?? TERRAIN_CHASM_MIN_CORE_FRACTION;
        int count = Math.max(0, width * height);
        var issueMask = new byte[count];
        if (!validDimensions(tiles, width, height))
        {
            return new TerrainChasmTopology
            {
                cells = 0,
                coreCells = 0,
                coreFraction = 0,
                components = new List<TerrainChasmComponent>(),
                smallComponents = 0,
                narrowComponents = 0,
                cellsFarFromCore = 0,
                issueMask = issueMask,
            };
        }

        var coreMask = new byte[count];
        int cells = 0;
        int coreCells = 0;
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int index = ty * width + tx;
                if (tiles[index] != TileType.Chasm) continue;
                cells++;
                // `CARDINAL_DIRECTIONS.every(...)`: stops at the first direction that fails.
                bool core = true;
                foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!(
                        nx >= 0 &&
                        ny >= 0 &&
                        nx < width &&
                        ny < height &&
                        tiles[ny * width + nx] == TileType.Chasm))
                    {
                        core = false;
                        break;
                    }
                }
                if (core)
                {
                    coreMask[index] = 1;
                    coreCells++;
                }
            }
        }

        var seen = new byte[count];
        var queue = new int[count];
        var components = new List<TerrainChasmComponent>();
        int smallComponents = 0;
        int narrowComponents = 0;
        int cellsFarFromCore = 0;
        for (int start = 0; start < count; start++)
        {
            if (seen[start] != 0 || tiles[start] != TileType.Chasm) continue;
            int head = 0;
            int tail = 0;
            bool touchesBoundary = false;
            var indices = new List<int>();
            var coreIndices = new List<int>();
            seen[start] = 1;
            queue[tail++] = start;
            while (head < tail)
            {
                int index = queue[head++];
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                indices.push(index);
                if (coreMask[index] != 0) coreIndices.push(index);
                if (tx == 0 || ty == 0 || tx == width - 1 || ty == height - 1) touchesBoundary = true;
                foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int ni = ny * width + nx;
                    if (seen[ni] != 0 || tiles[ni] != TileType.Chasm) continue;
                    seen[ni] = 1;
                    queue[tail++] = ni;
                }
            }

            var farFromCoreIndices = new List<int>();
            foreach (int index in indices)
            {
                if (coreMask[index] != 0) continue;
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                bool nearCore = false;
                for (int dy = -1; dy <= 1 && !nearCore; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        if (coreMask[ny * width + nx] != 0)
                        {
                            nearCore = true;
                            break;
                        }
                    }
                }
                if (!nearCore) farFromCoreIndices.push(index);
            }

            double coreFraction = (double)coreIndices.Count / Math.max(1, indices.Count);
            bool small = indices.Count < minimumComponentCells;
            bool narrow = coreFraction + Number.EPSILON < minimumCoreFraction;
            bool ignored = options.ignoreBoundaryComponents == true && touchesBoundary;
            bool valid = ignored || (!small && !narrow && farFromCoreIndices.Count == 0);
            if (small && !ignored) smallComponents++;
            if (narrow && !ignored) narrowComponents++;
            if (!ignored) cellsFarFromCore += farFromCoreIndices.Count;
            if (!valid) foreach (int index in indices) issueMask[index] = 1;
            components.push(new TerrainChasmComponent
            {
                indices = indices,
                coreIndices = coreIndices,
                farFromCoreIndices = farFromCoreIndices,
                touchesBoundary = touchesBoundary,
                coreFraction = coreFraction,
                valid = valid,
            });
        }

        return new TerrainChasmTopology
        {
            cells = cells,
            coreCells = coreCells,
            coreFraction = (double)coreCells / Math.max(1, cells),
            components = components,
            smallComponents = smallComponents,
            narrowComponents = narrowComponents,
            cellsFarFromCore = cellsFarFromCore,
            issueMask = issueMask,
        };
    }

    /// <summary>
    /// Final generator gate. Thin tendrils are peeled first; components that still cannot provide the required
    /// aperture are restored from the exact pre-Chasm terrain snapshot. Removing a blocker cannot invalidate an
    /// already connected walkable route, and retaining the snapshot preserves Water/Solid/Floor semantics.
    /// </summary>
    public static EnforceTerrainChasmTopologyResult enforceTerrainChasmTopology(
        byte[] tiles,
        byte[] fallbackTiles,
        int width,
        int height,
        TerrainChasmTopologyOptions? options = null)
    {
        options ??= new TerrainChasmTopologyOptions();
        if (!validDimensions(tiles, width, height) || fallbackTiles.Length < width * height)
        {
            return new EnforceTerrainChasmTopologyResult
            {
                removedCells = 0,
                removedComponents = 0,
                topology = analyzeTerrainChasmTopology(tiles, width, height, options),
            };
        }

        int removedCells = 0;
        int removedComponents = 0;
        // Peeling may expose a second thin fringe. A fixed small bound is enough because every pass removes cells.
        for (int pass = 0; pass < 4; pass++)
        {
            var passTopology = analyzeTerrainChasmTopology(tiles, width, height, options);
            var far = new List<int>();
            foreach (var component in passTopology.components) far.AddRange(component.farFromCoreIndices);
            if (far.Count == 0) break;
            foreach (int index in far)
            {
                if (tiles[index] != TileType.Chasm) continue;
                tiles[index] = fallbackTiles[index];
                options.onRestoreCell?.Invoke(index, tiles[index]);
                removedCells++;
            }
        }

        var topology = analyzeTerrainChasmTopology(tiles, width, height, options);
        foreach (var component in topology.components)
        {
            if (component.valid) continue;
            removedComponents++;
            foreach (int index in component.indices)
            {
                if (tiles[index] != TileType.Chasm) continue;
                tiles[index] = fallbackTiles[index];
                options.onRestoreCell?.Invoke(index, tiles[index]);
                removedCells++;
            }
        }

        return new EnforceTerrainChasmTopologyResult
        {
            removedCells = removedCells,
            removedComponents = removedComponents,
            topology = analyzeTerrainChasmTopology(tiles, width, height, options),
        };
    }
}
