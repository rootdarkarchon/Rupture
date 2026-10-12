[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/RuptureWoundValidation'
foreach ($directory in @('Assets/Rupture','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0","com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.physics":"1.0.0","com.unity.modules.assetbundle":"1.0.0"}}'
$sources = Join-Path $repo 'unity/RuptureWound/Assets/Rupture'
foreach ($directory in @('HeadWounds','BloodStreams','Limbs')) {
    Copy-Item -LiteralPath (Join-Path $sources $directory) -Destination (Join-Path $project 'Assets/Rupture') -Recurse -Force
}
foreach ($shader in @('WoundInterior.shader','BloodStream.shader','LimbSeverSurface.shader','ClothingSoak.shader')) {
    Copy-Item -LiteralPath (Join-Path $sources $shader) -Destination (Join-Path $project 'Assets/Rupture') -Force
}
$visualLog = Join-Path $repo 'artifacts/rupture-parcel-visual.log'
$visualArgs = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-rptBundlePath',('"'+(Join-Path $repo 'assets/bundles/rupture-wound.bundle')+'"'),'-executeMethod','Rupture.UnityPreview.BloodStreams.BloodParcelVisualTests.RunBatch','-logFile',('"'+$visualLog+'"'))
$visual = Start-Process -FilePath $UnityEditorPath -ArgumentList $visualArgs -WindowStyle Hidden -Wait -PassThru
if ($visual.ExitCode) { throw "Parcel visual equivalence failed; see $visualLog" }
Copy-Item -LiteralPath (Join-Path $project 'ParcelVerification/verification.json') -Destination (Join-Path $repo 'assets/bundles/rupture-parcels.verification.json') -Force
$methods = @(
    @('head','Rupture.Dismemberment.Classes.HeadWounds.BenchmarkRuptureHeadWound.RunBatch','rupture-wound.benchmark.json'),
    @('blood','Rupture.UnityPreview.BloodStreams.PulsatingBloodStreamEditorTools.Benchmark','rupture-bloodstream.benchmark.json'),
    @('limb','Rupture.Dismemberment.Classes.Limbs.BenchmarkAndPreviewProceduralLimbWounds.RunBenchmark','rupture-limb-wound.benchmark.json')
)
foreach ($entry in $methods) {
    $log = Join-Path $repo ("artifacts/rupture-benchmark-" + $entry[0] + '.log')
    $arguments = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod',$entry[1],'-logFile',('"'+$log+'"'))
    $process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode) { throw "Unity benchmark failed: $($entry[0]); see $log" }
    $report = Get-Content -LiteralPath (Join-Path $project ('AssetBundles/' + $entry[2])) -Raw | ConvertFrom-Json
    if (!$report.allPassed) { throw "Unity benchmark rejected: $($entry[0])" }
}
# Validate fresh results before replacing the current reports. Historical docs
# retain their original measured values; these files explicitly record new runs.
$reportRoot = Join-Path $project 'AssetBundles'
& (Join-Path $repo 'tests/WoundBundle.Tests.ps1') -BenchmarkPath (Join-Path $reportRoot $methods[0][2]) -BloodStreamBenchmarkPath (Join-Path $reportRoot $methods[1][2]) -LimbBenchmarkPath (Join-Path $reportRoot $methods[2][2])
foreach ($entry in $methods) { Copy-Item -LiteralPath (Join-Path $reportRoot $entry[2]) -Destination (Join-Path $repo 'assets/bundles') -Force }
Write-Output 'PASS: fresh head, blood-stream and limb Unity benchmarks; current reports updated.'
