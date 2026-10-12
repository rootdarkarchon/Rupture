using System;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Rupture.Ragdolls.Classes;

/// <summary>
/// Reasserts only the already-computed visible corpse pose after Unity has
/// completed late animation/graphics jobs. Shadow physics remains exclusively
/// in RupturePlugin.LateUpdate.
/// </summary>
internal static class ShadowVisiblePosePlayerLoop
{
	private sealed class VisiblePosePass
	{
	}

	internal static bool Installed { get; private set; }
	internal static bool HasExecuted { get; private set; }

	internal static void EnsureInstalled()
	{
		PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
		if (ContainsPass(loop))
		{
			Installed = true;
			return;
		}
		Install();
	}

	internal static bool Install()
	{
		Remove();
		PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
		if (!InsertAfter(ref loop, typeof(PostLateUpdate.EndGraphicsJobsAfterScriptLateUpdate)))
		{
			RupturePlugin.Log.LogWarning("Unity's EndGraphicsJobsAfterScriptLateUpdate player-loop stage is unavailable. " +
				"RPT will retain the ordinary LateUpdate visible-pose fallback.");
			return false;
		}

		PlayerLoop.SetPlayerLoop(loop);
		Installed = true;
		HasExecuted = false;
		RupturePlugin.Log.LogInfo("RPT visible shadow pose pass installed after EndGraphicsJobsAfterScriptLateUpdate; physics simulation remains in LateUpdate.");
		return true;
	}

	internal static void Remove()
	{
		PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
		if (RemovePass(ref loop))
		{
			PlayerLoop.SetPlayerLoop(loop);
		}
		Installed = false;
		HasExecuted = false;
	}

	private static void Run()
	{
		if (!HasExecuted)
		{
			HasExecuted = true;
			RupturePlugin.Log.LogInfo("RPT post-animation visible shadow pose pass executed; remote Fika corpse transforms will be reasserted without another physics step.");
		}
		if (RagdollCoordinator.IsRemoteClient)
		{
			using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.PostAnimationVisibleShell))
				ShadowRagdollWorld.LateUpdateVisibleShell();
		}
	}

	private static bool ContainsPass(PlayerLoopSystem system)
	{
		PlayerLoopSystem[] children = system.subSystemList;
		if (children == null)
		{
			return false;
		}
		for (int index = 0; index < children.Length; index++)
		{
			if (children[index].type == typeof(VisiblePosePass) || ContainsPass(children[index]))
			{
				return true;
			}
		}
		return false;
	}

	private static bool InsertAfter(ref PlayerLoopSystem system, Type targetType)
	{
		PlayerLoopSystem[] children = system.subSystemList;
		if (children == null)
		{
			return false;
		}

		for (int index = 0; index < children.Length; index++)
		{
			if (children[index].type == targetType)
			{
				PlayerLoopSystem[] expanded = new PlayerLoopSystem[children.Length + 1];
				Array.Copy(children, 0, expanded, 0, index + 1);
				expanded[index + 1] = new PlayerLoopSystem
				{
					type = typeof(VisiblePosePass),
					updateDelegate = Run
				};
				Array.Copy(children, index + 1, expanded, index + 2, children.Length - index - 1);
				system.subSystemList = expanded;
				return true;
			}

			PlayerLoopSystem child = children[index];
			if (InsertAfter(ref child, targetType))
			{
				children[index] = child;
				system.subSystemList = children;
				return true;
			}
		}
		return false;
	}

	private static bool RemovePass(ref PlayerLoopSystem system)
	{
		PlayerLoopSystem[] children = system.subSystemList;
		if (children == null)
		{
			return false;
		}

		bool removed = false;
		for (int index = children.Length - 1; index >= 0; index--)
		{
			if (children[index].type == typeof(VisiblePosePass))
			{
				PlayerLoopSystem[] reduced = new PlayerLoopSystem[children.Length - 1];
				Array.Copy(children, 0, reduced, 0, index);
				Array.Copy(children, index + 1, reduced, index, children.Length - index - 1);
				children = reduced;
				removed = true;
				continue;
			}

			PlayerLoopSystem child = children[index];
			if (RemovePass(ref child))
			{
				children[index] = child;
				removed = true;
			}
		}

		if (removed)
		{
			system.subSystemList = children;
		}
		return removed;
	}
}
