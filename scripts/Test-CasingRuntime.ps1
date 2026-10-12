[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/CasingRuntimeUnity'
foreach ($directory in @('Assets/Casings','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.physics":"1.0.0"}}'
foreach ($source in @('src/Rupture/Casings/CasingWorld.cs','src/Rupture/Casings/CasingGeometry.cs','src/Rupture/Casings/CasingImpactQuery.cs',
    'src/Rupture/Ragdolls/External/ExternalCasingContracts.cs','src/Rupture/ObjectLifetime.cs','tests/Unity/MainThreadTracingFixture.cs','tests/Unity/CasingRuntimeTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    if ($content -match '(?m)^namespace [\w.]+;\r?$') { $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n" }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/Casings/' + [IO.Path]::GetFileName($source))), $content)
}
$log = Join-Path $repo 'artifacts/casing-runtime-unity.log'
$arguments = @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-executeMethod','CasingRuntimeTests.RunBatch','-logFile',('"'+$log+'"'))
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Unity casing regression failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh casing report was not produced.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Casing runtime regression failed.' }
$report | ConvertTo-Json
