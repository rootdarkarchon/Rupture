# Procedural geometry

Rupture derives injury surfaces from the equipped character's actual meshes and anatomical bones. Network packets carry injury decisions and seeds; local rendering, LODs and materials determine the generated meshes. Refer to the [architecture entry point](ARCHITECTURE.md), [lifecycle](ARCHITECTURE-LIFECYCLE.md) and [multiplayer](ARCHITECTURE-MULTIPLAYER.md) for their callers.

## Three ownership paths

| Path | Existing geometry | Generated geometry | Lifetime owner |
| --- | --- | --- | --- |
| Destructive limb cut | Native `SkinnedMeshRenderer` retained; `sharedMesh` replaced transactionally | Body-side cut mesh, skinned socket/cap and clothing closure | `LimbCutLifetime` after commit; request before commit. |
| Destructive head wound | Native head renderer tiers disabled | Replacement skinned head and rigid head-local cavity | `HeadWoundLifetime`; cumulative source/descriptor state retained by `HeadWoundSystem`. |
| Additive living fracture | Source mesh remains intact | Skinned surface collar and anchored exposed bone ends added to native LOD membership | `FractureVisual`, owned by living actor state. |
| Detached limb | Distal source fragment baked at the current pose | Static rendered gib plus convex query/proxy geometry | Gib lifetime component for meshes; external runtime for simulation actor. |

Ownership rule: original assets, original bones, native materials and native renderers are borrowed. Only Rupture-created resources are destroyed. A successful adoption must transfer every staged resource to a definite cleanup owner.

[RendererSource](../src/Rupture/Dismemberment/Classes/RendererSource.cs) shares named/group LOD lookup, property-block transfer and blend-shape copying. It owns no resources. Unknown LODs remain unknown to the caller: limb capture defaults to tier zero while head capture requires a confirmed tier zero. Head and limb renderer settings, culling and LOD mutation policies remain distinct.

## Limb request pipeline

[LimbDismembermentSystem](../src/Rupture/Dismemberment/Classes/Limbs/LimbDismembermentSystem.cs) owns the FIFO, per-corpse/limb reservations and one active request. Authority policy calls `RequestSever`; completion determines whether a sever event can be published. Headless handling resolves anatomy, marks the logical sever and disables the corresponding native collider without allocating the graphical pipeline.

Its [Worker partial](../src/Rupture/Dismemberment/Classes/Limbs/LimbDismembermentSystem.Worker.cs) groups process-lifetime job execution/publication, and its [Decals partial](../src/Rupture/Dismemberment/Classes/Limbs/LimbDismembermentSystem.Decals.cs) groups native-paint transfer and postcommit diagnostics. These retain the same private request/work types and call order. Main-thread staging, cancellation, adoption and deferred cleanup remain together in the primary file; the worker and cosmetic paths do not become independent schedulers.

```mermaid
flowchart LR
    Reserve[Reserve corpse / limb] --> Prepare[Prepare: capture source and submit geometry]
    Prepare --> Materialize[Materialize worker result on main thread]
    Materialize --> Bake[BakeAndColliders: posed gib and proxies]
    Bake --> Wait[WaitForUnityColliderBake]
    Wait --> Register[RegisterAndCommit: external actor ready]
    Register --> Commit[Adopt body meshes / attachments]
    Commit --> Lifetime[Lifetime owns resources]
    Prepare -. failure / cancellation .-> Cleanup[Release staging; retain intact body]
    Register -. failure / cancellation .-> Cleanup
```

The frame budget is soft: default 2 ms, configurable from 0.5 to 8 ms. `Update` can advance multiple stages or materializations while progress and budget permit, but runs one active request and commits at most one request per update. Atomic Unity/native operations can overrun the budget.

### Discovery, immutable source and anatomy

Capture walks `PlayerBody.BodySkins` and their renderer tiers. It accepts readable skinned meshes and records actual renderer LOD, bones, bind poses, indices, normals, tangents, colors, UV0–UV7, weights and blend shapes before any destructive replacement. Original source snapshots are cached for the raid. Subsequent cuts rebuild from those snapshots plus previously committed sever records rather than cutting an already truncated mesh.

