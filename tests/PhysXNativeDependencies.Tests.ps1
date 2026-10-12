[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BridgePath
)

$ErrorActionPreference = "Stop"
$bridgePath = [IO.Path]::GetFullPath($BridgePath)
if (-not (Test-Path -LiteralPath $bridgePath -PathType Leaf)) {
    throw "Native bridge not found: $bridgePath"
}

$dumpbin = $null
if ($env:VCToolsInstallDir) {
    $candidate = Join-Path $env:VCToolsInstallDir "bin\Hostx64\x64\dumpbin.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $dumpbin = $candidate }
}
if (-not $dumpbin) {
    $toolRoots = Get-ChildItem -LiteralPath "C:\Program Files (x86)\Microsoft Visual Studio" -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue } |
        ForEach-Object { Join-Path $_.FullName "VC\Tools\MSVC" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    $dumpbin = $toolRoots |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter dumpbin.exe -Recurse -File -ErrorAction SilentlyContinue } |
        Where-Object { $_.FullName -match 'Hostx64\\x64\\dumpbin\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -ExpandProperty FullName -First 1
}
if (-not $dumpbin) { throw "dumpbin.exe was not found; native dependency isolation cannot be verified." }

$output = & $dumpbin /dependents $bridgePath
if ($LASTEXITCODE -ne 0) { throw "dumpbin failed with exit code $LASTEXITCODE." }
$dependencies = @($output | ForEach-Object {
    if ($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') { $matches[1].ToUpperInvariant() }
} | Sort-Object -Unique)
$forbidden = @($dependencies | Where-Object {
    $_ -match '^PHYSX' -or $_ -match '^VCRUNTIME' -or $_ -match '^MSVCP' -or $_ -match '^UCRTBASE'
})
if ($forbidden.Count -ne 0) {
    throw "The bridge has forbidden non-system runtime dependencies: $($forbidden -join ', ')"
}
if ($dependencies.Count -ne 1 -or $dependencies[0] -ne "KERNEL32.DLL") {
    throw "Unexpected bridge dependencies (expected only KERNEL32.DLL): $($dependencies -join ', ')"
}

$repo = Split-Path $PSScriptRoot -Parent
$exports = & $dumpbin /exports $bridgePath
if ($LASTEXITCODE) { throw 'Native export inspection failed.' }
$actualExports = @($exports | ForEach-Object { if ($_ -match '^\s+\d+\s+[A-F0-9]+\s+[A-F0-9]+\s+(\w+)(?:\s+=\s+.+)?\s*$') { $matches[1] } } | Sort-Object -Unique)
$header = Get-Content -LiteralPath (Join-Path $repo 'src/Rupture.PhysX.Native/include/rpt_physx_bridge.h') -Raw
$expectedExports = @([regex]::Matches($header, 'RPTPX_CALL\s+(rptpx_\w+)\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if (!$actualExports.Count -or @(Compare-Object $expectedExports $actualExports -CaseSensitive).Count) { throw 'Native exports differ from the exact bridge API.' }
Write-Output "PASS: all $($actualExports.Count) native exports match the RPTPX header."
Write-Output "PASS: rpt_physx_bridge.dll statically contains PhysX and the CRT; its only import dependency is KERNEL32.dll."
