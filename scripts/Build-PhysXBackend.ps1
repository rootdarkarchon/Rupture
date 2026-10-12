[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $PhysXRepositoryPath
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$expectedCommit = "a2c0428acab643e60618c681b501e86f7fd558cc"
if (-not $PhysXRepositoryPath) {
    $PhysXRepositoryPath = Join-Path $repoRoot "artifacts\dependencies\PhysX-4.1.2"
}
$PhysXRepositoryPath = [IO.Path]::GetFullPath($PhysXRepositoryPath)
$physxSource = Join-Path $PhysXRepositoryPath "physx"
$buildRoot = Join-Path $repoRoot "artifacts\rupture-physx-native-build"

if (-not (Test-Path -LiteralPath (Join-Path $physxSource "include\PxPhysicsVersion.h") -PathType Leaf)) {
    throw "Pinned PhysX source is missing. Checkout NVIDIA PhysX commit $expectedCommit at '$PhysXRepositoryPath'."
}

$actualCommit = (& git -C $PhysXRepositoryPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not [string]::Equals($actualCommit, $expectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "PhysX source must be pinned to $expectedCommit; found '$actualCommit'."
}

& cmake -S (Join-Path $repoRoot "src\Rupture.PhysX.Native") `
    -B $buildRoot `
    -G "Visual Studio 17 2022" `
    -A x64 `
    "-DRPT_PHYSX_SOURCE_DIR=$($physxSource.Replace('\', '/'))" `
    -DBUILD_TESTING=ON
if ($LASTEXITCODE -ne 0) { throw "PhysX bridge configuration failed with exit code $LASTEXITCODE." }

$nativeConfiguration = $Configuration.ToLowerInvariant()
& cmake --build $buildRoot --config $nativeConfiguration --target rpt_physx_bridge rpt_physx_bridge_tests --parallel 4
if ($LASTEXITCODE -ne 0) { throw "PhysX bridge build failed with exit code $LASTEXITCODE." }

$testExecutable = Join-Path $buildRoot "$nativeConfiguration\rpt_physx_bridge_tests.exe"
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw "PhysX bridge tests failed with exit code $LASTEXITCODE." }

$bridge = Join-Path $buildRoot "$nativeConfiguration\rpt_physx_bridge.dll"
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw "Native bridge output not found: $bridge" }
& (Join-Path $repoRoot "tests\PhysXNativeDependencies.Tests.ps1") -BridgePath $bridge
if ($LASTEXITCODE -ne 0) { throw "Native dependency validation failed with exit code $LASTEXITCODE." }
Write-Output $bridge
