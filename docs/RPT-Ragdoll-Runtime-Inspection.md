# Runtime inspection checklist

Use this checklist with a known build and matched EFT/SPT/Fika installation. It checks the behavior described by the [architecture](ARCHITECTURE.md), [geometry](ARCHITECTURE-GEOMETRY.md), [physics](ARCHITECTURE-PHYSX.md), [blood](ARCHITECTURE-BLOOD.md) and [multiplayer](ARCHITECTURE-MULTIPLAYER.md) guides. This is not a record of completed acceptance.

## Establish the run

Record package identity, game/Fika versions, peer roles, enabled mods, configuration and map. Current source is Rupture 1.3.1, impact protocol 15, native physics ABI 10 and nineteen synchronized settings. The canonical package has nine runtime/license files; the bundle has four shaders and one compute shader. Verify identical Rupture builds on host/headless and all clients. See [development setup](DEVELOPMENT.md) for build inputs.

Enable relevant local diagnostics in `RuptureConfiguration`. Correlate `RPTPhysicsStatus`, `RPTPhysicsBodies`, `RPTPhysicsScene`, `RPTPhysicsSample`, `RPTShadowStatus` and `RPTHeadWoundStatus` with logs and the profiler. Keep overlays, captures and performance logging explicit in measurements; a visible corpse alone does not prove which solver owns it.

## Shared settings and raid start

- On a graphical host, verify clients receive host settings and their saved local preferences return on disconnect.
- On headless with **Force Headless Configuration** disabled, start from differing requester/headless settings. Verify adoption of the first valid requester snapshot, broadcast before raid start, and consistent settings on other clients and later joiners.
- Verify later declarations/reconnects cannot replace the adopted requester settings, and temporary overrides do not enter the headless configuration file.
- Repeat with **Force Headless Configuration** enabled: headless-local settings must win and raid start must wait for broadcast. Toggle changes take effect next raid.
- Verify casing, diagnostic and machine-specific settings remain local in both modes. Mixed Rupture releases/protocols must be rejected.

## Corpse simulation and environment

- Create ordinary and dismembered corpses. Verify complete rig commit before native support-list removal, native kinematic query shells after commit and independent weapon movement.
- Confirm bullets, looting, body-part queries, decals and native cleanup still target the correct visible shell. Test sleeping, terminal and reactivated corpses.
- Compare native default-scene support counts with external rig counts. Unrelated supported objects can keep EFT default simulation active.
- In `ProximityCache` and `PrebakeEntireRaid`, test terrain slopes/holes, supported mesh/primitive collision, stairs/rails, moving doors/platforms and late-loaded/spawned geometry. Verify terrain capture and cache reuse.
- Exercise cuts during pending rig construction, touching corpses, weapon release and detached limbs. Procedural gibs use environment-only contacts on the standalone path; backend fallback can restore native Unity dynamics.
- Induce a supported backend/load/geometry failure in a controlled test. Before commit native simulation must remain active; after commit fallback must restore pose, velocity, collision state and support registration.
- Verify bounded dynamic lifetime, early dormancy, explicit impact wake and authority terminal settle. Ragdoll contact alone must not become wake authority.

## Production death reactions

All three settings in **Ragdolls | Death Reactions** default on: corrected lethal impulse (Rupture-only at 1x presentation gain), movement inheritance, and brief angular resistance. The visible **Old behavior** / **New behavior** buttons both enable movement inheritance. Old behavior uses the original amplified lethal impulse and passive joints; new behavior uses the reduced single-source lethal impulse and brief angular resistance. The buttons change only these three settings and save once. They affect new deaths; captured transactions and active resistance finish using their own snapshot. Host-controlled clients cannot override the preset. Impact protocol 15 synchronizes the three booleans and rejects protocol 14 peers.

