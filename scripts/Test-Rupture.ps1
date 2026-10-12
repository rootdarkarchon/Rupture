[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SptInstallPath,
    [string]$FikaPluginPath,
    [string]$ClothingReport,
    [string]$FixtureDirectory,
    [string]$Python = 'py',
    [switch]$Calibration,
    [switch]$Native,
    [switch]$UnityRuntime
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$referenceArgs = @("-p:SptInstallPath=$SptInstallPath")
if ($FikaPluginPath) { $referenceArgs += "-p:FikaPluginPath=$FikaPluginPath" }
& dotnet build (Join-Path $repo 'Rupture.slnx') -c Release @referenceArgs --disable-build-servers -m:1 -v minimal
if ($LASTEXITCODE) { throw 'Managed build failed.' }
& dotnet (Join-Path $repo 'tests/Rupture.RegressionTests/bin/Release/net8.0/Rupture.RegressionTests.dll') (Join-Path $repo 'src/Rupture/bin/Release/netstandard2.1/Rupture.dll') (Join-Path $repo 'src/Rupture.Fika/bin/Release/netstandard2.1/Rupture.Fika.dll')
if ($LASTEXITCODE) { throw 'Managed regressions failed.' }
foreach ($test in @('BundleDirectoryCase','ExternalPhysXArchitecture','GrenadeDismemberment','ImpactWiring','ProceduralHeadWound','PulsatingBloodStream','Reliability','WoundBundle')) {
    & (Join-Path $repo "tests/$test.Tests.ps1")
    if (!$?) { throw "$test failed." }
}
if ($Calibration) {
    & $Python (Join-Path $repo 'tests/LimbClothingCalibration.Tests.py')
    if ($LASTEXITCODE) { throw 'Clothing calibration regression failed.' }
}
if ($ClothingReport) {
    if (!$FixtureDirectory) { throw 'FixtureDirectory is required with ClothingReport.' }
    & (Join-Path $repo 'tests/ClothingHarness.Tests.ps1') -ReportPath $ClothingReport -FixtureDirectory $FixtureDirectory
    & $Python (Join-Path $repo 'tests/LimbClothingLod.Tests.py') $ClothingReport
    if ($LASTEXITCODE) { throw 'Clothing LOD regression failed.' }
}
if ($Native) { & (Join-Path $repo 'scripts/Build-PhysXBackend.ps1') }
if ($UnityRuntime) {
    & (Join-Path $repo 'scripts/Test-LiveSeverRuntime.ps1')
    & (Join-Path $repo 'scripts/Test-HeadWoundWorker.ps1')
    & (Join-Path $repo 'scripts/Test-ReliabilityRuntime.ps1')
    & (Join-Path $repo 'scripts/Test-CasingRuntime.ps1')
    & (Join-Path $repo 'scripts/Test-HotPathRuntime.ps1') -SptInstallPath $SptInstallPath
    & (Join-Path $repo 'scripts/Test-BloodParcelRuntime.ps1')
    & (Join-Path $repo 'scripts/Test-BloodEmitterRuntime.ps1')
}
Write-Output "PASS: managed behavioral regressions and structural checks. Native requested=$Native; Unity runtime requested=$UnityRuntime. Live EFT/Fika acceptance remains separate."
