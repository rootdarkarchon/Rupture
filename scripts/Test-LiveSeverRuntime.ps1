[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/LiveSeverRuntimeUnity'
foreach ($directory in @('Assets/Living','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.ai":"1.0.0"}}'
foreach ($source in @('src/Rupture/Ragdolls/Classes/ShadowRagdollWorld.Living.cs',
    'src/Rupture/Ragdolls/Classes/BodyHierarchyPath.cs','src/Rupture/Health/BotSeverPolicy.cs','src/Rupture/Impacts/LimbImpactPolicy.cs',
    'src/Rupture/Health/BotSeverData.cs','unity/RuptureWound/Assets/Rupture/Limbs/LimbSeverLevel.cs',
    'src/Rupture/Ragdolls/Classes/ShadowBodyProxy.cs',
    'src/Rupture/Ragdolls/External/ExternalPhysicsContracts.cs','src/Rupture/Ragdolls/External/ExternalCasingContracts.cs',
    'tests/Unity/LiveSeverRuntimeTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') {
        $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
    }
    $name = [IO.Path]::GetFileName($source)
    # Remove this fixture's previous editor-only copies, if present.
    foreach ($old in @("Assets/Editor/$name", "Assets/Editor/$name.meta")) {
        $oldPath = Join-Path $project $old
        if (Test-Path -LiteralPath $oldPath) { Remove-Item -LiteralPath $oldPath -Force }
    }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Living/' + $name)), $content)
}
$world = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture/Ragdolls/Classes/ShadowRagdollWorld.cs') -Raw
function Get-ProductionMember([string]$source, [string]$marker) {
    $memberStart = $source.IndexOf($marker, [StringComparison]::Ordinal)
    if ($memberStart -lt 0) { throw "Missing production fixture member: $marker" }
    $memberOpen = $source.IndexOf('{', $memberStart); $memberDepth = 1; $memberEnd = $memberOpen + 1
    while ($memberDepth -gt 0) {
        if ($source[$memberEnd] -eq '{') { $memberDepth++ }
        if ($source[$memberEnd] -eq '}') { $memberDepth-- }
        $memberEnd++
    }
    $source.Substring($memberStart, $memberEnd - $memberStart)
}
$corpseMembers = foreach ($marker in @('private sealed class ShadowBody', 'private sealed class ShadowRig',
    'private sealed class ExternalJointBinding', 'private sealed class PendingColliderDisable',
    'private static bool IsRemovedBody(', 'private static bool IsPreseveredBody(',
    'private static List<Collider> GetAttachedBodyColliders(', 'private static void MergeColliderDisable(',
    'private static PendingColliderDisable CaptureColliderDisable(', 'private static void ApplyColliderDisable(',
    'private static void ParkSeveredBody(', 'private static ShadowBody FindRetainedParentBody(',
    'private static void UpdateParkedBodyPose(', 'private static void RemoveSeveredBodyJoints(',
    'private static void ApplyPendingColliderDisable(', 'private static bool TryCreateBody(',
    'private static bool TryCloneJoints(', 'private static bool TryGetConnectedExternalBody(',
    'private static void RegisterSpecialBody(', 'private static int GetHierarchyDepth(')) { Get-ProductionMember $world $marker }
[IO.File]::WriteAllText((Join-Path $project 'Assets/Living/ShadowRagdollWorld.CorpseFixture.cs'),
    'using System; using System.Collections.Generic; using EFT; using EFT.AssetsManager; using EFT.Interactive; using UnityEngine; using Rupture.Ragdolls.External; namespace Rupture.Ragdolls.Classes { internal static partial class ShadowRagdollWorld {' +
    ($corpseMembers -join "`n") + '} }')
$coordinator = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture/Ragdolls/Classes/RagdollCoordinator.cs') -Raw
[IO.File]::WriteAllText((Join-Path $project 'Assets/Living/RagdollCoordinator.ColliderFixture.cs'),
    'using System; using System.Collections.Generic; using EFT; using UnityEngine; using Rupture.Dismemberment.Classes.Limbs; namespace Rupture.Ragdolls.Classes { internal static partial class RagdollCoordinator {' +
    (Get-ProductionMember $coordinator 'internal static Collider[] GetLivingLimbColliders(') +
    (Get-ProductionMember $coordinator 'private static bool IsRemovedLimbCollider(') + '} }')
$limbs = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture/Dismemberment/Classes/Limbs/LimbDismembermentSystem.cs') -Raw
[IO.File]::WriteAllText((Join-Path $project 'Assets/Living/LimbDismembermentSystem.CancellationFixture.cs'),
    'using System; using EFT; using Rupture.Ragdolls.Classes; namespace Rupture.Dismemberment.Classes.Limbs { internal static partial class LimbDismembermentSystem {' +
    (Get-ProductionMember $limbs 'internal static void CancelLivingCuts(') +
    (Get-ProductionMember $limbs 'private static void CancelPending(') + '} }')
$state = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture/Health/BotSeverStateService.cs') -Raw
$stateMarker = 'internal static bool RemovedLivingBone('
$stateStart = $state.IndexOf($stateMarker, [StringComparison]::Ordinal)
if ($stateStart -lt 0) { throw 'Missing production living anatomy filter.' }
$stateOpen = $state.IndexOf('{', $stateStart); $stateDepth = 1; $stateEnd = $stateOpen + 1
while ($stateDepth -gt 0) { if ($state[$stateEnd] -eq '{') { $stateDepth++ }; if ($state[$stateEnd] -eq '}') { $stateDepth-- }; $stateEnd++ }
[IO.File]::WriteAllText((Join-Path $project 'Assets/Living/BotSeverStateService.Topology.cs'),
    'using EFT; using UnityEngine; using Rupture.Dismemberment.Classes.Limbs; namespace Rupture.Health { internal static partial class BotSeverStateService {' +
    $state.Substring($stateStart, $stateEnd - $stateStart) + '} }')
$marker = 'private static void ParkLocalDetachedEffectShell('
$start = $world.IndexOf($marker, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Missing production detached-shell parking method.' }
$open = $world.IndexOf('{', $start); $depth = 1; $end = $open + 1
while ($depth -gt 0) { if ($world[$end] -eq '{') { $depth++ }; if ($world[$end] -eq '}') { $depth-- }; $end++ }
$parking = $world.Substring($start, $end - $start)
$constants = foreach ($name in @('LocalGibMaximumDepenetrationSpeed','LocalGibMaximumAngularSpeed','LocalGibMaximumInitialLaunchSpeed')) {
    $match = [regex]::Match($world, "private const float $name = [^;]+;")
    if (!$match.Success) { throw "Missing production gib limit: $name" }
    $match.Value
}
$gibImpactMembers = foreach ($member in @('private static void ConfigureLocalGibQueryCollider(',
    'private static bool ApplyRadialImpulse(IShadowBodyProxy')) { Get-ProductionMember $world $member }
[IO.File]::WriteAllText((Join-Path $project 'Assets/Living/ShadowRagdollWorld.Shared.cs'),
    'using UnityEngine; namespace Rupture.Ragdolls.Classes { internal static partial class ShadowRagdollWorld {' +
    ($constants -join "`n") + "`n" + $parking + "`n" + ($gibImpactMembers -join "`n") + '} }')
$log = Join-Path $repo 'artifacts/live-sever-runtime-unity.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-nographics',
    '-projectPath',('"'+$project+'"'),'-executeMethod','LiveSeverRuntimeTests.RunBatch','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Living sever runtime regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) {
    throw 'Fresh living sever runtime report was not produced.'
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Living sever runtime report failed.' }
$report | ConvertTo-Json
