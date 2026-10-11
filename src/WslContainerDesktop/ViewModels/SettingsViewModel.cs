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

/// <summary>Backs the Settings page, mirroring persisted options into bindable properties and exposing commands for engine, startup, theme, notification and AI-provider setup.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ITextLocalizer _localizer;
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

    /// <summary>Bindable state for wslc path used by the view.</summary>
    [ObservableProperty]
    private string _wslcPath;

    /// <summary>Bindable state for refresh interval seconds used by the view.</summary>
    [ObservableProperty]
    private int _refreshIntervalSeconds;

    /// <summary>Bindable state for close to tray used by the view.</summary>
    [ObservableProperty]
    private bool _closeToTray;

    /// <summary>Bindable state for the "Animations" switch used by the view.</summary>
    [ObservableProperty]
    private bool _pageAnimations;

    /// <summary>Bindable state for start minimized used by the view.</summary>
    [ObservableProperty]
    private bool _startMinimized;

    /// <summary>Bindable state for restart running containers on launch used by the view.</summary>
    [ObservableProperty]
    private bool _restartRunningContainersOnLaunch;

    /// <summary>Bindable state for run at login used by the view.</summary>
    [ObservableProperty]
    private bool _runAtLogin;

    /// <summary>Bindable state for run at login enabled used by the view.</summary>
    [ObservableProperty]
    private bool _runAtLoginEnabled = true;

    /// <summary>Bindable state for run at login note used by the view.</summary>
    [ObservableProperty]
    private string _runAtLoginNote =
        UiText.Get("Common_Text0000", "Automatically launch WSL Container Desktop when you sign in to Windows.");

    /// <summary>Bindable state for selected theme index used by the view.</summary>
    private int _selectedThemeIndex;

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (value < 0 || value >= ThemeOptions.Count || !SetProperty(ref _selectedThemeIndex, value)) return;
            OnSelectedThemeIndexChanged(value);
        }
    }

    /// <summary>Bindable state for the selected UI language index used by the view.</summary>
    private int _selectedLanguageIndex;

    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set
        {
            if (value < 0 || value >= LanguageOptions.Count || !SetProperty(ref _selectedLanguageIndex, value)) return;
            OnSelectedLanguageIndexChanged(value);
        }
    }

    /// <summary>Bindable state for notifications enabled used by the view.</summary>
    [ObservableProperty]
    private bool _notificationsEnabled;

    /// <summary>Bindable state for notify image events used by the view.</summary>
    [ObservableProperty]
    private bool _notifyImageEvents;

    /// <summary>Bindable state for notify container events used by the view.</summary>
    [ObservableProperty]
    private bool _notifyContainerEvents;

    /// <summary>Bindable state for notify engine events used by the view.</summary>
    [ObservableProperty]
    private bool _notifyEngineEvents;

    /// <summary>Bindable state for check for updates on launch used by the view.</summary>
    [ObservableProperty]
    private bool _checkForUpdatesOnLaunch;

    /// <summary>Bindable state for dev container npm registry used by the view.</summary>
    [ObservableProperty]
    private string _devContainerNpmRegistry = string.Empty;

    /// <summary>Whether ollama runtime present for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoveOllama))]
    [NotifyPropertyChangedFor(nameof(ShowLocalSetupButtons))]
    [NotifyPropertyChangedFor(nameof(CanQuickStartOllama))]
    [NotifyPropertyChangedFor(nameof(QuickStartHint))]
    private bool _isOllamaRuntimePresent;

    /// <summary>Bindable state for ai features enabled used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProviderDetail))]
    private bool _aiFeaturesEnabled;

    /// <summary>Bindable state for selected ai provider index used by the view.</summary>
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

    /// <summary>Bindable state for ai ollama endpoint used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProviderDetail))]
    private string _aiOllamaEndpoint = string.Empty;

    /// <summary>Bindable state for ai ollama model used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProviderDetail))]
    private string _aiOllamaModel = string.Empty;

    /// <summary>Bindable state for ollama pull model used by the view.</summary>
    [ObservableProperty]
    private string _ollamaPullModel = string.Empty;

    /// <summary>Whether ollama busy for view binding.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOllamaModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshOpenAiModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullOllamaModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetUpLocalAiCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveLocalAiCommand))]
    [NotifyPropertyChangedFor(nameof(CanQuickStartOllama))]
    private bool _isOllamaBusy;

    /// <summary>Bindable state for ai azure open ai endpoint used by the view.</summary>
    [ObservableProperty]
    private string _aiAzureOpenAiEndpoint = string.Empty;

    /// <summary>Bindable state for ai azure open ai deployment used by the view.</summary>
    [ObservableProperty]
    private string _aiAzureOpenAiDeployment = string.Empty;

    /// <summary>Bindable state for ai open ai endpoint used by the view.</summary>
    [ObservableProperty]
    private string _aiOpenAiEndpoint = string.Empty;

    /// <summary>Bindable state for ai open ai model used by the view.</summary>
    [ObservableProperty]
    private string _aiOpenAiModel = string.Empty;

    /// <summary>Bindable state for ai git hub copilot model used by the view.</summary>
    [ObservableProperty]
    private string _aiGitHubCopilotModel = string.Empty;

    /// <summary>Bindable state for ai api key used by the view.</summary>
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
        UiText.Get("Common_Text0001", "AI features are off"),
        UiText.Get("Common_Text0002", "AI features are off by default. Enable them and review the payload preview before sending diagnostics."));

    /// <summary>Bindable state for engine version used by the view.</summary>
    [ObservableProperty]
    private string _engineVersion = UiText.Get("Common_Text0003", "Unknown");

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Bindable state for app version used by the view.</summary>
    public string AppVersion => ResolveAppVersion();

    /// <summary>The app's GitHub repository page, shown in About.</summary>
    public Uri RepositoryUri { get; } = new($"https://github.com/{AppConstants.UpdateRepository}");

    /// <summary>New-issue page of the app's GitHub repository, shown in About.</summary>
    public Uri NewIssueUri { get; } = new($"https://github.com/{AppConstants.UpdateRepository}/issues/new");

    /// <summary>Bindable state for show ai provider settings used by the view.</summary>
    public bool ShowAiProviderSettings => CurrentAiProvider != AiProviderKind.None;

    /// <summary>Bindable state for show git hub copilot settings used by the view.</summary>
    public bool ShowGitHubCopilotSettings => CurrentAiProvider == AiProviderKind.GitHubCopilot;

    /// <summary>Bindable state for show ollama settings used by the view.</summary>
    public bool ShowOllamaSettings => CurrentAiProvider == AiProviderKind.Ollama;

    /// <summary>Bindable state for show azure open ai settings used by the view.</summary>
    public bool ShowAzureOpenAiSettings => CurrentAiProvider == AiProviderKind.AzureOpenAi;

    /// <summary>Bindable state for show open ai settings used by the view.</summary>
    public bool ShowOpenAiSettings => CurrentAiProvider == AiProviderKind.OpenAi;

    /// <summary>Bindable state for show foundry local settings used by the view.</summary>
    public bool ShowFoundryLocalSettings => CurrentAiProvider == AiProviderKind.FoundryLocal;
    /// <summary>Bindable state for foundry local used by the view.</summary>
    public FoundryLocalSettingsViewModel FoundryLocal { get; }

    /// <summary>Bindable state for show ai secret settings used by the view.</summary>
    public bool ShowAiSecretSettings => CurrentAiProvider is AiProviderKind.AzureOpenAi or AiProviderKind.OpenAi;

    private AiProviderKind CurrentAiProvider => Enum.IsDefined(typeof(AiProviderKind), SelectedAiProviderIndex)
        ? (AiProviderKind)SelectedAiProviderIndex
        : AiProviderKind.None;

    /// <summary>Quick start is driven by what is installed, not by the Provider list, which is for
    /// manual configuration. Foundry Local is implemented but hidden from the UI for now.</summary>
    public bool ShowLocalSetupButtons => !IsOllamaRuntimePresent;

    /// <summary>Whether the user can quick start ollama from the view.</summary>
    public bool CanQuickStartOllama => ShowLocalSetupButtons && !IsOllamaBusy;

    /// <summary>Removal is offered only for the local runtime that is actually installed.</summary>
    public bool ShowRemoveOllama => IsOllamaRuntimePresent;

    /// <summary>Bindable state for quick start hint used by the view.</summary>
    public string QuickStartHint => IsOllamaRuntimePresent
        ? UiText.Get("Common_Text0004", "Ollama is set up and selected as your provider.")
        : UiText.Get("Common_Text0005", "Run a model on this machine, or use Provider below to configure a service yourself.");

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
        AiProviderKind.Ollama => UiText.Get("Common_Text0007", "Ollama (local container)"),
        AiProviderKind.AzureOpenAi => "Azure OpenAI",
        AiProviderKind.OpenAi => UiText.Get("Common_OpenAiCompatible", "OpenAI-compatible"),
        AiProviderKind.FoundryLocal => UiText.Get("Common_Text0009", "Foundry Local (on Windows)"),
        _ => UiText.Get("Common_Text0010", "None selected"),
    };

    /// <summary>Endpoint/model detail for the active provider so the page cannot be misread.</summary>
    public string ActiveProviderDetail
    {
        get
        {
            if (!AiFeaturesEnabled)
                return UiText.Get("Common_Text0011", "AI features are off. Turn them on to use a provider.");
            return CurrentAiProvider switch
            {
                AiProviderKind.None => UiText.Get("Common_Text0012", "Choose a provider below, or use Quick start to run a model on this machine."),
                AiProviderKind.GitHubCopilot => Describe(UiText.Get("Common_Text0013", "Model"), AiGitHubCopilotModel),
                AiProviderKind.Ollama => $"{Describe("Model", AiOllamaModel)} · {Describe("Endpoint", AiOllamaEndpoint)}",
                AiProviderKind.AzureOpenAi => $"{Describe("Deployment", AiAzureOpenAiDeployment)} · {Describe("Endpoint", AiAzureOpenAiEndpoint)}",
                AiProviderKind.OpenAi => $"{Describe("Model", AiOpenAiModel)} · {Describe("Endpoint", AiOpenAiEndpoint)}",
                AiProviderKind.FoundryLocal => $"{Describe("Model", FoundryLocal.Model)} · {Describe("Endpoint", FoundryLocal.Endpoint)}",
                _ => string.Empty,
            };
            static string Describe(string label, string value) =>
                $"{UiText.Translate(label)}: {(string.IsNullOrWhiteSpace(value) ? UiText.Get("Common_NotSet", "not set") : value.Trim())}";
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

    /// <summary>Refreshes active provider summary state for the view model.</summary>
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

    /// <summary>Command handler for dismiss local ai feedback actions triggered from the view.</summary>
    [RelayCommand]
    private void DismissLocalAiFeedback() => LocalAiFeedback = AiFeedback.None;

    /// <summary>Command handler for dismiss provider feedback actions triggered from the view.</summary>
    [RelayCommand]
    private void DismissProviderFeedback() => ProviderFeedback = AiFeedback.None;

    /// <summary>Command handler for copy local ai feedback details actions triggered from the view.</summary>
    [RelayCommand]
    private void CopyLocalAiFeedbackDetails() => CopyFeedbackDetails(LocalAiFeedback);

    /// <summary>Command handler for copy provider feedback details actions triggered from the view.</summary>
    [RelayCommand]
    private void CopyProviderFeedbackDetails() => CopyFeedbackDetails(ProviderFeedback);

    /// <summary>Helper for the copy feedback details workflow in this view model.</summary>
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
            return UiText.Get("Common_Text0014", "Version {0}.{1}.{2}", v.Major, v.Minor, v.Build);
        }
        catch
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? UiText.Get("Common_Text0015", "Version 1.0.0") : UiText.Get("Common_Text0014", "Version {0}.{1}.{2}", v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>Bindable state for git hub copilot models used by the view.</summary>
    public ObservableCollection<AiModelOption> GitHubCopilotModels { get; } = new() { new AiModelOption("auto", "auto") };

    /// <summary>Bindable state for ollama models used by the view.</summary>
    public ObservableCollection<AiModelOption> OllamaModels { get; } = new();

    /// <summary>
    /// Model ids advertised by the configured OpenAI-compatible endpoint (<c>GET /models</c>).
    /// Bound to an editable ComboBox, so a model that the server does not list can still be typed.
    /// </summary>
    public ObservableCollection<string> OpenAiModels { get; } = new();

    /// <summary>Bindable state for assistant tool permissions used by the view.</summary>
    public ObservableCollection<AssistantToolPermissionGroup> AssistantToolPermissions { get; }

    /// <summary>
    /// Per-tool switches are meaningless while every action is auto-approved, so they grey out
    /// rather than appearing to still govern anything.
    /// </summary>
    public bool AssistantPerToolPermissionsEnabled => !AssistantApproveEverything;

    /// <summary>
    /// Waives every assistant approval prompt. Turning it on asks once, because the setting
    /// persists and its whole purpose is that nothing will ask again.
    /// </summary>
    public bool AssistantApproveEverything
    {
        get => _settings.AiAssistantApproveEverything;
        set
        {
            if (_settings.AiAssistantApproveEverything == value)
            {
                return;
            }

            if (!value)
            {
                Apply(false);
                return;
            }

            // Re-assert the current (off) state until the user confirms, so a stray tap cannot
            // leave approvals disabled. The dialog is async; the setter is not.
            OnPropertyChanged();
            Helpers.UiSafe.Run(async () =>
            {
                var confirmed = await _dialogs.ShowConfirmAsync(
                    UiText.Get("Common_Text0016", "Let the assistant act without asking?"),
                    UiText.Get("Common_Text0017", "The assistant will carry out every action immediately — including removing containers and ")
                        + UiText.Get("Common_Text0018", "volumes, and deploying Compose stacks it wrote itself.\n\n")
                        + UiText.Get("Common_Text0019", "You will not be asked again until you turn this back off. Actions still appear in ")
                        + UiText.Get("Common_Text0020", "Activity, and plans the app cannot apply safely are still refused."),
                    UiText.Get("Common_Text0021", "Don't ask me again"),
                    UiText.Get("Common_Text0022", "Keep asking"));
                if (confirmed)
                {
                    Apply(true);
                }
            });

            void Apply(bool enabled)
            {
                _settings.AiAssistantApproveEverything = enabled;
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(AssistantPerToolPermissionsEnabled));
                RefreshAssistantToolPermissionState();
            }
        }
    }

    /// <summary>
    /// Grants the assistant the ability to destroy things at all. Separate from the auto-approve
    /// switches below it, which only decide whether an action it is already allowed to take needs a
    /// prompt. Turning it on asks once.
    /// </summary>
    public bool AssistantAllowDestructive
    {
        get => _settings.AiAssistantAllowDestructive;
        set
        {
            if (_settings.AiAssistantAllowDestructive == value)
            {
                return;
            }

            if (!value)
            {
                Apply(false);
                return;
            }

            OnPropertyChanged();
            Helpers.UiSafe.Run(async () =>
            {
                var confirmed = await _dialogs.ShowConfirmAsync(
                    UiText.Get("Common_Text0023", "Allow the assistant to delete things?"),
                    UiText.Get("Common_Text0024", "The assistant will be able to remove containers, volumes and networks, and delete Kubernetes ")
                        + UiText.Get("Common_Text0025", "resources. Removing a volume destroys the data in it, and none of this can be undone by ")
                        + UiText.Get("Common_Text0026", "the app.\n\n")
                        + UiText.Get("Common_Text0027", "It will still ask before each one unless you also auto-approve that action below."),
                    UiText.Get("Common_Text0028", "Allow deleting"),
                    UiText.Get("Common_Text0029", "Keep it off"));
                if (confirmed)
                {
                    Apply(true);
                }
            });

            void Apply(bool enabled)
            {
                _settings.AiAssistantAllowDestructive = enabled;
                _settings.Save();
                OnPropertyChanged();
                RefreshAssistantToolPermissionState();
            }
        }
    }

    /// <summary>Keeps each row's enabled state in step with the two capability switches above it.</summary>
    private void RefreshAssistantToolPermissionState()
    {
        foreach (var group in AssistantToolPermissions)
        {
            foreach (var tool in group.Tools)
            {
                tool.IsEnabled = !AssistantApproveEverything
                    && (!tool.IsDestructive || AssistantAllowDestructive);
            }
        }
    }

    /// <summary>Helper for the build assistant tool permissions workflow in this view model.</summary>
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
                    (name, autoApprove) => _settings.SetAssistantToolAutoApproved(name, autoApprove))
                {
                    IsDestructive = tool.IsDestructive,
                    IsEnabled = !_settings.AiAssistantApproveEverything
                        && (!tool.IsDestructive || _settings.AiAssistantAllowDestructive),
                })
                .ToList();
            groups.Add(new AssistantToolPermissionGroup { Header = UiText.Translate(group.Header), Tools = tools });
        }

        return groups;
    }

    /// <summary>Creates the Settings view model and stores its injected services.</summary>
    public SettingsViewModel(ISettingsService settings, ITextLocalizer localizer, IWslcService wslc, DialogService dialogs, StartupService startup, FileLoggerProvider fileLogger, IAiDiagnosticsService aiDiagnostics, IAiCredentialStore aiCredentials, ILocalAiSetupService localAi, IAiAvailabilityService aiAvailability, IAiCapabilityService aiCapabilities, HttpClient http, ILogger<SettingsViewModel> logger, FoundryLocalSettingsViewModel foundryLocal)
    {
        FoundryLocal = foundryLocal;
        _settings = settings;
        _localizer = localizer;
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
        UiText.LanguageChanged += (_, _) => RefreshCommonLocalizedText();

        _wslcPath = settings.WslcPath;
        _refreshIntervalSeconds = settings.RefreshIntervalSeconds;
        _closeToTray = settings.CloseToTray;
        _pageAnimations = settings.PageAnimations;
        _startMinimized = settings.StartMinimized;
        _restartRunningContainersOnLaunch = settings.RestartRunningContainersOnLaunch;
        _notificationsEnabled = settings.NotificationsEnabled;
        _notifyImageEvents = settings.NotifyImageEvents;
        _notifyContainerEvents = settings.NotifyContainerEvents;
        _notifyEngineEvents = settings.NotifyEngineEvents;
        _checkForUpdatesOnLaunch = settings.CheckForUpdatesOnLaunch;
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

        RefreshLocalizedOptions();

        // Seed the backing fields directly: assigning the observable properties here would run their
        // change handlers, persisting a value the user never chose.
        _selectedLanguageIndex = AppLanguage.IndexOf(settings.Language);
        UiText.LanguageChanged += (_, _) => RefreshLocalizedOptions();
    }

    /// <summary>Handles wslc path changed changes and updates related view-model state.</summary>
    partial void OnWslcPathChanged(string value)
    {
        _settings.WslcPath = value;
        _settings.Save();
    }

    /// <summary>Handles refresh interval seconds changed changes and updates related view-model state.</summary>
    partial void OnRefreshIntervalSecondsChanged(int value)
    {
        _settings.RefreshIntervalSeconds = Math.Clamp(value, AppConstants.RefreshIntervalMinSeconds, AppConstants.RefreshIntervalMaxSeconds);
        _settings.Save();
    }

    /// <summary>Handles close to tray changed changes and updates related view-model state.</summary>
    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        _settings.Save();
    }

    /// <summary>Persists the animations switch; the shell applies it live via the settings Changed event.</summary>
    partial void OnPageAnimationsChanged(bool value)
    {
        _settings.PageAnimations = value;
        _settings.Save();
    }

    /// <summary>Handles start minimized changed changes and updates related view-model state.</summary>
    partial void OnStartMinimizedChanged(bool value)
    {
        _settings.StartMinimized = value;
        _settings.Save();
    }

    /// <summary>Handles restart running containers on launch changed changes and updates related view-model state.</summary>
    partial void OnRestartRunningContainersOnLaunchChanged(bool value)
    {
        _settings.RestartRunningContainersOnLaunch = value;
        _settings.Save();
    }

    /// <summary>Handles notifications enabled changed changes and updates related view-model state.</summary>
    partial void OnNotificationsEnabledChanged(bool value)
    {
        _settings.NotificationsEnabled = value;
        _settings.Save();
    }

    /// <summary>Handles notify image events changed changes and updates related view-model state.</summary>
    partial void OnNotifyImageEventsChanged(bool value)
    {
        _settings.NotifyImageEvents = value;
        _settings.Save();
    }

    /// <summary>Handles notify container events changed changes and updates related view-model state.</summary>
    partial void OnNotifyContainerEventsChanged(bool value)
    {
        _settings.NotifyContainerEvents = value;
        _settings.Save();
    }

    /// <summary>Handles notify engine events changed changes and updates related view-model state.</summary>
    partial void OnNotifyEngineEventsChanged(bool value)
    {
        _settings.NotifyEngineEvents = value;
        _settings.Save();
    }

    /// <summary>Handles check for updates on launch changed changes and updates related view-model state.</summary>
    partial void OnCheckForUpdatesOnLaunchChanged(bool value)
    {
        _settings.CheckForUpdatesOnLaunch = value;
        _settings.Save();
    }

    /// <summary>Handles dev container npm registry changed changes and updates related view-model state.</summary>
    partial void OnDevContainerNpmRegistryChanged(string value)
    {
        _settings.DevContainerNpmRegistry = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settings.Save();
    }

    /// <summary>Handles ai features enabled changed changes and updates related view-model state.</summary>
    partial void OnAiFeaturesEnabledChanged(bool value)
    {
        _settings.AiFeaturesEnabled = value;
        _settings.Save();
    }

    /// <summary>Handles selected ai provider index changed changes and updates related view-model state.</summary>
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

    /// <summary>Handles ai ollama endpoint changed changes and updates related view-model state.</summary>
    partial void OnAiOllamaEndpointChanged(string value)
    {
        _settings.AiOllamaEndpoint = value;
        _settings.Save();
    }

    /// <summary>Handles ai ollama model changed changes and updates related view-model state.</summary>
    partial void OnAiOllamaModelChanged(string value)
    {
        if (_suppressAiOllamaModelWrite)
        {
            return;
        }

        _settings.AiOllamaModel = value ?? string.Empty;
        _settings.Save();
    }

    /// <summary>Handles ai azure open ai endpoint changed changes and updates related view-model state.</summary>
    partial void OnAiAzureOpenAiEndpointChanged(string value)
    {
        _settings.AiAzureOpenAiEndpoint = value;
        _settings.Save();
    }

    /// <summary>Handles ai azure open ai deployment changed changes and updates related view-model state.</summary>
    partial void OnAiAzureOpenAiDeploymentChanged(string value)
    {
        _settings.AiAzureOpenAiDeployment = value;
        _settings.Save();
    }

    /// <summary>Handles ai open ai endpoint changed changes and updates related view-model state.</summary>
    partial void OnAiOpenAiEndpointChanged(string value)
    {
        _settings.AiOpenAiEndpoint = value;
        _settings.Save();
    }

    /// <summary>Handles ai open ai model changed changes and updates related view-model state.</summary>
    partial void OnAiOpenAiModelChanged(string value)
    {
        _settings.AiOpenAiModel = value;
        _settings.Save();
    }

    /// <summary>Handles ai git hub copilot model changed changes and updates related view-model state.</summary>
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

    /// <summary>Provides the save ai api key operation to views or collaborating view models.</summary>
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
            UiText.Get("Common_Text0030", "Credential saved"),
            UiText.Get("Common_Text0031", "Saved {0} credential in Windows Credential Manager.", _settings.AiProvider.DisplayName()));
    }

    /// <summary>Refreshes load stored ai secret indicator state for the view model.</summary>
    private void LoadStoredAiSecretIndicator()
    {
        AiApiKey = string.Empty;
        if (_settings.AiProvider == AiProviderKind.FoundryLocal)
        {
            ProviderFeedback = AiFeedback.Informational("Foundry Local",
                UiText.Get("Common_Text0033", "Uses only your explicit loopback URL and actual model ID. No API key, cloud fallback, WSLC engine or Ollama container is used for inference."));
            return;
        }
        if (_settings.AiProvider == AiProviderKind.GitHubCopilot)
        {
            ProviderFeedback = AiFeedback.Informational(
                "GitHub Copilot",
                UiText.Get("Common_Text0034", "Uses your logged-in Copilot CLI account. No API key is needed."));
            return;
        }

        if (_settings.AiProvider is AiProviderKind.None or AiProviderKind.Ollama)
        {
            ProviderFeedback = _settings.AiProvider == AiProviderKind.Ollama
                ? AiFeedback.Informational("Ollama", UiText.Get("Common_Text0035", "Uses the configured local endpoint and model."))
                : AiFeedback.Informational(UiText.Get("Common_Text0036", "No provider selected"), UiText.Get("Common_Text0037", "Choose an AI provider to configure diagnostics."));
            return;
        }

        if (_settings.AiProvider == AiProviderKind.OpenAi)
        {
            var endpoint = string.IsNullOrWhiteSpace(_settings.AiOpenAiEndpoint)
                ? OpenAiProvider.DefaultEndpoint
                : _settings.AiOpenAiEndpoint.Trim();
            ProviderFeedback = _aiCredentials.TryReadSecret(AiProviderKind.OpenAi, out _)
                ? AiFeedback.Informational("OpenAI-compatible", UiText.Get("Common_Text0038", "Using {0} with a saved API key.", endpoint))
                : AiFeedback.Informational("OpenAI-compatible", UiText.Get("Common_Text0039", "Using {0} with no API key. That is fine for local servers; hosted services such as OpenAI need one.", endpoint));
            return;
        }

        ProviderFeedback = _aiCredentials.TryReadSecret(_settings.AiProvider, out _)
            ? AiFeedback.Informational(_settings.AiProvider.DisplayName(), UiText.Get("Common_Text0040", "{0} has a saved credential.", _settings.AiProvider.DisplayName()))
            : AiFeedback.Warning(_settings.AiProvider.DisplayName(), UiText.Get("Common_Text0041", "No credential is saved for the selected provider."));
    }

    /// <summary>Handles run at login changed changes and updates related view-model state.</summary>
    partial void OnRunAtLoginChanged(bool value)
    {
        if (_suppressStartupWrite)
        {
            return;
        }

        _ = ApplyRunAtLoginAsync(value);
    }

    /// <summary>Applies apply run at login state to bindable properties.</summary>
    private async Task ApplyRunAtLoginAsync(bool value)
    {
        var result = await _startup.SetEnabledAsync(value);
        switch (result)
        {
            case StartupToggleResult.Applied:
                RunAtLoginNote = value
                    ? UiText.Get("Common_Text0042", "WSL Container Desktop will launch when you sign in to Windows.")
                    : UiText.Get("Common_Text0000", "Automatically launch WSL Container Desktop when you sign in to Windows.");
                break;

            case StartupToggleResult.BlockedByUser:
                // The user disabled startup in Task Manager; only they can change it there.
                SetRunAtLoginSilently(false);
                RunAtLoginEnabled = false;
                RunAtLoginNote =
                    UiText.Get("Common_Text0043", "Startup is turned off for this app in Windows Task Manager (Startup apps). ") +
                    UiText.Get("Common_Text0044", "Re-enable it there to allow launching at sign-in.");
                await _dialogs.ShowMessageAsync(UiText.Get("Common_Text0045", "Managed by Windows"),
                    UiText.Get("Common_Text0046", "This app's startup is controlled in Task Manager → Startup apps. ") +
                    UiText.Get("Common_Text0047", "Please enable it there."));
                break;

            case StartupToggleResult.BlockedByPolicy:
                SetRunAtLoginSilently(!value);
                RunAtLoginEnabled = false;
                RunAtLoginNote = UiText.Get("Common_Text0048", "This setting is managed by your organization's policy.");
                break;

            case StartupToggleResult.Unavailable:
                SetRunAtLoginSilently(!value);
                RunAtLoginNote = UiText.Get("Common_Text0049", "Run at sign-in isn't available for this installation.");
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
                UiText.Get("Common_Text0050", "Startup for this app is turned off in Windows Task Manager (Startup apps). ") +
                UiText.Get("Common_Text0051", "Enable it there to allow launching at sign-in.");
        }
        else
        {
            RunAtLoginNote = enabled
                ? UiText.Get("Common_Text0042", "WSL Container Desktop will launch when you sign in to Windows.")
                : UiText.Get("Common_Text0000", "Automatically launch WSL Container Desktop when you sign in to Windows.");
        }
    }

    /// <summary>Helper for the set run at login silently workflow in this view model.</summary>
    private void SetRunAtLoginSilently(bool value)
    {
        _suppressStartupWrite = true;
        RunAtLogin = value;
        _suppressStartupWrite = false;
    }

    /// <summary>Handles selected theme index changed changes and updates related view-model state.</summary>
    private void OnSelectedThemeIndexChanged(int value)
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

    /// <summary>Localized theme names, in the order <see cref="SelectedThemeIndex"/> expects.</summary>
    public ObservableCollection<LocalizedOption> ThemeOptions { get; } = new();

    /// <summary>Localized UI language names, in the order <see cref="SelectedLanguageIndex"/> expects.</summary>
    public ObservableCollection<LocalizedOption> LanguageOptions { get; } = new();

    private void RefreshLocalizedOptions()
    {
        var themes = new[]
        {
            _localizer.Get("Settings_Theme_Option_System"),
            _localizer.Get("Settings_Theme_Option_Light"),
            _localizer.Get("Settings_Theme_Option_Dark"),
        };
        var languages = AppLanguage.Supported
            .Select(option => _localizer.Get(option.ResourceKey))
            .ToArray();
        UpdateOptions(ThemeOptions, themes);
        UpdateOptions(LanguageOptions, languages);
    }

    private static void UpdateOptions(ObservableCollection<LocalizedOption> options, IReadOnlyList<string> translated)
    {
        for (var i = 0; i < translated.Count; i++)
        {
            if (i == options.Count)
                options.Add(new LocalizedOption(i.ToString(System.Globalization.CultureInfo.InvariantCulture), translated[i]));
            else
                options[i].DisplayName = translated[i];
        }
    }

    /// <summary>Handles selected UI language index changed and persists the choice.</summary>
    private void OnSelectedLanguageIndexChanged(int value)
    {
        // A ComboBox can briefly report -1 while its items are replaced. That is not a user choice.
        if (value < 0 || value >= AppLanguage.Supported.Count)
        {
            return;
        }

        var tag = AppLanguage.Supported[value].Tag;

        _settings.Language = tag;
        _settings.Save();

        // The shell refreshes its own labels and navigates to a fresh page for x:Uid resources.
        LanguageChangeRequested?.Invoke(this, tag);

        // Refresh after the shell changes the app-wide resource override. The option collection stays
        // bound across the navigation, so changing its items updates the newly loaded picker.
        RefreshLocalizedOptions();
    }

    /// <summary>Raised after the language choice is persisted, carrying the new BCP-47 tag (empty = system).</summary>
    public event EventHandler<string>? LanguageChangeRequested;

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

    /// <summary>Command handler for test connection actions triggered from the view.</summary>
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
                await _dialogs.ShowMessageAsync(UiText.Get("Common_Text0052", "Connection OK"), UiText.Get("Common_Text0053", "Connected to WSL container engine.\n\n{0}", EngineVersion));
            }
            else
            {
                EngineVersion = UiText.Get("Common_EngineUnreachable", "Unreachable");
                await _dialogs.ShowMessageAsync(UiText.Get("Common_Text0054", "Connection failed"),
                    UiText.Get("Common_Text0055", "Could not reach the WSL container engine using:\n{0}\n\n{1}", _settings.WslcPath, result.ErrorText));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Bindable state for ai capability status used by the view.</summary>
    public string AiCapabilityStatus => UiText.TranslateLines(_aiAvailability.Observation?.StatusText
        ?? UiText.Get("Common_Text0056", "Enable AI and choose a provider. Capability observations are unknown until checked."));

    /// <summary>Single-line headline so the full capability report can stay collapsed.</summary>
    public string AiCapabilityHeadline
    {
        get
        {
            var observation = _aiAvailability.Observation;
            if (observation is null)
                return UiText.Get("Common_Text0057", "AI capabilities: not checked yet");
            var chat = observation.Chat.Support;
            var tools = observation.Tools.Support;
            return chat switch
            {
                AiSupport.Supported when tools == AiSupport.Supported => UiText.Get("Common_Text0058", "AI capabilities: chat and tools ready"),
                AiSupport.Supported => UiText.Get("Common_Text0059", "AI capabilities: chat ready, tools unconfirmed"),
                AiSupport.Unsupported => UiText.Get("Common_Text0060", "AI capabilities: provider not usable — see details"),
                _ => UiText.Get("Common_Text0061", "AI capabilities: unconfirmed — see details"),
            };
        }
    }

    /// <summary>Command handler for test ai provider actions triggered from the view.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestAiProviderAsync(CancellationToken ct)
    {
        IsBusy = true;
        ProviderFeedback = AiFeedback.Informational(UiText.Get("Common_Text0062", "Testing capabilities"),
            UiText.Get("Common_Text0063", "Checking metadata and bounded synthetic chat/tool/JSON requests. Cold starts may take up to 90 seconds. No app actions or downloads run; cancel at any time."));
        try
        {
            var result = await _aiDiagnostics.TestProviderAsync(ct);
            await _aiAvailability.RefreshAsync(ct);
            ProviderFeedback = AiFeedback.Informational(UiText.Get("Common_Text0064", "Capability observations"), result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProviderFeedback = AiFeedback.Informational(UiText.Get("Common_Text0065", "Test cancelled"),
                UiText.Get("Common_Text0066", "The test stopped. Only completed checks can remain cached. No model download or app action was started."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AI capability test could not complete.");
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext(UiText.Get("Common_Text0067", "Provider test")));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for sign in git hub copilot actions triggered from the view.</summary>
    [RelayCommand]
    private void SignInGitHubCopilot()
    {
        ProviderFeedback = AiFeedback.Informational(
            UiText.Get("Common_Text0068", "GitHub Copilot sign-in"),
            UiText.Get("Common_Text0069", "GitHub Copilot uses your logged-in Copilot CLI account. Run `copilot login` outside the app if you need to sign in."));
    }

    /// <summary>Command handler for refresh git hub copilot models actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RefreshGitHubCopilotModelsAsync() => await LoadGitHubCopilotModelsAsync();

    /// <summary>Refreshes load git hub copilot models state for the view model.</summary>
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
            ProviderFeedback = AiFeedback.Informational(UiText.Get("Common_Text0070", "Loading models"), UiText.Get("Common_Text0071", "Loading GitHub Copilot models…"));
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
                    var suffix = supportsReasoning ? UiText.Get("Common_Text0072", " · reasoning") : string.Empty;
                    return new AiModelOption(model.Id, $"{model.Id} — {model.Name}{suffix}");
                })
                .ToList();

            EnsureOption(options, "auto", "auto");
            EnsureOption(options, persisted, UiText.Get("Common_Text0073", "{0} (configured)", persisted));
            ReplaceGitHubCopilotModels(options);
            ReapplyGitHubCopilotModel(persisted);

            ProviderFeedback = !configuredAvailable
                ? AiFeedback.Warning(UiText.Get("Common_Text0074", "Configured model unavailable"), UiText.Get("Common_Text0075", "Configured model '{0}' is not available. Choose a model from the list.", persisted))
                : AiFeedback.Success(UiText.Get("Common_Text0076", "Models loaded"), UiText.Get("Common_Text0077", "Loaded {0} GitHub Copilot model(s).", GitHubCopilotModels.Count));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load GitHub Copilot models.");
            SeedGitHubCopilotModels(persisted);
            ReapplyGitHubCopilotModel(persisted);
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext(UiText.Get("Common_Text0078", "Refresh GitHub Copilot models")));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Helper for the seed github copilot models workflow in this view model.</summary>
    private void SeedGitHubCopilotModels(string persisted)
    {
        ReplaceGitHubCopilotModels([
            new AiModelOption("auto", "auto"),
            new AiModelOption(persisted, string.Equals(persisted, "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : UiText.Get("Common_Text0073", "{0} (configured)", persisted)),
        ]);
        ReapplyGitHubCopilotModel(persisted);
    }

    /// <summary>Helper for the ensure github copilot model option workflow in this view model.</summary>
    private void EnsureGitHubCopilotModelOption(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || GitHubCopilotModels.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        GitHubCopilotModels.Add(new AiModelOption(id, UiText.Get("Common_Text0073", "{0} (configured)", id)));
    }

    /// <summary>Helper for the reapply github copilot model workflow in this view model.</summary>
    private void ReapplyGitHubCopilotModel(string model)
    {
        var value = string.IsNullOrWhiteSpace(model) ? "auto" : model;
        EnsureGitHubCopilotModelOption(value);
        _suppressAiModelWrite = true;
        AiGitHubCopilotModel = string.Empty;
        _suppressAiModelWrite = false;
        AiGitHubCopilotModel = value;
    }

    /// <summary>Helper for the replace github copilot models workflow in this view model.</summary>
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

    /// <summary>Helper for the ensure option workflow in this view model.</summary>
    private static void EnsureOption(List<AiModelOption> options, string id, string displayName)
    {
        if (!options.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new AiModelOption(id, displayName));
        }
    }

    /// <summary>Returns whether the can run ollama command command can run now.</summary>
    private bool CanRunOllamaCommand() => !IsOllamaBusy;

    /// <summary>Command handler for refresh ollama models actions triggered from the view.</summary>
    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task RefreshOllamaModelsAsync() => await LoadOllamaModelsAsync();

    /// <summary>Refreshes load ollama models state for the view model.</summary>
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
            ProviderFeedback = AiFeedback.Informational(UiText.Get("Common_Text0070", "Loading models"), UiText.Get("Common_Text0079", "Loading installed Ollama models…"));
            endpoint = NormalizeOllamaEndpoint(_settings.AiOllamaEndpoint);
            var names = await ReadInstalledOllamaModelsAsync(endpoint, CancellationToken.None);

            ReplaceOllamaModels(names, persisted);
            ProviderFeedback = names.Count == 0
                ? AiFeedback.Warning(UiText.Get("Common_Text0080", "No models installed"), UiText.Get("Common_Text0081", "No Ollama models are installed. Download one below to use this provider."))
                : AiFeedback.Success(UiText.Get("Common_Text0076", "Models loaded"), UiText.Get("Common_Text0082", "Loaded {0} installed Ollama model(s).", names.Count));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load Ollama models.");
            ReplaceOllamaModels([], persisted);
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext(UiText.Get("Common_Text0083", "Refresh Ollama models"), endpoint?.ToString(), persisted));
        }
        finally
        {
            IsOllamaBusy = false;
        }
    }

    /// <summary>Command handler for refresh open ai models actions triggered from the view.</summary>
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
            ProviderFeedback = AiFeedback.Informational(UiText.Get("Common_Text0070", "Loading models"), UiText.Get("Common_Text0084", "Loading models from the OpenAI-compatible endpoint…"));

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
                throw AiProviderException.FromHttpFailure(AiProviderKind.OpenAi, UiText.Get("Common_Text0085", "Refresh OpenAI-compatible models"), response.StatusCode, endpoint, persisted, body);
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
                ? AiFeedback.Warning(UiText.Get("Common_Text0086", "No models listed"), UiText.Get("Common_Text0087", "The endpoint responded but listed no models. Type the model id your server expects."))
                : AiFeedback.Success(UiText.Get("Common_Text0076", "Models loaded"), UiText.Get("Common_Text0088", "Loaded {0} model(s) from the OpenAI-compatible endpoint.", ids.Count));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load OpenAI-compatible models.");
            ReplaceOpenAiModels([], persisted);
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext(UiText.Get("Common_Text0085", "Refresh OpenAI-compatible models"), endpoint, persisted));
        }
        finally
        {
            IsOllamaBusy = false;
        }
    }

    /// <summary>Helper for the replace openai models workflow in this view model.</summary>
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

    /// <summary>Command handler for pull ollama model actions triggered from the view.</summary>
    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task PullOllamaModelAsync()
    {
        var name = OllamaPullModel?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ProviderFeedback = AiFeedback.Warning(UiText.Get("Common_Text0089", "Model name required"), UiText.Get("Common_Text0090", "Enter a model name to pull (for example qwen2.5:7b)."));
            return;
        }

        Uri? endpoint = null;
        var isPullStarted = false;
        try
        {
            IsOllamaBusy = true;
            endpoint = NormalizeOllamaEndpoint(_settings.AiOllamaEndpoint);
            if (!await _dialogs.ShowConfirmAsync(
                    UiText.Get("Common_Text0091", "Download model"),
                    UiText.Get("Common_Text0092", "Download '{0}' to {1}?\n\n", name, endpoint) +
                    UiText.Get("Common_Text0093", "Models can be several GB and are downloaded by the Ollama runtime."),
                    primaryText: "Download",
                    closeText: UiText.Get("Common_Text0094", "Cancel")))
            {
                ProviderFeedback = AiFeedback.Informational(UiText.Get("Common_Text0095", "Download cancelled"), UiText.Get("Common_Text0096", "No model was downloaded."));
                return;
            }

            isPullStarted = true;
            _aiCapabilities.Invalidate();
            if (await StreamPullModelAsync(name, endpoint, fb => ProviderFeedback = fb))
            {
                OllamaPullModel = string.Empty;
                await LoadOllamaModelsAsync();
                AiOllamaModel = name;
                ProviderFeedback = AiFeedback.Success(UiText.Get("Common_Text0097", "Model pulled"), UiText.Get("Common_Text0098", "Pulled '{0}' and selected it.", name));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama pull failed.");
            ProviderFeedback = AiErrorClassifier.Classify(ex, ProviderContext(UiText.Get("Common_Text0099", "Pull Ollama model"), endpoint?.ToString(), name));
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
    /// <paramref name="report"/> for the explicitly confirmed "Pull a model" action.
    /// Runtime setup never calls this method. Returns true when the model
    /// finished downloading, false on a reported error. Callers own <see cref="IsOllamaBusy"/> and
    /// any follow-up (model list refresh, selection).
    /// </summary>
    private async Task<bool> StreamPullModelAsync(string name, Uri endpoint, Action<AiFeedback> report, CancellationToken ct = default)
    {
        report(AiFeedback.Informational(UiText.Get("Common_Text0100", "Pulling model"), UiText.Get("Common_Text0101", "Pulling '{0}'…", name)));

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
            var ex = AiProviderException.FromHttpFailure(AiProviderKind.Ollama, UiText.Get("Common_Text0102", "Pull model"), response.StatusCode, endpoint.ToString(), name, err);
            report(AiErrorClassifier.Classify(ex, ProviderContext(UiText.Get("Common_Text0102", "Pull model"), endpoint.ToString(), name)));
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
                    report(AiFeedback.Error(UiText.Get("Common_Text0103", "Pull failed"), error.GetString() ?? UiText.Get("Common_Text0104", "Unknown error.")));
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
                    report(AiFeedback.Informational(UiText.Get("Common_Text0100", "Pulling model"), UiText.Get("Common_Text0105", "Pulling {0}: {1} {2:0}% ({3} / {4})", name, status, pct, FormatBytes(completed), FormatBytes(total))));
                }
                else if (!string.IsNullOrWhiteSpace(status))
                {
                    report(AiFeedback.Informational(UiText.Get("Common_Text0100", "Pulling model"), UiText.Get("Common_Text0106", "Pulling {0}: {1}", name, status)));
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON progress lines.
            }
        }

        report(AiFeedback.Error(UiText.Get("Common_Text0107", "Pull incomplete"), UiText.Get("Common_Text0108", "The server closed the progress stream without confirming success. Refresh installed models before retrying.")));
        return false;
    }

    /// <summary>Command handler for set up local ai actions triggered from the view.</summary>
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
                    LocalAiFeedback = AiFeedback.Informational(UiText.Get("Common_Text0109", "Setting up local AI"), msg);
                }
            });

            LocalAiFeedback = AiFeedback.Informational(UiText.Get("Common_Text0109", "Setting up local AI"), UiText.Get("Common_Text0110", "Verifying the app-owned Ollama container…"));
            var result = await _localAi.EnsureOllamaContainerAsync(progress, ct);
            Volatile.Write(ref isAcceptingProgress, false);
            if (!result.Success || result.State is not (LocalAiContainerState.AlreadyRunning
                or LocalAiContainerState.StartedExisting or LocalAiContainerState.CreatedWithGpu
                or LocalAiContainerState.CreatedCpuOnly))
            {
                // Failure/cancellation messages include the service's recovery outcome.
                LocalAiFeedback = result.State == LocalAiContainerState.Cancelled
                    ? AiFeedback.Warning(UiText.Get("Common_Text0111", "Local AI setup cancelled"), LocalizeLocalAiSetupMessage(result))
                    : AiFeedback.Error(UiText.Get("Common_Text0112", "Local AI setup failed"), LocalizeLocalAiSetupMessage(result));
                return;
            }

            ct.ThrowIfCancellationRequested();
            LocalAiFeedback = AiFeedback.Informational(UiText.Get("Common_Text0109", "Setting up local AI"), UiText.Get("Common_Text0113", "Waiting for the owned Ollama runtime API…"));
            if (!await WaitForOllamaReadyAsync(endpoint, TimeSpan.FromSeconds(90), ct))
            {
                LocalAiFeedback = AiFeedback.Error(
                    UiText.Get("Common_Text0112", "Local AI setup failed"),
                    UiText.Get("Common_Text0114", "The owned container started but its API did not become ready in time. The container may still be running. Check its logs before retrying or removing it."));
                return;
            }

            LocalAiFeedback = AiFeedback.Informational(UiText.Get("Common_Text0109", "Setting up local AI"), UiText.Get("Common_Text0115", "Reading installed model metadata…"));
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
                        UiText.Get("Common_Text0116", "Download a model?"),
                        UiText.Get("Common_Text0117", "The runtime is ready but has no model yet, so it cannot answer anything.\n\n") +
                        UiText.Get("Common_Text0118", "Download {0}? It is a small chat model that supports tool calling, so the AI assistant can work.\n\n", DefaultOllamaModel) +
                        UiText.Get("Common_Text0119", "• About 5 GB, downloaded by the Ollama runtime\n") +
                        UiText.Get("Common_Text0120", "• You can pick a different model later under Provider"),
                        primaryText: UiText.Get("Common_Text0121", "Download {0}", DefaultOllamaModel),
                        closeText: UiText.Get("Common_Text0122", "Skip for now")))
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
                ? UiText.Get("Common_Text0123", "No model is installed yet, so the assistant stays hidden. Download one under Provider below to finish.")
                : UiText.Get("Common_Text0124", "Using model '{0}'.", model);
            LocalAiFeedback = AiFeedback.Success(UiText.Get("Common_Text0125", "Ollama is ready"),
                string.IsNullOrEmpty(model)
                    ? modelMessage
                    : UiText.Get("Common_Text0126", "{0} Checking what this model supports…", modelMessage));

            // Setup is only useful once capabilities are observed: the assistant button stays hidden
            // until tool support is confirmed, so check now instead of leaving the user wondering.
            if (!string.IsNullOrEmpty(model))
            {
                try
                {
                    await _aiAvailability.RefreshAsync(ct);
                    var observation = _aiAvailability.Observation;
                    LocalAiFeedback = observation?.CanUseTools == true
                        ? AiFeedback.Success(UiText.Get("Common_Text0125", "Ollama is ready"),
                            UiText.Get("Common_Text0127", "Using model '{0}'. Tool calling is supported, so the AI assistant is available from the toolbar.", model))
                        : AiFeedback.Warning(UiText.Get("Common_Text0128", "Ollama is ready, assistant unavailable"),
                            UiText.Get("Common_Text0129", "Using model '{0}', but tool calling was not confirmed, so the assistant stays hidden. ", model) +
                            UiText.Get("Common_Text0130", "Try a tool-capable model such as qwen2.5:7b, or use Test capabilities for details."));
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "Capability check after local AI setup was unavailable.");
                    LocalAiFeedback = AiFeedback.Success(UiText.Get("Common_Text0125", "Ollama is ready"),
                        UiText.Get("Common_Text0131", "Using model '{0}'. Capabilities were not checked; use Test capabilities below.", model));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LocalAiFeedback = AiFeedback.Warning(UiText.Get("Common_Text0111", "Local AI setup cancelled"),
                UiText.Get("Common_Text0132", "Setup was cancelled. The owned container may still be running and model data may be retained. Check the runtime before retrying or removing local AI. No model download or warm-up was requested."));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local AI setup failed.");
            LocalAiFeedback = AiErrorClassifier.Classify(ex, LocalAiContext(UiText.Get("Common_Text0133", "Set up local AI")));
        }
        finally
        {
            Volatile.Write(ref isAcceptingProgress, false);
            IsOllamaBusy = false;
        }
    }

    /// <summary>Command handler for remove local ai actions triggered from the view.</summary>
    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task RemoveLocalAiAsync()
    {
        if (!await _dialogs.ShowConfirmAsync(
                UiText.Get("Common_Text0134", "Remove Ollama"),
                UiText.Get("Common_Text0135", "This removes the app's '{0}' container.\n\n", _localAi.ContainerName) +
                UiText.Get("Common_Text0136", "Ollama installed outside this app, other containers, and remote endpoints are not touched."),
                primaryText: "Remove",
                closeText: UiText.Get("Common_Text0094", "Cancel")))
        {
            return;
        }

        var removeVolume = await _dialogs.ShowConfirmAsync(
            UiText.Get("Common_Text0137", "Delete downloaded models too?"),
            UiText.Get("Common_Text0138", "Downloaded models can be several GB.\n\n") +
            UiText.Get("Common_Text0139", "Delete them to free the space, or keep them so setting up again is fast."),
            primaryText: UiText.Get("Common_Text0140", "Delete models"),
            closeText: UiText.Get("Common_Text0141", "Keep models"));

        try
        {
            IsOllamaBusy = true;
            LocalAiFeedback = AiFeedback.Informational(UiText.Get("Common_Text0142", "Removing local AI"), UiText.Get("Common_Text0143", "Removing the local AI container…"));
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
                ? AiFeedback.Success(UiText.Get("Common_Text0144", "Ollama removed"), result.Message)
                : isRuntimeGone
                    ? AiFeedback.Warning(UiText.Get("Common_Text0145", "Ollama removed, models kept"), result.Message)
                    : AiFeedback.Error(UiText.Get("Common_Text0146", "Could not remove Ollama"), result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local AI removal failed.");
            LocalAiFeedback = AiErrorClassifier.Classify(ex, LocalAiContext(UiText.Get("Common_Text0147", "Remove local AI")));
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

    /// <summary>Helper for the is ollama healthy workflow in this view model.</summary>
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

    /// <summary>Helper for the wait for ollama ready workflow in this view model.</summary>
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

    /// <summary>Helper for the read installed ollama models workflow in this view model.</summary>
    private async Task<IReadOnlyCollection<string>> ReadInstalledOllamaModelsAsync(Uri endpoint, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(endpoint, "api/tags"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw AiProviderException.FromHttpFailure(AiProviderKind.Ollama, UiText.Get("Common_Text0148", "Read installed Ollama models"),
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

    /// <summary>Helper for the replace ollama models workflow in this view model.</summary>
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
                OllamaModels.Add(new AiModelOption(persisted, UiText.Get("Common_Text0149", "{0} (not installed)", persisted)));
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

    /// <summary>Helper for the normalize ollama endpoint workflow in this view model.</summary>
    private static Uri NormalizeOllamaEndpoint(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "http://localhost:11434" : value.Trim();
        return new Uri(text.EndsWith('/') ? text : text + "/", UriKind.Absolute);
    }

    /// <summary>Helper for the format bytes workflow in this view model.</summary>
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

    /// <summary>Refreshes load version state for the view model.</summary>
    public async Task LoadVersionAsync()
    {
        try
        {
            var result = await _wslc.GetVersionAsync();
            EngineVersion = result.Success ? result.StandardOutput.Trim() : UiText.Get("Common_EngineUnreachable", "Unreachable");
        }
        catch
        {
            EngineVersion = UiText.Get("Common_EngineUnreachable", "Unreachable");
        }
    }

    /// <summary>Refreshes load ai secret state state for the view model.</summary>
    public void LoadAiSecretState() => LoadStoredAiSecretIndicator();

    /// <summary>Projects the setup service's structured recovery message without rewriting raw evidence.</summary>
    private static string LocalizeLocalAiSetupMessage(LocalAiSetupResult result)
    {
        // These delimiters and cleanup outcomes are emitted by LocalAiSetupService itself.
        // Requiring the complete wrapper keeps unrelated diagnostics and user text untouched.
        const string marker = " Model data was not deleted (";
        const string recovery = "Recovery: inspect wslcd-ollama labels and immutable container ID; retry setup only after resolving conflicts.";
        var message = result.Message;
        var markerIndex = message.IndexOf(marker, StringComparison.Ordinal);
        var recoveryIndex = message.IndexOf("). " + recovery, StringComparison.Ordinal);
        if (markerIndex < 0 || recoveryIndex <= markerIndex)
            return UiText.TranslateLines(message);

        string[] cleanupOutcomes =
        [
            "No automatic runtime cleanup was attempted.",
            "No partial runtime was observed; an interrupted creation may still finish. Inspect before retrying.",
            "A different runtime was retained; this operation does not own it.",
            "Only this operation's verified partial runtime was removed.",
            "Partial runtime cleanup could not be confirmed; inspect before retrying.",
        ];
        var prefix = message[..markerIndex];
        var cleanup = cleanupOutcomes.FirstOrDefault(outcome => prefix.EndsWith(" " + outcome, StringComparison.Ordinal));
        if (cleanup is null)
            return UiText.TranslateLines(message);

        var reason = prefix[..(prefix.Length - cleanup.Length - 1)];
        var suffix = message[(recoveryIndex + 3 + recovery.Length)..];
        return UiText.Get("Common_LocalAiSetupRecovery", "{0} {1} Model data was not deleted ({2}). {3}{4}",
            UiText.TranslateLines(reason), UiText.Translate(cleanup),
            UiText.Translate(result.ModelData.ToString()), UiText.Translate(recovery), UiText.Translate(suffix));
    }

    /// <summary>Refreshes public display text while keeping user input and active operations intact.</summary>
    private void RefreshCommonLocalizedText()
    {
        RunAtLoginNote = UiText.Translate(RunAtLoginNote);
        EngineVersion = UiText.Translate(EngineVersion);
        foreach (var model in GitHubCopilotModels.Concat(OllamaModels))
            model.RefreshLanguage();
        ProviderFeedback = LocalizedFeedback(ProviderFeedback);
        LocalAiFeedback = LocalizedFeedback(LocalAiFeedback);
        foreach (var group in AssistantToolPermissions)
            group.RefreshLocalizedText();
        foreach (var property in new[] { nameof(AppVersion), nameof(QuickStartHint), nameof(ActiveProviderName),
            nameof(ActiveProviderDetail), nameof(AiCapabilityStatus), nameof(AiCapabilityHeadline) })
            OnPropertyChanged(property);
    }

    private static AiFeedback LocalizedFeedback(AiFeedback value) => new()
    {
        Severity = value.Severity,
        Title = UiText.Translate(value.Title),
        Message = UiText.TranslateLines(value.Message),
        TechnicalDetails = value.TechnicalDetails,
    };

    partial void OnProviderFeedbackChanged(AiFeedback value)
    {
        var translated = LocalizedFeedback(value);
        if (translated.Title != value.Title || translated.Message != value.Message)
            ProviderFeedback = translated;
    }

    partial void OnLocalAiFeedbackChanged(AiFeedback value)
    {
        var translated = LocalizedFeedback(value);
        if (translated.Title != value.Title || translated.Message != value.Message)
            LocalAiFeedback = translated;
    }
}

/// <summary>Defines the AiModelOption type used by WSL Container Desktop.</summary>
public sealed record AiModelOption(string Id, string DisplayName) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Localizes app-owned annotations while preserving the exact model ID and name.</summary>
    public string LocalizedDisplayName => string.Equals(Id, DisplayName, StringComparison.Ordinal)
        ? DisplayName : UiText.Translate(DisplayName);

    /// <summary>Raised when an app-owned caption changes language.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Updates the caption without replacing the selected model object.</summary>
    public void RefreshLanguage() =>
        PropertyChanged?.Invoke(this, new(nameof(LocalizedDisplayName)));
}
