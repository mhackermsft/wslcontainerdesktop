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
using Microsoft.UI.Xaml;
using Windows.System;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Backs the requirement gate that blocks container pages when the configured wslc.exe is missing, too old, disabled by policy or unverifiable.</summary>
public partial class RequirementGateViewModel : ObservableObject
{
    private readonly IWslRequirementService _requirements;
    private readonly IWslSystemService _wslSystem;

    /// <summary>Value for status shown or edited by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGateVisible))]
    [NotifyPropertyChangedFor(nameof(IsUpdateVisible))]
    [NotifyPropertyChangedFor(nameof(IsOpenSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(FoundVersionVisibility))]
    [NotifyPropertyChangedFor(nameof(DiagnosticVisibility))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Explanation))]
    [NotifyPropertyChangedFor(nameof(FoundVersionText))]
    private WslRequirementStatus _status;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Bindable state for progress text used by the view.</summary>
    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>Creates the RequirementGate view model and stores its injected services.</summary>
    public RequirementGateViewModel(IWslRequirementService requirements, IWslSystemService wslSystem)
    {
        _requirements = requirements;
        _wslSystem = wslSystem;
        _status = requirements.Current;
        _requirements.Changed += (_, status) => Status = status;
    }

    public event EventHandler? OpenSettingsRequested;

    /// <summary>Whether gate visible for view binding.</summary>
    public bool IsGateVisible => Status.State != WslRequirementState.Ok;

    /// <summary>Whether update visible for view binding.</summary>
    public bool IsUpdateVisible => Status.State != WslRequirementState.Ok &&
        Status.State != WslRequirementState.DisabledByPolicy;

    /// <summary>Whether open settings visible for view binding.</summary>
    public bool IsOpenSettingsVisible => Status.State == WslRequirementState.NotInstalled;

    /// <summary>Bindable state for found version visibility used by the view.</summary>
    public Visibility FoundVersionVisibility =>
        string.IsNullOrWhiteSpace(Status.FoundVersion) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Bindable state for diagnostic visibility used by the view.</summary>
    public Visibility DiagnosticVisibility =>
        string.IsNullOrWhiteSpace(Status.Diagnostic) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Value for title shown or edited by the view.</summary>
    public string Title => "WSL 3.0.1 or later is required";

    /// <summary>Value for explanation shown or edited by the view.</summary>
    public string Explanation => Status.State switch
    {
        WslRequirementState.DisabledByPolicy =>
            "Your organization disabled WSL containers with computer policy. The app can still show Settings, WSL Engine and Kubernetes pages, but container-engine pages are unavailable until the policy changes.",
        WslRequirementState.NotInstalled =>
            "The configured wslc.exe path does not exist. Install or update WSL with 'wsl --install' / 'wsl --update', or open Settings and correct the container engine path.",
        WslRequirementState.TooOld =>
            $"Update WSL to {WslcRequirements.MinimumVersionDisplay} or later to use WSL containers in this app.",
        _ =>
            "The app could not verify the configured WSL container engine. Review the diagnostic and re-check after fixing the issue.",
    };

    /// <summary>Bindable state for found version text used by the view.</summary>
    public string FoundVersionText => string.IsNullOrWhiteSpace(Status.FoundVersion)
        ? string.Empty
        : $"Found wslc {Status.FoundVersion}";

    /// <summary>Command handler for recheck actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RecheckAsync()
    {
        IsBusy = true;
        ProgressText = "Checking WSL container requirements…";
        try
        {
            await _requirements.RecheckAsync();
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
        }
    }

    /// <summary>Command handler for update wsl actions triggered from the view.</summary>
    [RelayCommand]
    private async Task UpdateWslAsync()
    {
        IsBusy = true;
        ProgressText = "Updating WSL…";
        try
        {
            var result = await _wslSystem.UpdateWslAsync(includePreRelease: false);
            ProgressText = result.Success ? "Update finished. Re-checking…" : result.ErrorText;
            await _requirements.RecheckAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for open settings actions triggered from the view.</summary>
    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Command handler for open announcement actions triggered from the view.</summary>
    [RelayCommand]
    private async Task OpenAnnouncementAsync()
    {
        await Launcher.LaunchUriAsync(new Uri(WslcRequirements.AnnouncementUrl));
    }
}
