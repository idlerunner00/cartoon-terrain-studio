// Port of packages/shared/src/domain/dungeon/endlessCountryTopology.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.EndlessCoordinates;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public static class EndlessCountryTopology
{
    // The 4-neighbourhood in the original's literal order: [x - 1, y], [x + 1, y], [x, y - 1], [x, y + 1].
    private static readonly (int dx, int dy)[] CARDINAL = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    /// <summary>
    /// Keep the walkable component the chunk's node stands in and demote every other one.
    ///
    /// A blocked start falls back to the largest component, which is the useful answer after a set piece has raised
    /// structure over the original node. An optional elevation field makes the flood use the artifact's exact
    /// one-level climb rule; `protectMask` preserves explicitly authored cells during non-final repair passes.
    /// </summary>
    public static void dropDisconnectedWalkable(
        byte[] tiles,
        int startX,
        int startY,
        byte[]? protectMask = null,
        sbyte[]? climbElevation = null)
    {
        int width = ENDLESS_CHUNK_TILES;
        int start = startY * width + startX;
        if (!isWalkable(tiles[start]))
        {
            keepLargestWalkableComponent(tiles, protectMask);
            return;
        }
        var reached = new byte[tiles.Length];
        var stack = new List<int> { start };
        reached[start] = 1;
        while (stack.Count > 0)
        {
            int index = stack.pop();
            int x = index % width;
            int y = (int)Math.floor((double)index / width);
            foreach (var (ddx, ddy) in CARDINAL)
            {
                int nx = x + ddx;
                int ny = y + ddy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= width) continue;
                int neighbour = ny * width + nx;
                if (reached[neighbour] != 0 || !isWalkable(tiles[neighbour])) continue;
                if (climbElevation != null && Math.abs(climbElevation[index] - climbElevation[neighbour]) > 1)
                    continue;
                reached[neighbour] = 1;
                stack.push(neighbour);
            }
        }
        for (int index = 0; index < tiles.Length; index++)
        {
            if ((protectMask != null && protectMask[index] != 0) || !isWalkable(tiles[index]) || reached[index] != 0) continue;
            tiles[index] = TileType.Solid;
        }
    }

    /// <summary>Demote every walkable component but the biggest. Deterministic ties keep the lowest seed index.</summary>
    private static void keepLargestWalkableComponent(byte[] tiles, byte[]? protectMask)
    {
        int width = ENDLESS_CHUNK_TILES;
        var component = new int[tiles.Length].fill(-1);
        int best = -1;
        int bestSize = 0;
        int next = 0;
        var stack = new List<int>();
        for (int seed = 0; seed < tiles.Length; seed++)
        {
            if (component[seed] != -1 || !isWalkable(tiles[seed])) continue;
            int id = next++;
            int size = 0;
            stack.Clear();
            stack.push(seed);
            component[seed] = id;
            while (stack.Count > 0)
            {
                int index = stack.pop();
                size++;
                int x = index % width;
                int y = (int)Math.floor((double)index / width);
                foreach (var (ddx, ddy) in CARDINAL)
                {
                    int nx = x + ddx;
                    int ny = y + ddy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width) continue;
                    int neighbour = ny * width + nx;
                    if (component[neighbour] != -1 || !isWalkable(tiles[neighbour])) continue;
                    component[neighbour] = id;
                    stack.push(neighbour);
                }
            }
            if (size > bestSize)
            {
                bestSize = size;
                best = id;
            }
        }
        if (best < 0) return;
        for (int index = 0; index < tiles.Length; index++)
        {
            if (protectMask != null && protectMask[index] != 0) continue;
            if (component[index] != -1 && component[index] != best) tiles[index] = TileType.Solid;
        }
    }

    /// <summary>Fill one- and two-cell walkable stubs back into rock without touching authored routes or seam exits.</summary>
    public static void trimShallowDeadEnds(byte[] tiles, byte[] routeMask, byte[] courtMask)
    {
        int width = ENDLESS_CHUNK_TILES;
        const int trimDepth = 2;
        for (int pass = 0; pass < trimDepth; pass++)
        {
            var doomed = new List<int>();
            for (int ty = 1; ty < width - 1; ty++)
            {
                for (int tx = 1; tx < width - 1; tx++)
                {
                    int index = ty * width + tx;
                    int tile = tiles[index];
                    if (!isWalkable(tile) || routeMask[index] != 0 || courtMask[index] != 0) continue;
                    // A deck or an underpass is an authored crossing, not a corridor that overshot.
                    if (tile != TileType.Floor) continue;
                    int degree = 0;
                    if (isWalkable(tiles[index - 1])) degree++;
                    if (isWalkable(tiles[index + 1])) degree++;
                    if (isWalkable(tiles[index - width])) degree++;
                    if (isWalkable(tiles[index + width])) degree++;
                    if (degree == 1) doomed.push(index);
                }
            }
            if (doomed.Count == 0) return;
            foreach (int index in doomed) tiles[index] = TileType.Solid;
        }
    }
}
