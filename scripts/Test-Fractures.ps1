[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe',
    [Parameter(Mandatory=$true)][string]$SptInstallPath, [string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/FractureRuntimeUnity'
$env:RPT_FRACTURE_GAME_ROOT = $SptInstallPath
if (!$FixtureDirectory) { $FixtureDirectory = Join-Path $repo 'artifacts/ClothAllLodFixtures' }
$env:RPT_FRACTURE_FIXTURES = $FixtureDirectory
foreach ($directory in @('Assets/Fractures','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0","com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.physics":"1.0.0","com.unity.modules.assetbundle":"1.0.0"}}'
$sources = @('src/Rupture/Health/NativeHealthEffects.cs',
    'src/Rupture/Dismemberment/Classes/Fractures/FractureAppearance.cs',
    'src/Rupture/Dismemberment/Classes/Fractures/FractureSurface.cs',
    'src/Rupture/Dismemberment/Classes/Fractures/FractureVisual.cs',
    'src/Rupture/Dismemberment/Classes/Fractures/FractureSystem.cs',
    'tests/Unity/FractureRuntimeFixture.cs','tests/Unity/FractureRuntimeTests.cs','tests/Unity/FractureNativeFixtureTests.cs',
    'unity/RuptureWound/Assets/Editor/ClothingHarness/ClothingFixtures.cs')
foreach ($source in $sources) {
    Copy-Item -LiteralPath (Join-Path $repo $source) -Destination (Join-Path $project 'Assets/Fractures') -Force
}
Copy-Item -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll') -Destination (Join-Path $project 'Assets/Fractures') -Force
foreach ($name in @('ProceduralLimbWoundGeometry.cs','ProceduralLimbClothGeometry.cs','LimbSeverLevel.cs','LimbClothingProfiles.cs','LimbClothingProfiles.Generated.cs')) {
    Copy-Item -LiteralPath (Join-Path $repo "unity/RuptureWound/Assets/Rupture/Limbs/$name") -Destination (Join-Path $project 'Assets/Fractures') -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'unity/RuptureWound/Assets/Rupture/LimbSeverSurface.shader') -Destination (Join-Path $project 'Assets/Fractures') -Force
$log = Join-Path $repo 'artifacts/fracture-runtime.log'
$arguments = @('-batchmode','-projectPath',('"'+$project+'"'),'-executeMethod','FractureRuntimeTests.RunBatch','-logFile',('"'+$log+'"'))
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Fracture runtime tests failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'No fresh fracture verification report.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Fracture runtime report failed.' }
$report | ConvertTo-Json -Depth 6
