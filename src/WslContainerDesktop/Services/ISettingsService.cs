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

namespace WslContainerDesktop.Services;

public interface ISettingsService
{
    /// <summary>Full path to wslc.exe.</summary>
    string WslcPath { get; set; }

    /// <summary>Auto-refresh interval for lists in seconds.</summary>
    int RefreshIntervalSeconds { get; set; }

    /// <summary>Hide the window to the tray instead of exiting when closed.</summary>
    bool CloseToTray { get; set; }

    /// <summary>Start the app minimized to the tray.</summary>
    bool StartMinimized { get; set; }

    /// <summary>App theme: "Default", "Light", or "Dark".</summary>
    string Theme { get; set; }

    /// <summary>Master switch for Windows toast notifications. When false, all toasts are muted.</summary>
    bool NotificationsEnabled { get; set; }

    /// <summary>Emit toasts for image pull/build completion and failures.</summary>
    bool NotifyImageEvents { get; set; }

    /// <summary>Emit toasts when a container exits/crashes unexpectedly.</summary>
    bool NotifyContainerEvents { get; set; }

    /// <summary>Emit toasts when the container engine becomes unavailable or recovers.</summary>
    bool NotifyEngineEvents { get; set; }

    /// <summary>Master opt-in switch for AI diagnostics. Defaults off.</summary>
    bool AiFeaturesEnabled { get; set; }

    /// <summary>Selected AI provider.</summary>
    Models.AiProviderKind AiProvider { get; set; }

    string AiOllamaEndpoint { get; set; }

    string AiOllamaModel { get; set; }

    string AiAzureOpenAiEndpoint { get; set; }

    string AiAzureOpenAiDeployment { get; set; }

    string AiOpenAiEndpoint { get; set; }

    string AiOpenAiModel { get; set; }

    string AiGitHubCopilotModel { get; set; }

    bool AiAssistantAutoCreateRun { get; set; }

    bool AiAssistantAutoLifecycle { get; set; }

    bool AiAssistantAutoComposeTemplate { get; set; }

    bool AiAssistantAutoKubernetes { get; set; }

    /// <summary>WSL distro to host the k3s cluster. Null/empty uses the WSL default distro.</summary>
    string? WslDistro { get; set; }

    /// <summary>Include WSL pre-release versions when checking for and applying updates (<c>wsl --update --pre-release</c>).</summary>
    bool WslUpdatePreRelease { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the last k3s installer script (get.k3s.io) the user has
    /// approved. Used to detect if the remote installer changes between installs/upgrades.
    /// Null until the first install establishes the trust-on-first-use pin.
    /// </summary>
    string? K3sInstallerSha256 { get; set; }

    /// <summary>
    /// User-registered container registries (Docker Hub is always present as the default).
    /// Credentials are never stored here; login is delegated to `wslc login`.
    /// </summary>
    System.Collections.Generic.List<Models.RegistryEntry> Registries { get; set; }

    /// <summary>
    /// Per-container health probe and restart policies enforced by the health watchdog.
    /// Keyed by container name so they survive stop/start and recreation.
    /// </summary>
    System.Collections.Generic.List<Models.HealthCheckConfig> HealthChecks { get; set; }

    /// <summary>
    /// Per-container restart policies for containers without a health check (compose services with
    /// a <c>restart:</c> policy). Enforced in-process by the restart watchdog while the app runs.
    /// </summary>
    System.Collections.Generic.List<Models.RestartPolicyConfig> RestartPolicies { get; set; }

    void Load();
    void Save();
}
