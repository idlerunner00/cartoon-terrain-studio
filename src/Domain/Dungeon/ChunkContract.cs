// Port of packages/shared/src/domain/dungeon/chunkContract.ts — keep in lockstep with the original.
namespace Fluitown.Domain;

public static class ChunkContract
{
    /// <summary>One source of truth for the terrain/collision residency quantum used by every streamed world.</summary>
    public const int TERRAIN_CHUNK_TILES = 32;
}
