# Corpse control API V1

Rupture exposes optional authority-side control of its managed corpse physics through `Rupture.Integration.CorpseDragV1`. API version is **1**; native physics ABI is separately **10**. This is the implemented contract, not a proposal to replace corpse ownership. A mod such as BodyDragging can consume it through a compatible adapter; installing Rupture alone does not provide another mod's interaction or transport.

## Ownership and authority

Rupture owns the physical grip, corpse actors, simulation cadence, presentation, settlement and authoritative motion. The consumer owns UI/range rules, camera-relative targets, player movement/hands handling, assistance calculations and explicit recovery decisions. One exclusive lease prevents settlement while held; it does not transfer actors to Unity. The hand/tether are separate from network core indices, weapons and detached effects.

Calls that drive physics belong on the solo instance or Fika host/headless authority. Clients need consumer-owned claim/intent transport to the authority; Rupture does not provide a client-to-host dragging interaction channel. Managed corpses retain Rupture's existing motion stream. Do not run a second remote bone follower or write native shell poses alongside it.

Only actual `Corpse` owners are supported. Living bots and Fika-downed `ObservedPlayer` ragdolls are outside this API. Missing shadow construction alone does not authorize a native route: deferred/reserved or unavailable managed ownership must be handled explicitly. Rupture absent permits an independent native route; an installed provider without V1 is not equivalent to absence.

## Public managed declarations

The public type lives in `Rupture.dll`, namespace `Rupture.Integration`. Discover the plugin through BepInEx GUID `com.rootdarkarchon.rupture`, then resolve this exact type/version through an optional adapter. The consumer must remain loadable without Rupture. DTOs below are public managed types, not native interop structures or network packets. Their names and enum values form the consumer contract; opaque lease values and body indices are never PhysX pointers. The exact source is [CorpseDragV1.cs](../src/Rupture/Integration/CorpseDragV1.cs).

```csharp
public static class CorpseDragV1
{
    public static int AbiVersion { get; } // exactly 1
    public static DragResult Inspect(Corpse corpse, out CorpseControlInfo info);
    public static DragResult Begin(Corpse corpse, DragBegin request, out ulong lease);
    public static DragResult Read(ulong lease, out DragView view);
    public static DragResult Submit(ulong lease, DragFrame frame);
    public static DragResult End(ulong lease, DragEndReason reason);
}

public enum DragResult
{
    Ok = 0, Pending = 1, Closed = 2, NotManaged = 3,
    NotAuthority = 4, Busy = 5, InvalidArgument = 6,
    InvalidLease = 7, Unsupported = 8, Failed = 9, Stale = 10
}
public enum CorpseControlState { Unmanaged = 0, Preparing = 1, Managed = 2, Unavailable = 3 }
public enum DragStage { Preparing = 0, Held = 1, Releasing = 2, Closed = 3, Failed = 4 }
public enum DragEndReason { Released = 0, Cancelled = 1, LostTarget = 2, Disconnected = 3 }
[Flags]
public enum DragCapabilities
{
    None = 0, RadialTether = 1, BodyAcceleration = 2, RigTranslation = 4
}

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
    public Rigidbody PreferredBody; // identity selector only; null selects surviving chest
    public float GripSlack;         // metres; finite, > 0
    public float MaxGripSpeed;      // metres/second; finite, > 0
    public float LeaseTimeout;      // unscaled seconds; finite, range [0.5, 10]
}
public sealed class DragView
{
    public DragStage Stage;
    public ulong CompletedStep;
    public float SimulationDeltaTime; // seconds for CompletedStep
    public uint TopologyRevision;
    public int GripBodyIndex;
    public Vector3 GripPosition;    // actual hand position, not the requested target
    public uint LastTranslationId; // last successfully applied one-shot translation
    public DragBodyView[] Bodies;   // copied snapshot; original core-spawner order
}
public struct DragBodyView
{
    public int BodyIndex;
    public Rigidbody NativeBody;    // optional identity; null after EFT rigidbody cleanup
    public Transform NativeTransform; // retained bone identity/metadata; read-only to consumer
    public bool Eligible;
    public bool IsSleeping;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 WorldCenterOfMass;
    public Vector3 Velocity;
    public Vector3 AngularVelocity; // radians/second
}
public sealed class DragFrame
{
    public uint Sequence;
    public uint TopologyRevision;
    public Vector3 GripTarget;      // absolute world point
    public DragAcceleration[] Accelerations; // world acceleration at each body's COM
    public uint TranslationId;     // 0 = none; otherwise a new monotonically increasing id
    public Vector3 RigTranslation; // one-shot world offset; paired with TranslationId
}
public struct DragAcceleration
{
    public int BodyIndex;
    public Vector3 Acceleration;    // metres/second squared; no caller dt multiplier
}
```

