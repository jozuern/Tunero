namespace Optimizer.App.Tests;

/// <summary>
/// Runs a test on one thread with a synchronization context, as the window's dispatcher does: everything awaited comes
/// back to that thread, so code that is single-threaded by contract (<see cref="ViewModels.ScanState"/>) is tested the
/// way the window runs it.
/// </summary>
internal sealed class SingleThread : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly object _lock = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_lock)
        {
            _queue.Enqueue((d, state));
            Monitor.Pulse(_lock);
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="test"/> and every continuation it posts on this thread until it ends (at most 20 seconds).</summary>
    public static void Run(Func<Task> test)
    {
        var previous = Current;
        var context = new SingleThread();
        SetSynchronizationContext(context);
        try
        {
            var task = test();
            task.ContinueWith(_ => context.Post(_ => { }, null), TaskScheduler.Default);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!task.IsCompleted)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (context._lock)
                {
                    while (context._queue.Count == 0)
                    {
                        var left = deadline - DateTime.UtcNow;
                        if (left <= TimeSpan.Zero) throw new TimeoutException("the test did not end within 20 seconds");
                        Monitor.Wait(context._lock, left);
                    }
                    next = context._queue.Dequeue();
                }
                next.Callback(next.State);
            }
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>Waits on this thread (posted continuations keep running) until <paramref name="condition"/> holds, at most 10 seconds.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        for (var waited = 0; !condition(); waited += 5)
        {
            if (waited > 10_000) throw new TimeoutException($"waited 10 seconds for {what}");
            await Task.Delay(5);
        }
    }
}
