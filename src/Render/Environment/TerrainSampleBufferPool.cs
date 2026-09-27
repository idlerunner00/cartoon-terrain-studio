// Port of packages/client/src/render/environment/terrainSampleBufferPool.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `ArrayBufferLike` stores are typed arrays here (`byte[]`, `sbyte[]`, `float[]`), bucketed by BYTE length like
//   the original; `acquire` returns the newest store of the requested element type (see TerrainTransferBufferPool.cs
//   for the same deviation and why it cannot change output).
// * `SharedArrayBuffer` vs `ArrayBuffer`: every C# array is visible to every thread of the process, so the two JS
//   modes collapse into one. What the SAB mode guaranteed by convention still holds by the same convention: a
//   store handed to a bake worker is not touched by the presentation thread until the worker's result returns it
//   (`acceptTerrainPlan` releases it into this pool). `shared` is kept as a reported flag only.
// * One instance per presentation thread (the tile manager owns it); not thread-safe, like the original.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>
/// Exact-size terrain input storage shared between the presentation thread and bake workers.
///
/// Cross-origin isolated deployments use SharedArrayBuffer, so posting a request clones only the tiny typed
/// array views and never detaches or transfers the sampled planes themselves. The same stores return to this
/// bounded pool after the result fence. Development hosts without isolation retain the established transferable
/// ArrayBuffer fallback and therefore keep identical behaviour and output.
/// </summary>
public sealed class TerrainSampleBufferPool
{
    private readonly JsMap<double, List<Array>> buckets = new();
    private double retainedBytes = 0;
    private double hits = 0;
    private double misses = 0;
    private double sharedAcquires = 0;
    private readonly bool shared;
    private readonly double maximumBytes;
    private readonly double maximumBuffersPerSize;

    public TerrainSampleBufferPool(bool shared, double maximumBytes = 64 * 1024, double maximumBuffersPerSize = 8)
    {
        this.shared = shared;
        this.maximumBytes = maximumBytes;
        this.maximumBuffersPerSize = maximumBuffersPerSize;
    }

    /// <summary>`new Uint8Array(pool.acquire(count))`.</summary>
    public byte[] acquireUint8(int count) => (byte[])acquire(count, typeof(byte));

    /// <summary>`new Int8Array(pool.acquire(count))`.</summary>
    public sbyte[] acquireInt8(int count) => (sbyte[])acquire(count, typeof(sbyte));

    /// <summary>`new Float32Array(pool.acquire(count * 4))` (`count` in elements).</summary>
    public float[] acquireFloat32(int count) => (float[])acquire(count * 4.0, typeof(float));

    public Array acquire(double byteLength, Type elementType)
    {
        double size = Math.max(0, Math.floor(byteLength));
        List<Array>? bucket = buckets.get(size);
        Array? reused = TerrainTransferBufferPool.popTyped(bucket, elementType);
        if (reused != null)
        {
            retainedBytes -= size;
            hits++;
            if (bucket!.Count == 0) buckets.delete(size);
            if (isSharedArrayBuffer(reused)) sharedAcquires++;
            return reused;
        }
        misses++;
        Array buffer = Array.CreateInstance(elementType, (int)(size / TerrainTransferBufferPool.ElementSize(elementType)));
        if (isSharedArrayBuffer(buffer)) sharedAcquires++;
        return buffer;
    }

    public bool release(Array buffer)
    {
        double size = TerrainTransferBufferPool.ByteLength(buffer);
        if (
            size <= 0 ||
            retainedBytes + size > maximumBytes ||
            maximumBuffersPerSize <= 0)
            return false;
        List<Array>? bucket = buckets.get(size);
        if (bucket == null)
        {
            bucket = new List<Array>();
            buckets.set(size, bucket);
        }
        if (bucket.Count >= maximumBuffersPerSize) return false;
        bucket.Add(buffer);
        retainedBytes += size;
        return true;
    }

    /// <summary>`buffer instanceof SharedArrayBuffer`: true exactly when the pool was created in shared mode.</summary>
    private bool isSharedArrayBuffer(Array buffer) => shared;
}
