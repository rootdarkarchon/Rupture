# Development setup

Run commands from the active `rupture` repository root. The former `spt_visceral_combat` checkout and branch names in historical reports are provenance only. Do not copy historical binaries or revive old experiments as build inputs.

## Toolchain and external references

- Windows x64, PowerShell 7, and a .NET SDK that supports the `Rupture.slnx` solution format and the projects' target frameworks. .NET SDK 10 is the documented development baseline. Managed runtime projects target `netstandard2.1`; managed test executables target `net8.0` and require a compatible .NET runtime.
- A local SPT 4.1.6 / EFT 0.16.9.40743 installation with its Unity/BepInEx/SPT assemblies. Obtain game/reference inputs through your own installation; do not commit or distribute them.
- Fika Core 2.4.4 for the complete solution and managed integration tests. Server 2.4.2 and headless 1.5.3 are the corresponding documented baseline. Core-only builds do not require Fika.
- Unity Editor **2022.3.43f1** for shader builds and Unity fixtures. Scripts accept `-UnityEditorPath`; their default is `C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe`.
- CMake **3.24 or later**, Visual Studio 2022 with x64 C++ tools and a Windows SDK, plus Git for the pinned PhysX checkout.
- Python with NumPy for optional calibration, NumPy/Pillow for review images and UnityPy for clothing-fixture extraction. Check each script's input requirements before starting an extraction or sweep.

`Directory.Build.props` accepts `SptInstallPath` or `SPT_ROOT`; its existing `E:\Games\Tarkov-SPT` fallback remains for local compatibility. Pass absolute installation/reference paths explicitly for reproducibility; MSBuild resolves relative hint paths from each project directory. `FikaPluginPath` defaults to the installation's `BepInEx/plugins/Fika` directory. Several focused scripts retain a `D:\Tarkov-SPT` default; override it where they expose `SptInstallPath`.

```powershell
$game = 'D:\Tarkov-SPT-4.1' # Replace with your local installation
$fika = (Resolve-Path -LiteralPath '.\.refs\Fika-2.4.4-release\BepInEx\plugins\Fika').Path # Optional external reference directory
$unity = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe'

dotnet build Rupture.slnx -c Release -p:SptInstallPath="$game" -p:FikaPluginPath="$fika"
.\scripts\Test-Rupture.ps1 -SptInstallPath $game -FikaPluginPath $fika
```

If installed Fika already matches the baseline, omit the Fika override. `.refs/` is ignored local reference storage, not supplied by a fresh source checkout. Build baselines are not runtime version-number gates; changed external APIs may still require an updated mod.

## Compatibility and integration baseline

The current baseline is SPT 4.1.6 / EFT 0.16.9.40743, Fika Core 2.4.4, server 2.4.2 and headless 1.5.3. Rupture has no SPT/Fika server plugin. Standalone SPT does not require Fika; the complete solution and adapter tests do. SPT 4.0 is no longer maintained. Newer SPT/Fika versions are allowed to load without a version-number gate, but incompatible external APIs may require a new Rupture build. Baseline compatibility and automated checks do not establish live acceptance.

The adapter preserves Fika 2.4.4's readonly damage packet and separate victim network ID, and observes its current inventory dispatch. See [multiplayer contracts](ARCHITECTURE-MULTIPLAYER.md#living-damage-and-deferred-death) and [headgear ownership](ARCHITECTURE-MULTIPLAYER.md#headgear-and-corpse-inventory). Recoverable downing is distinct from death; use the [runtime checklist](RPT-Ragdoll-Runtime-Inspection.md#cadence-and-fika-motion) when checking revival or bleedout.

## Native backend

`Build-PhysXBackend.ps1` requires the complete NVIDIA PhysX source at commit `a2c0428acab643e60618c681b501e86f7fd558cc`. Its default location is `artifacts/dependencies/PhysX-4.1.2`. It checks both `physx/include/PxPhysicsVersion.h` and the checkout's exact Git commit; a binary-only SDK or source archive without Git metadata does not meet that contract.

For a fresh local dependency checkout, when that directory does not already exist:

```powershell
git clone https://github.com/NVIDIAGameWorks/PhysX.git artifacts/dependencies/PhysX-4.1.2
git -C artifacts/dependencies/PhysX-4.1.2 checkout --detach a2c0428acab643e60618c681b501e86f7fd558cc
.\scripts\Build-PhysXBackend.ps1 -Configuration Release
```

These Git commands operate on the external SDK only; they do not initialize Rupture. Preserve an existing dependency checkout instead of recloning it. Alternatively pass `-PhysXRepositoryPath` pointing to another complete checkout at the same revision.