The lethal path still requires a reliable terminal hit, compatible stock descriptor evidence, exact retained dynamic target and successful external admission. Ambiguity, failed admission or a missing callback replays the original route rather than discarding its force. A 250 ms timeout requires a full callback-frame opportunity; cold same/next-frame stalls cannot by themselves expire it. A complete transaction resolves at admission before its age is considered. Production selection uses the existing authority impulse transport; remote clients retain existing prediction/reconciliation. Standalone, graphical host and headless authority paths are eligible; incomplete role states fail open.

Movement and angular resistance capture independently for actual corpses, including nonbullet deaths and lethal impulse fallback. They do not take over recoverable/downed ragdolls. Movement adds only the missing pre-clamp linear component once to retained external core actors; finite inputs over 15 m/s, mismatched/repeated native starts, failed admission or invalid actors retain stock initialization. No angular velocity is inherited. Resistance begins at the initial simulation boundary and fades over at most 0.45 seconds; native simulation time and the wall-clock deadline both bound it. Dragging, settlement, removed actors, invalid joints and reset retain their existing cleanup rules. Subsequent corpse impacts do not recapture movement or rearm resistance. Nonlethal bullets, explosion impulse magnitude, dismemberment, weapons and gibs retain their original routes.

The 32-record comparison cap no longer controls gameplay. Production records follow short transaction/capture lifetimes and corpse lifecycle; diagnostics retain only 256 events of at most 1,200 characters per subsystem. Nothing is logged per frame. `RPTLethalDump`, `RPTVelocityDump` and `RPTAngularDump` remain; experimental mode-changing console commands are no longer registered. The older experiment descriptions below are historical and their commands are not current instructions.

Local regression fixtures cover configured source selection/fallback, cold callback windows, preset snapshots, all three independent switches, nonbullet movement, retained-core validation/rollback, role gates, native joint cleanup and simultaneous captures above the diagnostic cap. They do not replace live configuration-panel, first-raid death or Fika replication acceptance.

For live acceptance, start a fresh raid with **New behavior**. Kill one stationary bot first, then a moving bot, shoot a corpse once and enter `RPTAngularDump` (includes velocity/lethal journals). Check physical `single-source-queued`, matching velocity preparation/readback, and resistance clearing to `activeDrives=0`. Click **Old behavior**, repeat a few deaths for visual comparison, then restore **New behavior**. Repeat host/client and headless tests with identical packages; check remote corpse motion, profile synchronization and down/revive behavior before final death.

## Historical session-only lethal impulse experiment

Default behaviour is unchanged. No experimental settings are persisted or added to the Fika configuration wire. Console commands affect **new deaths** and reset at raid teardown:

| Command | Effect |
| --- | --- |
| `RPTLethalBaseline` | Enable bounded tracing with the existing stock plus completed-shot paths unchanged. |
| `RPTLethalStockOnly` | Local raid experiment: choose the stock impulse after reliable correlation and external admission. |
| `RPTLethalRuptureOnly` | Local raid experiment: choose the correlated completed-shot impulse after external admission, with stock held or its descriptor proven unarmed. |
| `RPTLethalPhysical` | Use 1x projectile momentum for new Rupture-only transactions. The existing Bullet Momentum Multiplier and 500 N s cap still apply. |
| `RPTLethalAmplified` | Use the existing 4x presentation gain for new Rupture-only transactions. |
| `RPTLethalDump` | Write the retained event ring to the BepInEx log, independently of general debug logging. |
| `RPTLethalOff` | Release pending originals and disable tracing/experiments. |

Use the console controls in raid before creating the test corpse. Gain selection is independent of source selection; compare Rupture-only 4x against Rupture-only 1x to isolate the gain. Baseline and stock-only ignore the gain selector. These lethal controls do not enable velocity inheritance or angular drives. Fika being installed does not block local raid comparisons; an initialized graphical Fika host with zero connected peers is also eligible. Remote clients, headless, hosts with peers, and ambiguous/uninitialized transport state permit only baseline tracing: unsynchronized diagnostic controls must not change another peer's stock-force prediction, authoritative events, or the wire protocol. Eligibility is checked again when holding and committing impulses, including a peer connecting during the transaction. Command output reports the selected source, gain, eligibility, hook availability and live role/peer flags; a rejected command reports that the mode is unchanged.

