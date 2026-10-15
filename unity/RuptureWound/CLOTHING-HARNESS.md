# Clothing cutter editor harness

An offline, inspectable run of the production limb cutter against installed EFT
upper/lower garments. The default project is isolated under
`artifacts/ClothingHarnessUnity`. Nothing is deployed into EFT.

The [geometry guide](../../docs/ARCHITECTURE-GEOMETRY.md) owns the runtime architecture; [clothing preparation](../../docs/ARCHITECTURE.md#clothing-bundle-preparation) describes the in-game readability loader. This guide covers operating and interpreting the offline harness.

## Setup

Run from the repository root with PowerShell 7:

```powershell
py -m pip install --target artifacts/clothing-python UnityPy==1.25.3
./scripts/Setup-ClothingHarness.ps1 -ExtractFixtures `
    -SptInstallPath <absolute-SPT-4.1-path> `
    -PythonDependencyPath (Resolve-Path artifacts/clothing-python)
```

Open `artifacts/ClothingHarnessUnity` with **Unity 2022.3.43f1**, then choose
**Tools > Rupture > Clothing Cutter Harness**. The initial fixture path
is filled automatically. **Run catalog** creates and cuts every catalog entry.

For LOD transition inspection, add `-AllLods` to setup when using
`-ExtractFixtures`, or use `scripts/Export-ClothingFixtures.py --all-lods`.
The exporter reads explicitly referenced renderer tiers; it does not fabricate
lower LODs for meshes such as the bare mannequins, which have only LOD0.
All tiers enter the same production transaction. Select the case object and change
**Preview Lod** in its Inspector to switch body renderers. Detached geometry is
built only from LOD0, matching runtime. Existing LOD0-only fixtures remain valid.

Catalog size depends on the extracted installation; use the fixture manifest and
run totals for coverage. Identical
bundle paths are deduplicated within each body-part category; all template IDs
and names remain in the manifest. The catalog includes PMC, scav, boss and zombie
entries. Each garment gets four independent clones, arranged left-to-right:

| Column | Side | Cut level |
| --- | --- | --- |
| 1 | Left | Proximal |
| 2 | Left | Distal |
| 3 | Right | Proximal |
| 4 | Right | Distal |

Upper garments exercise arms and lower garments exercise legs. There is no
accumulation of cuts across columns: each starts from a fresh source mesh and a
single cut specification. All referenced default LOD0 renderers stay together in
their garment case. Unaffected companion renderers remain visible. This models
the serialized default garment, not EFT's equipment-dependent mesh switches or
combinations of separately equipped upper/lower garments.

`-ProjectPath <path>` can install into an existing project of the same Unity
version. It writes only the dedicated harness folders, its module dependencies,
and local fixtures; the default isolated project avoids importing game code into
an existing editor project. Re-run setup after changing the harness UI/extractor.
The private Newtonsoft dependency is copied from the installed game.

## Locate a failure

1. Leave **Catalog name/path filter** blank for coverage, or enter a garment
   substring to run only matching garments. Select a deterministic **Wound seed**.
2. Wait for the run, or stop between cases and resume later in the same session.
   The editor processes one case per update; individual cutter calls are atomic.
3. Set the status filter to **Failed** and use **Apply visibility filter**.
   The result text filter also searches the stage, side, level and rejection
   message. **Unsupported** means no renderer had a usable weighted limb chain;
   it is recorded separately from a successful cut.
4. Click a result / **Frame clone**. The selected object's Inspector lists every
   renderer and its stage/reason. Its child hierarchy contains the intact
   reference, retained body, wound, detached limb, or partial exterior.
5. Toggle **Show Classification**, **Show Partial Exterior** and **Show Intact
   Reference** on that Inspector. The intact reference also appears automatically
   when a rejected case has no partial surface. Reference/classification toggles
   suppress overlapping result surfaces for inspection.
6. With Scene-view Gizmos enabled and the case selected, boundary lines show:
   **yellow** = cut-plane edges; **magenta** = selection interfaces;
   **cyan** = pre-existing garment openings; **green** = ordered loops. These
   traces describe the retained exterior before wound-rim disturbance.
   **Show Bone Palette** adds the actual palette names and reconstructed bind
   locations. Switch the Scene view to Shaded Wireframe to inspect triangles.
7. **Open case evidence** opens the exact result file. **Save preview PNG** exports
   the current visible geometry for sharing; Scene-view Gizmo lines are not part
   of this camera render.

The generated surfaces use neutral clothing and the production limb wound shader,
with small procedural preview textures. They
preserve source topology/UVs/submesh slots but do not extract EFT textures or
reproduce its shaders and decals. Skin weights, bind poses, all cutter-consumed
vertex channels, blend shapes and original prefab transforms are preserved.
Normalized integer vertex channels are converted according to their storage
format, including Kaban's UNorm16 weights. No remeshing, vertex welding, axis
conversion, or automatic bone-palette repair occurs during extraction.

## Adjust and rerun

The cutter implementation is **not forked**. Setup copies these ten production
sources into the isolated editor assembly:

- `ProceduralLimbCutGeometry.cs`
- `ProceduralLimbCutBoundaries.cs`
- `ProceduralLimbCutBatch.cs`
- `LimbCutCaptureSession.cs`
- `ProceduralLimbWoundGeometry.cs`
- `LimbSeverLevel.cs`
- `ProceduralLimbClothIntegration.cs`
- `ProceduralLimbClothGeometry.cs`
- `LimbClothingProfiles.cs`
- `LimbClothingProfiles.Generated.cs`

Only file-scoped namespaces are wrapped in braces for Unity's C# 9 compiler.
Source and compiled-copy SHA-256 hashes are checked before a run and each case.
Editing the game-side source therefore stops a running sweep rather than mixing
revisions silently. **Sync production sources** refreshes the ten copies and
triggers compilation. If a case was selected, its bundle becomes the catalog
filter, so the next run retests that garment. Scene clones are discarded across
assembly reload; previously written evidence remains on disk.

For a different seed without recompilation, select a case, change **Wound seed**,
and choose **Rerun selected**. The new result goes into a separate run directory;
old results and snapshots remain available.

The harness calls `TryCaptureClassificationWork`, the shared production
`TryBuildBatchWithRetries`, and `Result.TryMaterialize`. The four columns retain
their **requested** level. **Effective cut** shows the successful level and offset.
Candidates run in this order, stopping at the first complete transaction:

1. Requested level: nominal, -5% toward the torso, +5% toward the hand/foot.
2. Other level: nominal, -5%, +5%.

The 5% is measured against the length of the chosen bone segment. Every clothing
layer and LOD uses the same candidate. Failed candidates never replace renderers.
The successful level also controls runtime limb roots, colliders and completion
notifications; the offset remains stored for subsequent cuts of other limbs.

**Unreadable** is a terminal source classification, separate from **Failed**.
Extracted CPU data permits an intact preview, but never bypasses native
readability or enters cutting/retry work. The runtime remembers unreadable mesh
assets for the cache lifetime. Geometric candidate failures are cached by immutable
source, palette, scale, affected LODs and existing cuts. Wound-generation failures
also include the seed, because changing that seed can change the result. Raid
reset invalidates these caches. The editor deliberately resets caches for each
fresh case so an explicit diagnostic rerun exercises the current algorithm.

Each renderer lists all attempts with level, offset, stage, reason and cache
status. JSON captures additionally retain each attempt's selected triangles and
boundary surfaces. The Scene view displays the final attempted/successful
candidate; earlier failed candidates remain available in the JSON evidence.
Strongly limb-owned disconnected clothing details follow the detached limb even
outside the fitted capsule; torso-owned details stay on the retained body.
Collapsed retained faces are removed before boundary-owner counting.

After this update, run `./scripts/Setup-ClothingHarness.ps1 -SptInstallPath <path>` once to install the
new shared batch source and UI. Existing scene clones are discarded on assembly
reload; save any previews first. Subsequent algorithm edits can use **Sync
production sources** as usual. Fixtures and previous on-disk reports are preserved.

Failures are localized to fixture loading, eligibility, source capture,
classification, boundary analysis, aggregate boundaries, geometry building,
mesh upload or diagnostic export. The cutter's exact rejection text is retained.
Each cut also verifies that the original mesh vertices/indices stayed unchanged.

## Reports and unattended runs

Fixture extraction reads the installed inputs. The runtime readability loader is
separate; see [clothing bundle preparation](../../docs/ARCHITECTURE.md#clothing-bundle-preparation).
Run `scripts/Test-ClothingLoader.ps1 -SptInstallPath <game>` for that loader's
production discovery/rewrite, Unity hook and installed-ABI checks. These use
boundary doubles and do not establish full-mod-set live startup behavior.
Readability is necessary but does not guarantee an acceptable cut.

`ClothingHarnessRuns/<run>/` contains:

- `summary.json`: expected/completed counts, status totals, grouped rejection
  messages, source hashes and all case results.
- `cases.csv`: one row per garment/side/level/seed, including stage and reason.
- `<case>/result.json`: renderer-by-renderer result, mesh/triangle/loop counts.
- `<case>/renderer-N/source-*.json.gz`: immutable production source snapshot.
- `<case>/renderer-N/case-*.json.gz`: bone palette, cut frame, selected/candidate/
  protected triangles, boundary topology and retained exterior positions.

Cases rejected before capture still reference the original fixture and its
bundle hash; they do not fabricate missing classification evidence. Unlike raid
capture, the editor uses a separate capture session per renderer/case, so raid
quotas and seed deduplication cannot suppress a diagnostic.

```powershell
& 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe' `
    -batchmode -nographics `
    -projectPath "$PWD/artifacts/ClothingHarnessUnity" `
    -executeMethod Rupture.ClothingHarness.ClothingHarnessWindow.RunBatch `
    -logFile "$PWD/artifacts/clothing-harness.log"
```

Optional arguments: `-rptGarment <substring>` (separate alternatives with `|`),
`-rptFixtures <directory>` and `-rptSeed <uint>`. Add `-rptPreviews` and omit
`-nographics` to write overview and body/gib front, oblique and wireframe PNGs
for each case (use a small garment filter). Re-run setup after shader changes;
the source-sync button refreshes C# only. The method exits Unity itself. Exit
code 0 means the sweep completed; cutter rejections are findings in the report.
Infrastructure errors return 1. Batch mode writes evidence and closes the
temporary scene; use the editor window to retain and inspect all spawned clones.

Wound preview names include their tier (`lod0-body-oblique.png`,
`lod1-body-oblique.png`, etc.). Renderer reports and captures include `LodLevel`.
`shared-core` means a renderer reused the reference interior geometry and mapped
its bone weights into its own palette, rather than rebuilding a different cap.

**Calibration** / `-rptCalibrate` runs each side and level at nominal, -5% and
+5% of the selected bone segment independently, without alternate-candidate retries.
The fixture manifest determines the total case count.
Short hashed evidence directory names avoid Windows path-length export failures.
`-rptRetryReport <summary.json>` retries only diagnostic-export failures from an
older report. Calibration records the fixed offset separately from normal retry
results. See [limb clothing folds](../../docs/Limb-Clothing-Folds.md) for reducing
these measurements to the bare mannequin profiles and reviewing exceptions.

Renderer `Clothing` entries report each loop's status/reason, measured diameter,
threshold, ellipse axes, recess depth, containment scale and rim movement scale.
These also appear in the Inspector and runtime cut captures. A clothing fallback
preserves the accepted cutter result and its basic wound treatment.

Validate a completed full sweep (including the evidence for rejected cases):

```powershell
./tests/ClothingHarness.Tests.ps1 `
    -ReportPath artifacts/ClothingHarnessUnity/ClothingHarnessRuns/<run>/summary.json `
    -FixtureDirectory artifacts/ClothingHarnessUnity/ClothingFixtures
```

Fixtures and reports are local game-derived data under ignored `artifacts/`.
They are not release-package inputs. Mesh palette mismatches are deliberately
reported without guessing how EFT might alter those assets at runtime. Offline
findings do not establish live renderer lifecycle, posed gib physics, decal
behavior or in-raid performance.

## Latency measurements

Pass `-rptBenchmark` to a batch run to disable the classification overlay while
retaining diagnostic evidence. Renderer reports separate
`ClassificationMilliseconds`, `AnalysisMilliseconds`, `BuildMilliseconds` and
`CaptureMilliseconds`; geometry totals exclude fixture reconstruction, Unity
mesh upload and diagnostic serialization. The editor still exports captures
synchronously so a completed run has complete evidence.

In-game capture instead uses a separate bounded writer. Its file I/O is outside
geometry-job completion and collider work. At most four exports are outstanding;
excess exports explicitly report `skipped-backlog-limit`. Raid reset discards
pending exports; an already-running export retains its old session directory.
Disabling capture avoids diagnostic serialization and identity hashing entirely,
except hashes needed by source-pinned clothing exceptions. The cut algorithm,
six candidates and commit checks are unchanged.
