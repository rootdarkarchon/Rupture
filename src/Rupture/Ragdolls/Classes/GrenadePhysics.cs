using System.Collections.Generic;
using System.Diagnostics;
using EFT;
using EFT.Interactive;
using UnityEngine;
using Rupture.Combined.Patches;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Ragdolls.Classes;

internal static class GrenadePhysics
{
	private const int InitialBufferSize = 128;
	private const int MaximumBufferSize = 4096;
	private static readonly HashSet<object> AffectedStates = new HashSet<object>();
	private static readonly HashSet<Corpse> AffectedCorpses = new HashSet<Corpse>();
	private static Collider[] _overlapBuffer = new Collider[InitialBufferSize];
	private static bool _reportedTruncation;

	internal static void ApplyAuthorityRadialImpulse(IExplosiveItem grenadeItem, Vector3 grenadePosition)
	{
		if (grenadeItem == null || grenadeItem.IsDummy || RupturePlugin.Instance == null)
		{
			return;
		}
		float radius = Mathf.Max(0f, grenadeItem.MaxExplosionDistance);
		float force = Mathf.Max(0f, grenadeItem.GetStrength * 0.5f * RuptureConfiguration.GrenadeExplIntensity.Value);
		if (radius <= 0f || force <= 0f)
		{
			return;
		}
		int layerMask = LayerMask.GetMask("Deadbody", "HitCollider");
		int count;
		do
		{
			count = Physics.OverlapSphereNonAlloc(grenadePosition, radius, _overlapBuffer, layerMask, QueryTriggerInteraction.Ignore);
			if (count < _overlapBuffer.Length || _overlapBuffer.Length >= MaximumBufferSize)
			{
				break;
			}
			_overlapBuffer = new Collider[Mathf.Min(MaximumBufferSize, _overlapBuffer.Length * 2)];
		}
		while (true);

		if (count == _overlapBuffer.Length && _overlapBuffer.Length >= MaximumBufferSize && !_reportedTruncation)
		{
			_reportedTruncation = true;
			RupturePlugin.Log.LogWarning($"Corpse grenade overlap reached {MaximumBufferSize:N0} colliders; excess colliders are ignored.");
		}

		AffectedStates.Clear();
		AffectedCorpses.Clear();
		long requestLoopStarted = ShadowRagdollWorld.PerformanceLoggingEnabled ? Stopwatch.GetTimestamp() : 0L;
		for (int index = 0; index < count; index++)
		{
			Collider collider = _overlapBuffer[index];
			Corpse corpse = collider?.GetComponentInParent<Corpse>();
			if (corpse != null)
			{
				AffectedCorpses.Add(corpse);
			}
		}

		int cutRequests = 0;
		foreach (Corpse corpse in AffectedCorpses)
		{
			if (RagdollCoordinator.IsRemoteClient)
			{
				if (RagdollCoordinator.TryPrepareLocalRadialPrediction(corpse, out object predictedState))
					AffectedStates.Add(predictedState);
				continue;
			}
			if (RagdollCoordinator.PrepareCorpseImpact(corpse, explosion: true, AffectedStates, out CorpseInteractionTarget target, out bool postMortemImpact) &&
				postMortemImpact && target.VisualRoot != null)
			{
				Vector3 direction = corpse.transform.position - grenadePosition;
				if (KillPatch.TryDismemberCorpseExplosion(target, grenadePosition,
					direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.up)) cutRequests++;
			}
			if (RagdollCoordinator.TryGetState(corpse, out object stateToken) && RagdollCoordinator.IsDynamicState(stateToken))
			{
				AffectedStates.Add(stateToken);
			}
		}

		double requestLoopMilliseconds = requestLoopStarted == 0L ? 0d :
			(Stopwatch.GetTimestamp() - requestLoopStarted) * 1000d / Stopwatch.Frequency;
		long impulseStarted = ShadowRagdollWorld.PerformanceLoggingEnabled ? Stopwatch.GetTimestamp() : 0L;
		int appliedBodies = 0;
		foreach (object stateToken in AffectedStates)
		{
			appliedBodies += RagdollCoordinator.ApplyAuthorityRadialImpulse(stateToken, grenadePosition, radius, force);
		}
		// A settled corpse can retain a naturally sleeping independent weapon even
		// when corpse impact reactivation is disabled. That weapon no longer has a
		// dynamic coordinator state, so address it through the retained shadow rig.
		foreach (Corpse corpse in AffectedCorpses)
		{
			if (!RagdollCoordinator.IsRemoteClient && ShadowRagdollWorld.IsSettled(corpse) &&
				ShadowRagdollWorld.TryApplyRadialImpulse(corpse, grenadePosition, radius, force, out int independentBodies))
			{
				appliedBodies += independentBodies;
			}
		}
		// Living-sever gibs are local presentation on every graphical peer and are
		// not part of any corpse rig, including after their bot has died.
		int livingGibBodies = ShadowRagdollWorld.ApplyLivingGibRadialImpulse(grenadePosition, radius, force);
		if (livingGibBodies > 0)
			RupturePlugin.Log.LogInfo($"RPT living gib explosion impulse: appliedBodies={livingGibBodies}, radius={radius:F2}, force={force:F2}.");
		if (AffectedCorpses.Count > 0)
		{
			double impulseMilliseconds = impulseStarted == 0L ? 0d :
				(Stopwatch.GetTimestamp() - impulseStarted) * 1000d / Stopwatch.Frequency;
			RupturePlugin.Log.LogInfo($"RPT corpse explosion impulse: scannedCorpses={AffectedCorpses.Count}, dynamicCorpses={AffectedStates.Count}, appliedBodies={appliedBodies}, radius={radius:F2}, force={force:F2}." +
				(ShadowRagdollWorld.PerformanceLoggingEnabled
					? $" cutRequests={cutRequests}, requestAndWakeMs={requestLoopMilliseconds:F3}, corpseImpulseMs={impulseMilliseconds:F3}, {LimbDismembermentSystem.PerformanceSnapshot}."
					: string.Empty));
		}
	}
}