Tracking is limited to 32 terminal bullet deaths for four seconds each, with a 256-entry ring of at most 1,200 characters per entry. Full capacity fails open and counts dropped deaths. There is no per-frame log output. IDs include the local death record, profile, projectile identity and fire index; events record frame/time, admission state, impulse magnitude/point, body and sampled velocity. Dismemberment/blood processing still runs before the completed-shot impulse decision.

While the collision callback is active, the experiment captures the lethal collider, point, damage direction, incoming mass/speed and the tracked shot instance. Capture requires the terminal damage's projectile/fire identity and exact hit to agree with that callback. Nested callbacks and exceptions restore the preceding context. Penetration continuations may bind to the tracked physical projectile; untracked fragmentation siblings cannot borrow its completion. A tracked instance and birth fingerprint (ammo reference, fire index, fragment index and seed) guard pooled object reuse. Records remain bounded; no second shot registry or per-frame log was added.

For an opt-in source selection, a bound completion uses the frozen lethal contact even if the shot's later collider, point, direction, mass or speed has changed. The target is the lethal collider's attached Rigidbody, verified against that corpse's native core spawners and the retained external actor; it never uses nearest-anatomy fallback to choose the selected impulse. The existing completed-shot body, collider, point and amplified impulse are retained separately for exact fallback. Baseline does not substitute contact data. Without a frozen binding, the original strict same-corpse/projectile/collider/point/direction correlation remains; ambiguous samples and unrelated corpse impacts stay on their existing routes. A stronger captured binding cannot fall back to geometric matching against another tracked lifetime.

The experiment holds original submissions for at most 250 ms, checked at the next event/update, and commits only after reliable correlation and availability of the exact retained external actor. Normally both submissions must be held. Rupture-only may also commit when a single correlated EFT descriptor demonstrably ran, with no pending stock descriptor before it, no stock submission, no armed descriptor afterwards, and a failed exact spawner lookup. This is an observed rejection, not absence inferred from elapsed time. It commits as soon as the completed shot and admission are available, without waiting 250 ms. Stock-only cannot synthesize a stock impulse in this case; it falls back immediately. Baseline still follows the original path.

Admission failure, timeout, unknown/skipped/repeated descriptors, missing counterpart and cancellation replay the original impulses, including the original amplified value when a physical replacement did not commit. Fallback stays latched for late events. A timeout followed by late admission transfers only fallback forces still observed queued at capture; it never re-injects sources already consumed by native simulation. Destroyed or inactive fallback targets produce an explicit diagnostic warning rather than silently redirecting the selected impulse to another anatomical connection. An unexpected stock descriptor/submission after successful selection is left unchanged and emits `comparison-invalidated`; exclude that death from comparison. Subsequent corpse impacts retain their original behavior.

Interpret evidence carefully:

