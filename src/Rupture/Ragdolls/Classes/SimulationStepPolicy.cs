using System;

namespace Rupture.Ragdolls.Classes;

internal static class HeadlessSimulationPolicy
{
	internal const float MaximumStepRateInterval = 1f / 30f;
	private const float TimingEpsilon = 0.000001f;

	internal static bool TryConsume(float frameDeltaTime, ref float accumulator, out float simulationDeltaTime)
	{
		simulationDeltaTime = 0f;
		if (float.IsNaN(frameDeltaTime) || float.IsInfinity(frameDeltaTime) || frameDeltaTime <= 0f)
		{
			return false;
		}
		accumulator += frameDeltaTime;
		if (accumulator < MaximumStepRateInterval - TimingEpsilon)
		{
			return false;
		}
		if (frameDeltaTime >= MaximumStepRateInterval)
		{
			simulationDeltaTime = accumulator;
			accumulator = 0f;
		}
		else
		{
			simulationDeltaTime = MaximumStepRateInterval;
			accumulator = Math.Max(0f, accumulator - MaximumStepRateInterval);
		}
		return true;
	}
}

internal static class GraphicalSimulationPolicy
{
	internal static bool TryConsume(float elapsed, out float simulationDeltaTime)
	{
		simulationDeltaTime = 0f;
		if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed <= 0f) return false;
		simulationDeltaTime = elapsed;
		return true;
	}
}
