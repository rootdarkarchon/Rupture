using System;
using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;
using Rupture.Integration;

namespace Rupture.Ragdolls.Classes;

// CorpseHandoffV1 lease truth. A live lease (Queued, Held or Adopting) is the single answer to IsHandoffHeld:
// the held corpse's core rig is treated as absent by every pose, wake, settle and impulse path, while its
// independent weapon and detached effects keep simulating. Terminal records only answer Adopt/Renew until retired.
internal static partial class ShadowRagdollWorld
{
	private enum HandoffStage { Queued, Held, Adopting, Closed, Failed, Cancelled }
	private enum HandoffRigFate { Keep, Destroy, ReturnToNative }

	private sealed class HandoffLease
	{
		internal ulong Id;
		internal Corpse Corpse;
		internal EFT.Interactive.CorpseRagdoll Ragdoll;
		internal uint DeathSequence;
		internal HandoffStage Stage = HandoffStage.Queued;
		internal HandoffLeaseClock Clock;
		internal ShadowRig Rig;
		internal int RequestedFrame;
		internal bool WasPreparing;
		internal bool AdoptRequested;
		internal bool ReleaseNotified;
		internal int StripFrame = -1;
		internal int StripAttempts;
		internal HashSet<Transform> SeveredBones;
		internal bool NativesTouched; // the consumer's native bodies were stripped; they are no longer the consumer's to keep
		internal float RetireAt;
	}

	private const int MaximumHandoffStripAttempts = 5;
	private static readonly Dictionary<ulong, HandoffLease> HandoffLeases = new Dictionary<ulong, HandoffLease>();
	private static readonly Dictionary<Corpse, HandoffLease> HandoffByCorpse = new Dictionary<Corpse, HandoffLease>();
	private static readonly HashSet<Corpse> HandoffNativeCorpses = new HashSet<Corpse>();
	private static readonly List<HandoffLease> HandoffSnapshot = new List<HandoffLease>();
	private static readonly List<Joint> HandoffJointScratch = new List<Joint>();
	private static ulong _nextHandoffLease; // deliberately not reset between raids

	// True from an accepted Release until adoption completes, fails or the token is invalidated.
	internal static bool IsHandoffHeld(Corpse corpse) =>
		HandoffByCorpse.Count != 0 && !ReferenceEquals(corpse, null) && HandoffByCorpse.ContainsKey(corpse);

	private static bool IsHeldCoreBody(ShadowBody body) =>
		body?.Rig != null && !ReferenceEquals(body, body.Rig.WeaponBody) && IsHandoffHeld(body.Rig.Corpse);

	private static bool IsHandoffTerminal(HandoffStage stage) => stage >= HandoffStage.Closed;

	internal static HandoffResult InspectHandoff(Corpse corpse, out CorpseHandoffInfo info)
	{
		info = null;
		if (corpse == null || corpse.Ragdoll == null || !ReferenceEquals(corpse.Ragdoll._owner, corpse)) return HandoffResult.InvalidArgument;
		bool coordinatorOwned = RagdollCoordinator.TryGetDragIdentity(corpse, out uint sequence);
		bool hasRig = RigsByCorpse.TryGetValue(corpse, out ShadowRig rig);
		bool pending = HasPendingDragRig(corpse.Ragdoll);
		HandoffByCorpse.TryGetValue(corpse, out HandoffLease lease);
		info = new CorpseHandoffInfo
		{
			State = HandoffState.Unmanaged, ProfileId = corpse.PlayerProfileID, DeathSequence = sequence,
			OwnershipEpoch = RagdollCoordinator.GetOwnershipEpoch(corpse), SeveredRoots = Array.Empty<Transform>()
		};
		if (lease != null && lease.Stage != HandoffStage.Queued)
		{
			info.State = HandoffState.Released;
			FillHandoffInfo(info, corpse, rig, quiesced: true);
			return HandoffResult.Ok;
		}
		if (!hasRig && !pending && (HandoffNativeCorpses.Contains(corpse) || (!coordinatorOwned && lease == null)))
			return HandoffResult.NotManaged;
		if (!Enabled || !_dragSimulationActive || RagdollCoordinator.FreezeCorpseRagdollsForWoundDebug ||
			_externalRuntime == null || !string.IsNullOrEmpty(_externalRuntime.Failure) ||
			!coordinatorOwned || (sequence == 0 && (MultiplayerRuntime.Current.HasServer || RagdollCoordinator.IsRemoteClient)) ||
			(hasRig && rig.ControlDeathSequence != sequence) || (!hasRig && !pending))
		{
			info.State = HandoffState.Unavailable;
			return HandoffResult.Unavailable;
		}
		info.State = hasRig && _externalRuntime.IsRaidReady ? HandoffState.Managed : HandoffState.Preparing;
		FillHandoffInfo(info, corpse, rig, quiesced: false);
		return HandoffResult.Ok;
	}

