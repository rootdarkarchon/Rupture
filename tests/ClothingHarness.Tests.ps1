param(
    [Parameter(Mandatory)][string]$ReportPath,
    [Parameter(Mandatory)][string]$FixtureDirectory
)
$ErrorActionPreference = 'Stop'
function Assert-Harness([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Read-GzipJson([string]$Path) {
    $file = [IO.File]::OpenRead($Path)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
    $reader = [IO.StreamReader]::new($gzip)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
    finally { $reader.Dispose(); $gzip.Dispose(); $file.Dispose() }
}
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$catalog = Get-Content -LiteralPath (Join-Path $FixtureDirectory 'catalog.json') -Raw | ConvertFrom-Json
Assert-Harness ($report.State -eq 'Complete') 'The catalog sweep did not complete.'
Assert-Harness ($report.CompletedCases -eq $report.ExpectedCases) 'Cases are missing from the result.'
Assert-Harness ($report.ExpectedCases -eq 4 * $catalog.Garments.Count) 'Run the entire catalog before coverage validation.'
Assert-Harness (@($report.Cases | Select-Object -ExpandProperty Id -Unique).Count -eq $report.Cases.Count) 'Duplicate case IDs.'
Assert-Harness (@($report.Cases | Where-Object { $_.Status -notin @('Passed','Failed','Unsupported','Unreadable') }).Count -eq 0) 'Unfinished case status.'
$checkedCaptures = 0
foreach ($entry in $catalog.Garments) {
    $cases = @($report.Cases | Where-Object GarmentId -eq $entry.Id)
    Assert-Harness ($cases.Count -eq 4) "Missing garment clone: $($entry.Bundle)"
    Assert-Harness (@($cases | ForEach-Object { $_.Side + ':' + $_.Level } | Select-Object -Unique).Count -eq 4) 'Missing independent side/level case.'
    Assert-Harness (@($cases | Where-Object Level -eq Proximal).Count -eq 2) 'Proximal cases are incomplete.'
    Assert-Harness (@($cases | Where-Object Level -eq Distal).Count -eq 2) 'Distal cases are incomplete.'
    if (!$entry.Error) {
        $fixturePath = Join-Path $FixtureDirectory $entry.Fixture
        Assert-Harness ((Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash -eq $entry.FixtureHash) "Fixture changed: $($entry.Bundle)"
    }
    foreach ($case in $cases) {
        Assert-Harness (Test-Path -LiteralPath (Join-Path $case.EvidenceDirectory 'result.json')) "Missing case result: $($case.Id)"
        if ($case.Status -eq 'Failed') {
            Assert-Harness (![string]::IsNullOrWhiteSpace($case.Reason)) 'Rejected case lost its reason.'
            Assert-Harness ($case.Reason -notmatch 'NullReferenceException|Harness invariant|IndexOutOfRangeException') "Harness implementation fault: $($case.Reason)"
        }
        foreach ($renderer in $case.Renderers) {
            if ($renderer.Status -eq 'Unreadable') {
                Assert-Harness (@($renderer.Attempts).Count -eq 0 -and !$renderer.Capture) 'Unreadable source was submitted to the cutter.'
            }
            $attempts = @($renderer.Attempts)
            Assert-Harness ($attempts.Count -le 6) 'Cutter exceeded six candidates.'
            for ($attemptIndex = 0; $attemptIndex -lt $attempts.Count; $attemptIndex++) {
                $expectedLevel = if ($attemptIndex -lt 3) { $case.Level } elseif ($case.Level -eq 'Proximal') { 'Distal' } else { 'Proximal' }
                $expectedOffset = @(0.0, -0.05, 0.05)[$attemptIndex % 3]
                Assert-Harness ($attempts[$attemptIndex].Level -eq $expectedLevel) 'Candidate level order changed.'
                Assert-Harness ([Math]::Abs($attempts[$attemptIndex].FractionOffset - $expectedOffset) -lt 0.000001) 'Candidate offset order changed.'
            }
            if ($renderer.Status -eq 'Passed') {
                Assert-Harness ($attempts.Count -gt 0 -and !$attempts[-1].Failure) 'Successful cut has no successful candidate.'
                Assert-Harness ($case.EffectiveLevel -eq $attempts[-1].Level) 'Committed level differs from successful candidate.'
            }

            if ($renderer.Capture -and (Test-Path -LiteralPath $renderer.Capture)) {
                $capture = Read-GzipJson $renderer.Capture
                Assert-Harness ($capture.Cuts.Count -eq 1) 'A clone accumulated more than one cut.'
                Assert-Harness ($capture.Cuts[0].Level -eq $capture.Attempts[-1].Level) 'Captured cut level differs from the selected column.'
                Assert-Harness ($capture.Cuts[0].Seed -eq $case.Seed) 'Captured wound seed differs from its result.'
                Assert-Harness ($capture.BodyPart -eq $case.Side) 'Captured side differs from its result.'
                Assert-Harness ($capture.NewCutIndex -eq 0) 'Clone did not start at its first cut.'
                $sideLetter = if ($case.Side.StartsWith('Left')) { 'L' } else { 'R' }
                $leg = $entry.Part -eq 'Feet'
                $startSuffix = if ($capture.Cuts[0].Level -eq 'Proximal') { if ($leg) { 'Thigh1' } else { 'Upperarm' } } else { if ($leg) { 'Calf' } else { 'Forearm1' } }
                $endSuffix = if ($capture.Cuts[0].Level -eq 'Proximal') { if ($leg) { 'Calf' } else { 'Forearm1' } } else { if ($leg) { 'Foot' } else { 'Palm' } }
                Assert-Harness ($capture.Bones[$capture.Cuts[0].StartIndex].Name -eq "Base Human$sideLetter$startSuffix") 'Captured start bone belongs to the wrong side/segment.'
                Assert-Harness ($capture.Bones[$capture.Cuts[0].EndIndex].Name -eq "Base Human$sideLetter$endSuffix") 'Captured end bone belongs to the wrong side/segment.'
                $sourcePath = Join-Path (Split-Path $renderer.Capture -Parent) $capture.Source
                Assert-Harness (Test-Path -LiteralPath $sourcePath) 'Diagnostic source snapshot is missing.'
                if ($renderer.Status -eq 'Passed') {
                    Assert-Harness ($capture.Outcome -eq 'prepared') 'Successful renderer did not complete production preparation.'
                    $expectedSides = if ($renderer.LodLevel -gt 0) { 1 } else { 2 }
                    Assert-Harness (@($capture.Boundaries).Count -eq $expectedSides) 'Successful renderer lost its body or LOD0 gib boundary evidence.'
                }
                $checkedCaptures++
            } elseif ($renderer.Status -eq 'Passed' -or $renderer.Stage -in @('boundary-analysis','geometry-build','mesh-upload')) {
                throw "Cutter result lost its replay evidence: $($case.Id) / $($renderer.Renderer)"
            }
        }
    }
}
Assert-Harness ($checkedCaptures -gt 0) 'No production cutter captures were exercised.'
Write-Output "PASS: $($catalog.Garments.Count) garments / $($report.Cases.Count) isolated side-level cases; $checkedCaptures source/selection/boundary captures verified."
Write-Output ('Findings: ' + ($report.Counts | ConvertTo-Json -Compress))