All methods run on the Unity main thread, including calls marshalled from a network handler. A call from another thread returns `InvalidArgument` without touching Unity. Expected failures return results, not exceptions. Arrays/DTOs are copied on submit/read; they are not mutable views of a solver. A V1 provider advertises all three baseline capabilities only when those operations are implemented and usable. A future extension must not reinterpret existing fields or enum values.

### Acquisition and reads

`Inspect` is read-only and works on every peer. `NotManaged` is safe for a native route only when Rupture has neither a rig nor coordinator ownership/reservation for this corpse. A missing rig alone is insufficient. `Preparing` includes deferred rig capture. `Unavailable` includes backend failure, unsupported fallback and debug-freeze incompatibility; it does not authorize native takeover.

On networked peers, an unassigned death sequence is unavailable for acquisition until the exact corpse/ragdoll receives its authoritative identity. Rupture promotes only an unassigned rig with no active lease; it never rebinds an assigned rig or lease.

`Begin` checks authority, corpse identity/lifetime, backend health, settings and exclusive ownership. A specific preferred body must belong to the core rig and be eligible; invalid selection is not silently replaced. Null selects an eligible chest and fails if none exists. Successful acceptance returns a nonzero opaque lease and `Pending` while work waits for a safe simulation boundary. A rejection returns lease zero and makes no changes. The lease is bound internally to this corpse instance, ragdoll instance and death sequence; its token is never reused across raid resets. It cannot attach to a replacement corpse with the same profile. The timeout range is 0.5 to 10 unscaled seconds, measured from acquisition and then each accepted frame; preparation must complete within that same window. Values outside the range are rejected, not silently clamped.

`Read` returns `Pending` with a preparing/releasing view during those phases, `Ok` with a held view when ready, and `Closed` or `Failed` at completion. Terminal records are retained for 30 unscaled seconds, then pruned at a simulation boundary; raid reset invalidates them immediately. Subsequently a retired handle returns `InvalidLease`. Held reads report the last **completed** external step and its duration in `SimulationDeltaTime`; repeated reads need not advance it. Consumers can use that duration to stabilize acceleration calculations. Body indices remain fixed even for missing or disabled entries. `TopologyRevision` advances when eligibility changes. Reads do not extend the lease. The consumer may use these velocities for its calm-wait policy; reading native kinematic shells would give a false result. Native rigidbody cleanup alone does not remove a surviving external actor from eligibility: `NativeBody` can be null while `NativeTransform` retains bone identity. Null preferred-body selection uses chest metadata captured when the rig was built, without depending on surviving native spawners. Read transforms only for identity/metadata; use snapshot fields for simulated state.

### Target, assistance and recovery

`Submit` accepts only a held lease, a fresh sequence and the current topology revision. Sequences start at 1 and strictly increase within a lease; duplicate/older sequences and outdated topology return `Stale`. Reject the entire frame on invalid/non-finite input, duplicate body indices, invalid body selection or unsupported operations. Null/empty acceleration arrays mean no assistance. `Ok` means accepted for the next safe step, not already simulated. An accepted frame refreshes the lease timeout.

The newest accepted target and acceleration set persist until replaced or the lease expires. Move the hand toward the target by at most `MaxGripSpeed * simulationDeltaTime` per step. The grip starts at the selected body's actual COM, with zero initial tether error. It limits radial separation to `GripSlack` and leaves all angular motion free. It is a solver constraint, not a hidden replacement spring applied to the chest.

Apply each assistance acceleration once per simulation step at the relevant body's COM, using `AddForce(..., Acceleration)`. Do not replay each rendered frame as an additional force, multiply acceleration by caller delta time, or send a second Rupture force event for the same submitted assistance. The consumer owns the acceleration calculation and any damping/caps in it. Ordinary Rupture impact/explosion impulses still reach the actual actors through their existing routes.

Translations are explicit recovery commands. Translate all eligible core bodies and the grip by the same offset, preserve their relative pose, clear core linear/angular velocity, update parked bodies through their retained anchors, and refresh environment coverage at the destination before stepping. Weapons and detached effects are not translated. A target jump alone does not imply teleportation.

Apply each nonzero `TranslationId` exactly once. Keep at most one unapplied translation per lease: repeated submission of that same id is not another relocation; a different translation returns `Busy` until the previous one is applied. Expose completion through `LastTranslationId`. Target-only coalescing must not discard or replay a recovery command. If environment acquisition or application fails, report failure and release safely rather than claim the translation succeeded. Recheck eligibility at consumption; never drive a newly severed body from an older accepted frame.

### Release and expiration

`End` cancels future drives immediately and queues grip destruction at a safe boundary. Accepted release returns `Pending`. Repeated `End` during release is harmless; a known closed handle returns `Closed`, a retired/foreign handle `InvalidLease`. A preparing lease can also be cancelled. An implementation must not require a final pose from the caller: the last completed Rupture state is authoritative.

