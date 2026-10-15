# Standalone corpse physics

`ShadowRagdollWorld` coordinates temporary corpse dynamics through `IExternalPhysicsRuntime`. EFT's native corpse hierarchy remains the gameplay/query and presentation shell. A separate PhysX scene owns integration and contacts after a rig commits. The [ownership contract](RPT-External-PhysX-Architecture.md) records the core invariants; [multiplayer](ARCHITECTURE-MULTIPLAYER.md) explains authority playback.

## Boundaries and version contract

| Boundary | Implementation / contract |
| --- | --- |
| Core → managed backend | [ExternalPhysicsContracts](../src/Rupture/Ragdolls/External/ExternalPhysicsContracts.cs), loaded by [ExternalPhysicsBackendLoader](../src/Rupture/Ragdolls/External/ExternalPhysicsBackendLoader.cs) from the plugin directory. |
| Managed backend → native DLL | [NativeApi](../src/Rupture.PhysXBackend/NativeApi.cs): explicit local DLL loading, Cdecl delegates and sequential interop structures. |
| Native API | [rpt_physx_bridge.h](../src/Rupture.PhysX.Native/include/rpt_physx_bridge.h) and [implementation](../src/Rupture.PhysX.Native/src/rpt_physx_bridge.cpp). |
| Pinned SDK | PhysX **4.1.2**, numeric version **`0x04010200`**, source revision **`a2c0428acab643e60618c681b501e86f7fd558cc`**. |
| Bridge ABI | **10** in the header, managed loader and [backend manifest](../src/Rupture.PhysXBackend/physx-backend.json). Windows x64, static CRT build. |
| Unity extraction ABI | `UnityPhysXMeshGeometrySource` targets **Unity 2022.3.43f1** and validates the required native/private layout and entry points. |

The manifest is package metadata, not a substitute for runtime native export/ABI validation. Do not change native structures, handle semantics or exported signatures independently of the managed declarations, ABI and tests. The standalone SDK owns only its own objects; pointers borrowed from Unity are never released by this bridge.