`CorpseLimbContext` supplies the native chain, first through `PlayerBones` and then through known hierarchy names retained before player disposal. [MeshFrameTransform](../src/Rupture/Dismemberment/Classes/MeshFrameTransform.cs) and the cutter work in explicit bind/source/posed frames. Missing bind endpoints in a particular clothing renderer can be supplied from other retained renderer frames. An unsupported anatomical mapping is a failure, not permission to invent a bone or cut a nearby unrelated mesh.

[ProceduralLimbCutGeometry](../src/Rupture/Dismemberment/Classes/Limbs/ProceduralLimbCutGeometry.cs) captures `SourceData` on the main thread. It retains immutable arrays and lazily builds locked adjacency/classification caches. The shared serial [ManagedGeometryWorker](../src/Rupture/Dismemberment/Classes/ManagedGeometryWorker.cs) executes limb `GeometryJob`, head geometry, and collider-bake work; volatile completion publishes finished results. It is a retained below-normal background worker with process lifetime. Cancellation invalidates requests, not the immutable arrays a running job still holds.

### Classification and cut surfaces

Selection combines bone weights, mesh connectivity, anatomical chain segments and a finite capsule region. Distal hand/foot seeds help find the intended component. Strongly target-weighted disconnected triangles can remain eligible outside the capsule; triangles whose corners are dominated by other body parts remain protected. A proximity-only selection would incorrectly consume adjacent torso or clothing surfaces.

The `ProceduralLimbCutGeometry` partial in [ProceduralLimbCutBatch.cs](../src/Rupture/Dismemberment/Classes/Limbs/ProceduralLimbCutBatch.cs) tries the requested and alternate sever levels with segment-fraction offsets `0`, `-0.05`, `+0.05` (nominal and ±5 percentage points along the selected bone segment). Each candidate is shared across all participating renderers and LODs. Proximal placement is at 50% and distal placement at 15% of the respective segment, with a 10 cm removed band. Preparation requires aggregate body boundary and LOD0 distal-gib support, not an independently plausible cut in only one clothing layer. Failed geometry candidates are cached separately from seed-dependent final wound construction; cancelled work is not a geometry failure cache entry.

Triangles are clipped against two indexed planes with shared edge intersections. Existing channels and skin weights are interpolated at new vertices. The same type's boundary partial in [ProceduralLimbCutBoundaries.cs](../src/Rupture/Dismemberment/Classes/Limbs/ProceduralLimbCutBoundaries.cs) distinguishes source garment openings from selection tears. Open/branching or distant invalid cut boundaries are rejected; recognized original openings can receive cap-only closure. Closure is not an arbitrary bridge across disconnected body regions.

### Clothing and virtual anatomy

The cutter partial in [ProceduralLimbClothIntegration.cs](../src/Rupture/Dismemberment/Classes/Limbs/ProceduralLimbClothIntegration.cs) connects runtime cut loops to the shared [limb geometry sources](../unity/RuptureWound/Assets/Rupture/Limbs). `LimbClothingProfiles` contains calibrated anatomical reference profiles for the side/level/offset cases and bare-region fingerprints. The loose-clothing threshold is 110% of the reference. Runtime selection still uses the actual garment boundary; profiles bound internal anatomy rather than replacing a garment with a template mesh.

The batch selects primary loops per LOD and shares an internal core, preferring LOD0. Skinning weights are remapped through actual bone names between layers. `ProceduralLimbWoundGeometry` creates the flesh/bone wound surface; anatomical bone count is one for upper segments and two for lower segments. Its `ProceduralLimbClothGeometry.cs` partial adds recessed/folded garment geometry around that core. Accessory layers can use a dark closure. A non-star-shaped boundary or failed optional recess can fall back to the simpler accepted wound construction without invalidating a sound anatomical cut.

“Virtual anatomy” here means the procedurally bounded flesh/bone core inside actual limb clothing. It is not a separate animated body, independent full anatomy simulation, or abdominal organ system. Keep the [clothing calibration guide](Limb-Clothing-Folds.md) and [harness](../unity/RuptureWound/CLOTHING-HARNESS.md) as validation tools for this production path.

