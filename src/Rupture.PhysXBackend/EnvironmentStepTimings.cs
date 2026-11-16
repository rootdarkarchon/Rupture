using System;
using System.Diagnostics;
using System.Text;

namespace Rupture.PhysXBackend;

/// <summary>The measured sub-steps of one <c>CaptureEnvironmentChanges</c> call; the names are the status-line keys.</summary>
/// <remarks>
/// Steps run one after another and sum to (nearly) the call's total, except the dotted ones, which are measured inside a step
/// that already contains them: <see cref="JobsTerrain"/> and <see cref="JobsSync"/> inside <see cref="Jobs"/>,
/// <see cref="Promote"/> inside <see cref="StaticAudit"/>. <see cref="Rebuild"/> counts every actor-shape rebuild wherever it runs
/// (the movable poll and the promotion completion), so it overlaps <see cref="Movable"/> and <see cref="Promotions"/>.
/// </remarks>
internal enum EnvironmentStep
{
	Source,
	Startup,
	Terrain,
	Scenes,
	Deferred,
	Expire,
	Jobs,
	JobsTerrain,
	JobsSync,
	Promotions,
	StaticAudit,
	Promote,
	Movable,
	Rebuild,
	ShapeAudit
}

/// <summary>
/// Allocation-free per-step timing of the environment maintenance call: the last and the longest duration of every step, and
/// the complete breakdown of the longest call so far, so a frame hitch can be attributed to one step without a profiler.
/// Main thread only; the arrays are allocated once and <see cref="Stopwatch"/> ticks are the only unit stored.
/// </summary>
internal sealed class EnvironmentStepTimings
{
	private static readonly string[] Names =
	{
		"source", "startup", "terrain", "scenes", "deferred", "expire", "jobs", "jobs.terrain", "jobs.sync",
		"promotions", "staticAudit", "staticAudit.promote", "movable", "rebuild", "shapeAudit"
	};

	private static readonly int StepCount = Names.Length;
	private readonly long[] _frame = new long[StepCount];
	private readonly long[] _last = new long[StepCount];
	private readonly long[] _max = new long[StepCount];
	private readonly long[] _worst = new long[StepCount];
	private long _lastTotal, _maxTotal;
	private long _calls;

	/// <summary>Number of calls measured since the last <see cref="Reset"/>.</summary>
	internal long Calls => _calls;

	/// <summary>Longest call so far, in stopwatch ticks.</summary>
	internal long MaxTotalTicks => _maxTotal;

	/// <summary>Longest duration of one step so far, in stopwatch ticks.</summary>
	internal long MaxTicks(EnvironmentStep step) => _max[(int)step];

	/// <summary>Duration of one step in the longest call so far, in stopwatch ticks.</summary>
	internal long WorstFrameTicks(EnvironmentStep step) => _worst[(int)step];

	/// <summary>Starts a call: clears the per-call accumulators.</summary>
	internal void BeginFrame() => Array.Clear(_frame, 0, _frame.Length);

	/// <summary>Charges the time since <paramref name="since"/> to <paramref name="step"/> and returns the new timestamp, so consecutive steps chain.</summary>
	internal long Lap(EnvironmentStep step, long since)
	{
		long now = Stopwatch.GetTimestamp();
		_frame[(int)step] += now - since;
		return now;
	}

	/// <summary>Charges a duration measured by the caller (a step nested in another one) to <paramref name="step"/>.</summary>
	internal void Add(EnvironmentStep step, long ticks) => _frame[(int)step] += ticks;

	/// <summary>Finishes a call; true when it was the longest so far, in which case its breakdown is kept.</summary>
	internal bool EndFrame(long totalTicks)
	{
		_calls++;
		_lastTotal = totalTicks;
		Array.Copy(_frame, _last, StepCount);
		for (int index = 0; index < StepCount; index++)
			if (_frame[index] > _max[index]) _max[index] = _frame[index];
		if (totalTicks <= _maxTotal) return false;
		_maxTotal = totalTicks;
		Array.Copy(_frame, _worst, StepCount);
		return true;
	}

	internal void Reset()
	{
		Array.Clear(_frame, 0, StepCount);
		Array.Clear(_last, 0, StepCount);
		Array.Clear(_max, 0, StepCount);
		Array.Clear(_worst, 0, StepCount);
		_lastTotal = _maxTotal = 0L;
		_calls = 0L;
	}

	/// <summary>
	/// <c>envSteps=calls/lastMs/maxMs</c> of the whole call, then <c>name:lastMs/maxMs</c> per step, then the steps of the longest
	/// call as <c>worstFrame=totalMs{name:ms,...}</c> (steps under 0.05 ms omitted). Status-line text; allocates.
	/// </summary>
	internal string Describe()
	{
		StringBuilder text = new StringBuilder(512);
		text.Append("envSteps=calls:").Append(_calls)
			.Append("/lastMs:").Append(Milliseconds(_lastTotal))
			.Append("/maxMs:").Append(Milliseconds(_maxTotal));
		for (int index = 0; index < StepCount; index++)
			text.Append('/').Append(Names[index]).Append(':').Append(Milliseconds(_last[index])).Append('|').Append(Milliseconds(_max[index]));
		text.Append(", envWorstFrame=").Append(Milliseconds(_maxTotal)).Append("ms{");
		bool first = true;
		for (int index = 0; index < StepCount; index++)
		{
			if (_worst[index] * 20000L < Stopwatch.Frequency) continue;
			if (!first) text.Append(',');
			first = false;
			text.Append(Names[index]).Append(':').Append(Milliseconds(_worst[index]));
		}
		return text.Append("}, ").ToString();
	}

	private static string Milliseconds(long ticks) => (ticks * 1000d / Stopwatch.Frequency).ToString("F2");
}
