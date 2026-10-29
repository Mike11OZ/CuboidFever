using System;
using System.Collections.Concurrent;
using System.Threading;

namespace CuboidFever;

public sealed class WorkerPool
{
    private readonly ConcurrentQueue<Action> _jobs = new();
    private readonly SemaphoreSlim _signal = new(0);

    public int Count { get; }

    public WorkerPool(int threads)
    {
        Count = threads;
        for (int i = 0; i < threads; i++)
            new Thread(Loop) { IsBackground = true, Name = $"CuboidFever worker {i}" }.Start();
    }

    public void Enqueue(Action job)
    {
        _jobs.Enqueue(job);
        _signal.Release();
    }

    private void Loop()
    {
        while (true)
        {
            _signal.Wait();
            if (!_jobs.TryDequeue(out var job)) continue;
            try { job(); }
            catch (Exception e) { Console.Error.WriteLine($"[worker] {e}"); } // errors show in the terminal
        }
    }
}