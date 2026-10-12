# Persistent local casing physics

## Ownership and headless boundary

Graphical clients take over spent `AmmoPoolObject` instances that EFT actually spawns. No distant ejections are synthesized. Four local settings control enablement, capacity (default 4,096; zero disables), sounds and nearby-impact scatter. Fika packets, handshake and authority configuration are unchanged.

`RupturePlugin.Awake` checks the initialized multiplayer role **before** calling `CasingHooks.Install`. Headless installs none of these Harmony hooks, constructs no `CasingWorld`, and registers no shadow-world casing callbacks. Its backend option remains false: casing delegates are not loaded, native casing storage is not constructed, and the original scene filter/publication path remains in use. There are no headless casing environment, query, contact, audio, pose or cleanup passes. Existing headless corpse simulation remains enabled as before.

The verified EFT boundaries are `AmmoPoolObject.SetUsed`, `GameWorld.SpawnShellInTheWorld`, `Shell.ActivatePhysics`, countdown and both instance/static pool returns. Admission requires spent state, world registration and captured activation; this handles the later `SetUsed` order used by spent jams. Ejected live rounds retain native ownership. The capture preserves configured ejection rotation, velocity and play multipliers; local spin is converted from EFT's degrees-per-step convention into world radians/second.

Admission parks native bounce/countdown, colliders and any existing Unity rigidbodies. It creates no Unity physics components. Geometry is cached by pooled visual variant and scale, using only active mesh geometry under transforms whose names contain `shell`, matching EFT visual discovery (for example, `patron_556x45_m855_shell_LOD0`). The full cartridge is excluded. Environment materialization and primitive creation run through the existing asynchronous owner queue; at most 128 primitives are staged per frame. Creation success commits ownership: remove the visual from `GameWorld.list_0`, clear its weapon collision listener and parent it under the raid casing root. No firing player or weapon manager is retained.

Only admitted generations block EFT pool return. Failed admission resumes native behavior. Oldest-first eviction hides the visual immediately, releases environment references and queues actor removal; its pool guard remains until the owner acknowledges removal. Raid disposal drains visual ownership before native pool disposal. Monotonic generation IDs reject stale results, including after reset. Backend failure retires owned visuals and disables takeover for the raid. There is no lifetime or distance eviction.

## Simulation and impacts

The casing manager is independent of corpse lifetime and keeps the shared scene running while casings move, even without corpses. Retained sleeping casings keep their actors, visuals and environment support without requiring recurring simulation. Final poses and removal acknowledgements are consumed before the next-step decision. Pending admissions, impacts, removals, capacity changes and native wake states restart work. Environment transform/shape changes and body commands also trigger simulation through a versioned dirty state; a command queued behind an in-flight step cannot be cleared by that step's completion. Environment capture remains active while the scene sleeps. A single fitted **box** is the chosen primitive, with casing-scale contact offsets, computed inertia, brass-shell mass approximation, modest restitution and natural sleep. The capsule implementation remains available to the instrumented native comparison fixture.

The owner thread filters unchanged sleeping body states before main-thread publication. Initial/final sleeping poses, changed sleeping poses and awake bodies still publish. Stable joint membership reuses a snapshot instead of allocating an array for every step. Schema 11 reports `ExternalIdleSkips`, `ExternalBodiesPublished` and `ExternalSleepingBodiesSkipped` so the next raid can distinguish avoided work from a lighter workload.

Schema 12 polls retained movable environment roots at most 4 Hz, in both proximity-cache and full-prebake modes. Initial actor capture remains immediate; subsequent pose/scale/destruction detection can lag by approximately 250 ms plus frame scheduling. Detected changes still queue native commands and wake an idle scene. Per-frame wake bounds are cleared between polls, preventing repeated wakes from stale movement. Collider-enabled-state audits and geometry admission keep their existing cadence. The status line reports poll and root-check counts. Fast movers can therefore have coarser collision-proxy motion; this is an explicit accuracy/performance tradeoff.

Casing-to-casing contacts use the ordinary discrete solver, as approved after dense piles exposed PhysX's CCD pair-sorting cost. Swept CCD remains enabled for casing/environment and casing/corpse pairs. A dominance pair gives casings the full response and gives corpses zero response. PhysX 4.1 still clipped the dominant body's CCD trajectory despite applying zero impulse; a source-hash-checked build-tree copy of `PxsCCD.cpp` corrects only zero-dominance advancement. The pinned SDK checkout is unchanged. Equal-dominance paths, including headless corpses, keep their original behavior.

**Known contact limit:** PhysX 4.1 does not support swept CCD on kinematic movers. A 0.10 m thick kinematic test body moving at 5 m/s crossed a stationary casing without moving it; a 0.20 m thick body at the same speed pushes it correctly. Fast dynamic contacts, ordinary kinematic contacts and a slowly rising support pass. This remains a limitation for very fast thin kinematic objects or teleporting supports; the implementation does not claim to resolve it.

Support geometry uses shared metre cells and the existing two-metre environment probes, in both proximity-cache and full-prebake modes. Moving casings acquire coverage ahead of their travel. Sleeping casings retain support references. Required unsupported geometry fails admission explicitly. Blast/scatter exposure acquires temporary geometry along the entire visibility segment, so an intervening wall is represented even when outside the support probes.

