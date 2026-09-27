// Port of packages/shared/src/domain/dungeon/grid.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>
/// Tile-grid primitives shared by every generator and by collision. A grid is a flat row-major byte array
/// of <see cref="TileType"/>; out-of-bounds reads are treated as Solid so the world is always enclosed.
/// </summary>
public static class Grid
{
    /// <summary>World units per standard tile.</summary>
    public const double TILE_SIZE = 62.5;

    public static int tileIndex(int width, int tx, int ty) => ty * width + tx;

    public static bool inBounds(int width, int height, int tx, int ty) => tx >= 0 && ty >= 0 && tx < width && ty < height;

    /// <summary>Read a tile; out-of-bounds is Solid (the world is enclosed by rock).</summary>
    public static int getTile(byte[] tiles, int width, int height, int tx, int ty)
    {
        if (!inBounds(width, height, tx, ty)) return TileType.Solid;
        return tiles[tileIndex(width, tx, ty)];
    }

    public static void setTile(byte[] tiles, int width, int height, int tx, int ty, int value)
    {
        if (inBounds(width, height, tx, ty)) tiles[tileIndex(width, tx, ty)] = (byte)value;
    }

    public static bool isFloor(byte[] tiles, int width, int height, int tx, int ty) =>
        getTile(tiles, width, height, tx, ty) == TileType.Floor;

    /// <summary>Carve a tile rectangle to Floor (clipped to the grid; the outer 1-tile border is left solid).</summary>
    public static void carveRect(byte[] tiles, int width, int height, TileRect r)
    {
        int x0 = Math.max(1, r.tx);
        int y0 = Math.max(1, r.ty);
        int x1 = Math.min(width - 1, r.tx + r.tw);
        int y1 = Math.min(height - 1, r.ty + r.th);
        for (int ty = y0; ty < y1; ty++)
        {
            for (int tx = x0; tx < x1; tx++)
            {
                tiles[tileIndex(width, tx, ty)] = TileType.Floor;
            }
        }
    }

    /// <summary>
    /// Carve a `thickness`-wide axis path between two tiles (horizontal then vertical, or vice-versa). When
    /// `mask` is supplied, every tile this carves is also flagged `1` in it — the corridor's exact footprint.
    /// </summary>
    public static void carveCorridor(
        byte[] tiles,
        int width,
        int height,
        int ax,
        int ay,
        int bx,
        int by,
        double thickness,
        bool horizontalFirst,
        byte[]? mask = null)
    {
        int half = (int)Math.max(0, Math.floor((thickness - 1) / 2));
        void set(int x, int y)
        {
            setTile(tiles, width, height, x, y, TileType.Floor);
            if (mask != null && inBounds(width, height, x, y)) mask[tileIndex(width, x, y)] = 1;
        }
        void carveH(int x0, int x1, int y)
        {
            int lo = Math.min(x0, x1);
            int hi = Math.max(x0, x1);
            for (int x = lo; x <= hi; x++)
            {
                for (int dy = -half; dy <= half; dy++) set(x, y + dy);
            }
        }
        void carveV(int y0, int y1, int x)
        {
            int lo = Math.min(y0, y1);
            int hi = Math.max(y0, y1);
            for (int y = lo; y <= hi; y++)
            {
                for (int dx = -half; dx <= half; dx++) set(x + dx, y);
            }
        }
        if (horizontalFirst)
        {
            carveH(ax, bx, ay);
            carveV(ay, by, bx);
        }
        else
        {
            carveV(ay, by, ax);
            carveH(ax, bx, by);
        }
    }

    /// <summary>4-connected flood fill from a seed tile over Floor cells. Returns a 0/1 reachability mask.</summary>
    public static byte[] floodFill(byte[] tiles, int width, int height, int seedTx, int seedTy)
    {
        var visited = new byte[width * height];
        if (!isFloor(tiles, width, height, seedTx, seedTy)) return visited;
        var stack = new List<int> { seedTx, seedTy };
        visited[tileIndex(width, seedTx, seedTy)] = 1;
        while (stack.Count > 0)
        {
            int ty = stack.pop();
            int tx = stack.pop();
            // 4-neighbourhood, in the original's order.
            for (int k = 0; k < 4; k++)
            {
                int nx = k == 0 ? tx + 1 : k == 1 ? tx - 1 : tx;
                int ny = k == 2 ? ty + 1 : k == 3 ? ty - 1 : ty;
                if (!inBounds(width, height, nx, ny)) continue;
                int i = tileIndex(width, nx, ny);
                if (visited[i] != 0) continue;
                if (tiles[i] != TileType.Floor) continue;
                visited[i] = 1;
                stack.push(nx, ny);
            }
        }
        return visited;
    }

    /// <summary>
    /// 4-connected flood fill from a seed over WALKABLE cells (Floor or Bridge…), treating Water and Solid as
    /// barriers — the river-aware counterpart of <see cref="floodFill"/>.
    /// </summary>
    public static byte[] floodFillWalkable(byte[] tiles, int width, int height, int seedTx, int seedTy)
    {
        var visited = new byte[width * height];
        if (!isWalkable(getTile(tiles, width, height, seedTx, seedTy))) return visited;
        var stack = new List<int> { seedTx, seedTy };
        visited[tileIndex(width, seedTx, seedTy)] = 1;
        while (stack.Count > 0)
        {
            int ty = stack.pop();
            int tx = stack.pop();
            for (int k = 0; k < 4; k++)
            {
                int nx = k == 0 ? tx + 1 : k == 1 ? tx - 1 : tx;
                int ny = k == 2 ? ty + 1 : k == 3 ? ty - 1 : ty;
                if (!inBounds(width, height, nx, ny)) continue;
                int i = tileIndex(width, nx, ny);
                if (visited[i] != 0) continue;
                if (!isWalkable(tiles[i])) continue;
                visited[i] = 1;
                stack.push(nx, ny);
            }
        }
        return visited;
    }

    /// <summary>Count set entries in a 0/1 mask.</summary>
    public static int countMask(byte[] mask)
    {
        int n = 0;
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] != 0) n++;
        return n;
    }

    /// <summary>Number of Solid tiles among the 8 neighbours of (tx,ty). Out-of-bounds counts as Solid.</summary>
    public static int solidNeighbours(byte[] tiles, int width, int height, int tx, int ty)
    {
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (getTile(tiles, width, height, tx + dx, ty + dy) == TileType.Solid) n++;
            }
        }
        return n;
    }
}
