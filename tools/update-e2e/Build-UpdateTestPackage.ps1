<#
.SYNOPSIS
    Builds a signed, side-by-side "update test" MSIX of WSL Container Desktop for exercising the
    in-app updater end to end without touching the real installation.

.DESCRIPTION
    Copies the working tree to -WorkDir and patches only that copy so it installs next to the real
    app and checks a scratch GitHub repository for releases:

      * package identity Name / display name, toast COM activator CLSID and single-instance key
        (so the two apps never collide);
      * AppConstants.UpdateRepository -> -Repository;
      * Identity Version -> -Version.

    It then builds exactly as .github/workflows/release.yml does (Release, x64, self-contained,
    unsigned), signs the package with the certificate -CertThumbprint from Cert:\CurrentUser\My,
    and writes it to -OutDir using the asset name the updater requires
    (WSLContainerDesktop_<X.Y.Z>_x64.msix). The source tree itself is never modified.

    Everything else - download, verification, forced shutdown, install and relaunch - is the
    shipping code path. See tools/update-e2e/README.md for the full procedure.

.EXAMPLE
    ./tools/update-e2e/Build-UpdateTestPackage.ps1 -Version 1.9.1 -CertThumbprint $thumb `
        -Repository mhackermsft/wslcd-update-test -OutDir $env:TEMP\wslcd-update-e2e\out
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertThumbprint,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$')]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [string]$OutDir,

    [string]$WorkDir = (Join-Path $env:TEMP 'wslcd-update-e2e\src')
)

$ErrorActionPreference = 'Stop'

# Fixed test identity, distinct from the shipping package in every place Windows keys on.
$TestName = 'WslContainerDesktop.UpdateTest'
$TestDisplayName = 'WSL Container Desktop (update test)'
$ShippingClsid = '0A09D2E0-BDE4-4315-988E-3E808AC12994'
$TestClsid = '7E3A5C1B-2D4F-4A6B-9C8D-0E1F2A3B4C5D'
$TestPhoneProductId = '7E3A5C1B-2D4F-4A6B-9C8D-0E1F2A3B4C5E'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Set-FileText([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Replace-Exact([string]$Content, [string]$Old, [string]$New, [string]$What) {
    if (-not $Content.Contains($Old)) { throw "Could not find $What to patch." }
    return $Content.Replace($Old, $New)
}

# 1. Fresh copy of the tree (build outputs, git metadata and tests are not needed).
Write-Host "Copying working tree to $WorkDir"
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
robocopy $repoRoot $WorkDir /MIR /NFL /NDL /NJH /NJS /NP /XD .git bin obj AppPackages tests .vs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

$app = Join-Path $WorkDir 'src\WslContainerDesktop'

# 2. Patch the copy's identity so it installs side by side with the real app.
$manifestPath = Join-Path $app 'Package.appxmanifest'
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$shippingName = [regex]::Match($manifest, '<Identity\b[^>]*\bName="([^"]+)"').Groups[1].Value
if (-not $shippingName) { throw 'Could not read the shipping package Name.' }
$manifest = [regex]::Replace($manifest, '(<Identity\b[^>]*\bName=")[^"]*(")', "`${1}$TestName`$2")
$manifest = [regex]::Replace($manifest, '(<Identity\b[^>]*\bVersion=")[^"]*(")', "`${1}$Version.0`$2")
$manifest = Replace-Exact $manifest "PhoneProductId=""$shippingName""" "PhoneProductId=""$TestPhoneProductId""" 'PhoneProductId'
$manifest = Replace-Exact $manifest $ShippingClsid $TestClsid 'the toast activator CLSID'
$manifest = $manifest.Replace('<DisplayName>WSL Container Desktop</DisplayName>', "<DisplayName>$TestDisplayName</DisplayName>")
$manifest = $manifest.Replace('DisplayName="WSL Container Desktop"', "DisplayName=""$TestDisplayName""")
Set-FileText $manifestPath $manifest

$programPath = Join-Path $app 'Program.cs'
Set-FileText $programPath (Replace-Exact (Get-Content -LiteralPath $programPath -Raw) `
    '"WslContainerDesktop.SingleInstance"' '"WslContainerDesktop.UpdateTest.SingleInstance"' 'the single-instance key')

$constantsPath = Join-Path $app 'Services\AppConstants.cs'
$constants = Get-Content -LiteralPath $constantsPath -Raw
if (-not [regex]::IsMatch($constants, 'UpdateRepository = "[^"]+"')) { throw 'Could not find UpdateRepository to patch.' }
Set-FileText $constantsPath ([regex]::Replace($constants, 'UpdateRepository = "[^"]+"', "UpdateRepository = ""$Repository"""))

Write-Host "Patched copy: Name=$TestName Version=$Version.0 UpdateRepository=$Repository"

# 3. Build exactly as the release workflow does (unsigned, self-contained).
$pkgDir = Join-Path $WorkDir 'AppPackages'
Remove-Item -Recurse -Force $pkgDir -ErrorAction SilentlyContinue
dotnet build (Join-Path $app 'WslContainerDesktop.csproj') `
    -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
    "-p:Version=$Version" "-p:FileVersion=$Version.0" `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=false `
    "-p:AppxPackageDir=$pkgDir\" `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxBundle=Never `
    -p:WindowsAppSDKSelfContained=true `
    -p:SelfContained=true `
    -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$msix = Get-ChildItem $pkgDir -Recurse -Filter *.msix |
    Where-Object { $_.FullName -notmatch '\\Dependencies\\' } | Select-Object -First 1
if (-not $msix) { throw "No .msix produced under $pkgDir" }

# 4. Sign with the test certificate and publish under the updater's asset-name contract.
$signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found (restore the app once to populate the NuGet cache).' }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$target = Join-Path $OutDir "WSLContainerDesktop_${Version}_x64.msix"
Copy-Item $msix.FullName $target -Force
& $signtool.FullName sign /fd SHA256 /sha1 $CertThumbprint /s My $target | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }

Write-Host "Built $target"