### Skinning, gib baking and colliders

Main-thread materialization uploads body and socket meshes and creates temporary distal skinned renderers using the captured bones/bind poses, native material properties and current blend-shape weights. Attached surfaces remain skinned to the corpse.

Each LOD0 distal renderer is baked once at the current ragdoll pose. The resulting detached mesh is recentered into its center-of-mass-local frame and rendered with a static `MeshFilter` / `MeshRenderer`. Segment proxy hulls come from the undecorated anatomical fragment, not clothing folds or wound decoration. Each proxy is bounded to 42 vertices / 80 triangles and checked for topology, size and pose/COM consistency.

Unity convex prebake is an explicit worker exception to ordinary Unity-object restrictions: `Physics.BakeMesh` runs with `MeshColliderCookingOptions.None`; main-thread collider attachment uses matching settings. Meshes cannot be destroyed while that bake is in flight.

The external runtime's transient-convex ticket creates the standalone actor asynchronously. A local gib can remain parked, kinematic and collision-disabled for up to ten seconds while waiting for its corpse rig. The ordinary standalone path gives it environment contacts only, excluding player/corpse/gib contacts. Procedural gib motion is local, absent from authority pose/settle payloads, and not allocated on headless peers. Backend failover can restore ordinary Unity dynamic behavior, so environment-only contacts are a property of the external path, not an unconditional fallback guarantee.

### Commit, rollback and cleanup

`CommitPrepared` uses [CommitTransaction](../src/Rupture/CommitTransaction.cs) to set each native renderer's prepared `sharedMesh` and replace its LOD attachments. A failing operation is included in reverse rollback; rollback failures are collected rather than silently hiding earlier mutations. Native renderer identity is preserved for limb cuts.

After adoption succeeds, `request.Committed` is the boundary: sever records and owned resources transfer to `LimbCutLifetime`, then superseded owned meshes can be retired. Collider disabling, deferred native decal work and blood start are postcommit actions. Their failure is reported without pretending that the committed body cut was rolled back.

[LimbCutLifetime](../src/Rupture/Dismemberment/Classes/Limbs/LimbCutLifetime.cs) stores original meshes and LOD membership, owns generated body/socket/debug resources, mirrors source enabled/`forceRenderingOff` state and restores borrowed state on destruction. `LimbGibMeshLifetime` destroys baked and proxy meshes. `LimbDecalTextureTransfer` gives copied native paint textures their own cleanup owner. Replaced attachments must be detached from LOD membership before destruction.

Before commit, any failure/cancellation destroys staging and releases reservations. Deferred gib cleanup waits for Unity collider prebake to complete. External tickets separately arbitrate actor publication versus cancellation. An observer whose cut expires or fails retains the intact native limb collider. A diagnostic capture is a separate bounded writer (four outstanding exports); it is not part of the geometry worker or commit requirement.

## Head entrance, exit and cumulative wounds

[HeadWoundSystem](../src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundSystem.cs) owns per-corpse source state, descriptor queues and up to sixteen policy attempt ordinals. Authority reserves before local graphical work; failure or headless handling still consumes the attempt. Each graphical peer adopts at most four successful wound descriptors; failed attempts do not consume those four visible slots. Projectile deduplication precedes headgear/wound handling. Native headgear throws are an independent bounded sequence, not conditional on successful mesh cutting.

The first graphical wound locates the actual readable LOD0 head intersected by the trajectory and bakes it into head-local space. Entry resolution searches along the same trajectory within bounded tolerances, with a nearest-surface fallback and bounded full-trajectory search. The furthest valid crossing resolves an exit. Without a real terminal surface, a bounded internal cone does not invent an external exit collar/flap.

The main-thread [capture stage](../src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundSystem.Capture.cs) bakes eligible LOD0 renderers and snapshots mesh channels, bone references, and bind transforms together. [HeadWoundGeometryJob](../src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundGeometryJob.cs) runs frame conversion, entry/exit selection, clipping, cumulative union, bounds, and strict validation on the shared serial geometry worker. The immutable selected source is retained as managed data. One head job is admitted at a time; Update polls without waiting, preserving ordinal order and the existing visible head until adoption. Unity mesh upload, renderer/LOD state, decals, and destruction remain main-thread operations. [HeadWoundSkinning](../src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundSkinning.cs) binds the replacement skin while cavity geometry follows the retained head transform rigidly.