	private static void FillHandoffInfo(CorpseHandoffInfo info, Corpse corpse, ShadowRig rig, bool quiesced)
	{
		if (rig == null) return;
		info.SeveredRoots = CopySeveredRoots(rig);
		info.WeaponIndependent = rig.WeaponBody?.Proxy != null && !rig.WeaponReleased;
		info.Simulating = !quiesced && (RagdollCoordinator.IsRemoteClient ? RagdollCoordinator.IsClientMotionLive(corpse) : rig.Active);
	}

	private static Transform[] CopySeveredRoots(ShadowRig rig)
	{
		if (rig.PermanentlyDisabledBodies.Count == 0) return Array.Empty<Transform>();
		List<Transform> roots = new List<Transform>(rig.PermanentlyDisabledBodies.Count);
		foreach (ShadowBody candidate in rig.PermanentlyDisabledBodies)
		{
			Transform bone = candidate?.NativeTransform;
			if (bone == null || ReferenceEquals(candidate, rig.WeaponBody)) continue;
			bool topMost = true;
			foreach (ShadowBody other in rig.PermanentlyDisabledBodies)
			{
				Transform otherBone = other?.NativeTransform;
				if (otherBone == null || ReferenceEquals(other, candidate) || bone == otherBone || !bone.IsChildOf(otherBone)) continue;
				topMost = false;
				break;
			}
			if (topMost) roots.Add(bone);
		}
		return roots.ToArray();
	}

	internal static HandoffResult ReleaseHandoff(Corpse corpse, HandoffRelease request, out ulong token)
	{
		token = 0;
		if (request == null || !CorpseHandoffPolicy.IsValidLeaseTimeout(request.LeaseTimeout)) return HandoffResult.InvalidArgument;
		HandoffResult result = InspectHandoff(corpse, out CorpseHandoffInfo info);
		if (result == HandoffResult.InvalidArgument) return result;
		// Busy means another owner; a caller holding the token must reuse it instead of releasing again.
		if (HandoffByCorpse.ContainsKey(corpse) || DragByCorpse.ContainsKey(corpse)) return HandoffResult.Busy;
		if (result != HandoffResult.Ok) return result;
		if (_nextHandoffLease == ulong.MaxValue) return HandoffResult.Failed;
		HandoffLease created = new HandoffLease
		{
			Id = ++_nextHandoffLease, Corpse = corpse, Ragdoll = corpse.Ragdoll, DeathSequence = info.DeathSequence,
			Clock = new HandoffLeaseClock(Time.realtimeSinceStartup, request.LeaseTimeout),
			RequestedFrame = Time.frameCount, WasPreparing = info.State == HandoffState.Preparing
		};
		HandoffLeases.Add(created.Id, created);
		HandoffByCorpse.Add(corpse, created);
		token = created.Id;
		return HandoffResult.Pending;
	}