The native script configures the x64 Visual Studio 2022 build, runs native fixtures and validates exports/dependencies. Outputs live under ignored `artifacts/rupture-physx-native-build/`. See the [physics ownership guide](ARCHITECTURE-PHYSX.md) before changing ABI declarations, layouts or SDK integration.

## Shader resources and focused validation

The sources under `unity/RuptureWound/` are templates/shared runtime inputs. Scripts generate isolated Unity projects under `artifacts/`; there is no need to commit generated Unity caches or extracted game resources.

```powershell
.\scripts\Build-WoundBundle.ps1 -UnityEditorPath $unity
.\scripts\Test-ClothingSoaking.ps1 -UnityEditorPath $unity -SptInstallPath $game
.\scripts\Test-WoundBenchmarks.ps1 -UnityEditorPath $unity
.\scripts\Test-Rupture.ps1 -SptInstallPath $game -FikaPluginPath $fika
```

The soaking check requires the native bridge and installed native texture/compositor references. Build the native backend first on a clean machine. Bundle rebuilding must be followed by soaking and parcel/geometry verification before packaging. These commands update the maintained bundle/reports in `assets/bundles/`; preserve them as validation inputs rather than deleting them as generic outputs.

For head geometry changes, `Test-HeadWoundWorker.ps1 -UnityEditorPath $unity` runs worker/topology/lifecycle fixtures. Other focused scripts cover living severing, fractures, clothing loading, culling, decals, blood and casings. `Test-Rupture.ps1 -Native -UnityRuntime` runs its selected expanded checks using their defaults; invoke scripts individually to override editor paths and inspect their individual outcomes. Source assertions do not substitute for Unity/native behavior checks.

## Calibration and game-derived fixtures

Keep `unity/RuptureWound/Assets/Rupture/Limbs/LimbClothingProfiles.Generated.cs` and `docs/limb-clothing-review.json`. The optional `Test-Rupture.ps1 -Calibration` also reads **`artifacts/cloth-calibration.json`**, which is a local required input, not committed source. Preserve it on this machine. A fresh checkout must reproduce the clothing calibration sweep before running that optional check. Select a NumPy-enabled interpreter with `-Python <absolute-python-executable>` when the default `py` environment lacks it; the scripts do not install Python packages automatically.

See [limb calibration](Limb-Clothing-Folds.md) and the [clothing harness](../unity/RuptureWound/CLOTHING-HARNESS.md) for extraction, sweep and table generation. Review-image evidence paths refer to local artifacts. Do not fabricate missing review evidence, replace measurements with synthetic guesses or change the frozen protocol-4 wire fixture to make a regression pass.

## Packaging and publication boundary

The current source version is 1.2.1, impact protocol 14, native physics ABI 10 and corpse control API V1. All multiplayer peers require the same Rupture build. Preserve configuration keys, wire order, public APIs and compatibility identifiers, including HollywoodFX's legacy GUID match.

Packaging requires **Rupture itself to be a Git repository** because the script enumerates non-ignored files with `git ls-files`. Git initialization is a separate repository-management step; the scripts do not recover history or initialize Git. Until it is authorized, managed/native/Unity validation can proceed but no canonical release ZIP can be generated.

After Git initialization and successful managed, native and resource checks, use only:

```powershell
.\scripts\Build-Package.ps1 -SptInstallPath $game -FikaPluginPath $fika -Configuration Release
```

The script builds managed/native outputs and validates resource and package layout. Its unchanged canonical result is `artifacts/Rupture-<version>-<githash>.zip`; the twelve-character suffix is a digest of non-ignored working-tree file paths/content, including untracked files. It is not merely the latest commit ID. Never manually create/rename deployment ZIPs. The payload is nine runtime/license files and excludes repository documentation, reports and game/reference assemblies.

Keep `.refs/`, `.tools/`, `.vs/`, `artifacts/`, build outputs and editor/Python caches out of Git. Rupture-authored source, documentation and assets are covered by the root [MIT license](../LICENSE). Preserve the existing PhysX and AssetsTools.NET license notices and the vendored LZMA public-domain notice. External EFT/SPT/Fika references are build inputs, not redistributed Rupture content. Local historical evidence remains in `artifacts/`; do not treat it as current acceptance or disposable source.

Automated validation does not establish live EFT/Fika visuals, AI treatment behavior, mod compatibility or FPS improvements. Use the [runtime checklist](RPT-Ragdoll-Runtime-Inspection.md) and report unperformed checks explicitly.
