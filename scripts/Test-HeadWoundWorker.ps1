[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/HeadWoundWorkerUnity'
foreach ($directory in @('Assets/HeadWounds','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.imageconversion":"1.0.0"}}'
$sources = @('src/Rupture/Dismemberment/Classes/ManagedGeometryWorker.cs',
    'src/Rupture/Dismemberment/Classes/RendererSource.cs',
    'src/Rupture/Dismemberment/Classes/MeshFrameTransform.cs',
    'src/Rupture/Dismemberment/Classes/Packets/HeadWoundPacket.cs',
    'tests/Unity/HeadWoundWorkerTests.cs','tests/Unity/HeadWoundWorkerFixture.cs')
$sources += Get-ChildItem (Join-Path $repo 'src/Rupture/Dismemberment/Classes/HeadWounds') -Filter '*.cs' |
    Where-Object Name -ne 'HeadWoundMaterialProvider.cs' | ForEach-Object FullName
$sources += Get-ChildItem (Join-Path $repo 'unity/RuptureWound/Assets/Rupture/HeadWounds') -Filter '*.cs' | ForEach-Object FullName
foreach ($source in $sources) {
    $path = if ([IO.Path]::IsPathRooted($source)) { $source } else { Join-Path $repo $source }
    $content = Get-Content -LiteralPath $path -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') {
        $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
    }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/HeadWounds/' + [IO.Path]::GetFileName($source))), $content)
}
$log = Join-Path $repo 'artifacts/head-wound-worker.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-nographics',
    '-projectPath',('"'+$project+'"'),'-executeMethod','Rupture.Dismemberment.Classes.HeadWounds.BenchmarkRuptureHeadWound.RunThreadedBatch',
    '-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Head-wound worker tests failed; see $log" }
$reportPath = Join-Path $project 'worker-verification.json'
if (!(Test-Path $reportPath) -or (Get-Item $reportPath).LastWriteTimeUtc -lt $started) { throw 'No fresh worker report.' }
$report = Get-Content $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Head-wound worker report failed.' }
$report | ConvertTo-Json -Depth 5
