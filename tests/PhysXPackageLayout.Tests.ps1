[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $PackageRoot)
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($PackageRoot)
$expected = @(
    "BepInEx/plugins/rupture/Rupture.Fika.dll",
    "BepInEx/plugins/rupture/Rupture.dll",
    "BepInEx/plugins/rupture/Rupture.PhysXBackend.dll",
    "BepInEx/plugins/rupture/rpt_physx_bridge.dll",
    "BepInEx/plugins/rupture/physx-backend.json",
    "BepInEx/plugins/rupture/rupture-wound.bundle",
    "BepInEx/plugins/rupture/AssetsTools.NET.dll",
    "BepInEx/plugins/rupture/AssetsTools.NET-LICENSE.txt",
    "BepInEx/plugins/rupture/PhysX-LICENSE.txt"
)
$actual = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/') })
$delta = @(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object) -CaseSensitive)
if ($delta.Count) { throw "Release payload differs from the exact allowlist: $($delta | Out-String)" }
$plugin = Join-Path $root "BepInEx/plugins/rupture"
$manifest = Get-Content -LiteralPath (Join-Path $plugin "physx-backend.json") -Raw | ConvertFrom-Json
if ($manifest.abiVersion -ne 10 -or $manifest.nativeBridge -cne "rpt_physx_bridge.dll" -or $manifest.managedAssembly -cne "Rupture.PhysXBackend.dll") { throw "Backend identity/ABI mismatch." }
$identity = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $plugin "Rupture.dll"))
if ($identity.Name -cne "Rupture" -or $identity.Version.ToString() -ne "1.2.1.0") { throw "Main assembly identity mismatch." }
$fikaIdentity = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $plugin "Rupture.Fika.dll"))
if ($fikaIdentity.Name -cne "Rupture.Fika" -or $fikaIdentity.Version -ne $identity.Version) { throw "Optional Fika bridge identity/version mismatch." }
$backendIdentity = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $plugin "Rupture.PhysXBackend.dll"))
if ($backendIdentity.Name -cne "Rupture.PhysXBackend" -or $backendIdentity.Version -ne $identity.Version) { throw "Physics backend identity/version mismatch." }
$assetsIdentity = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $plugin "AssetsTools.NET.dll"))
if ($assetsIdentity.Name -cne "AssetsTools.NET" -or $assetsIdentity.Version.ToString() -ne "3.0.0.0") { throw "Clothing bundle parser identity/version mismatch." }
Write-Output "PASS: exact nine-file Rupture runtime payload including required licenses, clothing bundle parser, assembly identity, and native ABI 10."
