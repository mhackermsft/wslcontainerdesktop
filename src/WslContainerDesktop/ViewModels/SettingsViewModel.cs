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
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly HttpClient _http;

    private bool _suppressStartupWrite;
    private bool _suppressAiModelWrite;
    private bool _suppressAiOllamaModelWrite;

    [ObservableProperty]
    private string _wslcPath;

    [ObservableProperty]
    private int _refreshIntervalSeconds;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startMinimized;

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
    private bool _aiFeaturesEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiProviderSettings))]
    [NotifyPropertyChangedFor(nameof(ShowGitHubCopilotSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOllamaSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAzureOpenAiSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOpenAiSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAiSecretSettings))]
    private int _selectedAiProviderIndex;

    [ObservableProperty]
    private string _aiOllamaEndpoint = string.Empty;

    [ObservableProperty]
    private string _aiOllamaModel = string.Empty;

    [ObservableProperty]
    private string _ollamaPullModel = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOllamaModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullOllamaModelCommand))]
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

    [ObservableProperty]
    private bool _aiAssistantAutoCreateRun;

    [ObservableProperty]
    private bool _aiAssistantAutoLifecycle;

    [ObservableProperty]
    private bool _aiAssistantAutoComposeTemplate;

    [ObservableProperty]
    private bool _aiAssistantAutoKubernetes;

    [ObservableProperty]
    private string _aiStatus = "AI features are off by default. Enable them and review the payload preview before sending diagnostics.";

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

    public bool ShowAiSecretSettings => CurrentAiProvider is AiProviderKind.AzureOpenAi or AiProviderKind.OpenAi;

    private AiProviderKind CurrentAiProvider => Enum.IsDefined(typeof(AiProviderKind), SelectedAiProviderIndex)
        ? (AiProviderKind)SelectedAiProviderIndex
        : AiProviderKind.None;

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

    public SettingsViewModel(ISettingsService settings, IWslcService wslc, DialogService dialogs, StartupService startup, FileLoggerProvider fileLogger, IAiDiagnosticsService aiDiagnostics, IAiCredentialStore aiCredentials, HttpClient http, ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _wslc = wslc;
        _dialogs = dialogs;
        _startup = startup;
        _fileLogger = fileLogger;
        _aiDiagnostics = aiDiagnostics;
        _aiCredentials = aiCredentials;
        _http = http;
        _logger = logger;

        _wslcPath = settings.WslcPath;
        _refreshIntervalSeconds = settings.RefreshIntervalSeconds;
        _closeToTray = settings.CloseToTray;
        _startMinimized = settings.StartMinimized;
        _notificationsEnabled = settings.NotificationsEnabled;
        _notifyImageEvents = settings.NotifyImageEvents;
        _notifyContainerEvents = settings.NotifyContainerEvents;
        _notifyEngineEvents = settings.NotifyEngineEvents;
        _aiFeaturesEnabled = settings.AiFeaturesEnabled;
        _selectedAiProviderIndex = (int)settings.AiProvider;
        _aiOllamaEndpoint = settings.AiOllamaEndpoint;
        _aiOllamaModel = settings.AiOllamaModel;
        _aiAzureOpenAiEndpoint = settings.AiAzureOpenAiEndpoint;
        _aiAzureOpenAiDeployment = settings.AiAzureOpenAiDeployment;
        _aiOpenAiEndpoint = settings.AiOpenAiEndpoint;
        _aiOpenAiModel = settings.AiOpenAiModel;
        _aiGitHubCopilotModel = settings.AiGitHubCopilotModel;
        _aiAssistantAutoCreateRun = settings.AiAssistantAutoCreateRun;
        _aiAssistantAutoLifecycle = settings.AiAssistantAutoLifecycle;
        _aiAssistantAutoComposeTemplate = settings.AiAssistantAutoComposeTemplate;
        _aiAssistantAutoKubernetes = settings.AiAssistantAutoKubernetes;
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
        LoadStoredAiSecretIndicator();
        _settings.Save();
        if (_settings.AiProvider == AiProviderKind.GitHubCopilot)
        {
            _ = LoadGitHubCopilotModelsAsync();
        }
        else if (_settings.AiProvider == AiProviderKind.Ollama)
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

    partial void OnAiAssistantAutoCreateRunChanged(bool value)
    {
        _settings.AiAssistantAutoCreateRun = value;
        _settings.Save();
    }

    partial void OnAiAssistantAutoLifecycleChanged(bool value)
    {
        _settings.AiAssistantAutoLifecycle = value;
        _settings.Save();
    }

    partial void OnAiAssistantAutoComposeTemplateChanged(bool value)
    {
        _settings.AiAssistantAutoComposeTemplate = value;
        _settings.Save();
    }

    partial void OnAiAssistantAutoKubernetesChanged(bool value)
    {
        _settings.AiAssistantAutoKubernetes = value;
        _settings.Save();
    }

    public void SaveAiApiKey(string secret)
    {
        if (_settings.AiProvider is not (AiProviderKind.AzureOpenAi or AiProviderKind.OpenAi) || string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        _aiCredentials.WriteSecret(_settings.AiProvider, secret);
        AiApiKey = string.Empty;
        AiStatus = $"Saved {_settings.AiProvider} credential in Windows Credential Manager.";
    }

    private void LoadStoredAiSecretIndicator()
    {
        AiApiKey = string.Empty;
        if (_settings.AiProvider == AiProviderKind.GitHubCopilot)
        {
            AiStatus = "GitHub Copilot uses your logged-in Copilot CLI account. No API key is needed.";
            return;
        }

        if (_settings.AiProvider is AiProviderKind.None or AiProviderKind.Ollama)
        {
            AiStatus = _settings.AiProvider == AiProviderKind.Ollama
                ? "Ollama uses the configured local endpoint and model."
                : "Choose an AI provider to configure diagnostics.";
            return;
        }

        AiStatus = _aiCredentials.TryReadSecret(_settings.AiProvider, out _)
            ? $"{_settings.AiProvider} has a saved credential."
            : "No credential is saved for the selected provider.";
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

    [RelayCommand]
    private async Task TestAiProviderAsync()
    {
        IsBusy = true;
        try
        {
            AiStatus = await _aiDiagnostics.TestProviderAsync();
            await _dialogs.ShowMessageAsync("AI provider OK", AiStatus);
        }
        catch (Exception ex)
        {
            AiStatus = ex.Message;
            await _dialogs.ShowMessageAsync("AI provider failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignInGitHubCopilotAsync()
    {
        AiStatus = "GitHub Copilot uses your logged-in Copilot CLI account. Run `copilot login` outside the app if you need to sign in.";
        await _dialogs.ShowMessageAsync("GitHub Copilot", AiStatus);
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
            AiStatus = "Loading GitHub Copilot models…";
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

            if (!configuredAvailable)
            {
                AiStatus = $"Configured model '{persisted}' is not available. Choose a model from the list.";
            }
            else
            {
                AiStatus = $"Loaded {GitHubCopilotModels.Count} GitHub Copilot model(s).";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load GitHub Copilot models.");
            SeedGitHubCopilotModels(persisted);
            ReapplyGitHubCopilotModel(persisted);
            AiStatus = "Could not load GitHub Copilot models. Showing fallback list with the saved model and 'auto'. Details: " + ex.Message;
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
        try
        {
            IsOllamaBusy = true;
            AiStatus = "Loading installed Ollama models…";
            var endpoint = NormalizeOllamaEndpoint(_settings.AiOllamaEndpoint);
            using var response = await _http.GetAsync(new Uri(endpoint, "api/tags"));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {body}");
            }

            var names = new List<string>();
            using (var doc = JsonDocument.Parse(body))
            {
                if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    foreach (var model in models.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            var value = name.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                names.Add(value!);
                            }
                        }
                    }
                }
            }

            ReplaceOllamaModels(names, persisted);
            AiStatus = names.Count == 0
                ? "No Ollama models installed. Pull one below (for example qwen2.5:7b)."
                : $"Loaded {names.Count} installed Ollama model(s).";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load Ollama models.");
            ReplaceOllamaModels([], persisted);
            AiStatus = "Could not reach Ollama at the configured endpoint. Make sure Ollama is running. Details: " + ex.Message;
        }
        finally
        {
            IsOllamaBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOllamaCommand))]
    private async Task PullOllamaModelAsync()
    {
        var name = OllamaPullModel?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            AiStatus = "Enter a model name to pull (for example qwen2.5:7b).";
            return;
        }

        try
        {
            IsOllamaBusy = true;
            AiStatus = $"Pulling '{name}'…";
            var endpoint = NormalizeOllamaEndpoint(_settings.AiOllamaEndpoint);

            // Pulls can take minutes for multi-GB models, so use a dedicated client with no timeout
            // and stream the NDJSON progress rather than the shared 20s HttpClient.
            using var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "api/pull"))
            {
                Content = JsonContent.Create(new { model = name, stream = true }),
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                AiStatus = $"Pull failed ({(int)response.StatusCode}): {Truncate(err)}";
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new System.IO.StreamReader(stream);
            while (await reader.ReadLineAsync() is { } line)
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
                        AiStatus = $"Pull failed: {error.GetString()}";
                        return;
                    }

                    var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString() ?? string.Empty
                        : string.Empty;
                    if (root.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt64(out var total) && total > 0
                        && root.TryGetProperty("completed", out var compEl) && compEl.TryGetInt64(out var completed))
                    {
                        var pct = Math.Clamp(completed * 100.0 / total, 0, 100);
                        AiStatus = $"Pulling {name}: {status} {pct:0}% ({FormatBytes(completed)} / {FormatBytes(total)})";
                    }
                    else if (!string.IsNullOrWhiteSpace(status))
                    {
                        AiStatus = $"Pulling {name}: {status}";
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-JSON progress lines.
                }
            }

            OllamaPullModel = string.Empty;
            await LoadOllamaModelsAsync();
            AiOllamaModel = name;
            AiStatus = $"Pulled '{name}' and selected it.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama pull failed.");
            AiStatus = "Pull failed: " + ex.Message;
        }
        finally
        {
            IsOllamaBusy = false;
        }
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

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";

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
