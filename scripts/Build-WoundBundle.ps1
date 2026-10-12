[CmdletBinding()]
param(
    [string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$project = Join-Path $repo 'artifacts\RuptureBundle'
$templates = Join-Path $repo 'unity\RuptureWound\Assets'
foreach ($directory in @('Assets\Editor','Assets\Rupture','ProjectSettings','Packages')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
# AssetBundle must be enabled even though its managed assembly is available to
# Editor scripts without the module. Otherwise LoadFromFile rejects the result.
Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') '{"dependencies":{"com.unity.modules.assetbundle":"1.0.0"}}'
Copy-Item -LiteralPath (Join-Path $templates 'Editor\BuildRuptureWoundBundle.cs') -Destination (Join-Path $project 'Assets\Editor') -Force
foreach ($shader in @('WoundInterior.shader','BloodStream.shader','LimbSeverSurface.shader','ClothingSoak.shader','ClothingSoak.compute')) {
    Copy-Item -LiteralPath (Join-Path $templates "Rupture\$shader") -Destination (Join-Path $project 'Assets\Rupture') -Force
}
$log = Join-Path $repo 'artifacts\cloth-bundle-build.log'
$arguments = @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','BuildRuptureWoundBundle.Build','-logFile',('"'+$log+'"'))
$process = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -PassThru -WindowStyle Hidden
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity bundle build/verification failed ($($process.ExitCode)); see $log" }
$bundle = Join-Path $project 'AssetBundles\rupture-wound.bundle'
if (!(Test-Path -LiteralPath $bundle)) { throw 'Unity did not produce the wound bundle.' }
Copy-Item -LiteralPath $bundle -Destination (Join-Path $repo 'assets\bundles\rupture-wound.bundle') -Force
Copy-Item -LiteralPath ($bundle + '.verification.json') -Destination (Join-Path $repo 'assets\bundles\rupture-wound.bundle.verification.json') -Force
Get-FileHash -LiteralPath $bundle -Algorithm SHA256
