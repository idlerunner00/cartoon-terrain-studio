// Port of packages/shared/src/domain/dungeon/waveFunctionCollapse.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using static Fluitown.Domain.Elevation;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

// Deterministic, bit-set Wave Function Collapse for small semantic grids.
//
// This is intentionally independent of tiles and rendering. Callers own the state registry, local priors and
// adjacency grammar; this module owns minimum-entropy observation and constraint propagation. Keeping the
// solver generic gives the shared kernel one implementation for future room, landmark and landscape grammars.

public sealed class WaveCollapseModel
{
    public int width;
    public int height;
    /// <summary>Bit-set domains use one positive signed 32-bit word, so at most thirty states are supported.</summary>
    public int stateCount;
    /// <summary>Cell-major local priors (`cell * stateCount + state`). Every value must be finite and >= 0.</summary>
    public double[] weights;
    /// <summary>State-major neighbour masks (`state * 4 + direction`).</summary>
    public uint[] compatibility;
    /// <summary>Optional state-major Markov weight (`from * stateCount + to`) used during observation.</summary>
    public double[]? transitions;
    /// <summary>Optional cell-major fixed state; -1 leaves a cell unresolved.</summary>
    public short[]? presets;
    public double seed;
}

public sealed class WaveCollapseContradiction : InvalidOperationException
{
    public readonly int cell;

    public WaveCollapseContradiction(int cell)
        : base($"Wave Function Collapse removed every state from cell {Js.Str(cell)}")
    {
        this.cell = cell;
    }
}

public static partial class WaveFunctionCollapse
{
    private static readonly int[] DIRECTION_X = { 0, 1, 0, -1 };
    private static readonly int[] DIRECTION_Y = { -1, 0, 1, 0 };

    private static int singletonState(uint mask)
    {
        if (mask == 0 || (mask & (mask - 1)) != 0) return -1;
        return 31 - Math.clz32(mask);
    }

    private static uint allowedNeighbourMask(uint domain, int direction, uint[] compatibility, int stateCount)
    {
        uint allowed = 0;
        for (int state = 0; state < stateCount; state++)
        {
            if ((domain & (uint)(1 << state)) != 0) allowed |= compatibility[state * 4 + direction];
        }
        return allowed;
    }

    private static void validateModel(WaveCollapseModel model)
    {
        int cells = model.width * model.height;
        // The TS also rejects non-integer width/height; the C# fields are int, so only the sign check remains.
        if (cells <= 0)
            throw new InvalidOperationException("Wave Function Collapse requires a positive integer grid");
        if (model.stateCount < 1 || model.stateCount > 30)
            throw new InvalidOperationException("Wave Function Collapse supports 1..30 states");
        if (model.weights.Length != cells * model.stateCount)
            throw new InvalidOperationException("Wave Function Collapse weight array has the wrong length");
        if (model.compatibility.Length != model.stateCount * 4)
            throw new InvalidOperationException("Wave Function Collapse compatibility array has the wrong length");
        if (model.transitions != null && model.transitions.Length != model.stateCount * model.stateCount)
            throw new InvalidOperationException("Wave Function Collapse transition array has the wrong length");
        if (model.presets != null && model.presets.Length != cells)
            throw new InvalidOperationException("Wave Function Collapse preset array has the wrong length");
        foreach (double weight in model.weights)
        {
            if (!Number.isFinite(weight) || weight < 0)
                throw new InvalidOperationException("Wave Function Collapse weights must be finite and non-negative");
        }
    }

