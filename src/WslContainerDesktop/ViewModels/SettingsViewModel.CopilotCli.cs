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

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Copilot CLI selection and installation for the GitHub Copilot provider settings.</summary>
public partial class SettingsViewModel
{
    private readonly ConcurrentDictionary<string, string?> _copilotCliVersions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CopilotCliCandidate> _copilotCliCandidates = [];
    private bool _suppressCopilotCliWrite;
    private CancellationTokenSource? _copilotCliInstallCts;

    /// <summary>Copilot CLI choices. The option with an empty <see cref="AiModelOption.Id"/> means automatic selection.</summary>
    public ObservableCollection<AiModelOption> GitHubCopilotCliOptions { get; } = new();

    /// <summary>Selected Copilot CLI path; empty selects automatically.</summary>
    [ObservableProperty]
    private string _aiGitHubCopilotCliPath = string.Empty;

    /// <summary>Whether a Copilot CLI download is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallGitHubCopilotCli))]
    [NotifyCanExecuteChangedFor(nameof(InstallGitHubCopilotCliCommand))]
    private bool _isInstallingGitHubCopilotCli;

    /// <summary>Whether the Copilot CLI list is being refreshed.</summary>
    [ObservableProperty]
    private bool _isRefreshingGitHubCopilotCli;

    /// <summary>Progress text for the Copilot CLI install.</summary>
    [ObservableProperty]
    private string _gitHubCopilotCliInstallStatus = string.Empty;

    /// <summary>Copilot CLI version this build's Copilot SDK expects, or empty when unknown.</summary>
    public string GitHubCopilotCompatibleCliVersion => CopilotCliInstaller.PinnedCliVersion ?? string.Empty;

    /// <summary>Whether the compatible CLI can be downloaded on this machine.</summary>
    public bool CanInstallGitHubCopilotCli =>
        !IsInstallingGitHubCopilotCli && CopilotCliInstaller.PinnedCliVersion is not null && CopilotCliInstaller.PlatformSuffix is not null;

    /// <summary>Whether this app has installed at least one Copilot CLI that can be removed.</summary>
    public bool HasAppInstalledGitHubCopilotCli => CopilotCliInstaller.ListInstalled().Count > 0;

    /// <summary>Label for the install button, including the version it installs.</summary>
    public string InstallGitHubCopilotCliLabel => CopilotCliInstaller.FindInstalled() is not null
        ? UiText.Get("Settings_Ai_CopilotCli_UseInstalled", "Use installed compatible Copilot CLI ({0})", GitHubCopilotCompatibleCliVersion)
        : UiText.Get("Settings_Ai_CopilotCli_Install", "Install compatible Copilot CLI ({0})", GitHubCopilotCompatibleCliVersion);

    /// <summary>Help text under the Copilot CLI picker.</summary>
    public string GitHubCopilotCliHelp => string.IsNullOrEmpty(GitHubCopilotCompatibleCliVersion)
        ? UiText.Get("Settings_Ai_CopilotCli_HelpUnknown", "Choose which Copilot CLI WSL Container Desktop starts. Automatic uses the first one found.")
        : UiText.Get("Settings_Ai_CopilotCli_Help", "Choose which Copilot CLI WSL Container Desktop starts. Automatic uses the first one found. This version of the app needs Copilot CLI {0}, or another version that uses the same protocol. Install downloads that exact version for this app only, so the CLI's self-updates cannot replace it.", GitHubCopilotCompatibleCliVersion);

    /// <summary>
    /// Finds Copilot CLI candidates, reads their versions, and rebuilds the picker without changing the selection.
    /// </summary>
    public async Task RefreshGitHubCopilotCliCandidatesAsync()
    {
        if (IsRefreshingGitHubCopilotCli)
            return;

        IsRefreshingGitHubCopilotCli = true;
        try
        {
            var candidates = await Task.Run(CopilotCliLocator.FindCandidates).ConfigureAwait(true);
            var configured = _settings.AiGitHubCopilotCliPath;
            var paths = candidates.Select(c => c.Path).ToList();
            if (!string.IsNullOrWhiteSpace(configured) && !paths.Contains(configured, StringComparer.OrdinalIgnoreCase) && File.Exists(configured))
                paths.Add(configured);

            _copilotCliVersions.Clear();
            await Task.WhenAll(paths.Select(async path =>
                _copilotCliVersions[path] = await CopilotCliLocator.ProbeVersionAsync(path, CancellationToken.None).ConfigureAwait(false)))
                .ConfigureAwait(true);

            _copilotCliCandidates = candidates;
            RebuildGitHubCopilotCliOptions();
            OnPropertyChanged(nameof(HasAppInstalledGitHubCopilotCli));
            OnPropertyChanged(nameof(InstallGitHubCopilotCliLabel));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to list Copilot CLI candidates.");
            RebuildGitHubCopilotCliOptions();
        }
        finally
        {
            IsRefreshingGitHubCopilotCli = false;
        }
    }

