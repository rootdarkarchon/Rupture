using System;
using System.Collections.Generic;

namespace Rupture.Ragdolls.Classes;

// EFT removals complete asynchronously. Never start the next inventory operation
// until removal, successful completion and world-loot creation are confirmed.
// Failure or timeout stops the remaining sequence.
internal class HeadgearThrowSequence<T> where T : class
{
	internal readonly List<T> Items = new List<T>(4);
	internal T WaitingItem;
	private int _nextItem;
	private float _deadline;

	internal bool Advance(float now, Func<T, bool> equipped, Action<T> startThrow, Action<T> timedOut,
		Func<T, bool> completed, Func<T, bool> failed)
	{
		if (WaitingItem != null)
		{
			if (failed(WaitingItem)) return true;
			if (equipped(WaitingItem) || !completed(WaitingItem))
			{
				if (now < _deadline) return false;
				timedOut(WaitingItem);
				return true;
			}
			WaitingItem = null;
			_nextItem++;
		}
		while (_nextItem < Items.Count && !equipped(Items[_nextItem])) _nextItem++;
		if (_nextItem == Items.Count) return true;
		WaitingItem = Items[_nextItem];
		_deadline = now + 1f;
		startThrow(WaitingItem);
		return false;
	}
}
