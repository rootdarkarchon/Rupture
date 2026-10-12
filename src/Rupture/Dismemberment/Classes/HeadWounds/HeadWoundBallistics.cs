using System;

namespace Rupture.Dismemberment.Classes.HeadWounds;

internal readonly struct HeadWoundShape
{
	internal readonly float Momentum;
	internal readonly float Energy;
	internal readonly float EntryRadius;
	internal readonly float ExitRadius;
	internal readonly float ConeDepth;

	internal HeadWoundShape(float momentum, float energy, float entryRadius, float exitRadius, float coneDepth)
	{
		Momentum = momentum;
		Energy = energy;
		EntryRadius = entryRadius;
		ExitRadius = exitRadius;
		ConeDepth = coneDepth;
	}
}

internal static class HeadWoundBallistics
{
	internal const float MinimumEntryRadius = 0.003f;
	internal const float MaximumEntryRadius = 0.010f;
	internal const float MinimumExitMultiplier = 1.6f;
	internal const float MaximumExitMultiplier = 4f;
	internal const float MaximumAbsoluteExitRadius = 0.030f;
	internal const float DefaultConeDepth = 0.3f;

	internal static bool TryCalculate(float massGrams, float impactVelocity, float diameterMillimeters,
		float penetrationPower, string caliber, out HeadWoundShape shape)
	{
		shape = default;
		if (!IsFinitePositive(massGrams) || !IsFinitePositive(impactVelocity) || !IsFinitePositive(diameterMillimeters))
		{
			return false;
		}

		float massKilograms = massGrams * 0.001f;
		float momentum = massKilograms * impactVelocity;
		float energy = 0.5f * massKilograms * impactVelocity * impactVelocity;
		if (!IsFinitePositive(momentum) || !IsFinitePositive(energy))
		{
			return false;
		}

		float entryRadius = Clamp(diameterMillimeters * 0.0006f, MinimumEntryRadius, MaximumEntryRadius);
		float energyTerm = (float)Math.Sqrt(Clamp(energy / 1000f, 0f, 8f)) * 0.65f;
		float momentumTerm = (float)Math.Sqrt(Clamp(momentum / 8f, 0f, 2f)) * 0.25f;
		float penetrationTerm = Clamp(IsFinitePositive(penetrationPower) ? penetrationPower / 60f : 0f, 0f, 1f) * 0.5f;
		float caliberTerm = ResolveCaliberExitTerm(caliber);
		float exitMultiplier = Clamp(1.2f + energyTerm + momentumTerm + penetrationTerm + caliberTerm,
			MinimumExitMultiplier, MaximumExitMultiplier);
		float exitRadius = Math.Min(entryRadius * exitMultiplier, MaximumAbsoluteExitRadius);
		shape = new HeadWoundShape(momentum, energy, entryRadius, exitRadius, DefaultConeDepth);
		return true;
	}

	internal static uint CreateSeed(string profileId, uint deathSequence, int fireIndex)
	{
		uint hash = 2166136261u;
		if (!string.IsNullOrEmpty(profileId))
		{
			for (int index = 0; index < profileId.Length; index++)
			{
				hash ^= profileId[index];
				hash *= 16777619u;
			}
		}
		hash ^= deathSequence;
		hash *= 16777619u;
		hash ^= unchecked((uint)fireIndex);
		hash *= 16777619u;
		return hash == 0 ? 1u : hash;
	}

	internal static float Next01(ref uint state)
	{
		state = state == 0 ? 0x6D2B79F5u : state;
		state ^= state << 13;
		state ^= state >> 17;
		state ^= state << 5;
		return (state & 0x00FFFFFFu) / 16777215f;
	}

	private static float ResolveCaliberExitTerm(string caliber)
	{
		if (string.IsNullOrEmpty(caliber))
		{
			return 0f;
		}
		string normalized = caliber.ToLowerInvariant();
		if (normalized.Contains("12g") || normalized.Contains("20g") || normalized.Contains("23x75") || normalized.Contains("shotgun"))
		{
			return 0.55f;
		}
		if (normalized.Contains("338") || normalized.Contains("127x") || normalized.Contains("12.7") || normalized.Contains("50bmg"))
		{
			return 0.65f;
		}
		if (normalized.Contains("762x51") || normalized.Contains("762x54") || normalized.Contains("68x51") || normalized.Contains("86x70"))
		{
			return 0.35f;
		}
		if (normalized.Contains("762x39") || normalized.Contains("545x39") || normalized.Contains("556x45") || normalized.Contains("9x39"))
		{
			return 0.2f;
		}
		return 0.05f;
	}

	private static bool IsFinitePositive(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
	}

	private static float Clamp(float value, float minimum, float maximum)
	{
		return Math.Max(minimum, Math.Min(maximum, value));
	}
}
