<#
    Build-and-run launcher for WSL Container Desktop.

    Rebuilds the project (x64 Debug), registers the freshly built packaged
    output, then launches it by its app identity (AUMID) so the running app is
    detached from this console and survives after the window closes.

    Used by the "WSL Container Desktop (Dev)" desktop shortcut.
#>

$ErrorActionPreference = 'Stop'

$projectDir  = Join-Path $PSScriptRoot '..\..\src\WslContainerDesktop'
$project     = Join-Path $projectDir 'WslContainerDesktop.csproj'
$packageName = '393193CD-4A5B-4502-BC94-7C6AF142CD28'
# Register from the OutDir *root* manifest, which `dotnet build` regenerates on
# every build. The `...\win-x64\AppX\` subfolder is a stale VS deploy artifact
# that a command-line build never refreshes, so registering it launches old bits.
$manifest    = Join-Path $projectDir 'bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppxManifest.xml'

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
# Re-register from this exact location so the AUMID resolves to the fresh build
# even if a previous registration pointed somewhere else.
if (Test-Path $manifest) {
    try {
        Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion -ForceApplicationShutdown -ErrorAction Stop
    }
    catch {
        Write-Host "  Warning: registration reported: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '  Launching...' -ForegroundColor Green

# Resolve the app's AUMID from the registered package rather than hard-coding
# the publisher hash (which changes if the manifest Publisher ever changes).
$pkg = Get-AppxPackage -Name $packageName | Select-Object -First 1
if (-not $pkg) {
    Write-Host '  Could not find the registered package to launch.' -ForegroundColor Red
    Read-Host '  Press Enter to close'
    exit 1
}
$appId = (Get-AppxPackageManifest $pkg).Package.Applications.Application.Id
$aumid = "$($pkg.PackageFamilyName)!$appId"

# Launch by identity via the shell so the app is detached from this console.
Start-Process "shell:AppsFolder\$aumid"

Start-Sleep -Seconds 2
