// Port of packages/shared/src/domain/dungeon/terrainBridgeSpan.ts — keep in lockstep with the original.
using Fluitown.Runtime;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// `TerrainBridgeSpan` (TS: `TileType.Water | TileType.Chasm | TileType.Floor`) is a TileType value → `int` in C#.
//
// Terrain kind hidden below a walkable Bridge deck. `Floor` is the fail-closed answer, not a world shape anybody
// authors: a deck that crosses neither Water nor Chasm spans ordinary ground. It exists because the
// classification must be TOTAL. A Bridge whose span was unknown emitted a deck cap, a thin fascia and nothing
// else — the camera looked straight past it into the backdrop, since a deck is the one cell whose cap does not
// sit on a terrain volume that closes itself.

public static class TerrainBridgeSpanModule
{
    /// <summary>Dense-array sentinel used where a cell is not a Bridge. Every Bridge cell resolves to a real span.</summary>
    public const int TERRAIN_BRIDGE_SPAN_NONE = 0xff;

    private static readonly (int dx, int dy)[] CARDINAL_DIRECTIONS =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };

    /// <summary>
    /// Is this tile something a deck CROSSES?
    ///
    /// Deliberately narrower than TerrainBridgeSpan: `Floor` is a legal answer to "what lies under this deck"
    /// but never a reason to build one, so generators, width scoring and demotion must keep reading exactly
    /// Water and Chasm here. Accepting Floor would let a deck laid over plain ground count as a real crossing.
    /// </summary>
    public static bool isTerrainBridgeSpan(int tile) => tile == TileType.Water || tile == TileType.Chasm;

    /// <summary>
    /// Infer Water/Chasm below each Bridge from direct contacts, then nearest connected deck evidence, and answer
    /// `Floor` for every deck no such evidence ever reaches. The result is TOTAL over Bridge cells — a deck with
    /// no span underneath is a hole in the world, not a missing detail.
    /// </summary>
    public static byte[] classifyTerrainBridgeSpans(byte[] tiles, int width, int height)
    {
        int count = Math.max(0, width * height);
        var spans = new byte[count].fill((byte)TERRAIN_BRIDGE_SPAN_NONE);
        if (width <= 0 || height <= 0 || tiles.Length < count) return spans;

        var distance = new int[count].fill(-1);
        // Every cell is enqueued at most twice (first reach, then at most one equal-distance Water takeover),
        // so this bound is never exceeded; JS would silently drop out-of-range stores, C# would throw.
        var queue = new int[Math.max(1, count * 2)];
        int head = 0;
        int tail = 0;
        for (int index = 0; index < count; index++)
        {
            if (tiles[index] != TileType.Bridge) continue;
            int tx = index % width;
            int ty = (int)Math.floor((double)index / width);
            bool touchesWater = false;
            bool touchesChasm = false;
            foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int tile = tiles[tileIndex(width, nx, ny)];
                touchesWater = touchesWater || tile == TileType.Water;
                touchesChasm = touchesChasm || tile == TileType.Chasm;
            }
            if (!touchesWater && !touchesChasm) continue;
            spans[index] = (byte)(touchesWater ? TileType.Water : TileType.Chasm);
            distance[index] = 0;
            queue[tail++] = index;
        }

        // Equal-distance Water wins so an existing river stays continuous where it drops into Chasm.
        while (head < tail)
        {
            int index = queue[head++];
            int tx = index % width;
            int ty = (int)Math.floor((double)index / width);
            int span = spans[index];
            int nextDistance = distance[index] + 1;
            foreach (var (dx, dy) in CARDINAL_DIRECTIONS)
            {
                int nx = tx + dx;
                int ny = ty + dy;
                if (!inBounds(width, height, nx, ny)) continue;
                int ni = tileIndex(width, nx, ny);
                if (tiles[ni] != TileType.Bridge) continue;
                int previousDistance = distance[ni];
                bool improves =
                    previousDistance < 0 ||
                    nextDistance < previousDistance ||
                    (nextDistance == previousDistance &&
                        span == TileType.Water &&
                        spans[ni] == TileType.Chasm);
                if (!improves) continue;
                distance[ni] = nextDistance;
                spans[ni] = (byte)span;
                queue[tail++] = ni;
            }
        }

        // Total by construction: a deck the flood never reached crosses ordinary ground.
        for (int index = 0; index < count; index++)
            if (tiles[index] == TileType.Bridge && spans[index] == TERRAIN_BRIDGE_SPAN_NONE)
                spans[index] = TileType.Floor;
        return spans;
    }
}
