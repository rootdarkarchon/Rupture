using System;
using System.Diagnostics;
using Rupture.Ragdolls.Classes;
using MainThreadStage = Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadStage;

namespace Rupture;

// Frame-cost summary of the main-thread work Rupture does right after kills. Only
// the performance diagnostic switches it on; all state is preallocated, so a
// disabled trace costs one flag check per kill and per callback.
internal static class KillWindowTrace
{
	internal const int WindowFrames = 60;
	// A firefight keeps extending the window; report it periodically instead of never.
	internal const int MaximumWindowFrames = 600;
	private const int SeenCapacity = 32;
	private const long OverBudgetFrameMilliseconds = 4L;

	private static readonly string[] SeenProfiles = new string[SeenCapacity];
	private static int _seenCount;
	private static bool _active;
	private static int _kills;
	private static int _frames;
	private static int _framesRemaining;
	private static int _framesOverBudget;
	private static long _frameTicks;
	private static long _totalTicks;
	private static long _worstFrameTicks;
	private static MainThreadStage _worstStage;
	private static double _worstStageMilliseconds;
	private static int _gen0Start;
	private static long _heapStart;

	/// <summary>Opens a window for a new kill, or extends the running one. Death-frame path: flag check only when disabled.</summary>
	internal static void BeginKill(string profileId)
	{
		if (!ShadowRagdollWorld.PerformanceLoggingEnabled) return;
		if (_active)
		{
			// A repeated lethal callback for the same death is not another kill.
			for (int index = 0; index < _seenCount; index++)
				if (string.Equals(SeenProfiles[index], profileId, StringComparison.Ordinal)) return;
			_kills++;
			_framesRemaining = WindowFrames;
			Remember(profileId);
			return;
		}
		_active = true;
		_kills = 1;
		_frames = 0;
		_framesRemaining = WindowFrames;
		_framesOverBudget = 0;
		_totalTicks = 0L;
		_worstFrameTicks = 0L;
		_worstStage = default;
		_worstStageMilliseconds = 0d;
		_gen0Start = GC.CollectionCount(0);
		_heapStart = GC.GetTotalMemory(false);
		Remember(profileId);
	}

	/// <summary>Adds one plugin callback's elapsed time to the current frame. A zero timestamp means timing was off.</summary>
	internal static void AddMainThread(long startedTimestamp)
	{
		if (startedTimestamp != 0L) _frameTicks += Stopwatch.GetTimestamp() - startedTimestamp;
	}

	/// <summary>Closes the frame after the last plugin callback; reports the window when it has run its course.</summary>
	internal static void EndFrame(long lateUpdateStartedTimestamp)
	{
		AddMainThread(lateUpdateStartedTimestamp);
		long frameTicks = _frameTicks;
		_frameTicks = 0L;
		if (!_active) return;
		_frames++;
		_totalTicks += frameTicks;
		if (frameTicks * 1000L > OverBudgetFrameMilliseconds * Stopwatch.Frequency) _framesOverBudget++;
		if (frameTicks > _worstFrameTicks)
		{
			_worstFrameTicks = frameTicks;
			ShadowRagdollWorld.GetFrameWorstStage(out _worstStage, out _worstStageMilliseconds);
		}
		if (--_framesRemaining > 0 && _frames < MaximumWindowFrames) return;
		Report();
	}

	internal static void Reset()
	{
		_active = false;
		_frameTicks = 0L;
		ClearSeen();
	}

	private static void Remember(string profileId)
	{
		// A full table only weakens duplicate suppression; it never drops a kill.
		if (_seenCount < SeenCapacity) SeenProfiles[_seenCount++] = profileId;
	}

	private static void ClearSeen()
	{
		Array.Clear(SeenProfiles, 0, _seenCount);
		_seenCount = 0;
	}

	private static void Report()
	{
		_active = false;
		ClearSeen();
		if (!ShadowRagdollWorld.PerformanceLoggingEnabled) return;
		long heapDeltaKb = (GC.GetTotalMemory(false) - _heapStart) / 1024L;
		RupturePlugin.Log.LogInfo($"RPT kill window: kills={_kills}, frames={_frames}, totalMainMs={Milliseconds(_totalTicks):F3}, " +
			$"worstFrameMs={Milliseconds(_worstFrameTicks):F3}, worstFrameStage={_worstStage}:{_worstStageMilliseconds:F3}, " +
			$"framesOver4ms={_framesOverBudget}, gen0Collections={GC.CollectionCount(0) - _gen0Start}, heapDeltaKb={heapDeltaKb}.");
	}

	private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}
