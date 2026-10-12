[CmdletBinding()]
param(
    [string] $BundlePath = (Join-Path $PSScriptRoot "..\assets\bundles\rupture-wound.bundle"),
    [string] $BenchmarkPath = (Join-Path $PSScriptRoot "..\assets\bundles\rupture-wound.benchmark.json"),
    [string] $BloodStreamBenchmarkPath = (Join-Path $PSScriptRoot "..\assets\bundles\rupture-bloodstream.benchmark.json"),
    [string] $LimbBenchmarkPath = (Join-Path $PSScriptRoot "..\assets\bundles\rupture-limb-wound.benchmark.json")
)

$ErrorActionPreference = "Stop"
$resolvedBundle = [IO.Path]::GetFullPath($BundlePath)
if (-not (Test-Path -LiteralPath $resolvedBundle -PathType Leaf)) {
    throw "Wound bundle not found: $resolvedBundle. Build it with unity\RuptureWound first."
}

$file = Get-Item -LiteralPath $resolvedBundle
if ($file.Length -lt 32) {
    throw "Wound bundle is implausibly small: $($file.Length) bytes."
}

$stream = [IO.File]::OpenRead($resolvedBundle)
try {
    $header = [byte[]]::new(7)
    if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or [Text.Encoding]::ASCII.GetString($header) -ne "UnityFS") {
        throw "Wound bundle does not have a UnityFS header: $resolvedBundle"
    }
}
finally {
    $stream.Dispose()
}

