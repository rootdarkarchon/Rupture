# Rupture wound resources

Rebuild and reopen-verify all four wound shaders with Unity 2022.3.43f1:

```powershell
.\scripts\Build-WoundBundle.ps1
```

The isolated project is generated under `artifacts/RuptureBundle`. The bundle contains `Rupture/HeadWoundInterior`, `Rupture/LimbSeverSurface`, `Rupture/PulsatingBloodStream`, and `Rupture/ClothingSoak`. Runtime materials reuse EFT's loaded textures; those textures are not redistributed.

Run `scripts/Test-ClothingSoaking.ps1` for the source-fingerprinted clothing transport, LOD/sleeve correspondence, treatment persistence and allocation report. It uses synthetic garments, EFT boundary doubles, and the installed native blood texture, projection shader and compositor. Initial visible coverage, timed downhill growth, intensity bounded by the native blood core, and unchanged texels after repeated updates are required. Real garment mapping, raid appearance and GPU cost remain separate verification.

Treat adjacent benchmark reports as measurements of their recorded source/bundle hashes and timestamps. Rebuild and revalidate after relevant source changes; presence of a report is not proof it matches the current tree. Bundle verification and CPU-versus-instanced parcel image comparisons are tied to the compiled bundle's SHA-256 hash. The blood-stream report measures CPU preparation and uploads, not GPU frame time.

See [resource ownership](../../docs/ARCHITECTURE.md#configuration-and-resources), [blood rendering](../../docs/ARCHITECTURE-BLOOD.md), [procedural geometry](../../docs/ARCHITECTURE-GEOMETRY.md) and [limb clothing calibration](../../docs/Limb-Clothing-Folds.md).
