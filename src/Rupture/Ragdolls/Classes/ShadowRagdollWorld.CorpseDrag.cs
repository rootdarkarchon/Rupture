using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;
using Rupture.Integration;
using Rupture.Ragdolls.External;

namespace Rupture.Ragdolls.Classes;

internal static partial class ShadowRagdollWorld
{
	private sealed class DragLease
	{
		internal ulong Id;
		internal Corpse Corpse;
		internal EFT.Interactive.CorpseRagdoll Ragdoll;
		internal uint DeathSequence;
		internal DragBegin Request;
		internal DragStage Stage = DragStage.Preparing;
		internal ShadowRig Rig;
		internal IExternalPhysicsBody Hand;
		internal IExternalPhysicsJoint Tether;
		internal int GripIndex = -1;
		internal bool[] Eligible = Array.Empty<bool>();
		internal uint TopologyRevision = 1;
		internal readonly DragInputBuffer Input = new DragInputBuffer();
		internal DragView View;
		internal float RenewedAt;
		internal float RetireAt;
		internal bool Failed;
	}

	private const DragCapabilities CorpseDragCapabilities = DragCapabilities.RadialTether | DragCapabilities.BodyAcceleration | DragCapabilities.RigTranslation;
	private static readonly Dictionary<ulong, DragLease> DragLeases = new Dictionary<ulong, DragLease>();
	private static readonly Dictionary<Corpse, DragLease> DragByCorpse = new Dictionary<Corpse, DragLease>();
	private static readonly List<DragLease> DragSnapshot = new List<DragLease>();
	private static ulong _nextDragLease; // deliberately not reset between raids
	private static ulong _dragCompletedStep;
	private static bool _dragSimulationActive;

