using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rupture.Ragdolls.Classes;

internal static class PhysicsSceneCensus
{
	private const int MaximumGroupsToLog = 24;
	private const int MaximumAwakeBodiesToLog = 96;

	internal static void Log()
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			Rigidbody[] rigidbodies = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None);
			Collider[] colliders = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
			Joint[] joints = UnityEngine.Object.FindObjectsByType<Joint>(FindObjectsInactive.Include, FindObjectsSortMode.None);

			int bodyTotal = 0;
			int bodyActive = 0;
			int bodyDynamic = 0;
			int bodyAwake = 0;
			int bodySleeping = 0;
			int bodyKinematic = 0;
			int bodyCollisionDisabled = 0;
			Dictionary<string, int> bodyLayers = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, int> bodyOwners = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, int> bodyCollisionModes = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, int> awakeBodyLayers = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, int> awakeBodyOwners = new Dictionary<string, int>(StringComparer.Ordinal);
			List<Rigidbody> awakeBodies = new List<Rigidbody>();

			foreach (Rigidbody rigidbody in rigidbodies)
			{
				if (rigidbody == null || !IsInDefaultPhysicsScene(rigidbody.gameObject))
				{
					continue;
				}

				bodyTotal++;
				if (rigidbody.gameObject.activeInHierarchy)
				{
					bodyActive++;
				}
				if (rigidbody.isKinematic)
				{
					bodyKinematic++;
				}
				else
				{
					bodyDynamic++;
					if (rigidbody.IsSleeping())
					{
						bodySleeping++;
					}
					else
					{
						bodyAwake++;
						if (rigidbody.gameObject.activeInHierarchy)
						{
							awakeBodies.Add(rigidbody);
							Increment(awakeBodyLayers, GetLayerName(rigidbody.gameObject.layer));
							Increment(awakeBodyOwners, GetPrimaryOwnerType(rigidbody));
						}
					}
				}
				if (!rigidbody.detectCollisions)
				{
					bodyCollisionDisabled++;
				}

				Increment(bodyLayers, GetLayerName(rigidbody.gameObject.layer));
				Increment(bodyOwners, GetPrimaryOwnerType(rigidbody));
				Increment(bodyCollisionModes, rigidbody.collisionDetectionMode.ToString());
			}

			int colliderTotal = 0;
			int colliderActive = 0;
			int colliderTriggers = 0;
			int colliderStatic = 0;
			int colliderDynamic = 0;
			int colliderKinematic = 0;
			int meshConvex = 0;
			int meshNonConvex = 0;
			Dictionary<string, int> colliderLayers = new Dictionary<string, int>(StringComparer.Ordinal);
			Dictionary<string, int> colliderTypes = new Dictionary<string, int>(StringComparer.Ordinal);

			foreach (Collider collider in colliders)
			{
				if (collider == null || !IsInDefaultPhysicsScene(collider.gameObject))
				{
					continue;
				}

				colliderTotal++;
				if (collider.enabled && collider.gameObject.activeInHierarchy)
				{
					colliderActive++;
				}
				if (collider.isTrigger)
				{
					colliderTriggers++;
				}

				Rigidbody attached = collider.attachedRigidbody;
				if (attached == null)
				{
					colliderStatic++;
				}
				else if (attached.isKinematic)
				{
					colliderKinematic++;
				}
				else
				{
					colliderDynamic++;
				}

				if (collider is MeshCollider meshCollider)
				{
					if (meshCollider.convex)
					{
						meshConvex++;
					}
					else
					{
						meshNonConvex++;
					}
				}

				Increment(colliderLayers, GetLayerName(collider.gameObject.layer));
				Increment(colliderTypes, collider.GetType().Name);
			}

			int jointTotal = joints.Count(joint => joint != null && IsInDefaultPhysicsScene(joint.gameObject));
			int jointActive = joints.Count(joint => joint != null && joint.gameObject.activeInHierarchy && IsInDefaultPhysicsScene(joint.gameObject));
			int jointConnected = joints.Count(joint => joint != null && joint.connectedBody != null && IsInDefaultPhysicsScene(joint.gameObject));

			stopwatch.Stop();
			RupturePlugin.Log.LogInfo($"RPT default physics scene census: elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}, loadedScenes={SceneManager.loadedSceneCount}, " +
				$"rigidbodies={bodyTotal}, active={bodyActive}, dynamic={bodyDynamic}, awake={bodyAwake}, sleeping={bodySleeping}, " +
				$"kinematic={bodyKinematic}, collisionsDisabled={bodyCollisionDisabled}, " +
				$"colliders={colliderTotal}, colliderActive={colliderActive}, static={colliderStatic}, dynamicAttached={colliderDynamic}, " +
				$"kinematicAttached={colliderKinematic}, triggers={colliderTriggers}, meshConvex={meshConvex}, meshNonConvex={meshNonConvex}, " +
				$"joints={jointTotal}, jointActive={jointActive}, jointConnected={jointConnected}.");

			LogGroups("rigidbody layers", bodyLayers);
			LogGroups("rigidbody owners", bodyOwners);
			LogGroups("rigidbody collision modes", bodyCollisionModes);
			LogGroups("awake dynamic rigidbody layers", awakeBodyLayers);
			LogGroups("awake dynamic rigidbody owners", awakeBodyOwners);
			LogGroups("collider layers", colliderLayers);
			LogGroups("collider types", colliderTypes);
			LogAwakeBodies(awakeBodies);
		}
		catch (Exception exception)
		{
			stopwatch.Stop();
			RupturePlugin.Log.LogWarning($"RPT default physics scene census failed after {stopwatch.Elapsed.TotalMilliseconds:F2} ms: {exception}");
		}
	}

	internal static string GetHierarchyPath(Transform transform)
	{
		if (transform == null)
		{
			return "(null)";
		}

		List<string> names = new List<string>(8);
		Transform current = transform;
		while (current != null && names.Count < 8)
		{
			names.Add(current.name);
			current = current.parent;
		}
		if (current != null)
		{
			names.Add("...");
		}
		names.Reverse();
		return string.Join("/", names);
	}

	internal static string GetOwnerTypes(Component component)
	{
		if (component == null)
		{
			return "(none)";
		}

		HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
		Transform current = component.transform;
		for (int depth = 0; current != null && depth < 6; depth++, current = current.parent)
		{
			foreach (MonoBehaviour behaviour in current.GetComponents<MonoBehaviour>())
			{
				if (behaviour != null)
				{
					names.Add(behaviour.GetType().FullName ?? behaviour.GetType().Name);
				}
			}
		}
		return names.Count == 0 ? "(none)" : string.Join("|", names.OrderBy(name => name, StringComparer.Ordinal));
	}

	private static string GetPrimaryOwnerType(Component component)
	{
		Transform current = component.transform;
		for (int depth = 0; current != null && depth < 8; depth++, current = current.parent)
		{
			foreach (MonoBehaviour behaviour in current.GetComponents<MonoBehaviour>())
			{
				if (behaviour == null)
				{
					continue;
				}

				Type type = behaviour.GetType();
				if (type == typeof(RigidbodySpawner) || type == typeof(CharacterJointSpawner))
				{
					continue;
				}
				return type.FullName ?? type.Name;
			}
		}
		return "(no MonoBehaviour owner)";
	}

	private static bool IsInDefaultPhysicsScene(GameObject gameObject)
	{
		if (gameObject == null)
		{
			return false;
		}
		Scene scene = gameObject.scene;
		return scene.IsValid() && scene.isLoaded && scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene);
	}

	private static string GetLayerName(int layer)
	{
		string name = LayerMask.LayerToName(layer);
		return string.IsNullOrEmpty(name) ? layer.ToString() : name;
	}

	private static void Increment(Dictionary<string, int> groups, string key)
	{
		groups.TryGetValue(key ?? "(null)", out int count);
		groups[key ?? "(null)"] = count + 1;
	}

	private static void LogGroups(string label, Dictionary<string, int> groups)
	{
		string values = string.Join(", ", groups
			.OrderByDescending(pair => pair.Value)
			.ThenBy(pair => pair.Key, StringComparer.Ordinal)
			.Take(MaximumGroupsToLog)
			.Select(pair => $"{pair.Key}={pair.Value}"));
		RupturePlugin.Log.LogInfo($"RPT physics census {label}: {values}.");
	}

	private static void LogAwakeBodies(List<Rigidbody> rigidbodies)
	{
		List<Collider> colliderScratch = new List<Collider>(32);
		int index = 0;
		foreach (Rigidbody rigidbody in rigidbodies
			.Where(rigidbody => rigidbody != null)
			.OrderBy(rigidbody => GetHierarchyPath(rigidbody.transform), StringComparer.Ordinal)
			.Take(MaximumAwakeBodiesToLog))
		{
			colliderScratch.Clear();
			rigidbody.GetComponentsInChildren(includeInactive: true, result: colliderScratch);
			int enabledColliders = colliderScratch.Count(collider =>
				collider != null &&
				collider.attachedRigidbody == rigidbody &&
				collider.enabled &&
				collider.gameObject.activeInHierarchy);

			RupturePlugin.Log.LogInfo($"RPT awake dynamic body [{index++}]: id={rigidbody.GetInstanceID()}, name={rigidbody.name}, " +
				$"path={GetHierarchyPath(rigidbody.transform)}, owner={GetPrimaryOwnerType(rigidbody)}, " +
				$"layer={GetLayerName(rigidbody.gameObject.layer)}, mass={rigidbody.mass:F3}, " +
				$"speed={rigidbody.velocity.magnitude:F3}, angularSpeed={rigidbody.angularVelocity.magnitude:F3}, " +
				$"detectCollisions={rigidbody.detectCollisions}, colliders={enabledColliders}, " +
				$"collisionMode={rigidbody.collisionDetectionMode}, interpolation={rigidbody.interpolation}, constraints={rigidbody.constraints}.");
		}

		if (rigidbodies.Count > MaximumAwakeBodiesToLog)
		{
			RupturePlugin.Log.LogInfo($"RPT awake dynamic body list truncated: logged={MaximumAwakeBodiesToLog}, total={rigidbodies.Count}.");
		}
	}
}
