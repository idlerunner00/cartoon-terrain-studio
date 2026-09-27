// NOT a port of a TypeScript module: the engine-free stand-in for the browser's `new Worker(terrainBakeWorker.ts)`,
// `worker.postMessage`, `worker.onmessage/onerror` and `worker.terminate()` as ThreeTerrainLayer uses them.
//
// Contract (identical to the browser's):
//  * Each worker is one isolated realm — one <see cref="TerrainBakeWorker"/> instance — that runs on its own thread
//    and never touches presentation-thread state. Requests reach it only through `postMessage`.
//  * `postMessage` never runs anything synchronously; the realm handles its inbox in FIFO order. While a job is in
//    flight the realm yields at every compiler checkpoint and handles the messages queued meanwhile (a newer
//    revision or a cancel can therefore stop the running job, exactly like the MessageChannel/scheduler.yield
//    round trips of the original).
//  * Responses are delivered through the owner's <see cref="ClientEventLoop"/> (the presentation thread's task
//    queue — the same loop ClientDungeon delivers its chunk workers through), never concurrently with other
//    owner code. After `terminate()` no further `onmessage`/`onerror` of that worker is delivered.
//  * Every bake request produces exactly one result (completed or cancelled at some stage); the loop's external-work
//    accounting (`beginExternalWork`/`endExternalWork`) follows that, so `ClientEventLoop.runUntilIdle()` waits for
//    all outstanding terrain work.
// The implementation is <see cref="ThreadedTerrainBakeWorker"/>: one dedicated background thread per worker (the Godot
// layer runs N of them).
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Fluitown.Runtime;

namespace Fluitown.Render;

/// <summary>The DOM <c>Worker</c> surface the tile manager uses for one terrain bake worker (presentation thread).</summary>
public interface ITerrainBakeWorkerHandle
{
    /// <summary>`worker.onmessage = (event) => …(event.data)` — invoked on the owner's event loop.</summary>
    Action<TerrainBakeWorkerResponse>? onmessage { get; set; }
    /// <summary>`worker.onerror = (event) => …(event.message, event.error)` — invoked on the owner's event loop.</summary>
    Action<string, Exception?>? onerror { get; set; }
    /// <summary>`worker.postMessage(message, transfer)`. Never runs a handler synchronously. Presentation thread.</summary>
    void postMessage(TerrainBakeWorkerRequest message);
    /// <summary>`worker.terminate()`: pending work is abandoned and no handler of this worker fires afterwards.</summary>
    void terminate();
}

