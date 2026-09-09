# WSL Container Desktop - a WinUI 3 manager for WSL containers.
# Copyright (C) 2026 Michael Hacker
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program. If not, see <https://www.gnu.org/licenses/>.

#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ComposeExecutable,
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ApprovedSha256,
    [switch]$Regenerate
)

$ErrorActionPreference = 'Stop'
if (!$Regenerate) { throw 'Reference writes require explicit -Regenerate. No commands were run.' }
$executable = (Resolve-Path -LiteralPath $ComposeExecutable).Path
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $ApprovedSha256) {
    throw 'Compose executable does not match the operator-approved release checksum.'
}
$corpus = (Resolve-Path (Join-Path $PSScriptRoot '..\..\tests\WslContainerDesktop.Tests\Fixtures\Compose\v1')).Path
$provenance = Get-Content (Join-Path $corpus 'reference-provenance.json') -Raw | ConvertFrom-Json
if ([DateTimeOffset]::Parse($provenance.composeReleasePublishedAt) -gt [DateTimeOffset]::UtcNow.AddDays(-7)) {
    throw 'Pinned Compose release is less than seven days old.'
}

function Invoke-Reference([string[]]$Arguments, [string]$Directory, $FixtureEnvironment) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $Directory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    if ($env:SystemRoot) { $start.Environment['SystemRoot'] = $env:SystemRoot }
    foreach ($key in @('TEMP', 'TMP', 'HOME', 'USERPROFILE', 'DOCKER_CONFIG')) {
        $start.Environment[$key] = $Directory
    }
    # Configuration rendering must never use the user's daemon, context, or registry credentials.
    $start.Environment['DOCKER_HOST'] = 'tcp://127.0.0.1:1'
    foreach ($key in $FixtureEnvironment.Keys) {
        if (!$key.StartsWith('WCD_', [StringComparison]::Ordinal) -and $key -ne 'COMPOSE_PROFILES') {
            throw 'Fixture environment keys must be synthetic WCD_* variables or COMPOSE_PROFILES.'
        }
        $start.Environment[$key] = $FixtureEnvironment[$key]
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Compose configuration rendering timed out.'
        }
        return @{
            exitCode = $process.ExitCode
            stdout = $stdout.GetAwaiter().GetResult()
            stderr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("wslcd-compose-reference-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $version = Invoke-Reference @('version', '--short') $temporary @{}
    if ($version.exitCode -ne 0 -or $version.stdout.Trim().TrimStart('v') -ne $provenance.composeCli) {
        throw "Expected standalone Compose $($provenance.composeCli); version probe failed or returned a different version."
    }
    foreach ($case in Get-ChildItem -LiteralPath $corpus -Directory | Sort-Object Name) {
        $expectationPath = Join-Path $case.FullName 'expectations.json'
        if (!(Test-Path -LiteralPath $expectationPath)) { continue }
        $expected = Get-Content -LiteralPath $expectationPath -Raw | ConvertFrom-Json -AsHashtable
        $directory = Join-Path $temporary $case.Name
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $hashes = [ordered]@{}
        foreach ($file in Get-ChildItem -LiteralPath $case.FullName -File -Recurse -Force | Sort-Object FullName) {
            if ($file.Name -eq 'reference.json') { continue }
            $relative = [IO.Path]::GetRelativePath($case.FullName, $file.FullName)
            $destination = Join-Path $directory $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            $hashes[$relative.Replace('\', '/')] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $arguments = @('--project-directory', $directory, '-p', 'wcd-conformance', '--profile', '*', '-f', 'compose.yaml')
        if (Test-Path -LiteralPath (Join-Path $directory 'compose.override.yaml')) {
            $arguments += @('-f', 'compose.override.yaml')
        }
        $arguments += @('config', '--format', 'json')
        $capture = Invoke-Reference $arguments $directory $expected.environment
        if ($expected.ContainsKey('referenceError')) {
            if ($capture.exitCode -eq 0 -or !$capture.stderr.Contains($expected.referenceError)) {
                throw "$($case.Name): expected configuration rejection containing '$($expected.referenceError)'."
            }
        }
        elseif ($capture.exitCode -ne 0) {
            throw "$($case.Name): config failed: $($capture.stderr)"
        }
        # Normalize only the uniquely-owned fixture root, including JSON-escaped Windows paths.
        $stdout = $capture.stdout.Replace($directory.Replace('\', '\\'), '$FIXTURE').Replace($directory.Replace('\', '/'), '$FIXTURE')
        $stderr = $capture.stderr.Replace($directory, '$FIXTURE').Replace($directory.Replace('\', '/'), '$FIXTURE')
        $record = [ordered]@{
            cliVersion = $provenance.composeCli
            executableSha256 = $ApprovedSha256.ToLowerInvariant()
            capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            origin = 'standalone-compose-config'
            inputHashes = $hashes
            arguments = @($arguments | ForEach-Object { $_.Replace($directory, '$FIXTURE') })
            exitCode = $capture.exitCode
            stderr = $stderr
            config = $(if ($capture.exitCode -eq 0) { $stdout | ConvertFrom-Json -AsHashtable } else { $null })
        }
        $record | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $case.FullName 'reference.json') -Encoding utf8
        Write-Output "Captured $($case.Name); review reference.json before committing."
    }
}
finally {
    # This exact path was freshly created by this invocation; never prune engine resources.
    [IO.Directory]::Delete($temporary, $true)
}
