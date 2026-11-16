using System;
using UnityEngine;

namespace Rupture.Ragdolls.External;

/// <summary>Progress of one environment prefetch consumer, see <see cref="IExternalEnvironmentPrefetchRuntime"/>.</summary>
public enum ExternalEnvironmentPrefetchState
{
	/// <summary>No prefetch is registered for the consumer (never requested, promoted, cancelled, expired or the raid is not ready).</summary>
	None = 0,
	/// <summary>The region is still being captured or its actors are still being created on the scene owner.</summary>
	Pending = 1,
	/// <summary>Every actor of the region exists and is referenced by the prefetch consumer.</summary>
	Ready = 2,
	/// <summary>The prefetch failed; the synchronous <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/> path is the fallback.</summary>
	Failed = 3
}

/// <summary>
/// Optional managed extension; older backends simply don't implement it (callers use <c>as</c>). It moves the main-thread
/// part of environment capture (overlap query, shape mapping, mesh extraction) out of the kill frame and lets the scene
/// owner create the actors without anyone waiting for them.
/// </summary>
/// <remarks>
/// Consumer id ranges (disjoint by their top two bits): the raw <c>ShadowRig.ExternalGroup</c> sequence number is the
/// <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/> consumer of a rig and never reaches bit 30; casing cell and
/// exposure consumers use <c>0x4000_0000u..0x7FFF_FFFFu</c> (bit 30 set, bit 31 clear, counting up from <c>0x4000_0000u</c>);
/// bot prefetch consumers and the local player's area prefetch use the same bot formula
/// <c>0x8000_0000u | (uint)(Player.Id &amp; 0x3FFF_FFFF)</c> (the local player's own <c>Player.Id</c>); a rig's corridor prefetch
/// uses <c>0xC000_0000u | group</c>. The backend treats every id as a plain key and assumes nothing about its range.
/// <c>EFT.Player.Id</c> is an <see cref="int"/> (it is written to <c>int PlayerId</c> packet fields). Zero is never a valid
/// consumer id. In a full-prebake raid there is nothing to capture, so every prefetch is
/// <see cref="ExternalEnvironmentPrefetchState.Ready"/> immediately and promotion is a no-op.
/// <see cref="PromoteEnvironmentPrefetch"/> and <see cref="CancelEnvironmentPrefetch"/> never throw: an unknown id is ignored,
/// and after promotion <see cref="GetEnvironmentPrefetchState"/> returns <see cref="ExternalEnvironmentPrefetchState.None"/> for the
/// prefetch id. Promoting a pending prefetch transfers whatever is committed or still in flight.
/// <para>
/// Acquisition guarantee (both environment modes): when a non-casing <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/>
/// returns success, every active eligible collider of its corridor has a native actor (and, in ProximityCache mode, a reference
/// held by the consumer), whether it was already created, unowned, or claimed by a pending prefetch, late-scene or deferred capture. The acquisition
/// captures the corridor's unowned colliders itself and takes over the corridor's claimed colliders from their jobs (an
/// unbuilt collider is unclaimed so the job skips it, a built one is created now), so its cost is bounded by the corridor
/// and not by the job; it never runs a whole late-scene job. Casing acquisitions run the claiming jobs to completion.
/// </para>
/// </remarks>
public interface IExternalEnvironmentPrefetchRuntime
{
	/// <summary>
	/// Non-blocking, main thread. Registers or refreshes a prefetch region for a consumer (a corridor sphere-swept from
	/// <paramref name="center"/> to <paramref name="sweepEnd"/>). The backend captures that corridor with an extra 0.5 m of
	/// radius. Re-requesting the same consumer with a corridor whose endpoints moved by more than 0.5 m replaces the region
	/// and returns the state to <see cref="ExternalEnvironmentPrefetchState.Pending"/> until the new corridor is captured.
	/// <see cref="ExternalEnvironmentPrefetchState.Ready"/> therefore means that every corridor whose endpoints are within
	/// 0.5 m of the requested ones, radius included, is captured. A corridor outside that distance is not covered and needs its
	/// own request or <see cref="TryAcquireEnvironment"/>. A re-request within 0.5 m only refreshes
	/// <paramref name="ttlSeconds"/>: the prefetch consumer releases its environment references after this long without a
	/// refresh or <see cref="PromoteEnvironmentPrefetch"/>.
	/// </summary>
	void RequestEnvironmentPrefetch(uint prefetchConsumerId, Vector3 center, Vector3 sweepEnd, float ttlSeconds);

