using EFT.Interactive;
using UnityEngine;
using Rupture.Ragdolls.Classes;

namespace Rupture.Integration;

/// <summary>Optional, per-peer handoff of a Rupture-managed corpse to the caller's own Unity physics.</summary>
public static class CorpseHandoffV1
{
	public static int AbiVersion => 1;

	public static HandoffResult Inspect(Corpse corpse, out CorpseHandoffInfo info)
	{
		info = null;
		return CorpseDragV1.OnMainThread ? ShadowRagdollWorld.InspectHandoff(corpse, out info) : HandoffResult.InvalidArgument;
	}
	public static HandoffResult Release(Corpse corpse, HandoffRelease request, out ulong token)
	{
		token = 0;
		return CorpseDragV1.OnMainThread ? ShadowRagdollWorld.ReleaseHandoff(corpse, request, out token) : HandoffResult.InvalidArgument;
	}
	public static HandoffResult Renew(ulong token) =>
		CorpseDragV1.OnMainThread ? ShadowRagdollWorld.RenewHandoff(token) : HandoffResult.InvalidArgument;
	public static HandoffResult Adopt(ulong token) =>
		CorpseDragV1.OnMainThread ? ShadowRagdollWorld.AdoptHandoff(token) : HandoffResult.InvalidArgument;
}

public enum HandoffResult
{
	Ok = 0, Pending = 1, NotManaged = 2, Busy = 3, Unavailable = 4,
	InvalidArgument = 5, InvalidToken = 6, Failed = 7, Stale = 8
}
public enum HandoffState { Unmanaged = 0, Preparing = 1, Managed = 2, Released = 3, Unavailable = 4 }

public sealed class CorpseHandoffInfo
{
	public HandoffState State;
	public bool Simulating;            // Rupture is still moving the body
	public string ProfileId;
	public uint DeathSequence;
	public uint OwnershipEpoch;
	public Transform[] SeveredRoots;   // copied; bones whose subtree Rupture removed
	public bool WeaponIndependent;     // weapon simulated apart from the core rig
}

public sealed class HandoffRelease
{
	public float LeaseTimeout;         // unscaled seconds; finite, [1, 120]; refreshed by Renew
}
