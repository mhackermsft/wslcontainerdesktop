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
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the Registries page. Persists registry definitions in settings and delegates
/// credential handling to `wslc login`/`logout` so secrets never touch the app's storage.
/// </summary>
public partial class RegistriesViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay
    {
        get
        {
            var message = _statusFailure is { } failure
                ? UiText.Get("Registry_StatusCheckFailed", "Registry login status could not be checked: {0}",
                    WslcSessionInventoryErrors.Describe(failure))
                : _keyedStatus is { } status
                    ? UiText.Get(status.Key, status.Fallback, status.Arguments)
                    : UiText.Translate(StatusMessage);
            return !_hasStatusSession ? message :
                UiText.Get("Registry_StatusWithSession", "{0} · Last session: {1}",
                    message, _statusSessionName ?? UiText.Get("WslEngine_Session_Default", "Default CLI session"));
        }
    }

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PolicyInfoMessageDisplay => UiText.Translate(PolicyInfoMessage);

    private readonly IAzureCliService _azure;
    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly IRegistryCredentialStore _credentials;
    private readonly RegistryLogoutService _registryLogout;
    private readonly IWslPolicyService _policy;
    private readonly IWslcSessionSelectionService _sessionSelection;
    private readonly ILogger<RegistriesViewModel> _logger;
    private readonly IWslcSettingsFileService _wslcSettings;
    private bool _hasStatusSession;
    private string? _statusSessionName;
    private int _statusRefreshGeneration;
    private (string Key, string Fallback, object?[] Arguments)? _keyedStatus;
    private Exception? _statusFailure;
    private CancellationTokenSource? _statusLifetime;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Manage the registries available when running or pulling images.";

    partial void OnStatusMessageChanged(string value)
    {
        _keyedStatus = null;
        _statusFailure = null;
    }

    private void SetKeyedStatus(string key, string fallback, params object?[] arguments)
    {
        StatusMessage = fallback;
        _statusFailure = null;
        _keyedStatus = (key, fallback, arguments);
        OnPropertyChanged(nameof(StatusMessageDisplay));
    }

    /// <summary>Whether policy info visible for view binding.</summary>
    [ObservableProperty]
    private bool _isPolicyInfoVisible;

    /// <summary>Bindable state for policy info message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyInfoMessageDisplay))]
    private string _policyInfoMessage = string.Empty;

    /// <summary>Value for registries shown or edited by the view.</summary>
    public ObservableCollection<RegistryEntry> Registries { get; } = new();

    /// <summary>Creates the Registries view model and stores its injected services.</summary>
    public RegistriesViewModel(IAzureCliService azure, ISettingsService settings, DialogService dialogs, RegistryAuthRefresher authRefresher, IRegistryCredentialStore credentials, IWslPolicyService policy, RegistryLogoutService registryLogout, IWslcSessionSelectionService sessionSelection, ILogger<RegistriesViewModel> logger, IWslcSettingsFileService wslcSettings)
    {
        _azure = azure;
        _settings = settings;
        _dialogs = dialogs;
        _authRefresher = authRefresher;
        _credentials = credentials;
        _registryLogout = registryLogout;
        _policy = policy;
        _sessionSelection = sessionSelection;
        _logger = logger;
        _wslcSettings = wslcSettings;
    }

    /// <summary>Refreshes load state for the view model.</summary>
    public Task LoadAsync()
    {
        StopStatusRefresh();
        _statusLifetime = new CancellationTokenSource();
        RefreshPolicyInfo();
        Registries.Clear();
        foreach (var r in _settings.Registries)
        {
            r.LoginState = RegistryLoginState.Unknown;
            Registries.Add(r);
        }

        _hasStatusSession = false;
        SetKeyedStatus("Registry_StatusNotChecked",
            "Registry login status has not been checked. Select Refresh to try again.");
        OnPropertyChanged(nameof(StatusMessageDisplay));
        return Task.CompletedTask;
    }

    /// <summary>Refreshes policy info state for the view model.</summary>
    private void RefreshPolicyInfo()
    {
        var allowlist = _policy.GetPolicy().RegistryAllowlist;
        IsPolicyInfoVisible = allowlist.State != WslRegistryAllowlistState.Unrestricted;
        PolicyInfoMessage = allowlist.State switch
        {
            WslRegistryAllowlistState.Configured =>
                "Your organization restricts WSL container image registries to: " +
                string.Join(", ", allowlist.Registries),
            WslRegistryAllowlistState.Invalid =>
                "Your organization's WSLContainerRegistryAllowlist policy is invalid; registry operations fail closed until it is corrected.",
            _ => string.Empty,
        };
    }

    /// <summary>Probes each non-default registry's login state and updates the row indicators.
    /// Azure-backed registries that have lapsed are silently re-authenticated when possible. The
    /// built-in Docker Hub entry reflects the engine's stored credential (logged in vs anonymous).</summary>
    [RelayCommand]
    internal async Task RefreshLoginStatesAsync()
    {
        var ct = _statusLifetime?.Token ?? default;
        try { await RefreshLoginStatesCoreAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Cancels page-owned status checks and dismisses any pending session chooser.</summary>
    public void StopStatusRefresh()
    {
        _statusLifetime?.Cancel();
        _statusLifetime?.Dispose();
        _statusLifetime = null;
        InvalidatePendingStatus();
    }

    private async Task RefreshLoginStatesCoreAsync(CancellationToken ct)
    {
        var version = Interlocked.Increment(ref _statusRefreshGeneration);
        bool IsLatest() => version == Volatile.Read(ref _statusRefreshGeneration);
        try
        {
            var choice = await _sessionSelection.ChooseActiveAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!IsLatest()) return;
            if (choice is null)
            {
                MarkStatusNotChecked();
                return;
            }
            await _sessionSelection.VerifyAsync(choice, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsLatest()) return;
            var target = new SessionOperationTarget(choice, _sessionSelection.CreateClient(choice), _sessionSelection);
            await RefreshLoginStatesForAsync(target, IsLatest, IsLatest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Registry status session selection failed.");
            if (IsLatest()) MarkStatusFailure(ex);
        }
    }

    private async Task RefreshLoginStatesForAsync(
        SessionOperationTarget target, Func<bool> isCurrent, Func<bool> isLatest, CancellationToken ct)
    {
        if (!isCurrent()) { if (isLatest()) MarkStatusUnavailable(); return; }
        try
        {
            await target.VerifyAsync(ct);
            await ReconcileDockerHubStateAsync(isCurrent);
            await target.VerifyAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Registry status could not be checked for the chosen session.");
            if (isLatest()) MarkStatusUnavailable();
            return;
        }
        if (!isCurrent()) { if (isLatest()) MarkStatusUnavailable(); return; }

        foreach (var r in Registries.Where(x => x.HasHost))
        {
            r.LoginState = Models.RegistryLoginState.Checking;
        }

        foreach (var r in Registries.Where(x => x.HasHost).ToList())
        {
            try
            {
                async Task VerifyAsync(CancellationToken ct)
                {
                    if (!isCurrent())
                        throw new InvalidOperationException(UiText.Get("CombinedSession_ActionSessionChanged",
                            "The session changed or is no longer available; refresh before taking action."));
                    await target.VerifyAsync(ct);
                }
                await VerifyAsync(ct);
                var state = await target.Client.ProbeRegistryLoginAsync(r.Host, "wslcd-login-probe", ct);
                if (state != Models.RegistryLoginState.LoggedIn && r.IsAzure &&
                    await _authRefresher.RefreshAsync(r, ct, sessionClient: target.Client, verifyTarget: VerifyAsync))
                    state = Models.RegistryLoginState.LoggedIn;
                await VerifyAsync(ct);
                r.LoginState = state;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
            {
                _logger.LogWarning(ex, "Registry status could not be checked for the chosen session.");
                if (isLatest()) MarkStatusUnavailable();
                return;
            }
        }
        if (!isCurrent()) { if (isLatest()) MarkStatusUnavailable(); return; }
        SetKeyedStatus("Registry_StatusUpdated", "Registry login status updated.");
        SetStatusSession(target.Selection);
    }

    /// <summary>
    /// Sets the Docker Hub row to "Logged in" (with the stored username) or "Anonymous" based on
    /// whether the engine has a stored credential for it.
    /// </summary>
    private async Task ReconcileDockerHubStateAsync(Func<bool> isCurrent)
    {
        var dockerHub = Registries.FirstOrDefault(r => r.IsDefault);
        if (dockerHub is null)
        {
            return;
        }

        string? backend;
        try { backend = (await _wslcSettings.ReadAsync()).CredentialStore?.Trim(); }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Could not determine the registry credential backend.");
            if (isCurrent()) dockerHub.LoginState = RegistryLoginState.Unknown;
            return;
        }
        if (!isCurrent()) return;
        if (!string.IsNullOrWhiteSpace(backend) &&
            !backend.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !backend.Equals("wincred", StringComparison.OrdinalIgnoreCase))
        {
            dockerHub.LoginState = RegistryLoginState.Unknown;
            return;
        }
        var status = _credentials.GetLoginStatus(dockerHub.LoginServer, out var user);
        if (status == RegistryCredentialStatus.Present)
        {
            dockerHub.Username = user;
            dockerHub.LoginState = Models.RegistryLoginState.LoggedIn;
        }
        else if (status == RegistryCredentialStatus.Missing)
        {
            dockerHub.Username = null;
            dockerHub.LoginState = Models.RegistryLoginState.Anonymous;
        }
        else dockerHub.LoginState = RegistryLoginState.Unknown;
    }

    private void InvalidatePendingStatus()
    {
        Interlocked.Increment(ref _statusRefreshGeneration);
        foreach (var registry in Registries.Where(item => item.LoginState == RegistryLoginState.Checking))
            registry.LoginState = RegistryLoginState.Unknown;
    }

    private void SetStatusSession(WslcSessionSelection selection, bool clearOtherSessionStatus = false)
    {
        if (clearOtherSessionStatus && (!_hasStatusSession ||
            !string.Equals(_statusSessionName, selection.Target.Name, StringComparison.Ordinal)))
            foreach (var registry in Registries)
                registry.LoginState = RegistryLoginState.Unknown;
        _hasStatusSession = true;
        _statusSessionName = selection.Target.Name;
        OnPropertyChanged(nameof(StatusMessageDisplay));
    }

    private void MarkStatusUnavailable()
    {
        foreach (var registry in Registries)
            registry.LoginState = Models.RegistryLoginState.Unknown;
        _hasStatusSession = false;
        SetKeyedStatus("Registry_StatusUnavailable",
            "Registry login status could not be checked. Choose an active session and refresh.");
        OnPropertyChanged(nameof(StatusMessageDisplay));
    }

    private void MarkStatusNotChecked()
    {
        foreach (var registry in Registries)
            registry.LoginState = RegistryLoginState.Unknown;
        _hasStatusSession = false;
        SetKeyedStatus("Registry_StatusNotChecked",
            "Registry login status has not been checked. Select Refresh to try again.");
        OnPropertyChanged(nameof(StatusMessageDisplay));
    }

    private void MarkStatusFailure(Exception error)
    {
        foreach (var registry in Registries)
            registry.LoginState = RegistryLoginState.Unknown;
        _hasStatusSession = false;
        _keyedStatus = null;
        _statusFailure = error;
        OnPropertyChanged(nameof(StatusMessageDisplay));
    }

    /// <summary>Command handler for add actions triggered from the view.</summary>
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
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_2683787a3644", "Already added"),
                UiText.Get("Resource_Text_215d03e9ca3f", "A registry with host \"{0}\" already exists.", entry.Host));
            return;
        }

        _settings.Registries.Add(entry);
        _settings.Save();
        Registries.Add(entry);

        if (dialog.LoginNow && !string.IsNullOrEmpty(dialog.Password))
        {
            var target = await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
            if (target is null)
            {
                SetKeyedStatus("Registry_AddedWithoutSignIn",
                    "Registry {0} was added, but sign-in did not start.", entry.Name);
                return;
            }
            await LoginCoreAsync(entry, entry.Username ?? string.Empty, dialog.Password, target);
        }
        else
        {
            StatusMessage = $"Added {entry.Name}.";
        }
    }

    /// <summary>Command handler for add from azure actions triggered from the view.</summary>
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
            var target = await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
            if (target is null)
            {
                SetKeyedStatus("Registry_AddedWithoutSignIn",
                    "Registry {0} was added, but sign-in did not start.", existing.Name);
                return;
            }
            await LoginCoreAsync(existing, dialog.TokenUsername, dialog.Token, target);
        }
    }

    /// <summary>Command handler for login actions triggered from the view.</summary>
    [RelayCommand]
    private async Task LoginAsync(RegistryEntry? registry)
    {
        if (registry is null)
        {
            return;
        }

        var target = await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
        if (target is null) return;
        InvalidatePendingStatus();
        SetStatusSession(target.Selection, clearOtherSessionStatus: true);

        // Azure-backed registries re-authenticate through Azure, not a username/password prompt.
        if (registry.IsAzure)
        {
            IsBusy = true;
            StatusMessage = $"Refreshing Azure sign-in for {registry.Name}…";
            try
            {
                await target.VerifyAsync();
                var refreshed = await _authRefresher.RefreshAsync(registry, sessionClient: target.Client,
                    verifyTarget: token => target.VerifyAsync(token));
                await target.VerifyAsync();
                if (refreshed)
                {
                    registry.LoginState = Models.RegistryLoginState.LoggedIn;
                    SetStatusSession(target.Selection);
                    StatusMessage = $"Logged in to {registry.Name} using Azure.";
                    return;
                }
            }
            catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
            {
                _logger.LogWarning("Azure registry sign-in did not complete for the chosen session; details withheld.");
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c00f6627f603", "Login failed"),
                    ex is OperationCanceledException
                        ? UiText.Get("CombinedSession_ActionOutcomeUnknown",
                            "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.")
                        : UiText.Get("Registry_SessionLoginFailed",
                            "Sign-in could not be completed for the chosen session. Refresh the session list and try again."));
                return;
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
                try { await target.VerifyAsync(); }
                catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
                {
                    _logger.LogWarning("The chosen session changed before Azure registry sign-in.");
                    await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c00f6627f603", "Login failed"),
                        UiText.Get("Registry_SessionLoginFailed",
                            "Sign-in could not be completed for the chosen session. Refresh the session list and try again."));
                    return;
                }
                registry.IsAzure = true;
                registry.SubscriptionId = azDialog.Registry.SubscriptionId;
                registry.AzureAcrName = azDialog.Registry.AzureAcrName;
                _settings.Save();
                await LoginCoreAsync(registry, azDialog.TokenUsername, azDialog.Token, target);
            }

            return;
        }

        var dialog = new RegistryLoginDialog(
            registry.Name, registry.Username, RegistryUsername.IsDockerHub(registry.LoginServer));
        dialog.SetSession(target.Selection);
        if (await _dialogs.ShowDialogAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        await LoginCoreAsync(registry, dialog.Username, dialog.Password, target);
    }

    /// <summary>Helper for the login core workflow in this view model.</summary>
    private async Task LoginCoreAsync(RegistryEntry registry, string username, string password, SessionOperationTarget target)
    {
        username = RegistryUsername.Normalize(username, RegistryUsername.IsDockerHub(registry.LoginServer));
        InvalidatePendingStatus();
        SetStatusSession(target.Selection, clearOtherSessionStatus: true);
        IsBusy = true;
        StatusMessage = $"Logging in to {registry.Name}…";
        try
        {
            await target.VerifyAsync();
            var result = await target.Client.LoginRegistryAsync(registry.LoginServer, username, password);
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
                SetStatusSession(target.Selection);
            }
            else
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_c00f6627f603", "Login failed"), result.ErrorText);
                StatusMessage = $"Login to {registry.Name} failed.";
                registry.LoginState = Models.RegistryLoginState.LoggedOut;
            }
        }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning("Registry sign-in did not complete for the chosen session; details withheld.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c00f6627f603", "Login failed"),
                ex is OperationCanceledException
                    ? UiText.Get("CombinedSession_ActionOutcomeUnknown",
                        "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.")
                    : UiText.Get("Registry_SessionLoginFailed",
                        "Sign-in could not be completed for the chosen session. Refresh the session list and try again."));
            StatusMessage = $"Login to {registry.Name} failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for logout actions triggered from the view.</summary>
    [RelayCommand]
    private async Task LogoutAsync(RegistryEntry? registry)
    {
        if (registry is null)
        {
            return;
        }

        var target = await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger, startDefault: false);
        if (target is null) return;
        if (!await ConfirmLogoutAsync(registry, target, removing: false)) return;
        IsBusy = true;
        StatusMessage = $"Logging out of {registry.Name}…";
        try
        {
            var error = await TryLogoutAsync(registry, target);
            if (error is null)
            {
                _settings.Save();
            }
            else
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_d2313d7e8a46", "Logout failed"), error);
                StatusMessage = $"Logout of {registry.Name} failed.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Logs out through the shared service and clears the row's login state only once completion is
    /// confirmed. Returns null on success, otherwise the localized reason the logout did not complete.
    /// </summary>
    private async Task<string?> TryLogoutAsync(RegistryEntry registry, SessionOperationTarget target)
    {
        InvalidatePendingStatus();
        SetStatusSession(target.Selection, clearOtherSessionStatus: true);
        RegistryLogoutResult result;
        try
        {
            result = await _registryLogout.LogoutAsync(registry.LoginServer, sessionClient: target.Client,
                verifyTarget: token => target.VerifyAsync(token));
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Registry logout was interrupted; its outcome may be unknown.");
            return UiText.Get("CombinedSession_ActionOutcomeUnknown",
                "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.");
        }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Registry logout failed for the chosen session.");
            return string.IsNullOrWhiteSpace(ex.Message)
                ? UiText.Get("Resource_Text_441c9ae8b3cd", "Logout did not complete.")
                : ex.Message;
        }

        if (result.Status != RegistryLogoutStatus.Completed)
        {
            var error = result.Status switch
            {
                RegistryLogoutStatus.CredentialsRemain => UiText.Get("Resource_Text_242b7214af2f",
                    "A stored login remains for this registry. Refresh its login state and try logging out again."),
                RegistryLogoutStatus.CredentialStateUnknown => UiText.Get("Resource_Text_edf6b3a96ba2",
                    "The stored login state could not be checked. Refresh its login state before trying again."),
                _ => result.Error?.ErrorText ?? string.Empty,
            };
            return string.IsNullOrWhiteSpace(error)
                ? UiText.Get("Resource_Text_441c9ae8b3cd", "Logout did not complete.")
                : error;
        }

        registry.LoginState = registry.IsDefault
            ? Models.RegistryLoginState.Anonymous
            : Models.RegistryLoginState.LoggedOut;
        registry.Username = null;
        SetStatusSession(target.Selection);
        StatusMessage = $"Logged out of {registry.Name}.";
        return null;
    }

    /// <summary>Command handler for remove actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RemoveAsync(RegistryEntry? registry)
    {
        if (registry is null || registry.IsDefault)
        {
            return;
        }

        var confirmed = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_a5d663720ca0", "Remove registry"),
            UiText.Get("Registry_RemoveListOnlyConfirmation",
                "Remove \"{0}\" ({1}) from the shared list? Stored credentials will remain. To remove them, sign out before removing this entry.",
                registry.Name, registry.Host),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"),
            UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            _settings.Registries.Remove(registry);
            _settings.Save();
            Registries.Remove(registry);
            SetKeyedStatus("Registry_RemovedListOnly", "Removed {0} from the list. Stored credentials were not changed.", registry.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task<bool> ConfirmLogoutAsync(RegistryEntry registry, SessionOperationTarget target, bool removing) =>
        _dialogs.ShowLocalizedConfirmAsync(() => (
            removing ? UiText.Get("Resource_Text_a5d663720ca0", "Remove registry") :
                UiText.Get("Registry_LogoutTitle", "Sign out of registry"),
            removing
                ? UiText.Get("Registry_RemoveSessionConfirmation",
                    "Remove \"{0}\" ({1}) and sign out using session {2}? Registry credentials may be shared with other sessions.",
                    registry.Name, registry.Host, target.DisplayName)
                : UiText.Get("Registry_LogoutSessionConfirmation",
                    "Sign out of \"{0}\" using session {1}? Registry credentials may be shared with other sessions.",
                    registry.Name, target.DisplayName),
            removing ? UiText.Get("Resource_Text_e963907dac5c", "Remove") :
                UiText.Get("Registry_LogoutAction", "Sign out"),
            UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        foreach (var item in Registries) item.RefreshLocalizedText();
    }
}
