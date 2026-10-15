[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe', [Parameter(Mandatory=$true)][string]$SptInstallPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/HotPathRuntimeUnity'
foreach ($directory in @('Assets/Editor','Assets/Plugins','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.particlesystem":"1.0.0"}}'
foreach ($source in @('src/Rupture/Casings/CasingImpactQuery.cs','src/Rupture/Ragdolls/External/ExternalCasingContracts.cs',
    'src/Rupture.PhysXBackend/EnvironmentHierarchyFilter.cs','src/Rupture.PhysXBackend/EnvironmentTransformProbe.cs',
    'src/Rupture.PhysXBackend/SimulationActivity.cs','src/Rupture.PhysXBackend/BodyStatePublication.cs','src/Rupture.PhysXBackend/NativeApi.cs',
    'tests/Unity/PhysicsIdleRuntimeTests.cs','src/Rupture/Dismemberment/Classes/BloodStreams/BloodDecalBatch.cs',
    'src/Rupture/Dismemberment/Patches/DecalMeshIndexFormatPatch.cs','src/Rupture/Ragdolls/Patches/CorpseWeaponReleasePatch.cs',
    'tests/Unity/WeaponReleaseRuntimeTests.cs','tests/Unity/MainThreadTracingFixture.cs','tests/Unity/HotPathRuntimeTests.cs',
    'src/Rupture/Ragdolls/Classes/HeadgearPhysicsLifetime.cs','src/Rupture/Ragdolls/Patches/HeadgearPhysicsLifetimePatch.cs',
    'tests/Unity/HeadgearPhysicsRuntimeTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') { $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n" }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Editor/' + [IO.Path]::GetFileName($source))), $content)
}
$world = Get-Content (Join-Path $repo 'src/Rupture/Ragdolls/Classes/ShadowRagdollWorld.cs') -Raw
$blocks = foreach ($marker in @('internal static void ReleaseWeapon(EFT.Interactive.CorpseRagdoll','private static void ReleaseWeapon(ShadowRig','private static void ReleaseInvalidWeapon(')) {
    $start = $world.IndexOf($marker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production lifecycle method: $marker" }
    $open = $world.IndexOf('{', $start); $depth = 1; $end = $open + 1
    while ($depth -gt 0) { if ($world[$end] -eq '{') { $depth++ }; if ($world[$end] -eq '}') { $depth-- }; $end++ }
    $world.Substring($start, $end - $start)
}
[IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/WeaponReleaseExtracted.cs'),
    'using System; namespace Rupture.Ragdolls.Classes { internal static partial class ShadowRagdollWorld {' + ($blocks -join "`n") + '} }')
foreach ($dependency in @('0Harmony.dll','Mono.Cecil.dll','MonoMod.RuntimeDetour.dll','MonoMod.Utils.dll')) {
    Copy-Item -LiteralPath (Join-Path $SptInstallPath "BepInEx/core/$dependency") -Destination (Join-Path $project 'Assets/Plugins') -Force
}
$log = Join-Path $repo 'artifacts/hotpath-runtime-unity.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','HotPathRuntimeTests.RunBatch','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Hot-path runtime regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh hot-path runtime report was not produced.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Hot-path runtime report failed.' }
$report | ConvertTo-Json