/// <summary>Shared inbox, loop accounting and delivery plumbing of both worker hosts.</summary>
public abstract class TerrainBakeWorkerHostBase : ITerrainBakeWorkerHandle
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    protected readonly ClientEventLoop loop;
    protected readonly ConcurrentQueue<TerrainBakeWorkerRequest> inbox = new();
    private readonly object gate = new();
    /// <summary>External-work units opened on the loop and not yet closed (ready handshake + unanswered messages).</summary>
    private int openUnits;
    private volatile bool terminatedFlag;

    public string name { get; }
    public Action<TerrainBakeWorkerResponse>? onmessage { get; set; }
    public Action<string, Exception?>? onerror { get; set; }

    protected TerrainBakeWorkerHostBase(ClientEventLoop loop, string name)
    {
        this.loop = loop;
        this.name = name;
        // The module evaluation + `{ kind: 'ready' }` handshake is one unit of outstanding work.
        openUnit();
    }

    public bool terminated => terminatedFlag;

    public void postMessage(TerrainBakeWorkerRequest message)
    {
        if (!openUnit()) return;
        inbox.Enqueue(message);
        onInboxChanged();
    }

    public void terminate()
    {
        int abandoned;
        lock (gate)
        {
            if (terminatedFlag) return;
            terminatedFlag = true;
            abandoned = openUnits;
            openUnits = 0;
        }
        for (int i = 0; i < abandoned; i++) loop.endExternalWork();
        onInboxChanged();
    }

    /// <summary>The realm's scope: its `postMessage`, its view of the inbox and its clock. Worker thread only.</summary>
    protected ITerrainBakeWorkerScope createScope() => new Scope(this);

    /// <summary>
    /// Worker thread: hand one inbox message to the realm (`onmessage`). A bake keeps its accounting unit open until
    /// its single result is posted; a control message's unit closes once its handler has run.
    /// </summary>
    protected void dispatch(TerrainBakeWorker realm, TerrainBakeWorkerRequest request)
    {
        realm.onmessage(request);
        if (request is not TerrainBakeWorkerBakeRequest) closeUnit();
    }

    /// <summary>An uncaught failure of the realm's synchronous `onmessage` (the browser's `error` event).</summary>
    protected void postError(string message, Exception? error)
    {
        if (terminatedFlag) return;
        JsConsole.error($"[{name}] {message}: {error}");
        loop.post(() =>
        {
            if (!terminatedFlag) onerror?.Invoke(message, error);
        });
    }

    protected virtual void onInboxChanged() { }

    private bool openUnit()
    {
        lock (gate)
        {
            if (terminatedFlag) return false;
            openUnits++;
        }
        loop.beginExternalWork();
        return true;
    }

    private void closeUnit()
    {
        lock (gate)
        {
            if (terminatedFlag || openUnits == 0) return;
            openUnits--;
        }
        loop.endExternalWork();
    }

    private sealed class Scope : ITerrainBakeWorkerScope
    {
        private readonly TerrainBakeWorkerHostBase host;

        public Scope(TerrainBakeWorkerHostBase host) => this.host = host;

        /// <summary>`workerScope.postMessage(response)`: queue delivery on the owner's loop.</summary>
        public void postMessage(TerrainBakeWorkerResponse response)
        {
            if (host.terminatedFlag) return;
            host.loop.post(() =>
            {
                if (!host.terminatedFlag) host.onmessage?.Invoke(response);
            });
            // Every result answers exactly one bake; the ready handshake answers the construction unit. Close after
            // posting so the loop never reports idle between the two.
            host.closeUnit();
        }

        /// <summary>A control-message yield inside a job: take the next queued message (handled right after).</summary>
        public bool tryTakeMessage(out TerrainBakeWorkerRequest request)
        {
            if (host.terminatedFlag || !host.inbox.TryDequeue(out TerrainBakeWorkerRequest? taken))
            {
                request = null!;
                return false;
            }
            request = taken;
            // The realm handles a taken control message synchronously right after this call; any result it causes
            // carries the affected bake's own unit, so the control unit can close now.
            if (taken is not TerrainBakeWorkerBakeRequest) host.closeUnit();
            return true;
        }

        public double now() => Clock.Elapsed.TotalMilliseconds;

        public bool terminated => host.terminatedFlag;
    }
}

/// <summary>A terrain bake worker on its own background thread (the Godot runtime's worker).</summary>
public sealed class ThreadedTerrainBakeWorker : TerrainBakeWorkerHostBase
{
    /// <summary>The compiler recurses deeply in places (flood fills, contour walks); give the lane a browser-like stack.</summary>
    private const int WORKER_STACK_BYTES = 16 * 1024 * 1024;

    private readonly SemaphoreSlim signal = new(0);
    private readonly Thread thread;

    public ThreadedTerrainBakeWorker(ClientEventLoop loop, string name) : base(loop, name)
    {
        thread = new Thread(run, WORKER_STACK_BYTES) { IsBackground = true, Name = name };
        thread.Start();
    }

    protected override void onInboxChanged() => signal.Release();

    private void run()
    {
        TerrainBakeWorker realm;
        try
        {
            realm = new TerrainBakeWorker(createScope());
        }
        catch (Exception error)
        {
            postError("worker module evaluation failed", error);
            return;
        }
        while (!terminated)
        {
            // Handle every message that is already queued before the zero-delay pump timer fires.
            bool handled = false;
            while (!terminated && inbox.TryDequeue(out TerrainBakeWorkerRequest? request))
            {
                handled = true;
                try
                {
                    dispatch(realm, request);
                }
                catch (Exception error)
                {
                    postError("worker execution failed", error);
                    return;
                }
            }
            if (terminated) return;
            if (realm.isPumpScheduled)
            {
                try
                {
                    realm.runScheduledPump();
                }
                catch (Exception error)
                {
                    // `void processNext()` rejects: the browser logs an unhandled rejection and the realm keeps its
                    // stuck `activeJob`. The owner's stall budget then restarts the pool. Keep that behaviour.
                    JsConsole.error($"[{name}] terrain bake job failed: {error}");
                }
                continue;
            }
            if (!handled) signal.Wait();
        }
    }
}
