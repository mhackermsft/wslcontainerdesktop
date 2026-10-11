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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Covers WSL requirement evaluation so policy, executable path, version parsing, settings changes, and retry cadence produce clear status.</summary>
public sealed class WslRequirementServiceTests
{
    [Theory]
    [InlineData("3.0.0.9", WslRequirementState.TooOld)]
    [InlineData("3.0.1", WslRequirementState.Ok)]
    [InlineData("3.0.1.0", WslRequirementState.Ok)]
    [InlineData("3.1", WslRequirementState.Ok)]
    [InlineData("2.9.13", WslRequirementState.TooOld)]
    public async Task VersionGate_ComparesAgainstMinimum(string version, WslRequirementState expected)
    {
        using var h = new Harness();
        h.Result = new() { ExitCode = 0, StandardOutput = $"wslc {version}" };

        var status = await h.Service.RecheckAsync();

        Assert.Equal(expected, status.State);
        Assert.Equal(version, status.FoundVersion);
    }

    [Fact]
    public async Task VersionGate_AcceptsContainerExeAliasOutput()
    {
        using var h = new Harness();
        h.Result = new() { ExitCode = 0, StandardOutput = "container 3.0.1.0" };

        var status = await h.Service.RecheckAsync();

        Assert.Equal(WslRequirementState.Ok, status.State);
        Assert.Equal("3.0.1.0", status.FoundVersion);
    }

    [Fact]
    public async Task Evaluation_Order_ChecksPolicyBeforePathOrVersion()
    {
        using var h = new Harness { AllowWsl = 0, PathExists = false };

        var status = await h.Service.RecheckAsync();

        Assert.Equal(WslRequirementState.DisabledByPolicy, status.State);
        Assert.Equal(0, h.RunCount);
    }

    [Fact]
    public async Task MissingConfiguredPath_IsNotInstalled()
    {
        using var h = new Harness { PathExists = false };

        var status = await h.Service.RecheckAsync();

        Assert.Equal(WslRequirementState.NotInstalled, status.State);
        Assert.Equal(0, h.RunCount);
    }

    [Fact]
    public async Task EnginePolicyError_MapsToDisabledByPolicy()
    {
        using var h = new Harness
        {
            Result = new()
            {
                ExitCode = 1,
                StandardError = "WSL container is disabled by the computer policy.",
            },
        };

        var status = await h.Service.RecheckAsync();

        Assert.Equal(WslRequirementState.DisabledByPolicy, status.State);
    }

    [Theory]
    [InlineData(1, "wslc nope")]
    [InlineData(0, "hello")]
    public async Task FailedOrUnparseableVersion_IsUnknown(int exitCode, string output)
    {
        using var h = new Harness
        {
            Result = new() { ExitCode = exitCode, StandardOutput = output, StandardError = "bad" },
        };

        var status = await h.Service.RecheckAsync();

        Assert.Equal(WslRequirementState.Unknown, status.State);
        Assert.NotNull(status.Diagnostic);
    }

    [Fact]
    public async Task SettingsChange_ReevaluatesRequirement()
    {
        using var h = new Harness();
        var states = new List<WslRequirementState>();
        h.Service.Changed += (_, status) => states.Add(status.State);

        h.Result = new() { ExitCode = 0, StandardOutput = "wslc 3.0.0.9" };
        await h.Service.RecheckAsync();
        h.Result = new() { ExitCode = 0, StandardOutput = "wslc 3.0.1.0" };

        h.RaiseSettingsChanged();
        await SpinUntilAsync(() => states.Contains(WslRequirementState.Ok));

        Assert.Contains(WslRequirementState.TooOld, states);
        Assert.Contains(WslRequirementState.Ok, states);
    }

    [Fact]
    public async Task InitialCheckFlag_IsFalseUntilFirstEvaluationPublishes()
    {
        using var h = new Harness();

        Assert.False(h.Service.HasCompletedInitialCheck);

        await h.Service.RecheckAsync();

        Assert.True(h.Service.HasCompletedInitialCheck);
    }

    [Fact]
    public async Task TransientFailure_AutomaticallyRechecksWithCappedBackoff()
    {
        using var h = new Harness(useManualDelay: true);
        var states = new List<WslRequirementState>();
        h.Service.Changed += (_, status) => states.Add(status.State);
        h.Result = new() { ExitCode = 1, StandardError = "i/o timeout" };

        await h.Service.RecheckAsync();
        await SpinUntilAsync(() => h.PendingDelay is not null);

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(h.Delays));

        h.Result = new() { ExitCode = 0, StandardOutput = "wslc 3.0.1.0" };
        h.CompleteDelay();
        await SpinUntilAsync(() => states.Contains(WslRequirementState.Ok));