- `eft-queue` describes EFT's descriptor call; it is not a Rigidbody force submission. It records original execution, prior/current pending state, exact spawner lookup, hit collider type/name, first collider type, same-object spawner presence and attached body.
- `stock-unarmed-confirmed` proves that the correlated original descriptor did not arm a force. `non-primary-collider-on-spawner` identifies another collider on the spawner object; `no-exact-spawner-collider` identifies the broader identity-lookup miss.
- `lethal-contact-captured` records the frozen terminal contact and tracked instance. `completed-lethal-contact` records the original completed-shot target/point/magnitude alongside the selected lethal target/point/momentum. These allow back-head and other penetrating hits to qualify without widening spatial tolerance or depending on a particular aim direction.
- `stock-submit` / `rupture-submit` distinguish native and external queueing. They do not prove integration.
- `native-*-step-consumed` observes a completed default-scene simulation while the submitted native body was dynamic. Its velocity difference includes gravity, contact, other forces and joints; it is not an isolated measurement of that impulse's contribution.
- `admission-capture` records the velocity handed to external PhysX. Pending native force accumulators are not part of that capture.
- `external-step-completed` is fenced by the next submission, so completion of an older in-flight step does not count as consumption of a newly queued force.
- `single-source-queued` proves source selection and queueing, not subsequent visual acceptance. Its `stock=held` / `stock=confirmed-unarmed` field distinguishes the two eligibility paths. `fallback` or a subsequent `comparison-invalidated` means that death is unsuitable for a clean single-source comparison.
- Automatic engine simulation and force changes made directly by other mods/native code cannot be fully attributed by these managed hooks. Force-changing comparisons require script simulation. A throwing native simulation disables pending-force migration for that record.

Suggested matched runs: baseline, stock-only, Rupture-only amplified, then Rupture-only physical. Hold the existing Bullet Momentum Multiplier at 1 and change no other settings. Test stationary head/chest deaths, different shot directions, running deaths, penetrating rounds, shotguns, severed limbs, subsequent corpse hits, failed/delayed admission, and early settlement. Dump after each small group before ring overwrite or raid exit. Record how many deaths actually reached `single-source-queued`; do not compare fallback deaths as successful experimental samples. Use existing performance logging for matched worker/main-thread time, active-body population and dormancy/settlement timing.

For just the gain comparison, enter `RPTLethalRuptureOnly` and `RPTLethalAmplified`, create a small group of matched deaths and dump. Then enter `RPTLethalPhysical`, repeat and dump. The source remains Rupture-only in both groups; explicitly selecting Amplified avoids carrying 1x over from an earlier experiment. Expect `single-source-queued` with gain 4x versus 1x, followed by `external-step-completed`, and no fallback/invalidation for an accepted sample.

The 2026-10-16 raid log contained eleven distinct deaths: eight descriptor calls returned with `_hasImpulse=False` and no stock Rigidbody submission, while three armed and reached the stock overload. Installed EFT `CorpseRagdoll.ApplyImpulse(Collider,...)` only arms when `FindRigidbodySpawner` accepts the hit. That lookup compares the hit with each spawner's single `GetComponent<Collider>()`; it does not search all colliders or resolve an attached body. Thus another collider on the same body can be valid for Rupture while failing EFT's stock gate. The old trace did not retain collider component details or original-execution state, so the precise collider mismatch for each historical death cannot be reconstructed. The new bounded descriptor evidence distinguishes those cases without changing EFT's mapping or manufacturing a missing stock force. Controlled Unity cases reproduce both secondary-component and child-hitbox misses using the installed method's lookup/arming logic.

Local validation: `scripts/Test-LethalImpulseRuntime.ps1` exercises production transaction code and Harmony hooks in Unity 2022.3.43f1, with minimal EFT lifecycle adapters and the production standalone PhysX bridge. Its isolated 4 kg free-body cases establish that 45 N s stock plus 14.4 N s Rupture can produce 14.85 m/s, including native integration before external handoff. They also establish that a still-queued native force is absent from velocity-only handoff. These are controlled mechanism tests, not evidence of frequency, actual EFT corpse masses, live hit correlation, visual quality, or raid frametime. The normal managed regression suite verifies the installed hook ABI and existing configuration/network contracts.

The same fixture now compiles and executes the production `ShotImpactProcessor` completion path. A head/chest sphere/box and child capsule matrix covers both gains, stock present/unarmed, changed endpoint/body and both admission orderings. Failure cases verify that admission failure, timeout, unavailable backend, a peer joining and removed lethal colliders restore the original completed-shot body/point/direction/amplified value exactly once. Further cases cover penetration lineage, distinct fragments, reused/ambiguous shot lifetimes, nested throwing collision scopes, nonlethal completion, later corpse impacts and disabled-path allocation. The managed suite checks collision-scope/finalizer wiring against the installed EFT ABI; actual EFT callback timing and ordinary raid acceptance still require in-game verification.

