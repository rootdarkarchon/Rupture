[CmdletBinding()]
param([string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'artifacts/WeaponColliderDebugUnity'
foreach ($directory in @('Assets/DebugView','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.imgui":"1.0.0","com.unity.modules.imageconversion":"1.0.0"}}'
foreach ($source in @('src/Rupture/Ragdolls/Classes/WeaponColliderDebugView.cs','tests/Unity/WeaponColliderDebugRuntimeTests.cs')) {
    $content = Get-Content -LiteralPath (Join-Path $repo $source) -Raw
    $content = [regex]::Replace($content, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {')
    if ($source.EndsWith('WeaponColliderDebugView.cs')) { $content += "`n}`n" }
    [IO.File]::WriteAllText((Join-Path $project ('Assets/DebugView/' + [IO.Path]::GetFileName($source))), $content)
}
$log = Join-Path $repo 'artifacts/weapon-collider-debug-unity.log'
$arguments = @('-batchmode','-projectPath',('"'+$project+'"'),'-executeMethod','WeaponColliderDebugRuntimeTests.RunBatch','-logFile',('"'+$log+'"'))
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode) { throw "Weapon wireframe rendering test failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw 'Fresh wireframe report was not produced.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Weapon wireframe rendering test failed.' }
$report | ConvertTo-Json
