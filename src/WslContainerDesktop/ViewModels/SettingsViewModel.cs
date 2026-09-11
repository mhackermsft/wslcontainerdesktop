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

using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;
    private readonly StartupService _startup;
    private readonly FileLoggerProvider _fileLogger;
    private readonly IAiDiagnosticsService _aiDiagnostics;
    private readonly IAiCredentialStore _aiCredentials;
    private readonly ILocalAiSetupService _localAi;
    private readonly IAiCapabilityService _aiCapabilities;
    private readonly IAiAvailabilityService _aiAvailability;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly HttpClient _http;

    private bool _suppressStartupWrite;
    private bool _suppressProviderModelRefresh;
    private bool _suppressAiModelWrite;
    private bool _suppressAiOllamaModelWrite;
    private int _localAiSetupGeneration;

    /// <summary>Default quick-start model: small, widely available, and tool-capable so the
    /// assistant works immediately after setup.</summary>
    private const string DefaultOllamaModel = "qwen2.5:7b";

    [ObservableProperty]
    private string _wslcPath;

    [ObservableProperty]
    private int _refreshIntervalSeconds;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _restartRunningContainersOnLaunch;

    [ObservableProperty]
    private bool _runAtLogin;

    [ObservableProperty]
    private bool _runAtLoginEnabled = true;

    [ObservableProperty]
    private string _runAtLoginNote =
        "Automatically launch WSL Container Desktop when you sign in to Windows.";

    [ObservableProperty]
    private int _selectedThemeIndex;

    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private bool _notifyImageEvents;

    [ObservableProperty]
    private bool _notifyContainerEvents;

    [ObservableProperty]
    private bool _notifyEngineEvents;

    [ObservableProperty]
    private string _devContainerNpmRegistry = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoveOllama))]
    [NotifyPropertyChangedFor(nameof(ShowLocalSetupButtons))]
    [NotifyPropertyChangedFor(nameof(CanQuickStartOllama))]
    [NotifyPropertyChangedFor(nameof(QuickStartHint))]
    private bool _isOllamaRuntimePresent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProviderDetail))]
    private bool _aiFeaturesEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiProviderSettings))]
    [NotifyPropertyChangedFor(nameof(ShowGitHubCopilotSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOllamaSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAzureOpenAiSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOpenAiSettings))]
    [NotifyPropertyChangedFor(nameof(ShowFoundryLocalSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAiSecretSettings))]
    [NotifyPropertyChangedFor(nameof(ActiveProviderName))]
    [NotifyPropertyChangedFor(nameof(ActiveProviderDetail))]
    [NotifyPropertyChangedFor(nameof(CanQuickStartOllama))]
    [NotifyPropertyChangedFor(nameof(ShowRemoveOllama))]
    private int _selectedAiProviderIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProviderDetail))]
    private string _aiOllamaEndpoint = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProviderDetail))]
    private string _aiOllamaModel = string.Empty;

    [ObservableProperty]
    private string _ollamaPullModel = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOllamaModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshOpenAiModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullOllamaModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetUpLocalAiCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveLocalAiCommand))]
    [NotifyPropertyChangedFor(nameof(CanQuickStartOllama))]
    private bool _isOllamaBusy;

    [ObservableProperty]
    private string _aiAzureOpenAiEndpoint = string.Empty;

    [ObservableProperty]
    private string _aiAzureOpenAiDeployment = string.Empty;

    [ObservableProperty]
    private string _aiOpenAiEndpoint = string.Empty;

    [ObservableProperty]
    private string _aiOpenAiModel = string.Empty;

    [ObservableProperty]
    private string _aiGitHubCopilotModel = string.Empty;

    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    /// <summary>Feedback for the Quick start / one-click local AI setup and removal only — never
    /// touched by provider selection, credential, or model-list operations.</summary>
    [ObservableProperty]
    private AiFeedback _localAiFeedback = AiFeedback.None;

    /// <summary>Feedback for provider selection/configuration, credential save, model list/pull,
    /// and connectivity test — rendered inside Provider settings, never in the local AI card.</summary>
    [ObservableProperty]
    private AiFeedback _providerFeedback = AiFeedback.Informational(
        "AI features are off",
        "AI features are off by default. Enable them and review the payload preview before sending diagnostics.");

    [ObservableProperty]
    private string _engineVersion = "Unknown";

    [ObservableProperty]
    private bool _isBusy;

    public string AppVersion { get; } = ResolveAppVersion();

    public bool ShowAiProviderSettings => CurrentAiProvider != AiProviderKind.None;

    public bool ShowGitHubCopilotSettings => CurrentAiProvider == AiProviderKind.GitHubCopilot;

    public bool ShowOllamaSettings => CurrentAiProvider == AiProviderKind.Ollama;

    public bool ShowAzureOpenAiSettings => CurrentAiProvider == AiProviderKind.AzureOpenAi;

    public bool ShowOpenAiSettings => CurrentAiProvider == AiProviderKind.OpenAi;

    public bool ShowFoundryLocalSettings => CurrentAiProvider == AiProviderKind.FoundryLocal;
    public FoundryLocalSettingsViewModel FoundryLocal { get; }

    public bool ShowAiSecretSettings => CurrentAiProvider is AiProviderKind.AzureOpenAi or AiProviderKind.OpenAi;

    private AiProviderKind CurrentAiProvider => Enum.IsDefined(typeof(AiProviderKind), SelectedAiProviderIndex)
        ? (AiProviderKind)SelectedAiProviderIndex
        : AiProviderKind.None;

    /// <summary>Quick start is driven by what is installed, not by the Provider list, which is for
    /// manual configuration. Foundry Local is implemented but hidden from the UI for now.</summary>
    public bool ShowLocalSetupButtons => !IsOllamaRuntimePresent;

    public bool CanQuickStartOllama => ShowLocalSetupButtons && !IsOllamaBusy;

    /// <summary>Removal is offered only for the local runtime that is actually installed.</summary>
    public bool ShowRemoveOllama => IsOllamaRuntimePresent;

    public string QuickStartHint => IsOllamaRuntimePresent
        ? "Ollama is set up and selected as your provider."
        : "Run a model on this machine, or use Provider below to configure a service yourself.";

    /// <summary>Refreshes which local runtimes exist, so setup and removal reflect reality.</summary>
    public async Task RefreshLocalRuntimePresenceAsync(CancellationToken ct = default)
    {
        var present = await _localAi.IsRuntimePresentAsync(ct);
        if (present != IsOllamaRuntimePresent)
            IsOllamaRuntimePresent = present;
    }

    /// <summary>Short name of the provider the app will actually use, for the always-visible header.</summary>
    public string ActiveProviderName => CurrentAiProvider switch
    {
        AiProviderKind.GitHubCopilot => "GitHub Copilot",
        AiProviderKind.Ollama => "Ollama (local container)",
        AiProviderKind.AzureOpenAi => "Azure OpenAI",
        AiProviderKind.OpenAi => "OpenAI-compatible",
        AiProviderKind.FoundryLocal => "Foundry Local (on Windows)",
        _ => "None selected",
    };

    /// <summary>Endpoint/model detail for the active provider so the page cannot be misread.</summary>
    public string ActiveProviderDetail
    {
        get
        {
            if (!AiFeaturesEnabled)
                return "AI features are off. Turn them on to use a provider.";
            return CurrentAiProvider switch
            {
                AiProviderKind.None => "Choose a provider below, or use Quick start to run a model on this machine.",
                AiProviderKind.GitHubCopilot => Describe("Model", AiGitHubCopilotModel),
                AiProviderKind.Ollama => $"{Describe("Model", AiOllamaModel)} · {Describe("Endpoint", AiOllamaEndpoint)}",
                AiProviderKind.AzureOpenAi => $"{Describe("Deployment", AiAzureOpenAiDeployment)} · {Describe("Endpoint", AiAzureOpenAiEndpoint)}",
                AiProviderKind.OpenAi => $"{Describe("Model", AiOpenAiModel)} · {Describe("Endpoint", AiOpenAiEndpoint)}",
                AiProviderKind.FoundryLocal => $"{Describe("Model", FoundryLocal.Model)} · {Describe("Endpoint", FoundryLocal.Endpoint)}",
                _ => string.Empty,
            };
            static string Describe(string label, string value) =>
                $"{label}: {(string.IsNullOrWhiteSpace(value) ? "not set" : value.Trim())}";
        }
    }

    /// <summary>Quick start for Foundry Local: select the provider, then run the same
    /// single-approval setup that installs only when needed and configures what is already there.</summary>
    public async Task SetUpFoundryLocalAsync(Func<string, CancellationToken, Task<bool>> confirm)
    {
        SelectedAiProviderIndex = (int)AiProviderKind.FoundryLocal;
        AiFeaturesEnabled = true;
        await FoundryLocal.PrepareInitialModelAsync(confirm);
        RefreshActiveProviderSummary();
    }

    public void RefreshActiveProviderSummary()
    {
        OnPropertyChanged(nameof(ActiveProviderName));
        OnPropertyChanged(nameof(ActiveProviderDetail));
    }

    /// <summary>Builds classification context for the currently selected provider.</summary>
    private AiErrorContext ProviderContext(string operation, string? endpoint = null, string? modelOrDeployment = null) =>
        AiErrorContext.For(_settings.AiProvider, operation, endpoint, modelOrDeployment);

    /// <summary>Local AI setup/removal always targets the Ollama container, regardless of which
    /// provider is currently selected in Settings.</summary>
    private static AiErrorContext LocalAiContext(string operation) =>
        AiErrorContext.For(AiProviderKind.Ollama, operation);

    [RelayCommand]
    private void DismissLocalAiFeedback() => LocalAiFeedback = AiFeedback.None;

    [RelayCommand]
    private void DismissProviderFeedback() => ProviderFeedback = AiFeedback.None;

    [RelayCommand]
    private void CopyLocalAiFeedbackDetails() => CopyFeedbackDetails(LocalAiFeedback);

    [RelayCommand]
    private void CopyProviderFeedbackDetails() => CopyFeedbackDetails(ProviderFeedback);

    private static void CopyFeedbackDetails(AiFeedback feedback)
    {
        if (!feedback.HasTechnicalDetails)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText($"{feedback.Title}\n{feedback.Message}\n\n{feedback.TechnicalDetails}");
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// The app's version for display. Reads the packaged identity version (which the release
    /// pipeline stamps into the MSIX), falling back to the assembly version when unpackaged.
    /// </summary>
    private static string ResolveAppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "Version 1.0.0" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public ObservableCollection<AiModelOption> GitHubCopilotModels { get; } = new() { new AiModelOption("auto", "auto") };

    public ObservableCollection<AiModelOption> OllamaModels { get; } = new();

    /// <summary>
    /// Model ids advertised by the configured OpenAI-compatible endpoint (<c>GET /models</c>).
    /// Bound to an editable ComboBox, so a model that the server does not list can still be typed.
    /// </summary>
    public ObservableCollection<string> OpenAiModels { get; } = new();

    public ObservableCollection<AssistantToolPermissionGroup> AssistantToolPermissions { get; }

    private ObservableCollection<AssistantToolPermissionGroup> BuildAssistantToolPermissions()
    {
        var groups = new ObservableCollection<AssistantToolPermissionGroup>();
        foreach (var group in AssistantToolCatalog.Groups)
        {
            var tools = group.Tools
                .Select(tool => new AssistantToolPermission(
                    tool.Name,
                    tool.DisplayName,
                    _settings.IsAssistantToolAutoApproved(tool.Name),
                    (name, autoApprove) => _settings.SetAssistantToolAutoApproved(name, autoApprove)))
                .ToList();
            groups.Add(new AssistantToolPermissionGroup { Header = group.Header, Tools = tools });
        }

        return groups;
    }

    public SettingsViewModel(ISettingsService settings, IWslcService wslc, DialogService dialogs, StartupService startup, FileLoggerProvider fileLogger, IAiDiagnosticsService aiDiagnostics, IAiCredentialStore aiCredentials, ILocalAiSetupService localAi, IAiAvailabilityService aiAvailability, IAiCapabilityService aiCapabilities, HttpClient http, ILogger<SettingsViewModel> logger, FoundryLocalSettingsViewModel foundryLocal)
    {
        FoundryLocal = foundryLocal;
        _settings = settings;
        _wslc = wslc;
        _dialogs = dialogs;
        _startup = startup;
        _fileLogger = fileLogger;
        _aiDiagnostics = aiDiagnostics;
        _aiCredentials = aiCredentials;
        _localAi = localAi;
        _aiCapabilities = aiCapabilities;
        _aiAvailability = aiAvailability;
        _aiAvailability.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(AiCapabilityStatus));
            OnPropertyChanged(nameof(AiCapabilityHeadline));
        };
        _http = http;
        _logger = logger;

        _wslcPath = settings.WslcPath;
        _refreshIntervalSeconds = settings.RefreshIntervalSeconds;
        _closeToTray = settings.CloseToTray;
        _startMinimized = settings.StartMinimized;
        _restartRunningContainersOnLaunch = settings.RestartRunningContainersOnLaunch;
        _notificationsEnabled = settings.NotificationsEnabled;
        _notifyImageEvents = settings.NotifyImageEvents;
        _notifyContainerEvents = settings.NotifyContainerEvents;
        _notifyEngineEvents = settings.NotifyEngineEvents;
        _devContainerNpmRegistry = settings.DevContainerNpmRegistry ?? string.Empty;
        _aiFeaturesEnabled = settings.AiFeaturesEnabled;
        // Foundry Local is implemented but hidden from Settings for now. A previously saved
        // selection must not leave the picker showing an option that no longer exists.
        _selectedAiProviderIndex = settings.AiProvider == AiProviderKind.FoundryLocal
            ? (int)AiProviderKind.None
            : (int)settings.AiProvider;
        _aiOllamaEndpoint = settings.AiOllamaEndpoint;
        _aiOllamaModel = settings.AiOllamaModel;
        _aiAzureOpenAiEndpoint = settings.AiAzureOpenAiEndpoint;
        _aiAzureOpenAiDeployment = settings.AiAzureOpenAiDeployment;
        _aiOpenAiEndpoint = settings.AiOpenAiEndpoint;
        _aiOpenAiModel = settings.AiOpenAiModel;
        _aiGitHubCopilotModel = settings.AiGitHubCopilotModel;
        AssistantToolPermissions = BuildAssistantToolPermissions();
        EnsureGitHubCopilotModelOption(_aiGitHubCopilotModel);
        _selectedThemeIndex = settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
    }

    partial void OnWslcPathChanged(string value)
    {
        _settings.WslcPath = value;
        _settings.Save();
    }

    partial void OnRefreshIntervalSecondsChanged(int value)
    {
        _settings.RefreshIntervalSeconds = Math.Clamp(value, AppConstants.RefreshIntervalMinSeconds, AppConstants.RefreshIntervalMaxSeconds);
        _settings.Save();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        _settings.Save();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        _settings.StartMinimized = value;
        _settings.Save();
    }

    partial void OnRestartRunningContainersOnLaunchChanged(bool value)
    {
        _settings.RestartRunningContainersOnLaunch = value;
        _settings.Save();
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        _settings.NotificationsEnabled = value;
        _settings.Save();
    }

    partial void OnNotifyImageEventsChanged(bool value)
    {
        _settings.NotifyImageEvents = value;
        _settings.Save();
    }

    partial void OnNotifyContainerEventsChanged(bool value)
    {
        _settings.NotifyContainerEvents = value;
        _settings.Save();
    }

    partial void OnNotifyEngineEventsChanged(bool value)
    {
        _settings.NotifyEngineEvents = value;
        _settings.Save();
    }

    partial void OnDevContainerNpmRegistryChanged(string value)
    {
        _settings.DevContainerNpmRegistry = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settings.Save();
    }

    partial void OnAiFeaturesEnabledChanged(bool value)
    {
        _settings.AiFeaturesEnabled = value;
        _settings.Save();
    }

    partial void OnSelectedAiProviderIndexChanged(int value)
    {
        _settings.AiProvider = Enum.IsDefined(typeof(AiProviderKind), value)
            ? (AiProviderKind)value
            : AiProviderKind.None;
        FoundryLocal.OnProviderChanged();
        LoadStoredAiSecretIndicator();
        _settings.Save();
        if (_settings.AiProvider == AiProviderKind.GitHubCopilot)
        {
            _ = LoadGitHubCopilotModelsAsync();
        }
        else if (_settings.AiProvider == AiProviderKind.Ollama && !_suppressProviderModelRefresh)
        {
            _ = LoadOllamaModelsAsync();
        }
    }

    partial void OnAiOllamaEndpointChanged(string value)
    {
        _settings.AiOllamaEndpoint = value;
        _settings.Save();
    }

    partial void OnAiOllamaModelChanged(string value)
    {
        if (_suppressAiOllamaModelWrite)
        {
            return;
        }

        _settings.AiOllamaModel = value ?? string.Empty;
        _settings.Save();
    }

    partial void OnAiAzureOpenAiEndpointChanged(string value)
    {
        _settings.AiAzureOpenAiEndpoint = value;
        _settings.Save();
    }

    partial void OnAiAzureOpenAiDeploymentChanged(string value)
    {
        _settings.AiAzureOpenAiDeployment = value;
        _settings.Save();
    }

    partial void OnAiOpenAiEndpointChanged(string value)
    {
        _settings.AiOpenAiEndpoint = value;
        _settings.Save();
    }

    partial void OnAiOpenAiModelChanged(string value)
    {
        _settings.AiOpenAiModel = value;
        _settings.Save();
    }

    partial void OnAiGitHubCopilotModelChanged(string value)
    {
        if (_suppressAiModelWrite)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            ReapplyGitHubCopilotModel(_settings.AiGitHubCopilotModel);
            return;
        }

        EnsureGitHubCopilotModelOption(value);
        _settings.AiGitHubCopilotModel = value;
        _settings.Save();
    }

    public void SaveAiApiKey(string secret)
    {
        if (_settings.AiProvider is not (AiProviderKind.AzureOpenAi or AiProviderKind.OpenAi) || string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        _aiCredentials.WriteSecret(_settings.AiProvider, secret);
        // Notify existing settings observers to re-read readiness using the new credential identity.
        _settings.Save();
        AiApiKey = string.Empty;
        ProviderFeedback = AiFeedback.Success(
            "Credential saved",
            $"Saved {_settings.AiProvider.DisplayName()} credential in Windows Credential Manager.");
    }

    private void LoadStoredAiSecretIndicator()
    {
        AiApiKey = string.Empty;
        if (_settings.AiProvider == AiProviderKind.FoundryLocal)
        {
            ProviderFeedback = AiFeedback.Informational("Foundry Local",
                "Uses only your explicit loopback URL and actual model ID. No API key, cloud fallback, WSLC engine or Ollama container is used for inference.");
            return;
        }
        if (_settings.AiProvider == AiProviderKind.GitHubCopilot)
        {
            ProviderFeedback = AiFeedback.Informational(
                "GitHub Copilot",
                "Uses your logged-in Copilot CLI account. No API key is needed.");
            return;
        }

        if (_settings.AiProvider is AiProviderKind.None or AiProviderKind.Ollama)
        {
            ProviderFeedback = _settings.AiProvider == AiProviderKind.Ollama
                ? AiFeedback.Informational("Ollama", "Uses the configured local endpoint and model.")
                : AiFeedback.Informational("No provider selected", "Choose an AI provider to configure diagnostics.");
            return;
        }

        if (_settings.AiProvider == AiProviderKind.OpenAi)
        {
            var endpoint = string.IsNullOrWhiteSpace(_settings.AiOpenAiEndpoint)
                ? OpenAiProvider.DefaultEndpoint
                : _settings.AiOpenAiEndpoint.Trim();
            ProviderFeedback = _aiCredentials.TryReadSecret(AiProviderKind.OpenAi, out _)
                ? AiFeedback.Informational("OpenAI-compatible", $"Using {endpoint} with a saved API key.")
                : AiFeedback.Informational("OpenAI-compatible", $"Using {endpoint} with no API key. That is fine for local servers; hosted services such as OpenAI need one.");
            return;
        }

        ProviderFeedback = _aiCredentials.TryReadSecret(_settings.AiProvider, out _)
            ? AiFeedback.Informational(_settings.AiProvider.DisplayName(), $"{_settings.AiProvider.DisplayName()} has a saved credential.")
            : AiFeedback.Warning(_settings.AiProvider.DisplayName(), "No credential is saved for the selected provider.");
    }

    partial void OnRunAtLoginChanged(bool value)
    {
        if (_suppressStartupWrite)
        {
            return;
        }

        _ = ApplyRunAtLoginAsync(value);
    }

    private async Task ApplyRunAtLoginAsync(bool value)
    {
        var result = await _startup.SetEnabledAsync(value);
        switch (result)
        {
            case StartupToggleResult.Applied:
                RunAtLoginNote = value
                    ? "WSL Container Desktop will launch when you sign in to Windows."
                    : "Automatically launch WSL Container Desktop when you sign in to Windows.";
                break;

            case StartupToggleResult.BlockedByUser:
                // The user disabled startup in Task Manager; only they can change it there.
                SetRunAtLoginSilently(false);
                RunAtLoginEnabled = false;
                RunAtLoginNote =
                    "Startup is turned off for this app in Windows Task Manager (Startup apps). " +
                    "Re-enable it there to allow launching at sign-in.";
                await _dialogs.ShowMessageAsync("Managed by Windows",
                    "This app's startup is controlled in Task Manager → Startup apps. " +
                    "Please enable it there.");
                break;

            case StartupToggleResult.BlockedByPolicy:
                SetRunAtLoginSilently(!value);
                RunAtLoginEnabled = false;
                RunAtLoginNote = "This setting is managed by your organization's policy.";
                break;

            case StartupToggleResult.Unavailable:
                SetRunAtLoginSilently(!value);
                RunAtLoginNote = "Run at sign-in isn't available for this installation.";
                break;
        }
    }

    /// <summary>Loads the current run-at-login state without triggering a write.</summary>
    public async Task LoadStartupStateAsync()
    {
        var enabled = await _startup.IsEnabledAsync();
        var canToggle = await _startup.CanToggleAsync();

        SetRunAtLoginSilently(enabled);
        RunAtLoginEnabled = canToggle || enabled;

        if (!canToggle && !enabled)
        {
            RunAtLoginNote =
                "Startup for this app is turned off in Windows Task Manager (Startup apps). " +
                "Enable it there to allow launching at sign-in.";
        }
        else
        {
            RunAtLoginNote = enabled
                ? "WSL Container Desktop will launch when you sign in to Windows."
                : "Automatically launch WSL Container Desktop when you sign in to Windows.";
        }
    }

    private void SetRunAtLoginSilently(bool value)
    {
        _suppressStartupWrite = true;
        RunAtLogin = value;
        _suppressStartupWrite = false;
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        _settings.Theme = value switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "Default",
        };
        _settings.Save();
        ThemeChangeRequested?.Invoke(this, _settings.Theme);
    }

    public event EventHandler<string>? ThemeChangeRequested;

    /// <summary>Opens the folder that holds the rolling diagnostic logs in File Explorer.</summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var dir = _fileLogger.Directory;
            System.IO.Directory.CreateDirectory(dir);

            // Launch explorer.exe with the folder as an argument. This is more reliable than
            // shell-executing a bare directory path, especially from a packaged (MSIX) process.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Opening the folder is a convenience; ignore failures (e.g. no shell handler).
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _wslc.GetVersionAsync();
            if (result.Success)
            {
                EngineVersion = result.StandardOutput.Trim();
                await _dialogs.ShowMessageAsync("Connection OK", $"Connected to WSL container engine.\n\n{EngineVersion}");
            }
            else
            {
                EngineVersion = "Unreachable";
                await _dialogs.ShowMessageAsync("Connection failed",
                    $"Could not reach the WSL container engine using:\n{_settings.WslcPath}\n\n{result.ErrorText}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string AiCapabilityStatus => _aiAvailability.Observation?.StatusText
        ?? "Enable AI and choose a provider. Capability observations are unknown until checked.";

    /// <summary>Single-line headline so the full capability report can stay collapsed.</summary>
    public string AiCapabilityHeadline
    {
        get
        {
            var observation = _aiAvailability.Observation;
            if (observation is null)
                return "AI capabilities: not checked yet";
            var chat = observation.Chat.Support;
            var tools = observation.Tools.Support;
            return chat switch
            {
                AiSupport.Supported when tools == AiSupport.Supported => "AI capabilities: chat and tools ready",
                AiSupport.Supported => "AI capabilities: chat ready, tools unconfirmed",
                AiSupport.Unsupported => "AI capabilities: provider not usable — see details",
                _ => "AI capabilities: unconfirmed — see details",
            };
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestAiProviderAsync(CancellationToken ct)
    {
        IsBusy = true;
        ProviderFeedback = AiFeedback.Informational("Testing capabilities",
            "Checking metadata and bounded synthetic chat/tool/JSON requests. Cold starts may take up to 90 seconds. No app actions or downloads run; cancel at any time.");
        try
        {
            var result = await _aiDiagnostics.TestProviderAsync(ct);
            await _aiAvailability.RefreshAsync(ct);
            ProviderFeedback = AiFeedback.Informational("Capability observations", result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProviderFeedback = AiFeedback.Informational("Test cancelled",
                "The test stopped. Only completed checks can remain cached. No model download or app action was started.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AI capability test could not complete.");
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext("Provider test"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SignInGitHubCopilot()
    {
        ProviderFeedback = AiFeedback.Informational(
            "GitHub Copilot sign-in",
            "GitHub Copilot uses your logged-in Copilot CLI account. Run `copilot login` outside the app if you need to sign in.");
    }

    [RelayCommand]
    private async Task RefreshGitHubCopilotModelsAsync() => await LoadGitHubCopilotModelsAsync();

    public async Task LoadGitHubCopilotModelsAsync()
    {
        if (CurrentAiProvider != AiProviderKind.GitHubCopilot)
        {
            return;
        }

        var persisted = string.IsNullOrWhiteSpace(_settings.AiGitHubCopilotModel)
            ? "auto"
            : _settings.AiGitHubCopilotModel;
        SeedGitHubCopilotModels(persisted);

        try
        {
            IsBusy = true;
            ProviderFeedback = AiFeedback.Informational("Loading models", "Loading GitHub Copilot models…");
            await using var client = GitHubCopilotProvider.CreateClient(_logger);
            await client.StartAsync();
            var models = await client.ListModelsAsync();

            var modelList = models.ToList();
            var configuredAvailable = modelList.Any(m => string.Equals(m.Id, persisted, StringComparison.OrdinalIgnoreCase));
            var options = modelList
                .OrderBy(m => string.Equals(m.Id, "auto", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(model =>
                {
                    var supportsReasoning = model.SupportedReasoningEfforts is { Count: > 0 };
                    var suffix = supportsReasoning ? " · reasoning" : string.Empty;
                    return new AiModelOption(model.Id, $"{model.Id} — {model.Name}{suffix}");
                })
                .ToList();

            EnsureOption(options, "auto", "auto");
            EnsureOption(options, persisted, $"{persisted} (configured)");
            ReplaceGitHubCopilotModels(options);
            ReapplyGitHubCopilotModel(persisted);

            ProviderFeedback = !configuredAvailable
                ? AiFeedback.Warning("Configured model unavailable", $"Configured model '{persisted}' is not available. Choose a model from the list.")
                : AiFeedback.Success("Models loaded", $"Loaded {GitHubCopilotModels.Count} GitHub Copilot model(s).");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load GitHub Copilot models.");
            SeedGitHubCopilotModels(persisted);
            ReapplyGitHubCopilotModel(persisted);
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext("Refresh GitHub Copilot models"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SeedGitHubCopilotModels(string persisted)
    {
        ReplaceGitHubCopilotModels([
            new AiModelOption("auto", "auto"),
            new AiModelOption(persisted, string.Equals(persisted, "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : $"{persisted} (configured)"),
        ]);
        ReapplyGitHubCopilotModel(persisted);
    }

    private void EnsureGitHubCopilotModelOption(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || GitHubCopilotModels.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        GitHubCopilotModels.Add(new AiModelOption(id, $"{id} (configured)"));
    }

    private void ReapplyGitHubCopilotModel(string model)
    {
        var value = string.IsNullOrWhiteSpace(model) ? "auto" : model;
        EnsureGitHubCopilotModelOption(value);
        _suppressAiModelWrite = true;
        AiGitHubCopilotModel = string.Empty;
        _suppressAiModelWrite = false;
        AiGitHubCopilotModel = value;
    }

    private void ReplaceGitHubCopilotModels(IEnumerable<AiModelOption> models)
    {
        _suppressAiModelWrite = true;
        try
        {
            GitHubCopilotModels.Clear();
            foreach (var model in models
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()))
            {
                GitHubCopilotModels.Add(model);
            }
        }
        finally
        {
            _suppressAiModelWrite = false;
        }
    }

    private static void EnsureOption(List<AiModelOption> options, string id, string displayName)
    {
        if (!options.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new AiModelOption(id, displayName));
        }
    }

    private bool CanRunOllamaCommand() => !IsOllamaBusy;

    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task RefreshOllamaModelsAsync() => await LoadOllamaModelsAsync();

    public async Task LoadOllamaModelsAsync()
    {
        if (CurrentAiProvider != AiProviderKind.Ollama)
        {
            return;
        }

        var persisted = _settings.AiOllamaModel?.Trim() ?? string.Empty;
        Uri? endpoint = null;
        try
        {
            IsOllamaBusy = true;
            ProviderFeedback = AiFeedback.Informational("Loading models", "Loading installed Ollama models…");
            endpoint = NormalizeOllamaEndpoint(_settings.AiOllamaEndpoint);
            var names = await ReadInstalledOllamaModelsAsync(endpoint, CancellationToken.None);

            ReplaceOllamaModels(names, persisted);
            ProviderFeedback = names.Count == 0
                ? AiFeedback.Warning("No models installed", "No Ollama models installed. Before pulling one below, audit its immutable digest and authoritative publication date (at least seven days old).")
                : AiFeedback.Success("Models loaded", $"Loaded {names.Count} installed Ollama model(s).");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load Ollama models.");
            ReplaceOllamaModels([], persisted);
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext("Refresh Ollama models", endpoint?.ToString(), persisted));
        }
        finally
        {
            IsOllamaBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task RefreshOpenAiModelsAsync() => await LoadOpenAiModelsAsync();

    /// <summary>
    /// Queries <c>GET {endpoint}/models</c> on the configured OpenAI-compatible server so the user
    /// can pick from what that host actually serves. The saved model id is always kept in the list
    /// (and selected) so a custom value survives a failed or partial refresh.
    /// </summary>
    public async Task LoadOpenAiModelsAsync()
    {
        if (CurrentAiProvider != AiProviderKind.OpenAi)
        {
            return;
        }

        var persisted = _settings.AiOpenAiModel?.Trim() ?? string.Empty;
        string? endpoint = null;
        try
        {
            IsOllamaBusy = true;
            ProviderFeedback = AiFeedback.Informational("Loading models", "Loading models from the OpenAI-compatible endpoint…");

            var uri = OpenAiProvider.BuildUri(_settings.AiOpenAiEndpoint, "models");
            endpoint = uri.ToString();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (_aiCredentials.TryReadSecret(AiProviderKind.OpenAi, out var key) && !string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw AiProviderException.FromHttpFailure(AiProviderKind.OpenAi, "Refresh OpenAI-compatible models", response.StatusCode, endpoint, persisted, body);
            }

            var ids = new List<string>();
            using (var doc = JsonDocument.Parse(body))
            {
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var model in data.EnumerateArray())
                    {
                        if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            var value = id.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                ids.Add(value!);
                            }
                        }
                    }
                }
            }

            ReplaceOpenAiModels(ids, persisted);
            ProviderFeedback = ids.Count == 0
                ? AiFeedback.Warning("No models listed", "The endpoint responded but listed no models. Type the model id your server expects.")
                : AiFeedback.Success("Models loaded", $"Loaded {ids.Count} model(s) from the OpenAI-compatible endpoint.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load OpenAI-compatible models.");
            ReplaceOpenAiModels([], persisted);
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext("Refresh OpenAI-compatible models", endpoint, persisted));
        }
        finally
        {
            IsOllamaBusy = false;
        }
    }

    private void ReplaceOpenAiModels(IEnumerable<string> ids, string persisted)
    {
        var ordered = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(persisted)
            && !ordered.Any(id => string.Equals(id, persisted, StringComparison.OrdinalIgnoreCase)))
        {
            ordered.Insert(0, persisted);
        }

        OpenAiModels.Clear();
        foreach (var id in ordered)
        {
            OpenAiModels.Add(id);
        }

        // Re-assert the saved id so the editable ComboBox keeps showing it after the list swap.
        if (!string.IsNullOrWhiteSpace(persisted))
        {
            AiOpenAiModel = persisted;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task PullOllamaModelAsync()
    {
        var name = OllamaPullModel?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ProviderFeedback = AiFeedback.Warning("Model name required", "Enter a model name to pull (for example qwen2.5:7b).");
            return;
        }

        Uri? endpoint = null;
        var isPullStarted = false;
        try
        {
            IsOllamaBusy = true;
            endpoint = NormalizeOllamaEndpoint(_settings.AiOllamaEndpoint);
            if (!await _dialogs.ShowConfirmAsync(
                    "Confirm audited model download",
                    $"Download '{name}' at '{endpoint}'? This may download several GB on that server. " +
                    "Continue only if you have audited the immutable model digest and verified from authoritative publication metadata that it is at least seven days old. " +
                    "A mutable tag or model name alone is not proof. The app cannot verify this audit; cancel if the digest or publication date is unknown.",
                    primaryText: "I audited it — pull",
                    closeText: "Cancel"))
            {
                ProviderFeedback = AiFeedback.Informational("Model pull cancelled", "No model download was requested.");
                return;
            }

            isPullStarted = true;
            _aiCapabilities.Invalidate();
            if (await StreamPullModelAsync(name, endpoint, fb => ProviderFeedback = fb))
            {
                OllamaPullModel = string.Empty;
                await LoadOllamaModelsAsync();
                AiOllamaModel = name;
                ProviderFeedback = AiFeedback.Success("Model pulled", $"Pulled '{name}' and selected it.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama pull failed.");
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext("Pull Ollama model", endpoint?.ToString(), name));
        }
        finally
        {
            if (isPullStarted)
            {
                _aiCapabilities.Invalidate();
            }
            IsOllamaBusy = false;
        }
    }

    /// <summary>
    /// Streams an Ollama <c>/api/pull</c> for <paramref name="name"/>, reporting progress through
    /// <paramref name="report"/> for the explicitly confirmed, audited "Pull a model" action.
    /// Runtime setup never calls this method. Returns true when the model
    /// finished downloading, false on a reported error. Callers own <see cref="IsOllamaBusy"/> and
    /// any follow-up (model list refresh, selection).
    /// </summary>
    private async Task<bool> StreamPullModelAsync(string name, Uri endpoint, Action<AiFeedback> report, CancellationToken ct = default)
    {
        report(AiFeedback.Informational("Pulling model", $"Pulling '{name}'…"));

        // Pulls can take minutes for multi-GB models, so use a dedicated client with no timeout
        // and stream the NDJSON progress rather than the shared 20s HttpClient.
        using var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "api/pull"))
        {
            Content = JsonContent.Create(new { model = name, stream = true }),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            var ex = AiProviderException.FromHttpFailure(AiProviderKind.Ollama, "Pull model", response.StatusCode, endpoint.ToString(), name, err);
            report(AiErrorClassifier.Classify(ex, ProviderContext("Pull model", endpoint.ToString(), name)));
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                {
                    report(AiFeedback.Error("Pull failed", error.GetString() ?? "Unknown error."));
                    return false;
                }

                var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? string.Empty
                    : string.Empty;
                if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (root.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt64(out var total) && total > 0
                    && root.TryGetProperty("completed", out var compEl) && compEl.TryGetInt64(out var completed))
                {
                    var pct = Math.Clamp(completed * 100.0 / total, 0, 100);
                    report(AiFeedback.Informational("Pulling model", $"Pulling {name}: {status} {pct:0}% ({FormatBytes(completed)} / {FormatBytes(total)})"));
                }
                else if (!string.IsNullOrWhiteSpace(status))
                {
                    report(AiFeedback.Informational("Pulling model", $"Pulling {name}: {status}"));
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON progress lines.
            }
        }

        report(AiFeedback.Error("Pull incomplete", "The server closed the progress stream without confirming success. Refresh installed models before retrying."));
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand), IncludeCancelCommand = true)]
    private async Task SetUpLocalAiAsync(CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _localAiSetupGeneration);
        var isAcceptingProgress = true;
        try
        {
            IsOllamaBusy = true;

            // Setup is always ownership-verified and local, independent of provider configuration.
            var endpoint = new Uri($"http://127.0.0.1:{_localAi.HostPort}/", UriKind.Absolute);
            var progress = new Progress<string>(msg =>
            {
                // Progress<T> queues delivery to the UI context. Check ownership here,
                // not at Report(), so a late callback cannot overwrite a terminal result
                // or feedback belonging to a subsequent setup generation.
                if (generation == Volatile.Read(ref _localAiSetupGeneration)
                    && Volatile.Read(ref isAcceptingProgress) && !ct.IsCancellationRequested)
                {
                    LocalAiFeedback = AiFeedback.Informational("Setting up local AI", msg);
                }
            });

            LocalAiFeedback = AiFeedback.Informational("Setting up local AI", "Verifying the app-owned Ollama container…");
            var result = await _localAi.EnsureOllamaContainerAsync(progress, ct);
            Volatile.Write(ref isAcceptingProgress, false);
            if (!result.Success || result.State is not (LocalAiContainerState.AlreadyRunning
                or LocalAiContainerState.StartedExisting or LocalAiContainerState.CreatedWithGpu
                or LocalAiContainerState.CreatedCpuOnly))
            {
                // Failure/cancellation messages include the service's recovery outcome.
                LocalAiFeedback = result.State == LocalAiContainerState.Cancelled
                    ? AiFeedback.Warning("Local AI setup cancelled", result.Message)
                    : AiFeedback.Error("Local AI setup failed", result.Message);
                return;
            }

            ct.ThrowIfCancellationRequested();
            LocalAiFeedback = AiFeedback.Informational("Setting up local AI", "Waiting for the owned Ollama runtime API…");
            if (!await WaitForOllamaReadyAsync(endpoint, TimeSpan.FromSeconds(90), ct))
            {
                LocalAiFeedback = AiFeedback.Error(
                    "Local AI setup failed",
                    "The owned container started but its API did not become ready in time. The container may still be running. Check its logs before retrying or removing it.");
                return;
            }

            LocalAiFeedback = AiFeedback.Informational("Setting up local AI", "Reading installed model metadata…");
            var installedModels = await ReadInstalledOllamaModelsAsync(endpoint, ct);
            ct.ThrowIfCancellationRequested();
            var previousModel = _settings.AiOllamaModel?.Trim();
            var model = installedModels.FirstOrDefault(name => string.Equals(name, previousModel, StringComparison.OrdinalIgnoreCase))
                ?? installedModels.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                ?? string.Empty;

            // A runtime with no model cannot answer anything, so quick start is not finished until a
            // model is present. Offer the small tool-capable default; the user still approves the download.
            if (string.IsNullOrEmpty(model))
            {
                Volatile.Write(ref isAcceptingProgress, false);
                if (await _dialogs.ShowConfirmAsync(
                        "Download a model?",
                        $"The runtime is ready but has no model yet, so it cannot answer anything.\n\n" +
                        $"Download {DefaultOllamaModel}? It is a small chat model that supports tool calling, so the AI assistant can work.\n\n" +
                        "• About 5 GB, downloaded by the Ollama runtime\n" +
                        "• You can pick a different model later under Provider",
                        primaryText: $"Download {DefaultOllamaModel}",
                        closeText: "Skip for now"))
                {
                    _aiCapabilities.Invalidate();
                    if (await StreamPullModelAsync(DefaultOllamaModel, endpoint,
                            fb => LocalAiFeedback = fb, ct))
                    {
                        installedModels = await ReadInstalledOllamaModelsAsync(endpoint, ct);
                        model = installedModels.FirstOrDefault(name =>
                            string.Equals(name, DefaultOllamaModel, StringComparison.OrdinalIgnoreCase))
                            ?? installedModels.FirstOrDefault() ?? string.Empty;
                    }
                    _aiCapabilities.Invalidate();
                }
            }
            ct.ThrowIfCancellationRequested();
            // Do not acquire or warm a model, or infer capabilities from a model name.
            // Model acquisition requires a separate, explicit digest/publication-age audit.
            AiOllamaEndpoint = endpoint.ToString().TrimEnd('/');
            ReplaceOllamaModels(installedModels, model);
            AiOllamaModel = model;
            _suppressProviderModelRefresh = true;
            try
            {
                SelectedAiProviderIndex = (int)AiProviderKind.Ollama;
            }
            finally
            {
                _suppressProviderModelRefresh = false;
            }
            AiFeaturesEnabled = true;
            await RefreshLocalRuntimePresenceAsync(ct);

            var modelMessage = string.IsNullOrEmpty(model)
                ? "No model is installed yet, so the assistant stays hidden. Download one under Provider below to finish."
                : $"Using model '{model}'.";
            LocalAiFeedback = AiFeedback.Success("Ollama is ready",
                string.IsNullOrEmpty(model)
                    ? modelMessage
                    : $"{modelMessage} Checking what this model supports…");

            // Setup is only useful once capabilities are observed: the assistant button stays hidden
            // until tool support is confirmed, so check now instead of leaving the user wondering.
            if (!string.IsNullOrEmpty(model))
            {
                try
                {
                    await _aiAvailability.RefreshAsync(ct);
                    var observation = _aiAvailability.Observation;
                    LocalAiFeedback = observation?.CanUseTools == true
                        ? AiFeedback.Success("Ollama is ready",
                            $"Using model '{model}'. Tool calling is supported, so the AI assistant is available from the toolbar.")
                        : AiFeedback.Warning("Ollama is ready, assistant unavailable",
                            $"Using model '{model}', but tool calling was not confirmed, so the assistant stays hidden. " +
                            "Try a tool-capable model such as qwen2.5:7b, or use Test capabilities for details.");
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "Capability check after local AI setup was unavailable.");
                    LocalAiFeedback = AiFeedback.Success("Ollama is ready",
                        $"Using model '{model}'. Capabilities were not checked; use Test capabilities below.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LocalAiFeedback = AiFeedback.Warning("Local AI setup cancelled",
                "Setup was cancelled. The owned container may still be running and model data may be retained. Check the runtime before retrying or removing local AI. No model download or warm-up was requested.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local AI setup failed.");
            LocalAiFeedback = AiErrorClassifier.Classify(ex, LocalAiContext("Set up local AI"));
        }
        finally
        {
            Volatile.Write(ref isAcceptingProgress, false);
            IsOllamaBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task RemoveLocalAiAsync()
    {
        if (!await _dialogs.ShowConfirmAsync(
                "Remove Ollama",
                $"This removes the app's '{_localAi.ContainerName}' container.\n\n" +
                "Ollama installed outside this app, other containers, and remote endpoints are not touched.",
                primaryText: "Remove",
                closeText: "Cancel"))
        {
            return;
        }

        var removeVolume = await _dialogs.ShowConfirmAsync(
            "Delete downloaded models too?",
            "Downloaded models can be several GB.\n\n" +
            "Delete them to free the space, or keep them so setting up again is fast.",
            primaryText: "Delete models",
            closeText: "Keep models");

        try
        {
            IsOllamaBusy = true;
            LocalAiFeedback = AiFeedback.Informational("Removing local AI", "Removing the local AI container…");
            if (removeVolume)
            {
                _aiCapabilities.Invalidate();
            }
            var result = await _localAi.RemoveOllamaContainerAsync(removeVolume, CancellationToken.None);
            var isRuntimeGone = result.Runtime is LocalRuntimeResourceState.Removed or LocalRuntimeResourceState.Absent;
            var isModelDeletionUnfulfilled = removeVolume
                && result.ModelData is not (LocalRuntimeResourceState.Removed or LocalRuntimeResourceState.Absent);
            if (isRuntimeGone && CurrentAiProvider == AiProviderKind.Ollama)
            {
                // The runtime it pointed at is gone; leaving it selected would fail on every request.
                ReplaceOllamaModels([], string.Empty);
                AiOllamaModel = string.Empty;
                SelectedAiProviderIndex = (int)AiProviderKind.None;
                _settings.AiProvider = AiProviderKind.None;
                _settings.AiOllamaModel = string.Empty;
                _settings.Save();
            }
            LocalAiFeedback = result.Success && isRuntimeGone && !isModelDeletionUnfulfilled
                ? AiFeedback.Success("Ollama removed", result.Message)
                : isRuntimeGone
                    ? AiFeedback.Warning("Ollama removed, models kept", result.Message)
                    : AiFeedback.Error("Could not remove Ollama", result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local AI removal failed.");
            LocalAiFeedback = AiErrorClassifier.Classify(ex, LocalAiContext("Remove local AI"));
        }
        finally
        {
            if (removeVolume)
            {
                _aiCapabilities.Invalidate();
            }
            // Refresh metadata/affordances only; a failed observation must not rewrite the removal outcome.
            try
            {
                await RefreshLocalRuntimePresenceAsync();
                await _aiAvailability.RefreshAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                _logger.LogDebug("Capability refresh after runtime removal was unavailable ({FailureType}).", ex.GetType().Name);
            }
            IsOllamaBusy = false;
        }
    }

    private async Task<bool> IsOllamaHealthyAsync(Uri endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(endpoint, "api/version"), ct);
            if (!response.IsSuccessStatusCode)
                return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug("Owned Ollama runtime API is not ready ({FailureType}).", ex.GetType().Name);
            return false;
        }
    }

    private async Task<bool> WaitForOllamaReadyAsync(Uri endpoint, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (await IsOllamaHealthyAsync(endpoint, deadline.Token))
                    return true;
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<IReadOnlyCollection<string>> ReadInstalledOllamaModelsAsync(Uri endpoint, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(endpoint, "api/tags"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw AiProviderException.FromHttpFailure(AiProviderKind.Ollama, "Read installed Ollama models",
                response.StatusCode, endpoint.ToString(), null, body);
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The endpoint did not return an installed-model inventory.");
        }

        var names = new List<string>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw new JsonException("Installed-model inventory contains an incomplete entry.");
            names.Add(name.GetString()!);
        }

        return names;
    }

    private void ReplaceOllamaModels(IReadOnlyCollection<string> names, string persisted)
    {
        _suppressAiOllamaModelWrite = true;
        try
        {
            OllamaModels.Clear();
            foreach (var name in names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                OllamaModels.Add(new AiModelOption(name, name));
            }

            if (!string.IsNullOrWhiteSpace(persisted)
                && !OllamaModels.Any(m => string.Equals(m.Id, persisted, StringComparison.OrdinalIgnoreCase)))
            {
                OllamaModels.Add(new AiModelOption(persisted, $"{persisted} (not installed)"));
            }
        }
        finally
        {
            _suppressAiOllamaModelWrite = false;
        }

        if (!string.IsNullOrWhiteSpace(persisted))
        {
            _suppressAiOllamaModelWrite = true;
            AiOllamaModel = string.Empty;
            _suppressAiOllamaModelWrite = false;
            AiOllamaModel = persisted;
        }
    }

    private static Uri NormalizeOllamaEndpoint(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "http://localhost:11434" : value.Trim();
        return new Uri(text.EndsWith('/') ? text : text + "/", UriKind.Absolute);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    public async Task LoadVersionAsync()
    {
        try
        {
            var result = await _wslc.GetVersionAsync();
            EngineVersion = result.Success ? result.StandardOutput.Trim() : "Unreachable";
        }
        catch
        {
            EngineVersion = "Unreachable";
        }
    }

    public void LoadAiSecretState() => LoadStoredAiSecretIndicator();
}

public sealed record AiModelOption(string Id, string DisplayName);
