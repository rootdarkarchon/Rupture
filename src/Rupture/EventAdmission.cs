using System;
using System.Collections.Generic;

namespace Rupture;

internal static class EventAdmission
{
	// A deferred attempt retains the event for retry; admission commits deduplication.
	internal static bool TryApply(HashSet<uint> received, uint sequence, Func<bool> admit)
	{
		if (received.Contains(sequence)) return true;
		if (!admit()) return false;
		received.Add(sequence);
		return true;
	}
}
