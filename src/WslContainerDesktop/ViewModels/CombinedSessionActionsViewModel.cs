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

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Coordinates user-confirmed actions on session-qualified rows.</summary>
public sealed partial class CombinedSessionActionsViewModel : ObservableObject, IDisposable
{
    private readonly WslcSessionContainerActions _actions;
    private readonly WslcSessionResourceActions _resources;
    private readonly CombinedSessionViewModel _inventory;
    private readonly DialogService _dialogs;
    private readonly IAiCapabilityService _aiCapabilities;
    private readonly ILogger<CombinedSessionActionsViewModel> _logger;
    private readonly WslcSessionContext _sessionContext;

    /// <summary>Creates the action model without starting background work.</summary>
    public CombinedSessionActionsViewModel(WslcSessionContainerActions actions, WslcSessionResourceActions resources,
        CombinedSessionViewModel inventory, DialogService dialogs, IAiCapabilityService aiCapabilities,
        ILogger<CombinedSessionActionsViewModel> logger, WslcSessionContext sessionContext)
    {
        _actions = actions;
        _resources = resources;
        _inventory = inventory;
        _dialogs = dialogs;
        _aiCapabilities = aiCapabilities;
        _logger = logger;
        _sessionContext = sessionContext;
        _inventory.PropertyChanged += OnInventoryChanged;
    }

