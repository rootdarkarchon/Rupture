param(
    [string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$SptInstallPath,
    [switch]$ExtractFixtures,
    [switch]$AllLods,
    [string]$Python = 'py',
    [string]$PythonDependencyPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$ProjectPath) { $ProjectPath = Join-Path $repo 'artifacts\ClothingHarnessUnity' }
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
if (Test-Path (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt')) {
    if ((Get-Content (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt') -Raw) -notmatch '2022\.3\.43f1') {
        throw 'The harness requires Unity 2022.3.43f1.'
    }
} else {
    New-Item (Join-Path $ProjectPath 'ProjectSettings') -ItemType Directory -Force | Out-Null
    New-Item (Join-Path $ProjectPath 'Packages') -ItemType Directory -Force | Out-Null
    Set-Content (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt') 'm_EditorVersion: 2022.3.43f1'
    Set-Content (Join-Path $ProjectPath 'Packages\manifest.json') '{"dependencies":{}}'
}
$packageManifestPath = Join-Path $ProjectPath 'Packages\manifest.json'
$packageManifest = Get-Content $packageManifestPath -Raw | ConvertFrom-Json
foreach ($module in @('com.unity.modules.imgui', 'com.unity.modules.imageconversion')) {
    $packageManifest.dependencies | Add-Member -NotePropertyName $module -NotePropertyValue '1.0.0' -Force
}
[IO.File]::WriteAllText($packageManifestPath, (ConvertTo-Json $packageManifest -Depth 20))
$destination = Join-Path $ProjectPath 'Assets\Editor\RuptureClothing'
New-Item (Join-Path $destination 'Shared') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo 'unity\RuptureWound\Assets\Editor\ClothingHarness\*') $destination -Recurse -Force
Copy-Item (Join-Path $repo 'unity\RuptureWound\Assets\Rupture\LimbSeverSurface.shader') $destination -Force
$views = Join-Path $ProjectPath 'Assets\RuptureClothingViews'
New-Item $views -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo 'unity\RuptureWound\Assets\Rupture\ClothingHarness\*') $views -Force
# Keep the repository templates inactive during the existing wound-asset copy
# workflow. Only the complete installed harness has its assembly gates removed.
foreach ($definition in @((Join-Path $destination 'Rupture.ClothingHarness.Editor.asmdef'), (Join-Path $views 'Rupture.ClothingHarness.Views.asmdef'))) {
    $assembly = Get-Content $definition -Raw | ConvertFrom-Json
    $assembly.defineConstraints = @()
    [IO.File]::WriteAllText($definition, (ConvertTo-Json $assembly -Depth 10))
}
# Migrate only the two files owned by the first harness prototype.
foreach ($obsolete in @('ClothingCaseView.cs', 'ClothingCaseView.cs.meta')) {
    $obsoletePath = Join-Path $destination $obsolete
    if (Test-Path -LiteralPath $obsoletePath) { Remove-Item -LiteralPath $obsoletePath }
}
# Compile the exact production algorithms into an isolated editor assembly.
# Unity's C# 9 compiler needs block namespaces; no algorithm text is changed.
$paths = @(
    'src\Rupture\Dismemberment\Classes\Limbs\ProceduralLimbCutGeometry.cs',
    'src\Rupture\Dismemberment\Classes\Limbs\ProceduralLimbCutBoundaries.cs',
    'src\Rupture\Dismemberment\Classes\Limbs\ProceduralLimbCutBatch.cs',
    'src\Rupture\Dismemberment\Classes\Limbs\LimbCutCaptureSession.cs',
    'src\Rupture\Dismemberment\Classes\Limbs\ProceduralLimbClothIntegration.cs',
    'unity\RuptureWound\Assets\Rupture\Limbs\ProceduralLimbWoundGeometry.cs',
    'unity\RuptureWound\Assets\Rupture\Limbs\ProceduralLimbClothGeometry.cs',
    'unity\RuptureWound\Assets\Rupture\Limbs\LimbClothingProfiles.cs',
    'unity\RuptureWound\Assets\Rupture\Limbs\LimbClothingProfiles.Generated.cs',
    'unity\RuptureWound\Assets\Rupture\Limbs\LimbSeverLevel.cs'
)
$stamp = foreach ($relative in $paths) {
    $source = Join-Path $repo $relative
    $target = Join-Path $destination ('Shared\' + [IO.Path]::GetFileName($source))
    $code = [IO.File]::ReadAllText($source)
    if ($code -match '(?m)^namespace ([\w.]+);\r?$') {
        $code = [regex]::Replace($code, '(?m)^namespace ([\w.]+);\r?$', 'namespace $1 {') + "`n}`n"
    }
    [IO.File]::WriteAllText($target, $code, [Text.UTF8Encoding]::new($false))
    @{ Source=$source; File=[IO.Path]::GetFileName($target); Hash=(Get-FileHash $source -Algorithm SHA256).Hash; CompiledHash=(Get-FileHash $target -Algorithm SHA256).Hash }
}
[IO.File]::WriteAllText((Join-Path $destination 'production-sources.json'), (ConvertTo-Json -InputObject @($stamp) -Depth 5))
Copy-Item (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Newtonsoft.Json.dll') $destination -Force
if ($ExtractFixtures) {
    $oldPythonPath = $env:PYTHONPATH
    try {
        if ($PythonDependencyPath) { $env:PYTHONPATH = $PythonDependencyPath }
        $lodArguments = if ($AllLods) { @('--all-lods') } else { @() }
        & $Python (Join-Path $PSScriptRoot 'Export-ClothingFixtures.py') --game $SptInstallPath --output (Join-Path $ProjectPath 'ClothingFixtures') @lodArguments
        if ($LASTEXITCODE -ne 0) { throw 'Extraction reported failures; inspect ClothingFixtures\catalog.json. Every failed garment remains visible to the harness.' }
    } finally { $env:PYTHONPATH = $oldPythonPath }
}
Write-Output "Harness ready: $ProjectPath"
Write-Output 'Open with Unity 2022.3.43f1, then Tools > Rupture > Clothing Cutter Harness.'