[ProceduralHeadWoundGeometry](../unity/RuptureWound/Assets/Rupture/HeadWounds/ProceduralHeadWoundGeometry.cs) locally subdivides source triangles and clips an implicit tapered wound volume. Bounded intersection search shares edge crossings and interpolates source channels/weights. Unchanged source vertices remain a prefix. Cut components are ordered along the trajectory; paired closed crossings define canal spans through actual solid regions, leaving separated-shell gaps empty. Entry/intermediate lips and a resolved terminal collar with eight thick flaps are generated from those boundaries.

`TryGenerateMultiple` rebuilds from the immutable source plus all committed descriptors and the candidate wound. The geometry/workspace partials in [ProceduralHeadWoundUnion.cs](../unity/RuptureWound/Assets/Rupture/HeadWounds/ProceduralHeadWoundUnion.cs) reuse individual fragments, reclip overlapping skin, subtract overlapping volumes from canals/lips and assign coincident surfaces once. There is no runtime third-party CSG dependency. Individual construction caps generated head and cavity vertices at 4,096 each; cumulative construction caps generated skin and cavity vertices at 16,384 each. Invalid boundaries or caps reject the candidate without discarding earlier committed wounds. Union scratch and timing data belong to each workspace; jobs never share mutable scratch. Adoption rechecks corpse-state identity, revision, ordinal, head/root, native mesh and bone palette. Expiry, removal and reset cancel publication without freeing arrays still used by an executing job. A destroyed wound lifetime invalidates its captured source and revision while preserving admission policy.

[HeadWoundLifetime](../src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundLifetime.cs) owns the replacement skinned renderer and cavity mesh/renderer, disables all native head tiers and reasserts replacement visibility late. It does not generate lower wound LODs or replace gameplay head colliders. First adoption creates a lifetime after validation; later `ReplaceMeshes` restores prior meshes/bounds on failure. This is a dedicated adoption path, not the limb `CommitTransaction`.

Native paint is copied and entry/exact-exit decals can be deferred for up to 120 frames. Blood waits briefly for the wound's resolved exit. These are postcommit cosmetics. On removal, original renderer enabled states are restored, owned meshes are destroyed, and retained source/descriptor state is retired. `CorpseHeadRetention` makes policy cleanup independent of the disposed Player, including headless corpses.

### Bounded head wound recovery

The authority owns attempt ordering; a headless host does not infer graphical success. Clients apply their own successful subset in that order. Deterministic candidates do not guarantee identical geometry across different source meshes or captured poses. Pending work is discarded once four wounds are adopted, and attempt ordinals are never reused.

The worker first tries the best original-trajectory candidates (at most two full builds). On failure it resolves the nearest head surface, bounded to half the head bounds' extent magnitude (minimum 30 mm, maximum 80 mm), and tests inward tilts at 35% and 70% toward the surface normal (at most two builds). It next tries the inward normal (one build), then an entry-only closed crater (one build). At most eight captured sources are searched and at most six complete mesh builds occur per attempt. Original trajectory projection retains its previous limits; the wider snap applies only to recovery. Invalid directions and nonfinite inputs still reject.

Every candidate starts from captured immutable skin plus previously adopted descriptors. Failed candidates never publish meshes or mutate committed wounds. Recovery is entirely on the managed geometry worker; Unity capture and final adoption stay on the main thread. Cancellation, expiry, source renderer and corpse revision checks continue to reject stale work.

A crater has no exit lip or exit stream, tapers to 35% of the entry radius and has an intended depth of 8–25 mm, also bounded by the supplied maximum depth. It must have valid indices, skinning, retained skin, an entry lip, an open entrance and a closed terminal surface. Combined-wound refinement leaves crater caps intact unless another intersecting wound cuts through them. All finite-data, vertex-count and mesh-adoption checks remain active. The original four-descriptor union limit is unchanged.