$hash = (Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256).Hash
$verification = Get-Content -LiteralPath ($resolvedBundle + '.verification.json') -Raw | ConvertFrom-Json
$expectedShaders = @('Rupture/ClothingSoak','Rupture/HeadWoundInterior','Rupture/LimbSeverSurface','Rupture/PulsatingBloodStream')
if ($verification.sha256 -ne $hash -or $verification.bundle -ne 'rupture-wound.bundle' -or
    $verification.unityVersion -ne '2022.3.43f1' -or !$verification.verifiedUtc -or
    (($verification.shaders | Sort-Object) -join ',') -ne ($expectedShaders -join ',') -or
    ($verification.computeShaders -join ',') -ne 'ClothingSoak') {
    throw 'Current bundle verification hash, Unity version, or shader identity mismatch.'
}
$parcelReport = Get-Content -LiteralPath (Join-Path (Split-Path $resolvedBundle -Parent) 'rupture-parcels.verification.json') -Raw | ConvertFrom-Json
if (!$parcelReport.passed -or $parcelReport.bundleSha256 -ne $hash -or $parcelReport.cases -lt 12 -or
    $parcelReport.minimumCoveredPixels -lt 100 -or $parcelReport.maximumMeanChannelError -ge 2.0) {
    throw 'Built bundle has no passing CPU/GPU parcel image equivalence report.'
}
$resolvedBenchmark = [IO.Path]::GetFullPath($BenchmarkPath)
if (-not (Test-Path -LiteralPath $resolvedBenchmark -PathType Leaf)) {
    throw "Specialized wound-cutter benchmark report not found: $resolvedBenchmark. Run Tools > Rupture > Benchmark Head Wound Cutter."
}
$benchmark = Get-Content -LiteralPath $resolvedBenchmark -Raw | ConvertFrom-Json
if ($benchmark.unityVersion -ne "2022.3.43f1" -or -not $benchmark.allPassed -or
    -not $benchmark.index32Passed -or @($benchmark.cases).Count -ne 3) {
    throw "Specialized wound-cutter benchmark is not a valid Unity 2022.3.43f1 common/high/separated-surface report: $resolvedBenchmark"
}
$multipleNames = @('separate', 'intersecting', 'near-coincident', 'coincident', 'reverse', 'separated', 'high-intersecting', 'forced-rejection')
if (@($benchmark.multipleCases).Count -ne $multipleNames.Count) { throw 'Head benchmark is missing cumulative wound acceptance cases.' }
foreach ($name in $multipleNames) {
    $matches = @($benchmark.multipleCases | Where-Object name -eq $name)
    if ($matches.Count -ne 1) { throw "Missing or duplicate cumulative wound case: $name" }
    $case = $matches[0]
    $geometryLimit = if ($name -eq 'high-intersecting') { 140 } else { 60 }
    $totalLimit = if ($name -eq 'high-intersecting') { 200 } else { 100 }
    $allocationLimit = if ($name -eq 'high-intersecting') { 4194304 } else { 2097152 }
    if (!$case.valid -or $case.triangleClassificationMilliseconds -gt $geometryLimit -or
        $case.totalMilliseconds -gt $totalLimit -or $case.managedDeltaBytes -gt $allocationLimit -or
        $case.generatedHeadVertexCount -gt 16384 -or $case.generatedWoundVertexCount -gt 16384) {
        throw "Cumulative head wound case failed geometry, timing or allocation acceptance: $name"
    }
}
foreach ($case in @($benchmark.cases)) {
    $geometryMilliseconds = [double]$case.triangleClassificationMilliseconds + [double]$case.cavityGenerationMilliseconds
    $policyGeometryMaximum = [double]$case.maximumGeometryMilliseconds
    if (-not $case.valid -or $geometryMilliseconds -gt $policyGeometryMaximum -or
        [double]$case.totalMilliseconds -gt [double]$case.maximumTotalMilliseconds -or
        [long]$case.managedDeltaBytes -gt [long]$case.maximumManagedBytes -or
		[int]$case.resultHeadVertexCount -ne ([int]$case.sourceVertexCount + [int]$case.generatedHeadVertexCount) -or
		[int]$case.generatedHeadVertexCount -le 0 -or [int]$case.clippedTriangleCount -le 0 -or
		[int]$case.removedTriangleCount -le 0 -or [int]$case.cavityVertexCount -le 0 -or
        [int]$case.generatedWoundVertexCount -ne ([int]$case.cavityVertexCount + [int]$case.rimVertexCount) -or
        [int]$case.generatedWoundTriangleCount -ne ([int]$case.cavityTriangleCount + [int]$case.rimTriangleCount) -or
		[int]$case.generatedWoundVertexCount -gt 2048) {
        throw "Specialized wound-cutter benchmark case '$($case.name)' is invalid or exceeds its geometry, total-time, allocation, or bounded-growth gate."
    }
    if ([double]$case.entryOpeningCoverageRadiusMillimeters -le 0 -or
        [double]$case.exitOpeningCoverageRadiusMillimeters -le 0 -or
		[double]$case.minimumRimCoverageMarginMillimeters -lt 0.8 -or
		[int]$case.rimVertexCount -le 0 -or [int]$case.rimTriangleCount -le 0 -or
        [double]$case.minimumEntryRimFacingDot -lt 0 -or [double]$case.minimumExitRimFacingDot -lt 0 -or
		[int]$case.entryRimSurfaceHitCount -le 0 -or [int]$case.exitRimSurfaceHitCount -le 0 -or
		[int]$case.boundaryComponentCount -lt 2 -or [int]$case.solidCanalSpanCount -lt 1 -or
		[int]$case.entryLipCount -ne 1 -or [int]$case.exitFlapCount -ne 8) {
		throw "Specialized wound-cutter benchmark case '$($case.name)' does not contain bounded component-driven spans, lips, and the eight-flap terminal exit burst."
    }
	if ($case.name -eq "separated-shell-crossings" -and
		([int]$case.boundaryComponentCount -lt 4 -or [int]$case.solidCanalSpanCount -lt 2 -or
		 [int]$case.intermediateLipCount -lt 2)) {
		throw "Separated-surface benchmark did not preserve its empty interval as two independent canal spans."
	}
}

