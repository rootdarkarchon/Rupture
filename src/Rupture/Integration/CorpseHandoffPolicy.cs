namespace Rupture.Integration;

// Pure handoff rules shared by the world (lease truth) and the coordinator (epoch truth).
// Deliberately free of Unity object and scene access so the regression harness can exercise it by reflection.
internal static class CorpseHandoffPolicy
{
	internal const float MinimumLeaseTimeout = 1f;
	internal const float MaximumLeaseTimeout = 120f;
	// A managed release must complete within this many rendered frames.
	internal const int ReleaseBoundaryFrames = 2;
	// Client Simulating fallback: no authority motion for this long reports false.
	internal const float ClientMotionSilenceSeconds = 2f;
	// Closed or failed tokens keep answering Adopt/Renew for this long before turning InvalidToken.
	internal const float TerminalRecordSeconds = 30f;

	internal static bool IsValidLeaseTimeout(float seconds) =>
		!float.IsNaN(seconds) && seconds >= MinimumLeaseTimeout && seconds <= MaximumLeaseTimeout;

	internal static OwnershipEpochDecision ClassifyEpoch(uint localEpoch, uint incomingEpoch, bool released)
	{
		if (incomingEpoch < localEpoch) return OwnershipEpochDecision.Stale;
		if (incomingEpoch == localEpoch) return OwnershipEpochDecision.Current;
		return released ? OwnershipEpochDecision.NewerRetain : OwnershipEpochDecision.NewerAdopt;
	}
}

internal enum OwnershipEpochDecision
{
	// Incoming epoch is older than local: drop.
	Stale = 0,
	// Same epoch: normal handling (while released: drop events, retain latest pose/settle).
	Current = 1,
	// Newer epoch while released: retain latest pose/settle only, never adopt.
	NewerRetain = 2,
	// Newer epoch while managed: take the epoch and handle the packet normally.
	NewerAdopt = 3
}

// Hitch-safe lease clock. The caller supplies unscaled time and the rendered frame count.
internal sealed class HandoffLeaseClock
{
	private bool _pastDue;
	private int _pastDueFrame;

	internal HandoffLeaseClock(float now, float timeout)
	{
		Timeout = timeout;
		Deadline = now + timeout;
	}

	internal float Timeout { get; }
	internal float Deadline { get; private set; }

	internal void Renew(float now)
	{
		Deadline = now + Timeout;
		_pastDue = false;
	}

	// Called once per simulation boundary. Expires only when the deadline is past due at two distinct
	// boundaries with at least one rendered frame between them, so a single main-thread stall lets the
	// consumer's next Renew win.
	internal bool ObserveExpired(float now, int frame)
	{
		if (now < Deadline)
		{
			_pastDue = false;
			return false;
		}
		if (!_pastDue)
		{
			_pastDue = true;
			_pastDueFrame = frame;
			return false;
		}
		return frame > _pastDueFrame;
	}
}
