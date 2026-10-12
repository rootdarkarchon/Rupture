using System;

namespace Rupture.Ragdolls.Classes;

internal static class CorpseSleepPolicy
{
	internal const int MinimumDurationSeconds = 1;
	internal const int MaximumDurationSeconds = 15;
	internal const float NativeInitialDelaySeconds = 1f;

	internal static int ClampDuration(int seconds)
	{
		return Math.Max(MinimumDurationSeconds, Math.Min(MaximumDurationSeconds, seconds));
	}

	internal static bool ShouldForceSleep(float nativeTimePass, int totalDurationSeconds)
	{
		float loopDuration = Math.Max(0f, ClampDuration(totalDurationSeconds) - NativeInitialDelaySeconds);
		return nativeTimePass >= loopDuration;
	}
}
