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

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcSessionContainerActionsTests
{
    private static readonly WslcSessionInfo Session = new() { ID = 7, CreatorPid = 100, Name = "target" };
    private static readonly ContainerInfo Container = new()
    {
        Id = "123456789abc", Name = "web", Image = "example", CreatedAt = 123, CreatedAtKnown = true,
    };

    [Fact]
    public async Task GpuDisplayCacheAvoidsRepeatedVerificationAndInvalidatesOnLifecycleAndRefresh()
    {
        var running = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt, CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
        };
        var calls = new List<string>();
        var discoveries = 0;
        var actions = Create(new WslcSessionContext(), () =>
        {
            discoveries++;
            return [Session];
        }, (_, _, _) => Client(running, calls));
        var target = WslcContainerActionTarget.Capture(Session, running);
        Assert.True((await actions.ReadGpuInfoAsync(target, stateChangedAt: 100)).HasGpu);
        var initialDiscoveries = discoveries;
        Assert.True((await actions.ReadGpuInfoAsync(target, stateChangedAt: 100)).HasGpu);
        Assert.Equal(initialDiscoveries, discoveries);
        Assert.Equal(2, calls.Count);

        await actions.ReadGpuInfoAsync(target, stateChangedAt: 101);
        Assert.Equal(4, calls.Count);
        actions.InvalidateGpuInfo();
        await actions.ReadGpuInfoAsync(target, stateChangedAt: 101);
        Assert.Equal(6, calls.Count);
        await actions.ExecuteAsync(target, WslcContainerAction.Restart);
        await actions.ReadGpuInfoAsync(target, stateChangedAt: 101);
        Assert.Equal(10, calls.Count);
    }

    [Fact]
    public async Task GpuCacheDoesNotCrossExecutableOrSessionGenerationOrBypassCancellation()
    {
        var running = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt, CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
        };
        var session = Session;
        var executable = "engine";
        var calls = new List<string>();
        var actions = Create(new WslcSessionContext(), () => [session],
            (_, _, _) => Client(running, calls), path: () => executable);
        var target = WslcContainerActionTarget.Capture(session, running);
        await actions.ReadGpuInfoAsync(target, stateChangedAt: 100);
        executable = "other-engine";
        await actions.ReadGpuInfoAsync(target, stateChangedAt: 100);
        session = session with { CreatorPid = 999 };
        target = WslcContainerActionTarget.Capture(session, running);
        await actions.ReadGpuInfoAsync(target, stateChangedAt: 100);
        Assert.Equal(6, calls.Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ReadGpuInfoAsync(target, cancellation.Token, 100));
        Assert.Equal(6, calls.Count);
    }

    [Fact]
    public async Task GpuDisplayCacheEvictsOldResultsInsteadOfGrowingWithEveryVisitedContainer()
    {
        var running = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt, CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
        };
        var calls = new List<string>();
        var actions = Create(new WslcSessionContext(), () => [Session], (_, _, _) => Client(running, calls));
        var target = WslcContainerActionTarget.Capture(Session, running);
        for (ulong generation = 1; generation <= 257; generation++)
            await actions.ReadGpuInfoAsync(target, stateChangedAt: generation);
        await actions.ReadGpuInfoAsync(target, stateChangedAt: 257);
        Assert.Equal(514, calls.Count);
        await actions.ReadGpuInfoAsync(target, stateChangedAt: 1);
        Assert.Equal(516, calls.Count);
    }

    [Theory]
    [InlineData("target", true)]
    [InlineData("different", false)]
    [InlineData("TARGET", false)]
    [InlineData(null, false)]
    public async Task ActionUsesOwningSessionAndOnlyRefreshesSelectedSession(string? selected, bool managed)
    {
        var context = new WslcSessionContext();
        context.Select(selected);
        var clientManaged = false;
        var calls = new List<string>();
        var before = 0;
        var after = 0;
        var actions = Create(context, () => [Session],
            (session, path, isManaged) =>
            {
                Assert.Equal(Session, session);
                Assert.Equal("engine", path);
                clientManaged = isManaged;
                return Client(Container, calls);
            },
            (_, _) => before++, () => after++);

        var result = await actions.ExecuteAsync(WslcContainerActionTarget.Capture(Session, Container), WslcContainerAction.Stop);

        Assert.True(result.Success);
        Assert.True(clientManaged);
        Assert.Equal(["list", "stop:123456789abc"], calls);
        Assert.Equal(managed ? 1 : 0, before);
        Assert.Equal(managed ? 1 : 0, after);
        Assert.Equal(selected, context.SelectedSessionName);
    }

    [Fact]
    public void ManualStopSuppressesOnlyTheOwningSession()
    {
        var suppression = new RestartSuppressionState();
        var target = WslcContainerActionTarget.Capture(Session, Container);

        WslcSessionContainerActions.SuppressManualAction(suppression, WslcContainerAction.Stop, target,
            "engine", () => "wslc-cli-user");

        Assert.True(suppression.IsSuppressed("web", AutomationSessionOwner.Capture("engine", new("target"))));
        Assert.False(suppression.IsSuppressed("web", AutomationSessionOwner.Capture("engine", new("other"))));
    }

    [Fact]
    public async Task ReplacedSessionIsNeverMutated()
    {
        var context = new WslcSessionContext();
        context.Select("target");
        var discovery = 0;
        var calls = new List<string>();
        var actions = Create(context, () => ++discovery == 1 ? [Session] : [Session with { ID = 8 }],
            (_, _, _) => Client(Container, calls));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.ExecuteAsync(WslcContainerActionTarget.Capture(Session, Container), WslcContainerAction.Remove));

        Assert.Equal(["list"], calls);
        Assert.True(context.TrySelect("another"));
    }

    [Fact]
    public async Task DefaultCliSessionUsesTheExistingManagementScope()
    {
        var context = new WslcSessionContext();
        var session = Session with { Name = "wslc-cli-user" };
        var managed = false;
        var actions = Create(context, () => [session],
            (_, _, value) =>
            {
                managed = value;
                return Client(Container, []);
            },
            defaultName: () => "wslc-cli-user");

        var result = await actions.ExecuteAsync(WslcContainerActionTarget.Capture(session, Container), WslcContainerAction.Stop);

        Assert.True(result.Success);
        Assert.True(managed);
    }

    [Fact]
    public async Task AmbiguousDefaultSessionCasingFailsClosed()
    {
        var context = new WslcSessionContext();
        var session = Session with { Name = "wslc-cli-USER" };
        var calls = new List<string>();
        var actions = Create(context, () => [session], (_, _, _) => Client(Container, calls),
            defaultName: () => "wslc-cli-user");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.ExecuteAsync(WslcContainerActionTarget.Capture(session, Container), WslcContainerAction.Stop));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task ChangedContainerIsNeverMutated()
    {
        var context = new WslcSessionContext();
        var replacement = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt + 1, CreatedAtKnown = true,
        };
        var calls = new List<string>();
        var actions = Create(context, () => [Session], (_, _, _) => Client(replacement, calls));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.ExecuteAsync(WslcContainerActionTarget.Capture(Session, Container), WslcContainerAction.Start));

        Assert.Equal(["list"], calls);
    }

    [Fact]
    public async Task LogsRemainBoundToTheOwnerAndDoNotTouchManagement()
    {
        var context = new WslcSessionContext();
        context.Select("target");
        var calls = new List<string>();
        var before = 0;
        var actions = Create(context, () => [Session],
            (_, _, managed) =>
            {
                Assert.False(managed);
                return Client(Container, calls);
            }, (_, _) => before++);

        var result = await actions.ReadLogsAsync(WslcContainerActionTarget.Capture(Session, Container));

        Assert.Equal("sample log", result.StandardOutput);
        Assert.Equal(["list", "logs:123456789abc"], calls);
        Assert.Equal(0, before);
    }

    [Fact]
    public async Task LongReadOnlyCopyDoesNotBlockChangingTheSelectedSession()
    {
        var context = new WslcSessionContext();
        context.Select("target");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Create(context, () => [Session], (session, _, _) =>
        {
            Assert.Equal(Session, session);
            return NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([Container]),
                nameof(IWslcService.CopyFromContainerAsync) => CopyAsync(),
                _ => throw new InvalidOperationException(method.Name),
            });
        });
        async Task<CommandResult> CopyAsync()
        {
            started.TrySetResult();
            await release.Task;
            return new CommandResult();
        }

        var copy = actions.CopyFromAsync(WslcContainerActionTarget.Capture(Session, Container),
            "/payload", @"C:\downloads");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(context.TrySelect("another"));
        release.TrySetResult();
        Assert.True((await copy).Success);
        Assert.Equal("another", context.SelectedSessionName);
    }

    [Fact]
    public async Task StatsRejectAnotherContainerIdentity()
    {
        var running = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt, CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
        };
        var calls = new List<string>();
        var actions = Create(new WslcSessionContext(), () => [Session], (_, _, _) => Client(running, calls));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.ReadStatsAsync(WslcContainerActionTarget.Capture(Session, running)));
        Assert.Equal(["list", "stats:123456789abc"], calls);
    }

    [Fact]
    public async Task GpuProbeUsesCapturedSessionAndRejectsReplacement()
    {
        var running = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt, CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
        };
        var context = new WslcSessionContext();
        context.Select("other");
        var calls = new List<string>();
        var discoveries = 0;
        var actions = Create(context, () => ++discoveries <= 2 ? [Session] : [Session with { ID = 9 }],
            (session, _, managed) =>
            {
                Assert.Equal(Session, session);
                Assert.False(managed);
                return Client(running, calls);
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.ReadGpuInfoAsync(WslcContainerActionTarget.Capture(Session, running)));

        Assert.Equal(["list", "gpu:123456789abc"], calls);
        Assert.Equal("other", context.SelectedSessionName);
    }

    [Theory]
    [InlineData("""{"Id":"123456789abc","Ports":{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}]}}""", true)]
    [InlineData("""{"Id":"123456789abc","Ports":{"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"8080"}]}}""", false)]
    [InlineData("""{"Id":"123456789abc","Ports":{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"8081"}]}}""", false)]
    [InlineData("""{"Id":"123456789abc","Ports":{"81/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}]}}""", false)]
    [InlineData("""{"Id":"different","Ports":{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}]}}""", false)]
    [InlineData("""{"Id":"123456789abc"}""", false)]
    public async Task EndpointVerificationRequiresSameOwnerAndPort(string inspect, bool expected)
    {
        var running = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt, CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
        };
        var calls = new List<string>();
        var actions = Create(new WslcSessionContext(), () => [Session],
            (_, _, _) => Client(running, calls, inspect: new CommandResult { StandardOutput = inspect }));
        var target = WslcContainerActionTarget.Capture(Session, running);
        var port = new PortMapping { BindingAddress = "", HostPort = 8080, ContainerPort = 80, Protocol = 6 };

        if (expected)
            await actions.VerifyEndpointAsync(target, port);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.VerifyEndpointAsync(target, port));

        Assert.Equal(["list", "inspect:123456789abc"], calls);
    }

    [Fact]
    public async Task EndpointVerificationRejectsReplacedSessionBeforeInspect()
    {
        var calls = new List<string>();
        var actions = Create(new WslcSessionContext(), () => [Session with { ID = 8 }],
            (_, _, _) => Client(Container, calls));
        var port = new PortMapping { HostPort = 8080, ContainerPort = 80, Protocol = 6 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.VerifyEndpointAsync(WslcContainerActionTarget.Capture(Session, Container), port));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task EndpointVerificationSurfacesInspectErrors()
    {
        var running = new ContainerInfo
        {
            Id = Container.Id, Name = Container.Name, Image = Container.Image,
            CreatedAt = Container.CreatedAt, CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
        };
        var actions = Create(new WslcSessionContext(), () => [Session],
            (_, _, _) => Client(running, [], inspect: new CommandResult { ExitCode = 1, StandardError = "inspect failed" }));
        var port = new PortMapping { HostPort = 8080, ContainerPort = 80, Protocol = 6 };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.VerifyEndpointAsync(WslcContainerActionTarget.Capture(Session, running), port));
        Assert.Contains("inspect failed", error.Message);
    }

    [Fact]
    public async Task FileTransfersUseTheCapturedOwnerAndPreservePaths()
    {
        var context = new WslcSessionContext();
        context.Select("other");
        var calls = new List<string>();
        var actions = Create(context, () => [Session], (_, _, managed) =>
        {
            Assert.False(managed);
            return Client(Container, calls);
        });
        var target = WslcContainerActionTarget.Capture(Session, Container);

        Assert.True((await actions.CopyToAsync(target, @"C:\files\a b.txt", "/target dir")).Success);
        Assert.True((await actions.CopyFromAsync(target, "/source/file ", @"C:\downloads")).Success);

        Assert.Equal(["list", @"copy-in:123456789abc:C:\files\a b.txt:/target dir",
            "list", @"copy-out:123456789abc:/source/file :C:\downloads"], calls);
        Assert.Equal("other", context.SelectedSessionName);
    }

    [Fact]
    public async Task MissingCreationIdentityIsRejectedBeforeDiscovery()
    {
        var context = new WslcSessionContext();
        var discovery = 0;
        var target = WslcContainerActionTarget.Capture(Session, Container) with { CreatedAtKnown = false };
        var actions = Create(context, () => { discovery++; return [Session]; },
            (_, _, _) => Client(Container, []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExecuteAsync(target, WslcContainerAction.Remove));

        Assert.Equal(0, discovery);
    }

    [Fact]
    public async Task SentinelCreationIdentityIsRejectedBeforeDiscovery()
    {
        var context = new WslcSessionContext();
        var discovery = 0;
        var target = WslcContainerActionTarget.Capture(Session, Container) with { CreatedAt = 0 };
        var actions = Create(context, () => { discovery++; return [Session]; },
            (_, _, _) => Client(Container, []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExecuteAsync(target, WslcContainerAction.Remove));

        Assert.Equal(0, discovery);
    }

    [Fact]
    public async Task SelectionCannotChangeUntilActionCompletes()
    {
        var context = new WslcSessionContext();
        context.Select("target");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Create(context, () => [Session], (_, _, _) =>
            Client(Container, [], async () =>
            {
                started.TrySetResult();
                await release.Task;
                return new CommandResult();
            }));

        var operation = actions.ExecuteAsync(WslcContainerActionTarget.Capture(Session, Container), WslcContainerAction.Stop);
        await started.Task;
        Assert.False(context.TrySelect("other"));
        release.SetResult();
        Assert.True((await operation).Success);
        Assert.True(context.TrySelect("other"));
    }

    [Fact]
    public async Task ExecutableChangeBeforeMutationIsRejected()
    {
        var context = new WslcSessionContext();
        var path = "engine";
        var calls = new List<string>();
        var discovery = 0;
        var actions = Create(context, () =>
        {
            if (++discovery == 2)
                path = "other-engine";
            return [Session];
        }, (_, _, _) => Client(Container, calls), path: () => path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.ExecuteAsync(WslcContainerActionTarget.Capture(Session, Container), WslcContainerAction.Stop));

        Assert.Equal(["list"], calls);
    }

    private static WslcSessionContainerActions Create(
        WslcSessionContext context,
        Func<IReadOnlyList<WslcSessionInfo>> sessions,
        Func<WslcSessionInfo, string, bool, IWslcService> client,
        Action<WslcContainerAction, WslcContainerActionTarget>? before = null,
        Action? after = null,
        Func<string>? path = null,
        Func<string>? defaultName = null)
    {
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = sessions() },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        return new WslcSessionContainerActions(discovery, context, client, path ?? (() => "engine"),
            defaultName ?? (() => "wslc-cli-user"), before ?? ((_, _) => { }), after ?? (() => { }));
    }

    private static IWslcService Client(ContainerInfo current, List<string> calls, Func<Task<CommandResult>>? mutation = null,
        CommandResult? inspect = null)
    {
        Task<IReadOnlyList<ContainerInfo>> RecordList()
        {
            calls.Add("list");
            return Task.FromResult<IReadOnlyList<ContainerInfo>>([current]);
        }

        Task<CommandResult> RecordMutation(string name, object?[] args)
        {
            calls.Add($"{name}:{args[0]}");
            return mutation?.Invoke() ?? Task.FromResult(new CommandResult());
        }

        return NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => RecordList(),
            nameof(IWslcService.StartContainerAsync) => RecordMutation("start", args),
            nameof(IWslcService.StopContainerAsync) => RecordMutation("stop", args),
            nameof(IWslcService.RestartContainerAsync) => RecordMutation("restart", args),
            nameof(IWslcService.KillContainerAsync) => RecordMutation("kill", args),
            nameof(IWslcService.RemoveContainerAsync) => RecordMutation("remove", args),
            nameof(IWslcService.GetLogsAsync) => RecordLogs(args),
            nameof(IWslcService.GetStatsAsync) => RecordStats(args),
            nameof(IWslcService.GetGpuInfoAsync) => RecordGpu(args),
            nameof(IWslcService.InspectContainerAsync) => RecordInspect(args),
            nameof(IWslcService.CopyToContainerAsync) => RecordCopy("copy-in", args),
            nameof(IWslcService.CopyFromContainerAsync) => RecordCopy("copy-out", args),
            _ => throw new InvalidOperationException(method.Name),
        });

        Task<CommandResult> RecordLogs(object?[] args)
        {
            calls.Add($"logs:{args[0]}");
            return Task.FromResult(new CommandResult { StandardOutput = "sample log" });
        }

        Task<ContainerStats?> RecordStats(object?[] args)
        {
            calls.Add($"stats:{args[0]}");
            return Task.FromResult<ContainerStats?>(new ContainerStats { Id = "different-id" });
        }

        Task<(bool HasGpu, string? GpuName)> RecordGpu(object?[] args)
        {
            calls.Add($"gpu:{args[0]}");
            return Task.FromResult<(bool, string?)>((true, "test GPU"));
        }

        Task<CommandResult> RecordInspect(object?[] args)
        {
            calls.Add($"inspect:{args[0]}");
            return Task.FromResult(inspect ?? new CommandResult());
        }

        Task<CommandResult> RecordCopy(string name, object?[] args)
        {
            calls.Add($"{name}:{args[0]}:{args[1]}:{args[2]}");
            return Task.FromResult(new CommandResult());
        }
    }
}
