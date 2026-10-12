# Rupture Unity resources and validation

These inputs target Unity 2022.3.43f1 and the built-in render pipeline.

From the repository root:

```powershell
.\scripts\Build-WoundBundle.ps1
.\scripts\Test-WoundBenchmarks.ps1
```

The bundle build reopens the compiled output and verifies exactly four named Rupture shaders. Its verification report records the Unity version, timestamp, and SHA-256. The benchmark script runs fresh head-wound, blood-stream, and limb-wound cases in an isolated project, validates their geometry/timing/allocation gates, and updates reports in `assets/bundles`. Clothing soaking has its own `scripts/Test-ClothingSoaking.ps1` validation path.

For visual inspection, open `artifacts/RuptureWoundValidation` in the pinned editor. The **Tools > Rupture** menus expose head-wound previews, blood-stream examples, and the limb-sever matrix. Inspect common, oblique, non-through, and separated-shell head shots for connected canal spans, an empty gap between separated surfaces, and the bounded terminal collar/flaps. See [geometry](../../docs/ARCHITECTURE-GEOMETRY.md), [blood](../../docs/ARCHITECTURE-BLOOD.md) and the [runtime checklist](../../docs/RPT-Ragdoll-Runtime-Inspection.md).

For real garment cuts, use the [clothing cutter harness](CLOTHING-HARNESS.md). It compiles the production cutter into its own editor assembly and runs independent side/level cases across every fixture and LOD.

Only shaders are bundled. Preview textures are temporary synthetic resources; runtime EFT textures are resolved in memory and are not redistributed. Editor measurements do not establish live raid or multiplayer acceptance.
