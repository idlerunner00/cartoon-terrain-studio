// Port of packages/shared/src/domain/dungeon/endlessCoordinates.ts — keep in lockstep with the original.
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

public static class EndlessCoordinates
{
    /// <summary>Side length of one streamed Endless chunk in terrain cells.</summary>
    public const int ENDLESS_CHUNK_TILES = ChunkContract.TERRAIN_CHUNK_TILES;
    /// <summary>Side length of one streamed Endless chunk in world units.</summary>
    public const double ENDLESS_CHUNK_WORLD = ENDLESS_CHUNK_TILES * Grid.TILE_SIZE;
    private const double ENDLESS_HALF = ENDLESS_CHUNK_WORLD / 2;

    public static double endlessChunkOriginX(double cx) => cx * ENDLESS_CHUNK_WORLD - ENDLESS_HALF;

    public static double endlessChunkOriginY(double cy) => cy * ENDLESS_CHUNK_WORLD - ENDLESS_HALF;

    public static int endlessChunkCoordX(double x) => (int)Math.floor((x + ENDLESS_HALF) / ENDLESS_CHUNK_WORLD);

    public static int endlessChunkCoordY(double y) => (int)Math.floor((y + ENDLESS_HALF) / ENDLESS_CHUNK_WORLD);

    public const double ENDLESS_GRID_ORIGIN = -ENDLESS_HALF;

    private const double COORD_OFFSET = 1 << 20;
    private const double COORD_STRIDE = 1 << 21;

    public static double endlessChunkKey(double cx, double cy) => (cx + COORD_OFFSET) * COORD_STRIDE + (cy + COORD_OFFSET);
}