After completing outstanding simulation, destroy the tether before its hand actor, clamp surviving core release velocities to the release limits (2 m/s linear, 4 rad/s angular), reset the coordinator's motion/settlement window from release time, and allow normal bounded Rupture settlement. Retain corpse identity, sever masks, effect state and weapon simulation. Publish the release transition and final/current pose through Rupture's normal authority stream. Do not resurrect already removed joints or disabled bodies.

Unscaled lease expiration follows the same release path. Corpse destruction, raid reset, disconnect and backend failure invalidate handles and dispose grip resources exactly once. Expiration removes the hold; it does not permanently exempt the corpse from settlement. Preparation is also bounded, so a lost caller cannot leave a pending corpse reserved forever. Normal backend failure remains Rupture's fallback responsibility; the consumer stops its managed route after failure instead of mutating a half-restored rig.

## Consumer session flow

1. Discover the optional provider and inspect the exact corpse. Keep a failed/denied claim separate from physical engagement. Treat `Preparing` and `Unavailable` as retained Rupture ownership, not permission to enable native dynamics.
2. Acquire on the authority. Keep a nonzero pending lease and poll readiness. Use actual completed velocities for any calm-wait or assistance policy; native shell velocities are not solver state.
3. Once held, read eligibility/topology and submit fresh sequence/target frames. Continue refreshing a stationary target. On stale topology read again; on failure or grip loss unwind the interaction.
4. Give each recovery translation a separate ID, await `LastTranslationId`, and never translate weapons/effects or treat an ordinary target jump as teleportation.
5. End on release, denial, disconnect, corpse/raid change or consumer failure. Preparing leases can be cancelled; the provider timeout recovers a lost end. Restore consumer-owned player/UI state separately.

Network claims must bind the current holder and a session to exact profile/death/corpse identity. The authority's lease handle is local and is not a portable network token. Validate the holder on updates and end, reject old sessions, and marshal callbacks to Unity main. Keep the claim until queued release completes. Any separate consumer input watchdog is consumer policy; V1 itself uses the requested lease timeout.

Do not call EFT ragdoll startup, change native joint/support state, add a native hand/tether, make shells dynamic, or apply native pose/trigger corrections while the corpse is managed. Those writes compete with presentation or fail to reach captured external actors. Existing hit impulses, gore, weapons and effects retain their Rupture lifetimes throughout the hold.

## Supported operations and limits

V1 supports a radial positional tether, COM body acceleration and explicit common core translation. It provides no arbitrary joint setters, per-limb pose repairs, contact suppression, solver-iteration overrides or off-centre acceleration grip. The radial constraint does not promise numerical parity with Unity `ConfigurableJoint` projection/preprocessing. Do not reinterpret public fields/enums or backend character-joint layouts to add an operation.

Consumer acceleration/damping should use completed solver duration and actual snapshot state, with bounded calculations appropriate to the consumer. A render-frame spring replay is not equivalent to applying accepted acceleration once per solver step. Native fallback remains Rupture's failure recovery, not a normal lease acquisition/release technique.

## Validation

| Scenario | Required observation |
| --- | --- |
| Fresh, dormant and terminally settled corpses; pending build | Acquire once, start at actual pose, no native dynamic takeover or death impulse replay. |
| Drag beyond the configured ragdoll deadline | No forced stop; release re-enters bounded settlement. |
| Slow pulls, corners, stairs and terrain snag | Tether remains usable; establish whether joint overrides/repair/contact extensions are actually required. |
| Vault/teleport recovery | One translation per id, complete core pose preserved, environment ready, weapon/effects retained. |
| Corpse piles, moving environment, gunshot/explosion during hold | Existing external contact, impact and gore handling continues; assistance is not applied twice. |
| Severed corpse and new severing during hold | No parked-body revival; topology changes visible; removing grip releases cleanly. |
| Host dragging, client dragging, headless host | Accepted client intent drives the host solver; one motion stream; settled remote corpses wake and follow. |
| Denial, disconnect, lost end, raid exit, backend failure | No resource leaks or indefinite hold; invalid handles cannot drive a later corpse. |
| Rupture absent/unavailable; future downed support | Native route remains independent when absent; unavailable managed ownership is handled explicitly; downed behavior is not claimed by this ABI. |

Managed [ABI regressions](../tests/Rupture.RegressionTests/CorpseDragAbiTests.cs) exercise signatures, copied frame validation, sequence/translation deduplication, timeout boundaries, release ordering and retained bone selection. Native [tether fixtures](../src/Rupture.PhysX.Native/tests/rpt_corpse_drag_tests.inl) exercise the shape-free hand, free rotation, translation and mass-independent acceleration. The live scenarios above remain separate acceptance requirements; compilation and fixtures do not establish integrated EFT/Fika behavior or another mod's published adapter availability.

Use [development setup](DEVELOPMENT.md) for current verification commands and the [physics ownership contract](RPT-External-PhysX-Architecture.md) before changing simulation/lifecycle boundaries.