## Historical session-only local root velocity experiment

Defaults remain unchanged. This experiment adds no saved settings and never changes angular initialization or drives. Both comparison commands automatically select and lock **Rupture-only 1x** for new lethal bullet transactions. Existing fail-open impulse handling remains in force; a fallback death is excluded from velocity comparison.

| Console command | Behavior for new deaths |
| --- | --- |
| `RPTVelocityBaseline` | Preserve the stock clamped root velocity; lethal source fixed at Rupture-only 1x. |
| `RPTVelocityInherited` | Restore the difference between raw root velocity and EFT's initial seed once, before the first external step. Same lethal selection. |
| `RPTVelocityDump` | Dump the bounded velocity journal and lethal impulse journal, without requiring general debug logging. |
| `RPTVelocityOff` | Disable both experiments and restore ordinary defaults for future deaths. Existing corpse motion is retained. |

The installed EFT constructor stores the supplied player movement velocity in `CorpseRagdoll._velocity`, then `Start()` overwrites it while initializing bodies: magnitude is clamped to 2 m/s, except a normalized vector exactly equal to up becomes 1 m/s. A prefix captures the raw value and a postfix validates the resulting seed. The experiment does not patch that native assignment. At the existing scene-owner boundary, after any preceding result is published and before submission, the inherited mode adds `raw - stockSeed` to each retained dynamic external core body's **current** linear velocity. Already integrated native motion, gravity/contact changes and queued selected impulses are preserved. It writes no angular velocity or kinematic state, creates no drives and does not alter independent weapons, gibs, removed bodies, sleep timing or networking.

Only a tracked terminal bullet identity, validated native start, successful admission and completed Rupture-only 1x selection before the rig's first external step qualify. Pending/failed/invalidated source selection, repeated starts, missing actors, nonfinite input, raw speed over 15 m/s, ownership changes and capture-to-first-step delays over 500 ms are excluded. Excluded deaths retain their existing velocity path and ordinary impulse fallback. A late selection cannot retroactively correct a stepped corpse. Both modes apply the same eligibility checks. Local raids and initialized graphical solo Fika hosts with zero peers qualify; client/headless/peer-host experiments are rejected and live roles are checked again at the boundary.

Velocity records are bounded to 32 captures, a four-second retention window at the next capture/scene boundary/dump, and a 256-entry journal with a 1,200-character limit. Raid reset clears all state. There is no per-frame logging. `captured` reports raw velocity, stock seed and missing component. `prepared` reports selected body count and mean velocity before/after; `submitted` and `completed` fence the first step. Count only matching `prepared` / `completed valid=True` samples, with no later lethal `comparison-invalidated`. Readback includes impulses, gravity, contacts and joints and does not isolate each force's contribution. Stationary/slow movement normally yields zero correction; running victims above 2 m/s are the useful comparison.

In one local raid, enter `RPTVelocityBaseline`, kill one stationary bot and 2–3 visibly moving/running bots, then `RPTVelocityDump`. Enter `RPTVelocityInherited`, repeat a similar group using the same weapon/ammunition and conditions, then `RPTVelocityDump`. Ordinary lethal head/chest hits are suitable; precise front-facing headshots are unnecessary. Include movement across the shot direction to distinguish inherited motion from projectile momentum. Shoot one resulting corpse once and check its usual impact and settlement. Keep Bullet Momentum Multiplier at 1 and other settings unchanged. `RPTVelocityOff` or raid exit restores defaults.

Local validation runs the production capture/selection/lifecycle code, exact extracted production retained-core transfer method and production native bridge in Unity. It covers clamp/upward rules, preserved integrated motion and pending impulses, repeated-step prevention, removed/invalid/kinematic actors, multiple core bodies, independent weapon exclusion, write rollback, role/source guards and reset/off. It does not establish full articulated EFT admission, moving-bot visual quality or live performance; those require the matched raid test.

