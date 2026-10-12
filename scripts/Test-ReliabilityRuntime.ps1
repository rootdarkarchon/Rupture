[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/ReliabilityRuntimeUnity'
foreach ($directory in @('Assets/Reliability','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0"}}'
foreach ($source in @('src/Rupture/CommitTransaction.cs','src/Rupture/ObjectLifetime.cs',
    'src/Rupture/Dismemberment/Classes/MeshFrameTransform.cs','src/Rupture/Ragdolls/Classes/ColliderContactState.cs',
    'src/Rupture/Ragdolls/Classes/WeaponColliderGeometry.cs','tests/Unity/WeaponColliderGeometryTests.cs',
    'src/Rupture/Dismemberment/Classes/RendererSource.cs','src/Rupture/Ragdolls/Classes/BodyHierarchyPath.cs','tests/Unity/RendererSourceTests.cs',
    'tests/Unity/ReliabilityRuntimeTests.cs','tests/Unity/LimbRetentionRuntimeTests.cs')) {
    $oldEditorCopy = Join-Path $project ('Assets/Editor/' + [IO.Path]::GetFileName($source))
    if (Test-Path -LiteralPath $oldEditorCopy) { Remove-Item -LiteralPath $oldEditorCopy }
    Copy-Item -LiteralPath (Join-Path $repo $source) -Destination (Join-Path $project 'Assets/Reliability') -Force
}
foreach ($context in @('CorpseLimbContext', 'CorpseHeadContext')) {
$contextSource = Get-Content -LiteralPath (Join-Path $repo "src/Rupture/Ragdolls/Classes/$context.cs") -Raw
$contextSource = [regex]::Replace($contextSource, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
[IO.File]::WriteAllText((Join-Path $project "Assets/Reliability/$context.cs"), $contextSource)
}
$log = Join-Path $repo 'artifacts/reliability-runtime-unity.log'
$arguments = @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-executeMethod','ReliabilityRuntimeTests.RunBatch','-logFile',('"'+$log+'"'))
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
# Wait for the editor itself; its shared licensing helper may outlive the run.
$process.WaitForExit()
if ($process.ExitCode) { throw "Unity reliability regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Unity reliability report was not produced by this run.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Unity reliability report failed.' }
$report | ConvertTo-Json
