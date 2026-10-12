[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$loader = Get-Content -LiteralPath (Join-Path $repo "src/Rupture/WoundResources.cs") -Raw
$plugin = Get-Content -LiteralPath (Join-Path $repo "src/Rupture/RupturePlugin.cs") -Raw
$package = Get-Content -LiteralPath (Join-Path $repo "scripts/Build-Package.ps1") -Raw
foreach ($needle in @('BepInEx.Paths.PluginPath', '"rupture"', '"rupture-wound.bundle"', 'LoadFromFileAsync', 'GetShader')) {
    if (!$loader.Contains($needle)) { throw "Main-plugin resource contract missing: $needle" }
}
if (!$plugin.Contains('StartCoroutine(WoundResources.Load())') -or $plugin.Contains('com.pandahhcorp.bundleloader')) { throw "Separate bundle-loader dependency remains." }
if ($package -match 'SourceArchive|original|bloodfx|bloodsfx|bundleloader') { throw "Package depends on original payload." }
$maintained = @(Get-ChildItem (Join-Path $repo "assets") -Recurse -File)
if (@($maintained | Where-Object { $_.Name -match 'bloodfx|bloodsfx|gorecaps|active_ragdoll_base|death_animations' }).Count) { throw "Unused original bundles remain in maintained assets." }
if (Test-Path (Join-Path $repo "config/VD_Calibers.json")) { throw "Obsolete caliber file remains." }
if (Test-Path (Join-Path $repo "src/BundleLoader/bundleloader.csproj")) { throw "Standalone loader project remains." }
Write-Output "PASS: plugin-owned explicit bundle loading and original-payload independence."
