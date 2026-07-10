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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the Registries page. Persists registry definitions in settings and delegates
/// credential handling to `wslc login`/`logout` so secrets never touch the app's storage.
/// </summary>
public partial class RegistriesViewModel : ObservableObject
{
    private readonly IWslcService _wslc;
    private readonly IAzureCliService _azure;
    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly RegistryAuthRefresher _authRefresher;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Manage the registries available when running or pulling images.";

    public ObservableCollection<RegistryEntry> Registries { get; } = new();

    public RegistriesViewModel(IWslcService wslc, IAzureCliService azure, ISettingsService settings, DialogService dialogs, RegistryAuthRefresher authRefresher)
    {
        _wslc = wslc;
        _azure = azure;
        _settings = settings;
        _dialogs = dialogs;
        _authRefresher = authRefresher;
    }

    public void Load()
    {
        Registries.Clear();
        foreach (var r in _settings.Registries)
        {
            Registries.Add(r);
        }

        _ = RefreshLoginStatesAsync();
    }

    /// <summary>Probes each non-default registry's login state and updates the row indicators.
    /// Azure-backed registries that have lapsed are silently re-authenticated when possible.</summary>
    [RelayCommand]
    private async Task RefreshLoginStatesAsync()
    {
        foreach (var r in Registries.Where(x => x.HasHost))
        {
            r.LoginState = Models.RegistryLoginState.Checking;
        }

        foreach (var r in Registries.Where(x => x.HasHost).ToList())
        {
            var state = await _wslc.ProbeRegistryLoginAsync(r.Host, "wslcd-login-probe");

            // Auto-refresh Azure registries whose token has expired, using the cached az
            // session (no prompt). If that succeeds the row shows Logged in again.
            if (state != Models.RegistryLoginState.LoggedIn && r.IsAzure)
            {
                if (await TryAzureRefreshAsync(r))
                {
                    state = Models.RegistryLoginState.LoggedIn;
                }
            }

            r.LoginState = state;
        }
    }

    /// <summary>
    /// Silently re-mints an ACR token from the cached Azure CLI session and logs the engine
    /// back in. Returns false if the Azure session itself is gone (interactive sign-in needed).
    /// </summary>
    private Task<bool> TryAzureRefreshAsync(RegistryEntry registry) =>
        _authRefresher.RefreshAsync(registry);

    [RelayCommand]
    private async Task AddAsync()
    {
        var dialog = new AddRegistryDialog();
        if (await _dialogs.ShowDialogAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            dialog.Registry is null)
        {
            return;
        }

        var entry = dialog.Registry;
        if (_settings.Registries.Any(r =>
                string.Equals(r.Host, entry.Host, StringComparison.OrdinalIgnoreCase)))
        {
            await _dialogs.ShowMessageAsync("Already added",
                $"A registry with host \"{entry.Host}\" already exists.");
            return;
        }

        _settings.Registries.Add(entry);
        _settings.Save();
        Registries.Add(entry);

        if (dialog.LoginNow && !string.IsNullOrEmpty(dialog.Password))
        {
            await LoginCoreAsync(entry, entry.Username ?? string.Empty, dialog.Password);
        }
        else
        {
            StatusMessage = $"Added {entry.Name}.";
        }
    }