	internal static HandoffResult RenewHandoff(ulong token)
	{
		if (!TryGetHandoff(token, out HandoffLease lease)) return HandoffResult.InvalidToken;
		switch (lease.Stage)
		{
		case HandoffStage.Queued:
			if (lease.AdoptRequested) return HandoffResult.Stale;
			lease.Clock.Renew(Time.realtimeSinceStartup);
			return HandoffResult.Ok;
		case HandoffStage.Held:
			lease.Clock.Renew(Time.realtimeSinceStartup);
			return HandoffResult.Ok;
		case HandoffStage.Failed:
			return HandoffResult.Failed;
		default:
			return HandoffResult.Stale;
		}
	}

	internal static HandoffResult AdoptHandoff(ulong token)
	{
		if (!TryGetHandoff(token, out HandoffLease lease)) return HandoffResult.InvalidToken;
		switch (lease.Stage)
		{
		case HandoffStage.Queued:
			// Nothing has been taken yet; the boundary closes the token without quiescing the rig.
			lease.AdoptRequested = true;
			return HandoffResult.Pending;
		case HandoffStage.Held:
			lease.Stage = HandoffStage.Adopting;
			return HandoffResult.Pending;
		case HandoffStage.Adopting:
			return HandoffResult.Pending;
		case HandoffStage.Closed:
			return HandoffResult.Ok;
		case HandoffStage.Failed:
			return HandoffResult.Failed;
		default:
			return HandoffResult.Stale;
		}
	}

	private static bool TryGetHandoff(ulong token, out HandoffLease lease)
	{
		if (!HandoffLeases.TryGetValue(token, out lease)) return false;
		if (!IsHandoffTerminal(lease.Stage) || Time.realtimeSinceStartup < lease.RetireAt) return true;
		HandoffLeases.Remove(token);
		lease = null;
		return false;
	}

	// Runs at the same safe simulation boundary as ProcessDragBoundary: the preceding external step is published
	// and the next is not submitted, so no caller can rewrite actors mid-simulation.
	private static void ProcessHandoffBoundary()
	{
		if (HandoffLeases.Count == 0) return;
		HandoffSnapshot.Clear();
		HandoffSnapshot.AddRange(HandoffLeases.Values);
		float now = Time.realtimeSinceStartup;
		int frame = Time.frameCount;
		foreach (HandoffLease lease in HandoffSnapshot)
		{
			if (IsHandoffTerminal(lease.Stage))
			{
				if (now >= lease.RetireAt) HandoffLeases.Remove(lease.Id);
				continue;
			}
			try { StepHandoff(lease, now, frame); }
			catch (Exception exception)
			{
				RupturePlugin.Log.LogWarning("Corpse handoff failed: " + exception.Message);
				// Held and Adopting leases own the independent weapon and detached effects: restore them to native before the rig goes.
				FinishHandoff(lease, HandoffStage.Failed, lease.Stage == HandoffStage.Queued ? HandoffRigFate.Keep : HandoffRigFate.ReturnToNative,
					"corpse handoff step failed: " + exception.Message);
			}
		}
		HandoffSnapshot.Clear();
	}

	// The graphical path steps every frame; headless steps at most every 1/30 s. A queued release is bounded in
	// rendered frames, so headless services it between steps once the in-flight step is published.
	private static void ServiceQueuedHandoffs()
	{
		if (HandoffByCorpse.Count == 0 || !UsingExternalBackend) return;
		bool queued = false;
		foreach (HandoffLease lease in HandoffByCorpse.Values)
		{
			if (lease.Stage != HandoffStage.Queued) continue;
			queued = true;
			break;
		}
		if (!queued) return;
		if (_externalStepSubmitted && !CompleteExternalStep()) return;
		ProcessHandoffBoundary();
	}

