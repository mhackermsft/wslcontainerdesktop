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

using System.Net;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Executes container actions against an explicitly captured session and resource.</summary>
public sealed class WslcSessionContainerActions
{
    private readonly IWslcService _discovery;
    private readonly WslcSessionContext _context;
    private readonly Func<WslcSessionInfo, string, bool, IWslcService> _createClient;
    private readonly Func<WslcSessionInfo, string, bool, IWslcService>? _createGuardedClient;
    private readonly Func<string> _executablePath;
    private readonly Func<string> _defaultSessionName;
    private readonly Action<WslcContainerAction, WslcContainerActionTarget> _beforeManagedAction;
    private readonly Action _afterManagedAction;
    private readonly Action<WslcContainerAction, WslcContainerActionTarget, string> _beforeAnyAction;
    private readonly Action _afterAnyAction;
    private readonly object _gpuCacheGate = new();
    private readonly Dictionary<(string Executable, WslcContainerActionTarget Target, ulong StateChangedAt),
        (DateTimeOffset Expires, (bool HasGpu, string? GpuName) Result)> _gpuCache = [];

    /// <summary>Invalidates display-only GPU results for an explicit refresh.</summary>
    public void InvalidateGpuInfo()
    {
        lock (_gpuCacheGate) _gpuCache.Clear();
    }

#if !WSLCD_TEST_NO_DISPATCHER
    /// <summary>Creates session-qualified actions while preserving the existing managed session's watchdog behavior.</summary>
    public WslcSessionContainerActions(
        IWslcService discovery,
        WslcSessionContext context,
        WslcSessionClientFactory clients,
        ISettingsService settings,
        StatusMonitor monitor,
        RestartSuppressionState suppression)
        : this(discovery, context, clients.CreateForContainerActions, () => settings.WslcPath, WslcSessionActionGuard.ResolveDefaultSessionName,
            (action, target) =>
            {
                if (action is not WslcContainerAction.Start)
                    monitor.SuppressExitNotification(target.Id);
            },
            monitor.RequestRefresh,
            (action, target, executable) =>
                SuppressManualAction(suppression, action, target, executable, WslcSessionActionGuard.ResolveDefaultSessionName),
            monitor.RequestAutomationRefresh,
            createGuardedClient: clients.CreateGuarded)
    {
    }

#endif

    internal static void SuppressManualAction(RestartSuppressionState suppression, WslcContainerAction action,
        WslcContainerActionTarget target, string executable, Func<string> defaultSessionName)
    {
        if (action is not (WslcContainerAction.Stop or WslcContainerAction.Kill or WslcContainerAction.Remove))
            return;
        var targetName = string.Equals(target.Session.Name, defaultSessionName(), StringComparison.Ordinal)
            ? null : target.Session.Name;
        suppression.Suppress(target.Name, AutomationSessionOwner.Capture(executable, new(targetName)), target.Id);
    }

    internal WslcSessionContainerActions(
        IWslcService discovery,
        WslcSessionContext context,
        Func<WslcSessionInfo, string, bool, IWslcService> createClient,
        Func<string> executablePath,
        Func<string> defaultSessionName,
        Action<WslcContainerAction, WslcContainerActionTarget> beforeManagedAction,
        Action afterManagedAction,
        Action<WslcContainerAction, WslcContainerActionTarget, string>? beforeAnyAction = null,
        Action? afterAnyAction = null,
        Func<WslcSessionInfo, string, bool, IWslcService>? createGuardedClient = null)
    {
        _discovery = discovery;
        _context = context;
        _createClient = createClient;
        _createGuardedClient = createGuardedClient;
        _executablePath = executablePath;
        _defaultSessionName = defaultSessionName;
        _beforeManagedAction = beforeManagedAction;
        _afterManagedAction = afterManagedAction;
        _beforeAnyAction = beforeAnyAction ?? ((_, _, _) => { });
        _afterAnyAction = afterAnyAction ?? (() => { });
    }

