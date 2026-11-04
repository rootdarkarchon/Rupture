<#
.SYNOPSIS
Runs the production blood emitter, tracking, and human-head stream regressions in Unity.
.DESCRIPTION
Requires PowerShell and Unity 2022.3.43f1 (override -UnityEditorPath when needed).
The eligibility suite extracts current CanStartStream, typed standalone/Fika IsHumanPlayer
methods, and the real StartOrReplace/Receive/ProcessPending/RestoreLivingVisuals bodies.
Foreign player/network/render boundaries are fixtures; eligibility is never substituted.
.PARAMETER StartBaselineSource
Optional pre-fix PulsatingBloodStreamSystem.cs snapshot. Only its unchanged, unguarded
StartOrReplace is compiled instead of the current method. The identical regression suite
must fail when a human HeavyWound Head actually starts; no baseline policy is synthesized.
Independent of -BaselineSource, which retains its existing emitter benchmark comparison.
.EXAMPLE
# Preserve the production emitter before applying the fix:
Copy-Item src/Rupture/Dismemberment/Classes/BloodStreams/PulsatingBloodStreamSystem.cs artifacts/HeadStreams.Before.cs
# After applying the fix, demonstrate the regression (expected failure):
./scripts/Test-BloodEmitterRuntime.ps1 -StartBaselineSource ./artifacts/HeadStreams.Before.cs
# Run the same cases with current production StartOrReplace (expected pass):
./scripts/Test-BloodEmitterRuntime.ps1
# An existing frozen emitter performance comparison can still be added with -BaselineSource.
#>
[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe', [string]$BaselineSource, [string]$StartBaselineSource)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/BloodEmitterRuntimeUnity'
foreach ($dir in @('Assets/Editor','ProjectSettings','Packages')) { New-Item -ItemType Directory -Force (Join-Path $project $dir) | Out-Null }
Set-Content (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
$runtimePath = Join-Path $repo 'src/Rupture/Dismemberment/Classes/BloodStreams/PulsatingBloodStreamSystem.cs'
function Get-ProductionBlock([string]$source, [string]$marker) {
    $start = $source.IndexOf($marker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production block: $marker" }
    $open = $source.IndexOf('{', $start)
    $arrow = $source.IndexOf('=>', $start)
    if ($arrow -ge 0 -and ($open -lt 0 -or $arrow -lt $open)) { $end = $source.IndexOf(';', $arrow) + 1 }
    else {
        if ($open -lt 0) { throw "Missing production block body: $marker" }
        $depth = 1; $end = $open + 1
        while ($depth -gt 0 -and $end -lt $source.Length) {
            if ($source[$end] -eq '{') { $depth++ }
            if ($source[$end] -eq '}') { $depth-- }
            $end++
        }
        if ($depth -ne 0) { throw "Unterminated production block: $marker" }
    }
    $source.Substring($start, $end - $start)
}
function Write-Extracted([string]$path, [string]$className) {
    $runtime = Get-Content -LiteralPath $path -Raw
    $markers = @('private struct BaseFlowState','private sealed class StreamEmitter','private struct StreamRingSample',
        'private static bool UsesWoundOffshootHandoff(', 'private static void TriggerHeartbeat(',
        'private static float WoundOffshootPressure(', 'private static float WobbleTransition(',
        'private static Vector2 RandomInsideUnitCircle(', 'private static void ResolveWobbledFrame(',
        'private static float BaseRadiusScale(', 'private static float NormalSpeedScale(', 'private static float HeartbeatSpeedScale(',
        'private static float StreamMaximumFlight(', 'private static void UpdateWoundOffshootFlow(',
        'private static void UpdateStreamFlow(', 'private static void CaptureStreamSegment(', 'private static float StreamStrength(',
        'private static bool HeartbeatIsActive(', 'private static void BeginDrain(', 'private static float DrainDuration(',
        'private static void AppendStreamSample(', 'private static void PruneStreamSamples(', 'private static void ClearStreamSamples(',
        'private static StreamRingSample GetStreamSample(', 'private static int DecalCellKey(', 'private static void PredictImpact(',
        'private static Vector3 PositionAt(', 'private static float Next01(',
        'private struct DecalCell', 'private struct DripImpact', 'private static void UpdateDecals(',
        'private static void TryEmitHeartbeatDecal(', 'private static void TryEmitBaseDecal(',
        'private static Vector3 ParcelVelocity(', 'private static float BaseDepositInterval(', 'private static bool QueueImpact(',
        'private static bool IsDismembermentFlow(', 'private static int DecalFrameLimit(',
        'private static int DecalSecondLimit(', 'private static bool ReserveDecalCell(', 'private static int StreamPriority(')
    $blocks = foreach ($marker in $markers) {
        $start = $runtime.IndexOf($marker, [StringComparison]::Ordinal)
        if ($start -lt 0) {
            if ($marker -match 'HeartbeatIsActive|CaptureStreamSegment|TryEmitHeartbeatDecal|TryEmitBaseDecal|ParcelVelocity|BaseDepositInterval|QueueImpact') { continue }
            throw "Missing production block: $marker"
        }
        $open = $runtime.IndexOf('{', $start); $arrow = $runtime.IndexOf('=>', $start)
        if ($arrow -ge 0 -and $arrow -lt $open) { $end = $runtime.IndexOf(';', $arrow) + 1 }
        else {
            $depth = 1; $end = $open + 1
            while ($depth -gt 0) { if ($runtime[$end] -eq '{') { $depth++ }; if ($runtime[$end] -eq '}') { $depth-- }; $end++ }
        }
        $runtime.Substring($start, $end - $start)
    }
    $constants = [regex]::Matches($runtime, '(?m)^\s*(?:internal|private) const (?:int|float) \w+ = [^;]+;').Value
    $body = $blocks -join "`n"
    if (!$body.Contains('internal float NextSampleExpiryAt;')) { $body = $body.Replace('internal int SampleCount;', 'internal int SampleCount; internal float NextSampleExpiryAt;') }
    # Measure the frozen baseline with the same counter-only fixture.
    if (!$body.Contains('MainThreadWork.BloodRaycasts')) {
        $body = $body.Replace('int count = Physics.RaycastNonAlloc', 'ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodRaycasts, 1); int count = Physics.RaycastNonAlloc')
        $body = $body.Replace('hasImpact = false;', 'ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodImpactPredictions, 1); hasImpact = false;')
    }
    $prefix = "using System; using UnityEngine; using EFT; using Comfort.Common; using Systems.Effects; using Rupture.Ragdolls.Classes; namespace Rupture.Dismemberment.Classes.BloodStreams { internal static partial class $className {`n"
    [IO.File]::WriteAllText((Join-Path $project "Assets/Editor/$className.cs"), $prefix + ($constants -join "`n") + "`n" + $body + "`n} }")
    $fixture = Get-Content (Join-Path $repo 'tests/Unity/BloodEmitterScenario.cs') -Raw
    if ($body.Contains('internal bool ImpactArrivalPending;')) { $fixture = "#define BASELINE_EMITTER`n" + $fixture }
    [IO.File]::WriteAllText((Join-Path $project "Assets/Editor/$className.Scenario.cs"), $fixture.Replace('PulsatingBloodStreamSystem', $className))
    $decalFixture = Get-Content (Join-Path $repo 'tests/Unity/BloodDepositScenario.cs') -Raw
    [IO.File]::WriteAllText((Join-Path $project "Assets/Editor/$className.Deposits.cs"), $decalFixture.Replace('PulsatingBloodStreamSystem', $className))
}
Write-Extracted $runtimePath 'PulsatingBloodStreamSystem'
$trackerBaseline = Join-Path $project 'Assets/Editor/BaselineBleedingWoundTracker.cs'
[IO.File]::WriteAllText($trackerBaseline, '')
if ($BaselineSource) {
    $trackerSource = Join-Path (Split-Path $BaselineSource -Parent) 'BleedingWoundTracker.cs'
    if (Test-Path -LiteralPath $trackerSource) {
        $text = [IO.File]::ReadAllText($trackerSource).Replace('class BleedingWoundTracker', 'class BaselineBleedingWoundTracker')
        $text = [regex]::Replace($text, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
        [IO.File]::WriteAllText($trackerBaseline, $text)
    }
}
if ($BaselineSource) { Write-Extracted $BaselineSource 'BaselinePulsatingBloodStreamSystem' }
else {
    # Avoid accidentally reusing an older comparison in this persistent project.
    [IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/BaselinePulsatingBloodStreamSystem.cs'), '')
    [IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/BaselinePulsatingBloodStreamSystem.Scenario.cs'), '')
    [IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/BaselinePulsatingBloodStreamSystem.Deposits.cs'), '')
}
foreach ($name in @('BloodEmitterRuntimeTests.cs','BloodTrackingRuntimeTests.cs','MainThreadTracingFixture.cs','BloodReferenceScenario.cs','BloodClosestHitScenario.cs','LivingIndexRuntimeTests.cs')) { Copy-Item (Join-Path $repo "tests/Unity/$name") (Join-Path $project 'Assets/Editor') -Force }
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$eligibilityMarkers = @('private struct BaseFlowState', 'private sealed class StreamEmitter',
    'private sealed class LivingStream', 'private struct PendingDescriptor',
    'private static bool CanStartStream(', 'internal static void Receive(',
    'private static void RestoreLivingVisuals(', 'private static void ProcessPending(',
    'private static void QueuePending(', 'private static void RemovePending(',
    'private static int FindActive(', 'private static int FindEmitterSlot(',
    'private static int StreamPriority(', 'private static void ResetBaseFlow(',
    'private static void TrackLivingStream(', 'private static void UnindexLivingStream(',
    'private static void Retire(', 'private static void ClearEmitter(', 'private static void ClearStreamSamples(',
    'private static void RememberRetired(', 'private static bool IsRetired(', 'private static void StopById(',
    'private static void ResetHeartbeatState(', 'private static float Next01(',
    'private static bool ProfileMatches(')
$eligibilityBlocks = @($eligibilityMarkers | ForEach-Object { Get-ProductionBlock $runtime $_ })
$startSource = if ($StartBaselineSource) { Get-Content -LiteralPath $StartBaselineSource -Raw } else { $runtime }
$startBlock = Get-ProductionBlock $startSource 'private static void StartOrReplace('
if ($StartBaselineSource -and $startBlock.Contains('CanStartStream(')) { throw '-StartBaselineSource must contain the unchanged pre-fix, unguarded StartOrReplace.' }
$eligibilityBlocks += $startBlock
$constants = [regex]::Matches($runtime, '(?m)^\s*(?:internal|private) const (?:int|float) \w+ = [^;]+;').Value
$prefix = 'using System; using System.Collections.Generic; using UnityEngine; using EFT; namespace Rupture.Dismemberment.Classes.BloodStreams { internal static partial class BloodStreamEligibilityRuntimeTests {'
[IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/BloodStreamEligibilityExtracted.cs'), $prefix + ($constants -join "`n") + "`n" + ($eligibilityBlocks -join "`n") + '} }')
foreach ($adapter in @(
    @('src/Rupture/Multiplayer/StandaloneRuntime.cs', 'Rupture', 'StandaloneRuntime'),
    @('src/Rupture.Fika/FikaRuntime.cs', 'Rupture.Fika', 'FikaRuntime'))) {
    $method = Get-ProductionBlock (Get-Content (Join-Path $repo $adapter[0]) -Raw) 'public bool IsHumanPlayer('
    $text = "using EFT; using Fika.Core.Main.Players; namespace $($adapter[1]) { internal sealed class $($adapter[2]) { $method } }"
    [IO.File]::WriteAllText((Join-Path $project "Assets/Editor/$($adapter[2]).cs"), $text)
}
# Use the real packet fields and every production trigger kind, in the existing fixture namespace.
$packet = Get-Content (Join-Path $repo 'src/Rupture/Dismemberment/Classes/Packets/BloodStreamPacket.cs') -Raw
$packet = $packet.Replace('namespace Rupture.Dismemberment.Classes.Packets;', 'namespace Rupture.Dismemberment.Classes.BloodStreams {') + "`n}"
[IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/BloodStreamPacket.cs'), $packet)
$indexBlocks = foreach ($marker in @('private sealed class LivingStream','private static void TrackLivingStream(', 'private static void UnindexLivingStream(', 'internal static bool HasLivingWound(')) {
    $start = $runtime.IndexOf($marker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production index method: $marker" }
    $open = $runtime.IndexOf('{', $start); $depth = 1; $end = $open + 1
    while ($depth -gt 0) { if ($runtime[$end] -eq '{') { $depth++ }; if ($runtime[$end] -eq '}') { $depth-- }; $end++ }
    $runtime.Substring($start, $end - $start)
}
[IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/LivingIndexExtracted.cs'), 'using System.Collections.Generic; using UnityEngine; using EFT; namespace Rupture.Dismemberment.Classes.BloodStreams { internal static partial class LivingIndexRuntimeTests {' + ($indexBlocks -join "`n") + '} }')
foreach ($source in @('src/Rupture/EffectLifetimes.cs','src/Rupture/Dismemberment/Classes/BloodStreams/BleedingWoundTracker.cs','src/Rupture/Dismemberment/Classes/BloodStreams/BleedEmissionPolicy.cs','src/Rupture/Health/NativeHealthEffects.cs')) {
    $content = Get-Content (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') { $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n" }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Editor/' + [IO.Path]::GetFileName($source))), $content)
}
$log = Join-Path $repo 'artifacts/blood-emitter-runtime.log'
$started = [DateTime]::UtcNow
$process = Start-Process $UnityEditorPath -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','Rupture.Dismemberment.Classes.BloodStreams.BloodEmitterRuntimeTests.RunBatch','-logFile',('"'+$log+'"'))
$process.WaitForExit()
if ($process.ExitCode) { throw "Emitter runtime validation failed: $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh emitter report was not produced.' }
$report = Get-Content $reportPath -Raw | ConvertFrom-Json
if (!$report.passed -or $report.referenceChecks -lt 18) { throw 'Reference parcel checks did not pass.' }
if ($report.eligibilityPolicyChecks -lt 441 -or $report.eligibilityAdapterChecks -lt 11 -or $report.eligibilityStartChecks -lt 1764 -or $report.eligibilityFunnelChecks -lt 1764 -or $report.eligibilityRestoreBudgetChecks -lt 1) { throw 'Human-head eligibility/start/funnel checks did not pass.' }
$report | ConvertTo-Json -Depth 6
