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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using Xunit;
using SdkSession = Microsoft.WSL.Containers.Session;
using SdkSessionSettings = Microsoft.WSL.Containers.SessionSettings;

namespace WslContainerDesktop.Tests.Runtime;

public sealed class CombinedSessionsRuntimeTests
{
    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task ComposeDeploymentUsesChosenSessionWithoutChangingManagementSelection()
    {
        const string executable = @"C:\Program Files\WSL\wslc.exe";
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wcd-compose-scope-" + suffix);
        var firstName = "wcd-compose-a-" + suffix;
        var secondName = "wcd-compose-b-" + suffix;
        var image = "wcd-offline-probe:" + suffix;
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "first");
        var secondPath = Path.Combine(root, "second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        using var first = new SdkSession(new SdkSessionSettings(firstName, firstPath));
        using var second = new SdkSession(new SdkSessionSettings(secondName, secondPath));
        var firstStarted = false;
        var secondStarted = false;
        try
        {
            first.Start();
            firstStarted = true;
            second.Start();
            secondStarted = true;
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(root, "settings"))
            {
                WslcPath = executable,
            };
            var capabilities = new WslcCapabilitiesService(settings, NullLogger<WslcCapabilitiesService>.Instance);
            var suppression = new RestartSuppressionState();
            IWslcService Client(WslcSessionTarget target, Func<CancellationToken, Task>? verify = null) =>
                new WslcService(new ProcessRunner(settings, fixedSession: target, fixedExecutablePath: executable,
                        scopeVerifier: verify), NullLogger<WslcService>.Instance, capabilities, settings, suppression,
                    fileTransferStagingRoot: () => Path.Combine(root, "transfer"));
            var discovery = Client(WslcSessionTarget.Default);
            var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(new(session.Name)),
                () => executable, NullLogger<WslcSessionInventoryReader>.Instance, TimeSpan.FromMinutes(2));
            var generation = Assert.Single(await reader.DiscoverAsync(), session => session.Name == secondName);
            var guarded = Client(new(secondName), ct =>
                WslcSessionActionGuard.VerifyAsync(discovery, () => settings.WslcPath, generation, executable, ct));
            var archive = Path.Combine(root, "image.tar");
            OfflineRuntimeImage.Create(archive, image);
            Require(await guarded.LoadImageAsync(archive), "load isolated image");
            var context = new WslcSessionContext();
            context.Select(firstName);
            var scoped = new WslcSessionContext();
            scoped.Select(secondName);
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, Path.Combine(root, "compose"));
            var presenter = NetworkTestProxy.Create<IComposeReviewPresenter>((method, _) =>
                method.Name == nameof(IComposeReviewPresenter.ConfirmAsync)
                    ? Task.FromResult(true) : throw new InvalidOperationException(method.Name));
            var monitor = new StatusMonitor();
            var health = new HealthWatchdog();
            var global = new ComposeProjectSupervisor(Client(new(firstName)), store, settings,
                NullLogger<ComposeProjectSupervisor>.Instance, capabilities, health, monitor, suppression,
                presenter, sessionContext: context);
            var supervisor = global.ForSession(guarded, scoped, health, () => []);
            var project = new ComposeProject
            {
                Name = "probe",
                SessionOwner = AutomationSessionOwner.Capture(executable, new(secondName)),
                Services = [new ComposeService
                {
                    Name = "web",
                    Options = new RunContainerOptions { Image = image, Detached = true, NeverPull = true },
                }],
            };
            project.ApplyProjectNamespacing();

            var result = await supervisor.UpNewAsync(project);

            Assert.True(result.AllSucceeded, string.Join("; ", result.Services.Select(item => item.Detail)));
            Assert.Empty(await Client(new(firstName)).ListContainersAsync());
            Assert.Single(await guarded.ListContainersAsync());
            Assert.Equal(firstName, context.SelectedSessionName);
            Assert.Equal(secondName, store.Get(project.Name)!.SessionOwner!.SessionName);

            var deployed = Assert.Single(await guarded.ListContainersAsync());
            var sharedName = deployed.Name.TrimStart('/');
            var firstClient = Client(new(firstName));
            Require(await firstClient.LoadImageAsync(archive), "load first-session image");
            Require(await firstClient.RunContainerAsync(new RunContainerOptions
            {
                Image = image, Name = sharedName, Detached = true, NeverPull = true,
            }), "create first-session container");
            var firstContainer = Assert.Single(await firstClient.ListContainersAsync());
            Require(await firstClient.StopContainerAsync(firstContainer.Id), "stop first-session container");
            Require(await guarded.StopContainerAsync(deployed.Id), "stop second-session container");
            settings.RestartPolicies.Add(new RestartPolicyConfig
            {
                ContainerName = sharedName, Policy = RestartPolicyKind.Always,
                SessionOwner = AutomationSessionOwner.Capture(executable, new(secondName)),
            });
            var owners = new LegacyPolicyOwnershipStore(NullLogger<LegacyPolicyOwnershipStore>.Instance,
                Path.Combine(root, "owners.json"));
            var clients = new WslcSessionClientFactory((session, _, _) =>
                Client(new(session.Name), ct => WslcSessionActionGuard.VerifyAsync(
                    discovery, () => settings.WslcPath, session, executable, ct)));
            var emptyStore = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, Path.Combine(root, "empty-compose"));
            var recovery = new ComposeProjectSupervisor(firstClient, emptyStore, settings,
                NullLogger<ComposeProjectSupervisor>.Instance, capabilities, health, monitor, suppression,
                presenter, sessionContext: context);
            using (var coordinator = new SessionAutomationCoordinatorHarness(monitor, settings, context, clients,
                discovery, suppression, owners, NullLoggerFactory.Instance, recovery, health))
            {
                coordinator.Start();
                var stopped = await guarded.ListContainersAsync();
                monitor.PublishAutomation(new(executable, [new(generation, new(stopped), ObservedAt: DateTimeOffset.UtcNow)],
                    SelectedSessionName: firstName, IsPartial: true));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    monitor.Dispatcher.Drain();
                    if ((await guarded.ListContainersAsync()).Any(container =>
                            container.Id == deployed.Id && container.State == ContainerState.Running))
                        break;
                    await Task.Delay(250, timeout.Token);
                }
                Assert.Equal(ContainerState.Stopped, Assert.Single(await firstClient.ListContainersAsync()).State);
                Assert.Equal(firstName, context.SelectedSessionName);
            }
            settings.RestartPolicies.Clear();
            Require(await firstClient.RemoveContainerAsync(firstContainer.Id), "remove first-session container");
            await supervisor.DownAndDeleteAsync(project.Name, removeVolumes: false);

            Assert.Empty(await guarded.ListContainersAsync());
            Assert.Empty(await Client(new(firstName)).ListContainersAsync());
            Assert.Null(store.Get(project.Name));
            Assert.Equal(firstName, context.SelectedSessionName);
        }
        finally
        {
            try
            {
                if (secondStarted) { second.Terminate(); secondStarted = false; }
            }
            finally
            {
                try
                {
                    if (firstStarted) { first.Terminate(); firstStarted = false; }
                }
                finally
                {
                    second.Dispose();
                    first.Dispose();
                    if (!firstStarted && !secondStarted && Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task AppOwnedSessionCanBeRestoredAndRemovedWithData()
    {
        const string executable = @"C:\Program Files\WSL\wslc.exe";
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wcd-managed-runtime-" + suffix);
        var name = "wcd-managed-" + suffix;
        Directory.CreateDirectory(root);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(root, "settings"))
        {
            WslcPath = executable,
        };
        var capabilities = new WslcCapabilitiesService(settings, NullLogger<WslcCapabilitiesService>.Instance);
        IWslcService Client(WslcSessionTarget target) => new WslcService(
            new ProcessRunner(settings, fixedSession: target, fixedExecutablePath: executable),
            NullLogger<WslcService>.Instance, capabilities, settings, new RestartSuppressionState());
        var discovery = Client(WslcSessionTarget.Default);
        var reader = new WslcSessionInventoryReader(discovery,
            (session, _) => Client(new(session.Name)), () => executable,
            NullLogger<WslcSessionInventoryReader>.Instance, TimeSpan.FromMinutes(2));
        var store = new ManagedSessionStore(NullLogger.Instance, Path.Combine(root, "store"));
        ManagedSessionService CreateService() => new(store, reader, discovery, (target, _) => Client(target),
            settings, new WslcSessionContext(), new DispatcherQueue(), NullLogger<ManagedSessionService>.Instance,
            (sessionName, path) => new ManagedSdkSessionHandle(sessionName, path));
        var first = CreateService();
        ManagedSessionService? second = null;
        try
        {
            var definition = await first.CreateAsync(name);
            Assert.True(first.IsRunning(definition));
            Assert.True(first.Owns(definition, Assert.Single(await reader.DiscoverAsync(), s => s.Name == name)));
            await first.StopAsync(definition);
            Assert.False(first.IsRunning(definition));
            Assert.DoesNotContain(await reader.DiscoverAsync(), s => s.Name == name);
            await first.StartSavedAsync(definition);
            Assert.True(first.IsRunning(definition));
            first.Dispose();
            Assert.DoesNotContain(await reader.DiscoverAsync(), s => s.Name == name);

            second = CreateService();
            await second.RestoreAsync();
            Assert.True(second.IsRunning(definition));
            await second.RemoveAsync(definition, deleteData: true);
            Assert.False(Directory.Exists(store.StoragePath(definition)));
            Assert.Empty(store.Snapshot());
        }
        finally
        {
            first.Dispose();
            second?.Dispose();
            var remaining = (await reader.DiscoverAsync()).SingleOrDefault(session => session.Name == name);
            if (remaining is not null)
            {
                var result = await Client(new(name)).RestartSessionAsync();
                Require(result, "clean up disposable session");
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task SdkSessionDiscoveryIdentifiesItsCreator()
    {
        const string executable = @"C:\Program Files\WSL\wslc.exe";
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wcd-session-owner-" + suffix);
        var name = "wcd-owner-" + suffix;
        Directory.CreateDirectory(root);
        using var session = new SdkSession(new SdkSessionSettings(name, root));
        var started = false;
        try
        {
            session.Start();
            started = true;
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(root, "settings"))
            {
                WslcPath = executable,
            };
            var discovery = new WslcService(new ProcessRunner(settings, fixedSession: WslcSessionTarget.Default),
                NullLogger<WslcService>.Instance, new WslcCapabilitiesService(settings, NullLogger<WslcCapabilitiesService>.Instance),
                settings, new RestartSuppressionState());
            var information = await discovery.GetSystemInfoAsync();
            var observed = Assert.Single(information.Server!.Sessions, item => item.Name == name);
            Assert.Equal(Environment.ProcessId, observed.CreatorPid);
            Assert.True(observed.ID > 0);
        }
        finally
        {
            if (started)
                session.Terminate();
            session.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task IsolatedContainersUseTheirOwningSessionsForLifecycleLogsAndCopy()
    {
        const string executable = @"C:\Program Files\WSL\wslc.exe";
        Assert.Equal(System.Runtime.InteropServices.Architecture.X64,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wcd-container-combined-" + suffix);
        var firstName = "wcd-container-a-" + suffix;
        var secondName = "wcd-container-b-" + suffix;
        var containerName = "wcd-probe-" + suffix;
        var networkName = "wcd-net-" + suffix;
        var image = "wcd-offline-probe:" + suffix;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        var archive = Path.Combine(root, "probe.tar");
        var first = new SdkSession(new SdkSessionSettings(firstName, Path.Combine(root, "first")));
        var second = new SdkSession(new SdkSessionSettings(secondName, Path.Combine(root, "second")));
        var firstStarted = false;
        var secondStarted = false;
        try
        {
            OfflineRuntimeImage.Create(archive, image);
            first.Start();
            firstStarted = true;
            second.Start();
            secondStarted = true;
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(root, "settings"))
            {
                WslcPath = executable,
            };
            var capabilities = new WslcCapabilitiesService(settings, NullLogger<WslcCapabilitiesService>.Instance);
            IWslcService Client(WslcSessionTarget target) => new WslcService(
                new ProcessRunner(settings, fixedSession: target, fixedExecutablePath: executable),
                NullLogger<WslcService>.Instance, capabilities, settings, new RestartSuppressionState(),
                fileTransferStagingRoot: () => Path.Combine(root, "transfer"));
            var firstClient = Client(new(firstName));
            var secondClient = Client(new(secondName));
            Require(await firstClient.LoadImageAsync(archive), "load first image");
            Require(await secondClient.LoadImageAsync(archive), "load second image");
            Require(await firstClient.RunContainerAsync(new RunContainerOptions
                { Image = image, Name = containerName, Detached = true, NeverPull = true }), "run first container");
            Require(await secondClient.RunContainerAsync(new RunContainerOptions
                { Image = image, Name = containerName, Detached = true, NeverPull = true }), "run second container");
            Require(await firstClient.CreateNetworkAsync(networkName), "create first network");
            Require(await secondClient.CreateNetworkAsync(networkName), "create second network");

            var discovery = Client(WslcSessionTarget.Default);
            var reader = new WslcSessionInventoryReader(discovery,
                (session, _) => Client(new(session.Name)), () => executable,
                NullLogger<WslcSessionInventoryReader>.Instance, TimeSpan.FromMinutes(2));
            var selected = (await reader.DiscoverAsync())
                .Where(session => session.Name == firstName || session.Name == secondName).ToArray();
            Assert.Equal(2, selected.Length);
            var inventory = WslcCombinedInventory.Create(await reader.ReadAsync(selected));
            var containers = inventory.Containers.Where(row => row.Value.Name == containerName).ToArray();
            Assert.Equal(2, containers.Length);
            Assert.Equal(2, inventory.Networks.Count(row => row.Value.Name == networkName));
            Assert.Equal(2, inventory.Images.Count);
            Assert.All(containers, row => Assert.Equal(ContainerState.Running, row.Value.State));
            var target = WslcContainerActionTarget.Capture(
                containers.Single(row => row.Session.Name == secondName).Session,
                containers.Single(row => row.Session.Name == secondName).Value);
            var context = new WslcSessionContext();
            var actions = new WslcSessionContainerActions(discovery, context,
                (session, _, _) => Client(new(session.Name)), () => executable, () => "not-a-temporary-session",
                (_, _) => throw new InvalidOperationException("Unexpected management callback"),
                () => throw new InvalidOperationException("Unexpected management callback"));
            var logs = await actions.ReadLogsAsync(target);
            Require(logs, "read second container logs");
            Assert.Contains("ready", logs.StandardOutput);
            var input = Path.Combine(root, "payload.txt");
            await File.WriteAllTextAsync(input, "session-specific content");
            Require(await actions.CopyToAsync(target, input, "/"), "copy into second container");
            var output = Path.Combine(root, "download");
            Directory.CreateDirectory(output);
            Require(await actions.CopyFromAsync(target, "/payload.txt", output), "copy from second container");
            Assert.Equal("session-specific content", await File.ReadAllTextAsync(Path.Combine(output, "payload.txt")));
            var otherOutput = Path.Combine(root, "first-download");
            Directory.CreateDirectory(otherOutput);
            Assert.False((await firstClient.CopyFromContainerAsync(
                containerName, "/payload.txt", otherOutput)).Success);
            Require(await actions.ExecuteAsync(target, WslcContainerAction.Stop), "stop second container");
            Assert.Equal(ContainerState.Running, Assert.Single(await firstClient.ListContainersAsync()).State);
            Assert.Equal(ContainerState.Stopped, Assert.Single(await secondClient.ListContainersAsync()).State);
            Require(await actions.ExecuteAsync(target, WslcContainerAction.Start), "restart second container");
            Require(await actions.ExecuteAsync(target, WslcContainerAction.Restart), "restart second container explicitly");
            Require(await actions.ExecuteAsync(target, WslcContainerAction.Kill), "kill second container");
            Assert.Equal(ContainerState.Running, Assert.Single(await firstClient.ListContainersAsync()).State);
            Require(await actions.ExecuteAsync(target, WslcContainerAction.Start), "start second container after kill");
            Require(await actions.ExecuteAsync(target, WslcContainerAction.Remove), "remove second container");
            Assert.Single(await firstClient.ListContainersAsync());
            Assert.Empty(await secondClient.ListContainersAsync());
            var resources = new WslcSessionResourceActions(discovery, context,
                (session, _, _) => Client(new(session.Name)), () => executable,
                () => "not-a-temporary-session", () => throw new InvalidOperationException("Unexpected management callback"));
            var network = inventory.Networks.Single(row => row.Session.Name == secondName && row.Value.Name == networkName);
            Require(await resources.RemoveAsync(WslcResourceActionTarget.Capture(network.Session, network.Value)),
                "remove second network");
            Assert.Contains(await firstClient.ListNetworksAsync(), item => item.Name == networkName);
            Assert.DoesNotContain(await secondClient.ListNetworksAsync(), item => item.Name == networkName);
            var imageRow = inventory.Images.Single(row => row.Session.Name == secondName);
            Require(await resources.RemoveAsync(WslcResourceActionTarget.Capture(imageRow.Session, imageRow.Value)),
                "remove second image");
            Assert.Single(await firstClient.ListImagesAsync());
            Assert.Empty(await secondClient.ListImagesAsync());
            Assert.Null(context.SelectedSessionName);
        }
        finally
        {
            try
            {
                if (secondStarted)
                {
                    second.Terminate();
                    secondStarted = false;
                }
            }
            finally
            {
                try
                {
                    if (firstStarted)
                    {
                        first.Terminate();
                        firstStarted = false;
                    }
                }
                finally
                {
                    second.Dispose();
                    first.Dispose();
                    if (!firstStarted && !secondStarted && Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    private static void Require(CommandResult result, string operation) =>
        Assert.True(result.Success, $"{operation}: {result.ErrorText}");

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task IsolatedSessionsKeepSameNamedVolumesSeparateDuringCombinedRemoval()
    {
        const string executable = @"C:\Program Files\WSL\wslc.exe";
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "wcd-combined-" + suffix);
        var firstName = "wcd-combined-a-" + suffix;
        var secondName = "wcd-combined-b-" + suffix;
        var volumeName = "wcd-volume-" + suffix;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        var first = new SdkSession(new SdkSessionSettings(firstName, Path.Combine(root, "first")));
        var second = new SdkSession(new SdkSessionSettings(secondName, Path.Combine(root, "second")));
        var firstStarted = false;
        var secondStarted = false;
        try
        {
            first.Start();
            firstStarted = true;
            second.Start();
            secondStarted = true;
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(root, "settings"))
            {
                WslcPath = executable,
            };
            var capabilities = new WslcCapabilitiesService(settings, NullLogger<WslcCapabilitiesService>.Instance);
            IWslcService Client(WslcSessionTarget target) => new WslcService(
                new ProcessRunner(settings, fixedSession: target, fixedExecutablePath: executable),
                NullLogger<WslcService>.Instance, capabilities, settings, new RestartSuppressionState());
            var firstClient = Client(new(firstName));
            var secondClient = Client(new(secondName));
            var createFirst = await firstClient.CreateVolumeAsync(volumeName);
            Assert.True(createFirst.Success, createFirst.ErrorText);
            var createSecond = await secondClient.CreateVolumeAsync(volumeName);
            Assert.True(createSecond.Success, createSecond.ErrorText);

            var discovery = Client(WslcSessionTarget.Default);
            var reader = new WslcSessionInventoryReader(discovery,
                (session, _) => Client(new(session.Name)), () => executable,
                NullLogger<WslcSessionInventoryReader>.Instance, TimeSpan.FromMinutes(2));
            var sessions = await reader.DiscoverAsync();
            var selected = sessions.Where(session => session.Name == firstName || session.Name == secondName).ToArray();
            Assert.Equal(2, selected.Length);
            var snapshots = await reader.ReadAsync(selected);
            var combined = WslcCombinedInventory.Create(snapshots);
            Assert.Equal(2, combined.Volumes.Count(row => row.Value.Name == volumeName));

            var context = new WslcSessionContext();
            var actions = new WslcSessionResourceActions(discovery, context,
                (session, _, _) => Client(new(session.Name)), () => executable,
                () => "not-a-temporary-session", () => throw new InvalidOperationException("Unexpected management callback"));
            var target = await actions.PrepareVolumeRemovalAsync(selected.Single(session => session.Name == secondName), volumeName);
            var removal = await actions.RemoveAsync(target);
            Assert.True(removal.Success, removal.ErrorText);
            Assert.Contains(await firstClient.ListVolumesAsync(), volume => volume.Name == volumeName);
            Assert.DoesNotContain(await secondClient.ListVolumesAsync(), volume => volume.Name == volumeName);
            Assert.Null(context.SelectedSessionName);
        }
        finally
        {
            try
            {
                if (secondStarted)
                {
                    second.Terminate();
                    secondStarted = false;
                }
            }
            finally
            {
                try
                {
                    if (firstStarted)
                    {
                        first.Terminate();
                        firstStarted = false;
                    }
                }
                finally
                {
                    second.Dispose();
                    first.Dispose();
                    if (!firstStarted && !secondStarted && Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
            }
        }
    }
}
