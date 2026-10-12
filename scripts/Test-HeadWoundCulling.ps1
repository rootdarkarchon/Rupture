[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/HeadWoundCullingUnity'
foreach ($directory in @('Assets/Editor','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
foreach ($source in @('src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundSkinning.cs','tests/Unity/HeadWoundCullingTests.cs')) {
    Copy-Item -LiteralPath (Join-Path $repo $source) -Destination (Join-Path $project 'Assets/Editor') -Force
}
$log = Join-Path $repo 'artifacts/head-wound-culling.log'
$arguments = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','HeadWoundCullingTests.RunBatch','-logFile',('"'+$log+'"'))
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode) { throw "Head culling test failed; see $log" }
$report = Get-Content -LiteralPath (Join-Path $project 'CullingVerification/verification.json') -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Head culling report failed.' }
$report | ConvertTo-Json
