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

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class DevContainerLifecycleTests
{
    [Fact]
    public async Task CreateThenKeepDoesNotExecuteAnyContainerHookAgain()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        fixture.Commands.Clear();
        fixture.Compose.Engine.Mutations.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Empty(fixture.Commands);
        Assert.DoesNotContain(fixture.Compose.Engine.Mutations, m => m.StartsWith("start:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartingStoppedPrimaryRunsOnlyPostStart()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        fixture.Compose.Engine.States["demo_web"] = ContainerState.Stopped;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Equal(["started"], fixture.Commands);
    }

    [Fact]
    public async Task RecreatingPrimaryRunsCreationHooksForTheNewIdentity()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        fixture.Config.Compose!.Project.Services[0].Options.EnvironmentVariables.Add("UPDATED=yes");
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds.TakeLast(4), id => Assert.Equal("instance-2", id));
    }

    [Fact]
    public async Task SiblingFailureDoesNotLosePrimaryHooksOrRepeatThemOnRetry()
    {
        var fixture = new Fixture();
        fixture.Config.Compose!.Project.Services.Add(new() { Name = "other", Options = new() { Image = "fixture" } });
        fixture.Compose.Engine.FailRunAfterCreate = true;
        fixture.Compose.Engine.AfterRun = name =>
        {
            fixture.Compose.Engine.ContainerIds[name] = name;
            fixture.Compose.Engine.FailRunAfterCreate = name == "demo_other";
        };

        Assert.False((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        fixture.Reload();
        fixture.Compose.Engine.AfterRun = null;
        fixture.Compose.Engine.FailRunAfterCreate = false;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Theory]
    [InlineData("create", 0)]
    [InlineData("content", 1)]
    [InlineData("created", 2)]
    [InlineData("started", 3)]
    public async Task HookFailureResumesWithoutRepeatingAcknowledgedCommands(string failed, int index)
    {
        var fixture = new Fixture { FailCommand = failed };
        Assert.False((await fixture.Up()).Success);
        Assert.Equal(new[] { "create", "content", "created", "started" }.Take(index + 1), fixture.Commands);
        fixture.Reload();
        fixture.FailCommand = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(new[] { "create", "content", "created", "started" }.Skip(index), fixture.Commands);
        fixture.Commands.Clear();
        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task CompletedHooksAreNotReplayedAfterDevContainerReimport()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        // A fresh import has no runtime checkpoint. The persisted record still owns it.
        fixture.Config = JsonSerializer.Deserialize<DevContainerConfig>(
            JsonSerializer.Serialize(new DevContainerConfig
            {
                Id = fixture.Config.Id, Name = fixture.Config.Name, Compose = fixture.Config.Compose,
                WorkspaceFolder = fixture.Config.WorkspaceFolder, Lifecycle = fixture.Config.Lifecycle,
            }))!;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task FailedCommandWithinCreationStepResumesFrozenCommandsAfterConfigEdit()
    {
        var fixture = new Fixture { FailCommand = "create-two" };
        fixture.Config.Lifecycle.OnCreate.Add("create-two");
        Assert.False((await fixture.Up()).Success);
        Assert.Equal(["create", "create-two"], fixture.Commands);
        fixture.Reload();
        fixture.Config.Lifecycle.OnCreate = ["replacement-command"];
        fixture.Config.WorkspaceFolder = "/edited";
        fixture.FailCommand = null;
        fixture.Commands.Clear();
        fixture.Scripts.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Equal(["create-two", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.Scripts, script => Assert.StartsWith("cd '/work' && ", script));
    }

    [Fact]
    public async Task CancellationBetweenHooksRetainsAcknowledgedProgress()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.AfterExec = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Up(cancellation.Token));
        Assert.Equal(["create"], fixture.Commands);
        fixture.Reload();
        fixture.AfterExec = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["content", "created", "started"], fixture.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSiblingStartupDoesNotLosePrimaryHooks(bool existing)
    {
        var fixture = new Fixture();
        if (existing)
        {
            Assert.True((await fixture.Up()).Success);
            fixture.Compose.Engine.States["demo_web"] = ContainerState.Stopped;
            fixture.Commands.Clear();
        }
        using var cancellation = new CancellationTokenSource();
        fixture.Config.Compose!.Project.Services.Add(new() { Name = "other", Options = new() { Image = "fixture" } });
        fixture.Config.Compose.Project.Services.Add(new() { Name = "last", Options = new() { Image = "fixture" } });
        fixture.Persist();
        fixture.Compose.Engine.AfterRun = name =>
        {
            fixture.Compose.Engine.ContainerIds[name] = name;
            if (name == "demo_other") cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Up(cancellation.Token));
        Assert.Empty(fixture.Commands);
        fixture.Reload();
        fixture.Compose.Engine.AfterRun = null;

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(existing ? ["started"] : new[] { "create", "content", "created", "started" }, fixture.Commands);
        fixture.Commands.Clear();
        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task FailedStartDoesNotExecuteHooksAndSuccessfulRetryRunsOnlyPostStart()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        fixture.Compose.Engine.States["demo_web"] = ContainerState.Stopped;
        fixture.Compose.Engine.FailStart = "demo_web";
        fixture.Commands.Clear();

        Assert.False((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
        fixture.Compose.Engine.FailStart = null;
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["started"], fixture.Commands);
    }

    [Fact]
    public async Task ReplacementDoesNotInheritOldIdentityPendingHooks()
    {
        var fixture = new Fixture { FailCommand = "content" };
        Assert.False((await fixture.Up()).Success);
        fixture.Reload();
        fixture.Config.Compose!.Project.Services[0].Options.EnvironmentVariables.Add("UPDATED=yes");
        fixture.FailCommand = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds.TakeLast(4), id => Assert.Equal("instance-2", id));
    }

    [Fact]
    public async Task ShortAndFullIdsShareTheSamePendingCheckpoint()
    {
        var fixture = new Fixture { FailCommand = "content" };
        var fullId = new string('a', 64);
        fixture.Compose.Engine.AfterRun = name => fixture.Compose.Engine.ContainerIds[name] = fullId;
        Assert.False((await fixture.Up()).Success);
        fixture.Reload();
        fixture.Compose.Engine.ContainerIds["demo_web"] = fullId[..12];
        fixture.FailCommand = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds.TakeLast(3), id => Assert.Equal(fullId[..12], id));
    }

    [Fact]
    public async Task PersistenceFailureIsSurfacedBeforeExecutingHooks()
    {
        var fixture = new Fixture { FailSave = true };
        var result = await fixture.Up();
        Assert.False(result.Success);
        Assert.Contains("fixture save failure", result.Detail);
        Assert.Empty(fixture.Commands);
        Assert.Equal("instance-1", fixture.Compose.SavedProject!.AppliedServices["web"].ContainerId);
        Assert.Equal(ContainerState.Running, fixture.Compose.Engine.States["demo_web"]);
        fixture.FailSave = false;

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
    }

    [Fact]
    public async Task SingleContainerUpStillReplacesContainerAndRunsAllHooks()
    {
        var fixture = new Fixture();
        fixture.Config.Compose = null;
        fixture.Config.Image = "fixture";
        fixture.Config.RunOptions.Image = "fixture";
        Assert.True((await fixture.Up()).Success);
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started", "create", "content", "created", "started"], fixture.Commands);
        Assert.Equal(2, fixture.Compose.Engine.Mutations.Count(m => m == "run:devcontainer-test"));
        Assert.Contains("remove:instance-1", fixture.Compose.Engine.Mutations);
    }

    [Fact]
    public void RealStorePreservesPendingProgressThroughImportAndReload()
    {
        using var files = new StoreFiles();
        var store = new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File);
        store.Save(new()
        {
            Id = "test", ComposeLifecycleProgress = new()
            {
                ContainerId = "old-id",
                PendingCreate = [new() { Step = "updateContentCommand", Command = "pending" }],
                PendingStart = [new() { Step = "postStartCommand", Command = "start" }],
            },
        });
        store.Save(new() { Id = "test", Name = "reimported" });

        var reloaded = new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File).Get("test")!;
        Assert.Equal("reimported", reloaded.Name);
        Assert.Equal("old-id", reloaded.ComposeLifecycleProgress!.ContainerId);
        Assert.Equal("pending", Assert.Single(reloaded.ComposeLifecycleProgress.PendingCreate).Command);
        Assert.Equal("start", Assert.Single(reloaded.ComposeLifecycleProgress.PendingStart).Command);
        store.Save(new() { Id = "test", ComposeLifecycleProgress = new() { ContainerId = "new-id" } });
        Assert.Equal("new-id", new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File)
            .Get("test")!.ComposeLifecycleProgress!.ContainerId);
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    [Fact]
    public void RealStoreSurfacesWriteFailuresAndCleansTemporaryFile()
    {
        using var files = new StoreFiles();
        Directory.CreateDirectory(files.File);
        var store = new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File);
        Assert.Throws<InvalidOperationException>(() => store.Save(new() { Id = "test" }));
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    private sealed class StoreFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "devcontainer-lifecycle-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "devcontainers.json");
        public StoreFiles() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Fixture
    {
        public ComposeNetworkSupervisorTests.Fixture Compose { get; } = new();
        public DevContainerConfig Config { get; set; }
        public List<string> Commands { get; } = new();
        public List<string> ExecIds { get; } = new();
        public List<string> Scripts { get; } = new();
        public string? FailCommand { get; set; }
        public bool FailSave { get; set; }
        public Action<string>? AfterExec { get; set; }
        private DevContainerConfig? _saved;
        private readonly DevContainerSupervisor _supervisor;

        public Fixture()
        {
            Compose.Project.Services[0].Options = new() { Image = "fixture" };
            Compose.Engine.NextImageId = "sha256:fixture-v1";
            var generation = 0;
            Compose.Engine.AfterRun = name => Compose.Engine.ContainerIds[name] = $"instance-{++generation}";
            Config = new()
            {
                Id = "test", Name = "dev", WorkspaceFolder = "/work",
                Compose = new() { Service = "web", Project = Compose.Project },
                Lifecycle = new() { OnCreate = ["create"], UpdateContent = ["content"], PostCreate = ["created"], PostStart = ["started"] },
            };
            var wslc = NetworkTestProxy.Create<IWslcService>((method, args) =>
            {
                if (method.Name != nameof(IWslcService.ExecAsync))
                    return method.Invoke(Compose.Engine.Service, args);
                var command = ((string)args[1]!).Split(" && ", 2)[1];
                Commands.Add(command);
                Scripts.Add((string)args[1]!);
                ExecIds.Add((string)args[0]!);
                AfterExec?.Invoke(command);
                return Task.FromResult(new CommandResult
                {
                    ExitCode = command == FailCommand ? 1 : 0,
                    StandardError = command == FailCommand ? "hook failed" : "",
                });
            });
            var store = NetworkTestProxy.Create<IDevContainerStore>((method, args) =>
            {
                if (method.Name == nameof(IDevContainerStore.Get)) return _saved;
                if (method.Name != nameof(IDevContainerStore.Save)) throw new InvalidOperationException(method.Name);
                if (FailSave) throw new InvalidOperationException("fixture save failure");
                _saved = JsonSerializer.Deserialize<DevContainerConfig>(JsonSerializer.Serialize((DevContainerConfig)args[0]!))!;
                return null;
            });
            var features = NetworkTestProxy.Create<IDevContainerFeatureResolver>((method, _) => throw new InvalidOperationException(method.Name));
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => throw new InvalidOperationException(method.Name));
            _supervisor = new(wslc, store, features, Compose.Supervisor, new ProcessRunner(settings),
                NullLogger<DevContainerSupervisor>.Instance);
        }

        public Task<DevContainerOperationResult> Up(CancellationToken ct = default) => _supervisor.UpAsync(Config, ct: ct);
        public void Persist() => _saved = JsonSerializer.Deserialize<DevContainerConfig>(JsonSerializer.Serialize(Config))!;
        public void Reload() => Config = JsonSerializer.Deserialize<DevContainerConfig>(JsonSerializer.Serialize(_saved))!;
    }
}
