namespace Rupture.PhysXBackend;

/// <summary>
/// Peak-holding estimate of a measured main-thread cost in <see cref="System.Diagnostics.Stopwatch"/> ticks. A sample above
/// the estimate replaces it; a lower one decays it by an eighth, so a one-off spike fades after a few cheaper samples while a
/// recurring cost stays visible. A budgeted slice uses it to decide whether the next item still fits before it starts.
/// </summary>
internal struct PeakCost
{
	internal long Ticks;

	internal void Observe(long ticks) => Ticks = ticks >= Ticks ? ticks : Ticks - ((Ticks - ticks) >> 3);

	internal void Reset() => Ticks = 0;
}
