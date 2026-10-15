# Runtime inspection checklist

Use this checklist with a known build and matched EFT/SPT/Fika installation. It checks the behavior described by the [architecture](ARCHITECTURE.md), [geometry](ARCHITECTURE-GEOMETRY.md), [physics](ARCHITECTURE-PHYSX.md), [blood](ARCHITECTURE-BLOOD.md) and [multiplayer](ARCHITECTURE-MULTIPLAYER.md) guides. This is not a record of completed acceptance.

## Establish the run

Record package identity, game/Fika versions, peer roles, enabled mods, configuration and map. Current source is Rupture 1.2.2, impact protocol 14, native physics ABI 10 and sixteen synchronized settings. The canonical package has nine runtime/license files; the bundle has four shaders and one compute shader. Verify identical Rupture builds on host/headless and all clients. See [development setup](DEVELOPMENT.md) for build inputs.

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