    /// <summary>Selects a Copilot CLI the user browsed to.</summary>
    public async Task SelectCustomGitHubCopilotCliAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _copilotCliVersions[path] = await CopilotCliLocator.ProbeVersionAsync(path, CancellationToken.None).ConfigureAwait(true);
        AiGitHubCopilotCliPath = path;
        RebuildGitHubCopilotCliOptions();
    }

    /// <summary>Command handler that refreshes the Copilot CLI list.</summary>
    [RelayCommand]
    private Task RefreshGitHubCopilotCliAsync() => RefreshGitHubCopilotCliCandidatesAsync();

    /// <summary>Downloads the compatible Copilot CLI (or reuses an existing download) and selects it.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallGitHubCopilotCli))]
    private async Task InstallGitHubCopilotCliAsync()
    {
        if (!CanInstallGitHubCopilotCli)
            return;

        _copilotCliInstallCts?.Dispose();
        _copilotCliInstallCts = new CancellationTokenSource();
        IsInstallingGitHubCopilotCli = true;
        var version = GitHubCopilotCompatibleCliVersion;
        var progress = new Progress<CopilotCliInstallProgress>(p => GitHubCopilotCliInstallStatus = DescribeInstallProgress(p, version));
        try
        {
            var path = await _copilotCliInstaller.InstallAsync(progress, _copilotCliInstallCts.Token).ConfigureAwait(true);
            AiGitHubCopilotCliPath = path;
            await RefreshGitHubCopilotCliCandidatesAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(InstallGitHubCopilotCliLabel));
            ProviderFeedback = AiFeedback.Success(
                UiText.Get("Settings_Ai_CopilotCli_InstalledTitle", "Copilot CLI installed"),
                UiText.Get("Settings_Ai_CopilotCli_InstalledMessage", "Copilot CLI {0} is installed and selected.", version));
            await LoadGitHubCopilotModelsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            GitHubCopilotCliInstallStatus = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot CLI install failed.");
            ProviderFeedback = AiFeedback.Error(
                UiText.Get("Settings_Ai_CopilotCli_InstallFailedTitle", "Copilot CLI install failed"),
                UiText.Get("Settings_Ai_CopilotCli_InstallFailedMessage", "Could not install Copilot CLI {0}. Check your internet connection and try again.", version),
                ex.Message);
        }
        finally
        {
            IsInstallingGitHubCopilotCli = false;
            GitHubCopilotCliInstallStatus = string.Empty;
        }
    }

    /// <summary>
    /// Removes every Copilot CLI this app installed. If one of them was selected, the selection returns to Automatic.
    /// </summary>
    public async Task RemoveAppInstalledGitHubCopilotCliAsync()
    {
        if (IsInstallingGitHubCopilotCli)
            return;

        var root = Path.GetFullPath(CopilotCliLocator.DefaultAppManagedRoot);
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var selected = _settings.AiGitHubCopilotCliPath;
        var selectedWasRemoved = !string.IsNullOrWhiteSpace(selected)
            && Path.GetFullPath(selected).StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);

        try
        {
            await Task.Run(() => _copilotCliInstaller.UninstallAll()).ConfigureAwait(true);
            ProviderFeedback = AiFeedback.Success(
                UiText.Get("Settings_Ai_CopilotCli_RemovedTitle", "Copilot CLI removed"),
                selectedWasRemoved
                    ? UiText.Get("Settings_Ai_CopilotCli_RemovedSelectedMessage", "The Copilot CLI installed by this app was removed. Copilot CLI selection is back to Automatic.")
                    : UiText.Get("Settings_Ai_CopilotCli_RemovedMessage", "The Copilot CLI installed by this app was removed."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the app-installed Copilot CLI.");
            ProviderFeedback = AiFeedback.Error(
                UiText.Get("Settings_Ai_CopilotCli_RemoveFailedTitle", "Copilot CLI not removed"),
                UiText.Get("Settings_Ai_CopilotCli_RemoveFailedMessage", "Some files are still in use. Wait for any Copilot request to finish, then try again."),
                ex.Message);
        }
        finally
        {
            if (selectedWasRemoved && !File.Exists(selected))
                AiGitHubCopilotCliPath = string.Empty;
            await RefreshGitHubCopilotCliCandidatesAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(HasAppInstalledGitHubCopilotCli));
            OnPropertyChanged(nameof(InstallGitHubCopilotCliLabel));
        }
    }

    /// <summary>Persists a picker change and reloads models from the newly selected CLI.</summary>
    partial void OnAiGitHubCopilotCliPathChanged(string value)
    {
        if (_suppressCopilotCliWrite)
            return;

        var normalized = value?.Trim() ?? string.Empty;
        if (string.Equals(normalized, _settings.AiGitHubCopilotCliPath, StringComparison.Ordinal))
            return;

        _settings.AiGitHubCopilotCliPath = normalized;
        _settings.Save();
        RebuildGitHubCopilotCliOptions();
        if (CurrentAiProvider == AiProviderKind.GitHubCopilot)
            _ = LoadGitHubCopilotModelsAsync();
    }

    /// <summary>Rebuilds picker captions from the last discovery (for example after a language change).</summary>
    private void RebuildGitHubCopilotCliOptions()
    {
        var configured = _settings.AiGitHubCopilotCliPath ?? string.Empty;
        var options = new List<AiModelOption>();

        var automatic = _copilotCliCandidates.FirstOrDefault();
        options.Add(new AiModelOption(string.Empty, automatic is null
            ? UiText.Get("Settings_Ai_CopilotCli_AutomaticNone", "Automatic (no Copilot CLI found)")
            : UiText.Get("Settings_Ai_CopilotCli_Automatic", "Automatic: {0}", DescribeCandidate(automatic))));

        foreach (var candidate in _copilotCliCandidates)
            options.Add(new AiModelOption(candidate.Path, DescribeCandidate(candidate)));

        if (!string.IsNullOrWhiteSpace(configured)
            && !options.Any(o => string.Equals(o.Id, configured, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new AiModelOption(configured, File.Exists(configured)
                ? DescribeCandidate(new CopilotCliCandidate(configured, CopilotCliSource.Custom))
                : UiText.Get("Settings_Ai_CopilotCli_Missing", "Not found: {0}", configured)));
        }

        _suppressCopilotCliWrite = true;
        try
        {
            GitHubCopilotCliOptions.Clear();
            foreach (var option in options)
                GitHubCopilotCliOptions.Add(option);
            // Re-assert the stored selection after the ComboBox has reacted to the cleared collection.
            AiGitHubCopilotCliPath = "\0";
            AiGitHubCopilotCliPath = configured;
        }
        finally
        {
            _suppressCopilotCliWrite = false;
        }
    }

    private string DescribeCandidate(CopilotCliCandidate candidate)
    {
        var source = candidate.Source switch
        {
            CopilotCliSource.Environment => UiText.Get("Settings_Ai_CopilotCli_SourceEnvironment", "COPILOT_CLI_PATH"),
            CopilotCliSource.AppManaged => UiText.Get("Settings_Ai_CopilotCli_SourceApp", "Installed by this app"),
            CopilotCliSource.WinGetLinks or CopilotCliSource.WinGetInstall => UiText.Get("Settings_Ai_CopilotCli_SourceWinGet", "WinGet"),
            CopilotCliSource.Npm => UiText.Get("Settings_Ai_CopilotCli_SourceNpm", "npm"),
            CopilotCliSource.Path => UiText.Get("Settings_Ai_CopilotCli_SourcePath", "PATH"),
            _ => UiText.Get("Settings_Ai_CopilotCli_SourceCustom", "Selected file"),
        };

        _copilotCliVersions.TryGetValue(candidate.Path, out var version);
        var versionText = version is null
            ? UiText.Get("Settings_Ai_CopilotCli_VersionUnknown", "no version reported")
            : string.Equals(version, CopilotCliInstaller.PinnedCliVersion, StringComparison.OrdinalIgnoreCase)
                ? UiText.Get("Settings_Ai_CopilotCli_VersionCompatible", "{0} (compatible)", version)
                : version;
        return $"{versionText} · {source} · {candidate.Path}";
    }

    private static string DescribeInstallProgress(CopilotCliInstallProgress progress, string version) => progress.Stage switch
    {
        CopilotCliInstallStage.FetchingChecksums or CopilotCliInstallStage.Downloading when progress.BytesReceived == 0 =>
            UiText.Get("Settings_Ai_CopilotCli_Downloading", "Downloading Copilot CLI {0}…", version),
        CopilotCliInstallStage.Downloading when progress.TotalBytes is > 0 =>
            UiText.Get("Settings_Ai_CopilotCli_DownloadingProgress", "Downloading Copilot CLI {0}… {1} of {2} MB", version,
                Math.Round(progress.BytesReceived / 1048576d, 1), Math.Round(progress.TotalBytes.Value / 1048576d, 1)),
        CopilotCliInstallStage.Downloading =>
            UiText.Get("Settings_Ai_CopilotCli_DownloadingBytes", "Downloading Copilot CLI {0}… {1} MB", version,
                Math.Round(progress.BytesReceived / 1048576d, 1)),
        CopilotCliInstallStage.Verifying => UiText.Get("Settings_Ai_CopilotCli_Verifying", "Verifying the download…"),
        CopilotCliInstallStage.Extracting => UiText.Get("Settings_Ai_CopilotCli_Extracting", "Extracting Copilot CLI…"),
        _ => string.Empty,
    };
}
