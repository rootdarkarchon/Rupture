# Performance evidence and measurement limits

This guide consolidates historical measurements that explain implementation choices. Values retain their original workload, date or snapshot, units and limitations. They have not been rerun by this documentation consolidation and are not current FPS promises. Measurements from different workloads must not be combined into a before/after result.

Current runtime counters and inclusive-timing interpretation are documented in the [architecture guide](ARCHITECTURE.md#logging), [blood guide](ARCHITECTURE-BLOOD.md) and [physics guide](ARCHITECTURE-PHYSX.md). Maintained reports in [assets/bundles](../assets/bundles/README.md) remain tied to their recorded inputs/hashes. Existing raw logs, captures and experiments under ignored `artifacts/` are unchanged.

## Historical native physics captures

Historical measurement artifact. The values below describe the named local captures, not current Rupture architecture, current performance or completed acceptance of the present backend. Implementation prescriptions have been removed from this report.

| Capture | Mean frame | `SyncTransformsClass.Update` self/frame | Recorded observation |
| --- | ---: | ---: | --- |
| `2026-08-22_201638` | 21.445 ms | 1.6845 ms | Physics simulation active for most of the capture. |
| `2026-08-22_202051` | 21.889 ms | 2.6496 ms | Physics simulation active throughout. |
| `2026-08-22_202233` | 17.594 ms | 0.0004 ms | Update gate inactive; no `Physics.Simulate` call observed. |
| `2026-08-22_202708` | 21.306 ms | 1.8023 ms | Physics simulation activated after capture start. |

The captures recorded one `SyncTransformsClass.Update` call per rendered frame. The measured cost did not scale monotonically with the approximate native corpse-body population. AI workload was uncontrolled, so total-frame differences cannot be attributed to corpse physics from these captures.

The original investigation also observed two Fika corpses asleep after roughly three to six seconds while retaining twelve tracked bodies in EFT's support list through thirty-second captures. This is a recorded observation of that environment, not a claim about the current integration.

These measurements motivated separating temporary corpse dynamics from EFT's default physics workload. They do not quantify the improvement of the present standalone backend, whose ownership and scheduling differ. Total frame time also contains unrelated work.

## Rejected head-wound CSG experiment

Historical rejected-spike measurements only. This library is not a current runtime dependency. The figures describe the rejected spike rather than production behavior.

Unity: `2022.3.43f1`

Source pin: `pb_CSG` 2.0.0 commit `9b35e1c90ee2d3fe1b7b881a25242f47e975d94b`

### Recorded result

Rejected as the runtime mesh-generation backend because it exceeded the spike's performance constraints.

After correcting the synthetic ellipsoid's triangle winding, the high-vertex case took 110.376 ms against the 100 ms synchronous limit and allocated 79,347,712 bytes of managed memory for one operation. The common case allocated 7,852,032 bytes. Limiting generation to one wound per frame does not remove that single-frame hitch or allocation pressure, so the performance gate failed.

The output also retained no cutter/wound material in either case. That was a separate recorded correctness failure; it did not change the performance rejection. The 32-bit index adaptation passed.

| Case | Source vertices | Source triangles | Result vertices | Result triangles | Time | Managed delta | Geometry/material validation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| common-head | 1,986 | 3,968 | 10,695 | 3,565 | 11.541 ms | 7,852,032 bytes | Failed: no wound-interior material |
| high-vertex-head | 18,242 | 36,480 | 97,878 | 32,626 | 110.376 ms | 79,347,712 bytes | Failed: no wound-interior material and over 100 ms |

## Superseded specialized head-wound benchmark

The following Unity 2022.3.43f1 report was generated on 2026-08-26 at `10:59:09.6675394Z`. It passed its recorded timing/allocation/index gates but was superseded after visual review by the topology-independent overlapping-lip revision. Its small timings cannot be used as a baseline for current cumulative wound geometry or as proof of accepted appearance. Preserve the raw fields here so the old standalone JSON is unnecessary:

```json
{
  "unityVersion": "2022.3.43f1",
  "generatedUtc": "2026-08-26T10:59:09.6675394Z",
  "allPassed": true,
  "index32Passed": true,
  "status": "superseded by topology-independent overlapping-lip revision after visual review",
  "cases": [
    {
      "name": "common-head",
      "sourceVertexCount": 1986,
      "sourceTriangleCount": 3968,
      "candidateTriangleCount": 240,
      "removedTriangleCount": 67,
      "resultHeadVertexCount": 1986,
      "resultHeadTriangleCount": 3901,
      "cavityVertexCount": 136,
      "cavityTriangleCount": 224,
      "bakeOrAcquisitionMilliseconds": 0.054900000000000007,
      "triangleClassificationMilliseconds": 1.4473,
      "cavityGenerationMilliseconds": 0.0608,
      "unityMeshApplicationMilliseconds": 0.3018,
      "totalMilliseconds": 1.9325,
      "managedDeltaBytes": 0,
      "maximumGeometryMilliseconds": 10.0,
      "maximumTotalMilliseconds": 25.0,
      "maximumManagedBytes": 524288,
      "valid": true,
      "failure": ""
    },
    {
      "name": "high-vertex-head",
      "sourceVertexCount": 18242,
      "sourceTriangleCount": 36480,
      "candidateTriangleCount": 1872,
      "removedTriangleCount": 442,
      "resultHeadVertexCount": 18242,
      "resultHeadTriangleCount": 36038,
      "cavityVertexCount": 136,
      "cavityTriangleCount": 224,
      "bakeOrAcquisitionMilliseconds": 0.0935,
      "triangleClassificationMilliseconds": 11.4176,
      "cavityGenerationMilliseconds": 0.059,
      "unityMeshApplicationMilliseconds": 0.2267,
      "totalMilliseconds": 11.804400000000002,
      "managedDeltaBytes": 12288,
      "maximumGeometryMilliseconds": 25.0,
      "maximumTotalMilliseconds": 50.0,
      "maximumManagedBytes": 1048576,
      "valid": true,
      "failure": ""
    }
  ]
}
```

## Historical parcel CPU preparation and image equivalence

The following belongs to the 2026-09-06 Rupture 1.0.0 release investigation. References to tubes/motes and budgets describe that snapshot, not the current presentation pipeline. CPU preparation/upload and GPU execution are separate costs.

At this snapshot, parcel geometry used an immutable 26-vertex, 48-triangle mesh with a 64-byte captured-state upload per instance. The vertex shader evaluates the same seeded shape and ballistic trajectory. Tubes and motes retain their CPU mesh path; unsupported instancing retains CPU parcel generation. No parcel simplification or lower-detail geometry was introduced.

The fresh CPU preparation/upload benchmark measured 720 frames per case:

| Emitters | Average CPU ms | p99 CPU ms | Steady-state managed bytes |
| --- | ---: | ---: | ---: |
| 1 | 0.0269 | 0.0906 | 0 |
| 8 | 0.1641 | 0.3448 | 0 |
| 24 | 0.4614 | 0.6712 | 0 |

The initial pre-optimization run failed at approximately 15.20 ms average / 16.19 ms p99 for 24 emitters. The refreshed benchmark exercises captured emission history and instance uploads. This comparison is editor CPU evidence, not a controlled raid FPS or GPU speedup measurement.

The recorded CPU gates were below 0.75 ms average, below 2 ms p99, and zero steady-state managed allocation. Geometry accounting explicitly distinguishes unique uploaded meshes from expanded instance geometry: the 24-emitter case uploads at most 2,330 mesh vertices and 3,120 mesh triangles plus 138,240 instance bytes, while rendering up to 58,464 vertices and 106,752 triangles across the complete scene geometry. Instancing reduces CPU transformation and transfer work, not visible triangle count. GPU execution time is not measured by this benchmark.

Twelve CPU-reference versus instanced render comparisons used the rebuilt bundle: four parcel ages and three seeds. All passed with a maximum mean RGB-channel error of zero and at least 542 covered pixels. This verifies those fixtures, not every in-game lighting or camera configuration. Reports are under `assets/bundles`; comparison images are under `artifacts/RuptureWoundValidation/ParcelVerification`.

## Historical allocation and refactor checks

The review at snapshot `1a7d76e` recorded exact float-hash parity, including signed zero/extreme values: hashing 100,000 vertices allocated approximately 1.20 MB for packed input instead of 300,000 temporary arrays. This is an allocation result, not a measured raid-frame saving. The same review exercised three cycles of 5,000 actors with shapes for handle reuse/lookup correctness; it supplied no actor-throughput timing.

The 2026-09-09 refactor based on `52a3c44` recorded zero allocated bytes in the unchanged-update fracture fixture (296 cases). Clothing soaking passed its then-current appearance/persistence/allocation gates (32,789 field checks and 49,175 runtime checks). Those pass counts identify controlled checks; they are not a present-frame allocation profile or an FPS improvement. Current shader/transport implementations must be verified with their current sources and resources.

## Casing prototype measurements

Release build, pinned PhysX 4.1.2, two dispatcher threads, PGS, 60 Hz, 360 steps. Each population is dropped simultaneously from 0.15 m plus layer height. Dispersed spacing is 0.12 m. The deliberately extreme pile uses 16 × 16 footprints spaced 0.036 × 0.012 m, with 0.013 m vertical layers. These are standalone native timings, **not live EFT FPS**.

Capsule/box comparison (mean milliseconds per step, including pose read):

| Population | Layout | Capsule ms | Capsule asleep after 6 s | Box ms | Box asleep after 6 s |
|---:|---|---:|---:|---:|---:|
| 1,024 | Dispersed | 0.066 | 1,024 | 0.067 | 1,024 |
| 1,024 | Dense pile | 1.006 | 1,022 | 0.873 | 1,024 |
| 4,096 | Dispersed | 0.132 | 4,096 | 0.147 | 4,096 |
| 4,096 | Dense pile | 19.099 | 19 | 24.420 | 4,096 |
| 8,192 | Dispersed | 0.316 | 8,192 | 0.367 | 8,192 |
| 8,192 | Dense pile | 87.993 | 24 | 123.630 | 5 |

The box costs more during the initial 4,096-object pile drop, but the entire pile settles; almost all capsules continue solving. This is the reason for the primitive choice. Neither primitive makes an extreme 8,192-object pile affordable.

A final box run separated cost categories (milliseconds; shared desktop, indicative wall-clock measurements):

| Population/layout | Create all | Mean simulation | Mean pose read | Last second simulation | Worst step | Explosion queries/impulses | First wake step/read |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1,024 dispersed | 1.50 | 0.064 | 0.0016 | 0.040 | 1.33 | 0.65 | 1.33 |
| 1,024 pile | 1.42 | 0.836 | 0.0059 | 0.040 | 8.55 | 1.03 | 11.59 |
| 4,096 dispersed | 5.35 | 0.138 | 0.0059 | 0.038 | 4.61 | 3.42 | 6.61 |
| 4,096 pile | 5.36 | 23.713 | 0.1055 | 0.057 | 395.15 | 6.07 | 218.80 |
| 8,192 dispersed | 19.68 | 0.382 | 0.0232 | 0.049 | 18.10 | 9.48 | 20.60 |
| 8,192 pile | 13.30 | 122.658 | 0.3482 | 61.427 | 2,362.81 | 11.79 | 728.01 |

The shared runtime's existing 500 ms owner watchdog can retire the feature and restore native corpse ownership under the extreme pile workload. The 4,096 default is a retained population budget, not a promise that a tightly concentrated pile has negligible cost.

Sustained firing fixture: 15 ejections/second into dispersed landing positions, initial velocity `(0.8, 2, 0)` m/s and spin 20 rad/s, followed by ten seconds to settle:

| Retained | Mean simulation ms | Mean pose read ms | Worst step ms | Mean create/actor ms | Remove all ms | Finally asleep |
|---:|---:|---:|---:|---:|---:|---:|
| 1,024 | 0.078 | 0.00047 | 0.222 | 0.0042 | 0.53 | 1,024 |
| 4,096 | 0.090 | 0.00050 | 0.256 | 0.0042 | 2.54 | 4,096 |
| 8,192 | 0.105 | 0.00053 | 0.349 | 0.0043 | 6.51 | 8,192 |

Main-thread Unity work, allocation, rendering, audible playback and frame waits require live profiling and are not established by these native fixtures. In particular, rendering thousands of separate pooled meshes may dominate sleeping physics cost.

See [casing ownership, reproduction and reported failure](Persistent-Casings.md) for current interfaces and the unresolved historical watchdog/collision observations. Native settling fixtures do not clear those live symptoms.

## Making a current performance claim

Use matched game/mod versions, settings, workload and diagnostic gates. Record package identity, frame/call denominators, warm-up and the capture's actual enabled features. Separate direct Rupture main-thread work, owner-thread simulation, waits, native EFT physics and GPU work. Inclusive child timings are already included in parents and must not be summed twice. Zero steady-state allocation in one fixture does not describe all admission, mesh-generation or teardown paths.

A build or regression pass establishes its own contract. For raid performance or visual acceptance, use the [runtime checklist](RPT-Ragdoll-Runtime-Inspection.md) and controlled live captures; missing GPU measurements mean unavailable evidence, not zero cost.
