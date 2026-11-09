# Corpse handoff API V1

`Rupture.Integration.CorpseHandoffV1` lets another mod take a Rupture-managed corpse into its own Unity physics on each peer. When the mod hands it back, Rupture rebuilds its managed physics from wherever the bones ended up. This is the implemented contract in Rupture 1.4.0 (impact protocol 16). It is uncommitted working-tree code until it is validated and merged.

The motivating consumer is KMA Body Drag. It drags corpses with its own Unity rigidbodies and joints, simulates the held body on every Fika peer from the dragger's position and heading, and broadcasts final bone poses on drop. [CORPSE-CONTROL-API.md](CORPSE-CONTROL-API.md) (`CorpseDragV1`) does not fit that model:

- it runs only on the authority, so a client dragger would see the body through the authority motion stream with round-trip lag;
- its single radial tether leaves angular motion free, while the consumer holds orientation with a two-shoulder joint grip, rolls face-down bodies and re-centres native joint limits. V1 offers no joint setters, pose repairs or off-centre grip;
- the consumer needs a Unity route anyway when Rupture is absent.

`CorpseDragV1` remains the API for consumers that want Rupture's solver to own the drag. Handoff is for consumers that run their own Unity simulation.

## Current behaviour this replaces

Before this API, a consumer that took a corpse into Unity physics had to call the private `ShadowRagdollWorld.AbortToNative` by reflection on pickup. That is Rupture's failure path. It writes proxy poses onto native bodies, restores native kinematic and support state, increments the build-failure counter, logs a "returned to native EFT physics" warning and destroys the rig permanently. After the drop, shots no longer move the corpse.

Without a handoff, Rupture conflicts with a consumer's hold in these ways:

| Conflict | Cause in Rupture |
| --- | --- |
| Rupture writes poses over the consumer's hold | A woken rig presents its proxy poses onto native bones every step (`SyncNativeFromProxy`, visible-pose reassertion). |
| A shot after the drop snaps the body back to its death pose | `TryWakeFromImpact` reactivates the settled rig from its stored proxy pose with zero velocity; it never reads current bone transforms. |
| Network traffic ignores the move | Pose, wake, settle, force and impulse packets carry proxy state keyed by death sequence. |
| Neighbouring corpses can wake the held body | Impact wake propagation and moving-environment reactivation target the rig. |

## Design decisions

**Release and rebuild, not suspend and resume.** The consumer recreates rigidbodies and joints from `_rigidbodySpawners` / `_jointSpawners` at pickup and removes them at drop. The rig and coordinator are keyed by those object instances (`ShadowRig.BodiesByNative`, `RagdollCoordinator.BindingsByRigidbody`, `ExternalJointBinding.NativeJoint`), so a suspended rig would reference destroyed objects. Release quiesces Rupture's ownership; Adopt builds a fresh rig from the current bones.

**Rupture always rebuilds from the spawners, never from live bodies.** Rig capture reads the live native objects. The consumer's bodies carry drag tuning (segment masses, joint axes and limits, mass scale, solver iterations, sleep threshold, speed caps, drag and collider material). None of that may enter the rig.

**The authority owns the ownership epoch.** This follows the existing model, where the authority assigns death sequences and clients replay complete authority pose sets.

## Public managed declarations

The API sits in `Rupture.dll`, namespace `Rupture.Integration`, and follows `CorpseDragV1` conventions: exact type and version resolution through an optional adapter, and main-thread calls only. Source: [CorpseHandoffV1.cs](../src/Rupture/Integration/CorpseHandoffV1.cs).

```csharp
public static class CorpseHandoffV1
{
    public static int AbiVersion { get; } // exactly 1
    public static HandoffResult Inspect(Corpse corpse, out CorpseHandoffInfo info);
    public static HandoffResult Release(Corpse corpse, HandoffRelease request, out ulong token);
    public static HandoffResult Renew(ulong token);
    public static HandoffResult Adopt(ulong token);
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
    public bool Simulating;            // Rupture is still moving the body; see Inspect
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
```

`HandoffState`, `HandoffResult` values and field meanings form the consumer contract. A future version must not reinterpret them.

All four methods are main-thread only. A call from another thread returns `InvalidArgument` without side effects. Expected failures are results, not exceptions. DTOs are copied.

## Semantics

### Inspect

Inspect is read-only and works on every peer in every role.

