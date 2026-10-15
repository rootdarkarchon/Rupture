[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe',
    [Parameter(Mandatory=$true)][string]$SptInstallPath, [switch]$FrameBenchmark, [string]$BaselineSource)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:RPT_SOAK_GAME_ROOT = $SptInstallPath
$env:RPT_SOAK_BUNDLE = Join-Path $repo 'assets/bundles/rupture-wound.bundle'
$started = [DateTime]::UtcNow
function Read-FreshReport([string]$path) {
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).LastWriteTimeUtc -lt $started) { throw "Fresh clothing report missing: $path" }
    $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if (!$value.passed) { throw "Clothing report failed: $path" }
    return $value
}
$project = Join-Path $repo 'artifacts/ClothingSoakUnity'
$settings = Join-Path $project 'ProjectSettings/ProjectSettings.asset'
if (Test-Path -LiteralPath $settings) {
    $mode = 0
    [IO.File]::WriteAllText($settings, [regex]::Replace([IO.File]::ReadAllText($settings), 'm_ActiveColorSpace: \d+', "m_ActiveColorSpace: $mode"))
}
foreach ($directory in @('Assets/Editor','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.assetbundle":"1.0.0","com.unity.modules.imageconversion":"1.0.0","com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.physics":"1.0.0"}}'
foreach ($source in @('unity/RuptureWound/Assets/Rupture/BloodStreams/ClothingSoakField.cs','tests/Unity/ClothingSoakTests.cs','tests/Unity/ClothingSoakSparseTests.cs',
    'src/Rupture/Dismemberment/Classes/BloodStreams/ClothingSoakingSystem.cs','tests/Unity/ClothingRuntimeFixture.cs','tests/Unity/MainThreadTracingFixture.cs','tests/Unity/ClothingSoakRuntimeTests.cs','tests/Unity/ClothingSoakAppearanceTests.cs','tests/Unity/ClothingSoakGpuTests.cs','tests/Unity/ClothingSoakFrameBenchmark.cs','unity/RuptureWound/Assets/Rupture/BloodStreams/ClothingSoakGpu.cs')) {
    if ($source -like '*/ClothingSoakingSystem.cs') {
        $runtime = [IO.File]::ReadAllText((Join-Path $repo $source)).Replace('UnityEngine.Object.Destroy(', 'FixtureDestroy.Destroy(')
        [IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/ClothingSoakingSystem.cs'), $runtime)
    } else { Copy-Item -LiteralPath (Join-Path $repo $source) -Destination (Join-Path $project 'Assets/Editor') -Force }
}
Copy-Item -LiteralPath (Join-Path $repo 'unity/RuptureWound/Assets/Rupture/ClothingSoak.compute') -Destination (Join-Path $project 'Assets') -Force
Copy-Item -LiteralPath (Join-Path $repo 'unity/RuptureWound/Assets/Rupture/ClothingSoak.shader') -Destination (Join-Path $project 'Assets') -Force
foreach($shader in @('tests/Unity/BodyDecalProjection.shader','tests/Unity/ClothingSoakCompose.shader')) {
    Copy-Item -LiteralPath (Join-Path $repo $shader) -Destination (Join-Path $project 'Assets') -Force
}
$gpuLog = Join-Path $repo 'artifacts/clothing-soak-gpu.log'
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','ClothingSoakGpuTests.RunBatch','-logFile',('"'+$gpuLog+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "GPU field equivalence failed: $gpuLog" }
$gpuReport = Read-FreshReport (Join-Path $project 'SoakVerification/gpu-field.json')
$log = Join-Path $repo 'artifacts/clothing-soak.log'
$arguments = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','ClothingSoakTests.RunBatch','-logFile',('"'+$log+'"'))
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Clothing soak tests failed; see $log" }
$report = Read-FreshReport (Join-Path $project 'SoakVerification/verification.json')
if (!$report.passed) { throw 'Clothing soak report failed.' }
$report | ConvertTo-Json -Depth 5
$arguments = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','ClothingSoakRuntimeTests.RunBatch','-logFile',('"'+(Join-Path $repo 'artifacts/clothing-soak-runtime.log')+'"'))
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw 'Clothing runtime tests failed; see artifacts/clothing-soak-runtime.log' }
$runtimeReport = Read-FreshReport (Join-Path $project 'SoakVerification/runtime-verification.json')
if (!$runtimeReport.passed -or $runtimeReport.colorSpace -ne 'Gamma') { throw 'Clothing report did not validate the requested color space.' }
$runtimeReport | Add-Member -NotePropertyName gpuField -NotePropertyValue $gpuReport -Force
$runtimeReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $project "SoakVerification/runtime-Gamma.json")
if ($FrameBenchmark) {
    $baselineFile = Join-Path $project 'Assets/Editor/BaselineClothingSoakingSystem.cs'
    if ($BaselineSource) {
        $baseline = Get-Content -LiteralPath $BaselineSource -Raw
        # Freeze the old runtime intact except for its class name and GPU timing markers.
        $baseline = $baseline.Replace('class ClothingSoakingSystem','class BaselineClothingSoakingSystem')
        $baseline = $baseline.Replace('UnityEngine.Object.Destroy(', 'FixtureDestroy.Destroy(')
        if ($baseline.Contains('ClothingSoakGpuField')) {
            $baselineRoot = Split-Path $BaselineSource -Parent
            $baseline = $baseline.Replace('ClothingSoakGpu', 'BaselineClothingSoakGpu').Replace('WoundResources.', 'BaselineWoundResources.').Replace('"RPT.GPU.ClothingSoak"', '"RPT.GPU.ClothingSoak.Baseline"')
            $helper = [IO.File]::ReadAllText((Join-Path $baselineRoot 'ClothingSoakGpu.cs')).Replace('ClothingSoakGpu', 'BaselineClothingSoakGpu')
            [IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/BaselineClothingSoakGpu.cs'), $helper)
            $shader = [IO.File]::ReadAllText((Join-Path $baselineRoot 'ClothingSoak.shader')).Replace('Shader "Rupture/ClothingSoak"', 'Shader "Rupture/ClothingSoakBaseline"')
            [IO.File]::WriteAllText((Join-Path $project 'Assets/BaselineClothingSoak.shader'), $shader)
            Copy-Item -LiteralPath (Join-Path $baselineRoot 'ClothingSoak.compute') -Destination (Join-Path $project 'Assets/BaselineClothingSoak.compute') -Force
            [IO.File]::WriteAllText((Join-Path $project 'Assets/Editor/BaselineWoundResources.cs'), 'using UnityEngine; namespace Rupture.Dismemberment.Classes.BloodStreams { internal static class BaselineWoundResources { internal static bool IsLoading => false; internal static Shader GetShader(string name) => UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/BaselineClothingSoak.shader"); internal static ComputeShader GetComputeShader(string name) => UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/BaselineClothingSoak.compute"); } }')
        } else {
            $baseline = $baseline.Replace('private static CommandBuffer _commands;', 'private static CommandBuffer _commands; private static readonly UnityEngine.Profiling.CustomSampler GpuSample = UnityEngine.Profiling.CustomSampler.Create("RPT.GPU.ClothingSoak.Baseline", true);')
            $baseline = $baseline.Replace('_commands.SetRenderTarget(stamp);', 'if (PendingPaint.Count == 1) _commands.BeginSample(GpuSample); _commands.SetRenderTarget(stamp);')
            $baseline = $baseline.Replace('Graphics.ExecuteCommandBuffer(_commands);', '_commands.EndSample(GpuSample); Graphics.ExecuteCommandBuffer(_commands);')
        }
        [IO.File]::WriteAllText($baselineFile, $baseline)
    } elseif (Test-Path -LiteralPath $baselineFile) { Remove-Item -LiteralPath $baselineFile -Force }
    $benchmarkLog = Join-Path $repo 'artifacts/clothing-frame-benchmark.log'
    $process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-projectPath',('"'+$project+'"'),'-executeMethod','ClothingSoakFrameBenchmark.RunBatch','-logFile',('"'+$benchmarkLog+'"')) -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode) { throw "Clothing frame benchmark failed: $benchmarkLog" }
    $frameReport = Read-FreshReport (Join-Path $project 'SoakVerification/frame-benchmark.json')
    if ($BaselineSource) { $frameReport | Add-Member -NotePropertyName baselineSourceSha256 -NotePropertyValue (Get-FileHash -LiteralPath $BaselineSource -Algorithm SHA256).Hash }
    $runtimeReport | Add-Member -NotePropertyName frameBenchmark -NotePropertyValue $frameReport -Force
}
# The full Gamma suite includes native brightness calibration. In Linear,
# compare the new compositor against the unchanged native path directly;
# the pre-existing projection shader has different brightness calibration.
try {
    [IO.File]::WriteAllText($settings, [regex]::Replace([IO.File]::ReadAllText($settings), 'm_ActiveColorSpace: \d+', 'm_ActiveColorSpace: 1'))
    foreach ($method in @('RunCommandComparisonBatch','RunSharedComparisonBatch')) {
        $comparisonLog = Join-Path $repo "artifacts/clothing-linear-$method.log"
        $arguments = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod',"ClothingSoakRuntimeTests.$method",'-logFile',('"'+$comparisonLog+'"'))
        $process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $process.WaitForExit()
        if ($process.ExitCode) { throw "Linear clothing comparison failed: $comparisonLog" }
    }
    foreach ($kind in @('command','shared')) {
        $comparison = Read-FreshReport (Join-Path $project "SoakVerification/$kind-comparison-Linear.json")
        if (!$comparison.passed -or $comparison.colorSpace -ne 'Linear') { throw "Linear $kind comparison failed." }
        $runtimeReport | Add-Member -NotePropertyName ($kind+'ComparisonLinear') -NotePropertyValue $comparison -Force
    }
}
finally {
    [IO.File]::WriteAllText($settings, [regex]::Replace([IO.File]::ReadAllText($settings), 'm_ActiveColorSpace: \d+', 'm_ActiveColorSpace: 0'))
}
foreach ($source in @(
    @('runtimeSourceSha256','src/Rupture/Dismemberment/Classes/BloodStreams/ClothingSoakingSystem.cs'),
    @('fieldSourceSha256','unity/RuptureWound/Assets/Rupture/BloodStreams/ClothingSoakField.cs'),
    @('gpuSourceSha256','unity/RuptureWound/Assets/Rupture/BloodStreams/ClothingSoakGpu.cs'),
    @('computeSourceSha256','unity/RuptureWound/Assets/Rupture/ClothingSoak.compute'),
    @('shaderSourceSha256','unity/RuptureWound/Assets/Rupture/ClothingSoak.shader'))) {
    $runtimeReport | Add-Member -NotePropertyName $source[0] -NotePropertyValue (Get-FileHash -LiteralPath (Join-Path $repo $source[1]) -Algorithm SHA256).Hash -Force
}
$runtimeReport | Add-Member -NotePropertyName bundleSha256 -NotePropertyValue (Get-FileHash -LiteralPath $env:RPT_SOAK_BUNDLE -Algorithm SHA256).Hash -Force
$runtimeReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $repo 'assets/bundles/rupture-soaking.verification.json')
$runtimeReport | ConvertTo-Json -Depth 6
