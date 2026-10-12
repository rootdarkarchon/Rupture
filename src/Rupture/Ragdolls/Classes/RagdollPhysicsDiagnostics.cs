using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal static class RagdollPhysicsDiagnostics
{
	private static readonly List<Collider> ColliderScratch = new List<Collider>(16);

	internal static void Update()
	{
		PhysicsCounterSampler.Update();
	}

	internal static void Reset()
	{
		PhysicsCounterSampler.Reset();
		ColliderScratch.Clear();
	}

	internal static void LogStatus()
	{
		try
		{
			int supported = 0;
			int supportedActive = 0;
			int supportedVisible = 0;
			int supportedDynamic = 0;
			int supportedAwake = 0;
			int supportedRptBodies = 0;
			int supportedWithVisibilityCheck = 0;

			if (PhysicsExtensions.UpdateController._rigidbodies != null)
			{
				foreach (PhysicsExtensions.UpdateController.RigidbodyData entry in PhysicsExtensions.UpdateController._rigidbodies)
				{
					Rigidbody rigidbody = entry.rigidbody;
					if (rigidbody == null)
					{
						continue;
					}

					supported++;
					if (rigidbody.gameObject.activeInHierarchy)
					{
						supportedActive++;
					}
					if (!rigidbody.isKinematic)
					{
						supportedDynamic++;
					}
					if (!rigidbody.IsSleeping())
					{
						supportedAwake++;
					}
					if (RagdollCoordinator.IsTrackedBody(rigidbody))
					{
						supportedRptBodies++;
					}

					if (entry.visibilityChecker == null)
					{
						supportedVisible++;
					}
					else
					{
						supportedWithVisibilityCheck++;
						if (entry.visibilityChecker.IsVisible)
						{
							supportedVisible++;
						}
					}
				}
			}

			RagdollCoordinator.GetDiagnosticCounts(
				out int activeStates,
				out int trackedBodies,
				out int trackedDynamic,
				out int trackedAwake);

			RupturePlugin.Log.LogInfo($"RPT physics status: nativeHitMask={EFT.Ballistics.BallisticsCalculatorConstants.HitMask.value}, supportEnabled={PhysicsExtensions.UpdateController.Enabled}, " +
				$"updateMode={PhysicsExtensions.Simulation.UpdateMode}, " +
				$"simulationMode={Physics.simulationMode}, autoSyncTransforms={Physics.autoSyncTransforms}, " +
				$"updateEnabled={PhysicsExtensions.Simulation.UpdateEnabled}, " +
				$"fixedDeltaTime={Time.fixedDeltaTime:F4}, averageSimulationDelta={PhysicsExtensions.Simulation.SimulationAVGDeltaTime:F4}, " +
				$"supported={supported}, active={supportedActive}, visible={supportedVisible}, dynamic={supportedDynamic}, awake={supportedAwake}, " +
				$"withVisibilityCheck={supportedWithVisibilityCheck}, rptBodies={supportedRptBodies}, " +
				$"rptStates={activeStates}, rptTracked={trackedBodies}, rptDynamic={trackedDynamic}, rptAwake={trackedAwake}.");
		}
		catch (Exception exception)
		{
			RupturePlugin.Log.LogWarning($"RPT physics status is unavailable: {exception.Message}");
		}
	}

	internal static void LogSupportedBodies()
	{
		try
		{
			int index = 0;
			IReadOnlyList<PhysicsExtensions.UpdateController.RigidbodyData> entries = PhysicsExtensions.UpdateController._rigidbodies;
			RupturePlugin.Log.LogInfo($"RPT supported physics bodies: count={entries?.Count ?? 0}.");
			if (entries == null)
			{
				return;
			}

			foreach (PhysicsExtensions.UpdateController.RigidbodyData entry in entries)
			{
				Rigidbody rigidbody = entry.rigidbody;
				if (rigidbody == null)
				{
					RupturePlugin.Log.LogInfo($"RPT supported body [{index++}]: destroyed/null.");
					continue;
				}

				ColliderScratch.Clear();
				rigidbody.GetComponentsInChildren(includeInactive: true, result: ColliderScratch);
				int ownedColliders = ColliderScratch.Count(collider => collider != null && collider.attachedRigidbody == rigidbody);
				string layer = LayerMask.LayerToName(rigidbody.gameObject.layer);
				if (string.IsNullOrEmpty(layer))
				{
					layer = rigidbody.gameObject.layer.ToString();
				}

				RupturePlugin.Log.LogInfo($"RPT supported body [{index++}]: id={rigidbody.GetInstanceID()}, name={rigidbody.name}, " +
					$"path={PhysicsSceneCensus.GetHierarchyPath(rigidbody.transform)}, layer={layer}, " +
					$"active={rigidbody.gameObject.activeInHierarchy}, kinematic={rigidbody.isKinematic}, sleeping={rigidbody.IsSleeping()}, " +
					$"detectCollisions={rigidbody.detectCollisions}, collisionMode={rigidbody.collisionDetectionMode}, colliders={ownedColliders}, " +
					$"rptTracked={RagdollCoordinator.IsTrackedBody(rigidbody)}, visibilityCheck={entry.visibilityChecker != null}, " +
					$"owners={PhysicsSceneCensus.GetOwnerTypes(rigidbody)}.");
			}
			ColliderScratch.Clear();
		}
		catch (Exception exception)
		{
			RupturePlugin.Log.LogWarning($"RPT supported physics body dump is unavailable: {exception.Message}");
		}
	}

	internal static void LogSceneCensus()
	{
		PhysicsSceneCensus.Log();
	}

	internal static void ToggleCounterSample()
	{
		PhysicsCounterSampler.Toggle();
	}
}