The same DLL also packages the independent `rptpx_lzma_decode_v1` clothing decoder. Its additive versioned export does not change physics ABI 10 or require a scene; the clothing loader owns a separate process-lifetime library reference. See the [external LZMA decision](ARCHITECTURE.md#external-lzma-decoder-decision) for codec ownership and fallback behavior.

## Why isolate corpse simulation

Historical EFT captures showed that retained corpses could remain registered after visible settling, keeping default-scene simulation active, while `Physics.SyncTransforms` had measurable self cost in those captures. The [historical measurements](PERFORMANCE.md#historical-native-physics-captures) were uncontrolled AI runs, so they do not quantify an isolated corpse cost or a current FPS improvement. Standalone dynamics still require Unity capture, environment cooking and visible-shell synchronization; unrelated supported objects can continue to keep EFT simulation active.

The implemented [corpse-control API V1](CORPSE-CONTROL-API.md) permits authority-owned consumer interaction without transferring solver ownership. Preserve its public declarations, topology/lease semantics and native ABI alongside the [frozen ownership contract](RPT-External-PhysX-Architecture.md).

## Rig construction and ownership transfer

[ShadowRagdollWorld](../src/Rupture/Ragdolls/Classes/ShadowRagdollWorld.cs) defers capture until a frame after native `EFT.Interactive.CorpseRagdoll.Start`. It snapshots actual bodies, collision state, support-list registration counts, joints, poses and velocities. Weapon attachment and pending sever-collider removal must finish consistently with this capture. Body capture includes disabled colliders as disabled shapes, except MeshColliders that Unity itself simulates no native shape for (no mesh, inactive equipment LODs, rejected shapes): those carry no native contact and are omitted, logged once per collider name, rather than rejecting the rig. A body left with no simulated shape still fails. A build scheduled by a native restart whose ragdoll stopped again before capture is dropped without a failure warning.

Construction creates external actors/joints and validates required environment before changing native ownership. Weapons use an independent dynamic body with rounded-box geometry; there is no corpse-hand joint. Native-to-shadow dictionaries provide direct force routing through [IShadowBodyProxy / ExternalShadowBodyProxy](../src/Rupture/Ragdolls/Classes/ShadowBodyProxy.cs).

Only a complete rig commits. Then native supported bodies/weapon are unregistered from EFT's simulation support list and made kinematic while their colliders, transforms, loot and components remain available. Partial construction destroys staged proxies and leaves native simulation active. Committed failure copies the latest proxy state back, restores native collision/support bookkeeping and sleep hierarchy, then retires the external rig.

`ShadowRagdollWorld` owns rig associations, native bindings and presentation-shell restoration. [ExternalPhysXRuntime](../src/Rupture.PhysXBackend/ExternalPhysXRuntime.cs) owns its managed body/joint wrappers and work queues. [NativeScene](../src/Rupture.PhysXBackend/NativeScene.cs) owns the native scene and its actor/material/cooked-mesh caches. The bridge's scene context owns native actor/shape/joint/mesh handles and resolves them through its handle tables. Handles are opaque, scene-bound identifiers with stale-handle checks, not pointers a caller may dispose arbitrarily. Release joints before their actors and actors before referenced private geometry; shared scene geometry outlives its users.

[ExternalPhysicsHandles](../src/Rupture.PhysXBackend/ExternalPhysicsHandles.cs) contains the managed body/joint state caches and command forwarding. They do not destroy native resources. `ExternalPhysXRuntime.RegisterBody` registers body and collider lookup entries on the consuming thread after synchronous creation or a successful transient-ticket take; the ticket's `OwnedResult` remains the asynchronous ownership boundary.

Synchronous body creation captures Unity center-of-mass on main before invoking the owner. Compound weapon collider identities are bound on main after native creation returns. The owner callback consumes managed snapshots and returns native handle data; it does not query those Unity objects.

## Owner thread and publication

```mermaid
sequenceDiagram
    participant Main as Unity main thread
    participant Owner as Scene-owner thread
    participant Native as Standalone PhysX
    Main->>Main: Capture Unity state into managed snapshots
    Main->>Owner: Enqueue work / SubmitStep
    Owner->>Native: Mutate scene; simulate and fetch results
    Native-->>Owner: Body states and break events
    Owner-->>Main: Complete work / publish snapshot
    Main->>Main: Apply body states and visible poses
```

The owner is a below-normal background thread using a queue and wake event. Work completion uses task completion sources with asynchronous continuations. Explicit command failures propagate through their work result; failures in queued operations latch owner failure for subsequent health checks. Native callbacks publish data rather than manipulating Unity objects.

Threading rule: all standalone scene construction, mutation, stepping and destruction run on the owner thread. Unity discovery/capture and wrapper materialization run on the main thread. PhysX dispatcher workers are internal to that scene; the configurable count (0–4, default 2) does not permit multiple callers to mutate it. At zero, PhysX tasks execute on the owner thread.

Ordinary low-frequency body/joint/environment operations use synchronous owner invocations with a 500 ms watchdog. This is an accepted blocking boundary, not an entirely asynchronous backend. `SubmitStep` permits only one in-flight step. `CompleteStep` publishes finished body state into managed wrappers; joint-break publication uses thread-safe state.

### Transient convex creation

The limb gib path uses a separate asynchronous ticket. Main-thread capture produces a convex snapshot; owner work creates the actor and attempts to publish it into [OwnedResult](../src/Rupture.PhysXBackend/OwnedResult.cs). Polling takes the completed result and materializes its managed wrapper. The successful take closes ticket ownership and transfers responsibility to the caller/runtime body lifecycle.

Cancellation also closes/takes that publication slot. If an actor already arrived, it is queued for owner-thread destruction. If it arrives after closure, publication fails and the producer destroys it on the owner thread. There is exactly one owner at every boundary. Cancelling a ticket must not leak a late actor or destroy one already handed to its caller. Transient convex meshes are private to their actor and bypass the disk/shared geometry cache.

## Frame cadence and pose synchronization

`ShadowRagdollWorld.Update` performs lifecycle, environment and pending-build work. Simulation is driven by `LateUpdate` on both graphical and headless peers:

1. Complete the previous external step, with the 500 ms watchdog.
2. Apply pending authority state/playback corrections to the completed scene state.
3. Submit the next eligible step and return while the owner executes it.

Completed results update proxy caches, native shells, sleep state and authority snapshot publication. A committed bootstrap rig can publish an initial authority pose immediately, without waiting for the next scheduled simulation tick.

Graphical peers use the full unscaled frame delta. Headless peers accumulate `Time.deltaTime` and run at most approximately 30 eligible steps per second; a slow frame uses its accumulated larger delta. This is not a universal 1/30-second clamp and not an `Update`-versus-`LateUpdate` split between roles. [GraphicalSimulationPolicy and HeadlessSimulationPolicy](../src/Rupture/Ragdolls/Classes/SimulationStepPolicy.cs) define the cadence.

[ShadowVisiblePosePlayerLoop](../src/Rupture/Ragdolls/Classes/ShadowVisiblePosePlayerLoop.cs) reasserts native visible poses after the targeted late graphics player-loop stage. It does not submit or complete another simulation step. Client authority playback sets complete body states kinematically; it is separate from locally integrated prediction. Do not add a late pose fix that covertly advances the scene twice.

## Impulses, sleep and wake

`BodiesImpulsePatch`, `GrenadeDeadBodiesPatch`, `ShotImpactProcessor` and the six managed Rigidbody force/torque wrappers feed core routing. Native collider hits resolve the correct corpse/body; point inputs retain body-local coordinates where required. Bullet policy uses captured projectile momentum, and explosion policy uses the native explosion event. Independent weapons are not mistaken for anatomical limb hits. Prediction bookkeeping suppresses the matching authority echo rather than applying the same impulse twice.

A rig can become dormant after completed sleeping steps, before its maximum configured dynamic lifetime. Dormancy makes core bodies kinematic but is not terminal retirement. Moving environment mechanisms or explicit authorized impacts can reactivate it; ragdoll-to-ragdoll contact alone does not grant wake authority. Final settle parks the core; an independent weapon can have its own remaining dynamic lifetime.

With `WakeCorpsesOnImpact`, retained native query shells remain eligible for later bullets/explosions and connected resting corpses can wake through actual contact bounds. Reactivation establishes another bounded motion epoch and sends an ordered wake event. Settled coordinator state is parked outside ordinary active scans but remains identity-addressable. Without this retention policy, native terminal cleanup is allowed. A remote client does not decide final settle from its own wall clock or sleep observation; reliable authority state decides it.

## Environment extraction and caching

[MeshGeometrySources](../src/Rupture.PhysXBackend/MeshGeometrySources.cs) captures Unity's already-cooked triangle/convex topology and native heightfield data, including holes, flags, scales and material indices. Primitive shape descriptions come from supported Unity colliders. This is a read of Unity-owned geometry into managed snapshots for independent standalone cooking; it is not a second simulation of Unity's scene.

Bootstrap validates Unity native layout, observes a probe collision to capture actor context, resolves the source scene/settings on the main thread and removes the probe subscription. Contact callback work is limited to capturing synchronized data. Readiness warns after its wait threshold but has no hard overall readiness timeout. Bootstrap/map refresh can call `Physics.SyncTransforms`; do not infer that this backend never invokes it.

Environment selection follows the relevant collision layers and supported nontrigger box, sphere, capsule, mesh and terrain shapes, excluding player/corpse/effect geometry. Inactive/unready shapes can be deferred. Required eligible active geometry that cannot be captured prevents a safe commit or triggers fallback; terrain cannot be silently omitted. The selection filter does not promise that every other Unity collider class is supported or diagnosed as active geometry.

| Mode | Work and ownership |
| --- | --- |
| `ProximityCache` | Captures near each consumer, including a bounded velocity sweep. Shared references retain active geometry; unused static entries are disabled and retained in a 2,048-entry LRU. Moving mechanisms update kinematically. Bounded audits also detect static/topology changes. |
| `PrebakeEntireRaid` | Waits for loaded scenes to stabilize for two frames, captures on main, builds a staged environment on owner, and atomically swaps it into use. Failure discards staging. Scene additions use the same complete capture/commit principle; proximity safety handling covers runtime additions. |

Both paths use the same geometry capture/cooking rules. A scale/topology replacement is prepared before replacing the old actor. Capture, cooking and whole-raid staging can block during their low-frequency owner invocations and remain subject to watchdog failure; “prebake” is not a promise of zero loading stalls.

[MeshGeometryRepository](../src/Rupture.PhysXBackend/MeshGeometry.cs) caches captured geometry for the raid. `NativeScene` caches standalone cooked resources and can persist `.pxc` data using keys that include build/geometry/cooking inputs. Heightfields include their native samples and metadata. Unity pointers are borrowed only during capture, not retained as owned external mesh resources.

## Teardown, quarantine and cost

`ResetRaid` completes outstanding simulation before owner-thread scene destruction and clears wrapper, extraction and environment state. An in-flight step has a 500 ms reset watchdog. Timeout quarantines that runtime instance: further normal work cannot safely access its scene. `Dispose` signals the owner and waits up to 1,000 ms for it to stop; native unloading is allowed only after it has stopped. If it is still running, the native module is retained rather than unloaded beneath executing code. The eventual owner exit cleans its scene and faults abandoned work. The core loader clears its runtime reference on shutdown/reset; this is not evidence that a timed-out native operation was forcibly terminated, nor that an `Assembly.LoadFrom` assembly was unloaded from the CLR.

Performance-sensitive costs are main-thread Unity snapshot/bake work, synchronous owner waits, standalone cooking/cache misses, pose publication and native kinematic-shell updates. Stepping overlaps frames but may still be waited upon at the next eligible callback. Track main-thread work, wait time, owner simulation and unrelated default-scene cost separately. Native actor isolation alone does not establish a measured FPS improvement.

Validate ABI/load failure, partial builds, terrain holes, moving mechanisms, runtime geometry, collider changes during severing, ticket cancellation, repeated resets and a stalled owner. Then verify live shell alignment, support-list registration, corpse/weapon motion and Fika authority transitions with the [runtime checklist](RPT-Ragdoll-Runtime-Inspection.md).
