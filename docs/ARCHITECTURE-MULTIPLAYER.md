# Multiplayer and authority

Core owns injury policy consumers, procedural rendering and corpse state. The optional Fika assembly owns integration with Fika's player roles, manager lifecycle and packet transport. A graphical peer constructs its own meshes from authoritative descriptors and its local equipped assets; health remains native EFT/Fika state.

## Runtime selection

[IMultiplayerRuntime](../src/Rupture/Multiplayer/IMultiplayerRuntime.cs) defines role/identity queries, lifecycle, impact/death routing, blood requests and descriptor sends. [StandaloneRuntime](../src/Rupture/Multiplayer/StandaloneRuntime.cs) supplies no-op networking and local reset behavior. Its network-role flags, including `IsNetworkAuthority`, are false. Core solo policy therefore uses the appropriate `!IsRemoteClient` path; do not equate “not a server” with “must wait for an authority.”

[MultiplayerRuntime](../src/Rupture/Multiplayer/MultiplayerRuntime.cs) checks Chainloader for `com.fika.core`. Without Fika it never loads the Fika-dependent assembly. With Fika it loads adjacent `Rupture.Fika.dll`, verifies assembly version against core, constructs `Rupture.Fika.FikaRuntime`, assigns the current runtime before initialization and initializes it. Failure disposes partial state, restores the standalone object and propagates the error; plugin startup then disables Rupture. The restore is cleanup, not permission to continue a Fika raid with solo decisions.

[FikaRuntime](../src/Rupture.Fika/FikaRuntime.cs) implements the contract and subscribes to network-manager creation/destruction. Remote client means client without server; authority means server or headless. Headless detection uses Fika's backend utility. Adapter packet wrappers translate core data into Fika serialization types; core does not reference `Fika.Core`.

## Authority matrix

| Action | Origin / decision owner | Receiver behavior |
| --- | --- | --- |
| Native living damage | Existing Fika damage route | Execute the stock damage packet with captured Rupture context; no second health application. |
| Death limb totals | Victim owner for a remote human | Authority validates identity, waits for death/controller state, imports totals and evaluates outcome. |
| Corpse hit intent | Hitting peer with captured sample/target | Authority resolves lifetime and admits the impact; local prediction can respond immediately. |
| Limb sever / head wound | Authority policy (or solo locally) | Graphical peers generate local geometry; headless records required logical state. |
| Living bot leg sever / treatment / fall | Solo instance or Fika authority | Clients present injury, phase and pose state without applying stump health damage; current prone state is refreshed periodically. |
| Living blood request | Owner of the bleeding player | Host checks peer/profile and active native effect, assigns normalized stream descriptor; all peers consume start/stop. |
| Corpse forces, start, wake and settle | Authority | Clients deduplicate/predict or apply lifecycle/motion state. |
| Live corpse pose | Authority completed external step | Client assembles complete body sets and performs kinematic playback. |
| Fracture appearance | Each graphical peer from native replicated fracture effects | Deterministic local additive geometry; no Rupture fracture packet. |
| Procedural gib motion, particles, clothing paint | Local graphical simulation | No gib trajectory, vertex buffer, parcel or paint texture replication. |

## Handshake, protocol and configuration

[FikaImpactNetwork](../src/Rupture.Fika/Impacts/FikaImpactNetwork.cs) defines **protocol 14** and exact release string **`1.2.2`**. Each network session uses a 32-character GUID session string. Hello exchange is reliable/ordered with a 15-second deadline. Protocol/release mismatch, malformed session, or changing a session without reconnect is rejected. This wire check is separate from adapter assembly-version and native ABI checks. The current hello includes a headless requester declaration and the configuration snapshot; living bot injuries use their own bounded packets. The damage envelope embeds the separate victim network ID followed by Fika 2.4.4's readonly `DamagePacket` serialization. The [frozen fixture guide](../tests/Rupture.RegressionTests/Fixtures/README.md) distinguishes historical payload coverage from current protocol tests.

The authority's configuration snapshot has a positive revision and exactly sixteen validated serialized values, in this order:

