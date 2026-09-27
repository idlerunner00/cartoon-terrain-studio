// Port of packages/client/src/render/environment/terrainBakeQueue.ts — keep in lockstep with the original.
//
// PORT NOTES
// * `TerrainBakePriority = 0 | 1 | 2` → int.
// * `TerrainQueuedJob<T> extends TerrainQueueDescriptor<T>` built by object spread (`{ ...descriptor, order, state }`)
//   → a class carrying the descriptor fields plus `order`/`state`; `state` is the string literal union.
// * The queue is plain single-threaded state. In the port it is owned by exactly one worker thread (see
//   TerrainBakeWorker.cs), exactly like one queue per browser Worker realm.
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

public class TerrainQueueDescriptor<T>
{
    public string tileKey = "";
    public double generation;
    public double requestId;
    public double revision;
    /// <summary>TerrainBakePriority (0 | 1 | 2).</summary>
    public int priority;
    public T value = default!;
}

public sealed class TerrainQueuedJob<T> : TerrainQueueDescriptor<T>
{
    public double order;
    /// <summary>'queued' | 'active' | 'cancelled' | 'done'.</summary>
    public string state = "queued";
}

public sealed class TerrainQueueInvalidation<T>
{
    public TerrainQueuedJob<T> job = null!;
    public bool wasActive;
}

public sealed class TerrainQueueEnqueueResult<T>
{
    public bool accepted;
    public TerrainQueuedJob<T> job = null!;
    public TerrainQueueInvalidation<T>? superseded;
}

/// <summary>The `audit()` record of <see cref="TerrainLatestPriorityQueue{T}"/>.</summary>
public sealed class TerrainQueueAudit
{
    public int queued;
    public int heapEntries;
    public double enqueued;
    public double superseded;
    public double cancelled;
    public double rejectedOlder;
    public double tombstones;
    public double heapPops;
    public double compactions;
}

/// <summary>
/// Stable latest-wins priority queue for terrain work.
///
/// A tile/generation has at most one current job. Superseded heap entries become tombstones and are normally
/// discarded at the root. Rare proportional compaction prevents adversarial revision churn from retaining an
/// unbounded heap while keeping the common enqueue/cancel path scan-free.
/// </summary>
public sealed class TerrainLatestPriorityQueue<T>
{
    private readonly List<TerrainQueuedJob<T>> heap = new();
    private readonly JsMap<string, TerrainQueuedJob<T>> byTile = new();
    private readonly JsMap<string, TerrainQueuedJob<T>> byRequest = new();
    private double nextOrder = 0;
    private double enqueued = 0;
    private double superseded = 0;
    private double cancelled = 0;
    private double rejectedOlder = 0;
    private double tombstones = 0;
    private double heapPops = 0;
    private double compactions = 0;

    public int size => byTile.size;

    public TerrainQueueEnqueueResult<T> enqueue(TerrainQueueDescriptor<T> descriptor)
    {
        TerrainQueuedJob<T>? previous = byTile.get(descriptor.tileKey);
        var job = new TerrainQueuedJob<T>
        {
            tileKey = descriptor.tileKey,
            generation = descriptor.generation,
            requestId = descriptor.requestId,
            revision = descriptor.revision,
            priority = descriptor.priority,
            value = descriptor.value,
            // A newer revision inherits the tile's original winner slot. Camera churn therefore cannot demote a
            // repeatedly refreshed visible tile behind unrelated work at the same priority.
            order = previous?.order ?? nextOrder++,
            state = "queued",
        };
        if (
            previous != null &&
            (previous.generation > descriptor.generation ||
                (previous.generation == descriptor.generation && previous.revision >= descriptor.revision)))
        {
            job.state = "cancelled";
            rejectedOlder++;
            return new TerrainQueueEnqueueResult<T> { accepted = false, job = job };
        }

        TerrainQueueInvalidation<T>? supersededJob = null;
        if (previous != null)
        {
            bool wasActive = previous.state == "active";
            previous.state = "cancelled";
            byRequest.delete(requestKey(previous.generation, previous.requestId));
            if (!wasActive) tombstones++;
            superseded++;
            supersededJob = new TerrainQueueInvalidation<T> { job = previous, wasActive = wasActive };
        }

        byTile.set(job.tileKey, job);
        byRequest.set(requestKey(job.generation, job.requestId), job);
        push(job);
        enqueued++;
        compactIfNeeded();
        return new TerrainQueueEnqueueResult<T> { accepted = true, job = job, superseded = supersededJob };
    }

