using System.Collections.Generic;

namespace Rupture.Dismemberment.Classes.HeadWounds;

// Authority admission is independent of graphical success (including on headless hosts).
internal sealed class HeadWoundPolicy
{
	internal const int MaximumWounds = 4;
	internal const int MaximumAttempts = 16;
	private readonly HashSet<ulong> _projectiles = new HashSet<ulong>();
	internal int Count { get; private set; }
	internal bool Remember(ulong projectile) => projectile != 0 && _projectiles.Add(projectile);
	internal int Reserve(bool enabled)
	{
		if (!enabled || Count == MaximumAttempts) return 0;
		return ++Count;
	}
}

// Attempt ordinals are independent of the four successfully adopted meshes.
internal sealed class HeadWoundOrder
{
	private int _queued;
	private int _seen;
	private int _finished;
	internal int Next { get; private set; } = 1;
	internal bool TryQueue(int ordinal)
	{
		if (ordinal < 1 || ordinal > HeadWoundPolicy.MaximumAttempts || (_seen & (1 << ordinal)) != 0) return false;
		_queued |= 1 << ordinal; _seen |= 1 << ordinal;
		return true;
	}
	internal bool Contains(int ordinal) => (_queued & (1 << ordinal)) != 0;
	internal void Discard(int ordinal)
	{
		if (!Contains(ordinal)) return;
		_queued &= ~(1 << ordinal); _finished |= 1 << ordinal;
		while (Next <= HeadWoundPolicy.MaximumAttempts && (_finished & (1 << Next)) != 0) Next++;
	}
	internal void Complete(int ordinal)
	{
		if (ordinal != Next || !Contains(ordinal)) return;
		Discard(ordinal);
	}
}
