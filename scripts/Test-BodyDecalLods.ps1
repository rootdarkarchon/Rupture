[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/BodyDecalLodsUnity'
foreach ($directory in @('Assets/Editor','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
foreach ($source in @('src/Rupture/Dismemberment/Classes/Limbs/BodyDecalRendering.cs','tests/Unity/BodyDecalLodTests.cs')) {
    Copy-Item -LiteralPath (Join-Path $repo $source) -Destination (Join-Path $project 'Assets/Editor') -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'tests/Unity/BodyDecalProjection.shader') -Destination (Join-Path $project 'Assets') -Force
$log = Join-Path $repo 'artifacts/body-decal-lods.log'
$arguments = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','BodyDecalLodTests.RunBatch','-logFile',('"'+$log+'"'))
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode) { throw "Body decal LOD test failed; see $log" }
$report = Get-Content -LiteralPath (Join-Path $project 'DecalVerification/verification.json') -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Body decal LOD report failed.' }
$report | ConvertTo-Json
