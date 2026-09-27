// Port of packages/client/src/render/environment/terrainTransferBufferPool.ts — keep in lockstep with the original.
//
// PORT NOTES
// * JS pools raw `ArrayBuffer`s and lets the caller lay any typed view over them. C# arrays cannot alias, so a
//   pooled store is the typed array itself (`float[]` / `uint[]`, stored as `Array`). Buckets stay keyed by BYTE
//   length exactly like the original, so the byte budget, the per-size cap and every audit counter are computed
//   over the same numbers. The single deviation: `acquire` can only hand out a store of the requested element
//   type, so it takes the newest store of that type from the bucket (JS: the newest store of any type). A bucket
//   holding only the other type counts as a miss. Every acquired store is fully overwritten by the compiler, so
//   geometry output is unaffected either way.
// * The class implements the compiler's <see cref="TerrainGeometryBufferPool"/> (`acquireFloat32`/`acquireUint32`
//   in ELEMENTS, see TerrainGeometryCompiler.cs), which is what the TS structural type did with `acquire`.
// * One instance per worker thread (TerrainBakeWorker owns it); not thread-safe, like the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>The `audit()` record of <see cref="TerrainTransferBufferPool"/>.</summary>
public sealed class TerrainTransferBufferPoolAudit
{
    public double retainedBytes;
    public double retainedBuffers;
    public int sizes;
    public double hits;
    public double misses;
    public double rejected;
    public double allocations;
    public double allocatedBytes;
    public double acquiredBytes;
    public double releasedBytes;
    public double peakRetainedBytes;
    public double peakRetainedBuffers;
}

/// <summary>
/// Exact-size ArrayBuffer reuse for the terrain worker boundary.
///
/// Exact sizing is deliberate: a transferred buffer always moves its complete backing store. Bucketed
/// capacities would either transfer unused megabytes or expose phantom vertices through a longer typed view.
/// Terrain tiles repeat many topology sizes while traversing and backtracking, so exact buckets still recover
/// the large position/normal/colour/index stores without compromising the byte accounting contract.
/// </summary>
public sealed class TerrainTransferBufferPool : TerrainGeometryBufferPool
{
    private readonly JsMap<double, List<Array>> buckets = new();
    private double retainedBytes = 0;
    private double retainedBuffers = 0;
    private double hits = 0;
    private double misses = 0;
    private double rejected = 0;
    private double allocations = 0;
    private double allocatedBytes = 0;
    private double acquiredBytes = 0;
    private double releasedBytes = 0;
    private double peakRetainedBytes = 0;
    private double peakRetainedBuffers = 0;
    private readonly double maximumBytes;
    private readonly double maximumBuffersPerSize;

    public TerrainTransferBufferPool(double maximumBytes = 64 * 1024 * 1024, double maximumBuffersPerSize = 8)
    {
        this.maximumBytes = maximumBytes;
        this.maximumBuffersPerSize = maximumBuffersPerSize;
    }

    /// <summary>`acquire(byteLength)` laid out as a Float32Array view (`length` in elements).</summary>
    public float[] acquireFloat32(int length) => (float[])acquire(length * 4.0, typeof(float));

    /// <summary>`acquire(byteLength)` laid out as a Uint32Array view (`length` in elements).</summary>
    public uint[] acquireUint32(int length) => (uint[])acquire(length * 4.0, typeof(uint));

    /// <summary>`acquire(byteLength): ArrayBuffer` for one element type (see the port note on typed stores).</summary>
    public Array acquire(double byteLength, Type elementType)
    {
        double size = Math.max(0, Math.floor(byteLength));
        acquiredBytes += size;
        List<Array>? bucket = buckets.get(size);
        Array? reused = popTyped(bucket, elementType);
        if (reused != null)
        {
            retainedBytes -= size;
            retainedBuffers--;
            hits++;
            if (bucket!.Count == 0) buckets.delete(size);
            return reused;
        }
        misses++;
        allocations++;
        allocatedBytes += size;
        return Array.CreateInstance(elementType, (int)(size / ElementSize(elementType)));
    }

    public bool release(Array buffer)
    {
        double size = ByteLength(buffer);
        if (
            size <= 0 ||
            retainedBytes + size > maximumBytes ||
            maximumBuffersPerSize <= 0)
        {
            rejected++;
            return false;
        }
        List<Array>? bucket = buckets.get(size);
        if (bucket == null)
        {
            bucket = new List<Array>();
            buckets.set(size, bucket);
        }
        if (bucket.Count >= maximumBuffersPerSize)
        {
            rejected++;
            return false;
        }
        bucket.Add(buffer);
        retainedBytes += size;
        retainedBuffers++;
        releasedBytes += size;
        peakRetainedBytes = Math.max(peakRetainedBytes, retainedBytes);
        peakRetainedBuffers = Math.max(peakRetainedBuffers, retainedBuffers);
        return true;
    }

    public double releaseAll(IReadOnlyList<Array> buffers)
    {
        double retained = 0;
        foreach (Array buffer in buffers)
            if (release(buffer)) retained++;
        return retained;
    }

    public TerrainTransferBufferPoolAudit audit()
    {
        return new TerrainTransferBufferPoolAudit
        {
            retainedBytes = retainedBytes,
            retainedBuffers = retainedBuffers,
            sizes = buckets.size,
            hits = hits,
            misses = misses,
            rejected = rejected,
            allocations = allocations,
            allocatedBytes = allocatedBytes,
            acquiredBytes = acquiredBytes,
            releasedBytes = releasedBytes,
            peakRetainedBytes = peakRetainedBytes,
            peakRetainedBuffers = peakRetainedBuffers,
        };
    }

    /// <summary>`bucket?.pop()` restricted to one element type (newest matching store; see the port note).</summary>
    internal static Array? popTyped(List<Array>? bucket, Type elementType)
    {
        if (bucket == null) return null;
        for (int i = bucket.Count - 1; i >= 0; i--)
        {
            Array candidate = bucket[i];
            if (candidate.GetType().GetElementType() != elementType) continue;
            bucket.RemoveAt(i);
            return candidate;
        }
        return null;
    }

    /// <summary>`ArrayBuffer.byteLength` of a typed store.</summary>
    internal static double ByteLength(Array buffer) => Buffer.ByteLength(buffer);

    internal static int ElementSize(Type elementType) =>
        elementType == typeof(byte) || elementType == typeof(sbyte) ? 1
        : elementType == typeof(short) || elementType == typeof(ushort) ? 2
        : elementType == typeof(double) ? 8
        : 4;
}
