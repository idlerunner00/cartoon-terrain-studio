// Port of packages/shared/src/math/rng.ts — keep in lockstep with the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Domain;

/// <summary>
/// Deterministic pseudo-random number generator.
///
/// Domain code (world generation) MUST use this and never a system random, so any roll is reproducible
/// from a seed and identical wherever it is evaluated. The generator is `mulberry32`, seeded via a
/// string/number hash. Use <see cref="Rng.fork"/> to derive independent child streams deterministically.
/// </summary>
public static class RngModule
{
    /// <summary>xfnv-1a style hash of a string seed (UTF-16 code units, as JS charCodeAt).</summary>
    public static double hashSeed(string seed)
    {
        int h = unchecked((int)0x811c9dc5);
        for (int i = 0; i < seed.Length; i++)
        {
            h ^= seed[i];
            h = Math.imul(h, 0x01000193);
        }
        return (uint)h;
    }
}

public sealed class Rng
{
    private uint state;

    public Rng(string seed)
    {
        state = (uint)RngModule.hashSeed(seed);
        if (state == 0) state = 1;
    }

    private Rng() { }

    /// <summary>Restore a generator to a known raw state (for snapshotting).</summary>
    public static Rng fromState(double state)
    {
        var rng = new Rng();
        uint s = Js.ToUint32(state);
        rng.state = s == 0 ? 1 : s;
        return rng;
    }

    /// <summary>Next uint32.</summary>
    public uint nextU32()
    {
        // `this.state += 0x6d2b79f5` stores a double in JS, but every consumer reads it through ToInt32 /
        // ToUint32, so modular 32-bit arithmetic yields identical streams.
        state = unchecked(state + 0x6d2b79f5u);
        int t = (int)state;
        t = Math.imul(t ^ (int)((uint)t >> 15), t | 1);
        t ^= unchecked(t + Math.imul(t ^ (int)((uint)t >> 7), t | 61));
        return (uint)(t ^ (int)((uint)t >> 14));
    }

    /// <summary>Float in [0, 1).</summary>
    public double next() => nextU32() / 4294967296.0;

    /// <summary>
    /// Integer in [min, max] inclusive. Returns a double like the original: callers occasionally pass
    /// fractional bounds, and then JS yields a fractional result (min + u32 % span) that must survive.
    /// Cast at the call site when the bounds are integers and an index is needed.
    /// </summary>
    public double @int(double min, double max)
    {
        if (max <= min) return min;
        return min + (nextU32() % (max - min + 1));
    }

    /// <summary>Float in [min, max).</summary>
    public double range(double min, double max) => min + next() * (max - min);

    /// <summary>True with probability `p` (default 0.5).</summary>
    public bool @bool(double p = 0.5) => next() < p;

    /// <summary>Pick a uniformly random element. Throws on empty input (a programming error).</summary>
    public T pick<T>(IReadOnlyList<T> arr)
    {
        if (arr.Count == 0) throw new InvalidOperationException("Rng.pick: empty array");
        return arr[(int)@int(0, arr.Count - 1)];
    }

    /// <summary>Weighted pick over parallel value/weight arrays (weights need not be normalized).</summary>
    public T weighted<T>(IReadOnlyList<T> values, IReadOnlyList<double> weights)
    {
        if (values.Count == 0) throw new InvalidOperationException("Rng.weighted: empty array");
        double total = 0;
        foreach (double w in weights) total += w;
        double roll = next() * total;
        for (int i = 0; i < values.Count; i++)
        {
            roll -= i < weights.Count ? weights[i] : 0;
            if (roll < 0) return values[i];
        }
        return values[values.Count - 1];
    }

    /// <summary>Returns a new Fisher-Yates–shuffled copy.</summary>
    public List<T> shuffle<T>(IReadOnlyList<T> arr)
    {
        var @out = new List<T>(arr);
        for (int i = @out.Count - 1; i > 0; i--)
        {
            int j = (int)@int(0, i);
            (@out[i], @out[j]) = (@out[j], @out[i]);
        }
        return @out;
    }

    public Rng fork(string salt)
    {
        uint mixed = nextU32() ^ (uint)RngModule.hashSeed(salt);
        return fromState(mixed == 0 ? 1 : mixed);
    }
}