## Historical session-only local residual angular-drive experiment

Defaults remain unchanged. These commands add a separate local comparison with **Rupture-only 1x and velocity inheritance ON in both groups**. The earlier controls cannot switch either factor while the angular experiment is enabled. No saved settings or wire fields are added.

| Console command | Behavior for new deaths |
| --- | --- |
| `RPTAngularBaseline` | Existing passive joints; inherited velocity and Rupture-only 1x fixed. |
| `RPTAngularResidual` | Brief bounded resistance toward the captured relative death pose, with the same inherited velocity and impulse selection. |
| `RPTAngularDump` | Dump angular, velocity and lethal journals, independently of general debug logging. |
| `RPTAngularOff` | Restore defaults for new deaths and cancel remaining drives at the next safe owner boundary. |

A postfix of the existing native `Start()` capture records connected bodies' relative orientations before their first simulation. Targets stay fixed; there is no continued animation, pose differencing, pose/velocity overwrite, world-space upright target, foot placement or locomotion controller. Only exact retained core connections mapped to existing native D6 joints qualify. Independent weapons/gibs, missing or removed connections, broken/invalid joints, world anchors and dragged corpses are excluded. Captured poses already more than 45 degrees from admission are excluded. Reliable lethal selection and the existing first-step velocity eligibility must succeed before the angular window closes. Explosions, nonlethal hits, later corpse impacts and multiplayer authority paths retain their ordinary handling.

The optional additive native ABI 10 capability configures each D6 drive in one completed-step owner transaction. Existing interface/descriptor layouts are unchanged; older bridges without the two optional exports continue normal operation and reject the angular experiment. Drive pose conversion accounts for both local joint frames and the captured parent-to-child body orientation. SLERP is used only when all angular axes have freedom; locked-axis joints use available twist/swing drives instead. Joint limits, limit springs, solver iterations and independent-body ownership are unchanged.

Resistance ends at 0.45 seconds after the native start capture, including admission delay. The native scene owner applies a quadratic fade to spring, damping and torque limits over the remaining simulation window. A wall-clock deadline independently cancels at the next boundary, so stalled simulation cannot postpone resistance into a later corpse hit. There are no wake calls or active-lifetime extensions. Sleeping/kinematic islands, removed actors, destroyed joints and broken connections stop their drives natively. Core lifecycle cancellation covers off, live role changes, invalidated lethal selection, dragging, dormancy, settlement and reset. Failed cleanup remains queued for retry with a single warning, rather than losing the transaction.

Prototype values are fixed for this experiment, in Nm/rad, Nm s/rad and **Nm per solver drive row** respectively: torso 35/3/8, neck/head 12/1.2/2, legs 25/2/6, arms/other retained core connections 18/1.5/4. The row limit is not a claimed total-body torque budget. Drives use force units and no acceleration drive. At most 256 joints can be driven concurrently in the native scene; batch validation is atomic and capacity failure leaves that angular sample passive/excluded. Tracking retains at most 32 captures for ten seconds, allowing bounded lifecycle observations; the event ring holds 256 entries of at most 1,200 characters. Targets and body sampling run only for this short transition; no per-frame logging occurs.

For a valid comparison, match `prepared` / `completed valid=True` for the same death, then `disabled` with `nativeRead=True` and `activeDrives=0`. Baseline readback must report zero drives; residual readback must report active drives and a decayed nonzero row limit before expiry. Peak pose deviation/angular speed are bounded transition observations, not isolated drive-energy measurements or proof of visual quality. `lifecycle-ended` reports captured dormancy/settlement/destruction timing if retained. `corpse-hit` records ordinary postmortem application acceptance during this session without requiring general debug logging; this is not isolated force-integration evidence.