- `NotManaged` authorizes an independent native route only when Rupture has neither a rig nor a coordinator reservation for the corpse (same rule as `CorpseDragV1.Inspect`).
- `Preparing` and `Unavailable` mean Rupture retains ownership; they do not grant permission.
- `Simulating` is true while Rupture is still moving the body:
  - On the authority or solo, the rig is active (not dormant, not settled).
  - On a client, the rig is predicting or playing back authority poses and no settle packet has been accepted yet.
  - Native shells are kinematic, so they cannot answer this. `State == Managed` covers active, dormant and settled alike.
- `SeveredRoots` lists removed subtrees. Rupture disables those colliders and parks those bodies. The consumer must not create bodies or joints under them.
- `WeaponIndependent` is true when the weapon is a separate Rupture body detached from the hand.

### Release

Release is per peer, in any role, with no authority check. Every peer releases its own copy when the consumer's pickup event reaches it.

Preconditions, checked in this order (`ShadowRagdollWorld.ReleaseHandoff`):

1. `request` is non-null and `LeaseTimeout` is valid (`CorpseHandoffPolicy.IsValidLeaseTimeout`), else `InvalidArgument`.
2. `InspectHandoff` runs. An `InvalidArgument` result is returned as-is.
3. The corpse has no handoff lease and no `CorpseDragV1` lease, else `Busy`.
4. The token counter is not exhausted, else `Failed`.

A rejection returns token 0 and changes nothing. `Busy` means another owner; retrying every frame is pointless, so consumers should refuse the pickup. An accepted call returns `Pending` and a nonzero token. The effects below apply at a later simulation boundary. `Inspect` then reports `Released`.

Effects:

1. Present the latest proxy pose onto the native bones once, so the consumer starts from what the player saw.
2. Make every core and parked external body kinematic with collision disabled. The independent weapon body stays Rupture-owned and keeps simulating. Already detached gibs and headgear keep their own lifetimes.
3. Stop all rig participation:
   - no native pose presentation, visible-pose reassertion, settle or sleep bookkeeping;
   - no wake from impact, explosion, network, moving environment or neighbouring-corpse propagation;
   - point and radial impulses on the corpse fall through to whatever native bodies exist, as if Rupture were absent.
