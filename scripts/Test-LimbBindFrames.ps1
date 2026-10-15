[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe',
    [Parameter(Mandatory=$true)][string]$SptInstallPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/LimbBindFrameUnity'
& (Join-Path $PSScriptRoot 'Setup-ClothingHarness.ps1') -ProjectPath $project -SptInstallPath $SptInstallPath
Copy-Item -LiteralPath (Join-Path $repo 'tests/Unity/LimbBindFrameTests.cs') -Destination (Join-Path $project 'Assets/Editor/RuptureClothing') -Force
$log = Join-Path $repo 'artifacts/limb-bind-frames-unity.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),
    '-executeMethod','LimbBindFrameTests.RunBatch','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Limb bind-frame regressions failed; see $log" }
$reportPath = Join-Path $project 'bind-frame-verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'No current bind-frame report.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Limb bind-frame report failed.' }
$report | ConvertTo-Json
