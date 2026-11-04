[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe', [string]$FixturePath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/WeaponColliderGeometryUnity'
foreach ($directory in @('Assets/Geometry','Assets/Plugins/x86_64','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
foreach ($source in @('src/Rupture/Ragdolls/Classes/WeaponColliderGeometry.cs',
    'src/Rupture.PhysXBackend/MeshGeometry.cs','src/Rupture.PhysXBackend/NativeApi.cs',
    'src/Rupture/Ragdolls/External/ExternalPhysicsContracts.cs','src/Rupture/Ragdolls/External/ExternalCasingContracts.cs',
    'tests/Unity/WeaponColliderGeometryTests.cs','tests/Unity/WeaponColliderShapeRuntimeTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') {
        $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
    }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Geometry/' + [IO.Path]::GetFileName($source))), $content)
}
# Test the exact production snapshot methods with a small storage adapter, not the EFT world.
$sceneSource = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture.PhysXBackend/NativeScene.cs') -Raw
$start = $sceneSource.IndexOf('internal static ActorSnapshot CaptureBoxBody(')
$end = $sceneSource.IndexOf('private static NativeApi.BodyDesc StaticOrKinematic(', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Weapon snapshot fixture extraction failed.' }
$adapter = "using System; using System.Collections.Generic; using System.Runtime.InteropServices; using UnityEngine; using Rupture.Ragdolls.External; namespace Rupture.PhysXBackend { internal sealed partial class ActorSnapshot {`n" + $sceneSource.Substring($start,$end-$start) + "`n} }"
[IO.File]::WriteAllText((Join-Path $project 'Assets/Geometry/WeaponSnapshotAdapter.cs'), $adapter)
$bridge = Join-Path $repo 'artifacts/rupture-physx-native-build/release/rpt_physx_bridge.dll'
Copy-Item -LiteralPath $bridge -Destination (Join-Path $project 'Assets/Plugins/x86_64/rpt_physx_bridge.dll') -Force
$log = Join-Path $repo 'artifacts/weapon-collider-geometry-unity.log'
$arguments = @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-executeMethod','WeaponColliderShapeRuntimeTests.RunBatch','-logFile',('"'+$log+'"'))
if ($FixturePath) { $arguments += @('-weaponFixtures', ('"'+[IO.Path]::GetFullPath($FixturePath)+'"')) }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Weapon geometry regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh geometry report was not produced.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Weapon geometry regression failed.' }
$report | ConvertTo-Json