	internal static bool CanBindDragSequence(uint current, uint incoming) => current == 0 && incoming != 0;
	internal static void BindDragDeathIdentity(Corpse corpse, EFT.Interactive.CorpseRagdoll ragdoll, uint sequence)
	{
		// A client can capture before its reliable authority start arrives. Promote
		// only the unassigned identity of the exact rig; never rebind an old death
		// or a lease acquired before a lifetime change.
		if (corpse != null && RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) &&
			ReferenceEquals(rig.NativeRagdoll, ragdoll) && CanBindDragSequence(rig.ControlDeathSequence, sequence) &&
			!DragByCorpse.ContainsKey(corpse)) rig.ControlDeathSequence = sequence;
	}

	internal static DragResult InspectDrag(Corpse corpse, out CorpseControlInfo info)
	{
		info = null;
		if (corpse == null || corpse.Ragdoll == null || !ReferenceEquals(corpse.Ragdoll._owner, corpse)) return DragResult.InvalidArgument;
		bool coordinatorOwned = RagdollCoordinator.TryGetDragIdentity(corpse, out uint sequence);
		bool hasRig = RigsByCorpse.TryGetValue(corpse, out ShadowRig rig);
		bool pending = HasPendingDragRig(corpse.Ragdoll);
		info = new CorpseControlInfo
		{
			ProfileId = corpse.PlayerProfileID, DeathSequence = sequence,
			IsAuthority = RagdollCoordinator.IsNetworkAuthority, State = CorpseControlState.Unmanaged
		};
		if (!hasRig && !pending && !coordinatorOwned && !DragByCorpse.ContainsKey(corpse)) return DragResult.NotManaged;
		if (!Enabled || !_dragSimulationActive || RagdollCoordinator.FreezeCorpseRagdollsForWoundDebug ||
			_externalRuntime is not IExternalCorpseControlRuntime || !string.IsNullOrEmpty(_externalRuntime.Failure) ||
			!coordinatorOwned || (sequence == 0 && (MultiplayerRuntime.Current.HasServer || RagdollCoordinator.IsRemoteClient)) ||
			(hasRig && rig.ControlDeathSequence != sequence))
		{
			info.State = CorpseControlState.Unavailable;
			return DragResult.Unsupported;
		}
		if (!hasRig && !pending)
		{
			info.State = CorpseControlState.Unavailable;
			return DragResult.Unsupported;
		}
		info.Capabilities = CorpseDragCapabilities;
		info.State = hasRig && _externalRuntime.IsRaidReady ? CorpseControlState.Managed : CorpseControlState.Preparing;
		return DragResult.Ok;
	}

	internal static DragResult BeginDrag(Corpse corpse, DragBegin request, out ulong lease)
	{
		lease = 0;
		if (!DragInputBuffer.ValidBegin(request)) return DragResult.InvalidArgument;
		DragResult result = InspectDrag(corpse, out CorpseControlInfo info);
		if (result != DragResult.Ok) return result;
		if (!info.IsAuthority) return DragResult.NotAuthority;
		if (DragByCorpse.ContainsKey(corpse)) return DragResult.Busy;
		if (!ReferenceEquals(request.PreferredBody, null) &&
			(request.PreferredBody == null || !IsPreferredDragBody(corpse.Ragdoll, request.PreferredBody))) return DragResult.InvalidArgument;
		if (RigsByCorpse.TryGetValue(corpse, out ShadowRig existing) && SelectDragBody(existing, request.PreferredBody) < 0)
			return DragResult.InvalidArgument;
		if (_nextDragLease == ulong.MaxValue) return DragResult.Failed;
		DragLease created = new DragLease
		{
			Id = ++_nextDragLease, Corpse = corpse, Ragdoll = corpse.Ragdoll, DeathSequence = info.DeathSequence,
			RenewedAt = Time.realtimeSinceStartup,
			Request = new DragBegin
			{
				PreferredBody = request.PreferredBody, GripSlack = request.GripSlack,
				MaxGripSpeed = request.MaxGripSpeed, LeaseTimeout = request.LeaseTimeout
			}
		};
		DragLeases.Add(created.Id, created);
		DragByCorpse.Add(corpse, created);
		lease = created.Id;
		return DragResult.Pending;
	}

	internal static DragResult ReadDrag(ulong lease, out DragView view)
	{
		view = null;
		if (!DragLeases.TryGetValue(lease, out DragLease control)) return DragResult.InvalidLease;
		CheckDragLife(control, Time.realtimeSinceStartup);
		RefreshDragTopology(control);
		DragView published = control.View;
		view = new DragView
		{
			Stage = control.Stage, CompletedStep = published?.CompletedStep ?? _dragCompletedStep,
			SimulationDeltaTime = published?.SimulationDeltaTime ?? 0f,
			TopologyRevision = control.TopologyRevision, GripBodyIndex = control.GripIndex,
			GripPosition = published?.GripPosition ?? Vector3.zero,
			LastTranslationId = published?.LastTranslationId ?? 0,
			Bodies = published?.Bodies != null ? (DragBodyView[])published.Bodies.Clone() : Array.Empty<DragBodyView>()
		};
		for (int i = 0; i < view.Bodies.Length && i < control.Eligible.Length; i++) view.Bodies[i].Eligible = control.Eligible[i];
		return DragStageResult(control.Stage);
	}

	internal static DragResult SubmitDrag(ulong lease, DragFrame frame)
	{
		if (!DragLeases.TryGetValue(lease, out DragLease control)) return DragResult.InvalidLease;
		CheckDragLife(control, Time.realtimeSinceStartup);
		RefreshDragTopology(control);
		if (control.Stage != DragStage.Held) return DragStageResult(control.Stage);
		DragResult accepted = control.Input.Accept(frame, control.TopologyRevision, control.Eligible);
		if (accepted == DragResult.Ok) control.RenewedAt = Time.realtimeSinceStartup;
		return accepted;
	}

	internal static DragResult EndDrag(ulong lease, DragEndReason reason)
	{
		if ((int)reason < 0 || reason > DragEndReason.Disconnected) return DragResult.InvalidArgument;
		if (!DragLeases.TryGetValue(lease, out DragLease control)) return DragResult.InvalidLease;
		if (control.Stage == DragStage.Closed || control.Stage == DragStage.Failed) return DragStageResult(control.Stage);
		RequestDragRelease(control, failed: false);
		return DragResult.Pending;
	}

	internal static bool IsDragHeld(Corpse corpse) => DragByCorpse.Count != 0 && !ReferenceEquals(corpse, null) && DragByCorpse.ContainsKey(corpse);

	private static DragResult DragStageResult(DragStage stage) => stage == DragStage.Held ? DragResult.Ok :
		stage == DragStage.Closed ? DragResult.Closed : stage == DragStage.Failed ? DragResult.Failed : DragResult.Pending;
	private static bool HasPendingDragRig(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		foreach (PendingBuild pending in PendingBuilds) if (ReferenceEquals(pending.Ragdoll, ragdoll)) return true;
		return false;
	}
	private static bool IsPreferredDragBody(EFT.Interactive.CorpseRagdoll ragdoll, Rigidbody preferred)
	{
		if (ragdoll._rigidbodySpawners == null) return false;
		foreach (var spawner in ragdoll._rigidbodySpawners)
			if (spawner != null && ReferenceEquals(spawner.Rigidbody, preferred)) return true;
		return false;
	}
	private static bool DragBodyEligible(ShadowRig rig, int index) => index >= 0 && index < rig.CoreBodies.Count &&
		rig.CoreBodies[index]?.NativeTransform != null && rig.CoreBodies[index].Proxy is ExternalShadowBodyProxy external &&
		external.ExternalBody.IsValid && !rig.PermanentlyDisabledBodies.Contains(rig.CoreBodies[index]);
	private static int SelectDragBody(ShadowRig rig, Rigidbody preferred)
	{
		for (int i = 0; i < rig.CoreBodies.Count; i++)
		{
			if (!DragBodyEligible(rig, i)) continue;
			if (!ReferenceEquals(preferred, null))
			{
				if (ReferenceEquals(rig.CoreBodies[i].Native, preferred)) return i;
			}
			else if (rig.CoreBodies[i].IsDragChest) return i;
		}
		return -1;
	}

	private static void CheckDragLife(DragLease control, float now)
	{
		if (control.Stage == DragStage.Closed || control.Stage == DragStage.Failed) return;
		bool invalid = control.Corpse == null || !ReferenceEquals(control.Corpse.Ragdoll, control.Ragdoll) ||
			!RagdollCoordinator.TryGetDragIdentity(control.Corpse, out uint sequence) || sequence != control.DeathSequence ||
			!Enabled || !_dragSimulationActive || !RagdollCoordinator.IsNetworkAuthority ||
			RagdollCoordinator.FreezeCorpseRagdollsForWoundDebug || _externalRuntime is not IExternalCorpseControlRuntime ||
			!string.IsNullOrEmpty(_externalRuntime.Failure);
		if (invalid) RequestDragRelease(control, failed: true);
		else if (DragInputBuffer.Expired(now, control.RenewedAt, control.Request.LeaseTimeout)) RequestDragRelease(control, failed: false);
	}
	private static void RequestDragRelease(DragLease control, bool failed)
	{
		control.Failed |= failed;
		control.Stage = DragStage.Releasing;
		control.Input.Cancel();
	}
	private static void RefreshDragTopology(DragLease control)
	{
		if (control.Rig == null || control.Stage == DragStage.Closed || control.Stage == DragStage.Failed) return;
		bool changed = false;
		if (control.Eligible.Length != control.Rig.CoreBodies.Count) { control.Eligible = new bool[control.Rig.CoreBodies.Count]; changed = true; }
		for (int i = 0; i < control.Eligible.Length; i++)
		{
			bool eligible = DragBodyEligible(control.Rig, i);
			if (eligible != control.Eligible[i]) { control.Eligible[i] = eligible; changed = true; }
		}
		if (changed) control.TopologyRevision++;
		if (control.GripIndex >= 0 && (control.GripIndex >= control.Eligible.Length || !control.Eligible[control.GripIndex]))
			RequestDragRelease(control, failed: false);
	}

	// This runs only after the preceding external step is published. No caller's
	// Update/packet handler can create a joint or rewrite actors mid-simulation.
	private static void ProcessDragBoundary()
	{
		if (DragLeases.Count == 0) return;
		DragSnapshot.Clear();
		DragSnapshot.AddRange(DragLeases.Values);
		float now = Time.realtimeSinceStartup;
		foreach (DragLease control in DragSnapshot)
		{
			if (control.Stage == DragStage.Closed || control.Stage == DragStage.Failed)
			{
				if (now >= control.RetireAt) DragLeases.Remove(control.Id);
				continue;
			}
			CheckDragLife(control, now);
			RefreshDragTopology(control);
			try
			{
				if (control.Stage == DragStage.Preparing && _externalRuntime.IsRaidReady)
				{
					if (RigsByCorpse.TryGetValue(control.Corpse, out ShadowRig rig)) ActivateDrag(control, rig);
					else if (!HasPendingDragRig(control.Ragdoll)) RequestDragRelease(control, failed: true);
				}
				if (control.Stage == DragStage.Releasing) FinishDrag(control);
			}
			catch (Exception exception)
			{
				RupturePlugin.Log.LogWarning("Corpse control failed: " + exception.Message);
				RequestDragRelease(control, failed: true);
				FinishDrag(control);
			}
		}
	}

	private static void ActivateDrag(DragLease control, ShadowRig rig)
	{
		int grip = SelectDragBody(rig, control.Request.PreferredBody);
		if (grip < 0 || rig.ControlDeathSequence != control.DeathSequence) { RequestDragRelease(control, failed: true); return; }
		control.Rig = rig;
		control.GripIndex = grip;
		RefreshDragTopology(control);
		// Explicit control activation is independent of the impact-reactivation option.
		if (!TryWakeFromImpact(rig)) { RequestDragRelease(control, failed: true); return; }
		rig.IgnoreNativeStopped = true;
		if (!RagdollCoordinator.ActivateDragMotion(control.Corpse, control.DeathSequence))
		{ RequestDragRelease(control, failed: true); return; }
		IExternalPhysicsBody body = ((ExternalShadowBodyProxy)rig.CoreBodies[grip].Proxy).ExternalBody;
		if (!((IExternalCorpseControlRuntime)_externalRuntime).TryCreateCorpseGrip(body, rig.ExternalGroup, control.Request.GripSlack,
			out control.Hand, out control.Tether, out string failure))
		{
			RupturePlugin.Log.LogWarning("Corpse grip creation failed: " + failure);
			RequestDragRelease(control, failed: true);
			return;
		}
		control.Stage = DragStage.Held;
		CaptureDragView(control);
	}

	private static void DriveDragLeases(float deltaTime)
	{
		foreach (DragLease control in DragLeases.Values)
		{
			if (control.Stage != DragStage.Held) continue;
			RefreshDragTopology(control);
			if (control.Stage != DragStage.Held) continue;
			if (control.Hand?.IsValid != true || control.Tether?.IsValid != true || control.Tether.IsBroken)
			{ RequestDragRelease(control, failed: true); continue; }
			try
			{
				if (control.Input.PendingTranslationId != 0 && !TranslateDragRig(control))
				{ RequestDragRelease(control, failed: true); continue; }
				DragFrame frame = control.Input.Current;
				if (frame == null) continue;
				Vector3 target = Vector3.MoveTowards(control.Hand.Position, frame.GripTarget, control.Request.MaxGripSpeed * deltaTime);
				if (!((IExternalCorpseControlRuntime)_externalRuntime).TryMoveCorpseGrip(control.Hand, target, out string failure))
				{
					RupturePlugin.Log.LogWarning("Corpse grip target failed: " + failure);
					RequestDragRelease(control, failed: true);
					continue;
				}
				foreach (DragAcceleration acceleration in frame.Accelerations)
					if (control.Eligible[acceleration.BodyIndex])
						control.Rig.CoreBodies[acceleration.BodyIndex].Proxy.AddForce(acceleration.Acceleration, ForceMode.Acceleration);
			}
			catch (Exception exception)
			{
				RupturePlugin.Log.LogWarning("Corpse drive failed: " + exception.Message);
				RequestDragRelease(control, failed: true);
			}
		}
	}

	private static bool TranslateDragRig(DragLease control)
	{
		ShadowRig rig = control.Rig;
		Vector3 offset = control.Input.PendingTranslation;
		Vector3 center = CalculateCenter(rig) + offset;
		if (!DragInputBuffer.Finite(center) || !TryAcquireEnvironment(rig, center, center, out string failure, out _, out _)) return false;
		List<ExternalBodyState> states = new List<ExternalBodyState>();
		for (int i = 0; i < rig.CoreBodies.Count; i++)
		{
			if (!control.Eligible[i]) continue;
			IExternalPhysicsBody body = ((ExternalShadowBodyProxy)rig.CoreBodies[i].Proxy).ExternalBody;
			Vector3 position = body.Position + offset;
			if (!DragInputBuffer.Finite(position)) return false;
			states.Add(new ExternalBodyState(body, position, body.Rotation, Vector3.zero, Vector3.zero, false, false));
		}
		Vector3 handPosition = control.Hand.Position + offset;
		if (!DragInputBuffer.Finite(handPosition)) return false;
		states.Add(new ExternalBodyState(control.Hand, handPosition, control.Hand.Rotation, Vector3.zero, Vector3.zero, true, false));
		if (!_externalRuntime.TryApplyBodyStates(states, out failure)) return false;
		foreach (ShadowBody parked in rig.PermanentlyDisabledBodies) UpdateParkedBodyPose(parked, updateVisible: false);
		control.Input.TranslationApplied();
		return true;
	}

	private static void CaptureDragView(DragLease control)
	{
		DragBodyView[] bodies = new DragBodyView[control.Rig.CoreBodies.Count];
		for (int i = 0; i < bodies.Length; i++)
		{
			ShadowBody body = control.Rig.CoreBodies[i];
			IShadowBodyProxy proxy = body?.Proxy;
			bodies[i] = new DragBodyView
			{
				BodyIndex = i, NativeBody = body?.Native != null ? body.Native : null,
				NativeTransform = body?.NativeTransform, Eligible = control.Eligible[i],
				IsSleeping = proxy?.IsSleeping() ?? true, Position = proxy?.position ?? Vector3.zero,
				Rotation = proxy?.rotation ?? Quaternion.identity, WorldCenterOfMass = proxy?.worldCenterOfMass ?? Vector3.zero,
				Velocity = proxy?.velocity ?? Vector3.zero, AngularVelocity = proxy?.angularVelocity ?? Vector3.zero
			};
		}
		control.View = new DragView
		{
			Stage = control.Stage, CompletedStep = _dragCompletedStep, TopologyRevision = control.TopologyRevision,
			SimulationDeltaTime = _completedExternalSimulationDelta,
			GripBodyIndex = control.GripIndex, GripPosition = control.Hand?.Position ?? control.View?.GripPosition ?? Vector3.zero,
			LastTranslationId = control.Input.AppliedTranslationId, Bodies = bodies
		};
	}
	private static void PublishDragViews(ulong step)
	{
		_dragCompletedStep = step;
		foreach (DragLease control in DragLeases.Values)
			if (control.Stage == DragStage.Held || (control.Stage == DragStage.Releasing && control.Hand != null))
			{
				RefreshDragTopology(control);
				CaptureDragView(control);
			}
	}

	private static void DisposeDragGrip(DragLease control)
	{
		IExternalPhysicsJoint tether = control.Tether;
		IExternalPhysicsBody hand = control.Hand;
		control.Tether = null;
		control.Hand = null;
		try { _externalRuntime?.DestroyJoint(tether); }
		finally { _externalRuntime?.DestroyBody(hand); }
	}
	private static void FinishDrag(DragLease control)
	{
		try
		{
			DisposeDragGrip(control);
			ShadowRig rig = control.Rig;
			if (rig?.Corpse != null && rig.Root != null && ReferenceEquals(control.Corpse.Ragdoll, control.Ragdoll))
			{
				foreach (ShadowBody body in rig.CoreBodies)
					if (body?.Proxy != null && !rig.PermanentlyDisabledBodies.Contains(body))
					{
						body.Proxy.velocity = Vector3.ClampMagnitude(body.Proxy.velocity, 2f);
						body.Proxy.angularVelocity = Vector3.ClampMagnitude(body.Proxy.angularVelocity, 4f);
					}
				rig.IgnoreNativeStopped = true;
				rig.StartedAt = Time.realtimeSinceStartup;
				rig.SleepingSteps = 0;
				RagdollCoordinator.ReleaseDragMotion(control.Corpse, control.Ragdoll, control.DeathSequence);
				CaptureDragView(control);
				RagdollCoordinator.SendPoseSnapshot(control.Corpse, force: true);
			}
		}
		catch (Exception exception)
		{
			control.Failed = true;
			RupturePlugin.Log.LogWarning("Corpse release failed: " + exception.Message);
		}
		finally { CloseDragRecord(control); }
	}
	private static void CloseDragRecord(DragLease control)
	{
		control.Stage = control.Failed ? DragStage.Failed : DragStage.Closed;
		control.RetireAt = Time.realtimeSinceStartup + 30f;
		control.Input.Cancel();
		DragByCorpse.Remove(control.Corpse);
		control.Rig = null;
		control.Request = null;
		control.Corpse = null;
		control.Ragdoll = null;
	}
	private static void InvalidateRigDrag(ShadowRig rig)
	{
		if (rig == null || ReferenceEquals(rig.Corpse, null) || !DragByCorpse.TryGetValue(rig.Corpse, out DragLease control)) return;
		DisposeDragGrip(control); // queue joint before actor before rig actors
		control.Failed = true;
		CloseDragRecord(control);
	}
	private static void ResetDragLeases()
	{
		foreach (DragLease control in DragLeases.Values) DisposeDragGrip(control);
		DragByCorpse.Clear();
		DragLeases.Clear();
		DragSnapshot.Clear();
		_dragCompletedStep = 0;
		_dragSimulationActive = false;
	}
}
