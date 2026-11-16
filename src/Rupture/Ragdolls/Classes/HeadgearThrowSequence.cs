using System.Collections.Generic;

namespace Rupture.Ragdolls.Classes;

// EFT removals complete asynchronously. Never start the next inventory operation
// until removal, successful completion and world-loot creation are confirmed.
// Failure or timeout stops the remaining sequence. The sequence's own state
// supplies the callbacks, so queuing a request allocates no delegates.
internal abstract class HeadgearThrowSequence<T> where T : class
{
	/// <summary>Realtime seconds the next throw may wait for a frame with room before the caller should start it anyway.</summary>
	internal const float MaximumStartWaitSeconds = 0.5f;

	internal readonly List<T> Items = new List<T>(4);
	internal T WaitingItem;
	private int _nextItem;
	private float _deadline;
	private float _readySince = float.NaN;

	/// <summary>Index of the first item that was not yet submitted; the waiting item, when any, sits at this index.</summary>
	internal int NextItemIndex => _nextItem;

	/// <summary>Whether the item is still equipped and should be thrown or kept waiting on.</summary>
	protected abstract bool ItemEquipped(T item);

	/// <summary>Submits the throw. <see cref="WaitingItem"/> is already the item and its deadline is armed.</summary>
	protected abstract void BeginThrow(T item);

	protected abstract void ThrowTimedOut(T item);

	protected abstract bool ThrowCompleted(T item);

	protected abstract bool ThrowFailed(T item);

	/// <summary>
	/// True when the next throw is ready to start but has been waiting for a frame with room for at least
	/// <see cref="MaximumStartWaitSeconds"/>. The clock starts at the first <see cref="Advance"/> call and again
	/// whenever a throw completes, never while a throw is in flight.
	/// </summary>
	internal bool StartOverdue(float now) => WaitingItem == null && now - _readySince >= MaximumStartWaitSeconds;

	/// <summary>
	/// Polls the in-flight throw and, when <paramref name="allowStart"/> is set, starts the next one.
	/// Waiting for completion is never deferred; only the start of a throw is.
	/// </summary>
	/// <param name="now">Realtime clock shared with the one second completion deadline.</param>
	/// <param name="allowStart">False while the frame has no room for another throw.</param>
	/// <param name="started">True when this call began a throw.</param>
	/// <returns>True when the sequence is finished, failed or timed out.</returns>
	internal bool Advance(float now, bool allowStart, out bool started)
	{
		started = false;
		if (float.IsNaN(_readySince)) _readySince = now;
		if (WaitingItem != null)
		{
			if (ThrowFailed(WaitingItem)) return true;
			if (ItemEquipped(WaitingItem) || !ThrowCompleted(WaitingItem))
			{
				if (now < _deadline) return false;
				ThrowTimedOut(WaitingItem);
				return true;
			}
			WaitingItem = null;
			_nextItem++;
			_readySince = now;
		}
		if (_nextItem >= Items.Count) return true;
		if (!allowStart) return false;
		while (_nextItem < Items.Count && !ItemEquipped(Items[_nextItem])) _nextItem++;
		if (_nextItem == Items.Count) return true;
		WaitingItem = Items[_nextItem];
		// The completion deadline starts with the throw, never while the item waits for a frame slot.
		_deadline = now + 1f;
		started = true;
		BeginThrow(WaitingItem);
		return false;
	}
}
