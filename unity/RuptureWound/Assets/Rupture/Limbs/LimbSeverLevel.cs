using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs
{

public enum LimbSeverLevel : byte
{
	Proximal = 1,
	Distal = 2
}

internal static class LimbSeverMath
{
	// Upper cuts need to leave a visible anatomical stump. Fifteen percent from
	// the hip/shoulder was effectively a joint disarticulation and made the cut
	// appear to enter the torso. Lower cuts intentionally remain close to the
	// elbow/knee end of their segment.
	// Move another five percent away from the torso after the original 45%
	// torso-to-joint placement.
	internal const float ProximalSegmentFraction = 0.50f;
	internal const float DistalSegmentFraction = 0.15f;

	internal static Vector3 Candidate(Vector3 proximal, Vector3 distal, LimbSeverLevel level, float fractionOffset = 0f)
	{
		float fraction = level == LimbSeverLevel.Proximal
			? ProximalSegmentFraction
			: DistalSegmentFraction;
		return Vector3.Lerp(proximal, distal, Mathf.Clamp01(fraction + fractionOffset));
	}

	internal static LimbSeverLevel Closest(Vector3 impact, Vector3 proximalCandidate, Vector3 distalCandidate)
	{
		float proximalDistance = (impact - proximalCandidate).sqrMagnitude;
		float distalDistance = (impact - distalCandidate).sqrMagnitude;
		return proximalDistance < distalDistance ? LimbSeverLevel.Proximal : LimbSeverLevel.Distal;
	}
}
}
