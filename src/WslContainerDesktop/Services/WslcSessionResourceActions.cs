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

using System.Globalization;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Inspects and removes session-qualified images, volumes, and networks.</summary>
public sealed class WslcSessionResourceActions
{
    private readonly IWslcService _discovery;
    private readonly WslcSessionContext _context;
    private readonly Func<WslcSessionInfo, string, bool, IWslcService> _createClient;
    private readonly Func<string> _executablePath;
    private readonly Func<string> _defaultSessionName;
    private readonly Action _afterManagedAction;

#if !WSLCD_TEST_NO_DISPATCHER
    /// <summary>Creates resource actions without extending background management to viewed sessions.</summary>
    public WslcSessionResourceActions(IWslcService discovery, WslcSessionContext context,
        WslcSessionClientFactory clients, ISettingsService settings, StatusMonitor monitor)
        : this(discovery, context, clients.Create, () => settings.WslcPath,
            WslcSessionActionGuard.ResolveDefaultSessionName, monitor.RequestRefresh)
    {
    }
#endif

    internal WslcSessionResourceActions(IWslcService discovery, WslcSessionContext context,
        Func<WslcSessionInfo, string, bool, IWslcService> createClient,
        Func<string> executablePath, Func<string> defaultSessionName, Action afterManagedAction)
    {
        _discovery = discovery;
        _context = context;
        _createClient = createClient;
        _executablePath = executablePath;
        _defaultSessionName = defaultSessionName;
        _afterManagedAction = afterManagedAction;
    }

