<#
.SYNOPSIS
  Populates bridge/u300-bridge/libs and native from your own copy of U300.rar.

.DESCRIPTION
  The Chainway SDK is not in source control. The U300 manual grants a
  non-transferable, non-exclusive licence and states that no right to copy the
  licensed program is granted, so redistributing those jars would be
  sublicensing them. Each developer extracts them from the archive Chainway
  supplied instead.

  Needs one of: WinRAR (UnRAR.exe), 7-Zip, or the `unrar` command on PATH.

.PARAMETER Archive
  Path to U300.rar. Defaults to the repository root.

.EXAMPLE
  .\scripts\fetch-vendor-libs.ps1
  .\scripts\fetch-vendor-libs.ps1 -Archive D:\downloads\U300.rar
#>
[CmdletBinding()]
param(
    [string]$Archive
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Archive) { $Archive = Join-Path $root 'U300.rar' }

if (-not (Test-Path $Archive)) {
    throw "U300.rar not found at '$Archive'. Pass -Archive with the path to the archive Chainway supplied."
}

# Locate an extractor.
$extractors = @(
    @{ Path = 'C:\Program Files\WinRAR\UnRAR.exe';   Args = { param($a, $d) @('x', '-o+', '-y', $a, "$d\") } },
    @{ Path = 'C:\Program Files\7-Zip\7z.exe';       Args = { param($a, $d) @('x', '-y', "-o$d", $a) } },
    @{ Path = 'C:\Program Files (x86)\7-Zip\7z.exe'; Args = { param($a, $d) @('x', '-y', "-o$d", $a) } }
)

$tool = $extractors | Where-Object { Test-Path $_.Path } | Select-Object -First 1

if (-not $tool) {
    $unrar = Get-Command unrar -ErrorAction SilentlyContinue
    if ($unrar) { $tool = @{ Path = $unrar.Source; Args = { param($a, $d) @('x', '-o+', '-y', $a, "$d\") } } }
}

if (-not $tool) {
    throw 'No extractor found. Install WinRAR or 7-Zip, or put `unrar` on PATH.'
}

Write-Host "Using $($tool.Path)"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("u300-vendor-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    # U300.rar -> Demo-URA4-JAVA_EN.rar -> URA4Demo/libs/*.jar
    Write-Host 'Extracting U300.rar...'
    & $tool.Path @(& $tool.Args $Archive $work) | Out-Null

    $demo = Get-ChildItem $work -Recurse -Filter 'Demo-URA4-JAVA_EN.rar' | Select-Object -First 1
    if (-not $demo) { throw 'Demo-URA4-JAVA_EN.rar was not found inside the archive.' }

    $inner = Join-Path $work 'demo'
    New-Item -ItemType Directory -Force -Path $inner | Out-Null

    Write-Host 'Extracting the host SDK demo...'
    & $tool.Path @(& $tool.Args $demo.FullName $inner) | Out-Null

    $libSource = Get-ChildItem $inner -Recurse -Directory -Filter 'libs' |
        Where-Object { Get-ChildItem $_.FullName -Filter 'UHFAPI*.jar' -ErrorAction SilentlyContinue } |
        Select-Object -First 1

    if (-not $libSource) { throw 'Could not find the SDK libs folder (expected UHFAPI*.jar).' }

    $libTarget    = Join-Path $root 'bridge\u300-bridge\libs'
    $nativeTarget = Join-Path $root 'bridge\u300-bridge\native'
    New-Item -ItemType Directory -Force -Path $libTarget, $nativeTarget | Out-Null

    Copy-Item (Join-Path $libSource.FullName '*.jar') $libTarget -Force
    $jars = (Get-ChildItem $libTarget -Filter *.jar).Count
    Write-Host "Copied $jars jar(s) to bridge/u300-bridge/libs"

    # RXTX natives, needed only for the RS-232 transport.
    Get-ChildItem $inner -Recurse -Include 'rxtx*.dll', 'librxtx*.so' -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName $nativeTarget -Force }

    $natives = (Get-ChildItem $nativeTarget -File | Where-Object { $_.Name -ne '.gitkeep' }).Count
    Write-Host "Copied $natives native file(s) to bridge/u300-bridge/native"

    Write-Host ''
    Write-Host 'Done. Build the bridge with: cd bridge\u300-bridge; .\build.ps1'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
