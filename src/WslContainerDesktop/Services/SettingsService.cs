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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class SettingsService(ILogger<SettingsService> logger) : ISettingsService
{
    private const string DefaultWslcPath = @"C:\Program Files\WSL\wslc.exe";

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");

    public string WslcPath { get; set; } = ResolveDefaultWslcPath();
    public int RefreshIntervalSeconds { get; set; } = 5;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public string Theme { get; set; } = "Default";
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotifyImageEvents { get; set; } = true;
    public bool NotifyContainerEvents { get; set; } = true;
    public bool NotifyEngineEvents { get; set; } = true;
    public bool AiFeaturesEnabled { get; set; }
    public AiProviderKind AiProvider { get; set; }
    public string AiOllamaEndpoint { get; set; } = "http://localhost:11434";
    public string AiOllamaModel { get; set; } = "llama3.1";
    public string AiAzureOpenAiEndpoint { get; set; } = string.Empty;
    public string AiAzureOpenAiDeployment { get; set; } = string.Empty;
    public string AiOpenAiEndpoint { get; set; } = "https://api.openai.com/v1";
    public string AiOpenAiModel { get; set; } = "gpt-4o-mini";
    public string AiGitHubCopilotModel { get; set; } = "auto";
    public bool AiAssistantAutoCreateRun { get; set; }
    public bool AiAssistantAutoLifecycle { get; set; }
    public bool AiAssistantAutoComposeTemplate { get; set; }
    public bool AiAssistantAutoKubernetes { get; set; }
    public string? WslDistro { get; set; }
    public bool WslUpdatePreRelease { get; set; }

    private HashSet<string> _autoApprovedTools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> AiAssistantAutoApprovedTools => _autoApprovedTools;

    public bool IsAssistantToolAutoApproved(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _autoApprovedTools.Contains(toolName);

    public void SetAssistantToolAutoApproved(string toolName, bool autoApprove)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        var changed = autoApprove ? _autoApprovedTools.Add(toolName) : _autoApprovedTools.Remove(toolName);
        if (changed)
        {
            Save();
        }
    }

    public event EventHandler? Changed;

    public string? K3sInstallerSha256 { get; set; }
    public List<RegistryEntry> Registries { get; set; } = new() { RegistryEntry.DockerHub() };
    public List<HealthCheckConfig> HealthChecks { get; set; } = new();
    public List<RestartPolicyConfig> RestartPolicies { get; set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return;
            }

            var json = File.ReadAllText(SettingsFile);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json);
            if (dto is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(dto.WslcPath))
            {
                WslcPath = dto.WslcPath;
            }

            RefreshIntervalSeconds = Math.Clamp(dto.RefreshIntervalSeconds, AppConstants.RefreshIntervalMinSeconds, AppConstants.RefreshIntervalMaxSeconds);
            CloseToTray = dto.CloseToTray;
            StartMinimized = dto.StartMinimized;
            Theme = string.IsNullOrWhiteSpace(dto.Theme) ? "Default" : dto.Theme;
            NotificationsEnabled = dto.NotificationsEnabled;
            NotifyImageEvents = dto.NotifyImageEvents;
            NotifyContainerEvents = dto.NotifyContainerEvents;
            NotifyEngineEvents = dto.NotifyEngineEvents;
            AiFeaturesEnabled = dto.AiFeaturesEnabled;
            AiProvider = Enum.IsDefined(typeof(AiProviderKind), dto.AiProvider)
                ? (AiProviderKind)dto.AiProvider
                : AiProviderKind.None;
            AiOllamaEndpoint = string.IsNullOrWhiteSpace(dto.AiOllamaEndpoint) ? "http://localhost:11434" : dto.AiOllamaEndpoint;
            AiOllamaModel = string.IsNullOrWhiteSpace(dto.AiOllamaModel) ? "llama3.1" : dto.AiOllamaModel;
            AiAzureOpenAiEndpoint = dto.AiAzureOpenAiEndpoint ?? string.Empty;
            AiAzureOpenAiDeployment = dto.AiAzureOpenAiDeployment ?? string.Empty;
            AiOpenAiEndpoint = string.IsNullOrWhiteSpace(dto.AiOpenAiEndpoint) ? "https://api.openai.com/v1" : dto.AiOpenAiEndpoint;
            AiOpenAiModel = string.IsNullOrWhiteSpace(dto.AiOpenAiModel) ? "gpt-4o-mini" : dto.AiOpenAiModel;
            AiGitHubCopilotModel = string.IsNullOrWhiteSpace(dto.AiGitHubCopilotModel) ? "auto" : dto.AiGitHubCopilotModel;
            AiAssistantAutoCreateRun = dto.AiAssistantAutoCreateRun;
            AiAssistantAutoLifecycle = dto.AiAssistantAutoLifecycle;
            AiAssistantAutoComposeTemplate = dto.AiAssistantAutoComposeTemplate;
            AiAssistantAutoKubernetes = dto.AiAssistantAutoKubernetes;
            _autoApprovedTools = dto.AiAssistantAutoApprovedTools is { } approvedTools
                ? new HashSet<string>(approvedTools.Where(t => !string.IsNullOrWhiteSpace(t)), StringComparer.Ordinal)
                : MigrateLegacyToolApprovals(dto);
            WslDistro = string.IsNullOrWhiteSpace(dto.WslDistro) ? null : dto.WslDistro;
            WslUpdatePreRelease = dto.WslUpdatePreRelease;
            K3sInstallerSha256 = string.IsNullOrWhiteSpace(dto.K3sInstallerSha256) ? null : dto.K3sInstallerSha256.Trim().ToLowerInvariant();

            // Load registries, always keeping a single Docker Hub default at the top.
            var registries = new List<RegistryEntry> { RegistryEntry.DockerHub() };
            if (dto.Registries is not null)
            {
                foreach (var r in dto.Registries)
                {
                    if (r is null || r.IsDefault || string.IsNullOrWhiteSpace(r.Host))
                    {
                        continue;
                    }

                    registries.Add(new RegistryEntry
                    {
                        Name = string.IsNullOrWhiteSpace(r.Name) ? r.Host! : r.Name!,
                        Host = r.Host!.Trim(),
                        Username = string.IsNullOrWhiteSpace(r.Username) ? null : r.Username,
                        IsAzure = r.IsAzure,
                        SubscriptionId = string.IsNullOrWhiteSpace(r.SubscriptionId) ? null : r.SubscriptionId,
                        AzureAcrName = string.IsNullOrWhiteSpace(r.AzureAcrName) ? null : r.AzureAcrName,
                    });
                }
            }

            Registries = registries;

            // Load persisted health-check policies (skip malformed / incomplete entries).
            var healthChecks = new List<HealthCheckConfig>();
            if (dto.HealthChecks is not null)
            {
                foreach (var h in dto.HealthChecks)
                {
                    if (h is null || string.IsNullOrWhiteSpace(h.ContainerName))
                    {
                        continue;
                    }

                    var config = new HealthCheckConfig
                    {
                        ContainerName = h.ContainerName!.Trim(),
                        Kind = h.Kind == (int)HealthProbeKind.Tcp ? HealthProbeKind.Tcp : HealthProbeKind.Command,
                        Command = h.Command ?? string.Empty,
                        TcpPort = h.TcpPort,
                        IntervalSeconds = Math.Clamp(
                            h.IntervalSeconds <= 0 ? 30 : h.IntervalSeconds,
                            HealthCheckConfig.MinIntervalSeconds,
                            HealthCheckConfig.MaxIntervalSeconds),
                        MaxRestarts = Math.Clamp(h.MaxRestarts, 0, HealthCheckConfig.MaxRestartLimit),
                        Enabled = h.Enabled,
                    };

                    if (config.IsValid)
                    {
                        healthChecks.Add(config);
                    }
                }
            }

            HealthChecks = healthChecks;

            // Load persisted restart policies (skip malformed / "no" entries).
            var restartPolicies = new List<RestartPolicyConfig>();
            if (dto.RestartPolicies is not null)
            {
                foreach (var r in dto.RestartPolicies)
                {
                    if (r is null || string.IsNullOrWhiteSpace(r.ContainerName))
                    {
                        continue;
                    }

                    var policy = new RestartPolicyConfig
                    {
                        ContainerName = r.ContainerName!.Trim(),
                        Policy = Enum.IsDefined(typeof(RestartPolicyKind), r.Policy)
                            ? (RestartPolicyKind)r.Policy
                            : RestartPolicyKind.No,
                        MaxRestarts = Math.Clamp(r.MaxRestarts, 0, RestartPolicyConfig.MaxRestartLimit),
                        Enabled = r.Enabled,
                    };

                    if (policy.IsValid)
                    {
                        restartPolicies.Add(policy);
                    }
                }
            }

            RestartPolicies = restartPolicies;
        }
        catch (Exception ex)
        {
            // Corrupt settings should never crash the app; fall back to defaults.
            logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults.", SettingsFile);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var dto = new SettingsDto
            {
                WslcPath = WslcPath,
                RefreshIntervalSeconds = RefreshIntervalSeconds,
                CloseToTray = CloseToTray,
                StartMinimized = StartMinimized,
                Theme = Theme,
                NotificationsEnabled = NotificationsEnabled,
                NotifyImageEvents = NotifyImageEvents,
                NotifyContainerEvents = NotifyContainerEvents,
                NotifyEngineEvents = NotifyEngineEvents,
                AiFeaturesEnabled = AiFeaturesEnabled,
                AiProvider = (int)AiProvider,
                AiOllamaEndpoint = AiOllamaEndpoint,
                AiOllamaModel = AiOllamaModel,
                AiAzureOpenAiEndpoint = AiAzureOpenAiEndpoint,
                AiAzureOpenAiDeployment = AiAzureOpenAiDeployment,
                AiOpenAiEndpoint = AiOpenAiEndpoint,
                AiOpenAiModel = AiOpenAiModel,
                AiGitHubCopilotModel = AiGitHubCopilotModel,
                AiAssistantAutoCreateRun = AiAssistantAutoCreateRun,
                AiAssistantAutoLifecycle = AiAssistantAutoLifecycle,
                AiAssistantAutoComposeTemplate = AiAssistantAutoComposeTemplate,
                AiAssistantAutoKubernetes = AiAssistantAutoKubernetes,
                AiAssistantAutoApprovedTools = _autoApprovedTools.ToList(),
                WslDistro = WslDistro,
                WslUpdatePreRelease = WslUpdatePreRelease,
                K3sInstallerSha256 = K3sInstallerSha256,
                Registries = Registries
                    .Where(r => !r.IsDefault && !string.IsNullOrWhiteSpace(r.Host))
                    .Select(r => new RegistryDto
                    {
                        Name = r.Name,
                        Host = r.Host,
                        Username = r.Username,
                        IsAzure = r.IsAzure,
                        SubscriptionId = r.SubscriptionId,
                        AzureAcrName = r.AzureAcrName,
                    })
                    .ToList(),
                HealthChecks = HealthChecks
                    .Where(h => h.IsValid)
                    .Select(h => new HealthCheckDto
                    {
                        ContainerName = h.ContainerName,
                        Kind = (int)h.Kind,
                        Command = h.Command,
                        TcpPort = h.TcpPort,
                        IntervalSeconds = h.IntervalSeconds,
                        MaxRestarts = h.MaxRestarts,
                        Enabled = h.Enabled,
                    })
                    .ToList(),
                RestartPolicies = RestartPolicies
                    .Where(r => r.IsValid)
                    .Select(r => new RestartPolicyDto
                    {
                        ContainerName = r.ContainerName,
                        Policy = (int)r.Policy,
                        MaxRestarts = r.MaxRestarts,
                        Enabled = r.Enabled,
                    })
                    .ToList(),
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch (Exception ex)
        {
            // Best effort; ignore persistence failures.
            logger.LogWarning(ex, "Failed to save settings to {Path}.", SettingsFile);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static HashSet<string> MigrateLegacyToolApprovals(SettingsDto dto)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (dto.AiAssistantAutoCreateRun)
        {
            set.UnionWith(AssistantToolCatalog.CreateRunTools);
        }

        if (dto.AiAssistantAutoLifecycle)
        {
            set.UnionWith(AssistantToolCatalog.LifecycleTools);
        }

        if (dto.AiAssistantAutoComposeTemplate)
        {
            set.UnionWith(AssistantToolCatalog.ComposeTemplateTools);
        }

        if (dto.AiAssistantAutoKubernetes)
        {
            set.UnionWith(AssistantToolCatalog.KubernetesTools);
        }

        return set;
    }

    private static string ResolveDefaultWslcPath()
    {
        if (File.Exists(DefaultWslcPath))
        {
            return DefaultWslcPath;
        }

        // Fall back to whatever is on PATH.
        return "wslc.exe";
    }

    private sealed class SettingsDto
    {
        public string? WslcPath { get; set; }
        public int RefreshIntervalSeconds { get; set; } = 5;
        public bool CloseToTray { get; set; } = true;
        public bool StartMinimized { get; set; }
        public string? Theme { get; set; }
        public bool NotificationsEnabled { get; set; } = true;
        public bool NotifyImageEvents { get; set; } = true;
        public bool NotifyContainerEvents { get; set; } = true;
        public bool NotifyEngineEvents { get; set; } = true;
        public bool AiFeaturesEnabled { get; set; }
        public int AiProvider { get; set; }
        public string? AiOllamaEndpoint { get; set; }
        public string? AiOllamaModel { get; set; }
        public string? AiAzureOpenAiEndpoint { get; set; }
        public string? AiAzureOpenAiDeployment { get; set; }
        public string? AiOpenAiEndpoint { get; set; }
        public string? AiOpenAiModel { get; set; }
        public string? AiGitHubCopilotModel { get; set; }
        public bool AiAssistantAutoCreateRun { get; set; }
        public bool AiAssistantAutoLifecycle { get; set; }
        public bool AiAssistantAutoComposeTemplate { get; set; }
        public bool AiAssistantAutoKubernetes { get; set; }
        public List<string>? AiAssistantAutoApprovedTools { get; set; }
        public string? WslDistro { get; set; }
        public bool WslUpdatePreRelease { get; set; }
        public string? K3sInstallerSha256 { get; set; }
        public List<RegistryDto>? Registries { get; set; }
        public List<HealthCheckDto>? HealthChecks { get; set; }
        public List<RestartPolicyDto>? RestartPolicies { get; set; }
    }

    private sealed class RestartPolicyDto
    {
        public string? ContainerName { get; set; }
        public int Policy { get; set; }
        public int MaxRestarts { get; set; } = 3;
        public bool Enabled { get; set; } = true;
    }

    private sealed class HealthCheckDto
    {
        public string? ContainerName { get; set; }
        public int Kind { get; set; }
        public string? Command { get; set; }
        public int TcpPort { get; set; }
        public int IntervalSeconds { get; set; } = 30;
        public int MaxRestarts { get; set; } = 3;
        public bool Enabled { get; set; } = true;
    }

    private sealed class RegistryDto
    {
        public string? Name { get; set; }
        public string? Host { get; set; }
        public string? Username { get; set; }
        public bool IsDefault { get; set; }
        public bool IsAzure { get; set; }
        public string? SubscriptionId { get; set; }
        public string? AzureAcrName { get; set; }
    }
}