Native bullet trace segments are clipped to EFT's blocking hit, then queried against casings only. They never change EFT damage, penetration or hit selection. Direct hits apply point impulses with at most 20 m/s added speed. Environmental scatter uses a 0.35 m radius, edge falloff, exposure testing and at most 2 m/s added speed. Locally observed grenade blasts use radial queries, distance falloff and environment/corpse occlusion, capped at 12 m/s. Each event is deduplicated per casing. Queries finish before impulses wake actors, avoiding repeated scene-query rebuilds within one explosion.

Changed moving poses and sleep transitions, including final resting poses, are published through reusable batches. Ordinary corpse snapshots remain separate. Contact events are bounded to 512, rejected beyond 30 m, coalesced per casing and limited to 12 sounds per graphical frame. EFT's `MonoBehaviourSingleton<BetterAudio>` and caliber/surface casing banks provide playback. Audio limits do not suppress physical contacts. Debug logging reports casing main-thread, pose, audio, command, shared-scene and native-read times; shared-scene time includes any corpses.

## Performance evidence

The [performance guide](PERFORMANCE.md#casing-prototype-measurements) retains the capsule/box, dispersed/dense-pile and sustained-fire measurements, exact workload and watchdog limits. These native prototypes do not measure rendering, audio, main-thread waits or live raid FPS. Dense piles can remain expensive within the retained-count budget.

## Validation and reproduction

ABI **10** coordinates managed contracts, header, manifest and export checks. The release still has nine payload files. Automated checks cover:

- Installed EFT lifecycle/field signatures, local defaults, compiled headless registration gate and blittable layouts.
- Unity production geometry/ownership code with EFT/backend doubles: live-round exclusion, configured ejection velocity/spin, jam callback order, creation acknowledgment, native physics parking/restoration, pool guards, oldest eviction, capacity reduction, disabling, zero capacity, failed creation/environment, stale results, object destruction, reset and backend failure and prefixed shell LOD names with full-cartridge exclusion (29 checks).
- Native capsule/box ejection, thin floors, natural sleep, final-pose suppression, wakeups, direct-hit limits/deduplication, sound events, audio-off physical response, one-way dynamic/kinematic corpse contacts, terrain holes, stairs, moving supports, wall-occluded blasts and bounded scatter.
- Existing managed, native corpse, dependency, package and Unity reliability regressions. A non-graphical native scene has no casing service and rejects casing commands/readback.

```powershell
.\scripts\Test-Rupture.ps1 -SptInstallPath D:\Tarkov-SPT -Native -UnityRuntime
# After the native build:
.\artifacts\rupture-physx-native-build\release\rpt_physx_bridge_tests.exe --casings 4096 1 1
.\artifacts\rupture-physx-native-build\release\rpt_physx_bridge_tests.exe --casing-fire 4096
# Mixed wake fixture: 1,459 casings and 390 capsule bodies, 100 ms steps:
.\artifacts\rupture-physx-native-build\release\rpt_physx_bridge_tests.exe --casing-mixed 1459 .1
# Full capsule/box, dispersed/pile population matrix:
$env:RPT_CASING_BENCHMARK = '1'
.\artifacts\rupture-physx-native-build\release\rpt_physx_bridge_tests.exe
Remove-Item Env:\RPT_CASING_BENCHMARK
# Only after validation:
.\scripts\Build-Package.ps1 -SptInstallPath D:\Tarkov-SPT
```

**Live acceptance remains outstanding:** ordinary/automatic fire, shotgun/revolver ejection, real spent jams and pool reuse, observed-player firing, solo/Fika appearance and sound, unchanged corpse motion in real raids, moving map geometry, both environment policies on real maps, and headless firing/explosion counters staying at zero. Automated fixtures do not establish these results. Do not treat the native benchmark as FPS acceptance.

### September 16 failure investigation

The Factory log records 1,459 retained casings, then a grenade reactivating 30
corpses and applying impulses to 378 bodies. An environment transaction hit the
500 ms owner watchdog, permanently quarantining the backend. The original log
does not distinguish time spent executing that transaction from time queued
behind simulation or other work. This shutdown is confirmed; its slow operation
was **not** reproduced or fixed by that investigation; no later live acceptance is recorded here.

The user also observed casings passing through corpses before the shutdown.
Expanded native tests cover capsule corpse shapes, Deadbody layer 23, ordinary
owner groups, and stationary kinematic targets without artificial target updates.
Those tests pass; the live corpse collision defect remains unresolved.

A separate filter defect is reproduced and corrected: negative Unity instance
IDs can set the same owner-group high bit used for the casing marker. Ordinary
shapes now also require a zero pair-filter ID to qualify as casings. Six contact
regressions failed before this correction. This preserves swept contacts against
such map objects and preserves ordinary ignore-pair handling. It is not established
as the cause of either reported symptom.

The mixed fixture uses 1,459 settled casings and 390 unjointed capsule parts;
at a 100 ms step it measured 3.848 ms mean / 20.577 ms maximum over 180 steps on
this machine. It does not recreate EFT ragdoll joints, map geometry, concurrent
gib creation, or the live pile layout and therefore cannot clear the live issue.

Watchdog messages now report the waiting operation separately from the executing
operation, its phase, elapsed execution time, step delta, body/joint counts and
queue depth. Operations taking at least 100 ms also report when they eventually
finish, including whether quarantine already occurred. Existing status logging on
graphical casing clients adds `bodyShapeCommands`: enabled/total shape counts,
bodies without collision, and kinematic body count. These are managed command
states, not a native contact count. No additional setting is needed for watchdog
messages; enable the existing shadow status log for body-shape counts.
