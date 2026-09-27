// NOT a port of a TypeScript module: an engine-free stand-in for the browser APIs client/src/dungeon.ts relies on
// (`new Worker(...)`, `worker.postMessage`, `worker.onmessage/onerror`, `worker.terminate`, and the main-thread
// task queue that delivers worker messages, `setTimeout(fn, 0)` and `requestIdleCallback`).
//
// Why it exists: in the browser every Web Worker is a separate JS realm and every `onmessage` handler runs later,
// on the main thread's event loop — never synchronously inside `postMessage`, never concurrently with other
// main-thread code. `ClientDungeon` is main-thread state and its scheduling logic (lanes, in-flight bookkeeping,
// serial checks) is ported literally, so the C# stand-ins keep exactly that contract:
//  * `IClientWorker.postMessage` only enqueues work;
//  * the response (or error) is posted to a `ClientEventLoop`, and the host runs that loop on the thread that owns
//    the dungeon (Godot: the main thread, once per frame via `runQueued()`);
//  * `terminate()` guarantees that no further `onmessage`/`onerror` of that worker is ever delivered.
// Two implementations are provided: `ThreadedClientWorker` (one dedicated background thread per lane — the real
// Worker) and `InlineClientWorker` (runs the job synchronously on the posting thread but still delivers the
// response through the loop — deterministic replays/tests).
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Fluitown.Render;

/// <summary>The DOM <c>Worker</c> surface used by <see cref="ClientDungeon"/>.</summary>
public interface IClientWorker<TRequest, TResponse>
{
    /// <summary>`worker.onmessage = (event) => …(event.data)` — always invoked on the owning event loop.</summary>
    Action<TResponse>? onmessage { get; set; }
    /// <summary>`worker.onerror = () => …` — always invoked on the owning event loop.</summary>
    Action? onerror { get; set; }
    /// <summary>`worker.postMessage(message)`. Never runs a handler synchronously.</summary>
    void postMessage(TRequest message);
    /// <summary>`worker.terminate()`. Pending work is abandoned and no handler of this worker fires afterwards.</summary>
    void terminate();
}

/// <summary>
/// The main-thread task queue: worker messages, `setTimeout(fn, 0)` and `requestIdleCallback(fn)` callbacks are
/// posted here from any thread and executed by whoever owns the <see cref="ClientDungeon"/> (the host's frame loop).
/// </summary>
public sealed class ClientEventLoop
{
    private readonly ConcurrentQueue<Action> tasks = new();
    private readonly SemaphoreSlim signal = new(0);
    private int outstanding;

    /// <summary>Queue a task for the owning thread. Thread-safe.</summary>
    public void post(Action task)
    {
        tasks.Enqueue(task);
        signal.Release();
    }

    /// <summary>A background job whose completion will <see cref="post"/> a task later (keeps <c>runUntilIdle</c> waiting).</summary>
    public void beginExternalWork() => Interlocked.Increment(ref outstanding);

    /// <summary>The matching end of <see cref="beginExternalWork"/>; call it after posting the completion task.</summary>
    public void endExternalWork()
    {
        Interlocked.Decrement(ref outstanding);
        signal.Release();
    }

    /// <summary>
    /// Run the tasks that were queued when the call started (one browser "turn"). Tasks posted while running wait
    /// for the next call, so a handler that schedules more work cannot starve the frame. Owning thread only.
    /// </summary>
    public int runQueued()
    {
        int budget = tasks.Count;
        int ran = 0;
        while (ran < budget && tasks.TryDequeue(out Action? task))
        {
            task();
            ran++;
        }
        return ran;
    }
}

/// <summary>
/// A Web Worker backed by one dedicated background thread. Requests are processed strictly in posting order
/// (a Worker's message queue is FIFO); every response is delivered through the <see cref="ClientEventLoop"/>.
/// The job body runs on the worker thread and therefore must only touch thread-safe state (the ported
/// generator keeps its module state in [ThreadStatic] fields, mirroring one module instance per Worker).
/// </summary>
public sealed class ThreadedClientWorker<TRequest, TResponse> : IClientWorker<TRequest, TResponse>
{
    /// <summary>Generation recursion is deep in places (flood fills, graph walks); give the lane a browser-like stack.</summary>
    private const int WORKER_STACK_BYTES = 16 * 1024 * 1024;

    private readonly ClientEventLoop loop;
    private readonly Func<TRequest, TResponse> body;
    private readonly BlockingCollection<TRequest> queue = new(new ConcurrentQueue<TRequest>());
    private readonly Thread thread;
    private volatile bool terminated;

    public Action<TResponse>? onmessage { get; set; }
    public Action? onerror { get; set; }
    public string name { get; }

    public ThreadedClientWorker(ClientEventLoop loop, Func<TRequest, TResponse> body, string name)
    {
        this.loop = loop;
        this.body = body;
        this.name = name;
        thread = new Thread(run, WORKER_STACK_BYTES) { IsBackground = true, Name = name };
        thread.Start();
    }

    public void postMessage(TRequest message)
    {
        if (terminated) return;
        loop.beginExternalWork();
        try
        {
            queue.Add(message);
        }
        catch (InvalidOperationException)
        {
            // Terminated between the check and the add: a terminated Worker silently drops messages.
            loop.endExternalWork();
        }
    }

    public void terminate()
    {
        if (terminated) return;
        terminated = true;
        queue.CompleteAdding();
    }

    private void run()
    {
        foreach (TRequest request in queue.GetConsumingEnumerable())
        {
            try
            {
                if (terminated) continue;
                TResponse response;
                try
                {
                    response = body(request);
                }
                catch (Exception error)
                {
                    Fluitown.Runtime.JsConsole.error($"[{name}] worker job failed: {error}");
                    loop.post(() =>
                    {
                        if (!terminated) onerror?.Invoke();
                    });
                    continue;
                }
                loop.post(() =>
                {
                    if (!terminated) onmessage?.Invoke(response);
                });
            }
            finally
            {
                loop.endExternalWork();
            }
        }
    }
}
