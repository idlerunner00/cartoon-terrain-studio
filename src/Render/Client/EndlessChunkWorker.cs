// Port of packages/client/src/endlessChunkWorker.ts — keep in lockstep with the original.
//
// PORT NOTE: the TS module is a Web Worker script whose body is `workerScope.onmessage = (event) => …`. In C# the
// body is the pure function <see cref="EndlessChunkWorker.onmessage"/>; a host runs it on a background lane through
// `ThreadedClientWorker` (or `InlineClientWorker`) — see ClientWorkers.cs and <see cref="ClientDungeonHost"/>.
// Transfer lists (`postMessage(response, [layout.tiles.buffer, …])`) have no equivalent: the worker thread hands
// the freshly generated layout object to the main thread and never touches it again, which is what the transfer
// guaranteed in the browser.
using System.Diagnostics;
using Fluitown.Domain;

namespace Fluitown.Render;

public sealed class EndlessChunkWorkerRequest
{
    public int id;
    public int cx;
    public int cy;
    public DungeonDescriptor descriptor = null!;
}

public sealed class EndlessChunkWorkerResponse
{
    public int id;
    public int cx;
    public int cy;
    public double durationMs;
    public DungeonLayout layout = null!;
}

/// <summary>
/// Endless chunks are pure `(descriptor,cx,cy)` work. Building them here keeps the generator's transient
/// graph/finalization allocations and their scavenges off the renderer thread. Ambient decoration planning has
/// its own worker: completing it here used to hold an already-generated, visibility-critical chunk hostage to
/// optional dressing work and made fast camera pans outrun the prefetch ring.
/// </summary>
public static class EndlessChunkWorker
{
    /// <summary>
    /// The worker body (`workerScope.onmessage`). Runs on a worker thread: it touches only the request, the
    /// generator (whose module state is [ThreadStatic]) and the layout it creates. The descriptor is shared with
    /// the main thread and only read.
    /// </summary>
    public static EndlessChunkWorkerResponse onmessage(EndlessChunkWorkerRequest request)
    {
        long started = Stopwatch.GetTimestamp();
        DungeonLayout layout = Descriptor.generateEndlessChunk(request.descriptor, request.cx, request.cy);
        var response = new EndlessChunkWorkerResponse
        {
            id = request.id,
            cx = request.cx,
            cy = request.cy,
            durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            layout = layout,
        };
        // `transfer` list: tiles, elevation, surface, variant, themeIndex and floorUsage buffers change owner here.
        return response;
    }
}
