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

using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
namespace WslContainerDesktop.Services;

/// <summary>Chooses and verifies a workload destination without changing management selection.</summary>
public sealed class WslcSessionSelectionService(
    WslcSessionInventoryReader reader, IWslcService discovery, ISettingsService settings,
    WslcSessionClientFactory clients, DialogService dialogs, WslcSessionContext sessionContext) : IWslcSessionSelectionService
{
    /// <summary>Uses the sole active session, or asks when multiple sessions are active.</summary>
    public Task<WslcSessionSelection?> ChooseAsync(CancellationToken ct = default) =>
        ChooseCoreAsync(startDefault: true, activeOnly: false, ct);

    /// <summary>Chooses an owner for saved work without starting a dormant default session.</summary>
    public Task<WslcSessionSelection?> ChooseForLaterAsync(CancellationToken ct = default) =>
        ChooseCoreAsync(startDefault: false, activeOnly: false, ct);

    /// <summary>Chooses an active session without starting a stopped one.</summary>
    public Task<WslcSessionSelection?> ChooseActiveAsync(CancellationToken ct = default) =>
        ChooseCoreAsync(startDefault: false, activeOnly: true, ct);

    private async Task<WslcSessionSelection?> ChooseCoreAsync(bool startDefault, bool activeOnly, CancellationToken ct)
    {
        var executable = settings.WslcPath;
        var sessions = await reader.DiscoverAsync(ct);
        if (!string.Equals(executable, settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("WslcSessionInventory_ExecutableChanged",
                "The WSLC executable changed while session inventory was being read."));
        var defaultName = WslcSessionActionGuard.ResolveDefaultSessionName();
        IReadOnlyList<WslcSessionSelection> choices = WslcSessionChoices.Create(sessions, executable, defaultName,
            UiText.Get("WslEngine_Session_Default", "Default CLI session"));
        if (activeOnly)
        {
            choices = choices.Where(choice => choice.Generation is not null &&
                !sessionContext.IsDefaultSessionStopped(executable, choice.Target)).ToArray();
            if (choices.Count == 0)
                throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.NoActiveSessions);
        }
        var selected = await WslcSessionChoices.SelectAsync(choices, async available =>
        {
            ct.ThrowIfCancellationRequested();
            var dialog = new SelectSessionDialog(available);
            var dispatcher = dialogs.Dispatcher;
            using var registration = ct.Register(() => dispatcher?.TryEnqueue(dialog.Hide));
            var result = await dialogs.ShowDialogAsync(dialog);
            ct.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary ? dialog.Selection : null;
        });
        if (selected is null)
            return null;
        if (startDefault && sessionContext.IsDefaultSessionStopped(executable, selected.Target))
            throw new InvalidOperationException(UiText.Get("ManagedSession_DefaultStartRequired",
                "The default CLI session was stopped in this app. Open WSL engine and select Start for that session."));
        if (selected.Generation is not null || !startDefault)
            return selected;

        // A user-initiated workload can start the default CLI session when none is active.
        // Capture the resulting generation before loading the run dialog or taking any later action.
        await clients.Create(selected.Target, executable, managed: false).ListImagesAsync(ct);
        var current = await reader.DiscoverAsync(ct);
        var observedChoices = WslcSessionChoices.Create(current, executable, defaultName, selected.DisplayName);
        var generation = observedChoices[0].Generation;
        if (generation is null)
            throw new InvalidOperationException(UiText.Get("SessionPicker_DefaultUnavailable",
                "The default CLI session could not be started or identified. No action was started."));
        return selected with { Generation = generation };
    }

    /// <summary>Checks that the chosen destination has not changed since it was presented.</summary>
    public Task VerifyAsync(WslcSessionSelection selection, CancellationToken ct = default) =>
        VerifyCoreAsync(selection, allowPaused: false, ct);

    /// <summary>Verifies saved-only assignments without resuming an intentionally stopped session.</summary>
    public Task VerifyForLaterAsync(WslcSessionSelection selection, CancellationToken ct = default) =>
        VerifyCoreAsync(selection, allowPaused: true, ct);

    private async Task VerifyCoreAsync(WslcSessionSelection selection, bool allowPaused, CancellationToken ct)
    {
        if (!string.Equals(selection.ExecutablePath, settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("WslcSessionInventory_ExecutableChanged",
                "The WSLC executable changed while session inventory was being read."));
        if (!allowPaused && sessionContext.IsDefaultSessionStopped(selection.ExecutablePath, selection.Target))
            throw new InvalidOperationException(UiText.Get("ManagedSession_DefaultStartRequired",
                "The default CLI session was stopped in this app. Open WSL engine and select Start for that session."));
        if (selection.Generation is { } generation)
            await WslcSessionActionGuard.VerifyAsync(discovery, () => settings.WslcPath,
                generation, selection.ExecutablePath, ct);
    }

    /// <summary>Creates a session-bound client that owns its own automatic-action settings.</summary>
    public IWslcService CreateClient(WslcSessionSelection selection) =>
        selection.Generation is { } generation
            ? clients.CreateGuarded(generation, selection.ExecutablePath, managed: true)
            : throw new InvalidOperationException(UiText.Get("CombinedSession_ActionSessionChanged",
                "The session changed or is no longer available; refresh before taking action."));
}
