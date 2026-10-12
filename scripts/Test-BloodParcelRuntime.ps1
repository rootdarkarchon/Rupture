[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/BloodParcelRuntimeUnity'
foreach ($directory in @('Assets/Editor','Assets/Rupture','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0","com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.physics":"1.0.0","com.unity.modules.assetbundle":"1.0.0"}}'
# Compile the production renderer and its actual history layout/math. Only the
# emitter/material providers are fixture-owned; no alternative renderer is tested.
$runtime = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture/Dismemberment/Classes/BloodStreams/PulsatingBloodStreamSystem.cs') -Raw
function Extract-Block([string]$marker) {
    $start = $runtime.IndexOf($marker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production block: $marker" }
    $open = $runtime.IndexOf('{', $start)
    $depth = 1; $end = $open + 1
    while ($depth -gt 0 -and $end -lt $runtime.Length) {
        if ($runtime[$end] -eq '{') { $depth++ }
        if ($runtime[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw "Unbalanced production block: $marker" }
    $runtime.Substring($start, $end - $start)
}
$constants = [regex]::Matches($runtime, '(?m)^\s*(?:internal|private) const (?:int|float) (?:MaximumVisualEmitters|MaximumRenderedEmitters|MaximumStreamSamplesPerEmitter|MaximumFlightSeconds|StreamSampleInterval|WoundParcelLengthOverlap|MaximumWoundParcelLength|WoundParcelSides|WoundParcelAxialRings) = [^;]+;')
if ($constants.Count -ne 9) { throw 'Production parcel constants changed; update fixture extraction.' }
$blocks = @('private struct StreamRingSample', 'private static StreamRingSample GetStreamSample(', 'private static Vector3 PositionAt(', 'private static float Next01(') | ForEach-Object { Extract-Block $_ }
$generated = "using UnityEngine;`nnamespace Rupture.Dismemberment.Classes.BloodStreams { internal static partial class PulsatingBloodStreamSystem {`n" + ($constants.Value -join "`n") + "`n" + ($blocks -join "`n") + "`n} }"
[IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/ProductionParcelHistory.cs'), $generated)
foreach ($source in @('src/Rupture/Dismemberment/Classes/BloodStreams/PulsatingBloodStreamSystem.Rendering.cs',
    'unity/RuptureWound/Assets/Rupture/BloodStreams/BloodParcelRenderer.cs',
    'unity/RuptureWound/Assets/Rupture/BloodStreams/CapturedParcelHistory.cs',
    'unity/RuptureWound/Assets/Rupture/BloodStreams/BenchmarkAndPreviewPulsatingBloodStream.cs',
    'unity/RuptureWound/Assets/Rupture/BloodStreams/BloodParcelVisualTests.cs',
    'tests/Unity/MainThreadTracingFixture.cs', 'tests/Unity/BloodParcelRuntimeTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') { $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n" }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Editor/' + [IO.Path]::GetFileName($source))), $content)
}
Copy-Item -LiteralPath (Join-Path $repo 'unity/RuptureWound/Assets/Rupture/BloodStream.shader') -Destination (Join-Path $project 'Assets/Rupture') -Force
$log = Join-Path $repo 'artifacts/blood-parcel-runtime-unity.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-rptBundlePath',('"'+(Join-Path $repo 'assets/bundles/rupture-wound.bundle')+'"'),'-executeMethod','Rupture.Dismemberment.Classes.BloodStreams.BloodParcelRuntimeTests.RunBatch','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Blood parcel runtime regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh blood parcel report was not produced.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Blood parcel runtime report failed.' }
$imageReportPath = Join-Path $project 'ParcelVerification/verification.json'
$imageReport = Get-Content -LiteralPath $imageReportPath -Raw | ConvertFrom-Json
if ((Get-Item -LiteralPath $imageReportPath).LastWriteTimeUtc -lt $started -or !$imageReport.passed -or
    $imageReport.bundleSha256 -ne (Get-FileHash (Join-Path $repo 'assets/bundles/rupture-wound.bundle') -Algorithm SHA256).Hash -or
    $imageReport.cases -lt 12 -or $imageReport.minimumCoveredPixels -lt 100 -or $imageReport.maximumMeanChannelError -ge 2) {
    throw 'Fresh built-bundle parcel image comparison did not pass.'
}
Copy-Item -LiteralPath $imageReportPath -Destination (Join-Path $repo 'assets/bundles/rupture-parcels.verification.json') -Force
$benchmark = Join-Path $project 'AssetBundles/rupture-bloodstream.benchmark.json'
& (Join-Path $repo 'tests/WoundBundle.Tests.ps1') -BloodStreamBenchmarkPath $benchmark
if (!$?) { throw 'Fresh blood benchmark failed validation.' }
Copy-Item -LiteralPath $benchmark -Destination (Join-Path $repo 'assets/bundles/rupture-bloodstream.benchmark.json') -Force
$report | ConvertTo-Json