    [RelayCommand]
    private async Task AddFromAzureAsync()
    {
        var dialog = new AddAcrDialog(_azure);
        if (await _dialogs.ShowDialogAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            dialog.Registry is null)
        {
            return;
        }

        var entry = dialog.Registry;

        // If this ACR is already registered, reuse the existing row; otherwise add it.
        var existing = _settings.Registries.FirstOrDefault(r =>
            string.Equals(r.Host, entry.Host, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _settings.Registries.Add(entry);
            _settings.Save();
            Registries.Add(entry);
            existing = entry;
        }
        else
        {
            existing.Username = entry.Username;
            existing.IsAzure = entry.IsAzure;
            existing.SubscriptionId = entry.SubscriptionId;
            existing.AzureAcrName = entry.AzureAcrName;
            _settings.Save();
        }

        // Log in with the Azure token so the registry is immediately usable.
        if (!string.IsNullOrEmpty(dialog.Token))
        {
            await LoginCoreAsync(existing, dialog.TokenUsername, dialog.Token);
        }
    }

    [RelayCommand]
    private async Task LoginAsync(RegistryEntry? registry)
    {
        if (registry is null)
        {
            return;
        }

        // Azure-backed registries re-authenticate through Azure, not a username/password prompt.
        if (registry.IsAzure)
        {
            IsBusy = true;
            StatusMessage = $"Refreshing Azure sign-in for {registry.Name}…";
            try
            {
                if (await TryAzureRefreshAsync(registry))
                {
                    registry.LoginState = Models.RegistryLoginState.LoggedIn;
                    StatusMessage = $"Logged in to {registry.Name} using Azure.";
                    return;
                }
            }
            finally
            {
                IsBusy = false;
            }

            // Silent refresh failed (az session expired) — run the interactive Azure flow.
            var azDialog = new AddAcrDialog(_azure);
            if (await _dialogs.ShowDialogAsync(azDialog) == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary &&
                azDialog.Registry is not null && !string.IsNullOrEmpty(azDialog.Token))
            {
                registry.IsAzure = true;
                registry.SubscriptionId = azDialog.Registry.SubscriptionId;
                registry.AzureAcrName = azDialog.Registry.AzureAcrName;
                _settings.Save();
                await LoginCoreAsync(registry, azDialog.TokenUsername, azDialog.Token);
            }

            return;
        }

        var dialog = new RegistryLoginDialog(registry.Name, registry.Username);
        if (await _dialogs.ShowDialogAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        await LoginCoreAsync(registry, dialog.Username, dialog.Password);
    }

    private async Task LoginCoreAsync(RegistryEntry registry, string username, string password)
    {
        IsBusy = true;
        StatusMessage = $"Logging in to {registry.Name}…";
        try
        {
            var result = await _wslc.LoginRegistryAsync(registry.LoginServer, username, password);
            if (result.Success)
            {
                // Remember the username for convenience (never the password).
                if (!string.IsNullOrWhiteSpace(username))
                {
                    registry.Username = username;
                    _settings.Save();
                }

                StatusMessage = $"Logged in to {registry.Name}.";
                registry.LoginState = Models.RegistryLoginState.LoggedIn;
            }
            else
            {
                await _dialogs.ShowMessageAsync("Login failed", result.ErrorText);
                StatusMessage = $"Login to {registry.Name} failed.";
                registry.LoginState = Models.RegistryLoginState.LoggedOut;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync(RegistryEntry? registry)
    {
        if (registry is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Logging out of {registry.Name}…";
        try
        {
            var result = await _wslc.LogoutRegistryAsync(registry.LoginServer);
            if (result.Success)
            {
                // Docker Hub reverts to anonymous access; user-added registries become "not logged in".
                registry.LoginState = registry.IsDefault
                    ? Models.RegistryLoginState.Anonymous
                    : Models.RegistryLoginState.LoggedOut;
                registry.Username = null;
                _settings.Save();
                StatusMessage = $"Logged out of {registry.Name}.";
            }
            else
            {
                await _dialogs.ShowMessageAsync("Logout failed", result.ErrorText);
                StatusMessage = $"Logout of {registry.Name} failed.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(RegistryEntry? registry)
    {
        if (registry is null || registry.IsDefault)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove registry",
            $"Remove \"{registry.Name}\" ({registry.Host})? This also logs out of it.",
            "Remove");
        if (!ok)
        {
            return;
        }

        // Best-effort logout so stored credentials don't linger.
        try
        {
            await _wslc.LogoutRegistryAsync(registry.Host);
        }
        catch
        {
            // ignore
        }

        _settings.Registries.Remove(registry);
        _settings.Save();
        Registries.Remove(registry);
        StatusMessage = $"Removed {registry.Name}.";
    }
}
