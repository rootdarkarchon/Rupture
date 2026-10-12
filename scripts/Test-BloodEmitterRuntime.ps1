[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe', [string]$BaselineSource)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/BloodEmitterRuntimeUnity'
foreach ($dir in @('Assets/Editor','ProjectSettings','Packages')) { New-Item -ItemType Directory -Force (Join-Path $project $dir) | Out-Null }
Set-Content (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
$runtimePath = Join-Path $repo 'src/Rupture/Dismemberment/Classes/BloodStreams/PulsatingBloodStreamSystem.cs'
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
$report | ConvertTo-Json -Depth 6