    /// <summary>Collapse a complete semantic grid. The same model and seed always produce byte-identical state indices.</summary>
    public static byte[] collapseWaveFunction(WaveCollapseModel model)
    {
        validateModel(model);
        int width = model.width;
        int height = model.height;
        int stateCount = model.stateCount;
        double[] weights = model.weights;
        uint[] compatibility = model.compatibility;
        double[]? transitions = model.transitions;
        short[]? presets = model.presets;
        int cellCount = width * height;
        uint fullDomain = (uint)((1 << stateCount) - 1);
        var domains = new uint[cellCount];
        domains.fill(fullDomain);
        var queue = new List<int>();
        if (presets != null)
        {
            for (int cell = 0; cell < cellCount; cell++)
            {
                int preset = presets[cell];
                if (preset < 0) continue;
                if (preset >= stateCount)
                    throw new InvalidOperationException($"Invalid Wave Function Collapse preset {Js.Str(preset)}");
                domains[cell] = (uint)(1 << preset);
                queue.push(cell);
            }
        }

        void propagate()
        {
            for (int cursor = 0; cursor < queue.Count; cursor++)
            {
                int source = queue[cursor];
                int sx = source % width;
                int sy = source / width;
                for (int direction = 0; direction < 4; direction++)
                {
                    int nx = sx + DIRECTION_X[direction];
                    int ny = sy + DIRECTION_Y[direction];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int target = ny * width + nx;
                    uint allowed = allowedNeighbourMask(domains[source], direction, compatibility, stateCount);
                    uint narrowed = domains[target] & allowed;
                    if (narrowed == domains[target]) continue;
                    if (narrowed == 0) throw new WaveCollapseContradiction(target);
                    domains[target] = narrowed;
                    queue.push(target);
                }
            }
            queue.Clear();
        }

        propagate();
        int observation = 0;
        while (true)
        {
            int observedCell = -1;
            double minimumEntropy = double.PositiveInfinity;
            for (int cell = 0; cell < cellCount; cell++)
            {
                uint domain = domains[cell];
                if (singletonState(domain) >= 0) continue;
                double total = 0;
                double weightedLog = 0;
                for (int state = 0; state < stateCount; state++)
                {
                    if ((domain & (uint)(1 << state)) == 0) continue;
                    double weight = weights[cell * stateCount + state];
                    if (weight <= 0) continue;
                    total += weight;
                    weightedLog += weight * Math.log(weight);
                }
                if (total <= 0) throw new WaveCollapseContradiction(cell);
                // A microscopic deterministic tie-break preserves minimum entropy while avoiding scan-line artefacts.
                double entropy =
                    Math.log(total) -
                    weightedLog / total +
                    latticeHash(Js.ToUint32(model.seed) ^ 0x9e3779b9u, cell, observation) * 1e-9;
                if (entropy < minimumEntropy)
                {
                    minimumEntropy = entropy;
                    observedCell = cell;
                }
            }
            if (observedCell < 0) break;

            int x = observedCell % width;
            int y = observedCell / width;
            uint observedDomain = domains[observedCell];
            var candidateWeights = new double[stateCount];
            double candidateTotal = 0;
            for (int state = 0; state < stateCount; state++)
            {
                if ((observedDomain & (uint)(1 << state)) == 0) continue;
                double weight = weights[observedCell * stateCount + state];
                if (transitions != null)
                {
                    for (int direction = 0; direction < 4; direction++)
                    {
                        int nx = x + DIRECTION_X[direction];
                        int ny = y + DIRECTION_Y[direction];
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int neighbour = singletonState(domains[ny * width + nx]);
                        if (neighbour >= 0) weight *= transitions[neighbour * stateCount + state];
                    }
                }
                candidateWeights[state] = weight;
                candidateTotal += weight;
            }
            if (candidateTotal <= 0) throw new WaveCollapseContradiction(observedCell);
            double roll = latticeHash(Js.ToUint32(model.seed) ^ 0x85ebca6bu, observation, observedCell) * candidateTotal;
            int selected = -1;
            for (int state = 0; state < stateCount; state++)
            {
                roll -= candidateWeights[state];
                if (roll <= 0 && candidateWeights[state] > 0)
                {
                    selected = state;
                    break;
                }
            }
            if (selected < 0)
            {
                for (int state = stateCount - 1; state >= 0; state--)
                {
                    if (candidateWeights[state] > 0)
                    {
                        selected = state;
                        break;
                    }
                }
            }
            // `1 << -1` is `1 << 31` in both languages (shift counts are masked to five bits).
            domains[observedCell] = (uint)(1 << selected);
            queue.push(observedCell);
            propagate();
            observation++;
        }

        var result = new byte[cellCount];
        for (int cell = 0; cell < cellCount; cell++)
        {
            int state = singletonState(domains[cell]);
            if (state < 0) throw new InvalidOperationException($"Wave Function Collapse left cell {Js.Str(cell)} unresolved");
            result[cell] = (byte)state;
        }
        return result;
    }
}
