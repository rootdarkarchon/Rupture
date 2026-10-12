using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Rupture.Dismemberment.Classes
{
// Result fields are published by Completed. Cancellation never releases data a
// running job still owns; Unity adoption and cleanup belong to the caller.
internal abstract class ManagedGeometryJob
{
    internal volatile bool Completed;
    internal volatile bool Cancelled;
    internal bool Succeeded;
    internal string Failure;
    internal double WorkerMilliseconds;

    internal void Execute()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            if (!Cancelled) Succeeded = TryExecute(out Failure);
        }
        catch (Exception exception)
        {
            Succeeded = false;
            Failure = exception.GetType().Name + ": " + exception.Message;
        }
        finally
        {
            if (Cancelled) { Succeeded = false; Failure = "cancelled"; }
            WorkerMilliseconds = timer.Elapsed.TotalMilliseconds;
            Completed = true;
        }
    }

    protected abstract bool TryExecute(out string failure);
}

// Shared by head generation, limb cutting, and Unity collider prebake. Callers
// admit bounded work and poll completion; the Unity thread never waits here.
internal static class ManagedGeometryWorker
{
    private static readonly object Gate = new object();
    private static readonly Queue<ManagedGeometryJob> Jobs = new Queue<ManagedGeometryJob>();
    private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
    private static Thread _thread;

    internal static T Enqueue<T>(T job) where T : ManagedGeometryJob
    {
        lock (Gate)
        {
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "RPT Geometry",
                    Priority = System.Threading.ThreadPriority.BelowNormal };
                _thread.Start();
            }
            Jobs.Enqueue(job);
        }
        Signal.Set();
        return job;
    }

    private static void Run()
    {
        while (true)
        {
            ManagedGeometryJob job = null;
            lock (Gate) if (Jobs.Count > 0) job = Jobs.Dequeue();
            if (job == null) { Signal.WaitOne(); continue; }
            job.Execute();
        }
    }
}
}