$resolvedBloodStreamBenchmark = [IO.Path]::GetFullPath($BloodStreamBenchmarkPath)
if (-not (Test-Path -LiteralPath $resolvedBloodStreamBenchmark -PathType Leaf)) {
    throw "Pulsating blood-stream benchmark report not found: $resolvedBloodStreamBenchmark. Run Tools > Rupture > Benchmark Pulsating Blood Streams."
}
$bloodStreamBenchmark = Get-Content -LiteralPath $resolvedBloodStreamBenchmark -Raw | ConvertFrom-Json
if (($bloodStreamBenchmark.backend -ne 'instanced-parcels' -or $bloodStreamBenchmark.rendering -ne 'parcel-only')) { throw 'Blood-stream benchmark does not exercise the current renderer.' }
$streamCases = @($bloodStreamBenchmark.cases)
if ($bloodStreamBenchmark.unityVersion -ne "2022.3.43f1" -or -not $bloodStreamBenchmark.allPassed -or
    $streamCases.Count -ne 3 -or (($streamCases | ForEach-Object { [int]$_.emitterCount }) -join ',') -ne '1,8,24') {
    throw "Pulsating blood-stream benchmark is not a valid Unity 2022.3.43f1 1/8/24-emitter report: $resolvedBloodStreamBenchmark"
}
foreach ($case in $streamCases) {
    if ([int]$case.maximumRenderedVertices -ne [int]$case.parcelVertexCount -or
        [int]$case.maximumRenderedTriangles -ne [int]$case.parcelTriangleCount -or
        [int]$case.maximumInstanceUploadBytes -ne [int]$case.maximumRenderedParcels * 64 -or
        [int]$case.maximumRenderedParcels -gt 2304) { throw 'Instanced geometry/upload accounting or capacity is invalid.' }
    if (-not $case.valid -or [long]$case.managedSteadyStateBytes -ne 0 -or
        [double]$case.totalAverageMilliseconds -ge 0.75 -or [double]$case.totalP99Milliseconds -ge 2.0 -or
        [int]$case.maximumLogicalEmitters -ne 64 -or [int]$case.maximumRenderedEmitters -ne 24 -or
        [int]$case.maximumStreamSamplesPerEmitter -ne 96 -or [int]$case.maximumVertices -ne 26 -or
        [int]$case.maximumTriangles -ne 48 -or [int]$case.decalEvents -gt 144) {
        throw "Pulsating blood-stream benchmark case '$($case.emitterCount)' violates its timing, allocation, decal, or geometry bounds."
    }
    if ($null -ne $case.maximumStreamSamplesPerEmitter -and
        [int]$case.maximumStreamSamplesPerEmitter -ne 96) {
        throw "Pulsating blood-stream benchmark case '$($case.emitterCount)' violates its fixed stream-history bound."
    }
}

$resolvedLimbBenchmark = [IO.Path]::GetFullPath($LimbBenchmarkPath)
if (-not (Test-Path -LiteralPath $resolvedLimbBenchmark -PathType Leaf)) {
    throw "Procedural limb-wound benchmark report not found: $resolvedLimbBenchmark. Run Tools > Rupture > Benchmark Limb Sever Wounds."
}
$limbBenchmark = Get-Content -LiteralPath $resolvedLimbBenchmark -Raw | ConvertFrom-Json
$limbCases = @($limbBenchmark.cases)
if ($limbBenchmark.unityVersion -ne "2022.3.43f1" -or -not $limbBenchmark.allPassed -or $limbCases.Count -ne 12) {
    throw "Procedural limb-wound benchmark is not a valid Unity 2022.3.43f1 four-cut/three-seed report: $resolvedLimbBenchmark"
}
foreach ($case in $limbCases) {
    $expectedBones = if ($case.level -eq 'Proximal') { 1 } else { 2 }
    $expectedDecals = if ($case.leg) { 8 } else { 6 }
    if (-not $case.valid -or [int]$case.bonesPerSide -ne $expectedBones -or
        [int]$case.bodyDecals -ne $expectedDecals -or [int]$case.gibDecals -ne $expectedDecals -or
        [int]$case.bodyFlaps -lt 3 -or [int]$case.bodyFlaps -gt 7 -or
        [int]$case.gibFlaps -lt 3 -or [int]$case.gibFlaps -gt 7 -or
        [int]$case.bodyCords -lt 2 -or [int]$case.bodyCords -gt 4 -or
        [int]$case.gibCords -lt 2 -or [int]$case.gibCords -gt 4 -or
        [double]$case.bodyPlateauMillimeters -le 0 -or [double]$case.gibPlateauMillimeters -le 0 -or
        [double]$case.maximumReachMillimeters -gt 42.01 -or
        [double]$case.minimumFractureSpanMillimeters -le 0.5 -or [double]$case.milliseconds -ge 25) {
        throw "Procedural limb-wound benchmark case '$($case.name)' violates topology, anatomical, decal, reach, or timing gates."
    }
}