4. Leave native bodies kinematic and unsupported. Do not restore `NativeWasKinematic` and do not call `PhysicsExtensions.UpdateController.SupportRigidbody` (unlike `AbortToNative`). The consumer strips and recreates bodies itself, so a re-supported dynamic shell would only be undone again. The rig is not destroyed and no build failure is counted.
5. In the coordinator (`RagdollCoordinator.Handoff.cs`):
   - the `RagdollState` is marked held (`IsHeld`), so `ShouldStop` returns the native result unchanged and never calls `ForceSettle`;
   - pose snapshot, wake, force, impulse and settle sends are suppressed for the held corpse (`IsHeld` guards in `RagdollCoordinator.cs`);
   - inbound rules in [Networking](#networking-fika) apply.
6. Gore, wounds, dismemberment and head-wound state stay registered with the corpse. A sever during the hold goes through the normal native route and is reflected in the rebuilt rig at Adopt.

The token is opaque and local. It is bound to the corpse instance, ragdoll instance and death sequence, is never reused across raid resets, and is not a network identity.

`CorpseRagdoll.ClearWeapon()` while released goes through `CorpseWeaponReleasePatch` → `ShadowRagdollWorld.ReleaseWeapon`. That permanently unbinds the weapon from Rupture whatever the handoff state; Adopt does not restore weapon simulation. Consumers should skip `ClearWeapon` when `WeaponIndependent` is true.

### Renew and expiry

`Renew` extends the lease. It returns `Ok` for a queued (not adopt-requested) or held token, and the other codes in [Result codes](#result-codes) for the remaining stages. When a lease expires, Rupture adopts the corpse at its current pose. A lost or crashed consumer therefore leaves a normal Rupture corpse, not a permanent native one. Corpse destruction and raid reset invalidate tokens and dispose of retained actors exactly once.

Expiry is checked at simulation boundaries. After a main-thread stall, the consumer's next `Renew` can still keep the lease. Recommended consumer settings: `LeaseTimeout` 30 s, `Renew` every second.

On a client, local expiry is the only Rupture-initiated adoption. Authority epochs never force-adopt a client that holds an unexpired token (see Networking).

### Re-pickup during the drop window

A token is not tied to a single drag. While it is unexpired and not adopted, the consumer may keep it and keep renewing it for a new pickup of the same corpse, instead of calling `Adopt` and then `Release` again.

- `Release` on a corpse that already has a handoff token returns `Busy`, including when the caller holds that token. Reuse is the only way to re-grab during this window.
- Reusing a token changes nothing on the Rupture side: no rebuild, no epoch change, no packets.
- Before reusing, check `Inspect`. If it reports `Managed`, the lease already expired and Rupture adopted the corpse, so call `Release` for a fresh token instead.
- A final pose from the superseded drag that arrives after the re-pickup belongs to the consumer's own transport. Discard it rather than writing it under the new hold.
- Exactly one `Adopt` closes the token, after the last drag ends.

### Adopt

**Consumer rule:** before Adopt, settle the body, apply the final pose, restore any native collider settings changed during the hold (for example `PhysicMaterial`), then remove all consumer rigidbodies and joints. Rig capture reads the native colliders as they are, so leftover collider changes would enter the rig. Rupture cannot restore them because it does not know the originals.

Adopt does not trust that rule. Rupture performs these steps (`ShadowRagdollWorld.Handoff.cs`, `StepAdoptingHandoff`):

1. Return `Pending`. At a later boundary, if any `Rigidbody`, `CharacterJoint` or other `Joint` remains on spawner bones, strip them and wait until they are gone before rebuilding. Strip attempts are capped at five (`MaximumHandoffStripAttempts`); exceeding the cap fails the adoption.
2. Destroy the released rig's external joints and bodies without restoring native state. The coordinator `RagdollState`, death sequence, sever masks, wound state and detached-effect registry are kept.
3. Recreate native rigidbodies and joints from `_rigidbodySpawners` / `_jointSpawners` at the current bone transforms. Severed bones stay severed (`CaptureSeveredBones` / `AddSeveredBones`).
4. Rebuild with a `TryBuild` variant (`TryRebuildForHandoff`) that bypasses the `_isPhysicsDone` rejection, reapplies permanently disabled and parked bodies, applies no lethal impulse, movement inheritance or angular-resistance drives, and starts settled with zero velocity. A later shot wakes the rig through `TryWakeFromImpact` from the new pose.
5. Rebind the coordinator's identities and the rig's native maps to the recreated objects.
6. On the authority or solo, increment `OwnershipEpoch` and publish a forced pose snapshot, then a reliable settle, stamped with the new epoch (see Networking).

When the rebuild completes, `Inspect` reports `Managed` and the token closes with `Ok`.

If the rebuild fails, the corpse stays native and `Inspect` reports `Unmanaged`. That is a valid end state, not the abort path: no failure counter, no raid-level fallback, one info log.

After Adopt, the consumer must not strip or reconfigure the native rigidbodies or joints. Rupture owns them as the native query shell; without it, later bullets cannot find the body.

### Result codes

Codes returned by the token methods, from the stage the token is in (`ShadowRagdollWorld.Handoff.cs`):

| Call | Token stage | Result |
| --- | --- | --- |
| `Renew` | Queued, not adopt-requested | `Ok` |
| `Renew` | Queued, adopt already requested | `Stale` |
| `Renew` | Held | `Ok` |
| `Renew` | Failed | `Failed` |
| `Renew` | Adopting, Closed, Cancelled | `Stale` |
| `Adopt` | Queued | `Pending`; a queued release that has not reached the boundary is closed at the next boundary without being released to the coordinator |
| `Adopt` | Held | `Pending`; moves to Adopting |
| `Adopt` | Adopting | `Pending` |
| `Adopt` | Closed | `Ok` (repeat adopt, or expiry already adopted it) |
| `Adopt` | Failed | `Failed` (rebuild failed or the lease was otherwise invalidated) |
| `Adopt` | Cancelled | `Stale` |
| either | Retired (terminal record pruned) or unknown | `InvalidToken` |

Terminal records are retained for `CorpseHandoffPolicy.TerminalRecordSeconds` before pruning, so a repeat call within that window still sees the terminal result.

## Networking (Fika)

Every ragdoll packet that targets a corpse carries `OwnershipEpoch`. These are the seven ragdoll packets (`Ragdolls/Classes/Packets` in core, `Rupture.Fika/Ragdolls/Classes/Packets` in the adapter): force, point impulse, pose snapshot, radial impulse, settle, start and wake. The epoch sits after `DeathSequence` in each. The impact-state packet path (`FikaImpactNetwork`) is unchanged apart from the protocol number.

The authority stamps it. Only the authority increments it, on each authority Adopt, including expiry adoption. It starts at 0 for a new death sequence and skips 0 when wrapping (`NextOwnershipEpoch`).

Client rules (`ClassifyIncomingEpoch` and `CorpseHandoffPolicy.ClassifyEpoch`):

- Track the highest epoch seen per corpse.
- **Incoming lower than local:** drop as stale.
- **Incoming equal to local:** handled normally when managed; retained like other held packets while held.
- **Incoming higher than local, corpse released (client holds an unexpired token):** keep only the newest pose snapshot and the newest settle for that corpse, and record the highest epoch seen (`HeldHighestEpoch`). Do not adopt. The client adopts on its own `Adopt` or its own lease expiry, takes the highest epoch seen, then replays the retained settle and snapshot. An authority epoch only proves the authority adopted, which may be an expiry during a live hold, so it must not destroy a client's live drag bodies.
- **Incoming higher than local, corpse managed:** adopt the epoch and apply the packet normally. A full-state snapshot re-anchors playback.
- **While released:** drop wake, force, point and radial impulse events for the current epoch. Only the newest snapshot and settle are retained; they replace the effect of the earlier events.
- **Start packets while held:** still sent and still recorded. The authority announces a held corpse with no launch velocity (`hasInitialVelocity: false`), and the client's start handling does not check the held flag.

Authority adoption publishes a forced pose snapshot, then a reliable settle, both under the new epoch. Pose snapshot chunks are unreliable and ordered by snapshot sequence; the settle is `ReliableOrdered`. Clients never increment the epoch themselves.

A retired client (its ragdoll tombstone exists for the same death sequence) accepts a newer-epoch settle or snapshot of that death. `TryRestoreRetiredForNewerEpoch` applies only when the incoming epoch is `NewerAdopt` relative to the tombstone epoch. Equal or lower epochs stay stale.

A missed local Adopt is recovered by the client's lease expiry and the next authority packet; it cannot desynchronize the epoch permanently. Late joiners need no special handling: Rupture has no historical corpse replay ([ARCHITECTURE-MULTIPLAYER.md](ARCHITECTURE-MULTIPLAYER.md)).

The epoch change is a wire change. Impact protocol is 16 (`FikaImpactNetwork.Protocol`). Peers must match exactly, and a mismatch is rejected with the version message.

Rejected alternative: modelling adoption as a new death sequence with a zero-impulse start. It conflates identity with ownership, re-runs death-start paths and breaks event deduplication for wounds and dismemberment keyed by death sequence.

The consumer keeps its own pickup, drop and final-pose transport; Rupture does not add one.

## Late or missing final pose

There is no `Repose`. The consumer delays `Adopt` until its final pose arrives or its own timeout passes, then adopts at the current bones. Clients converge through the authority:

- the authority's Adopt publishes a forced pose snapshot under the new epoch;
- a client plays back that snapshot after its own adoption completes (its own Adopt, or its own lease expiry).

The remaining divergence is on the authority itself. A headless authority does not simulate the hold, so if it times out without a final pose it adopts at the pickup pose, and every client follows it back there. The consumer must give headless a longer wait than graphical peers, or have the dropping peer resend the final pose.

If the authority's own lease expires during a genuine hold, the authority adopts at its current bones while clients keep dragging. Clients converge to the authority's pose when they adopt. The consumer's later authority `Adopt` returns `Ok` without rebuilding, so a final pose applied there after expiry must not be written to the native bones of the now managed corpse; the consumer should log and drop it.

## Consumer flow

1. Resolve the provider. If Rupture is absent, or `Inspect` returns `NotManaged`, an independent native route is allowed.
2. Optionally wait for `Simulating == false` before allowing pickup.
3. On the pickup event, on every peer (headless included): call `Release`. On `Busy`, refuse the pickup. On `Pending`, hold the pickup until `Inspect` reports `Released`. Then create rigidbodies and joints from the spawners, skipping `SeveredRoots`. Refuse bodies that cannot be gripped. Skip `ClearWeapon` when `WeaponIndependent` is true.
4. Run the Unity drag. `Renew` every second with a 30 s `LeaseTimeout`, on every peer including headless.
5. On drop, on every peer, including a non-simulating headless: settle, apply the final pose (or time out; headless waits longer than the dropper's settle plus send window), restore changed collider settings, remove all consumer rigidbodies and joints, then call `Adopt`.
6. Pair every token with exactly one `Adopt` on every exit path: refused claim, dragger dead or gone, corpse destroyed, failed take. A re-pickup during the drop window reuses the existing token. Treat an `Adopt` result of `Ok` on an already adopted token as success.
7. After adoption, never strip or reconfigure native rigidbodies, joints or colliders; Rupture owns culling interaction and later physics.

## Interactions

- `CorpseDragV1` and handoff are mutually exclusive per corpse. `CorpseDragV1.Begin` returns `Busy` while a handoff token holds the corpse, and `Release` returns `Busy` while a drag lease holds it.
- Debug freeze (`FreezeCorpseRagdollsForWoundDebug`) reports `Unavailable`.
- Living bots and Fika-downed `ObservedPlayer` ragdolls are out of scope, as with `CorpseDragV1`.
- [INFERENCE] Moving consumer bodies may still trigger moving-environment wakes of other managed corpses they overlap. That is acceptable, but the released corpse's own native bodies must not wake its own rig.

## Source map

| Area | Source |
| --- | --- |
| Public surface, DTOs, main-thread guard | `src/Rupture/Integration/CorpseHandoffV1.cs` |
| Stage policy, lease timeout, epoch classification | `src/Rupture/Integration/CorpseHandoffPolicy.cs` |
| Released rig state, quiesce, strip/rebuild, finish, invalidation | `src/Rupture/Ragdolls/Classes/ShadowRagdollWorld.Handoff.cs` |
| Held guards on wake/impulse/settle/present paths | `src/Rupture/Ragdolls/Classes/ShadowRagdollWorld*.cs` |
| Epoch classification, retention, adoption epoch, queue end, tombstone revival | `src/Rupture/Ragdolls/Classes/RagdollCoordinator.Handoff.cs` |
| Held guards on send/receive, start while held, `ShouldStop` passthrough | `src/Rupture/Ragdolls/Classes/RagdollCoordinator.cs` |
| Ragdoll packets with `OwnershipEpoch` | `src/Rupture/Ragdolls/Classes/Packets/*.cs`, `src/Rupture.Fika/Ragdolls/Classes/Packets/*.cs` |
| Protocol 16 and release 1.4.0 | `src/Rupture.Fika/Impacts/FikaImpactNetwork.cs` |

## Validation

Managed regression coverage: [CorpseHandoffAbiTests.cs](../tests/Rupture.RegressionTests/CorpseHandoffAbiTests.cs) covers the ABI surface and handoff policy. [PacketCompatibilityTests.cs](../tests/Rupture.RegressionTests/PacketCompatibilityTests.cs) covers the epoch on the ragdoll packets and protocol behaviour. Compilation and these tests do not establish integrated EFT/Fika behaviour.

The live scenarios below are separate acceptance requirements and are outstanding.

| Scenario | Required observation |
| --- | --- |
| Release a settled, dormant or active corpse | Native bodies are left kinematic and unsupported; no Rupture pose writes, wakes, settles or packets for the corpse; external actors do not collide; no failure counter or warning. |
| `Simulating` | True during the fall and authority playback; false after settle on authority and clients; false on a client within 2 s of authority silence when the settle packet is lost. |
| Release timing | Managed corpse reaches `Released` at the next simulation boundary; a preparing corpse queues and completes after capture or reports `Failed`. |
| Main-thread hitch past the lease deadline | Consumer's next `Renew` keeps the lease; no adoption mid-drag. |
| Shots and explosions while released | Native/Unity response only; neighbouring Rupture corpses do not wake the released rig. |
| Adopt after moving the corpse | Rig rebuilt at the moved pose with spawner tuning, not consumer tuning; a later shot moves it from there with no snap to the death pose. |
| Consumer leaves bodies or joints before Adopt; same-frame Destroy | Rupture strips them, waits for destruction, recreates from spawners, and the corpse stays shootable. |
| Re-pickup during the drop window | Existing token reused and renewed; no rebuild, epoch change or packets; `Release` with a held token returns `Busy`; superseded final pose discarded. |
| Severed corpse; sever during hold | Removed limbs stay removed after Adopt; no revived joints or parked bodies. |
| `ClearWeapon` while released | Weapon unbound permanently; Adopt succeeds without weapon simulation. |
| Lease expiry, raid reset, corpse destruction | Expiry adopts; the others dispose once; stale tokens return `InvalidToken`; `Adopt` after expiry returns `Ok`. |
| Collider material changed during hold | Restored by the consumer before Adopt; rebuilt rig uses native materials. |
| Final pose late or missing | Graphical clients converge to the authority snapshot; headless timeout behaviour matches the documented limitation. |
| Fika host drag, client drag, headless host | Epoch is authority-stamped only; a client holding an unexpired token is never force-adopted and plays back the retained snapshot after its own adoption; stale epochs are dropped; a missed local Adopt recovers through expiry. |
| Authority lease expires during a live hold | Clients keep dragging; authority `Adopt` returns `Ok`; clients converge to the authority pose on their own adoption. |
| Mixed Rupture versions | Protocol 16 mismatch is rejected by the existing version check. |
| Rupture absent or feature off | Consumer native route unaffected. |
