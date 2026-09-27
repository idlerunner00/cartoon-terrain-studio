// Port of packages/shared/src/domain/dungeon/terrainBridgeRescue.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.DungeonTypes;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Inside Fluitown.Domain a bare `Js` binds to the namespace Fluitown.Runtime (outer-namespace members are searched
// before compilation-unit usings); this namespace-level alias makes it the runtime class.
using Js = Fluitown.Runtime.Js;

public sealed class FunctionalBridgeRescueOptions
{
    public int startX;
    public int startY;
    public double seed;
    public double? salt;
    public byte[]? routeMask;
    /// <summary>Optional cells the new deck is allowed to occupy. Used when thinning an existing authored footprint.</summary>
    public byte[]? deckMask;
    /// <summary>Accept any non-Bridge walkable bank instead of only ordinary Floor.</summary>
    public bool? allowWalkableBanks;
    public double? margin;
    public double? maxSpan;
    /// <summary>Barrier crossed by the new deck (a TerrainBridgeSpan). Defaults to Water for backward compatibility.</summary>
    public int? barrier;
    public double? minimumBarrierBodyCells;
    /// <summary>Deprecated: use <see cref="minimumBarrierBodyCells"/>; retained for existing Water callers.</summary>
    public double? minimumWaterBodyCells;
    public double? minimumRescuedAreaCells;
    public double? minimumUsefulDetour;

    public FunctionalBridgeRescueOptions Clone() => (FunctionalBridgeRescueOptions)MemberwiseClone();
}

public sealed class FunctionalBridgeRescueResult
{
    public bool added;
    public int bridgeCells;
    public int rescuedAreaCells;
    public double replacedDetour;
    /// <summary>'island' | 'shortcut' (null when nothing was added).</summary>
    public string? kind;
}

public static class TerrainBridgeRescue
{
    private static readonly (int dx, int dy)[] DIRECTIONS = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private sealed class Components
    {
        public short[] labels = Array.Empty<short>();
        public List<int> sizes = new();
    }

    private sealed class BridgeCandidate
    {
        public List<int> deck = new();
        /// <summary>'island' | 'shortcut'.</summary>
        public string kind = "";
        public int rescuedAreaCells;
        public double detour;
        public double score;
    }