    /// <summary>Inspects a resource only in its captured session generation.</summary>
    public async Task<CommandResult> InspectAsync(WslcSessionInfo session,
        WslcResourceActionTarget.ResourceKind kind, string id, CancellationToken ct = default)
    {
        ValidateSessionAndId(session, id);
        using var selection = _context.LockSelection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ct = timeout.Token;
        var executable = _executablePath();
        var managed = WslcSessionActionGuard.IsManaged(_context, session, _defaultSessionName);
        await WslcSessionActionGuard.VerifyAsync(_discovery, _executablePath, session, executable, ct).ConfigureAwait(false);
        var client = _createClient(session, executable, managed);
        var result = kind switch
        {
            WslcResourceActionTarget.ResourceKind.Container => await client.InspectContainerAsync(id, ct).ConfigureAwait(false),
            WslcResourceActionTarget.ResourceKind.Image => await client.InspectImageAsync(id, ct).ConfigureAwait(false),
            WslcResourceActionTarget.ResourceKind.Volume => await client.InspectVolumeAsync(id, ct).ConfigureAwait(false),
            WslcResourceActionTarget.ResourceKind.Network => await client.InspectNetworkAsync(id, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await WslcSessionActionGuard.VerifyAsync(_discovery, _executablePath, session, executable, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Captures a volume's creation identity before a deletion confirmation is shown.</summary>
    public async Task<WslcResourceActionTarget> PrepareVolumeRemovalAsync(
        WslcSessionInfo session, string name, CancellationToken ct = default)
    {
        var result = await InspectAsync(session, WslcResourceActionTarget.ResourceKind.Volume, name, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorText);
        var created = ReadVolumeCreationTime(result.StandardOutput, name);
        return new WslcResourceActionTarget(session, WslcResourceActionTarget.ResourceKind.Volume, name, name, created);
    }

    /// <summary>Revalidates the captured resource and removes it from exactly one session.</summary>
    public async Task<CommandResult> RemoveAsync(WslcResourceActionTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateSessionAndId(target.Session, target.Id);
        if (target.Session.CreatorPid <= 0 || string.IsNullOrWhiteSpace(target.Name) ||
            target.Kind == WslcResourceActionTarget.ResourceKind.Volume &&
                (target.CreatedAt is null || target.Id != target.Name))
            throw ChangedResource();
        if (target.Kind == WslcResourceActionTarget.ResourceKind.Network && IsBuiltInNetwork(target.Name))
            throw new InvalidOperationException(UiText.Get("CombinedSession_BuiltInNetwork",
                "Built-in networks cannot be removed."));

        using var selection = _context.LockSelection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ct = timeout.Token;
        var executable = _executablePath();
        var managed = WslcSessionActionGuard.IsManaged(_context, target.Session, _defaultSessionName);
        await WslcSessionActionGuard.VerifyAsync(_discovery, _executablePath, target.Session, executable, ct).ConfigureAwait(false);
        var client = _createClient(target.Session, executable, managed);
        var valid = target.Kind switch
        {
            WslcResourceActionTarget.ResourceKind.Image =>
                (await client.ListImagesAsync(ct).ConfigureAwait(false)).Count(image =>
                    image.Id == target.Id && image.Reference == target.Name) == 1,
            WslcResourceActionTarget.ResourceKind.Volume =>
                await IsSameVolumeAsync(client, target, ct).ConfigureAwait(false),
            WslcResourceActionTarget.ResourceKind.Network =>
                IsSameNetwork(await client.ListNetworksAsync(ct).ConfigureAwait(false), target),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        if (!valid)
            throw ChangedResource();
        await WslcSessionActionGuard.VerifyAsync(_discovery, _executablePath, target.Session, executable, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            return target.Kind switch
            {
                WslcResourceActionTarget.ResourceKind.Image => await client.RemoveImageAsync(target.Id, force: false, ct: ct).ConfigureAwait(false),
                WslcResourceActionTarget.ResourceKind.Volume => await client.RemoveVolumeAsync(target.Id, ct).ConfigureAwait(false),
                WslcResourceActionTarget.ResourceKind.Network => await client.RemoveNetworkAsync(target.Name, ct).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
        }
        finally
        {
            if (managed)
                _afterManagedAction();
        }
    }

    private static async Task<bool> IsSameVolumeAsync(IWslcService client, WslcResourceActionTarget target, CancellationToken ct)
    {
        var volumes = await client.ListVolumesAsync(ct).ConfigureAwait(false);
        if (volumes.Count(volume => volume.Name == target.Name) != 1)
            return false;
        var inspect = await client.InspectVolumeAsync(target.Name, ct).ConfigureAwait(false);
        return inspect.Success && ReadVolumeCreationTime(inspect.StandardOutput, target.Name) == target.CreatedAt;
    }

    private static bool IsSameNetwork(IReadOnlyList<NetworkInfo> networks, WslcResourceActionTarget target)
    {
        var matches = networks.Where(network => network.Id == target.Id).Take(2).ToArray();
        return matches.Length == 1 && matches[0].Name == target.Name;
    }

    internal static DateTimeOffset ReadVolumeCreationTime(string json, string expectedName)
    {
        var records = WslcJsonParser.ParseList<JsonElement>(json);
        if (records.Count != 1 || records[0].ValueKind != JsonValueKind.Object ||
            !records[0].TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String ||
            name.GetString() != expectedName ||
            !records[0].TryGetProperty("CreatedAt", out var created) || created.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(created.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
            timestamp <= DateTimeOffset.UnixEpoch)
        {
            throw new InvalidOperationException(UiText.Get("CombinedSession_VolumeIdentityUnknown",
                "The volume's creation identity could not be verified; no removal was attempted."));
        }
        return timestamp;
    }

    private static bool IsBuiltInNetwork(string name) =>
        name.Equals("bridge", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static void ValidateSessionAndId(WslcSessionInfo session, string id)
    {
        if (session is null || session.ID <= 0 || session.CreatorPid <= 0 ||
            string.IsNullOrWhiteSpace(session.Name) || string.IsNullOrWhiteSpace(id))
            throw ChangedResource();
    }

    private static InvalidOperationException ChangedResource() =>
        new(UiText.Get("CombinedSession_ResourceChanged",
            "The resource changed or is no longer available; refresh before taking action."));
}