In one local raid, keep the same weapon/ammunition and Bullet Momentum Multiplier 1 throughout. Enter `RPTAngularBaseline`, kill 2–3 bots with ordinary head/chest hits, then `RPTAngularDump`. Enter `RPTAngularResidual`, repeat a similar group, then `RPTAngularDump`. Stationary and crouched victims are useful; running is optional. Look for a brief yielding resistance followed by natural collapse, and report stiffness, snapping, jitter, floating or excessive spins. Shoot a resulting corpse once and dump again if checking postmortem handling. Off or raid exit restores defaults.

Validation includes the production native API loader, captured joint frames, angular backend batch methods, world connection selection and session lifecycle in Unity against real D6 joints. Owner dispatch is synchronous in that fixture; production dispatch/fences are checked structurally. Native tests cover correct target coordinates, restorative/equal-and-opposite angular response, torque force units at 30/120 Hz, exact zero after fade, locked axes, atomic invalid batches, capacity, sleeping, ownership and removal. A small matched 16-joint/32-body benchmark measures isolated solver cost, not raid frametime. Full articulated EFT appearance, live time-to-sleep and performance still require in-game validation.

## Cadence and Fika motion

Graphical and headless submission runs in `LateUpdate`: complete the prior step, apply authority state, then submit the next step. Graphical delta uses full unscaled frame time; headless cadence accumulates delta at up to approximately 30 Hz. The later player-loop pass reasserts visible transforms without stepping.

Run solo, graphical host/client and headless host/client cases:

- Test down → revive → lethal hit, down → bleedout, and at least three successive revives. Recoverable downing must not publish death, consume terminal death state, cut corpse anatomy or take over corpse physics. Removed native bleeding/fracture effects must stay removed after revival; final death must execute once even if a downed flag lingers.
- Owner damage totals include the lethal sample once; delayed native death/corpse identity does not drop the outcome.
- New impacts predict immediately; matching authority echoes do not double the force.
- Out-of-order pose chunks apply atomically. Missing chunks must not move only part of a corpse.
- Complete bindable motion is usable before delayed reliable start metadata.
- Playback buffers one observed authority interval, drops obsolete backlog and resumes after prediction.
- Reliable settle reaches the exact authority pose; clients do not settle from a local timeout.
- Core and weapon motion synchronize. Procedural limb-gib trajectories remain local.
- Reactivation actually publishes and applies fresh snapshots. Use sent/received/applied/discarded counters, not only final position.

## Injury geometry and decals

Inspect every arm/leg, both cut levels, lethal/postmortem cuts, repeated impacts, multiple severed limbs and explosions. Include bare skin, loose garments, sleeves, equipment combinations and LOD changes. Check retained skin, body/gib caps, internal bone count, garment openings and shared-core ownership.

Successful cuts remove only the intended anatomical hit colliders. Failed observer reconstruction retains intact colliders. Test despawn/reset while capture, geometry work, convex bake and actor publication are pending.

For head wounds, test bare/equipped heads, oblique and non-through paths, separated surfaces, overlapping wounds and Player disposal with a surviving corpse. Check the sixteen-attempt/four-adopted-wound distinction, failure recovery and persistence of earlier wounds after rejection. Inspect real exits and entry-crater fallback separately, hidden native head tiers, distance/culling behavior and headgear detachment independent of wound success.

Apply new body/head/sever decals while changing LODs and pose. Existing native paint should survive supported adoption. Check current-pose painting on enabled hidden LODs; missing older decals are not generally reconstructed.

## Headgear detachment and pickup

With general debug logging disabled, successful events remain only in a bounded in-memory journal: 256 item IDs, eight events each, 512 characters per event. Item references used for matching are weak, evidence entries follow history eviction, and raid teardown clears the journal. The first failure per retained item emits one `Headgear audit` warning containing role, item ID and recent events. The normal warning/error rate limit is 60 per minute with a suppression notice. Evicted history and unknown item IDs cannot reconstruct an earlier failure, and missing player IDs may be silent if the item was never observed locally.

