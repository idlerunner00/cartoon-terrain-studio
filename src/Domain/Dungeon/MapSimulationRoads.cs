// Port of packages/shared/src/domain/dungeon/mapSimulationRoads.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Grid;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/*
 * The circulation machinery behind a simulated map's **Roads** layer: what the network has to reach, the
 * skeleton that reaches it, and the router that draws one avenue.
 *
 * Kept out of the composer (MapSimulation) because none of it needs the composition's state — it is graph work
 * over a cost field, and it is the kind of code that hides inside a large module until nobody can see it any more.
 */

/// <summary>A place the circulation network must reach — the heart, a wonder, a frame gate.</summary>
public sealed class RoadNode
{
    public int tx;
    public int ty;
    /// <summary>Plaza radius stamped at this node. 0 means the node is only a waypoint.</summary>
    public int plaza;
    /// <summary>'heart' | 'wonder' | 'gate'.</summary>
    public string kind = "";
}

/// <summary>The world an avenue is routed through. Read-only: routing decides, painting writes.</summary>
public sealed class RoadTerrain
{
    public int width;
    public int height;
    public byte[] tiles = Array.Empty<byte>();
    public sbyte[] elevation = Array.Empty<sbyte>();
    /// <summary>Cells already carrying an avenue — reusing one is nearly free, which is what makes a network.</summary>
    public byte[] road = Array.Empty<byte>();
    /// <summary>Cells a wonder owns; an avenue may clip one, but it should look for a way around first.</summary>
    public byte[] claim = Array.Empty<byte>();
    /// <summary>Border ring the router must stay inside.</summary>
    public int border;
}

