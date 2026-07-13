<#
.SYNOPSIS
    Installs WSL Container Desktop from the release assets.

.DESCRIPTION
    Download all three release assets (the .msix, the .cer, and this Install.ps1) into the
    same folder, then right-click this script and "Run with PowerShell" (or run it from a
    terminal). The script:
      1. Trusts the app's signing certificate (LocalMachine\TrustedPeople) so Windows will
         accept the sideloaded package. This requires administrator rights, so the script
         self-elevates.
      2. Installs (or updates) the MSIX package.

    The certificate is a self-signed publisher certificate for "CN=Michael Hacker". Review it
    before trusting if you wish (right-click the .cer > Open).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Self-elevate: trusting a certificate in the machine store requires administrator rights.
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Requesting administrator rights...'
    Start-Process -FilePath 'powershell.exe' -Verb RunAs `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    return
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cer = Get-ChildItem -LiteralPath $dir -Filter *.cer | Select-Object -First 1
$msix = Get-ChildItem -LiteralPath $dir -Filter *.msix | Select-Object -First 1

if (-not $cer) { throw "No .cer file found next to this script ($dir)." }
if (-not $msix) { throw "No .msix file found next to this script ($dir)." }

Write-Host "Trusting signing certificate: $($cer.Name)"
Import-Certificate -FilePath $cer.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null

Write-Host "Installing package: $($msix.Name)"
Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion

Write-Host ''
Write-Host 'Done. Launch "WSL Container Desktop" from the Start menu.' -ForegroundColor Green
Write-Host 'Press any key to close...'
[void][System.Console]::ReadKey($true)