        Assert.Equal([WslRequirementState.Unknown, WslRequirementState.Ok], states);
        Assert.Equal(2, h.RunCount);
    }

    [Fact]
    public async Task TooOld_AutomaticallyRechecksAtSlowCadence()
    {
        using var h = new Harness(useManualDelay: true);
        h.Result = new() { ExitCode = 0, StandardOutput = "wslc 3.0.0.9" };

        await h.Service.RecheckAsync();
        await SpinUntilAsync(() => h.PendingDelay is not null);

        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(h.Delays));
    }

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Creates fake settings and policy services for requirement-state tests.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wcd-req-" + Guid.NewGuid().ToString("N"));
        private readonly Settings _settings;

        public Harness(bool useManualDelay = false)
        {
            UseManualDelay = useManualDelay;
            Directory.CreateDirectory(_directory);
            _settings = new(Path.Combine(_directory, "wslc.exe"));
            File.WriteAllText(_settings.WslcPath, "");
            Service = new WslRequirementService(
                _settings,
                new Policy(this),
                null,
                NullLogger<WslRequirementService>.Instance,
                RunAsync,
                DelayAsync);
        }

        public WslRequirementService Service { get; }
        public CommandResult Result { get; set; } = new() { ExitCode = 0, StandardOutput = "wslc 3.0.1.0" };
        public int? AllowWsl { get; set; }
        public int? AllowContainer { get; set; }
        public int RunCount { get; private set; }
        public bool UseManualDelay { get; }
        public List<TimeSpan> Delays { get; } = [];
        public TaskCompletionSource? PendingDelay { get; private set; }

        public bool PathExists
        {
            set
            {
                if (value)
                {
                    File.WriteAllText(_settings.WslcPath, "");
                }
                else if (File.Exists(_settings.WslcPath))
                {
                    File.Delete(_settings.WslcPath);
                }
            }
        }

        public void RaiseSettingsChanged() => _settings.RaiseChanged();

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        private Task<CommandResult> RunAsync(string path, IEnumerable<string> args, CancellationToken ct)
        {
            _ = path;
            _ = args;
            _ = ct;
            RunCount++;
            return Task.FromResult(Result);
        }

        public void CompleteDelay()
        {
            PendingDelay?.TrySetResult();
            PendingDelay = null;
        }

        private Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            if (!UseManualDelay)
            {
                return Task.Delay(delay, ct);
            }

            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingDelay = pending;
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        }

        /// <summary>Lets tests control whether the app is disabled by enterprise WSL policy.</summary>
        private sealed class Policy(Harness owner) : IWslPolicyService
        {
            public WslPolicySnapshot GetPolicy() => new(
                owner.AllowWsl is not 0,
                owner.AllowContainer is not 0,
                new WslRegistryAllowlist(WslRegistryAllowlistState.Unrestricted, []));
        }

        /// <summary>Provides a minimal settings source that can raise changes during requirement evaluation tests.</summary>
        private sealed class Settings(string path) : ISettingsService
        {
            public string WslcPath { get; set; } = path;
            public int RefreshIntervalSeconds { get; set; }
            public bool CloseToTray { get; set; }
            public bool PageAnimations { get; set; } = true;
            public bool StartMinimized { get; set; }
            public bool RestartRunningContainersOnLaunch { get; set; }
            public string Theme { get; set; } = "Default";
            public string Language { get; set; } = "";
            public bool NotificationsEnabled { get; set; }
            public bool NotifyImageEvents { get; set; }
            public bool NotifyContainerEvents { get; set; }
            public bool NotifyEngineEvents { get; set; }
            public bool CheckForUpdatesOnLaunch { get; set; }
            public bool AiFeaturesEnabled { get; set; }
            public AiProviderKind AiProvider { get; set; }
            public string AiOllamaEndpoint { get; set; } = "";
            public string AiOllamaModel { get; set; } = "";
            public string AiAzureOpenAiEndpoint { get; set; } = "";
            public string AiAzureOpenAiDeployment { get; set; } = "";
            public string AiOpenAiEndpoint { get; set; } = "";
            public string AiOpenAiModel { get; set; } = "";
            public string AiFoundryLocalEndpoint { get; set; } = "";
            public string AiFoundryLocalModel { get; set; } = "";
            public string AiGitHubCopilotModel { get; set; } = "";
            public bool AiAssistantAutoCreateRun { get; set; }
            public bool AiAssistantAutoLifecycle { get; set; }
            public bool AiAssistantAutoComposeTemplate { get; set; }
            public bool AiAssistantAutoKubernetes { get; set; }
            public IReadOnlyCollection<string> AiAssistantAutoApprovedTools => [];
            public bool AiAssistantApproveEverything { get; set; }
            public bool AiAssistantAllowDestructive { get; set; }
            public string? WslDistro { get; set; }
            public bool KubernetesStoppedByUser { get; set; }
            public bool WslUpdatePreRelease { get; set; }
            public string? DevContainerNpmRegistry { get; set; }
            public string? K3sInstallerSha256 { get; set; }
            public List<RegistryEntry> Registries { get; set; } = [];
            public List<HealthCheckConfig> HealthChecks { get; set; } = [];
            public List<RestartPolicyConfig> RestartPolicies { get; set; } = [];
            public event EventHandler? Changed;
            public void Load() { }
            public void Save() { }
            public bool IsAssistantToolAutoApproved(string toolName) => false;
            public void SetAssistantToolAutoApproved(string toolName, bool autoApprove) { }
            public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
