using System.Collections.Concurrent;

namespace Ai4Sts2.Workbench;

public sealed class LeakedAwaitException(string message) : InvalidOperationException(message);

public sealed class Pump : SynchronizationContext
{
    private const int DrainBudget = 100_000;

    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public long Posted { get; private set; }

    public long Drained { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        Posted++;
        _queue.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public override SynchronizationContext CreateCopy() => this;

    public void Run(Action action)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            action();
            DrainAll();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public T Drive<T>(Func<Task<T>> start, string label)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            var task = start();
            DrainAll();
            Verify(task, label);
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public void Drive(Func<Task> start, string label)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            var task = start();
            DrainAll();
            Verify(task, label);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public long Foreign { get; private set; }

    private void DrainAll()
    {
        var budget = DrainBudget;
        while (true)
        {
            while (_queue.TryDequeue(out var item))
            {
                if (--budget < 0)
                {
                    _queue.Clear();
                    throw new LeakedAwaitException($"runaway pump: more than {DrainBudget} continuations in one drive");
                }
                Drained++;
                item.Callback(item.State);
            }
            if (!DrainForeign())
            {
                return;
            }
        }
    }

    private bool DrainForeign()
    {
        var before = Posted;
        var context = Godot.Dispatcher.SynchronizationContext;
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            context.ExecutePendingContinuations();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
        var ran = Posted != before || !_queue.IsEmpty;
        if (ran)
        {
            Foreign++;
        }
        return ran;
    }

    public long Settled { get; private set; }

    private void Verify(Task task, string label)
    {
        if (!task.IsCompleted)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && sw.ElapsedMilliseconds < 2000)
            {
                Thread.Sleep(5);
                DrainAll();
            }
            if (task.IsCompleted)
            {
                Settled++;
                return;
            }
        }
        if (!task.IsCompleted)
        {
            throw new LeakedAwaitException(
                $"{label}: task still {task.Status} with an empty pump (posted={Posted}, drained={Drained})"
            );
        }
    }
}