    /// <summary>Shows engine inspection output for one resource in its owning session.</summary>
    public Task InspectAsync(CombinedSessionRow row) => RunResourceAsync(row, async () =>
    {
        var result = row.Container is not null && row.Port is null
            ? await _actions.InspectAsync(CaptureContainer(row))
            : await InspectResourceAsync(row);
        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("CombinedSession_InspectTitle", "Inspect {0} · {1}", row.Name, row.SessionName),
            result.Success ? result.StandardOutput : result.ErrorText);
    });

    private Task<CommandResult> InspectResourceAsync(CombinedSessionRow row)
    {
        var (kind, id) = Identify(row);
        return _resources.InspectAsync(row.Session, kind, id);
    }

    /// <summary>Shows a bounded log snapshot from the container's owning session.</summary>
    public Task LogsAsync(CombinedSessionRow row) => RunResourceAsync(row, async () =>
    {
        var target = CaptureContainer(row);
        var result = await _actions.ReadLogsAsync(target);
        var text = !result.Success ? result.ErrorText :
            string.IsNullOrWhiteSpace(result.StandardOutput)
                ? (string.IsNullOrWhiteSpace(result.StandardError)
                    ? UiText.Get("Resource_Text_800c9b197e97", "(no output)") : result.StandardError)
                : result.StandardOutput;
        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("CombinedSession_LogsTitle", "Logs · {0} · {1}", target.Name, target.Session.Name), text);
    });

    /// <summary>Shows an owner-scoped resource-usage snapshot.</summary>
    public Task StatsAsync(CombinedSessionRow row) => RunResourceAsync(row, async () =>
    {
        var target = CaptureContainer(row);
        var stats = await _actions.ReadStatsAsync(target);
        var text = stats is null
            ? UiText.Get("CombinedSession_StatsUnavailable", "Container statistics are unavailable.")
            : UiText.Get("CombinedSession_StatsDetails",
                "CPU: {0}\nMemory: {1}\nNetwork I/O: {2}\nDisk I/O: {3}\nProcesses: {4}",
                stats.CpuPercent, stats.MemUsage, stats.NetIO, stats.BlockIO, stats.Pids);
        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("CombinedSession_StatsTitle", "Statistics · {0} · {1}", target.Name, target.Session.Name), text);
    });

    /// <summary>Opens a terminal in the captured container's owning session.</summary>
    public Task TerminalAsync(CombinedSessionRow row) => RunResourceAsync(row, async () =>
        await _actions.OpenTerminalAsync(CaptureContainer(row)));

    /// <summary>Prompts for a container directory and copies a selected host file into it.</summary>
    public Task CopyToContainerAsync(CombinedSessionRow row, string hostPath) => RunResourceAsync(row, async () =>
    {
        var target = CaptureContainer(row);
        var input = new SimpleInputDialog(
            UiText.Get("CombinedSession_CopyInTitle", "Copy into container"),
            UiText.Get("CombinedSession_CopyInDirectory", "Container destination directory"),
            "/tmp");
        if (await _dialogs.ShowDialogAsync(input) != ContentDialogResult.Primary)
            return;
        var directory = input.Value;
        if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("CombinedSession_CopyInTitle", "Copy into container"),
                UiText.Get("CombinedSession_CopyInConfirmation",
                    "Copy \"{0}\" into \"{1}\" in container \"{2}\" (session \"{3}\")? Existing files may be overwritten.",
                    hostPath, directory, target.Name, target.Session.Name),
                UiText.Get("CombinedSession_CopyButton", "Copy"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
            return;
        var result = await _actions.CopyToAsync(target, hostPath, directory);
        await ShowCopyResultAsync(result);
    });

    /// <summary>Prompts for a container path and copies it into a selected host directory.</summary>
    public Task CopyFromContainerAsync(CombinedSessionRow row, string hostDirectory) => RunResourceAsync(row, async () =>
    {
        var target = CaptureContainer(row);
        var input = new SimpleInputDialog(
            UiText.Get("CombinedSession_CopyOutTitle", "Copy from container"),
            UiText.Get("CombinedSession_CopyOutPath", "Absolute container file or directory path"),
            "/tmp/example");
        if (await _dialogs.ShowDialogAsync(input) != ContentDialogResult.Primary)
            return;
        var path = input.Value;
        if (path.Length > 0 && path.All(character => character == '/'))
            throw new InvalidOperationException(UiText.Get("CombinedSession_CopyRootRefused",
                "Choose a file or directory below the container root."));
        if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("CombinedSession_CopyOutTitle", "Copy from container"),
                UiText.Get("CombinedSession_CopyOutConfirmation",
                    "Copy \"{0}\" from container \"{1}\" (session \"{2}\") into \"{3}\"? Existing files may be overwritten.",
                    path, target.Name, target.Session.Name, hostDirectory),
                UiText.Get("CombinedSession_CopyButton", "Copy"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
            return;
        var result = await _actions.CopyFromAsync(target, path, hostDirectory);
        await ShowCopyResultAsync(result);
    });

    private Task ShowCopyResultAsync(CommandResult result) => _dialogs.ShowLocalizedMessageAsync(
        result.Success ? UiText.Get("CombinedSession_CopyComplete", "Copy complete")
            : UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"),
        result.Success ? UiText.Get("CombinedSession_CopyCompleteMessage", "The copy completed.") : result.ErrorText);

    /// <summary>Reports a host file-picker failure without altering container data.</summary>
    public Task ShowPickerErrorAsync(Exception error)
    {
        _logger.LogWarning(error, "A session-qualified file picker failed.");
        return _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"), error.Message);
    }

    /// <summary>Confirms and removes one image, volume, or non-built-in network from its owner.</summary>
    public Task RemoveResourceAsync(CombinedSessionRow row) => RunResourceAsync(row, async () =>
    {
        WslcResourceActionTarget target;
        string confirmation;
        if (row.Image is { } image)
        {
            target = WslcResourceActionTarget.Capture(row.Session, image);
            confirmation = UiText.Get("CombinedSession_RemoveImageConfirmation",
                "Remove image \"{0}\" from session \"{1}\"? Other tags for the same image may also be affected.",
                target.Name, target.Session.Name);
        }
        else if (row.Volume is { } volume)
        {
            target = await _resources.PrepareVolumeRemovalAsync(row.Session, volume.Name);
            var created = target.CreatedAt ?? throw new InvalidOperationException(UiText.Get("CombinedSession_VolumeIdentityUnknown",
                "The volume's creation identity could not be verified; no removal was attempted."));
            confirmation = UiText.Get("CombinedSession_RemoveVolumeConfirmation",
                "Remove volume \"{0}\" from session \"{1}\"? All data in the volume will be lost.\n\nThe volume currently has creation time {2}; it may differ from the loaded snapshot.",
                target.Name, target.Session.Name, created.ToLocalTime().ToString("g"));
        }
        else if (row.Network is { } network)
        {
            target = WslcResourceActionTarget.Capture(row.Session, network);
            confirmation = UiText.Get("CombinedSession_RemoveNetworkConfirmation",
                "Remove network \"{0}\" from session \"{1}\"?", target.Name, target.Session.Name);
        }
        else
        {
            throw new InvalidOperationException(UiText.Get("CombinedSession_NotResource",
                "The selected row is not a removable resource."));
        }

        if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("CombinedSession_RemoveResourceTitle", "Remove resource"),
                confirmation, UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
            return;

        _inventory.InvalidateSession(target.Session);
        var result = await _resources.RemoveAsync(target);
        if (!result.Success)
        {
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"), result.ErrorText);
            return;
        }
        if (target.Kind == WslcResourceActionTarget.ResourceKind.Image)
            _aiCapabilities.Invalidate();
        await _inventory.RefreshSessionAsync(target.Session);
    });

    /// <summary>Opens a validated TCP endpoint in the user's browser.</summary>
    public Task OpenEndpointAsync(CombinedSessionRow row) => RunResourceAsync(row, async () =>
    {
        var uri = row.GetEndpointUri() ?? throw new InvalidOperationException(UiText.Get("CombinedSession_InvalidEndpoint",
            "The endpoint does not have a usable TCP address."));
        var port = row.Port ?? throw new InvalidOperationException(UiText.Get("CombinedSession_InvalidEndpoint",
            "The endpoint does not have a usable TCP address."));
        await _actions.VerifyEndpointAsync(CaptureContainer(row), port);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
    });

    private static (WslcResourceActionTarget.ResourceKind Kind, string Id) Identify(CombinedSessionRow row)
    {
        if (row.Container is { } container && row.Port is null)
            return (WslcResourceActionTarget.ResourceKind.Container, container.Id);
        if (row.Image is { } image)
            return (WslcResourceActionTarget.ResourceKind.Image, image.Id);
        if (row.Volume is { } volume)
            return (WslcResourceActionTarget.ResourceKind.Volume, volume.Name);
        if (row.Network is { Id: { } id } && !string.IsNullOrWhiteSpace(id))
            return (WslcResourceActionTarget.ResourceKind.Network, id);
        if (row.Network is { IsBuiltIn: true } network)
            return (WslcResourceActionTarget.ResourceKind.Network, network.Name);
        throw new InvalidOperationException(UiText.Get("CombinedSession_NotResource",
            "The selected row is not a removable resource."));
    }

    private static WslcContainerActionTarget CaptureContainer(CombinedSessionRow row) =>
        row.Container is { } container
            ? WslcContainerActionTarget.Capture(row.Session, container)
            : throw new InvalidOperationException(UiText.Get("CombinedSession_NotContainer",
                "The selected row is not a container."));

    private async Task RunResourceAsync(CombinedSessionRow row, Func<Task> action)
    {
        if (!CanInteract)
            return;
        using var selection = _sessionContext.LockSelection();
        if (!await EnsureCurrentRowAsync(row))
            return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "A session-qualified resource action was cancelled.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionInterrupted", "Resource action interrupted"),
                UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException or ArgumentException)
        {
            _logger.LogWarning(ex, "A session-qualified resource action failed.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Whether a user-confirmed action or its follow-up refresh is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isBusy;

    /// <summary>Whether selection and row actions can safely start.</summary>
    public bool CanInteract => !IsBusy && !_inventory.IsBusy;

    /// <summary>Executes one lifecycle action against the row's captured session.</summary>
    public async Task ExecuteContainerAsync(CombinedSessionRow row, WslcContainerAction action)
    {
        if (!CanInteract)
            return;
        using var selection = _sessionContext.LockSelection();
        if (!await EnsureCurrentRowAsync(row))
            return;
        if (row.Container is null)
            throw new ArgumentException(UiText.Get("CombinedSession_NotContainer",
                "The selected row is not a container."), nameof(row));

        var target = WslcContainerActionTarget.Capture(row.Session, row.Container);
        IsBusy = true;
        try
        {
            if (action == WslcContainerAction.Remove &&
                !await _dialogs.ShowLocalizedConfirmAsync(
                    UiText.Get("CombinedSession_RemoveTitle", "Remove container"),
                    UiText.Get("CombinedSession_RemoveConfirmation",
                        "Remove container \"{0}\" from session \"{1}\"? This cannot be undone. Volumes are kept.",
                        target.Name, target.Session.Name),
                    UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
                return;

            _inventory.InvalidateSession(target.Session);
            var result = await _actions.ExecuteAsync(target, action);
            if (!result.Success)
            {
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ActionFailed", "Container action failed"), result.ErrorText);
                return;
            }
            if (action == WslcContainerAction.Remove)
                _aiCapabilities.Invalidate();
            await _inventory.RefreshSessionAsync(target.Session);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "A session-qualified container action was cancelled.");
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("CombinedSession_ActionInterrupted", "Container action interrupted"),
                UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException or ArgumentException)
        {
            _logger.LogWarning(ex, "A session-qualified container action failed.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ActionFailed", "Container action failed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnsureCurrentRowAsync(CombinedSessionRow row)
    {
        if (_inventory.IsCurrentRow(row))
            return true;
        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
            UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        return false;
    }

    private void OnInventoryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CombinedSessionViewModel.IsBusy))
            OnPropertyChanged(nameof(CanInteract));
    }

    /// <summary>Releases view-model event subscriptions.</summary>
    public void Dispose() => _inventory.PropertyChanged -= OnInventoryChanged;
}