Postmortem blood and decals use the accepted entry, exit and direction. The delayed lethal stream resolves to the accepted exit when present; otherwise it resolves outward from the accepted entrance. Successful fallbacks emit a bounded `Head wound recovered` warning with mode, number of full builds, snap distance and angle. Failures continue to report `Procedural head wound skipped`. Neither requires enabling general debug logging.

Validation covers deterministic off-surface recovery, distant-hit rejection, actual crater fallback, mixed crater/through geometry, rejection preserving committed meshes, four visible wounds across five attempts, no fifth build, cancellation and headless exclusion. Worker timings are fixture measurements, not live raid performance or visual acceptance. Live Fika checks should cover grazing and jaw/ear hits, repeated overlapping hits, ground-level corpse poses and both clients retaining ordinary headgear loot behavior.

`scripts/Test-HeadWoundWorker.ps1` runs the existing topology benchmarks plus exact synchronous/worker channel comparisons, concurrent-workspace isolation, cancellation and exception recovery, ordered multi-wound adoption, stale/expired/reset/removal rejection, headless admission, moved-skeleton skinning and mesh cleanup in Unity. Its cold and warm timings distinguish worker computation, enqueue, mesh application and pending Update polling. These controlled fixtures do not establish live EFT/Fika appearance or raid frame times.

## Living bot leg cuts

[BotSeverStateService](../src/Rupture/Health/BotSeverStateService.cs) admits eligible living AI legs independently of corpse momentum. Bullet buildup requires an already blacked leg; explosions use actual damaging leg hits and distance instead. Healing and completed condition treatment invalidate pending reservations. Geometry and temporary physics must be admitted before authority health changes; failed cuts preserve the intact limb and health. Headless follows logical eligibility and physics admission without building graphical meshes.

A committed cut removes the missing segments and their joints from the temporary living rig. Native skeletal transforms remain available for skinning and later corpse construction. Local and observed graphical peers retain the committed cut across death; pending living cuts are cancelled before corpse handoff. The fall is not a fabricated death or Fika downing. See [living severing](Living-Bot-Leg-Severing.md) for treatment, replication and topology details.

## Additive living fractures

[FractureSystem](../src/Rupture/Dismemberment/Classes/Fractures/FractureSystem.cs) scans living characters and binds native fracture effects. Blood effects enable this graphical path; dismemberment enablement is not its gate. Appearance is deterministic from profile and canonical body part, with one/two sites and anatomy-appropriate bone count. Native effect replication supplies observer state; no fracture-specific mesh or packet is sent.

Source capture covers equipped body/feet and arm sleeves, groups compatible LOD/view/site renderers, and retains immutable meshes. [FractureSurface](../src/Rupture/Dismemberment/Classes/Fractures/FractureSurface.cs) chooses the outermost actual covering surface, with stable hierarchy ordering for ties. Selection uses meaningful enabled renderer state, not camera visibility. Incompatible LOD arrangements fail explicitly. A 2 ms soft queue budget and one-source work steps amortize capture/build; indivisible operations can exceed that budget. Each mask retains up to sixteen variants and remembers failures.

[FractureVisual](../src/Rupture/Dismemberment/Classes/Fractures/FractureVisual.cs) adds a weighted collar and bone geometry. Bone ends use added anchors updated to posed roots in `LateUpdate`; the collar retains source weights. Only the new renderers are added to the appropriate native LOD entries; global LOD bounds and source meshes are not replaced. No collider or external physics actor is created.

One local blood source per site is shared across LOD/view variants. Disposal removes that source, added LOD membership, anchors and owned meshes. Treatment, controller replacement, death, lost actor/world and reset retire these living visuals; they are not transferred into corpse fracture state.

## Maintenance boundaries

Important invariant: graphical success on one renderer/LOD is insufficient for a destructive limb batch. Preserve complete-source capture, shared candidate selection, transaction rollback and postcommit ownership together.

Do not access Unity renderer/mesh APIs from managed geometry jobs, destroy collider meshes under an active bake, or let an expired network event adopt results into a new corpse. Test geometry topology and cleanup with the existing fixtures, then inspect real garments, multiple cuts, head overlaps and LOD transitions in EFT. Passing synthetic or editor geometry gates does not establish live visual acceptance.