    public TerrainQueueInvalidation<T>? cancel(double generation, double requestId)
    {
        string key = requestKey(generation, requestId);
        TerrainQueuedJob<T>? job = byRequest.get(key);
        if (job == null || job.state == "cancelled" || job.state == "done") return null;
        bool wasActive = job.state == "active";
        job.state = "cancelled";
        byRequest.delete(key);
        if (byTile.get(job.tileKey) == job) byTile.delete(job.tileKey);
        if (!wasActive) tombstones++;
        cancelled++;
        compactIfNeeded();
        return new TerrainQueueInvalidation<T> { job = job, wasActive = wasActive };
    }

    public TerrainQueuedJob<T>? dequeue()
    {
        while (heap.Count > 0)
        {
            TerrainQueuedJob<T> job = pop()!;
            heapPops++;
            if (job.state != "queued" || byTile.get(job.tileKey) != job)
            {
                if (tombstones > 0) tombstones--;
                continue;
            }
            job.state = "active";
            return job;
        }
        return null;
    }

    public bool isCurrent(TerrainQueuedJob<T> job)
    {
        return job.state == "active" && byTile.get(job.tileKey) == job;
    }

    public bool complete(TerrainQueuedJob<T> job)
    {
        if (!isCurrent(job)) return false;
        job.state = "done";
        byTile.delete(job.tileKey);
        byRequest.delete(requestKey(job.generation, job.requestId));
        return true;
    }

    public TerrainQueueAudit audit()
    {
        return new TerrainQueueAudit
        {
            queued = byTile.size,
            heapEntries = heap.Count,
            enqueued = enqueued,
            superseded = superseded,
            cancelled = cancelled,
            rejectedOlder = rejectedOlder,
            tombstones = tombstones,
            heapPops = heapPops,
            compactions = compactions,
        };
    }

    private string requestKey(double generation, double requestId)
    {
        return $"{Js.Str(generation)}:{Js.Str(requestId)}";
    }

    private bool before(TerrainQueuedJob<T> a, TerrainQueuedJob<T> b)
    {
        return a.priority < b.priority || (a.priority == b.priority && a.order < b.order);
    }

    private void push(TerrainQueuedJob<T> job)
    {
        int index = heap.Count;
        heap.Add(job);
        while (index > 0)
        {
            // `(index - 1) >>> 1`: index is a small non-negative heap slot.
            int parent = (int)((uint)(index - 1) >> 1);
            if (before(heap[parent], job)) break;
            heap[index] = heap[parent];
            index = parent;
        }
        heap[index] = job;
    }

    private void compactIfNeeded()
    {
        int live = byTile.size;
        int maximumEntries = Math.max(64, live * 4);
        if (heap.Count <= maximumEntries || tombstones < Math.max(16, live)) return;
        var retained = new List<TerrainQueuedJob<T>>();
        foreach (TerrainQueuedJob<T> job in heap)
            if (job.state == "queued" && byTile.get(job.tileKey) == job) retained.Add(job);
        heap.Clear();
        tombstones = 0;
        foreach (TerrainQueuedJob<T> job in retained) push(job);
        compactions++;
    }

    private TerrainQueuedJob<T>? pop()
    {
        TerrainQueuedJob<T>? root = heap.Count > 0 ? heap[0] : null;
        // `this.heap.pop()`
        TerrainQueuedJob<T>? tail = null;
        if (heap.Count > 0)
        {
            tail = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
        }
        if (root == null || tail == null || heap.Count == 0) return root;
        int index = 0;
        while (true)
        {
            int left = index * 2 + 1;
            if (left >= heap.Count) break;
            int right = left + 1;
            int child = right < heap.Count && before(heap[right], heap[left]) ? right : left;
            if (before(tail, heap[child])) break;
            heap[index] = heap[child];
            index = child;
        }
        heap[index] = tail;
        return root;
    }
}