	private static void StepHandoff(HandoffLease lease, float now, int frame)
	{
		if (HandoffInvalid(lease, out bool backendFailure))
		{
			bool queued = lease.Stage == HandoffStage.Queued;
			FinishHandoff(lease, queued && backendFailure ? HandoffStage.Failed : HandoffStage.Cancelled,
				queued ? HandoffRigFate.Keep : HandoffRigFate.Destroy);
			return;
		}
		if (lease.Stage == HandoffStage.Queued)
		{
			StepQueuedHandoff(lease, now, frame);
			return;
		}
		if (lease.Stage == HandoffStage.Held && lease.Clock.ObserveExpired(now, frame)) lease.Stage = HandoffStage.Adopting;
		if (lease.Stage == HandoffStage.Adopting) StepAdoptingHandoff(lease, frame);
	}

	private static bool HandoffInvalid(HandoffLease lease, out bool backendFailure)
	{
		backendFailure = !UsingExternalBackend || !string.IsNullOrEmpty(_externalRuntime.Failure);
		return backendFailure || lease.Corpse == null || !ReferenceEquals(lease.Corpse.Ragdoll, lease.Ragdoll) ||
			!RagdollCoordinator.TryGetDragIdentity(lease.Corpse, out uint sequence) || sequence != lease.DeathSequence ||
			!Enabled || RagdollCoordinator.FreezeCorpseRagdollsForWoundDebug;
	}

	private static void StepQueuedHandoff(HandoffLease lease, float now, int frame)
	{
		bool hasRig = RigsByCorpse.TryGetValue(lease.Corpse, out ShadowRig rig) && ReferenceEquals(rig.NativeRagdoll, lease.Ragdoll);
		if (!hasRig)
		{
			if (HasPendingDragRig(lease.Ragdoll)) return;
			// Capture finished without a rig: the corpse stays native and the consumer may use its own route.
			if (lease.WasPreparing) HandoffNativeCorpses.Add(lease.Corpse);
			FinishHandoff(lease, HandoffStage.Failed, HandoffRigFate.Keep);
			return;
		}
		if (lease.AdoptRequested)
		{
			FinishHandoff(lease, HandoffStage.Closed, HandoffRigFate.Keep);
			return;
		}
		if (!lease.WasPreparing && frame - lease.RequestedFrame > CorpseHandoffPolicy.ReleaseBoundaryFrames)
		{
			FinishHandoff(lease, HandoffStage.Failed, HandoffRigFate.Keep);
			return;
		}
		try { QuiesceRigForHandoff(rig); }
		catch (Exception exception)
		{
			RupturePlugin.Log.LogWarning("Corpse handoff release failed: " + exception.Message);
			// Release the lease first so the abort sees an ordinary corpse; Rupture's backend-failure fallback owns it now.
			FinishHandoff(lease, HandoffStage.Failed, HandoffRigFate.Keep);
			AbortToNative(rig, "corpse handoff release failed: " + exception.Message);
			return;
		}
		lease.Rig = rig;
		lease.Stage = HandoffStage.Held;
		lease.Clock.Renew(now);
		lease.ReleaseNotified = true;
		RagdollCoordinator.OnHandoffReleased(lease.Corpse);
	}