	/// <summary>
	/// Main thread, never waits on the owner and never extracts geometry inline. If every active eligible collider in the
	/// corridor already has a native actor (none unknown, none claimed by an unfinished job), binds them to
	/// <paramref name="consumerId"/> exactly like <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/> and returns
	/// <see cref="ExternalEnvironmentPrefetchState.Ready"/>. Otherwise starts or continues an asynchronous capture for that
	/// corridor on behalf of <paramref name="consumerId"/> (pumped by <see cref="PumpEnvironmentPrefetch"/>) and returns
	/// <see cref="ExternalEnvironmentPrefetchState.Pending"/>; when the capture commits, the next call returns Ready and binds.
	/// <see cref="ExternalEnvironmentPrefetchState.Failed"/> means the capture failed; the caller may fall back to
	/// <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/> / EnsureCasingEnvironment. <paramref name="casing"/> selects
	/// casing semantics (as EnsureCasingEnvironment does). <see cref="IExternalPhysicsRuntime.ReleaseEnvironment"/> of a pending
	/// consumer cancels its capture's claim on that consumer, so nothing the capture commits later is bound to it (the actors
	/// stay in the cache), and a synchronous acquisition for the same id may follow a Pending result at any time: the next
	/// call then returns Ready. A consumer that asks about another corridor while the previous one is pending stops owning the
	/// previous corridor's capture (its commit binds nothing to the consumer). In a full-prebake raid nothing pumps the
	/// capture jobs, so a corridor with any collider that is not yet captured or is claimed returns
	/// <see cref="ExternalEnvironmentPrefetchState.Failed"/> at once, without creating a job, a pending record or a claim: the
	/// caller uses the synchronous path immediately (the full-prebake <c>EnsureEnvironment</c>). A fully captured corridor is
	/// Ready as usual. Ids are plain keys; the backend assumes nothing about their range.
	/// </summary>
	ExternalEnvironmentPrefetchState TryAcquireEnvironment(uint consumerId, Vector3 center, Vector3 sweepEnd, bool casing);

	ExternalEnvironmentPrefetchState GetEnvironmentPrefetchState(uint prefetchConsumerId);

	/// <summary>
	/// Main thread, once per frame. Does main-thread capture work (overlap, shape mapping, extraction) for pending
	/// prefetches within the given millisecond budget, in slices, and submits owner work without waiting. Returns the
	/// milliseconds spent.
	/// </summary>
	double PumpEnvironmentPrefetch(double budgetMilliseconds);

	/// <summary>
	/// Transfers the prefetched environment references to a real consumer (e.g. a corpse rig group) so
	/// <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/> for that consumer finds everything already created (reused),
	/// then drops the prefetch consumer. Non-blocking.
	/// </summary>
	void PromoteEnvironmentPrefetch(uint prefetchConsumerId, uint consumerId);

	void CancelEnvironmentPrefetch(uint prefetchConsumerId);
}

/// <summary>Optional extension: batch body/joint creation into one owner transaction.</summary>
public interface IExternalCreationBatchRuntime
{
	/// <summary>
	/// While the returned scope is open (main thread), <c>TryCreateBody</c>/<c>TryCreateRoundedBoxBody</c>/<c>TryCreateBoxBody</c>/<c>TryCreateJoint</c>
	/// queue their owner work instead of blocking; returned body/joint objects are valid managed objects whose native
	/// handles become usable when the scope is disposed (one blocking owner round trip for all queued work, same 500 ms
	/// watchdog semantics). Calls that need a native handle before disposal must throw <see cref="InvalidOperationException"/>.
	/// Dispose reports failure of any queued item by throwing; the caller treats that like a failed build. A creation that can
	/// only fail on the owner (a procedural weapon shape that fails to cook) must not be issued inside a batch when the caller
	/// has a fallback shape for it, because the whole batch rolls back instead of returning false.
	/// </summary>
	IExternalCreationBatch BeginCreationBatch();
}

/// <summary>An open creation batch (see <see cref="IExternalCreationBatchRuntime"/>).</summary>
public interface IExternalCreationBatch : IDisposable
{
	/// <summary>
	/// Gives the batch up without running it: nothing queued is created, every queued body/joint wrapper becomes invalid and
	/// a later <see cref="IDisposable.Dispose"/> does nothing. Never throws. A build that fails inside the scope calls this so
	/// the failure does not first pay for, and then tear down, the native creation.
	/// </summary>
	void Abandon();
}