$bundleBuilder = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\unity\RuptureWound\Assets\Editor\BuildRuptureWoundBundle.cs") -Raw
$limbShader = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\unity\RuptureWound\Assets\Rupture\LimbSeverSurface.shader") -Raw
if ($bundleBuilder -notmatch 'LimbSeverSurface\.shader' -or $bundleBuilder -notmatch 'assetNames\.Length != 5' -or
    $limbShader -notmatch 'Shader\s+"Rupture/LimbSeverSurface"') {
    throw "The five-asset wound-bundle contract or dedicated limb-sever shader source is missing."
}

$soaking = Get-Content -LiteralPath (Join-Path (Split-Path $resolvedBundle -Parent) 'rupture-soaking.verification.json') -Raw | ConvertFrom-Json
if (!$soaking.passed -or @($soaking.samples).Count -ne 3 -or @($soaking.samples | Where-Object allocatedBytes -ne 0).Count -ne 0) {
    throw 'Clothing soaking has no passing allocation/LOD/persistence runtime report.'
}
if (!$soaking.commandComparisonLinear.passed -or !$soaking.sharedComparisonLinear.passed) { throw 'Clothing soaking lacks Linear native-composition comparisons.' }
if (!$soaking.gpuResident -or !$soaking.builtBundle -or !$soaking.destinationRecovery -or $soaking.bundleSha256 -ne $hash -or
    !$soaking.gpuField.passed -or $soaking.gpuField.checks -lt 2000000 -or $soaking.gpuField.maximumSaturationError -gt .0001) {
    throw 'GPU clothing soaking lacks current built-bundle field equivalence and destination recovery verification.'
}
if (!$soaking.sharedWoundsMatchImmediate) { throw 'Clothing soaking lacks shared-wound batching and partial-upload image verification.' }
if (!$soaking.nativeCompositorReference -or !$soaking.twoPassPainting -or $soaking.initialPixels -lt 8 -or
    $soaking.pixelsAfter30Seconds -le $soaking.initialPixels * 4 -or $soaking.downhillCentimetres -lt 10) {
    throw 'Clothing soaking lacks visible initial coverage and timed downhill growth with EFT native composition.'
}
if (!$soaking.appearance.passed -or $soaking.appearance.maximumNativeChannelDifference -gt 3 -or !$soaking.appearance.unchangedStainRetained -or
    $soaking.appearance.nativeCoreRed -lt 5 -or $soaking.appearance.maximumSoakRed -gt $soaking.appearance.nativeCoreRed + 1) {
    throw 'Clothing soaking lacks native brightness calibration and unchanged-texel persistence verification.'
}
foreach ($source in @(
    @('runtimeSourceSha256','src/Rupture/Dismemberment/Classes/BloodStreams/ClothingSoakingSystem.cs'),
    @('fieldSourceSha256','unity/RuptureWound/Assets/Rupture/BloodStreams/ClothingSoakField.cs'),
    @('gpuSourceSha256','unity/RuptureWound/Assets/Rupture/BloodStreams/ClothingSoakGpu.cs'),
    @('computeSourceSha256','unity/RuptureWound/Assets/Rupture/ClothingSoak.compute'),
    @('shaderSourceSha256','unity/RuptureWound/Assets/Rupture/ClothingSoak.shader'))) {
    if ($soaking.($source[0]) -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot ('../' + $source[1])) -Algorithm SHA256).Hash) {
        throw "Stale clothing soaking verification: $($source[1])"
    }
}
Write-Output "PASS: five-asset wound bundle, head/limb/stream benchmarks, GPU soaking equivalence/persistence/LOD/allocation report, and size are valid. SHA256=$hash bytes=$($file.Length)"