    /// <summary>Revalidates the captured session and container before issuing one mutation.</summary>
    public async Task<CommandResult> ExecuteAsync(
        WslcContainerActionTarget target, WslcContainerAction action, CancellationToken ct = default,
        bool removeAnonymousVolumes = false)
    {
        ValidateTarget(target);
        lock (_gpuCacheGate)
            foreach (var key in _gpuCache.Keys.Where(key => key.Target == target).ToArray())
                _gpuCache.Remove(key);
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action));

        using var selection = _context.LockSelection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ct = timeout.Token;
        var executable = _executablePath();
        var managed = WslcSessionActionGuard.IsManaged(_context, target.Session, _defaultSessionName);
        var (client, _) = await PrepareAsync(target, executable, managed: true, ct, guardedClient: true).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _beforeAnyAction(action, target, executable);
        if (managed)
            _beforeManagedAction(action, target);

        try
        {
            return action switch
            {
                WslcContainerAction.Start => await client.StartContainerAsync(target.Id, ct).ConfigureAwait(false),
                WslcContainerAction.Stop => await client.StopContainerAsync(target.Id, ct).ConfigureAwait(false),
                WslcContainerAction.Restart => await client.RestartContainerAsync(target.Id, ct).ConfigureAwait(false),
                WslcContainerAction.Kill => await client.KillContainerAsync(target.Id, ct).ConfigureAwait(false),
                WslcContainerAction.Remove => await client.RemoveContainerAsync(target.Id, force: true, ct: ct,
                    removeAnonymousVolumes: removeAnonymousVolumes).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }

        finally
        {
            if (managed)
                _afterManagedAction();
            _afterAnyAction();
        }
    }

    /// <summary>Creates an immutable client only after revalidating the session and container identity.</summary>
    public async Task<IWslcService> CreateVerifiedClientAsync(WslcContainerActionTarget target, CancellationToken ct = default)
    {
        ValidateTarget(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var executable = _executablePath();
        var (client, _) = await PrepareAsync(target, executable, managed: true, timeout.Token,
            guardedClient: true).ConfigureAwait(false);
        return client;
    }

    /// <summary>Reads a bounded log snapshot from the captured container.</summary>
    public Task<CommandResult> ReadLogsAsync(WslcContainerActionTarget target, CancellationToken ct = default) =>
        ReadAsync(target, false, (client, token) => client.GetLogsAsync(target.Id, ct: token), ct);

    /// <summary>Inspects the captured container without following a reused row identity.</summary>
    public Task<CommandResult> InspectAsync(WslcContainerActionTarget target, CancellationToken ct = default) =>
        ReadAsync(target, false, (client, token) => client.InspectContainerAsync(target.Id, token), ct);

    /// <summary>Reads a current resource-usage snapshot from the captured running container.</summary>
    public async Task<ContainerStats?> ReadStatsAsync(WslcContainerActionTarget target, CancellationToken ct = default)
    {
        var result = await ReadAsync(target, true, (client, token) => client.GetStatsAsync(target.Id, token), ct).ConfigureAwait(false);
        if (result is not null && ContainerIdentity.ResolveId([target.Id], result.Id) is null)
            throw ChangedContainer();
        return result;
    }

    /// <summary>Reads GPU information from the captured running container's session.</summary>
    public async Task<(bool HasGpu, string? GpuName)> ReadGpuInfoAsync(WslcContainerActionTarget target,
        CancellationToken ct = default, ulong stateChangedAt = 0)
    {
        ValidateTarget(target);
        ct.ThrowIfCancellationRequested();
        var executable = _executablePath();
        var key = (executable, target, stateChangedAt);
        lock (_gpuCacheGate)
        {
            if (stateChangedAt > 0 && _gpuCache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Result;
        }
        var result = await ReadAsync(target, true, (client, token) => client.GetGpuInfoAsync(target.Id, token), ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (stateChangedAt == 0) return result;
        lock (_gpuCacheGate)
        {
            foreach (var expired in _gpuCache.Where(entry => entry.Value.Expires <= DateTimeOffset.UtcNow ||
                         !string.Equals(entry.Key.Executable, executable, StringComparison.OrdinalIgnoreCase))
                         .Select(entry => entry.Key).ToArray())
                _gpuCache.Remove(expired);
            if (_gpuCache.Count >= 256)
                _gpuCache.Remove(_gpuCache.MinBy(entry => entry.Value.Expires).Key);
            _gpuCache[key] = (DateTimeOffset.UtcNow.AddMinutes(5), result);
        }
        return result;
    }

    /// <summary>Confirms a running container still owns the captured published port.</summary>
    public async Task VerifyEndpointAsync(WslcContainerActionTarget target, PortMapping port, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(port);
        var hostPort = port.HostPort;
        var containerPort = port.ContainerPort;
        var protocol = port.Protocol;
        var address = NormalizeBindingAddress(port.BindingAddress);
        if (protocol != 6 || hostPort is <= 0 or > 65535 || containerPort is <= 0 or > 65535 || address is null)
            throw ChangedEndpoint();

        var result = await ReadAsync(target, true,
            (client, token) => client.InspectContainerAsync(target.Id, token), ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(UiText.Get("CombinedSession_EndpointInspectFailed",
                "Could not verify the endpoint: {0}", result.ErrorText));

        var records = WslcJsonParser.ParseList<JsonElement>(result.StandardOutput);
        if (records.Count != 1 ||
            !ContainerPortResolver.MatchesId(target.Id, ContainerInfoJsonConverter.ReadString(records[0], "Id")) ||
            !ContainerPortParser.TryInspect(records[0], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, out var ports) ||
            !ports.Any(current => current.HostPort == hostPort && current.ContainerPort == containerPort &&
                current.Protocol == protocol && NormalizeBindingAddress(current.BindingAddress) == address))
            throw ChangedEndpoint();
    }

    private static string? NormalizeBindingAddress(string? binding)
    {
        var text = binding?.Trim('[', ']');
        if (string.IsNullOrEmpty(text))
            return IPAddress.Any.ToString();
        return IPAddress.TryParse(text, out var address) ? address.ToString() : null;
    }

    private static InvalidOperationException ChangedEndpoint() =>
        new(UiText.Get("CombinedSession_EndpointChanged",
            "The endpoint changed or is no longer available; refresh before opening it."));

    /// <summary>Opens a terminal only after confirming the captured container is still running.</summary>
    public async Task OpenTerminalAsync(WslcContainerActionTarget target, CancellationToken ct = default)
    {
        await ReadAsync(target, true, async (client, _) =>
        {
            await client.OpenTerminal(target.Id);
            return true;
        }, ct, verifyAfter: false).ConfigureAwait(false);
    }

    /// <summary>Copies one host path into the captured container's owning session.</summary>
    public Task<CommandResult> CopyToAsync(WslcContainerActionTarget target, string hostPath,
        string containerDirectory, CancellationToken ct = default) =>
        ReadAsync(target, false,
            (client, token) => client.CopyToContainerAsync(target.Id, hostPath, containerDirectory, token),
            ct, verifyAfter: false);

    /// <summary>Copies a container path to the host without changing the app's selected session.</summary>
    public Task<CommandResult> CopyFromAsync(WslcContainerActionTarget target, string containerPath,
        string hostDirectory, CancellationToken ct = default) =>
        ReadAsync(target, false,
            (client, token) => client.CopyFromContainerAsync(target.Id, containerPath, hostDirectory, token),
            ct, verifyAfter: false);

    private async Task<T> ReadAsync<T>(WslcContainerActionTarget target, bool requireRunning,
        Func<IWslcService, CancellationToken, Task<T>> operation, CancellationToken ct,
        bool verifyAfter = true)
    {
        ValidateTarget(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ct = timeout.Token;
        var executable = _executablePath();
        var (client, current) = await PrepareAsync(target, executable, managed: false, ct,
            guardedClient: true).ConfigureAwait(false);
        if (requireRunning && current.State != ContainerState.Running)
            throw new InvalidOperationException(UiText.Get("CombinedSession_ContainerNotRunning",
                "The container is not running; refresh before taking action."));
        ct.ThrowIfCancellationRequested();
        var result = await operation(client, ct).ConfigureAwait(false);
        if (verifyAfter)
            await WslcSessionActionGuard.VerifyAsync(_discovery, _executablePath, target.Session, executable, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<(IWslcService Client, ContainerInfo Current)> PrepareAsync(
        WslcContainerActionTarget target, string executable, bool managed, CancellationToken ct,
        bool guardedClient = false)
    {
        var useGuardedClient = guardedClient && _createGuardedClient is not null;
        if (!useGuardedClient)
            await WslcSessionActionGuard.VerifyAsync(_discovery, _executablePath, target.Session, executable, ct).ConfigureAwait(false);
        var client = useGuardedClient
            ? _createGuardedClient!(target.Session, executable, managed)
            : _createClient(target.Session, executable, managed);
        var inventory = await client.ListContainersAsync(all: true, ct: ct, resolvePorts: false).ConfigureAwait(false);
        var matches = inventory.Where(container => container.Id == target.Id).Take(2).ToArray();
        if (matches.Length != 1 || !target.Matches(matches[0]))
            throw ChangedContainer();
        if (!useGuardedClient)
            await WslcSessionActionGuard.VerifyAsync(_discovery, _executablePath, target.Session, executable, ct).ConfigureAwait(false);
        return (client, matches[0]);
    }

    private static void ValidateTarget(WslcContainerActionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Session is null || target.Session.ID <= 0 || target.Session.CreatorPid <= 0 ||
            string.IsNullOrWhiteSpace(target.Session.Name) ||
            string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(target.Name) ||
            !target.CreatedAtKnown || target.CreatedAt <= 0)
        {
            throw new InvalidOperationException(UiText.Get("CombinedSession_UnstableContainer",
                "The container identity is incomplete; refresh before taking action."));
        }
    }

    private static InvalidOperationException ChangedContainer() =>
        new(UiText.Get("CombinedSession_ContainerChanged",
            "The container changed or is no longer available; refresh before taking action."));
}
