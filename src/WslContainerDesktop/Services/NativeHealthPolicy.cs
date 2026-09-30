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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Decision returned when mapping a Compose health check to native <c>wslc</c> flags or app-owned probing.
/// </summary>
public sealed record NativeHealthSelection(bool Native, IReadOnlyList<string> Arguments, string? Diagnostic = null);

/// <summary>
/// Chooses how health checks are enforced. The app uses native <c>wslc</c> support when it can,
/// otherwise it falls back to the in-process health watchdog with a clear diagnostic.
/// </summary>
public static class NativeHealthPolicy
{
    /// <summary>Builds health-related run/create arguments or selects app-owned probing when required.</summary>
    public static NativeHealthSelection Select(NativeHealthOptions? options, WslcCapabilities capabilities, bool forCreate = false)
    {
        if (options is null)
            return new(true, []);

        Validate(options);
        if (options.IsDisabled)
            return new(true, ["--no-healthcheck"]);

        var arguments = new List<string>();
        void Add(string flag, string? value)
        {
            arguments.Add(flag);
            if (value is not null)
                arguments.Add(value);
        }

        if (options.HasCommand)
            Add("--health-cmd", options.Test[1]);
        if (options.Interval is not null)
            Add("--health-interval", options.Interval);
        if (options.Timeout is not null)
            Add("--health-timeout", options.Timeout);
        if (options.StartPeriod is not null)
            Add("--health-start-period", options.StartPeriod);
        if (options.Retries is not null)
            Add("--health-retries", options.Retries.Value.ToString(CultureInfo.InvariantCulture));

        var startIntervalCapability = forCreate
            ? capabilities[WslcFeature.CreateHealthStartInterval]
            : capabilities[WslcFeature.HealthStartInterval];
        var startIntervalNative = options.StartInterval is null ||
            startIntervalCapability.Support == WslcCapabilitySupport.Supported;
        if (options.StartInterval is not null && startIntervalNative)
            Add("--health-start-interval", options.StartInterval);

        // WSLC constructs CMD-SHELL from --health-cmd.
        // Exec-form argv must not be reinterpreted as shell source.
        var execForm = options.Test.FirstOrDefault() == "CMD";
        if (!execForm && startIntervalNative)
            return new(true, arguments);

        if (!options.HasCommand)
            throw new InvalidOperationException("This engine cannot apply inherited health timing overrides. Supply an explicit health test or use an engine supporting the requested flags.");
        var timingDiagnostic = new[] { options.Interval, options.StartInterval }.Any(value =>
            value is not null && Duration(value) < TimeSpan.FromSeconds(1))
            ? " App scheduling has 1s resolution; sub-second probe intervals cannot be honored." : string.Empty;
        var reason = execForm ? "CMD argv is not a native shell check" :
            startIntervalCapability.Support == WslcCapabilitySupport.Unknown
                ? $"--health-start-interval support is unknown: {startIntervalCapability.Diagnostic}"
                : "--health-start-interval is not advertised";
        return new(false, ["--no-healthcheck"],
            $"Using app-owned health probes ({reason}); the app must remain open.{timingDiagnostic}");
    }

    /// <summary>Validates a normalized Compose health-check definition before it reaches the engine.</summary>
    public static void Validate(NativeHealthOptions options)
    {
        if (options.IsDisabled)
            return;
        if (options.Test.Count > 0 && (!options.HasCommand ||
            options.Test[0] == "CMD-SHELL" && options.Test.Count != 2))
            throw new InvalidOperationException("Health test must be CMD with argv, CMD-SHELL with one script, or NONE.");
        if (options.Retries is <= 0)
            throw new InvalidOperationException("Health retries must be positive.");
        if (options.Interval is not null) Duration(options.Interval);
        if (options.Timeout is not null) Duration(options.Timeout);
        if (options.StartPeriod is not null) Duration(options.StartPeriod, allowZero: true);
        if (options.StartInterval is not null) Duration(options.StartInterval);
    }

    /// <summary>Parses Compose-style duration text such as <c>500ms</c> or <c>1m30s</c>.</summary>
    public static TimeSpan Duration(string text, bool allowZero = false)
    {
        var matches = Regex.Matches(text, @"(\d+(?:\.\d+)?)(ns|us|ms|s|m|h)", RegexOptions.CultureInvariant);
        if (matches.Count == 0 || string.Concat(matches.Select(m => m.Value)) != text)
            throw new InvalidOperationException($"Unsupported health duration '{text}'. Use units such as 500ms, 30s, or 1m30s.");
        double seconds = 0;
        foreach (Match match in matches)
            seconds += double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (match.Groups[2].Value switch
            {
                "ns" => 1e-9, "us" => 1e-6, "ms" => 1e-3, "m" => 60, "h" => 3600, _ => 1,
            });
        if (!double.IsFinite(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds ||
            seconds < 0 || seconds == 0 && !allowZero)
            throw new InvalidOperationException($"Health duration '{text}' is out of range.");
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>True when a container state and health state satisfy <c>service_healthy</c>.</summary>
    public static bool IsDependencyReady(ContainerState state, ContainerHealthState health) =>
        state == ContainerState.Running && health == ContainerHealthState.Healthy;

    /// <summary>True when observed health belongs to the expected container generation and is still fresh.</summary>
    public static bool IsDependencyReady(ContainerInfo container, ContainerHealthSnapshot health,
        string expectedId, DateTimeOffset notBefore) =>
        !string.IsNullOrWhiteSpace(container.Id) &&
        (container.Id == expectedId || container.Id.Length >= 12 && expectedId.StartsWith(container.Id, StringComparison.Ordinal)) &&
        health.ContainerId == container.Id && health.ContainerGeneration == container.StateChangedAt &&
        health.ObservedAt >= notBefore && DateTimeOffset.UtcNow - health.ObservedAt <= health.ObservationMaxAge &&
        IsDependencyReady(container.State, health.State);

    /// <summary>Converts an app-owned health check into <c>wslc exec</c> arguments.</summary>
    public static IReadOnlyList<string> ExecArguments(string id, NativeHealthOptions health)
    {
        Validate(health);
        if (!health.HasCommand || health.IsDisabled)
            throw new InvalidOperationException("No executable health test is configured.");
        return health.Test[0] == "CMD"
            ? new[] { "exec", id }.Concat(health.Test.Skip(1)).ToArray()
            : ["exec", id, "sh", "-c", health.Test[1]];
    }

    /// <summary>Compares a saved app policy with the engine's observed native health-check configuration.</summary>
    public static bool MatchesDesiredCheck(HealthCheckConfig config, NativeHealthOptions? actual)
    {
        var desired = config.DesiredHealth;
        if (desired is null)
            return config.Command == "engine-owned" || actual?.Test.SequenceEqual(new[] { "CMD-SHELL", config.Command }) == true;
        if (actual is null)
            return !desired.HasCommand;
        if (desired.HasCommand && !desired.Test.SequenceEqual(actual.Test))
            return false;
        bool Same(string? requested, string? observed, string defaultValue) => requested is null ||
            Duration(requested, allowZero: true) == Duration(observed ?? defaultValue, allowZero: true);
        return Same(desired.Interval, actual.Interval, "30s") &&
            Same(desired.Timeout, actual.Timeout, "30s") &&
            Same(desired.StartPeriod, actual.StartPeriod, "0s") &&
            Same(desired.StartInterval, actual.StartInterval, "5s") &&
            (desired.Retries is null || desired.Retries == (actual.Retries ?? 3));
    }
}
