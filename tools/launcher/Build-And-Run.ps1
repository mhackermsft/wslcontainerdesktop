<#
    Build-and-run launcher for WSL Container Desktop.

    Rebuilds the project (x64 Debug), registers the freshly built packaged
    output, then launches it by its app identity (AUMID) so the running app is
    detached from this console and survives after the window closes.

    Used by the "WSL Container Desktop (Dev)" desktop shortcut.
#>

$ErrorActionPreference = 'Stop'

$projectDir = Join-Path $PSScriptRoot '..\..\src\WslContainerDesktop'
$project    = Join-Path $projectDir 'WslContainerDesktop.csproj'
$aumid      = '393193CD-4A5B-4502-BC94-7C6AF142CD28_1z32rh13vfry6!App'
$manifest   = Join-Path $projectDir 'bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppX\AppxManifest.xml'

# Locate dotnet.
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source } else { $dotnet = 'dotnet' }
}

Write-Host ''
Write-Host '  Building WSL Container Desktop...' -ForegroundColor Cyan
Write-Host ''

& $dotnet build $project -c Debug -p:Platform=x64 -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  Build FAILED. See errors above.' -ForegroundColor Red
    Read-Host '  Press Enter to close'
    exit 1
}

# Register the freshly built loose files so the new version is what launches.
if (Test-Path $manifest) {
    try {
        Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion -ErrorAction Stop
    }
    catch {
        Write-Host "  Warning: registration reported: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '  Launching...' -ForegroundColor Green

# Launch by identity via the shell so the app is detached from this console.
Start-Process "shell:AppsFolder\$aumid"

Start-Sleep -Seconds 2
