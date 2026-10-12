# Limb clothing calibration and inspection

Use this guide to maintain the measured anatomical profiles and review real garment cuts. The runtime algorithm, skinning, shared internal cores, fallback and ownership live in [ARCHITECTURE-GEOMETRY.md](ARCHITECTURE-GEOMETRY.md#clothing-and-virtual-anatomy).

## Calibration inputs

`Mannequin_body_LOD0` and `Mannequin_legs_LOD0` provide the bare anatomical reference. The generated table contains 24 arm/leg, proximal/distal, stump/gib and nominal/−5%/+5% segment-fraction cases. Mirrored measurements are normalized by the selected bone segment length; runtime skeleton scale then determines core dimensions. The loose-clothing threshold is 110% of the applicable reference.

Garment outliers do not lower the anatomical baseline. Missing required mirrored references is a calibration error. The [review manifest](limb-clothing-review.json) records source hashes, reviewed flags and region-specific bare exceptions. Exceptions identify an exact mesh, limb region and face; they do not make an entire garment bare. Unknown meshes follow the measured threshold.

## Reproduce

Set up the [clothing harness](../unity/RuptureWound/CLOTHING-HARNESS.md) and run `-rptCalibrate`. Use Python with NumPy for calibration and NumPy/Pillow for review images:

```powershell
python scripts/Calibrate-LimbClothing.py <calibration-run>/summary.json `
    --output artifacts/cloth-calibration.json `
    --review docs/limb-clothing-review.json `
    --emit-cs unity/RuptureWound/Assets/Rupture/Limbs/LimbClothingProfiles.Generated.cs
python tests/LimbClothingCalibration.Tests.py
```

`scripts/Review-LimbClothing.py` creates source/cut review images. Preserve source hashes and inspect flagged actual surfaces before modifying measured values or exceptions. Diagnostic-export retry reports must match the compiled sources of the original run. Keep game-derived fixtures/captures under ignored `artifacts/`; commit only the intended measured table and reviewed manifest.

After shader changes, run `scripts/Build-WoundBundle.ps1` with Unity 2022.3.43f1. This rebuilds and reopens the bundle before replacing the package input.

## Inspect the result

Compare body and gib faces across LODs and clothing layers. The primary surface supplies one internal core; other layers connect to it. Inspect seam containment, flesh/bone placement, cloth recess, accessory closure and skinning during pose changes.

Harness/runtime `Clothing` diagnostics include status/reason, measured diameter, threshold, ellipse dimensions, recess depth, containment scale and rim-movement scale. Optional clothing fallback must preserve an otherwise accepted cut and report its reason. It must not weaken cut-boundary validation.

Catalog case counts depend on the extracted fixture set. Use the run's manifest, source hashes and expected/completed totals rather than a dated count as the current baseline. Editor calibration does not establish live equipment combinations, material appearance, decal behavior or Fika acceptance.