	// Leaves the core rig as a kinematic, collision-free set of proxies that nothing presents, wakes or settles.
	// Native bodies stay kinematic and unsupported: no NativeWasKinematic restore and no SupportRigidbody.
	private static void QuiesceRigForHandoff(ShadowRig rig)
	{
		PresentRigPose(rig);
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body?.Proxy == null || ReferenceEquals(body, rig.WeaponBody)) continue;
			body.Proxy.velocity = Vector3.zero;
			body.Proxy.angularVelocity = Vector3.zero;
			body.Proxy.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.Proxy.isKinematic = true;
			if (body.Proxy is ExternalShadowBodyProxy external) external.ExternalBody.CollisionEnabled = false;
			body.NativeHandedOff = false;
		}
		LethalImpulseExperiment.End(rig.NativeRagdoll, "handoff");
		if (CountDynamicDetachedBodies(rig) == 0 && !HasDynamicIndependentWeapon(rig)) ReleaseEnvironment(rig);
		// The settled shape keeps the independent weapon and detached effects on their existing settled-rig paths.
		rig.Active = false;
		rig.Dormant = false;
		rig.Settled = true;
		rig.SleepingSteps = 0;
	}

	private static void StepAdoptingHandoff(HandoffLease lease, int frame)
	{
		ShadowRig rig = lease.Rig;
		if (rig == null || rig.Root == null || !RigsByCorpse.TryGetValue(lease.Corpse, out ShadowRig current) || !ReferenceEquals(current, rig))
		{
			FinishHandoff(lease, HandoffStage.Cancelled, HandoffRigFate.Keep);
			return;
		}
		// Unity completes Destroy at the end of the frame; rebuilding earlier would find the old components still present.
		if (lease.StripFrame >= 0 && frame <= lease.StripFrame) return;
		EFT.Interactive.CorpseRagdoll ragdoll = lease.Ragdoll;
		if (lease.SeveredBones == null) lease.SeveredBones = CaptureSeveredBones(rig);
		// Cheap preconditions run before anything is stripped so a refusal leaves the consumer's bodies as they are.
		if (!CheckHandoffRebuildPreconditions(rig, ragdoll, out string precondition))
		{
			FailHandoffAdoption(lease, rig, precondition);
			return;
		}
		if (HasNativeLeftovers(ragdoll))
		{
			if (++lease.StripAttempts > MaximumHandoffStripAttempts)
			{
				FailHandoffAdoption(lease, rig, "the handoff consumer's rigidbodies or joints were not removed");
				return;
			}
			lease.NativesTouched = true;
			StripNativeRagdoll(ragdoll);
			lease.StripFrame = frame;
			return;
		}
		string failure;
		bool rebuilt;
		// A limb severed while the strip waited is not in the earlier capture; the rebuild must not rejoin it.
		AddSeveredBones(rig, lease.SeveredBones);
		lease.NativesTouched = true;
		try { rebuilt = TryRebuildForHandoff(rig, ragdoll, lease.SeveredBones, out failure); }
		catch (Exception exception)
		{
			rebuilt = false;
			failure = exception.GetType().Name + ": " + exception.Message;
		}
		if (!rebuilt)
		{
			FailHandoffAdoption(lease, rig, failure);
			return;
		}
		FinishHandoff(lease, HandoffStage.Closed, HandoffRigFate.Keep);
	}

	// A rebuild failure is a valid end state, not the abort path: no failure counter, no raid-level fallback.
	private static void FailHandoffAdoption(HandoffLease lease, ShadowRig rig, string failure)
	{
		RupturePlugin.Log.LogInfo($"Corpse handoff adoption could not rebuild shadow physics for {GetCorpseName(lease.Ragdoll)}; " +
			$"the corpse stays on native EFT physics. {failure}");
		HandoffNativeCorpses.Add(lease.Corpse);
		FinishHandoff(lease, HandoffStage.Failed, HandoffRigFate.ReturnToNative, failure);
	}

	private static HashSet<Transform> CaptureSeveredBones(ShadowRig rig)
	{
		HashSet<Transform> severed = new HashSet<Transform>();
		AddSeveredBones(rig, severed);
		return severed;
	}

	private static void AddSeveredBones(ShadowRig rig, HashSet<Transform> severed)
	{
		foreach (ShadowBody body in rig.PermanentlyDisabledBodies)
		{
			if (body?.NativeTransform != null && !ReferenceEquals(body, rig.WeaponBody)) severed.Add(body.NativeTransform);
		}
	}

	private static bool CheckHandoffRebuildPreconditions(ShadowRig rig, EFT.Interactive.CorpseRagdoll ragdoll, out string failure)
	{
		failure = null;
		if (!Enabled)
		{
			failure = "the feature is disabled";
			return false;
		}
		if (!PhysicsExtensions.UpdateController.Enabled)
		{
			failure = "EFT supported-rigidbody simulation is disabled";
			return false;
		}
		if (ragdoll._rigidbodySpawners == null || ragdoll._rigidbodySpawners.Length != rig.CoreBodyCount)
		{
			failure = "the native rigidbody spawners no longer match the retained rig";
			return false;
		}
		return EnsureScene(out failure);
	}

	private static bool HasNativeLeftovers(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll._rigidbodySpawners != null)
		{
			foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
			{
				if (spawner != null && (spawner.Rigidbody != null || spawner.GetComponent<Rigidbody>() != null || spawner.GetComponent<Joint>() != null))
					return true;
			}
		}
		if (ragdoll._jointSpawners != null)
		{
			foreach (CharacterJointSpawner spawner in ragdoll._jointSpawners)
			{
				if (spawner != null && (spawner.GetComponent<Rigidbody>() != null || spawner.GetComponent<Joint>() != null))
					return true;
			}
		}
		return false;
	}

	// The consumer is not trusted to leave a clean ragdoll: remove everything on the spawner bones, joints first.
	private static void StripNativeRagdoll(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll._jointSpawners != null)
		{
			foreach (CharacterJointSpawner spawner in ragdoll._jointSpawners)
			{
				if (spawner == null) continue;
				spawner.Remove();
				DestroyBoneComponents(spawner.gameObject);
			}
		}
		if (ragdoll._rigidbodySpawners == null) return;
		foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
		{
			if (spawner == null) continue;
			DestroyBoneComponents(spawner.gameObject);
			spawner.Remove();
			DestroyObject(spawner.GetComponent<Rigidbody>());
		}
	}

	private static void DestroyBoneComponents(GameObject bone)
	{
		bone.GetComponents(HandoffJointScratch);
		foreach (Joint joint in HandoffJointScratch) DestroyObject(joint);
		HandoffJointScratch.Clear();
		Rigidbody body = bone.GetComponent<Rigidbody>();
		if (body != null) DestroyObject(body);
	}

	// TryBuild variant for adoption: the native ragdoll is recreated from its spawners at the current bones, the core
	// rig is rebuilt from them and ends settled with the native query shell. It never reads _isPhysicsDone, applies no
	// lethal impulse, inherits no movement and adds no angular-resistance drives. The independent weapon, detached
	// effects, group and registrations survive.
	private static bool TryRebuildForHandoff(ShadowRig rig, EFT.Interactive.CorpseRagdoll ragdoll, HashSet<Transform> severed, out string failure)
	{
		failure = null;
		ReleaseInvalidWeapon(rig);
		CreateNativeRagdollBodies(ragdoll, severed);
		foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
		{
			if (spawner?.Rigidbody != null) continue;
			failure = "a native ragdoll body could not be recreated";
			ReturnNativeRagdollToKinematic(ragdoll);
			return false;
		}

		DestroyHandoffCore(rig);
		rig.RebuildSeveredBones = severed;
		try
		{
			Transform skeletonRoot = ragdoll._playerBody?.SkeletonRootJoint?.transform;
			foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
			{
				if (!TryAddCoreBody(rig, spawner, spawner.Rigidbody, skeletonRoot, out failure)) return false;
			}
			if (!TryCloneJoints(rig, out failure)) return false;
			rig.Bodies.Sort((left, right) => GetHierarchyDepth(left.Native?.transform).CompareTo(GetHierarchyDepth(right.Native?.transform)));
			ApplyNativeIgnorePairs(rig);
			foreach (ShadowBody parked in rig.PermanentlyDisabledBodies) ParkSeveredBody(rig, parked);
		}
		finally { rig.RebuildSeveredBones = null; }

		bool collisionEnabled = rig.Root.activeSelf;
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body?.Proxy == null || ReferenceEquals(body, rig.WeaponBody) || rig.PermanentlyDisabledBodies.Contains(body)) continue;
			body.Proxy.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.Proxy.isKinematic = true;
			if (body.Proxy is ExternalShadowBodyProxy external) external.ExternalBody.CollisionEnabled = collisionEnabled;
		}
		// Nothing below can fail: the native bodies become the kinematic, unsupported query shell exactly as after TryBuild.
		foreach (ShadowBody body in rig.Bodies)
		{
			if (ReferenceEquals(body, rig.WeaponBody)) continue;
			BodiesByNative[body.Native] = body;
			body.NativeHandedOff = true;
			for (int supportIndex = 0; supportIndex < body.NativeSupportCount; supportIndex++)
				PhysicsExtensions.UpdateController.UnsupportRigidbody(body.Native);
			body.Native.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.Native.isKinematic = true;
		}
		rig.Active = false;
		rig.Dormant = false;
		rig.Settled = true;
		rig.IgnoreNativeStopped = true;
		rig.SleepingSteps = 0;
		return true;
	}

	// Mirrors CorpseRagdoll.Start minus velocity, the sleep coroutine and armor-plate toggling. Joints are never
	// created on a severed bone; EFT's own order (joints first, then bodies) is kept.
	private static void CreateNativeRagdollBodies(EFT.Interactive.CorpseRagdoll ragdoll, HashSet<Transform> severed)
	{
		foreach (CharacterJointSpawner jointSpawner in ragdoll._jointSpawners ?? Array.Empty<CharacterJointSpawner>())
		{
			if (jointSpawner == null || severed.Contains(jointSpawner.transform)) continue;
			Joint joint = jointSpawner.Create();
			joint.enablePreprocessing = false;
			if (joint is CharacterJoint characterJoint) characterJoint.enableProjection = true;
			Rigidbody own = joint.GetComponent<Rigidbody>();
			if (joint.connectedBody != null && own != null) joint.massScale = joint.connectedBody.mass / own.mass;
			joint.connectedMassScale = 1f;
		}
		foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
		{
			if (spawner == null) continue;
			Rigidbody body = spawner.Create();
			body.isKinematic = false;
			body.maxDepenetrationVelocity = ragdoll._maxDepenetrationVelocity;
			body.collisionDetectionMode = ragdoll._collisionDetectionMode;
			PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f);
		}
	}

	private static void ReturnNativeRagdollToKinematic(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll?._rigidbodySpawners == null) return;
		foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
		{
			Rigidbody body = spawner != null ? spawner.Rigidbody : null;
			if (body == null || body.isKinematic) continue;
			PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
			body.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.isKinematic = true;
		}
	}

	// Drops the released core and parked proxies and their joints; the weapon body stays Rupture-owned.
	private static void DestroyHandoffCore(ShadowRig rig)
	{
		foreach (ExternalJointBinding binding in rig.ExternalJoints) _externalRuntime?.DestroyJoint(binding?.Joint);
		rig.ExternalJoints.Clear();
		for (int index = rig.Bodies.Count - 1; index >= 0; index--)
		{
			ShadowBody body = rig.Bodies[index];
			if (ReferenceEquals(body, rig.WeaponBody)) continue;
			if (body?.Proxy is ExternalShadowBodyProxy external) _externalRuntime?.DestroyBody(external.ExternalBody);
			if (!ReferenceEquals(body?.Native, null))
			{
				rig.BodiesByNative.Remove(body.Native);
				if (BodiesByNative.TryGetValue(body.Native, out ShadowBody current) && ReferenceEquals(current, body))
					BodiesByNative.Remove(body.Native);
			}
			rig.Bodies.RemoveAt(index);
		}
		rig.CoreBodies.Clear();
		rig.PermanentlyDisabledBodies.Clear();
		rig.CenterBody = null;
	}

	private static bool IsSeveredJointSpawner(ShadowRig rig, CharacterJointSpawner spawner)
	{
		Rigidbody own = spawner != null ? spawner.GetComponent<Rigidbody>() : null;
		return own != null && rig.BodiesByNative.TryGetValue(own, out ShadowBody body) && rig.PermanentlyDisabledBodies.Contains(body);
	}

	// Terminal transition. The live record is removed first so every guard and coordinator hook below sees an
	// ordinary corpse; hooks fire only for a lease that was released to the coordinator, and exactly once.
	private static void FinishHandoff(HandoffLease lease, HandoffStage terminal, HandoffRigFate fate, string reason = null)
	{
		if (IsHandoffTerminal(lease.Stage)) return;
		Corpse corpse = lease.Corpse;
		EFT.Interactive.CorpseRagdoll ragdoll = lease.Ragdoll;
		ShadowRig rig = lease.Rig;
		bool notified = lease.ReleaseNotified;
		bool nativesTouched = lease.NativesTouched;
		lease.Stage = terminal;
		lease.RetireAt = Time.realtimeSinceStartup + CorpseHandoffPolicy.TerminalRecordSeconds;
		if (!ReferenceEquals(corpse, null) && HandoffByCorpse.TryGetValue(corpse, out HandoffLease live) && ReferenceEquals(live, lease))
			HandoffByCorpse.Remove(corpse);
		lease.Corpse = null;
		lease.Ragdoll = null;
		lease.Rig = null;
		lease.Clock = null;
		lease.SeveredBones = null;
		if (rig != null && fate != HandoffRigFate.Keep && Rigs.Contains(rig))
		{
			if (fate == HandoffRigFate.Destroy)
			{
				DestroyRig(rig, restoreNative: false);
			}
			else
			{
				if (nativesTouched) ReturnNativeRagdollToKinematic(ragdoll);
				AbortToNative(rig, reason ?? "corpse handoff adoption failed", log: false);
			}
		}
		if (!notified)
		{
			// Accepted but never released to the coordinator: it retained packets during the window and must replay them.
			if (!ReferenceEquals(corpse, null)) RagdollCoordinator.OnHandoffQueueEnded(corpse);
			return;
		}
		switch (terminal)
		{
		case HandoffStage.Closed:
			RagdollCoordinator.OnHandoffAdopted(corpse, ragdoll);
			break;
		case HandoffStage.Failed:
			RagdollCoordinator.OnHandoffAdoptionFailed(corpse);
			break;
		case HandoffStage.Cancelled:
			RagdollCoordinator.OnHandoffCancelled(corpse);
			break;
		}
	}

	// DestroyRig path: the rig is going away under a live lease (corpse destroyed, backend abort, native fallback).
	private static void InvalidateRigHandoff(ShadowRig rig)
	{
		if (HandoffByCorpse.Count == 0 || rig == null || ReferenceEquals(rig.Corpse, null) ||
			!HandoffByCorpse.TryGetValue(rig.Corpse, out HandoffLease lease) || (lease.Rig != null && !ReferenceEquals(lease.Rig, rig))) return;
		if (lease.Stage == HandoffStage.Queued)
		{
			if (lease.WasPreparing) HandoffNativeCorpses.Add(rig.Corpse);
			FinishHandoff(lease, HandoffStage.Failed, HandoffRigFate.Keep);
			return;
		}
		FinishHandoff(lease, HandoffStage.Cancelled, HandoffRigFate.Keep);
	}

	// Backend failure: queued releases fail (the normal fallback owns the corpse); held leases are invalidated.
	private static void FailHandoffLeases()
	{
		if (HandoffLeases.Count == 0) return;
		List<HandoffLease> live = new List<HandoffLease>(HandoffByCorpse.Values);
		foreach (HandoffLease lease in live)
			FinishHandoff(lease, lease.Stage == HandoffStage.Queued ? HandoffStage.Failed : HandoffStage.Cancelled, HandoffRigFate.Keep);
	}

	private static void ResetHandoffLeases()
	{
		if (HandoffByCorpse.Count != 0)
		{
			List<HandoffLease> live = new List<HandoffLease>(HandoffByCorpse.Values);
			foreach (HandoffLease lease in live) FinishHandoff(lease, HandoffStage.Cancelled, HandoffRigFate.Keep);
		}
		HandoffLeases.Clear();
		HandoffByCorpse.Clear();
		HandoffNativeCorpses.Clear();
		HandoffSnapshot.Clear();
		HandoffJointScratch.Clear();
	}
}