1. `EnableDismemberment`
2. `EnableBloodEffects`
3. `KillshotStreamDuration`
4. `DismembermentStreamDuration`
5. `MaxDecals`
6. `ShotIntensity`
7. `GrenadeExplIntensity`
8. `RagdollSleepTime`
9. `LocalizedRagdollPhysics`
10. `WakeCorpsesOnImpact`
11. `DismembermentMomentumThreshold`
12. `AutomaticHeadgearDetachment`
13. `LivingBotLegSevering`
14. `LivingLegMomentumThreshold`
15. `LivingLegSeveringSpecialAI`
16. `LivingLegSeveringExplosionsOnly`

[RuptureConfiguration](../src/Rupture/RuptureConfiguration.cs) parses and validates the whole snapshot before assigning anything. Nonfinite numeric values, invalid ranges, length/type errors and malformed snapshots cannot partially override a client. Revisions order subsequent authority updates. Local preferences are saved separately from in-memory overrides and restored on network reset. Casing settings, debug settings, geometry scheduling budget, external environment mode and dispatcher threads remain machine-local.

Graphical hosts use their own settings. A headless authority normally adopts the first valid requester's complete snapshot before raid start, then broadcasts it to clients and later joiners. The requester declares `IsHeadlessRequester`, sends revision zero and a 32-character session ID, and supplies its local preferences before incoming authority values replace them. This declaration is trusted; it is not independently authenticated as the matchmaking requester. Later declarations cannot replace an adopted snapshot, and reconnects do not become requesters. Ordinary clients cannot supply authority configuration.

Headless raid-start requests wait for configuration adoption and successful broadcast. **Force Headless Configuration** selects headless-local settings instead, ignores requester payloads without rejecting the declaration, and waits only for broadcast. The toggle is local, defaults to disabled and is sampled for the next raid. Temporary adopted values are protected from configuration-file persistence. Network-manager reset restores local preferences and clears requester/start-gating state.

Important boundary: handshake/session validation is explicit in the impact-network path. Ordinary descriptor packet registration and server send wrappers in `FikaRuntime` do not each perform an additional per-peer handshake filter. Do not describe the handshake as a universal wrapper around every send/receive call; ordinary descriptors also rely on their registered server/client direction and core admission validation.

## Living damage and deferred death

[FikaImpactHooks](../src/Rupture.Fika/Impacts/FikaImpactHooks.cs) intercepts the concrete `IFikaNetworkManager.SendPlayerPacket<DamagePacket>` call sites, preserving the `SendPlayerPacket<T>(in T, int, DeliveryMethod, bool, NetPeer)` signature and separate victim network ID. The Rupture envelope contains the victim network ID and stock serialized damage bytes (bounded to 2,048), a captured impact sample, session and nonzero sequence. Eligible projectile/fragment paths preserve source momentum rather than deriving it from a later reconstructed hit. Other player packet types retain their stock sender. The receiver uses Fika's `DamagePacket(NetDataReader)` constructor and `Execute(player)` once, preserving Fika's victim ownership check.

On authority receive, the network verifies session, sequence and sender/profile association, forwards to the appropriate other peers, scopes `ImpactStateService.Current`, and calls stock `packet.Execute(player)` once. Scope restoration and packet clearing occur in `finally`. The envelope supplies context for Rupture's existing applied-damage hooks; it does not replace Fika's damage ownership.

Victim-owner death snapshots already include the lethal sample in accumulated limb totals. The host validates totals, owner profile and player ID and can wait for local native death/controller readiness. Importing totals and remembering the lethal sample must not add that sample again. The authority then invokes the ordinary death policy path. Lifecycle-only native death callbacks are deferred so they cannot preempt a still-running lethal applied-damage postfix.

## Living bot injury state

[BotSeverStateService](../src/Rupture/Health/BotSeverStateService.cs) owns living anatomy, reservations, stump health loss and treatment on the solo instance or Fika authority. [BotSeverPacket](../src/Rupture.Fika/Impacts/BotSeverPacket.cs) carries reliable injury/treatment/phase changes, falling poses at up to 20 Hz and periodic reliable prone state. Clients bind profile/network identity to the server session, reject invalid or stale state, and present the result without ticking stump damage. A bounded five-second queue handles delayed observed-player creation; periodic state permits retrying missing local geometry. This refresh is specific to living severed bots, not a general injury-history replay service.

