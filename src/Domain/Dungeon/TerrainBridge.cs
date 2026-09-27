// Port of packages/shared/src/domain/dungeon/terrainBridge.ts — keep in lockstep with the original.
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Inside Fluitown.Domain a bare `Js` binds to the namespace Fluitown.Runtime (outer-namespace members are searched
// before compilation-unit usings); this namespace-level alias makes it the runtime class.

// `export * from './terrainBridgeSpan.js'`: the re-exported span vocabulary (TerrainBridgeSpan,
// TERRAIN_BRIDGE_SPAN_NONE, isTerrainBridgeSpan, classifyTerrainBridgeSpans) lives in TerrainBridgeSpanModule.
// It is deliberately not forwarded here: forwarding members with identical signatures would make every file that
// `using static`s both module classes ambiguous.

public sealed class BridgeWidthIssue
{
    public int tx;
    public int ty;
    public int index;
    public int component;
    /// <summary>'horizontal' | 'vertical'.</summary>
    public string axis = "";
    public int horizontalNeighbours;
    public int verticalNeighbours;
}

public sealed class BridgeThicknessOptions
{
    public double? minWidth;
    public sbyte[]? elevation;
    public byte[]? corridorMask;
    public byte[]? ascentMask;
    public double? maxPasses;
}

public static class TerrainBridge
{
    // Inline `[[1, 0], [-1, 0], [0, 1], [0, -1]]` literals of the original.
    private static readonly (int dx, int dy)[] ANCHOR_STEPS = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    // Inline `[[x - 1, y], [x + 1, y], [x, y - 1], [x, y + 1]]` literals of the original.
    private static readonly (int dx, int dy)[] DECK_STEPS = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    private readonly record struct BridgeWidthCheck(
        bool ok,
        string axis,
        int horizontalNeighbours,
        int verticalNeighbours);

    private static bool isBridge(byte[] tiles, int width, int height, int tx, int ty) =>
        inBounds(width, height, tx, ty) && tiles[tileIndex(width, tx, ty)] == TileType.Bridge;

    /// <summary>
    /// Count the locally distinct dry banks touching one connected bridge deck.
    ///
    /// A standard deck is two cells wide, so one landing normally contributes at least two contact cells. Counting
    /// those cells as independent anchors lets a bridge that stops at the opposite end masquerade as a complete
    /// crossing. Contacts belong to one bank when they touch cardinally; the barrier between the two real landings
    /// keeps those groups separate even when a long route eventually joins them elsewhere in the world.
    /// </summary>
    public static int countTerrainBridgeLandAnchorGroups(IEnumerable<int> anchorIndices, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        var ungrouped = new JsSet<int>(anchorIndices);
        var stack = new List<int>();
        int groups = 0;
        foreach (int anchor in anchorIndices)
        {
            if (anchor < 0 || anchor >= width * height || !ungrouped.delete(anchor)) continue;
            groups++;
            stack.push(anchor);
            while (stack.Count > 0)
            {
                int index = stack.pop();
                int x = index % width;
                int y = index / width; // Math.floor of a non-negative quotient
                foreach (var (dx, dy) in ANCHOR_STEPS)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int neighbour = tileIndex(width, nx, ny);
                    if (!ungrouped.delete(neighbour)) continue;
                    stack.push(neighbour);
                }
            }
        }
        return groups;
    }

    private static bool canWidenBridgeInto(int tile) =>
        tile == TileType.Water ||
        tile == TileType.Chasm ||
        tile == TileType.Floor ||
        tile == TileType.Bridge ||
        tile == TileType.Solid;

    private static int bridgeWidenScore(int tile) =>
        (TerrainBridgeSpanModule.isTerrainBridgeSpan(tile) ? 8 : 0) +
        (tile == TileType.Bridge ? 4 : 0) +
        (isWalkable(tile) ? 1 : 0);

    private static (int horizontal, int vertical) bridgeNeighbourCounts(
        byte[] tiles,
        int width,
        int height,
        int tx,
        int ty) =>
        (
            (isBridge(tiles, width, height, tx - 1, ty) ? 1 : 0) +
            (isBridge(tiles, width, height, tx + 1, ty) ? 1 : 0),
            (isBridge(tiles, width, height, tx, ty - 1) ? 1 : 0) +
            (isBridge(tiles, width, height, tx, ty + 1) ? 1 : 0)
        );

    private static bool bridgeSquareSupportAt(byte[] tiles, int width, int height, int tx, int ty)
    {
        for (int top = ty - 1; top <= ty; top++)
        {
            for (int left = tx - 1; left <= tx; left++)
            {
                if (left < 0 || top < 0 || left + 1 >= width || top + 1 >= height) continue;
                if (
                    isBridge(tiles, width, height, left, top) &&
                    isBridge(tiles, width, height, left + 1, top) &&
                    isBridge(tiles, width, height, left, top + 1) &&
                    isBridge(tiles, width, height, left + 1, top + 1))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static BridgeWidthCheck bridgeHasMinimumWidthAt(
        byte[] tiles,
        int width,
        int height,
        int tx,
        int ty,
        double minWidth)
    {
        var counts = bridgeNeighbourCounts(tiles, width, height, tx, ty);
        if (minWidth <= 1)
        {
            return new BridgeWidthCheck(true, "horizontal", counts.horizontal, counts.vertical);
        }
        if (minWidth == 2 && bridgeSquareSupportAt(tiles, width, height, tx, ty))
        {
            return new BridgeWidthCheck(true, "horizontal", counts.horizontal, counts.vertical);
        }
        if (minWidth == 2 && counts.horizontal >= 1 && counts.vertical >= 1)
        {
            return new BridgeWidthCheck(true, "horizontal", counts.horizontal, counts.vertical);
        }

        bool runsHorizontal = counts.horizontal >= counts.vertical;
        int crossWidth = runsHorizontal ? counts.vertical + 1 : counts.horizontal + 1;
        return new BridgeWidthCheck(
            crossWidth >= minWidth,
            runsHorizontal ? "horizontal" : "vertical",
            counts.horizontal,
            counts.vertical);
    }

    public static List<BridgeWidthIssue> findBridgeWidthIssues(
        byte[] tiles,
        int width,
        int height,
        double minWidth = 2,
        double maxIssues = double.PositiveInfinity)
    {
        var issues = new List<BridgeWidthIssue>();
        var seen = new byte[tiles.Length];
        var stack = new List<int>();
        int component = 0;

        for (int sy = 0; sy < height; sy++)
        {
            for (int sx = 0; sx < width; sx++)
            {
                int start = tileIndex(width, sx, sy);
                if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
                seen[start] = 1;
                stack.push(sx, sy);
                while (stack.Count > 0)
                {
                    int y = stack.pop();
                    int x = stack.pop();
                    int idx = tileIndex(width, x, y);
                    var widthCheck = bridgeHasMinimumWidthAt(tiles, width, height, x, y, minWidth);
                    if (!widthCheck.ok && issues.Count < maxIssues)
                    {
                        issues.push(new BridgeWidthIssue
                        {
                            tx = x,
                            ty = y,
                            index = idx,
                            component = component,
                            axis = widthCheck.axis,
                            horizontalNeighbours = widthCheck.horizontalNeighbours,
                            verticalNeighbours = widthCheck.verticalNeighbours,
                        });
                    }
                    foreach (var (dx, dy) in DECK_STEPS)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        if (seen[ni] != 0 || tiles[ni] != TileType.Bridge) continue;
                        seen[ni] = 1;
                        stack.push(nx, ny);
                    }
                }
                component++;
            }
        }

        return issues;
    }

    public static void enforceMinimumBridgeThickness(
        byte[] tiles,
        int width,
        int height,
        BridgeThicknessOptions? options = null)
    {
        options ??= new BridgeThicknessOptions();
        double minWidth = Math.max(1, options.minWidth ?? 2);
        if (minWidth <= 1) return;
        double maxPasses = Math.max(1, options.maxPasses ?? minWidth + 2);
        var mark = new byte[tiles.Length];
        var elevation = options.elevation;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            mark.fill((byte)0);
            int marked = 0;

            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int idx = tileIndex(width, tx, ty);
                    if (tiles[idx] != TileType.Bridge) continue;
                    var check = bridgeHasMinimumWidthAt(tiles, width, height, tx, ty, minWidth);
                    if (check.ok) continue;

                    var candidates = check.axis == "horizontal"
                        ? new (int nx, int ny)[] { (tx, ty - 1), (tx, ty + 1) }
                        : new (int nx, int ny)[] { (tx - 1, ty), (tx + 1, ty) };
                    int best = -1;
                    int bestScore = -1;
                    foreach (var (nx, ny) in candidates)
                    {
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        int tile = tiles[ni];
                        if (!canWidenBridgeInto(tile)) continue;
                        if (tile == TileType.Bridge) continue;
                        int score = bridgeWidenScore(tile);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = ni;
                        }
                    }
                    if (best < 0) continue;
                    if (mark[best] != 0) continue;
                    mark[best] = 1;
                    marked++;
                }
            }

            if (marked == 0) return;

            for (int i = 0; i < mark.Length; i++)
            {
                if (mark[i] == 0) continue;
                // `options.elevation?.[i] ?? 1`: a read past the layer's end is undefined and falls back.
                int level = elevation != null && (uint)i < (uint)elevation.Length ? elevation[i] : 1;
                int tx = i % width;
                int ty = i / width; // Math.floor of a non-negative quotient
                foreach (var (dx, dy) in ANCHOR_STEPS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int ni = tileIndex(width, nx, ny);
                    if (tiles[ni] != TileType.Bridge) continue;
                    level = elevation != null && (uint)ni < (uint)elevation.Length ? elevation[ni] : level;
                    break;
                }
                tiles[i] = TileType.Bridge;
                // Typed-array stores past the end are silently dropped in JS.
                if (elevation != null && (uint)i < (uint)elevation.Length) elevation[i] = (sbyte)level;
                if (options.corridorMask != null && (uint)i < (uint)options.corridorMask.Length) options.corridorMask[i] = 1;
                if (options.ascentMask != null && (uint)i < (uint)options.ascentMask.Length) options.ascentMask[i] = 1;
            }
            TerrainRules.invalidateTerrainRulesMaterialization(tiles);
        }
    }

    public static void demoteStrayBridgeComponents(
        byte[] tiles,
        int width,
        int height,
        int replacement = TileType.Floor)
    {
        var seen = new byte[tiles.Length];
        var stack = new List<int>();
        var cells = new List<int>();

        for (int sy = 0; sy < height; sy++)
        {
            for (int sx = 0; sx < width; sx++)
            {
                int start = tileIndex(width, sx, sy);
                if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
                seen[start] = 1;
                stack.push(sx, sy);
                cells.Clear();
                bool touchesSpan = false;

                while (stack.Count > 0)
                {
                    int y = stack.pop();
                    int x = stack.pop();
                    int idx = tileIndex(width, x, y);
                    cells.push(idx);
                    foreach (var (dx, dy) in DECK_STEPS)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        int tile = tiles[ni];
                        if (TerrainBridgeSpanModule.isTerrainBridgeSpan(tile)) touchesSpan = true;
                        if (seen[ni] != 0 || tile != TileType.Bridge) continue;
                        seen[ni] = 1;
                        stack.push(nx, ny);
                    }
                }

                if (touchesSpan) continue;
                foreach (int idx in cells) tiles[idx] = (byte)replacement;
            }
        }
    }

    /// <summary>
    /// Restore one-bank bridge components that touch a streamed artifact boundary.
    ///
    /// A chunk cannot prove where a boundary-clipped deck reaches its second bank, and a neighbouring chunk may
    /// legitimately choose a different local hydrology treatment. Keeping that guess is how a one-bank timber stub
    /// can survive into the stitched world. Restoring the component's pre-hydrology terrain preserves the route's
    /// walkability without publishing a bridge whose complete span is unknowable inside this artifact.
    /// </summary>
    public static int restoreUnanchoredBoundaryBridgeComponents(
        byte[] tiles,
        byte[] fallbackTiles,
        int width,
        int height)
    {
        int count = width * height;
        if (width <= 0 || height <= 0 || tiles.Length < count || fallbackTiles.Length < count) return 0;
        var seen = new byte[count];
        var stack = new List<int>();
        var component = new List<int>();
        int restored = 0;

        for (int start = 0; start < count; start++)
        {
            if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
            seen[start] = 1;
            stack.push(start);
            component.Clear();
            bool touchesBoundary = false;
            var landAnchors = new JsSet<int>();
            while (stack.Count > 0)
            {
                int index = stack.pop();
                component.push(index);
                int tx = index % width;
                int ty = index / width; // Math.floor of a non-negative quotient
                touchesBoundary = touchesBoundary || tx == 0 || ty == 0 || tx == width - 1 || ty == height - 1;
                foreach (var (dx, dy) in ANCHOR_STEPS)
                {
                    int nx = tx + dx;
                    int ny = ty + dy;
                    if (!inBounds(width, height, nx, ny)) continue;
                    int neighbour = tileIndex(width, nx, ny);
                    if (tiles[neighbour] != TileType.Bridge && isWalkable(tiles[neighbour]))
                        landAnchors.add(neighbour);
                    if (seen[neighbour] != 0 || tiles[neighbour] != TileType.Bridge) continue;
                    seen[neighbour] = 1;
                    stack.push(neighbour);
                }
            }
            if (!touchesBoundary || countTerrainBridgeLandAnchorGroups(landAnchors, width, height) >= 2)
                continue;
            foreach (int index in component)
            {
                tiles[index] = fallbackTiles[index];
                restored++;
            }
        }

        if (restored > 0) TerrainRules.invalidateTerrainRulesMaterialization(tiles);
        return restored;
    }

    /// <summary>
    /// Remove finite bridge components that do not connect two distinct land banks. This is intentionally separate
    /// from <see cref="demoteStrayBridgeComponents"/>: streamed Endless chunks may contain a valid one-bank seam stub,
    /// while a fully materialized finite dungeon must never retain a dead-end deck. Demoted cells recover their
    /// inferred Water/Chasm substrate instead of turning a failed crossing into walkable land.
    /// </summary>
    public static int demoteIncompleteFiniteBridgeComponents(
        byte[] tiles,
        int width,
        int height,
        int replacement = TileType.Floor)
    {
        var spans = TerrainBridgeSpanModule.classifyTerrainBridgeSpans(tiles, width, height);
        var seen = new byte[tiles.Length];
        var stack = new List<int>();
        var cells = new List<int>();
        int demoted = 0;

        for (int sy = 0; sy < height; sy++)
        {
            for (int sx = 0; sx < width; sx++)
            {
                int start = tileIndex(width, sx, sy);
                if (seen[start] != 0 || tiles[start] != TileType.Bridge) continue;
                seen[start] = 1;
                stack.push(sx, sy);
                cells.Clear();
                var landAnchors = new JsSet<int>();

                while (stack.Count > 0)
                {
                    int y = stack.pop();
                    int x = stack.pop();
                    int idx = tileIndex(width, x, y);
                    cells.push(idx);
                    foreach (var (dx, dy) in DECK_STEPS)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (!inBounds(width, height, nx, ny)) continue;
                        int ni = tileIndex(width, nx, ny);
                        int tile = tiles[ni];
                        if (tile != TileType.Bridge && isWalkable(tile)) landAnchors.add(ni);
                        if (seen[ni] != 0 || tile != TileType.Bridge) continue;
                        seen[ni] = 1;
                        stack.push(nx, ny);
                    }
                }

                int landAnchorGroups = 0;
                var ungroupedAnchors = new JsSet<int>(landAnchors);
                var anchorStack = new List<int>();
                foreach (int anchor in landAnchors)
                {
                    if (!ungroupedAnchors.delete(anchor)) continue;
                    landAnchorGroups++;
                    anchorStack.push(anchor);
                    while (anchorStack.Count > 0)
                    {
                        int index = anchorStack.pop();
                        int ax = index % width;
                        int ay = index / width; // Math.floor of a non-negative quotient
                        foreach (var (dx, dy) in ANCHOR_STEPS)
                        {
                            int nx = ax + dx;
                            int ny = ay + dy;
                            if (!inBounds(width, height, nx, ny)) continue;
                            int ni = tileIndex(width, nx, ny);
                            if (!ungroupedAnchors.delete(ni)) continue;
                            anchorStack.push(ni);
                        }
                    }
                }
                if (landAnchorGroups >= 2) continue;

                foreach (int idx in cells)
                {
                    int span = spans[idx];
                    tiles[idx] = (byte)(TerrainBridgeSpanModule.isTerrainBridgeSpan(span) ? span : replacement);
                    demoted++;
                }
            }
        }

        if (demoted > 0) TerrainRules.invalidateTerrainRulesMaterialization(tiles);
        return demoted;
    }
}
