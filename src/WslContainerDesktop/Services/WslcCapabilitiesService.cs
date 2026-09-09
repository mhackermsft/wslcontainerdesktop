// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class WslcCapabilitiesService : IWslcCapabilitiesService, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string> _getPath;
    private readonly Func<string, WslcExecutableIdentity> _readIdentity;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<CommandResult>> _run;
    private readonly Action<string> _logWarning;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _probeTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ISettingsService? _settings;
    private CacheEntry? _entry;
    private bool _disposed;

    public WslcCapabilitiesService(ISettingsService settings, ILogger<WslcCapabilitiesService> logger)
        : this(() => settings.WslcPath, WslcExecutableIdentity.Read, ProcessRunner.RunAtPathAsync,
            message => logger.LogWarning("{CapabilityDiagnostic}", message))
    {
        _settings = settings;
        settings.Changed += OnSettingsChanged;
    }

    internal WslcCapabilitiesService(
        Func<string> getPath,
        Func<string, WslcExecutableIdentity> readIdentity,
        Func<string, IEnumerable<string>, CancellationToken, Task<CommandResult>> run,
        Action<string> logWarning,
        TimeProvider? clock = null,
        TimeSpan? probeTimeout = null)
    {
        _getPath = getPath;
        _readIdentity = readIdentity;
        _run = run;
        _logWarning = logWarning;
        _clock = clock ?? TimeProvider.System;
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<WslcCapabilities> GetAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = _readIdentity(_getPath());
            CacheEntry entry;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entry is null || _entry.Identity != identity || IsExpired(_entry))
                {
                    var lifetimeToken = _lifetime.Token;
                    _entry = new(identity, Task.Run(async () =>
                    {
                        var capabilities = await ProbeAsync(identity, lifetimeToken).ConfigureAwait(false);
                        return new ProbeResult(capabilities, _clock.GetUtcNow());
                    }));
                }

                entry = _entry;
            }

            var result = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var currentIdentity = _readIdentity(_getPath());
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (ReferenceEquals(entry, _entry) && currentIdentity == entry.Identity)
                {
                    return result.Capabilities;
                }
            }
            // Settings/binary changed while probing. Never publish evidence from the previous engine.
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _entry = null;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_entry is not null && _entry.Identity.ConfiguredPath != _getPath())
            {
                _entry = null;
            }
        }
    }

    private bool IsExpired(CacheEntry entry)
    {
        if (!entry.Task.IsCompleted)
        {
            return false;
        }

        return !entry.Task.IsCompletedSuccessfully ||
            _clock.GetUtcNow() - entry.Task.Result.CompletedAt >=
            (entry.Task.Result.Capabilities.HasProbeFailures ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(5));
    }

    private async Task<WslcCapabilities> ProbeAsync(WslcExecutableIdentity identity, CancellationToken lifetimeToken)
    {
        var features = new Dictionary<WslcFeature, WslcCapability>();
        if (identity.Diagnostic is { } identityError)
        {
            _logWarning(identityError);
            foreach (var feature in Enum.GetValues<WslcFeature>())
            {
                features[feature] = new(WslcCapabilitySupport.Unknown, identityError);
            }

            return new(identity.ExecutablePath, null, features, identityError);
        }

        var versionResult = await RunProbeAsync(identity.ExecutablePath, ["--version"], lifetimeToken).ConfigureAwait(false);
        var versionText = versionResult.StandardOutput + "\n" + versionResult.StandardError;
        var versionMatch = Regex.Match(versionText, @"(?m)^wslc\s+(\d+(?:\.\d+){2,3})\s*$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var version = versionResult.Success && versionMatch.Success ? versionMatch.Groups[1].Value : null;
        var versionDiagnostic = version is null
            ? Failure(identity.ExecutablePath, "--version", versionResult, "Unrecognized version output.")
            : null;

        await ProbeHelpAsync("network", false,
            [(WslcFeature.NetworkConnect, "connect"), (WslcFeature.NetworkDisconnect, "disconnect")]).ConfigureAwait(false);
        await ProbeHelpAsync("container", false, [(WslcFeature.ContainerCp, "cp")]).ConfigureAwait(false);
        await ProbeHelpAsync("run", true,
        [
            (WslcFeature.HealthCmd, "--health-cmd"),
            (WslcFeature.HealthInterval, "--health-interval"),
            (WslcFeature.HealthRetries, "--health-retries"),
            (WslcFeature.HealthStartPeriod, "--health-start-period"),
            (WslcFeature.HealthTimeout, "--health-timeout"),
            (WslcFeature.HealthStartInterval, "--health-start-interval"),
            (WslcFeature.NoHealthcheck, "--no-healthcheck"),
        ]).ConfigureAwait(false);
        await ProbeHelpAsync("create", true,
        [
            (WslcFeature.CreateHealthCmd, "--health-cmd"),
            (WslcFeature.CreateHealthInterval, "--health-interval"),
            (WslcFeature.CreateHealthRetries, "--health-retries"),
            (WslcFeature.CreateHealthStartPeriod, "--health-start-period"),
            (WslcFeature.CreateHealthTimeout, "--health-timeout"),
            (WslcFeature.CreateHealthStartInterval, "--health-start-interval"),
            (WslcFeature.CreateNoHealthcheck, "--no-healthcheck"),
        ]).ConfigureAwait(false);
        return new(identity.ExecutablePath, version, features, versionDiagnostic);

        async Task ProbeHelpAsync(string command, bool options, (WslcFeature Feature, string Token)[] expected)
        {
            var result = await RunProbeAsync(identity.ExecutablePath, [command, "--help"], lifetimeToken).ConfigureAwait(false);
            var entries = result.Success
                ? WslcCapabilityHelpParser.ReadEntries(result.StandardOutput + "\n" + result.StandardError, command, options)
                : null;
            var diagnostic = entries is null
                ? Failure(identity.ExecutablePath, $"{command} --help", result, "Unrecognized or incomplete command help.")
                : null;
            foreach (var (feature, token) in expected)
            {
                features[feature] = entries is null
                    ? new(WslcCapabilitySupport.Unknown, diagnostic)
                    : entries.Contains(token)
                        ? new(WslcCapabilitySupport.Supported)
                        : new(WslcCapabilitySupport.Unsupported, $"'{identity.ExecutablePath}' does not advertise {token} in {command} --help.");
            }
        }
    }

    private async Task<CommandResult> RunProbeAsync(string path, string[] arguments, CancellationToken lifetimeToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        timeout.CancelAfter(_probeTimeout);
        try
        {
            return await _run(path, arguments, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!lifetimeToken.IsCancellationRequested)
        {
            return new()
            {
                ExitCode = -1,
                StandardError = timeout.IsCancellationRequested
                    ? $"Capability probe timed out after {_probeTimeout.TotalSeconds:g} seconds."
                    : "Capability probe was cancelled before completing.",
            };
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            return new() { ExitCode = -1, StandardError = $"Could not complete capability probe: {ex.Message}" };
        }
    }

    private string Failure(string path, string arguments, CommandResult result, string invalidOutputMessage)
    {
        var detail = result.Success ? invalidOutputMessage : result.ErrorText;
        if (detail.Length > 1024)
        {
            detail = detail[..1024];
        }

        var diagnostic = $"WSLC capability probe '{path}' {arguments} failed: {detail} Check the executable path and retry.";
        _logWarning(diagnostic);
        return diagnostic;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed record ProbeResult(WslcCapabilities Capabilities, DateTimeOffset CompletedAt);

    private sealed record CacheEntry(WslcExecutableIdentity Identity, Task<ProbeResult> Task);
}
