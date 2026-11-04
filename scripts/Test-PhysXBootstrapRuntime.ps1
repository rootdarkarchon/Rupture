[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/PhysXBootstrapRuntimeUnity'
foreach ($directory in @('Assets/Bootstrap','Assets/Plugins/x86_64','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.terrainphysics":"1.0.0"}}'
foreach ($source in @('src/Rupture.PhysXBackend/MeshGeometrySources.cs','src/Rupture.PhysXBackend/MeshGeometry.cs',
    'src/Rupture.PhysXBackend/NativeApi.cs','src/Rupture/Ragdolls/External/ExternalPhysicsContracts.cs',
    'src/Rupture/Ragdolls/External/ExternalCasingContracts.cs','src/Rupture/Ragdolls/Classes/PhysicsBootstrapProbeLease.cs',
    'tests/Unity/PhysXBootstrapRuntimeTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') {
        $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
    }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Bootstrap/' + [IO.Path]::GetFileName($source))), $content)
}
$bridge = Join-Path $repo 'artifacts/rupture-physx-native-build/release/rpt_physx_bridge.dll'
if (!(Test-Path -LiteralPath $bridge)) { throw 'Build the native backend first with scripts/Build-PhysXBackend.ps1.' }
Copy-Item -LiteralPath $bridge -Destination (Join-Path $project 'Assets/Plugins/x86_64/rpt_physx_bridge.dll') -Force
$log = Join-Path $repo 'artifacts/physx-bootstrap-runtime-unity.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-nographics',
    '-projectPath',('"'+$project+'"'),'-executeMethod','PhysXBootstrapRuntimeTests.RunBatch','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Unity PhysX bootstrap regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh bootstrap report was not produced.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'PhysX bootstrap runtime report failed.' }
$report | Add-Member -NotePropertyName nativeBridgeSha256 -NotePropertyValue (Get-FileHash -LiteralPath (Join-Path $project 'Assets/Plugins/x86_64/rpt_physx_bridge.dll') -Algorithm SHA256).Hash
$report | ConvertTo-Json | Set-Content -LiteralPath $reportPath
$report | ConvertTo-Json