Death cancels pending living cuts and releases temporary living physics before corpse construction. Committed missing anatomy survives that transition. The [living severing guide](Living-Bot-Leg-Severing.md) describes health, surgery, topology and live acceptance requirements. The [corpse control API V1](CORPSE-CONTROL-API.md) remains restricted to actual corpses and must not be used for living or Fika-downed ragdolls. Consumers transport their own interaction claims to the authority; Rupture continues to own the sole corpse pose stream.

## Headgear and corpse inventory

Automatic Headgear Detachment defaults to enabled and uses the existing host visual configuration snapshot. Disabling it stops new Rupture throws; an already submitted EFT operation is allowed to finish. Head wounds and other effects keep their existing settings.

Each corpse still throws headwear, earpiece, face cover and eyewear in order, at most one new operation per update. Advancement requires local slot removal, a successful terminal operation callback, and creation of world loot with the same item instance. A missing callback or world object stops the sequence at the existing one-second deadline; no retries, extra delays, forced item moves or inventory repairs are performed. Local completion does not acknowledge remote clients.

The core wraps the callback on EFT AbstractOperation.Execute directly, before Fika's pooled callback handler. The original callback and exceptions retain their normal behavior. A controller callback also records simulation rejection before an operation exists. Equipment/controller bindings resolve current inventory on access. Before starting a throw, the exact item and slot address must still agree. Fika owners must remain registered under the same NetId and have an authoritative sender; observed human owners and retired network actors are skipped with a diagnostic instead of initiating an unsynchronized throw. This does not implement an owner-client request protocol for remote humans.

The optional Fika adapter observes incoming tracked throws/moves, operation conversion, pickup rejection and inventory replacement. It does not modify those packets or their results. Missing player registration is captured when the item's ID is already in the local history. World-loot creation is observed on both host and clients. A replacement snapshot that equips an item previously observed as world loot is reported.

The adapter also aligns an observed controller's lookup root with its replacement equipment. In the installed EFT 4.1 baseline, `InventoryController.ReplaceInventory` replaces `Inventory.Equipment` but leaves `ItemController._rootItem` unchanged. `GameWorld.FindItemWithWorldData` searches registered owners through `TryFindItem`, which still traverses that root. This can resolve the pre-corpse item copy while corpse slots contain the replacement copy with the same ID. The fix runs immediately after inventory replacement, only for `ObservedInventoryController`, only when both roots are equipment with the same nonempty ID, and only when their references differ. It changes the root alias only: native corpse creation and inventory operations retain responsibility for addresses, slots and world registration. Hosts, standalone controllers and different inventory IDs are not changed. Successful repairs enter the bounded journal as `lookup-root synchronized=True`; guarded mismatches report a warning. This is independent of the detachment toggle because a stale registered owner also affects normal item lookup.