public static class MapSimulationRoads
{
    /// <summary>Prim's minimum spanning tree over the road nodes — the smallest network that still reaches everything.</summary>
    public static List<(int, int)> minimumSpanningEdges(IReadOnlyList<RoadNode> nodes)
    {
        var edges = new List<(int, int)>();
        if (nodes.Count < 2) return edges;
        var inTree = new byte[nodes.Count];
        var best = new double[nodes.Count].fill(double.PositiveInfinity);
        var parent = new int[nodes.Count].fill(-1);
        best[0] = 0;
        for (int iteration = 0; iteration < nodes.Count; iteration++)
        {
            int pick = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (inTree[i] != 0) continue;
                if (pick < 0 || best[i] < best[pick]) pick = i;
            }
            if (pick < 0) break;
            inTree[pick] = 1;
            if (parent[pick] >= 0) edges.push((parent[pick], pick));
            for (int i = 0; i < nodes.Count; i++)
            {
                if (inTree[i] != 0) continue;
                double distance = Math.hypot(nodes[i].tx - nodes[pick].tx, nodes[i].ty - nodes[pick].ty);
                if (distance < best[i])
                {
                    best[i] = distance;
                    parent[i] = pick;
                }
            }
        }
        return edges;
    }

    private sealed class LoopCandidate
    {
        public int a;
        public int b;
        public double distance;
    }

    /// <summary>The shortest connections a spanning tree left out — what turns a tree into a network you can loop.</summary>
    public static List<(int, int)> extraLoopEdges(
        IReadOnlyList<RoadNode> nodes,
        IReadOnlyList<(int, int)> existing,
        int count)
    {
        if (count <= 0 || nodes.Count < 3) return new List<(int, int)>();
        var taken = new HashSet<string>();
        foreach (var (a, b) in existing) taken.Add(a < b ? $"{a}:{b}" : $"{b}:{a}");
        var candidates = new List<LoopCandidate>();
        for (int a = 0; a < nodes.Count; a++)
        {
            for (int b = a + 1; b < nodes.Count; b++)
            {
                if (taken.Contains($"{a}:{b}")) continue;
                candidates.push(new LoopCandidate
                {
                    a = a,
                    b = b,
                    distance = Math.hypot(nodes[a].tx - nodes[b].tx, nodes[a].ty - nodes[b].ty),
                });
            }
        }
        candidates.sort((x, y) =>
        {
            double d = x.distance - y.distance;
            if (Js.Truthy(d)) return d;
            if (x.a != y.a) return x.a - y.a;
            return x.b - y.b;
        });
        return candidates.slice(0, count).map((entry) => (entry.a, entry.b));
    }

    private static readonly (int, int)[] ROAD_NEIGHBOURS =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1),
    };

    /// <summary>
    /// Route one avenue with A* over a cost field: open ground is cheap, an existing avenue cheaper still, rock is
    /// expensive but carveable, and hazards are simply not roads (the crossings layer already decided where the
    /// world spans water and rift). The heuristic keeps the search local, which is what makes a dozen avenues
    /// across a 320x240 world a few milliseconds rather than a stall.
    /// </summary>
    public static uint[]? routeRoad(RoadTerrain terrain, int from, int to)
    {
        int width = terrain.width;
        int height = terrain.height;
        byte[] tiles = terrain.tiles;
        sbyte[] elevation = terrain.elevation;
        byte[] road = terrain.road;
        byte[] claim = terrain.claim;
        int border = terrain.border;
        if (from == to) return null;
        int total = width * height;
        // A Float32Array in the original: stored costs round to single precision, the heap priorities do not.
        var cost = new float[total].fill(float.PositiveInfinity);
        var cameFrom = new int[total].fill(-1);
        var closed = new byte[total];
        var heap = new MinHeap(Math.min(total, 4096));
        int goalX = to % width;
        int goalY = to / width;
        double heuristic(int index) => Math.hypot((index % width) - goalX, (index / width) - goalY) * 1.02;

        cost[from] = 0;
        heap.push(from, heuristic(from));

        double guard = (double)total * 4;
        while (heap.size > 0 && guard-- > 0)
        {
            int current = heap.pop();
            if (current == to) break;
            if (closed[current] != 0) continue;
            closed[current] = 1;
            int cx = current % width;
            int cy = current / width;
            int currentLevel = elevation[current];
            foreach (var (dx, dy) in ROAD_NEIGHBOURS)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < border || ny < border || nx >= width - border || ny >= height - border) continue;
                if (!inBounds(width, height, nx, ny)) continue;
                int next = tileIndex(width, nx, ny);
                if (closed[next] != 0) continue;
                int tile = tiles[next];
                if (tile == TileType.Water || tile == TileType.Chasm) continue;
                double stepCost = tile == TileType.Solid ? 13 : 1;
                if (road[next] != 0) stepCost = 0.4;
                if (claim[next] != 0) stepCost += 6;
                stepCost += Math.abs(elevation[next] - currentLevel) * 4.5;
                double tentative = cost[current] + stepCost;
                if (tentative >= cost[next]) continue;
                cost[next] = (float)tentative;
                cameFrom[next] = current;
                heap.push(next, tentative + heuristic(next));
            }
        }
        if (cameFrom[to] < 0) return null;

        var reversed = new List<int>();
        int at = to;
        int steps = 0;
        while (at >= 0 && steps++ < total)
        {
            reversed.push(at);
            if (at == from) break;
            at = cameFrom[at];
        }
        if (reversed[reversed.Count - 1] != from) return null;
        reversed.Reverse();
        var path = new uint[reversed.Count];
        for (int i = 0; i < path.Length; i++) path[i] = (uint)reversed[i];
        return path;
    }

    /// <summary>
    /// Binary min-heap over (cell, priority). The router's frontier — a plain array scan turns a dozen avenues
    /// across a continent into a visible stall.
    /// </summary>
    private sealed class MinHeap
    {
        private int[] items;
        private double[] priorities;
        private int length;

        public MinHeap(int capacity)
        {
            items = new int[Math.max(16, capacity)];
            priorities = new double[items.Length];
        }

        public int size => length;

        public void push(int item, double priority)
        {
            if (length == items.Length) grow();
            int at = length++;
            items[at] = item;
            priorities[at] = priority;
            while (at > 0)
            {
                int parent = (at - 1) >> 1;
                if (priorities[parent] <= priorities[at]) break;
                swap(at, parent);
                at = parent;
            }
        }

        public int pop()
        {
            int top = items[0];
            length--;
            if (length > 0)
            {
                items[0] = items[length];
                priorities[0] = priorities[length];
                int at = 0;
                for (;;)
                {
                    int left = at * 2 + 1;
                    int right = left + 1;
                    int smallest = at;
                    if (left < length && priorities[left] < priorities[smallest])
                        smallest = left;
                    if (right < length && priorities[right] < priorities[smallest])
                        smallest = right;
                    if (smallest == at) break;
                    swap(at, smallest);
                    at = smallest;
                }
            }
            return top;
        }

        private void swap(int a, int b)
        {
            int item = items[a];
            double priority = priorities[a];
            items[a] = items[b];
            priorities[a] = priorities[b];
            items[b] = item;
            priorities[b] = priority;
        }

        private void grow()
        {
            var nextItems = new int[items.Length * 2];
            var nextPriorities = new double[nextItems.Length];
            Array.Copy(items, nextItems, items.Length);
            Array.Copy(priorities, nextPriorities, priorities.Length);
            items = nextItems;
            priorities = nextPriorities;
        }
    }
}
