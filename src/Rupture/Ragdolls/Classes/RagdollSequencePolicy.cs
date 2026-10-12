using System.Collections.Generic;

namespace Rupture.Ragdolls.Classes;

internal enum DeathSequenceRelation
{
	Invalid,
	Stale,
	Current,
	Newer
}

internal static class RagdollSequencePolicy
{
	internal static DeathSequenceRelation Classify(uint currentDeathSequence, uint incomingDeathSequence)
	{
		if (incomingDeathSequence == 0)
		{
			return DeathSequenceRelation.Invalid;
		}
		if (currentDeathSequence == 0 || incomingDeathSequence > currentDeathSequence)
		{
			return DeathSequenceRelation.Newer;
		}
		return incomingDeathSequence == currentDeathSequence
			? DeathSequenceRelation.Current
			: DeathSequenceRelation.Stale;
	}

	internal static bool TryAcceptEvent(uint currentDeathSequence, uint incomingDeathSequence, uint eventSequence, HashSet<uint> receivedEvents)
	{
		return eventSequence != 0
			&& Classify(currentDeathSequence, incomingDeathSequence) == DeathSequenceRelation.Current
			&& receivedEvents != null
			&& receivedEvents.Add(eventSequence);
	}

	internal static bool IsExpired(float now, float expiresAt)
	{
		return expiresAt > 0f && now >= expiresAt;
	}
}
