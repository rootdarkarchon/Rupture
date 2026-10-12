using System;
using System.Collections.Generic;
using System.Threading;

namespace Rupture.Dismemberment.Classes.Limbs;

// Completed geometry is immutable while exported. Never place disk work on the
// geometry/collider worker or make renderer commit wait for a diagnostic file.
internal static class LimbCutCaptureQueue
{
	private sealed class Export
	{
		internal Func<string> Write;
		internal int Generation;
	}
	private static readonly object Gate = new object();
	private static readonly Queue<Export> Pending = new Queue<Export>();
	private static readonly Queue<string> Completed = new Queue<string>();
	private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
	private const int MaximumOutstanding = 4;
	private static int _generation, _outstanding;
	private static Thread _worker;

	internal static bool TryEnqueue(Func<string> write)
	{
		lock (Gate)
		{
			if (_outstanding >= MaximumOutstanding) return false;
			if (_worker == null)
			{
				_worker = new Thread(Run) { IsBackground = true, Name = "RPT Limb Cut Capture", Priority = ThreadPriority.Lowest };
				try { _worker.Start(); }
				catch { _worker = null; throw; }
			}
			_outstanding++;
			Pending.Enqueue(new Export { Write = write, Generation = _generation });
		}
		Signal.Set();
		return true;
	}

	internal static bool TryDequeueReport(out string report)
	{
		lock (Gate)
		{
			report = Completed.Count > 0 ? Completed.Dequeue() : null;
			return report != null;
		}
	}

	internal static void Reset()
	{
		lock (Gate)
		{
			_generation++;
			_outstanding -= Pending.Count;
			Pending.Clear();
			Completed.Clear();
		}
	}

	private static void Run()
	{
		while (true)
		{
			Export export;
			lock (Gate) export = Pending.Count > 0 ? Pending.Dequeue() : null;
			if (export == null) { Signal.WaitOne(); continue; }
			string report;
			try { report = export.Write(); }
			catch (Exception exception) { report = "captureError=" + exception.GetType().Name + ": " + exception.Message; }
			lock (Gate)
			{
				_outstanding--;
				if (export.Generation != _generation) continue;
				if (Completed.Count >= 64) Completed.Dequeue();
				Completed.Enqueue(report);
			}
		}
	}
}
