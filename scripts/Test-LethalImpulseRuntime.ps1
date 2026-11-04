[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe',
    [string]$SptInstallPath = 'D:\Tarkov-SPT-4.1')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/LethalImpulseRuntimeUnity'
$legacyBridge = Join-Path $project 'LegacyBridge/rpt_physx_bridge.dll'
$installedBridge = Join-Path $SptInstallPath 'BepInEx/plugins/rupture/rpt_physx_bridge.dll'
if (!(Test-Path -LiteralPath $legacyBridge) -and (Test-Path -LiteralPath $installedBridge)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $legacyBridge) | Out-Null
    Copy-Item -LiteralPath $installedBridge -Destination $legacyBridge
}
foreach ($directory in @('Assets/Impulse','Assets/Plugins/x86_64','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0"}}'
foreach ($source in @('src/Rupture/Ragdolls/Classes/LethalImpulseExperiment.cs',
    'src/Rupture/Ragdolls/Classes/LethalImpulsePolicy.cs','src/Rupture/Ragdolls/Classes/PhysicsMath.cs',
    'src/Rupture/Ragdolls/Classes/ShotImpactProcessor.cs',
    'src/Rupture/Ragdolls/Classes/RootVelocityExperiment.cs',
    'src/Rupture/Ragdolls/Classes/AngularDriveExperiment.cs',
    'src/Rupture/Ragdolls/Classes/ShadowRagdollWorld.AngularExperiment.cs',
    'src/Rupture/Ragdolls/External/ExternalPhysicsContracts.cs',
    'src/Rupture/Ragdolls/External/ExternalCasingContracts.cs',
    'src/Rupture/Ragdolls/External/ExternalAngularDriveContracts.cs',
    'src/Rupture.PhysXBackend/NativeApi.cs',
    'src/Rupture.PhysXBackend/AngularDriveRuntime.cs',
    'src/Rupture/Ragdolls/Patches/RootVelocityHooks.cs',
    'src/Rupture/Ragdolls/Patches/LethalImpulseHooks.cs',
    'tests/Unity/LethalImpulseRuntimeTests.cs','tests/Unity/RootVelocityRuntimeTests.cs','tests/Unity/AngularDriveRuntimeTests.cs',
    'tests/Unity/AngularDriveBackendFixture.cs','tests/Unity/LethalImpulseNativeFixture.cs',
    'tests/Unity/DeathTransitionProductionTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') {
        $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
    }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Impulse/' + [IO.Path]::GetFileName($source))), $content)
}
# Exercise the exact production core-selection / transactional velocity method,
# with small lifecycle adapters instead of compiling the entire EFT scene world.
$world = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture/Ragdolls/Classes/ShadowRagdollWorld.cs') -Raw
$start = $world.IndexOf('internal static bool TryAdjustRootVelocity(')
$end = $world.IndexOf('internal static bool HasActiveLethalExperimentBody(', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Production velocity transfer fixture extraction failed.' }
$adapter = "using System; using UnityEngine; namespace Rupture.Ragdolls.Classes { internal static partial class ShadowRagdollWorld {`n" + $world.Substring($start, $end - $start) + "`n} }"
[IO.File]::WriteAllText((Join-Path $project 'Assets/Impulse/RootVelocityWorldAdapter.cs'), $adapter)
$sceneSource = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture.PhysXBackend/NativeScene.cs') -Raw
$start = $sceneSource.IndexOf('internal enum JointSnapshotKind')
$end = $sceneSource.IndexOf('internal sealed class EnvironmentActor', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Production joint snapshot extraction failed.' }
$adapter = "using System; using System.Runtime.InteropServices; using UnityEngine; namespace Rupture.PhysXBackend {`n" + $sceneSource.Substring($start,$end-$start) + "`n}"
[IO.File]::WriteAllText((Join-Path $project 'Assets/Impulse/JointSnapshotAdapter.cs'), $adapter)
$bridge = Join-Path $repo 'artifacts/rupture-physx-native-build/release/rpt_physx_bridge.dll'
if (!(Test-Path -LiteralPath $bridge)) { throw 'Build the native backend first with scripts/Build-PhysXBackend.ps1.' }
Copy-Item -LiteralPath $bridge -Destination (Join-Path $project 'Assets/Plugins/x86_64/rpt_physx_bridge.dll') -Force
foreach ($assembly in @('0Harmony.dll','Mono.Cecil.dll','MonoMod.Utils.dll','MonoMod.RuntimeDetour.dll')) {
    Copy-Item -LiteralPath (Join-Path $SptInstallPath "BepInEx/core/$assembly") -Destination (Join-Path $project "Assets/Plugins/$assembly") -Force
}
$log = Join-Path $repo 'artifacts/lethal-impulse-runtime-unity.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-nographics',
    '-projectPath',('"'+$project+'"'),'-executeMethod','LethalImpulseRuntimeTests.RunBatch','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Lethal impulse runtime regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh impulse report was not produced.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Lethal impulse runtime report failed.' }
$report | Add-Member -NotePropertyName nativeBridgeSha256 -NotePropertyValue (Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath
$report | ConvertTo-Json -Depth 5