The adapter observes `FikaClient.HandleInventoryPacket(in InventoryPacket, FikaPlayer)` with a readonly prefix before stock execution. It does not hook the removed `OnInventoryPacketReceived` method or alter stock inventory packet results. Installed-type regression fixtures cover stale item lookup, same-ID root replacement and removal; successful fixtures do not replace live multi-peer pickup checks. Diagnostic bounds and reproduction requirements are in the [runtime checklist](RPT-Ragdoll-Runtime-Inspection.md#headgear-detachment-and-pickup).

## Corpse identity, queues and admission

`CorpseInteractionTarget` binds captured intent to the corpse and its impact lifetime, not just a reusable network ID. Head impacts retain point and direction in head-local space before transport. Pending corpse intents are bounded to 128 with a ten-second deadline. A deferred intent receives its outgoing sequence when it is actually sent, avoiding stale sequence allocation while identity is unresolved. The processing result distinguishes applied, stale, waiting-for-identity, waiting-for-death-data and rejected.

[CorpseInteractionTarget](../src/Rupture/Ragdolls/Classes/CorpseInteractionTarget.cs) is the retained identity/anatomy value shared by impact and geometry paths. [BodyHierarchyPath](../src/Rupture/Ragdolls/Classes/BodyHierarchyPath.cs) supplies identical relative body names to the coordinator and external rig bindings, including `.` for the root and null for a transform outside that hierarchy.

[RagdollCoordinator](../src/Rupture/Ragdolls/Classes/RagdollCoordinator.cs) keys state by profile/player/death sequence and maintains event deduplication and terminal tombstones. Head events permit order-independent admission. Pending head/limb reconstruction expires after ten seconds. [EventAdmission](../src/Rupture/EventAdmission.cs) records successful admission rather than marking an event consumed before its local binding can succeed.

A provisional native death sequence can be replaced by the authority identity for the same player without erasing the impact lifetime/totals needed to process already captured work. Real corpse retirement invalidates that lifetime so old requests cannot target a later corpse. An observer geometry failure/expiry logs and preserves its intact limb collider; it does not turn failed rendering into a phantom missing hit target.

## Motion, prediction and settle

Lifecycle/force/destruction descriptors use reliable ordered transport by default. Live pose snapshots use unreliable transport and are published from completed external steps at up to 30 Hz, plus a committed bootstrap snapshot. Each carries snapshot sequence, authority simulation time, represented delta, chunk metadata and body state.

The current snapshot limit is 64 bodies and fourteen poses per unreliable chunk. The complete core fits one chunk; independent bodies use additional chunks. Core bodies and independent weapons are included. Generic registered detached motion can be eligible, but local procedural limb gibs explicitly opt out of both live and settle capture.

Clients validate and assemble out-of-order chunks before exposing any frame. Incomplete assemblies are discarded by update-cycle expiry; duplicate, inconsistent, stale or oversized sets do not partially move a corpse. A complete, locally bindable snapshot need not wait for reliable start metadata that arrives later.

[PendingPoseSnapshotAssembly](../src/Rupture/Ragdolls/Classes/PendingPoseSnapshotAssembly.cs) owns the chunk slots, matching timing/count metadata and complete-array construction for one snapshot. The coordinator owns corpse admission, sequence barriers, discard counters, update-cycle expiry and playback publication. Duplicate chunks leave the earlier slots intact; inconsistent metadata or a complete count mismatch discards the assembly.

Initially the client predicts with local dynamics. The first usable authority set atomically takes kinematic ownership, anchoring from the current visible prediction, and subsequent complete sets are interpolated on the authority timeline. Playback delays by one observed authority interval (with a nominal minimum), retains at most eight frames and drops obsolete backlog. It is full-state playback, not separate per-bone spring correction. The next step applies pending authority state after completing prior local simulation and before submitting again.

A locally originated new impact immediately begins a prediction epoch and raises a snapshot-sequence barrier. Point/radial/force echo records match body/path/local-point/force data with bounded lifetime/tolerances, preventing double application when the authority event returns. A newer complete authority state can then resume playback. The independent weapon can enter prediction without unnecessarily reactivating the entire corpse core.

Reliable settle carries an exact terminal state on the same authority timeline. Once playback reaches it, the represented bodies are parked at that exact state. Clients never choose terminal settle merely because their own sleep timer expired. Authority motion-epoch state and logs distinguish a reactivated corpse that is publishing new samples from a retained settled corpse with no active publication.

## Reset, reconnect and external assumptions

Raid reset clears pending impacts, effect state, coordinator identities/assemblies, predictions, corpse bindings and graphical work. Adapter `ResetRaid` is narrower than a complete network reset: peer/session/configuration state is reset on manager destruction/disposal. Network reset clears sequence/session bookkeeping and restores local configuration overrides; disposal removes retained hooks/event subscriptions.

There is no general historical corpse/geometry replay store. Living severed-bot state is refreshed periodically, and authority configuration is sent to later joiners; these do not reconstruct other historical injuries. Reconnect creates a new network session and cannot authorize retained descriptors from an earlier raid/session. Retained corpse state during one raid exists for its live lifecycle and stale-event rejection.

Threading boundary: Fika registered receive callbacks call core handlers directly, including Unity-facing resolution; the adapter contains no general main-thread dispatch queue. Correct callback thread scheduling therefore depends on the installed Fika manager's execution contract. Repository source establishes this dependency, not the scheduling behavior of every external Fika build. Verify the matched runtime before changing transport callback threading.

Use protocol fixtures, source wiring tests and native motion tests for their specific contracts. Live graphical-host/headless-host runs remain necessary for owner damage ordering, delayed corpse identity, dropped/reordered pose chunks, prediction echoes, weapon independence and terminal alignment. Position synchronization alone does not prove identical locally reconstructed geometry or motion inputs.
