<#
.SYNOPSIS
    Stamps a 4-part version (X.Y.Z.B) into the app package manifest.

.DESCRIPTION
    Used by the release workflow (and runnable locally) to set the MSIX identity version
    before packaging. Only the <Identity Version="..."> attribute is changed.

.EXAMPLE
    ./build/Set-Version.ps1 -Version 1.2.0.0
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$manifest = Join-Path $PSScriptRoot '..\src\WslContainerDesktop\Package.appxmanifest'
$manifest = (Resolve-Path -LiteralPath $manifest).Path

$content = Get-Content -LiteralPath $manifest -Raw
$pattern = '(<Identity\b[^>]*\bVersion=")[^"]*(")'

if (-not [regex]::IsMatch($content, $pattern)) {
    throw "Could not find an <Identity ... Version=`"...`"> attribute to stamp in $manifest"
}

$updated = [regex]::Replace($content, $pattern, "`${1}$Version`$2")

# Write back without a BOM (MSBuild reads either; keep it clean).
[System.IO.File]::WriteAllText($manifest, $updated, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Stamped Package.appxmanifest Identity Version = $Version"
