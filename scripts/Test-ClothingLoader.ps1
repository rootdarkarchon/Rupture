[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SptInstallPath, [switch]$SkipData,
      [string]$UnityEditorPath = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$data = Join-Path $repo 'artifacts/ClothingLoadValidation'
if (!$SkipData) {
    & dotnet run --project (Join-Path $repo 'tests/Rupture.ClothingTests/Rupture.ClothingTests.csproj') -c Release "-p:SptInstallPath=$SptInstallPath" -- $SptInstallPath $data
    if ($LASTEXITCODE) { throw 'Clothing loader data validation failed.' }
}
$project = Join-Path $repo 'artifacts/ClothingLoadUnity'
foreach ($directory in @('Assets/Tests','Packages','ProjectSettings')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null
}
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') '{"dependencies":{"com.unity.modules.assetbundle":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
Copy-Item -LiteralPath (Join-Path $repo 'tests/Unity/ClothingBundleLoadTests.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
Copy-Item -LiteralPath (Join-Path $repo 'src/Rupture/Clothing/ClothingBundleHooks.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
Copy-Item -LiteralPath (Join-Path $repo 'src/Rupture/Clothing/ClothingBundleData.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
Copy-Item -LiteralPath (Join-Path $repo 'src/Rupture/Clothing/ClothingBundleDecompression.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
Copy-Item -LiteralPath (Join-Path $repo 'src/Rupture/Clothing/ClothingBundleWork.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
Copy-Item -LiteralPath (Join-Path $repo 'src/Rupture/Clothing/ClothingBundleRequests.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
Copy-Item -LiteralPath (Join-Path $repo 'src/Rupture/Clothing/ClothingBundleLoader.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
Copy-Item -LiteralPath (Join-Path $repo 'tests/Unity/ClothingLoaderIntegration.cs') -Destination (Join-Path $project 'Assets/Tests') -Force
$plugins = Join-Path $project 'Assets/Plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
foreach ($name in @('0Harmony.dll','Mono.Cecil.dll','Mono.Cecil.Mdb.dll','Mono.Cecil.Pdb.dll','Mono.Cecil.Rocks.dll','MonoMod.RuntimeDetour.dll','MonoMod.Utils.dll')) {
    Copy-Item -LiteralPath (Join-Path $SptInstallPath "BepInEx/core/$name") -Destination $plugins -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'src/Rupture/bin/Release/netstandard2.1/AssetsTools.NET.dll') -Destination $plugins -Force
Copy-Item -LiteralPath (Join-Path $repo 'artifacts/rupture-physx-native-build/release/rpt_physx_bridge.dll') -Destination $plugins -Force
Copy-Item -LiteralPath (Join-Path $data 'discovery.json') -Destination $project -Force
Copy-Item -LiteralPath (Join-Path $data 'bundles.json') -Destination $project -Force
$log = Join-Path $repo 'artifacts/clothing-loader-unity.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityEditorPath -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-executeMethod','ClothingBundleLoadTests.RunBatch','-logFile',('"'+$log+'"'))
$process.WaitForExit()
if ($process.ExitCode) { throw "Unity clothing loader test failed; see $log" }
$reportPath = Join-Path $project 'verification.json'
if (!(Test-Path $reportPath) -or (Get-Item $reportPath).LastWriteTimeUtc -lt $started) { throw 'No fresh clothing-loader Unity verification.' }
$report = Get-Content $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw 'Clothing loader Unity verification failed.' }
$report | ConvertTo-Json