    private static Components componentsFor(
        byte[] tiles,
        int width,
        int height,
        Func<int, int, bool> predicate)
    {
        // Int16Array labels: the stores wrap exactly like the original's typed array.
        var labels = new short[tiles.Length];
        labels.fill((short)-1);
        var sizes = new List<int>();
        var queue = new int[tiles.Length];
        for (int start = 0; start < tiles.Length; start++)
        {
            if (labels[start] >= 0 || !predicate(tiles[start], start)) continue;
            int label = sizes.Count;
            int head = 0;
            int tail = 0;
            int size = 0;
            labels[start] = unchecked((short)label);
            queue[tail++] = start;
            while (head < tail)
            {
                int index = queue[head++];
                size++;
                int x = index % width;
                int y = index / width; // Math.floor of a non-negative quotient
                foreach (var (dx, dy) in DIRECTIONS)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int neighbour = ny * width + nx;
                    if (labels[neighbour] >= 0 || !predicate(tiles[neighbour], neighbour))
                        continue;
                    labels[neighbour] = unchecked((short)label);
                    queue[tail++] = neighbour;
                }
            }
            sizes.push(size);
        }
        return new Components { labels = labels, sizes = sizes };
    }

    private static double shortestWalkableDistance(
        byte[] tiles,
        int width,
        int height,
        IReadOnlyList<int> starts,
        JsSet<int> targets)
    {
        // Int16Array distances, as in the original.
        var distance = new short[tiles.Length];
        distance.fill((short)-1);
        var queue = new int[tiles.Length];
        int head = 0;
        int tail = 0;
        foreach (int start in starts)
        {
            distance[start] = 0;
            queue[tail++] = start;
        }
        while (head < tail)
        {
            int index = queue[head++];
            if (targets.has(index)) return distance[index];
            int x = index % width;
            int y = index / width; // Math.floor of a non-negative quotient
            foreach (var (dx, dy) in DIRECTIONS)
            {
                int nx = x + dx;
                int ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int neighbour = ny * width + nx;
                if (distance[neighbour] >= 0 || !isWalkable(tiles[neighbour])) continue;
                distance[neighbour] = Js.I16(distance[index] + 1);
                queue[tail++] = neighbour;
            }
        }
        return double.PositiveInfinity;
    }

    private static int localWalkableMass(byte[] tiles, int width, int height, IReadOnlyList<int> indices)
    {
        int mass = 0;
        var seen = new byte[tiles.Length];
        foreach (int index in indices)
        {
            int x = index % width;
            int y = index / width; // Math.floor of a non-negative quotient
            for (int oy = -3; oy <= 3; oy++)
            {
                for (int ox = -3; ox <= 3; ox++)
                {
                    int nx = x + ox;
                    int ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int neighbour = ny * width + nx;
                    if (seen[neighbour] != 0 || !isWalkable(tiles[neighbour])) continue;
                    seen[neighbour] = 1;
                    mass++;
                }
            }
        }
        return mass;
    }

    /// <summary>
    /// Add at most one useful two-wide deck across Water or Chasm. A deck either reconnects a substantial
    /// walkable component which would otherwise be discarded, or replaces a long existing detour and therefore
    /// turns a peninsula/dead-end district into a loop. Short pond decks and seam-adjacent guesses are rejected.
    /// </summary>
    public static FunctionalBridgeRescueResult rescueFunctionalBridge(
        byte[] tiles,
        int width,
        int height,
        FunctionalBridgeRescueOptions options)
    {
        if (width < 8 || height < 8 || tiles.Length < width * height)
            return new FunctionalBridgeRescueResult { added = false, bridgeCells = 0, rescuedAreaCells = 0, replacedDetour = 0 };
        // margin/maxSpan are integral tile counts at every call site; the loops below index with them.
        int margin = (int)Math.max(2, Math.floor(options.margin ?? 3));
        int maxSpan = (int)Math.max(2, Math.floor(options.maxSpan ?? 10));
        int barrier = options.barrier ?? TileType.Water;
        double minimumBarrierBodyCells = Math.max(
            4,
            options.minimumBarrierBodyCells ?? options.minimumWaterBodyCells ?? 20);
        double minimumRescuedAreaCells = Math.max(4, options.minimumRescuedAreaCells ?? 18);
        double minimumUsefulDetour = Math.max(8, options.minimumUsefulDetour ?? 30);
        int start = options.startY * width + options.startX;
        var walkable = componentsFor(tiles, width, height, (tile, _) => isWalkable(tile));
        int mainLabel = start >= 0 && start < tiles.Length ? walkable.labels[start] : -1;
        if (mainLabel < 0)
        {
            int largest = 0;
            for (int label = 1; label < walkable.sizes.Count; label++)
                if (walkable.sizes[label] > walkable.sizes[largest]) largest = label;
            mainLabel = walkable.sizes.Count > 0 ? largest : -1;
        }
        if (mainLabel < 0)
            return new FunctionalBridgeRescueResult { added = false, bridgeCells = 0, rescuedAreaCells = 0, replacedDetour = 0 };
        var bridgeSpans = TerrainBridgeSpanModule.classifyTerrainBridgeSpans(tiles, width, height);
        var barrierBodies = componentsFor(
            tiles,
            width,
            height,
            (tile, index) =>
                tile == barrier ||
                // A read past the span raster is undefined in JS and never equals the barrier.
                (tile == TileType.Bridge && (uint)index < (uint)bridgeSpans.Length && bridgeSpans[index] == barrier));
        var candidates = new List<BridgeCandidate>();

        bool validBank(int index) =>
            options.allowWalkableBanks == true
                ? tiles[index] != TileType.Bridge && isWalkable(tiles[index])
                : tiles[index] == TileType.Floor;

        void consider(
            List<int> nearBank,
            List<int> farBank,
            List<int> deck,
            int orderX,
            int orderY)
        {
            if (nearBank.some(index => !validBank(index))) return;
            if (farBank.some(index => !validBank(index))) return;
            if (deck.some(index => tiles[index] != barrier)) return;
            var deckMask = options.deckMask;
            // `options.deckMask![index] === 0`: a read past the mask is undefined, which is not 0.
            if (deckMask != null && deck.some(index => (uint)index < (uint)deckMask.Length && deckMask[index] == 0)) return;
            int barrierLabel = barrierBodies.labels[deck[0]];
            if (
                barrierLabel < 0 ||
                barrierBodies.sizes[barrierLabel] < minimumBarrierBodyCells ||
                deck.some(index => barrierBodies.labels[index] != barrierLabel))
                return;
            int nearLabel = walkable.labels[nearBank[0]];
            int farLabel = walkable.labels[farBank[0]];
            if (nearLabel < 0 || farLabel < 0) return;
            if (nearBank.some(index => walkable.labels[index] != nearLabel)) return;
            if (farBank.some(index => walkable.labels[index] != farLabel)) return;
            double span = deck.Count / 2.0;
            string kind;
            int rescuedAreaCells = 0;
            double detour = double.PositiveInfinity;
            if (nearLabel != farLabel)
            {
                if (nearLabel != mainLabel && farLabel != mainLabel) return;
                int rescuedLabel = nearLabel == mainLabel ? farLabel : nearLabel;
                rescuedAreaCells = walkable.sizes[rescuedLabel];
                if (rescuedAreaCells < minimumRescuedAreaCells) return;
                kind = "island";
            }
            else
            {
                if (nearLabel != mainLabel) return;
                if (
                    localWalkableMass(tiles, width, height, nearBank) < 12 ||
                    localWalkableMass(tiles, width, height, farBank) < 12)
                    return;
                detour = shortestWalkableDistance(tiles, width, height, nearBank, new JsSet<int>(farBank));
                if (detour < minimumUsefulDetour || detour < span * 2.6) return;
                kind = "shortcut";
            }
            double jitter =
                Elevation.latticeHash(
                    Js.ToUint32(Js.ToInt32(options.seed) ^ Js.ToInt32(options.salt ?? 0x6a09e667)),
                    orderX,
                    orderY) * 0.01;
            candidates.push(new BridgeCandidate
            {
                deck = deck,
                kind = kind,
                rescuedAreaCells = rescuedAreaCells,
                detour = detour,
                score =
                    (kind == "island" ? 10_000 + Math.min(500, rescuedAreaCells) : detour) -
                    span * 2.5 +
                    jitter,
            });
        }

        // Horizontal crossings: two adjacent rows form the deck width.
        for (int y = margin; y < height - margin - 1; y++)
        {
            for (int x = margin; x < width - margin - 3; x++)
            {
                var nearBank = new List<int> { y * width + x, (y + 1) * width + x };
                for (int span = 2; span <= maxSpan && x + span + 1 < width - margin; span++)
                {
                    var deck = new List<int>();
                    for (int offset = 1; offset <= span; offset++)
                    {
                        deck.push(y * width + x + offset, (y + 1) * width + x + offset);
                    }
                    if (deck.some(index => tiles[index] != barrier)) break;
                    int farX = x + span + 1;
                    consider(nearBank, new List<int> { y * width + farX, (y + 1) * width + farX }, deck, x, y);
                }
            }
        }
        // Vertical crossings: two adjacent columns form the deck width.
        for (int x = margin; x < width - margin - 1; x++)
        {
            for (int y = margin; y < height - margin - 3; y++)
            {
                var nearBank = new List<int> { y * width + x, y * width + x + 1 };
                for (int span = 2; span <= maxSpan && y + span + 1 < height - margin; span++)
                {
                    var deck = new List<int>();
                    for (int offset = 1; offset <= span; offset++)
                    {
                        deck.push((y + offset) * width + x, (y + offset) * width + x + 1);
                    }
                    if (deck.some(index => tiles[index] != barrier)) break;
                    int farY = y + span + 1;
                    consider(nearBank, new List<int> { farY * width + x, farY * width + x + 1 }, deck, x, y);
                }
            }
        }

        candidates.sort((a, b) =>
        {
            double byScore = b.score - a.score;
            return Js.Truthy(byScore) ? byScore : a.deck[0] - b.deck[0];
        });
        var selected = candidates.Count > 0 ? candidates[0] : null;
        if (selected == null)
            return new FunctionalBridgeRescueResult { added = false, bridgeCells = 0, rescuedAreaCells = 0, replacedDetour = 0 };
        var routeMask = options.routeMask;
        foreach (int index in selected.deck)
        {
            tiles[index] = TileType.Bridge;
            // Typed-array stores past the end are silently dropped in JS.
            if (routeMask != null && (uint)index < (uint)routeMask.Length) routeMask[index] = 1;
        }
        return new FunctionalBridgeRescueResult
        {
            added = true,
            bridgeCells = selected.deck.Count,
            rescuedAreaCells = selected.rescuedAreaCells,
            replacedDetour = Number.isFinite(selected.detour) ? selected.detour : 0,
            kind = selected.kind,
        };
    }

    /// <summary>Backward-compatible Water-specialized entry point.</summary>
    public static FunctionalBridgeRescueResult rescueFunctionalWaterBridge(
        byte[] tiles,
        int width,
        int height,
        FunctionalBridgeRescueOptions options)
    {
        var waterOptions = options.Clone();
        waterOptions.barrier = TileType.Water;
        return rescueFunctionalBridge(tiles, width, height, waterOptions);
    }
}
