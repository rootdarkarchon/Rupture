using System;
using System.Threading;
using EFT.Interactive;
using UnityEngine;
using Rupture.Ragdolls.Classes;

namespace Rupture.Integration;

/// <summary>Optional, authority-side control of Rupture-owned corpse physics.</summary>
public static class CorpseDragV1
{
	private static int _mainThread;
	public static int AbiVersion => 1;
	internal static void InitializeThread() => _mainThread = Thread.CurrentThread.ManagedThreadId;
	private static bool OnMainThread => _mainThread != 0 && Thread.CurrentThread.ManagedThreadId == _mainThread;

	public static DragResult Inspect(Corpse corpse, out CorpseControlInfo info)
	{
		info = null;
		return OnMainThread ? ShadowRagdollWorld.InspectDrag(corpse, out info) : DragResult.InvalidArgument;
	}
	public static DragResult Begin(Corpse corpse, DragBegin request, out ulong lease)
	{
		lease = 0;
		return OnMainThread ? ShadowRagdollWorld.BeginDrag(corpse, request, out lease) : DragResult.InvalidArgument;
	}
	public static DragResult Read(ulong lease, out DragView view)
	{
		view = null;
		return OnMainThread ? ShadowRagdollWorld.ReadDrag(lease, out view) : DragResult.InvalidArgument;
	}
	public static DragResult Submit(ulong lease, DragFrame frame) =>
		OnMainThread ? ShadowRagdollWorld.SubmitDrag(lease, frame) : DragResult.InvalidArgument;
	public static DragResult End(ulong lease, DragEndReason reason) =>
		OnMainThread ? ShadowRagdollWorld.EndDrag(lease, reason) : DragResult.InvalidArgument;
}

public enum DragResult
{
	Ok = 0, Pending = 1, Closed = 2, NotManaged = 3, NotAuthority = 4, Busy = 5,
	InvalidArgument = 6, InvalidLease = 7, Unsupported = 8, Failed = 9, Stale = 10
}
public enum CorpseControlState { Unmanaged = 0, Preparing = 1, Managed = 2, Unavailable = 3 }
public enum DragStage { Preparing = 0, Held = 1, Releasing = 2, Closed = 3, Failed = 4 }
public enum DragEndReason { Released = 0, Cancelled = 1, LostTarget = 2, Disconnected = 3 }
[Flags]
public enum DragCapabilities { None = 0, RadialTether = 1, BodyAcceleration = 2, RigTranslation = 4 }

public sealed class CorpseControlInfo
{
	public CorpseControlState State;
	public DragCapabilities Capabilities;
	public bool IsAuthority;
	public string ProfileId;
	public uint DeathSequence;
}
public sealed class DragBegin
{
	public Rigidbody PreferredBody;
	public float GripSlack;
	public float MaxGripSpeed;
	public float LeaseTimeout;
}
public sealed class DragView
{
	public DragStage Stage;
	public ulong CompletedStep;
	public float SimulationDeltaTime;
	public uint TopologyRevision;
	public int GripBodyIndex;
	public Vector3 GripPosition;
	public uint LastTranslationId;
	public DragBodyView[] Bodies;
}
public struct DragBodyView
{
	public int BodyIndex;
	public Rigidbody NativeBody;
	public Transform NativeTransform;
	public bool Eligible;
	public bool IsSleeping;
	public Vector3 Position;
	public Quaternion Rotation;
	public Vector3 WorldCenterOfMass;
	public Vector3 Velocity;
	public Vector3 AngularVelocity;
}
public sealed class DragFrame
{
	public uint Sequence;
	public uint TopologyRevision;
	public Vector3 GripTarget;
	public DragAcceleration[] Accelerations;
	public uint TranslationId;
	public Vector3 RigTranslation;
}
public struct DragAcceleration
{
	public int BodyIndex;
	public Vector3 Acceleration;
}

// The input buffer has no Unity/native side effects. A rejected frame must not
// renew a lease, change its drive, or overwrite an accepted recovery request.
internal sealed class DragInputBuffer
{
	internal DragFrame Current { get; private set; }
	internal uint Sequence { get; private set; }
	internal uint PendingTranslationId { get; private set; }
	internal Vector3 PendingTranslation { get; private set; }
	internal uint AppliedTranslationId { get; private set; }
	private Vector3 _appliedTranslation;

	internal static bool ValidBegin(DragBegin request) => request != null &&
		Finite(request.GripSlack) && request.GripSlack > 0f &&
		Finite(request.MaxGripSpeed) && request.MaxGripSpeed > 0f &&
		Finite(request.LeaseTimeout) && request.LeaseTimeout >= 0.5f && request.LeaseTimeout <= 10f;
	internal static bool Expired(float now, float renewedAt, float timeout) => now - renewedAt >= timeout;
	internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
	internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
	private static bool Equal(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

	internal DragResult Accept(DragFrame frame, uint topology, bool[] eligible)
	{
		if (frame == null || frame.Sequence == 0 || eligible == null || !Finite(frame.GripTarget) || !Finite(frame.RigTranslation))
			return DragResult.InvalidArgument;
		if (frame.Sequence <= Sequence || frame.TopologyRevision != topology) return DragResult.Stale;
		if (frame.TranslationId == 0 && !Equal(frame.RigTranslation, Vector3.zero)) return DragResult.InvalidArgument;
		if (frame.TranslationId != 0)
		{
			if (frame.TranslationId < AppliedTranslationId) return DragResult.Stale;
			if (frame.TranslationId == AppliedTranslationId && !Equal(frame.RigTranslation, _appliedTranslation)) return DragResult.InvalidArgument;
			if (PendingTranslationId != 0 && frame.TranslationId != AppliedTranslationId)
			{
				if (frame.TranslationId != PendingTranslationId) return DragResult.Busy;
				if (!Equal(frame.RigTranslation, PendingTranslation)) return DragResult.InvalidArgument;
			}
		}
		DragAcceleration[] inputs = frame.Accelerations ?? Array.Empty<DragAcceleration>();
		if (inputs.Length > eligible.Length) return DragResult.InvalidArgument;
		bool[] seen = new bool[eligible.Length];
		foreach (DragAcceleration acceleration in inputs)
		{
			int index = acceleration.BodyIndex;
			if (index < 0 || index >= eligible.Length || !eligible[index] || seen[index] || !Finite(acceleration.Acceleration))
				return DragResult.InvalidArgument;
			seen[index] = true;
		}
		Current = new DragFrame
		{
			Sequence = frame.Sequence, TopologyRevision = topology, GripTarget = frame.GripTarget,
			Accelerations = (DragAcceleration[])inputs.Clone()
		};
		Sequence = frame.Sequence;
		if (frame.TranslationId > AppliedTranslationId && PendingTranslationId == 0)
		{
			PendingTranslationId = frame.TranslationId;
			PendingTranslation = frame.RigTranslation;
		}
		return DragResult.Ok;
	}

	internal void TranslationApplied()
	{
		AppliedTranslationId = PendingTranslationId;
		_appliedTranslation = PendingTranslation;
		PendingTranslationId = 0;
		PendingTranslation = Vector3.zero;
	}
	internal void Cancel() { Current = null; PendingTranslationId = 0; PendingTranslation = Vector3.zero; }
}
