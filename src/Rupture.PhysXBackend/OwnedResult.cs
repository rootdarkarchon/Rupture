using System;

namespace Rupture.PhysXBackend;

// Publication and cancellation share one ownership boundary, independent of Task completion.
internal sealed class OwnedResult<T> where T : class
{
	private readonly object _gate = new object();
	private T _value;
	private bool _closed;

	internal bool TryPublish(T value)
	{
		lock (_gate)
		{
			if (_closed) return false;
			if (_value != null) throw new InvalidOperationException("A result was already published.");
			_value = value;
			return true;
		}
	}

	internal T Take()
	{
		lock (_gate)
		{
			_closed = true;
			T value = _value;
			_value = null;
			return value;
		}
	}
}
