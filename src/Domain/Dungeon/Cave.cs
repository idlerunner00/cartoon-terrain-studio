// Port of packages/shared/src/domain/dungeon/cave.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public sealed class CaveResult
{
    public byte[] tiles;
    public List<TileRect> rooms;
}

/// <summary>
/// Cellular-automata caverns: random fill, then 4-5 smoothing iterations grow organic chambers. We
/// keep only the largest connected floor region (filling stray pockets solid) so the result is always
/// one fully connected cave, then scatter chamber anchors via farthest-point sampling for encounters.
/// Caves have no doors — they read as open, organic spaces (variety against the BSP "rooms" style).
///
/// Pure: the only randomness is the initial fill, drawn from the provided <see cref="Rng"/>; smoothing is
/// deterministic, so the whole cave is reproducible from the seed.
/// </summary>
public static class Cave
{
    public static CaveResult generateCaveGrid(Rng rng, int width, int height, int tier)
    {
        var tiles = new byte[width * height];

        // Initial random fill of the interior (border stays Solid).
        const double wallProb = 0.46;
        for (int ty = 1; ty < height - 1; ty++)
        {
            for (int tx = 1; tx < width - 1; tx++)
            {
                tiles[tileIndex(width, tx, ty)] = rng.next() < wallProb ? (byte)TileType.Solid : (byte)TileType.Floor;
            }
        }

        // Smooth: a tile is Solid if it has >=5 solid neighbours, Floor if <=3, unchanged otherwise.
        for (int iter = 0; iter < 5; iter++)
        {
            var next = new byte[width * height];
            for (int ty = 1; ty < height - 1; ty++)
            {
                for (int tx = 1; tx < width - 1; tx++)
                {
                    int n = solidNeighbours(tiles, width, height, tx, ty);
                    int i = tileIndex(width, tx, ty);
                    if (n >= 5) next[i] = TileType.Solid;
                    else if (n <= 3) next[i] = TileType.Floor;
                    else next[i] = tiles[i];
                }
            }
            tiles = next;
        }

        // Keep only the largest connected floor region; fill everything else solid.
        keepLargestRegion(tiles, width, height);

        // Scatter chamber anchors over the surviving floor for encounter placement.
        var rooms = scatterChambers(rng, tiles, width, height, tier);

        return new CaveResult { tiles = tiles, rooms = rooms };
    }

    /// <summary>Flood every floor region, keep the biggest, and fill the rest with rock.</summary>
    private static void keepLargestRegion(byte[] tiles, int width, int height)
    {
        var seen = new byte[width * height];
        byte[]? best = null;
        int bestSize = 0;
        for (int ty = 1; ty < height - 1; ty++)
        {
            for (int tx = 1; tx < width - 1; tx++)
            {
                int i = tileIndex(width, tx, ty);
                if (tiles[i] != TileType.Floor || seen[i] != 0) continue;
                var mask = floodFill(tiles, width, height, tx, ty);
                int size = 0;
                for (int k = 0; k < mask.Length; k++)
                {
                    if (mask[k] != 0)
                    {
                        seen[k] = 1;
                        size++;
                    }
                }
                if (size > bestSize)
                {
                    bestSize = size;
                    best = mask;
                }
            }
        }
        if (best == null) return;
        for (int k = 0; k < tiles.Length; k++)
        {
            if (tiles[k] == TileType.Floor && best[k] == 0) tiles[k] = TileType.Solid;
        }
    }

    /// <summary>Farthest-point sample N chamber centres over the floor, each wrapped in a small marker rect.</summary>
    private static List<TileRect> scatterChambers(Rng rng, byte[] tiles, int width, int height, int tier)
    {
        var floor = new List<(int tx, int ty)>();
        for (int ty = 1; ty < height - 1; ty++)
        {
            for (int tx = 1; tx < width - 1; tx++)
            {
                if (tiles[tileIndex(width, tx, ty)] == TileType.Floor) floor.push((tx, ty));
            }
        }
        int wantChambers = Math.min(floor.Count, 5 + tier);
        if (floor.Count == 0) return new List<TileRect>();

        var chosen = new List<(int tx, int ty)>();
        chosen.push(floor[(int)rng.@int(0, floor.Count - 1)]);
        while (chosen.Count < wantChambers)
        {
            var bestTile = floor[0];
            double bestDist = -1;
            foreach (var f in floor)
            {
                double nearest = double.PositiveInfinity;
                foreach (var c in chosen)
                {
                    double d = Math.pow(f.tx - c.tx, 2) + Math.pow(f.ty - c.ty, 2);
                    if (d < nearest) nearest = d;
                }
                if (nearest > bestDist)
                {
                    bestDist = nearest;
                    bestTile = f;
                }
            }
            if (bestDist <= 0) break; // every floor tile already coincides with a chamber
            chosen.push(bestTile);
        }

        int half = 2 + (int)Math.floor(tier / 2.0);
        return chosen.map(c => new TileRect(c.tx - half, c.ty - half, half * 2 + 1, half * 2 + 1));
    }
}
