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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private bool _suppressStartupWrite;

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
    private int _selectedAiProviderIndex;

    [ObservableProperty]
    private string _aiOllamaEndpoint = string.Empty;

    [ObservableProperty]
    private string _aiOllamaModel = string.Empty;

    [ObservableProperty]
    private string _aiAzureOpenAiEndpoint = string.Empty;

    [ObservableProperty]
    private string _aiAzureOpenAiDeployment = string.Empty;

    [ObservableProperty]
    private string _aiOpenAiEndpoint = string.Empty;

    [ObservableProperty]
    private string _aiOpenAiModel = string.Empty;

    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    [ObservableProperty]
    private string _aiStatus = "AI features are off by default. Enable them and review the payload preview before sending diagnostics.";

    [ObservableProperty]
    private string _engineVersion = "Unknown";

    [ObservableProperty]
    private bool _isBusy;

    public string AppVersion { get; } = ResolveAppVersion();

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

    public SettingsViewModel(ISettingsService settings, IWslcService wslc, DialogService dialogs, StartupService startup, FileLoggerProvider fileLogger, IAiDiagnosticsService aiDiagnostics, IAiCredentialStore aiCredentials)
    {
        _settings = settings;
        _wslc = wslc;
        _dialogs = dialogs;
        _startup = startup;
        _fileLogger = fileLogger;
        _aiDiagnostics = aiDiagnostics;
        _aiCredentials = aiCredentials;

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
    }

    partial void OnAiOllamaEndpointChanged(string value)
    {
        _settings.AiOllamaEndpoint = value;
        _settings.Save();
    }

    partial void OnAiOllamaModelChanged(string value)
    {
        _settings.AiOllamaModel = value;
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

    public void SaveAiApiKey(string secret)
    {
        if (_settings.AiProvider is not (AiProviderKind.AzureOpenAi or AiProviderKind.OpenAi) || string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        _aiCredentials.WriteSecret(_settings.AiProvider, secret);
        AiApiKey = string.Empty;
        AiStatus = $"Saved {_settings.AiProvider} key in Windows Credential Manager.";
    }

    private void LoadStoredAiSecretIndicator()
    {
        AiApiKey = string.Empty;
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
        AiStatus = "GitHub Copilot sign-in is not wired yet. Use Ollama, Azure OpenAI, or OpenAI.";
        await _dialogs.ShowMessageAsync("GitHub Copilot", AiStatus);
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
