[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $SptInstallPath,
    [string] $FikaPluginPath,
    [string] $PhysXRepositoryPath,
    [ValidateSet("Debug","Release")][string] $Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = Join-Path $repoRoot "artifacts"
$project = [xml](Get-Content -LiteralPath (Join-Path $repoRoot "src/Rupture/Rupture.csproj") -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw "Invalid package version: $version" }
# Hash the actual files being built, including untracked files but excluding ignored outputs.
# NUL delimiters keep paths unambiguous; ordinal sorting makes the result deterministic.
$fileList = (& git -C $repoRoot ls-files -z --cached --others --exclude-standard) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Cannot enumerate package working tree." }
$paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $fileList.Split([char]0)) {
    if ($path.Length -gt 0) { [void]$paths.Add($path) }
}
$manifest = [Text.StringBuilder]::new()
foreach ($path in $paths) {
    $fullPath = Join-Path $repoRoot $path
    # Deleted tracked files are absent from the working tree.
    if (!(Test-Path -LiteralPath $fullPath)) { continue }
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Cannot hash working-tree entry: $path" }
    $fileHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    [void]$manifest.Append($path).Append([char]0).Append($fileHash).Append([char]0)
}
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $digest = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest.ToString()))
    $workingTreeHash = ([BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant().Substring(0, 12)
} finally {
    $sha256.Dispose()
}
$OutputFileName = "Rupture-$version-$workingTreeHash.zip"
$referenceArgs = @("-p:SptInstallPath=$SptInstallPath")
if ($FikaPluginPath) { $referenceArgs += "-p:FikaPluginPath=$FikaPluginPath" }
& dotnet build (Join-Path $repoRoot "Rupture.slnx") -c $Configuration @referenceArgs --disable-build-servers -m:1 -v minimal
if ($LASTEXITCODE -ne 0) { throw "Managed build failed." }
$nativeBridge = & (Join-Path $PSScriptRoot "Build-PhysXBackend.ps1") -Configuration $Configuration -PhysXRepositoryPath $PhysXRepositoryPath | Select-Object -Last 1
if (!(Test-Path -LiteralPath $nativeBridge -PathType Leaf)) { throw "Native bridge missing." }
& (Join-Path $repoRoot "tests/WoundBundle.Tests.ps1")
if (!$?) { throw "Wound bundle validation failed." }
$packageRoot = Join-Path $artifactRoot ("rupture-package-" + [Guid]::NewGuid().ToString("N"))
$pluginRoot = Join-Path $packageRoot "BepInEx/plugins/rupture"
New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
$payload = @(
    @("src/Rupture.Fika/bin/$Configuration/netstandard2.1/Rupture.Fika.dll", "Rupture.Fika.dll"),
    @("src/Rupture/bin/$Configuration/netstandard2.1/Rupture.dll", "Rupture.dll"),
    @("src/Rupture.PhysXBackend/bin/$Configuration/netstandard2.1/Rupture.PhysXBackend.dll", "Rupture.PhysXBackend.dll"),
    @("assets/bundles/rupture-wound.bundle", "rupture-wound.bundle"),
    @("src/Rupture/bin/$Configuration/netstandard2.1/AssetsTools.NET.dll", "AssetsTools.NET.dll"),
    @("src/Rupture/Clothing/AssetsTools.NET-LICENSE.txt", "AssetsTools.NET-LICENSE.txt"),
    @("src/Rupture.PhysXBackend/physx-backend.json", "physx-backend.json"),
    @("src/Rupture.PhysX.Native/PhysX-LICENSE.txt", "PhysX-LICENSE.txt")
)
foreach ($entry in $payload) { Copy-Item -LiteralPath (Join-Path $repoRoot $entry[0]) -Destination (Join-Path $pluginRoot $entry[1]) }
Copy-Item -LiteralPath $nativeBridge -Destination (Join-Path $pluginRoot "rpt_physx_bridge.dll")
& (Join-Path $repoRoot "tests/PhysXPackageLayout.Tests.ps1") -PackageRoot $packageRoot
if (!$?) { throw "Release payload validation failed." }
$outputArchive = Join-Path $artifactRoot $OutputFileName
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $outputArchive -CompressionLevel Optimal -Force
$resolvedStage = [IO.Path]::GetFullPath($packageRoot)
if (!$resolvedStage.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($resolvedStage) -notlike "rupture-package-*") { throw "Unsafe staging cleanup target." }
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
Write-Output $outputArchive