For a reproduction, collect complete BepInEx logs from the authority and both clients, exact Rupture/Fika versions, the victim and approximate kill time, and whether pickup was attempted from the corpse or floor. Correlate `Headgear audit` records and Fika errors by item ID. Test an AI with all four head slots, delayed networking, pickup during detachment, later corpse hits, the toggle disabled, and remote-human deaths. Automated fixtures establish sequencing, bounds, ABI and ownership checks; live EFT/Fika acceptance remains outstanding.

Verify the ordered headwear/earpiece/face-cover/eyewear throws, at most one new operation per update. A missing callback or matching world-loot object must stop advancement at the one-second deadline; do not treat local completion as a remote acknowledgment. Disabling detachment prevents new throws but allows submitted operations to finish. Check [observed inventory root replacement](ARCHITECTURE-MULTIPLAYER.md#headgear-and-corpse-inventory) without changing native inventory results.

## Living blood, fractures and severed legs

- Heavy bleed, treatment, remaining light bleed and complete treatment: anchors remain appropriate, stop is published once and new supply ends.
- Check light droplets, pulsing heavy/kill streams and shorter postmortem flows against configured durations.
- Exceed visual pools and move in/out of range. Logical bleeds survive eviction and restore only within the visual budget.
- Inspect local/observed garments and sleeves. Soaking follows sampled posed gravity, stops receiving supply after treatment and retains accumulated native paint. The current approximately half-second sampling can produce coarser visible growth; inspect it during movement.
- Fracture every limb, including blacked limbs; change equipment/LODs and treat/refracture. Check bone/collar alignment and cleanup on death/controller replacement.
- Use the full [living severing checklist](Living-Bot-Leg-Severing.md#live-acceptance-still-required): bullet blacked-leg buildup, explosion distance/damage rules, role protection, both cut levels, one-second fall/blend, persistent prone anatomy, stump damage and actual surgical completion.
- Verify delayed observed spawns/current-state refresh without client health ticking, and death during pending cuts or a fall without duplicate native ragdolls. Corpse control ABI V1 must not be invoked on living or downed actors.
- Headless must process logical authority outcomes and physics without wound, stream, fracture, soaking or casing graphics/services.

## Optional corpse-control consumers

Use the [corpse-control API acceptance scenarios](CORPSE-CONTROL-API.md#validation) for a compatible consumer such as BodyDragging. Test claim/release, exclusive leases, timeout, removal/reset, reactivation, holder motion and graphical/headless authority. A downed or living actor is never eligible. Automated API fixtures do not establish a consumer's gameplay behavior or public availability.

## Casings, lifecycle and measurement

Test local retention/capacity eviction, sleep, collision sounds, nearby bullet/explosion responses and backend failure. Casing state/settings are not replicated. Dense piles can be expensive despite the retained-count budget; see [casing evidence](Persistent-Casings.md).

Test controller replacement, despawn, disconnect/reconnect, repeated raids and teardown with work pending. Raid-reset success does not establish safe plugin hot unloading; see [lifecycle limits](ARCHITECTURE-LIFECYCLE.md#plugin-shutdown).

Measure main-thread capture/upload, owner waits, external simulation, visible-shell synchronization and unrelated default-scene work separately. Include many corpses/injuries with diagnostics both off and on. The limb budget is soft; head geometry runs on the serial managed worker while Unity capture/adoption and some owner calls remain atomic. CPU parcel preparation/submission timings do not measure GPU execution. Do not sum inclusive parent and child stages twice. Keep comparisons matched by workload, diagnostic settings and call/frame denominator; [performance evidence](PERFORMANCE.md) separates historical fixtures from current live claims.

Record observations/failures against the actual package. Automated geometry, packet and native fixtures establish their own contracts; they do not replace these live checks.
