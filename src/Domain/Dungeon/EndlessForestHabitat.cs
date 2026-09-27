// Port of packages/shared/src/domain/dungeon/endlessForestHabitat.ts — keep in lockstep with the original.
using System;
using Fluitown.Runtime;
using static Fluitown.Domain.EndlessRelief;
using static Fluitown.Domain.Scalar;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/* Terrain-aware habitat scoring for world-space Endless forest formations. */

public sealed class EndlessForestHabitatInput
{
    public byte[] tiles = Array.Empty<byte>();
    public sbyte[] elevation = Array.Empty<sbyte>();
    public byte[] waterDistance = Array.Empty<byte>();
    public byte[] chasmDistance = Array.Empty<byte>();
    public int width;
    public int index;
    public double erosionSeed;
    public double x;
    public double y;
}

public static partial class EndlessForestHabitat
{
    /// <summary>Eight-connected distance to one terrain kind; 255 means that the chunk contains no such habitat.</summary>
    public static byte[] endlessForestDistanceTo(byte[] tiles, int width, int target)
    {
        var distance = new byte[tiles.Length];
        distance.fill((byte)255);
        var queue = new int[tiles.Length];
        int head = 0;
        int tail = 0;
        for (int index = 0; index < tiles.Length; index++)
        {
            if (tiles[index] != target) continue;
            distance[index] = 0;
            queue[tail++] = index;
        }
        while (head < tail)
        {
            int index = queue[head++];
            int tx = index % width;
            int ty = (index - tx) / width;
            int next = distance[index] + 1;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = ty + dy;
                if (ny < 0 || ny >= width) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = tx + dx;
                    if (nx < 0 || nx >= width) continue;
                    int neighbour = ny * width + nx;
                    if (distance[neighbour] <= next) continue;
                    distance[neighbour] = Js.U8(next);
                    queue[tail++] = neighbour;
                }
            }
        }
        return distance;
    }

    /// <summary>
    /// Bend a formation towards alluvial shelves and the 2..8-tile bank band that the hand reference establishes.
    /// Altitude is deliberately neutral: reference trees occupy the complete -25..+25 ground domain.
    /// </summary>
    public static EndlessForestSample endlessForestHabitatAt(EndlessForestSample forest, EndlessForestHabitatInput input)
    {
        byte[] tiles = input.tiles;
        sbyte[] elevation = input.elevation;
        byte[] waterDistance = input.waterDistance;
        byte[] chasmDistance = input.chasmDistance;
        int width = input.width;
        int index = input.index;
        double erosionSeed = input.erosionSeed;
        double x = input.x;
        double y = input.y;
        var erosion = endlessErosionAt(erosionSeed, x + 0.5, y + 0.5);
        int waterSteps = waterDistance[index];
        int chasmSteps = chasmDistance[index];
        double riparian = waterSteps == 255 ? 0 : clamp01(1 - Math.abs(waterSteps - 4) / 6.0);
        double shelteredShelf = chasmSteps == 255 ? 0 : clamp01(1 - Math.abs(chasmSteps - 5) / 7.0);
        double roughness = 0;
        int samples = 0;
        for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                int neighbour = index + oy * width + ox;
                // Outside the raster JS reads `undefined`: the tile is neither Water nor Chasm, and the elevation
                // difference is NaN, which poisons the roughness exactly as the original does.
                bool inside = (uint)neighbour < (uint)tiles.Length;
                int tile = inside ? tiles[neighbour] : -1;
                if (tile == TileType.Water || tile == TileType.Chasm) continue;
                double neighbourElevation = (uint)neighbour < (uint)elevation.Length ? elevation[neighbour] : double.NaN;
                roughness += Math.min(2, Math.abs(elevation[index] - neighbourElevation));
                samples++;
            }
        roughness = samples > 0 ? roughness / (samples * 2) : 0;
        double habitat = clamp(
            0.66 +
                erosion.moisture * 0.38 +
                erosion.deposition * 0.24 +
                riparian * 0.2 +
                shelteredShelf * 0.08 -
                erosion.exposure * 0.18 -
                roughness * 0.14,
            0.42,
            1.32);
        EndlessForestSample result = forest.Clone();
        result.strength = clamp01(forest.strength * habitat);
        return result;
    }
}
