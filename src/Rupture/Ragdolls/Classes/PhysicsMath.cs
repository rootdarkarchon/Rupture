namespace Rupture.Ragdolls.Classes;

internal static class PhysicsMath
{
	// Real projectile momentum barely perturbs a jointed, human-mass ragdoll.
	// RPT is a visual-impact mod, so retain a bounded presentation gain while
	// keeping caliber, mass, velocity and the user multiplier proportional.
	private const float CorpseReactionGain = 4f;
	private const float MaximumProjectileImpulse = 500f;

	internal static float CalculateProjectileImpulse(float massGram, float velocityMetersPerSecond, float multiplier)
	{
		if (float.IsNaN(massGram) || float.IsInfinity(massGram) || float.IsNaN(velocityMetersPerSecond) || float.IsInfinity(velocityMetersPerSecond) || float.IsNaN(multiplier) || float.IsInfinity(multiplier) || massGram <= 0f || velocityMetersPerSecond <= 0f || multiplier <= 0f)
		{
			return 0f;
		}
		float momentum = massGram / 1000f * velocityMetersPerSecond * multiplier * CorpseReactionGain;
		return momentum > MaximumProjectileImpulse ? MaximumProjectileImpulse : momentum;
	}
}
